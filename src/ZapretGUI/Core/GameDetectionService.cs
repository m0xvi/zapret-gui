using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>
    /// Фоновый сервис автоматического обнаружения запущенных игровых процессов и лаунчеров.
    /// </summary>
    public sealed class GameDetectionService : IDisposable
    {
        private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Популярные сетевые и соревновательные игры
            { "cs2", "Counter-Strike 2" },
            { "csgo", "Counter-Strike: GO" },
            { "dota2", "Dota 2" },
            { "valorant", "Valorant" },
            { "VALORANT-Win64-Shipping", "Valorant" },
            { "r5apex", "Apex Legends" },
            { "pubg", "PUBG: BATTLEGROUNDS" },
            { "TslGame", "PUBG: BATTLEGROUNDS" },
            { "aces", "War Thunder" },
            { "aces_dx11", "War Thunder" },
            { "GenshinImpact", "Genshin Impact" },
            { "YuanShen", "Genshin Impact" },
            { "StarRail", "Honkai: Star Rail" },
            { "ZenlessZoneZero", "Zenless Zone Zero" },
            { "RobloxPlayerBeta", "Roblox" },
            { "javaw", "Minecraft" },
            { "Minecraft.Windows", "Minecraft Bedrock" },
            { "EscapeFromTarkov", "Escape from Tarkov" },
            { "RustClient", "Rust" },
            { "RainbowSix", "Rainbow Six Siege" },
            { "RainbowSix_Vulkan", "Rainbow Six Siege" },
            { "GTA5", "Grand Theft Auto V" },
            { "PlayGTAV", "GTA V Online" },
            { "Deadlock", "Deadlock" },
            { "Overwatch", "Overwatch 2" },
            { "Wow", "World of Warcraft" },
            { "WowClassic", "World of Warcraft Classic" },
            { "LeagueClient", "League of Legends" },
            { "League of Legends", "League of Legends" },
            { "FortniteClient-Win64-Shipping", "Fortnite" },
            { "cod", "Call of Duty" },
            { "Cyberpunk2077", "Cyberpunk 2077" },
            { "helldivers2", "Helldivers 2" },
            { "WorldOfTanks", "World of Tanks" },
            { "Albion-Online", "Albion Online" },
            { "PathOfExile", "Path of Exile" },
            { "PathOfExile_x64", "Path of Exile" },
            { "Warframe.x64", "Warframe" },
            { "DayZ_x64", "DayZ" },
            { "HuntGame", "Hunt: Showdown" },
            { "Squad", "Squad" },

            // Игровые лаунчеры и платформы
            { "steam", "Steam" },
            { "steamwebhelper", "Steam Client" },
            { "EpicGamesLauncher", "Epic Games" },
            { "Battle.net", "Battle.net" },
            { "EADesktop", "EA App" },
            { "Origin", "Origin" },
            { "upc", "Ubisoft Connect" },
            { "UbisoftGameLauncher", "Ubisoft Connect" },
            { "RiotClientServices", "Riot Client" },
            { "FACEIT", "FACEIT Client" }
        };

        private readonly AppSettings _settings;
        private readonly Timer _timer;
        private bool _isDisposed;
        private bool _isPolling;
        private string? _lastDetectedGame;

        public event Action<bool, string?>? GameStatusChanged;

        public bool IsGameRunning => !string.IsNullOrEmpty(ActiveGameName);
        public string? ActiveGameName { get; private set; }
        public string? ActiveProcessName { get; private set; }

        public GameDetectionService(AppSettings settings)
        {
            _settings = settings;
            _timer = new Timer(OnPoll, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (_isDisposed) return;
            _timer.Change(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3));
        }

        public void Stop()
        {
            if (_isDisposed) return;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public static (bool IsRunning, string? GameName, string? ProcessName) DetectRunningGame()
        {
            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        var name = p.ProcessName;
                        if (KnownGames.TryGetValue(name, out var friendlyName))
                        {
                            return (true, friendlyName, name);
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Ошибка сканирования игровых процессов: " + ex.Message);
            }

            return (false, null, null);
        }

        private void OnPoll(object? state)
        {
            if (_isPolling || _isDisposed || !_settings.GameDetectionEnabled) return;
            _isPolling = true;

            try
            {
                var (isRunning, gameName, procName) = DetectRunningGame();
                if (isRunning != IsGameRunning || gameName != _lastDetectedGame)
                {
                    ActiveGameName = gameName;
                    ActiveProcessName = procName;
                    _lastDetectedGame = gameName;

                    AppLog.Info(isRunning
                        ? $"[GameDetector] Обнаружен игровой процесс: «{gameName}» ({procName}.exe)"
                        : "[GameDetector] Игровой процесс завершён.");

                    GameStatusChanged?.Invoke(isRunning, gameName);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Ошибка детектора игр: " + ex.Message);
            }
            finally
            {
                _isPolling = false;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Dispose();
        }
    }
}
