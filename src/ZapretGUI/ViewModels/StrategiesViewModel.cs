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
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class BuilderPreset
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string DesyncMode { get; init; } = "";
        public string SplitPos { get; init; } = "";
        public string FakeSni { get; init; } = "";
        public string Ttl { get; init; } = "";
        public string Fooling { get; init; } = "";
        public bool UseMultisplit { get; init; }
        public bool UseGameUdp { get; init; }
        public bool UseHostlist { get; init; }
        public bool UseIpSet { get; init; }
        public string DisplayText => $"{Name} — {Description}";
    }

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
        private double _lastProgressValue;
        private double _testProgressMaximum = 1;
        private bool _testProgressIndeterminate;
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

        // --- Раздел 1: Интеллектуальный подбор и авто-настройка ---
        private int _selectedSubTabIndex;
        private StrategyInfo? _bestEmpiricalStrategy;

        // Конструктор параметров
        private string _builderStrategyName = "custom_split2_google";
        private string _builderDesyncMode = "split2";
        private string _builderSplitPos = "midsld";
        private string _builderFakeSni = "www.google.com";
        private string _builderTtl = "auto";
        private string _builderFooling = "badsum";
        private bool _builderUseMultisplit = true;
        private bool _builderUseGameUdp = true;
        private bool _builderUseHostlist = true;
        private bool _builderUseIpSet = true;
        private string _builderTestStatus = "";
        private string _builderTestStatusKey = "Info";
        private BuilderPreset? _selectedBuilderPreset;

        // Контрольные адреса
        private string _newTargetName = "";
        private string _newTargetUrl = "";

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
            SubTabs = new[] { "Каталог стратегий", "Конструктор параметров", "Умный автоподбор", "Пул TLS SNI", "Контрольные адреса" };

            // Инициализация контрольных адресов
            TargetEndpoints = new ObservableCollection<MonitorTarget>(main.Settings.MonitorTargets);

            RunCommand = new AsyncRelayCommand(RunAsync,
                parameter => (parameter is StrategyInfo || Selected != null) && !IsBusy && !IsTestingAll && !IsGeneratingCandidates && !IsEvaluatingCandidates && !IsAutoTuningRunning);
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => Selected != null && !IsBusy && !IsTestingAll && !IsGeneratingCandidates);
            TestStrategyCommand = new AsyncRelayCommand(TestStrategyAsync, _ => !IsTestingAll && !IsBusy && !IsGeneratingCandidates && !IsEvaluatingCandidates && !IsAutoTuningRunning);
            TestAllCommand = new AsyncRelayCommand(_ => TestAllAsync(), _ => !IsTestingAll && !IsBusy && !IsGeneratingCandidates && !IsEvaluatingCandidates && !IsAutoTuningRunning && Store.Items.Count > 0);
            CancelTestCommand = new RelayCommand(() => _testCts?.Cancel(), () => IsTestingAll);
            OpenBatCommand = new RelayCommand(() => { if (Selected != null) Shell.OpenInNotepad(Selected.FullPath); });
            CopyArgsCommand = new RelayCommand(CopyArgs, () => Selected != null);
            SetDefaultCommand = new AsyncRelayCommand(SetDefaultAsync, () => Selected != null && !IsBusy && !IsTestingAll);
            RefreshCommand = new RelayCommand(Refresh);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(Store.Folder));
            UseRecommendedCommand = new RelayCommand(UseRecommended);
            ApplyBestRecommendedCommand = new AsyncRelayCommand(ApplyBestRecommendedAsync, () => BestEmpiricalStrategy != null && !IsBusy && !IsTestingAll);

            // Команды конструктора параметров
            BuilderPresets = new List<BuilderPreset>
            {
                new() { Id="preset-standard", Name="Стандарт", Description="fake,split2 + Google SNI, multisplit — баланс скорости и обхода", DesyncMode="fake,split2", SplitPos="midsld", FakeSni="www.google.com", Ttl="auto", Fooling="badsum", UseMultisplit=true, UseGameUdp=true, UseHostlist=true, UseIpSet=true },
                new() { Id="preset-aggressive", Name="Агрессивный", Description="disorder2 + sniext, TTL 2 — для строгих ТСПУ", DesyncMode="disorder2", SplitPos="sniext", FakeSni="www.microsoft.com", Ttl="2", Fooling="badsum,ts", UseMultisplit=true, UseGameUdp=true, UseHostlist=true, UseIpSet=true },
                new() { Id="preset-light", Name="Лёгкий", Description="только fake — минимальная нагрузка, для слабых ТСПУ", DesyncMode="fake", SplitPos="none", FakeSni="www.cloudflare.com", Ttl="auto", Fooling="none", UseMultisplit=false, UseGameUdp=false, UseHostlist=true, UseIpSet=false },
                new() { Id="preset-gaming", Name="Игровой", Description="split2 + UDP 50000-65535 — голос Discord и игры", DesyncMode="split2", SplitPos="1", FakeSni="yandex.ru", Ttl="auto", Fooling="badseq", UseMultisplit=true, UseGameUdp=true, UseHostlist=true, UseIpSet=true },
            };
            SelectedBuilderPreset = BuilderPresets[0];

            BuilderTestCommand = new AsyncRelayCommand(BuilderTestAsync, () => !IsBusy && !IsTestingAll && !IsAutoTuningRunning);
            BuilderSaveCommand = new RelayCommand(BuilderSave, () => !string.IsNullOrWhiteSpace(BuilderStrategyName) && IsBuilderNameValid);
            BuilderApplyCommand = new AsyncRelayCommand(BuilderApplyAsync, () => !IsBusy && !IsTestingAll && !IsAutoTuningRunning && !string.IsNullOrWhiteSpace(BuilderStrategyName) && IsBuilderNameValid);
            ApplyBuilderPresetCommand = new RelayCommand(() => { if (SelectedBuilderPreset != null) ApplyBuilderPreset(SelectedBuilderPreset); });
            LoadBuilderFromSelectedCommand = new RelayCommand(LoadBuilderFromSelected, () => Selected != null);
            CopyBuilderArgsCommand = new RelayCommand(CopyBuilderArgs, () => BuilderGeneratedArgs.Count > 0);


            // Команды умного автоподбора
            StartSmartAutoTuningCommand = new AsyncRelayCommand(StartSmartAutoTuningAsync, () => !IsAutoTuningRunning && !IsBusy && !IsTestingAll);
            CancelSmartAutoTuningCommand = new RelayCommand(CancelSmartAutoTuning, () => IsAutoTuningRunning);
            ApplyWinnerStrategyCommand = new AsyncRelayCommand(ApplyWinnerStrategyAsync, () => WinnerCandidate != null && !IsBusy && !IsAutoTuningRunning);

            // Команды контрольных адресов
            AddTargetCommand = new RelayCommand(AddTarget, () => !string.IsNullOrWhiteSpace(NewTargetUrl));
            RemoveTargetCommand = new RelayCommand(RemoveTarget);
            ToggleTargetCommand = new RelayCommand(ToggleTarget);

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
            ClearSwitchHistoryCommand = new RelayCommand(ClearSwitchHistory, () => SwitchHistory.Count > 0);
            SaveCandidateCommand = new RelayCommand(SaveCandidate, () => CandidatePreview != null);
            RunSavedCandidateCommand = new AsyncRelayCommand(RunSavedCandidateAsync,
                () => CandidatePreview != null && IsCandidatePreviewSaved && !IsBusy && !IsTestingAll && !IsGeneratingCandidates && !IsEvaluatingCandidates);
            PreviewSavedCandidateCommand = new RelayCommand(PreviewSavedCandidate);
            DeleteSavedCandidateCommand = new RelayCommand(DeleteSavedCandidate);
            MakeCandidatePrimaryCommand = new RelayCommand(MakeCandidatePrimary, () => CandidatePreview != null && IsCandidatePreviewSaved);
            RestorePreviousCommand = new RelayCommand(RestorePrevious, () => CanRestorePrevious);

            // Команды TLS SNI пула и автоподбора
            TestSniPoolCommand = new AsyncRelayCommand(TestSniPoolAsync, () => !IsTestingSniPool && !IsBusy);
            AutoSelectBestSniCommand = new AsyncRelayCommand(AutoSelectBestSniAsync, () => !IsTestingSniPool && !IsBusy);
            RotateToNextSniCommand = new RelayCommand(RotateToNextSni);

            foreach (var saved in StrategyCandidateStore.Load()) SavedCandidates.Add(saved);
            foreach (var record in _evaluationHistory.Take(50)) EvaluationHistory.Add(record);
            foreach (var rec in StrategySwitchHistoryStore.Load().Take(20)) SwitchHistory.Add(rec);
        }

        public StrategyStore Store => _main.Strategies;
        public ICollectionView View { get; }
        public ICollectionView CandidateEvaluationsView => _candidateEvaluationView;
        public string[] Categories { get; }
        public string[] SubTabs { get; }

        public int SelectedSubTabIndex
        {
            get => _selectedSubTabIndex;
            set
            {
                if (Set(ref _selectedSubTabIndex, Math.Clamp(value, 0, SubTabs.Length - 1)))
                {
                    Raise(nameof(IsCatalogTabVisible));
                    Raise(nameof(IsBuilderTabVisible));
                    Raise(nameof(IsAutoTunerTabVisible));
                    Raise(nameof(IsSniPoolTabVisible));
                    Raise(nameof(IsTargetsTabVisible));
                }
            }
        }

        public bool IsCatalogTabVisible => SelectedSubTabIndex == 0;
        public bool IsBuilderTabVisible => SelectedSubTabIndex == 1;
        public bool IsAutoTunerTabVisible => SelectedSubTabIndex == 2;
        public bool IsSniPoolTabVisible => SelectedSubTabIndex == 3;
        public bool IsTargetsTabVisible => SelectedSubTabIndex == 4;

        public AppSettings Settings => _main.Settings;
        public BypassController Bypass => _main.Bypass;

        // ------------------------------------------------------------------ Умный глубокий автоподбор
        private bool _isAutoTuningRunning;
        private string _autoTuningStatusText = "Готов к запуску глубокого автоподбора";
        private double _autoTuningProgressValue;
        private double _autoTuningProgressMaximum = 12;
        private string _autoTuningProgressPercentText = "0%";
        private SavedStrategyCandidate? _winnerCandidate;
        private CancellationTokenSource? _autoTuningCts;

        public ObservableCollection<AutoTunerStepResult> AutoTuningResults { get; } = new();

        public bool IsAutoTuningRunning
        {
            get => _isAutoTuningRunning;
            private set
            {
                if (Set(ref _isAutoTuningRunning, value))
                {
                    (StartSmartAutoTuningCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelSmartAutoTuningCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyWinnerStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string AutoTuningStatusText
        {
            get => _autoTuningStatusText;
            private set => Set(ref _autoTuningStatusText, value);
        }

        public double AutoTuningProgressValue
        {
            get => _autoTuningProgressValue;
            private set => Set(ref _autoTuningProgressValue, value);
        }

        public double AutoTuningProgressMaximum
        {
            get => _autoTuningProgressMaximum;
            private set => Set(ref _autoTuningProgressMaximum, value);
        }

        public string AutoTuningProgressPercentText
        {
            get => _autoTuningProgressPercentText;
            private set => Set(ref _autoTuningProgressPercentText, value);
        }

        public SavedStrategyCandidate? WinnerCandidate
        {
            get => _winnerCandidate;
            private set
            {
                if (Set(ref _winnerCandidate, value))
                {
                    Raise(nameof(HasWinnerCandidate));
                    Raise(nameof(WinnerCandidateTitle));
                    (ApplyWinnerStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasWinnerCandidate => WinnerCandidate != null;
        public string WinnerCandidateTitle => WinnerCandidate != null
            ? $"🏆 Лучший результат: {WinnerCandidate.DisplayName}"
            : "";

        public IReadOnlyList<SniCandidate> SniCandidates => SniFakePoolManager.PredefinedSniPool;

        public SniCandidate? SelectedSniCandidate
        {
            get => SniFakePoolManager.PredefinedSniPool.FirstOrDefault(c => string.Equals(c.Domain, Settings.SelectedFakeSni, StringComparison.OrdinalIgnoreCase))
                   ?? SniFakePoolManager.PredefinedSniPool[0];
            set
            {
                if (value != null)
                {
                    Settings.SelectedFakeSni = value.Domain;
                    SettingsStore.Save(Settings);
                    Raise(nameof(SelectedSniCandidate));
                    Raise(nameof(SelectedFakeSni));
                    Message = $"Выбран TLS SNI фейк: {value.DisplayText}";
                }
            }
        }

        public string SelectedFakeSni
        {
            get => Settings.SelectedFakeSni;
            set
            {
                Settings.SelectedFakeSni = value ?? "gosuslugi.ru";
                SettingsStore.Save(Settings);
                Raise(nameof(SelectedFakeSni));
                Raise(nameof(SelectedSniCandidate));
            }
        }

        public bool AutoSniRotationEnabled
        {
            get => Settings.AutoSniRotationEnabled;
            set
            {
                Settings.AutoSniRotationEnabled = value;
                SettingsStore.Save(Settings);
                Raise(nameof(AutoSniRotationEnabled));
            }
        }

        public ObservableCollection<SniTestResult> SniTestResults { get; } = new();

        private bool _isTestingSniPool;
        private string _sniTestingStatusText = "Тестирование пула SNI ещё не выполнялось";
        private SniTestResult? _winnerSni;

        public bool IsTestingSniPool
        {
            get => _isTestingSniPool;
            private set
            {
                if (Set(ref _isTestingSniPool, value))
                {
                    (TestSniPoolCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (AutoSelectBestSniCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string SniTestingStatusText
        {
            get => _sniTestingStatusText;
            private set => Set(ref _sniTestingStatusText, value);
        }

        public SniTestResult? WinnerSni
        {
            get => _winnerSni;
            private set
            {
                if (Set(ref _winnerSni, value))
                {
                    Raise(nameof(HasWinnerSni));
                }
            }
        }

        public bool HasWinnerSni => _winnerSni != null;
        public bool HasSniResults => SniTestResults.Count > 0;

        public ICommand StartSmartAutoTuningCommand { get; }
        public ICommand CancelSmartAutoTuningCommand { get; }
        public ICommand ApplyWinnerStrategyCommand { get; }

        public ICommand TestSniPoolCommand { get; }
        public ICommand AutoSelectBestSniCommand { get; }
        public ICommand RotateToNextSniCommand { get; }

        private async Task StartSmartAutoTuningAsync()
        {
            if (IsAutoTuningRunning) return;
            IsAutoTuningRunning = true;
            AutoTuningResults.Clear();
            WinnerCandidate = null;
            AutoTuningStatusText = "Запуск глубокого многопроходного автоподбора…";
            AutoTuningProgressValue = 0;
            AutoTuningProgressMaximum = SmartStrategyAutoTuner.Hypotheses.Count;
            AutoTuningProgressPercentText = "0%";
            _autoTuningCts = new CancellationTokenSource();
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Умный автоподбор", "Глубокий перебор гипотез DPI — не закрывайте окно", "Подготовка…", 0, false, true, () => _autoTuningCts?.Cancel())); } catch {}

            var progress = new Progress<AutoTunerProgress>(p =>
            {
                AutoTuningProgressValue = p.CurrentStep;
                AutoTuningProgressMaximum = p.TotalSteps;
                AutoTuningProgressPercentText = p.TotalSteps > 0 ? $"{(int)((double)p.CurrentStep / p.TotalSteps * 100)}%" : "0%";
                AutoTuningStatusText = p.StatusMessage;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update(p.StatusMessage, $"{p.CurrentStep}/{p.TotalSteps} гипотез", p.TotalSteps > 0 ? (double)p.CurrentStep / p.TotalSteps * 100 : 0, false)); } catch {}
            });

            try
            {
                var (ok, msg, winner, results) = await SmartStrategyAutoTuner.RunDeepAutoTuningAsync(
                    Store.Folder,
                    _main.Bypass,
                    Store,
                    TargetEndpoints,
                    _main.Settings.ProviderContext,
                    progress,
                    step => Application.Current?.Dispatcher?.Invoke(() => AutoTuningResults.Insert(0, step)),
                    _autoTuningCts.Token);

                AutoTuningStatusText = msg;
                WinnerCandidate = winner;

                if (ok && winner != null)
                {
                    Refresh();
                    _main.Home.ShowSuccess($"Подобран оптимальный пресет: {winner.DisplayName}");
                }
            }
            catch (OperationCanceledException)
            {
                AutoTuningStatusText = "Автоподбор отменён пользователем.";
            }
            catch (Exception ex)
            {
                AutoTuningStatusText = "Ошибка автоподбора: " + ex.Message;
            }
            finally
            {
                IsAutoTuningRunning = false;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        private void CancelSmartAutoTuning()
        {
            _autoTuningCts?.Cancel();
        }

        private async Task ApplyWinnerStrategyAsync()
        {
            if (WinnerCandidate == null) return;
            var strat = WinnerCandidate.ToStrategyInfo();
            var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
            var status = _main.Bypass.GetStatus();
            if (status.IsRunning)
            {
                var res = await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                if (!res.Ok)
                {
                    Message = res.Message;
                    _main.Home.ShowError(res.Message);
                    return;
                }
            }
            Settings.SelectedStrategy = strat.Name;
            SettingsStore.Save(Settings);

            _main.Home.RefreshStatus();
            _main.Home.ShowSuccess($"Стратегия «{strat.Name}» установлена как основная и применена.");
        }

        // ------------------------------------------------------------------ Интеллектуальный подбор
        public StrategyInfo? BestEmpiricalStrategy
        {
            get => _bestEmpiricalStrategy ?? Store.Recommended;
            private set
            {
                if (Set(ref _bestEmpiricalStrategy, value))
                {
                    Raise(nameof(HasBestRecommendation));
                    Raise(nameof(BestStrategyRecommendationText));
                    Raise(nameof(BestStrategyDetailsText));
                    (ApplyBestRecommendedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasBestRecommendation => BestEmpiricalStrategy != null;

        public string BestStrategyRecommendationText => BestEmpiricalStrategy != null
            ? $"Рекомендовано для вашей сети: «{BestEmpiricalStrategy.Name}»"
            : "Проверьте стратегии для подбора лучшей под вашего провайдера";

        public string BestStrategyDetailsText
        {
            get
            {
                if (BestEmpiricalStrategy?.TestResult != null)
                {
                    var r = BestEmpiricalStrategy.TestResult;
                    return $"{r.PassedCount}/{r.Checks.Count} проверок OK · средняя задержка {r.AverageLatencyMs} мс";
                }
                return "Базовая проверенная стратегия с максимальной совместимостью";
            }
        }

        public ICommand ApplyBestRecommendedCommand { get; }

        // ------------------------------------------------------------------ Визуальный конструктор параметров winws
        public ObservableCollection<string> BuilderDesyncModes { get; } = new()
        {
            "fake,split2", "fake,disorder2", "split2", "disorder2", "fake", "fakedsni", "multisplit", "disorder", "split", "none"
        };

        public ObservableCollection<string> BuilderSplitPositions { get; } = new()
        {
            "1", "2", "3", "sniext", "midsld", "host", "none"
        };

        public ObservableCollection<string> BuilderFakeSnis { get; } = new()
        {
            "www.google.com", "www.microsoft.com", "www.cloudflare.com", "yandex.ru", "none"
        };

        public ObservableCollection<string> BuilderTtls { get; } = new()
        {
            "auto", "1", "2", "3", "4", "5", "8", "12"
        };

        public ObservableCollection<string> BuilderFoolings { get; } = new()
        {
            "ts", "badsum,ts", "badsum", "badseq", "md5sig", "datanoack", "none"
        };

        public string BuilderStrategyName
        {
            get => _builderStrategyName;
            set
            {
                if (Set(ref _builderStrategyName, value ?? ""))
                {
                    Raise(nameof(BuilderNameValidationText));
                    Raise(nameof(IsBuilderNameValid));
                    (BuilderSaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (BuilderApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string BuilderDesyncMode
        {
            get => _builderDesyncMode;
            set { if (Set(ref _builderDesyncMode, value ?? "split2")) RaiseBuilderPreview(); }
        }

        public string BuilderSplitPos
        {
            get => _builderSplitPos;
            set { if (Set(ref _builderSplitPos, value ?? "midsld")) RaiseBuilderPreview(); }
        }

        public string BuilderFakeSni
        {
            get => _builderFakeSni;
            set { if (Set(ref _builderFakeSni, value ?? "www.google.com")) RaiseBuilderPreview(); }
        }

        public string BuilderTtl
        {
            get => _builderTtl;
            set { if (Set(ref _builderTtl, value ?? "auto")) RaiseBuilderPreview(); }
        }

        public string BuilderFooling
        {
            get => _builderFooling;
            set { if (Set(ref _builderFooling, value ?? "badsum")) RaiseBuilderPreview(); }
        }

        public bool BuilderUseMultisplit
        {
            get => _builderUseMultisplit;
            set { if (Set(ref _builderUseMultisplit, value)) RaiseBuilderPreview(); }
        }

        public bool BuilderUseGameUdp
        {
            get => _builderUseGameUdp;
            set { if (Set(ref _builderUseGameUdp, value)) RaiseBuilderPreview(); }
        }

        public bool BuilderUseHostlist
        {
            get => _builderUseHostlist;
            set { if (Set(ref _builderUseHostlist, value)) RaiseBuilderPreview(); }
        }

        public bool BuilderUseIpSet
        {
            get => _builderUseIpSet;
            set { if (Set(ref _builderUseIpSet, value)) RaiseBuilderPreview(); }
        }

        public List<BuilderPreset> BuilderPresets { get; }
        public BuilderPreset? SelectedBuilderPreset
        {
            get => _selectedBuilderPreset;
            set => Set(ref _selectedBuilderPreset, value);
        }

        public string BuilderNameValidationText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BuilderStrategyName)) return "Укажите имя файла (без .bat)";
                var invalid = Path.GetInvalidFileNameChars();
                if (BuilderStrategyName.IndexOfAny(invalid) >= 0) return "Имя содержит недопустимые символы";
                if (BuilderStrategyName.Length > 40) return "Имя слишком длинное (до 40 символов)";
                if (Store.Items.Any(s => s.Name.Equals(BuilderStrategyName, StringComparison.OrdinalIgnoreCase))) return "Стратегия с таким именем уже существует — будет перезаписана";
                return "";
            }
        }

        public bool IsBuilderNameValid => string.IsNullOrWhiteSpace(BuilderNameValidationText) || BuilderNameValidationText.Contains("будет перезаписана");

        public ICommand ApplyBuilderPresetCommand { get; }
        public ICommand LoadBuilderFromSelectedCommand { get; }
        public ICommand CopyBuilderArgsCommand { get; }

        public List<string> BuilderGeneratedArgs => VisualStrategyBuilder.BuildArgs(
            Settings.EnginePath, BuilderDesyncMode, BuilderSplitPos, BuilderFakeSni, BuilderTtl,
            BuilderFooling, BuilderUseMultisplit, BuilderUseGameUdp, BuilderUseHostlist, BuilderUseIpSet);

        public string BuilderGeneratedArgsPreview => string.Join(" ", BuilderGeneratedArgs);

        public string BuilderTestStatus
        {
            get => _builderTestStatus;
            private set => Set(ref _builderTestStatus, value);
        }

        public string BuilderTestStatusKey
        {
            get => _builderTestStatusKey;
            private set => Set(ref _builderTestStatusKey, value);
        }

        public ICommand BuilderTestCommand { get; }
        public ICommand BuilderSaveCommand { get; }
        public ICommand BuilderApplyCommand { get; }

        private void RaiseBuilderPreview()
        {
            Raise(nameof(BuilderGeneratedArgs));
            Raise(nameof(BuilderGeneratedArgsPreview));
            Raise(nameof(BuilderArgsCountText));
            Raise(nameof(BuilderHasArgs));
        }

        public string BuilderArgsCountText => $"{BuilderGeneratedArgs.Count} аргументов · {BuilderGeneratedArgsPreview.Length} символов";
        public bool BuilderHasArgs => BuilderGeneratedArgs.Count > 0;

        private void ApplyBuilderPreset(BuilderPreset preset)
        {
            BuilderDesyncMode = preset.DesyncMode;
            BuilderSplitPos = preset.SplitPos;
            BuilderFakeSni = preset.FakeSni;
            BuilderTtl = preset.Ttl;
            BuilderFooling = preset.Fooling;
            BuilderUseMultisplit = preset.UseMultisplit;
            BuilderUseGameUdp = preset.UseGameUdp;
            BuilderUseHostlist = preset.UseHostlist;
            BuilderUseIpSet = preset.UseIpSet;
            BuilderTestStatus = $"Применён пресет «{preset.Name}»: {preset.Description}";
            BuilderTestStatusKey = "Info";
        }

        private void LoadBuilderFromSelected()
        {
            if (Selected == null) return;
            var s = Selected;
            BuilderStrategyName = s.Name + "_copy";
            // Пытаемся угадать параметры из Args
            var args = string.Join(" ", s.Args);
            if (args.Contains("disorder2")) BuilderDesyncMode = "disorder2";
            else if (args.Contains("fake,split2")) BuilderDesyncMode = "fake,split2";
            else if (args.Contains("split2")) BuilderDesyncMode = "split2";
            else if (args.Contains("fake")) BuilderDesyncMode = "fake";
            if (args.Contains("sniext")) BuilderSplitPos = "sniext";
            else if (args.Contains("midsld")) BuilderSplitPos = "midsld";
            foreach (var sni in BuilderFakeSnis)
            {
                if (sni != "none" && args.Contains(sni)) { BuilderFakeSni = sni; break; }
            }
            BuilderTestStatus = $"Параметры загружены из «{s.Name}» — отредактируйте и сохраните как новую стратегию";
            BuilderTestStatusKey = "Info";
        }

        private void CopyBuilderArgs()
        {
            try
            {
                System.Windows.Clipboard.SetText(BuilderGeneratedArgsPreview);
                BuilderTestStatus = "Аргументы скопированы в буфер обмена";
                BuilderTestStatusKey = "Success";
            }
            catch (Exception ex) { BuilderTestStatus = "Не удалось скопировать: " + ex.Message; BuilderTestStatusKey = "Danger"; }
        }

        // ------------------------------------------------------------------ Управление контрольными адресами
        public bool UseTargetsTxtForStrategyTest
        {
            get => Settings.UseTargetsTxtForStrategyTest;
            set
            {
                if (Settings.UseTargetsTxtForStrategyTest == value) return;
                Settings.UseTargetsTxtForStrategyTest = value;
                SettingsStore.Save(Settings);
                Raise(nameof(UseTargetsTxtForStrategyTest));
                Raise(nameof(TargetsTxtCountText));
                Raise(nameof(TargetsTxtStatusText));
                Raise(nameof(EffectiveTargetCountText));
            }
        }

        public int TargetsTxtCount => TargetsTxtLoader.Exists(Settings.EnginePath) ? TargetsTxtLoader.Count(Settings.EnginePath) : 0;
        public string TargetsTxtCountText => TargetsTxtCount == 0 ? "targets.txt не найден" : $"{TargetsTxtCount} доменов из targets.txt";
        public string TargetsTxtStatusText => UseTargetsTxtForStrategyTest
            ? (TargetsTxtCount == 0 ? "Файл utils/targets.txt не найден в папке движка" : $"Будет проверено дополнительно {TargetsTxtCount} HTTP-целей из utils/targets.txt (как в zapret.ps1) + ваши адреса")
            : "Доп-цели из targets.txt отключены";
        public int EffectiveTargetCount => ConnectionTester.GetEffectiveTargets(Settings).Count;
        public string EffectiveTargetCountText => $"Итого контрольных целей: {EffectiveTargetCount} (базовые 8 + targets.txt {TargetsTxtCount} + ваши)";

        public void RefreshTargetsTxtInfo()
        {
            Raise(nameof(TargetsTxtCount));
            Raise(nameof(TargetsTxtCountText));
            Raise(nameof(TargetsTxtStatusText));
            Raise(nameof(EffectiveTargetCount));
            Raise(nameof(EffectiveTargetCountText));
        }

        public ObservableCollection<MonitorTarget> TargetEndpoints { get; }

        public string NewTargetName
        {
            get => _newTargetName;
            set => Set(ref _newTargetName, value ?? "");
        }

        public string NewTargetUrl
        {
            get => _newTargetUrl;
            set
            {
                if (Set(ref _newTargetUrl, value ?? ""))
                {
                    (AddTargetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand AddTargetCommand { get; }
        public ICommand RemoveTargetCommand { get; }
        public ICommand ToggleTargetCommand { get; }

        public string ProviderContextText => Settings.ProviderContext?.DisplayText ?? "Провайдер не указан";

        public ObservableCollection<StrategyCandidate> GeneratedCandidates { get; } = new();
        public ObservableCollection<StrategyCandidateEvaluation> CandidateEvaluations { get; } = new();
        public ObservableCollection<SavedStrategyCandidate> SavedCandidates { get; } = new();
        public ObservableCollection<StrategyEvaluationHistoryRecord> EvaluationHistory { get; } = new();
        public ObservableCollection<StrategySwitchRecord> SwitchHistory { get; } = new();

        public StrategyCandidate? CandidatePreview
        {
            get => _candidatePreview;
            private set
            {
                if (!Set(ref _candidatePreview, value)) return;
                _isCandidatePreviewSaved = value != null && StrategyCandidateStore.Load().Any(item =>
                    item.Fingerprint.Equals(value.Fingerprint, StringComparison.Ordinal));
                Raise(nameof(CandidatePreview));
                Raise(nameof(CandidatePreviewName));
                Raise(nameof(CandidatePreviewProvider));
                Raise(nameof(CandidatePreviewSummary));
                Raise(nameof(CandidatePreviewArgs));
                Raise(nameof(CandidatePreviewFeatures));
                Raise(nameof(CandidatePreviewFixedArgs));
                Raise(nameof(CandidatePreviewTunableArgs));
                Raise(nameof(IsCandidatePreviewSaved));
                Raise(nameof(CandidateSaveStatusText));
                (SaveCandidateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (MakeCandidatePrimaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

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
            private set => Set(ref _candidateEvaluationProgressValue, value);
        }

        public double CandidateEvaluationProgressMaximum
        {
            get => _candidateEvaluationProgressMaximum;
            private set => Set(ref _candidateEvaluationProgressMaximum, value);
        }

        public string CandidateEvaluationProgressPercentText
        {
            get => _candidateEvaluationProgressPercentText;
            private set => Set(ref _candidateEvaluationProgressPercentText, value);
        }

        public bool CandidateEvaluationProgressVisible
        {
            get => _candidateEvaluationProgressVisible;
            private set => Set(ref _candidateEvaluationProgressVisible, value);
        }

        public bool CandidateEvaluationVisible => IsEvaluatingCandidates || CandidateEvaluations.Count > 0;
        public string CandidateEvaluationSummaryText => CandidateEvaluations.Count == 0
            ? "Оценка кандидатов не запускалась"
            : $"Проверено кандидатов: {CandidateEvaluations.Count} · лучших: {CandidateEvaluations.Count(item => item.IsWinner)}";

        public string EvaluationHistoryCountText => EvaluationHistory.Count == 0
            ? "История пуста"
            : $"Записей в истории: {EvaluationHistory.Count}";

        public string SwitchHistoryCountText => SwitchHistory.Count == 0
            ? "Переключений пока нет"
            : $"Последних переключений: {SwitchHistory.Count}";

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value))
                {
                    View.Refresh();
                    Raise(nameof(CountText));
                }
            }
        }

        public int CategoryIndex
        {
            get => _categoryIndex;
            set
            {
                if (Set(ref _categoryIndex, value))
                {
                    View.Refresh();
                    Raise(nameof(CountText));
                }
            }
        }

        public bool OnlyRecommended
        {
            get => _onlyRecommended;
            set
            {
                if (Set(ref _onlyRecommended, value))
                {
                    View.Refresh();
                    Raise(nameof(CountText));
                }
            }
        }

        public string CountText
        {
            get
            {
                var total = Store.Items.Count;
                var filtered = View.Cast<object>().Count();
                return filtered == total ? $"Всего: {total}" : $"Показано: {filtered} из {total}";
            }
        }

        public StrategyInfo? Selected
        {
            get => _selected ?? Store.Items.FirstOrDefault();
            set
            {
                if (!Set(ref _selected, value)) return;
                Raise(nameof(SelectedName));
                Raise(nameof(SelectedCategory));
                Raise(nameof(SelectedDescription));
                Raise(nameof(SelectedArgs));
                Raise(nameof(SelectedFeatures));
                Raise(nameof(SelectedPath));
                Raise(nameof(RunButtonText));
                Raise(nameof(RunButtonTooltip));
                Raise(nameof(IsRunSwitchMode));
                (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (OpenBatCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CopyArgsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SetDefaultCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (BuildCandidatePreviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string SelectedName => Selected?.Name ?? "Ничего не выбрано";
        public string SelectedCategory => Selected?.Category ?? "";
        public string SelectedDescription => Selected?.Description ?? "";
        public string SelectedArgs => Selected?.ShortArgs ?? "";
        public string SelectedFeatures => Selected == null ? "" : StrategyFeatureAnalyzer.Analyze(Selected).Summary;
        public string SelectedPath => Selected?.FullPath ?? "";

        // Динамический текст кнопки «Запустить/Переключить» — P1 1.6.9: отражает бесшовность
        public string RunButtonText
        {
            get
            {
                var running = CurrentRunningName();
                if (Selected != null && !string.IsNullOrWhiteSpace(running) && !string.Equals(running, Selected.Name, StringComparison.OrdinalIgnoreCase) && Bypass.GetStatus().IsRunning)
                    return $"Переключить на «{Selected.Name}»";
                if (Selected != null) return $"Запустить «{Selected.Name}»";
                return "Запустить";
            }
        }

        public string RunButtonTooltip
        {
            get
            {
                var running = CurrentRunningName();
                if (Selected != null && !string.IsNullOrWhiteSpace(running) && !string.Equals(running, Selected.Name, StringComparison.OrdinalIgnoreCase) && Bypass.GetStatus().IsRunning)
                    return $"Бесшовно переключить обход с «{running}» на «{Selected.Name}» — служба или процесс перезапустится без ручной остановки";
                if (Selected != null) return $"Запустить обход со стратегией «{Selected.Name}»";
                return "Выберите стратегию для запуска";
            }
        }

        public bool IsRunSwitchMode => Bypass.GetStatus().IsRunning && Selected != null && !string.IsNullOrWhiteSpace(CurrentRunningName()) && !string.Equals(CurrentRunningName(), Selected.Name, StringComparison.OrdinalIgnoreCase);

        private string CurrentRunningName()
        {
            var s = Bypass.GetStatus();
            if (!string.IsNullOrWhiteSpace(s.ServiceStrategy)) return s.ServiceStrategy;
            return s.StrategyName ?? "";
        }

        public void RefreshRunButton()
        {
            Raise(nameof(RunButtonText));
            Raise(nameof(RunButtonTooltip));
            Raise(nameof(IsRunSwitchMode));
            (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SetDefaultCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(RunButtonText));
                    Raise(nameof(RunButtonTooltip));
                    Raise(nameof(IsRunSwitchMode));
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (GenerateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RunSavedCandidateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyBestRecommendedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (BuilderTestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (BuilderApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SetDefaultCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                    (TestStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (TestAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelTestCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (GenerateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (EvaluateCandidatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyBestRecommendedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (BuilderTestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            set
            {
                // Монотонный прогресс: значение только растёт вперёд
                if (value >= _lastProgressValue || value == 0)
                {
                    _lastProgressValue = value;
                    Set(ref _testProgressValue, value);
                }
            }
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
        public ICommand EvaluateCandidatesCommand { get; }
        public ICommand CancelCandidateEvaluationCommand { get; }
        public ICommand ExportCandidateReportCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand ClearSwitchHistoryCommand { get; }

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
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
                return;
            }

            Store.Refresh();
            View.Refresh();
            ReloadSavedCandidates();
            ReloadTargetEndpoints();
            Raise(nameof(CountText));
            Raise(nameof(Selected));
            Raise(nameof(ProviderContextText));
            Raise(nameof(CanRestorePrevious));
            Raise(nameof(PreviousStrategyText));
            Raise(nameof(BestEmpiricalStrategy));
            Raise(nameof(HasBestRecommendation));
            Raise(nameof(BestStrategyRecommendationText));
            Raise(nameof(BestStrategyDetailsText));
            Raise(nameof(RunButtonText));
            Raise(nameof(RunButtonTooltip));
            Raise(nameof(IsRunSwitchMode));
            RefreshTargetsTxtInfo();
        }

        private void ReloadTargetEndpoints()
        {
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(ReloadTargetEndpoints);
                return;
            }

            TargetEndpoints.Clear();
            foreach (var target in Settings.MonitorTargets) TargetEndpoints.Add(target);
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
            if (strategy == null || IsBusy || IsTestingAll || IsGeneratingCandidates || IsEvaluatingCandidates)
                return;
            var target = Store.Find(strategy.Name) ?? strategy;
            Selected = target;
            await RunAsync(target);
        }

        private async Task RunAsync(object? parameter)
        {
            var target = parameter as StrategyInfo ?? Selected;
            if (target == null || IsBusy) return;

            if (!Shell.IsAdmin())
            {
                Message = "Для переключения стратегии нужны права администратора. Нажмите «Перезапустить от администратора» на странице Обзор.";
                return;
            }
            if (!System.IO.File.Exists(System.IO.Path.Combine(Store.Folder, "bin", "winws.exe")))
            {
                Message = "Не найден bin\\winws.exe. Скачайте движок на странице «Обновления».";
                return;
            }

            IsBusy = true;
            Message = $"Запускаю стратегию «{target.Name}»…";
            try
            {
                var mode = EngineService.GetGameFilterMode(Store.Folder);
                // P2 1.6.10: централизованная логика применения (admin/legacy/switch уже внутри Bypass, но используем сервис для консистентности)
                var result = await StrategyApplicationService.ApplyAsync(Bypass, target, mode, Settings.ShowWinwsConsole);
                var prev = CurrentRunningName();
                if (result.Ok)
                {
                    Settings.SelectedStrategy = target.Name;
                    SettingsStore.Save(Settings);
                    _main.Home.RefreshStatus();
                    RefreshRunButton();
                    AppendSwitchHistory(target.Name, prev, "вручную", true, result.Message);
                    Message = result.Message.Length > 0 ? result.Message : $"Стратегия «{target.Name}» успешно запущена";
                }
                else
                {
                    AppendSwitchHistory(target.Name, prev, "вручную", false, result.Message);
                    Message = result.Message;
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task InstallServiceAsync()
        {
            if (Selected == null || IsBusy) return;

            var answer = MessageBox.Show(
                $"Установить «{Selected.Name}» как системную службу zapret?",
                "Установка службы", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            IsBusy = true;
            Message = $"Устанавливаю службу с «{Selected.Name}»…";
            try
            {
                var result = await Bypass.InstallServiceAsync(Selected,
                    EngineService.GetGameFilterMode(Store.Folder));
                Message = result.Message;
                if (result.Ok)
                {
                    Settings.SelectedStrategy = Selected.Name;
                    SettingsStore.Save(Settings);
                    _main.Home.RefreshStatus();
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UseRecommended()
        {
            var rec = BestEmpiricalStrategy ?? Store.Recommended;
            if (rec != null)
            {
                Selected = rec;
                Message = $"Выбрана рекомендуемая стратегия «{rec.Name}»";
            }
            else
            {
                Message = "Рекомендуемая стратегия не найдена";
            }
        }

        private async Task ApplyBestRecommendedAsync()
        {
            if (BestEmpiricalStrategy == null || IsBusy) return;
            await RunAsync(BestEmpiricalStrategy);
        }

        private async Task SetDefaultAsync()
        {
            if (Selected == null) return;
            var wasRunning = Bypass.GetStatus().IsRunning;
            var runningName = CurrentRunningName();
            var needsSwitch = wasRunning && !string.Equals(runningName, Selected.Name, StringComparison.OrdinalIgnoreCase);
            if (needsSwitch)
            {
                var answer = MessageBox.Show(
                    $"Сделать «{Selected.Name}» основной и сразу бесшовно переключить обход с «{runningName}» на «{Selected.Name}»?\n\nТекущий обход будет перезапущен без ручной остановки.\n\nНажмите «Да» для переключения сейчас или «Нет» чтобы только запомнить выбор.",
                    "Сделать основной", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    if (!Shell.IsAdmin())
                    {
                        Message = "Для переключения стратегии нужны права администратора.";
                        return;
                    }
                    IsBusy = true;
                    try
                    {
                        var mode = EngineService.GetGameFilterMode(Store.Folder);
                        var res = await Bypass.SwitchToStrategyAsync(Selected, mode, Settings.ShowWinwsConsole);
                        if (!res.Ok)
                        {
                            Message = res.Message;
                            return;
                        }
                        Settings.SelectedStrategy = Selected.Name;
                        SettingsStore.Save(Settings);
                        _main.Home.RefreshStatus();
                        RefreshRunButton();
                        AppendSwitchHistory(Selected.Name, runningName, "сделать основной", true, res.Message);
                        Message = $"«{Selected.Name}» установлена как основная и сразу применена";
                        return;
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                }
            }
            Settings.SelectedStrategy = Selected.Name;
            SettingsStore.Save(Settings);
            _main.Home.ReloadFromEngine();
            RefreshRunButton();
            Message = $"«{Selected.Name}» установлена как основная стратегия";
        }

        private void SetDefault()
        {
            _ = SetDefaultAsync();
        }

        private void CopyArgs()
        {
            if (Selected == null) return;
            try
            {
                Clipboard.SetText(Selected.ArgsPreview);
                Message = "Команда запуска скопирована в буфер обмена";
            }
            catch (Exception ex) { Message = "Не удалось скопировать: " + ex.Message; }
        }

        private async Task TestStrategyAsync(object? parameter)
        {
            if (parameter is not StrategyInfo strategy || IsTestingAll || IsBusy) return;

            IsTestingAll = true;
            _lastProgressValue = 0;
            var effectiveTargets = ConnectionTester.GetEffectiveTargets(Settings);
            TestProgressValue = 0;
            TestProgressMaximum = Math.Max(1, effectiveTargets.Count);
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
                            TestProgressPercentText = $"{Math.Min(100, (int)(TestProgressValue / TestProgressMaximum * 100))}%";
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
                Store.RecordTestResult(strategy.Name, result);
                TestProgressValue = TestProgressMaximum;
                TestProgressPercentText = "100%";
                AppendHistory(StrategyEvaluationHistoryRecord.FromTestResult(
                    strategy, result, Settings.ProviderContext ?? new ProviderContext()));
                TestSummary = result.Started
                    ? $"«{strategy.Name}»: {result.SummaryText}"
                    : $"«{strategy.Name}»: {result.ErrorMessage}";
                TestSummaryKey = result.IsSuitable ? "Success" : result.Started ? "Warning" : "Danger";
                Message = result.IsSuitable
                    ? "Стратегия проходит основные проверки соединений"
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
            // Глобальный оверлей затемнения — блокирует окно на время проверки всех стратегий
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Проверка всех стратегий", $"Подготовка: {Store.Items.Count} стратегий × {ConnectionTester.GetEffectiveTargets(Settings).Count} целей", "Не закрывайте окно — идёт важная проверка", 0, false, true, () => _testCts?.Cancel())); } catch {}
            try
            {
                var total = Store.Items.Count;
                var effectiveTargets = ConnectionTester.GetEffectiveTargets(Settings);
                var targetCount = Math.Max(1, effectiveTargets.Count);

                _lastProgressValue = 0;
                TestProgressValue = 0;
                TestProgressMaximum = Math.Max(1, total * targetCount);
                TestProgressIndeterminate = false;
                TestProgressPercentText = "0%";

                for (var index = 0; index < total; index++)
                {
                    _testCts.Token.ThrowIfCancellationRequested();
                    var strategy = Store.Items[index];
                    TestProgressText = $"[{index + 1}/{total}] Проверяю «{strategy.Name}»…";
                    externalProgress?.Report(TestProgressText);
                    strategy.SetTestStarted();

                    var baseOffset = index * targetCount;
                    var subProgress = new Progress<string>(text =>
                    {
                        if (text.StartsWith("CONNECTION_PROGRESS:", StringComparison.Ordinal))
                        {
                            var parts = text.Substring("CONNECTION_PROGRESS:".Length).Split(" — ", 2);
                            var numbers = parts[0].Split('/');
                            if (numbers.Length == 2 && int.TryParse(numbers[0], out var cur))
                            {
                                TestProgressValue = baseOffset + cur;
                                var pct = Math.Min(100, (int)(TestProgressValue / TestProgressMaximum * 100));
                                TestProgressPercentText = $"{pct}%";
                            }
                            if (parts.Length > 1)
                            {
                                TestProgressText = $"[{index + 1}/{total}] «{strategy.Name}»: {parts[1]}…";
                            }
                        }
                        else
                        {
                            TestProgressText = $"[{index + 1}/{total}] «{strategy.Name}»: {text}";
                        }
                    });

                    var result = await Bypass.TestStrategyAsync(strategy, _testCts.Token, subProgress);
                    strategy.SetTestResult(result);
                    Store.RecordTestResult(strategy.Name, result);
                    AppendHistory(StrategyEvaluationHistoryRecord.FromTestResult(
                        strategy, result, Settings.ProviderContext ?? new ProviderContext()));
                    results.Add(result);

                    TestProgressValue = baseOffset + targetCount;
                    TestProgressPercentText = $"{Math.Min(100, (int)(TestProgressValue / TestProgressMaximum * 100))}%";

                    var passed = results.Count(r => r.IsSuitable);
                    TestSummary = $"Проверено: {index + 1} из {total}. Подходящих стратегий: {passed}";
                    TestSummaryKey = passed > 0 ? "Success" : "Warning";
                    try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update($"[{index + 1}/{total}] «{strategy.Name}» — {passed} подходящих", TestSummary, ((double)(index + 1) / total) * 100, false)); } catch {}
                }

                TestProgressValue = TestProgressMaximum;
                TestProgressPercentText = "100%";

                var batch = new StrategyTestBatchResult { Results = results };
                var best = batch.Best;
                if (best != null)
                {
                    BestEmpiricalStrategy = best.Strategy;
                    if (best.IsSuitable)
                    {
                        TestSummary = $"Лидер тестирования: «{best.Strategy.Name}» ({best.PassedCount}/{best.Checks.Count} проверок OK).";
                    }
                    else
                    {
                        TestSummary = $"Лучший кандидат: «{best.Strategy.Name}» ({best.PassedCount}/{best.Checks.Count} проверок OK).";
                    }
                    TestSummaryKey = best.IsSuitable ? "Success" : "Warning";
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
                TestProgressText = "";
                IsTestingAll = false;
                _testCts?.Dispose();
                _testCts = null;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        // ------------------------------------------------------------------ Логика визуального конструктора параметров
        private async Task BuilderTestAsync()
        {
            if (IsBusy || IsTestingAll) return;

            var tempStrategy = new StrategyInfo
            {
                Name = string.IsNullOrWhiteSpace(BuilderStrategyName) ? "custom_builder_test" : BuilderStrategyName,
                Category = "АВТОКОНСТРУКТОР",
                Description = $"Конструктор: {BuilderDesyncMode}, split={BuilderSplitPos}, fake={BuilderFakeSni}",
                Args = BuilderGeneratedArgs
            };

            IsBusy = true;
            BuilderTestStatus = "Запускаю пробную проверку параметров…";
            BuilderTestStatusKey = "Info";
            try
            {
                var result = await Bypass.TestStrategyAsync(tempStrategy);
                if (result.Started)
                {
                    BuilderTestStatus = $"Результат: {result.PassedCount}/{result.Checks.Count} проверок OK · среднее время {result.AverageLatencyMs} мс";
                    BuilderTestStatusKey = result.IsSuitable ? "Success" : "Warning";
                }
                else
                {
                    BuilderTestStatus = $"Ошибка запуска параметров: {result.ErrorMessage}";
                    BuilderTestStatusKey = "Danger";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void BuilderSave()
        {
            var name = string.IsNullOrWhiteSpace(BuilderStrategyName) ? "custom_strategy" : BuilderStrategyName.Trim();
            var args = BuilderGeneratedArgs;
            var candidate = new SavedStrategyCandidate
            {
                Name = name,
                SourceStrategy = "Visual Builder",
                MutationDescription = $"Конструктор: {BuilderDesyncMode}, split-pos={BuilderSplitPos}, fake-sni={BuilderFakeSni}",
                Args = args,
                Fingerprint = string.Join(" ", args)
            };

            if (StrategyCandidateStore.Save(candidate))
            {
                BuilderTestStatus = $"Стратегия «{name}» сохранена в каталог!";
                BuilderTestStatusKey = "Success";
                Refresh();
            }
            else
            {
                BuilderTestStatus = "Не удалось сохранить стратегию.";
                BuilderTestStatusKey = "Danger";
            }
        }

        private async Task BuilderApplyAsync()
        {
            BuilderSave();
            var strategy = Store.Find(BuilderStrategyName);
            if (strategy != null)
            {
                await RunAsync(strategy);
            }
        }

        // ------------------------------------------------------------------ Логика контрольных адресов
        private void AddTarget()
        {
            if (string.IsNullOrWhiteSpace(NewTargetUrl)) return;
            var input = NewTargetUrl.Trim();
            var name = string.IsNullOrWhiteSpace(NewTargetName) ? input : NewTargetName.Trim();

            if (MonitorTarget.TryCreate(input, name, out var target, out var error) && target != null)
            {
                Settings.MonitorTargets.Add(target);
                TargetEndpoints.Add(target);
                SettingsStore.Save(Settings);
                NewTargetName = "";
                NewTargetUrl = "";
                Message = $"Контрольный адрес «{target.Name}» добавлен.";
            }
            else
            {
                Message = "Некорректный адрес: " + error;
            }
        }

        private void RemoveTarget(object? parameter)
        {
            if (parameter is not MonitorTarget target) return;
            Settings.MonitorTargets.Remove(target);
            TargetEndpoints.Remove(target);
            SettingsStore.Save(Settings);
            Message = $"Адрес «{target.Name}» удалён.";
        }

        private void ToggleTarget(object? parameter)
        {
            if (parameter is not MonitorTarget target) return;
            SettingsStore.Save(Settings);
        }

        // ------------------------------------------------------------------ Кандидаты и история
        private void AppendHistory(StrategyEvaluationHistoryRecord record)
        {
            StrategyEvaluationHistoryStore.TryAppend(record);
            EvaluationHistory.Insert(0, record);
            while (EvaluationHistory.Count > 50) EvaluationHistory.RemoveAt(EvaluationHistory.Count - 1);
            Raise(nameof(EvaluationHistoryCountText));
            (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void ClearHistory()
        {
            StrategyEvaluationHistoryStore.Clear();
            EvaluationHistory.Clear();
            Raise(nameof(EvaluationHistoryCountText));
            (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            Message = "История проверок очищена";
        }

        private void AppendSwitchHistory(string strategyName, string previousName, string source, bool success, string message)
        {
            var mode = Bypass.GetStatus().ServiceState == ServiceState.Running ? "служба" : Bypass.GetStatus().IsRunning ? "процесс" : "выкл";
            var rec = new StrategySwitchRecord
            {
                StrategyName = strategyName,
                PreviousStrategyName = previousName,
                Source = source,
                Mode = mode,
                Success = success,
                Message = message ?? ""
            };
            StrategySwitchHistoryStore.TryAppend(rec);
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                SwitchHistory.Insert(0, rec);
                while (SwitchHistory.Count > 20) SwitchHistory.RemoveAt(SwitchHistory.Count - 1);
                Raise(nameof(SwitchHistoryCountText));
                (ClearSwitchHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }

        public void ClearSwitchHistory()
        {
            StrategySwitchHistoryStore.Clear();
            SwitchHistory.Clear();
            Raise(nameof(SwitchHistoryCountText));
            (ClearSwitchHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void BuildCandidatePreview()
        {
            if (Selected == null) return;
            CandidatePreview = StrategyCandidateFactory.CreatePreview(Selected, Settings.ProviderContext ?? new ProviderContext());
            Message = $"Подготовлен базовый кандидат для «{Selected.Name}»";
        }

        private async Task GenerateCandidatesAsync()
        {
            if (Store.Items.Count == 0 || IsGeneratingCandidates || IsTestingAll || IsBusy) return;

            IsGeneratingCandidates = true;
            _candidateGenerationCts = new CancellationTokenSource();
            CandidateGenerationText = "Формирую мутации аргументов на основе признаков…";
            GeneratedCandidates.Clear();
            CandidateEvaluations.Clear();

            try
            {
                var options = new StrategyCandidateGenerationOptions { MaxCandidates = 18 };
                var gameFilter = EngineService.GetGameFilterMode(Store.Folder);
                var genResult = StrategyCandidateGenerator.Generate(
                    Store.Items, Settings.ProviderContext ?? new ProviderContext(), gameFilter, options, _candidateGenerationCts.Token);
                foreach (var candidate in genResult.Candidates) GeneratedCandidates.Add(candidate);
                foreach (var candidate in genResult.Candidates) CandidateEvaluations.Add(new StrategyCandidateEvaluation(candidate));

                CandidateGenerationText = $"Сформировано кандидатов: {GeneratedCandidates.Count}. Запустите оценку для безопасного тестирования.";
                Message = $"Сформировано {GeneratedCandidates.Count} кандидатов для проверки";
            }
            catch (OperationCanceledException)
            {
                CandidateGenerationText = "Генерация кандидатов отменена";
            }
            finally
            {
                IsGeneratingCandidates = false;
                _candidateGenerationCts?.Dispose();
                _candidateGenerationCts = null;
            }
        }

        private void CancelCandidateGeneration() => _candidateGenerationCts?.Cancel();

        private void PreviewGeneratedCandidate(object? parameter)
        {
            if (parameter is StrategyCandidate candidate)
            {
                CandidatePreview = candidate;
                Message = $"Просмотр кандидата «{candidate.Name}»";
            }
            else if (parameter is StrategyCandidateEvaluation eval)
            {
                CandidatePreview = eval.Candidate;
                Message = $"Просмотр кандидата «{eval.Candidate.Name}»";
            }
        }

        private async Task EvaluateCandidatesAsync()
        {
            if (CandidateEvaluations.Count == 0 || IsEvaluatingCandidates || IsGeneratingCandidates || IsTestingAll || IsBusy) return;

            IsEvaluatingCandidates = true;
            _candidateEvaluationCts = new CancellationTokenSource();
            CandidateEvaluationProgressVisible = true;
            CandidateEvaluationProgressValue = 0;
            CandidateEvaluationProgressMaximum = Math.Max(1, CandidateEvaluations.Count);
            CandidateEvaluationProgressPercentText = "0%";
            CandidateEvaluationText = "Начинаю безопасную проверку кандидатов…";

            try
            {
                var completed = 0;
                for (var i = 0; i < CandidateEvaluations.Count; i++)
                {
                    _candidateEvaluationCts.Token.ThrowIfCancellationRequested();
                    var eval = CandidateEvaluations[i];
                    CandidateEvaluationText = $"Проверяю кандидата {i + 1} из {CandidateEvaluations.Count}: «{eval.Candidate.Name}»…";

                    var fakeInfo = new StrategyInfo
                    {
                        Name = eval.Candidate.Name,
                        Category = "АВТОКОНСТРУКТОР",
                        Description = eval.Candidate.MutationDescription,
                        Args = eval.Candidate.Args
                    };

                    var result = await Bypass.TestStrategyAsync(fakeInfo, _candidateEvaluationCts.Token);
                    eval.AddResult(result, 1, 1);
                    eval.Complete();
                    completed++;
                    CandidateEvaluationProgressValue = completed;
                    CandidateEvaluationProgressPercentText = $"{CandidateEvaluationProgressValue / CandidateEvaluationProgressMaximum * 100:0}%";
                }

                var best = CandidateEvaluations.OrderByDescending(item => item.Score).FirstOrDefault();
                if (best != null && best.IsWinner)
                {
                    CandidatePreview = best.Candidate;
                    CandidateEvaluationText = $"Оценка завершена. Лучший кандидат: «{best.Candidate.Name}» ({best.PassedChecks}/{best.TotalChecks} проверок OK, средний пинг {best.AverageElapsedText}).";
                }
                else
                {
                    CandidateEvaluationText = "Оценка завершена. Ни один кандидат не показал полного успеха.";
                }
            }
            catch (OperationCanceledException)
            {
                CandidateEvaluationText = "Оценка кандидатов отменена";
            }
            finally
            {
                IsEvaluatingCandidates = false;
                CandidateEvaluationProgressVisible = false;
                _candidateEvaluationCts?.Dispose();
                _candidateEvaluationCts = null;
                _candidateEvaluationView.Refresh();
                Raise(nameof(CandidateEvaluationSummaryText));
            }
        }

        private void CancelCandidateEvaluation() => _candidateEvaluationCts?.Cancel();

        private void ExportCandidateReport()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "JSON-отчёт (*.json)|*.json|Текстовый отчёт (*.txt)|*.txt",
                    FileName = $"strategy-report-{DateTime.Now:yyyyMMdd-HHmmss}.json"
                };
                if (dialog.ShowDialog() != true) return;

                var data = new
                {
                    ExportedAt = DateTime.Now,
                    Evaluations = CandidateEvaluations.ToList(),
                    History = EvaluationHistory.ToList()
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
                Message = "Отчёт сохранён: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                Message = "Не удалось сохранить отчёт: " + ex.Message;
            }
        }

        private void SaveCandidate()
        {
            if (CandidatePreview == null) return;
            if (StrategyCandidateStore.TrySave(CandidatePreview, out var saved))
            {
                ReloadSavedCandidates();
                CandidatePreview = CandidatePreview; // обновление IsCandidatePreviewSaved
                Refresh();
                Message = $"Кандидат «{saved.DisplayName}» успешно сохранён";
            }
            else
            {
                Message = "Не удалось сохранить кандидата";
            }
        }

        private async Task RunSavedCandidateAsync()
        {
            if (CandidatePreview == null || !IsCandidatePreviewSaved) return;
            var fakeInfo = new StrategyInfo
            {
                Name = CandidatePreview.Name,
                Category = "АВТОКОНСТРУКТОР",
                Description = CandidatePreview.MutationDescription,
                Args = CandidatePreview.Args
            };
            await RunAsync(fakeInfo);
        }

        private void PreviewSavedCandidate(object? parameter)
        {
            if (parameter is SavedStrategyCandidate saved)
            {
                CandidatePreview = new StrategyCandidate
                {
                    Name = saved.DisplayName,
                    SourceStrategy = saved.SourceStrategy,
                    MutationDescription = saved.MutationDescription,
                    ProviderText = saved.ProviderText,
                    Provider = saved.Provider,
                    Args = saved.Args,
                    Summary = saved.ArgsText
                };
                Message = $"Выбран сохранённый кандидат «{saved.DisplayName}»";
            }
        }

        private void DeleteSavedCandidate(object? parameter)
        {
            if (parameter is not SavedStrategyCandidate saved) return;
            StrategyCandidateStore.Delete(saved.Id);
            ReloadSavedCandidates();
            CandidatePreview = null;
            Refresh();
            Message = $"Кандидат «{saved.DisplayName}» удалён";
        }

        private async void MakeCandidatePrimary()
        {
            if (CandidatePreview == null) return;
            var wasRunning = Bypass.GetStatus().IsRunning;
            var runningName = CurrentRunningName();
            var needsSwitch = wasRunning && !string.Equals(runningName, CandidatePreview.Name, StringComparison.OrdinalIgnoreCase);
            if (needsSwitch)
            {
                var answer = MessageBox.Show(
                    $"Сделать кандидата «{CandidatePreview.Name}» основной и сразу бесшовно переключить обход с «{runningName}» на него?",
                    "Сделать основной", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    if (!Shell.IsAdmin())
                    {
                        Message = "Для переключения стратегии нужны права администратора.";
                        return;
                    }
                    var candidateInfo = new StrategyInfo { Name = CandidatePreview.Name, Args = CandidatePreview.Args, Category = "АВТОКОНСТРУКТОР", Description = CandidatePreview.MutationDescription };
                    var mode = EngineService.GetGameFilterMode(Store.Folder);
                    IsBusy = true;
                    try
                    {
                        var res = await Bypass.SwitchToStrategyAsync(candidateInfo, mode, Settings.ShowWinwsConsole);
                        if (!res.Ok) { Message = res.Message; return; }
                    }
                    finally { IsBusy = false; }
                }
            }
            Settings.SelectedStrategy = CandidatePreview.Name;
            SettingsStore.Save(Settings);
            _main.Home.ReloadFromEngine();
            RefreshRunButton();
            Message = $"Кандидат «{CandidatePreview.Name}» выбран основной стратегией";
        }

        private void RestorePrevious()
        {
            if (!CanRestorePrevious) return;
            Settings.SelectedStrategy = Settings.PreviousSelectedStrategy;
            SettingsStore.Save(Settings);
            _main.Home.ReloadFromEngine();
            Refresh();
            Message = $"Восстановлена прежняя стратегия «{Settings.SelectedStrategy}»";
        }

        private void ReloadSavedCandidates()
        {
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(ReloadSavedCandidates);
                return;
            }

            SavedCandidates.Clear();
            foreach (var item in StrategyCandidateStore.Load()) SavedCandidates.Add(item);
        }

        public async Task TestSniPoolAsync()
        {
            if (IsTestingSniPool) return;

            IsTestingSniPool = true;
            SniTestingStatusText = "Тестирование пула TLS SNI фейков…";
            SniTestResults.Clear();
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Пул TLS SNI", "Параллельное TLS-тестирование доменов — не закрывайте окно", SniTestingStatusText, 0, true, false)); } catch {}

            try
            {
                var progress = new Progress<string>(s => { SniTestingStatusText = s; try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update(s, "Проверка SNI…", null, true)); } catch {} });
                var results = await SniFakePoolManager.TestPoolAsync(null, progress).ConfigureAwait(true);

                foreach (var r in results)
                {
                    SniTestResults.Add(r);
                }

                Raise(nameof(HasSniResults));

                var best = results.FirstOrDefault(r => r.Ok);
                if (best != null)
                {
                    WinnerSni = best;
                    SniTestingStatusText = $"Тест завершён! Лучший SNI: {best.Domain} ({best.LatencyMs} мс, {best.Category}).";
                }
                else
                {
                    SniTestingStatusText = "Тест завершён. Доступные SNI не ответили.";
                }
            }
            catch (Exception ex)
            {
                SniTestingStatusText = "Ошибка тестирования SNI: " + ex.Message;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Пул TLS SNI — ошибка", ex.Message, "Попробуйте ещё раз")); } catch {}
                return;
            }
            finally
            {
                IsTestingSniPool = false;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        public async Task AutoSelectBestSniAsync()
        {
            await TestSniPoolAsync();
            if (WinnerSni != null && WinnerSni.Ok)
            {
                SelectedFakeSni = WinnerSni.Domain;
                Message = $"✅ Автоматически выбран оптимальный SNI фейк: {WinnerSni.Domain} ({WinnerSni.LatencyMs} мс).";
                _main.Home.ShowSuccess($"✅ Оптимальный TLS SNI: {WinnerSni.Domain} ({WinnerSni.LatencyMs} мс).");
            }
        }

        public void RotateToNextSni()
        {
            var pool = SniFakePoolManager.PredefinedSniPool;
            var currentIdx = pool.ToList().FindIndex(c => string.Equals(c.Domain, Settings.SelectedFakeSni, StringComparison.OrdinalIgnoreCase));
            var nextIdx = (currentIdx + 1) % pool.Count;
            var next = pool[nextIdx];

            SelectedFakeSni = next.Domain;
            Message = $"🔄 Ротация SNI: активирован {next.DisplayText}";
            _main.Home.ShowSuccess($"🔄 Активирован TLS SNI: {next.Domain}");
        }
    }
}
