using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class HomeViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private BypassStatus _status = new();
        private bool _isBusy;
        private string _message = "";
        private string _messageKey = "Info";
        private string _selectedStrategyName = "";
        private int _gameFilterIndex;
        private int _ipsetIndex;
        private bool _batAutoUpdate;
        private string _latestVersion = "";
        private bool _connectionBusy;
        private int _suppressWrites;

        public HomeViewModel(MainViewModel main)
        {
            _main = main;

            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && HasStrategy && !IsBusy);
            StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning && !IsBusy);
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => HasStrategy && !IsBusy);
            RemoveServiceCommand = new AsyncRelayCommand(RemoveServiceAsync, () => ServiceInstalled && !IsBusy);
            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !ConnectionBusy);
            CheckUpdatesCommand = new RelayCommand(() => _main.Navigate("updates"));
            OpenDiagnosticsCommand = new RelayCommand(() => _main.Navigate("diagnostics"));
            OpenStrategiesCommand = new RelayCommand(() => _main.Navigate("strategies"));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            RestartAsAdminCommand = new RelayCommand(() => _main.RestartAsAdminCommand.Execute(null));
            ClearMessageCommand = new RelayCommand(() => Message = "");
        }

        public AppSettings Settings => _main.Settings;
        public BypassController Bypass => _main.Bypass;
        public StrategyStore Store => _main.Strategies;

        public ObservableCollection<string> StrategyNames { get; } = new();
        public ObservableCollection<ConnectionCheck> ConnectionChecks { get; } = new();

        public string[] GameFilterOptions { get; } = { "Выключен", "TCP + UDP", "Только TCP", "Только UDP" };
        public string[] IpsetOptions { get; } = { "Списки (loaded)", "Отключён (none)", "Все IP (any)" };

        public bool IsRunning => _status.IsRunning;
        public bool ServiceInstalled => _status.ServiceState != ServiceState.NotInstalled;
        public bool HasStrategy => Store.Items.Count > 0;
        public string StatusText => _status.StateText;
        public string StatusKey => _status.State switch
        {
            BypassState.RunningStandalone or BypassState.RunningService => "Success",
            BypassState.Starting or BypassState.Stopping => "Warning",
            BypassState.Error => "Danger",
            _ => "Muted"
        };
        public string BypassStateKey => _status.State switch
        {
            BypassState.RunningStandalone or BypassState.RunningService => "Success",
            BypassState.Starting or BypassState.Stopping => "Warning",
            BypassState.Error => "Danger",
            _ => "Muted"
        };
        public BypassState State => _status.State;

        public string StrategyText => string.IsNullOrEmpty(_status.StrategyName)
            ? (HasStrategy ? "Стратегия не выбрана" : "Движок ещё не установлен")
            : "Стратегия: " + _status.StrategyName;

        public string UptimeText => _status.UptimeText;
        public string PidText => _status.Pid.HasValue ? "PID " + _status.Pid.Value : "—";

        public string ServiceText => _status.ServiceState switch
        {
            ServiceState.NotInstalled => "Служба не установлена",
            ServiceState.Running => "Служба запущена" + (string.IsNullOrEmpty(_status.ServiceStrategy) ? "" : ": " + _status.ServiceStrategy),
            ServiceState.StopPending => "Служба в состоянии STOP_PENDING",
            ServiceState.StartPending => "Служба в состоянии START_PENDING",
            ServiceState.Stopped => "Служба установлена, но остановлена",
            _ => "Служба: " + _status.ServiceState
        };

        public string EngineVersionText
        {
            get
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                return string.IsNullOrWhiteSpace(version) ? "версия неизвестна" : "v" + version;
            }
        }

        public string LatestVersionText => string.IsNullOrEmpty(_latestVersion) ? "не проверялось" : "v" + _latestVersion;

        public bool UpdateAvailable
        {
            get
            {
                if (string.IsNullOrEmpty(_latestVersion)) return false;
                var current = EngineService.ReadVersion(Settings.EnginePath);
                return EngineService.CompareVersions(_latestVersion, current) > 0;
            }
        }

        public string StrategyCountText => HasStrategy
            ? $"Доступно стратегий: {Store.Items.Count}"
            : "Стратегии не найдены — проверьте папку движка";

        public string EnginePathText => Settings.EnginePath;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value)) RaiseCommands();
            }
        }

        public bool ConnectionBusy
        {
            get => _connectionBusy;
            private set
            {
                if (Set(ref _connectionBusy, value)) RaiseCommands();
            }
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

        public string SelectedStrategyName
        {
            get => _selectedStrategyName;
            set
            {
                if (!Set(ref _selectedStrategyName, value)) return;
                if (_suppressWrites > 0 || string.IsNullOrEmpty(value)) return;

                var strategy = Store.Find(value);
                if (strategy != null)
                {
                    Settings.SelectedStrategy = strategy.Name;
                    SettingsStore.Save(Settings);
                    Raise(nameof(StrategyText));
                }
            }
        }

        /// <summary>0 — выключен, 1 — TCP+UDP, 2 — только TCP, 3 — только UDP.</summary>
        public int GameFilterIndex
        {
            get => _gameFilterIndex;
            set
            {
                if (!Set(ref _gameFilterIndex, value)) return;
                if (_suppressWrites > 0) return;

                var mode = value switch
                {
                    1 => GameFilterMode.TcpAndUdp,
                    2 => GameFilterMode.TcpOnly,
                    3 => GameFilterMode.UdpOnly,
                    _ => GameFilterMode.Disabled
                };
                EngineService.SetGameFilterMode(Settings.EnginePath, mode);
                ShowWarning(IsRunning
                    ? "Режим игрового фильтра сохранён. Перезапустите обход, чтобы применить изменения."
                    : "Режим игрового фильтра сохранён");
            }
        }

        /// <summary>0 — loaded, 1 — none, 2 — any.</summary>
        public int IpsetIndex
        {
            get => _ipsetIndex;
            set
            {
                if (!Set(ref _ipsetIndex, value)) return;
                if (_suppressWrites > 0) return;

                var mode = value switch
                {
                    1 => IpsetMode.None,
                    2 => IpsetMode.Any,
                    _ => IpsetMode.Loaded
                };

                if (EngineService.SetIpsetMode(Settings.EnginePath, mode))
                    ShowWarning(IsRunning
                        ? "Режим ipset изменён. Перезапустите обход, чтобы применить изменения."
                        : "Режим ipset изменён");
                else
                    ShowError("Не удалось переключить режим ipset: нет резервной копии списка. Обновите список на странице «Обновления».");
            }
        }

        /// <summary>Легаси-флаг utils\check_updates.enabled, который читают .bat-файлы.</summary>
        public bool BatAutoUpdate
        {
            get => _batAutoUpdate;
            set
            {
                if (!Set(ref _batAutoUpdate, value)) return;
                if (_suppressWrites > 0) return;
                EngineService.SetBatAutoUpdateFlag(Settings.EnginePath, value);
                ShowInfo(value
                    ? "Проверка обновлений для .bat-файлов включена"
                    : "Проверка обновлений для .bat-файлов выключена");
            }
        }

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand RemoveServiceCommand { get; }
        public ICommand TestConnectionCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand OpenDiagnosticsCommand { get; }
        public ICommand OpenStrategiesCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand ClearMessageCommand { get; }

        // ------------------------------------------------------------------ логика

        public void ReloadFromEngine()
        {
            _suppressWrites++;

            Store.Refresh();
            StrategyNames.Clear();
            foreach (var strategy in Store.Items) StrategyNames.Add(strategy.Name);

            var selected = Settings.SelectedStrategy;
            if (string.IsNullOrEmpty(selected) || Store.Find(selected) == null)
                selected = Store.Recommended?.Name ?? Store.Items.FirstOrDefault()?.Name ?? "";
            SelectedStrategyName = selected;

            _gameFilterIndex = EngineService.GetGameFilterMode(Settings.EnginePath) switch
            {
                GameFilterMode.TcpAndUdp => 1,
                GameFilterMode.TcpOnly => 2,
                GameFilterMode.UdpOnly => 3,
                _ => 0
            };
            Raise(nameof(GameFilterIndex));

            _ipsetIndex = EngineService.GetIpsetMode(Settings.EnginePath) switch
            {
                IpsetMode.None => 1,
                IpsetMode.Any => 2,
                _ => 0
            };
            Raise(nameof(IpsetIndex));

            _batAutoUpdate = EngineService.GetBatAutoUpdateFlag(Settings.EnginePath);
            Raise(nameof(BatAutoUpdate));

            _suppressWrites--;

            Raise(nameof(HasStrategy));
            Raise(nameof(StrategyCountText));
            Raise(nameof(EnginePathText));
            Raise(nameof(EngineVersionText));
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            _status = Bypass.GetStatus();
            Raise(nameof(IsRunning));
            Raise(nameof(ServiceInstalled));
            Raise(nameof(StatusText));
            Raise(nameof(StatusKey));
            Raise(nameof(BypassStateKey));
            Raise(nameof(State));
            Raise(nameof(StrategyText));
            Raise(nameof(UptimeText));
            Raise(nameof(PidText));
            Raise(nameof(ServiceText));
            Raise(nameof(UpdateAvailable));
            RaiseCommands();
        }

        private void RaiseCommands()
        {
            (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RemoveServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (TestConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private StrategyInfo? Current()
        {
            var strategy = Store.Find(SelectedStrategyName) ?? Store.Recommended ?? Store.Items.FirstOrDefault();
            if (strategy == null)
                ShowError("Стратегии не найдены. Скачайте движок на странице «Обновления».");
            return strategy;
        }

        private async Task StartAsync()
        {
            var strategy = Current();
            if (strategy == null) return;

            if (!Shell.IsAdmin())
            {
                ShowError("Для запуска обхода нужны права администратора. Нажмите «Перезапустить от админа».");
                return;
            }

            IsBusy = true;
            ShowInfo("Запускаю обход…");
            try
            {
                var result = await Bypass.StartAsync(strategy, CurrentGameFilter(), Settings.ShowWinwsConsole);
                if (result.Ok)
                {
                    ShowSuccess(result.Message);
                    _main.Updates.LastKnownLatest = await SafeLatestAsync();
                }
                else ShowError(result.Message);
            }
            finally
            {
                IsBusy = false;
                RefreshStatus();
            }
        }

        private GameFilterMode CurrentGameFilter() => GameFilterIndex switch
        {
            1 => GameFilterMode.TcpAndUdp,
            2 => GameFilterMode.TcpOnly,
            3 => GameFilterMode.UdpOnly,
            _ => GameFilterMode.Disabled
        };

        private async Task StopAsync()
        {
            var confirmed = !Settings.ConfirmOnStop || Confirm("Остановить обход?", "Трафик перестанет обрабатываться, доступ к части сайтов может пропасть.");
            if (!confirmed) return;

            IsBusy = true;
            ShowInfo("Останавливаю обход…");
            try
            {
                var result = await Bypass.StopAsync();
                if (result.Ok) ShowSuccess(result.Message); else ShowError(result.Message);
            }
            finally
            {
                IsBusy = false;
                RefreshStatus();
            }
        }

        private async Task InstallServiceAsync()
        {
            var strategy = Current();
            if (strategy == null) return;

            if (!Shell.IsAdmin())
            {
                ShowError("Для установки службы нужны права администратора.");
                return;
            }

            IsBusy = true;
            ShowInfo("Устанавливаю службу zapret…");
            try
            {
                var result = await Bypass.InstallServiceAsync(strategy, CurrentGameFilter());
                if (result.Ok) ShowSuccess(result.Message); else ShowError(result.Message);
            }
            finally
            {
                IsBusy = false;
                RefreshStatus();
            }
        }

        private async Task RemoveServiceAsync()
        {
            IsBusy = true;
            ShowInfo("Удаляю службы zapret и WinDivert…");
            try
            {
                var result = await Bypass.RemoveServiceAsync();
                ShowSuccess(result.Message);
            }
            finally
            {
                IsBusy = false;
                RefreshStatus();
            }
        }

        private async Task TestConnectionAsync()
        {
            ConnectionBusy = true;
            ConnectionChecks.Clear();
            try
            {
                var results = await ConnectionTester.RunAsync();
                foreach (var check in results) ConnectionChecks.Add(check);

                var failed = results.Count(r => !r.Ok);
                if (failed == 0) ShowSuccess("Все проверенные ресурсы доступны");
                else if (failed == results.Count) ShowError("Ни один ресурс не открылся — проверьте обход и DNS");
                else ShowWarning($"Часть ресурсов недоступна ({failed} из {results.Count})");
            }
            finally
            {
                ConnectionBusy = false;
            }
        }

        private async Task<string> SafeLatestAsync()
        {
            try
            {
                var version = await EngineService.GetLatestVersionTextAsync();
                _latestVersion = version ?? "";
                Raise(nameof(LatestVersionText));
                Raise(nameof(UpdateAvailable));
                return _latestVersion;
            }
            catch { return ""; }
        }

        private static bool Confirm(string title, string text)
            => System.Windows.MessageBox.Show(text, title, System.Windows.MessageBoxButton.YesNo,
                   System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

        public void ShowInfo(string message) => SetMessage(message, "Info");
        public void ShowWarning(string message) => SetMessage(message, "Warning");
        public void ShowError(string message) => SetMessage(message, "Danger");
        public void ShowSuccess(string message) => SetMessage(message, "Success");

        private void SetMessage(string message, string key)
        {
            MessageKey = key;
            Message = message;
        }
    }
}
