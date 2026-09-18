using System;

namespace ZapretGui.Core
{
    public enum DiagStatus { Ok, Warning, Error, Info }

    public sealed class DiagnosticItem
    {
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
        public string FixHint { get; init; } = "";
        public DiagStatus Status { get; init; }

        /// <summary>Идентификатор автоисправления (пусто — чинится только вручную).</summary>
        public string FixId { get; init; } = "";

        public string FixLabel { get; init; } = "Исправить";

        public bool HasAutoFix => FixId.Length > 0;

        public string Icon => Status switch
        {
            DiagStatus.Ok => "✓",
            DiagStatus.Warning => "!",
            DiagStatus.Error => "✕",
            _ => "i"
        };

        public string SeverityKey => Status switch
        {
            DiagStatus.Ok => "Success",
            DiagStatus.Warning => "Warning",
            DiagStatus.Error => "Danger",
            _ => "Info"
        };
    }
}
