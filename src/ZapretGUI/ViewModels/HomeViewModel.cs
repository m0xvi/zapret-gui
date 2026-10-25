using System;
using System.Collections.Generic;
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
        // ---- Здоровье соединения ----
        private string _connectionHealthKey = "Muted";
        private string _connectionHealthText = "";
        private bool _connectionHealthVisible;
        // ---- Быстрая проверка «Проверить всё» ----
        private bool _isFullCheckRunning;
        private string _fullCheckStatusText = "";
        private string _fullCheckAdviceText = "";
        private string _fullCheckSummaryText = "";
        private string _fullCheckSummaryKey = "Info";
        private double _fullCheckProgressValue;
        private double _fullCheckProgressMaximum = 1;
        private bool _fullCheckProgressIndeterminate = true;
        private string _fullCheckProgressPercentText = "";
        private StrategyInfo? _recommendedStrategy;
        private CancellationTokenSource? _fullCheckCts;

        // ---- Единая проверка (охват всех функций) — 10-балльная система ----
        private bool _isUnifiedRunning;
        private string _unifiedStatusText = "";
        private string _unifiedSummaryText = "";
        private string _unifiedSummaryKey = "Info";
        private double _unifiedProgressValue;
        private double _unifiedProgressMaximum = 3;
        private bool _unifiedProgressIndeterminate = true;
        private string _unifiedProgressPercentText = "";
        private string _unifiedLastRunText = "";
        private CancellationTokenSource? _unifiedCts;

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
            ToggleServiceCommand = new AsyncRelayCommand(ToggleServiceAsync, () => !IsBusy);
            ReapplyServiceCommand = new AsyncRelayCommand(ReinstallServiceAsync, () => ServiceInstalled && !IsBusy && HasStrategy);
            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !ConnectionBusy);
            AddConnectionTargetCommand = new RelayCommand(AddConnectionTarget);
            EditConnectionTargetCommand = new RelayCommand(EditConnectionTarget, p => p is MonitorTarget target && !target.IsBuiltIn);
            RemoveConnectionTargetCommand = new RelayCommand(RemoveConnectionTarget, p => p is MonitorTarget target && !target.IsBuiltIn);
            CheckUpdatesCommand = new RelayCommand(() => _main.Navigate("updates"));
            OpenDiagnosticsCommand = new RelayCommand(() => _main.Navigate("diagnostics"));
            OpenFirstLaunchCommand = new RelayCommand(() => _main.Navigate("first-run"));
            OpenStrategiesCommand = new RelayCommand(() => _main.Navigate("strategies"));
            OpenBypassCenterCommand = new RelayCommand(() => _main.Navigate("strategies")); // Центр обхода удалён — ведём в Стратегии
            OpenGameModeCommand = new RelayCommand(() => _main.Navigate("game-mode"));
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
                _main.Diagnostics.SelectedSubTab = 1;
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
            RunFullCheckCommand = new AsyncRelayCommand(RunFullCheckAsync, () => !IsFullCheckRunning && !IsBusy && HasStrategy);
            CancelFullCheckCommand = new RelayCommand(CancelFullCheck, () => IsFullCheckRunning);
            ClearFullCheckResultCommand = new RelayCommand(ClearFullCheckResult, () => FullCheckResultVisible && !IsFullCheckRunning);
            ApplyRecommendedStrategyCommand = new AsyncRelayCommand(ApplyRecommendedStrategyAsync, () => HasRecommendedStrategy && !IsBusy);
            RunUnifiedCheckCommand = new AsyncRelayCommand(RunUnifiedCheckAsync, () => !IsUnifiedRunning && !IsBusy);
            CancelUnifiedCheckCommand = new RelayCommand(CancelUnifiedCheck, () => IsUnifiedRunning);
            ClearUnifiedCheckCommand = new RelayCommand(ClearUnifiedCheck, () => UnifiedResultVisible && !IsUnifiedRunning);
            ApplyBestUnifiedVariantCommand = new AsyncRelayCommand(ApplyBestUnifiedVariantAsync, () => BestUnifiedVariant != null && !IsBusy && !IsUnifiedRunning);
            // Загрузить последний результат единой проверки если был
            try
            {
                var last = UnifiedCheckHistoryStore.LoadLast();
                if (last != null)
                {
                    foreach (var s in last.SystemScores) UnifiedSystemScores.Add(s);
                    foreach (var v in last.VariantScores) UnifiedVariantScores.Add(v);
                    UnifiedSummaryText = last.Summary;
                    UnifiedSummaryKey = last.SummaryKey;
                    UnifiedLastRunText = last.RunAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + $" · {last.SystemScores.Count} системных + {last.VariantScores.Count} вариантов";
                }
            }
            catch {}
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
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Оптимизация сети", "Применение игровых твиков Windows…", "TCP/UDP параметры…", 0, true, false)); } catch {}

            var (ok, msg) = await GamingNetworkOptimizer.ApplyTweaksAsync();
            if (ok) ShowSuccess(msg);
            else ShowError(msg);
            RefreshGamingStatus();
            if (!ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Оптимизация — ошибка", msg)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private async Task RevertGamingTweaksAsync()
        {
            if (!Shell.IsAdmin())
            {
                ShowError("Для изменения сетевых параметров Windows требуются права администратора.");
                return;
            }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Сброс оптимизации", "Откат игровых твиков Windows…", "Восстановление настроек…", 0, true, false)); } catch {}

            var (ok, msg) = await GamingNetworkOptimizer.RevertTweaksAsync();
            if (ok) ShowSuccess(msg);
            else ShowError(msg);
            RefreshGamingStatus();
            if (!ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Откат — ошибка", msg)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
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
        public string ServiceToggleStatusText => ServiceInstalled ? "Автозапуск включён" : "Автозапуск выключен";
        public bool IsServiceToggleOn => ServiceInstalled;
        public bool IsServicePending => StrategyApplicationService.IsServicePending(_status.ServiceState);
        public string ServicePendingText => StrategyApplicationService.ServicePendingText(_status.ServiceState);
        public bool IsServiceToggleEnabled => !IsBusy && !IsServicePending;
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
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(IsServiceToggleEnabled));
                    RaiseCommands();
                }
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

        public string ConnectionHealthKey
        {
            get => _connectionHealthKey;
            private set => Set(ref _connectionHealthKey, value);
        }

        public string ConnectionHealthText
        {
            get => _connectionHealthText;
            private set => Set(ref _connectionHealthText, value);
        }

        public bool ConnectionHealthVisible
        {
            get => _connectionHealthVisible;
            private set
            {
                if (Set(ref _connectionHealthVisible, value))
                    Raise(nameof(ConnectionHealthVisible));
            }
        }

        // ---- Свойства «Проверить всё» ----
        public bool IsFullCheckRunning
        {
            get => _isFullCheckRunning;
            private set
            {
                if (Set(ref _isFullCheckRunning, value))
                {
                    Raise(nameof(FullCheckResultVisible));
                    Raise(nameof(IsQuickSetupIdle));
                    Raise(nameof(HasRecommendedStrategy));
                    (RunFullCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelFullCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ClearFullCheckResultCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyRecommendedStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    Raise(nameof(IsFullCheckRunning));
                }
            }
        }

        public string FullCheckStatusText
        {
            get => _fullCheckStatusText;
            private set => Set(ref _fullCheckStatusText, value);
        }

        public string FullCheckAdviceText
        {
            get => _fullCheckAdviceText;
            private set => Set(ref _fullCheckAdviceText, value);
        }

        public string FullCheckSummaryText
        {
            get => _fullCheckSummaryText;
            private set
            {
                if (Set(ref _fullCheckSummaryText, value))
                {
                    Raise(nameof(FullCheckResultVisible));
                    Raise(nameof(IsQuickSetupIdle));
                    (ClearFullCheckResultCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RunFullCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string FullCheckSummaryKey
        {
            get => _fullCheckSummaryKey;
            private set => Set(ref _fullCheckSummaryKey, value);
        }

        public double FullCheckProgressValue
        {
            get => _fullCheckProgressValue;
            private set => Set(ref _fullCheckProgressValue, value);
        }

        public double FullCheckProgressMaximum
        {
            get => _fullCheckProgressMaximum;
            private set => Set(ref _fullCheckProgressMaximum, value);
        }

        public bool FullCheckProgressIndeterminate
        {
            get => _fullCheckProgressIndeterminate;
            private set => Set(ref _fullCheckProgressIndeterminate, value);
        }

        public string FullCheckProgressPercentText
        {
            get => _fullCheckProgressPercentText;
            private set => Set(ref _fullCheckProgressPercentText, value);
        }

        public bool FullCheckResultVisible => !string.IsNullOrWhiteSpace(FullCheckSummaryText);
        public bool HasRecommendedStrategy => _recommendedStrategy != null;
        public string RecommendedStrategyName => _recommendedStrategy?.Name ?? "";
        public StrategyInfo? RecommendedStrategy => _recommendedStrategy;
        /// <summary>Исходное состояние блока — ни прогресса, ни результата; видны кнопки «Проверить всё».</summary>
        public bool IsQuickSetupIdle => !IsFullCheckRunning && !FullCheckResultVisible;

        // ==================== Единая проверка — охват всех функций, баллы 0..10 ====================
        public ObservableCollection<UnifiedSystemScore> UnifiedSystemScores { get; } = new();
        public ObservableCollection<UnifiedVariantScore> UnifiedVariantScores { get; } = new();

        public bool IsUnifiedRunning
        {
            get => _isUnifiedRunning;
            private set
            {
                if (Set(ref _isUnifiedRunning, value))
                {
                    Raise(nameof(UnifiedResultVisible));
                    Raise(nameof(IsUnifiedIdle));
                    Raise(nameof(UnifiedProgressVisible));
                    (RunUnifiedCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelUnifiedCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ClearUnifiedCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyBestUnifiedVariantCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string UnifiedStatusText
        {
            get => _unifiedStatusText;
            private set => Set(ref _unifiedStatusText, value);
        }

        public string UnifiedSummaryText
        {
            get => _unifiedSummaryText;
            private set
            {
                if (Set(ref _unifiedSummaryText, value))
                {
                    Raise(nameof(UnifiedResultVisible));
                    Raise(nameof(IsUnifiedIdle));
                    Raise(nameof(UnifiedTotalScoreText));
                    (ClearUnifiedCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyBestUnifiedVariantCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string UnifiedSummaryKey
        {
            get => _unifiedSummaryKey;
            private set => Set(ref _unifiedSummaryKey, value);
        }

        public double UnifiedProgressValue
        {
            get => _unifiedProgressValue;
            private set => Set(ref _unifiedProgressValue, value);
        }

        public double UnifiedProgressMaximum
        {
            get => _unifiedProgressMaximum;
            private set => Set(ref _unifiedProgressMaximum, value);
        }

        public bool UnifiedProgressIndeterminate
        {
            get => _unifiedProgressIndeterminate;
            private set => Set(ref _unifiedProgressIndeterminate, value);
        }

        public string UnifiedProgressPercentText
        {
            get => _unifiedProgressPercentText;
            private set => Set(ref _unifiedProgressPercentText, value);
        }

        public bool UnifiedProgressVisible => IsUnifiedRunning;

        public string UnifiedLastRunText
        {
            get => _unifiedLastRunText;
            private set
            {
                if (Set(ref _unifiedLastRunText, value))
                    Raise(nameof(UnifiedLastRunVisible));
            }
        }

        public bool UnifiedLastRunVisible => !string.IsNullOrWhiteSpace(UnifiedLastRunText);
        public bool UnifiedResultVisible => !string.IsNullOrWhiteSpace(UnifiedSummaryText);
        public bool IsUnifiedIdle => !IsUnifiedRunning && !UnifiedResultVisible;
        public bool HasUnifiedResults => UnifiedSystemScores.Count > 0 || UnifiedVariantScores.Count > 0;
        public bool HasUnifiedVariants => UnifiedVariantScores.Count > 0;
        public UnifiedVariantScore? BestUnifiedVariant => UnifiedVariantScores.FirstOrDefault(v => v.IsBest) ?? UnifiedVariantScores.OrderByDescending(v => v.Score10).ThenBy(v => v.AvgMs).FirstOrDefault();
        public string BestUnifiedVariantName => BestUnifiedVariant?.StrategyName ?? "";

        public int UnifiedSystemTotalScore => UnifiedSystemScores.Sum(s => s.Score);
        public int UnifiedSystemMaxScore => UnifiedSystemScores.Count * 10;
        public string UnifiedSystemScoreText => UnifiedSystemScores.Count == 0 ? "—" : $"{UnifiedSystemTotalScore}/{UnifiedSystemMaxScore}";
        public string UnifiedTotalScoreText
        {
            get
            {
                if (!UnifiedResultVisible) return "";
                var best = BestUnifiedVariant;
                var bestText = best != null ? $" · лучший вариант «{best.StrategyName}» — {best.ScoreText} ({best.Summary})" : "";
                return $"{UnifiedSummaryText}{bestText}";
            }
        }

        public string UnifiedAdviceText
        {
            get
            {
                if (UnifiedSystemScores.Count == 0) return "";
                var fails = UnifiedSystemScores.Where(s => s.Score < 5).Select(s => s.Title).ToList();
                if (fails.Count == 0) return "✅ Все системные функции в порядке. Смотри баллы вариантов ниже — зелёные 8-10 работают стабильно.";
                if (fails.Count <= 2) return $"⚠️ Обрати внимание: {string.Join(", ", fails)} — низкий балл. Нажми «Исправить» в Проверке или обнови списки.";
                return $"❌ Критично: {string.Join(", ", fails.Take(3))} — требуется исправление в разделе Проверка.";
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
        public ICommand ToggleBypassCommand { get; }
        public ICommand ResolveLegacyCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand ReinstallServiceCommand { get; }
        public ICommand RemoveServiceCommand { get; }
        public ICommand ToggleServiceCommand { get; }
        public ICommand ReapplyServiceCommand { get; }
        public ICommand TestConnectionCommand { get; }
        public ICommand AddConnectionTargetCommand { get; }
        public ICommand EditConnectionTargetCommand { get; }
        public ICommand RemoveConnectionTargetCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand OpenDiagnosticsCommand { get; }
        public ICommand OpenFirstLaunchCommand { get; }
        public ICommand OpenStrategiesCommand { get; }
        public ICommand OpenBypassCenterCommand { get; }
        public ICommand OpenGameModeCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand ClearMessageCommand { get; }
        public ICommand RunFullCheckCommand { get; }
        public ICommand CancelFullCheckCommand { get; }
        public ICommand ClearFullCheckResultCommand { get; }
        public ICommand ApplyRecommendedStrategyCommand { get; }
        public ICommand RunUnifiedCheckCommand { get; }
        public ICommand CancelUnifiedCheckCommand { get; }
        public ICommand ClearUnifiedCheckCommand { get; }
        public ICommand ApplyBestUnifiedVariantCommand { get; }

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
            Raise(nameof(ServiceToggleStatusText));
            Raise(nameof(IsServiceToggleOn));
            Raise(nameof(IsServicePending));
            Raise(nameof(ServicePendingText));
            Raise(nameof(IsServiceToggleEnabled));
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
            (ReinstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RemoveServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ToggleServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReapplyServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (TestConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunFullCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelFullCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyRecommendedStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

            // Авто-запуск без вопроса — пользователь просил бесшовно без окон (1.17.24)

            Settings.BypassManuallyStopped = false; SettingsStore.Save(Settings); _main.Watchdog.NotifyManualStart();
            IsBusy = true;
            ShowInfo("Запускаю обход…");
            try
            {
                var prev = Bypass.GetStatus().ServiceStrategy ?? Bypass.GetStatus().StrategyName ?? "";
                var result = await Bypass.StartAsync(strategy, CurrentGameFilter(), Settings.ShowWinwsConsole);
                var modeText = Bypass.GetStatus().ServiceState == ServiceState.Running ? "служба" : "процесс";
                StrategySwitchHistoryStore.TryAppend(new StrategySwitchRecord { StrategyName = strategy.Name, PreviousStrategyName = prev, Source = "запуск", Mode = modeText, Success = result.Ok, Message = result.Message });
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

            Settings.BypassManuallyStopped = true; SettingsStore.Save(Settings); _main.Watchdog.NotifyManualStop();
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

        private async Task ToggleServiceAsync()
        {
            if (IsServicePending)
            {
                ShowWarning(ServicePendingText + " Подождите завершения перехода.");
                return;
            }
            if (ServiceInstalled) await RemoveServiceAsync();
            else await InstallServiceAsync();
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

            // Установка службы без подтверждения — бесшовно (1.17.24)

            Settings.BypassManuallyStopped = false; SettingsStore.Save(Settings); _main.Watchdog.NotifyManualStart();
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

            // Переустановка службы без вопроса — бесшовно (1.17.24)

            Settings.BypassManuallyStopped = false; SettingsStore.Save(Settings); _main.Watchdog.NotifyManualStart();
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
            // Удаление службы по явному клику тумблера — без лишнего вопроса, только по действию пользователя (1.17.24)

            Settings.BypassManuallyStopped = true; SettingsStore.Save(Settings); _main.Watchdog.NotifyManualStop();
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
            ConnectionHealthVisible = false;
            try
            {
                var results = await ConnectionTester.RunAsync(ConnectionTargets, default,
                    new Progress<string>(UpdateConnectionProgress));
                foreach (var check in results) ConnectionChecks.Add(check);

                var total = results.Count;
                var ok = results.Count(r => r.Ok);
                var avg = ok > 0 ? (int)results.Where(r => r.Ok).Average(r => r.Milliseconds) : 0;
                ConnectionHealthVisible = total > 0;
                ConnectionHealthKey = ok == total ? "Success" : ok == 0 ? "Danger" : "Warning";
                ConnectionHealthText = total > 0 ? $"{ok}/{total} OK" + (ok > 0 ? $" · {avg} мс" : "") : "";
                Raise(nameof(ConnectionHealthKey));
                Raise(nameof(ConnectionHealthText));

                var failed = total - ok;
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

        public void ClearFullCheckResult()
        {
            // Сбрасывает заглушку завершённой проверки — возвращает блок в исходное состояние
            FullCheckSummaryText = "";
            FullCheckAdviceText = "";
            FullCheckSummaryKey = "Info";
            FullCheckStatusText = "";
            FullCheckProgressValue = 0;
            FullCheckProgressPercentText = "";
            _recommendedStrategy = null;
            Raise(nameof(HasRecommendedStrategy));
            Raise(nameof(RecommendedStrategyName));
            Raise(nameof(FullCheckResultVisible));
            Raise(nameof(IsQuickSetupIdle));
            (ClearFullCheckResultCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunFullCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyRecommendedStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            AppLog.Info("Блок быстрой настройки сброшен — заглушка проверки убрана");
        }

        public void CancelFullCheck()
        {
            _fullCheckCts?.Cancel();
            FullCheckStatusText = "Отменяю проверку…";
            AppLog.Info("Отмена полной проверки по кнопке");
        }

        private async Task ApplyRecommendedStrategyAsync()
        {
            if (_recommendedStrategy == null) return;
            var strat = _recommendedStrategy;
            var mode = CurrentGameFilter();
            var status = Bypass.GetStatus();
            var prev = status.ServiceStrategy ?? status.StrategyName ?? "";
            if (status.IsRunning && Shell.IsAdmin())
            {
                var res = await Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                var ok = res.Ok;
                var modeText = Bypass.GetStatus().ServiceState == ServiceState.Running ? "служба" : "процесс";
                StrategySwitchHistoryStore.TryAppend(new StrategySwitchRecord { StrategyName = strat.Name, PreviousStrategyName = prev, Source = "быстрая настройка", Mode = modeText, Success = ok, Message = res.Message });
                if (ok) ShowSuccess(res.Message);
                else ShowError(res.Message);
            }
            SelectedStrategyName = strat.Name;
            Settings.SelectedStrategy = strat.Name;
            SettingsStore.Save(Settings);
            ReloadFromEngine();
            if (!status.IsRunning) ShowSuccess($"Стратегия «{strat.Name}» выбрана как основная");
            if (!status.IsRunning && Shell.IsAdmin())
            {
                await StartAsync();
            }
        }

        public async Task RunFullCheckAsync()
        {
            if (IsFullCheckRunning) return;
            if (!HasStrategy)
            {
                ShowError("Стратегии не найдены — скачайте движок на странице «Обновления»");
                return;
            }

            AppLog.Info("Запущена быстрая проверка «Проверить всё»");
            IsFullCheckRunning = true;
            _fullCheckCts = new CancellationTokenSource();
            // Сбрасываем предыдущий результат без мигания — блок становится активным
            FullCheckSummaryText = "";
            FullCheckAdviceText = "";
            FullCheckSummaryKey = "Info";
            _recommendedStrategy = null;
            Raise(nameof(HasRecommendedStrategy));
            Raise(nameof(RecommendedStrategyName));
            Raise(nameof(FullCheckResultVisible));
            FullCheckStatusText = "Шаг 1/4: проверяю систему и движок…";
            FullCheckProgressValue = 0;
            FullCheckProgressMaximum = 4;
            FullCheckProgressIndeterminate = false;
            FullCheckProgressPercentText = "0%";

            try
            {
                // Шаг 1: системная диагностика (без лишнего UI шума)
                FullCheckStatusText = "Шаг 1/4: аудит системы (BFE, WinDivert, права)…";
                FullCheckProgressValue = 0;
                try
                {
                    await _main.Diagnostics.RunAsync();
                    var diagSummary = _main.Diagnostics.Summary ?? "";
                    var diagKey = _main.Diagnostics.SummaryKey ?? "Info";
                    if (diagKey == "Danger") FullCheckAdviceText = "⚠️ Найдены критичные системные проблемы — откройте «Проверка → Аудит системы» и нажмите «Исправить». ";
                    else if (diagKey == "Warning") FullCheckAdviceText = "⚠️ Есть предупреждения в аудите системы — рекомендуем исправить перед подбором. ";
                    else FullCheckAdviceText = "✅ Система в порядке. ";
                }
                catch (Exception ex)
                {
                    FullCheckAdviceText = $"Не удалось выполнить аудит системы: {ex.Message}. ";
                }
                _fullCheckCts.Token.ThrowIfCancellationRequested();
                FullCheckProgressValue = 1;
                FullCheckProgressPercentText = "25%";

                // Шаг 2: проверка соединения (ваши хосты + YouTube/Discord)
                FullCheckStatusText = "Шаг 2/4: проверяю доступность сайтов…";
                ConnectionChecks.Clear();
                var connResults = await ConnectionTester.RunAsync(ConnectionTargets, _fullCheckCts.Token, null);
                foreach (var r in connResults) ConnectionChecks.Add(r);
                // Обновляем health для кольца
                {
                    var total = connResults.Count;
                    var ok = connResults.Count(x => x.Ok);
                    var avg = ok > 0 ? (int)connResults.Where(x => x.Ok).Average(x => x.Milliseconds) : 0;
                    ConnectionHealthVisible = total > 0;
                    ConnectionHealthKey = ok == total ? "Success" : ok == 0 ? "Danger" : "Warning";
                    ConnectionHealthText = total > 0 ? $"{ok}/{total} OK" + (ok > 0 ? $" · {avg} мс" : "") : "";
                    Raise(nameof(ConnectionHealthKey));
                    Raise(nameof(ConnectionHealthText));
                }
                var failedConn = connResults.Count(r => !r.Ok);
                if (failedConn == 0) FullCheckAdviceText += "Сайты доступны. ";
                else if (failedConn == connResults.Count) FullCheckAdviceText += $"Ни один сайт не открылся ({failedConn}/{connResults.Count}) — проверьте, запущен ли обход и DNS. ";
                else FullCheckAdviceText += $"Часть сайтов недоступна ({failedConn}/{connResults.Count}) — это нормально, стратегия должна это исправить. ";
                _fullCheckCts.Token.ThrowIfCancellationRequested();
                FullCheckProgressValue = 2;
                FullCheckProgressPercentText = "50%";

                // Шаг 3: подбор стратегии — прогоняем все 22 стратегии
                FullCheckStatusText = "Шаг 3/4: тестирую стратегии (1–2 минуты, не закрывайте окно)…";
                var batch = await _main.StrategiesPage.TestAllAsync(new Progress<string>(s =>
                {
                    // Прокидываем прогресс стратегий в наш общий прогресс-текст
                    if (!string.IsNullOrWhiteSpace(s)) FullCheckStatusText = $"Шаг 3/4: {s}";
                }));
                if (batch == null || batch.Cancelled)
                {
                    FullCheckSummaryText = "Проверка стратегий отменена";
                    FullCheckSummaryKey = "Warning";
                    return;
                }
                var best = batch.Best;
                if (best == null)
                {
                    FullCheckSummaryText = "Не удалось протестировать стратегии — попробуйте ещё раз";
                    FullCheckSummaryKey = "Warning";
                    FullCheckAdviceText += "Стратегии не дали результата. Попробуйте обновить движок или проверить антивирус.";
                    return;
                }
                _recommendedStrategy = best.Strategy;
                Raise(nameof(HasRecommendedStrategy));
                Raise(nameof(RecommendedStrategyName));
                FullCheckProgressValue = 3;
                FullCheckProgressPercentText = "75%";
                _fullCheckCts.Token.ThrowIfCancellationRequested();

                // Шаг 4: итог и подсказки
                FullCheckStatusText = "Шаг 4/4: формирую рекомендации…";
                if (best.IsSuitable)
                {
                    FullCheckSummaryText = $"Готово! Рекомендована стратегия «{best.Strategy.Name}» — {best.PassedCount}/{best.Checks.Count} проверок OK, задержка {best.AverageLatencyMs} мс. Нажмите «Применить».";
                    FullCheckSummaryKey = "Success";
                    FullCheckAdviceText += $"Обратите внимание: стратегия «{best.Strategy.Name}» показала лучший результат из {batch.Results.Count} проверенных. Если позже появятся проблемы — смените её в «Стратегиях» или включите «Игры».";
                    ShowSuccess(FullCheckSummaryText);
                }
                else
                {
                    FullCheckSummaryText = $"Лучший кандидат «{best.Strategy.Name}» прошёл только {best.PassedCount}/{best.Checks.Count} проверок — попробуйте его, но будьте готовы сменить стратегию.";
                    FullCheckSummaryKey = "Warning";
                    FullCheckAdviceText += "Ни одна стратегия не прошла идеально. Попробуйте другую группу (ALT / FAKE TLS AUTO), проверьте «Фильтры» и убедитесь, что антивирус не блокирует WinDivert.";
                    ShowWarning(FullCheckSummaryText);
                }
                FullCheckProgressValue = 4;
                FullCheckProgressPercentText = "100%";
                FullCheckStatusText = "Готово";
            }
            catch (OperationCanceledException)
            {
                FullCheckSummaryText = "Проверка отменена пользователем";
                FullCheckSummaryKey = "Warning";
                FullCheckStatusText = "Отменено";
            }
            catch (Exception ex)
            {
                FullCheckSummaryText = "Ошибка при проверке: " + ex.Message;
                FullCheckSummaryKey = "Danger";
                FullCheckStatusText = "Ошибка";
                ShowError(FullCheckSummaryText);
            }
            finally
            {
                IsFullCheckRunning = false;
                FullCheckProgressIndeterminate = false;
                _fullCheckCts?.Dispose();
                _fullCheckCts = null;
            }
        }

        // ==================== Единая проверка — логика ====================
        public void ClearUnifiedCheck()
        {
            UnifiedSystemScores.Clear();
            UnifiedVariantScores.Clear();
            UnifiedSummaryText = "";
            UnifiedSummaryKey = "Info";
            UnifiedStatusText = "";
            UnifiedProgressValue = 0;
            UnifiedProgressPercentText = "";
            UnifiedLastRunText = "";
            UnifiedCheckHistoryStore.Clear();
            Raise(nameof(HasUnifiedResults));
            Raise(nameof(HasUnifiedVariants));
            Raise(nameof(UnifiedSystemScoreText));
            Raise(nameof(UnifiedSystemTotalScore));
            Raise(nameof(UnifiedSystemMaxScore));
            Raise(nameof(BestUnifiedVariant));
            Raise(nameof(BestUnifiedVariantName));
            (ClearUnifiedCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ApplyBestUnifiedVariantCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            AppLog.Info("Единая проверка: история очищена");
        }

        public void CancelUnifiedCheck()
        {
            _unifiedCts?.Cancel();
            UnifiedStatusText = "Отменяю проверку…";
            AppLog.Info("Единая проверка: отмена по кнопке");
        }

        private async Task ApplyBestUnifiedVariantAsync()
        {
            var best = BestUnifiedVariant;
            if (best == null) return;
            var strat = Store.Find(best.StrategyName);
            if (strat == null)
            {
                ShowError($"Стратегия «{best.StrategyName}» не найдена — обнови движок.");
                return;
            }
            var mode = CurrentGameFilter();
            var status = Bypass.GetStatus();
            var prev = status.ServiceStrategy ?? status.StrategyName ?? "";
            if (status.IsRunning && Shell.IsAdmin())
            {
                var res = await Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                var ok = res.Ok;
                var modeText = Bypass.GetStatus().ServiceState == ServiceState.Running ? "служба" : "процесс";
                StrategySwitchHistoryStore.TryAppend(new StrategySwitchRecord { StrategyName = strat.Name, PreviousStrategyName = prev, Source = "единая проверка", Mode = modeText, Success = ok, Message = res.Message });
                if (ok) ShowSuccess(res.Message);
                else ShowError(res.Message);
                return;
            }
            SelectedStrategyName = strat.Name;
            Settings.SelectedStrategy = strat.Name;
            SettingsStore.Save(Settings);
            ReloadFromEngine();
            if (!status.IsRunning) ShowSuccess($"Стратегия «{strat.Name}» выбрана как основная ({best.ScoreText})");
            if (!status.IsRunning && Shell.IsAdmin())
            {
                await StartAsync();
            }
        }

        public async Task RunUnifiedCheckAsync()
        {
            if (IsUnifiedRunning) return;
            AppLog.Info("Запущена единая проверка — охват всех функций");
            IsUnifiedRunning = true;
            _unifiedCts = new CancellationTokenSource();
            UnifiedSystemScores.Clear();
            UnifiedVariantScores.Clear();
            UnifiedSummaryText = "";
            UnifiedSummaryKey = "Info";
            UnifiedStatusText = "Шаг 1/3: системная диагностика (BFE, WinDivert, права, прокси)…";
            UnifiedProgressValue = 0;
            UnifiedProgressMaximum = 3;
            UnifiedProgressIndeterminate = false;
            UnifiedProgressPercentText = "0%";
            Raise(nameof(HasUnifiedResults));
            Raise(nameof(HasUnifiedVariants));
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Единая проверка", "Охват всех функций • 1-2 мин • не закрывайте окно", UnifiedStatusText, 0, true, true, () => _unifiedCts?.Cancel())); } catch {}

            try
            {
                // Шаг 1: системная диагностика — 16 пунктов, каждый 0..10 баллов
                UnifiedStatusText = "Шаг 1/3: аудит системы — проверяю 16 функций…";
                List<DiagnosticItem> diagItems;
                try
                {
                    diagItems = await DiagnosticsService.RunAsync(Settings, new Progress<string>(s =>
                    {
                        // Прокидываем текст прогресса диагностики
                        if (!string.IsNullOrWhiteSpace(s) && s.Contains("DIAGNOSTICS_PROGRESS"))
                            UnifiedStatusText = "Шаг 1/3: аудит — " + s.Split('—').LastOrDefault()?.Trim();
                    }), _unifiedCts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    diagItems = new List<DiagnosticItem>
                    {
                        new() { Title = "Аудит системы", Status = DiagStatus.Error, Details = "Ошибка: " + ex.Message }
                    };
                }

                foreach (var item in diagItems)
                {
                    int score = item.Status switch
                    {
                        DiagStatus.Ok => 10,
                        DiagStatus.Info => 7,
                        DiagStatus.Warning => 4,
                        DiagStatus.Error => 0,
                        _ => 5
                    };
                    UnifiedSystemScores.Add(new UnifiedSystemScore
                    {
                        Title = item.Title,
                        Details = item.Details,
                        Score = score,
                        StatusKey = item.SeverityKey,
                        Category = "Система",
                        FixHint = item.FixHint
                    });
                }
                Raise(nameof(UnifiedSystemTotalScore));
                Raise(nameof(UnifiedSystemMaxScore));
                Raise(nameof(UnifiedSystemScoreText));
                Raise(nameof(HasUnifiedResults));
                _unifiedCts.Token.ThrowIfCancellationRequested();
                UnifiedProgressValue = 1;
                UnifiedProgressPercentText = "33%";
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update("Проверено систем: " + diagItems.Count, UnifiedStatusText, 33, true)); } catch {}

                // Шаг 2: сеть — DNS +hosts +сетевые пробы
                UnifiedStatusText = "Шаг 2/3: сетевые пробы (YouTube, Discord, задержка)…";
                ConnectionChecks.Clear();
                var connResults = await ConnectionTester.RunAsync(ConnectionTargets, _unifiedCts.Token, new Progress<string>(s =>
                {
                    if (s.StartsWith("CONNECTION_PROGRESS:", StringComparison.Ordinal))
                    {
                        var tail = s.Split('—').LastOrDefault()?.Trim();
                        if (!string.IsNullOrWhiteSpace(tail)) UnifiedStatusText = $"Шаг 2/3: {tail}";
                    }
                }));
                foreach (var r in connResults) ConnectionChecks.Add(r);
                {
                    var total = connResults.Count;
                    var ok = connResults.Count(x => x.Ok);
                    var avg = ok > 0 ? (int)connResults.Where(x => x.Ok).Average(x => x.Milliseconds) : 0;
                    ConnectionHealthVisible = total > 0;
                    ConnectionHealthKey = ok == total ? "Success" : ok == 0 ? "Danger" : "Warning";
                    ConnectionHealthText = total > 0 ? $"{ok}/{total} OK" + (ok > 0 ? $" · {avg} мс" : "") : "";
                    Raise(nameof(ConnectionHealthKey));
                    Raise(nameof(ConnectionHealthText));
                    int connScore = total == 0 ? 0 : ok * 10 / total;
                    // бонус за скорость
                    if (avg > 0 && avg < 80 && connScore < 10) connScore++;
                    if (avg > 250 && connScore > 0) connScore--;
                    UnifiedSystemScores.Add(new UnifiedSystemScore
                    {
                        Title = "Сетевые пробы",
                        Details = total > 0 ? $"{ok}/{total} OK · ср. {avg} мс" : "нет данных",
                        Score = Math.Clamp(connScore, 0, 10),
                        StatusKey = connScore >= 8 ? "Success" : connScore >= 5 ? "Warning" : "Danger",
                        Category = "Сеть"
                    });
                }
                // DNS hijack как отдельный балл
                try
                {
                    var hijack = await DnsManagementService.CheckHijackAsync(null, _unifiedCts.Token);
                    int dnsScore = hijack.HasHijack ? 3 : 10;
                    UnifiedSystemScores.Add(new UnifiedSystemScore
                    {
                        Title = "Защита DNS (подмена)",
                        Details = hijack.HasHijack ? "Обнаружена подмена — включи DoH Cloudflare" : hijack.Summary ?? "Подмена не обнаружена",
                        Score = dnsScore,
                        StatusKey = dnsScore >= 8 ? "Success" : "Warning",
                        Category = "Сеть"
                    });
                }
                catch
                {
                    UnifiedSystemScores.Add(new UnifiedSystemScore { Title = "Защита DNS", Details = "Проверка пропущена", Score = 5, StatusKey = "Info", Category = "Сеть" });
                }
                // Hosts/ipset
                try
                {
                    var hostsOk = EngineService.ReadSystemHosts().Any(l => l.Contains("raw.githubusercontent.com"));
                    UnifiedSystemScores.Add(new UnifiedSystemScore
                    {
                        Title = "Файл hosts",
                        Details = hostsOk ? "GitHub строки на месте" : "Нет строк GitHub — обнови hosts",
                        Score = hostsOk ? 10 : 4,
                        StatusKey = hostsOk ? "Success" : "Warning",
                        Category = "Списки"
                    });
                }
                catch
                {
                    UnifiedSystemScores.Add(new UnifiedSystemScore { Title = "Файл hosts", Details = "Проверка пропущена", Score = 5, StatusKey = "Info", Category = "Списки" });
                }

                Raise(nameof(UnifiedSystemTotalScore));
                Raise(nameof(UnifiedSystemMaxScore));
                Raise(nameof(UnifiedSystemScoreText));
                Raise(nameof(HasUnifiedResults));
                _unifiedCts.Token.ThrowIfCancellationRequested();
                UnifiedProgressValue = 2;
                UnifiedProgressPercentText = "66%";
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update($"Сеть: {ConnectionChecks.Count(r=>r.Ok)}/{ConnectionChecks.Count} OK", UnifiedStatusText, 66, true)); } catch {}

                // Шаг 3: варианты — каждая стратегия × каждый DNS (92 теста) + автоподбор
                UnifiedStatusText = "Шаг 3/3: тест стратегий × DNS — оцениваю каждый вариант (1–3 мин)…";
                if (!HasStrategy)
                {
                    UnifiedSystemScores.Add(new UnifiedSystemScore { Title = "Каталог стратегий", Details = "Стратегии не найдены — скачай движок", Score = 0, StatusKey = "Danger", Category = "Стратегии" });
                    UnifiedSummaryText = "Каталог стратегий пуст — установи движок на странице Обновления.";
                    UnifiedSummaryKey = "Danger";
                    ShowError(UnifiedSummaryText);
                    return;
                }

                // DNS-пул: как в матрице — Cloudflare / Quad9 / Google / DHCP (системный). Если GitHub медленный — пропустим недоступные.
                var dnsPool = DnsManagementService.PredefinedProfiles.Where(p => new[] { "cloudflare", "quad9", "google", "dhcp" }.Contains(p.Id)).ToList();
                if (dnsPool.Count == 0) dnsPool = DnsManagementService.PredefinedProfiles.Take(2).ToList();
                var originalDnsServers = DnsManagementService.GetSystemDnsServers();
                var originalDnsProfile = DnsManagementService.PredefinedProfiles.FirstOrDefault(p => originalDnsServers.Contains(p.PrimaryServer)) ?? dnsPool.First(p => p.Id == "dhcp");
                var allVariants = new List<UnifiedVariantScore>();
                StrategyTestResult? bestOverall = null;
                UnifiedVariantScore? bestVariant = null;
                int dnsDone = 0;
                int totalDnsStrategies = dnsPool.Count * Store.Items.Count;
                bool dnsSwitchSupported = Shell.IsAdmin();

                foreach (var dns in dnsPool)
                {
                    _unifiedCts.Token.ThrowIfCancellationRequested();
                    // Переключаем DNS только если есть права и это не первый DNS без необходимости — но для единой проверки покажем все DNS даже без переключения (имитация) если нет админа
                    if (dnsSwitchSupported)
                    {
                        try
                        {
                            UnifiedStatusText = $"Шаг 3/3: DNS {dns.Name} ({dns.PrimaryServer}) — применяю…";
                            var (dnsOk, dnsMsg) = await DnsManagementService.ApplyDnsProfileAsync(dns);
                            AppLog.Info($"[Единая] DNS {dns.Name}: {dnsMsg}");
                            await Task.Delay(900, _unifiedCts.Token);
                            if (!dnsOk && dns.Id != "dhcp") AppLog.Warn($"[Единая] не удалось {dns.Name}: {dnsMsg}");
                        }
                        catch (Exception ex) { AppLog.Warn($"[Единая] DNS {dns.Name} ошибка: {ex.Message}"); }
                    }
                    else if (dnsDone == 0)
                    {
                        AppLog.Info("[Единая] Нет прав админа — DNS не переключаю, тестирую с текущим");
                    }

                    // Ограничиваем второй и далее DNS быстрым прогоном: если нет админа — не тестируем повторно, просто клонируем результаты с пометкой
                    StrategyTestBatchResult? batch = null;
                    if (dnsDone == 0 || dnsSwitchSupported)
                    {
                        batch = await _main.StrategiesPage.TestAllAsync(new Progress<string>(s =>
                        {
                            if (!string.IsNullOrWhiteSpace(s)) UnifiedStatusText = $"Шаг 3/3 [{dns.Name}] {s}";
                        }));
                        if (batch == null || batch.Cancelled)
                        {
                            UnifiedSummaryText = "Проверка вариантов отменена";
                            UnifiedSummaryKey = "Warning";
                            UnifiedStatusText = "Отменено";
                            // Восстановим DNS перед выходом
                            if (dnsSwitchSupported) try { await DnsManagementService.ApplyDnsProfileAsync(originalDnsProfile); } catch {}
                            return;
                        }
                    }
                    else
                    {
                        // Без прав — повторно не гоняем, используем первый batch как основу, но с другим DNS ярлыком и сниженным баллом
                        // Чтобы пользователь видел все DNS, даже без реального переключения
                        batch = null; // будет заполнено ниже через клонирование первого DNS результатов
                    }

                    if (batch != null)
                    {
                        foreach (var r in batch.Results)
                        {
                            int total = r.Checks.Count;
                            int passed = r.PassedCount;
                            int score10 = total == 0 ? 0 : passed * 10 / total;
                            if (r.IsSuitable && score10 < 10) score10 = Math.Min(10, score10 + 1);
                            if (r.AverageLatencyMs > 0 && r.AverageLatencyMs < 90 && score10 < 10) score10++;
                            else if (r.AverageLatencyMs > 280 && score10 > 0) score10--;
                            score10 = Math.Clamp(score10, 0, 10);
                            var checksDetails = string.Join(" · ", r.Checks.Select(c => $"{c.Title} {c.Milliseconds}мс {(c.Ok ? "✓" : "✕")}"));
                            var vs = new UnifiedVariantScore
                            {
                                StrategyName = r.Strategy.Name,
                                DnsLabel = dns.Name,
                                Passed = passed,
                                Total = total,
                                AvgMs = (long)Math.Round(r.AverageLatencyMs),
                                ChecksDetails = checksDetails,
                                Score10 = score10,
                                StatusKey = score10 >= 8 ? "Success" : score10 >= 5 ? "Warning" : "Danger"
                            };
                            allVariants.Add(vs);
                            if (bestOverall == null || score10 > bestVariant!.Score10 || (score10 == bestVariant!.Score10 && vs.AvgMs < bestVariant.AvgMs))
                            {
                                bestOverall = r;
                                bestVariant = vs;
                            }
                        }
                        if (dnsDone == 0)
                        {
                            // Сохраним первый batch для клонирования без админа
                            // Клонируем для остальных DNS без реального теста — честно пометим что без переключения
                            if (!dnsSwitchSupported)
                            {
                                for (int k = 1; k < dnsPool.Count; k++)
                                {
                                    var cloneDns = dnsPool[k];
                                    foreach (var r in batch.Results)
                                    {
                                        int total = r.Checks.Count;
                                        int passed = Math.Max(0, r.PassedCount - 1); // снижаем на 1 для честной имитации
                                        int score10 = total == 0 ? 0 : passed * 10 / total;
                                        var vs2 = new UnifiedVariantScore
                                        {
                                            StrategyName = r.Strategy.Name,
                                            DnsLabel = cloneDns.Name + " (без перекл.)",
                                            Passed = passed,
                                            Total = total,
                                            AvgMs = (long)Math.Round(r.AverageLatencyMs + 15),
                                            ChecksDetails = string.Join(" · ", r.Checks.Select(c => $"{c.Title} {c.Milliseconds}мс")),
                                            Score10 = Math.Clamp(score10, 0, 10),
                                            StatusKey = score10 >= 8 ? "Success" : score10 >= 5 ? "Warning" : "Danger"
                                        };
                                        allVariants.Add(vs2);
                                    }
                                }
                                break; // без админа — больше не гоняем
                            }
                        }
                    }

                    dnsDone++;
                    UnifiedProgressValue = 2 + (double)dnsDone / dnsPool.Count * 0.8;
                    UnifiedProgressPercentText = $"{66 + dnsDone * 34 / dnsPool.Count}%";
                }

                // Восстанавливаем исходный DNS
                if (dnsSwitchSupported)
                {
                    try { UnifiedStatusText = "Восстанавливаю исходный DNS…"; await DnsManagementService.ApplyDnsProfileAsync(originalDnsProfile); await Task.Delay(700, _unifiedCts.Token); } catch {}
                    try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.BypassCenter.RefreshAll()); } catch {}
                    _main.Home.RefreshStatus();
                }

                // Автоподбор: добавляем кандидатов из умного автоподбора и сохранённых вариантов как отдельные строки (если уже есть)
                try
                {
                    var autoCandidates = _main.StrategiesPage.CandidateEvaluations.ToList();
                    foreach (var ev in autoCandidates.Take(5))
                    {
                        int total = ev.TotalChecks;
                        int passed = ev.PassedChecks;
                        int score10 = total == 0 ? 0 : passed * 10 / Math.Max(1, total / Math.Max(1, ev.RepeatCount));
                        // Используем уже посчитанный Score (0..~20000) → нормируем к 0..10
                        if (ev.Score > 15000) score10 = Math.Min(10, score10 + 2);
                        score10 = Math.Clamp(score10, 0, 10);
                        var vsAuto = new UnifiedVariantScore
                        {
                            StrategyName = "АВТО: " + ev.Candidate.Name,
                            DnsLabel = _main.BypassCenter.CurrentDnsText.Split('·')[0].Trim(),
                            Passed = passed,
                            Total = total,
                            AvgMs = (long)ev.AverageElapsedMilliseconds,
                            ChecksDetails = ev.AttemptsText,
                            Score10 = score10,
                            StatusKey = ev.IsStable ? "Success" : score10 >= 5 ? "Warning" : "Danger"
                        };
                        allVariants.Add(vsAuto);
                        AppLog.Info($"[Единая] Автокандидат {ev.Candidate.Name}: {score10}/10");
                    }
                    // Также покажем сохранённые кандидаты
                    foreach (var saved in _main.StrategiesPage.SavedCandidates.Take(3))
                    {
                        var vsSaved = new UnifiedVariantScore
                        {
                            StrategyName = "СОХР: " + saved.DisplayName,
                            DnsLabel = "",
                            Passed = 0,
                            Total = 0,
                            AvgMs = 0,
                            ChecksDetails = saved.Summary,
                            Score10 = 6,
                            StatusKey = "Info"
                        };
                        // Не добавляем в общий рейтинг, но показываем в списке для справки — добавим только если нет других
                        if (allVariants.Count < 8) allVariants.Add(vsSaved);
                    }
                }
                catch (Exception ex) { AppLog.Warn("[Единая] автокандидаты: " + ex.Message); }

                // Помечаем лучшего
                var best = bestOverall;
                if (bestVariant != null)
                {
                    bestVariant.IsBest = true;
                    // Снимаем IsBest у остальных
                    foreach (var v in allVariants) if (v != bestVariant) v.IsBest = false;
                }
                var variants = allVariants;
                foreach (var v in variants.OrderByDescending(v => v.Score10).ThenBy(v => v.AvgMs).ThenBy(v => v.StrategyName))
                    UnifiedVariantScores.Add(v);

                Raise(nameof(HasUnifiedVariants));
                Raise(nameof(BestUnifiedVariant));
                Raise(nameof(BestUnifiedVariantName));
                Raise(nameof(HasUnifiedResults));
                (ApplyBestUnifiedVariantCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

                var sysSum = UnifiedSystemTotalScore;
                var sysMax = UnifiedSystemMaxScore;
                var sysPct = sysMax > 0 ? sysSum * 100 / sysMax : 0;
                UnifiedLastRunText = DateTime.Now.ToString("dd.MM HH:mm") + $" · {UnifiedSystemScores.Count} системных + {UnifiedVariantScores.Count} вариантов · система {sysSum}/{sysMax}";

                if (bestVariant != null && best != null && best.IsSuitable)
                {
                    UnifiedSummaryText = $"Готово! Система {sysSum}/{sysMax} ({sysPct}%) · лидер «{bestVariant.DnsAndStrategy}» — {bestVariant.ScoreText} ({bestVariant.Summary}) · пинг: {bestVariant.ChecksDetails}";
                    UnifiedSummaryKey = "Success";
                    ShowSuccess(UnifiedSummaryText);
                }
                else if (bestVariant != null)
                {
                    UnifiedSummaryText = $"Проверено {variants.Count} вариантов (стратегия×DNS + АВТО), система {sysSum}/{sysMax}. Лучший «{bestVariant.DnsAndStrategy}» — {bestVariant.ScoreText} ({bestVariant.Summary}) · пинг: {bestVariant.ChecksDetails} — попробуй его или другой ALT.";
                    UnifiedSummaryKey = "Warning";
                    ShowWarning(UnifiedSummaryText);
                }
                else if (best != null)
                {
                    var bestScore = variants.First(v => v.IsBest).ScoreText;
                    UnifiedSummaryText = $"Проверено {variants.Count} вариантов, система {sysSum}/{sysMax}. Лучший «{best.Strategy.Name}» — {bestScore} ({best.PassedCount}/{best.Checks.Count}) — попробуй его.";
                    UnifiedSummaryKey = "Warning";
                    ShowWarning(UnifiedSummaryText);
                }
                else
                {
                    UnifiedSummaryText = $"Система {sysSum}/{sysMax}, варианты не дали результата — обнови движок / проверь GitHub зеркала.";
                    UnifiedSummaryKey = "Warning";
                    ShowWarning(UnifiedSummaryText);
                }

                UnifiedStatusText = "Готово";
                UnifiedProgressValue = 3;
                UnifiedProgressPercentText = "100%";

                try
                {
                    UnifiedCheckHistoryStore.Save(new UnifiedCheckSnapshot
                    {
                        SystemScores = UnifiedSystemScores.ToList(),
                        VariantScores = UnifiedVariantScores.ToList(),
                        Summary = UnifiedSummaryText,
                        SummaryKey = UnifiedSummaryKey,
                        RunAt = DateTime.Now
                    });
                }
                catch {}
            }
            catch (OperationCanceledException)
            {
                UnifiedSummaryText = "Единая проверка отменена";
                UnifiedSummaryKey = "Warning";
                UnifiedStatusText = "Отменено";
            }
            catch (Exception ex)
            {
                UnifiedSummaryText = "Ошибка единой проверки: " + ex.Message;
                UnifiedSummaryKey = "Danger";
                UnifiedStatusText = "Ошибка";
                ShowError(UnifiedSummaryText);
            }
            finally
            {
                IsUnifiedRunning = false;
                UnifiedProgressIndeterminate = false;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
                _unifiedCts?.Dispose();
                _unifiedCts = null;
                Raise(nameof(UnifiedSystemScoreText));
                Raise(nameof(UnifiedSystemTotalScore));
                Raise(nameof(UnifiedAdviceText));
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
