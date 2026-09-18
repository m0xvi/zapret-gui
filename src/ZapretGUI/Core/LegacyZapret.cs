using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Что пользователь выбрал в диалоге про найденный старый запуск запрета.</summary>
    public enum LegacyChoice { None, TakeOver, ImportAndTakeOver, StopOnly, Later, DontAsk }

    /// <summary>Информация о запрете, запущенном до GUI (служба или процесс из чужой папки).</summary>
    public sealed class LegacyInstallInfo
    {
        public bool ServiceExists { get; init; }
        public ServiceState ServiceState { get; init; } = ServiceState.NotInstalled;
        public string ServiceBinPath { get; init; } = "";
        public string ServiceEngineRoot { get; init; } = "";
        public bool IsForeignService { get; init; }
        public List<(int Pid, string Path)> ForeignWinws { get; init; } = new();
        public string StrategyName { get; init; } = "";

        /// <summary>Активный конфликт: чужой обход прямо сейчас работает и помешает нашему.</summary>
        public bool HasConflict => (IsForeignService && ServiceState == ServiceState.Running)
                                   || ForeignWinws.Count > 0;

        /// <summary>Папка чужого движка: из службы, иначе — из первого чужого процесса.</summary>
        public string ForeignRoot
        {
            get
            {
                if (ServiceEngineRoot.Length > 0) return ServiceEngineRoot;
                var first = ForeignWinws.FirstOrDefault().Path;
                return string.IsNullOrEmpty(first) ? "" : LegacyZapret.GuessEngineRoot(first);
            }
        }

        public string StateText => ServiceState switch
        {
            ServiceState.Running => "запущена",
            ServiceState.Stopped => "остановлена",
            ServiceState.StopPending => "зависли в остановке (STOP_PENDING)",
            ServiceState.StartPending => "запускается",
            ServiceState.NotInstalled => "не установлена",
            _ => ServiceState.ToString()
        };
    }

    /// <summary>
    /// Обнаружение запрета, установленного и запущенного до GUI (служба zapret из чужой папки
    /// или процессы winws.exe не из нашего движка), и разрешение конфликта: взять управление
    /// на себя, скопировать настройки или просто выключить старый.
    /// </summary>
    public static class LegacyZapret
    {
        /// <summary>Ищет чужой запуск запрета относительно папки движка из настроек.</summary>
        public static LegacyInstallInfo Detect(AppSettings settings)
        {
            var serviceState = WinServices.Query(WinServices.ZapretService);
            var serviceExists = serviceState != ServiceState.NotInstalled;

            var binPath = serviceExists ? WinServices.GetImagePath(WinServices.ZapretService) : "";
            var exePath = ExtractExePath(binPath);
            var root = exePath.Length > 0 ? GuessEngineRoot(exePath) : "";

            // Служба «чужая», если её winws.exe лежит не в нашей папке движка.
            // Если путь распознать не удалось — считаем своей, чтобы не пугать ложно.
            var foreign = serviceExists && root.Length > 0 && !PathsEqual(root, settings.EnginePath);

            var ownBin = Path.Combine(settings.EnginePath, "bin");
            var foreignWinws = new List<(int Pid, string Path)>();
            try
            {
                foreach (var (pid, path) in GetWinwsProcesses())
                {
                    var dir = Path.GetDirectoryName(path) ?? "";
                    if (!PathsEqual(dir, ownBin))
                        foreignWinws.Add((pid, path));
                }
            }
            catch { }

            return new LegacyInstallInfo
            {
                ServiceExists = serviceExists,
                ServiceState = serviceState,
                ServiceBinPath = binPath,
                ServiceEngineRoot = root,
                IsForeignService = foreign,
                ForeignWinws = foreignWinws,
                StrategyName = serviceExists ? WinServices.GetInstalledStrategyName() : ""
            };
        }

        /// <summary>Все процессы winws.exe с полными путями (требуются права администратора).</summary>
        public static List<(int Pid, string Path)> GetWinwsProcesses()
        {
            var result = new List<(int Pid, string Path)>();
            try
            {
                foreach (var process in Process.GetProcessesByName("winws"))
                {
                    try
                    {
                        var path = process.MainModule?.FileName ?? "";
                        if (path.Length > 0) result.Add((process.Id, path));
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Останавливает и удаляет чужую службу zapret, завершает чужие winws.exe.</summary>
        public static async Task<OperationResult> StopLegacyAsync(LegacyInstallInfo info,
            CancellationToken ct = default)
        {
            var parts = new List<string>();

            try
            {
                if (info.IsForeignService)
                {
                    AppLog.SvcInfo("Останавливаю чужую службу zapret: " + info.ServiceBinPath);
                    if (info.ServiceState == ServiceState.Running ||
                        info.ServiceState == ServiceState.StartPending)
                    {
                        WinServices.Stop(WinServices.ZapretService);
                        await Shell.WaitForAsync(
                            () => WinServices.Query(WinServices.ZapretService) != ServiceState.Running,
                            15000).ConfigureAwait(false);
                    }

                    // Службу удаляем, а не просто останавливаем: у неё обычно автозапуск,
                    // и после перезагрузки конфликт вернулся бы. Папка старого движка не трогается.
                    Shell.Run("sc.exe", new[] { "delete", WinServices.ZapretService }, 20000);
                    AppLog.SvcInfo("Чужая служба zapret удалена (папка старого движка не изменена)");
                    parts.Add("старая служба zapret остановлена и удалена");
                }

                if (info.ForeignWinws.Count > 0)
                {
                    var killed = 0;
                    foreach (var (pid, path) in info.ForeignWinws)
                    {
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            process.Kill(true);
                            killed++;
                            AppLog.SvcInfo($"Завершён чужой winws.exe (PID {pid}): {path}");
                        }
                        catch { }
                    }
                    parts.Add($"завершено чужих процессов winws.exe: {killed}");
                }

                // Даём драйверу отпустить ресурсы перед запуском нашего обхода
                var unloadRoot = info.ForeignRoot.Length > 0 ? info.ForeignRoot : AppPaths.DefaultEngine;
                await WinServices.WaitForDriverUnloadAsync(unloadRoot, 8000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.SvcWarn("Ошибка при остановке старого запрета: " + ex.Message);
                return OperationResult.Fail("Не удалось полностью остановить старый запрет: " + ex.Message);
            }

            if (ct.IsCancellationRequested)
                return OperationResult.Fail("Остановка старого запрета отменена");

            return OperationResult.Success(parts.Count > 0
                ? "Старый запрет выключен: " + string.Join("; ", parts)
                : "Старый запрет уже был выключен");
        }

        /// <summary>
        /// Копирует пользовательские данные из чужой папки в нашу: списки *-user.txt,
        /// резерв ipset, режимы фильтров. Возвращает также имя стратегии из реестра.
        /// </summary>
        public static (bool Ok, string Message, string StrategyName) ImportUserData(
            string oldRoot, string newRoot, string strategyName)
        {
            if (string.IsNullOrWhiteSpace(oldRoot) || !Directory.Exists(oldRoot))
                return (false, "Папка старого движка не найдена — копировать нечего", strategyName);

            var copied = 0;
            try
            {
                var oldLists = Path.Combine(oldRoot, "lists");
                var newLists = Path.Combine(newRoot, "lists");
                AppPaths.EnsureDir(newLists);

                if (Directory.Exists(oldLists))
                {
                    foreach (var file in Directory.GetFiles(oldLists, "*-user.txt"))
                    {
                        File.Copy(file, Path.Combine(newLists, Path.GetFileName(file)), true);
                        copied++;
                    }

                    var backup = Path.Combine(oldLists, "ipset-all.txt.backup");
                    if (File.Exists(backup))
                    {
                        File.Copy(backup, Path.Combine(newLists, "ipset-all.txt.backup"), true);
                        copied++;
                    }
                }

                var oldUtils = Path.Combine(oldRoot, "utils");
                var newUtils = Path.Combine(newRoot, "utils");
                if (Directory.Exists(oldUtils))
                {
                    AppPaths.EnsureDir(newUtils);
                    foreach (var name in new[] { "game_filter.enabled", "check_updates.enabled" })
                    {
                        var source = Path.Combine(oldUtils, name);
                        if (File.Exists(source))
                        {
                            File.Copy(source, Path.Combine(newUtils, name), true);
                            copied++;
                        }
                    }
                }

                AppLog.Info($"Из старого движка скопировано файлов настроек: {copied} ({oldRoot})");
                return (true, $"Скопировано файлов настроек: {copied}", strategyName);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось скопировать настройки старого запрета: " + ex.Message);
                return (false, "Не удалось скопировать настройки: " + ex.Message, strategyName);
            }
        }

        /// <summary>Достаёт путь к exe из командной строки службы (в кавычках или первый токен).</summary>
        public static string ExtractExePath(string binPath)
        {
            if (string.IsNullOrWhiteSpace(binPath)) return "";
            var text = binPath.Trim();
            if (text.StartsWith("\""))
            {
                var end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : "";
            }
            var space = text.IndexOf(' ');
            return space < 0 ? text : text.Substring(0, space);
        }

        /// <summary>Папка движка по пути winws.exe (…\bin\winws.exe → …).</summary>
        public static string GuessEngineRoot(string winwsExePath)
        {
            try
            {
                var bin = Path.GetDirectoryName(winwsExePath);
                if (string.IsNullOrEmpty(bin)) return "";
                if (Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(bin) ?? "";
                return bin;
            }
            catch { return ""; }
        }

        private static bool PathsEqual(string left, string right)
            => string.Equals(
                left.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
