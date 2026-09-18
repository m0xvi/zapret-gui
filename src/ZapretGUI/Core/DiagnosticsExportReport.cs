using System;
using System.Collections.Generic;

namespace ZapretGui.Core
{
    /// <summary>
    /// Безопасная форма экспортируемого отчёта. В неё намеренно не попадают
    /// содержимое settings.json целиком, пароли, токены и аргументы команд.
    /// </summary>
    public sealed class DiagnosticsExportReport
    {
        public string ReportType { get; init; } = "Zapret GUI — диагностический отчёт";
        public int FormatVersion { get; init; } = 1;
        public DateTime GeneratedAtUtc { get; init; }
        public DiagnosticsExportApplication Application { get; init; } = new();
        public DiagnosticsExportReadiness Readiness { get; init; } = new();
        public DiagnosticsSnapshot? CurrentDiagnostics { get; init; }
        public DiagnosticsSnapshot? LastSavedDiagnostics { get; init; }
        public DiagnosticsExportDpi? CurrentDpi { get; init; }
        public DiagnosticsExportDpi? LastSavedDpi { get; init; }
        public List<StrategyEvaluationHistoryRecord> StrategyHistory { get; init; } = new();
        public List<RecoveryJournalEntry> RecoveryHistory { get; init; } = new();
        public List<string> LogTail { get; init; } = new();
    }

    public sealed class DiagnosticsExportApplication
    {
        public string Version { get; init; } = "";
        public string EngineVersion { get; init; } = "";
        public string EnginePath { get; init; } = "";
        public string SelectedStrategy { get; init; } = "";
        public bool IsAdmin { get; init; }
        public bool SafeMode { get; init; }
        public bool FirstLaunchWizardCompleted { get; init; }
        public ProviderContext Provider { get; init; } = new();
    }

    public sealed class DiagnosticsExportReadiness
    {
        public string Status { get; init; } = "";
        public string Details { get; init; } = "";
        public string Key { get; init; } = "";
    }

    public sealed class DiagnosticsExportDpi
    {
        public DateTime CreatedAt { get; init; }
        public int TargetsTotal { get; init; }
        public int TargetsTested { get; init; }
        public string Summary { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public string SuiteSource { get; init; } = "";
        public DateTime? SuiteLoadedAt { get; init; }
        public NetworkObservationSnapshot Observation { get; init; } = new();
        public List<DpiTargetResult> Results { get; init; } = new();
        public DpiTargetResult? ControlResult { get; init; }
        public DiagnosticsExportComparison? BypassComparison { get; init; }
    }

    public sealed class DiagnosticsExportComparison
    {
        public ResourceDiagnosisKind Kind { get; init; }
        public string Level { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string Summary { get; init; } = "";
    }
}
