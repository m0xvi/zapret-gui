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
    }

    /// <summary>Главная модель: держит настройки, контроллер обхода и все подстраницы.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _toolbarMetricsTimer;
        private Views.TaskbarMetricsWindow? _taskbarMetricsWindow;
        private NavItem? _selectedNav;
        private NavItem? _selectedUtility;
        private bool _isHelpActive;
        private bool _isAdmin;
        private DateTime _lastPingProbeTime = DateTime.MinValue;
        private bool _isPingProbing;

        public event Action<string>? WatchdogNotificationRequested;
        public event Action? RequestToggleOverlay;

        public BypassCenterViewModel BypassCenter { get; }

        public WatchdogService Watchdog { get; }
        public SeamlessFailoverService SeamlessFailover { get; }
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
            BypassCenter = new BypassCenterViewModel(this);
            StrategiesPage = new StrategiesViewModel(this);
            Updates = new UpdatesViewModel(this);
            GlobalOverlay = new GlobalOverlayViewModel();
            Monitoring = new MonitoringViewModel(this);
            Diagnostics = new DiagnosticsViewModel(this);
            DeepCheck = new DeepCheckViewModel(this);
            UserLists = new UserListsViewModel(this);
            Profiles = new ProfilesViewModel(this);
            FirstLaunch = new FirstLaunchViewModel(this);
            Logs = new LogsViewModel(this);
            SettingsPage = new SettingsViewModel(this);
            Configuration = new ConfigurationViewModel(this);
            NetworkProfile = new NetworkProfileViewModel(this);
            Help = new HelpViewModel(this);
            Search = new SearchViewModel(this);

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

            Hotkeys.ToggleExpertModeRequested += () => Application.Current?.Dispatcher?.Invoke(() =>
            {
                ToggleExpertMode();
                WatchdogNotificationRequested?.Invoke(Settings.ExpertModeEnabled
                    ? "🔧 Режим «Эксперт» включён — технические блоки видны"
                    : "🙂 Включён «Простой» режим — технические блоки скрыты");
            });

            Watchdog = new WatchdogService(settings, Bypass, () => Strategies.Find(settings.SelectedStrategy) ?? Strategies.Recommended, () => Strategies.Items.ToList());
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

            SeamlessFailover = new SeamlessFailoverService(settings, () => Bypass, () => Strategies);
            SeamlessFailover.StatusChanged += msg => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Raise(nameof(SeamlessStatusText));
                Raise(nameof(SeamlessStatusKey));
            });
            SeamlessFailover.FailoverSucceeded += msg => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Home.RefreshStatus();
                StrategiesPage.Refresh();
                Raise(nameof(ActiveStrategySummaryText));
                Raise(nameof(SeamlessStatusText));
                Raise(nameof(SeamlessStatusKey));
                if (Settings.MonitorNotificationsEnabled || Settings.WatchdogNotifyUser)
                    WatchdogNotificationRequested?.Invoke(msg);
            });
            SeamlessFailover.FailoverFailed += msg => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Raise(nameof(SeamlessStatusText));
                if (Settings.MonitorNotificationsEnabled)
                    WatchdogNotificationRequested?.Invoke(msg);
            });
            if (settings.SeamlessFailoverEnabled && !settings.SafeMode)
                SeamlessFailover.Start();

            ProfileAutoSwitch = new ProfileAutoSwitchService(settings, () => Bypass, () => Strategies);
            ProfileAutoSwitch.StatusChanged += msg => System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                Profiles.RefreshNetwork();
                Raise(nameof(AutoSwitchNetworkStatus));
            });
            ScheduleService = new BypassScheduleService(settings, () => Bypass, () => Strategies.Find(settings.SelectedStrategy) ?? Strategies.Recommended);
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

            // Навигация v1.21.0 (этап 4.5, docs/IA_REDESIGN.md §2): плоский список из 5 разделов, без групп.
            // «Помощь» (этап 6) — утилита в подвале меню: своя коллекция, поэтому разделов по-прежнему пять.
            // Прежние пункты «Стратегии», «Списки», «Журнал», «Профили и копии», «Обновления» и «О программе»
            // больше не верхний уровень: их страницы открываются кнопками внутри своих разделов
            // (ключи `_pages` и `Navigate` не тронуты — трей, шапка и старые ссылки работают как раньше).
            NavSections = new ObservableCollection<NavItem>
            {
                new() { Key = "home", Title = "Главная", Icon = "\uE80F", Hint = "Состояние обхода и включение" },
                new() { Key = "bypass-center", Title = "Обход", Icon = "\uE8D2", Hint = "Способ обхода, подбор, DNS, списки, сложные сайты" },
                new() { Key = "diagnostics", Title = "Проверки", Icon = "\uE90F", Hint = "Быстрая проверка, сайты и звонки, система, журнал" },
                new() { Key = "automation", Title = "Автоматизация", Icon = "\uE945", Hint = "Автозапуск, присмотр за обходом и расписание" },
                new() { Key = "settings", Title = "Настройки", Icon = "\uE713", Hint = "Движок и обновления, профили и копии, оформление" },
            };
            NavUtilities = new ObservableCollection<NavItem>
            {
                new() { Key = "help", Title = "Помощь", Icon = "\uE897", Hint = "Не работает? Пять сценариев и переходы" },
            };
            NavItems = new ObservableCollection<NavItem>(NavSections.Concat(NavUtilities));
            // Стартовый пункт ищем по ключу, а не по индексу: состав меню меняется.
            _selectedNav = NavSections.First(i => i.Key == "home");

            _isAdmin = Shell.IsAdmin();

            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            NavigateHomeCommand = new RelayCommand(() => Navigate("home"));
            NavigateDiagnosticsCommand = new RelayCommand(() => { Diagnostics.OpenSystemSubTab(); Navigate("diagnostics"); });
            NavigateStrategiesCommand = new RelayCommand(() => Navigate("strategies"));
            NavigateMonitoringCommand = new RelayCommand(() => Navigate("monitoring"));
            NavigateActiveCheckCommand = new RelayCommand(NavigateToActiveCheck);
            ToggleExpertModeCommand = new RelayCommand(ToggleExpertMode);
            OpenSearchCommand = new RelayCommand(Search.Open);

            Strategies.Refresh();
            Home.ReloadFromEngine();
            ThemeService.Changed += RefreshThemeBindings;

            _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, OnTick, Application.Current.Dispatcher);
            _timer.Start();
            // Тулбар-метрики как в MSI Afterburner — обновление каждую минуту, управляется в Настройках
            var toolbarInterval = Math.Clamp(Settings.ToolbarMetricsIntervalSeconds, 15, 300);
            _toolbarMetricsTimer = new DispatcherTimer(TimeSpan.FromSeconds(toolbarInterval), DispatcherPriority.Background, OnToolbarMetricsTick, Application.Current.Dispatcher);
            if (Settings.ToolbarMetricsEnabled) _toolbarMetricsTimer.Start();
            // Создаём окно метрик на панели задач (как в MSI Afterburner) — рядом с трей-иконками
            try
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _taskbarMetricsWindow = new Views.TaskbarMetricsWindow { DataContext = this };
                        if (Settings.ToolbarMetricsEnabled) { _taskbarMetricsWindow.Show(); _taskbarMetricsWindow.UpdatePosition(); }
                    }
                    catch {}
                });
            }
            catch {}
        }

        public AppSettings Settings { get; }
        public BypassController Bypass { get; }
        public StrategyStore Strategies { get; }

        public HomeViewModel Home { get; }
        public StrategiesViewModel StrategiesPage { get; }
        public UpdatesViewModel Updates { get; }
    public GlobalOverlayViewModel GlobalOverlay { get; }
        public SettingsViewModel SettingsPage { get; }

        /// <summary>Рабочий стол настройщика (v1.29.3): сводка, применение, история и откат конфигурации обхода.</summary>
        public ConfigurationViewModel Configuration { get; }

        /// <summary>«Под мою сеть» (v1.30.0): проверка сети по шагам под конкретного провайдера.</summary>
        public NetworkProfileViewModel NetworkProfile { get; }
        public DiagnosticsViewModel Diagnostics { get; }
        public DeepCheckViewModel DeepCheck { get; }
        public UserListsViewModel UserLists { get; }
        public ProfilesViewModel Profiles { get; }
        public FirstLaunchViewModel FirstLaunch { get; }
        public LogsViewModel Logs { get; }
        public MonitoringViewModel Monitoring { get; }

        /// <summary>«Помощь» — только тексты и переходы (этап 6, docs/IA_REDESIGN.md §3.6).</summary>
        public HelpViewModel Help { get; }

        /// <summary>Поиск по приложению `Ctrl+K` (этап 7): разделы, настройки, команды, сценарии.</summary>
        public SearchViewModel Search { get; }

        /// <summary>Пять разделов верхнего уровня (основной список меню).</summary>
        public ObservableCollection<NavItem> NavSections { get; }

        /// <summary>Утилиты подвала меню: «Помощь». Отдельная коллекция, чтобы разделов оставалось пять.</summary>
        public ObservableCollection<NavItem> NavUtilities { get; }

        /// <summary>Все пункты навигации (разделы + подвал) — для `Navigate` и подсветки.</summary>
        public ObservableCollection<NavItem> NavItems { get; }

        public NavItem? SelectedNav
        {
            get => _selectedNav;
            set
            {
                if (Set(ref _selectedNav, value))
                {
                    if (value != null) SetSelectedUtility(null);
                    Raise(nameof(SelectedNavKey));
                    NavChanged?.Invoke(value?.Key ?? "home");
                }
            }
        }

        public string SelectedNavKey => _selectedNav?.Key ?? "home";

        /// <summary>Выбор в подвале меню («Помощь»). Отдельно от <see cref="SelectedNav"/>: два списка
        /// не должны сбрасывать выбор друг друга (сброс основного списка уводил бы на «Главную»).</summary>
        public NavItem? SelectedUtility
        {
            get => _selectedUtility;
            set
            {
                if (!Set(ref _selectedUtility, value)) return;
                if (value != null) OpenHelp();
            }
        }

        /// <summary>Подсвечена ли «Помощь» — для стиля пункта в подвале меню.</summary>
        public bool IsHelpActive
        {
            get => _isHelpActive;
            private set => Set(ref _isHelpActive, value);
        }

        /// <summary>Открыть «Помощь»: раздел вне пяти разделов, поэтому подсветку разделов снимаем
        /// без `NavChanged` (иначе сработала бы навигация на «Главную»).</summary>
        public void OpenHelp()
        {
            if (_selectedNav != null)
            {
                Set(ref _selectedNav, null, nameof(SelectedNav));
                Raise(nameof(SelectedNavKey));
            }
            // Подсвечиваем пункт подвала: без этого вход в «Помощь» из поиска, с «Главной»
            // или по старой ссылке оставлял пункт невыделенным (исправлено в v1.28.0).
            SetSelectedUtility(NavUtilities.FirstOrDefault(i => i.Key == "help"));
            IsHelpActive = true;
            NavChanged?.Invoke("help");
        }

        /// <summary>Подсветка пункта подвала меню (этап 6).</summary>
        private void SetSelectedUtility(NavItem? item)
        {
            if (ReferenceEquals(_selectedUtility, item)) return;
            _selectedUtility = item;
            Raise(nameof(SelectedUtility));
        }

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

        /// <summary>Экспертный режим интерфейса: технические блоки видимы (по умолчанию выключен — «Простой»).</summary>
        public bool ExpertMode => Settings.ExpertModeEnabled;

        /// <summary>Инверсия для привязок видимости простых блоков.</summary>
        public bool SimpleMode => !ExpertMode;

        /// <summary>Бейдж режима рядом с версией: «Эксперт» показывается только в экспертном режиме.</summary>
        public string ExpertModeBadgeText => ExpertMode ? "Эксперт" : "";

        public bool ExpertModeBadgeVisible => ExpertMode;

        public string ExpertModeToggleText => ExpertMode ? "Простой режим" : "Режим «Эксперт»";

        public string ExpertModeToggleHint => ExpertMode
            ? "Скрыть технические блоки (Ctrl+Shift+E)"
            : "Показать технические блоки: матрица 92 тестов, SNI-пул, режимы ipset и другое (Ctrl+Shift+E)";

        public RelayCommand ToggleExpertModeCommand { get; }

        /// <summary>Открыть оверлей поиска (кнопка в шапке и `Ctrl+K`).</summary>
        public RelayCommand OpenSearchCommand { get; }

        /// <summary>Переключение режима «Простой/Эксперт». Сам переключатель находится в шапке (Ctrl+Shift+E),
        /// режим сохраняется в настройках и считается источником истины для видимости экспертных блоков.</summary>
        public void ToggleExpertMode()
        {
            SetExpertMode(!Settings.ExpertModeEnabled);
        }

        public void SetExpertMode(bool enabled)
        {
            if (Settings.ExpertModeEnabled == enabled) return;
            Settings.ExpertModeEnabled = enabled;
            SettingsStore.Save(Settings);
            Raise(nameof(ExpertMode));
            Raise(nameof(SimpleMode));
            Raise(nameof(ExpertModeBadgeText));
            Raise(nameof(ExpertModeBadgeVisible));
            Raise(nameof(ExpertModeToggleText));
            Raise(nameof(ExpertModeToggleHint));
            AppLog.Info(enabled ? "[UI] Включён режим «Эксперт»" : "[UI] Включён «Простой» режим");
        }

        /// <summary>Миграция v1.22.0: если у пользователя есть нестандартные экспертные параметры,
        /// показываем один баллун с предложением включить «Эксперт» (без модального окна).</summary>
        public bool ShouldSuggestExpertMode()
        {
            if (Settings.ExpertModeEnabled || Settings.ExpertModeHintShown) return false;
            if (Settings.CustomSniList.Count > 0) return true;
            if (Settings.HostSpecificStrategies.Count > 0) return true;
            if (Settings.AutoSniRotationEnabled) return true;
            if (Settings.UseDohForBlockedHosts) return true;
            return !string.IsNullOrWhiteSpace(Settings.YoutubeSniOverride);
        }

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

        // Тулбар-метрики — как в MSI Afterburner, на панели задач внизу (вертикально)
        public bool ToolbarMetricsVisible => Settings.ToolbarMetricsEnabled;
        public string ToolbarMetricsText => BuildToolbarMetricsText();
        public string ToolbarMetricsTooltip => "Метрики на панели задач внизу (как в MSI Afterburner) — настройте в Настройки → Сеть → Метрики на панели задач";
        public System.Collections.Generic.IReadOnlyList<ToolbarMetricsRow> ToolbarMetricsRows => BuildToolbarMetricsRows();

        private string BuildToolbarMetricsText()
        {
            // Горизонтальная строка для совместимости — теперь основной вертикальный список
            try { return string.Join("  ", BuildToolbarMetricsRows().Select(r => r.CompactText)); } catch { return MonitoringSummaryText; }
        }

        private System.Collections.Generic.IReadOnlyList<ToolbarMetricsRow> BuildToolbarMetricsRows()
        {
            var list = new System.Collections.Generic.List<ToolbarMetricsRow>();
            if (!Settings.ToolbarMetricsEnabled) return list;
            if (Monitoring == null || Monitoring.Targets == null) return list;
            try
            {
                var visible = Settings.ToolbarMetricsVisibleTargets;
                var targets = Monitoring.Targets.Where(t => t.Enabled).ToList();
                if (visible != null && visible.Count > 0)
                    targets = targets.Where(t => visible.Any(v => v.Equals(t.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                if (targets.Count == 0) targets = Monitoring.Targets.Take(4).ToList();
                var results = Monitoring.Results.ToList();
                foreach (var tgt in targets.Take(6))
                {
                    var res = results.FirstOrDefault(r => r.Target.Name == tgt.Name);
                    if (res != null)
                    {
                        var latency = res.Ok ? $"{res.Milliseconds} мс" : "—";
                        var key = res.StatusKey;
                        list.Add(new ToolbarMetricsRow(tgt.Name, latency, key, res.Ok));
                    }
                    else
                    {
                        list.Add(new ToolbarMetricsRow(tgt.Name, "…", "Info", false));
                    }
                }
            }
            catch {}
            if (list.Count == 0) list.Add(new ToolbarMetricsRow("Нет данных", "…", "Muted", false));
            return list;
        }

        public void RefreshToolbarMetrics()
        {
            Raise(nameof(ToolbarMetricsVisible));
            Raise(nameof(ToolbarMetricsText));
            Raise(nameof(ToolbarMetricsTooltip));
            Raise(nameof(ToolbarMetricsRows));
            // Перезапуск таймера при изменении интервала
            try
            {
                var interval = Math.Clamp(Settings.ToolbarMetricsIntervalSeconds, 15, 300);
                _toolbarMetricsTimer.Interval = TimeSpan.FromSeconds(interval);
                if (Settings.ToolbarMetricsEnabled && !_toolbarMetricsTimer.IsEnabled) _toolbarMetricsTimer.Start();
                if (!Settings.ToolbarMetricsEnabled && _toolbarMetricsTimer.IsEnabled) _toolbarMetricsTimer.Stop();
            }
            catch {}
            // Управление окном на панели задач
            try
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (Settings.ToolbarMetricsEnabled)
                        {
                            if (_taskbarMetricsWindow == null)
                            {
                                _taskbarMetricsWindow = new Views.TaskbarMetricsWindow { DataContext = this };
                            }
                            if (!_taskbarMetricsWindow.IsVisible) _taskbarMetricsWindow.Show();
                            _taskbarMetricsWindow.UpdatePosition();
                        }
                        else
                        {
                            _taskbarMetricsWindow?.Hide();
                        }
                    }
                    catch {}
                });
            }
            catch {}
        }

        private void OnToolbarMetricsTick(object? sender, EventArgs e)
        {
            // Обновление каждую минуту — как в MSI Afterburner
            Raise(nameof(ToolbarMetricsText));
            Raise(nameof(ToolbarMetricsRows));
            Raise(nameof(MonitoringSummaryText));
            Raise(nameof(RealTimePingSummaryText));
            // Обновляем позицию окна на панели задач (на случай перемещения таскбара)
            try { _taskbarMetricsWindow?.UpdatePosition(); } catch {}
            // Фоновая проверка выбранных ресурсов
            if (Settings.ToolbarMetricsEnabled && !IsAnyCheckRunning)
            {
                _ = Monitoring.CheckAllAsync();
            }
        }

        public string AutoSwitchNetworkStatus => ProfileAutoSwitch?.CurrentIdentity?.DisplayName ?? "Сеть не определена";
        public string AutoSwitchLastReason => ProfileAutoSwitch?.LastReason ?? "";
        public string ScheduleSummaryText => ScheduleService?.Describe() ?? "расписание выключено";

        public string SeamlessStatusText => SeamlessFailover?.LastReason ?? "Бесшовное переключение неактивно";
        public string SeamlessStatusKey
        {
            get
            {
                var t = SeamlessStatusText;
                if (t.Contains("✅") || t.Contains("восстановлен") || t.Contains("доступны")) return "Success";
                if (t.Contains("❌") || t.Contains("не удалось") || t.Contains("ошибка", StringComparison.OrdinalIgnoreCase)) return "Danger";
                if (t.Contains("подбираю") || t.Contains("Диагностирую") || t.Contains("Сбой")) return "Warning";
                return "Info";
            }
        }
        public string SeamlessLastSwitchText => Settings.SeamlessLastSwitchTime.HasValue ? Settings.SeamlessLastSwitchTime.Value.ToLocalTime().ToString("dd.MM HH:mm") : "ещё не было";
        public void NotifySeamlessChanged()
        {
            if (Settings.SeamlessFailoverEnabled && !Settings.SafeMode) SeamlessFailover?.Start(); else SeamlessFailover?.Stop();
            SeamlessFailover?.UpdateInterval();
            Raise(nameof(SeamlessStatusText));
            Raise(nameof(SeamlessStatusKey));
            Raise(nameof(SeamlessLastSwitchText));
            BypassCenter?.NotifyAutoSwitchChanged();
        }
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
                Diagnostics.SelectedSubTab = 3;
                Navigate("diagnostics");
            }
            else if (StrategiesPage.IsTestingAll || StrategiesPage.IsEvaluatingCandidates)
            {
                Navigate("strategies");
            }
            else if (Diagnostics.IsRunning)
            {
                Diagnostics.SelectedSubTab = 2;
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
            // «Помощь» живёт в подвале меню (этап 6) — открывается своим путём.
            if (key == "help")
            {
                OpenHelp();
                return;
            }

            // «Журнал» с v1.21.0 живёт внутри «Проверок» (вкладка 5), отдельного пункта меню нет —
            // ключ сохранён, чтобы трей, «Настройки» и «Главная» продолжали работать.
            if (key is "monitoring" or "dpi" or "deep-check" or "results" or "logs")
            {
                // Индексы подразделов «Проверок» v1.20.0 (docs/IA_REDESIGN.md §3.3).
                var tab = key switch
                {
                    "monitoring" => 0,   // Быстрая проверка
                    "dpi" => 1,          // Сложные сайты и звонки
                    "deep-check" => 3,   // Глубокая проверка
                    "results" => 4,      // История и отчёты
                    "logs" => 5,         // Журнал
                    _ => 0
                };
                // В «Простом» режиме экспертных подразделов нет — ведём на быструю проверку.
                if (!ExpertMode && tab is 2 or 3 or 4) tab = 0;
                Diagnostics.SelectedSubTab = tab;
                key = "diagnostics";
            }

            foreach (var item in NavItems)
            {
                if (item.Key != key) continue;
                SelectedNav = item;
                return;
            }

            // Вложенный экран или старый ключ: подсветка «Помощи» в подвале снимается.
            IsHelpActive = false;
            SetSelectedUtility(null);

            // Вложенные экраны (этап 4.5): самого пункта в меню нет, но подсвечиваем родительский
            // раздел — так видно, где пользователь находится («Стратегии»/«Списки» → «Обход»,
            // «Профили и копии»/«Обновления»/«О программе» → «Настройки»).
            var parentKey = key switch
            {
                "strategies" or "user-lists" or "configuration" or "network" => "bypass-center",
                "profiles" or "updates" or "about" => "settings",
                _ => null
            };
            if (parentKey != null)
            {
                var parent = NavItems.FirstOrDefault(i => i.Key == parentKey);
                if (parent != null && !ReferenceEquals(_selectedNav, parent))
                {
                    Set(ref _selectedNav, parent, nameof(SelectedNav));
                    Raise(nameof(SelectedNavKey));
                }
            }

            // Сценарий «под мою сеть» перечитывает состояние при открытии (v1.30.0).
            if (key == "network") NetworkProfile.Reload();

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

        public void CloseTaskbarMetricsWindow()
        {
            try { _taskbarMetricsWindow?.Close(); } catch {}
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
            SeamlessFailover?.Stop();
            SeamlessFailover?.Dispose();
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
    
    public sealed class ToolbarMetricsRow
    {
        public ToolbarMetricsRow(string name, string latency, string statusKey, bool ok)
        {
            Name = name;
            Latency = latency;
            StatusKey = statusKey;
            IsOk = ok;
        }
        public string Name { get; }
        public string Latency { get; }
        public string StatusKey { get; }
        public bool IsOk { get; }
        public string CompactText => $"{Name} {Latency}";
    }
}
}
