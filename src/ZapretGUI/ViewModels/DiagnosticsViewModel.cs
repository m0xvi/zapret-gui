using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class DiagnosticsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private bool _isRunning;
        private double _progressValue;
        private double _progressMaximum = 14;
        private bool _progressIndeterminate = true;
        private string _progressPercentText = "";
        private string _progressText = "";
        private string _summary = "Диагностика ещё не запускалась";
        private string _summaryKey = "Muted";
        private string _message = "";
        private string _messageKey = "Info";
        private string _lastSavedText = "Результаты ещё не сохранялись";
        private bool _isDpiRunning;
        private double _dpiProgressValue;
        private double _dpiProgressMaximum = 1;
        private bool _dpiProgressIndeterminate = true;
        private string _dpiProgressPercentText = "";
        private string _dpiProgressText = "";
        private string _dpiSummary = "Проверка DPI ещё не запускалась";
        private string _dpiSummaryKey = "Muted";
        private string _dpiCustomHost = "";
        private NetworkObservationSnapshot _dpiObservation = new();
        private ResourceDiagnosisResult? _dpiBypassComparison;
        private DpiTargetResult? _dpiControlResult;
        private string _dpiSuiteSource = "";
        private DateTime? _dpiSuiteLoadedAt;
        private CancellationTokenSource? _dpiCts;

        public DiagnosticsViewModel(MainViewModel main)
        {
            _main = main;

            RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning && !IsDpiRunning);
            FixItemCommand = new AsyncRelayCommand(FixItemAsync, _ => !IsRunning && !IsDpiRunning);
            ClearDiscordCacheCommand = new RelayCommand(ClearDiscordCache);
            ResetNetworkCommand = new RelayCommand(ResetNetwork);
            RemoveServicesCommand = new AsyncRelayCommand(RemoveServicesAsync, () => !IsRunning && !IsDpiRunning);
            RecoverBypassCommand = new AsyncRelayCommand(RecoverBypassAsync, () => !IsRunning && !IsDpiRunning);
            OpenHostsCommand = new RelayCommand(() => Shell.OpenInNotepad(EngineService.SystemHostsPath));
            OpenSystemNetworkCommand = new RelayCommand(() => Shell.OpenUrl("ms-settings:network"));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            FixTimestampsCommand = new AsyncRelayCommand(FixTimestampsAsync, () => !IsRunning && !IsDpiRunning);
            RunDpiCommand = new AsyncRelayCommand(RunDpiAsync, () => !IsRunning && !IsDpiRunning);
            CancelDpiCommand = new RelayCommand(CancelDpi, () => IsDpiRunning);
            OpenDpiCommand = new RelayCommand(() => _main.Navigate("dpi"));
            SelectExpressTabCommand = new RelayCommand(() => SelectedSubTab = 0);
            SelectDpiTabCommand = new RelayCommand(() => SelectedSubTab = 1);
            SelectDeepCheckTabCommand = new RelayCommand(() => SelectedSubTab = 2);
            SelectSystemTabCommand = new RelayCommand(() => SelectedSubTab = 3);
            SelectResultsTabCommand = new RelayCommand(() => SelectedSubTab = 4);
            SelectVoiceRtcTabCommand = new RelayCommand(() => SelectedSubTab = 5);
            RunVoiceRtcAuditCommand = new AsyncRelayCommand(RunVoiceRtcAuditAsync, () => !IsRunning && !IsDpiRunning && !IsVoiceRtcRunning);
            OptimizeDiscordVoiceCommand = new AsyncRelayCommand(OptimizeDiscordVoiceAsync, () => !IsRunning && !IsDpiRunning && !IsVoiceRtcRunning);
            CleanDiscordAndNetworkCommand = new AsyncRelayCommand(() => CleanDiscordAndNetworkAsync(false), () => !IsCleaningDiscord && !IsRunning);
            CleanAndRestartDiscordCommand = new AsyncRelayCommand(() => CleanDiscordAndNetworkAsync(true), () => !IsCleaningDiscord && !IsRunning);
            DeepNetworkResetCommand = new AsyncRelayCommand(DeepNetworkResetAsync, () => !IsCleaningDiscord && !IsRunning);
            RefreshDiscordCacheStatusCommand = new RelayCommand(RefreshDiscordCacheStatus);
            ExportReportCommand = new RelayCommand(ExportReport,
                () => HasResults || DpiResults.Count > 0 ||
                     DiagnosticsHistoryStore.LoadLastDiagnostics() != null ||
                     DiagnosticsHistoryStore.LoadLastDpiCheck() != null);
            ExportArchiveCommand = new RelayCommand(ExportArchive,
                () => HasResults || DpiResults.Count > 0 ||
                     DiagnosticsHistoryStore.LoadLastDiagnostics() != null ||
                     DiagnosticsHistoryStore.LoadLastDpiCheck() != null);

            var saved = DiagnosticsHistoryStore.LoadLastDiagnostics();
            if (saved != null)
            {
                foreach (var item in saved.Items) Items.Add(item);
                Summary = saved.Summary;
                SummaryKey = GetSummaryKey(saved.Items);
                LastSavedText = "Сохранено ранее · устарело: " + saved.CreatedAt.ToString("dd.MM.yyyy HH:mm");
            }

            var savedDpi = DiagnosticsHistoryStore.LoadLastDpiCheck();
            if (savedDpi != null)
            {
                foreach (var result in savedDpi.Results) DpiResults.Add(result);
                DpiControlResult = savedDpi.ControlResult;
                _dpiSuiteSource = savedDpi.SuiteSource;
                _dpiSuiteLoadedAt = savedDpi.SuiteLoadedAt;
                DpiSummary = savedDpi.ErrorMessage.Length > 0 ? savedDpi.ErrorMessage : savedDpi.Summary;
                DpiSummaryKey = savedDpi.ErrorMessage.Length > 0
                    ? "Danger"
                    : savedDpi.Results.Concat(savedDpi.ControlResult == null
                        ? Array.Empty<DpiTargetResult>() : new[] { savedDpi.ControlResult })
                        .Any(r => r.HasSuspiciousProbe) ? "Warning" : "Success";
                DpiBypassComparison = savedDpi.BypassComparison;
                DpiObservation = savedDpi.Observation;
                DpiLastCheckText = "Сохранено ранее · устарело: " + savedDpi.CreatedAt.ToString("dd.MM.yyyy HH:mm");
            }
            (ExportReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportArchiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public AppSettings Settings => _main.Settings;

        public ObservableCollection<DiagnosticItem> Items { get; } = new();

        private int _selectedSubTab;

        public int SelectedSubTab
        {
            get => _selectedSubTab;
            set
            {
                if (Set(ref _selectedSubTab, Math.Clamp(value, 0, 5)))
                {
                    Raise(nameof(IsExpressTabSelected));
                    Raise(nameof(IsDpiTabSelected));
                    Raise(nameof(IsDeepCheckTabSelected));
                    Raise(nameof(IsSystemTabSelected));
                    Raise(nameof(IsResultsTabSelected));
                    Raise(nameof(IsVoiceRtcTabSelected));

                    if (_selectedSubTab == 3 || _selectedSubTab == 5)
                    {
                        RefreshDiscordCacheStatus();
                    }
                }
            }
        }

        public bool IsExpressTabSelected
        {
            get => _selectedSubTab == 0;
            set { if (value) SelectedSubTab = 0; }
        }

        public bool IsDpiTabSelected
        {
            get => _selectedSubTab == 1;
            set { if (value) SelectedSubTab = 1; }
        }

        public bool IsDeepCheckTabSelected
        {
            get => _selectedSubTab == 2;
            set { if (value) SelectedSubTab = 2; }
        }

        public bool IsSystemTabSelected
        {
            get => _selectedSubTab == 3;
            set { if (value) SelectedSubTab = 3; }
        }

        public bool IsResultsTabSelected
        {
            get => _selectedSubTab == 4;
            set { if (value) SelectedSubTab = 4; }
        }

        public bool IsVoiceRtcTabSelected
        {
            get => _selectedSubTab == 5;
            set { if (value) SelectedSubTab = 5; }
        }

        public ObservableCollection<DiscordVoiceServerCheck> VoiceServers { get; } = new();

        private bool _isVoiceRtcRunning;
        private string _voiceRtcStatusText = "Проверка Voice RTC ещё не выполнялась";
        private string _voiceRtcDiagnosisKey = "Muted";
        private string _voiceRtcSummary = "Нажмите «Проверить Voice RTC», чтобы протестировать WebRTC/STUN подключение к голосовым серверам Discord.";
        private string _voiceRtcRecommendation = "";
        private string _voiceRtcCheckedAtText = "";
        private DiscordVoiceRtcReport? _voiceRtcReport;

        public bool IsVoiceRtcRunning
        {
            get => _isVoiceRtcRunning;
            private set
            {
                if (Set(ref _isVoiceRtcRunning, value))
                {
                    (RunVoiceRtcAuditCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (OptimizeDiscordVoiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string VoiceRtcStatusText
        {
            get => _voiceRtcStatusText;
            private set => Set(ref _voiceRtcStatusText, value);
        }

        public string VoiceRtcDiagnosisKey
        {
            get => _voiceRtcDiagnosisKey;
            private set => Set(ref _voiceRtcDiagnosisKey, value);
        }

        public string VoiceRtcSummary
        {
            get => _voiceRtcSummary;
            private set => Set(ref _voiceRtcSummary, value);
        }

        public string VoiceRtcRecommendation
        {
            get => _voiceRtcRecommendation;
            private set => Set(ref _voiceRtcRecommendation, value);
        }

        public string VoiceRtcCheckedAtText
        {
            get => _voiceRtcCheckedAtText;
            private set => Set(ref _voiceRtcCheckedAtText, value);
        }

        public bool HasVoiceRtcResults => VoiceServers.Count > 0;
        public DiscordVoiceRtcReport? VoiceRtcReport
        {
            get => _voiceRtcReport;
            private set => Set(ref _voiceRtcReport, value);
        }

        private bool _isCleaningDiscord;
        private string _discordCleanStatusText = "Кэш не очищался в текущей сессии";
        private string _discordCacheStatusText = "Определение размера кэша Discord…";
        private bool _restartDiscordAfterClean;
        private DiscordCleanSummary? _lastDiscordCleanSummary;

        public bool IsCleaningDiscord
        {
            get => _isCleaningDiscord;
            private set
            {
                if (Set(ref _isCleaningDiscord, value))
                {
                    (CleanDiscordAndNetworkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CleanAndRestartDiscordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (DeepNetworkResetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string DiscordCleanStatusText
        {
            get => _discordCleanStatusText;
            private set => Set(ref _discordCleanStatusText, value);
        }

        public string DiscordCacheStatusText
        {
            get => _discordCacheStatusText;
            private set => Set(ref _discordCacheStatusText, value);
        }

        public bool RestartDiscordAfterClean
        {
            get => _restartDiscordAfterClean;
            set => Set(ref _restartDiscordAfterClean, value);
        }

        public DiscordCleanSummary? LastDiscordCleanSummary
        {
            get => _lastDiscordCleanSummary;
            private set
            {
                if (Set(ref _lastDiscordCleanSummary, value))
                {
                    Raise(nameof(HasDiscordCleanSummary));
                }
            }
        }

        public bool HasDiscordCleanSummary => _lastDiscordCleanSummary != null;

        public MonitoringViewModel Monitoring => _main.Monitoring;
        public DeepCheckViewModel DeepCheck => _main.DeepCheck;
        public HomeViewModel Home => _main.Home;
        public MainViewModel Main => _main;

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (Set(ref _isRunning, value))
                {
                    Raise(nameof(ProgressVisible));
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RunDpiCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelDpiCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (FixTimestampsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RecoverBypassCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (FixItemCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveServicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool ProgressVisible => IsRunning;

        public double ProgressValue
        {
            get => _progressValue;
            set => Set(ref _progressValue, value);
        }

        public double ProgressMaximum
        {
            get => _progressMaximum;
            set => Set(ref _progressMaximum, value);
        }

        public bool ProgressIndeterminate
        {
            get => _progressIndeterminate;
            set => Set(ref _progressIndeterminate, value);
        }

        public string ProgressPercentText
        {
            get => _progressPercentText;
            set => Set(ref _progressPercentText, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => Set(ref _progressText, value);
        }

        public string Summary
        {
            get => _summary;
            private set => Set(ref _summary, value);
        }

        public string SummaryKey
        {
            get => _summaryKey;
            private set => Set(ref _summaryKey, value);
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

        public string MessageKey
        {
            get => _messageKey;
            private set => Set(ref _messageKey, value);
        }

        public bool HasResults => Items.Count > 0;

        public string LastSavedText
        {
            get => _lastSavedText;
            private set => Set(ref _lastSavedText, value);
        }

        public ObservableCollection<DpiTargetResult> DpiResults { get; } = new();

        public bool IsDpiRunning
        {
            get => _isDpiRunning;
            private set
            {
                if (Set(ref _isDpiRunning, value))
                {
                    Raise(nameof(DpiProgressVisible));
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RunDpiCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelDpiCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (FixTimestampsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RecoverBypassCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool DpiProgressVisible => IsDpiRunning;
        public double DpiProgressValue
        {
            get => _dpiProgressValue;
            set => Set(ref _dpiProgressValue, value);
        }

        public double DpiProgressMaximum
        {
            get => _dpiProgressMaximum;
            set => Set(ref _dpiProgressMaximum, value);
        }

        public bool DpiProgressIndeterminate
        {
            get => _dpiProgressIndeterminate;
            set => Set(ref _dpiProgressIndeterminate, value);
        }

        public string DpiProgressText
        {
            get => _dpiProgressText;
            set => Set(ref _dpiProgressText, value);
        }

        public string DpiProgressPercentText
        {
            get => _dpiProgressPercentText;
            set => Set(ref _dpiProgressPercentText, value);
        }

        public string DpiSummary
        {
            get => _dpiSummary;
            private set => Set(ref _dpiSummary, value);
        }

        public string DpiSummaryKey
        {
            get => _dpiSummaryKey;
            private set => Set(ref _dpiSummaryKey, value);
        }

        private string _dpiLastCheckText = "Проверка ещё не выполнялась";
        public string DpiLastCheckText
        {
            get => _dpiLastCheckText;
            private set => Set(ref _dpiLastCheckText, value);
        }

        public NetworkObservationSnapshot DpiObservation
        {
            get => _dpiObservation;
            private set
            {
                if (Set(ref _dpiObservation, value)) Raise(nameof(DpiObservationText));
            }
        }

        public string DpiObservationText => string.IsNullOrWhiteSpace(DpiObservation.Summary)
            ? "Сетевые признаки пока не сохранены"
            : DpiObservation.Summary + $" · средняя задержка: {DpiObservation.AverageLatencyMs} мс";

        public ResourceDiagnosisResult? DpiBypassComparison
        {
            get => _dpiBypassComparison;
            private set
            {
                if (Set(ref _dpiBypassComparison, value)) Raise(nameof(DpiComparisonVisible));
            }
        }

        public bool DpiComparisonVisible => DpiBypassComparison != null;

        public DpiTargetResult? DpiControlResult
        {
            get => _dpiControlResult;
            private set
            {
                if (Set(ref _dpiControlResult, value)) Raise(nameof(DpiControlVisible));
            }
        }

        public bool DpiControlVisible => DpiControlResult != null;
        public string DpiControlAttemptText => DpiControlResult == null
            ? ""
            : "Попыток: " + DpiControlResult.AttemptCount;
        public string DpiSuiteSourceText => string.IsNullOrWhiteSpace(_dpiSuiteSource)
            ? "Источник набора не указан"
            : _dpiSuiteSource + (_dpiSuiteLoadedAt.HasValue
                ? " · загружен: " + _dpiSuiteLoadedAt.Value.ToString("dd.MM.yyyy HH:mm")
                : "");

        public string DpiCustomHost
        {
            get => _dpiCustomHost;
            set => Set(ref _dpiCustomHost, value);
        }

        public ICommand RunCommand { get; }
        public ICommand RunDpiCommand { get; }
        public ICommand CancelDpiCommand { get; }
        public ICommand OpenDpiCommand { get; }
        public ICommand SelectExpressTabCommand { get; }
        public ICommand SelectDpiTabCommand { get; }
        public ICommand SelectDeepCheckTabCommand { get; }
        public ICommand SelectSystemTabCommand { get; }
        public ICommand SelectResultsTabCommand { get; }
        public ICommand SelectVoiceRtcTabCommand { get; }
        public ICommand RunVoiceRtcAuditCommand { get; }
        public ICommand OptimizeDiscordVoiceCommand { get; }
        public ICommand CleanDiscordAndNetworkCommand { get; }
        public ICommand CleanAndRestartDiscordCommand { get; }
        public ICommand DeepNetworkResetCommand { get; }
        public ICommand RefreshDiscordCacheStatusCommand { get; }
        public ICommand FixItemCommand { get; }
        public ICommand ClearDiscordCacheCommand { get; }
        public ICommand ResetNetworkCommand { get; }
        public ICommand RemoveServicesCommand { get; }
        public ICommand RecoverBypassCommand { get; }
        public ICommand OpenHostsCommand { get; }
        public ICommand OpenSystemNetworkCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand FixTimestampsCommand { get; }
        public ICommand ExportReportCommand { get; }
        public ICommand ExportArchiveCommand { get; }

        public async Task RunAsync()
        {
            IsRunning = true;
            ProgressValue = 0;
            ProgressMaximum = 14;
            ProgressIndeterminate = false;
            ProgressPercentText = "0%";
            Items.Clear();
            Message = "";
            Summary = "Идёт проверка…";
            SummaryKey = "Warning";

            try
            {
                var progress = new Progress<string>(UpdateProgress);
                var items = await DiagnosticsService.RunAsync(Settings, progress);

                foreach (var item in items) Items.Add(item);
                Raise(nameof(HasResults));
                (ExportReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExportArchiveCommand as RelayCommand)?.RaiseCanExecuteChanged();

                var errors = items.Count(i => i.Status == DiagStatus.Error);
                var warnings = items.Count(i => i.Status == DiagStatus.Warning);

                Summary = errors > 0
                    ? $"Найдено критичных проблем: {errors}, предупреждений: {warnings}"
                    : warnings > 0
                        ? $"Критичных проблем нет, предупреждений: {warnings}"
                        : "Проблем не найдено — всё в порядке";
                SummaryKey = errors > 0 ? "Danger" : warnings > 0 ? "Warning" : "Success";
                DiagnosticsHistoryStore.SaveDiagnostics(items, Summary);
                LastSavedText = "Актуально · сохранено: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            }
            catch (Exception ex)
            {
                SetMessage("Ошибка диагностики: " + ex.Message, "Danger");
            }
            finally
            {
                ProgressText = "";
                IsRunning = false;
            }
        }

        private void ExportReport()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Экспорт диагностического отчёта",
                    Filter = "JSON-отчёт (*.json)|*.json",
                    FileName = "zapret-gui-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog() != true) return;

                File.WriteAllText(dialog.FileName, SerializeReport(BuildExportReport()), new UTF8Encoding(false));
                Message = "Отчёт сохранён: " + dialog.FileName;
                MessageKey = "Success";
            }
            catch (Exception ex)
            {
                Message = "Не удалось экспортировать отчёт: " + ex.Message;
                MessageKey = "Danger";
            }
        }

        private void ExportArchive()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Экспорт диагностического архива",
                    Filter = "Архив диагностики (*.zip)|*.zip",
                    FileName = "zapret-gui-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                    AddExtension = true,
                    DefaultExt = ".zip"
                };
                if (dialog.ShowDialog() != true) return;

                var reportJson = SerializeReport(BuildExportReport());
                var logText = string.Join(Environment.NewLine, ReadSafeLogTail());
                using var file = File.Create(dialog.FileName);
                using var archive = new ZipArchive(file, ZipArchiveMode.Create);
                AddArchiveText(archive, "diagnostics.json", reportJson);
                AddArchiveText(archive, "application.log", logText);
                AddArchiveText(archive, "README.txt",
                    "Архив Zapret GUI содержит диагностический JSON и обезличенный хвост журнала.\n" +
                    "Секреты, токены и содержимое settings.json в архив не включаются.\n");

                Message = "Диагностический архив сохранён: " + dialog.FileName;
                MessageKey = "Success";
            }
            catch (Exception ex)
            {
                Message = "Не удалось экспортировать архив: " + ex.Message;
                MessageKey = "Danger";
            }
        }

        private DiagnosticsExportReport BuildExportReport()
        {
            var savedDiagnostics = DiagnosticsHistoryStore.LoadLastDiagnostics();
            var savedDpi = DiagnosticsHistoryStore.LoadLastDpiCheck();
            return new DiagnosticsExportReport
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Application = new DiagnosticsExportApplication
                {
                    Version = _main.AppVersion,
                    EngineVersion = _main.EngineVersionText,
                    EnginePath = Settings.EnginePath,
                    SelectedStrategy = Settings.SelectedStrategy,
                    IsAdmin = _main.IsAdmin,
                    SafeMode = Settings.SafeMode,
                    FirstLaunchWizardCompleted = Settings.FirstLaunchWizardCompleted,
                    Provider = new ProviderContext
                    {
                        Name = Settings.ProviderContext?.Name ?? "",
                        Asn = Settings.ProviderContext?.Asn ?? "",
                        Source = Settings.ProviderContext?.Source ?? ProviderContextSource.Unknown,
                        CheckedAt = Settings.ProviderContext?.CheckedAt,
                        Confidence = Settings.ProviderContext?.Confidence ?? 0
                    }
                },
                Readiness = new DiagnosticsExportReadiness
                {
                    Status = _main.ReadinessText,
                    Details = _main.ReadinessDetails,
                    Key = _main.ReadinessKey
                },
                CurrentDiagnostics = Items.Count == 0 ? null : new DiagnosticsSnapshot
                {
                    CreatedAt = DateTime.Now,
                    Summary = Summary,
                    Items = Items.ToList()
                },
                LastSavedDiagnostics = savedDiagnostics,
                CurrentDpi = DpiResults.Count == 0 ? null : CreateDpiExport(
                    new DpiCheckSnapshot
                    {
                        CreatedAt = DateTime.Now,
                        TargetsTotal = DpiResults.Count,
                        TargetsTested = DpiResults.Count,
                        Summary = DpiSummary,
                        Observation = DpiObservation,
                        Results = DpiResults.ToList(),
                        ControlResult = DpiControlResult,
                        BypassComparison = DpiBypassComparison,
                        SuiteSource = "текущая проверка"
                    }),
                LastSavedDpi = savedDpi == null ? null : CreateDpiExport(savedDpi),
                StrategyHistory = StrategyEvaluationHistoryStore.Load().Take(100).ToList(),
                RecoveryHistory = RecoveryJournalStore.Load().Take(20).ToList(),
                LogTail = ReadSafeLogTail()
            };
        }

        private static string SerializeReport(DiagnosticsExportReport report)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = false,
                Converters = { new JsonStringEnumConverter() }
            };
            return JsonSerializer.Serialize(report, options);
        }

        private static void AddArchiveText(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        private static DiagnosticsExportDpi CreateDpiExport(DpiCheckSnapshot snapshot)
            => new()
            {
                CreatedAt = snapshot.CreatedAt,
                TargetsTotal = snapshot.TargetsTotal,
                TargetsTested = snapshot.TargetsTested,
                Summary = snapshot.Summary,
                ErrorMessage = snapshot.ErrorMessage,
                SuiteSource = snapshot.SuiteSource,
                SuiteLoadedAt = snapshot.SuiteLoadedAt,
                Observation = snapshot.Observation,
                Results = snapshot.Results.ToList(),
                ControlResult = snapshot.ControlResult,
                BypassComparison = snapshot.BypassComparison == null ? null : new DiagnosticsExportComparison
                {
                    Kind = snapshot.BypassComparison.Kind,
                    Level = snapshot.BypassComparison.Level,
                    Confidence = snapshot.BypassComparison.Confidence,
                    Summary = snapshot.BypassComparison.Summary
                }
            };

        private static List<string> ReadSafeLogTail()
        {
            try
            {
                if (!File.Exists(AppPaths.LogFile)) return new List<string>();
                return File.ReadLines(AppPaths.LogFile)
                    .TakeLast(200)
                    .Select(line => Regex.Replace(line,
                        @"(?i)(password|passwd|token|secret|api[_-]?key)=\S+", "$1=<скрыто>"))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void UpdateProgress(string text)
        {
            var match = Regex.Match(text, @"DIAGNOSTICS_PROGRESS:(\d+)/(\d+)\s*(?:—\s*)?(.*)");
            if (match.Success)
            {
                ProgressValue = Math.Min(
                    Math.Max(0, int.Parse(match.Groups[1].Value)),
                    Math.Max(1, int.Parse(match.Groups[2].Value)));
                ProgressMaximum = Math.Max(1, int.Parse(match.Groups[2].Value));
                ProgressIndeterminate = false;
                ProgressPercentText = $"{ProgressValue / ProgressMaximum * 100:0}%";
                ProgressText = match.Groups[3].Value + "…";
                return;
            }
            ProgressText = text + "…";
        }

        private void UpdateDpiProgress(string text)
        {
            var total = Regex.Match(text, @"DPI_TOTAL:(\d+)");
            if (total.Success)
            {
                DpiProgressMaximum = Math.Max(1, int.Parse(total.Groups[1].Value));
                DpiProgressValue = 0;
                DpiProgressIndeterminate = false;
                DpiProgressPercentText = "0%";
                DpiProgressText = "Подготовлены endpoint-ы для проверки";
                return;
            }

            var completed = Regex.Match(text, @"DPI_PROGRESS:(\d+)/(\d+)\s*(?:—\s*)?(.*)");
            if (completed.Success)
            {
                DpiProgressValue = Math.Min(
                    Math.Max(0, int.Parse(completed.Groups[1].Value)),
                    Math.Max(1, int.Parse(completed.Groups[2].Value)));
                DpiProgressMaximum = Math.Max(1, int.Parse(completed.Groups[2].Value));
                DpiProgressIndeterminate = false;
                DpiProgressPercentText = $"{DpiProgressValue / DpiProgressMaximum * 100:0}%";
                DpiProgressText = completed.Groups[3].Value.Length > 0
                    ? completed.Groups[3].Value + "…"
                    : "Проверка endpoint-ов…";
                return;
            }

            DpiProgressText = text + "…";
            var fallback = Regex.Match(text, @"(\d+)\s+из\s+(\d+)");
            if (!fallback.Success) return;
            DpiProgressValue = Math.Max(DpiProgressValue, int.Parse(fallback.Groups[1].Value));
            DpiProgressMaximum = Math.Max(1, int.Parse(fallback.Groups[2].Value));
            DpiProgressIndeterminate = false;
            DpiProgressPercentText = $"{DpiProgressValue / DpiProgressMaximum * 100:0}%";
        }

        private async Task RunDpiAsync()
        {
            if (IsRunning || IsDpiRunning) return;
            var confirm = System.Windows.MessageBox.Show(
                "Проверка DPI выполняет сетевые пробы и для сравнения может временно остановить и снова запустить текущий обход. Системная служба не устанавливается, но сетевой трафик на время теста изменится. Продолжить?",
                "Подтверждение проверки DPI", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            _dpiCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = _dpiCts.Token;
            IsDpiRunning = true;
            DpiProgressValue = 0;
            DpiProgressMaximum = 1;
            DpiProgressIndeterminate = true;
            DpiProgressPercentText = "";
            DpiProgressText = "Подготавливаю набор DPI-проверки… Предыдущий результат сохраняется до завершения новой проверки.";
            DpiSummaryKey = "Warning";
            try
            {
                var progress = new Progress<string>(UpdateDpiProgress);
                DpiProgressText = "Сравниваю прямое соединение с текущим обходом…";
                var comparisonTarget = MonitorTarget.CreateBuiltIn("DPI comparison", "https://www.youtube.com/generate_204");
                if (!string.IsNullOrWhiteSpace(DpiCustomHost) &&
                    MonitorTarget.TryCreate(DpiCustomHost, null, out var customTarget, out _) && customTarget != null)
                    comparisonTarget = customTarget;
                var comparison = await _main.Bypass.DiagnoseResourceAsync(comparisonTarget, ct);
                var snapshot = await _main.Bypass.RunDpiCheckAsync(
                    string.IsNullOrWhiteSpace(DpiCustomHost) ? null : DpiCustomHost,
                    progress, ct, maxTargets: 34);
                snapshot.BypassComparison = comparison;
                snapshot.Observation = NetworkObservationSnapshot.From(
                    snapshot.Results, comparison, snapshot.CreatedAt, snapshot.ControlResult);
                DpiBypassComparison = comparison;
                DpiControlResult = snapshot.ControlResult;
                _dpiSuiteSource = snapshot.SuiteSource;
                _dpiSuiteLoadedAt = snapshot.SuiteLoadedAt;
                Raise(nameof(DpiSuiteSourceText));
                DpiObservation = snapshot.Observation;
                DpiResults.Clear();
                foreach (var result in snapshot.Results) DpiResults.Add(result);
                (ExportReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExportArchiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                DpiSummary = snapshot.ErrorMessage.Length > 0 ? snapshot.ErrorMessage : snapshot.Summary;
                DpiSummaryKey = snapshot.ErrorMessage.Length > 0
                    ? "Danger"
                    : snapshot.Results.Any(r => r.HasSuspiciousProbe) ? "Warning" : "Success";
                DpiLastCheckText = "Актуально · проверено: " + snapshot.CreatedAt.ToString("dd.MM.yyyy HH:mm");
                Raise(nameof(DpiLastCheckText));
                DiagnosticsHistoryStore.SaveDpiCheck(snapshot);
            }
            catch (OperationCanceledException)
            {
                DpiSummary = "Проверка DPI отменена. Состояние обхода восстановлено.";
                DpiSummaryKey = "Info";
                DpiProgressText = "Отмена завершена";
            }
            catch (Exception ex)
            {
                DpiSummary = "Ошибка DPI-проверки: " + ex.Message;
                DpiSummaryKey = "Danger";
            }
            finally
            {
                _dpiCts?.Dispose();
                _dpiCts = null;
                DpiProgressText = "";
                IsDpiRunning = false;
            }
        }

        private void CancelDpi()
        {
            if (!IsDpiRunning) return;
            DpiProgressText = "Отменяю проверку и восстанавливаю состояние обхода…";
            _dpiCts?.Cancel();
        }

        private async Task RecoverBypassAsync()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Остановить текущий обход, проверить службы и timestamps, а затем выполнить диагностику?",
                "Восстановление обхода", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsRunning = true;
            ProgressText = "Останавливаю обход для восстановления…";
            try
            {
                var stopped = await _main.Bypass.StopAsync();
                var timestamps = WinServices.EnsureTcpTimestamps();
                _main.Home.RefreshStatus();
                SetMessage(stopped.Ok && timestamps.Ok
                    ? "Обход остановлен, TCP timestamps включены. Запускаю повторную диагностику."
                    : $"Восстановление завершено с предупреждением: {stopped.Message}; {timestamps.Message}",
                    stopped.Ok && timestamps.Ok ? "Success" : "Warning");
            }
            catch (Exception ex)
            {
                SetMessage("Не удалось восстановить обход: " + ex.Message, "Danger");
            }
            finally
            {
                ProgressText = "";
                IsRunning = false;
            }

            await RunAsync();
        }

        /// <summary>Автоисправление отдельного пункта (кнопка «Исправить» в строке).</summary>
        private async Task FixItemAsync(object? parameter)
        {
            if (parameter is not DiagnosticItem item || !item.HasAutoFix) return;
            var confirm = System.Windows.MessageBox.Show(
                $"Исправление «{item.Title}» может изменить службу, hosts, WinDivert, файлы движка или другие системные настройки. Диагностика останется доступна без этого действия. Выполнить вручную сейчас?",
                "Подтверждение исправления", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsRunning = true;
            ProgressText = "Исправляю: " + item.Title;
            try
            {
                var (ok, message) = item.FixId switch
                {
                    "admin" => FixAdmin(),
                    "engine" => await FixEngineAsync(),
                    "movengine" => FixMoveEngine(),
                    "bfe" => DiagnosticsService.FixBfe(),
                    "timestamps" => FixTimestampsResult(),
                    "proxy" => DiagnosticsService.DisableProxy(),
                    "conflicts" => DiagnosticsService.StopConflictingServices(),
                    "others" => DiagnosticsService.StopForeignBypass(Settings.EnginePath),
                    "divert" => DiagnosticsService.RemoveDivertLeftovers(),
                    "zapretstuck" => await FixZapretStuckAsync(),
                    "hosts" => await FixHostsAsync(),
                    "cache" => EngineService.ClearDiscordCache(),
                    _ => (false, "Неизвестное исправление: " + item.FixId)
                };
                SetMessage(message, ok ? "Success" : "Warning");
                AppLog.Info($"Автоисправление «{item.Title}»: {message}");
            }
            finally
            {
                ProgressText = "";
                IsRunning = false;
            }

            // Перепроверяем, чтобы пункт обновил статус (сообщение сохраняем)
            var savedMessage = Message;
            var savedKey = MessageKey;
            await RunAsync();
            SetMessage(savedMessage, savedKey);
        }

        private static (bool Ok, string Message) FixAdmin()
        {
            if (Shell.RestartElevated())
            {
                System.Windows.Application.Current.Shutdown();
                return (true, "Перезапускаю от администратора…");
            }
            return (false, "Не удалось запросить права администратора");
        }

        private async Task<(bool Ok, string Message)> FixEngineAsync()
        {
            if (!Shell.IsAdmin())
                return (false, "Для установки движка нужны права администратора");
            var installed = await _main.Updates.EnsureEngineInstalledAsync();
            if (installed)
            {
                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
                return (true, "Движок установлен");
            }
            return (false, "Не удалось скачать движок — смотрите страницу «Обновления»");
        }

        private (bool Ok, string Message) FixMoveEngine()
        {
            var (ok, message) = DiagnosticsService.MoveEngineToSafePath(Settings);
            if (ok)
            {
                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
                _main.SettingsPage.Reload();
            }
            return (ok, message);
        }

        private static (bool Ok, string Message) FixTimestampsResult()
            => WinServices.EnsureTcpTimestamps();

        private async Task<(bool Ok, string Message)> FixZapretStuckAsync()
        {
            var result = await _main.Bypass.RemoveServiceAsync();
            _main.Home.RefreshStatus();
            return (result.Ok, result.Message);
        }

        private async Task<(bool Ok, string Message)> FixHostsAsync()
        {
            var check = await EngineService.CheckHostsAsync();
            if (!check.Ok) return (false, check.Message);
            if (!check.NeedsUpdate) return (true, "Файл hosts уже актуален");
            return EngineService.ApplyHosts(check.TempFile);
        }

        public async Task CleanDiscordAndNetworkAsync(bool restartDiscord = false)
        {
            if (IsCleaningDiscord) return;

            IsCleaningDiscord = true;
            DiscordCleanStatusText = "Подготовка к очистке кэша Discord и сбросу сети…";
            try
            {
                var progress = new Progress<string>(s => DiscordCleanStatusText = s);
                var summary = await DiscordNetworkCleaner.CleanAsync(new DiscordCleanOptions
                {
                    CloseDiscordProcesses = true,
                    RestartDiscordAfterClean = restartDiscord || RestartDiscordAfterClean,
                    ResetNetworkStack = true
                }, progress).ConfigureAwait(true);

                LastDiscordCleanSummary = summary;
                RefreshDiscordCacheStatus();

                DiscordCleanStatusText = summary.Message;
                SetMessage(summary.Message, summary.Ok ? "Success" : "Warning");
                AppLog.Info($"[1-Клик Очистка Discord]: {summary.Message}");
                _main.Home.ShowSuccess($"✅ {summary.Message}");
            }
            catch (Exception ex)
            {
                DiscordCleanStatusText = "Ошибка очистки: " + ex.Message;
                SetMessage("Ошибка очистки кэша: " + ex.Message, "Danger");
                AppLog.Error("Ошибка очистки кэша Discord", ex);
            }
            finally
            {
                IsCleaningDiscord = false;
            }
        }

        public async Task DeepNetworkResetAsync()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Будет выполнен глубокий сброс сетевого стека Windows:\n\n" +
                "• netsh winsock reset (сброс каталога Winsock)\n" +
                "• netsh int ip reset all (сброс стека TCP/IP)\n" +
                "• netsh winhttp reset proxy (сброс прокси WinHTTP)\n" +
                "• ipconfig /flushdns (сброс кэша DNS)\n" +
                "• arp -d * (очистка ARP-таблицы)\n" +
                "• nbtstat -R (сброс NetBIOS кэша)\n" +
                "• netsh interface tcp set global timestamps=enabled\n\n" +
                "После выполнения потребуется перезагрузка компьютера. Продолжить?",
                "Глубокий сброс сетевого стека", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsCleaningDiscord = true;
            DiscordCleanStatusText = "Выполняется глубокий сброс сетевого стека Windows…";
            try
            {
                var progress = new Progress<string>(s => DiscordCleanStatusText = s);
                var report = await DiscordNetworkCleaner.DeepNetworkStackResetAsync(progress).ConfigureAwait(true);
                DiscordCleanStatusText = "Сброс сети завершён. Рекомендуется перезагрузить ПК.";
                SetMessage(string.Join("\n", report), "Warning");
                AppLog.Info("[Deep Network Reset]:\n" + string.Join("\n", report));
            }
            catch (Exception ex)
            {
                DiscordCleanStatusText = "Ошибка сброса сети: " + ex.Message;
                SetMessage("Ошибка сброса сети: " + ex.Message, "Danger");
            }
            finally
            {
                IsCleaningDiscord = false;
            }
        }

        public void RefreshDiscordCacheStatus()
        {
            try
            {
                var (totalBytes, totalFiles, editions) = DiscordNetworkCleaner.GetDetailedCacheStatus();
                if (editions.Count == 0 || totalFiles == 0)
                {
                    DiscordCacheStatusText = "Кэш Discord чист (0 файлов)";
                }
                else
                {
                    var editionNames = string.Join(", ", editions.Select(e => e.EditionName));
                    DiscordCacheStatusText = $"Кэш Discord: {DiscordCacheDirectoryInfo.FormatBytes(totalBytes)} ({totalFiles} файлов) · {editionNames}";
                }
            }
            catch
            {
                DiscordCacheStatusText = "Размер кэша неизвестен";
            }
        }

        private void ClearDiscordCache()
        {
            _ = CleanDiscordAndNetworkAsync(false);
        }

        private void ResetNetwork()
        {
            _ = DeepNetworkResetAsync();
        }

        private async Task RemoveServicesAsync()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Будут остановлены и удалены служба zapret и связанные службы WinDivert. Это системное действие требует администратора. Продолжить?",
                "Подтверждение удаления служб", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsRunning = true;
            ProgressText = "Удаляю службы zapret и WinDivert…";
            try
            {
                var result = await _main.Bypass.RemoveServiceAsync();
                SetMessage(result.Message, "Success");
                _main.Home.RefreshStatus();
                await RunAsync();
            }
            finally
            {
                ProgressText = "";
                IsRunning = false;
            }
        }

        private async Task FixTimestampsAsync()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Будет изменён глобальный параметр TCP timestamps через netsh. Это влияет на сетевой стек Windows и требует администратора. Выполнить?",
                "Подтверждение изменения TCP timestamps", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsRunning = true;
            try
            {
                var (ok, message) = WinServices.EnsureTcpTimestamps();
                SetMessage(message, ok ? "Success" : "Warning");
            }
            finally
            {
                IsRunning = false;
            }
            await RunAsync();
        }

        public async Task RunVoiceRtcAuditAsync()
        {
            if (IsVoiceRtcRunning) return;
            IsVoiceRtcRunning = true;
            VoiceRtcStatusText = "Запуск проверки голосовых серверов Discord (WebRTC/STUN)…";
            VoiceRtcDiagnosisKey = "Warning";
            VoiceServers.Clear();

            var progress = new Progress<string>(text => VoiceRtcStatusText = text);

            try
            {
                var report = await DiscordVoiceRtcProber.RunFullVoiceAuditAsync(progress).ConfigureAwait(true);
                VoiceRtcReport = report;
                VoiceRtcSummary = report.SummaryText;
                VoiceRtcDiagnosisKey = report.DiagnosisKey;
                VoiceRtcRecommendation = report.RecommendationText;
                VoiceRtcStatusText = $"Проверка завершена: {report.PassedCount}/{report.TotalCount} шлюзов доступны (пинг {report.AveragePingMs} мс)";
                VoiceRtcCheckedAtText = "Проверено: " + report.CheckedAt.ToString("HH:mm:ss");

                foreach (var s in report.Servers)
                {
                    VoiceServers.Add(s);
                }

                Raise(nameof(HasVoiceRtcResults));
                if (report.OverallVoiceReady)
                {
                    _main.Home.ShowSuccess("✅ Голосовой стек Discord (RTC/UDP) полностью доступен!");
                }
                else
                {
                    _main.Home.ShowWarning("⚠️ Обнаружен сбой голосового подключения Discord (UDP WebRTC дропается ТСПУ).");
                }
            }
            catch (Exception ex)
            {
                VoiceRtcStatusText = "Ошибка проверки: " + ex.Message;
                VoiceRtcDiagnosisKey = "Danger";
            }
            finally
            {
                IsVoiceRtcRunning = false;
                Raise(nameof(HasVoiceRtcResults));
            }
        }

        public async Task OptimizeDiscordVoiceAsync()
        {
            if (IsVoiceRtcRunning) return;
            IsVoiceRtcRunning = true;
            VoiceRtcStatusText = "Применяю оптимизированную конфигурацию для Discord Voice…";

            try
            {
                // 1. Гарантируем наполнение списков доменов
                DomainListUpdater.EnsureSeeded(Settings.EnginePath);

                // 2. Включаем поддержку TCP Timestamps
                WinServices.EnsureTcpTimestamps();

                // 3. Выбираем стратегию с поддержкой UDP desync (general (ALT9) или general (EXP))
                var targetStrat = _main.Strategies.Find("general (ALT9)")
                               ?? _main.Strategies.Find("general (EXP)")
                               ?? _main.Strategies.Find("general (ALT13)")
                               ?? _main.Strategies.Items.FirstOrDefault(s => s.UsesGameFilter || s.UsesFakeQuic);

                if (targetStrat != null)
                {
                    _main.Settings.SelectedStrategy = targetStrat.Name;
                    SettingsStore.Save(_main.Settings);
                    await _main.StrategiesPage.ApplyStrategyAsync(targetStrat).ConfigureAwait(true);
                }

                // 4. Очищаем кэш Discord и сбрасываем DNS
                EngineService.ClearDiscordCache();
                DnsManagementService.FlushDnsCache();

                VoiceRtcStatusText = "Оптимизация Discord Voice завершена! Перезапустите Discord и войдите в голосовой канал.";
                VoiceRtcDiagnosisKey = "Success";
                _main.Home.ShowSuccess("✅ Настройки для Discord Voice применены (стратегия " + (_main.Settings.SelectedStrategy ?? "ALT9") + ", UDP 50000-65535, кэш очищен).");

                // Перепроверяем войс
                await RunVoiceRtcAuditAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                VoiceRtcStatusText = "Ошибка оптимизации: " + ex.Message;
                VoiceRtcDiagnosisKey = "Danger";
            }
            finally
            {
                IsVoiceRtcRunning = false;
            }
        }

        public void RefreshTheme()
        {
            var savedItems = Items.ToList();
            Items.Clear();
            foreach (var item in savedItems) Items.Add(item);
            var savedDpi = DpiResults.ToList();
            DpiResults.Clear();
            foreach (var result in savedDpi) DpiResults.Add(result);
            Raise(nameof(SummaryKey));
            Raise(nameof(MessageKey));
            Raise(nameof(LastSavedText));
            Raise(nameof(DpiSummaryKey));
        }

        private static string GetSummaryKey(IEnumerable<DiagnosticItem> items)
        {
            var list = items.ToList();
            return list.Any(i => i.Status == DiagStatus.Error) ? "Danger"
                : list.Any(i => i.Status == DiagStatus.Warning) ? "Warning" : "Success";
        }

        private void SetMessage(string message, string key)
        {
            MessageKey = key;
            Message = message;
        }
    }
}
