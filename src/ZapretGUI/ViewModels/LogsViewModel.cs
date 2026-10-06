using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
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

        private readonly MainViewModel? _main;
        private readonly Queue<LogEntry> _pending = new();
        private readonly object _pendingLock = new();
        private readonly DispatcherTimer _flushTimer;
        private bool _isReloading;
        private DateTime _lastWinwsLog = DateTime.MinValue;
        private int _suppressedWinws;

        public LogsViewModel(MainViewModel? main = null)
        {
            _main = main;
            // Начальная загрузка — без триггера перефильтрации, сразу из буфера
            foreach (var entry in AppLog.Entries) Entries.Add(entry);
            AppLog.EntryAdded += OnEntryAdded;

            // Таймер батч-добавления: 180мс, Background — не блокирует ввод
            var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, OnFlush, dispatcher);
            _flushTimer.Stop();

            ClearCommand = new RelayCommand(Clear);
            CopyCommand = new RelayCommand(CopyAll, () => Entries.Count > 0);
            SaveCommand = new RelayCommand(SaveToFile, () => Entries.Count > 0);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.LogDir));
            RefreshCommand = new RelayCommand(Reload);
            BackToSettingsCommand = new RelayCommand(() => _main?.Navigate("settings"), () => _main != null);
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

        public string CountText => $"Записей: {Entries.Count}" + (_pending.Count > 0 ? $" (+{_pending.Count} в очереди)" : "");

        public ICommand ClearCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand BackToSettingsCommand { get; }

        private void OnEntryAdded(LogEntry entry)
        {
            if (!Accepts(entry)) return;

            // v1.32.4: троттлинг живого вывода winws — не флудим журнал и UI
            if (entry.Category == AppLog.BypassCategory && entry.Level == LogLevel.Debug && entry.Message.StartsWith("[winws", StringComparison.Ordinal))
            {
                var now = DateTime.UtcNow;
                // не чаще 1 строки в 250мс для winws, остальное агрегируем
                if ((now - _lastWinwsLog).TotalMilliseconds < 250)
                {
                    _suppressedWinws++;
                    // раз в 2 сек показываем агрегат
                    if (_suppressedWinws % 8 != 0) return;
                    entry = new LogEntry { Time = entry.Time, Level = entry.Level, Category = entry.Category, Message = $"[winws] подавлено {_suppressedWinws} строк, последняя: {entry.Message}" };
                }
                else
                {
                    if (_suppressedWinws > 0)
                    {
                        // сброс счётчика
                        _suppressedWinws = 0;
                    }
                    _lastWinwsLog = now;
                }
            }

            lock (_pendingLock) _pending.Enqueue(entry);
            // запускаем таймер батча — не дергаем Dispatcher на каждую строку
            if (!_flushTimer.IsEnabled)
                _flushTimer.Start();
        }

        private void OnFlush(object? sender, EventArgs e)
        {
            List<LogEntry> batch = new();
            lock (_pendingLock)
            {
                var take = Math.Min(80, _pending.Count);
                for (int i = 0; i < take; i++) batch.Add(_pending.Dequeue());
                if (_pending.Count == 0) _flushTimer.Stop();
            }
            if (batch.Count == 0) return;
            foreach (var entry in batch)
            {
                Entries.Add(entry);
                if (Entries.Count > 3000) Entries.RemoveAt(0); // v1.32.4: лимит 3000 вместо 4000 для лёгкости UI
            }
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
            // v1.32.4: не пересоздаём 2000 элементов — достаточно уведомить UI о смене темы
            // Старый код делал Clear+Add и вешал систему
            Raise(nameof(CountText));
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(new Action(() =>
            {
                // форсируем перерисовку без полной пересборки коллекции
                var tmp = Entries.ToList();
                Entries.Clear();
                // добавляем батчами по 200 с уступкой диспетчеру
                AddBatched(tmp, 200);
            }), DispatcherPriority.Background);
        }

        private void AddBatched(IReadOnlyList<LogEntry> items, int chunk)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                foreach (var e in items) Entries.Add(e);
                Raise(nameof(CountText));
                return;
            }
            // батчами чтобы не блокировать UI
            int index = 0;
            void AddChunk()
            {
                var take = Math.Min(chunk, items.Count - index);
                for (int i = 0; i < take; i++) Entries.Add(items[index + i]);
                index += take;
                Raise(nameof(CountText));
                (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                if (index < items.Count)
                    dispatcher.BeginInvoke(new Action(AddChunk), DispatcherPriority.Background);
                else if (AutoScroll)
                    ScrollToEndRequested?.Invoke();
            }
            AddChunk();
        }

        private void Reload()
        {
            if (_isReloading) return;
            _isReloading = true;
            // останавливаем флаш и чистим очередь — переходим к новому фильтру
            _flushTimer.Stop();
            lock (_pendingLock) _pending.Clear();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            System.Threading.Tasks.Task.Run(() =>
            {
                var filtered = AppLog.Entries.Where(Accepts).ToList();
                // ограничиваем показ последними 2500 для скорости (в буфере 3000)
                if (filtered.Count > 2500) filtered = filtered.Skip(filtered.Count - 2500).ToList();
                return filtered;
            }).ContinueWith(t =>
                {
                    var filtered = t.Result;
                    void Apply()
                    {
                        Entries.Clear();
                        AddBatched(filtered, 250);
                        _isReloading = false;
                    }
                    if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Background);
                    else Apply();
                }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private void Clear()
        {
            _flushTimer.Stop();
            lock (_pendingLock) _pending.Clear();
            AppLog.Clear();
            Entries.Clear();
            _suppressedWinws = 0;
            AppLog.Info("Журнал очищен");
            Raise(nameof(CountText));
        }

        private void CopyAll()
        {
            try
            {
                // копируем не более 2000 строк чтобы не вешать буфер обмена
                var lines = Entries.Take(2000).Select(e => e.ToString());
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            }
            catch { }
        }

        private void SaveToFile()
        {
            try
            {
                var path = Path.Combine(AppPaths.LogDir, $"zapretgui-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                // пишем батчами
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
