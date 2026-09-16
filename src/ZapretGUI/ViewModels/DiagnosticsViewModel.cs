using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class DiagnosticsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private bool _isRunning;
        private string _progressText = "";
        private string _summary = "Диагностика ещё не запускалась";
        private string _summaryKey = "Muted";
        private string _message = "";
        private string _messageKey = "Info";

        public DiagnosticsViewModel(MainViewModel main)
        {
            _main = main;

            RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
            ClearDiscordCacheCommand = new RelayCommand(ClearDiscordCache);
            ResetNetworkCommand = new RelayCommand(ResetNetwork);
            RemoveServicesCommand = new AsyncRelayCommand(RemoveServicesAsync, () => !IsRunning);
            OpenHostsCommand = new RelayCommand(() => Shell.OpenInNotepad(EngineService.SystemHostsPath));
            OpenSystemNetworkCommand = new RelayCommand(() => Shell.OpenUrl("ms-settings:network"));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            FixTimestampsCommand = new RelayCommand(FixTimestamps);
        }

        public AppSettings Settings => _main.Settings;

        public ObservableCollection<DiagnosticItem> Items { get; } = new();

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (Set(ref _isRunning, value))
                {
                    Raise(nameof(ProgressVisible));
                    (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveServicesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool ProgressVisible => IsRunning;

        public string ProgressText
        {
            get => _progressText;
            private set => Set(ref _progressText, value);
        }

        public string Summary
        {
            get => _summary;
            private set => Set(ref _summary, value);
        }

        public string SummaryKey
        {
            get => _summaryKey;
            private set => Set(ref _summaryKey, value);
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

        public bool HasResults => Items.Count > 0;

        public ICommand RunCommand { get; }
        public ICommand ClearDiscordCacheCommand { get; }
        public ICommand ResetNetworkCommand { get; }
        public ICommand RemoveServicesCommand { get; }
        public ICommand OpenHostsCommand { get; }
        public ICommand OpenSystemNetworkCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand FixTimestampsCommand { get; }

        public async Task RunAsync()
        {
            IsRunning = true;
            Items.Clear();
            Message = "";
            Summary = "Идёт проверка…";
            SummaryKey = "Warning";

            var progress = new Progress<string>(text => ProgressText = text + "…");
            var items = await DiagnosticsService.RunAsync(Settings, progress);

            foreach (var item in items) Items.Add(item);
            Raise(nameof(HasResults));

            var errors = items.Count(i => i.Status == DiagStatus.Error);
            var warnings = items.Count(i => i.Status == DiagStatus.Warning);

            Summary = errors > 0
                ? $"Найдено критичных проблем: {errors}, предупреждений: {warnings}"
                : warnings > 0
                    ? $"Критичных проблем нет, предупреждений: {warnings}"
                    : "Проблем не найдено — всё в порядке";
            SummaryKey = errors > 0 ? "Danger" : warnings > 0 ? "Warning" : "Success";

            ProgressText = "";
            IsRunning = false;
        }

        private void ClearDiscordCache()
        {
            var (ok, message) = EngineService.ClearDiscordCache();
            SetMessage(message, ok ? "Success" : "Warning");
        }

        private void ResetNetwork()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Будут выполнены команды:\n\nnetsh winsock reset\nnetsh int ip reset all\nnetsh winhttp reset proxy\nipconfig /flushdns\n\n" +
                "После этого потребуется перезагрузка. Продолжить?",
                "Сброс сетевых настроек", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var report = DiagnosticsService.ResetNetwork();
            SetMessage(string.Join(" · ", report), "Warning");
        }

        private async Task RemoveServicesAsync()
        {
            IsRunning = true;
            ProgressText = "Удаляю службы zapret и WinDivert…";
            try
            {
                var result = await _main.Bypass.RemoveServiceAsync();
                SetMessage(result.Message, "Success");
                _main.Home.RefreshStatus();
                await RunAsync();
            }
            finally
            {
                ProgressText = "";
                IsRunning = false;
            }
        }

        private void FixTimestamps()
        {
            WinServices.EnsureTcpTimestamps();
            SetMessage("TCP timestamps включены (если были выключены).", "Success");
        }

        private void SetMessage(string message, string key)
        {
            MessageKey = key;
            Message = message;
        }
    }
}
