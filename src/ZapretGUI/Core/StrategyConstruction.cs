using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZapretGui.Core
{
    /// <summary>Источник контекста провайдера. Endpoint-провайдеры DPI-suite сюда не подставляются.</summary>
    public enum ProviderContextSource
    {
        Unknown,
        UserInput,
        ExternalService
    }

    /// <summary>
    /// Контекст провайдера пользователя для будущего подбора. Имя не считается доказанным,
    /// пока источник не указан явно.
    /// </summary>
    public sealed class ProviderContext
    {
        public string Name { get; set; } = "";
        public string Asn { get; set; } = "";
        public ProviderContextSource Source { get; set; } = ProviderContextSource.Unknown;
        public DateTime? CheckedAt { get; set; }
        public int Confidence { get; set; }

        public bool IsKnown => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Asn);
        public bool IsUserProvided => Source == ProviderContextSource.UserInput;
        public string SourceText => Source switch
        {
            ProviderContextSource.UserInput => "указан пользователем",
            ProviderContextSource.ExternalService => "получен из внешнего источника",
            _ => "источник не подтверждён"
        };
        public string DisplayText
        {
            get
            {
                if (!IsKnown) return "Провайдер не указан — подбор работает по локальным признакам";
                var identity = string.Join(" · ", new[] { Name, Asn }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var confidence = Confidence <= 0 ? "не указана" : Math.Clamp(Confidence, 0, 100) + "%";
                return $"{identity} · {SourceText} · уверенность {confidence}";
            }
        }
    }

    /// <summary>Нормализованные признаки существующей стратегии без запуска winws.exe.</summary>
    public sealed class StrategyFeatures
    {
        public string[] DesyncModes { get; init; } = Array.Empty<string>();
        public int SplitParameterCount { get; init; }
        public string[] SplitPositions { get; init; } = Array.Empty<string>();
        public string[] SeqOvlValues { get; init; } = Array.Empty<string>();
        public string[] TtlValues { get; init; } = Array.Empty<string>();
        public bool UsesMultisplit { get; init; }
        public bool UsesFakeTls { get; init; }
        public bool UsesFakeQuic { get; init; }
        public string[] Hostlists { get; init; } = Array.Empty<string>();
        public string[] Ipsets { get; init; } = Array.Empty<string>();
        public string[] TcpFilters { get; init; } = Array.Empty<string>();
        public string[] UdpFilters { get; init; } = Array.Empty<string>();
        public string[] FakeParameters { get; init; } = Array.Empty<string>();
        public string[] ParameterNames { get; init; } = Array.Empty<string>();
        public string[] FixedArguments { get; init; } = Array.Empty<string>();
        public string[] TunableArguments { get; init; } = Array.Empty<string>();
        public string[] TunableParameterNames { get; init; } = Array.Empty<string>();

        public bool UsesHostlist => Hostlists.Length > 0;
        public bool UsesIpSet => Ipsets.Length > 0;
        public bool UsesTcpFilter => TcpFilters.Length > 0;
        public bool UsesUdpFilter => UdpFilters.Length > 0;
        public bool UsesGameFilter { get; init; }

        public string Fingerprint => string.Join(";", new[]
        {
            DesyncModes.Length == 0 ? "desync:none" : "desync:" + string.Join(",", DesyncModes),
            "split:" + string.Join(",", SplitPositions),
            "seqovl:" + string.Join(",", SeqOvlValues),
            "ttl:" + string.Join(",", TtlValues),
            "multisplit:" + (UsesMultisplit ? "yes" : "no"),
            "fake-tls:" + (UsesFakeTls ? "yes" : "no"),
            "fake-quic:" + (UsesFakeQuic ? "yes" : "no"),
            "hostlist:" + string.Join(",", Hostlists),
            "ipset:" + string.Join(",", Ipsets),
            "tcp:" + string.Join(",", TcpFilters),
            "udp:" + string.Join(",", UdpFilters),
            "game:" + (UsesGameFilter ? "yes" : "no")
        });

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (DesyncModes.Length > 0) parts.Add("desync: " + string.Join(", ", DesyncModes));
                if (SplitPositions.Length > 0) parts.Add("split-pos: " + string.Join(", ", SplitPositions));
                if (SeqOvlValues.Length > 0) parts.Add("seqovl: " + string.Join(", ", SeqOvlValues));
                if (TtlValues.Length > 0) parts.Add("TTL: " + string.Join(", ", TtlValues));
                if (UsesMultisplit) parts.Add("multisplit");
                if (UsesFakeTls) parts.Add("fake TLS");
                if (UsesFakeQuic) parts.Add("fake QUIC");
                if (UsesHostlist) parts.Add("hostlist");
                if (UsesIpSet) parts.Add("ipset");
                if (UsesTcpFilter || UsesUdpFilter) parts.Add("фильтры TCP/UDP");
                if (UsesGameFilter) parts.Add("игровой фильтр");
                return parts.Count == 0 ? "Специализированные признаки не найдены" : string.Join(" · ", parts);
            }
        }
    }

    /// <summary>Модель кандидата: только описание и аргументы, без запуска и сохранения.</summary>
    public sealed class StrategyCandidate
    {
        public string Name { get; init; } = "";
        public string SourceStrategy { get; init; } = "";
        public ProviderContext Provider { get; init; } = new();
        public string ProviderText { get; init; } = "";
        public StrategyFeatures Features { get; init; } = new();
        public List<string> Args { get; init; } = new();
        public string ArgsText => string.Join(" ", Args);
        public string FixedArgsText => string.Join(" ", Features.FixedArguments);
        public string TunableArgsText => string.Join(" ", Features.TunableArguments);
        public string Fingerprint => string.Join("\u001f", Args);
        public string MutationDescription { get; init; } = "Исходные аргументы стратегии";
        public string Summary { get; init; } = "";
    }

    public static class StrategyFeatureAnalyzer
    {
        private static readonly Regex DesyncRegex = new(@"^--dpi-desync=([a-z0-9_,-]+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static StrategyFeatures Analyze(StrategyInfo strategy)
        {
            var args = strategy.Args ?? new List<string>();
            var joined = string.Join(" ", args);
            var modes = args
                .SelectMany(argument => DesyncRegex.Matches(argument).Cast<Match>())
                .Select(match => match.Groups[1].Success && match.Groups[1].Value.Length > 0
                    ? match.Groups[1].Value.ToLowerInvariant()
                    : "enabled")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new StrategyFeatures
            {
                DesyncModes = modes,
                SplitParameterCount = args.Count(a => a.Contains("split", StringComparison.OrdinalIgnoreCase)),
                SplitPositions = Values(args, "split-pos"),
                SeqOvlValues = Values(args, "seqovl"),
                TtlValues = Values(args, "ttl"),
                UsesMultisplit = args.Any(a => a.Contains("multisplit", StringComparison.OrdinalIgnoreCase)),
                UsesFakeTls = strategy.UsesFakeTls || joined.Contains("fake-tls", StringComparison.OrdinalIgnoreCase),
                UsesFakeQuic = strategy.UsesFakeQuic || joined.Contains("fake-quic", StringComparison.OrdinalIgnoreCase),
                Hostlists = Values(args, "hostlist"),
                Ipsets = Values(args, "ipset"),
                TcpFilters = Values(args, "wf-tcp"),
                UdpFilters = Values(args, "wf-udp"),
                FakeParameters = args.Where(a => a.Contains("fake", StringComparison.OrdinalIgnoreCase)).ToArray(),
                ParameterNames = args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
                    .Select(a => a.Split('=', 2)[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                FixedArguments = args.Where(argument => !IsTunableArgument(argument)).ToArray(),
                TunableArguments = args.Where(IsTunableArgument).ToArray(),
                TunableParameterNames = args.Where(IsTunableArgument)
                    .Select(a => a.Split('=', 2)[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                UsesGameFilter = strategy.UsesGameFilter || args.Any(a => a.Contains(StrategyParser.GameFilterTcpToken, StringComparison.Ordinal) ||
                                                                         a.Contains(StrategyParser.GameFilterUdpToken, StringComparison.Ordinal))
            };
        }

        public static bool IsTunableArgument(string argument)
        {
            var value = argument.Trim().ToLowerInvariant();
            return value.StartsWith("--dpi-desync=", StringComparison.Ordinal) ||
                   value.Contains("--dpi-desync-split-pos", StringComparison.Ordinal) ||
                   value.Contains("--dpi-desync-split-seqovl", StringComparison.Ordinal) ||
                   value.Contains("--dpi-desync-ttl", StringComparison.Ordinal) ||
                   value.Contains("--dpi-desync-multisplit", StringComparison.Ordinal) ||
                   value.Contains("--dpi-desync-fake-tls", StringComparison.Ordinal) ||
                   value.Contains("--dpi-desync-fake-quic", StringComparison.Ordinal);
        }

        private static string[] Values(IEnumerable<string> args, string parameter)
        {
            return args
                .Where(argument => argument.Contains(parameter, StringComparison.OrdinalIgnoreCase))
                .Select(argument =>
                {
                    var equals = argument.IndexOf('=');
                    return equals >= 0 && equals + 1 < argument.Length
                        ? argument[(equals + 1)..]
                        : argument;
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }
    }

    public sealed class StrategyCandidateGenerationOptions
    {
        public int MaxCandidates { get; init; } = 16;
        public int MaxVariantsPerSource { get; init; } = 4;
    }

    public sealed class StrategyCandidateGenerationResult
    {
        public List<StrategyCandidate> Candidates { get; init; } = new();
        public int SourcesConsidered { get; init; }
        public bool UsedUntestedFallback { get; init; }
        public bool WasLimited { get; init; }
        public bool ProviderHeuristicApplied { get; init; }
        public bool LocalProfileApplied { get; init; }
        public string ProviderHeuristicText { get; init; } = "";
    }

    /// <summary>
    /// Создаёт небольшой набор вариантов из параметров уже существующих стратегий.
    /// Ничего не запускает и не записывает на диск.
    /// </summary>
    public static class StrategyCandidateGenerator
    {
        public static StrategyCandidateGenerationResult Generate(
            IEnumerable<StrategyInfo> strategies,
            ProviderContext provider,
            GameFilterMode gameFilter,
            StrategyCandidateGenerationOptions? options = null,
            CancellationToken cancellationToken = default,
            IReadOnlyList<StrategyEvaluationHistoryRecord>? history = null,
            NetworkObservationSnapshot? localProfile = null)
        {
            options ??= new StrategyCandidateGenerationOptions();
            var maxCandidates = Math.Clamp(options.MaxCandidates, 1, 128);
            var maxPerSource = Math.Clamp(options.MaxVariantsPerSource, 1, 32);
            var all = strategies.Where(strategy => strategy != null).ToList();
            var eligible = all.Where(strategy => strategy.IsRecommended ||
                                                  strategy.TestState == StrategyTestState.Passed).ToList();
            var usedUntestedFallback = eligible.Count == 0 && all.Count > 0;
            if (usedUntestedFallback) eligible = all;

            var ranked = RankSources(eligible, provider, history, localProfile,
                out var providerHeuristicApplied, out var localProfileApplied);
            var pools = BuildPools(ranked);
            var candidates = new List<StrategyCandidate>();
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            var wasLimited = false;

            foreach (var source in ranked)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= maxCandidates)
                {
                    wasLimited = true;
                    break;
                }

                var baseCandidate = CreateCandidate(source, source.Args, provider, gameFilter,
                    "Исходная стратегия без изменений");
                AddCandidate(baseCandidate, candidates, fingerprints, maxCandidates, ref wasLimited);
                var variantsForSource = 1;
                if (variantsForSource >= maxPerSource) continue;

                foreach (var argument in source.Args.Where(StrategyFeatureAnalyzer.IsTunableArgument))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parameter = ParameterName(argument);
                    if (!pools.TryGetValue(parameter, out var values)) continue;

                    foreach (var replacement in values)
                    {
                        if (string.Equals(argument, replacement, StringComparison.Ordinal)) continue;
                        if (candidates.Count >= maxCandidates)
                        {
                            wasLimited = true;
                            break;
                        }
                        if (variantsForSource >= maxPerSource) break;

                        var mutatedArgs = source.Args.ToList();
                        var argumentIndex = mutatedArgs.IndexOf(argument);
                        if (argumentIndex < 0) continue;
                        mutatedArgs[argumentIndex] = replacement;
                        var description = $"{parameter}: {argument} → {replacement}";
                        var candidate = CreateCandidate(source, mutatedArgs, provider, gameFilter, description);
                        if (AddCandidate(candidate, candidates, fingerprints, maxCandidates, ref wasLimited))
                            variantsForSource++;
                    }
                    if (candidates.Count >= maxCandidates || variantsForSource >= maxPerSource) break;
                }
            }

            return new StrategyCandidateGenerationResult
            {
                Candidates = candidates,
                SourcesConsidered = eligible.Count,
                UsedUntestedFallback = usedUntestedFallback,
                WasLimited = wasLimited,
                ProviderHeuristicApplied = providerHeuristicApplied,
                LocalProfileApplied = localProfileApplied,
                ProviderHeuristicText = BuildHeuristicText(provider, providerHeuristicApplied, localProfileApplied, localProfile)
            };
        }

        private static List<StrategyInfo> RankSources(
            List<StrategyInfo> strategies,
            ProviderContext provider,
            IReadOnlyList<StrategyEvaluationHistoryRecord>? history,
            NetworkObservationSnapshot? localProfile,
            out bool providerApplied,
            out bool localApplied)
        {
            providerApplied = false;
            localApplied = false;
            var hasProviderHistory = provider != null && provider.IsKnown && history != null && history.Count > 0;
            var hasLocalProfile = localProfile != null && HasUsefulLocalProfile(localProfile);
            if (strategies.Count < 2 || (!hasProviderHistory && !hasLocalProfile)) return strategies;

            var ranked = strategies.Select((strategy, index) =>
            {
                var matching = hasProviderHistory
                    ? history!.Where(record => record.MatchesProvider(provider!) &&
                                               record.SourceStrategy.Equals(strategy.Name, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(record => record.CompletedAtUtc)
                        .ToList()
                    : new List<StrategyEvaluationHistoryRecord>();
                var bestScore = matching.Count == 0 ? -1 : matching.Max(record => record.Score);
                var successful = matching.Count == 0 ? 0 : matching.Max(record => record.SuccessfulRepeats);
                var localScore = hasLocalProfile ? LocalHeuristicScore(strategy, localProfile!) : 0;
                return new { strategy, index, bestScore, successful, localScore };
            }).ToList();

            providerApplied = ranked.Any(item => item.bestScore >= 0);
            localApplied = ranked.Any(item => item.localScore > 0);
            if (!providerApplied && !localApplied) return strategies;

            return ranked
                .OrderByDescending(item => item.bestScore >= 0 ? 1 : 0)
                .ThenByDescending(item => item.bestScore)
                .ThenByDescending(item => item.localScore)
                .ThenByDescending(item => item.successful)
                .ThenBy(item => item.index)
                .Select(item => item.strategy)
                .ToList();
        }

        private static bool HasUsefulLocalProfile(NetworkObservationSnapshot profile)
            => profile.HasDnsError || profile.HasTcpTimeout || profile.HasTlsError ||
               profile.HasHttpError || profile.HasDpiFreeze || profile.BypassImprovesResult;

        private static int LocalHeuristicScore(StrategyInfo strategy, NetworkObservationSnapshot profile)
        {
            var features = StrategyFeatureAnalyzer.Analyze(strategy);
            var score = 0;
            if ((profile.HasDpiFreeze || profile.HasTlsError || profile.BypassImprovesResult) && features.UsesFakeTls) score += 3;
            if ((profile.HasDpiFreeze || profile.HasTcpTimeout) && features.SplitParameterCount > 0) score += 2;
            if (profile.HasHttpError && (features.UsesFakeTls || features.UsesFakeQuic)) score++;
            return score;
        }

        private static string BuildHeuristicText(ProviderContext provider, bool providerApplied,
            bool localApplied, NetworkObservationSnapshot? localProfile)
        {
            var parts = new List<string>();
            if (providerApplied)
                parts.Add("прошлый результат указанного провайдера");
            if (localApplied)
                parts.Add("локальный профиль признаков от " + (localProfile?.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "последней проверки"));
            if (parts.Count > 0)
                return "Порядок источников усилен по " + string.Join(" и ", parts) + "; это только эвристика, не гарантия и не определение ISP.";
            if (provider != null && provider.IsKnown)
                return "История для указанного провайдера не найдена — использован обычный порядок стратегий.";
            if (localProfile != null)
                return "В локальном профиле нет признаков для приоритизации — использован обычный порядок стратегий.";
            return "Провайдер не указан — порядок построен только по локальным результатам.";
        }

        private static StrategyCandidate CreateCandidate(StrategyInfo source, List<string> args,
            ProviderContext provider, GameFilterMode gameFilter, string mutationDescription)
        {
            var copy = new StrategyInfo
            {
                Name = source.Name,
                Args = args.ToList(),
                Category = source.Category,
                UsesFakeTls = source.UsesFakeTls,
                UsesFakeQuic = source.UsesFakeQuic,
                UsesSplit = source.UsesSplit,
                UsesGameFilter = source.UsesGameFilter
            };
            var name = $"Вариант на основе «{source.Name}»";
            return StrategyCandidateFactory.CreatePreview(copy, provider, gameFilter, name, mutationDescription);
        }

        private static Dictionary<string, List<string>> BuildPools(IEnumerable<StrategyInfo> strategies)
        {
            var pools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var argument in strategies.SelectMany(strategy => strategy.Args)
                         .Where(StrategyFeatureAnalyzer.IsTunableArgument))
            {
                var parameter = ParameterName(argument);
                if (!pools.TryGetValue(parameter, out var values))
                {
                    values = new List<string>();
                    pools[parameter] = values;
                }
                if (!values.Contains(argument, StringComparer.Ordinal)) values.Add(argument);
            }
            return pools;
        }

        private static string ParameterName(string argument)
            => argument.Split('=', 2)[0].Trim().ToLowerInvariant();

        private static bool AddCandidate(StrategyCandidate candidate, List<StrategyCandidate> candidates,
            HashSet<string> fingerprints, int maxCandidates, ref bool wasLimited)
        {
            if (!fingerprints.Add(candidate.Fingerprint)) return false;
            if (candidates.Count >= maxCandidates)
            {
                wasLimited = true;
                return false;
            }
            candidates.Add(candidate);
            return true;
        }
    }

    public static class StrategyCandidateFactory
    {
        public static StrategyCandidate CreatePreview(StrategyInfo strategy, ProviderContext provider,
            GameFilterMode gameFilter = GameFilterMode.Disabled, string? displayName = null,
            string mutationDescription = "Исходные аргументы стратегии")
        {
            var features = StrategyFeatureAnalyzer.Analyze(strategy);
            var providerText = provider.DisplayText;
            var providerName = !string.IsNullOrWhiteSpace(provider.Name) ? provider.Name.Trim() : provider.Asn.Trim();
            var name = provider.IsKnown
                ? $"Черновик для {providerName} на основе «{strategy.Name}»"
                : $"Черновик на основе «{strategy.Name}»";
            return new StrategyCandidate
            {
                Name = displayName ?? name,
                SourceStrategy = strategy.Name,
                Provider = provider,
                ProviderText = providerText,
                Features = features,
                Args = ExpandGameFilterArgs(strategy.Args, gameFilter),
                MutationDescription = mutationDescription,
                Summary = provider.IsKnown
                    ? "Провайдерский контекст сохранён как эвристика. Кандидат ещё не запускался и не изменяет файлы стратегии."
                    : "Провайдер не указан. Кандидат построен только по признакам существующей стратегии и ещё не запускался."
            };
        }

        private static List<string> ExpandGameFilterArgs(IEnumerable<string> args, GameFilterMode gameFilter)
        {
            var tcp = gameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.TcpOnly ? "1024-65535" : "12";
            var udp = gameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.UdpOnly ? "1024-65535" : "12";
            return args.Select(argument => argument
                    .Replace(StrategyParser.GameFilterTcpToken, tcp)
                    .Replace(StrategyParser.GameFilterUdpToken, udp))
                .ToList();
        }
    }
}
