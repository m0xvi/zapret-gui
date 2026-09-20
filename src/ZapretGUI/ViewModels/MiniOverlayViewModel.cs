using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>
    /// Модель представления для компактного плавающего мини-виджета (Mini HUD Overlay).
    /// </summary>
    public sealed class MiniOverlayViewModel : ObservableObject
    {
        private readonly MainViewModel _main;

        public event Action? RequestOpenMain;
        public event Action? RequestCloseOverlay;

        public MiniOverlayViewModel(MainViewModel main)
        {
            _main = main;

            ToggleBypassCommand = new AsyncRelayCommand(ToggleBypassAsync);
            ToggleGameModeCommand = new RelayCommand(ToggleGameMode);
            OpenMainWindowCommand = new RelayCommand(() => RequestOpenMain?.Invoke());
            CloseOverlayCommand = new RelayCommand(() => RequestCloseOverlay?.Invoke());
        }

        public AppSettings Settings => _main.Settings;

        public bool IsRunning => _main.Bypass.GetStatus().IsRunning;

        public string StatusText => _main.Home.StatusText;
        public string BypassStateKey => _main.Home.BypassStateKey;

        public string StrategyName => !string.IsNullOrEmpty(Settings.SelectedStrategy)
            ? Settings.SelectedStrategy
            : "Стратегия не выбрана";

        public string PingText
        {
            get
            {
                var snap = _main.RealTimePing;
                if (snap == null || !Settings.RealTimePingEnabled) return "RTT: —";
                var fast = snap.Fastest;
                return fast != null ? $"⚡ {fast.RttMs} мс ({fast.Target.Name})" : "RTT: —";
            }
        }

        public string PingSeverityKey => _main.RealTimePing?.StatusSeverityKey ?? "Muted";

        public bool IsGameRunning => _main.IsGameRunning;
        public string ActiveGameName => _main.ActiveGameName ?? "";

        public bool GameModeActive
        {
            get => Settings.GameModeActive;
            set
            {
                if (Settings.GameModeActive == value) return;
                Settings.GameModeActive = value;
                SettingsStore.Save(Settings);
                Raise(nameof(GameModeActive));
                Raise(nameof(GameModeStatusText));
                _main.ApplyGameFilterState();
            }
        }

        public string GameModeStatusText => GameModeActive ? "Игровой режим: ВКЛ (UDP исключён)" : "Игровой режим: ВЫКЛ";

        public double WindowOpacity => Math.Clamp(Settings.MiniOverlayOpacity / 100.0, 0.5, 1.0);
        public bool Topmost => Settings.MiniOverlayTopmost;

        public ICommand ToggleBypassCommand { get; }
        public ICommand ToggleGameModeCommand { get; }
        public ICommand OpenMainWindowCommand { get; }
        public ICommand CloseOverlayCommand { get; }

        public void Refresh()
        {
            Raise(nameof(IsRunning));
            Raise(nameof(StatusText));
            Raise(nameof(BypassStateKey));
            Raise(nameof(StrategyName));
            Raise(nameof(PingText));
            Raise(nameof(PingSeverityKey));
            Raise(nameof(IsGameRunning));
            Raise(nameof(ActiveGameName));
            Raise(nameof(GameModeActive));
            Raise(nameof(GameModeStatusText));
            Raise(nameof(WindowOpacity));
            Raise(nameof(Topmost));
        }

        private async Task ToggleBypassAsync()
        {
            await _main.Home.ToggleBypassAsync();
            Refresh();
        }

        private void ToggleGameMode()
        {
            GameModeActive = !GameModeActive;
        }
    }
}
