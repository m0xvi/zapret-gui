using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>
    /// Фоновый контроль ресурсов. В обычном режиме выполняет лёгкую проверку, а при сбое
    /// запускает сравнение «без обхода / с обходом», чтобы не путать DPI-блокировку со сбоем сети.
    /// </summary>
    public sealed class MonitoringViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _networkHistoryCleanupTimer;
        private bool _isBusy;
        private double _progressValue;
        private double _progressMaximum = 1;
        private bool _progressVisible;
        private string _progressText = "";
        private string _progressPercentText = "";
        private MonitorTarget? _selectedTarget;
        private ResourceDiagnosisResult? _diagnosis;
        private string _newResourceUrl = "";
        private string _newResourceName = "";
        private string _newProcessName = "";
        private bool _newResourceIsGame;
        private string _message = "";
        private string _messageKey = "Info";
        private string _lastCheckText = "Проверка ещё не выполнялась";
        private DateTime _lastRecovery = DateTime.MinValue;
        private DateTime _lastBestCheck = DateTime.MinValue;
        private int _consecutiveStrategyFailures;
        private string _failureTargetId = "";
        private const int RecoveryFailureThreshold = 2;
        private const int ObservationSampleSeconds = 15;
        private int _networkSessionDurationMinutes = 5;
        private readonly Dictionary<string, ObservedSocket> _observedSockets = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _observationCts;
        private bool _isRecordingNetwork;
        private bool _networkSessionStoppedByUser;
        private string _networkSessionStatus = "Сеанс не выполнялся";

        public MonitoringViewModel(MainViewModel main)
        {
            _main = main;
            MonitorTargetStore.EnsureDefaults(main.Settings);
            SettingsStore.Save(main.Settings);
            Targets = new ObservableCollection<MonitorTarget>(main.Settings.MonitorTargets);

            CheckAllCommand = new AsyncRelayCommand(() => CheckAllAsync(), () => !IsBusy && Targets.Any(t => t.Enabled));
            DiagnoseCommand = new AsyncRelayCommand(DiagnoseSelectedAsync, () => !IsBusy && SelectedTarget != null);
            AddResourceCommand = new RelayCommand(AddResource);
            RemoveResourceCommand = new RelayCommand(RemoveResource, p => p is MonitorTarget target && !target.IsBuiltIn);
            AddToBypassListCommand = new RelayCommand(AddSelectedToBypassList, () => SelectedTarget != null);
            OpenUnifiedCheckCommand = new RelayCommand(() => _main.Navigate("diagnostics"));
            StartNetworkSessionCommand = new AsyncRelayCommand(RunNetworkSessionAsync, () => !IsBusy && !IsRecordingNetwork);
            StopNetworkSessionCommand = new RelayCommand(StopNetworkSession, () => IsRecordingNetwork);
            RefreshProcessOptionsCommand = new RelayCommand(RefreshProcessOptions);
            AddObservedProcessCommand = new RelayCommand(AddObservedProcess);
            ExportNetworkObservationsCommand = new RelayCommand(ExportNetworkObservations, () => NetworkObservationCount > 0 && !IsRecordingNetwork);
            ClearNetworkObservationsCommand = new RelayCommand(ClearNetworkObservations, () => NetworkObservationCount > 0 && !IsRecordingNetwork);
            SelectedTarget = Targets.FirstOrDefault();

            foreach (var item in NetworkDiagnosticsStore.GetRecent().Take(12).Reverse())
                RecentNetworkObservations.Add(item);
            NetworkObservationCount = NetworkDiagnosticsStore.GetRecent().Count;
            RefreshProcessOptions();

            _timer = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromMinutes(GetInterval())
            };
            _timer.Tick += async (_, _) => await BackgroundCheckAsync();
            if (Settings.ResourceMonitoringEnabled) _timer.Start();
            _networkHistoryCleanupTimer = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromMinutes(15)
            };
            _networkHistoryCleanupTimer.Tick += (_, _) => NetworkObservationCount = NetworkDiagnosticsStore.CountRecent;
            _networkHistoryCleanupTimer.Start();
        }

        public AppSettings Settings => _main.Settings;
        public ObservableCollection<MonitorTarget> Targets { get; }
        public ObservableCollection<ResourceProbeResult> Results { get; } = new();
        public ObservableCollection<NetworkObservationRecord> RecentNetworkObservations { get; } = new();
        public ObservableCollection<NetworkProcessOption> ProcessOptions { get; } = new();
        public ObservableCollection<int> NetworkSessionDurationOptions { get; } = new() { 2, 5, 10 };

        public int NetworkSessionDurationMinutes
        {
            get => _networkSessionDurationMinutes;
            set
            {
                if (NetworkSessionDurationOptions.Contains(value) && Set(ref _networkSessionDurationMinutes, value))
                    Raise(nameof(NetworkSessionButtonText));
            }
        }

        public string NetworkSessionButtonText => $"Записать {NetworkSessionDurationMinutes} мин";

        public bool IsRecordingNetwork
        {
            get => _isRecordingNetwork;
            private set
            {
                if (Set(ref _isRecordingNetwork, value))
                {
                    (StartNetworkSessionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (StopNetworkSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ExportNetworkObservationsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ClearNetworkObservationsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    Raise(nameof(NetworkCaptureStatusKey));
                }
            }
        }

        public string NetworkSessionStatus
        {
            get => _networkSessionStatus;
            private set
            {
                if (Set(ref _networkSessionStatus, value)) Raise(nameof(NetworkCaptureStatusKey));
            }
        }

        public string NetworkCaptureStatusKey => IsRecordingNetwork ? "Warning" : "Info";

        private int _networkObservationCount;
        public int NetworkObservationCount
        {
            get => _networkObservationCount;
            private set
            {
                if (Set(ref _networkObservationCount, value))
                {
                    (ExportNetworkObservationsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ClearNetworkObservationsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    Raise(nameof(NetworkObservationCountText));
                }
            }
        }

        public string NetworkObservationCountText => $"Локально за 24 часа: {NetworkObservationCount} записей";

        public string NewProcessName
        {
            get => _newProcessName;
            set => Set(ref _newProcessName, value);
        }

        public string SelectedProcessNamesText
        {
            get
            {
                var selected = ProcessOptions.Where(option => option.IsSelected).Select(option => option.ProcessName).ToArray();
                return selected.Length == 0 ? "Не выбраны" : string.Join(", ", selected.Select(name => name + ".exe"));
            }
        }

        public MonitorTarget? SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (Set(ref _selectedTarget, value))
                {
                    (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (AddToBypassListCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveResourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public ResourceDiagnosisResult? Diagnosis
        {
            get => _diagnosis;
            private set
            {
                if (Set(ref _diagnosis, value))
                {
                    Raise(nameof(DiagnosisVisible));
                    Raise(nameof(DiagnosisText));
                    Raise(nameof(DiagnosisKey));
                }
            }
        }

        public bool DiagnosisVisible => Diagnosis != null;
        public string DiagnosisText => Diagnosis == null ? "" :
            $"{Diagnosis.Target.Name}: {Diagnosis.Level} · уверенность {Diagnosis.Confidence}\n{Diagnosis.Summary}\n" +
            $"Без обхода: {(Diagnosis.Direct.Ok ? "доступен" : "недоступен")} · " +
            $"С обходом: {(Diagnosis.WithBypass.Ok ? "доступен" : "недоступен")}\n" +
            $"DNS без обхода: {Diagnosis.Direct.DnsDetails} · через обход: {Diagnosis.WithBypass.DnsDetails}";
        public string DiagnosisKey => Diagnosis?.StatusKey ?? "Info";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    (CheckAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (DiagnoseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (StartNetworkSessionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

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

        public bool ProgressVisible
        {
            get => _progressVisible;
            set => Set(ref _progressVisible, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => Set(ref _progressText, value);
        }

        public string ProgressPercentText
        {
            get => _progressPercentText;
            set => Set(ref _progressPercentText, value);
        }

        public string NewResourceUrl
        {
            get => _newResourceUrl;
            set => Set(ref _newResourceUrl, value);
        }

        public string NewResourceName
        {
            get => _newResourceName;
            set => Set(ref _newResourceName, value);
        }

        public bool NewResourceIsGame
        {
            get => _newResourceIsGame;
            set => Set(ref _newResourceIsGame, value);
        }

        public string LastCheckText
        {
            get => _lastCheckText;
            private set => Set(ref _lastCheckText, value);
        }

        public string Message
        {
            get => _message;
            private set
            {
                if (Set(ref _message, value)) Raise(nameof(MessageVisible));
            }
        }

        public bool MessageVisible => !string.IsNullOrWhiteSpace(Message);
        public string MessageKey
        {
            get => _messageKey;
            private set => Set(ref _messageKey, value);
        }

        public bool ResourceMonitoringEnabled
        {
            get => Settings.ResourceMonitoringEnabled;
            set
            {
                if (Settings.ResourceMonitoringEnabled == value) return;
                Settings.ResourceMonitoringEnabled = value;
                SettingsStore.Save(Settings);
                if (value) _timer.Start(); else _timer.Stop();
                Raise(nameof(ResourceMonitoringEnabled));
                Message = value ? "Фоновый мониторинг включён" : "Фоновый мониторинг выключен";
                MessageKey = "Success";
            }
        }

        public bool AutoRecoverStrategy
        {
            get => Settings.AutoRecoverStrategy;
            set
            {
                Settings.AutoRecoverStrategy = value;
                // v2.2.2: подчинённый флаг без мастера ничего не значит — включаем мастер вместе с ним.
                if (value) Settings.AutoSwitchStrategyMasterEnabled = true;
                SettingsStore.Save(Settings);
                Raise(nameof(AutoRecoverStrategy));
                Raise(nameof(AutoSwitchAllowed));
            }
        }

        /// <summary>Разрешена ли автосмена стратегий сейчас (мастер-выключатель + не SafeMode).</summary>
        public bool AutoSwitchAllowed => Settings.StrategyAutoSwitchAllowed;

        public int MonitoringIntervalMinutes
        {
            get => GetInterval();
            set
            {
                Settings.ResourceMonitoringIntervalMinutes = Math.Clamp(value, 5, 120);
                SettingsStore.Save(Settings);
                _timer.Interval = TimeSpan.FromMinutes(GetInterval());
                Raise(nameof(MonitoringIntervalMinutes));
            }
        }

        public ICommand CheckAllCommand { get; }
        public ICommand DiagnoseCommand { get; }
        public ICommand OpenUnifiedCheckCommand { get; }
        public ICommand AddResourceCommand { get; }
        public ICommand RemoveResourceCommand { get; }
        public ICommand AddToBypassListCommand { get; }
        public ICommand StartNetworkSessionCommand { get; }
        public ICommand StopNetworkSessionCommand { get; }
        public ICommand RefreshProcessOptionsCommand { get; }
        public ICommand AddObservedProcessCommand { get; }
        public ICommand ExportNetworkObservationsCommand { get; }
        public ICommand ClearNetworkObservationsCommand { get; }

        public async Task CheckAllAsync(bool isBackground = false)
        {
            if (IsBusy) return;
            IsBusy = true;
            Message = "Проверяю ресурсы…";
            MessageKey = "Info";
            var enabled = Targets.Where(t => t.Enabled).ToList();
            ProgressMaximum = Math.Max(1, enabled.Count);
            ProgressValue = 0;
            ProgressPercentText = "0%";
            ProgressText = "Подготавливаю проверку ресурсов…";
            ProgressVisible = true;
            try
            {
                Results.Clear();
                for (var i = 0; i < enabled.Count; i++)
                {
                    var target = enabled[i];
                    ProgressText = $"Проверяю {i + 1} из {enabled.Count}: {target.Name}…";
                    var probe = await ResourceProbe.CheckAsync(target);
                    Results.Add(probe);
                    RecordProbe(target, probe, isBackground ? "фон" : "ручная проверка");
                    target.LastStatusText = probe.StatusText;
                    target.LastStatusKey = probe.StatusKey;
                    target.LastDetails = probe.Details;
                    ProgressValue = i + 1;
                    ProgressPercentText = $"{ProgressValue / ProgressMaximum * 100:0}%";
                }

                LastCheckText = "Последняя проверка: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
                var failed = Results.FirstOrDefault(r => !r.Ok);
                if (failed == null)
                {
                    _consecutiveStrategyFailures = 0;
                    _failureTargetId = "";
                    Message = "Все выбранные ресурсы доступны";
                    MessageKey = "Success";
                    return;
                }

                Message = $"Недоступен ресурс: {failed.Target.Name}. Запускаю сравнение прямого подключения и обхода.";
                MessageKey = "Warning";

                // v2.2.2: восстановление только при явно включённой автосмене.
                if (Settings.AutoRecoverStrategy && Settings.StrategyAutoSwitchAllowed && _main.Bypass.GetStatus().IsRunning)
                    await DiagnoseAndRecoverAsync(failed.Target);
            }
            catch (Exception ex)
            {
                SetMessage("Ошибка мониторинга: " + ex.Message, "Danger");
            }
            finally
            {
                ProgressVisible = false;
                ProgressText = "";
                IsBusy = false;
            }
        }

        private async Task DiagnoseSelectedAsync()
        {
            if (SelectedTarget == null || IsBusy) return;
            IsBusy = true;
            try
            {
                Diagnosis = await _main.Bypass.DiagnoseResourceAsync(SelectedTarget);
                RecordDiagnosis(Diagnosis);
                SetMessage(Diagnosis.Summary, Diagnosis.StatusKey);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DiagnoseAndRecoverAsync(MonitorTarget target)
        {
            if (!Settings.StrategyAutoSwitchAllowed) return;
            Diagnosis = await _main.Bypass.DiagnoseResourceAsync(target);
            RecordDiagnosis(Diagnosis);
            if (Diagnosis.Kind != ResourceDiagnosisKind.StrategyBreaks)
            {
                _consecutiveStrategyFailures = 0;
                _failureTargetId = "";
                Message = Diagnosis.Summary;
                MessageKey = Diagnosis.StatusKey;
                if (Settings.MonitorNotificationsEnabled)
                    NotificationRequested?.Invoke(Message);
                return;
            }

            if (_failureTargetId != target.Id)
            {
                _failureTargetId = target.Id;
                _consecutiveStrategyFailures = 0;
            }
            _consecutiveStrategyFailures++;
            if (_consecutiveStrategyFailures < RecoveryFailureThreshold)
            {
                SetMessage($"Сбой стратегии подтверждён {_consecutiveStrategyFailures} из {RecoveryFailureThreshold} раз. Переключение пока не выполняется.", "Warning");
                return;
            }

            if ((DateTime.Now - _lastRecovery).TotalMinutes < 10)
            {
                SetMessage("Сбой стратегии повторяется, но действует cooldown автоподбора 10 минут.", "Warning");
                return;
            }

            _lastRecovery = DateTime.Now;
            Message = "Текущая стратегия мешает ресурсу. Подбираю другую…";
            MessageKey = "Warning";
            var before = _main.Bypass.GetStatus();

            // P0 1.7.0: сначала пробуем профиль, привязанный к текущей сети / любой подходящий профиль
            if (Settings.AutoSwitchProfileOnFailure && Settings.AutoSwitchProfileOnNetworkChange)
            {
                try
                {
                    var switched = await _main.ProfileAutoSwitch.TrySwitchOnFailureAsync(target, before);
                    if (switched)
                    {
                        _main.Home.RefreshStatus();
                        _main.Profiles.RefreshNetwork();
                        Message = $"📶 Автопрофиль применён для восстановления «{target.Name}»";
                        MessageKey = "Success";
                        // v1.32.3: смена стратегии — уведомляем всегда
                        NotificationRequested?.Invoke(Message);
                        try { _main.NotifyStrategyAutoSwitched(Message); } catch {}
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("[Monitoring] Ошибка автопрофиля при сбое: " + ex.Message);
                }
            }

            if (target.IsGame)
            {
                await RecoverGameStrategyAsync(target, before);
                return;
            }

            var batch = await _main.StrategiesPage.TestAllAsync();
            var best = batch?.Best;
            if (best == null || !best.IsSuitable)
            {
                SetMessage("Подходящая стратегия не найдена. Проверьте ресурс позже — возможно, это внешняя блокировка.", "Warning");
                return;
            }

            var prevName = before.StrategyName;
            _main.StrategiesPage.SelectAsDefault(best.Strategy);
            var result = await StartSelectedStrategyAsync(best.Strategy, before);
            _main.Home.RefreshStatus();
            var okMsg = $"🔄 Стратегия сменена: «{prevName}» → «{best.Strategy.Name}» — ресурс «{target.Name}» восстановлен";
            SetMessage(result.Ok ? okMsg : "Новая стратегия найдена, но запустить её не удалось: " + result.Message,
                result.Ok ? "Success" : "Danger");
            // v1.32.3: уведомление о смене стратегии всегда
            if (result.Ok)
            {
                NotificationRequested?.Invoke(Message);
                try { _main.NotifyStrategyAutoSwitched(Message); } catch {}
            }
            else if (Settings.MonitorNotificationsEnabled)
                NotificationRequested?.Invoke(Message);
        }

        private async Task RecoverGameStrategyAsync(MonitorTarget target, BypassStatus before)
        {
            if (!Settings.StrategyAutoSwitchAllowed) return;
            var current = Diagnosis?.WithBypass;
            // v1.32.3: сортировка по реальным тестам, пропуск битых
            var candidates = _main.Strategies.Items
                .Where(s => !s.Name.Equals(before.StrategyName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.TestResult?.PassedCount ?? -1)
                .ThenByDescending(s => s.IsRecommended)
                .ThenBy(s => s.Name)
                .Take(3)
                .ToList();
            var tested = new List<(StrategyInfo Strategy, ResourceProbeResult Probe)>();
            foreach (var candidate in candidates)
            {
                var probe = await _main.Bypass.TestStrategyOnResourceAsync(candidate, target);
                tested.Add((candidate, probe));
            }

            var best = tested
                .Where(item => item.Probe.Ok)
                .OrderBy(item => item.Probe.Milliseconds)
                .FirstOrDefault();
            var currentMs = current?.Ok == true ? current.Milliseconds : long.MaxValue;
            if (best.Strategy == null || (current?.Ok == true && best.Probe.Milliseconds + 20 >= currentMs))
            {
                SetMessage("Альтернативы проверены: подходящего улучшения нет. Если прямой TCP-пинг тоже высокий, вероятно, медленный интернет, а не стратегия.", "Warning");
                return;
            }

            var prevGame = before.StrategyName;
            _main.StrategiesPage.SelectAsDefault(best.Strategy);
            var result = await StartSelectedStrategyAsync(best.Strategy, before);
            _main.Home.RefreshStatus();
            var gameMsg = result.Ok
                ? $"🔄 Стратегия для игры сменена: «{prevGame}» → «{best.Strategy.Name}»: TCP {best.Probe.Milliseconds} мс"
                : "Игровая стратегия найдена, но запустить её не удалось: " + result.Message;
            SetMessage(gameMsg, result.Ok ? "Success" : "Danger");
            if (result.Ok)
            {
                NotificationRequested?.Invoke(Message);
                try { _main.NotifyStrategyAutoSwitched(Message); } catch {}
            }
            else if (Settings.MonitorNotificationsEnabled)
                NotificationRequested?.Invoke(Message);
        }

        private async Task<OperationResult> StartSelectedStrategyAsync(StrategyInfo strategy, BypassStatus before)
        {
            // Бесшовное переключение с учётом актуального режима службы/процесса
            var current = _main.Bypass.GetStatus();
            if (current.ServiceState == ServiceState.Running
                || current.ServiceState == ServiceState.StartPending
                || current.ServiceState == ServiceState.StopPending)
                return await _main.Bypass.InstallServiceAsync(strategy,
                    EngineService.GetGameFilterMode(Settings.EnginePath));
            if (current.IsRunning)
                return await _main.Bypass.SwitchToStrategyAsync(strategy,
                    EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
            return await _main.Bypass.StartAsync(strategy,
                EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
        }

        private void AddResource()
        {
            var dialog = new Views.InputDialog(
                "Добавить ресурс в мониторинг",
                "Введите URL или домен ресурса:",
                "Название ресурса (необязательно):",
                "Это игровой сервер (проверять TCP-пинг)")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;
            NewResourceUrl = dialog.Value;
            NewResourceName = dialog.SecondaryValue;
            NewResourceIsGame = dialog.IsChecked;

            if (!MonitorTarget.TryCreate(NewResourceUrl, NewResourceName, out var target, out var error) || target == null)
            {
                SetMessage(error, "Warning");
                return;
            }
            if (Targets.Any(t => t.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)))
            {
                SetMessage("Этот домен уже добавлен", "Warning");
                return;
            }
            target.IsGame = NewResourceIsGame;
            Settings.MonitorTargets.Add(target);
            Targets.Add(target);
            SettingsStore.Save(Settings);
            SelectedTarget = target;
            NewResourceUrl = "";
            NewResourceName = "";
            NewResourceIsGame = false;
            SetMessage("Ресурс добавлен в фоновый мониторинг", "Success");
        }

        private void RemoveResource(object? parameter)
        {
            if (parameter is not MonitorTarget target || target.IsBuiltIn) return;
            Settings.MonitorTargets.Remove(target);
            Targets.Remove(target);
            SettingsStore.Save(Settings);
            if (SelectedTarget == target) SelectedTarget = Targets.FirstOrDefault();
        }

        private void AddSelectedToBypassList()
        {
            if (SelectedTarget == null) return;
            var result = MonitorTargetStore.AddToGeneralList(Settings, SelectedTarget);
            SetMessage(result.Message, result.Ok ? "Success" : "Danger");
        }

        private void RecordDiagnosis(ResourceDiagnosisResult diagnosis)
        {
            var status = _main.Bypass.GetStatus();
            RecordProbe(diagnosis.Target, diagnosis.Direct, "сравнение — напрямую", bypassOverride: false,
                strategyOverride: status.StrategyName);
            if (!ReferenceEquals(diagnosis.Direct, diagnosis.WithBypass))
                RecordProbe(diagnosis.Target, diagnosis.WithBypass, "сравнение — обход", bypassOverride: true,
                    strategyOverride: status.StrategyName);
        }

        private void RecordProbe(MonitorTarget target, ResourceProbeResult probe, string source,
            bool? bypassOverride = null, string? strategyOverride = null)
        {
            var status = _main.Bypass.GetStatus();
            var record = new NetworkObservationRecord
            {
                TimeUtc = DateTime.UtcNow,
                EventType = "Проверка",
                Source = source,
                TargetName = target.Name,
                Host = target.Host,
                Ok = probe.Ok,
                FailureKind = probe.Ok ? null : probe.Kind,
                Milliseconds = probe.Milliseconds,
                HttpStatusCode = probe.HttpStatusCode,
                DnsOk = probe.DnsOk,
                TcpOk = probe.TcpOk,
                BypassRunning = bypassOverride ?? status.IsRunning,
                StrategyName = strategyOverride ?? status.StrategyName,
                ServiceState = status.ServiceState.ToString(),
                Details = LimitDetails(NetworkObservationRecord.CombineDetails(probe.DnsDetails, probe.Details))
            };

            NetworkDiagnosticsStore.Add(record);
            AddRecentNetworkObservation(record);
        }

        private void AddRecentNetworkObservation(NetworkObservationRecord record)
        {
            RecentNetworkObservations.Insert(0, record);
            while (RecentNetworkObservations.Count > 12) RecentNetworkObservations.RemoveAt(RecentNetworkObservations.Count - 1);
            NetworkObservationCount = NetworkDiagnosticsStore.CountRecent;
        }

        private void RefreshProcessOptions()
        {
            var selected = (Settings.NetworkObservedProcessNames ?? new List<string>())
                .Select(WindowsNetworkActivity.NormalizeProcessName)
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                var running = WindowsNetworkActivity.GetCandidateProcessNames()
                    .Select(WindowsNetworkActivity.NormalizeProcessName)
                    .Where(name => name.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                ProcessOptions.Clear();
                foreach (var name in running.Union(selected).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    var option = new NetworkProcessOption(name, running.Contains(name), selected.Contains(name));
                    option.PropertyChanged += OnProcessOptionChanged;
                    ProcessOptions.Add(option);
                }
                Raise(nameof(SelectedProcessNamesText));
            }
            catch (Exception ex)
            {
                SetMessage("Не удалось прочитать список приложений: " + ex.Message, "Warning");
            }
        }

        private void OnProcessOptionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NetworkProcessOption.IsSelected)) return;
            Settings.NetworkObservedProcessNames = ProcessOptions.Where(option => option.IsSelected)
                .Select(option => option.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SettingsStore.Save(Settings);
            Raise(nameof(SelectedProcessNamesText));
        }

        private void AddObservedProcess()
        {
            var value = (NewProcessName ?? "").Trim().Trim('"');
            if (value.Length == 0)
            {
                SetMessage("Укажите имя EXE или путь к приложению, например chrome.exe.", "Warning");
                return;
            }
            var name = WindowsNetworkActivity.NormalizeProcessName(Path.GetFileName(value));
            if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                SetMessage("Не удалось определить имя процесса. Укажите имя EXE, например game.exe.", "Warning");
                return;
            }

            var option = ProcessOptions.FirstOrDefault(item => item.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (option == null)
            {
                option = new NetworkProcessOption(name, false, true);
                option.PropertyChanged += OnProcessOptionChanged;
                ProcessOptions.Add(option);
            }
            else option.IsSelected = true;
            NewProcessName = "";
            SetMessage($"Приложение {name}.exe добавлено для будущих сеансов.", "Success");
        }

        private async Task RunNetworkSessionAsync()
        {
            if (IsBusy || IsRecordingNetwork) return;
            var targets = Targets.Where(target => target.Enabled).ToList();
            var processNames = Settings.NetworkObservedProcessNames?.Select(WindowsNetworkActivity.NormalizeProcessName)
                .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
            if (targets.Count == 0 && processNames.Length == 0)
            {
                SetMessage("Выберите хотя бы один сайт или приложение для сеанса наблюдения.", "Warning");
                return;
            }

            _observationCts?.Dispose();
            _observationCts = new CancellationTokenSource(TimeSpan.FromMinutes(NetworkSessionDurationMinutes));
            _networkSessionStoppedByUser = false;
            var ct = _observationCts.Token;
            _observedSockets.Clear();
            IsBusy = true;
            IsRecordingNetwork = true;
            var sessionDurationMinutes = NetworkSessionDurationMinutes;
            NetworkSessionStatus = $"Подготовка {sessionDurationMinutes}-минутного сеанса наблюдения…";
            var started = DateTime.UtcNow;
            var observationsBeforeSession = NetworkDiagnosticsStore.CountRecent;
            var endsAt = started.AddMinutes(sessionDurationMinutes);
            var sessionStart = new NetworkObservationRecord
            {
                TimeUtc = started,
                EventType = "Сеанс",
                Source = "ручной сеанс",
                Details = $"Начат {sessionDurationMinutes}-минутный сеанс; выбрано сайтов: {targets.Count}; приложений: {processNames.Length}"
            };
            NetworkDiagnosticsStore.Add(sessionStart);
            AddRecentNetworkObservation(sessionStart);
            AppLog.Info($"[Сеть] Запущен {sessionDurationMinutes}-минутный сеанс наблюдения (без захвата содержимого пакетов)");

            var nextProbeAt = DateTime.MinValue;
            var socketCaptureAvailable = processNames.Length > 0;
            var socketCaptureErrorLogged = false;
            string? sessionError = null;
            try
            {
                while (DateTime.UtcNow < endsAt && !ct.IsCancellationRequested)
                {
                    if (targets.Count > 0 && DateTime.UtcNow >= nextProbeAt)
                    {
                        var semaphore = new SemaphoreSlim(4, 4);
                        try
                        {
                            var probes = targets.Select(async target =>
                            {
                                await semaphore.WaitAsync(ct).ConfigureAwait(true);
                                try
                                {
                                    var probe = await ResourceProbe.CheckAsync(target, ct).ConfigureAwait(true);
                                    RecordProbe(target, probe, "сеанс");
                                }
                                finally { semaphore.Release(); }
                            });
                            await Task.WhenAll(probes);
                        }
                        finally { semaphore.Dispose(); }
                        nextProbeAt = DateTime.UtcNow.AddSeconds(ObservationSampleSeconds);
                    }

                    if (socketCaptureAvailable && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            var current = WindowsNetworkActivity.Capture(processNames).ToDictionary(socket => socket.Key, StringComparer.OrdinalIgnoreCase);
                            RecordSocketChanges(current);
                        }
                        catch (Exception ex)
                        {
                            socketCaptureAvailable = false;
                            if (!socketCaptureErrorLogged)
                            {
                                socketCaptureErrorLogged = true;
                                var errorRecord = new NetworkObservationRecord
                                {
                                    TimeUtc = DateTime.UtcNow,
                                    EventType = "Сеанс",
                                    Source = "ручной сеанс",
                                    Details = "Снимок соединений приложений недоступен: " + LimitDetails(ex.Message)
                                };
                                NetworkDiagnosticsStore.Add(errorRecord);
                                AddRecentNetworkObservation(errorRecord);
                                AppLog.Warn("[Сеть] Снимок соединений приложений временно недоступен; подробности сохранены в локальной истории диагностики.");
                            }
                        }
                    }

                    var remaining = Math.Max(0, (int)Math.Ceiling((endsAt - DateTime.UtcNow).TotalSeconds));
                    NetworkSessionStatus = $"Идёт запись · осталось {remaining} с · сайты: {targets.Count} · процессы: {processNames.Length}";
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                NetworkSessionStatus = _networkSessionStoppedByUser
                    ? "Сеанс остановлен пользователем"
                    : "Отведённые две минуты истекли";
            }
            catch (Exception ex)
            {
                sessionError = LimitDetails(ex.Message);
                NetworkSessionStatus = "Ошибка сеанса: " + sessionError;
                var errorRecord = new NetworkObservationRecord
                {
                    TimeUtc = DateTime.UtcNow,
                    EventType = "Сеанс",
                    Source = "ручной сеанс",
                    Details = "Ошибка сеанса: " + sessionError
                };
                NetworkDiagnosticsStore.Add(errorRecord);
                AddRecentNetworkObservation(errorRecord);
                AppLog.Warn("[Сеть] Сеанс наблюдения завершился с ошибкой.");
            }
            finally
            {
                var duration = DateTime.UtcNow - started;
                var completed = new NetworkObservationRecord
                {
                    TimeUtc = DateTime.UtcNow,
                    EventType = "Сеанс",
                    Source = "ручной сеанс",
                    Details = $"Сеанс завершён через {(int)duration.TotalSeconds} с; собрано записей: {Math.Max(0, NetworkDiagnosticsStore.CountRecent - observationsBeforeSession)}"
                };
                NetworkDiagnosticsStore.Add(completed);
                AddRecentNetworkObservation(completed);
                _observedSockets.Clear();
                IsRecordingNetwork = false;
                IsBusy = false;
                NetworkSessionStatus = sessionError != null
                    ? "Сеанс завершился с ошибкой; предыдущие события сохранены"
                    : _networkSessionStoppedByUser
                        ? "Сеанс остановлен; события сохранены локально"
                        : "Сеанс завершён; события сохранены локально";
                _observationCts?.Dispose();
                _observationCts = null;
                AppLog.Info("[Сеть] " + NetworkSessionStatus);
            }
        }

        private void RecordSocketChanges(Dictionary<string, ObservedSocket> current)
        {
            foreach (var socket in current.Values)
            {
                if (_observedSockets.TryGetValue(socket.Key, out var previous))
                {
                    if (previous.State.Equals(socket.State, StringComparison.OrdinalIgnoreCase)) continue;
                    AddSocketObservation(socket, "Состояние изменилось");
                }
                else
                {
                    AddSocketObservation(socket, socket.Protocol == "TCP" ? "Соединение замечено" : "UDP-сокет открыт");
                }
            }

            foreach (var old in _observedSockets.Where(pair => !current.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray())
                AddSocketObservation(old, old.Protocol == "TCP"
                    ? "Не обнаружено в следующем снимке (закрытие не подтверждено)"
                    : "UDP-сокет больше не обнаружен в следующем снимке");

            _observedSockets.Clear();
            foreach (var pair in current) _observedSockets[pair.Key] = pair.Value;
        }

        private void AddSocketObservation(ObservedSocket socket, string eventText)
        {
            var bypassStatus = _main.Bypass.GetStatus();
            var record = new NetworkObservationRecord
            {
                TimeUtc = DateTime.UtcNow,
                EventType = "Сокет",
                Source = "ручной сеанс",
                ProcessName = socket.ProcessName,
                ProcessId = socket.ProcessId,
                Protocol = socket.Protocol,
                LocalEndpoint = socket.LocalEndpoint,
                RemoteEndpoint = socket.RemoteEndpoint,
                ConnectionState = socket.State,
                BypassRunning = bypassStatus.IsRunning,
                StrategyName = bypassStatus.StrategyName,
                Details = eventText
            };
            NetworkDiagnosticsStore.Add(record);
            AddRecentNetworkObservation(record);
        }

        private static string LimitDetails(string text)
            => string.IsNullOrWhiteSpace(text) ? "" : text.Length > 240 ? text.Substring(0, 240) : text;

        private void StopNetworkSession()
        {
            if (!IsRecordingNetwork) return;
            _networkSessionStoppedByUser = true;
            _observationCts?.Cancel();
        }

        private void ExportNetworkObservations()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Сохранить журнал сетевой диагностики",
                    Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
                    FileName = $"zapret-network-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                    InitialDirectory = AppPaths.LogDir
                };
                var preview = NetworkDiagnosticsStore.CreateSanitizedExportPreview();
                var sample = string.Join(Environment.NewLine, preview.Take(8).Select(item => item.LineText));
                var confirmation = System.Windows.MessageBox.Show(
                    $"Предварительный просмотр (первые {Math.Min(8, preview.Count)} из {preview.Count} записей):\n\n{sample}\n\nЭкспорт скроет адреса сайтов и IP, имена процессов и PID. Содержимое пакетов не собиралось. Продолжить к выбору файла?",
                    "Проверка и очистка экспорта", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                if (confirmation != System.Windows.MessageBoxResult.Yes) return;
                if (dialog.ShowDialog() != true) return;
                NetworkDiagnosticsStore.ExportTo(dialog.FileName, sanitize: true);
                SetMessage("Очищенный журнал сетевой диагностики сохранён.", "Success");
            }
            catch (Exception ex)
            {
                SetMessage("Не удалось экспортировать сетевую диагностику: " + ex.Message, "Danger");
            }
        }

        private void ClearNetworkObservations()
        {
            var answer = System.Windows.MessageBox.Show(
                "Удалить локальный журнал сетевой диагностики за последние 24 часа?",
                "Очистить журнал сети", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
            NetworkDiagnosticsStore.Clear();
            RecentNetworkObservations.Clear();
            NetworkObservationCount = 0;
            NetworkSessionStatus = "Локальный журнал очищен";
        }

        private async Task BackgroundCheckAsync()
        {
            if (!Settings.ResourceMonitoringEnabled || IsBusy) return;
            await CheckAllAsync(isBackground: true);
            // Во время сеанса наблюдения не меняем стратегию: иначе нельзя будет сравнить
            // состояние сети до и после какого-либо вмешательства.
            if (IsRecordingNetwork) return;
            // Фоновая проверка лучшей стратегии, если включено в Настройках → Сеть
            if (Settings.AutoSwitchToBestStrategy && Settings.StrategyAutoSwitchAllowed && _main.Bypass.GetStatus().IsRunning)
            {
                var minutes = Math.Clamp(Settings.BestStrategyCheckMinutes, 5, 120);
                if ((DateTime.Now - _lastBestCheck).TotalMinutes < minutes) return;
                _lastBestCheck = DateTime.Now;
                try { await TrySwitchToBestStrategyAsync(); } catch { }
            }
        }

        // v1.32.3: нормализовано — только рабочие (YouTube+Discord) и с уведомлением, а не «абы какие»
        private async Task TrySwitchToBestStrategyAsync()
        {
            if (!Settings.StrategyAutoSwitchAllowed) return;
            var before = _main.Bypass.GetStatus();
            // сортируем по реальным тестам, пропуск битых
            var candidates = _main.Strategies.Items
                .Where(s => !s.Name.Equals(before.StrategyName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.TestResult?.PassedCount ?? -1)
                .ThenByDescending(s => s.IsRecommended)
                .ThenBy(s => s.Name)
                .Take(6).ToList();
            if (candidates.Count == 0) return;
            // фильтруем заведомо нерабочие по последним тестам
            candidates = candidates.Where(c => c.TestResult == null || c.TestResult.PassedCount > 0).ToList();
            if (candidates.Count == 0) return;
            // для сравнения скорости используем YouTube как критичный ресурс, а не случайный первый
            var ytTarget = MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204");
            var discordTarget = MonitorTarget.CreateBuiltIn("Discord", "https://discord.com/api/v9/gateway");
            var currentStrat = _main.Strategies.Items.FirstOrDefault(s => s.Name == before.StrategyName) ?? candidates[0];
            var currentYt = await _main.Bypass.TestStrategyOnResourceAsync(currentStrat, ytTarget);
            var currentDiscord = await _main.Bypass.TestStrategyOnResourceAsync(currentStrat, discordTarget);
            // если текущая уже не подходит (YouTube/Discord не работают) — не сравниваем скорость, а ищем любую рабочую (логика Diagnose)
            bool currentIsSuitable = currentYt.Ok && currentDiscord.Ok;
            long currentMs = currentYt.Ok ? currentYt.Milliseconds : long.MaxValue;
            (StrategyInfo Strategy, ResourceProbeResult Probe) best = (null!, null!);
            foreach (var c in candidates)
            {
                // пропуск если уже известно что YouTube не работает
                if (c.TestResult != null && c.TestResult.Started && c.TestResult.Checks.Count >= 3)
                {
                    var ytCheck = c.TestResult.Checks.FirstOrDefault(x => x.Title == "YouTube");
                    if (ytCheck != null && !ytCheck.Ok) continue;
                    if (!c.TestResult.IsSuitable && c.TestResult.PassedCount < 3) continue;
                }
                var ytProbe = await _main.Bypass.TestStrategyOnResourceAsync(c, ytTarget);
                if (!ytProbe.Ok) continue;
                var discordProbe = await _main.Bypass.TestStrategyOnResourceAsync(c, discordTarget);
                if (!discordProbe.Ok) continue;
                // требуем чтобы была действительно подходящей (YouTube+Discord)
                // скорость сравниваем только если текущая тоже подходящая
                if (best.Strategy == null)
                    best = (c, ytProbe);
                else if (!currentIsSuitable)
                {
                    // текущая битая — берём первую рабочую
                    best = (c, ytProbe);
                    break;
                }
                else if (ytProbe.Milliseconds + 15 < currentMs && ytProbe.Milliseconds < (best.Probe?.Milliseconds ?? long.MaxValue))
                    best = (c, ytProbe);
            }
            if (best.Strategy == null || best.Probe == null) return;
            if (currentIsSuitable && currentMs != long.MaxValue && best.Probe!.Milliseconds + 15 >= currentMs) return;
            var prevName = before.StrategyName;
            _main.StrategiesPage.SelectAsDefault(best.Strategy);
            var res = await StartSelectedStrategyAsync(best.Strategy, before);
            if (res.Ok)
            {
                var msg = $"🔄 Стратегия сменена (фон): «{prevName}» → «{best.Strategy!.Name}» (YouTube {best.Probe!.Milliseconds} мс, Discord OK)";
                AppLog.Info($"[Фон] {msg} vs {currentMs} мс");
                // v1.32.3: уведомление всегда при смене стратегии (ранее только лог)
                NotificationRequested?.Invoke(msg);
                // также пробрасываем через MainViewModel для системного трея
                try { _main.NotifyStrategyAutoSwitched(msg); } catch {}
                SetMessage(msg, "Success");
            }
            else
            {
                AppLog.Warn($"[Фон] Не удалось переключить на «{best.Strategy!.Name}»: {res.Message}");
            }
        }

        private int GetInterval() => Math.Clamp(Settings.ResourceMonitoringIntervalMinutes, 5, 120);

        private void SetMessage(string text, string key)
        {
            Message = text;
            MessageKey = key;
        }

        public event Action<string>? NotificationRequested;

        public void Reload()
        {
            MonitorTargetStore.EnsureDefaults(Settings);
            SettingsStore.Save(Settings);
            Targets.Clear();
            foreach (var target in Settings.MonitorTargets) Targets.Add(target);
            SelectedTarget = Targets.FirstOrDefault();
            _timer.Interval = TimeSpan.FromMinutes(GetInterval());
            if (Settings.ResourceMonitoringEnabled) _timer.Start(); else _timer.Stop();
            Raise(nameof(ResourceMonitoringEnabled));
            Raise(nameof(AutoRecoverStrategy));
            Raise(nameof(AutoSwitchAllowed));
            Raise(nameof(MonitoringIntervalMinutes));
        }

        public void RefreshTheme()
        {
            var results = Results.ToList();
            Results.Clear();
            foreach (var result in results) Results.Add(result);
            Raise(nameof(MessageKey));
            Raise(nameof(DiagnosisKey));
        }

        public void Stop()
        {
            _timer.Stop();
            _networkHistoryCleanupTimer.Stop();
            StopNetworkSession();
        }
    }
}
