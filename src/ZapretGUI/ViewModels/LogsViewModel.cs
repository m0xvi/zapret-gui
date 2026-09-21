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
        private bool _showApp = true;
        private bool _showBypass = true;

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

        /// <summary>Показывать записи самого приложения (обновления, интерфейс, диагностика).</summary>
        public bool ShowApp { get => _showApp; set { if (Set(ref _showApp, value)) Reload(); } }

        /// <summary>Показывать записи обхода и служб (winws.exe, zapret, WinDivert).</summary>
        public bool ShowBypass { get => _showBypass; set { if (Set(ref _showBypass, value)) Reload(); } }

        public string CountText => $"Записей: {Entries.Count}";

        public ICommand ClearCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RefreshCommand { get; }

        private void OnEntryAdded(LogEntry entry)
        {
            if (!Accepts(entry)) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Entries.Add(entry);
                    if (Entries.Count > 4000) Entries.RemoveAt(0);
                    Raise(nameof(CountText));
                    (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    if (AutoScroll) ScrollToEndRequested?.Invoke();
                }));
                return;
            }
            Entries.Add(entry);
            if (Entries.Count > 4000) Entries.RemoveAt(0);
            Raise(nameof(CountText));
            (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            if (AutoScroll) ScrollToEndRequested?.Invoke();
        }

        private bool Accepts(LogEntry entry)
        {
            var levelOk = entry.Level switch
            {
                LogLevel.Debug => ShowDebug,
                LogLevel.Info => ShowInfo,
                LogLevel.Warn => ShowWarn,
                LogLevel.Error => ShowError,
                _ => true
            };
            if (!levelOk) return false;

            // Пустая категория — приложение, иначе (сейчас только «Обход») — обход и службы
            return string.IsNullOrEmpty(entry.Category) ? ShowApp : ShowBypass;
        }

        public void RefreshTheme()
        {
            var entries = Entries.ToList();
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
        }

        private void Reload()
        {
            var filtered = AppLog.Entries.Where(Accepts).ToList();
            // Батчевая перезагрузка — минимизирует уведомления коллекции
            Entries.Clear();
            foreach (var entry in filtered) Entries.Add(entry);
            Raise(nameof(CountText));
            (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
