using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    /// <summary>
    /// Один сохранённый результат проверки стратегии или кандидата. История хранится
    /// только в профиле пользователя и не влияет на запуск обхода.
    /// </summary>
    public sealed class StrategyEvaluationHistoryRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
        public string CandidateName { get; set; } = "";
        public string SourceStrategy { get; set; } = "";
        public string MutationDescription { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public List<string> Args { get; set; } = new();
        public string FeaturesSummary { get; set; } = "";
        public string FeaturesFingerprint { get; set; } = "";
        public ProviderContext Provider { get; set; } = new();
        public int RepeatCount { get; set; }
        public int SuccessfulRepeats { get; set; }
        public int PassedChecks { get; set; }
        public int TotalChecks { get; set; }
        public int Score { get; set; }
        public double AverageElapsedMilliseconds { get; set; }
        public string FailureReasons { get; set; } = "";
        public List<StrategyEvaluationHistoryAttempt> Attempts { get; set; } = new();

        [JsonIgnore]
        public string CompletedAtText => CompletedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        [JsonIgnore]
        public string StabilityText => RepeatCount == 0
            ? "не проверено"
            : $"успешно {SuccessfulRepeats}/{RepeatCount}";

        [JsonIgnore]
        public string ScoreText => RepeatCount == 0 ? "—" : Score.ToString();

        [JsonIgnore]
        public string ResultSummaryText => $"{StabilityText} · score: {ScoreText}";

        [JsonIgnore]
        public string AverageElapsedText
        {
            get
            {
                if (AverageElapsedMilliseconds <= 0) return "—";
                return AverageElapsedMilliseconds < 1000
                    ? $"{AverageElapsedMilliseconds:0} мс"
                    : $"{AverageElapsedMilliseconds / 1000:0.0} с";
            }
        }

        [JsonIgnore]
        public string ProviderText => Provider?.DisplayText ?? "Провайдер не указан";

        public bool MatchesProvider(ProviderContext context)
        {
            if (context == null || !context.IsKnown || Provider == null || !Provider.IsKnown) return false;
            return string.Equals(Normalize(context.Name), Normalize(Provider.Name), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Normalize(context.Asn), Normalize(Provider.Asn), StringComparison.OrdinalIgnoreCase);
        }

        public static StrategyEvaluationHistoryRecord FromEvaluation(StrategyCandidateEvaluation evaluation)
        {
            var candidate = evaluation.Candidate;
            return new StrategyEvaluationHistoryRecord
            {
                CandidateName = evaluation.Name,
                SourceStrategy = candidate.SourceStrategy,
                MutationDescription = evaluation.MutationDescription,
                Fingerprint = candidate.Fingerprint,
                Args = candidate.Args.ToList(),
                FeaturesSummary = candidate.Features.Summary,
                FeaturesFingerprint = candidate.Features.Fingerprint,
                Provider = CopyProvider(candidate.Provider),
                RepeatCount = evaluation.RepeatCount,
                SuccessfulRepeats = evaluation.SuccessfulRepeats,
                PassedChecks = evaluation.PassedChecks,
                TotalChecks = evaluation.TotalChecks,
                Score = evaluation.Score,
                AverageElapsedMilliseconds = evaluation.AverageElapsedMilliseconds,
                FailureReasons = evaluation.FailureReasonsText,
                Attempts = evaluation.Repeats.Select((result, index) => StrategyEvaluationHistoryAttempt.From(result, index + 1)).ToList()
            };
        }

        public static StrategyEvaluationHistoryRecord FromTestResult(
            StrategyInfo strategy, StrategyTestResult result, ProviderContext? provider = null)
        {
            var features = StrategyFeatureAnalyzer.Analyze(strategy);
            var candidate = new StrategyCandidate
            {
                Name = strategy.Name,
                SourceStrategy = strategy.Name,
                Args = strategy.Args.ToList(),
                Features = features,
                Provider = provider ?? new ProviderContext(),
                MutationDescription = "Проверка исходной стратегии"
            };
            var attempt = StrategyEvaluationHistoryAttempt.From(result, 1);
            return new StrategyEvaluationHistoryRecord
            {
                CandidateName = strategy.Name,
                SourceStrategy = strategy.Name,
                MutationDescription = candidate.MutationDescription,
                Fingerprint = candidate.Fingerprint,
                Args = candidate.Args.ToList(),
                FeaturesSummary = features.Summary,
                FeaturesFingerprint = features.Fingerprint,
                Provider = CopyProvider(candidate.Provider),
                RepeatCount = 1,
                SuccessfulRepeats = result.IsSuitable ? 1 : 0,
                PassedChecks = result.PassedCount,
                TotalChecks = result.Checks.Count,
                Score = result.IsSuitable ? 10000 + result.PassedCount * 100 : result.PassedCount * 100,
                AverageElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
                FailureReasons = result.FailureReasonsText,
                Attempts = new List<StrategyEvaluationHistoryAttempt> { attempt }
            };
        }

        private static ProviderContext CopyProvider(ProviderContext provider)
        {
            return new ProviderContext
            {
                Name = provider?.Name ?? "",
                Asn = provider?.Asn ?? "",
                Source = provider?.Source ?? ProviderContextSource.Unknown,
                CheckedAt = provider?.CheckedAt,
                Confidence = provider?.Confidence ?? 0
            };
        }

        private static string Normalize(string value) => (value ?? "").Trim();
    }

    public sealed class StrategyEvaluationHistoryAttempt
    {
        public int Number { get; set; }
        public bool Started { get; set; }
        public int PassedChecks { get; set; }
        public int TotalChecks { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string Summary { get; set; } = "";
        public string FailureReasons { get; set; } = "";

        public static StrategyEvaluationHistoryAttempt From(StrategyTestResult result, int number)
        {
            return new StrategyEvaluationHistoryAttempt
            {
                Number = number,
                Started = result.Started,
                PassedChecks = result.PassedCount,
                TotalChecks = result.Checks.Count,
                ElapsedMilliseconds = (long)Math.Max(0, result.Elapsed.TotalMilliseconds),
                Summary = result.SummaryText,
                FailureReasons = result.FailureReasonsText
            };
        }
    }

    /// <summary>JSON-отчёт, который пользователь может сохранить и отправить разработчику.</summary>
    public sealed class StrategyEvaluationReport
    {
        public string ReportType { get; set; } = "Zapret GUI — автоконструктор стратегий";
        public int FormatVersion { get; set; } = 1;
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
        public ProviderContext Provider { get; set; } = new();
        public List<StrategyCandidate> GeneratedCandidates { get; set; } = new();
        public List<StrategyEvaluationHistoryRecord> CurrentEvaluations { get; set; } = new();
        public List<StrategyEvaluationHistoryRecord> History { get; set; } = new();
    }

    /// <summary>Безопасное ограниченное хранилище каталога проверок в профиле пользователя.</summary>
    public static class StrategyEvaluationHistoryStore
    {
        private const int MaxRecords = 200;
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string FilePath => AppPaths.StrategyEvaluationHistoryFile;

        public static List<StrategyEvaluationHistoryRecord> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<StrategyEvaluationHistoryRecord>();
                var json = File.ReadAllText(FilePath);
                var records = JsonSerializer.Deserialize<List<StrategyEvaluationHistoryRecord>>(json, Options)
                    ?? new List<StrategyEvaluationHistoryRecord>();
                return records
                    .Where(record => record != null)
                    .OrderByDescending(record => record.CompletedAtUtc)
                    .Take(MaxRecords)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать историю автоконструктора: " + ex.Message);
                return new List<StrategyEvaluationHistoryRecord>();
            }
        }

        public static bool TryAppend(StrategyEvaluationHistoryRecord record)
        {
            try
            {
                var records = Load();
                records.Insert(0, record);
                records = records
                    .OrderByDescending(item => item.CompletedAtUtc)
                    .Take(MaxRecords)
                    .ToList();

                AppPaths.EnsureDir(Path.GetDirectoryName(FilePath) ?? AppPaths.AppData);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records, Options));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить историю автоконструктора: " + ex.Message);
                return false;
            }
        }
    }
}
