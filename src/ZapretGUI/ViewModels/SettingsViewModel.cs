using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _enginePath;
        private int _themeIndex;
        private string _providerName = "";
        private string _providerAsn = "";
        private int _providerConfidenceIndex;
        private string _status = "Изменения сохраняются автоматически";
        private bool _isProviderLookupBusy;
        private int _selectedTabIndex;

        public string[] SettingsTabs { get; } = { "⚙ Общие", "🛡 Обход", "🌐 Сеть", "🎨 Интерфейс", "🎮 Игры", "🔄 Обновления", "📄 Журнал", "ℹ️ О программе" };

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (Set(ref _selectedTabIndex, Math.Clamp(value, 0, SettingsTabs.Length - 1)))
                {
                    Raise(nameof(IsGeneralTabSelected));
                    Raise(nameof(IsBypassTabSelected));
                    Raise(nameof(IsNetworkTabSelected));
                    Raise(nameof(IsAppearanceTabSelected));
                    Raise(nameof(IsGamingTabSelected));
                    Raise(nameof(IsUpdatesTabSelected));
                    Raise(nameof(IsLogsTabSelected));
                    Raise(nameof(IsAboutTabSelected));
                    Raise(nameof(SelectedTabHint));
                }
            }
        }

        public bool IsGeneralTabSelected
        {
            get => _selectedTabIndex == 0;
            set { if (value) SelectedTabIndex = 0; }
        }

        public bool IsBypassTabSelected
        {
            get => _selectedTabIndex == 1;
            set { if (value) SelectedTabIndex = 1; }
        }

        public bool IsNetworkTabSelected
        {
            get => _selectedTabIndex == 2;
            set { if (value) SelectedTabIndex = 2; }
        }

        public bool IsAppearanceTabSelected
        {
            get => _selectedTabIndex == 3;
            set { if (value) SelectedTabIndex = 3; }
        }

        public bool IsGamingTabSelected
        {
            get => _selectedTabIndex == 4;
            set { if (value) SelectedTabIndex = 4; }
        }

        public bool IsUpdatesTabSelected
        {
            get => _selectedTabIndex == 5;
            set { if (value) SelectedTabIndex = 5; }
        }

        public bool IsLogsTabSelected
        {
            get => _selectedTabIndex == 6;
            set { if (value) SelectedTabIndex = 6; }
        }

        public bool IsAboutTabSelected
        {
            get => _selectedTabIndex == 7;
            set { if (value) SelectedTabIndex = 7; }
        }

        public bool SeamlessFailoverEnabled
        {
            get => Settings.SeamlessFailoverEnabled;
            set { if (Settings.SeamlessFailoverEnabled != value) { Settings.SeamlessFailoverEnabled = value; SettingsStore.Save(Settings); _main.NotifySeamlessChanged(); Raise(nameof(SeamlessFailoverEnabled)); Raise(nameof(SeamlessStatus)); Raise(nameof(SeamlessStatusKey)); } }
        }
        public int SeamlessCheckMinutes
        {
            get => Settings.SeamlessCheckMinutes;
            set { var v = Math.Clamp(value, 2, 60); if (Settings.SeamlessCheckMinutes != v) { Settings.SeamlessCheckMinutes = v; SettingsStore.Save(Settings); _main.NotifySeamlessChanged(); Raise(nameof(SeamlessCheckMinutes)); } }
        }
        public int SeamlessCooldownMinutes
        {
            get => Settings.SeamlessCooldownMinutes;
            set { var v = Math.Clamp(value, 5, 120); if (Settings.SeamlessCooldownMinutes != v) { Settings.SeamlessCooldownMinutes = v; SettingsStore.Save(Settings); Raise(nameof(SeamlessCooldownMinutes)); } }
        }
        public string SeamlessStatus => _main.SeamlessStatusText;
        public string SeamlessStatusKey => _main.SeamlessStatusKey;
        public string SeamlessLastSwitch => _main.SeamlessLastSwitchText;
        public ICommand TestSeamlessNowCommand => new AsyncRelayCommand(async () => { Status = "Запускаю внеплановую проверку…"; await _main.SeamlessFailover.CheckNowAsync(); Raise(nameof(SeamlessStatus)); Status = _main.SeamlessStatusText; }, () => !Settings.SafeMode && _main.Bypass.GetStatus().IsRunning);

        public bool AutoSwitchToBestStrategy
        {
            get => Settings.AutoSwitchToBestStrategy;
            set { if (Settings.AutoSwitchToBestStrategy != value) { Settings.AutoSwitchToBestStrategy = value; SettingsStore.Save(Settings); Raise(nameof(AutoSwitchToBestStrategy)); } }
        }

        public int BestStrategyCheckMinutes
        {
            get => Settings.BestStrategyCheckMinutes;
            set { var v = Math.Clamp(value, 5, 120); if (Settings.BestStrategyCheckMinutes != v) { Settings.BestStrategyCheckMinutes = v; SettingsStore.Save(Settings); Raise(nameof(BestStrategyCheckMinutes)); } }
        }

        public string SelectedTabHint => SelectedTabIndex switch
        {
            0 => "Движок, папки и системный автозапуск — всё, что нужно для первого старта.",
            1 => "Как ведёт себя обход: автозапуск, безопасный режим, сторож и автоподбор.",
            2 => "Провайдер, телеметрия и фоновый мониторинг — диагностика вашей сети.",
            3 => "Тема, масштаб, трей, горячие клавиши и мини-виджет HUD.",
            4 => "Детектор игр, игровой режим и твики сети для минимальных задержек.",
            _ => ""
        };

        public SettingsViewModel(MainViewModel main)
        {
            _main = main;
            _enginePath = main.Settings.EnginePath;
            _themeIndex = (int)main.Settings.Theme;
            SyncProviderDraft();
            LastBackupText = GetBackupText();

            BrowseEnginePathCommand = new RelayCommand(BrowseEnginePath);
            DetectEngineCommand = new RelayCommand(DetectEngine);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            OpenSettingsFileCommand = new RelayCommand(() => Shell.OpenInNotepad(AppPaths.SettingsFile));
            OpenLogsFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.LogDir));
            OpenBackupFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.BackupDir));
            ResetSettingsCommand = new RelayCommand(ResetSettings);
            ValidateEngineCommand = new RelayCommand(ValidateEngine);
            OpenRepoCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl));
            OpenHostsCommand = new RelayCommand(() => Shell.OpenInNotepad(EngineService.SystemHostsPath));
            SaveProviderContextCommand = new RelayCommand(SaveProviderContext);
            ClearProviderContextCommand = new RelayCommand(ClearProviderContext);
            LookupProviderCommand = new AsyncRelayCommand(LookupProviderAsync, () => !IsProviderLookupBusy);
            RerunFirstLaunchWizardCommand = new RelayCommand(RerunFirstLaunchWizard);
            ApplyGamingTweaksCommand = new AsyncRelayCommand(ApplyGamingTweaksAsync);
            RevertGamingTweaksCommand = new AsyncRelayCommand(RevertGamingTweaksAsync);
            OpenOverlayCommand = new RelayCommand(() => _main.ToggleMiniOverlay());
            OpenLogsCommand = new RelayCommand(() => _main.Navigate("logs"));
            OpenUpdatesCommand = new RelayCommand(() => _main.Navigate("updates"));
            OpenAboutCommand = new RelayCommand(() => _main.Navigate("about"));
            RunFullDiagnosticsAndExportCommand = new AsyncRelayCommand(RunFullDiagnosticsAndExportAsync, () => !IsRunningFullCheckCycle);
            CancelFullDiagnosticsCommand = new RelayCommand(CancelFullDiagnostics, () => IsRunningFullCheckCycle);
            CopyTelemetryMarkdownCommand = new RelayCommand(CopyTelemetryMarkdown);
            ExportTelemetryJsonCommand = new RelayCommand(ExportTelemetryJson);
            ExportTelemetryZipCommand = new AsyncRelayCommand(ExportTelemetryZipAsync);
            RefreshGamingOptimization();
            try { RefreshToolbarMetricsHosts(); } catch {}
            try
            {
                if (_main.Monitoring != null)
                {
                    _main.Monitoring.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MonitoringViewModel.Targets)) try { RefreshToolbarMetricsHosts(); } catch {} };
                    _main.Monitoring.Targets.CollectionChanged += (_, _) => { try { RefreshToolbarMetricsHosts(); } catch {} };
                }
            }
            catch {}
        }

        public ICommand RunFullDiagnosticsAndExportCommand { get; }
        public ICommand CancelFullDiagnosticsCommand { get; }
        public ICommand CopyTelemetryMarkdownCommand { get; }
        public ICommand ExportTelemetryJsonCommand { get; }
        public ICommand ExportTelemetryZipCommand { get; }

        private bool _isRunningFullCheckCycle;
        public bool IsRunningFullCheckCycle
        {
            get => _isRunningFullCheckCycle;
            private set
            {
                if (Set(ref _isRunningFullCheckCycle, value))
                {
                    (RunFullDiagnosticsAndExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelFullDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private string _fullCheckStatusText = "";
        public string FullCheckStatusText
        {
            get => _fullCheckStatusText;
            private set => Set(ref _fullCheckStatusText, value);
        }

        private double _fullCheckProgressValue;
        public double FullCheckProgressValue
        {
            get => _fullCheckProgressValue;
            private set => Set(ref _fullCheckProgressValue, value);
        }

        private double _fullCheckProgressMax = 100;
        public double FullCheckProgressMax
        {
            get => _fullCheckProgressMax;
            private set => Set(ref _fullCheckProgressMax, value);
        }

        private string _fullCheckProgressPercentText = "0%";
        public string FullCheckProgressPercentText
        {
            get => _fullCheckProgressPercentText;
            private set => Set(ref _fullCheckProgressPercentText, value);
        }

        private bool _fullCheckCompleted;
        public bool FullCheckCompleted
        {
            get => _fullCheckCompleted;
            private set => Set(ref _fullCheckCompleted, value);
        }

        public string TelemetrySummaryText
        {
            get
            {
                var tested = _main.Strategies.Items.Count(s => s.TestState != StrategyTestState.NotTested);
                var passed = _main.Strategies.Items.Count(s => s.IsRecommended || (s.TestResult != null && s.TestResult.PassedCount >= 6));
                var prov = _main.Settings.ProviderContext?.Name;
                var provText = string.IsNullOrWhiteSpace(prov) ? "Провайдер: авто/не указан" : $"Провайдер: {prov}";
                return $"Протестировано стратегий: {tested}/{_main.Strategies.Items.Count} (Рабочих: {passed}) · {provText}";
            }
        }

        private async Task RunFullDiagnosticsAndExportAsync()
        {
            if (IsRunningFullCheckCycle) return;
            IsRunningFullCheckCycle = true;
            FullCheckCompleted = false;
            FullCheckStatusText = "Подготовка: проверка списков доменов и системных служб…";
            FullCheckProgressValue = 0;
            FullCheckProgressMax = 100;
            FullCheckProgressPercentText = "0%";

            try
            {
                // 1. Проверка и автоматическое наполнение списков доменов
                DomainListUpdater.EnsureSeeded(Settings.EnginePath);
                WinServices.EnsureTcpTimestamps();

                // Попытка фонового обновления списков с GitHub
                try
                {
                    await Task.Run(() => DomainListUpdater.UpdateAllAsync(Settings.EnginePath)).ConfigureAwait(true);
                }
                catch { }

                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _main.Strategies.Refresh();
                        _main.StrategiesPage.Refresh();
                        _main.RefreshReadiness();
                    });
                }
                else
                {
                    _main.Strategies.Refresh();
                    _main.StrategiesPage.Refresh();
                    _main.RefreshReadiness();
                }

                var total = _main.Strategies.Items.Count;
                if (total == 0)
                {
                    IsRunningFullCheckCycle = false;
                    _main.Home.ShowError("Стратегии не найдены. Проверьте папку движка.");
                    return;
                }

                FullCheckProgressMax = total;

                var progress = new Progress<string>(text =>
                {
                    FullCheckStatusText = text;
                    if (text.StartsWith("[", StringComparison.Ordinal) && text.Contains('/'))
                    {
                        var endIdx = text.IndexOf(']');
                        if (endIdx > 1)
                        {
                            var span = text.Substring(1, endIdx - 1);
                            var parts = span.Split('/');
                            if (parts.Length == 2 && int.TryParse(parts[0], out var current) && int.TryParse(parts[1], out var max))
                            {
                                FullCheckProgressValue = current;
                                FullCheckProgressMax = max;
                                FullCheckProgressPercentText = $"{(int)((double)current / Math.Max(1, max) * 100)}%";
                            }
                        }
                    }
                });

                // 2. Полное тестирование всех стратегий каталога
                await _main.StrategiesPage.TestAllAsync(progress).ConfigureAwait(true);

                FullCheckProgressValue = FullCheckProgressMax;
                FullCheckProgressPercentText = "100%";
                FullCheckCompleted = true;
                FullCheckStatusText = "Тестирование завершено! Формирую полный диагностический слепок…";

                // 3. Автоматический сбор и копирование отчёта в буфер обмена
                var dump = ProviderTelemetryExporter.Collect(_main);
                var md = ProviderTelemetryExporter.GenerateMarkdown(dump);
                System.Windows.Clipboard.SetText(md);

                Raise(nameof(TelemetrySummaryText));
                _main.Home.ShowSuccess("✅ Все проверки завершены! Полный отчёт скопирован в буфер обмена (просто нажмите Ctrl+V в чат).");
                Status = "Слепок сформирован и скопирован в буфер обмена (" + DateTime.Now.ToString("HH:mm:ss") + ")";
                FullCheckStatusText = "Готово! Полный отчёт со всеми стратегиями скопирован в буфер обмена.";
            }
            catch (OperationCanceledException)
            {
                FullCheckStatusText = "Тестирование отменено пользователем.";
            }
            catch (Exception ex)
            {
                FullCheckStatusText = "Ошибка при выполнении проверок: " + ex.Message;
                _main.Home.ShowError("Сбой цикла проверок: " + ex.Message);
            }
            finally
            {
                IsRunningFullCheckCycle = false;
                Raise(nameof(TelemetrySummaryText));
            }
        }

        private void CancelFullDiagnostics()
        {
            _main.StrategiesPage.CancelTestCommand.Execute(null);
            FullCheckStatusText = "Отменяю тестирование…";
        }

        private void CopyTelemetryMarkdown()
        {
            try
            {
                var dump = ProviderTelemetryExporter.Collect(_main);
                var md = ProviderTelemetryExporter.GenerateMarkdown(dump);
                System.Windows.Clipboard.SetText(md);
                _main.Home.ShowSuccess("Полный отчёт и телеметрия скопированы в буфер обмена! Вы можете вставить его в чат.");
                Status = "Отчёт скопирован в буфер обмена (" + DateTime.Now.ToString("HH:mm:ss") + ")";
            }
            catch (Exception ex)
            {
                _main.Home.ShowError("Не удалось скопировать отчёт: " + ex.Message);
            }
        }

        private void ExportTelemetryJson()
        {
            try
            {
                var dump = ProviderTelemetryExporter.Collect(_main);
                var json = ProviderTelemetryExporter.GenerateJson(dump);
                var dlg = new SaveFileDialog
                {
                    FileName = $"ZapretGUI_Telemetry_{DateTime.Now:yyyyMMdd_HHmm}.json",
                    Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*",
                    Title = "Сохранить полный снимок телеметрии"
                };
                if (dlg.ShowDialog() == true)
                {
                    File.WriteAllText(dlg.FileName, json, Encoding.UTF8);
                    _main.Home.ShowSuccess("Телеметрия сохранена в файл: " + Path.GetFileName(dlg.FileName));
                    Status = "Телеметрия сохранена в " + Path.GetFileName(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                _main.Home.ShowError("Ошибка экспорта JSON: " + ex.Message);
            }
        }

        private async Task ExportTelemetryZipAsync()
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    FileName = $"ZapretGUI_Diagnostic_Package_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                    Filter = "ZIP архивы (*.zip)|*.zip|Все файлы (*.*)|*.*",
                    Title = "Сохранить полный диагностический пакет"
                };
                if (dlg.ShowDialog() == true)
                {
                    var dump = ProviderTelemetryExporter.Collect(_main);
                    var (ok, msg, _) = await ProviderTelemetryExporter.CreateDiagnosticZipArchiveAsync(dump, dlg.FileName);
                    if (ok)
                    {
                        _main.Home.ShowSuccess("Диагностический пакет сохранён: " + Path.GetFileName(dlg.FileName));
                        Status = "Пакет сохранён: " + Path.GetFileName(dlg.FileName);
                    }
                    else
                    {
                        _main.Home.ShowError(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                _main.Home.ShowError("Ошибка создания архива: " + ex.Message);
            }
        }

        public AppSettings Settings => _main.Settings;
        public HomeViewModel Home => _main.Home;

        private ProviderContext Provider => Settings.ProviderContext ??= new ProviderContext();

        public string ProviderName
        {
            get => _providerName;
            set => Set(ref _providerName, value ?? "");
        }

        public string ProviderAsn
        {
            get => _providerAsn;
            set => Set(ref _providerAsn, value ?? "");
        }

        public string[] ProviderConfidenceOptions { get; } = { "Не указана", "Низкая", "Средняя", "Высокая" };

        public int ProviderConfidenceIndex
        {
            get => _providerConfidenceIndex;
            set => Set(ref _providerConfidenceIndex, Math.Clamp(value, 0, 3));
        }

        public string ProviderSourceText => Provider.SourceText;
        public string ProviderCheckedAtText => Provider.CheckedAt.HasValue
            ? "Проверено: " + Provider.CheckedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "Время проверки не указано";
        public string ProviderContextText => Provider.DisplayText;
        public bool HasProviderContext => Provider.IsKnown;

        public bool IsProviderLookupBusy
        {
            get => _isProviderLookupBusy;
            private set
            {
                if (Set(ref _isProviderLookupBusy, value))
                    (LookupProviderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ICommand SaveProviderContextCommand { get; }
        public ICommand ClearProviderContextCommand { get; }
        public ICommand LookupProviderCommand { get; }

        public string[] ThemeOptions { get; } = { "Как в системе", "Тёмная", "Светлая" };

        public string EnginePath
        {
            get => _enginePath;
            set
            {
                if (!Set(ref _enginePath, value)) return;
                Settings.EnginePath = value;
                SettingsStore.Save(Settings);
                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
                Raise(nameof(EngineInfoText));
                Raise(nameof(EngineReady));
            }
        }

        public bool EngineReady => EngineService.IsEngineReady(Settings.EnginePath);

        public string EngineInfoText
        {
            get
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                var strategies = _main.Strategies.Items.Count;
                var versionText = string.IsNullOrWhiteSpace(version) ? "версия неизвестна" : "версия " + version;
                return EngineService.IsEngineReady(Settings.EnginePath)
                    ? $"Движок найден: {versionText}, стратегий: {strategies}"
                    : "В этой папке нет winws.exe — движок не готов к работе";
            }
        }

        public int ZoomPercent
        {
            get => Math.Clamp(Settings.InterfaceZoomPercent, 80, 140);
            set
            {
                Settings.InterfaceZoomPercent = Math.Clamp(value, 80, 140);
                SettingsStore.Save(Settings);
                _main.Notify(nameof(MainViewModel.InterfaceZoom));
                Raise(nameof(ZoomPercent));
                Status = "Масштаб интерфейса: " + Settings.InterfaceZoomPercent + "%";
            }
        }

        public int ThemeIndex
        {
            get => _themeIndex;
            set
            {
                if (!Set(ref _themeIndex, value)) return;
                Settings.Theme = (ThemeMode)Math.Clamp(value, 0, 2);
                ThemeService.Apply(Settings.Theme);
                SettingsStore.Save(Settings);
                _main.Notify(nameof(MainViewModel.ThemeText));
                Status = "Тема применена: " + ThemeService.ModeText(Settings.Theme);
            }
        }

        public bool CloseToTray
        {
            get => Settings.CloseToTray;
            set { Settings.CloseToTray = value; OnSettingChanged(); }
        }

        public bool StartMinimized
        {
            get => Settings.StartMinimized;
            set { Settings.StartMinimized = value; OnSettingChanged(); }
        }

        public bool AutoStartBypass
        {
            get => Settings.AutoStartBypass;
            set
            {
                Settings.AutoStartBypass = Settings.SafeMode ? false : value;
                OnSettingChanged();
            }
        }

        public bool SafeMode
        {
            get => Settings.SafeMode;
            set
            {
                Settings.SafeMode = value;
                if (value) Settings.AutoStartBypass = false;
                OnSettingChanged();
                _main.RefreshReadiness();
                _main.NotifySeamlessChanged();
                if (value) _main.Watchdog.Stop(); else if (Settings.WatchdogEnabled) _main.Watchdog.Start();
            }
        }

        public bool StopBypassOnExit
        {
            get => Settings.StopBypassOnExit;
            set { Settings.StopBypassOnExit = value; OnSettingChanged(); }
        }

        public bool ShowWinwsConsole
        {
            get => Settings.ShowWinwsConsole;
            set { Settings.ShowWinwsConsole = value; OnSettingChanged(); }
        }

        public bool AutoCheckEngineUpdates
        {
            get => Settings.AutoCheckEngineUpdates;
            set { Settings.AutoCheckEngineUpdates = value; OnSettingChanged(); }
        }

        public bool IncludePrerelease
        {
            get => Settings.IncludePrerelease;
            set { Settings.IncludePrerelease = value; OnSettingChanged(); }
        }

        public bool PreserveUserDataOnUpdate
        {
            get => Settings.PreserveUserDataOnUpdate;
            set { Settings.PreserveUserDataOnUpdate = value; OnSettingChanged(); }
        }

        public bool ConfirmOnStop
        {
            get => Settings.ConfirmOnStop;
            set { Settings.ConfirmOnStop = value; OnSettingChanged(); }
        }

        public bool UseGameFilterOnStart
        {
            get => Settings.UseGameFilterOnStart;
            set { Settings.UseGameFilterOnStart = value; OnSettingChanged(); }
        }

        public bool AutoTestStrategiesOnFirstLaunch
        {
            get => Settings.AutoTestStrategiesOnFirstLaunch;
            set { Settings.AutoTestStrategiesOnFirstLaunch = value; OnSettingChanged(); }
        }

        public bool AutoDiagnoseOnFirstLaunch
        {
            get => Settings.AutoDiagnoseOnFirstLaunch;
            set { Settings.AutoDiagnoseOnFirstLaunch = value; OnSettingChanged(); }
        }

        public bool ScheduleEnabled
        {
            get => Settings.ScheduleEnabled;
            set { Settings.ScheduleEnabled = value; SettingsStore.Save(Settings); _main.NotifyScheduleChanged(); Raise(nameof(ScheduleEnabled)); Raise(nameof(ScheduleSummary)); Status = value ? "Расписание включено" : "Расписание выключено"; }
        }

        public string ScheduleStartTime
        {
            get => Settings.ScheduleStartTime;
            set { if (System.TimeSpan.TryParse(value, out _)) { Settings.ScheduleStartTime = value; SettingsStore.Save(Settings); _main.NotifyScheduleChanged(); Raise(nameof(ScheduleStartTime)); Raise(nameof(ScheduleSummary)); } }
        }

        public string ScheduleStopTime
        {
            get => Settings.ScheduleStopTime;
            set { if (System.TimeSpan.TryParse(value, out _)) { Settings.ScheduleStopTime = value; SettingsStore.Save(Settings); _main.NotifyScheduleChanged(); Raise(nameof(ScheduleStopTime)); Raise(nameof(ScheduleSummary)); } }
        }

        public int ScheduleDaysMask
        {
            get => Settings.ScheduleDaysMask;
            set { Settings.ScheduleDaysMask = value & 127; SettingsStore.Save(Settings); _main.NotifyScheduleChanged(); Raise(nameof(ScheduleDaysMask)); Raise(nameof(ScheduleSummary)); Raise(nameof(ScheduleDayMonday)); Raise(nameof(ScheduleDayTuesday)); Raise(nameof(ScheduleDayWednesday)); Raise(nameof(ScheduleDayThursday)); Raise(nameof(ScheduleDayFriday)); Raise(nameof(ScheduleDaySaturday)); Raise(nameof(ScheduleDaySunday)); }
        }

        public bool ScheduleDayMonday { get => (ScheduleDaysMask & 1) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 1) : (ScheduleDaysMask & ~1); } }
        public bool ScheduleDayTuesday { get => (ScheduleDaysMask & 2) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 2) : (ScheduleDaysMask & ~2); } }
        public bool ScheduleDayWednesday { get => (ScheduleDaysMask & 4) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 4) : (ScheduleDaysMask & ~4); } }
        public bool ScheduleDayThursday { get => (ScheduleDaysMask & 8) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 8) : (ScheduleDaysMask & ~8); } }
        public bool ScheduleDayFriday { get => (ScheduleDaysMask & 16) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 16) : (ScheduleDaysMask & ~16); } }
        public bool ScheduleDaySaturday { get => (ScheduleDaysMask & 32) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 32) : (ScheduleDaysMask & ~32); } }
        public bool ScheduleDaySunday { get => (ScheduleDaysMask & 64) != 0; set { ScheduleDaysMask = value ? (ScheduleDaysMask | 64) : (ScheduleDaysMask & ~64); } }

        public bool ScheduleUseService
        {
            get => Settings.ScheduleUseService;
            set { Settings.ScheduleUseService = value; SettingsStore.Save(Settings); Raise(nameof(ScheduleUseService)); Status = value ? "Расписание: служба" : "Расписание: процесс"; }
        }

        public string ScheduleSummary => _main.ScheduleService?.Describe() ?? (ScheduleEnabled ? $"{ScheduleStartTime} → {ScheduleStopTime}" : "выключено");

        public bool ResourceMonitoringEnabled
        {
            get => _main.Monitoring.ResourceMonitoringEnabled;
            set => _main.Monitoring.ResourceMonitoringEnabled = value;
        }

        public bool AutoRecoverStrategy
        {
            get => _main.Monitoring.AutoRecoverStrategy;
            set => _main.Monitoring.AutoRecoverStrategy = value;
        }

        public bool MonitorNotificationsEnabled
        {
            get => Settings.MonitorNotificationsEnabled;
            set { Settings.MonitorNotificationsEnabled = value; OnSettingChanged(); }
        }

        public int MonitoringIntervalMinutes
        {
            get => _main.Monitoring.MonitoringIntervalMinutes;
            set => _main.Monitoring.MonitoringIntervalMinutes = value;
        }

        public bool WatchdogEnabled
        {
            get => Settings.WatchdogEnabled;
            set
            {
                Settings.WatchdogEnabled = value;
                OnSettingChanged();
                if (value && !Settings.SafeMode) _main.Watchdog.Start();
                else _main.Watchdog.Stop();
            }
        }

        public int WatchdogIntervalSeconds
        {
            get => Settings.WatchdogIntervalSeconds;
            set { Settings.WatchdogIntervalSeconds = Math.Clamp(value, 5, 120); OnSettingChanged(); }
        }

        public bool WatchdogAutoRestart
        {
            get => Settings.WatchdogAutoRestart;
            set { Settings.WatchdogAutoRestart = value; OnSettingChanged(); }
        }

        public bool WatchdogNotifyUser
        {
            get => Settings.WatchdogNotifyUser;
            set { Settings.WatchdogNotifyUser = value; OnSettingChanged(); }
        }

        public bool DisableQuicFake
        {
            get => Settings.DisableQuicFake;
            set { Settings.DisableQuicFake = value; SettingsStore.Save(Settings); Raise(nameof(DisableQuicFake)); Status = value ? "QUIC fake отключён для теста YouTube" : "QUIC fake включён"; }
        }
        public bool PreferIPv4ForBypass
        {
            get => Settings.PreferIPv4ForBypass;
            set { Settings.PreferIPv4ForBypass = value; SettingsStore.Save(Settings); Raise(nameof(PreferIPv4ForBypass)); Status = value ? "Приоритет IPv4 включён" : "IPv6 разрешён"; }
        }
        public bool UseDohForBlockedHosts
        {
            get => Settings.UseDohForBlockedHosts;
            set { Settings.UseDohForBlockedHosts = value; SettingsStore.Save(Settings); Raise(nameof(UseDohForBlockedHosts)); Status = value ? "DoH включён для заблокированных" : "DoH выключен"; }
        }
        public string YoutubeSniOverride
        {
            get => Settings.YoutubeSniOverride;
            set { Settings.YoutubeSniOverride = (value ?? "").Trim(); SettingsStore.Save(Settings); Raise(nameof(YoutubeSniOverride)); Raise(nameof(YoutubeSniDisplay)); Status = string.IsNullOrWhiteSpace(value) ? "YouTube SNI: авто" : $"YouTube SNI: {value}"; }
        }
        public string YoutubeSniDisplay => string.IsNullOrWhiteSpace(Settings.YoutubeSniOverride) ? "Авто (как в стратегии)" : Settings.YoutubeSniOverride;
        public System.Collections.Generic.List<string> YoutubeSniOptions { get; } = new() { "", "google.com", "www.google.com", "googlevideo.com", "youtube.com", "yt3.ggpht.com", "cloudflare.com" };

        public bool RealTimePingEnabled
        {
            get => Settings.RealTimePingEnabled;
            set
            {
                Settings.RealTimePingEnabled = value;
                OnSettingChanged();
                _main.Notify(nameof(MainViewModel.RealTimePingVisible));
            }
        }

        public int RealTimePingIntervalSeconds
        {
            get => Settings.RealTimePingIntervalSeconds;
            set { Settings.RealTimePingIntervalSeconds = Math.Clamp(value, 3, 120); OnSettingChanged(); }
        }

        public bool ToolbarMetricsEnabled
        {
            get => Settings.ToolbarMetricsEnabled;
            set
            {
                Settings.ToolbarMetricsEnabled = value;
                OnSettingChanged();
                _main.RefreshToolbarMetrics();
            }
        }

        public int ToolbarMetricsIntervalSeconds
        {
            get => Settings.ToolbarMetricsIntervalSeconds;
            set
            {
                Settings.ToolbarMetricsIntervalSeconds = Math.Clamp(value, 15, 300);
                OnSettingChanged();
                _main.RefreshToolbarMetrics();
                Raise(nameof(ToolbarMetricsIntervalIndex));
                Raise(nameof(ToolbarMetricsHint));
            }
        }

        public System.Collections.Generic.List<int> ToolbarMetricsIntervalOptions { get; } = new() { 15, 30, 60, 120, 180, 300 };
        public int ToolbarMetricsIntervalIndex
        {
            get
            {
                var v = Settings.ToolbarMetricsIntervalSeconds;
                var idx = ToolbarMetricsIntervalOptions.IndexOf(v);
                if (idx >= 0) return idx;
                // ближайший
                var best = 0; var bestDiff = int.MaxValue;
                for (int i = 0; i < ToolbarMetricsIntervalOptions.Count; i++) { var d = Math.Abs(ToolbarMetricsIntervalOptions[i] - v); if (d < bestDiff) { bestDiff = d; best = i; } }
                return best;
            }
            set
            {
                if (value < 0 || value >= ToolbarMetricsIntervalOptions.Count) return;
                ToolbarMetricsIntervalSeconds = ToolbarMetricsIntervalOptions[value];
            }
        }
        public string ToolbarMetricsIntervalDisplay => $"{ToolbarMetricsIntervalSeconds} сек";

        // Выбор хостов через селект-меню (чекбоксы), а не ручной ввод
        public System.Collections.ObjectModel.ObservableCollection<MetricHostOption> ToolbarMetricsHostOptions { get; } = new();
        public void RefreshToolbarMetricsHosts()
        {
            try
            {
                if (_main.Monitoring == null || _main.Monitoring.Targets == null) return;
                var targets = _main.Monitoring.Targets.ToList();
                var selected = Settings.ToolbarMetricsVisibleTargets;
                var allSelected = selected.Count == 0;
                ToolbarMetricsHostOptions.Clear();
                foreach (var tgt in targets)
                {
                    var isSel = allSelected || selected.Any(s => s.Equals(tgt.Name, StringComparison.OrdinalIgnoreCase));
                    ToolbarMetricsHostOptions.Add(new MetricHostOption(this, tgt.Name, tgt.Host, isSel));
                }
                // Если нет целей — добавить заглушку
                if (ToolbarMetricsHostOptions.Count == 0)
                    ToolbarMetricsHostOptions.Add(new MetricHostOption(this, "Нет ресурсов", "", false) { IsEnabled = false });
                Raise(nameof(ToolbarMetricsHostOptions));
                Raise(nameof(ToolbarMetricsHint));
                Raise(nameof(ToolbarMetricsVisibleTargetsText));
            }
            catch {}
        }
        public void UpdateToolbarMetricsHostsFromSelection()
        {
            try
            {
                var all = ToolbarMetricsHostOptions.Where(h => h.IsEnabled).ToList();
                var sel = all.Where(h => h.IsSelected).Select(h => h.Name).ToList();
                // Если выбраны все — храним пусто (значение Все)
                if (sel.Count == all.Count) sel.Clear();
                Settings.ToolbarMetricsVisibleTargets = sel;
                SettingsStore.Save(Settings);
                Raise(nameof(ToolbarMetricsVisibleTargetsText));
                Raise(nameof(ToolbarMetricsHint));
                _main.RefreshToolbarMetrics();
                Status = sel.Count == 0 ? "Метрики: показаны все ресурсы" : $"Метрики: {string.Join(", ", sel)}";
            }
            catch {}
        }
        public string ToolbarMetricsVisibleTargetsText
        {
            get => Settings.ToolbarMetricsVisibleTargets.Count == 0 ? "Все" : string.Join(", ", Settings.ToolbarMetricsVisibleTargets);
            set
            {
                var list = (value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Settings.ToolbarMetricsVisibleTargets = list;
                OnSettingChanged();
                _main.RefreshToolbarMetrics();
                RefreshToolbarMetricsHosts();
            }
        }

        public string ToolbarMetricsHint => ToolbarMetricsEnabled
            ? $"Панель задач: {ToolbarMetricsVisibleTargetsText} • каждые {ToolbarMetricsIntervalSeconds} сек как в MSI Afterburner"
            : "Метрики на панели задач отключены — окно над треем скрыто";

        public int StartupDelaySeconds
        {
            get => Settings.StartupDelaySeconds;
            set { Settings.StartupDelaySeconds = Math.Clamp(value, 0, 60); OnSettingChanged(); }
        }

        /// <summary>Автозапуск приложения: планировщик задач (нужны права администратора).</summary>
        public bool RunAtStartup
        {
            get => Settings.RunAtStartup;
            set
            {
                Settings.RunAtStartup = value;
                OnSettingChanged();
                ApplyAutostart(value);
            }
        }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public bool GameDetectionEnabled
        {
            get => Settings.GameDetectionEnabled;
            set
            {
                Settings.GameDetectionEnabled = value;
                OnSettingChanged();
                if (value) _main.GameDetector.Start();
                else _main.GameDetector.Stop();
            }
        }

        public bool AutoGameModeOnLaunch
        {
            get => Settings.AutoGameModeOnLaunch;
            set { Settings.AutoGameModeOnLaunch = value; OnSettingChanged(); }
        }

        public bool GlobalHotkeysEnabled
        {
            get => Settings.GlobalHotkeysEnabled;
            set { Settings.GlobalHotkeysEnabled = value; OnSettingChanged(); }
        }

        public string HotkeyToggleBypass
        {
            get => Settings.HotkeyToggleBypass;
            set { Settings.HotkeyToggleBypass = value ?? "Ctrl+Shift+Z"; OnSettingChanged(); }
        }

        public string HotkeyToggleGameMode
        {
            get => Settings.HotkeyToggleGameMode;
            set { Settings.HotkeyToggleGameMode = value ?? "Ctrl+Shift+G"; OnSettingChanged(); }
        }

        public string HotkeyToggleMiniOverlay
        {
            get => Settings.HotkeyToggleMiniOverlay;
            set { Settings.HotkeyToggleMiniOverlay = value ?? "Ctrl+Shift+O"; OnSettingChanged(); }
        }

        public bool MiniOverlayTopmost
        {
            get => Settings.MiniOverlayTopmost;
            set
            {
                Settings.MiniOverlayTopmost = value;
                OnSettingChanged();
                _main.MiniOverlay.Refresh();
            }
        }

        public int MiniOverlayOpacity
        {
            get => Math.Clamp(Settings.MiniOverlayOpacity, 50, 100);
            set
            {
                Settings.MiniOverlayOpacity = Math.Clamp(value, 50, 100);
                OnSettingChanged();
                _main.MiniOverlay.Refresh();
                Raise(nameof(MiniOverlayOpacity));
            }
        }

        private string _gamingOptimizationStatus = "";
        private bool _gamingIsOptimized;
        public string GamingOptimizationStatusText
        {
            get => _gamingOptimizationStatus;
            private set => Set(ref _gamingOptimizationStatus, value);
        }

        public bool GamingIsOptimized
        {
            get => _gamingIsOptimized;
            private set => Set(ref _gamingIsOptimized, value);
        }

        public ICommand ApplyGamingTweaksCommand { get; }
        public ICommand RevertGamingTweaksCommand { get; }
        public ICommand OpenOverlayCommand { get; }
        public ICommand OpenLogsCommand { get; }
        public ICommand OpenUpdatesCommand { get; }
        public ICommand OpenAboutCommand { get; }

        public void RefreshGamingOptimization()
        {
            try
            {
                var opt = GamingNetworkOptimizer.CheckStatus();
                GamingOptimizationStatusText = opt.Summary;
                GamingIsOptimized = opt.IsOptimized;
            }
            catch
            {
                GamingOptimizationStatusText = "Параметры сети по умолчанию";
                GamingIsOptimized = false;
            }
        }

        private async Task ApplyGamingTweaksAsync()
        {
            if (!Shell.IsAdmin())
            {
                Status = "Для изменения сетевых параметров Windows требуются права администратора";
                return;
            }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Оптимизация сети", "Применение игровых твиков Windows…", "TCP/UDP параметры…", 0, true, false)); } catch {}

            var (ok, msg) = await GamingNetworkOptimizer.ApplyTweaksAsync();
            Status = msg;
            RefreshGamingOptimization();
            if (!ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Оптимизация — ошибка", msg)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private async Task RevertGamingTweaksAsync()
        {
            if (!Shell.IsAdmin())
            {
                Status = "Для изменения сетевых параметров Windows требуются права администратора";
                return;
            }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Сброс оптимизации", "Откат игровых твиков Windows…", "Восстановление настроек…", 0, true, false)); } catch {}

            var (ok, msg) = await GamingNetworkOptimizer.RevertTweaksAsync();
            Status = msg;
            RefreshGamingOptimization();
            if (!ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Откат — ошибка", msg)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        public string LastBackupText { get; }

        public string SettingsFilePath => AppPaths.SettingsFile;
        public string LogsPath => AppPaths.LogDir;
        public string BackupPath => AppPaths.BackupDir;

        public ICommand BrowseEnginePathCommand { get; }
        public ICommand DetectEngineCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand OpenSettingsFileCommand { get; }
        public ICommand OpenLogsFolderCommand { get; }
        public ICommand OpenBackupFolderCommand { get; }
        public ICommand ResetSettingsCommand { get; }
        public ICommand ValidateEngineCommand { get; }
        public ICommand OpenRepoCommand { get; }
        public ICommand OpenHostsCommand { get; }
        public ICommand RerunFirstLaunchWizardCommand { get; }

        public void Reload()
        {
            _enginePath = Settings.EnginePath;
            Raise(nameof(EnginePath));
            _themeIndex = (int)Settings.Theme;
            SyncProviderDraft();
            Raise(nameof(ThemeIndex));
            Raise(nameof(ZoomPercent));
            RaiseProviderContext();
            Raise(nameof(EngineInfoText));
            Raise(nameof(EngineReady));
        }

        private void OnSettingChanged()
        {
            SettingsStore.Save(Settings);
            Status = "Настройки сохранены";
            Raise(nameof(CloseToTray));
            Raise(nameof(StartMinimized));
            Raise(nameof(AutoStartBypass));
            Raise(nameof(SafeMode));
            Raise(nameof(StopBypassOnExit));
            Raise(nameof(ShowWinwsConsole));
            Raise(nameof(AutoCheckEngineUpdates));
            Raise(nameof(IncludePrerelease));
            Raise(nameof(PreserveUserDataOnUpdate));
            Raise(nameof(ConfirmOnStop));
            Raise(nameof(UseGameFilterOnStart));
            Raise(nameof(AutoTestStrategiesOnFirstLaunch));
            Raise(nameof(AutoDiagnoseOnFirstLaunch));
            Raise(nameof(ResourceMonitoringEnabled));
            Raise(nameof(AutoRecoverStrategy));
            Raise(nameof(MonitorNotificationsEnabled));
            Raise(nameof(MonitoringIntervalMinutes));
            Raise(nameof(WatchdogEnabled));
            Raise(nameof(WatchdogIntervalSeconds));
            Raise(nameof(WatchdogAutoRestart));
            Raise(nameof(WatchdogNotifyUser));
            Raise(nameof(RealTimePingEnabled));
            Raise(nameof(DisableQuicFake));
            Raise(nameof(PreferIPv4ForBypass));
            Raise(nameof(UseDohForBlockedHosts));
            Raise(nameof(YoutubeSniOverride));
            Raise(nameof(YoutubeSniDisplay));
            Raise(nameof(RealTimePingIntervalSeconds));
            Raise(nameof(ToolbarMetricsIntervalSeconds));
            Raise(nameof(ToolbarMetricsIntervalIndex));
            Raise(nameof(ToolbarMetricsIntervalDisplay));
            Raise(nameof(ToolbarMetricsVisibleTargetsText));
            Raise(nameof(ToolbarMetricsHint));
            Raise(nameof(ToolbarMetricsHostOptions));
            Raise(nameof(StartupDelaySeconds));
            Raise(nameof(RunAtStartup));
            Raise(nameof(ProviderName));
            Raise(nameof(ProviderAsn));
            Raise(nameof(ProviderConfidenceIndex));
            Raise(nameof(ProviderSourceText));
            Raise(nameof(ProviderCheckedAtText));
            Raise(nameof(ProviderContextText));
            Raise(nameof(HasProviderContext));
            Raise(nameof(ScheduleEnabled));
            Raise(nameof(ScheduleStartTime));
            Raise(nameof(ScheduleStopTime));
            Raise(nameof(ScheduleDaysMask));
            Raise(nameof(ScheduleSummary));
        }

        private void SaveProviderContext()
        {
            var name = _providerName.Trim();
            var asn = _providerAsn.Trim();
            if (name.Length > 120)
            {
                Status = "Название провайдера слишком длинное";
                return;
            }

            if (asn.Length > 0 && !Regex.IsMatch(asn, @"^(?:AS)?\d{1,10}$", RegexOptions.IgnoreCase))
            {
                Status = "ASN должен иметь вид AS12345 или 12345";
                return;
            }

            if (asn.Length > 0 && !asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                asn = "AS" + asn;

            var known = name.Length > 0 || asn.Length > 0;
            Settings.ProviderContext = new ProviderContext
            {
                Name = name,
                Asn = asn.ToUpperInvariant(),
                Source = known ? ProviderContextSource.UserInput : ProviderContextSource.Unknown,
                CheckedAt = known ? DateTime.UtcNow : null,
                Confidence = known ? ProviderConfidenceValue(_providerConfidenceIndex) : 0
            };
            SyncProviderDraft();
            SettingsStore.Save(Settings);
            Status = known ? "Контекст провайдера сохранён" : "Провайдерский контекст очищен";
            RaiseProviderContext();
        }

        private async Task LookupProviderAsync()
        {
            var answer = System.Windows.MessageBox.Show(
                "Разрешить запрос к внешнему сервису ipapi.co? Сервис увидит ваш внешний IP и может определить ASN/организацию. Полученные данные будут сохранены с пометкой «внешний источник».",
                "Определение провайдера через внешний сервис", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            IsProviderLookupBusy = true;
            Status = "Запрашиваю ASN через внешний сервис…";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretGUI/1.2");
                using var response = await client.GetAsync("https://ipapi.co/json/").ConfigureAwait(true);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                var root = document.RootElement;
                var name = ReadJsonString(root, "org");
                if (string.IsNullOrWhiteSpace(name)) name = ReadJsonString(root, "isp");
                var asn = ReadJsonString(root, "asn").ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(asn) && !asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                    asn = "AS" + asn;

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(asn))
                {
                    Status = "Внешний сервис не вернул ASN или название организации";
                    return;
                }

                Settings.ProviderContext = new ProviderContext
                {
                    Name = name.Trim(),
                    Asn = asn.Trim(),
                    Source = ProviderContextSource.ExternalService,
                    CheckedAt = DateTime.UtcNow,
                    Confidence = 70
                };
                SyncProviderDraft();
                SettingsStore.Save(Settings);
                Status = "Провайдерский контекст получен из внешнего источника и сохранён";
                RaiseProviderContext();
            }
            catch (Exception ex)
            {
                Status = "Не удалось получить провайдерский контекст: " + ex.Message;
            }
            finally
            {
                IsProviderLookupBusy = false;
            }
        }

        private static string ReadJsonString(JsonElement root, string property)
            => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private void ClearProviderContext()
        {
            Settings.ProviderContext = new ProviderContext();
            SyncProviderDraft();
            SettingsStore.Save(Settings);
            Status = "Провайдерский контекст очищен";
            RaiseProviderContext();
        }

        private void SyncProviderDraft()
        {
            var context = Provider;
            _providerName = context.Name;
            _providerAsn = context.Asn;
            _providerConfidenceIndex = context.Confidence switch { >= 90 => 3, >= 60 => 2, > 0 => 1, _ => 0 };
        }

        private static int ProviderConfidenceValue(int index)
            => index switch { 3 => 95, 2 => 70, 1 => 35, _ => 0 };

        private void RaiseProviderContext()
        {
            Raise(nameof(ProviderName));
            Raise(nameof(ProviderAsn));
            Raise(nameof(ProviderConfidenceIndex));
            Raise(nameof(ProviderSourceText));
            Raise(nameof(ProviderCheckedAtText));
            Raise(nameof(ProviderContextText));
            Raise(nameof(HasProviderContext));
            Raise(nameof(ScheduleEnabled));
            Raise(nameof(ScheduleStartTime));
            Raise(nameof(ScheduleStopTime));
            Raise(nameof(ScheduleDaysMask));
            Raise(nameof(ScheduleSummary));
        }

        private void BrowseEnginePath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку с движком zapret (там, где лежат bin и lists)",
                InitialDirectory = Directory.Exists(Settings.EnginePath) ? Settings.EnginePath : AppPaths.DefaultEngine
            };

            if (dialog.ShowDialog() != true) return;

            EnginePath = dialog.FolderName;
            Status = EngineReady ? "Движок найден" : "В выбранной папке нет bin\\winws.exe";
        }

        /// <summary>Ищет уже распакованную сборку zapret рядом с приложением и в стандартных местах.</summary>
        private void DetectEngine()
        {
            var candidates = new List<string>
            {
                AppPaths.DefaultEngine,
                Path.Combine(AppContext.BaseDirectory, "engine"),
                AppContext.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };

            try
            {
                var downloads = candidates[^1];
                if (Directory.Exists(downloads))
                {
                    candidates.AddRange(Directory.GetDirectories(downloads, "zapret*")
                        .OrderByDescending(Directory.GetLastWriteTime));
                }
            }
            catch { }

            var found = candidates.FirstOrDefault(EngineService.IsEngineReady);
            if (found != null)
            {
                EnginePath = found;
                Status = "Найден движок: " + found;
                return;
            }

            // Пробуем поискать вложенные папки вида zapret-discord-youtube-1.10.2
            foreach (var root in candidates.Where(Directory.Exists))
            {
                try
                {
                    var nested = Directory.GetDirectories(root, "zapret*").FirstOrDefault(EngineService.IsEngineReady);
                    if (nested != null)
                    {
                        EnginePath = nested;
                        Status = "Найден движок: " + nested;
                        return;
                    }
                }
                catch { }
            }

            Status = "Готовый движок не найден — скачайте его на странице «Обновления»";
        }

        private void ValidateEngine()
        {
            if (EngineService.IsEngineReady(Settings.EnginePath))
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                Status = $"Движок готов к работе (версия {version})";
            }
            else
            {
                Status = "Не найдены bin\\winws.exe или WinDivert64.sys";
            }
            Raise(nameof(EngineInfoText));
            Raise(nameof(EngineReady));
        }

        private void ResetSettings()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Сбросить все настройки приложения к значениям по умолчанию?",
                "Сброс настроек", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var fresh = new AppSettings();
            _main.Settings.EnginePath = fresh.EnginePath;
            _main.Settings.Theme = fresh.Theme;
            _main.Settings.InterfaceZoomPercent = 100;
            _main.Settings.CloseToTray = false;
            _main.Settings.StartMinimized = false;
            _main.Settings.AutoStartBypass = false;
            _main.Settings.SafeMode = false;
            _main.Settings.FirstLaunchWizardCompleted = false;
            _main.Settings.StopBypassOnExit = false;
            _main.Settings.ShowWinwsConsole = false;
            _main.Settings.AutoCheckEngineUpdates = true;
            _main.Settings.IncludePrerelease = false;
            _main.Settings.PreserveUserDataOnUpdate = true;
            _main.Settings.ConfirmOnStop = false;
            _main.Settings.AutoTestStrategiesOnFirstLaunch = true;
            _main.Settings.AutoDiagnoseOnFirstLaunch = true;
            _main.Settings.StrategyTestsCompleted = false;
            _main.Settings.FirstLaunchDiagnosticsCompleted = false;
            _main.Settings.PreviousSelectedStrategy = "";
            _main.Settings.ResourceMonitoringEnabled = false;
            _main.Settings.AutoRecoverStrategy = true;
            _main.Settings.MonitorNotificationsEnabled = true;
            _main.Settings.ResourceMonitoringIntervalMinutes = 15;
            _main.Settings.MonitorTargets.Clear();
            _main.Settings.ProviderContext = new ProviderContext();

            SettingsStore.Save(_main.Settings);
            _main.Monitoring.Reload();
            Reload();
            ThemeService.Apply(_main.Settings.Theme);
            Status = "Настройки сброшены";
        }

        private void RerunFirstLaunchWizard()
        {
            var answer = System.Windows.MessageBox.Show(
                "Запустить мастер первого запуска? Вы сможете снова пройти все шаги настройки по порядку.",
                "Мастер настройки", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            _main.Settings.FirstLaunchWizardCompleted = false;
            SettingsStore.Save(_main.Settings);
            _main.FirstLaunch.Reset();
            _main.Navigate("first-run");
        }

        private void ApplyAutostart(bool enabled)
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exe)) return;

                if (enabled)
                {
                    var result = Shell.Run("schtasks", new[]
                    {
                        "/create", "/tn", "ZapretGUI", "/tr", "\"" + exe + "\"",
                        "/sc", "onlogon", "/rl", "highest", "/f"
                    }, 20000);

                    Status = result.Ok
                        ? "Автозапуск включён (планировщик задач: ZapretGUI)"
                        : "Не удалось включить автозапуск: " + result.All;
                }
                else
                {
                    Shell.Run("schtasks", new[] { "/delete", "/tn", "ZapretGUI", "/f" }, 20000);
                    Status = "Автозапуск отключён";
                }
            }
            catch (Exception ex)
            {
                Status = "Ошибка настройки автозапуска: " + ex.Message;
            }
        }

        private static string GetBackupText()
        {
            try
            {
                var backups = Directory.GetFiles(AppPaths.BackupDir)
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(1)
                    .ToList();
                return backups.Count == 0
                    ? "Резервных копий пока нет"
                    : "Последняя копия: " + Path.GetFileName(backups[0]);
            }
            catch { return "Резервных копий пока нет"; }
        }
    }
    public sealed class MetricHostOption : ObservableObject
    {
        private readonly SettingsViewModel _parent;
        private bool _isSelected;
        public MetricHostOption(SettingsViewModel parent, string name, string host, bool isSelected)
        {
            _parent = parent;
            Name = name;
            Host = host;
            _isSelected = isSelected;
        }
        public string Name { get; }
        public string Host { get; }
        public bool IsEnabled { get; set; } = true;
        public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value) && IsEnabled) _parent.UpdateToolbarMetricsHostsFromSelection(); } }
        public string DisplayText => string.IsNullOrWhiteSpace(Host) ? Name : $"{Name} ({Host})";
    }

}
