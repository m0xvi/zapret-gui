using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>
    /// Сторожевой таймер (Watchdog): отслеживает активность winws.exe и системной службы zapret,
    /// предотвращая зависания и выполняя мягкий перезапуск при аварийном падении.
    /// </summary>
    public sealed class WatchdogService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly BypassController _bypass;
        private readonly Func<StrategyInfo?> _strategyResolver;
        private readonly Timer _timer;
        private int _recentCrashCount;
        private DateTime _lastCrashTime = DateTime.MinValue;
        private bool _isRecovering;

        public event Action<string>? EventLogged;
        public event Action<string>? AlertRaised;

        public bool IsRunning { get; private set; }
        public string LastEventText { get; private set; } = "Сторожевой таймер активен";
        public DateTime? LastRecoveryTime { get; private set; }

        public WatchdogService(AppSettings settings, BypassController bypass, Func<StrategyInfo?> strategyResolver)
        {
            _settings = settings;
            _bypass = bypass;
            _strategyResolver = strategyResolver;
            _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (!_settings.WatchdogEnabled || _settings.SafeMode) return;
            IsRunning = true;
            var interval = Math.Clamp(_settings.WatchdogIntervalSeconds, 5, 120);
            _timer.Change(TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
        }

        public void Stop()
        {
            IsRunning = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private async void OnTimerTick(object? state)
        {
            if (!IsRunning || _isRecovering || !_settings.WatchdogEnabled || _settings.SafeMode) return;

            try
            {
                var status = _bypass.GetStatus();

                // Контролируем процесс только если обход был запущен
                if (status.State == BypassState.RunningStandalone)
                {
                    if (status.ProcessId > 0)
                    {
                        var dead = false;
                        try
                        {
                            var proc = Process.GetProcessById(status.ProcessId);
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
                }
                else if (status.State == BypassState.RunningService)
                {
                    var serviceState = WinServices.GetState(WinServices.ZapretService);
                    if (serviceState != ServiceState.Running)
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

                if (_recentCrashCount > 3)
                {
                    var alert = $"[Watchdog] Превышен лимит перезапусков ({_recentCrashCount} за минуту). Автоперезапуск приостановлен.";
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
                    WinServices.Start(WinServices.ZapretService);
                }
                else
                {
                    await _bypass.StartAsync(strat, EngineService.GetGameFilterMode(_settings.EnginePath), _settings.ShowWinwsConsole);
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

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
