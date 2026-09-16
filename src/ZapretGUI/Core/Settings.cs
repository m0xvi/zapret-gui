using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    public enum ThemeMode { System, Dark, Light }

    public enum GameFilterMode { Disabled = 0, TcpAndUdp = 1, TcpOnly = 2, UdpOnly = 3 }

    /// <summary>Режимы фильтра ipset: none / loaded / any (как в service.bat).</summary>
    public enum IpsetMode { None, Loaded, Any }

    /// <summary>Пользовательские настройки приложения.</summary>
    public sealed class AppSettings
    {
        /// <summary>Папка с движком zapret (bin\, lists\, *.bat).</summary>
        public string EnginePath { get; set; } = AppPaths.DefaultEngine;

        public ThemeMode Theme { get; set; } = ThemeMode.System;

        /// <summary>Сворачивать в трей при закрытии окна.</summary>
        public bool CloseToTray { get; set; }

        /// <summary>Запускать свёрнутым в трей.</summary>
        public bool StartMinimized { get; set; }

        /// <summary>Запускать обход сразу при старте приложения (если выбрана стратегия).</summary>
        public bool AutoStartBypass { get; set; }

        /// <summary>Останавливать обход при выходе из приложения.</summary>
        public bool StopBypassOnExit { get; set; }

        /// <summary>Показывать окно консоли winws.exe (по умолчанию — скрыто).</summary>
        public bool ShowWinwsConsole { get; set; }

        /// <summary>Автоматически проверять обновления движка при запуске.</summary>
        public bool AutoCheckEngineUpdates { get; set; } = true;

        /// <summary>Учитывать pre-release версии при проверке обновлений.</summary>
        public bool IncludePrerelease { get; set; }

        /// <summary>Сохранять пользовательские списки и флаги при обновлении движка.</summary>
        public bool PreserveUserDataOnUpdate { get; set; } = true;

        /// <summary>Запрашивать подтверждение при остановке обхода.</summary>
        public bool ConfirmOnStop { get; set; }

        /// <summary>Запускать приложение при входе в систему (через планировщик задач).</summary>
        public bool RunAtStartup { get; set; }

        /// <summary>Последняя выбранная в GUI стратегия (имя .bat файла без расширения).</summary>
        public string SelectedStrategy { get; set; } = "";

        /// <summary>Версия движка, установленная GUI (для проверки обновлений).</summary>
        public string EngineVersion { get; set; } = "";

        /// <summary>Запускать автоматическую проверку обновлений самого GUI.</summary>
        public bool AutoCheckGuiUpdates { get; set; } = true;

        /// <summary>Адрес нашего репозитория для обновлений GUI (пусто — проверка выключена).</summary>
        public string GuiRepo { get; set; } = "";

        /// <summary>Выбранный канал движка.</summary>
        public bool UseGameFilterOnStart { get; set; }
    }

    /// <summary>Загрузка/сохранение settings.json.</summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var json = File.ReadAllText(AppPaths.SettingsFile);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.EnginePath)) loaded.EnginePath = AppPaths.DefaultEngine;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать settings.json: " + ex.Message);
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, Options);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(AppPaths.SettingsFile)) File.Replace(tmp, AppPaths.SettingsFile, null);
                else File.Move(tmp, AppPaths.SettingsFile);
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить settings.json: " + ex.Message);
            }
        }
    }
}
