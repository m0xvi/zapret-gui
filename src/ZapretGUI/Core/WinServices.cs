using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace ZapretGui.Core
{
    public enum ServiceState { NotInstalled, Unknown, Stopped, StartPending, StopPending, Running, Paused }

    /// <summary>Обёртка над sc.exe для служб zapret и WinDivert (как в service.bat).</summary>
    public static class WinServices
    {
        public const string ZapretService = "zapret";
        public const string StrategyValueName = "zapret-discord-youtube";

        public static ServiceState Query(string name)
        {
            var result = Shell.Run("sc.exe", new[] { "query", name }, 15000);
            var text = result.All;
            if (text.Contains("1060") || text.Contains("не существует", StringComparison.OrdinalIgnoreCase))
                return ServiceState.NotInstalled;
            if (!text.Contains("STATE", StringComparison.OrdinalIgnoreCase))
                return result.ExitCode == 0 ? ServiceState.Unknown : ServiceState.NotInstalled;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
                var value = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                return parts[1].ToUpperInvariant() switch
                {
                    "RUNNING" => ServiceState.Running,
                    "STOPPED" => ServiceState.Stopped,
                    "START_PENDING" => ServiceState.StartPending,
                    "STOP_PENDING" => ServiceState.StopPending,
                    "PAUSED" => ServiceState.Paused,
                    _ => ServiceState.Unknown
                };
            }
            return ServiceState.Unknown;
        }

        public static bool Exists(string name) => Query(name) != ServiceState.NotInstalled;

        public static ShellResult Create(string name, string imagePath, string displayName, string description)
        {
            var create = Shell.Run("sc.exe", new[]
            {
                "create", name,
                "binPath=", imagePath,
                "DisplayName=", displayName,
                "start=", "auto"
            }, 20000);

            if (create.Ok && !string.IsNullOrWhiteSpace(description))
                Shell.Run("sc.exe", new[] { "description", name, description }, 15000);

            return create;
        }

        public static ShellResult Start(string name)
        {
            var r = Shell.Run("sc.exe", new[] { "start", name }, 30000);
            if (r.Ok) return r;
            return Shell.Run("net", new[] { "start", name }, 30000);
        }

        public static ShellResult Stop(string name)
        {
            var r = Shell.Run("sc.exe", new[] { "stop", name }, 30000);
            if (r.Ok) return r;
            return Shell.Run("net", new[] { "stop", name }, 30000);
        }

        public static void Delete(string name)
        {
            Stop(name);
            Shell.Run("sc.exe", new[] { "delete", name }, 20000);
        }

        /// <summary>Имя стратегии, установленной в службу (как в service.bat через reg add).</summary>
        public static string GetInstalledStrategyName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\" + ZapretService);
                return key?.GetValue(StrategyValueName) as string ?? "";
            }
            catch { return ""; }
        }

        public static void SetInstalledStrategyName(string name)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\" + ZapretService, true);
                key?.SetValue(StrategyValueName, name, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось записать имя стратегии в реестр: " + ex.Message);
            }
        }

        /// <summary>Полное удаление обхода: служба zapret, процесс winws.exe и службы WinDivert.</summary>
        public static List<string> RemoveEverything()
        {
            var report = new List<string>();

            if (Exists(ZapretService))
            {
                Delete(ZapretService);
                report.Add("Служба zapret удалена");
            }
            else report.Add("Служба zapret не была установлена");

            if (Shell.IsProcessRunning("winws"))
            {
                Shell.KillProcess("winws");
                report.Add("Процесс winws.exe остановлен");
            }

            foreach (var divert in new[] { "WinDivert", "WinDivert14" })
            {
                if (Exists(divert))
                {
                    Delete(divert);
                    report.Add($"Служба {divert} удалена");
                }
            }

            return report;
        }

        /// <summary>TCP timestamps нужны для корректной работы некоторых стратегий (tcp_enable в service.bat).</summary>
        public static void EnsureTcpTimestamps()
        {
            try
            {
                var show = Shell.Run("netsh", new[] { "interface", "tcp", "show", "global" }, 10000);
                if (show.All.Contains("timestamps", StringComparison.OrdinalIgnoreCase) &&
                    show.All.Contains("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    // «disallowed» тоже содержит "enabled"-подобный текст, поэтому уточняем
                    var ok = show.All.IndexOf("timestamps", StringComparison.OrdinalIgnoreCase);
                    var line = show.All.Substring(ok, Math.Min(80, show.All.Length - ok));
                    if (line.Contains("enabled", StringComparison.OrdinalIgnoreCase) &&
                        !line.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                Shell.Run("netsh", new[] { "interface", "tcp", "set", "global", "timestamps=enabled" }, 10000);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось включить TCP timestamps: " + ex.Message);
            }
        }
    }
}
