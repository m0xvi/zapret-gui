using System;

namespace ZapretGui.Core
{
    /// <summary>Один пункт единой проверки — системная функция с баллом 0..10.</summary>
    public sealed class UnifiedSystemScore : ObservableObject
    {
        private string _title = "";
        private string _details = "";
        private int _score;
        private string _statusKey = "Muted";

        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public string Category
        {
            get; set;
        } = "";

        public string Details
        {
            get => _details;
            set => Set(ref _details, value);
        }

        /// <summary>0..10</summary>
        public int Score
        {
            get => _score;
            set
            {
                if (Set(ref _score, Math.Clamp(value, 0, 10)))
                {
                    Raise(nameof(ScoreText));
                    Raise(nameof(PercentText));
                    Raise(nameof(StatusKey));
                }
            }
        }

        public int MaxScore => 10;

        public string ScoreText => $"{Score}/{MaxScore}";

        public string PercentText => $"{Score * 10}%";

        public string StatusKey
        {
            get => _statusKey;
            set => Set(ref _statusKey, value);
        }

        public string FixHint { get; set; } = "";
        public string Icon => Score >= 8 ? "✓" : Score >= 5 ? "!" : Score == 0 ? "✕" : "•";
    }

    /// <summary>Один вариант стратегии с бальной оценкой 0..10 (и 0..100 для совместимости).</summary>
    public sealed class UnifiedVariantScore : ObservableObject
    {
        private string _strategyName = "";
        private int _score10;
        private bool _isBest;
        private string _statusKey = "Info";

        public string StrategyName
        {
            get => _strategyName;
            set => Set(ref _strategyName, value);
        }

        public string DnsLabel { get; set; } = "";

        public int Passed { get; set; }
        public int Total { get; set; }
        public long AvgMs { get; set; }

        /// <summary>0..10</summary>
        public int Score10
        {
            get => _score10;
            set
            {
                if (Set(ref _score10, Math.Clamp(value, 0, 10)))
                {
                    Raise(nameof(ScoreText));
                    Raise(nameof(Score100));
                    Raise(nameof(Score100Text));
                }
            }
        }

        public int Score100 => Score10 * 10;
        public string ScoreText => $"{Score10}/10";
        public string Score100Text => $"{Score100}/100";

        public bool IsBest
        {
            get => _isBest;
            set => Set(ref _isBest, value);
        }

        public string StatusKey
        {
            get => _statusKey;
            set => Set(ref _statusKey, value);
        }

        public string Summary => $"{Passed}/{Total} OK · {AvgMs} мс";
        public string BallText => IsBest ? $"⭐ {ScoreText}" : ScoreText;
    }
}
