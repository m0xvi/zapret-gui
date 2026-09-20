using System;
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
    /// Управляемый мастер первого запуска. Ни одна кнопка здесь не запускается
    /// автоматически: действия, затрагивающие службу или сеть, предваряются объяснением.
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
            CheckAdminCommand = new RelayCommand(CheckAdmin);
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
            FinishManualCommand = new RelayCommand(() => FinishWizard(true));
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync,
                () => !IsBusy && IsAdmin && SelectedStrategy != null);
            SkipWizardCommand = new RelayCommand(() => FinishWizard(true));
            OpenUpdatesCommand = new RelayCommand(() => _main.Navigate("updates"));
        }

        public AppSettings Settings => _main.Settings;
        public bool IsAdmin => _main.IsAdmin;
        public bool IsEngineReady => EngineService.IsEngineReady(Settings.EnginePath);
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
            get => string.IsNullOrWhiteSpace(_busyText) ? "Выполняю выбранную операцию…" : _busyText;
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
            0 => "Права и границы действий",
            1 => "Папка и движок",
            2 => "Диагностика без изменений",
            3 => "Проверка и выбор стратегии",
            4 => "Пробный запуск",
            _ => "Завершение"
        };

        public string CurrentStepDescription => CurrentStep switch
        {
            0 => "Проверьте права. Без администратора чтение и диагностика остаются доступны, а служба, WinDivert и системные исправления будут недоступны.",
            1 => "Движок скачивается только по вашей команде из официального репозитория. Автоматического скачивания до согласия нет.",
            2 => "Диагностика только читает состояние системы. Она не запускает службу, не меняет proxy, hosts, TCP timestamps или сеть.",
            3 => "Проверка стратегий временно запускает их по очереди и возвращает прежнее состояние. Лучший кандидат не включается автоматически.",
            4 => "Пробный запуск проверяет выбранную стратегию и после проверки восстанавливает прежний режим обхода.",
            _ => "Выберите безопасный режим или явно подтвердите установку службы. Ничего системного не меняется без отдельной кнопки."
        };

        public bool IsAdminStep => CurrentStep == 0;
        public bool IsEngineStep => CurrentStep == 1;
        public bool IsDiagnosticsStep => CurrentStep == 2;
        public bool IsStrategyStep => CurrentStep == 3;
        public bool IsTrialStep => CurrentStep == 4;
        public bool IsFinishStep => CurrentStep == 5;

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
            : "Профиль: " + SelectedStrategyName;

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
            ? "Служба и сеть не меняются автоматически"
            : "Автозапуск разрешён настройками приложения; системные действия всё равно требуют кнопки";

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
            ? "Права администратора подтверждены."
            : "Права администратора не получены. Диагностика доступна, но для службы, WinDivert, hosts, proxy, TCP timestamps и reset network понадобится перезапуск от администратора.";

        public string EngineStatusText => IsEngineReady
            ? $"Движок найден: {Settings.EnginePath}"
            : "Движок не найден. Установка изменит только выбранную папку движка и потребует подтверждённого действия.";

        public string DiagnosticsStatusText => Settings.FirstLaunchDiagnosticsCompleted
            ? "Диагностика уже выполнена; повторный запуск безопасен и доступен."
            : "Диагностика ещё не запускалась.";

        public string StrategyStatusText => Settings.StrategyTestsCompleted
            ? "Проверка стратегий завершена. Результат не включает лучший кандидат автоматически."
            : "Стратегии не проверялись.";

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
            ? "Пробный запуск завершён, прежнее состояние восстановлено."
            : "Пробный запуск не выполнялся.";

        public string FinishStatusText => Settings.FirstLaunchWizardCompleted
            ? "Мастер завершён. Его можно открыть снова из меню, чтобы посмотреть выбранный режим."
            : "После завершения приложение останется в режиме наблюдения, если включён безопасный режим.";

        public string ReadinessText => _main.ReadinessText;
        public string ReadinessDetails => _main.ReadinessDetails;
        public string ReadinessKey => _main.ReadinessKey;

        public ServiceHealthSnapshot? ServiceHealth => _serviceHealth;
        public bool HasServiceHealth => _serviceHealth != null;
        public string ServiceHealthCheckedText => _serviceHealth?.CheckedText ?? "Кэш состояния BFE и WinDivert пока пуст";
        public string BfeHealthText => _serviceHealth == null
            ? "не проверена"
            : _serviceHealth.BfeText;
        public string WinDivertHealthText => _serviceHealth == null
            ? "не проверена"
            : _serviceHealth.WinDivertText;
        public string WinDivert14HealthText => _serviceHealth == null
            ? "не проверена"
            : _serviceHealth.WinDivert14Text;
        public string BfeHealthKey => _serviceHealth?.BfeKey ?? "Muted";
        public string WinDivertHealthKey => _serviceHealth?.WinDivertKey ?? "Muted";
        public string BfeRecoveryText => _serviceHealth?.BfeNeedsRecovery == true
            ? "BFE нужна драйверу WinDivert; исправление запускается только после подтверждения"
            : "BFE работает, исправление не требуется";
        public string WinDivertRecoveryText => _serviceHealth?.HasWinDivertLeftovers == true
            ? "Найдены остаточные службы; удаление допустимо только после остановки обхода"
            : "Остаточных служб WinDivert нет";

        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand CheckAdminCommand { get; }
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
            Raise(nameof(AdminStatusText));
            Raise(nameof(EngineStatusText));
            Raise(nameof(DiagnosticsStatusText));
            Raise(nameof(StrategyStatusText));
            Raise(nameof(TrialStatusText));
            Raise(nameof(FinishStatusText));
        }

        private void RaiseCommands()
        {
            (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NextCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
            return CurrentStep switch
            {
                0 => IsAdmin,
                1 => IsEngineReady,
                2 => Settings.FirstLaunchDiagnosticsCompleted,
                3 => Settings.StrategyTestsCompleted,
                4 => _trialCompleted,
                _ => false
            };
        }

        private void Previous() => CurrentStep--;

        private void Next()
        {
            if (CanAdvance()) CurrentStep++;
        }

        private void ContinueReadOnly()
        {
            CurrentStep = 2;
            SetStatus("Продолжаем без администратора. Доступны read-only диагностика и экспорт отчёта; системные действия будут пропущены.", "Warning");
        }

        private void CheckAdmin()
        {
            _main.RefreshReadiness();
            Raise(nameof(AdminStatusText));
            SetStatus(IsAdmin ? "Права администратора доступны." : "Продолжить можно в режиме диагностики; системные действия потребуют перезапуска от администратора.", IsAdmin ? "Success" : "Warning");
            RaiseCommands();
        }

        private void RefreshServiceHealth()
        {
            if (IsBusy) return;
            _serviceHealth = ServiceHealthCache.Capture();
            RaiseServiceHealth();
            SetStatus("Состояние BFE и WinDivert обновлено. Изменений в системе не выполнялось.", "Info");
        }

        private void RepairBfe()
        {
            if (!IsAdmin) return;
            var answer = MessageBox.Show(
                "Будет включён автозапуск и запущена служба BFE (Base Filtering Engine). Это системное изменение требует администратора. Продолжить?",
                "Восстановление BFE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "Включаю автозапуск и запускаю службу BFE…");
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
                "Будут остановлены и удалены только остаточные службы WinDivert и WinDivert14. Текущий обход должен быть остановлен; файлы движка и службу zapret эта кнопка не удаляет. Продолжить?",
                "Восстановление WinDivert", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "Останавливаю и удаляю остаточные службы WinDivert…");
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
            Raise(nameof(BfeRecoveryText));
            Raise(nameof(WinDivertRecoveryText));
            RaiseCommands();
        }

        private async Task InstallEngineAsync()
        {
            if (!IsAdmin)
            {
                SetStatus("Установка движка требует прав администратора. Откройте приложение от имени администратора и повторите действие.", "Warning");
                return;
            }

            SetBusy(true, "Скачиваю и устанавливаю комплект движка из официального репозитория…");
            try
            {
                var installed = await _main.Updates.EnsureEngineInstalledAsync();
                _main.Strategies.Refresh();
                _main.StrategiesPage.Refresh();
                RefreshStrategyList();
                _main.Home.ReloadFromEngine();
                _main.RefreshReadiness();
                SetStatus(installed ? "Движок установлен и готов." : "Движок не удалось установить. Подробности доступны на странице «Обновления».", installed ? "Success" : "Danger");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RunDiagnosticsAsync()
        {
            SetBusy(true, "Выполняю диагностику системных условий и служб…");
            try
            {
                await _main.Diagnostics.RunAsync();
                _serviceHealth = ServiceHealthCache.Load();
                RaiseServiceHealth();
                Settings.FirstLaunchDiagnosticsCompleted = _main.Diagnostics.HasResults;
                SettingsStore.Save(Settings);
                Raise(nameof(DiagnosticsStatusText));
                SetStatus(Settings.FirstLaunchDiagnosticsCompleted
                    ? "Диагностика завершена. Изменения не вносились."
                    : "Диагностика не вернула результатов; повторите попытку на странице «Диагностика».",
                    Settings.FirstLaunchDiagnosticsCompleted ? "Success" : "Warning");
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

            var answer = MessageBox.Show(
                "Проверка временно запускает каждую стратегию и выполняет сетевые пробы. После каждой пробы приложение пытается восстановить прежнее состояние. Служба не устанавливается; только стратегия с подтверждённым полным успехом будет выбрана основной. Продолжить?",
                "Подтверждение проверки стратегий", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, "Запускаю проверку стратегий…");
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
                SetStatus(batch?.Best == null
                    ? "Проверка завершена, подходящий кандидат не найден."
                    : batch.Best.IsSuitable
                        ? $"Проверка завершена. Стратегия «{batch.Best.Strategy.Name}» с полным успехом выбрана основной."
                        : $"Проверка завершена. Лучший результат: «{batch.Best.Strategy.Name}», но полного успеха нет — выбор не изменён.",
                    batch?.Best == null ? "Warning" : "Success");
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
            SetStatus($"Стратегия «{strategy.Name}» сохранена для ручного запуска. Автозапуск не включён.", "Success");
        }

        private async Task RunTrialAsync()
        {
            var strategy = SelectedStrategy;
            if (strategy == null) return;
            var answer = MessageBox.Show(
                $"Стратегия «{strategy.Name}» будет временно запущена для проверки YouTube, Discord и GitHub. Это изменит сетевой трафик на время теста, но после него прежнее состояние будет восстановлено. Продолжить?",
                "Подтверждение пробного запуска", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, $"Выполняю пробный запуск стратегии «{strategy.Name}»…");
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
                $"Будет создана и запущена системная служба zapret со стратегией «{strategy.Name}». Это действие требует администратора и изменяет службу, WinDivert и TCP timestamps. Выполнить его сейчас?",
                "Подтверждение установки службы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            SetBusy(true, $"Устанавливаю системную службу zapret со стратегией «{strategy.Name}»…");
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

        private void FinishWizard(bool safeMode)
        {
            IsSafeModeChoice = safeMode;
            Settings.SafeMode = safeMode;
            if (safeMode) Settings.AutoStartBypass = false;
            Settings.FirstLaunchWizardCompleted = true;
            SettingsStore.Save(Settings);
            _main.RefreshReadiness();
            Raise(nameof(FinishStatusText));
            SetStatus(safeMode
                ? "Мастер завершён. Безопасный режим не запускает обход и не меняет службу или сеть автоматически."
                : "Мастер завершён. Автоматические действия разрешены только настройками, системные изменения требуют отдельной кнопки.", "Success");
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
