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
        private double _connectionProgressValue;
        private double _connectionProgressMaximum = 1;
        private bool _connectionProgressIndeterminate = true;
        private string _connectionProgressPercentText = "";
        private string _connectionProgressText = "";
        private int _suppressWrites;
        private string _legacyWarning = "";
        private DateTime _lastLegacyCheck = DateTime.MinValue;
        private LegacyInstallInfo? _legacyCache;
        private string _newConnectionAddress = "";

        public HomeViewModel(MainViewModel main)
        {
            _main = main;
            MonitorTargetStore.EnsureDefaults(main.Settings);
            SettingsStore.Save(main.Settings);
            foreach (var target in main.Settings.MonitorTargets.Where(t => !t.IsGame))
                ConnectionTargets.Add(target);

            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && HasStrategy && !IsBusy);
            StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning && !IsBusy);
            ToggleBypassCommand = new AsyncRelayCommand(ToggleBypassAsync, () => HasStrategy && !IsBusy);
            ResolveLegacyCommand = new AsyncRelayCommand(ResolveLegacyFromBannerAsync, () => !IsBusy);
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => (HasStrategy || ServiceInstalled) && !IsBusy);
            ReinstallServiceCommand = new AsyncRelayCommand(ReinstallServiceAsync, () => HasStrategy && !IsBusy);
            RemoveServiceCommand = new AsyncRelayCommand(RemoveServiceAsync, () => ServiceInstalled && !IsBusy);
            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !ConnectionBusy);
            AddConnectionTargetCommand = new RelayCommand(AddConnectionTarget);
            EditConnectionTargetCommand = new RelayCommand(EditConnectionTarget, p => p is MonitorTarget target && !target.IsBuiltIn);
            RemoveConnectionTargetCommand = new RelayCommand(RemoveConnectionTarget, p => p is MonitorTarget target && !target.IsBuiltIn);
            CheckUpdatesCommand = new RelayCommand(() => _main.Navigate("updates"));
            OpenDiagnosticsCommand = new RelayCommand(() => _main.Navigate("diagnostics"));
            OpenFirstLaunchCommand = new RelayCommand(() => _main.Navigate("first-run"));
            OpenStrategiesCommand = new RelayCommand(() => _main.Navigate("strategies"));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            RestartAsAdminCommand = new RelayCommand(() => _main.RestartAsAdminCommand.Execute(null));
            ClearMessageCommand = new RelayCommand(() => Message = "");
            ToggleGameModeCommand = new RelayCommand(() => _main.ToggleGameMode());
            OpenOverlayCommand = new RelayCommand(() => _main.ToggleMiniOverlay());
            ApplyGamingTweaksCommand = new AsyncRelayCommand(ApplyGamingTweaksAsync);
            RevertGamingTweaksCommand = new AsyncRelayCommand(RevertGamingTweaksAsync);
            OpenDiscordVoiceFixCommand = new RelayCommand(() =>
            {
                _main.Navigate("diagnostics");
                _main.Diagnostics.SelectedSubTab = 5;
            });
            CleanDiscordAndNetworkCommand = new AsyncRelayCommand(async () =>
            {
                var summary = await DiscordNetworkCleaner.CleanAsync(new DiscordCleanOptions
                {
                    CloseDiscordProcesses = true,
                    ResetNetworkStack = true
                }).ConfigureAwait(true);

                if (summary.Ok) ShowSuccess($"✅ {summary.Message}");
                else ShowError(summary.Message);
            });
            RefreshGamingStatus();
        }

        public AppSettings Settings => _main.Settings;
        public BypassController Bypass => _main.Bypass;
        public StrategyStore Store => _main.Strategies;

        public bool IsGameRunning => _main.IsGameRunning;
        public string ActiveGameName => _main.ActiveGameName ?? "";
        public string GameStatusBadgeText => _main.GameStatusBadgeText;
        public bool GameModeActive => Settings.GameModeActive;
        public string GameModeStatusText => Settings.GameModeActive
            ? "Игровой режим ВКЛ (UDP порты исключены для минимального пинга)"
            : "Игровой режим ВЫКЛ (обычная фильтрация)";

        private string _gamingNetworkStatus = "";
        private bool _gamingNetworkOptimized;
        public string GamingNetworkStatusText
        {
            get => _gamingNetworkStatus;
            private set => Set(ref _gamingNetworkStatus, value);
        }
        public bool GamingNetworkIsOptimized
        {
            get => _gamingNetworkOptimized;
            private set => Set(ref _gamingNetworkOptimized, value);
        }

        public ICommand ToggleGameModeCommand { get; }
        public ICommand OpenOverlayCommand { get; }
        public ICommand ApplyGamingTweaksCommand { get; }
        public ICommand RevertGamingTweaksCommand { get; }
        public ICommand OpenDiscordVoiceFixCommand { get; }
        public ICommand CleanDiscordAndNetworkCommand { get; }

        public void RefreshGamingStatus()
        {
            try
            {
                var opt = GamingNetworkOptimizer.CheckStatus();
                GamingNetworkStatusText = opt.Summary;
                GamingNetworkIsOptimized = opt.IsOptimized;
            }
            catch
            {
                GamingNetworkStatusText = "Параметры сети по умолчанию";
                GamingNetworkIsOptimized = false;
            }

            Raise(nameof(IsGameRunning));
            Raise(nameof(ActiveGameName));
            Raise(nameof(GameStatusBadgeText));
            Raise(nameof(GameModeActive));
            Raise(nameof(GameModeStatusText));
        }

        private async Task ApplyGamingTweaksAsync()
        {
            if (!Shell.IsAdmin())
            {
                ShowError("Для изменения сетевых параметров Windows требуются права администратора.");
                return;
            }

            var (ok, msg) = await GamingNetworkOptimizer.ApplyTweaksAsync();
            if (ok) ShowSuccess(msg);
            else ShowError(msg);
            RefreshGamingStatus();
        }

        private async Task RevertGamingTweaksAsync()
        {
            if (!Shell.IsAdmin())
            {
                ShowError("Для изменения сетевых параметров Windows требуются права администратора.");
                return;
            }

            var (ok, msg) = await GamingNetworkOptimizer.RevertTweaksAsync();
            if (ok) ShowSuccess(msg);
            else ShowError(msg);
            RefreshGamingStatus();
        }

        public ObservableCollection<string> StrategyNames { get; } = new();
        public ObservableCollection<ConnectionCheck> ConnectionChecks { get; } = new();
        public ObservableCollection<MonitorTarget> ConnectionTargets { get; } = new();

        public string NewConnectionAddress
        {
            get => _newConnectionAddress;
            set => Set(ref _newConnectionAddress, value);
        }

        public string[] GameFilterOptions { get; } = { "Выключен", "TCP + UDP", "Только TCP", "Только UDP" };
        public string[] IpsetOptions { get; } = { "Списки (loaded)", "Отключён (none)", "Все IP (any)" };

        public bool IsRunning => _status.IsRunning;
        public BypassStatus CurrentStatus => _status;
        public string RunningStrategyName => _status.StrategyName;
        public bool ServiceInstalled => _status.ServiceState != ServiceState.NotInstalled;
        public string InstallServiceButtonText => ServiceInstalled
            ? "Служба уже установлена, удалить?"
            : "Установить в службу";
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

        public string ReadinessText => _main.ReadinessText;
        public string ReadinessDetails => _main.ReadinessDetails;
        public string ReadinessKey => _main.ReadinessKey;
        public bool ReadinessActionVisible => !_main.ReadinessIsReady || !_main.Settings.FirstLaunchWizardCompleted;

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

        /// <summary>Предупреждение о конфликте со старым запретом (баннер под статусом).</summary>
        public string LegacyWarningText => _legacyWarning;

        public bool LegacyWarningVisible => !string.IsNullOrWhiteSpace(_legacyWarning);

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

        public double ConnectionProgressValue
        {
            get => _connectionProgressValue;
            set => Set(ref _connectionProgressValue, value);
        }

        public double ConnectionProgressMaximum
        {
            get => _connectionProgressMaximum;
            set => Set(ref _connectionProgressMaximum, value);
        }

        public bool ConnectionProgressIndeterminate
        {
            get => _connectionProgressIndeterminate;
            set => Set(ref _connectionProgressIndeterminate, value);
        }

        public string ConnectionProgressPercentText
        {
            get => _connectionProgressPercentText;
            set => Set(ref _connectionProgressPercentText, value);
        }

        public string ConnectionProgressText
        {
            get => _connectionProgressText;
            set => Set(ref _connectionProgressText, value);
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
        public ICommand ToggleBypassCommand { get; }
        public ICommand ResolveLegacyCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand ReinstallServiceCommand { get; }
        public ICommand RemoveServiceCommand { get; }
        public ICommand TestConnectionCommand { get; }
        public ICommand AddConnectionTargetCommand { get; }
        public ICommand EditConnectionTargetCommand { get; }
        public ICommand RemoveConnectionTargetCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand OpenDiagnosticsCommand { get; }
        public ICommand OpenFirstLaunchCommand { get; }
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
            Raise(nameof(InstallServiceButtonText));
            Raise(nameof(StatusText));
            Raise(nameof(StatusKey));
            Raise(nameof(BypassStateKey));
            Raise(nameof(State));
            Raise(nameof(StrategyText));
            Raise(nameof(UptimeText));
            Raise(nameof(PidText));
            Raise(nameof(ServiceText));
            Raise(nameof(ReadinessText));
            Raise(nameof(ReadinessDetails));
            Raise(nameof(ReadinessKey));
            Raise(nameof(ReadinessActionVisible));
            Raise(nameof(UpdateAvailable));
            RefreshLegacyWarning();
            RaiseCommands();
        }

        private void RaiseCommands()
        {
            (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ToggleBypassCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ResolveLegacyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        /// <summary>
        /// Проверяет конфликт со старым запретом перед запуском: показывает диалог
        /// и выполняет выбранное действие. Возвращает стратегию для запуска или null (прервать).
        /// </summary>
        private async Task<StrategyInfo?> ResolveLegacyAsync()
        {
            if (!Settings.LegacyZapretDismissed)
            {
                LegacyInstallInfo info;
                try
                {
                    info = LegacyZapret.Detect(Settings);
                }
                catch
                {
                    return Current();
                }

                if (info.HasConflict)
                {
                    // Диалог — как MessageBox: ViewModel спрашивает, действия — ниже.
                    var dialog = new Views.LegacyZapretDialog(info);
                    var owner = System.Windows.Application.Current.MainWindow;
                    if (owner != null) dialog.Owner = owner;

                    var accepted = dialog.ShowDialog() == true;
                    var choice = accepted ? dialog.Choice : LegacyChoice.Later;
                    if (dialog.DontAskChecked) choice = LegacyChoice.DontAsk;

                    InvalidateLegacyCache();
                    switch (choice)
                    {
                        case LegacyChoice.TakeOver:
                            IsBusy = true;
                            try
                            {
                                ShowInfo("Останавливаю старый запрет…");
                                await LegacyZapret.StopLegacyAsync(info);
                            }
                            finally
                            {
                                IsBusy = false;
                            }
                            break;

                        case LegacyChoice.ImportAndTakeOver:
                            IsBusy = true;
                            try
                            {
                                ShowInfo("Копирую настройки старого запрета…");
                                var (ok, message, strategyName) = LegacyZapret.ImportUserData(
                                    info.ForeignRoot, Settings.EnginePath, info.StrategyName);
                                if (ok && !string.IsNullOrEmpty(strategyName))
                                {
                                    var imported = Store.Find(strategyName);
                                    if (imported != null)
                                    {
                                        Settings.SelectedStrategy = imported.Name;
                                        SettingsStore.Save(Settings);
                                        ReloadFromEngine();
                                    }
                                }
                                ShowInfo(message);
                                await LegacyZapret.StopLegacyAsync(info);
                            }
                            finally
                            {
                                IsBusy = false;
                            }
                            break;

                        case LegacyChoice.StopOnly:
                            IsBusy = true;
                            try
                            {
                                ShowInfo("Выключаю старый запрет…");
                                var stopped = await LegacyZapret.StopLegacyAsync(info);
                                ShowInfo(stopped.Message);
                            }
                            finally
                            {
                                IsBusy = false;
                            }
                            break;

                        case LegacyChoice.DontAsk:
                            Settings.LegacyZapretDismissed = true;
                            SettingsStore.Save(Settings);
                            return null;

                        default:
                            return null; // Later / окно закрыто — запуск прерываем
                    }
                    RefreshStatus();
                }
            }
            return Current();
        }

        private void RefreshLegacyWarning()
        {
            // Детект не чаще раза в 30 секунд (там опрос служб и процессов)
            if ((DateTime.Now - _lastLegacyCheck).TotalSeconds < 30 && _legacyCache != null)
            {
                ApplyLegacyCache();
                return;
            }
            _lastLegacyCheck = DateTime.Now;
            try
            {
                _legacyCache = Settings.LegacyZapretDismissed ? null : LegacyZapret.Detect(Settings);
            }
            catch
            {
                _legacyCache = null;
            }
            ApplyLegacyCache();
        }

        private void ApplyLegacyCache()
        {
            var conflict = _legacyCache is { HasConflict: true };
            _legacyWarning = conflict
                ? "В фоне работает старый запрет из другой папки — он конфликтует с этим приложением."
                : "";
            Raise(nameof(LegacyWarningText));
            Raise(nameof(LegacyWarningVisible));
        }

        private void InvalidateLegacyCache()
        {
            _lastLegacyCheck = DateTime.MinValue;
            _legacyCache = null;
        }

        public async Task ToggleBypassAsync()
        {
            if (IsRunning) await StopAsync();
            else await StartAsync();
        }

        private async Task ResolveLegacyFromBannerAsync()
        {
            var strategy = await ResolveLegacyAsync();
            if (strategy == null) return;
            // Конфликт решён выбором пользователя — запускаем обход через GUI
            await StartAsync();
        }

        private async Task StartAsync()
        {
            var strategy = await ResolveLegacyAsync();
            if (strategy == null) return;

            if (!Shell.IsAdmin())
            {
                ShowError("Для запуска обхода нужны права администратора. Нажмите «Перезапустить от админа».");
                return;
            }

            if (!Confirm("Запуск обхода", $"Будет запущен winws.exe со стратегией «{strategy.Name}». Это изменит обработку сетевого трафика и может потребовать WinDivert. Запустить вручную сейчас?"))
                return;

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
            if (ServiceInstalled)
            {
                await RemoveServiceAsync();
                return;
            }

            var strategy = await ResolveLegacyAsync();
            if (strategy == null) return;

            if (!Shell.IsAdmin())
            {
                ShowError("Для установки службы нужны права администратора.");
                return;
            }

            if (!Confirm("Установка службы", $"Будет создана и запущена служба zapret со стратегией «{strategy.Name}». Это изменит системную службу, WinDivert и TCP timestamps. Продолжить?"))
                return;

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

        private async Task ReinstallServiceAsync()
        {
            var strategy = await ResolveLegacyAsync();
            if (strategy == null) return;

            if (!Shell.IsAdmin())
            {
                ShowError("Для переустановки службы нужны права администратора.");
                return;
            }

            if (!Confirm("Переустановка службы", $"Служба zapret будет переустановлена со стратегией «{strategy.Name}». Продолжить?"))
                return;

            IsBusy = true;
            ShowInfo("Переустанавливаю службу zapret…");
            try
            {
                await Bypass.RemoveServiceAsync();
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
            if (!Confirm("Удаление службы", "Будут остановлены обход и служба zapret, а также удалены связанные службы WinDivert. Продолжить?"))
                return;

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

        private void UpdateConnectionProgress(string text)
        {
            if (text.StartsWith("CONNECTION_TOTAL:", StringComparison.Ordinal))
            {
                if (int.TryParse(text.Substring("CONNECTION_TOTAL:".Length), out var total))
                {
                    ConnectionProgressMaximum = Math.Max(1, total);
                    ConnectionProgressValue = 0;
                    ConnectionProgressIndeterminate = false;
                    ConnectionProgressPercentText = "0%";
                }
                ConnectionProgressText = "Подготовлены контрольные ресурсы";
                return;
            }

            if (text.StartsWith("CONNECTION_PROGRESS:", StringComparison.Ordinal))
            {
                var parts = text.Substring("CONNECTION_PROGRESS:".Length).Split(" — ", 2);
                var numbers = parts[0].Split('/');
                if (numbers.Length == 2 && int.TryParse(numbers[0], out var current) && int.TryParse(numbers[1], out var total))
                {
                    ConnectionProgressValue = current;
                    ConnectionProgressMaximum = Math.Max(1, total);
                    ConnectionProgressIndeterminate = false;
                    ConnectionProgressPercentText = $"{ConnectionProgressValue / ConnectionProgressMaximum * 100:0}%";
                }
                ConnectionProgressText = parts.Length > 1 ? "Завершён: " + parts[1] : "Проверка завершена";
                return;
            }

            ConnectionProgressText = text;
        }

        private async Task TestConnectionAsync()
        {
            ConnectionBusy = true;
            ConnectionProgressValue = 0;
            ConnectionProgressMaximum = 1;
            ConnectionProgressIndeterminate = true;
            ConnectionProgressPercentText = "";
            ConnectionProgressText = "Подготавливаю проверку…";
            ConnectionChecks.Clear();
            try
            {
                var results = await ConnectionTester.RunAsync(ConnectionTargets, default,
                    new Progress<string>(UpdateConnectionProgress));
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

        private void AddConnectionTarget()
        {
            var dialog = new Views.InputDialog(
                "Добавить адрес",
                "Введите URL или домен для проверки соединения:")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;
            NewConnectionAddress = dialog.Value;

            if (!MonitorTarget.TryCreate(NewConnectionAddress, null, out var target, out var error) || target == null)
            {
                ShowWarning(error);
                return;
            }
            if (ConnectionTargets.Any(t => t.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)))
            {
                ShowWarning("Этот адрес уже есть в проверке соединения");
                return;
            }
            Settings.MonitorTargets.Add(target);
            ConnectionTargets.Add(target);
            SettingsStore.Save(Settings);
            NewConnectionAddress = "";
            ShowSuccess("Адрес добавлен в проверку соединения");
        }

        private void EditConnectionTarget(object? parameter)
        {
            if (parameter is not MonitorTarget target || target.IsBuiltIn) return;
            var dialog = new Views.InputDialog(
                "Изменить адрес",
                "Укажите новый URL или домен для проверки:",
                target.Url)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;
            var updated = (dialog.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(updated)) return;

            if (!MonitorTarget.TryCreate(updated, target.Name, out var newTarget, out var error) || newTarget == null)
            {
                ShowWarning(error);
                return;
            }

            var index = ConnectionTargets.IndexOf(target);
            if (index >= 0)
            {
                ConnectionTargets[index] = newTarget;
                var sIndex = Settings.MonitorTargets.FindIndex(t => t.Id == target.Id || t.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase));
                if (sIndex >= 0) Settings.MonitorTargets[sIndex] = newTarget;
                SettingsStore.Save(Settings);
                ShowSuccess("Адрес проверки обновлён");
            }
        }

        private void RemoveConnectionTarget(object? parameter)
        {
            if (parameter is not MonitorTarget target || target.IsBuiltIn) return;
            Settings.MonitorTargets.Remove(target);
            ConnectionTargets.Remove(target);
            SettingsStore.Save(Settings);
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

        public void RefreshTheme()
        {
            var checks = ConnectionChecks.ToList();
            ConnectionChecks.Clear();
            foreach (var check in checks) ConnectionChecks.Add(check);
            Raise(nameof(MessageKey));
            Raise(nameof(StatusKey));
            Raise(nameof(BypassStateKey));
        }

        private void SetMessage(string message, string key)
        {
            MessageKey = key;
            Message = message;
        }
    }
}
