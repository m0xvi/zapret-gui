using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    /// <summary>
    /// Снимок «обходной» конфигурации (v1.29.3): то, что реально определяет, как работает обход —
    /// способ обхода, перехват (ipset, порты, игровой фильтр), DNS, SNI и транспортные флаги.
    /// Интерфейсные настройки (тема, окно, хоткеи) сюда не входят: рабочий стол отвечает за обход.
    /// </summary>
    public sealed class ConfigurationSnapshot
    {
        /// <summary>Почему сделан снимок: «перед применением», «перед откатом», «из профиля».</summary>
        public string Note { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string Strategy { get; set; } = "";
        public string GameFilterProfileId { get; set; } = "";
        public string GameFilterTcpPorts { get; set; } = "";
        public string GameFilterUdpPorts { get; set; } = "";
        public string ExcludedPorts { get; set; } = "";

        /// <summary>Режим ipset в движке: none / loaded / any.</summary>
        public string IpsetMode { get; set; } = "loaded";

        /// <summary>DNS-профиль, применённый приложением (пусто — DNS не трогали).</summary>
        public string DnsProfileId { get; set; } = "";

        public string FakeSni { get; set; } = "";
        public bool AutoSniRotation { get; set; }
        public string YoutubeSniOverride { get; set; } = "";
        public bool DisableQuicFake { get; set; }
        public bool PreferIPv4 { get; set; }
        public bool UseDoh { get; set; }

        [JsonIgnore] public string TimeText => CreatedAt.ToString("dd.MM.yyyy HH:mm");

        [JsonIgnore] public string StrategyText => string.IsNullOrWhiteSpace(Strategy) ? "способ не выбран" : Strategy;

        [JsonIgnore]
        public string SummaryText
        {
            get
            {
                var text = $"{StrategyText} · ipset {IpsetMode}";
                if (!string.IsNullOrWhiteSpace(FakeSni)) text += $" · SNI {FakeSni}";
                if (!string.IsNullOrWhiteSpace(DnsProfileId)) text += $" · DNS {DnsProfileId}";
                return text;
            }
        }

        [JsonIgnore] public string NoteText => string.IsNullOrWhiteSpace(Note) ? "снимок" : Note;

        public ConfigurationSnapshot Clone() => new()
        {
            Note = Note,
            CreatedAt = CreatedAt,
            Strategy = Strategy,
            GameFilterProfileId = GameFilterProfileId,
            GameFilterTcpPorts = GameFilterTcpPorts,
            GameFilterUdpPorts = GameFilterUdpPorts,
            ExcludedPorts = ExcludedPorts,
            IpsetMode = IpsetMode,
            DnsProfileId = DnsProfileId,
            FakeSni = FakeSni,
            AutoSniRotation = AutoSniRotation,
            YoutubeSniOverride = YoutubeSniOverride,
            DisableQuicFake = DisableQuicFake,
            PreferIPv4 = PreferIPv4,
            UseDoh = UseDoh
        };
    }

    /// <summary>История снимков конфигурации: рабочий стол настройщика пишет сюда «как было» перед изменением.</summary>
    public static class ConfigurationSnapshotStore
    {
        private const int MaxItems = 30;

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static readonly object Gate = new();

        public static List<ConfigurationSnapshot> Load()
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(AppPaths.ConfigHistoryFile)) return new List<ConfigurationSnapshot>();
                    var json = File.ReadAllText(AppPaths.ConfigHistoryFile);
                    var items = JsonSerializer.Deserialize<List<ConfigurationSnapshot>>(json, Options);
                    return items?.Where(i => i != null).ToList() ?? new List<ConfigurationSnapshot>();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать историю конфигурации: " + ex.Message);
                return new List<ConfigurationSnapshot>();
            }
        }

        public static void Save(List<ConfigurationSnapshot> items)
        {
            try
            {
                lock (Gate)
                {
                    AppPaths.EnsureDir(AppPaths.AppData);
                    var trimmed = items.Take(MaxItems).ToList();
                    File.WriteAllText(AppPaths.ConfigHistoryFile, JsonSerializer.Serialize(trimmed, Options));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить историю конфигурации: " + ex.Message);
            }
        }

        /// <summary>Добавляет снимок в начало истории (самый свежий — первый).</summary>
        public static List<ConfigurationSnapshot> Push(ConfigurationSnapshot snapshot)
        {
            var items = Load();
            items.Insert(0, snapshot);
            Save(items);
            return items.Take(MaxItems).ToList();
        }

        /// <summary>Убирает снимок из истории (после успешного отката к нему).</summary>
        public static List<ConfigurationSnapshot> Remove(ConfigurationSnapshot snapshot)
        {
            var items = Load();
            var found = items.FirstOrDefault(i => i.CreatedAt == snapshot.CreatedAt && i.Strategy == snapshot.Strategy && i.Note == snapshot.Note);
            if (found != null) items.Remove(found);
            Save(items);
            return items;
        }

        public static ConfigurationSnapshot Capture(AppSettings settings, string ipsetMode, string dnsProfileId, string note) => new()
        {
            Note = note,
            CreatedAt = DateTime.Now,
            Strategy = settings.SelectedStrategy ?? "",
            GameFilterProfileId = settings.GameFilterProfileId ?? "",
            GameFilterTcpPorts = settings.CustomGameFilterTcpPorts ?? "",
            GameFilterUdpPorts = settings.CustomGameFilterUdpPorts ?? "",
            ExcludedPorts = settings.CustomExcludedPorts ?? "",
            IpsetMode = ipsetMode,
            DnsProfileId = dnsProfileId ?? "",
            FakeSni = settings.SelectedFakeSni ?? "",
            AutoSniRotation = settings.AutoSniRotationEnabled,
            YoutubeSniOverride = settings.YoutubeSniOverride ?? "",
            DisableQuicFake = settings.DisableQuicFake,
            PreferIPv4 = settings.PreferIPv4ForBypass,
            UseDoh = settings.UseDohForBlockedHosts
        };

        /// <summary>Сохраняет снимок отдельным файлом (кнопка «Экспорт»). Возвращает путь или пусто.</summary>
        public static string ExportToFile(ConfigurationSnapshot snapshot, string path)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options));
                return path;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось экспортировать конфигурацию: " + ex.Message);
                return "";
            }
        }
    }
}
