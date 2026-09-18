using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ZapretGui.Core
{
    public sealed class EngineConsistencyItem
    {
        public string Name { get; init; } = "";
        public bool IsOk { get; init; }
        public string Details { get; init; } = "";
        public string SeverityKey => IsOk ? "Success" : "Warning";
    }

    /// <summary>Проверяет согласованность файлов движка после установки или отката.</summary>
    public sealed class EngineConsistencyReport
    {
        public bool IsConsistent => Checks.All(check => check.IsOk);
        public List<EngineConsistencyItem> Checks { get; init; } = new();
        public string Summary => IsConsistent
            ? "Файлы движка, драйвер, списки и стратегии согласованы"
            : "Есть предупреждения о комплектности движка";
    }

    public static class EngineConsistencyChecker
    {
        public static EngineConsistencyReport Check(string engineRoot)
        {
            var bin = Path.Combine(engineRoot, "bin");
            var lists = Path.Combine(engineRoot, "lists");
            var strategies = StrategyParser.LoadAll(engineRoot);
            var versionMarker = EngineService.ReadVersion(engineRoot);
            var divertDriver = Path.Combine(bin, "WinDivert64.sys");
            var divertDll = File.Exists(Path.Combine(bin, "WinDivert.dll"))
                ? Path.Combine(bin, "WinDivert.dll")
                : Path.Combine(engineRoot, "WinDivert.dll");
            var driverVersion = ReadFileVersion(divertDriver);
            var dllVersion = ReadFileVersion(divertDll);
            var versionsMatch = !string.IsNullOrWhiteSpace(driverVersion) &&
                                !string.IsNullOrWhiteSpace(dllVersion) &&
                                EngineService.CompareVersions(driverVersion, dllVersion) == 0;
            var hasVersionMarker = !string.IsNullOrWhiteSpace(versionMarker);
            var checks = new List<EngineConsistencyItem>
            {
                new()
                {
                    Name = "winws.exe",
                    IsOk = File.Exists(Path.Combine(bin, "winws.exe")),
                    Details = File.Exists(Path.Combine(bin, "winws.exe")) ? "Основной файл найден" : "Основной файл отсутствует"
                },
                new()
                {
                    Name = "WinDivert64.sys",
                    IsOk = File.Exists(Path.Combine(bin, "WinDivert64.sys")),
                    Details = File.Exists(Path.Combine(bin, "WinDivert64.sys")) ? "Драйвер найден" : "Драйвер отсутствует"
                },
                new()
                {
                    Name = "WinDivert.dll",
                    IsOk = File.Exists(Path.Combine(bin, "WinDivert.dll")) || File.Exists(Path.Combine(engineRoot, "WinDivert.dll")),
                    Details = File.Exists(Path.Combine(bin, "WinDivert.dll")) || File.Exists(Path.Combine(engineRoot, "WinDivert.dll"))
                        ? "Библиотека найдена" : "DLL не найдена; некоторые режимы могут не запуститься"
                },
                new()
                {
                    Name = "Версии движка и WinDivert",
                    IsOk = hasVersionMarker && versionsMatch,
                    Details = !hasVersionMarker
                        ? "Маркер версии движка отсутствует"
                        : !versionsMatch
                            ? $"Версии WinDivert не подтверждены: драйвер {DisplayVersion(driverVersion)}, DLL {DisplayVersion(dllVersion)}"
                            : $"Движок {versionMarker}; WinDivert {driverVersion} согласован с DLL"
                },
                new()
                {
                    Name = "Списки движка",
                    IsOk = HasFiles(lists),
                    Details = HasFiles(lists)
                        ? "Папка списков не пуста" : "Папка списков отсутствует или пуста"
                },
                new()
                {
                    Name = "Стратегии",
                    IsOk = strategies.Count > 0,
                    Details = strategies.Count > 0 ? $"Найдено стратегий: {strategies.Count}" : "Файлы стратегий не найдены"
                }
            };
            return new EngineConsistencyReport { Checks = checks };
        }

        private static string ReadFileVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                return FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? "";
            }
            catch { return ""; }
        }

        private static string DisplayVersion(string version)
            => string.IsNullOrWhiteSpace(version) ? "не встроена" : version;

        private static bool HasFiles(string folder)
        {
            try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any(); }
            catch { return false; }
        }
    }
}
