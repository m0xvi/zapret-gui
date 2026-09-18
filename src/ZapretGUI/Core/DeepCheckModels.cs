using System;
using System.Collections.Generic;

namespace ZapretGui.Core
{
    /// <summary>Одна проверка в расширенном отчёте сети и обхода.</summary>
    public sealed class DeepCheckFinding
    {
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
        public string Recommendation { get; init; } = "";
        public string StatusKey { get; init; } = "Info";
        public string Icon => StatusKey switch
        {
            "Success" => "✓",
            "Danger" => "✕",
            "Warning" => "!",
            _ => "i"
        };
    }

    /// <summary>Измерение, которое удобно сравнивать между повторными проверками.</summary>
    public sealed class DeepCheckMetric
    {
        public string Title { get; init; } = "";
        public string Value { get; init; } = "";
        public string Details { get; init; } = "";
        public string StatusKey { get; init; } = "Info";
    }

    /// <summary>Рекомендация с указанием, что приложение смогло сделать автоматически.</summary>
    public sealed class DeepCheckRecommendation
    {
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
        public string ActionText { get; init; } = "";
        public string StatusKey { get; init; } = "Info";
        public bool IsAutomatic { get; init; }
    }

    /// <summary>Обезличенный снимок глубокой проверки для экспорта и повторного анализа.</summary>
    public sealed class DeepCheckReport
    {
        public DateTime CreatedAt { get; init; }
        public string Summary { get; init; } = "";
        public string SummaryKey { get; init; } = "Info";
        public string ProviderContext { get; init; } = "";
        public string ProviderLimitations { get; init; } = "";
        public string SelectedStrategy { get; init; } = "";
        public string RecommendedStrategy { get; init; } = "";
        public string GeneratedCandidate { get; init; } = "";
        public string GeneratedCandidateArgs { get; init; } = "";
        public string GeneratedCandidateMutation { get; init; } = "";
        public string GeneratedCandidateValidation { get; init; } = "";
        public string NetworkObservation { get; init; } = "";
        public List<DeepCheckFinding> Findings { get; init; } = new();
        public List<DeepCheckMetric> Metrics { get; init; } = new();
        public List<DeepCheckRecommendation> Recommendations { get; init; } = new();
    }
}
