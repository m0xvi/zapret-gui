using System;
using System.Threading.Tasks;
using System.Timers;

namespace ZapretGui.Core
{
    /// <summary>
    /// Расписание обхода: авто-старт/стоп по времени и дням недели.
    /// Проверяет каждую минуту, включает защиту от повторного срабатывания в ту же минуту.
    /// </summary>
    public sealed class BypassScheduleService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly Func<BypassController> _bypassFactory;
        private readonly Func<StrategyInfo?> _strategyFactory;
        private readonly Timer _timer;
        private DateTime _lastStartTrigger = DateTime.MinValue;
        private DateTime _lastStopTrigger = DateTime.MinValue;

        public event Action<string>? StatusChanged;

        public BypassScheduleService(AppSettings settings, Func<BypassController> bypassFactory, Func<StrategyInfo?> strategyFactory)
        {
            _settings = settings;
            _bypassFactory = bypassFactory;
            _strategyFactory = strategyFactory;
            _timer = new Timer(TimeSpan.FromSeconds(30).TotalMilliseconds);
            _timer.Elapsed += async (_, _) => await TickAsync();
            _timer.AutoReset = true;
        }

        public void Start()
        {
            if (!_settings.ScheduleEnabled) return;
            _timer.Start();
            AppLog.Info("[Schedule] Расписание запущено: " + Describe());
        }

        public void Stop() => _timer.Stop();

        public void Restart()
        {
            Stop();
            if (_settings.ScheduleEnabled) Start();
        }

        public string Describe()
        {
            if (!_settings.ScheduleEnabled) return "выключено";
            var days = DaysMaskToText(_settings.ScheduleDaysMask);
            return $"{_settings.ScheduleStartTime} → {_settings.ScheduleStopTime} · {days}";
        }

        private async Task TickAsync()
        {
            if (!_settings.ScheduleEnabled) return;
            if (!TimeSpan.TryParse(_settings.ScheduleStartTime, out var start) ||
                !TimeSpan.TryParse(_settings.ScheduleStopTime, out var stop)) return;

            var now = DateTime.Now;
            var todayBit = 1 << ((int)now.DayOfWeek == 0 ? 6 : (int)now.DayOfWeek - 1); // Пн=0 … Вс=6
            if ((_settings.ScheduleDaysMask & todayBit) == 0) return;

            var nowSpan = now.TimeOfDay;
            var bypass = _bypassFactory();
            var status = bypass.GetStatus();

            // Старт: если сейчас в пределах 60 сек после старта и не запускали сегодня
            if (Math.Abs((nowSpan - start).TotalSeconds) < 60 && _lastStartTrigger.Date != now.Date)
            {
                if (!status.IsRunning)
                {
                    _lastStartTrigger = now;
                    AppLog.Info($"[Schedule] Авто-старт обхода по расписанию {start:hh\\:mm}");
                    StatusChanged?.Invoke($"Расписание: старт {start:hh\\:mm}");
                    var strat = _strategyFactory();
                    if (strat != null)
                    {
                        var res = _settings.ScheduleUseService
                            ? await bypass.InstallServiceAsync(strat, EngineService.GetGameFilterMode(_settings.EnginePath))
                            : await bypass.StartAsync(strat, EngineService.GetGameFilterMode(_settings.EnginePath), _settings.ShowWinwsConsole);
                        StatusChanged?.Invoke(res.Ok ? $"Расписание: обход запущен ({strat.Name})" : $"Расписание: ошибка старта — {res.Message}");
                    }
                }
            }

            // Стоп
            if (Math.Abs((nowSpan - stop).TotalSeconds) < 60 && _lastStopTrigger.Date != now.Date)
            {
                if (status.IsRunning)
                {
                    _lastStopTrigger = now;
                    AppLog.Info($"[Schedule] Авто-стоп обхода по расписанию {stop:hh\\:mm}");
                    StatusChanged?.Invoke($"Расписание: стоп {stop:hh\\:mm}");
                    var res = await bypass.StopAsync();
                    // Если служба — удалить службу тоже
                    if (status.ServiceState == ServiceState.Running)
                    {
                        await bypass.RemoveServiceAsync();
                    }
                    StatusChanged?.Invoke(res.Ok ? "Расписание: обход остановлен" : $"Расписание: ошибка стопа — {res.Message}");
                }
            }
        }

        public static string DaysMaskToText(int mask)
        {
            if (mask == 127) return "ежедневно";
            if (mask == 31) return "будни";
            if (mask == 96) return "выходные";
            var names = new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
            var list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 7; i++) if ((mask & (1 << i)) != 0) list.Add(names[i]);
            return list.Count == 0 ? "дни не выбраны" : string.Join(", ", list);
        }

        public void Dispose() => _timer.Dispose();
    }
}
