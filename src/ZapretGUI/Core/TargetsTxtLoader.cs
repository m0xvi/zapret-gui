using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZapretGui.Core
{
    /// <summary>
    /// Загружает дополнительные контрольные цели из <c>utils/targets.txt</c> движка (формат Flowseal: Key = "https://..." или "PING:ip").
    /// Используется для расширения проверки стратегий — к базовым YouTube/Discord добавляются цели из файла.
    /// </summary>
    public static class TargetsTxtLoader
    {
        private static readonly Regex LineRegex = new(@"^\s*([A-Za-z0-9_]+)\s*=\s*""([^""]+)""\s*$", RegexOptions.Compiled);

        public static IReadOnlyList<MonitorTarget> LoadFromEngine(string enginePath)
        {
            var path = Path.Combine(enginePath, "utils", "targets.txt");
            return LoadFromFile(path);
        }

        public static IReadOnlyList<MonitorTarget> LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return Array.Empty<MonitorTarget>();
            try
            {
                var lines = File.ReadAllLines(filePath);
                var list = new List<MonitorTarget>();
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var m = LineRegex.Match(line);
                    if (!m.Success) continue;
                    var key = m.Groups[1].Value.Trim();
                    var value = m.Groups[2].Value.Trim();
                    if (value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
                    {
                        // PING-only цели пропускаем для HTTP-чеков — их проверит DiagnosisService отдельно
                        continue;
                    }
                    if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (MonitorTarget.TryCreate(value, key, out var target, out _) && target != null)
                    {
                        // Помечаем как встроенные из targets.txt
                        list.Add(target);
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[TargetsTxt] Не удалось прочитать {filePath}: {ex.Message}");
                return Array.Empty<MonitorTarget>();
            }
        }

        public static string GetFilePath(string enginePath) => Path.Combine(enginePath, "utils", "targets.txt");

        public static bool Exists(string enginePath) => File.Exists(GetFilePath(enginePath));

        public static int Count(string enginePath) => LoadFromEngine(enginePath).Count;
    }
}
