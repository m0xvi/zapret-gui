using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class NavItem
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string Icon { get; init; } = "";
        public string Hint { get; init; } = "";
        public bool IsSectionHeader { get; init; }
    }

    /// <summary>Главная модель: держит настройки, контроллер обхода и все подстраницы.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly DispatcherTimer _timer;
        private NavItem _selectedNav;
        private bool _isAdmin;
        private DateTime _lastPingProbeTime = DateTime.MinValue;
        private bool _isPingProbing;

        public event Action<string>? WatchdogNotificationRequested;
        public event Action? RequestToggleOverlay;

        public WatchdogService Watchdog { get; }
        public ProfileAutoSwitchService ProfileAutoSwitch { get; }
        public BypassScheduleService ScheduleService { get; }
        public RealTimePingSnapshot? RealTimePing { get; private set; }
        public GameDetectionService GameDetector { get; }
        public GlobalHotkeyService Hotkeys { get; }
        public MiniOverlayViewModel MiniOverlay { get; }

        public bool IsGameRunning => GameDetector.IsGameRunning;
        public string? ActiveGameName => GameDetector.ActiveGameName;
        public string GameStatusBadgeText => IsGameRunning ? $"🎮 {ActiveGameName}" : "🎮 Игры: не обнаружены";
        public string GameModeSummaryText => Settings.GameModeActive ? "Игровой режим (ВКЛ)" : "Обычный режим";

        public MainViewModel(AppSettings settings)
        {
            Settings = settings;
            Bypass = new BypassController(settings);
            Strategies = new StrategyStore(settings);

            Home = new HomeViewModel(this);
            StrategiesPage = new StrategiesViewModel(this);
            Updates = new UpdatesViewModel(this);
            SettingsPage = new SettingsViewModel(this);
            Diagnostics = new DiagnosticsViewModel(this);
            DeepCheck = new DeepCheckViewModel(this);
            UserLists = new UserListsViewModel(this);
            Profiles = new ProfilesViewModel(this);
            FirstLaunch = new FirstLaunchViewModel(this);
            Logs = new LogsViewModel();
            Monitoring = new MonitoringViewModel(this);

            GameDetector = new GameDetectionService(settings);
            Hotkeys = new GlobalHotkeyService(settings);
            MiniOverlay = new MiniOverlayViewModel(this);

            GameDetector.GameStatusChanged += (isRunning, gameName) =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Raise(nameof(IsGameRunning));
                    Raise(nameof(ActiveGameName));
                    Raise(nameof(GameStatusBadgeText));
                    Home.RefreshGamingStatus();
                    MiniOverlay.Refresh();

                    if (isRunning && Settings.AutoGameModeOnLaunch && !Settings.GameModeActive)
                    {
                        AppLog.Info($"[GameMode] Автоматическая активация игрового режима для «{gameName}»");
                        Settings.GameModeActive = true;
                        SettingsStore.Save(Settings);
                        ApplyGameFilterState();
                        WatchdogNotificationRequested?.Invoke($"🎮 Запущена игра «{gameName}». Игровой режим активирован (UDP исключён).");
                    }
                });
            };

            if (settings.GameDetectionEnabled && !settings.SafeMode)
            {
                GameDetector.Start();
            }

            Hotkeys.ToggleBypassRequested += () => Application.Current?.Dispatcher?.Invoke(async () =>
            {
                await Home.ToggleBypassAsync();
                MiniOverlay.Refresh();
            });

            Hotkeys.ToggleGameModeRequested += () => Application.Current?.Dispatcher?.Invoke(() =>
            {
                ToggleGameMode();
                WatchdogNotificationRequested?.Invoke(Settings.GameModeActive
                    ? "🎮 Игровой режим включён"
                    : "🎮 Игровой режим выключен");
            });

            Hotkeys.ToggleMiniOverlayRequested += () => Application.Current?.Dispatcher?.Invoke(() =>
            {
                ToggleMiniOverlay();
            });

            Watchdog = new WatchdogService(settings, Bypass, () => Strategies.Find(settings.SelectedStrategy) ?? Strategies.Recommended);
            Watchdog.EventLogged += msg =>
            {
                if (Settings.WatchdogNotifyUser)
                    WatchdogNotificationRequested?.Invoke(msg);
            };
            Watchdog.AlertRaised += alert =>
            {
                WatchdogNotificationRequested?.Invoke(alert);
            };
            if (settings.WatchdogEnabled && !settings.SafeMode)
            {
                Watchdog.Start();
            }

            ProfileAutoSwitch = new ProfileAutoSwitchService(settings, () => Bypass, () => Strategies);
            ProfileAutoSwitch.StatusChanged += msg => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Profiles.RefreshNetwork();
                Raise(nameof(AutoSwitchNetworkStatus));
            });
            ScheduleService = new BypassScheduleService(settings, () => Bypass, () => Strategies);
            ScheduleService.StatusChanged += msg => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Home.RefreshStatus();
                WatchdogNotificationRequested?.Invoke(msg);
                Raise(nameof(ScheduleSummaryText));
            });
            if (settings.ScheduleEnabled && !settings.SafeMode) ScheduleService.Start();
            ProfileAutoSwitch.ProfileSwitched += (profile, identity) => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Home.RefreshStatus();
                Profiles.Reload();
                Profiles.RefreshNetwork();
                Raise(nameof(ActiveStrategySummaryText));
                WatchdogNotificationRequested?.Invoke($"📶 Автопрофиль «{profile.Name}» применён для сети «{identity.DisplayName}»");
            });
            if ((settings.AutoSwitchProfileOnNetworkChange || settings.AutoSwitchProfileOnFailure) && !settings.SafeMode)
            {
                ProfileAutoSwitch.Start();
            }

            NavItems = new ObservableCollection<NavItem>
            {
                new() { Key = "group-main", Title = "ОСНОВНОЕ", IsSectionHeader = true },
                new() { Key = "home", Title = "Обзор", Icon = "\uE80F", Hint = "Состояние обхода" },
                new() { Key = "strategies", Title = "Стратегии", Icon = "\uE71D", Hint = "Выбор и тестирование стратегий" },
                new() { Key = "group-checks", Title = "ПРОВЕРКИ", IsSectionHeader = true },
                new() { Key = "diagnostics", Title = "Проверка", Icon = "\uE90F", Hint = "Экспресс, DPI, Deep Check и результаты" },
                new() { Key = "group-data", Title = "СПИСКИ И ФИЛЬТРЫ", IsSectionHeader = true },
                new() { Key = "user-lists", Title = "Списки", Icon = "\uE8FD", Hint = "Домены, ipset и игровой фильтр" },
                new() { Key = "profiles", Title = "Профили", Icon = "\uE753", Hint = "Пресеты настроек и полные бэкапы" },
                new() { Key = "group-system", Title = "СИСТЕМА", IsSectionHeader = true },
                new() { Key = "updates", Title = "Обновления", Icon = "\uE895", Hint = "Движок, hosts, ipset и GUI" },
                new() { Key = "logs", Title = "Журнал", Icon = "\uE7C3", Hint = "События и отладка" },
                new() { Key = "settings", Title = "Настройки", Icon = "\uE713", Hint = "Конфигурация приложения" },
                new() { Key = "about", Title = "О программе", Icon = "\uE946", Hint = "Версия и лицензия" }
            };
            _selectedNav = NavItems[1];

            _isAdmin = Shell.IsAdmin();

            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            NavigateHomeCommand = new RelayCommand(() => Navigate("home"));
            NavigateDiagnosticsCommand = new RelayCommand(() => { Diagnostics.SelectedSubTab = 3; Navigate("diagnostics"); });
            NavigateStrategiesCommand = new RelayCommand(() => Navigate("strategies"));
            NavigateMonitoringCommand = new RelayCommand(() => Navigate("monitoring"));
            NavigateActiveCheckCommand = new RelayCommand(NavigateToActiveCheck);

            Strategies.Refresh();
            Home.ReloadFromEngine();
            ThemeService.Changed += RefreshThemeBindings;

            _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, OnTick, Application.Current.Dispatcher);
            _timer.Start();
        }

        public AppSettings Settings { get; }
        public BypassController Bypass { get; }
        public StrategyStore Strategies { get; }

        public HomeViewModel Home { get; }
        public StrategiesViewModel StrategiesPage { get; }
        public UpdatesViewModel Updates { get; }
        public SettingsViewModel SettingsPage { get; }
        public DiagnosticsViewModel Diagnostics { get; }
        public DeepCheckViewModel DeepCheck { get; }
        public UserListsViewModel UserLists { get; }
        public ProfilesViewModel Profiles { get; }
        public FirstLaunchViewModel FirstLaunch { get; }
        public LogsViewModel Logs { get; }
        public MonitoringViewModel Monitoring { get; }

        public ObservableCollection<NavItem> NavItems { get; }

        public NavItem SelectedNav
        {
            get => _selectedNav;
            set
            {
                if (Set(ref _selectedNav, value))
                {
                    Raise(nameof(SelectedNavKey));
                    NavChanged?.Invoke(value?.Key ?? "home");
                }
            }
        }

        public string SelectedNavKey => _selectedNav?.Key ?? "home";

        public event Action<string>? NavChanged;

        public bool IsAdmin
        {
            get => _isAdmin;
            private set
            {
                if (Set(ref _isAdmin, value)) Raise(nameof(AdminWarningVisible));
            }
        }

        public bool AdminWarningVisible => !IsAdmin;

        public string AdminWarningText => IsAdmin
            ? "Права администратора подтверждены"
            : "Приложение запущено без прав администратора";

        public string AdminWarningDetails => IsAdmin
            ? "Доступны запуск обхода, службы, WinDivert, обновление движка и изменение hosts."
            : "Чтение настроек и диагностика доступны, но запуск обхода, службы и системные исправления потребуют перезапуска от администратора.";

        public bool SafeMode => Settings.SafeMode;
        public string SafeModeText => SafeMode ? "Безопасный режим включён" : "Автоматические действия разрешены";

        private ReadinessSnapshot CurrentReadiness => ReadinessEvaluator.Evaluate(
            Settings, IsAdmin, EngineService.IsEngineReady(Settings.EnginePath), Strategies.Items.Count);

        public bool ReadinessIsReady => CurrentReadiness.IsReady;
        public string ReadinessText => CurrentReadiness.Status;
        public string ReadinessKey => CurrentReadiness.Key;
        public string ReadinessDetails => CurrentReadiness.Details;

        public string AppVersion => GuiUpdateService.CurrentVersion;

        public string EngineVersionText
        {
            get
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                if (!string.IsNullOrWhiteSpace(version)) return version;
                if (!string.IsNullOrWhiteSpace(Settings.EngineVersion)) return Settings.EngineVersion;
                return EngineService.IsEngineReady(Settings.EnginePath) ? "установлен" : "не установлен";
            }
        }

        public string ThemeText => ThemeService.ModeText(Settings.Theme);

        public double InterfaceZoom => Math.Clamp(Settings.InterfaceZoomPercent, 80, 140) / 100d;

        public string StatusPillText => Home.StatusText;

        public string StatusPillKey => Home.StatusKey;

        public string ActiveStrategySummaryText
        {
            get
            {
                var running = Home.RunningStrategyName;
                if (Home.IsRunning && !string.IsNullOrWhiteSpace(running))
                    return running;
                if (!string.IsNullOrWhiteSpace(Settings.SelectedStrategy))
                    return Settings.SelectedStrategy;
                return Strategies.Recommended?.Name ?? "не выбрана";
            }
        }

        public string ActiveStrategyKey => Home.IsRunning ? "Success" : "Info";

        public string ActiveStrategyTooltipText => Home.IsRunning
            ? $"Активная запущенная стратегия: «{ActiveStrategySummaryText}»\nНажмите для перехода к выбору стратегий"
            : $"Выбранная стратегия (обход выключен): «{ActiveStrategySummaryText}»\nНажмите для перехода к выбору стратегий";

        public string MonitoringSummaryText
        {
            get
            {
                if (Monitoring.Results.Count > 0)
                {
                    var ok = Monitoring.Results.Count(r => r.Ok);
                    var total = Monitoring.Results.Count;
                    return $"Узлы: {ok}/{total} OK";
                }
                var targets = Monitoring.Targets.Count(t => t.Enabled);
                return targets > 0 ? $"Узлы: {targets} в списке" : "Узлы: выкл";
            }
        }

        public string MonitoringSummaryKey
        {
            get
            {
                if (Monitoring.Results.Count == 0) return "Info";
                var ok = Monitoring.Results.Count(r => r.Ok);
                var total = Monitoring.Results.Count;
                if (ok == total) return "Success";
                if (ok > 0) return "Warning";
                return "Danger";
            }
        }

        public string MonitoringSummaryTooltip
        {
            get
            {
                if (Monitoring.Results.Count == 0)
                    return "Мониторинг ключевых ресурсов (YouTube, Discord и др.). Нажмите для перехода к экспресс-проверке.";
                var details = string.Join("\n", Monitoring.Results.Select(r => $"• {r.Target.Name}: {(r.Ok ? $"доступен ({r.Milliseconds} мс)" : "недоступен")}"));
                return $"Результаты проверки ресурсов:\n{details}\n\nНажмите для перехода к мониторингу.";
            }
        }

        public bool RealTimePingVisible => Settings.RealTimePingEnabled;
        public string RealTimePingSummaryText => RealTimePing?.SummaryText ?? "RTT: проверка…";
        public string RealTimePingStatusKey => RealTimePing?.StatusKey ?? "Muted";
        public string RealTimePingTooltip => RealTimePing?.TooltipText ?? "Живой мониторинг сетевой задержки (RTT)…";

        public string AutoSwitchNetworkStatus => ProfileAutoSwitch?.CurrentIdentity?.DisplayName ?? "Сеть не определена";
        public string AutoSwitchLastReason => ProfileAutoSwitch?.LastReason ?? "";
        public string ScheduleSummaryText => ScheduleService?.Describe() ?? "расписание выключено";
        public void NotifyScheduleChanged()
        {
            if (Settings.ScheduleEnabled && !Settings.SafeMode) ScheduleService?.Restart(); else ScheduleService?.Stop();
            Raise(nameof(ScheduleSummaryText));
        }

        public ICommand ToggleThemeCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand NavigateHomeCommand { get; }
        public ICommand NavigateDiagnosticsCommand { get; }
        public ICommand NavigateStrategiesCommand { get; }
        public ICommand NavigateMonitoringCommand { get; }
        public ICommand NavigateActiveCheckCommand { get; }

        public bool IsAnyCheckRunning =>
            Diagnostics.IsDpiRunning ||
            DeepCheck.IsRunning ||
            StrategiesPage.IsTestingAll ||
            StrategiesPage.IsEvaluatingCandidates ||
            Monitoring.IsBusy ||
            Diagnostics.IsRunning;

        public string ActiveCheckStatusText
        {
            get
            {
                if (Diagnostics.IsDpiRunning)
                    return string.IsNullOrWhiteSpace(Diagnostics.DpiProgressPercentText) ? "Проверка DPI…" : $"DPI: {Diagnostics.DpiProgressPercentText}";
                if (DeepCheck.IsRunning)
                    return string.IsNullOrWhiteSpace(DeepCheck.ProgressPercentText) ? "Deep Check…" : $"Deep Check: {DeepCheck.ProgressPercentText}";
                if (StrategiesPage.IsTestingAll)
                    return string.IsNullOrWhiteSpace(StrategiesPage.TestProgressPercentText) ? "Тест стратегий…" : $"Тест: {StrategiesPage.TestProgressPercentText}";
                if (StrategiesPage.IsEvaluatingCandidates)
                    return string.IsNullOrWhiteSpace(StrategiesPage.CandidateEvaluationProgressPercentText) ? "Автоконструктор…" : $"Кандидаты: {StrategiesPage.CandidateEvaluationProgressPercentText}";
                if (Diagnostics.IsRunning)
                    return string.IsNullOrWhiteSpace(Diagnostics.ProgressPercentText) ? "Аудит системы…" : $"Аудит: {Diagnostics.ProgressPercentText}";
                if (Monitoring.IsBusy)
                    return string.IsNullOrWhiteSpace(Monitoring.ProgressPercentText) ? "Мониторинг…" : $"Мониторинг: {Monitoring.ProgressPercentText}";
                return "Идёт проверка…";
            }
        }

        private void NavigateToActiveCheck()
        {
            if (Diagnostics.IsDpiRunning)
            {
                Diagnostics.SelectedSubTab = 1;
                Navigate("diagnostics");
            }
            else if (DeepCheck.IsRunning)
            {
                Diagnostics.SelectedSubTab = 2;
                Navigate("diagnostics");
            }
            else if (StrategiesPage.IsTestingAll || StrategiesPage.IsEvaluatingCandidates)
            {
                Navigate("strategies");
            }
            else if (Diagnostics.IsRunning)
            {
                Diagnostics.SelectedSubTab = 3;
                Navigate("diagnostics");
            }
            else if (Monitoring.IsBusy)
            {
                Diagnostics.SelectedSubTab = 0;
                Navigate("diagnostics");
            }
        }

        /// <summary>Публичное уведомление об изменении свойства (для подстраниц).</summary>
        public void Notify(string propertyName) => Raise(propertyName);

        public void RefreshReadiness()
        {
            Raise(nameof(IsAdmin));
            Raise(nameof(AdminWarningVisible));
            Raise(nameof(AdminWarningText));
            Raise(nameof(AdminWarningDetails));
            Raise(nameof(SafeMode));
            Raise(nameof(SafeModeText));
            Raise(nameof(ReadinessText));
            Raise(nameof(ReadinessKey));
            Raise(nameof(ReadinessDetails));
            Raise(nameof(ReadinessIsReady));
            Raise(nameof(ActiveStrategySummaryText));
            Raise(nameof(ActiveStrategyKey));
            Raise(nameof(ActiveStrategyTooltipText));
            Raise(nameof(MonitoringSummaryText));
            Raise(nameof(MonitoringSummaryKey));
            Raise(nameof(MonitoringSummaryTooltip));
            Raise(nameof(RealTimePingVisible));
            Raise(nameof(RealTimePingSummaryText));
            Raise(nameof(RealTimePingStatusKey));
            Raise(nameof(RealTimePingTooltip));
            Raise(nameof(IsAnyCheckRunning));
            Raise(nameof(ActiveCheckStatusText));
        }

        public void Navigate(string key)
        {
            if (key is "monitoring" or "dpi" or "deep-check" or "results")
            {
                var tab = key switch
                {
                    "monitoring" => 0,
                    "dpi" => 1,
                    "deep-check" => 2,
                    "results" => 4,
                    _ => 0
                };
                Diagnostics.SelectedSubTab = tab;
                key = "diagnostics";
            }

            foreach (var item in NavItems)
            {
                if (item.Key != key) continue;
                SelectedNav = item;
                return;
            }

            NavChanged?.Invoke(key);
        }

        /// <summary>Оставлено для совместимости со старым вызывающим кодом; мастер управляется вручную.</summary>
        public Task RunFirstLaunchChecksAsync()
        {
            AppLog.Debug("Автоматические проверки первого запуска отключены: используется мастер");
            return Task.CompletedTask;
        }

        private void RefreshThemeBindings()
        {
            Home.RefreshTheme();
            StrategiesPage.RefreshTheme();
            Updates.RefreshTheme();
            Diagnostics.RefreshTheme();
            FirstLaunch.RefreshTheme();
            Logs.RefreshTheme();
            Monitoring.RefreshTheme();
        }

        private void ToggleTheme()
        {
            Settings.Theme = Settings.Theme switch
            {
                ThemeMode.System => ThemeMode.Dark,
                ThemeMode.Dark => ThemeMode.Light,
                _ => ThemeMode.System
            };
            ThemeService.Apply(Settings.Theme);
            SettingsStore.Save(Settings);
            Raise(nameof(ThemeText));
            SettingsPage.Reload();
        }

        private void RestartAsAdmin()
        {
            if (Shell.IsAdmin()) return;
            if (Shell.RestartElevated())
                Application.Current.Shutdown();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                Home.RefreshStatus();
                StrategiesPage.RefreshRunButton();
                Updates.RefreshBadge();
                _ = CheckRealTimePingAsync();
                Raise(nameof(ReadinessText));
                Raise(nameof(ReadinessKey));
                Raise(nameof(ReadinessDetails));
                Raise(nameof(ReadinessIsReady));
                Raise(nameof(ActiveStrategySummaryText));
                Raise(nameof(ActiveStrategyKey));
                Raise(nameof(ActiveStrategyTooltipText));
                Raise(nameof(MonitoringSummaryText));
                Raise(nameof(MonitoringSummaryKey));
                Raise(nameof(MonitoringSummaryTooltip));
                Raise(nameof(RealTimePingVisible));
                Raise(nameof(RealTimePingSummaryText));
                Raise(nameof(RealTimePingStatusKey));
                Raise(nameof(RealTimePingTooltip));
                Raise(nameof(IsAnyCheckRunning));
                Raise(nameof(ActiveCheckStatusText));
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка обновления статуса: " + ex.Message);
            }
        }

        private async Task CheckRealTimePingAsync()
        {
            if (!Settings.RealTimePingEnabled || _isPingProbing) return;
            var interval = Math.Clamp(Settings.RealTimePingIntervalSeconds, 3, 120);
            if ((DateTime.Now - _lastPingProbeTime).TotalSeconds < interval) return;

            _isPingProbing = true;
            _lastPingProbeTime = DateTime.Now;
            try
            {
                RealTimePing = await RealTimePingService.ProbeAsync();
                Raise(nameof(RealTimePing));
                Raise(nameof(RealTimePingSummaryText));
                Raise(nameof(RealTimePingStatusKey));
                Raise(nameof(RealTimePingTooltip));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Ошибка RTT пинга: " + ex.Message);
            }
            finally
            {
                _isPingProbing = false;
            }
        }

        public void ApplyGameFilterState()
        {
            if (Settings.GameModeActive)
            {
                EngineService.SetGameFilterMode(Settings.EnginePath, GameFilterMode.TcpOnly);
            }
            else
            {
                EngineService.SetGameFilterMode(Settings.EnginePath, Settings.UseGameFilterOnStart ? GameFilterMode.TcpAndUdp : GameFilterMode.Disabled);
            }
            Home.ReloadFromEngine();
            Home.RefreshStatus();
            Home.RefreshGamingStatus();
            Raise(nameof(GameModeSummaryText));
        }

        public void ToggleGameMode()
        {
            Settings.GameModeActive = !Settings.GameModeActive;
            SettingsStore.Save(Settings);
            ApplyGameFilterState();
            MiniOverlay.Refresh();
            Home.RefreshGamingStatus();
            Raise(nameof(GameModeSummaryText));
        }

        public void ToggleMiniOverlay()
        {
            RequestToggleOverlay?.Invoke();
        }

        public void NotifyAutoSwitchChanged()
        {
            if (Settings.AutoSwitchProfileOnNetworkChange || Settings.AutoSwitchProfileOnFailure)
                ProfileAutoSwitch?.Start();
            else
                ProfileAutoSwitch?.Stop();
            Profiles?.RefreshNetwork();
            Raise(nameof(AutoSwitchNetworkStatus));
            Raise(nameof(AutoSwitchLastReason));
        }

        /// <summary>Вызывается при выходе: остановка обхода, если так настроено.</summary>
        public async System.Threading.Tasks.Task ShutdownAsync()
        {
            _timer.Stop();
            Monitoring.Stop();
            ScheduleService?.Stop();
            ScheduleService?.Dispose();
            ProfileAutoSwitch?.Stop();
            ProfileAutoSwitch?.Dispose();
            GameDetector.Dispose();
            Hotkeys.Dispose();
            if (Settings.StopBypassOnExit && Bypass.GetStatus().IsRunning)
            {
                AppLog.Info("Останавливаю обход при выходе из приложения");
                await Bypass.StopAsync();
            }
        }
    }
}
