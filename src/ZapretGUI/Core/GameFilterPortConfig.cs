using System;
using System.Collections.Generic;
using System.Linq;

namespace ZapretGui.Core
{
    public sealed class GameFilterProfile
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string TcpPorts { get; init; } = "1024-65535";
        public string UdpPorts { get; init; } = "1024-65535";
        public string ExcludedPorts { get; init; } = "";
        public string DisplayTitle => $"{Name} · UDP: {UdpPorts}";
    }

    /// <summary>
    /// Тонкая настройка игрового фильтра GameFilter, диапазонов портов и исключений для онлайн-игр (PR #17165, #11565).
    /// Предотвращает рост пинга и дропы в играх с античитами (Steam, CS2, Dota 2, Valorant, Apex, Roblox, Warface).
    /// </summary>
    public static class GameFilterPortConfig
    {
        public static readonly IReadOnlyList<GameFilterProfile> PredefinedProfiles = new List<GameFilterProfile>
        {
            new()
            {
                Id = "discord_voice",
                Name = "🎙️ Только Discord Voice (Рекомендуется)",
                Description = "Обрабатывать только стандартный диапазон голосовых серверов Discord (UDP 50000-65535). Онлайн-игры не затрагиваются, пинг в играх стандартный.",
                TcpPorts = "1024-65535",
                UdpPorts = "50000-65535",
                ExcludedPorts = ""
            },
            new()
            {
                Id = "all_broad",
                Name = "🎮 Полный диапазон (Игры + Voice + WebRTC)",
                Description = "Широкий перехват всех динамических портов 1024-65535 для обхода блокировок в любых играх и WebRTC-приложениях.",
                TcpPorts = "1024-65535",
                UdpPorts = "1024-65535",
                ExcludedPorts = ""
            },
            new()
            {
                Id = "steam_cs2",
                Name = "🎯 Steam / CS2 / Dota 2 (Исключить 27000-27100)",
                Description = "Исключает игровые серверы Valve Source Engine (UDP 27000-27100) из десинхронизации, исключая 'loss' и 'choke'. Голос Discord работает.",
                TcpPorts = "1024-65535",
                UdpPorts = "1024-26999,27101-65535",
                ExcludedPorts = "27000-27100"
            },
            new()
            {
                Id = "riot_games",
                Name = "⚔️ Riot Games / Valorant / LoL (Исключить 5000-5500)",
                Description = "Исключает трафик Vanguard/Riot Games (UDP 5000-5500) для избежания конфликтов с античитом. Дискорд работает.",
                TcpPorts = "1024-65535",
                UdpPorts = "1024-4999,5501-65535",
                ExcludedPorts = "5000-5500"
            },
            new()
            {
                Id = "apex_ea",
                Name = "🏆 Apex Legends / EA (Исключить 18000, 27000-30000)",
                Description = "Исключает матчмейкинг и игровые порты EA Apex Legends для минимального джиттера.",
                TcpPorts = "1024-65535",
                UdpPorts = "1024-17999,18001-26999,30001-65535",
                ExcludedPorts = "18000, 27000-30000"
            },
            new()
            {
                Id = "roblox",
                Name = "🧱 Roblox & WebRTC (UDP 49152-65535)",
                Description = "Оптимально для голосового чата Roblox, плейсов и WebRTC аудио/видео каналов.",
                TcpPorts = "1024-65535",
                UdpPorts = "49152-65535",
                ExcludedPorts = ""
            },
            new()
            {
                Id = "custom",
                Name = "⚙️ Пользовательский диапазон портов",
                Description = "Ручной ввод диапазонов TCP и UDP портов.",
                TcpPorts = "1024-65535",
                UdpPorts = "50000-65535",
                ExcludedPorts = ""
            }
        };

        public static GameFilterProfile GetProfileById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return PredefinedProfiles[0];
            return PredefinedProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? PredefinedProfiles[0];
        }

        /// <summary>
        /// Вычисляет итоговый диапазон портов TCP для подстановки в winws аргументы.
        /// </summary>
        public static string ResolveTcpPortString(GameFilterMode mode, string? profileId = null, string? customTcp = null)
        {
            if (mode is GameFilterMode.Disabled or GameFilterMode.UdpOnly)
                return "12";

            if (profileId == "custom" && !string.IsNullOrWhiteSpace(customTcp))
                return customTcp.Trim();

            var profile = GetProfileById(profileId);
            return string.IsNullOrWhiteSpace(profile.TcpPorts) ? "1024-65535" : profile.TcpPorts.Trim();
        }

        /// <summary>
        /// Вычисляет итоговый диапазон портов UDP для подстановки в winws аргументы.
        /// </summary>
        public static string ResolveUdpPortString(GameFilterMode mode, string? profileId = null, string? customUdp = null)
        {
            if (mode is GameFilterMode.Disabled or GameFilterMode.TcpOnly)
                return "12";

            if (profileId == "custom" && !string.IsNullOrWhiteSpace(customUdp))
                return customUdp.Trim();

            var profile = GetProfileById(profileId);
            return string.IsNullOrWhiteSpace(profile.UdpPorts) ? "50000-65535" : profile.UdpPorts.Trim();
        }
    }
}
