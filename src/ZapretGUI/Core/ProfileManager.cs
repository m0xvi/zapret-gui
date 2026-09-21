using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ZapretGui.ViewModels;

namespace ZapretGui.Core
{
    /// <summary>
    /// Именованный профиль конфигурации пользователя (пресет стратегии, DNS, игрового режима и фильтров).
    /// </summary>
    public sealed class UserProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string StrategyName { get; set; } = "";
        public GameFilterMode GameFilter { get; set; } = GameFilterMode.Disabled;
        public string DnsProfileId { get; set; } = "cloudflare";
        public bool GameModeActive { get; set; }
        public bool WatchdogEnabled { get; set; } = true;
        public bool RealTimePingEnabled { get; set; } = true;
        public string ProviderName { get; set; } = "";
        public string ProviderAsn { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastAppliedAt { get; set; }
        public bool IsBuiltIn { get; set; }

        public string SummaryText
        {
            get
            {
                var strat = string.IsNullOrWhiteSpace(StrategyName) ? "Стратегия по умолчанию" : StrategyName;
                var dns = string.IsNullOrWhiteSpace(DnsProfileId) ? "DNS (DHCP)" : $"DNS: {DnsProfileId}";
                var game = GameModeActive ? " · 🎮 Режим игры" : "";
                return $"{strat} · {dns}{game}";
            }
        }
    }

    /// <summary>
    /// Управление профилями пользователя, хранение в profiles.json, импорт/экспорт и применение.
    /// </summary>
    public static class ProfileManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static IReadOnlyList<UserProfile> GetBuiltInProfiles() => new List<UserProfile>
        {
            new()
            {
                Id = "profile-general",
                Name = "Универсальный (YouTube + Discord)",
                Description = "Сбалансированный профиль для ежедневного веб-серфинга, YouTube и Discord через быстрый Cloudflare DoH.",
                StrategyName = "general (fake-tls-auto)",
                GameFilter = GameFilterMode.Disabled,
                DnsProfileId = "cloudflare",
                GameModeActive = false,
                WatchdogEnabled = true,
                RealTimePingEnabled = true,
                IsBuiltIn = true
            },
            new()
            {
                Id = "profile-gaming",
                Name = "Игровой (Low Ping & Gaming)",
                Description = "Оптимизирован для соревновательных игр (CS2, Dota 2, Valorant). Исключает UDP порты игр и использует защищённый Quad9 DNS.",
                StrategyName = "general",
                GameFilter = GameFilterMode.TcpOnly,
                DnsProfileId = "quad9",
                GameModeActive = true,
                WatchdogEnabled = true,
                RealTimePingEnabled = true,
                IsBuiltIn = true
            },
            new()
            {
                Id = "profile-compatibility",
                Name = "Максимальный обход (Full Compatibility)",
                Description = "Агрессивная фильтрация с полной обработкой TCP и UDP портов для сетей со строгой провайдерской фильтрацией.",
                StrategyName = "general_alt",
                GameFilter = GameFilterMode.TcpAndUdp,
                DnsProfileId = "google",
                GameModeActive = false,
                WatchdogEnabled = true,
                RealTimePingEnabled = true,
                IsBuiltIn = true
            }
        };

        public static List<UserProfile> LoadProfiles()
        {
            try
            {
                if (File.Exists(AppPaths.ProfilesFile))
                {
                    var json = File.ReadAllText(AppPaths.ProfilesFile);
                    var list = JsonSerializer.Deserialize<List<UserProfile>>(json, JsonOptions);
                    if (list != null && list.Count > 0)
                    {
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Profiles] Не удалось прочитать profiles.json: " + ex.Message);
            }

            var defaults = GetBuiltInProfiles().ToList();
            SaveProfiles(defaults);
            return defaults;
        }

        public static void SaveProfiles(IEnumerable<UserProfile> profiles)
        {
            try
            {
                var json = JsonSerializer.Serialize(profiles, JsonOptions);
                var tmp = AppPaths.ProfilesFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(AppPaths.ProfilesFile)) File.Replace(tmp, AppPaths.ProfilesFile, null);
                else File.Move(tmp, AppPaths.ProfilesFile);
            }
            catch (Exception ex)
            {
                AppLog.Error("[Profiles] Не удалось сохранить profiles.json: " + ex.Message);
            }
        }

        public static UserProfile CreateFromCurrentSettings(AppSettings settings, string name, string description = "")
        {
            return new UserProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                Description = description.Trim(),
                StrategyName = settings.SelectedStrategy,
                GameFilter = EngineService.GetGameFilterMode(settings.EnginePath),
                DnsProfileId = "cloudflare",
                GameModeActive = settings.GameModeActive,
                WatchdogEnabled = settings.WatchdogEnabled,
                RealTimePingEnabled = settings.RealTimePingEnabled,
                ProviderName = settings.ProviderContext?.Name ?? "",
                ProviderAsn = settings.ProviderContext?.Asn ?? "",
                CreatedAt = DateTime.UtcNow
            };
        }

        public static async Task<(bool Ok, string Message)> ApplyProfileAsync(UserProfile profile, AppSettings settings, BypassController bypass, StrategyStore strategies)
        {
            try
            {
                AppLog.Info($"[Profiles] Применение профиля «{profile.Name}» (Стратегия: {profile.StrategyName}, DNS: {profile.DnsProfileId}, GameMode: {profile.GameModeActive})");

                // 1. Применяем настройки стратегии и игрового режима
                if (!string.IsNullOrWhiteSpace(profile.StrategyName))
                {
                    settings.SelectedStrategy = profile.StrategyName;
                }
                settings.GameModeActive = profile.GameModeActive;
                settings.WatchdogEnabled = profile.WatchdogEnabled;
                settings.RealTimePingEnabled = profile.RealTimePingEnabled;

                if (!string.IsNullOrEmpty(profile.ProviderName) || !string.IsNullOrEmpty(profile.ProviderAsn))
                {
                    settings.ProviderContext ??= new ProviderContext();
                    settings.ProviderContext.Name = profile.ProviderName;
                    settings.ProviderContext.Asn = profile.ProviderAsn;
                }

                EngineService.SetGameFilterMode(settings.EnginePath, profile.GameFilter);
                SettingsStore.Save(settings);

                // 2. Применяем профиль DNS
                if (!string.IsNullOrWhiteSpace(profile.DnsProfileId))
                {
                    var dnsProfile = DnsManagementService.PredefinedProfiles.FirstOrDefault(p =>
                        p.Id.Equals(profile.DnsProfileId, StringComparison.OrdinalIgnoreCase)) ??
                        DnsManagementService.PredefinedProfiles[0];
                    await DnsManagementService.ApplyDnsProfileAsync(dnsProfile).ConfigureAwait(false);
                }

                // 3. Если обход был запущен или выбранная стратегия существует — перезапускаем
                var isRunning = bypass.GetStatus().IsRunning;
                var strategy = strategies.Find(settings.SelectedStrategy) ?? strategies.Recommended;
                if (isRunning && strategy != null)
                {
                    var res = await bypass.SwitchToStrategyAsync(strategy, profile.GameFilter, settings.ShowWinwsConsole).ConfigureAwait(false);
                    if (!res.Ok) return (false, $"Профиль «{profile.Name}» применён, но перезапуск обхода не удался: {res.Message}");
                    profile.LastAppliedAt = DateTime.UtcNow;
                    return (true, $"Профиль «{profile.Name}» успешно применён. Обход перезапущен со стратегией «{strategy.Name}».");
                }

                profile.LastAppliedAt = DateTime.UtcNow;
                return (true, $"Профиль «{profile.Name}» успешно применён (стратегия «{settings.SelectedStrategy}», DNS: {profile.DnsProfileId}).");
            }
            catch (Exception ex)
            {
                AppLog.Error($"[Profiles] Ошибка применения профиля «{profile.Name}»: {ex.Message}");
                return (false, "Ошибка применения профиля: " + ex.Message);
            }
        }

        public static (bool Ok, string Message) ExportProfile(UserProfile profile, string targetFilePath)
        {
            try
            {
                var json = JsonSerializer.Serialize(profile, JsonOptions);
                File.WriteAllText(targetFilePath, json);
                return (true, $"Профиль «{profile.Name}» экспортирован в {Path.GetFileName(targetFilePath)}");
            }
            catch (Exception ex)
            {
                return (false, "Ошибка экспорта: " + ex.Message);
            }
        }

        public static (bool Ok, string Message, UserProfile? Profile) ImportProfile(string sourceFilePath)
        {
            try
            {
                if (!File.Exists(sourceFilePath)) return (false, "Файл не найден", null);
                var json = File.ReadAllText(sourceFilePath);
                var profile = JsonSerializer.Deserialize<UserProfile>(json, JsonOptions);
                if (profile == null || string.IsNullOrWhiteSpace(profile.Name))
                    return (false, "Неверный формат файла профиля", null);

                profile.Id = Guid.NewGuid().ToString("N");
                profile.IsBuiltIn = false;
                profile.CreatedAt = DateTime.UtcNow;

                return (true, $"Профиль «{profile.Name}» успешно импортирован", profile);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка импорта: " + ex.Message, null);
            }
        }
    }
}
