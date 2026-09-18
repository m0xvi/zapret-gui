using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>Общий список стратегий: используется на главной и на странице «Стратегии».</summary>
    public sealed class StrategyStore : ObservableObject
    {
        private readonly AppSettings _settings;

        public StrategyStore(AppSettings settings) => _settings = settings;

        public ObservableCollection<StrategyInfo> Items { get; } = new();

        public StrategyInfo? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Items.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public StrategyInfo? Recommended => Items.FirstOrDefault(s => s.IsRecommended);

        public void Refresh()
        {
            var loaded = StrategyParser.LoadAll(_settings.EnginePath);
            var saved = StrategyCandidateStore.Load();
            Items.Clear();
            foreach (var strategy in loaded) Items.Add(strategy);
            foreach (var candidate in saved) Items.Add(candidate.ToStrategyInfo());
            Raise(nameof(Items));
            AppLog.Debug($"Найдено стратегий: {Items.Count}");
        }

        /// <summary>Место, где лежат .bat файлы стратегий.</summary>
        public string Folder => _settings.EnginePath;

        public bool HasEngine => File.Exists(Path.Combine(_settings.EnginePath, "bin", "winws.exe"));
    }
}
