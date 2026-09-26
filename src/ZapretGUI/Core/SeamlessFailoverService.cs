using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using ZapretGui.ViewModels;

namespace ZapretGui.Core
{
    /// <summary>
    /// Бесшовное автопереключение стратегий без вмешательства пользователя.
    /// Фоново проверяет ключевые ресурсы (YouTube/Discord/GitHub) с текущим обходом,
    /// детектирует когда стратегия мешает (прямое OK, через обход FAIL) и тихо
    /// переключает на первую рабочую из каталога через SwitchToStrategyAsync / InstallServiceAsync.
    /// Никаких MessageBox, только лог и мягкое уведомление в трей.
    /// </summary>
    public sealed class SeamlessFailoverService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly Func<BypassController> _bypassFactory;
        private readonly Func<StrategyStore> _storeFactory;
        private readonly System.Timers.Timer _timer;
        private bool _isChecking;
        private int _consecutiveFailures;
        private string _lastFailedId = "";
        private DateTime _lastSwitchUtc = DateTime.MinValue;
        private const int Threshold = 2;
        private const int DefaultCooldownMinutes = 10;

        public event Action<string>? StatusChanged;
        public event Action<string>? FailoverSucceeded;
        public event Action<string>? FailoverFailed;

        public string LastReason { get; private set; } = "Ожидание проверки…";
        public DateTime? LastCheckTime { get; private set; }
        public DateTime? LastSwitchTime { get; private set; }
        public bool IsRunning { get; private set; }

        public SeamlessFailoverService(AppSettings settings, Func<BypassController> bypassFactory, Func<StrategyStore> storeFactory)
        {
            _settings = settings;
            _bypassFactory = bypassFactory;
            _storeFactory = storeFactory;
            _timer = new System.Timers.Timer(TimeSpan.FromMinutes(GetInterval()).TotalMilliseconds);
            _timer.AutoReset = true;
            _timer.Elapsed += async (_, _) => await CheckAndFailoverAsync(silent: true);
        }

        private int GetInterval() => Math.Clamp(_settings.SeamlessCheckMinutes > 0 ? _settings.SeamlessCheckMinutes : 5, 2, 60);
        private int GetCooldown() => Math.Clamp(_settings.SeamlessCooldownMinutes > 0 ? _settings.SeamlessCooldownMinutes : DefaultCooldownMinutes, 5, 120);
        private bool IsEnabled => !_settings.SafeMode && _settings.SeamlessFailoverEnabled;

        public void Start()
        {
            if (!IsEnabled) return;
            IsRunning = true;
            _timer.Interval = TimeSpan.FromMinutes(GetInterval()).TotalMilliseconds;
            _timer.Start();
            LastReason = $"Бесшовное переключение включено, проверка каждые {GetInterval()} мин";
            StatusChanged?.Invoke(LastReason);
            AppLog.Info($"[SeamlessFailover] Запущен, интервал {GetInterval()} мин, cooldown {GetCooldown()} мин");
        }

        public void Stop()
        {
            IsRunning = false;
            _timer.Stop();
            LastReason = "Бесшовное переключение выключено";
            StatusChanged?.Invoke(LastReason);
        }

        public void Restart()
        {
            Stop();
            if (IsEnabled) Start();
        }

        public void UpdateInterval()
        {
            if (IsRunning)
                _timer.Interval = TimeSpan.FromMinutes(GetInterval()).TotalMilliseconds;
        }

        /// <summary>Ручной запуск проверки (например по кнопке в настройках) — вне таймера.</summary>
        public Task CheckNowAsync() => CheckAndFailoverAsync(silent: false);

        private async Task CheckAndFailoverAsync(bool silent)
        {
            if (_isChecking) return;
            if (!IsEnabled) return;
            var bypass = _bypassFactory();
            var status = bypass.GetStatus();
            if (!status.IsRunning)
            {
                if (!silent) LastReason = "Обход выключен — проверка не нужна";
                return;
            }

            _isChecking = true;
            try
            {
                // Собираем цели: встроенные критичные + пользовательские включённые
                MonitorTargetStore.EnsureDefaults(_settings);
                var targets = _settings.MonitorTargets.Where(t => t.Enabled).ToList();
                if (targets.Count == 0)
                {
                    targets = new System.Collections.Generic.List<MonitorTarget>
                    {
                        MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204"),
                        MonitorTarget.CreateBuiltIn("Discord", "https://discord.com/api/v9/gateway"),
                        MonitorTarget.CreateBuiltIn("GitHub", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt")
                    };
                }
                // Ограничиваем 5 критичными для скорости
                targets = targets.Take(5).ToList();

                LastCheckTime = DateTime.Now;
                if (!silent) AppLog.Info("[SeamlessFailover] Ручная проверка критичных ресурсов…");

                // Лёгкая проверка С обходом (не останавливаем winws)
                var probes = new System.Collections.Generic.List<ResourceProbeResult>();
                foreach (var t in targets)
                {
                    try
                    {
                        var p = await ResourceProbe.CheckAsync(t).ConfigureAwait(false);
                        probes.Add(p);
                        // Обновляем UI-статус мягко (без Dispatcher — свойства INotifyPropertyChanged потокобезопасны для чтения, но запись лучше через Dispatcher)
                        try
                        {
                            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                t.LastStatusText = p.StatusText;
                                t.LastStatusKey = p.StatusKey;
                                t.LastDetails = p.Details;
                            });
                        }
                        catch { t.LastStatusText = p.StatusText; t.LastStatusKey = p.StatusKey; }
                    }
                    catch (Exception ex)
                    {
                        probes.Add(new ResourceProbeResult { Target = t, Details = ex.Message, Kind = ResourceResultKind.Unknown });
                    }
                }

                var failed = probes.FirstOrDefault(r => !r.Ok);
                if (failed == null)
                {
                    _consecutiveFailures = 0;
                    _lastFailedId = "";
                    LastReason = $"Все ресурсы доступны · {DateTime.Now:HH:mm:ss}";
                    StatusChanged?.Invoke(LastReason);
                    _settings.SeamlessLastReason = LastReason;
                    SettingsStore.Save(_settings);
                    return;
                }

                // Отслеживаем последовательные сбои одного ресурса
                if (_lastFailedId != failed.Target.Id)
                {
                    _lastFailedId = failed.Target.Id;
                    _consecutiveFailures = 1;
                }
                else _consecutiveFailures++;

                if (_consecutiveFailures < Threshold)
                {
                    LastReason = $"Сбой «{failed.Target.Name}» {_consecutiveFailures}/{Threshold} · жду подтверждения ({failed.Details})";
                    StatusChanged?.Invoke(LastReason);
                    AppLog.Debug($"[SeamlessFailover] {LastReason}");
                    return;
                }

                var cooldown = GetCooldown();
                if ((DateTime.UtcNow - _lastSwitchUtc).TotalMinutes < cooldown)
                {
                    var left = cooldown - (DateTime.UtcNow - _lastSwitchUtc).TotalMinutes;
                    LastReason = $"Сбой «{failed.Target.Name}», но cooldown {cooldown} мин (осталось {left:0} мин)";
                    StatusChanged?.Invoke(LastReason);
                    AppLog.Debug($"[SeamlessFailover] {LastReason}");
                    return;
                }

                // Детальная диагностика: прямое vs через обход (кратко останавливает обход на 2-3 сек)
                LastReason = $"Диагностирую «{failed.Target.Name}» (прямо vs обход)…";
                StatusChanged?.Invoke(LastReason);
                AppLog.Info($"[SeamlessFailover] {LastReason}");

                var diagnosis = await bypass.DiagnoseResourceAsync(failed.Target).ConfigureAwait(false);
                // После диагностики обход уже восстановлен (BypassController делает restore в finally)

                if (diagnosis.Kind != ResourceDiagnosisKind.StrategyBreaks)
                {
                    // Не вина стратегии — сбрасываем счётчик, чтобы не зациклиться
                    LastReason = diagnosis.Summary.Length > 120 ? diagnosis.Summary.Substring(0, 120) + "…" : diagnosis.Summary;
                    StatusChanged?.Invoke(LastReason);
                    _settings.SeamlessLastReason = LastReason;
                    SettingsStore.Save(_settings);
                    AppLog.Info($"[SeamlessFailover] {failed.Target.Name}: {diagnosis.Kind} — {diagnosis.Summary}");
                    // Для внешней проблемы сбрасываем, для Available тоже
                    if (diagnosis.Kind == ResourceDiagnosisKind.Available || diagnosis.Kind == ResourceDiagnosisKind.ProviderOrServerIssue)
                        _consecutiveFailures = 0;
                    return;
                }

                // Стратегия действительно мешает — ищем рабочую замену бесшовно
                LastReason = $"Стратегия мешает «{failed.Target.Name}», подбираю замену…";
                StatusChanged?.Invoke(LastReason);
                AppLog.Warn($"[SeamlessFailover] {diagnosis.Target.Name}: стратегия мешает (прямо OK, обход FAIL), ищу замену для {status.StrategyName}");

                var store = _storeFactory();
                var curName = status.StrategyName.Length > 0 ? status.StrategyName : _settings.SelectedStrategy;
                List<StrategyInfo> snapshot;
                try
                {
                    var disp = System.Windows.Application.Current?.Dispatcher;
                    if (disp != null && !disp.CheckAccess())
                        snapshot = disp.Invoke(() => store.Items.ToList());
                    else
                        snapshot = store.Items.ToList();
                }
                catch { snapshot = store.Items.ToList(); }
                var candidates = snapshot
                    .Where(s => !s.Name.Equals(curName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.IsRecommended)
                    .ThenByDescending(s => s.TestResult?.PassedCount ?? -1)
                    .ThenBy(s => s.Name)
                    .Take(8)
                    .ToList();

                if (candidates.Count == 0)
                {
                    LastReason = "Нет альтернативных стратегий для переключения";
                    FailoverFailed?.Invoke(LastReason);
                    return;
                }

                // Для игр — отдельные кандидаты с проверкой пинга
                if (failed.Target.IsGame)
                {
                    var gameBest = await FindBestForGameAsync(bypass, candidates, failed.Target, diagnosis.WithBypass).ConfigureAwait(false);
                    if (gameBest != null)
                    {
                        await DoSeamlessSwitchAsync(bypass, status, gameBest, failed.Target, gameBestProbe: null).ConfigureAwait(false);
                        return;
                    }
                }

                // Обычный путь: первый кандидат который чинит failing target
                StrategyInfo? best = null;
                ResourceProbeResult? bestProbe = null;
                foreach (var cand in candidates)
                {
                    try
                    {
                        var probe = await bypass.TestStrategyOnResourceAsync(cand, failed.Target).ConfigureAwait(false);
                        if (probe.Ok)
                        {
                            best = cand;
                            bestProbe = probe;
                            AppLog.Info($"[SeamlessFailover] Кандидат «{cand.Name}» починил «{failed.Target.Name}» за {probe.Milliseconds} мс");
                            break; // первый OK — сразу переключаем для бесшовности (минимальный downtime)
                        }
                        else AppLog.Debug($"[SeamlessFailover] Кандидат «{cand.Name}» не помог: {probe.Details}");
                    }
                    catch (Exception ex) { AppLog.Debug($"[SeamlessFailover] Ошибка теста «{cand.Name}»: {ex.Message}"); }
                }

                // Fallback: если ни один не починил одиночный target, пробуем полный TestAll (дороже, но надёжнее)
                if (best == null)
                {
                    AppLog.Info("[SeamlessFailover] Быстрый поиск не нашёл замену, пробую полный тест каталога…");
                    try
                    {
                        var batch = await TestAllQuickAsync(bypass, store, curName).ConfigureAwait(false);
                        var batchBest = batch?.Best;
                        if (batchBest != null && batchBest.IsSuitable)
                        {
                            best = batchBest.Strategy;
                            AppLog.Info($"[SeamlessFailover] Полный тест нашёл «{best.Name}» {batchBest.PassedCount}/{batchBest.Checks.Count}");
                        }
                    }
                    catch (Exception ex) { AppLog.Warn($"[SeamlessFailover] Ошибка полного теста: {ex.Message}"); }
                }

                if (best == null || bestProbe == null)
                {
                    // если best из полного теста без bestProbe — создаём заглушку
                    if (best != null)
                    {
                        await DoSeamlessSwitchAsync(bypass, status, best, failed.Target, null).ConfigureAwait(false);
                        return;
                    }
                    LastReason = $"Не нашёл рабочую замену для «{failed.Target.Name}» — внешняя проблема или все стратегии биты";
                    FailoverFailed?.Invoke(LastReason);
                    AppLog.Warn($"[SeamlessFailover] {LastReason}");
                    _settings.SeamlessLastReason = LastReason;
                    SettingsStore.Save(_settings);
                    return;
                }

                await DoSeamlessSwitchAsync(bypass, status, best, failed.Target, bestProbe).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastReason = "Ошибка бесшовного переключения: " + ex.Message;
                AppLog.Error($"[SeamlessFailover] {LastReason}");
                StatusChanged?.Invoke(LastReason);
            }
            finally { _isChecking = false; }
        }

        private async Task<StrategyInfo?> FindBestForGameAsync(BypassController bypass, System.Collections.Generic.List<StrategyInfo> candidates, MonitorTarget target, ResourceProbeResult currentWithBypass)
        {
            var currentMs = currentWithBypass.Ok ? currentWithBypass.Milliseconds : long.MaxValue;
            StrategyInfo? best = null;
            ResourceProbeResult? bestProbe = null;
            foreach (var cand in candidates)
            {
                var probe = await bypass.TestStrategyOnResourceAsync(cand, target).ConfigureAwait(false);
                if (!probe.Ok) continue;
                if (best == null || probe.Milliseconds + 20 < currentMs && probe.Milliseconds < (bestProbe?.Milliseconds ?? long.MaxValue))
                {
                    best = cand; bestProbe = probe;
                }
            }
            if (best != null && bestProbe != null && (currentMs == long.MaxValue || bestProbe.Milliseconds + 20 < currentMs))
                return best;
            return null;
        }

        private async Task<StrategyTestBatchResult?> TestAllQuickAsync(BypassController bypass, StrategyStore store, string curName)
        {
            List<StrategyInfo> snap;
            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp != null && !disp.CheckAccess())
                    snap = disp.Invoke(() => store.Items.ToList());
                else
                    snap = store.Items.ToList();
            }
            catch { snap = store.Items.ToList(); }
            var cands = snap.Where(s => !s.Name.Equals(curName, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            var results = new System.Collections.Generic.List<StrategyTestResult>();
            foreach (var cand in cands)
            {
                var res = await bypass.TestStrategyAsync(cand).ConfigureAwait(false);
                results.Add(res);
            }
            return new StrategyTestBatchResult { Results = results };
        }

        private async Task DoSeamlessSwitchAsync(BypassController bypass, BypassStatus before, StrategyInfo next, MonitorTarget failedTarget, ResourceProbeResult? gameBestProbe)
        {
            var mode = EngineService.GetGameFilterMode(_settings.EnginePath);
            OperationResult res;
            // Сохраняем предыдущую для отката
            var prev = _settings.SelectedStrategy;
            if (before.ServiceState == ServiceState.Running || before.ServiceState == ServiceState.StartPending || before.ServiceState == ServiceState.StopPending)
            {
                AppLog.Info($"[SeamlessFailover] Бесшовно переустанавливаю службу: {before.ServiceStrategy} → {next.Name}");
                res = await bypass.InstallServiceAsync(next, mode).ConfigureAwait(false);
            }
            else
            {
                AppLog.Info($"[SeamlessFailover] Бесшовно переключаю: {before.StrategyName} → {next.Name}");
                res = await bypass.SwitchToStrategyAsync(next, mode, _settings.ShowWinwsConsole).ConfigureAwait(false);
            }

            _lastSwitchUtc = DateTime.UtcNow;
            LastSwitchTime = DateTime.Now;
            _settings.SeamlessLastSwitchTime = _lastSwitchUtc;
            _consecutiveFailures = 0;
            _lastFailedId = "";

            if (res.Ok)
            {
                _settings.PreviousSelectedStrategy = prev;
                _settings.SeamlessLastReason = $"Авто {DateTime.Now:HH:mm} «{next.Name}» для «{failedTarget.Name}»";
                SettingsStore.Save(_settings);
                LastReason = $"✅ Переключил на «{next.Name}» — «{failedTarget.Name}» восстановлен бесшовно";
                StatusChanged?.Invoke(LastReason);
                FailoverSucceeded?.Invoke(LastReason);
                AppLog.Info($"[SeamlessFailover] Успех: {LastReason}");
            }
            else
            {
                LastReason = $"Не удалось переключить на «{next.Name}»: {res.Message}";
                _settings.SeamlessLastReason = LastReason;
                SettingsStore.Save(_settings);
                StatusChanged?.Invoke(LastReason);
                FailoverFailed?.Invoke(LastReason);
                AppLog.Warn($"[SeamlessFailover] {LastReason}");
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}
