using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    /// <summary>Запись о временном изменении обхода и результате восстановления.</summary>
    public sealed class RecoveryJournalEntry
    {
        public DateTime StartedAt { get; init; }
        public DateTime FinishedAt { get; init; }
        public string Operation { get; init; } = "";
        public string BeforeState { get; init; } = "";
        public string AfterState { get; init; } = "";
        public bool Restored { get; init; }
        public string Actions { get; init; } = "";
        public string RemainingIssue { get; init; } = "";
    }

    /// <summary>Локальный ограниченный журнал восстановления без сетевых запросов.</summary>
    public static class RecoveryJournalStore
    {
        private const int MaxEntries = 100;
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static IReadOnlyList<RecoveryJournalEntry> Load()
        {
            try
            {
                if (!File.Exists(AppPaths.RecoveryJournalFile))
                    return Array.Empty<RecoveryJournalEntry>();
                return JsonSerializer.Deserialize<List<RecoveryJournalEntry>>(
                           File.ReadAllText(AppPaths.RecoveryJournalFile), Options)
                       ?.OrderByDescending(entry => entry.FinishedAt)
                       .Take(MaxEntries)
                       .ToList()
                       ?? new List<RecoveryJournalEntry>();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать журнал восстановления: " + ex.Message);
                return Array.Empty<RecoveryJournalEntry>();
            }
        }

        public static void Append(string operation, string beforeState, string afterState,
            bool restored, string actions, string remainingIssue = "", DateTime? startedAt = null)
        {
            try
            {
                var entries = Load().ToList();
                entries.Add(new RecoveryJournalEntry
                {
                    StartedAt = startedAt ?? DateTime.Now,
                    FinishedAt = DateTime.Now,
                    Operation = operation,
                    BeforeState = beforeState,
                    AfterState = afterState,
                    Restored = restored,
                    Actions = actions,
                    RemainingIssue = remainingIssue
                });
                entries = entries.OrderByDescending(entry => entry.FinishedAt).Take(MaxEntries).ToList();
                var temporary = AppPaths.RecoveryJournalFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options));
                if (File.Exists(AppPaths.RecoveryJournalFile))
                    File.Replace(temporary, AppPaths.RecoveryJournalFile, null);
                else
                    File.Move(temporary, AppPaths.RecoveryJournalFile);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось записать журнал восстановления: " + ex.Message);
            }
        }
    }
}
