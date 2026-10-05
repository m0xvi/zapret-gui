using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>
    /// Сторожевой таймер (Watchdog): отслеживает активность winws.exe и системной службы zapret,
    /// предотвращая зависания и выполняя мягкий перезапуск при аварийном падении.
    /// 1.17.24: бесшовно без окон, автоматически переключает стратегию после 3 падений,
    /// обеспечивает BFE и надёжный перезапуск службы (проблема «Служба установлена, но остановлена»).
    /// </summary>
    public sealed class WatchdogService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly BypassController _bypass;
        private readonly Func<StrategyInfo?> _strategyResolver;
        private readonly Func<IReadOnlyList<StrategyInfo>>? _allStrategiesResolver;
        private readonly Timer _timer;
        private int _recentCrashCount;
        private DateTime _lastCrashTime = DateTime.MinValue;
        private bool _isRecovering;

        public event Action<string>? EventLogged;
        public event Action<string>? AlertRaised;

        public bool IsRunning { get; private set; }
        public string LastEventText { get; private set; } = "Сторожевой таймер активен";
        public DateTime? LastRecoveryTime { get; private set; }

        public WatchdogService(AppSettings settings, BypassController bypass, Func<StrategyInfo?> strategyResolver, Func<IReadOnlyList<StrategyInfo>>? allStrategiesResolver = null)
        {
            _settings = settings;
            _bypass = bypass;
            _strategyResolver = strategyResolver;
            _allStrategiesResolver = allStrategiesResolver;
            _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (!_settings.WatchdogEnabled || _settings.SafeMode) return;
            IsRunning = true;
            var interval = Math.Clamp(_settings.WatchdogIntervalSeconds, 5, 120);
            _timer.Change(TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
            AppLog.Info($"[Watchdog] Запущен, интервал {interval}с, автоперезапуск={_settings.WatchdogAutoRestart}");
        }

        public void Stop()
        {
            IsRunning = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Пользователь вручную выключил — не трогаем (фикс v1.32.1: «сам включается»).</summary>
        public void NotifyManualStop()
        {
            AppLog.Info("[Watchdog] Ручное выключение — подавляю автозапуск до ручного включения");
        }
        public void NotifyManualStart()
        {
            _recentCrashCount = 0;
            _lastCrashTime = DateTime.MinValue;
            AppLog.Info("[Watchdog] Ручное включение — сбрасываю подавление");
        }

        private async void OnTimerTick(object? state)
        {
            if (!IsRunning || _isRecovering || !_settings.WatchdogEnabled || _settings.SafeMode) return;
            if (_settings.BypassManuallyStopped)
            {
                // Пользователь выключил — не восстанавливаем принудительно
                return;
            }

            try
            {
                var status = _bypass.GetStatus();

                // 1) Если служба установлена, но не Running — это аварийное состояние (пользователь видит «Служба установлена, но остановлена»)
                //    Раньше обрабатывался только RunningService с ServiceState!=Running, теперь также Stopped с установленным сервисом.
                var serviceInstalled = status.ServiceState != ServiceState.NotInstalled;
                if (serviceInstalled && status.ServiceState != ServiceState.Running)
                {
                    // Служба есть, но остановлена / зависла — пытаемся восстановить бесшовно
                    // Отличаем от Stopped когда обход выключен намеренно: если служба установлена, но не запущена — считаем что должна работать
                    // (пользователь включил автозапуск). Исключение — SafeMode.
                    await HandleCrashAsync(status.ServiceStrategy.Length > 0 ? status.ServiceStrategy : status.StrategyName, isService: true);
                    return;
                }

                // 2) Standalone процесс упал по PID
                if (status.State == BypassState.RunningStandalone)
                {
                    if (status.Pid is int pid && pid > 0)
                    {
                        var dead = false;
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            if (proc.HasExited) dead = true;
                        }
                        catch (ArgumentException)
                        {
                            dead = true;
                        }

                        if (dead)
                        {
                            await HandleCrashAsync(status.StrategyName, isService: false);
                        }
                    }
                    else
                    {
                        // PID не определён, но состояние RunningStandalone без процесса — тоже падение
                        // Проверяем наличие winws процесса напрямую
                        if (!Shell.IsProcessRunning("winws"))
                            await HandleCrashAsync(status.StrategyName, isService: false);
                    }
                }
                else if (status.State == BypassState.RunningService)
                {
                    if (status.ServiceState != ServiceState.Running)
                    {
                        await HandleCrashAsync(status.ServiceStrategy, isService: true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[Watchdog] Ошибка проверки: {ex.Message}");
            }
        }

        private async Task HandleCrashAsync(string strategyName, bool isService)
        {
            if (_isRecovering || !_settings.WatchdogAutoRestart) return;
            if (_settings.BypassManuallyStopped) return;

            _isRecovering = true;
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastCrashTime).TotalSeconds < 60)
                {
                    _recentCrashCount++;
                }
                else
                {
                    _recentCrashCount = 1;
                }
                _lastCrashTime = now;

                // 1.17.24: после 3 быстрых падений не сдаёмся, а пробуем альтернативную стратегию
                if (_recentCrashCount > 3)
                {
                    AppLog.Warn($"[Watchdog] Превышен лимит перезапусков ({_recentCrashCount} за минуту) — пробую альтернативную стратегию бесшовно");
                    var fallbackOk = await TryFallbackStrategyAsync(strategyName, isService);
                    if (fallbackOk)
                    {
                        _recentCrashCount = 0;
                        return;
                    }
                    var alert = $"[Watchdog] Превышен лимит перезапусков ({_recentCrashCount} за минуту). Автоперезапуск приостановлен — попробуйте другую стратегию в Центре обхода.";
                    AppLog.Warn(alert);
                    LastEventText = alert;
                    AlertRaised?.Invoke(alert);
                    return;
                }

                var strat = _strategyResolver();
                if (strat == null) return;

                AppLog.Warn($"[Watchdog] Обнаружено аварийное завершение обхода. Выполняю автоматическое восстановление («{strat.Name}»)...");
                LastRecoveryTime = DateTime.Now;

                if (isService)
                {
                    // Обеспечиваем BFE — без него WinDivert не стартует и служба сразу падает
                    await EnsureBfeRunningAsync();

                    var selected = _strategyResolver();
                    // Если выбранная стратегия отличается от упавшей — переустанавливаем службу бесшовно
                    if (selected != null && !string.Equals(selected.Name, strategyName, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Info($"[Watchdog] Стратегия изменена ({strategyName} → {selected.Name}), переустанавливаю службу");
                        var mode = EngineService.GetGameFilterMode(_settings.EnginePath);
                        var res = await _bypass.InstallServiceAsync(selected, mode);
                        if (!res.Ok)
                        {
                            AppLog.Warn($"[Watchdog] Переустановка службы не удалась: {res.Message}, пробую обычный старт");
                            var started = await TryStartServiceWithWaitAsync();
                            if (!started)
                                await TryFallbackStrategyAsync(strategyName, isService);
                        }
                        else
                        {
                            // Проверяем что служба действительно Running
                            var running = await Shell.WaitForAsync(() => WinServices.Query(WinServices.ZapretService) == ServiceState.Running, 8000);
                            if (!running)
                            {
                                AppLog.Warn("[Watchdog] Служба не перешла в Running после переустановки — пробую альтернативную стратегию");
                                await TryFallbackStrategyAsync(strategyName, isService);
                                return;
                            }
                        }
                    }
                    else
                    {
                        var started = await TryStartServiceWithWaitAsync();
                        if (!started)
                        {
                            AppLog.Warn("[Watchdog] Обычный старт службы не помог — пробую переустановку той же стратегии");
                            var mode = EngineService.GetGameFilterMode(_settings.EnginePath);
                            var res = await _bypass.InstallServiceAsync(selected ?? strat, mode);
                            if (!res.Ok)
                            {
                                AppLog.Warn($"[Watchdog] Переустановка не удалась: {res.Message} — пробую альтернативную стратегию");
                                await TryFallbackStrategyAsync(strategyName, isService);
                                return;
                            }
                            var running = await Shell.WaitForAsync(() => WinServices.Query(WinServices.ZapretService) == ServiceState.Running, 8000);
                            if (!running)
                                await TryFallbackStrategyAsync(strategyName, isService);
                        }
                    }
                }
                else
                {
                    var res = await _bypass.SwitchToStrategyAsync(strat, EngineService.GetGameFilterMode(_settings.EnginePath), _settings.ShowWinwsConsole);
                    if (!res.Ok)
                    {
                        AppLog.Warn($"[Watchdog] Switch не удался: {res.Message} — пробую альтернативную стратегию");
                        await TryFallbackStrategyAsync(strategyName, isService: false);
                        return;
                    }
                }

                var msg = $"[Watchdog] Обход «{strat.Name}» успешно восстановлен.";
                AppLog.Info(msg);
                LastEventText = msg;
                EventLogged?.Invoke(msg);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[Watchdog] Ошибка автовосстановления: {ex.Message}");
            }
            finally
            {
                _isRecovering = false;
            }
        }

        private async Task<bool> TryStartServiceWithWaitAsync()
        {
            try
            {
                var r = WinServices.Start(WinServices.ZapretService);
                AppLog.Info($"[Watchdog] sc start zapret: ok={r.Ok} {r.All.Trim()}");
                var running = await Shell.WaitForAsync(() => WinServices.Query(WinServices.ZapretService) == ServiceState.Running, 15000);
                return running;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Watchdog] Ошибка старта службы: {ex.Message}");
                return false;
            }
        }

        private async Task EnsureBfeRunningAsync()
        {
            try
            {
                var bfe = WinServices.Query("BFE");
                if (bfe != ServiceState.Running && bfe != ServiceState.NotInstalled)
                {
                    AppLog.Info($"[Watchdog] BFE в состоянии {bfe} — пытаюсь запустить");
                    WinServices.Start("BFE");
                    await Shell.WaitForAsync(() => WinServices.Query("BFE") == ServiceState.Running, 10000);
                }
            }
            catch { }
        }

        private async Task<bool> TryFallbackStrategyAsync(string failedName, bool isService)
        {
            try
            {
                if (_allStrategiesResolver == null) return false;
                var all = _allStrategiesResolver();
                if (all == null || all.Count == 0) return false;
                var candidates = all
                    .Where(s => !s.Name.Equals(failedName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.IsRecommended)
                    .ThenByDescending(s => s.TestResult?.PassedCount ?? -1)
                    .ThenBy(s => s.Name)
                    .Take(5)
                    .ToList();
                if (candidates.Count == 0) return false;
                AppLog.Info($"[Watchdog] Пробую {candidates.Count} альтернативных стратегий после падения «{failedName}»");
                foreach (var cand in candidates)
                {
                    AppLog.Info($"[Watchdog] Пробую альтернативу «{cand.Name}» бесшовно");
                    var mode = EngineService.GetGameFilterMode(_settings.EnginePath);
                    OperationResult res;
                    if (isService)
                        res = await _bypass.InstallServiceAsync(cand, mode);
                    else
                        res = await _bypass.SwitchToStrategyAsync(cand, mode, _settings.ShowWinwsConsole);
                    if (res.Ok)
                    {
                        var running = isService
                            ? await Shell.WaitForAsync(() => WinServices.Query(WinServices.ZapretService) == ServiceState.Running, 12000)
                            : _bypass.GetStatus().IsRunning;
                        if (running)
                        {
                            _settings.SelectedStrategy = cand.Name;
                            SettingsStore.Save(_settings);
                            var msg = $"[Watchdog] ✅ Автоматически переключил на «{cand.Name}» после сбоя «{failedName}»";
                            AppLog.Info(msg);
                            LastEventText = msg;
                            EventLogged?.Invoke(msg);
                            LastRecoveryTime = DateTime.Now;
                            _recentCrashCount = 0;
                            return true;
                        }
                        else
                        {
                            AppLog.Warn($"[Watchdog] «{cand.Name}» не удержала службу — пробую следующую");
                        }
                    }
                    else
                    {
                        AppLog.Warn($"[Watchdog] «{cand.Name}» не удалось: {res.Message}");
                    }
                    await Task.Delay(800);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Watchdog] Ошибка fallback: {ex.Message}");
            }
            return false;
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
