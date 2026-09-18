using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ZapretGui.Core
{
    /// <summary>Результат проверки одной стратегии на реальном сетевом соединении.</summary>
    public sealed class StrategyTestResult
    {
        public StrategyInfo Strategy { get; init; } = new();
        public bool Started { get; init; }
        public string ErrorMessage { get; init; } = "";
        public IReadOnlyList<ConnectionCheck> Checks { get; init; } = Array.Empty<ConnectionCheck>();
        public TimeSpan Elapsed { get; init; }

        /// <summary>YouTube и Discord считаются основными проверками обхода.</summary>
        public bool IsSuitable => Started &&
            Checks.Any(c => c.Title == "YouTube" && c.Ok) &&
            Checks.Any(c => c.Title == "Discord" && c.Ok) &&
            // Одной удачной пары ресурсов недостаточно: Flowseal standard mode
            // проверяет несколько независимых доменов. Требуем минимум три ответа.
            Checks.Count(c => c.Ok) >= 3;

        public int PassedCount => Checks.Count(c => c.Ok);
        public int FailedCount => Checks.Count(c => !c.Ok);
        public int Score => PassedCount * 1000 + (IsSuitable ? 1000 : 0);
        public string ElapsedText => Elapsed.TotalSeconds < 1
            ? $"{Elapsed.TotalMilliseconds:0} мс"
            : $"{Elapsed.TotalSeconds:0.0} с";

        public string FailureReasonsText
        {
            get
            {
                if (!Started) return ErrorMessage.Length > 0 ? ErrorMessage : "winws.exe не запустился";
                var failures = Checks.Where(check => !check.Ok)
                    .Select(check => string.IsNullOrWhiteSpace(check.Details)
                        ? check.Title + ": нет соединения"
                        : check.Title + ": " + check.Details)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return failures.Length == 0 ? "нет" : string.Join(" · ", failures);
            }
        }

        public string ChecksDetailsText => string.Join(" · ", Checks.Select(check =>
            $"{check.Title}: {(check.Ok ? "OK" : "нет")} · {check.Milliseconds} мс"));

        public string SummaryText
        {
            get
            {
                if (!Started) return ErrorMessage.Length > 0 ? ErrorMessage : "winws.exe не запустился";
                var details = string.Join(" · ", Checks.Select(c =>
                    $"{c.Title}: {(c.Ok ? "OK" : "нет")}"));
                return $"{PassedCount}/{Checks.Count} проверок · {ElapsedText}" +
                       (details.Length > 0 ? "\n" + details : "");
            }
        }
    }

    /// <summary>Итоги последовательной проверки всех найденных стратегий.</summary>
    public sealed class StrategyTestBatchResult
    {
        public bool Cancelled { get; init; }
        public IReadOnlyList<StrategyTestResult> Results { get; init; } = Array.Empty<StrategyTestResult>();

        public StrategyTestResult? Best => Results
            .Where(r => r.Started && r.Checks.Count > 0)
            .OrderByDescending(r => r.IsSuitable)
            .ThenByDescending(r => r.PassedCount)
            .ThenBy(r => r.Checks.Where(c => c.Ok).Sum(c => c.Milliseconds))
            .FirstOrDefault();

        public int PassedStrategies => Results.Count(r => r.IsSuitable);
    }

    /// <summary>
    /// Накопленный результат повторных проверок одного кандидата. Сам объект кандидата
    /// остаётся только описанием аргументов и не получает права запускать обход.
    /// </summary>
    public sealed class StrategyCandidateEvaluation : INotifyPropertyChanged
    {
        private readonly List<StrategyTestResult> _repeats = new();
        private string _statusText = "не проверено";
        private bool _isTesting;

        public StrategyCandidateEvaluation(StrategyCandidate candidate)
        {
            Candidate = candidate;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public StrategyCandidate Candidate { get; }
        public string Name => Candidate.Name;
        public string MutationDescription => Candidate.MutationDescription;
        public IReadOnlyList<StrategyTestResult> Repeats => _repeats;
        public bool IsTesting => _isTesting;
        public int RepeatCount => _repeats.Count;
        public int SuccessfulRepeats => _repeats.Count(result => result.IsSuitable);
        public int PassedChecks => _repeats.Sum(result => result.PassedCount);
        public int TotalChecks => _repeats.Sum(result => result.Checks.Count);
        public int FailedChecks => _repeats.Sum(result => result.FailedCount);
        public string ChecksText => $"{PassedChecks}/{TotalChecks}";
        public bool IsStable => RepeatCount > 0 && SuccessfulRepeats == RepeatCount;

        public double AverageElapsedMilliseconds => RepeatCount == 0
            ? 0
            : _repeats.Average(result => Math.Max(0, result.Elapsed.TotalMilliseconds));

        public string AverageElapsedText
        {
            get
            {
                if (RepeatCount == 0 || AverageElapsedMilliseconds <= 0) return "—";
                return AverageElapsedMilliseconds < 1000
                    ? $"{AverageElapsedMilliseconds:0} мс"
                    : $"{AverageElapsedMilliseconds / 1000:0.0} с";
            }
        }

        /// <summary>Небольшая добавка за скорость; стабильность и успешность имеют больший вес.</summary>
        public int LatencyScore => RepeatCount == 0 || AverageElapsedMilliseconds <= 0
            ? 0
            : Math.Max(0, 100 - (int)Math.Min(100, AverageElapsedMilliseconds / 100));

        /// <summary>Воспроизводимый score: стабильность и успешные проверки важнее задержки.</summary>
        public int Score => SuccessfulRepeats * 10000 + PassedChecks * 100 +
            (RepeatCount == 0 ? 0 : SuccessfulRepeats * 100 / RepeatCount) + LatencyScore;

        public string ScoreText => RepeatCount == 0 ? "—" : Score.ToString();
        public string ScoreBreakdownText => RepeatCount == 0
            ? "score не рассчитан"
            : $"стабильность {SuccessfulRepeats * 10000} · проверки {PassedChecks * 100} · скорость {LatencyScore}";
        public string StabilityText => RepeatCount == 0
            ? "не проверено"
            : $"успешно {SuccessfulRepeats}/{RepeatCount}";

        public string FailureReasonsText
        {
            get
            {
                var failures = _repeats
                    .SelectMany(result => new[] { result.FailureReasonsText })
                    .Where(text => !string.IsNullOrWhiteSpace(text) && !text.Equals("нет", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return failures.Length == 0 ? "нет" : string.Join(" · ", failures);
            }
        }

        public string AttemptsText => RepeatCount == 0
            ? "попытки отсутствуют"
            : string.Join(" · ", _repeats.Select((result, index) =>
                $"#{index + 1}: {result.PassedCount}/{result.Checks.Count} · {result.ElapsedText}"));
        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText == value) return;
                _statusText = value;
                Raise();
            }
        }

        public void MarkTesting(int repeatNumber, int totalRepeats)
        {
            _isTesting = true;
            StatusText = $"проверка {repeatNumber}/{totalRepeats}…";
            Raise(nameof(IsTesting));
        }

        public void AddResult(StrategyTestResult result, int repeatNumber, int totalRepeats)
        {
            _repeats.Add(result);
            StatusText = $"проверено {repeatNumber}/{totalRepeats}";
            Raise(nameof(RepeatCount));
            Raise(nameof(SuccessfulRepeats));
            Raise(nameof(PassedChecks));
            Raise(nameof(TotalChecks));
            Raise(nameof(FailedChecks));
            Raise(nameof(ChecksText));
            Raise(nameof(IsStable));
            Raise(nameof(AverageElapsedMilliseconds));
            Raise(nameof(AverageElapsedText));
            Raise(nameof(LatencyScore));
            Raise(nameof(Score));
            Raise(nameof(ScoreText));
            Raise(nameof(ScoreBreakdownText));
            Raise(nameof(StabilityText));
            Raise(nameof(FailureReasonsText));
            Raise(nameof(AttemptsText));
        }

        public void Complete()
        {
            _isTesting = false;
            StatusText = RepeatCount == 0
                ? "не проверено"
                : $"{StabilityText} · среднее {AverageElapsedText} · score {Score}";
            Raise(nameof(IsTesting));
        }

        private void Raise([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
