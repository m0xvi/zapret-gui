using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    /// <summary>Одна ячейка матрицы стратегия × DNS.</summary>
    public sealed class MatrixHistoryEntry
    {
        public string Strategy { get; set; } = "";
        public string Dns { get; set; } = "";
        public string DnsIp { get; set; } = "";
        public int Passed { get; set; }
        public int Total { get; set; } = 16;
        public long AvgMs { get; set; }
        public bool IsBest { get; set; }
        public string Status { get; set; } = "";
        public string StatusKey { get; set; } = "Muted";
    }

    /// <summary>Сохранённый прогон матрицы 23 стратегии × 4 DNS.</summary>
    public sealed class MatrixHistoryRecord
    {
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string Summary { get; set; } = "";
        public string BestText { get; set; } = "";
        public int TotalTests { get; set; }
        public List<MatrixHistoryEntry> Entries { get; set; } = new();

        [JsonIgnore]
        public string CreatedAtText => CreatedAt.ToString("dd.MM.yyyy HH:mm");

        [JsonIgnore]
        public string ShortSummary => string.IsNullOrWhiteSpace(BestText) ? Summary : $"{CreatedAtText} — {BestText} · {Summary}";
    }

    /// <summary>Локальный журнал последних прогонов матрицы.</summary>
    public static class MatrixHistoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
        private const int MaxRecords = 20;
        public static string FilePath => Path.Combine(AppPaths.AppData, "matrix-history.json");

        public static MatrixHistoryRecord? LoadLast()
        {
            try
            {
                var all = LoadAll();
                return all.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            }
            catch { return null; }
        }

        public static List<MatrixHistoryRecord> LoadAll()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<MatrixHistoryRecord>();
                var json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<MatrixHistoryRecord>>(json, Options) ?? new List<MatrixHistoryRecord>();
                return list.Where(r => r != null).OrderByDescending(r => r.CreatedAt).Take(MaxRecords).ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать историю матрицы: " + ex.Message);
                return new List<MatrixHistoryRecord>();
            }
        }

        public static void Save(MatrixHistoryRecord record)
        {
            try
            {
                var all = LoadAll();
                all.Insert(0, record);
                all = all.OrderByDescending(r => r.CreatedAt).Take(MaxRecords).ToList();
                var dir = Path.GetDirectoryName(FilePath) ?? AppPaths.AppData;
                Directory.CreateDirectory(dir);
                var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, Options));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить историю матрицы: " + ex.Message);
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception ex) { AppLog.Warn("Не удалось удалить историю матрицы: " + ex.Message); }
        }
    }
}
