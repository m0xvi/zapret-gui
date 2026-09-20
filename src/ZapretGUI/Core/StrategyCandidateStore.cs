using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGui.Core
{
    /// <summary>Сохранённое пользователем описание кандидата, отдельно от файлов движка.</summary>
    public sealed class SavedStrategyCandidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public string Name { get; set; } = "";
        public string SourceStrategy { get; set; } = "";
        public string MutationDescription { get; set; } = "";
        public string ProviderText { get; set; } = "";
        public ProviderContext Provider { get; set; } = new();
        public List<string> Args { get; set; } = new();
        public string Fingerprint { get; set; } = "";

        public string SavedAtText => SavedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        public string ArgsText => string.Join(" ", Args);
        public string ArgumentsPreview => ArgsText;
        public string Summary => string.IsNullOrWhiteSpace(MutationDescription)
            ? $"Сохранённая стратегия ({SavedAtText})"
            : $"{MutationDescription} · сохранён {SavedAtText}";
        public string DisplayName => Name.Length == 0 ? "Сохранённый кандидат" : Name;
        public string StrategyName => $"{DisplayName} · {Id[..Math.Min(8, Id.Length)]}";

        public static SavedStrategyCandidate From(StrategyCandidate candidate)
        {
            return new SavedStrategyCandidate
            {
                Name = candidate.Name,
                SourceStrategy = candidate.SourceStrategy,
                MutationDescription = candidate.MutationDescription,
                ProviderText = candidate.ProviderText,
                Provider = candidate.Provider,
                Args = candidate.Args.ToList(),
                Fingerprint = candidate.Fingerprint
            };
        }

        public StrategyInfo ToStrategyInfo()
        {
            var source = new StrategyInfo
            {
                Name = SourceStrategy,
                Args = Args.ToList()
            };
            var candidate = StrategyCandidateFactory.CreatePreview(
                source,
                Provider ?? new ProviderContext(),
                GameFilterMode.Disabled,
                StrategyName,
                MutationDescription);
            var features = candidate.Features;
            return new StrategyInfo
            {
                Name = StrategyName,
                FileName = Id + ".json",
                FullPath = Path.Combine(AppPaths.CandidatesDir, "candidates.json"),
                Args = candidate.Args.ToList(),
                Category = "АВТОКОНСТРУКТОР",
                UsesFakeTls = features.UsesFakeTls,
                UsesFakeQuic = features.UsesFakeQuic,
                UsesSplit = features.SplitParameterCount > 0,
                UsesGameFilter = features.UsesGameFilter,
                Description = $"{MutationDescription} · источник: {SourceStrategy}"
            };
        }

        public StrategyCandidate ToCandidate()
        {
            var source = new StrategyInfo
            {
                Name = SourceStrategy,
                Args = Args.ToList()
            };
            return StrategyCandidateFactory.CreatePreview(
                source,
                Provider ?? new ProviderContext(),
                GameFilterMode.Disabled,
                Name,
                MutationDescription);
        }
    }

    /// <summary>
    /// Хранилище кандидатов в %APPDATA%\ZapretGUI\candidates. Оригинальные .bat и папка
    /// движка не затрагиваются.
    /// </summary>
    public static class StrategyCandidateStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static string FilePath => Path.Combine(AppPaths.CandidatesDir, "candidates.json");

        public static List<SavedStrategyCandidate> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<SavedStrategyCandidate>();
                return JsonSerializer.Deserialize<List<SavedStrategyCandidate>>(
                           File.ReadAllText(FilePath), Options)
                       ?? new List<SavedStrategyCandidate>();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать сохранённые кандидаты: " + ex.Message);
                return new List<SavedStrategyCandidate>();
            }
        }

        public static bool Save(SavedStrategyCandidate saved)
        {
            var candidates = Load();
            candidates.Insert(0, saved);
            return TryWrite(candidates);
        }

        public static bool TrySave(StrategyCandidate candidate, out SavedStrategyCandidate saved)
        {
            var candidates = Load();
            var existing = candidates.FirstOrDefault(item =>
                item.Fingerprint.Equals(candidate.Fingerprint, StringComparison.Ordinal));
            if (existing != null)
            {
                saved = existing;
                return true;
            }

            saved = SavedStrategyCandidate.From(candidate);
            candidates.Insert(0, saved);
            return TryWrite(candidates);
        }

        public static bool Delete(string id)
        {
            var candidates = Load();
            var removed = candidates.RemoveAll(candidate => candidate.Id == id) > 0;
            return !removed || TryWrite(candidates);
        }

        public static bool Contains(string fingerprint)
            => Load().Any(candidate => candidate.Fingerprint.Equals(fingerprint, StringComparison.Ordinal));

        public static StrategyInfo? FindStrategy(string strategyName)
        {
            var saved = Load().FirstOrDefault(candidate =>
                candidate.StrategyName.Equals(strategyName, StringComparison.OrdinalIgnoreCase));
            return saved?.ToStrategyInfo();
        }

        private static bool TryWrite(List<SavedStrategyCandidate> candidates)
        {
            try
            {
                AppPaths.EnsureDir(AppPaths.CandidatesDir);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(candidates, Options));
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось сохранить кандидатов: " + ex.Message);
                return false;
            }
        }
    }
}
