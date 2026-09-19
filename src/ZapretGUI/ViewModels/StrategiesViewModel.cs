using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class StrategiesViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _searchText = "";
        private int _categoryIndex;
        private bool _onlyRecommended;
        private StrategyInfo? _selected;
        private bool _isBusy;
        private bool _isTestingAll;
        private double _testProgressValue;
        private double _testProgressMaximum = 1;
        private bool _testProgressIndeterminate = true;
        private string _testProgressPercentText = "";
        private string _testProgressText = "";
        private string _testSummary = "";
        private string _testSummaryKey = "Info";
        private string _message = "";
        private StrategyCandidate? _candidatePreview;
        private bool _isCandidatePreviewSaved;
        private bool _isGeneratingCandidates;
        private string _candidateGenerationText = "";
        private bool _isEvaluatingCandidates;
        private string _candidateEvaluationText = "";
        private double _candidateEvaluationProgressValue;
        private double _candidateEvaluationProgressMaximum = 1;
        private string _candidateEvaluationProgressPercentText = "";
        private bool _candidateEvaluationProgressVisible;
        private CancellationTokenSource? _testCts;
        private readonly List<StrategyEvaluationHistoryRecord> _evaluationHistory;
        private readonly ICollectionView _candidateEvaluationView;
        private CancellationTokenSource? _candidateGenerationCts;
        private CancellationTokenSource? _candidateEvaluationCts;

        public StrategiesViewModel(MainViewModel main)
        {
            _main = main;

            View = CollectionViewSource.GetDefaultView(Store.Items);
            View.Filter = FilterItem;
            _candidateEvaluationView = CollectionViewSource.GetDefaultView(CandidateEvaluations);
            _candidateEvaluationView.SortDescriptions.Add(
                new SortDescription(nameof(StrategyCandidateEvaluation.Score), ListSortDirection.Descending));
            _evaluationHistory = StrategyEvaluationHistoryStore.Load();

            Categories = new[] { "Все категории", "FAKE TLS AUTO", "ALT", "SIMPLE FAKE", "БАЗОВАЯ", "EXP", "АВТОКОНСТРУКТОР" };

            RunCommand = new AsyncRelayCommand(RunAsync,
                parameter => (parameter is StrategyInfo || Selected != null) && !IsBusy && !IsTestingAll && !IsGeneratingCandidates && !IsEvaluatingCandidates);
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => Selected != null && !IsBusy && !IsTestingAll && !IsGeneratingCandidates);
            TestStrategyCommand = new AsyncRelayCommand(TestStrategyAsync, _ => !IsTestingAll && !IsBusy && !IsGeneratingCandidates && !IsEvaluatingCandidates);
            TestAllCommand = new AsyncRelayCommand(_ => TestAllAsync(), _ => !IsTestingAll && !IsBusy && !IsGeneratingCandidates && !IsEvaluatingCandidates && Store.Items.Count > 0);
            CancelTestCommand = new RelayCommand(() => _testCts?.Cancel(), () => IsTestingAll);
            OpenBatCommand = new RelayCommand(() => { if (Selected != null) Shell.OpenInNotepad(Selected.FullPath); });
            CopyArgsCommand = new RelayCommand(CopyArgs, () => Selected != null);
            SetDefaultCommand = new RelayCommand(SetDefault, () => Selected != null);
            RefreshCommand = new RelayCommand(Refresh);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(Store.Folder));
            UseRecommendedCommand = new RelayCommand(UseRecommended);
            BuildCandidatePreviewCommand = new RelayCommand(BuildCandidatePreview, () => Selected != null);
            GenerateCandidatesCommand = new AsyncRelayCommand(GenerateCandidatesAsync,
                () => !IsGeneratingCandidates && !IsTestingAll && !IsBusy && Store.Items.Count > 0);
            CancelCandidateGenerationCommand = new RelayCommand(CancelCandidateGeneration,
                () => IsGeneratingCandidates);
            PreviewGeneratedCandidateCommand = new RelayCommand(PreviewGeneratedCandidate);
            EvaluateCandidatesCommand = new AsyncRelayCommand(EvaluateCandidatesAsync,
                () => !IsEvaluatingCandidates && !IsGeneratingCandidates && !IsTestingAll && !IsBusy && CandidateEvaluations.Count > 0);
            CancelCandidateEvaluationCommand = new RelayCommand(CancelCandidateEvaluation,
                () => IsEvaluatingCandidates);
            ExportCandidateReportCommand = new RelayCommand(ExportCandidateReport,
                () => CandidateEvaluations.Count > 0 || EvaluationHistory.Count > 0);
            ClearHistoryCommand = new RelayCommand(ClearHistory, () => EvaluationHistory.Count > 0);
            SaveCandidateCommand = new RelayCommand(SaveCandidate, () => CandidatePreview != null);
            RunSavedCandidateCommand = new AsyncRelayCommand(RunSavedCandidateAsync,
                () => CandidatePreview != null && IsCandidatePreviewSaved && !IsBusy && !IsTestingAll && !IsGeneratingCandidates && !IsEvaluatingCandidates);
            PreviewSavedCandidateCommand = new RelayCommand(PreviewSavedCandidate);
            DeleteSavedCandidateCommand = new RelayCommand(DeleteSavedCandidate);
            MakeCandidatePrimaryCommand = new RelayCommand(MakeCandidatePrimary, () => CandidatePreview != null && IsCandidatePreviewSaved);
            RestorePreviousCommand = new RelayCommand(RestorePrevious, () => CanRestorePrevious);

            foreach (var saved in StrategyCandidateStore.Load()) SavedCandidates.Add(saved);
            foreach (var record in _evaluationHistory.Take(50)) EvaluationHistory.Add(record);
        }

        public StrategyStore Store => _main.Strategies;
        public AppSettings Settings => _main.Settings;
        public BypassController Bypass => _main.Bypass;
        public ObservableCollection<StrategyCandidate> GeneratedCandidates { get; } = new();
        public ObservableCollection<StrategyCandidateEvaluation> CandidateEvaluations { get; } = new();
        public ObservableCollection<SavedStrategyCandidate> SavedCandidates { get; } = new();
        public ObservableCollection<StrategyEvaluationHistoryRecord> EvaluationHistory { get; } = new();

        public ICollectionView View { get; }
        public ICollectionView CandidateEvaluationView => _candidateEvaluationView;
        public string[] Categories { get; }

        public bool IsEngineReady => EngineService.IsEngineReady(Settings.EnginePath);
        public bool IsEngineMissing => !IsEngineReady;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value)) View.Refresh();
            }
        }

        public int CategoryIndex
        {
            get => _categoryIndex;
            set
            {
                if (Set(ref _categoryIndex, value)) View.Refresh();
            }
        }

        public bool OnlyRecommended
        {
            get => _onlyRecommended;
            set
            {
                if (Set(ref _onlyRecommended, value)) View.Refresh();
            }
        }

        public StrategyInfo? Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                Raise(nameof(HasSelection));
                Raise(nameof(SelectedDescription));
                Raise(nameof(SelectedArgs));
                Raise(nameof(SelectedCategory));
                Raise(nameof(SelectedPath));
                Raise(nameof(SelectedFeatures));
                Raise(nameof(IsSelectedDefault));
                CandidatePreview = null;
                (BuildCandidatePreviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CopyArgsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SetDefaultCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool HasSelection => Selected != null;
        public string SelectedDescription => Selected?.Description ?? "";
        public string SelectedArgs => Selected?.ShortArgs ?? "";
        public string SelectedCategory => Selected?.Category ?? "";
        public string SelectedPath => Selected?.FullPath ?? "";
        public string SelectedFeatures => Selected == null
            ? "Признаки не определены"
            : StrategyFeatureAnalyzer.Analyze(Selected).Summary;
        public string ProviderContextText => Settings.ProviderContext?.DisplayText ?? "Провайдер не указан";

        public StrategyCandidate? CandidatePreview
        {
            get => _candidatePreview;
            private set
            {
                if (!Set(ref _candidatePreview, value)) return;
                _isCandidatePreviewSaved = value != null && StrategyCandidateStore.Contains(value.Fingerprint);
                Raise(nameof(IsCandidatePreviewSaved));
                Raise(nameof(CandidateSaveStatusText));
                (SaveCandidateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (MakeCandidatePrimaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                Raise(nameof(CandidatePreviewVisible));
                Raise(nameof(CandidatePreviewName));
                Raise(nameof(CandidatePreviewProvider));
                Raise(nameof(CandidatePreviewSummary));
                Raise(nameof(CandidatePreviewArgs));
                Raise(nameof(CandidatePreviewFeatures));
                Raise(nameof(CandidatePreviewFixedArgs));
                Raise(nameof(CandidatePreviewTunableArgs));
            }
        }

        public bool CandidatePreviewVisible => CandidatePreview != null;
        public string CandidatePreviewName => CandidatePreview?.Name ?? "";
        public string CandidatePreviewProvider => CandidatePreview?.ProviderText ?? "";
        public string CandidatePreviewSummary => CandidatePreview?.Summary ?? "";
        public string CandidatePreviewArgs => CandidatePreview?.ArgsText ?? "";
        public string CandidatePreviewFeatures => CandidatePreview?.Features.Summary ?? "";
        public string CandidatePreviewFixedArgs => CandidatePreview?.FixedArgsText ?? "";
        public string CandidatePreviewTunableArgs => CandidatePreview?.TunableArgsText ?? "";
        public bool IsCandidatePreviewSaved => _isCandidatePreviewSaved;
        public string CandidateSaveStatusText => IsCandidatePreviewSaved
            ? "Кандидат сохранён отдельно от файлов движка"
            : "Кандидат ещё не сохранён";

        public ICommand SaveCandidateCommand { get; }
        public ICommand RunSavedCandidateCommand { get; }
        public ICommand PreviewSavedCandidateCommand { get; }
        public ICommand DeleteSavedCandidateCommand { get; }
        public ICommand MakeCandidatePrimaryCommand { get; }
        public ICommand RestorePreviousCommand { get; }
        public bool CanRestorePrevious => !string.IsNullOrWhiteSpace(Settings.PreviousSelectedStrategy) &&
            Store.Find(Settings.PreviousSelectedStrategy) != null;
        public string PreviousStrategyText => CanRestorePrevious
            ? "Предыдущая стратегия: «" + Settings.PreviousSelectedStrategy + "»"
            : "Предыдущая стратегия для отката не сохранена";

        public bool IsGeneratingCandidates
        {
            get => _isGeneratingCandidates;
            private set
            {
                if (!Set(ref _isGeneratingCandidates, value)) return;
                Raise(nameof(CandidateGenerationVisible));
                (GenerateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelCandidateGenerationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (TestAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string CandidateGenerationText
        {
            get => _candidateGenerationText;
            private set
            {
                if (Set(ref _candidateGenerationText, value)) Raise(nameof(CandidateGenerationVisible));
            }
        }

        public bool CandidateGenerationVisible => IsGeneratingCandidates || GeneratedCandidates.Count > 0;

        public bool IsEvaluatingCandidates
        {
            get => _isEvaluatingCandidates;
            private set
            {
                if (!Set(ref _isEvaluatingCandidates, value)) return;
                Raise(nameof(CandidateEvaluationVisible));
                (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelCandidateEvaluationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (GenerateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (TestAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string CandidateEvaluationText
        {
            get => _candidateEvaluationText;
            private set
            {
                if (Set(ref _candidateEvaluationText, value)) Raise(nameof(CandidateEvaluationVisible));
            }
        }

        public double CandidateEvaluationProgressValue
        {
            get => _candidateEvaluationProgressValue;
            set => Set(ref _candidateEvaluationProgressValue, value);
        }

        public double CandidateEvaluationProgressMaximum
        {
            get => _candidateEvaluationProgressMaximum;
            set => Set(ref _candidateEvaluationProgressMaximum, value);
        }

        public string CandidateEvaluationProgressPercentText
        {
            get => _candidateEvaluationProgressPercentText;
            set => Set(ref _candidateEvaluationProgressPercentText, value);
        }

        public bool CandidateEvaluationProgressVisible
        {
            get => _candidateEvaluationProgressVisible;
            set => Set(ref _candidateEvaluationProgressVisible, value);
        }

        public bool CandidateEvaluationVisible => IsEvaluatingCandidates || CandidateEvaluations.Count > 0;
        public bool EvaluationHistoryVisible => EvaluationHistory.Count > 0;
        public string EvaluationHistoryCountText => EvaluationHistory.Count > 0
            ? $"Сохранено проверок: {EvaluationHistory.Count}"
            : "История проверок пуста";
        public bool SavedCandidatesVisible => SavedCandidates.Count > 0;

        public ICommand EvaluateCandidatesCommand { get; }
        public ICommand CancelCandidateEvaluationCommand { get; }
        public ICommand ExportCandidateReportCommand { get; }
        public ICommand ClearHistoryCommand { get; }

        public bool IsSelectedDefault => Selected != null &&
            Selected.Name.Equals(Settings.SelectedStrategy, StringComparison.OrdinalIgnoreCase);

        public string CountText => $"{View.Cast<object>().Count()} из {Store.Items.Count} стратегий";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (GenerateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsTestingAll
        {
            get => _isTestingAll;
            private set
            {
                if (Set(ref _isTestingAll, value))
                {
                    Raise(nameof(TestProgressVisible));
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelTestCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (GenerateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool TestProgressVisible => IsTestingAll || !string.IsNullOrWhiteSpace(_testSummary);

        public string TestProgressText
        {
            get => _testProgressText;
            set => Set(ref _testProgressText, value);
        }

        public double TestProgressValue
        {
            get => _testProgressValue;
            set => Set(ref _testProgressValue, value);
        }

        public double TestProgressMaximum
        {
            get => _testProgressMaximum;
            set => Set(ref _testProgressMaximum, value);
        }

        public bool TestProgressIndeterminate
        {
            get => _testProgressIndeterminate;
            set => Set(ref _testProgressIndeterminate, value);
        }

        public string TestProgressPercentText
        {
            get => _testProgressPercentText;
            set => Set(ref _testProgressPercentText, value);
        }

        public string TestSummary
        {
            get => _testSummary;
            private set
            {
                if (Set(ref _testSummary, value)) Raise(nameof(TestProgressVisible));
            }
        }

        public string TestSummaryKey
        {
            get => _testSummaryKey;
            private set => Set(ref _testSummaryKey, value);
        }

        public string Message
        {
            get => _message;
            private set
            {
                if (Set(ref _message, value)) Raise(nameof(MessageVisible));
            }
        }

        public bool MessageVisible => !string.IsNullOrWhiteSpace(_message);

        public ICommand RunCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand TestStrategyCommand { get; }
        public ICommand TestAllCommand { get; }
        public ICommand CancelTestCommand { get; }
        public ICommand OpenBatCommand { get; }
        public ICommand CopyArgsCommand { get; }
        public ICommand SetDefaultCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand UseRecommendedCommand { get; }
        public ICommand BuildCandidatePreviewCommand { get; }
        public ICommand GenerateCandidatesCommand { get; }
        public ICommand CancelCandidateGenerationCommand { get; }
        public ICommand PreviewGeneratedCandidateCommand { get; }

        // ------------------------------------------------------------------ логика

        private bool FilterItem(object item)
        {
            if (item is not StrategyInfo strategy) return false;

            if (OnlyRecommended && !strategy.IsRecommended) return false;

            if (CategoryIndex > 0 && !strategy.Category.Equals(Categories[CategoryIndex], StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var query = SearchText.Trim();
                var haystack = strategy.Name + " " + strategy.Description + " " + strategy.Category;
                if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            return true;
        }

        public void Refresh()
        {
            var keep = Selected?.Name;
            Store.Refresh();
            View.Refresh();

            Selected = Store.Find(keep) ?? Store.Recommended ?? Store.Items.FirstOrDefault();
            Raise(nameof(CountText));
            Raise(nameof(IsEngineReady));
            Raise(nameof(IsEngineMissing));
        }

        private void UseRecommended()
        {
            var recommended = Store.Recommended ?? Store.Items.FirstOrDefault();
            if (recommended == null) return;
            Selected = recommended;
            SetDefault();
        }

        private void BuildCandidatePreview()
        {
            if (Selected == null) return;
            CandidatePreview = StrategyCandidateFactory.CreatePreview(
                Selected,
                Settings.ProviderContext ?? new ProviderContext(),
                EngineService.GetGameFilterMode(Settings.EnginePath));
            Message = "Предпросмотр кандидата создан — обход не запускался, файлы не изменены";
        }

        private async Task GenerateCandidatesAsync()
        {
            if (IsGeneratingCandidates || IsTestingAll || IsBusy || Store.Items.Count == 0) return;

            IsGeneratingCandidates = true;
            CandidateGenerationText = "Собираю ограниченный набор вариантов…";
            GeneratedCandidates.Clear();
            CandidateEvaluations.Clear();
            CandidateEvaluationText = "";
            Raise(nameof(CandidateGenerationVisible));
            Raise(nameof(CandidateEvaluationVisible));
            (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            _candidateGenerationCts = new CancellationTokenSource();
            try
            {
                var sources = Store.Items
                    .Where(strategy => strategy.IsRecommended || strategy.TestState == StrategyTestState.Passed)
                    .ToList();
                var usedFallback = sources.Count == 0;
                if (usedFallback && Selected != null) sources.Add(Selected);
                if (sources.Count == 0)
                {
                    CandidateGenerationText = "Нет стратегии-основы для построения вариантов";
                    return;
                }

                var result = await Task.Run(() => StrategyCandidateGenerator.Generate(
                    sources,
                    Settings.ProviderContext ?? new ProviderContext(),
                    EngineService.GetGameFilterMode(Settings.EnginePath),
                    new StrategyCandidateGenerationOptions { MaxCandidates = 16, MaxVariantsPerSource = 4 },
                    _candidateGenerationCts.Token,
                    _evaluationHistory,
                    DiagnosticsHistoryStore.LoadLastDpiCheck()?.Observation), _candidateGenerationCts.Token).ConfigureAwait(true);

                foreach (var candidate in result.Candidates)
                {
                    GeneratedCandidates.Add(candidate);
                    CandidateEvaluations.Add(new StrategyCandidateEvaluation(candidate));
                }
                _candidateEvaluationView.Refresh();
                Raise(nameof(CandidateEvaluationVisible));
                (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ExportCandidateReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
                CandidateGenerationText = result.Candidates.Count == 0
                    ? "Варианты не найдены: в стратегиях нет разрешённых параметров"
                    : $"Собрано вариантов: {result.Candidates.Count}. Запуск и сохранение не выполнялись."
                      + (result.WasLimited ? " Достигнут лимит набора." : "")
                      + (usedFallback ? " Использована стратегия без результата тестирования." : "")
                      + " " + result.ProviderHeuristicText;
                Raise(nameof(CandidateGenerationVisible));
                Message = result.Candidates.Count > 0
                    ? "Набор кандидатов создан только в памяти — обход не запускался"
                    : "Не удалось собрать варианты кандидатов";
            }
            catch (OperationCanceledException)
            {
                CandidateGenerationText = "Сбор кандидатов отменён";
                Raise(nameof(CandidateGenerationVisible));
            }
            finally
            {
                _candidateGenerationCts?.Dispose();
                _candidateGenerationCts = null;
                IsGeneratingCandidates = false;
            }
        }

        private async Task EvaluateCandidatesAsync()
        {
            if (IsEvaluatingCandidates || IsGeneratingCandidates || IsTestingAll || IsBusy || CandidateEvaluations.Count == 0)
                return;

            const int repeats = 2;
            var total = CandidateEvaluations.Count * repeats;
            CandidateEvaluationProgressMaximum = Math.Max(1, total);
            CandidateEvaluationProgressValue = 0;
            CandidateEvaluationProgressPercentText = "0%";
            CandidateEvaluationProgressVisible = true;
            IsEvaluatingCandidates = true;
            CandidateEvaluationText = "Подготавливаю повторные проверки кандидатов…";
            _candidateEvaluationCts = new CancellationTokenSource();
            try
            {
                for (var index = 0; index < CandidateEvaluations.Count; index++)
                {
                    _candidateEvaluationCts.Token.ThrowIfCancellationRequested();
                    var evaluation = CandidateEvaluations[index];
                    for (var repeat = 1; repeat <= repeats; repeat++)
                    {
                        _candidateEvaluationCts.Token.ThrowIfCancellationRequested();
                        var done = index * repeats + repeat;
                        CandidateEvaluationProgressValue = done;
                        CandidateEvaluationProgressPercentText = $"{done * 100 / total:0}%";
                        CandidateEvaluationText = $"Проверяю вариант {index + 1} из {CandidateEvaluations.Count}, повтор {repeat} из {repeats}…";
                        evaluation.MarkTesting(repeat, repeats);
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_candidateEvaluationCts.Token);
                        attemptCts.CancelAfter(TimeSpan.FromSeconds(45));
                        var result = await Bypass.TestStrategyAsync(ToStrategyInfo(evaluation.Candidate), attemptCts.Token);
                        evaluation.AddResult(result, repeat, repeats);
                        _candidateEvaluationView.Refresh();
                        if (attemptCts.IsCancellationRequested && !_candidateEvaluationCts.Token.IsCancellationRequested)
                            break;
                    }
                    evaluation.Complete();
                    _candidateEvaluationView.Refresh();
                }

                var stable = CandidateEvaluations.Count(evaluation => evaluation.IsStable);
                CandidateEvaluationText = $"Проверено кандидатов: {CandidateEvaluations.Count}. Стабильных: {stable}. Лучший score: " +
                    (CandidateEvaluations.Count == 0 ? "—" : CandidateEvaluations.Max(evaluation => evaluation.Score).ToString());
                Message = "Проверка кандидатов завершена — лучший вариант не включён автоматически";
            }
            catch (OperationCanceledException)
            {
                CandidateEvaluationText = "Проверка кандидатов отменена. Состояние обхода восстанавливается после текущей пробы.";
                Message = "Проверка кандидатов отменена";
            }
            finally
            {
                CandidateEvaluationProgressVisible = false;
                foreach (var evaluation in CandidateEvaluations.Where(evaluation => evaluation.IsTesting))
                    evaluation.Complete();
                foreach (var evaluation in CandidateEvaluations.Where(evaluation => evaluation.RepeatCount > 0))
                    AppendHistory(StrategyEvaluationHistoryRecord.FromEvaluation(evaluation));
                _candidateEvaluationView.Refresh();
                _candidateEvaluationCts?.Dispose();
                _candidateEvaluationCts = null;
                IsEvaluatingCandidates = false;
            }
        }

        private void ExportCandidateReport()
        {
            if (CandidateEvaluations.Count == 0 && EvaluationHistory.Count == 0) return;

            var report = new StrategyEvaluationReport
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Provider = Settings.ProviderContext ?? new ProviderContext(),
                GeneratedCandidates = GeneratedCandidates.ToList(),
                CurrentEvaluations = CandidateEvaluations
                    .Where(evaluation => evaluation.RepeatCount > 0)
                    .Select(StrategyEvaluationHistoryRecord.FromEvaluation)
                    .ToList(),
                History = EvaluationHistory.ToList()
            };

            var dialog = new SaveFileDialog
            {
                Filter = "Отчёт автоконструктора (*.json)|*.json|Все файлы (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = "zapretgui-strategy-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                Title = "Экспорт отчёта автоконструктора"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, options));
                Message = "Отчёт автоконструктора сохранён: " + dialog.FileName;
            }
            catch (Exception ex)
            {
                Message = "Не удалось сохранить отчёт: " + ex.Message;
            }
        }

        private void AppendHistory(StrategyEvaluationHistoryRecord record)
        {
            if (record == null) return;
            if (!StrategyEvaluationHistoryStore.TryAppend(record)) return;

            lock (_evaluationHistory)
            {
                _evaluationHistory.Insert(0, record);
                while (_evaluationHistory.Count > 200) _evaluationHistory.RemoveAt(_evaluationHistory.Count - 1);
            }

            void UpdateUi()
            {
                EvaluationHistory.Insert(0, record);
                while (EvaluationHistory.Count > 50) EvaluationHistory.RemoveAt(EvaluationHistory.Count - 1);
                Raise(nameof(EvaluationHistoryVisible));
                Raise(nameof(EvaluationHistoryCountText));
                (ExportCandidateReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(UpdateUi));
            }
            else
            {
                UpdateUi();
            }
        }

        private void ClearHistory()
        {
            var answer = System.Windows.MessageBox.Show(
                "Очистить сохранённую историю проверок стратегий?",
                "Очистка истории", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            StrategyEvaluationHistoryStore.Clear();
            lock (_evaluationHistory)
            {
                _evaluationHistory.Clear();
            }
            EvaluationHistory.Clear();
            Raise(nameof(EvaluationHistoryVisible));
            Raise(nameof(EvaluationHistoryCountText));
            (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportCandidateReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Message = "История проверок стратегий очищена";
        }

        private void CancelCandidateEvaluation()
        {
            if (IsEvaluatingCandidates) _candidateEvaluationCts?.Cancel();
        }

        private static StrategyInfo ToStrategyInfo(StrategyCandidate candidate)
        {
            return new StrategyInfo
            {
                Name = candidate.Name,
                Args = candidate.Args.ToList(),
                Description = candidate.MutationDescription
            };
        }

        private void CancelCandidateGeneration()
        {
            if (IsGeneratingCandidates) _candidateGenerationCts?.Cancel();
        }

        private void PreviewGeneratedCandidate(object? parameter)
        {
            if (parameter is not StrategyCandidate candidate) return;
            CandidatePreview = candidate;
            Message = "Показан предпросмотр кандидата — запуск и сохранение не выполнялись";
        }

        private void PreviewSavedCandidate(object? parameter)
        {
            if (parameter is not SavedStrategyCandidate saved) return;
            CandidatePreview = saved.ToCandidate();
            Message = "Показан сохранённый кандидат — запуск требует отдельного подтверждения";
        }

        private void SaveCandidate()
        {
            if (CandidatePreview == null) return;
            var answer = System.Windows.MessageBox.Show(
                "Сохранить этот кандидат отдельно от оригинальных стратегий? Он будет записан только в профиль Zapret GUI и не запустится автоматически.",
                "Сохранение кандидата", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            if (!StrategyCandidateStore.TrySave(CandidatePreview, out var saved))
            {
                Message = "Не удалось сохранить кандидата";
                return;
            }

            ReloadSavedCandidates();
            _isCandidatePreviewSaved = true;
            Raise(nameof(IsCandidatePreviewSaved));
            Raise(nameof(CandidateSaveStatusText));
            (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (MakeCandidatePrimaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Message = $"Кандидат «{saved.DisplayName}» сохранён отдельно от файлов движка";
        }

        private async Task RunSavedCandidateAsync()
        {
            if (CandidatePreview == null || !IsCandidatePreviewSaved || IsBusy) return;
            if (!Shell.IsAdmin())
            {
                Message = "Нужны права администратора — перезапустите приложение от имени администратора";
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                "Запустить сохранённый кандидат временно? Текущий обход будет остановлен, служба и основная стратегия не изменятся.",
                "Запуск кандидата", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            IsBusy = true;
            Message = "Запускаю сохранённый кандидат…";
            try
            {
                var result = await Bypass.StartAsync(
                    ToStrategyInfo(CandidatePreview),
                    EngineService.GetGameFilterMode(Settings.EnginePath),
                    Settings.ShowWinwsConsole,
                    testMode: true);
                Message = result.Ok
                    ? result.Message + ". Основная стратегия и служба не изменены."
                    : result.Message;
                if (result.Ok) _main.Home.RefreshStatus();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void MakeCandidatePrimary()
        {
            if (CandidatePreview == null || !IsCandidatePreviewSaved) return;
            var saved = StrategyCandidateStore.Load().FirstOrDefault(item =>
                item.Fingerprint.Equals(CandidatePreview.Fingerprint, StringComparison.Ordinal));
            if (saved == null) return;

            var answer = System.Windows.MessageBox.Show(
                $"Сделать «{saved.DisplayName}» основной стратегией? Служба не будет изменена и не будет запущена автоматически. Текущая стратегия сохранится для отката.",
                "Назначение основной стратегии", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            var previous = Settings.SelectedStrategy;
            if (!string.Equals(previous, saved.StrategyName, StringComparison.OrdinalIgnoreCase))
                Settings.PreviousSelectedStrategy = previous;
            Settings.SelectedStrategy = saved.StrategyName;
            SettingsStore.Save(Settings);
            _main.Home.ReloadFromEngine();
            Refresh();
            Selected = Store.Find(saved.StrategyName) ?? Selected;
            Raise(nameof(CanRestorePrevious));
            Raise(nameof(PreviousStrategyText));
            (RestorePreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Message = $"«{saved.DisplayName}» назначена основной. Обход не запускался.";
        }

        private void RestorePrevious()
        {
            if (!CanRestorePrevious) return;
            var previous = Settings.PreviousSelectedStrategy;
            var current = Settings.SelectedStrategy;
            var answer = System.Windows.MessageBox.Show(
                $"Вернуть предыдущую стратегию «{previous}»? Служба и обход не будут изменены автоматически.",
                "Откат стратегии", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            Settings.SelectedStrategy = previous;
            Settings.PreviousSelectedStrategy = current;
            SettingsStore.Save(Settings);
            _main.Home.ReloadFromEngine();
            Refresh();
            Selected = Store.Find(previous) ?? Selected;
            Raise(nameof(CanRestorePrevious));
            Raise(nameof(PreviousStrategyText));
            (RestorePreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Message = $"Восстановлена стратегия «{previous}». Обход не запускался.";
        }

        private void DeleteSavedCandidate(object? parameter)
        {
            if (parameter is not SavedStrategyCandidate saved) return;
            if (string.Equals(Settings.SelectedStrategy, saved.StrategyName, StringComparison.OrdinalIgnoreCase))
            {
                Message = "Нельзя удалить основную стратегию. Сначала верните предыдущую или выберите другую.";
                return;
            }
            var wasPrevious = string.Equals(Settings.PreviousSelectedStrategy, saved.StrategyName, StringComparison.OrdinalIgnoreCase);
            var answer = System.Windows.MessageBox.Show(
                $"Удалить сохранённый кандидат «{saved.DisplayName}»? Оригинальная стратегия не будет затронута.",
                "Удаление кандидата", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            if (!StrategyCandidateStore.Delete(saved.Id))
            {
                Message = "Не удалось удалить кандидата";
                return;
            }
            if (wasPrevious)
            {
                Settings.PreviousSelectedStrategy = "";
                SettingsStore.Save(Settings);
                Raise(nameof(CanRestorePrevious));
                Raise(nameof(PreviousStrategyText));
                (RestorePreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            SavedCandidates.Remove(saved);
            if (CandidatePreview != null && CandidatePreview.Fingerprint == saved.Fingerprint)
            {
                _isCandidatePreviewSaved = false;
                Raise(nameof(IsCandidatePreviewSaved));
                Raise(nameof(CandidateSaveStatusText));
                (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (MakeCandidatePrimaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            _main.Home.ReloadFromEngine();
            Refresh();
            Message = "Сохранённый кандидат удалён";
        }

        private void ReloadSavedCandidates()
        {
            SavedCandidates.Clear();
            foreach (var saved in StrategyCandidateStore.Load()) SavedCandidates.Add(saved);
            Raise(nameof(SavedCandidatesVisible));
        }

        private void SetDefault()
        {
            if (Selected == null) return;
            if (Selected.Category.Equals("АВТОКОНСТРУКТОР", StringComparison.OrdinalIgnoreCase))
            {
                var saved = StrategyCandidateStore.Load().FirstOrDefault(candidate =>
                    candidate.StrategyName.Equals(Selected.Name, StringComparison.OrdinalIgnoreCase));
                if (saved != null)
                {
                    CandidatePreview = saved.ToCandidate();
                    MakeCandidatePrimary();
                }
                return;
            }
            Settings.SelectedStrategy = Selected.Name;
            SettingsStore.Save(Settings);
            Message = $"«{Selected.Name}» назначена основной стратегией";
            _main.Home.ReloadFromEngine();
            Raise(nameof(IsSelectedDefault));
        }

        private void CopyArgs()
        {
            if (Selected == null) return;
            try
            {
                System.Windows.Clipboard.SetText(Selected.ShortArgs);
                Message = "Команда запуска скопирована в буфер обмена";
            }
            catch (Exception ex) { Message = "Не удалось скопировать: " + ex.Message; }
        }

        private async Task TestStrategyAsync(object? parameter)
        {
            if (parameter is not StrategyInfo strategy || IsTestingAll || IsBusy) return;

            IsTestingAll = true;
            TestProgressValue = 0;
            TestProgressMaximum = 8;
            TestProgressIndeterminate = false;
            TestProgressPercentText = "0%";
            TestProgressText = $"Проверяю стратегию «{strategy.Name}»…";
            strategy.SetTestStarted();
            try
            {
                var progress = new Progress<string>(text =>
                {
                    if (text.StartsWith("CONNECTION_PROGRESS:", StringComparison.Ordinal))
                    {
                        var parts = text.Substring("CONNECTION_PROGRESS:".Length).Split(" — ", 2);
                        var numbers = parts[0].Split('/');
                        if (numbers.Length == 2 && int.TryParse(numbers[0], out var cur) && int.TryParse(numbers[1], out var tot))
                        {
                            TestProgressValue = cur;
                            TestProgressMaximum = Math.Max(1, tot);
                            TestProgressPercentText = $"{TestProgressValue / TestProgressMaximum * 100:0}%";
                        }
                        if (parts.Length > 1) TestProgressText = $"«{strategy.Name}»: {parts[1]}…";
                    }
                    else
                    {
                        TestProgressText = text;
                    }
                });
                var result = await Bypass.TestStrategyAsync(strategy, default, progress);
                strategy.SetTestResult(result);
                TestProgressValue = TestProgressMaximum;
                TestProgressPercentText = "100%";
                AppendHistory(StrategyEvaluationHistoryRecord.FromTestResult(
                    strategy, result, Settings.ProviderContext ?? new ProviderContext()));
                TestSummary = result.Started
                    ? $"«{strategy.Name}»: {result.SummaryText}"
                    : $"«{strategy.Name}»: {result.ErrorMessage}";
                TestSummaryKey = result.IsSuitable ? "Success" : result.Started ? "Warning" : "Danger";
                Message = result.IsSuitable
                    ? "Стратегия проходит основные проверки YouTube и Discord"
                    : "Стратегия не прошла все основные проверки — попробуйте другую";
            }
            finally
            {
                TestProgressText = "";
                IsTestingAll = false;
            }
        }

        /// <summary>Проверяет все стратегии по очереди и возвращает лучший результат.</summary>
        public async Task<StrategyTestBatchResult?> TestAllAsync(IProgress<string>? externalProgress = null)
        {
            if (IsTestingAll || IsBusy || Store.Items.Count == 0) return null;

            IsTestingAll = true;
            _testCts = new CancellationTokenSource();
            TestSummary = "";
            TestSummaryKey = "Info";
            var results = new List<StrategyTestResult>();
            try
            {
                var total = Store.Items.Count;
                TestProgressValue = 0;
                TestProgressMaximum = Math.Max(1, total);
                TestProgressIndeterminate = false;
                TestProgressPercentText = "0%";
                for (var index = 0; index < total; index++)
                {
                    _testCts.Token.ThrowIfCancellationRequested();
                    var strategy = Store.Items[index];
                    TestProgressText = $"Проверяю стратегию {index + 1} из {total}: «{strategy.Name}»…";
                    externalProgress?.Report(TestProgressText);
                    strategy.SetTestStarted();

                    var result = await Bypass.TestStrategyAsync(strategy, _testCts.Token);
                    strategy.SetTestResult(result);
                    AppendHistory(StrategyEvaluationHistoryRecord.FromTestResult(
                        strategy, result, Settings.ProviderContext ?? new ProviderContext()));
                    results.Add(result);
                    TestProgressValue = index + 1;
                    TestProgressPercentText = $"{TestProgressValue / TestProgressMaximum * 100:0}%";

                    var passed = results.Count(r => r.IsSuitable);
                    TestSummary = $"Проверено: {index + 1} из {total}. Подходящих стратегий: {passed}";
                    TestSummaryKey = passed > 0 ? "Success" : "Warning";
                }

                var batch = new StrategyTestBatchResult { Results = results };
                var best = batch.Best;
                if (best != null)
                {
                    if (best.IsSuitable)
                    {
                        // Одного прохода по трём контрольным ресурсам недостаточно,
                        // чтобы менять провайдерскую рекомендацию. Оставляем рабочую
                        // стратегию без изменений; глубокая проверка дополнительно
                        // учитывает DPI и повторяемость.
                        TestSummary = $"Лидер первичного теста: «{best.Strategy.Name}» — {best.PassedCount}/{best.Checks.Count} проверок. Активная стратегия не изменена.";
                    }
                    else
                    {
                        TestSummary = $"Лучшая стратегия: «{best.Strategy.Name}» — {best.PassedCount}/{best.Checks.Count} проверок. Полного успеха нет, выбор не изменён.";
                    }
                    TestSummaryKey = best.IsSuitable ? "Success" : "Warning";
                }
                else
                {
                    TestSummary = "Ни одна стратегия не запустилась или не ответила на проверки";
                    TestSummaryKey = "Danger";
                }
                return batch;
            }
            catch (OperationCanceledException)
            {
                TestSummary = "Проверка стратегий отменена";
                TestSummaryKey = "Warning";
                return new StrategyTestBatchResult { Cancelled = true, Results = results };
            }
            finally
            {
                _testCts?.Dispose();
                _testCts = null;
                TestProgressText = "";
                IsTestingAll = false;
            }
        }

        public void RefreshTheme()
        {
            foreach (var strategy in Store.Items) strategy.RefreshTheme();
            Raise(nameof(TestSummaryKey));
        }

        public void SelectAsDefault(StrategyInfo strategy)
        {
            if (!Store.Items.Contains(strategy)) return;
            Selected = strategy;
            SetDefault();
        }

        public async Task ApplyStrategyAsync(StrategyInfo strategy)
        {
            if (!Store.Items.Contains(strategy) || IsBusy || IsTestingAll || IsGeneratingCandidates || IsEvaluatingCandidates)
                return;
            Selected = strategy;
            await RunAsync(strategy);
        }

        private async Task RunAsync(object? parameter)
        {
            if (parameter is StrategyInfo strategy)
                Selected = strategy;
            if (Selected == null) return;
            if (!Shell.IsAdmin())
            {
                Message = "Нужны права администратора — перезапустите приложение от имени администратора";
                return;
            }

            IsBusy = true;
            Message = $"Запускаю «{Selected.Name}»…";
            try
            {
                var result = await Bypass.StartAsync(Selected, EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
                Message = result.Message;
                if (result.Ok)
                {
                    Settings.SelectedStrategy = Selected.Name;
                    SettingsStore.Save(Settings);
                    _main.Home.ReloadFromEngine();
                }
            }
            finally { IsBusy = false; }
        }

        private async Task InstallServiceAsync()
        {
            if (Selected == null) return;
            if (!Shell.IsAdmin())
            {
                Message = "Нужны права администратора";
                return;
            }

            IsBusy = true;
            Message = $"Устанавливаю службу со стратегией «{Selected.Name}»…";
            try
            {
                var result = await Bypass.InstallServiceAsync(Selected, EngineService.GetGameFilterMode(Settings.EnginePath));
                Message = result.Message;
                _main.Home.RefreshStatus();
            }
            finally { IsBusy = false; }
        }
    }
}
