using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
            FirstLaunch = new FirstLaunchViewModel(this);
            Logs = new LogsViewModel();
            Monitoring = new MonitoringViewModel(this);

            NavItems = new ObservableCollection<NavItem>
            {
                new() { Key = "group-main", Title = "ОСНОВНОЕ", IsSectionHeader = true },
                new() { Key = "home", Title = "Обзор", Icon = "\uE80F", Hint = "Состояние обхода" },
                new() { Key = "first-run", Title = "Первый запуск", Icon = "\uE748", Hint = "Мастер настройки" },
                new() { Key = "strategies", Title = "Стратегии", Icon = "\uE71D", Hint = "Выбор обхода" },
                new() { Key = "group-checks", Title = "ПРОВЕРКИ И НАБЛЮДЕНИЕ", IsSectionHeader = true },
                new() { Key = "monitoring", Title = "Мониторинг", Icon = "\uE701", Hint = "Ресурсы и провайдер" },
                new() { Key = "diagnostics", Title = "Диагностика", Icon = "\uE90F", Hint = "Проверка проблем" },
                new() { Key = "deep-check", Title = "Глубокая проверка", Icon = "\uE9CE", Hint = "Полный профиль сети" },
                new() { Key = "dpi", Title = "Проверка DPI", Icon = "\uE71C", Hint = "DNS, TCP и TLS" },
                new() { Key = "logs", Title = "Журнал", Icon = "\uE7C3", Hint = "События приложения" },
                new() { Key = "group-data", Title = "ПОЛЬЗОВАТЕЛЬСКИЕ ДАННЫЕ", IsSectionHeader = true },
                new() { Key = "user-lists", Title = "Списки пользователя", Icon = "\uE8FD", Hint = "Домены и IP" },
                new() { Key = "group-system", Title = "СИСТЕМА", IsSectionHeader = true },
                new() { Key = "updates", Title = "Обновления", Icon = "\uE895", Hint = "Движок и списки" },
                new() { Key = "settings", Title = "Настройки", Icon = "\uE713", Hint = "Путь, тема, автозапуск" },
                new() { Key = "about", Title = "О программе", Icon = "\uE946", Hint = "Авторы и лицензии" }
            };
            _selectedNav = NavItems[1];

            _isAdmin = Shell.IsAdmin();

            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));

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
                if (string.IsNullOrWhiteSpace(version)) version = string.IsNullOrWhiteSpace(Settings.EngineVersion) ? "не установлен" : Settings.EngineVersion;
                return version;
            }
        }

        public string ThemeText => ThemeService.ModeText(Settings.Theme);

        public double InterfaceZoom => Math.Clamp(Settings.InterfaceZoomPercent, 80, 140) / 100d;

        public string StatusPillText => Home.StatusText;

        public string StatusPillKey => Home.StatusKey;

        public ICommand ToggleThemeCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }

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
        }

        public void Navigate(string key)
        {
            foreach (var item in NavItems)
            {
                if (item.Key != key) continue;
                SelectedNav = item;
                return;
            }
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
                Updates.RefreshBadge();
                Raise(nameof(ReadinessText));
                Raise(nameof(ReadinessKey));
                Raise(nameof(ReadinessDetails));
                Raise(nameof(ReadinessIsReady));
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка обновления статуса: " + ex.Message);
            }
        }

        /// <summary>Вызывается при выходе: остановка обхода, если так настроено.</summary>
        public async System.Threading.Tasks.Task ShutdownAsync()
        {
            _timer.Stop();
            Monitoring.Stop();
            if (Settings.StopBypassOnExit && Bypass.GetStatus().IsRunning)
            {
                AppLog.Info("Останавливаю обход при выходе из приложения");
                await Bypass.StopAsync();
            }
        }
    }
}
