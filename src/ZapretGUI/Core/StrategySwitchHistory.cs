using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    public sealed class StrategySwitchRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime SwitchedAtUtc { get; set; } = DateTime.UtcNow;
        public string StrategyName { get; set; } = "";
        public string PreviousStrategyName { get; set; } = "";
        public string Source { get; set; } = ""; // manual, quick-setup, watchdog, monitoring, service-toggle
        public string Mode { get; set; } = ""; // service / standalone
        public bool Success { get; set; }
        public string Message { get; set; } = "";

        [JsonIgnore]
        public string SwitchedAtText => SwitchedAtUtc.ToLocalTime().ToString("dd.MM HH:mm");

        [JsonIgnore]
        public string SummaryText
        {
            get
            {
                var arrow = string.IsNullOrWhiteSpace(PreviousStrategyName) ? "" : $"{PreviousStrategyName} → ";
                var ok = Success ? "OK" : "ошибка";
                var mode = string.IsNullOrWhiteSpace(Mode) ? "" : $" · {Mode}";
                return $"{arrow}{StrategyName} · {Source}{mode} · {ok}";
            }
        }

        [JsonIgnore]
        public string StatusKey => Success ? "Success" : "Danger";
    }

    public static class StrategySwitchHistoryStore
    {
        private const int MaxRecords = 50;
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string FilePath => System.IO.Path.Combine(AppPaths.AppData, "strategy-switch-history.json");

        public static List<StrategySwitchRecord> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<StrategySwitchRecord>();
                var json = File.ReadAllText(FilePath);
                var records = JsonSerializer.Deserialize<List<StrategySwitchRecord>>(json, Options)
                    ?? new List<StrategySwitchRecord>();
                return records.Where(r => r != null).OrderByDescending(r => r.SwitchedAtUtc).Take(MaxRecords).ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать историю переключений: " + ex.Message);
                return new List<StrategySwitchRecord>();
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { AppLog.Warn("Не удалось удалить историю переключений: " + ex.Message); }
        }

        public static bool TryAppend(StrategySwitchRecord record)
        {
            try
            {
                var records = Load();
                records.Insert(0, record);
                records = records.OrderByDescending(r => r.SwitchedAtUtc).Take(MaxRecords).ToList();
                var dir = Path.GetDirectoryName(FilePath) ?? AppPaths.AppData;
                Directory.CreateDirectory(dir);
                var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records, Options));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить историю переключений: " + ex.Message);
                return false;
            }
        }
    }
}
