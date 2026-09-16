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
        public const string WinDivertService = "WinDivert";
        public const string WinDivert14Service = "WinDivert14";
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

        /// <summary>
        /// Безопасная остановка служб перед обновлением файлов движка.
        /// Останавливает zapret, WinDivert и WinDivert14 через «sc stop» (БЕЗ удаления),
        /// чтобы драйвер WinDivert выгрузился из ядра Windows до перезаписи WinDivert64.sys.
        /// Перезапись .sys-файла «на лету» приводит к BSOD — вызывать обязательно перед CopyEngine.
        /// </summary>
        public static async System.Threading.Tasks.Task<List<string>> StopForEngineUpdateAsync()
        {
            var report = new List<string>();

            foreach (var name in new[] { ZapretService, WinDivertService, WinDivert14Service })
            {
                try
                {
                    var state = Query(name);
                    if (state == ServiceState.NotInstalled)
                    {
                        report.Add($"Служба {name} не установлена");
                        continue;
                    }

                    if (state == ServiceState.Stopped)
                    {
                        report.Add($"Служба {name} уже остановлена");
                        continue;
                    }

                    AppLog.Info($"Останавливаю службу {name} перед обновлением движка…");
                    if (state != ServiceState.StopPending)
                        Stop(name); // при StopPending служба уже останавливается — только ждём

                    var stopped = await Shell.WaitForAsync(
                        () =>
                        {
                            var current = Query(name);
                            return current is ServiceState.Stopped or ServiceState.NotInstalled;
                        },
                        15000).ConfigureAwait(false);

                    if (stopped)
                    {
                        AppLog.Info($"Служба {name} остановлена");
                        report.Add($"Служба {name} остановлена");
                    }
                    else
                    {
                        AppLog.Warn($"Служба {name} не остановилась за 15 секунд — файлы драйвера могут быть заблокированы");
                        report.Add($"Служба {name} не остановилась (файлы драйвера пропустим при замене)");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Не удалось остановить службу {name}: {ex.Message}");
                    report.Add($"Служба {name}: ошибка остановки ({ex.Message})");
                }
            }

            return report;
        }

        /// <summary>
        /// Ожидание выгрузки драйвера WinDivert из ядра Windows после остановки служб.
        /// Даже после «sc stop» ядро держит .sys-файл ещё 1–2 секунды — без паузы
        /// перезапись WinDivert64.sys упирается в блокировку файла (или, хуже, в BSOD).
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> WaitForDriverUnloadAsync(
            string engineRoot, int timeoutMs = 12000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            // Сначала ждём, пока службы WinDivert реально перейдут в STOPPED
            while (DateTime.UtcNow < deadline)
            {
                var divert = Query(WinDivertService);
                var divert14 = Query(WinDivert14Service);
                var quiet = divert is ServiceState.Stopped or ServiceState.NotInstalled or ServiceState.Unknown
                            && divert14 is ServiceState.Stopped or ServiceState.NotInstalled or ServiceState.Unknown;
                if (quiet) break;
                await System.Threading.Tasks.Task.Delay(400).ConfigureAwait(false);
            }

            // Затем даём ядру выгрузить драйвер и проверяем, что файлы отпущены
            await System.Threading.Tasks.Task.Delay(2000).ConfigureAwait(false);

            var sys = System.IO.Path.Combine(engineRoot, "bin", "WinDivert64.sys");
            var dll = System.IO.Path.Combine(engineRoot, "bin", "WinDivert.dll");
            var waited = 0;
            while (DateTime.UtcNow < deadline && (IsFileLocked(sys) || IsFileLocked(dll)))
            {
                await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
                waited += 500;
            }

            var released = !IsFileLocked(sys) && !IsFileLocked(dll);
            if (released)
            {
                AppLog.Info(waited > 0
                    ? $"Драйвер WinDivert выгружен из ядра (ждали {waited / 1000.0:0.0} с)"
                    : "Драйвер WinDivert не держит файлы — можно обновлять");
            }
            else
            {
                AppLog.Warn("Файлы WinDivert всё ещё заблокированы — при обновлении они будут пропущены, " +
                            "перезагрузите Windows и повторите обновление");
            }

            return released;
        }

        /// <summary>Проверка, занят ли файл другим процессом или ядром (WinDivert64.sys в памяти).</summary>
        public static bool IsFileLocked(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return false;
                using var stream = System.IO.File.Open(path, System.IO.FileMode.Open,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
                return false;
            }
            catch (System.IO.IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                return false;
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

            foreach (var divert in new[] { WinDivertService, WinDivert14Service })
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
