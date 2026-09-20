using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        public static readonly IReadOnlyList<(string Desync, string Split, string Sni, string Ttl, string Fooling, bool Multi, string Desc)> Hypotheses =
            new List<(string, string, string, string, string, bool, string)>
            {
                // 1. Семейство Split2 (Разделение TLS ClientHello)
                ("split2", "midsld", "www.google.com", "auto", "badsum", true, "Разделение TLS (midsld) + Fake SNI Google + badsum + multisplit"),
                ("split2", "sniext", "www.google.com", "auto", "badsum", true, "Разделение на границе SNI (sniext) + Fake SNI Google + badsum"),
                ("split2", "sniext", "www.microsoft.com", "auto", "badsum", true, "Разделение на границе SNI (sniext) + Fake SNI Microsoft + badsum"),
                ("split2", "sniext", "www.cloudflare.com", "auto", "badseq", false, "Разделение SNI (sniext) + Fake SNI Cloudflare + badseq"),
                ("split2", "1", "www.google.com", "auto", "badsum", true, "Разделение 1-го байта ClientHello (split-pos=1) + badsum"),
                ("split2", "2", "www.google.com", "auto", "badsum", false, "Разделение 2-го байта ClientHello (split-pos=2) + badsum"),
                ("split2", "3", "www.google.com", "auto", "badseq", false, "Разделение 3-го байта ClientHello (split-pos=3) + badseq"),
                ("split2", "host", "www.google.com", "auto", "badsum", false, "Разделение по домену Host (split-pos=host) + badsum"),
                ("split2", "1", "none", "auto", "disoob", true, "Комбинированный сплит 1-го байта + OOB смещение"),

                // 2. Семейство Disorder / Out-of-Order (Нарушение порядка TCP-пакетов)
                ("disoob", "1", "none", "auto", "badseq", false, "Out-of-Order смещение 1 байта (disoob=1) + badseq"),
                ("disoob", "2", "none", "auto", "badsum", false, "Out-of-Order смещение 2 байт (disoob=2) + badsum"),
                ("disoob", "3", "none", "auto", "badsum", true, "Out-of-Order смещение 3 байт (disoob=3) + badsum + multisplit"),
                ("disoob", "sniext", "none", "auto", "badseq", false, "Out-of-Order по границе SNI (disoob=sniext) + badseq"),

                // 3. Семейство Fake ClientHello (Инъекция поддельных TLS-пакетов)
                ("fake", "none", "www.google.com", "auto", "badsum", false, "Fake TLS ClientHello + Google SNI + repeats=6"),
                ("fake", "none", "www.microsoft.com", "auto", "badsum", false, "Fake TLS ClientHello + Microsoft SNI + badsum"),
                ("fake", "none", "fonts.google.com", "auto", "badsum", false, "Fake TLS + fonts.google.com SNI + badsum"),
                ("fake", "none", "ru.wikipedia.org", "auto", "badsum", false, "Fake TLS + Wikipedia SNI (ru.wikipedia.org) + badsum"),
                ("fake", "none", "yandex.ru", "auto", "badsum", true, "Fake TLS с RU SNI (yandex.ru) + агрессивный QUIC"),

                // 4. Двойной Fake SNI и Fake Disorder
                ("fakedsni", "midsld", "www.google.com", "auto", "badsum", true, "Двойной Fake SNI (fakedsni midsld) + multisplit"),
                ("fakedsni", "sniext", "www.google.com", "auto", "badsum", false, "Двойной Fake SNI (fakedsni sniext) + badsum"),

                // 5. Семейство Multisplit & Sequence Overlap
                ("multisplit", "midsld", "none", "auto", "badsum", true, "Многосегментный оверлей TCP (split-seqovl=1, midsld)"),
                ("multisplit", "2", "none", "auto", "badsum", true, "Многосегментный оверлей TCP со смещением 2 байта"),

                // 6. Семейство TTL & MD5 Evasion (Обход через расстояние до узла фильтрации)
                ("fake", "none", "www.google.com", "1", "badsum", false, "Fake TLS с ультра-малым TTL (TTL=1) + badsum"),
                ("fake", "none", "www.google.com", "3", "badsum", false, "Fake TLS с фиксированным малым TTL (TTL=3)"),
                ("fake", "none", "www.google.com", "5", "badsum", false, "Fake TLS с фиксированным средним TTL (TTL=5)"),
                ("fake", "none", "www.google.com", "4", "md5sig", false, "Fake TLS с TCP MD5 Signature fooling (md5sig) + TTL=4")
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
            var testTargets = targets.Count > 0 ? targets.ToList() : ConnectionTester.GetEffectiveTargets().Take(3).ToList();
            var total = Hypotheses.Count;

            var before = bypass.GetStatus();
            var restoreService = before.State == BypassState.RunningService;
            var restoreStandalone = before.State == BypassState.RunningStandalone;
            var previousStrategyName = before.StrategyName;

            AppLog.Info($"[SmartAutoTuner] Запуск глубокого автоподбора стратегии ({total} гипотез, {testTargets.Count} контрольных точек)...");

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

                    var (desync, split, sni, ttl, fooling, multi, desc) = Hypotheses[i];
                    var stepNum = i + 1;
                    var candName = $"SmartHypothesis_{stepNum}_{desync}";

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
                        useGameUdp: true, useHostlist: true, useIpSet: true);

                    var tempStrategy = new StrategyInfo
                    {
                        Name = candName,
                        Args = args,
                        Category = "АВТОКОНСТРУКТОР",
                        Description = desc
                    };

                    // Запуск пробного изолированного процесса winws
                    var stepResult = await EvaluateSingleHypothesisAsync(
                        bypass, tempStrategy, testTargets, stepNum, total, desc, ct).ConfigureAwait(false);

                    if (stepResult.Score > bestScore)
                    {
                        bestScore = stepResult.Score;
                        bestResult = stepResult;
                        stepResult.IsWinnerSoFar = true;
                    }

                    results.Add(stepResult);
                    onStep?.Invoke(stepResult);

                    AppLog.Info($"[SmartAutoTuner] Тест {stepNum}/{total} ({candName}): Успех {stepResult.SuccessRate:0}%, Пинг {stepResult.AvgRttMs} мс, Скоринг {stepResult.Score}/100");
                    await Task.Delay(400, ct).ConfigureAwait(false);
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

                // Небольшая задержка для инициализации WinDivert перехвата
                await Task.Delay(1000, ct).ConfigureAwait(false);

                // Тестируем контрольные адреса
                int successCount = 0;
                long totalRtt = 0;
                var detailsList = new List<string>();

                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = true,
                    ConnectTimeout = TimeSpan.FromSeconds(3.5),
                    PooledConnectionLifetime = TimeSpan.FromSeconds(10)
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };

                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Head, target.Url);
                        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZapretTest/1.3");
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(3.5));

                        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                        sw.Stop();

                        successCount++;
                        totalRtt += sw.ElapsedMilliseconds;
                        detailsList.Add($"{target.Name}: OK ({sw.ElapsedMilliseconds} мс)");
                    }
                    catch (Exception)
                    {
                        sw.Stop();
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
