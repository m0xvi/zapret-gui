using System;
using System.Collections.Generic;
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

        /// <summary>Масштаб интерфейса в процентах (80–140).</summary>
        public int InterfaceZoomPercent { get; set; } = 100;

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

        /// <summary>Последняя выбранная в GUI стратегия (имя .bat или сохранённого кандидата).</summary>
        public string SelectedStrategy { get; set; } = "";

        /// <summary>Предыдущая стратегия для ручного отката после назначения кандидата.</summary>
        public string PreviousSelectedStrategy { get; set; } = "";

        /// <summary>Версия движка, установленная GUI (для проверки обновлений).</summary>
        public string EngineVersion { get; set; } = "";

        /// <summary>Запускать автоматическую проверку обновлений самого GUI.</summary>
        public bool AutoCheckGuiUpdates { get; set; } = true;

        /// <summary>Адрес репозитория для безопасного обновления GUI.</summary>
        public string GuiRepo { get; set; } = GuiUpdateService.DefaultRepository;

        /// <summary>Выбранный канал движка.</summary>
        public bool UseGameFilterOnStart { get; set; }

        /// <summary>Пользователь попросил больше не спрашивать про найденный старый запуск запрета.</summary>
        public bool LegacyZapretDismissed { get; set; }

        /// <summary>Проверить все стратегии при первом запуске после установки движка.</summary>
        public bool AutoTestStrategiesOnFirstLaunch { get; set; } = true;

        /// <summary>Запустить диагностику перед автоматическим подбором стратегии.</summary>
        public bool AutoDiagnoseOnFirstLaunch { get; set; } = true;

        /// <summary>Флаг завершения мастера первого запуска.</summary>
        public bool FirstLaunchWizardCompleted { get; set; }

        /// <summary>Безопасный режим: не выполнять автоматический запуск обхода и сетевые действия.</summary>
        public bool SafeMode { get; set; }

        /// <summary>Флаг завершения одноразовой проверки стратегий.</summary>
        public bool StrategyTestsCompleted { get; set; }

        /// <summary>Флаг завершения одноразовой диагностики.</summary>
        public bool FirstLaunchDiagnosticsCompleted { get; set; }

        /// <summary>Включить фоновую проверку избранных ресурсов.</summary>
        public bool ResourceMonitoringEnabled { get; set; }

        /// <summary>Включить сторожевой таймер (Watchdog) для автоматического контроля winws.exe.</summary>
        public bool WatchdogEnabled { get; set; } = true;

        /// <summary>Интервал проверки сторожевого таймера в секундах (5-120).</summary>
        public int WatchdogIntervalSeconds { get; set; } = 15;

        /// <summary>Автоматически перезапускать процесс winws / службу при сбое.</summary>
        public bool WatchdogAutoRestart { get; set; } = true;

        /// <summary>Уведомлять о восстановлении обхода через системный трей.</summary>
        public bool WatchdogNotifyUser { get; set; } = true;

        /// <summary>Отображать живой RTT пинг ключевых ресурсов (YouTube, Discord, GitHub) в шапке.</summary>
        public bool RealTimePingEnabled { get; set; } = true;

        /// <summary>Интервал живого пинга в секундах.</summary>
        public int RealTimePingIntervalSeconds { get; set; } = 10;

        /// <summary>Задержка автозапуска обхода при старте Windows в секундах (0-60).</summary>
        public int StartupDelaySeconds { get; set; } = 5;

        /// <summary>Пробовать подобрать другую стратегию после подтверждённого сбоя обхода.</summary>
        public bool AutoRecoverStrategy { get; set; } = true;

        /// <summary>Показывать уведомления мониторинга через значок в трее.</summary>
        public bool MonitorNotificationsEnabled { get; set; } = true;

        /// <summary>Интервал фоновой проверки в минутах.</summary>
        public int ResourceMonitoringIntervalMinutes { get; set; } = 15;

        /// <summary>Ресурсы пользователя для фонового контроля.</summary>
        public List<MonitorTarget> MonitorTargets { get; set; } = new();

        /// <summary>Включить автоматический мониторинг запущенных игр.</summary>
        public bool GameDetectionEnabled { get; set; } = true;

        /// <summary>Автоматически включать режим оптимизации для игр при обнаружении игры.</summary>
        public bool AutoGameModeOnLaunch { get; set; } = true;

        /// <summary>Игровой режим активен (облегчённая фильтрация и пропуск UDP).</summary>
        public bool GameModeActive { get; set; }

        /// <summary>Включить глобальные горячие клавиши Windows.</summary>
        public bool GlobalHotkeysEnabled { get; set; } = true;

        /// <summary>Горячая клавиша переключения обхода (по умолчанию Ctrl+Shift+Z).</summary>
        public string HotkeyToggleBypass { get; set; } = "Ctrl+Shift+Z";

        /// <summary>Горячая клавиша переключения игрового режима (по умолчанию Ctrl+Shift+G).</summary>
        public string HotkeyToggleGameMode { get; set; } = "Ctrl+Shift+G";

        /// <summary>Горячая клавиша открытия мини-виджета (по умолчанию Ctrl+Shift+O).</summary>
        public string HotkeyToggleMiniOverlay { get; set; } = "Ctrl+Shift+O";

        /// <summary>Координата X мини-виджета на экране (-1 = по умолчанию).</summary>
        public double MiniOverlayLeft { get; set; } = -1;

        /// <summary>Координата Y мини-виджета на экране (-1 = по умолчанию).</summary>
        public double MiniOverlayTop { get; set; } = -1;

        /// <summary>Прозрачность мини-виджета (50–100).</summary>
        public int MiniOverlayOpacity { get; set; } = 95;

        /// <summary>Мини-виджет поверх всех окон.</summary>
        public bool MiniOverlayTopmost { get; set; } = true;

        /// <summary>Флаг отображения мини-виджета вместо или рядом с главным окном.</summary>
        public bool MiniOverlayEnabled { get; set; }

        /// <summary>Необязательный контекст провайдера для будущего подбора стратегий.</summary>
        public ProviderContext ProviderContext { get; set; } = new();
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
                        if (string.IsNullOrWhiteSpace(loaded.GuiRepo)) loaded.GuiRepo = GuiUpdateService.DefaultRepository;
                        loaded.MonitorTargets ??= new List<MonitorTarget>();
                        loaded.ProviderContext ??= new ProviderContext();
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
