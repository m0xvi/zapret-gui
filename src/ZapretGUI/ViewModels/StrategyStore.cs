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
        private readonly Dictionary<string, StrategyTestResult> _cachedTestResults = new(StringComparer.OrdinalIgnoreCase);

        public StrategyStore(AppSettings settings) => _settings = settings;

        public ObservableCollection<StrategyInfo> Items { get; } = new();

        public StrategyInfo? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Items.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public StrategyInfo? Recommended => Items.FirstOrDefault(s => s.IsRecommended);

        public void RecordTestResult(string name, StrategyTestResult result)
        {
            if (string.IsNullOrWhiteSpace(name) || result == null) return;
            _cachedTestResults[name] = result;
            var strategy = Find(name);
            strategy?.SetTestResult(result);
        }

        public void Refresh()
        {
            // Сохраняем все текущие результаты проверок перед перезагрузкой
            foreach (var item in Items)
            {
                if (item.TestResult != null)
                {
                    _cachedTestResults[item.Name] = item.TestResult;
                }
            }

            var loaded = StrategyParser.LoadAll(_settings.EnginePath);
            var saved = StrategyCandidateStore.Load();
            Items.Clear();
            foreach (var strategy in loaded)
            {
                if (_cachedTestResults.TryGetValue(strategy.Name, out var tr))
                {
                    strategy.SetTestResult(tr);
                }
                Items.Add(strategy);
            }
            foreach (var candidate in saved)
            {
                var sInfo = candidate.ToStrategyInfo();
                if (_cachedTestResults.TryGetValue(sInfo.Name, out var tr))
                {
                    sInfo.SetTestResult(tr);
                }
                Items.Add(sInfo);
            }
            Raise(nameof(Items));
            AppLog.Debug($"Найдено стратегий: {Items.Count}");
        }

        /// <summary>Место, где лежат .bat файлы стратегий.</summary>
        public string Folder => _settings.EnginePath;

        public bool HasEngine => File.Exists(Path.Combine(_settings.EnginePath, "bin", "winws.exe"));
    }
}
