using System;
using System.IO;

namespace ZapretGui.Core
{
    /// <summary>Все пути приложения в одном месте.</summary>
    public static class AppPaths
    {
        public const string AppName = "ZapretGUI";

        /// <summary>Флаг портативного режима (наличие файла portable или папки data рядом с exe).</summary>
        public static bool IsPortableMode
        {
            get
            {
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    return File.Exists(Path.Combine(baseDir, "portable")) ||
                           File.Exists(Path.Combine(baseDir, ".portable")) ||
                           Directory.Exists(Path.Combine(baseDir, "data"));
                }
                catch { return false; }
            }
        }

        /// <summary>Папка настроек и логов: %APPDATA%\ZapretGUI (или ./data в портативном режиме).</summary>
        public static string AppData => IsPortableMode
            ? EnsureDir(Path.Combine(AppContext.BaseDirectory, "data"))
            : EnsureDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));

        public static string SettingsFile => Path.Combine(AppData, "settings.json");
        public static string ProfilesDir => EnsureDir(Path.Combine(AppData, "profiles"));
        public static string ProfilesFile => Path.Combine(ProfilesDir, "profiles.json");
        public static string LogDir => EnsureDir(Path.Combine(AppData, "logs"));
        public static string LogFile => Path.Combine(LogDir, "zapretgui.log");
        public static string BackupDir => EnsureDir(Path.Combine(AppData, "backups"));
        public static string GuiBackupDir => EnsureDir(Path.Combine(AppData, "gui-backups"));
        public static string GuiUpdatePlanFile => Path.Combine(AppData, "gui-update-plan.json");
        public static string CacheDir => EnsureDir(Path.Combine(AppData, "cache"));
        public static string CandidatesDir => EnsureDir(Path.Combine(AppData, "candidates"));
        public static string StrategyEvaluationHistoryFile => Path.Combine(AppData, "strategy-evaluation-history.json");
        public static string DpiSuiteCacheFile => Path.Combine(AppData, "dpi-suite-cache.json");
        public static string ServiceHealthCacheFile => Path.Combine(AppData, "service-health-cache.json");
        public static string RecoveryJournalFile => Path.Combine(AppData, "recovery-history.json");
        public static string DeepCheckReportsDir => EnsureDir(Path.Combine(AppData, "deep-check-reports"));
        public static string TempDir => EnsureDir(Path.Combine(Path.GetTempPath(), AppName));

        /// <summary>Папка с движком zapret по умолчанию: %LOCALAPPDATA%\ZapretGUI\engine (или ./engine в портативном режиме).</summary>
        public static string DefaultEngine => IsPortableMode
            ? Path.Combine(AppContext.BaseDirectory, "engine")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "engine");

        public static string EnsureDir(string path)
        {
            try { Directory.CreateDirectory(path); } catch { /* игнорируем */ }
            return path;
        }

        /// <summary>Файл-маркер версии движка, скачанного самим GUI.</summary>
        public static string VersionMarker(string engineRoot) => Path.Combine(engineRoot, ".gui-engine-version");
    }
}
