using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>
    /// Минималистичный и понятный мастер первого запуска.
    /// </summary>
    public sealed class FirstLaunchViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private int _step;
        private bool _isBusy;
        private string _busyText = "";
        private bool _trialCompleted;
        private bool _isSafeModeChoice = true;
        private string _selectedStrategyName = "";
        private string _customHostInput = "";
        private ServiceHealthSnapshot? _serviceHealth;
        private string _status = "";
        private string _statusKey = "Info";

        public FirstLaunchViewModel(MainViewModel main)
        {
            _main = main;
            _selectedStrategyName = main.Settings.SelectedStrategy;
            _serviceHealth = ServiceHealthCache.Load();
            RefreshStrategyList();
            _main.StrategiesPage.PropertyChanged += StrategiesPageOnPropertyChanged;

            PreviousCommand = new RelayCommand(Previous, () => CurrentStep > 0 && !IsBusy);
            NextCommand = new RelayCommand(Next, CanAdvance);
            SkipStepCommand = new RelayCommand(SkipStep, () => CurrentStep < StepCount - 1 && !IsBusy);
            CheckAdminCommand = new RelayCommand(CheckAdmin);
            RestartElevatedCommand = new RelayCommand(() => _main.RestartAsAdminCommand.Execute(null));
            ContinueReadOnlyCommand = new RelayCommand(ContinueReadOnly);
            InstallEngineCommand = new AsyncRelayCommand(InstallEngineAsync, () => !IsBusy && !IsEngineReady);
            RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync, () => !IsBusy);
            RefreshServiceHealthCommand = new RelayCommand(RefreshServiceHealth, () => !IsBusy);
            RepairBfeCommand = new RelayCommand(RepairBfe, () => !IsBusy && IsAdmin);
            RepairWinDivertCommand = new RelayCommand(RepairWinDivert, () => !IsBusy && IsAdmin);
            RunStrategyChecksCommand = new AsyncRelayCommand(RunStrategyChecksAsync,
                () => !IsBusy && StrategyNames.Count > 0);
            SaveSelectedStrategyCommand = new RelayCommand(SaveSelectedStrategy,
                () => !string.IsNullOrWhiteSpace(SelectedStrategyName));
            RunTrialCommand = new AsyncRelayCommand(RunTrialAsync,
                () => !IsBusy && IsAdmin && SelectedStrategy != null);
            FinishManualCommand = new RelayCommand(() => FinishWizard(IsSafeModeChoice));
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync,
                () => !IsBusy && IsAdmin && SelectedStrategy != null);
            SkipWizardCommand = new RelayCommand(() => FinishWizard(true));
            OpenUpdatesCommand = new RelayCommand(() => _main.Navigate("updates"));
            AddCustomHostCommand = new RelayCommand(AddCustomHost, () => !string.IsNullOrWhiteSpace(CustomHostInput) && !IsBusy);
            SkipCustomHostsCommand = new RelayCommand(() => SetStatus("Добавление своих сайтов пропущено — вы всегда можете добавить их позже в «Обзор → Проверка соединения» или «Проверка → Экспресс»", "Info"));
            RunWizardFullCheckCommand = new AsyncRelayCommand(RunWizardFullCheckAsync, () => !IsBusy && !IsEngineReady == false);

        }

        public AppSettings Settings => _main.Settings;
        public bool IsAdmin => _main.IsAdmin;
        public bool IsEngineReady => EngineService.IsEngineReady(Settings.EnginePath);
        public string EngineVersionText => _main.EngineVersionText;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                Raise(nameof(BusyText));
                RaiseCommands();
            }
        }

        public string BusyText
        {
            get => string.IsNullOrWhiteSpace(_busyText) ? "Выполняю операцию…" : _busyText;
            private set => Set(ref _busyText, value);
        }

        private void SetBusy(bool busy, string text = "")
        {
            BusyText = text;
            IsBusy = busy;
        }

        public int CurrentStep
        {
            get => _step;
            private set
            {
                if (!Set(ref _step, Math.Clamp(value, 0, StepCount - 1))) return;
                RefreshStrategyList();
                Raise(nameof(CurrentStepNumber));
                Raise(nameof(StepCounterText));
                Raise(nameof(StepTitleText));
                Raise(nameof(CurrentStepTitle));
                Raise(nameof(CurrentStepDescription));
                Raise(nameof(IsSkipVisible));
                RaiseStepVisibility();
                RaiseCommands();
            }
        }

        public int StepCount => 6;
        public int CurrentStepNumber => CurrentStep + 1;
        public string StepCounterText => $"Шаг {CurrentStepNumber} из {StepCount}";
        public string StepTitleText => $"ШАГ {CurrentStepNumber}";

        public string CurrentStepTitle => CurrentStep switch
        {
            0 => "Права администратора",
            1 => "Движок zapret",
            2 => "Диагностика системы",
            3 => "Выбор стратегии обхода",
            4 => "Пробный запуск",
            _ => "Завершение настройки"
        };

        public string CurrentStepDescription => CurrentStep switch
        {
            0 => "Для управления службой Windows, драйвером WinDivert и системным обходом требуются права администратора.",
            1 => "Движок выполняет непосредственную модификацию пакетов для обхода сетевых ограничений.",
            2 => "Проверка готовности системных служб Windows (BFE) и драйвера WinDivert.",
            3 => "Выберите стратегию обхода или запустите автоматический тест для подбора лучшей.",
            4 => "Временная проверка работы выбранной стратегии на контрольных ресурсах без изменения постоянных настроек.",
            _ => "Сохранение параметров и выбор режима запуска."
        };

        public bool IsAdminStep => CurrentStep == 0;
        public bool IsEngineStep => CurrentStep == 1;
        public bool IsDiagnosticsStep => CurrentStep == 2;
        public bool IsStrategyStep => CurrentStep == 3;
        public bool IsTrialStep => CurrentStep == 4;
        public bool IsFinishStep => CurrentStep == 5;
        public bool IsSkipVisible => CurrentStep >= 2 && CurrentStep <= 4;

        public ObservableCollection<string> StrategyNames { get; } = new();

        public string SelectedStrategyName
        {
            get => _selectedStrategyName;
            set
            {
                if (Set(ref _selectedStrategyName, value ?? ""))
                {
                    Raise(nameof(SelectedStrategy));
                    Raise(nameof(SelectedStrategyText));
                    RaiseCommands();
                }
            }
        }

        public StrategyInfo? SelectedStrategy => _main.Strategies.Find(SelectedStrategyName);
        public string SelectedStrategyText => string.IsNullOrWhiteSpace(SelectedStrategyName)
            ? "Стратегия не выбрана"
            : "Выбрана: " + SelectedStrategyName;

        public string CustomHostInput
        {
            get => _customHostInput;
            set
            {
                if (Set(ref _customHostInput, value ?? ""))
                {
                    (AddCustomHostCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string CustomHostsHint => "Введите адреса через запятую или с новой строки, например: youtube.com, mygame.example.com, https://custom.site";

        public string CustomHostsStatus
        {
            get
            {
                var count = _main.Settings.MonitorTargets.Count(t => !t.IsBuiltIn);
                return count == 0 ? "Свои сайты пока не добавлены — можно пропустить" : $"Добавлено своих сайтов: {count}";
            }
        }

        public bool IsSafeModeChoice
        {
            get => _isSafeModeChoice;
            set
            {
                if (!Set(ref _isSafeModeChoice, value)) return;
                Raise(nameof(SafeModeChoiceText));
            }
        }

        public string SafeModeChoiceText => IsSafeModeChoice
            ? "Обход и служба не запускаются автоматически без вашего подтверждения"
            : "Разрешён автозапуск и автоматическое применение настроек";

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public string StatusKey
        {
            get => _statusKey;
            private set => Set(ref _statusKey, value);
        }

        public string AdminStatusText => IsAdmin
            ? "Права администратора подтверждены. Все функции приложения и системные службы доступны."
            : "Приложение запущено без прав администратора. Доступны чтение настроек и диагностика; для запуска обхода и службы потребуется перезапуск.";

        public string AdminStatusKey => IsAdmin ? "Success" : "Warning";
        public string AdminIcon => IsAdmin ? "\uE73E" : "\uE7BA";
        public string EngineIcon => IsEngineReady ? "\uE73E" : "\uE7BA";
        public string DiagnosticOverallIcon => IsDiagnosticsAllOk ? "\uE73E" : "\uE7BA";

        public string EngineStatusText => IsEngineReady
            ? $"Движок zapret готов к работе (версия {EngineVersionText})."
            : "Движок zapret не установлен. Нажмите «Скачать и установить движок» для автоматической установки.";

        public string EngineStatusKey => IsEngineReady ? "Success" : "Warning";

        public string DiagnosticsStatusText => Settings.FirstLaunchDiagnosticsCompleted
            ? "Диагностика выполнена. Все компоненты проверены."
            : "Диагностика ещё не запускалась.";

        public string StrategyStatusText => Settings.StrategyTestsCompleted
            ? $"Проверка стратегий выполнена. Выбрана: «{SelectedStrategyName}»."
            : "Стратегии ещё не проверялись.";

        public bool StrategyProgressVisible => _main.StrategiesPage.IsTestingAll;
        public string StrategyProgressText => _main.StrategiesPage.TestProgressText;
        public double StrategyProgressValue
        {
            get => _main.StrategiesPage.TestProgressValue;
            set => _main.StrategiesPage.TestProgressValue = value;
        }
        public double StrategyProgressMaximum
        {
            get => _main.StrategiesPage.TestProgressMaximum;
            set => _main.StrategiesPage.TestProgressMaximum = value;
        }
        public bool StrategyProgressIndeterminate
        {
            get => _main.StrategiesPage.TestProgressIndeterminate;
            set => _main.StrategiesPage.TestProgressIndeterminate = value;
        }
        public string StrategyProgressPercentText => _main.StrategiesPage.TestProgressPercentText;

        public string TrialStatusText => _trialCompleted
            ? "Пробный запуск успешно завершён, исходное состояние восстановлено."
            : "Пробный запуск ещё не выполнялся.";

        public string FinishStatusText => $"Стратегия «{SelectedStrategyName}» сохранена.";

        public string ReadinessText => _main.ReadinessText;
        public string ReadinessDetails => _main.ReadinessDetails;
        public string ReadinessKey => _main.ReadinessKey;

        public ServiceHealthSnapshot? ServiceHealth => _serviceHealth;
        public bool HasServiceHealth => _serviceHealth != null;
        public string ServiceHealthCheckedText => _serviceHealth?.CheckedText ?? "Проверка состояния";

        public string BfeHealthText => _serviceHealth == null || _serviceHealth.Bfe == ServiceState.Running
            ? "Работает (OK)"
            : ServiceHealthSnapshot.FormatState(_serviceHealth.Bfe);

        public string BfeHealthKey => _serviceHealth == null || _serviceHealth.Bfe == ServiceState.Running
            ? "Success"
            : "Warning";

        public string WinDivertHealthText => _serviceHealth == null || _serviceHealth.WinDivert == ServiceState.NotInstalled
            ? "Чисто (OK)"
            : "Остаточная служба";

        public string WinDivertHealthKey => _serviceHealth == null || _serviceHealth.WinDivert == ServiceState.NotInstalled
            ? "Success"
            : "Warning";

        public string WinDivert14HealthText => _serviceHealth == null || _serviceHealth.WinDivert14 == ServiceState.NotInstalled
            ? "Чисто (OK)"
            : "Остаточная служба";

        public string WinDivert14HealthKey => _serviceHealth == null || _serviceHealth.WinDivert14 == ServiceState.NotInstalled
            ? "Success"
            : "Warning";

        public bool IsDiagnosticsAllOk => IsAdmin && IsEngineReady && (_serviceHealth == null || (!_serviceHealth.BfeNeedsRecovery && !_serviceHealth.HasWinDivertLeftovers));

        public string DiagnosticOverallSummary => IsDiagnosticsAllOk
            ? "Все системные службы и компоненты в порядке, никаких исправлений не требуется."
            : "Обнаружены системные замечания. Вы можете выполнить исправление кнопками ниже или пропустить шаг.";

        public string DiagnosticOverallKey => IsDiagnosticsAllOk ? "Success" : "Warning";

        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand SkipStepCommand { get; }
        public ICommand CheckAdminCommand { get; }
        public ICommand RestartElevatedCommand { get; }
        public ICommand ContinueReadOnlyCommand { get; }
        public ICommand InstallEngineCommand { get; }
        public ICommand RunDiagnosticsCommand { get; }
        public ICommand RefreshServiceHealthCommand { get; }
        public ICommand RepairBfeCommand { get; }
        public ICommand RepairWinDivertCommand { get; }
        public ICommand RunStrategyChecksCommand { get; }
        public ICommand SaveSelectedStrategyCommand { get; }
        public ICommand RunTrialCommand { get; }
        public ICommand FinishManualCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand SkipWizardCommand { get; }
        public ICommand OpenUpdatesCommand { get; }
        public ICommand AddCustomHostCommand { get; }
        public ICommand SkipCustomHostsCommand { get; }
        public ICommand RunWizardFullCheckCommand { get; }

        private void StrategiesPageOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(StrategiesViewModel.IsTestingAll))
                Raise(nameof(StrategyProgressVisible));
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(StrategiesViewModel.TestProgressText))
                Raise(nameof(StrategyProgressText));
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(StrategiesViewModel.TestProgressValue))
                Raise(nameof(StrategyProgressValue));
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(StrategiesViewModel.TestProgressMaximum))
                Raise(nameof(StrategyProgressMaximum));
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(StrategiesViewModel.TestProgressIndeterminate))
                Raise(nameof(StrategyProgressIndeterminate));
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(StrategiesViewModel.TestProgressPercentText))
                Raise(nameof(StrategyProgressPercentText));
        }

        public void RefreshTheme()
        {
            RefreshStrategyList();
            Raise(nameof(ReadinessText));
            Raise(nameof(ReadinessDetails));
            Raise(nameof(ReadinessKey));
            RaiseServiceHealth();
        }

        public void Reset()
        {
            CurrentStep = 0;
            _trialCompleted = false;
            _isSafeModeChoice = true;
            _selectedStrategyName = _main.Settings.SelectedStrategy;
            RefreshStrategyList();
            SetStatus("", "Info");
            RaiseStepVisibility();
            RaiseCommands();
        }

        public void RefreshStrategyList()
        {
            _main.Strategies.Refresh();
            var names = _main.Strategies.Items.Select(item => item.Name).ToList();
            StrategyNames.Clear();
            foreach (var name in names) StrategyNames.Add(name);
            if (!string.IsNullOrWhiteSpace(Settings.SelectedStrategy) && names.Contains(Settings.SelectedStrategy))
                _selectedStrategyName = Settings.SelectedStrategy;
            else if (names.Count > 0 && string.IsNullOrWhiteSpace(_selectedStrategyName))
                _selectedStrategyName = names[0];

            Raise(nameof(SelectedStrategyName));
            Raise(nameof(SelectedStrategy));
            Raise(nameof(SelectedStrategyText));
            Raise(nameof(EngineStatusText));
            Raise(nameof(EngineStatusKey));
            Raise(nameof(StrategyStatusText));
            RaiseCommands();
        }

        private void RaiseStepVisibility()
        {
            Raise(nameof(IsAdminStep));
            Raise(nameof(IsEngineStep));
            Raise(nameof(IsDiagnosticsStep));
            Raise(nameof(IsStrategyStep));
            Raise(nameof(IsTrialStep));
            Raise(nameof(IsFinishStep));
            Raise(nameof(IsSkipVisible));
            Raise(nameof(AdminStatusText));
            Raise(nameof(AdminStatusKey));
            Raise(nameof(AdminIcon));
            Raise(nameof(EngineStatusText));
            Raise(nameof(EngineStatusKey));
            Raise(nameof(EngineIcon));
            Raise(nameof(DiagnosticsStatusText));
            Raise(nameof(DiagnosticOverallIcon));
            Raise(nameof(DiagnosticOverallSummary));
            Raise(nameof(DiagnosticOverallKey));
            Raise(nameof(StrategyStatusText));
            Raise(nameof(TrialStatusText));
            Raise(nameof(FinishStatusText));
        }

        private void RaiseCommands()
        {
            (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SkipStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (InstallEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RunDiagnosticsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefreshServiceHealthCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RepairBfeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RepairWinDivertCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunStrategyChecksCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SaveSelectedStrategyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RunTrialCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (InstallServiceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private bool CanAdvance()
        {
            if (IsBusy) return false;
            return CurrentStep < StepCount - 1;
        }

        private void Previous() => CurrentStep--;

        private void Next()
        {
            if (CanAdvance()) CurrentStep++;
        }

        private void SkipStep()
        {
            if (CurrentStep < StepCount - 1)
            {
                CurrentStep++;
            }
        }

        private void ContinueReadOnly()
        {
            CurrentStep = 2;
            SetStatus("Режим без администратора: доступны чтение настроек и диагностика.", "Warning");
        }

        private void CheckAdmin()
        {
            _main.RefreshReadiness();
            Raise(nameof(AdminStatusText));
            Raise(nameof(AdminStatusKey));
            SetStatus(IsAdmin ? "Права администратора подтверждены." : "Права администратора не получены.", IsAdmin ? "Success" : "Warning");
            RaiseCommands();
        }

        private void RefreshServiceHealth()
        {
            if (IsBusy) return;
            _serviceHealth = ServiceHealthCache.Capture();
            RaiseServiceHealth();
            SetStatus("Состояние BFE и WinDivert обновлено.", "Info");
        }

        private void RepairBfe()
        {
            if (!IsAdmin) return;
            var answer = MessageBox.Show(
                "Будет включён автозапуск и запущена служба BFE (Base Filtering Engine). Продолжить?",
                "Восстановление BFE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "Запускаю службу BFE…");
            try
            {
                var result = DiagnosticsService.FixBfe();
                _serviceHealth = ServiceHealthCache.Capture();
                RaiseServiceHealth();
                SetStatus(result.Message, result.Ok ? "Success" : "Warning");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void RepairWinDivert()
        {
            if (!IsAdmin) return;
            var answer = MessageBox.Show(
                "Будут остановлены и удалены остаточные службы WinDivert. Продолжить?",
                "Очистка WinDivert", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "Удаляю остаточные службы WinDivert…");
            try
            {
                var result = DiagnosticsService.RemoveDivertLeftovers();
                _serviceHealth = ServiceHealthCache.Capture();
                RaiseServiceHealth();
                SetStatus(result.Message, result.Ok ? "Success" : "Warning");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void RaiseServiceHealth()
        {
            Raise(nameof(ServiceHealth));
            Raise(nameof(HasServiceHealth));
            Raise(nameof(ServiceHealthCheckedText));
            Raise(nameof(BfeHealthText));
            Raise(nameof(WinDivertHealthText));
            Raise(nameof(WinDivert14HealthText));
            Raise(nameof(BfeHealthKey));
            Raise(nameof(WinDivertHealthKey));
            Raise(nameof(WinDivert14HealthKey));
            Raise(nameof(IsDiagnosticsAllOk));
            Raise(nameof(DiagnosticOverallSummary));
            Raise(nameof(DiagnosticOverallKey));
            RaiseCommands();
        }

        private async Task InstallEngineAsync()
        {
            if (!IsAdmin)
            {
                SetStatus("Установка движка требует прав администратора.", "Warning");
                return;
            }

            SetBusy(true, "Скачиваю и устанавливаю движок zapret…");
            try
            {
                var installed = await _main.Updates.EnsureEngineInstalledAsync();
                _main.Strategies.Refresh();
                _main.StrategiesPage.Refresh();
                RefreshStrategyList();
                _main.Home.ReloadFromEngine();
                _main.RefreshReadiness();
                SetStatus(installed ? "Движок zapret установлен и готов к работе." : "Не удалось установить движок.", installed ? "Success" : "Danger");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RunDiagnosticsAsync()
        {
            SetBusy(true, "Выполняю диагностику системы…");
            try
            {
                await _main.Diagnostics.RunAsync();
                _serviceHealth = ServiceHealthCache.Load();
                RaiseServiceHealth();
                Settings.FirstLaunchDiagnosticsCompleted = _main.Diagnostics.HasResults;
                SettingsStore.Save(Settings);
                Raise(nameof(DiagnosticsStatusText));
                SetStatus("Диагностика системы успешно завершена.", "Success");
            }
            finally
            {
                SetBusy(false);
                RaiseCommands();
            }
        }

        private async Task RunStrategyChecksAsync()
        {
            if (StrategyNames.Count == 0)
            {
                SetStatus("Сначала установите движок и обновите список стратегий.", "Warning");
                return;
            }

            SetBusy(true, "Проверяю стратегии…");
            try
            {
                var batch = await _main.StrategiesPage.TestAllAsync();
                if (batch?.Cancelled == true)
                {
                    SetStatus("Проверка стратегий отменена.", "Warning");
                    return;
                }

                Settings.StrategyTestsCompleted = batch != null;
                SettingsStore.Save(Settings);
                Raise(nameof(StrategyStatusText));

                var best = batch?.Best;
                if (best != null)
                {
                    SelectedStrategyName = best.Strategy.Name;
                    Settings.SelectedStrategy = best.Strategy.Name;
                    SettingsStore.Save(Settings);
                    SetStatus(best.IsSuitable
                        ? $"Лучшая стратегия: «{best.Strategy.Name}» ({best.PassedCount}/{best.Checks.Count} проверок OK)."
                        : $"Проверка завершена. Выбрана «{best.Strategy.Name}» ({best.PassedCount}/{best.Checks.Count} проверок OK).", "Success");
                }
            }
            finally
            {
                SetBusy(false);
                RaiseCommands();
            }
        }

        private void SaveSelectedStrategy()
        {
            var strategy = SelectedStrategy;
            if (strategy == null) return;
            Settings.SelectedStrategy = strategy.Name;
            SettingsStore.Save(Settings);
            _main.Home.ReloadFromEngine();
            _main.RefreshReadiness();
            SetStatus($"Стратегия «{strategy.Name}» сохранена.", "Success");
        }

        private async Task RunTrialAsync()
        {
            var strategy = SelectedStrategy;
            if (strategy == null) return;

            SetBusy(true, $"Проверяю стратегию «{strategy.Name}»…");
            try
            {
                var result = await _main.Bypass.TestStrategyAsync(strategy);
                _trialCompleted = result.Started;
                Raise(nameof(TrialStatusText));
                SetStatus(result.Started ? result.SummaryText : result.ErrorMessage,
                    result.Started && result.IsSuitable ? "Success" : "Warning");
            }
            finally
            {
                SetBusy(false);
                RaiseCommands();
            }
        }

        private async Task InstallServiceAsync()
        {
            var strategy = SelectedStrategy;
            if (strategy == null) return;

            var answer = MessageBox.Show(
                $"Установить службу Windows zapret со стратегией «{strategy.Name}»?",
                "Установка службы", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, $"Устанавливаю службу zapret со стратегией «{strategy.Name}»…");
            try
            {
                var result = await _main.Bypass.InstallServiceAsync(strategy,
                    EngineService.GetGameFilterMode(Settings.EnginePath));
                SetStatus(result.Message, result.Ok ? "Success" : "Danger");
                if (result.Ok) FinishWizard(IsSafeModeChoice);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void AddCustomHost()
        {
            var raw = (CustomHostInput ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                SetStatus("Введите хотя бы один адрес", "Warning");
                return;
            }
            var tokens = raw.Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var added = 0;
            var errors = new List<string>();
            foreach (var token in tokens)
            {
                var trimmed = token.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (!MonitorTarget.TryCreate(trimmed, null, out var target, out var error) || target == null)
                {
                    errors.Add($"{trimmed}: {error}");
                    continue;
                }
                if (_main.Settings.MonitorTargets.Any(t => t.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{trimmed}: уже в списке");
                    continue;
                }
                _main.Settings.MonitorTargets.Add(target);
                // также в Home, чтобы сразу видно в Обзоре
                _main.Home.ConnectionTargets.Add(target);
                added++;
            }
            SettingsStore.Save(_main.Settings);
            _main.Monitoring.Reload();
            Raise(nameof(CustomHostsStatus));
            CustomHostInput = "";
            if (added > 0) SetStatus($"Добавлено сайтов: {added}. " + (errors.Count > 0 ? "Ошибки: " + string.Join("; ", errors) : ""), errors.Count > 0 ? "Warning" : "Success");
            else SetStatus("Ничего не добавлено: " + string.Join("; ", errors), "Warning");
            RaiseCommands();
        }

        private async Task RunWizardFullCheckAsync()
        {
            // Если введён текст, но не нажали «Добавить» — попробуем добавить автоматически
            if (!string.IsNullOrWhiteSpace(CustomHostInput))
                AddCustomHost();

            SetBusy(true, "Запускаю быструю настройку: аудит → проверка сайтов → подбор стратегии…");
            try
            {
                await _main.Home.RunFullCheckAsync();
                // после полного теста берём рекомендованную стратегию из Home
                var rec = _main.Home.RecommendedStrategy;
                if (rec != null)
                {
                    SelectedStrategyName = rec.Name;
                    Settings.SelectedStrategy = rec.Name;
                    SettingsStore.Save(Settings);
                    SetStatus($"Мастер: подобрана стратегия «{rec.Name}» — можете переходить к пробному запуску или завершить", "Success");
                }
                else
                {
                    SetStatus("Мастер: проверка завершена, но подходящая стратегия не найдена — попробуйте ручной выбор", "Warning");
                }
            }
            finally
            {
                SetBusy(false);
                Raise(nameof(CustomHostsStatus));
            }
        }

        private void FinishWizard(bool safeMode)
        {
            IsSafeModeChoice = safeMode;
            Settings.SafeMode = safeMode;
            if (safeMode) Settings.AutoStartBypass = false;
            Settings.FirstLaunchWizardCompleted = true;
            SettingsStore.Save(Settings);
            _main.RefreshReadiness();
            _main.Home.ReloadFromEngine();
            _main.Home.RefreshStatus();
            _main.Navigate("home");
        }

        private void SetStatus(string text, string key)
        {
            Status = text;
            StatusKey = key;
            Raise(nameof(ReadinessText));
            Raise(nameof(ReadinessDetails));
            Raise(nameof(ReadinessKey));
            RaiseCommands();
        }
    }
}
