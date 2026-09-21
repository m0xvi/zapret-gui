using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
        private bool _newResourceIsGame;
        private string _message = "";
        private string _messageKey = "Info";
        private string _lastCheckText = "Проверка ещё не выполнялась";
        private DateTime _lastRecovery = DateTime.MinValue;
        private int _consecutiveStrategyFailures;
        private string _failureTargetId = "";
        private const int RecoveryFailureThreshold = 2;

        public MonitoringViewModel(MainViewModel main)
        {
            _main = main;
            MonitorTargetStore.EnsureDefaults(main.Settings);
            SettingsStore.Save(main.Settings);
            Targets = new ObservableCollection<MonitorTarget>(main.Settings.MonitorTargets);

            CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => !IsBusy && Targets.Any(t => t.Enabled));
            DiagnoseCommand = new AsyncRelayCommand(DiagnoseSelectedAsync, () => !IsBusy && SelectedTarget != null);
            AddResourceCommand = new RelayCommand(AddResource);
            RemoveResourceCommand = new RelayCommand(RemoveResource, p => p is MonitorTarget target && !target.IsBuiltIn);
            AddToBypassListCommand = new RelayCommand(AddSelectedToBypassList, () => SelectedTarget != null);
            SelectedTarget = Targets.FirstOrDefault();

            _timer = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromMinutes(GetInterval())
            };
            _timer.Tick += async (_, _) => await BackgroundCheckAsync();
            if (Settings.ResourceMonitoringEnabled) _timer.Start();
        }

        public AppSettings Settings => _main.Settings;
        public ObservableCollection<MonitorTarget> Targets { get; }
        public ObservableCollection<ResourceProbeResult> Results { get; } = new();

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
                SettingsStore.Save(Settings);
                Raise(nameof(AutoRecoverStrategy));
            }
        }

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
        public ICommand AddResourceCommand { get; }
        public ICommand RemoveResourceCommand { get; }
        public ICommand AddToBypassListCommand { get; }

        public async Task CheckAllAsync()
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

                if (Settings.AutoRecoverStrategy && !Settings.SafeMode && _main.Bypass.GetStatus().IsRunning)
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
                SetMessage(Diagnosis.Summary, Diagnosis.StatusKey);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DiagnoseAndRecoverAsync(MonitorTarget target)
        {
            Diagnosis = await _main.Bypass.DiagnoseResourceAsync(target);
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
                        if (Settings.MonitorNotificationsEnabled) NotificationRequested?.Invoke(Message);
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

            _main.StrategiesPage.SelectAsDefault(best.Strategy);
            var result = await StartSelectedStrategyAsync(best.Strategy, before);
            _main.Home.RefreshStatus();
            SetMessage(result.Ok
                ? $"Выбрана стратегия «{best.Strategy.Name}» — ресурс восстановлен"
                : "Новая стратегия найдена, но запустить её не удалось: " + result.Message,
                result.Ok ? "Success" : "Danger");
            if (Settings.MonitorNotificationsEnabled)
                NotificationRequested?.Invoke(Message);
        }

        private async Task RecoverGameStrategyAsync(MonitorTarget target, BypassStatus before)
        {
            var current = Diagnosis?.WithBypass;
            var candidates = _main.Strategies.Items
                .Where(s => !s.Name.Equals(before.StrategyName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.IsRecommended)
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

            _main.StrategiesPage.SelectAsDefault(best.Strategy);
            var result = await StartSelectedStrategyAsync(best.Strategy, before);
            _main.Home.RefreshStatus();
            SetMessage(result.Ok
                ? $"Для игры выбрана «{best.Strategy.Name}»: TCP {best.Probe.Milliseconds} мс"
                : "Игровая стратегия найдена, но запустить её не удалось: " + result.Message,
                result.Ok ? "Success" : "Danger");
            if (Settings.MonitorNotificationsEnabled)
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

        private async Task BackgroundCheckAsync()
        {
            if (!Settings.ResourceMonitoringEnabled || IsBusy) return;
            await CheckAllAsync();
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
        }
    }
}
