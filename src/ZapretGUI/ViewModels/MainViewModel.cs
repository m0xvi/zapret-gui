using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
            Logs = new LogsViewModel();

            NavItems = new ObservableCollection<NavItem>
            {
                new() { Key = "home", Title = "Обзор", Icon = "\uE80F", Hint = "Состояние обхода" },
                new() { Key = "strategies", Title = "Стратегии", Icon = "\uE71D", Hint = "Выбор обхода" },
                new() { Key = "updates", Title = "Обновления", Icon = "\uE895", Hint = "Движок и списки" },
                new() { Key = "diagnostics", Title = "Диагностика", Icon = "\uE90F", Hint = "Проверка проблем" },
                new() { Key = "logs", Title = "Журнал", Icon = "\uE7C3", Hint = "События приложения" },
                new() { Key = "settings", Title = "Настройки", Icon = "\uE713", Hint = "Путь, тема, автозапуск" },
                new() { Key = "about", Title = "О программе", Icon = "\uE946", Hint = "Авторы и лицензии" }
            };
            _selectedNav = NavItems[0];

            _isAdmin = Shell.IsAdmin();

            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));

            Strategies.Refresh();
            Home.ReloadFromEngine();

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
        public LogsViewModel Logs { get; }

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

        public string AppVersion => "1.0.1";

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

        public string StatusPillText => Home.StatusText;

        public string StatusPillKey => Home.StatusKey;

        public ICommand ToggleThemeCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }

        /// <summary>Публичное уведомление об изменении свойства (для подстраниц).</summary>
        public void Notify(string propertyName) => Raise(propertyName);

        public void Navigate(string key)
        {
            foreach (var item in NavItems)
            {
                if (item.Key != key) continue;
                SelectedNav = item;
                return;
            }
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
            if (Settings.StopBypassOnExit && Bypass.GetStatus().IsRunning)
            {
                AppLog.Info("Останавливаю обход при выходе из приложения");
                await Bypass.StopAsync();
            }
        }
    }
}
