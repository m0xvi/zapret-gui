using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZapretGui.Core
{
    public enum LogLevel { Debug, Info, Warn, Error }

    public sealed class LogEntry
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public LogLevel Level { get; init; }
        public string Message { get; init; } = "";

        /// <summary>
        /// Источник записи: пусто — приложение, <see cref="AppLog.BypassCategory"/> — обход и службы
        /// (winws.exe, zapret, WinDivert). Используется фильтром «Источник» на странице «Журнал».
        /// </summary>
        public string Category { get; init; } = "";

        public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public string LevelText => Level switch
        {
            LogLevel.Debug => "ОТЛАДКА",
            LogLevel.Info => "ИНФО",
            LogLevel.Warn => "ВНИМАНИЕ",
            LogLevel.Error => "ОШИБКА",
            _ => "ИНФО"
        };
        public string SeverityKey => Level switch
        {
            LogLevel.Debug => "Muted",
            LogLevel.Info => "Info",
            LogLevel.Warn => "Warning",
            _ => "Danger"
        };
        public string SourceText => Category;
        public override string ToString() => Category.Length > 0
            ? $"[{Time:HH:mm:ss}] [{LevelText}] [{Category}] {Message}"
            : $"[{Time:HH:mm:ss}] [{LevelText}] {Message}";
    }

    /// <summary>Простой журнал: пишет в файл и рассылает события в UI.</summary>
    public static class AppLog
    {
        /// <summary>Категория записей о работе обхода и служб (для фильтра в журнале).</summary>
        public const string BypassCategory = "Обход";

        private static readonly object Gate = new();
        private static readonly List<LogEntry> Buffer = new();
        private const int MaxBuffer = 2000;
        private const long MaxFileSize = 1024 * 1024;

        public static event Action<LogEntry>? EntryAdded;

        public static IReadOnlyList<LogEntry> Entries
        {
            get { lock (Gate) return Buffer.ToArray(); }
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message);
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warn(string message) => Write(LogLevel.Warn, message);

        public static void Error(string message) => Write(LogLevel.Error, message);

        public static void Error(string message, Exception ex) => Write(LogLevel.Error, message + ": " + ex.Message);

        /// <summary>Записи о работе обхода и служб — попадают в фильтр «Обход и служба».</summary>
        public static void SvcDebug(string message) => Write(LogLevel.Debug, message, BypassCategory);
        public static void SvcInfo(string message) => Write(LogLevel.Info, message, BypassCategory);
        public static void SvcWarn(string message) => Write(LogLevel.Warn, message, BypassCategory);
        public static void SvcError(string message) => Write(LogLevel.Error, message, BypassCategory);

        public static void SvcError(string message, Exception ex)
            => Write(LogLevel.Error, message + ": " + ex.Message, BypassCategory);

        private static void Write(LogLevel level, string message, string category = "")
        {
            var entry = new LogEntry { Level = level, Message = message, Category = category };
            lock (Gate)
            {
                Buffer.Add(entry);
                if (Buffer.Count > MaxBuffer) Buffer.RemoveRange(0, Buffer.Count - MaxBuffer);
            }

            // Файл — в фоне, чтобы UI-поток не вис при живой отладке winws.exe
            try
            {
                var line = entry + Environment.NewLine;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        lock (Gate) { Rotate(); }
                        File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
                    }
                    catch { }
                });
            }
            catch { }

            try { EntryAdded?.Invoke(entry); } catch { }
        }

        private static void Rotate()
        {
            var info = new FileInfo(AppPaths.LogFile);
            if (!info.Exists || info.Length < MaxFileSize) return;
            var old = AppPaths.LogFile + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(AppPaths.LogFile, old);
            }
            catch { }
        }

        public static void Clear()
        {
            lock (Gate) Buffer.Clear();
            try { if (File.Exists(AppPaths.LogFile)) File.Delete(AppPaths.LogFile); } catch { }
        }
    }
}
