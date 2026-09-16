using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class LogsViewModel : ObservableObject
    {
        private bool _autoScroll = true;
        private bool _showDebug = true;
        private bool _showInfo = true;
        private bool _showWarn = true;
        private bool _showError = true;

        public LogsViewModel()
        {
            foreach (var entry in AppLog.Entries) Entries.Add(entry);
            AppLog.EntryAdded += OnEntryAdded;

            ClearCommand = new RelayCommand(Clear);
            CopyCommand = new RelayCommand(CopyAll, () => Entries.Count > 0);
            SaveCommand = new RelayCommand(SaveToFile, () => Entries.Count > 0);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.LogDir));
            RefreshCommand = new RelayCommand(Reload);
        }

        public ObservableCollection<LogEntry> Entries { get; } = new();

        public event Action? ScrollToEndRequested;

        public bool AutoScroll
        {
            get => _autoScroll;
            set => Set(ref _autoScroll, value);
        }

        public bool ShowDebug { get => _showDebug; set { if (Set(ref _showDebug, value)) Reload(); } }
        public bool ShowInfo { get => _showInfo; set { if (Set(ref _showInfo, value)) Reload(); } }
        public bool ShowWarn { get => _showWarn; set { if (Set(ref _showWarn, value)) Reload(); } }
        public bool ShowError { get => _showError; set { if (Set(ref _showError, value)) Reload(); } }

        public string CountText => $"Записей: {Entries.Count}";

        public ICommand ClearCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RefreshCommand { get; }

        private void OnEntryAdded(LogEntry entry)
        {
            if (!Accepts(entry)) return;
            RelayCommand.Dispatch(() =>
            {
                Entries.Add(entry);
                Raise(nameof(CountText));
                (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                if (AutoScroll) ScrollToEndRequested?.Invoke();
            });
        }

        private bool Accepts(LogEntry entry) => entry.Level switch
        {
            LogLevel.Debug => ShowDebug,
            LogLevel.Info => ShowInfo,
            LogLevel.Warn => ShowWarn,
            LogLevel.Error => ShowError,
            _ => true
        };

        private void Reload()
        {
            Entries.Clear();
            foreach (var entry in AppLog.Entries.Where(Accepts)) Entries.Add(entry);
            Raise(nameof(CountText));
        }

        private void Clear()
        {
            AppLog.Clear();
            Entries.Clear();
            AppLog.Info("Журнал очищен");
            Raise(nameof(CountText));
        }

        private void CopyAll()
        {
            try
            {
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Entries.Select(e => e.ToString())));
            }
            catch { }
        }

        private void SaveToFile()
        {
            try
            {
                var path = Path.Combine(AppPaths.LogDir, $"zapretgui-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllLines(path, Entries.Select(e => e.ToString()), new UTF8Encoding(false));
                Shell.OpenFolder(path, selectFile: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить журнал: " + ex.Message);
            }
        }
    }
}
