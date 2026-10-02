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

        /// <summary>В момент снимка обход был запущен. По этой пометке «вернуть предыдущую рабочую» находит
        /// последнюю конфигурацию, при которой обход действительно работал (v1.31.0).</summary>
        public bool BypassWasRunning { get; set; }

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

        [JsonIgnore] public string WorkingText => BypassWasRunning ? "рабочая" : "";

        public ConfigurationSnapshot Clone() => new()
        {
            Note = Note,
            CreatedAt = CreatedAt,
            BypassWasRunning = BypassWasRunning,
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

        public static ConfigurationSnapshot Capture(AppSettings settings, string ipsetMode, string dnsProfileId, string note, bool bypassWasRunning = false) => new()
        {
            Note = note,
            CreatedAt = DateTime.Now,
            BypassWasRunning = bypassWasRunning,
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

    /// <summary>
    /// Именованный пресет конфигурации обхода (v1.31.0): полный набор параметров, а не только стратегия
    /// с DNS, как у профилей. Профили привязываются к сетям и срабатывают сами; пресеты применяются вручную
    /// с рабочего стола.
    /// </summary>
    public sealed class ConfigurationPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public ConfigurationSnapshot Config { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonIgnore] public string TimeText => CreatedAt.ToString("dd.MM.yyyy HH:mm");
        [JsonIgnore] public string SummaryText => Config?.SummaryText ?? "";
        [JsonIgnore] public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? "без описания" : Description;
    }

    /// <summary>Хранилище пресетов: `presets.json` рядом с историей снимков.</summary>
    public static class ConfigurationPresetStore
    {
        private const int MaxItems = 50;

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static readonly object Gate = new();

        public static List<ConfigurationPreset> Load()
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(AppPaths.PresetsFile)) return new List<ConfigurationPreset>();
                    var json = File.ReadAllText(AppPaths.PresetsFile);
                    var items = JsonSerializer.Deserialize<List<ConfigurationPreset>>(json, Options);
                    return items?.Where(i => i != null).ToList() ?? new List<ConfigurationPreset>();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать пресеты конфигурации: " + ex.Message);
                return new List<ConfigurationPreset>();
            }
        }

        public static void Save(List<ConfigurationPreset> items)
        {
            try
            {
                lock (Gate)
                {
                    AppPaths.EnsureDir(AppPaths.AppData);
                    File.WriteAllText(AppPaths.PresetsFile, JsonSerializer.Serialize(items.Take(MaxItems).ToList(), Options));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить пресеты конфигурации: " + ex.Message);
            }
        }

        /// <summary>Добавляет пресет (одноимённый заменяется).</summary>
        public static List<ConfigurationPreset> Upsert(ConfigurationPreset preset)
        {
            var items = Load();
            var existing = items.FirstOrDefault(i => i.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) items.Remove(existing);
            items.Insert(0, preset);
            Save(items);
            return items.Take(MaxItems).ToList();
        }

        public static List<ConfigurationPreset> Remove(ConfigurationPreset preset)
        {
            var items = Load();
            var found = items.FirstOrDefault(i => i.Id == preset.Id);
            if (found != null) items.Remove(found);
            Save(items);
            return items;
        }

        /// <summary>Выгружает пресеты (все) в JSON-файл.</summary>
        public static string ExportToFile(IEnumerable<ConfigurationPreset> presets, string path)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(presets.ToList(), Options));
                return path;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось экспортировать пресеты: " + ex.Message);
                return "";
            }
        }

        /// <summary>Читает пресеты из файла (свой экспорт или от другого пользователя) и объединяет с текущими.</summary>
        public static (bool Ok, string Message) ImportFromFile(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var imported = JsonSerializer.Deserialize<List<ConfigurationPreset>>(json, Options);
                if (imported == null || imported.Count == 0)
                    return (false, "В файле нет ни одного пресета");

                var items = Load();
                var added = 0;
                var renamed = 0;
                foreach (var preset in imported.Where(p => p != null))
                {
                    if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "Пресет из файла";
                    if (preset.Config == null) preset.Config = new ConfigurationSnapshot();
                    var clash = items.FirstOrDefault(i => i.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
                    if (clash != null)
                    {
                        preset.Name = preset.Name + " (" + DateTime.Now.ToString("HH:mm") + ")";
                        renamed++;
                    }
                    preset.Id = Guid.NewGuid().ToString("N");
                    items.Insert(0, preset);
                    added++;
                }
                Save(items);
                var note = renamed > 0 ? $", переименовано из-за совпадений: {renamed}" : "";
                return (true, $"Импортировано пресетов: {added}{note}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось импортировать пресеты: " + ex.Message);
                return (false, "Не удалось прочитать файл: " + ex.Message);
            }
        }
    }
}
