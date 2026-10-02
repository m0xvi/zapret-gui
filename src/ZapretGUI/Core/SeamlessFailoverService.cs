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
        /// <summary>Сколько проверок подряд узел должен быть полностью недоступен, прежде чем
        /// приложение вообще задумается о смене стратегии (v1.28.1: было 2 — слишком дёргано).</summary>
        private const int Threshold = 3;
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
        /// <summary>Разовая проверка. `forceSwitch` ставится только кнопкой «Подобрать замену сейчас» —
        /// это осознанное действие пользователя, оно не подчиняется главному выключателю автосмены.</summary>
        public Task CheckNowAsync(bool forceSwitch = false) => CheckAndFailoverAsync(silent: false, forceSwitch);

        private async Task CheckAndFailoverAsync(bool silent, bool forceSwitch = false)
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
                        // Подтверждающая перепроверка: одиночный тайм-аут — не повод считать узел упавшим
                        var p = await ResourceProbe.CheckConfirmedAsync(t).ConfigureAwait(false);
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

                // Полная недоступность: DNS/TCP/TLS не отвечают вовсе. Если сервер ответил (HTTP 5xx)
                // или узел просто медленный — это не повод менять стратегию (правило v1.28.1).
                if (!ResourceProbe.IsCompleteOutage(failed))
                {
                    _consecutiveFailures = 0;
                    _lastFailedId = "";
                    LastReason = $"«{failed.Target.Name}»: частичная деградация ({failed.Details}) — переключение не нужно";
                    StatusChanged?.Invoke(LastReason);
                    _settings.SeamlessLastReason = LastReason;
                    SettingsStore.Save(_settings);
                    AppLog.Info($"[SeamlessFailover] {LastReason}");
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

                // Главный выключатель автосмены (v1.28.3): сообщаем о недоступности, но стратегию не меняем.
                if (!_settings.AutoSwitchStrategyEnabled && !forceSwitch)
                {
                    LastReason = $"«{failed.Target.Name}» недоступен совсем, но автопереключение выключено — стратегию не меняю. " +
                                 "Включить можно в «Автоматизация → Восстановление» или подобрать замену вручную.";
                    StatusChanged?.Invoke(LastReason);
                    _settings.SeamlessLastReason = LastReason;
                    SettingsStore.Save(_settings);
                    AppLog.Info($"[SeamlessFailover] {LastReason}");
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
                    .Take(4)   // v1.28.1: кандидат проверяется по всем узлам набора, 4 попытки — предел по времени
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

                // Обычный путь: берём кандидата, который чинит сбойный узел И удерживает остальные узлы
                // набора (v1.28.1). Раньше проверялся только сбойный узел — вылечив один сайт, стратегия
                // могла сломать другой, и приложение переключалось по кругу.
                var fullSet = new System.Collections.Generic.List<MonitorTarget> { failed.Target };
                fullSet.AddRange(targets.Where(t => t.Id != failed.Target.Id));
                StrategyInfo? best = null;
                ResourceProbeResult? bestProbe = null;
                foreach (var cand in candidates)
                {
                    try
                    {
                        var probesOnAll = await bypass.TestStrategyOnTargetsAsync(cand, fullSet).ConfigureAwait(false);
                        var onFailed = probesOnAll.FirstOrDefault(p => p.Target.Id == failed.Target.Id);
                        var broken = probesOnAll.Where(p => p.Target.Id != failed.Target.Id && !p.Ok).ToList();
                        if (onFailed is { Ok: true } && broken.Count == 0)
                        {
                            best = cand;
                            bestProbe = onFailed;
                            AppLog.Info($"[SeamlessFailover] Кандидат «{cand.Name}» чинит «{failed.Target.Name}» и держит остальные узлы ({onFailed.Milliseconds} мс)");
                            break; // первый подходящий — сразу переключаем для бесшовности (минимальный downtime)
                        }
                        AppLog.Info($"[SeamlessFailover] Кандидат «{cand.Name}» отклонён: " +
                            (onFailed is { Ok: true }
                                ? $"ломает «{broken[0].Target.Name}»"
                                : $"не чинит «{failed.Target.Name}» ({onFailed?.Details})"));
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
                        var (switched, prevName) = await DoSeamlessSwitchAsync(bypass, status, best, failed.Target, null).ConfigureAwait(false);
                        if (switched)
                            await VerifyAfterSwitchAsync(bypass, targets, probes, prevName, failed.Target).ConfigureAwait(false);
                        return;
                    }
                    LastReason = $"Не нашёл рабочую замену для «{failed.Target.Name}» — внешняя проблема или все стратегии биты";
                    FailoverFailed?.Invoke(LastReason);
                    AppLog.Warn($"[SeamlessFailover] {LastReason}");
                    _settings.SeamlessLastReason = LastReason;
                    SettingsStore.Save(_settings);
                    return;
                }

                var (switchOk, previousName) = await DoSeamlessSwitchAsync(bypass, status, best, failed.Target, bestProbe).ConfigureAwait(false);
                if (switchOk)
                    await VerifyAfterSwitchAsync(bypass, targets, probes, previousName, failed.Target).ConfigureAwait(false);
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

        /// <summary>Постпроверка после автозамены (v1.28.1): сбойный узел обязан открыться, а остальные
        /// узлы набора — не стать хуже. Если стало хуже, возвращаем прежнюю стратегию: «починка» одного
        /// сайта ценой поломки другого запрещена, иначе приложение переключалось бы по кругу.</summary>
        private async Task VerifyAfterSwitchAsync(BypassController bypass, System.Collections.Generic.List<MonitorTarget> targets,
            System.Collections.Generic.List<ResourceProbeResult> before, string previousName, MonitorTarget failedTarget)
        {
            var okBefore = before.Count(r => r.Ok);
            var after = new System.Collections.Generic.List<ResourceProbeResult>();
            foreach (var t in targets)
            {
                try { after.Add(await ResourceProbe.CheckConfirmedAsync(t).ConfigureAwait(false)); }
                catch (Exception ex) { after.Add(new ResourceProbeResult { Target = t, Kind = ResourceResultKind.Unknown, Details = ex.Message }); }
            }

            var okAfter = after.Count(r => r.Ok);
            var failedNowOk = after.FirstOrDefault(r => r.Target.Id == failedTarget.Id)?.Ok == true;
            if (failedNowOk && okAfter >= okBefore)
            {
                _settings.SeamlessLastReason = $"Авто {DateTime.Now:HH:mm} — «{failedTarget.Name}» открыт, узлов доступно {okAfter}/{targets.Count}";
                SettingsStore.Save(_settings);
                LastReason = $"Новая стратегия держит все узлы: {okAfter}/{targets.Count} доступны, «{failedTarget.Name}» открыт";
                StatusChanged?.Invoke(LastReason);
                AppLog.Info($"[SeamlessFailover] Постпроверка: {LastReason}");
                return;
            }

            // Откат: новая стратегия не лучше прежней
            AppLog.Warn($"[SeamlessFailover] Постпроверка: доступно {okAfter}/{targets.Count} (было {okBefore}/{targets.Count}), «{failedTarget.Name}» {(failedNowOk ? "открыт" : "всё ещё недоступен")} — откатываю на «{previousName}»");
            var prev = string.IsNullOrWhiteSpace(previousName) ? null : _storeFactory().Find(previousName);
            if (prev == null)
            {
                LastReason = "Новая стратегия не лучше прежней, но вернуть прежнюю не удалось — оставляю как есть";
                StatusChanged?.Invoke(LastReason);
                return;
            }

            var mode = EngineService.GetGameFilterMode(_settings.EnginePath);
            var status = bypass.GetStatus();
            var res = status.ServiceState is ServiceState.Running or ServiceState.StartPending or ServiceState.StopPending
                ? await bypass.InstallServiceAsync(prev, mode).ConfigureAwait(false)
                : await bypass.SwitchToStrategyAsync(prev, mode, _settings.ShowWinwsConsole).ConfigureAwait(false);
            // Cooldown обновляем в любом случае: без паузы был бы цикл «переключил → откатил → снова переключил».
            _lastSwitchUtc = DateTime.UtcNow;
            _settings.SeamlessLastSwitchTime = _lastSwitchUtc;
            _consecutiveFailures = 0;
            _lastFailedId = "";
            LastReason = res.Ok
                ? $"Вернул прежнюю стратегию «{prev.Name}»: замена ломала другие узлы ({okAfter}/{targets.Count} против {okBefore}/{targets.Count})"
                : $"Откат на «{prev.Name}» не удался: {res.Message}";
            _settings.SeamlessLastReason = LastReason;
            SettingsStore.Save(_settings);
            StatusChanged?.Invoke(LastReason);
            if (res.Ok) FailoverFailed?.Invoke(LastReason);
            AppLog.Warn($"[SeamlessFailover] {LastReason}");
        }

        private async Task<(bool Ok, string PreviousName)> DoSeamlessSwitchAsync(BypassController bypass, BypassStatus before, StrategyInfo next, MonitorTarget failedTarget, ResourceProbeResult? gameBestProbe)
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
                return (true, prev);
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
            return (false, prev);
        }

        public void Dispose() => _timer.Dispose();
    }
}
