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
        private readonly Func<string, ServiceState> _queryService;
        private readonly Func<(bool Ok, string Message)> _ensureTcpTimestamps;
        private readonly Func<bool> _isProcessRunning;
        private readonly Action _killProcess;
        private readonly Func<string, IEnumerable<string>, string?, Process?> _startHiddenProcess;
        private Process? _capturedWinws;

        public BypassController(AppSettings settings,
            Func<string, ServiceState>? queryService = null,
            Func<(bool Ok, string Message)>? ensureTcpTimestamps = null,
            Func<bool>? isProcessRunning = null,
            Action? killProcess = null,
            Func<string, IEnumerable<string>, string?, Process?>? startHiddenProcess = null)
        {
            _settings = settings;
            _queryService = queryService ?? WinServices.Query;
            _ensureTcpTimestamps = ensureTcpTimestamps ?? WinServices.EnsureTcpTimestamps;
            _isProcessRunning = isProcessRunning ?? (() => Shell.IsProcessRunning("winws"));
            _killProcess = killProcess ?? (() => Shell.KillProcess("winws"));
            _startHiddenProcess = startHiddenProcess ?? ((path, args, workDir) =>
                Shell.StartWithCapture(path, args,
                    static (line, isError) =>
                        AppLog.SvcDebug((isError ? "[winws:err] " : "[winws] ") + line.Trim()),
                    workDir));
        }

        /// <summary>Останавливает перехват вывода winws.exe (сам процесс не трогает).</summary>
        public void StopCapture()
        {
            var captured = Interlocked.Exchange(ref _capturedWinws, null);
            Shell.CancelCapture(captured);
        }

        public string EngineRoot => _settings.EnginePath;
        public string BinDir => Path.Combine(EngineRoot, "bin");
        public string ListsDir => Path.Combine(EngineRoot, "lists");
        public string UtilsDir => Path.Combine(EngineRoot, "utils");
        public string WinwsPath => Path.Combine(BinDir, "winws.exe");

        // ---------------------------------------------------------------- статус

        public BypassStatus GetStatus()
        {
            var serviceState = _queryService(WinServices.ZapretService);
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
            => BypassArgumentBuilder.Build(
                strategy,
                gameFilter,
                _settings.GameFilterProfileId,
                _settings.CustomGameFilterTcpPorts,
                _settings.CustomGameFilterUdpPorts,
                _settings.SelectedFakeSni);

        public async Task<OperationResult> StartAsync(StrategyInfo strategy, GameFilterMode gameFilter, bool showConsole,
            CancellationToken ct = default, bool testMode = false)
        {
            if (!File.Exists(WinwsPath))
                return OperationResult.Fail("Не найден bin\\winws.exe. Скачайте движок на странице «Обновления».");

            var serviceState = _queryService(WinServices.ZapretService);
            if (serviceState == ServiceState.Running)
                return OperationResult.Fail("Обход уже запущен как служба. Остановите службу или удалите её " +
                                            "(кнопка «Удалить службу»), чтобы запускать обход вручную.");

            if (_isProcessRunning())
            {
                AppLog.SvcWarn("Найден запущенный winws.exe — перезапускаю с новой стратегией");
                _killProcess();
                await Shell.WaitForAsync(() => !_isProcessRunning(), 5000).ConfigureAwait(false);
            }

            StrategyParser.EnsureUserLists(EngineRoot);
            _ensureTcpTimestamps();

            var args = BuildArgs(strategy, gameFilter);
            AppLog.SvcInfo($"Запуск стратегии «{strategy.Name}»: {args.Count} аргументов");

            Process? process;
            if (showConsole)
            {
                process = Shell.StartDetached(WinwsPath, args, BinDir, createNoWindow: false);
            }
            else
            {
                // Скрытый режим: перехватываем вывод winws.exe в журнал (категория «Обход»).
                // Видно на странице «Журнал» с включённым фильтром «Отладка».
                StopCapture();
                process = _startHiddenProcess(WinwsPath, args, BinDir);
                _capturedWinws = process;
            }
            if (process == null)
                return OperationResult.Fail("winws.exe не запустился. Проверьте антивирус (WinDivert) и права администратора.");

            var started = await Shell.WaitForAsync(() =>
            {
                try { return !process.HasExited && _isProcessRunning(); }
                catch { return false; }
            }, 6000).ConfigureAwait(false);
            if (started)
            {
                // Не принимать короткий всплеск процесса за успешный запуск: это важно
                // для диагностики ошибок winws.exe и восстановления прежнего состояния.
                await Task.Delay(100).ConfigureAwait(false);
                try { started = !process.HasExited && _isProcessRunning(); }
                catch { started = false; }
            }
            if (!started)
            {
                StopCapture();
                return OperationResult.Fail("winws.exe завершился сразу после запуска. Запустите диагностику: скорее всего конфликт с другим обходом.");
            }

            if (!testMode)
            {
                _settings.SelectedStrategy = strategy.Name;
                SettingsStore.Save(_settings);

                // Мягкая проверка обновлений — как это делает general.bat
                if (_settings.AutoCheckEngineUpdates)
                    _ = Task.Run(() => CheckEngineUpdateQuietAsync(ct), ct);
            }

            return OperationResult.Success($"Стратегия «{strategy.Name}» запущена");
        }

        /// <summary>
        /// Бесшовное переключение стратегии без ручной остановки обхода.
        /// Сохраняет текущий режим: если обход запущен как служба — переустанавливает службу
        /// с новой стратегией, если как отдельный процесс — перезапускает процесс.
        /// Если обход выключен — просто запускает стратегию.
        /// </summary>
        public async Task<OperationResult> SwitchToStrategyAsync(StrategyInfo strategy, GameFilterMode gameFilter, bool showConsole,
            CancellationToken ct = default)
        {
            var status = GetStatus();
            if (status.State == BypassState.RunningService)
            {
                AppLog.SvcInfo($"Бесшовное переключение: служба zapret с «{status.ServiceStrategy}» → «{strategy.Name}»");
                return await InstallServiceAsync(strategy, gameFilter, ct).ConfigureAwait(false);
            }
            if (status.State == BypassState.RunningStandalone)
            {
                AppLog.SvcInfo($"Бесшовное переключение: standalone «{status.StrategyName}» → «{strategy.Name}»");
                await StopAsync(ct).ConfigureAwait(false);
                return await StartAsync(strategy, gameFilter, showConsole, ct).ConfigureAwait(false);
            }
            return await StartAsync(strategy, gameFilter, showConsole, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Запускает стратегию во временном режиме, проверяет YouTube/Discord/GitHub
        /// и затем возвращает прежнее состояние обхода. Настройки пользователя не меняются.
        /// </summary>
        public async Task<StrategyTestResult> TestStrategyAsync(StrategyInfo strategy,
            CancellationToken ct = default, IProgress<string>? progress = null)
        {
            var startedAt = DateTime.UtcNow;
            if (!File.Exists(WinwsPath))
            {
                return new StrategyTestResult
                {
                    Strategy = strategy,
                    ErrorMessage = "Не найден bin\\winws.exe"
                };
            }

            var legacy = LegacyZapret.Detect(_settings);
            if (legacy.HasConflict)
            {
                return new StrategyTestResult
                {
                    Strategy = strategy,
                    ErrorMessage = "Найден другой запущенный запрет. Сначала разрешите конфликт на странице «Обзор»."
                };
            }

            var before = GetStatus();
            var recoveryStartedAt = DateTime.Now;
            var recoveryActions = new List<string>();
            var recoveryIssue = "";
            var restoreService = before.State == BypassState.RunningService;
            var restoreStandalone = before.State == BypassState.RunningStandalone;
            var previousName = before.ServiceStrategy.Length > 0
                ? before.ServiceStrategy
                : _settings.SelectedStrategy;

            try
            {
                if (before.IsRunning)
                {
                    progress?.Report("Останавливаю текущий обход…");
                    var stopped = await StopAsync(ct).ConfigureAwait(false);
                    if (!stopped.Ok)
                    {
                        return new StrategyTestResult
                        {
                            Strategy = strategy,
                            ErrorMessage = stopped.Message,
                            Elapsed = DateTime.UtcNow - startedAt
                        };
                    }
                }

                progress?.Report($"Запускаю пробный процесс со стратегией «{strategy.Name}»…");
                var start = await StartAsync(strategy,
                    EngineService.GetGameFilterMode(EngineRoot), false, ct, testMode: true).ConfigureAwait(false);
                if (!start.Ok)
                {
                    return new StrategyTestResult
                    {
                        Strategy = strategy,
                        ErrorMessage = start.Message,
                        Elapsed = DateTime.UtcNow - startedAt
                    };
                }

                // Даём winws.exe загрузить WinDivert и начать обрабатывать трафик.
                progress?.Report("Ожидаю инициализацию WinDivert…");
                await Task.Delay(1200, ct).ConfigureAwait(false);
                var checks = await ConnectionTester.RunAsync(_settings, ct, progress).ConfigureAwait(false);
                return new StrategyTestResult
                {
                    Strategy = strategy,
                    Started = true,
                    Checks = checks,
                    Elapsed = DateTime.UtcNow - startedAt
                };
            }
            catch (OperationCanceledException)
            {
                return new StrategyTestResult
                {
                    Strategy = strategy,
                    ErrorMessage = "Проверка отменена",
                    Elapsed = DateTime.UtcNow - startedAt
                };
            }
            catch (Exception ex)
            {
                AppLog.SvcWarn($"Ошибка проверки стратегии «{strategy.Name}»: {ex.Message}");
                return new StrategyTestResult
                {
                    Strategy = strategy,
                    ErrorMessage = ex.Message,
                    Elapsed = DateTime.UtcNow - startedAt
                };
            }
            finally
            {
                try
                {
                    var stopped = await StopAsync().ConfigureAwait(false);
                    if (stopped.Ok) recoveryActions.Add("временный процесс остановлен");
                    else recoveryIssue = stopped.Message;
                }
                catch (Exception ex)
                {
                    recoveryIssue = "не удалось остановить временный процесс: " + ex.Message;
                }

                try
                {
                    if (restoreService)
                    {
                        var result = WinServices.Start(WinServices.ZapretService);
                        var running = await Shell.WaitForAsync(
                            () => _queryService(WinServices.ZapretService) == ServiceState.Running,
                            15000).ConfigureAwait(false);
                        if (!result.Ok || !running)
                            recoveryIssue = "не удалось вернуть службу zapret";
                        else
                            recoveryActions.Add("служба zapret восстановлена");
                    }
                    else if (restoreStandalone && previousName.Length > 0)
                    {
                        var previous = StrategyParser.LoadAll(EngineRoot)
                            .FirstOrDefault(s => s.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase))
                            ?? StrategyCandidateStore.FindStrategy(previousName);
                        if (previous != null)
                        {
                            var restored = await StartAsync(previous,
                                EngineService.GetGameFilterMode(EngineRoot), _settings.ShowWinwsConsole).ConfigureAwait(false);
                            if (restored.Ok) recoveryActions.Add("прежняя стратегия восстановлена");
                            else recoveryIssue = restored.Message;
                        }
                    }
                }
                catch (Exception ex)
                {
                    recoveryIssue = "не удалось восстановить прежнее состояние: " + ex.Message;
                    AppLog.SvcWarn("Не удалось восстановить прежнее состояние обхода: " + ex.Message);
                }

                var after = GetStatus();
                var restoredState = string.IsNullOrWhiteSpace(recoveryIssue) &&
                    ((!before.IsRunning && !after.IsRunning) ||
                     (restoreService && after.State == BypassState.RunningService) ||
                     (restoreStandalone && after.State == BypassState.RunningStandalone));
                RecoveryJournalStore.Append(
                    "Пробная проверка стратегии «" + strategy.Name + "»",
                    before.State.ToString(), after.State.ToString(), restoredState,
                    string.Join("; ", recoveryActions), recoveryIssue, recoveryStartedAt);
            }
        }

        /// <summary>
        /// Запускает DPI-suite в прямом режиме: временно убирает активный обход,
        /// переводит ipset в режим any как в оригинальном test zapret.ps1 и
        /// обязательно восстанавливает состояние после проверки.
        /// </summary>
        public async Task<DpiCheckSnapshot> RunDpiCheckAsync(string? customHost,
            IProgress<string>? progress = null, CancellationToken ct = default, int? maxTargets = null,
            StrategyInfo? temporaryStrategy = null)
        {
            var before = GetStatus();
            var restoreService = before.State == BypassState.RunningService;
            var restoreStandalone = before.State == BypassState.RunningStandalone;
            var previousName = before.ServiceStrategy.Length > 0 ? before.ServiceStrategy : _settings.SelectedStrategy;
            var ipsetPath = Path.Combine(EngineRoot, "lists", "ipset-all.txt");
            var ipsetBefore = EngineService.GetIpsetMode(EngineRoot);
            var ipsetBytesBefore = File.Exists(ipsetPath) ? File.ReadAllBytes(ipsetPath) : null;
            var ipsetChanged = false;
            var temporaryStarted = false;

            try
            {
                if (before.IsRunning)
                {
                    var stopped = await StopAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!stopped.Ok)
                        AppLog.SvcWarn("Не удалось полностью остановить обход перед DPI-suite: " + stopped.Message);
                }

                if (ipsetBytesBefore != null && ipsetBefore != IpsetMode.Any)
                {
                    // Для теста сохраняем байты в памяти, не трогая штатный .backup
                    // движка. Это не ломает обновление и пользовательский режим ipset.
                    File.WriteAllBytes(ipsetPath, Array.Empty<byte>());
                    ipsetChanged = true;
                    AppLog.SvcInfo("DPI-suite: ipset временно переведён в режим any");
                }

                if (temporaryStrategy != null)
                {
                    var started = await StartAsync(temporaryStrategy,
                        EngineService.GetGameFilterMode(EngineRoot), false, ct,
                        testMode: true).ConfigureAwait(false);
                    if (!started.Ok)
                        throw new InvalidOperationException("Не удалось запустить временную стратегию для DPI-suite: " + started.Message);
                    temporaryStarted = true;
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                }

                return await DpiCheckerService.RunAsync(customHost, progress, ct, maxTargets)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (temporaryStarted)
                {
                    try
                    {
                        var stopped = await StopAsync(CancellationToken.None).ConfigureAwait(false);
                        if (!stopped.Ok)
                            AppLog.SvcWarn("Не удалось остановить временную стратегию после DPI-suite: " + stopped.Message);
                    }
                    catch (Exception ex)
                    {
                        AppLog.SvcWarn("Ошибка остановки временной стратегии после DPI-suite: " + ex.Message);
                    }
                }

                try
                {
                    if (ipsetChanged && ipsetBytesBefore != null)
                    {
                        File.WriteAllBytes(ipsetPath, ipsetBytesBefore);
                        AppLog.SvcInfo("DPI-suite: исходный режим ipset восстановлен");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.SvcWarn("Ошибка восстановления ipset после DPI-suite: " + ex.Message);
                }

                try
                {
                    if (restoreService)
                    {
                        var result = WinServices.Start(WinServices.ZapretService);
                        var running = await Shell.WaitForAsync(
                            () => _queryService(WinServices.ZapretService) == ServiceState.Running,
                            15000).ConfigureAwait(false);
                        if (!result.Ok || !running)
                            AppLog.SvcWarn("Не удалось восстановить службу zapret после DPI-suite");
                    }
                    else if (restoreStandalone && previousName.Length > 0)
                    {
                        var previous = StrategyParser.LoadAll(EngineRoot)
                            .FirstOrDefault(s => s.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase))
                            ?? StrategyCandidateStore.FindStrategy(previousName);
                        if (previous != null)
                        {
                            var restored = await StartAsync(previous,
                                EngineService.GetGameFilterMode(EngineRoot), _settings.ShowWinwsConsole,
                                CancellationToken.None).ConfigureAwait(false);
                            if (!restored.Ok)
                                AppLog.SvcWarn("Не удалось восстановить прежнюю стратегию после DPI-suite: " + restored.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.SvcWarn("Ошибка восстановления обхода после DPI-suite: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Сравнивает ресурс без обхода и с текущей стратегией. Это единственный способ
        /// отличить сбой самой стратегии от внешней блокировки без ручной проверки.
        /// Проверка кратко останавливает обход и затем возвращает прежнее состояние.
        /// </summary>
        public async Task<ResourceDiagnosisResult> DiagnoseResourceAsync(MonitorTarget target,
            CancellationToken ct = default)
        {
            var before = GetStatus();
            var recoveryStartedAt = DateTime.Now;
            var recoveryActions = new List<string>();
            var recoveryIssue = "";
            var restoreService = before.State == BypassState.RunningService;
            var restoreStandalone = before.State == BypassState.RunningStandalone;
            var previousName = before.ServiceStrategy.Length > 0 ? before.ServiceStrategy : _settings.SelectedStrategy;
            ResourceProbeResult direct;
            ResourceProbeResult withBypass;

            try
            {
                if (before.IsRunning)
                    await StopAsync(ct).ConfigureAwait(false);

                direct = await ResourceProbe.CheckAsync(target, ct).ConfigureAwait(false);
                var currentName = before.ServiceStrategy.Length > 0 ? before.ServiceStrategy : _settings.SelectedStrategy;
                var strategies = StrategyParser.LoadAll(EngineRoot);
                var strategy = strategies.FirstOrDefault(s => s.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase))
                    ?? StrategyCandidateStore.FindStrategy(currentName)
                    ?? strategies.FirstOrDefault();

                if (strategy == null)
                {
                    return new ResourceDiagnosisResult
                    {
                        Target = target,
                        Direct = direct,
                        WithBypass = direct,
                        Kind = ResourceDiagnosisKind.Unknown,
                        Level = "не определён",
                        Confidence = "низкая",
                        Summary = "Нет стратегии для сравнения. Выберите стратегию на странице «Стратегии»."
                    };
                }

                var start = await StartAsync(strategy,
                    EngineService.GetGameFilterMode(EngineRoot), false, ct, testMode: true).ConfigureAwait(false);
                if (!start.Ok)
                {
                    withBypass = new ResourceProbeResult
                    {
                        Target = target,
                        Details = start.Message,
                        Kind = ResourceResultKind.Unknown
                    };
                }
                else
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    withBypass = await ResourceProbe.CheckAsync(target, ct).ConfigureAwait(false);
                }

                return BuildDiagnosis(target, direct, withBypass);
            }
            catch (Exception ex)
            {
                AppLog.SvcWarn($"Не удалось сравнить ресурс «{target.Name}»: {ex.Message}");
                var failed = new ResourceProbeResult
                {
                    Target = target,
                    Details = ex.Message,
                    Kind = ResourceResultKind.Unknown
                };
                return new ResourceDiagnosisResult
                {
                    Target = target,
                    Direct = failed,
                    WithBypass = failed,
                    Kind = ResourceDiagnosisKind.Unknown,
                    Level = "не определён",
                    Confidence = "низкая",
                    Summary = "Не удалось выполнить сравнение: " + ex.Message
                };
            }
            finally
            {
                try
                {
                    var stopped = await StopAsync().ConfigureAwait(false);
                    if (stopped.Ok) recoveryActions.Add("временный процесс остановлен");
                    else recoveryIssue = stopped.Message;
                }
                catch (Exception ex)
                {
                    recoveryIssue = "не удалось остановить временный процесс: " + ex.Message;
                }
                try
                {
                    if (restoreService)
                    {
                        WinServices.Start(WinServices.ZapretService);
                        var running = await Shell.WaitForAsync(
                            () => _queryService(WinServices.ZapretService) == ServiceState.Running,
                            15000).ConfigureAwait(false);
                        if (running) recoveryActions.Add("служба zapret восстановлена");
                        else recoveryIssue = "не удалось вернуть службу zapret";
                    }
                    else if (restoreStandalone && previousName.Length > 0)
                    {
                        var previous = StrategyParser.LoadAll(EngineRoot)
                            .FirstOrDefault(s => s.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase))
                            ?? StrategyCandidateStore.FindStrategy(previousName);
                        if (previous != null)
                        {
                            var restored = await StartAsync(previous, EngineService.GetGameFilterMode(EngineRoot),
                                _settings.ShowWinwsConsole).ConfigureAwait(false);
                            if (restored.Ok) recoveryActions.Add("прежняя стратегия восстановлена");
                            else recoveryIssue = restored.Message;
                        }
                    }
                }
                catch (Exception ex)
                {
                    recoveryIssue = "не удалось восстановить обход после сравнения: " + ex.Message;
                    AppLog.SvcWarn("Не удалось восстановить обход после сравнения: " + ex.Message);
                }

                var after = GetStatus();
                var restoredState = string.IsNullOrWhiteSpace(recoveryIssue) &&
                    ((!before.IsRunning && !after.IsRunning) ||
                     (restoreService && after.State == BypassState.RunningService) ||
                     (restoreStandalone && after.State == BypassState.RunningStandalone));
                RecoveryJournalStore.Append(
                    "Сравнение ресурса «" + target.Name + "»",
                    before.State.ToString(), after.State.ToString(), restoredState,
                    string.Join("; ", recoveryActions), recoveryIssue, recoveryStartedAt);
            }
        }

        /// <summary>
        /// Проверяет конкретную стратегию непосредственно на игровом ресурсе и возвращает
        /// TCP-задержку. Пользовательские настройки и исходное состояние обхода восстанавливаются.
        /// </summary>
        public async Task<ResourceProbeResult> TestStrategyOnResourceAsync(StrategyInfo strategy,
            MonitorTarget target, CancellationToken ct = default)
        {
            var before = GetStatus();
            var restoreService = before.State == BypassState.RunningService;
            var restoreStandalone = before.State == BypassState.RunningStandalone;
            var previousName = before.ServiceStrategy.Length > 0 ? before.ServiceStrategy : _settings.SelectedStrategy;

            try
            {
                if (before.IsRunning)
                {
                    var stopped = await StopAsync(ct).ConfigureAwait(false);
                    if (!stopped.Ok)
                        return new ResourceProbeResult
                        {
                            Target = target,
                            Kind = ResourceResultKind.Unknown,
                            Details = stopped.Message
                        };
                }

                var start = await StartAsync(strategy,
                    EngineService.GetGameFilterMode(EngineRoot), false, ct, testMode: true).ConfigureAwait(false);
                if (!start.Ok)
                    return new ResourceProbeResult
                    {
                        Target = target,
                        Kind = ResourceResultKind.Unknown,
                        Details = start.Message
                    };

                await Task.Delay(1000, ct).ConfigureAwait(false);
                return await ResourceProbe.CheckAsync(target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ResourceProbeResult
                {
                    Target = target,
                    Kind = ResourceResultKind.Unknown,
                    Details = "Проверка отменена"
                };
            }
            catch (Exception ex)
            {
                return new ResourceProbeResult
                {
                    Target = target,
                    Kind = ResourceResultKind.Unknown,
                    Details = ex.Message
                };
            }
            finally
            {
                try { await StopAsync().ConfigureAwait(false); } catch { }
                try
                {
                    if (restoreService)
                    {
                        WinServices.Start(WinServices.ZapretService);
                        await Shell.WaitForAsync(
                            () => _queryService(WinServices.ZapretService) == ServiceState.Running,
                            15000).ConfigureAwait(false);
                    }
                    else if (restoreStandalone && previousName.Length > 0)
                    {
                        var previous = StrategyParser.LoadAll(EngineRoot)
                            .FirstOrDefault(s => s.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase))
                            ?? StrategyCandidateStore.FindStrategy(previousName);
                        if (previous != null)
                            await StartAsync(previous, EngineService.GetGameFilterMode(EngineRoot),
                                _settings.ShowWinwsConsole).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.SvcWarn("Не удалось восстановить обход после проверки игры: " + ex.Message);
                }
            }
        }

        private static ResourceDiagnosisResult BuildDiagnosis(MonitorTarget target,
            ResourceProbeResult direct, ResourceProbeResult withBypass)
        {
            if (direct.Ok && withBypass.Ok)
            {
                if (target.IsGame && withBypass.Milliseconds > direct.Milliseconds * 1.5 + 30)
                    return new ResourceDiagnosisResult
                    {
                        Target = target, Direct = direct, WithBypass = withBypass,
                        Kind = ResourceDiagnosisKind.StrategyBreaks, Level = "стратегия повышает пинг",
                        Confidence = "средняя",
                        Summary = $"Игра отвечает, но с текущей стратегией задержка выросла с {direct.Milliseconds} до {withBypass.Milliseconds} мс. Это похоже на неподходящую стратегию, а не на медленный интернет."
                    };
                return new ResourceDiagnosisResult
                {
                    Target = target, Direct = direct, WithBypass = withBypass,
                    Kind = ResourceDiagnosisKind.Available, Level = "нет блокировки",
                    Confidence = "высокая",
                    Summary = "Ресурс доступен и без обхода, и с ним."
                };
            }
            if (!direct.Ok && withBypass.Ok)
                return new ResourceDiagnosisResult
                {
                    Target = target, Direct = direct, WithBypass = withBypass,
                    Kind = ResourceDiagnosisKind.BypassHelps, Level = "вероятна блокировка DPI",
                    Confidence = "высокая",
                    Summary = "Без обхода ресурс недоступен, а с текущей стратегией открывается. Вероятна блокировка DPI/провайдера; точное имя провайдера определить по локальной проверке нельзя."
                };
            if (direct.Ok && !withBypass.Ok)
                return new ResourceDiagnosisResult
                {
                    Target = target, Direct = direct, WithBypass = withBypass,
                    Kind = ResourceDiagnosisKind.StrategyBreaks, Level = "стратегия мешает",
                    Confidence = "высокая",
                    Summary = "Без обхода ресурс открывается, а с текущей стратегией — нет. Стратегию нужно заменить."
                };
            return new ResourceDiagnosisResult
            {
                Target = target, Direct = direct, WithBypass = withBypass,
                Kind = ResourceDiagnosisKind.ProviderOrServerIssue, Level = "внешняя проблема",
                Confidence = "средняя",
                Summary = $"Ресурс не открылся ни напрямую, ни через текущую стратегию. Возможны блокировка провайдера, сбой сервера, проблема DNS или сети. DNS напрямую: {direct.DnsDetails}; через обход: {withBypass.DnsDetails}."
            };
        }

        public async Task<OperationResult> StopAsync(CancellationToken ct = default)
        {
            StopCapture();
            var serviceState = _queryService(WinServices.ZapretService);
            if (serviceState == ServiceState.Running)
            {
                WinServices.Stop(WinServices.ZapretService);
                await Shell.WaitForAsync(
                    () => _queryService(WinServices.ZapretService) != ServiceState.Running, 15000).ConfigureAwait(false);
            }

            if (_isProcessRunning())
            {
                _killProcess();
                await Shell.WaitForAsync(() => !_isProcessRunning(), 8000).ConfigureAwait(false);
                AppLog.SvcInfo("Обход остановлен");
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
                    AppLog.SvcWarn($"Доступна новая версия движка: {latest} (установлена {current})");
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
            AppLog.SvcInfo("Подготовка к обновлению движка: останавливаю обход…");

            try
            {
                // 1. Штатная остановка: служба zapret + процесс winws.exe
                await StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.SvcWarn("Ошибка при остановке обхода перед обновлением: " + ex.Message);
            }

            // 2. Добиваем зависшие процессы winws.exe (служба могла оставить процесс)
            if (_isProcessRunning())
            {
                AppLog.SvcInfo("Завершаю зависший процесс winws.exe…");
                _killProcess();
                await Shell.WaitForAsync(() => !_isProcessRunning(), 8000).ConfigureAwait(false);
            }

            if (_isProcessRunning())
            {
                AppLog.SvcWarn("Процесс winws.exe не завершился — файлы движка могут быть заблокированы");
            }

            // 3. Останавливаем службы WinDivert, чтобы ядро отпустило WinDivert64.sys
            try
            {
                await WinServices.StopForEngineUpdateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.SvcWarn("Ошибка при остановке служб WinDivert: " + ex.Message);
            }

            // 4. Ждём выгрузки драйвера из ядра (с паузой 2 с внутри)
            try
            {
                await WinServices.WaitForDriverUnloadAsync(EngineRoot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.SvcWarn("Ошибка при ожидании выгрузки драйвера: " + ex.Message);
            }

            if (ct.IsCancellationRequested)
                AppLog.SvcWarn("Подготовка к обновлению была отменена");

            AppLog.SvcInfo(wasRunning
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

            if (_isProcessRunning() && _queryService(WinServices.ZapretService) != ServiceState.Running)
            {
                _killProcess();
                await Shell.WaitForAsync(() => !_isProcessRunning(), 5000).ConfigureAwait(false);
            }

            StrategyParser.EnsureUserLists(EngineRoot);
            _ensureTcpTimestamps();

            var args = BuildArgs(strategy, gameFilter);
            var imagePath = "\"" + WinwsPath + "\" " + string.Join(" ", args.Select(QuoteIfNeeded));

            AppLog.SvcInfo($"Установка службы zapret со стратегией «{strategy.Name}»");
            WinServices.Delete(WinServices.ZapretService);

            var create = WinServices.Create(WinServices.ZapretService, imagePath, "zapret", "Zapret DPI bypass software");
            if (!create.Ok)
                return OperationResult.Fail("sc create не сработал: " + create.All);

            WinServices.SetInstalledStrategyName(strategy.Name);

            var start = WinServices.Start(WinServices.ZapretService);
            var running = await Shell.WaitForAsync(
                () => _queryService(WinServices.ZapretService) == ServiceState.Running, 15000).ConfigureAwait(false);

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

        public bool IsServiceInstalled() => _queryService(WinServices.ZapretService) != ServiceState.NotInstalled;

        private static string QuoteIfNeeded(string argument)
            => argument.Contains(' ') && !argument.StartsWith("\"", StringComparison.Ordinal)
                ? "\"" + argument + "\""
                : argument;
    }
}
