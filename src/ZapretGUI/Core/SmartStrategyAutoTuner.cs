using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZapretGui.ViewModels;

namespace ZapretGui.Core
{
    public sealed class AutoTunerStepResult
    {
        public int StepNumber { get; init; }
        public int TotalSteps { get; init; }
        public string CandidateName { get; init; } = "";
        public string MutationDescription { get; init; } = "";
        public double SuccessRate { get; init; }
        public long AvgRttMs { get; init; }
        public int Score { get; init; }
        public bool IsWinnerSoFar { get; set; }
        public string Details { get; init; } = "";
        public List<string> Args { get; init; } = new();

        public string HypothesisTitle => $"Гипотеза #{StepNumber}: {MutationDescription}";
        public string StatusSeverity => SuccessRate >= 80 ? "Success" : SuccessRate >= 40 ? "Warning" : "Danger";
        public string StatusText => SuccessRate >= 80 ? $"{SuccessRate:0}% РАБОТАЕТ" : SuccessRate > 0 ? $"{SuccessRate:0}% ЧАСТИЧНО" : "БЛОКИРУЕТСЯ";
        public string DetailsText => Details;
        public string ArgumentsPreview => string.Join(" ", Args);
        public string ScoreText => $"Балл: {Score}/100";
        public string RttText => AvgRttMs > 0 && AvgRttMs < 900 ? $"RTT: ~{AvgRttMs} мс" : "RTT: —";
    }

    public sealed class AutoTunerProgress
    {
        public int CurrentStep { get; init; }
        public int TotalSteps { get; init; }
        public string StatusMessage { get; init; } = "";
        public int BestScore { get; init; }
        public string BestCandidateName { get; init; } = "";
    }

    /// <summary>
    /// Интеллектуальный многопроходный автоподбор и синтез наиболее эффективной стратегии под DPI провайдера.
    /// Выполняет пошаговое тестирование синтезированных мутаций, эмпирический скоринг и сохранение победителя.
    /// </summary>
    public static class SmartStrategyAutoTuner
    {
        public static readonly IReadOnlyList<(string Desync, string Split, string Sni, string Ttl, string Fooling, int Repeats, bool Multi, string Desc)> Hypotheses =
            new List<(string, string, string, string, string, int, bool, string)>
            {
                // 1. Семейство доказанных чемпионов для ТСПУ РФ (Fake + Split2 / Disorder2 + Timestamp Fooling ts + Repeats=11 + IP-ID zero)
                ("fake,split2", "1", "none", "auto", "ts", 11, false, "Гибрид Fake TLS + Split2 (pos=1) + fooling ts + repeats=11 (Формула ALT11/ALT12)"),
                ("fake,split2", "1", "none", "auto", "badsum,ts", 11, false, "Гибрид Fake TLS + Split2 (pos=1) + badsum,ts + repeats=11"),
                ("fake", "1", "none", "auto", "ts", 11, false, "Fake TLS + split-pos 1 + fooling ts + repeats=11 (Формула ALT13/EXP)"),
                ("fake", "none", "none", "auto", "ts", 6, false, "Fake TLS + fooling ts + repeats=6 (Формула general ALT)"),
                ("fake,split2", "sniext", "none", "auto", "ts", 11, false, "Гибрид Fake TLS + Split2 (sniext) + fooling ts + repeats=11"),
                ("fake,split2", "midsld", "none", "auto", "ts", 11, false, "Гибрид Fake TLS + Split2 (midsld) + fooling ts + repeats=11"),
                ("fake,disorder2", "1", "none", "auto", "ts", 11, false, "Гибрид Fake TLS + Disorder2 (pos=1) + fooling ts + repeats=11"),
                ("fake,disorder2", "midsld", "none", "auto", "ts", 11, false, "Гибрид Fake TLS + Disorder2 (midsld) + fooling ts + repeats=11"),

                // 2. Семейство многосегментных оверлеев с защитой таймстемпами (Multisplit + ts)
                ("multisplit", "1", "none", "auto", "ts", 0, true, "Многосегментный оверлей TCP (multisplit, pos=1, seqovl=1) + fooling ts"),
                ("multisplit", "midsld", "none", "auto", "ts", 0, true, "Многосегментный оверлей TCP (multisplit, midsld, seqovl=1) + fooling ts"),

                // 3. Семейство гибридов с подстановкой SNI (Google, Microsoft, Cloudflare)
                ("fake,split2", "1", "www.google.com", "auto", "ts", 11, false, "Гибрид Fake TLS + Split2 (pos=1) + Google SNI + fooling ts"),
                ("fake,split2", "1", "www.google.com", "auto", "badsum", 6, false, "Гибрид Fake TLS + Split2 (pos=1) + Google SNI + badsum"),
                ("fake,split2", "sniext", "www.google.com", "auto", "badsum", 6, false, "Гибрид Fake TLS + Split2 (sniext) + Google SNI + badsum"),
                ("fake,split2", "1", "www.microsoft.com", "auto", "badsum", 6, false, "Гибрид Fake TLS + Split2 (pos=1) + Microsoft SNI + badsum"),
                ("fake,split2", "sniext", "www.cloudflare.com", "auto", "badseq", 6, false, "Гибрид Fake TLS + Split2 (sniext) + Cloudflare SNI + badseq"),

                // 4. Семейство чистого разделения TLS ClientHello (Split2)
                ("split2", "1", "none", "auto", "badsum", 0, false, "Разделение 1-го байта ClientHello (split-pos=1) + badsum"),
                ("split2", "sniext", "none", "auto", "badsum", 0, false, "Разделение на границе SNI (sniext) + badsum"),
                ("split2", "midsld", "none", "auto", "badsum", 0, false, "Разделение середины домена SNI (midsld) + badsum"),

                // 5. Семейство изменения порядка пакетов (Disorder / Disorder2)
                ("disorder2", "1", "none", "auto", "badsum", 0, false, "Перестановка порядка пакетов (disorder2, pos=1) + badsum"),
                ("disorder2", "midsld", "none", "auto", "badsum", 0, false, "Перестановка порядка пакетов (disorder2, midsld) + badsum"),
                ("disorder2", "sniext", "none", "auto", "badseq", 0, false, "Перестановка порядка пакетов (disorder2, sniext) + badseq"),

                // 6. Семейство чистого Multisplit
                ("multisplit", "1", "none", "auto", "badsum", 0, true, "Многосегментный оверлей TCP (multisplit, pos=1, seqovl=1) + badsum"),
                ("multisplit", "2", "none", "auto", "badsum", 0, true, "Многосегментный оверлей TCP (multisplit, pos=2, seqovl=1) + badsum"),

                // 7. Семейство TTL & MD5 Evasion
                ("fake", "none", "www.google.com", "1", "badsum", 6, false, "Fake TLS с ультра-малым TTL (TTL=1) + badsum"),
                ("fake", "none", "www.google.com", "3", "badsum", 6, false, "Fake TLS с малым TTL (TTL=3) + badsum"),
                ("fake", "none", "www.google.com", "4", "md5sig", 6, false, "Fake TLS с TCP MD5 Signature fooling (md5sig) + TTL=4")
            };

        public static async Task<(bool Ok, string Message, SavedStrategyCandidate? Winner, List<AutoTunerStepResult> Results)> RunDeepAutoTuningAsync(
            string enginePath,
            BypassController bypass,
            StrategyStore strategyStore,
            IReadOnlyList<MonitorTarget> targets,
            ProviderContext? provider,
            IProgress<AutoTunerProgress>? progress,
            Action<AutoTunerStepResult>? onStep,
            CancellationToken ct)
        {
            var results = new List<AutoTunerStepResult>();
            var activeTargets = targets.Where(t => t.Enabled).ToList();
            if (activeTargets.Count == 0)
            {
                activeTargets = ConnectionTester.GetEffectiveTargets().Take(4).ToList();
            }

            var total = Hypotheses.Count;

            var before = bypass.GetStatus();
            var restoreService = before.State == BypassState.RunningService;
            var restoreStandalone = before.State == BypassState.RunningStandalone;
            var previousStrategyName = before.StrategyName;

            AppLog.Info($"[SmartAutoTuner] Запуск глубокого автоподбора стратегии ({total} гипотез, {activeTargets.Count} контрольных точек)...");

            int bestScore = -1;
            AutoTunerStepResult? bestResult = null;

            try
            {
                if (before.IsRunning)
                {
                    progress?.Report(new AutoTunerProgress
                    {
                        CurrentStep = 0,
                        TotalSteps = total,
                        StatusMessage = "Останавливаю текущий обход для изолированного тестирования…"
                    });
                    await bypass.StopAsync(ct).ConfigureAwait(false);
                    await Task.Delay(800, ct).ConfigureAwait(false);
                }

                for (int i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (desync, split, sni, ttl, fooling, repeats, multi, desc) = Hypotheses[i];
                    var stepNum = i + 1;
                    var candName = $"SmartHypothesis_{stepNum}_{desync.Replace(',', '_')}";

                    progress?.Report(new AutoTunerProgress
                    {
                        CurrentStep = stepNum,
                        TotalSteps = total,
                        StatusMessage = $"[Тест {stepNum}/{total}] {desc}…",
                        BestScore = Math.Max(0, bestScore),
                        BestCandidateName = bestResult?.CandidateName ?? "—"
                    });

                    var args = VisualStrategyBuilder.BuildArgs(
                        enginePath, desync, split, sni, ttl, fooling, multi,
                        useGameUdp: true, useHostlist: true, useIpSet: true, repeats: repeats);

                    var tempStrategy = new StrategyInfo
                    {
                        Name = candName,
                        Args = args,
                        Category = "АВТОКОНСТРУКТОР",
                        Description = desc
                    };

                    // Запуск пробного изолированного процесса winws
                    var stepResult = await EvaluateSingleHypothesisAsync(
                        bypass, tempStrategy, activeTargets, stepNum, total, desc, ct).ConfigureAwait(false);

                    if (stepResult.Score > bestScore)
                    {
                        bestScore = stepResult.Score;
                        bestResult = stepResult;
                        stepResult.IsWinnerSoFar = true;
                    }

                    results.Add(stepResult);
                    onStep?.Invoke(stepResult);

                    AppLog.Info($"[SmartAutoTuner] Тест {stepNum}/{total} ({candName}): Успех {stepResult.SuccessRate:0}%, Пинг {stepResult.AvgRttMs} мс, Скоринг {stepResult.Score}/100");
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                // Если был запущен обход, восстанавливаем исходное состояние
                if (before.IsRunning)
                {
                    try
                    {
                        var previousStrategy = strategyStore.Find(previousStrategyName);
                        if (previousStrategy != null)
                        {
                            if (restoreService)
                            {
                                await bypass.InstallServiceAsync(previousStrategy,
                                    EngineService.GetGameFilterMode(enginePath), CancellationToken.None).ConfigureAwait(false);
                            }
                            else if (restoreStandalone)
                            {
                                await bypass.StartAsync(previousStrategy,
                                    EngineService.GetGameFilterMode(enginePath), false, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                    }
                    catch { }
                }
            }

            if (bestResult == null || bestScore < 20)
            {
                return (false, "Не удалось подобрать рабочую стратегию. Проверьте сетевое подключение и права администратора.", null, results);
            }

            // Создание и сохранение победителя в CandidateStore
            var timestamp = DateTime.Now.ToString("dd.MM_HH:mm");
            var winnerCandidate = new StrategyCandidate
            {
                Name = $"SmartTuned ({bestResult.MutationDescription}) [{timestamp}]",
                SourceStrategy = "SmartAutoTuner",
                MutationDescription = bestResult.MutationDescription,
                ProviderText = provider?.DisplayText ?? "Локальный подбор",
                Provider = provider ?? new ProviderContext(),
                Args = bestResult.Args,
                Features = new StrategyFeatures { DesyncModes = new[] { bestResult.CandidateName } }
            };

            StrategyCandidateStore.TrySave(winnerCandidate, out var saved);

            var summary = $"Автоподбор завершён успешно! Победитель: «{bestResult.MutationDescription}» со скором {bestResult.Score}/100 (Успех: {bestResult.SuccessRate:0}%, Задержка: {bestResult.AvgRttMs} мс).";
            AppLog.Info("[SmartAutoTuner] " + summary);

            return (true, summary, saved, results);
        }

        private static async Task<AutoTunerStepResult> EvaluateSingleHypothesisAsync(
            BypassController bypass,
            StrategyInfo strategy,
            IReadOnlyList<MonitorTarget> targets,
            int stepNumber,
            int totalSteps,
            string description,
            CancellationToken ct)
        {
            try
            {
                var start = await bypass.StartAsync(strategy, GameFilterMode.Disabled, false, ct, testMode: true).ConfigureAwait(false);
                if (!start.Ok)
                {
                    return new AutoTunerStepResult
                    {
                        StepNumber = stepNumber,
                        TotalSteps = totalSteps,
                        CandidateName = strategy.Name,
                        MutationDescription = description,
                        SuccessRate = 0,
                        AvgRttMs = 999,
                        Score = 0,
                        Details = "Не удалось запустить процесс winws: " + start.Message,
                        Args = strategy.Args
                    };
                }

                // Задержка для инициализации WinDivert перехвата
                await Task.Delay(1200, ct).ConfigureAwait(false);

                // Тестируем контрольные адреса через системный ResourceProbe
                int successCount = 0;
                long totalRtt = 0;
                var detailsList = new List<string>();

                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    var probe = await ResourceProbe.CheckAsync(target, ct).ConfigureAwait(false);
                    if (probe.Ok)
                    {
                        successCount++;
                        totalRtt += probe.Milliseconds;
                        detailsList.Add($"{target.Name}: OK ({probe.Milliseconds} мс)");
                    }
                    else
                    {
                        detailsList.Add($"{target.Name}: FAIL");
                    }
                }

                var successRate = targets.Count > 0 ? (double)successCount / targets.Count * 100.0 : 0;
                var avgRtt = successCount > 0 ? totalRtt / successCount : 999;

                // Скоринг: 75% вес доступности + 25% вес минимального пинга
                var speedScore = Math.Clamp((int)((300.0 - Math.Min(avgRtt, 300)) / 300.0 * 25.0), 0, 25);
                var score = (int)Math.Round((successRate * 0.75) + speedScore);

                return new AutoTunerStepResult
                {
                    StepNumber = stepNumber,
                    TotalSteps = totalSteps,
                    CandidateName = strategy.Name,
                    MutationDescription = description,
                    SuccessRate = successRate,
                    AvgRttMs = avgRtt,
                    Score = score,
                    Details = string.Join(" · ", detailsList),
                    Args = strategy.Args
                };
            }
            finally
            {
                try
                {
                    await bypass.StopAsync().ConfigureAwait(false);
                }
                catch { }
            }
        }
    }
}
