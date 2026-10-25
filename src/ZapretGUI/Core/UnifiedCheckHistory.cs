using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZapretGui.Core
{
    public sealed class UnifiedCheckSnapshot
    {
        public DateTime RunAt { get; set; } = DateTime.Now;
        public string Summary { get; set; } = "";
        public string SummaryKey { get; set; } = "Info";
        public List<UnifiedSystemScore> SystemScores { get; set; } = new();
        public List<UnifiedVariantScore> VariantScores { get; set; } = new();
    }

    public static class UnifiedCheckHistoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = false };
        private static string FilePath => Path.Combine(AppPaths.AppData, "unified-check-history.json");

        public static UnifiedCheckSnapshot? LoadLast()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<UnifiedCheckSnapshot>(json, Options);
            }
            catch { return null; }
        }

        public static void Save(UnifiedCheckSnapshot snap)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(snap, Options));
            }
            catch {}
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch {}
        }
    }
}
