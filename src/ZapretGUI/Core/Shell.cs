using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class ShellResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
        public bool Ok => ExitCode == 0;
        public string All => (StdOut + Environment.NewLine + StdErr).Trim();
    }

    /// <summary>Запуск внешних процессов и вспомогательные функции Windows.</summary>
    public static class Shell
    {
        static Shell()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        private static Encoding ConsoleEncoding => Encoding.GetEncoding(866);

        public static ShellResult Run(string fileName, IEnumerable<string> args, int timeoutMs = 30000, string? workDir = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using var p = Process.Start(psi);
                if (p == null) return new ShellResult { ExitCode = -1, StdErr = "Не удалось запустить " + fileName };

                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return new ShellResult { ExitCode = -2, StdOut = stdout.Result, StdErr = "Превышено время ожидания" };
                }
                return new ShellResult { ExitCode = p.ExitCode, StdOut = stdout.Result, StdErr = stderr.Result };
            }
            catch (Exception ex)
            {
                return new ShellResult { ExitCode = -1, StdErr = ex.Message };
            }
        }

        public static ShellResult RunCmd(string commandLine) => Run("cmd.exe", new[] { "/c", commandLine });
        public static ShellResult RunCmdHidden(string commandLine) => Run("cmd.exe", new[] { "/c", commandLine });

        /// <summary>Запуск процесса без ожидания (для winws.exe и браузера).</summary>
        public static Process? StartDetached(string fileName, IEnumerable<string> args, string? workDir = null,
            bool createNoWindow = true, bool shellExecute = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = shellExecute,
                CreateNoWindow = createNoWindow,
                WorkingDirectory = workDir ?? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
            };
            if (!shellExecute)
                foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Не удалось запустить {fileName}: {ex.Message}");
                return null;
            }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Error("Не удалось открыть ссылку: " + ex.Message); }
        }

        public static void OpenFolder(string path, bool selectFile = false)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) return;
                if (selectFile && File.Exists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                    Process.Start(new ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!)
                    {
                        UseShellExecute = true
                    });
            }
            catch (Exception ex) { AppLog.Error("Не удалось открыть проводник: " + ex.Message); }
        }

        public static void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "", Encoding.UTF8);
                }
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Error("Не удалось открыть файл: " + ex.Message); }
        }

        public static bool IsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>Перезапуск приложения с правами администратора.</summary>
        public static bool RestartElevated()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return false;
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (Win32Exception)
            {
                AppLog.Warn("Запрос прав администратора отклонён пользователем.");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка перезапуска с правами администратора: " + ex.Message);
                return false;
            }
        }

        public static bool IsProcessRunning(string processName)
        {
            try
            {
                var list = Process.GetProcessesByName(processName);
                foreach (var p in list) p.Dispose();
                return list.Length > 0;
            }
            catch { return false; }
        }

        public static void KillProcess(string processName)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try { p.Kill(true); p.WaitForExit(3000); } catch { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex) { AppLog.Warn($"Не удалось завершить {processName}: {ex.Message}"); }
        }

        public static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs, int stepMs = 200)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(stepMs).ConfigureAwait(false);
            }
            return condition();
        }
    }
}
