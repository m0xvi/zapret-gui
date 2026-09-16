using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public enum BypassState { Stopped, RunningStandalone, RunningService, Starting, Stopping, Error }

    public sealed class BypassStatus
    {
        public BypassState State { get; init; } = BypassState.Stopped;
        public string StrategyName { get; init; } = "";
        public int? Pid { get; init; }
        public TimeSpan Uptime { get; init; }
        public ServiceState ServiceState { get; init; } = ServiceState.NotInstalled;
        public string ServiceStrategy { get; init; } = "";

        public bool IsRunning => State is BypassState.RunningStandalone or BypassState.RunningService;

        public string StateText => State switch
        {
            BypassState.RunningStandalone => "Обход запущен",
            BypassState.RunningService => "Обход запущен (служба)",
            BypassState.Starting => "Запуск…",
            BypassState.Stopping => "Остановка…",
            BypassState.Error => "Ошибка",
            _ => "Обход выключен"
        };

        public string UptimeText
        {
            get
            {
                if (!IsRunning || Uptime <= TimeSpan.Zero) return "—";
                if (Uptime.TotalHours >= 1) return $"{(int)Uptime.TotalHours} ч {Uptime.Minutes} мин";
                if (Uptime.TotalMinutes >= 1) return $"{Uptime.Minutes} мин {Uptime.Seconds} с";
                return $"{Uptime.Seconds} с";
            }
        }
    }

    public sealed class OperationResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public static OperationResult Success(string message = "") => new() { Ok = true, Message = message };
        public static OperationResult Fail(string message) => new() { Ok = false, Message = message };
    }

    /// <summary>
    /// Управление обходом: запуск/остановка winws.exe напрямую или через службу zapret,
    /// а также игровой фильтр и режимы ipset.
    /// </summary>
    public sealed class BypassController
    {
        private readonly AppSettings _settings;

        public BypassController(AppSettings settings) => _settings = settings;

        public string EngineRoot => _settings.EnginePath;
        public string BinDir => Path.Combine(EngineRoot, "bin");
        public string ListsDir => Path.Combine(EngineRoot, "lists");
        public string UtilsDir => Path.Combine(EngineRoot, "utils");
        public string WinwsPath => Path.Combine(BinDir, "winws.exe");

        // ---------------------------------------------------------------- статус

        public BypassStatus GetStatus()
        {
            var serviceState = WinServices.Query(WinServices.ZapretService);
            var serviceInstalled = serviceState != ServiceState.NotInstalled;
            var processes = SafeGetProcesses("winws");
            var running = processes.Count > 0;

            var state = BypassState.Stopped;
            if (serviceState == ServiceState.Running && running) state = BypassState.RunningService;
            else if (running) state = BypassState.RunningStandalone;

            var uptime = TimeSpan.Zero;
            int? pid = null;
            if (running)
            {
                try
                {
                    var process = processes[0];
                    pid = process.Id;
                    uptime = DateTime.Now - process.StartTime;
                }
                catch { }
            }

            foreach (var p in processes) p.Dispose();

            var serviceStrategy = serviceInstalled ? WinServices.GetInstalledStrategyName() : "";
            var strategy = running
                ? (!string.IsNullOrEmpty(serviceStrategy) && state == BypassState.RunningService ? serviceStrategy : _settings.SelectedStrategy)
                : "";

            return new BypassStatus
            {
                State = state,
                StrategyName = strategy,
                Pid = pid,
                Uptime = uptime,
                ServiceState = serviceState,
                ServiceStrategy = serviceStrategy
            };
        }

        private static List<Process> SafeGetProcesses(string name)
        {
            try { return Process.GetProcessesByName(name).ToList(); }
            catch { return new List<Process>(); }
        }

        // ---------------------------------------------------------------- запуск / остановка

        public List<string> BuildArgs(StrategyInfo strategy, GameFilterMode gameFilter)
        {
            var tcp = gameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.TcpOnly ? "1024-65535" : "12";
            var udp = gameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.UdpOnly ? "1024-65535" : "12";

            return strategy.Args
                .Select(a => a
                    .Replace(StrategyParser.GameFilterTcpToken, tcp)
                    .Replace(StrategyParser.GameFilterUdpToken, udp))
                .ToList();
        }

        public async Task<OperationResult> StartAsync(StrategyInfo strategy, GameFilterMode gameFilter, bool showConsole,
            CancellationToken ct = default)
        {
            if (!File.Exists(WinwsPath))
                return OperationResult.Fail("Не найден bin\\winws.exe. Скачайте движок на странице «Обновления».");

            var serviceState = WinServices.Query(WinServices.ZapretService);
            if (serviceState == ServiceState.Running)
                return OperationResult.Fail("Обход уже запущен как служба. Остановите службу или удалите её " +
                                            "(кнопка «Удалить службу»), чтобы запускать обход вручную.");

            if (Shell.IsProcessRunning("winws"))
            {
                AppLog.Warn("Найден запущенный winws.exe — перезапускаю с новой стратегией");
                Shell.KillProcess("winws");
                await Shell.WaitForAsync(() => !Shell.IsProcessRunning("winws"), 5000).ConfigureAwait(false);
            }

            StrategyParser.EnsureUserLists(EngineRoot);
            WinServices.EnsureTcpTimestamps();

            var args = BuildArgs(strategy, gameFilter);
            AppLog.Info($"Запуск стратегии «{strategy.Name}»: {args.Count} аргументов");

            var process = Shell.StartDetached(WinwsPath, args, BinDir, createNoWindow: !showConsole);
            if (process == null)
                return OperationResult.Fail("winws.exe не запустился. Проверьте антивирус (WinDivert) и права администратора.");

            var started = await Shell.WaitForAsync(() => Shell.IsProcessRunning("winws"), 6000).ConfigureAwait(false);
            if (!started)
                return OperationResult.Fail("winws.exe завершился сразу после запуска. Запустите диагностику: скорее всего конфликт с другим обходом.");

            _settings.SelectedStrategy = strategy.Name;
            SettingsStore.Save(_settings);

            // Мягкая проверка обновлений — как это делает general.bat
            if (_settings.AutoCheckEngineUpdates)
                _ = Task.Run(() => CheckEngineUpdateQuietAsync(ct), ct);

            return OperationResult.Success($"Стратегия «{strategy.Name}» запущена");
        }

        public async Task<OperationResult> StopAsync(CancellationToken ct = default)
        {
            var serviceState = WinServices.Query(WinServices.ZapretService);
            if (serviceState == ServiceState.Running)
            {
                WinServices.Stop(WinServices.ZapretService);
                await Shell.WaitForAsync(
                    () => WinServices.Query(WinServices.ZapretService) != ServiceState.Running, 15000).ConfigureAwait(false);
            }

            if (Shell.IsProcessRunning("winws"))
            {
                Shell.KillProcess("winws");
                await Shell.WaitForAsync(() => !Shell.IsProcessRunning("winws"), 8000).ConfigureAwait(false);
                AppLog.Info("Обход остановлен");
                return OperationResult.Success("Обход остановлен");
            }

            return OperationResult.Success("Обход уже был остановлен");
        }

        private async Task CheckEngineUpdateQuietAsync(CancellationToken ct)
        {
            try
            {
                var current = EngineService.ReadVersion(EngineRoot);
                var latest = await EngineService.GetLatestVersionTextAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(latest) && EngineService.CompareVersions(latest, current) > 0)
                    AppLog.Warn($"Доступна новая версия движка: {latest} (установлена {current})");
            }
            catch { }
        }

        // ---------------------------------------------------------------- подготовка к обновлению

        /// <summary>
        /// Безопасная подготовка к перезаписи файлов движка.
        /// Останавливает обход (службу zapret и winws.exe), завершает зависшие процессы,
        /// останавливает службы WinDivert/WinDivert14 и ждёт выгрузки драйвера из ядра Windows.
        /// Без этого перезапись bin\WinDivert64.sys «на лету» заканчивается BSOD.
        /// Возвращает true, если обход работал (после обновления его стоит перезапустить).
        /// </summary>
        public async Task<bool> PrepareForEngineUpdateAsync(CancellationToken ct = default)
        {
            var wasRunning = GetStatus().IsRunning;
            AppLog.Info("Подготовка к обновлению движка: останавливаю обход…");

            try
            {
                // 1. Штатная остановка: служба zapret + процесс winws.exe
                await StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка при остановке обхода перед обновлением: " + ex.Message);
            }

            // 2. Добиваем зависшие процессы winws.exe (служба могла оставить процесс)
            if (Shell.IsProcessRunning("winws"))
            {
                AppLog.Info("Завершаю зависший процесс winws.exe…");
                Shell.KillProcess("winws");
                await Shell.WaitForAsync(() => !Shell.IsProcessRunning("winws"), 8000).ConfigureAwait(false);
            }

            if (Shell.IsProcessRunning("winws"))
            {
                AppLog.Warn("Процесс winws.exe не завершился — файлы движка могут быть заблокированы");
            }

            // 3. Останавливаем службы WinDivert, чтобы ядро отпустило WinDivert64.sys
            try
            {
                await WinServices.StopForEngineUpdateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка при остановке служб WinDivert: " + ex.Message);
            }

            // 4. Ждём выгрузки драйвера из ядра (с паузой 2 с внутри)
            try
            {
                await WinServices.WaitForDriverUnloadAsync(EngineRoot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка при ожидании выгрузки драйвера: " + ex.Message);
            }

            if (ct.IsCancellationRequested)
                AppLog.Warn("Подготовка к обновлению была отменена");

            AppLog.Info(wasRunning
                ? "Обход остановлен, драйвер выгружен — можно обновлять файлы"
                : "Обход не работал — файлы движка свободны для обновления");
            return wasRunning;
        }

        // ---------------------------------------------------------------- служба

        public async Task<OperationResult> InstallServiceAsync(StrategyInfo strategy, GameFilterMode gameFilter,
            CancellationToken ct = default)
        {
            if (!File.Exists(WinwsPath))
                return OperationResult.Fail("Не найден bin\\winws.exe. Скачайте движок на странице «Обновления».");

            if (Shell.IsProcessRunning("winws") && WinServices.Query(WinServices.ZapretService) != ServiceState.Running)
            {
                Shell.KillProcess("winws");
                await Shell.WaitForAsync(() => !Shell.IsProcessRunning("winws"), 5000).ConfigureAwait(false);
            }

            StrategyParser.EnsureUserLists(EngineRoot);
            WinServices.EnsureTcpTimestamps();

            var args = BuildArgs(strategy, gameFilter);
            var imagePath = "\"" + WinwsPath + "\" " + string.Join(" ", args.Select(QuoteIfNeeded));

            AppLog.Info($"Установка службы zapret со стратегией «{strategy.Name}»");
            WinServices.Delete(WinServices.ZapretService);

            var create = WinServices.Create(WinServices.ZapretService, imagePath, "zapret", "Zapret DPI bypass software");
            if (!create.Ok)
                return OperationResult.Fail("sc create не сработал: " + create.All);

            WinServices.SetInstalledStrategyName(strategy.Name);

            var start = WinServices.Start(WinServices.ZapretService);
            var running = await Shell.WaitForAsync(
                () => WinServices.Query(WinServices.ZapretService) == ServiceState.Running, 15000).ConfigureAwait(false);

            if (!start.Ok || !running)
                return OperationResult.Fail("Служба создана, но не запустилась: " + start.All);

            _settings.SelectedStrategy = strategy.Name;
            SettingsStore.Save(_settings);
            return OperationResult.Success($"Служба zapret установлена со стратегией «{strategy.Name}»");
        }

        public async Task<OperationResult> RemoveServiceAsync()
        {
            var report = WinServices.RemoveEverything();
            await Task.Delay(500).ConfigureAwait(false);
            return OperationResult.Success(string.Join("; ", report));
        }

        public bool IsServiceInstalled() => WinServices.Query(WinServices.ZapretService) != ServiceState.NotInstalled;

        private static string QuoteIfNeeded(string argument)
            => argument.Contains(' ') && !argument.StartsWith("\"", StringComparison.Ordinal)
                ? "\"" + argument + "\""
                : argument;
    }
}
