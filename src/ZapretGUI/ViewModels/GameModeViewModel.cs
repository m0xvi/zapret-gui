using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>Элемент списка «какие игры обходить» — галочка + домены + профиль.</summary>
    public sealed class GameBypassOption : ObservableObject
    {
        public GameBypassOption(string id, string name, string description, string[] domains, string profileId, string icon)
        {
            Id = id; Name = name; Description = description; Domains = domains; ProfileId = profileId; Icon = icon;
        }
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string[] Domains { get; }
        public string ProfileId { get; }
        public string Icon { get; }

        private bool _isEnabled;
        public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

        private string _status = "";
        public string Status { get => _status; set => Set(ref _status, value); }

        public string DomainsText => string.Join(", ", Domains.Take(4)) + (Domains.Length > 4 ? "…" : "");
    }

    public sealed class GameModeViewModel : ObservableObject
    {
        private readonly MainViewModel _main;

        public GameModeViewModel(MainViewModel main)
        {
            _main = main;
            GameOptions = new ObservableCollection<GameBypassOption>(CreateOptions());
            // восстановить состояние галочек из настроек
            foreach (var opt in GameOptions)
                opt.IsEnabled = _main.Settings.EnabledGameBypassIds.Contains(opt.Id);
            // подпидка на изменения: при клике добавляем/убираем домены
            foreach (var opt in GameOptions)
                opt.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(GameBypassOption.IsEnabled) && s is GameBypassOption o)
                        _ = ToggleGameAsync(o);
                };

            ToggleGameModeCommand = new RelayCommand(ToggleGameMode);
            ToggleDetectionCommand = new RelayCommand(() => { GameDetectionEnabled = !GameDetectionEnabled; });
            ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, () => SelectedProfile != null);
            ApplyGamingTweaksCommand = new AsyncRelayCommand(ApplyGamingTweaksAsync);
            RevertGamingTweaksCommand = new AsyncRelayCommand(RevertGamingTweaksAsync);
            FixPhasmophobiaCommand = new AsyncRelayCommand(FixPhasmophobiaAsync);
            FixPubgCommand = new AsyncRelayCommand(FixPubgAsync);
            FixSelectedGamesCommand = new AsyncRelayCommand(FixSelectedGamesAsync);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            RefreshCommand = new RelayCommand(Refresh);
            DismissMessageCommand = new RelayCommand(() => { Message = ""; MessageKey = "Info"; });

            RefreshProfiles();
            RefreshGamingStatus();
            RefreshDetectionStatus();
            if (_main.GameDetector != null) _main.GameDetector.GameStatusChanged += (_, __) => RefreshDetectionStatus();
        }

        private static IEnumerable<GameBypassOption> CreateOptions()
        {
            yield return new GameBypassOption(
                "discord_voice", "🎙️ Discord Voice", "Голосовые каналы Discord (UDP 50000-65535). Рекомендуется для всех — не мешает играм.",
                new[] { "discord.com", "discordapp.com", "discord.gg" }, "discord_voice", "🎙️");
            yield return new GameBypassOption(
                "steam_cs2", "🎯 Steam / CS2 / Dota 2", "Valve Source Engine (27000-27100) — исключает из обхода, убирает loss/choke.",
                new[] { "steampowered.com", "steamcommunity.com", "steamserver.net" }, "steam_cs2", "🎯");
            yield return new GameBypassOption(
                "phasmophobia", "👻 Phasmophobia", "Фикс лобби Phasmophobia (Steam P2P 27015-27200 + Photon). Если не коннектит в лобби — включи эту галочку и нажми «Починить».",
                new[] { "kineticgames.co.uk", "photonengine.io", "exitgames.com", "steamcommunity.com" }, "phasmophobia", "👻");
            yield return new GameBypassOption(
                "pubg", "🥘 PUBG: BATTLEGROUNDS", "Фикс ошибки 2610.1.1 — лобби и матчмейкинг PUBG (7086-7995, 10000-10100, 17300-17999).",
                new[] { "pubg.com", "krafton.com", "playbattlegrounds.com", "prod-live-front.playbattlegrounds.com" }, "pubg", "🥘");
            yield return new GameBypassOption(
                "riot_games", "⚔️ Valorant / LoL (Riot)", "Исключает 5000-5500 для Vanguard, не мешает DISCORD.",
                new[] { "riotgames.com", "valorant.com", "lol.com" }, "riot_games", "⚔️");
            yield return new GameBypassOption(
                "apex_ea", "🏆 Apex Legends / EA", "EA matcher 18000, 27000-30000 — меньше джиттера.",
                new[] { "ea.com", "apexlegends.com" }, "apex_ea", "🏆");
            yield return new GameBypassOption(
                "roblox", "🧱 Roblox & WebRTC", "Голосовой чат Roblox (49152-65535) + WebRTC.",
                new[] { "roblox.com", "rbxcdn.com" }, "roblox", "🧱");
            yield return new GameBypassOption(
                "war_thunder", "🛡️ War Thunder", "Gaijin (20000-30000) — стабильный пинг.",
                new[] { "warthunder.com", "gaijin.net" }, "all_broad", "🛡️");
        }

        public AppSettings Settings => _main.Settings;

        // ---- Игровой режим ----
        public bool GameModeActive
        {
            get => Settings.GameModeActive;
            set { Settings.GameModeActive = value; SettingsStore.Save(Settings); _main.ApplyGameFilterState(); Raise(nameof(GameModeActive)); Raise(nameof(GameModeStatusText)); Raise(nameof(GameModeBadge)); Raise(nameof(DetectionStatusText)); Raise(nameof(DetectionStatusKey)); }
        }
        public string GameModeStatusText => Settings.GameModeActive ? "Игровой режим ВКЛ — UDP пропускается для минимального пинга" : "Игровой режим ВЫКЛ — обычный обход (может мешать лобби)";
        public string GameModeBadge => Settings.GameModeActive ? "ВКЛ" : "ВЫКЛ";

        public bool GameDetectionEnabled
        {
            get => Settings.GameDetectionEnabled;
            set { Settings.GameDetectionEnabled = value; SettingsStore.Save(Settings); if (value) _main.GameDetector.Start(); else _main.GameDetector.Stop(); Raise(nameof(GameDetectionEnabled)); }
        }
        public bool AutoGameModeOnLaunch
        {
            get => Settings.AutoGameModeOnLaunch;
            set { Settings.AutoGameModeOnLaunch = value; SettingsStore.Save(Settings); Raise(nameof(AutoGameModeOnLaunch)); }
        }
        public string DetectionStatusText => _main.GameDetector != null && _main.IsGameRunning ? $"🎮 Запущена: {_main.ActiveGameName} — режим {(Settings.GameModeActive ? "ВКЛ" : "ВЫКЛ")}" : "🎮 Игры не обнаружены — всё спокойно";
        public string DetectionStatusKey => _main.IsGameRunning ? "Success" : "Muted";

        // ---- Фильтр ----
        public IReadOnlyList<GameFilterProfile> Profiles => GameFilterPortConfig.PredefinedProfiles;
        private GameFilterProfile? _selectedProfile;
        public GameFilterProfile? SelectedProfile
        {
            get => _selectedProfile;
            set { if (Set(ref _selectedProfile, value)) (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); Raise(nameof(SelectedProfileDescription)); }
        }
        public string SelectedProfileDescription => SelectedProfile?.Description ?? "";

        public int GameFilterIndex
        {
            get => EngineService.GetGameFilterMode(Settings.EnginePath) switch { GameFilterMode.TcpAndUdp => 1, GameFilterMode.TcpOnly => 2, GameFilterMode.UdpOnly => 3, _ => 0 };
            set
            {
                var mode = value switch { 1 => GameFilterMode.TcpAndUdp, 2 => GameFilterMode.TcpOnly, 3 => GameFilterMode.UdpOnly, _ => GameFilterMode.Disabled };
                EngineService.SetGameFilterMode(Settings.EnginePath, mode);
                Raise(nameof(GameFilterIndex));
                Message = value == 0 ? "Игровой фильтр выключен" : $"Игровой фильтр: {mode}. Перезапусти обход."; MessageKey = "Info";
            }
        }
        public string[] GameFilterOptions { get; } = { "Выключен (не трогать игры)", "TCP + UDP (максимум обхода)", "Только TCP", "Только UDP" };

        // ---- Сетевые твики ----
        private string _gamingNetworkStatus = "";
        public string GamingNetworkStatusText { get => _gamingNetworkStatus; private set => Set(ref _gamingNetworkStatus, value); }
        private bool _gamingNetworkOptimized;
        public bool GamingNetworkIsOptimized { get => _gamingNetworkOptimized; private set => Set(ref _gamingNetworkOptimized, value); }

        public ObservableCollection<GameBypassOption> GameOptions { get; }

        private string _message = "";
        public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(MessageVisible)); } }
        public bool MessageVisible => !string.IsNullOrWhiteSpace(Message);
        private string _messageKey = "Info";
        public string MessageKey { get => _messageKey; private set => Set(ref _messageKey, value); }

        public ICommand ToggleGameModeCommand { get; }
        public ICommand ToggleDetectionCommand { get; }
        public ICommand ApplyProfileCommand { get; }
        public ICommand ApplyGamingTweaksCommand { get; }
        public ICommand RevertGamingTweaksCommand { get; }
        public ICommand FixPhasmophobiaCommand { get; }
        public ICommand FixPubgCommand { get; }
        public ICommand FixSelectedGamesCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand DismissMessageCommand { get; }

        public void Refresh()
        {
            RefreshProfiles();
            RefreshGamingStatus();
            RefreshDetectionStatus();
            Raise(nameof(GameFilterIndex));
        }

        private void RefreshProfiles()
        {
            var id = Settings.GameFilterProfileId;
            SelectedProfile = GameFilterPortConfig.GetProfileById(id);
        }

        public void RefreshGamingStatus()
        {
            try
            {
                var opt = GamingNetworkOptimizer.CheckStatus();
                GamingNetworkStatusText = opt.Summary;
                GamingNetworkIsOptimized = opt.IsOptimized;
            }
            catch { GamingNetworkStatusText = "Параметры сети по умолчанию"; GamingNetworkIsOptimized = false; }
        }

        public void RefreshDetectionStatus()
        {
            Raise(nameof(DetectionStatusText));
            Raise(nameof(DetectionStatusKey));
        }

        private void ToggleGameMode()
        {
            _main.ToggleGameMode();
            Raise(nameof(GameModeActive));
            Raise(nameof(GameModeStatusText));
            Raise(nameof(GameModeBadge));
            Message = Settings.GameModeActive ? "🎮 Игровой режим включён" : "🎮 Игровой режим выключен";
            MessageKey = "Success";
        }

        private async Task ApplyProfileAsync()
        {
            if (SelectedProfile == null) return;
            Settings.GameFilterProfileId = SelectedProfile.Id;
            SettingsStore.Save(Settings);
            // применяем на лету если обход запущен — пересоздаём с новыми портами
            var status = _main.Bypass.GetStatus();
            if (status.IsRunning)
            {
                var strat = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                if (strat != null)
                {
                    var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                    var res = status.ServiceState == ServiceState.Running
                        ? await _main.Bypass.InstallServiceAsync(strat, mode)
                        : await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                    Message = res.Message; MessageKey = res.Ok ? "Success" : "Danger";
                    _main.Home.RefreshStatus();
                }
            }
            else
            {
                Message = $"Профиль «{SelectedProfile.Name}» сохранён. Запусти обход чтобы применить."; MessageKey = "Success";
            }
            Raise(nameof(GameModeStatusText));
        }

        private async Task ToggleGameAsync(GameBypassOption opt)
        {
            try
            {
                var enabled = opt.IsEnabled;
                // сохраняем в настройки
                var list = Settings.EnabledGameBypassIds;
                if (enabled && !list.Contains(opt.Id)) list.Add(opt.Id);
                if (!enabled && list.Contains(opt.Id)) list.Remove(opt.Id);
                SettingsStore.Save(Settings);

                if (enabled)
                {
                    // включаем — добавляем домены этой игры в списки
                    var added = 0; var skipped = 0;
                    var path = Path.Combine(Settings.EnginePath, "lists", "list-general-user.txt");
                    StrategyParser.EnsureUserLists(Settings.EnginePath);
                    var lines = File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet() : new HashSet<string>();
                    using var writer = File.AppendText(path);
                    foreach (var d in opt.Domains)
                    {
                        if (lines.Contains(d.ToLowerInvariant())) { skipped++; continue; }
                        writer.WriteLine(d); added++;
                    }
                    writer.Flush();
                    opt.Status = added > 0 ? $"✅ +{added} доменов" : "уже в списке";
                    // если есть профиль — предлагаем переключить
                    if (!string.IsNullOrWhiteSpace(opt.ProfileId) && Settings.GameFilterProfileId != opt.ProfileId)
                    {
                        Settings.GameFilterProfileId = opt.ProfileId;
                        SettingsStore.Save(Settings);
                        SelectedProfile = GameFilterPortConfig.GetProfileById(opt.ProfileId);
                        Message = $"Включено «{opt.Name}»: +{added} доменов, профиль «{SelectedProfile?.Name}» выбран. Перезапусти обход.";
                        MessageKey = "Success";
                    }
                    else
                    {
                        Message = $"Включено «{opt.Name}»: +{added} доменов (пропущено {skipped}).";
                        MessageKey = "Success";
                    }
                }
                else
                {
                    // выключаем — убираем домены этой игры из пользовательского списка (опционально)
                    // не удаляем агрессивно, просто сообщаем
                    opt.Status = "выключено";
                    Message = $"Выключено «{opt.Name}» — домены останутся в списке, удалить можно в «Списки».";
                    MessageKey = "Info";
                }
                Raise(nameof(GameOptions));
            }
            catch (Exception ex)
            {
                Message = $"Ошибка «{opt.Name}»: {ex.Message}"; MessageKey = "Danger";
            }
        }

        private async Task FixPhasmophobiaAsync()
        {
            var (ok, msg) = MonitorTargetStore.AddPhasmophobiaPreset(Settings);
            Message = msg; MessageKey = ok ? "Success" : "Danger";
            RefreshProfiles();
            Raise(nameof(GameFilterIndex));
            // отмечаем галочку
            var opt = GameOptions.FirstOrDefault(o => o.Id == "phasmophobia");
            if (opt != null) { opt.IsEnabled = true; if (!Settings.EnabledGameBypassIds.Contains(opt.Id)) { Settings.EnabledGameBypassIds.Add(opt.Id); SettingsStore.Save(Settings); } }
            if (ok && _main.Bypass.GetStatus().IsRunning)
            {
                var strat = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                if (strat != null)
                {
                    var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                    var res = _main.Bypass.GetStatus().ServiceState == ServiceState.Running
                        ? await _main.Bypass.InstallServiceAsync(strat, mode)
                        : await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                    Message += " " + res.Message; _main.Home.RefreshStatus();
                }
            }
        }

        private async Task FixPubgAsync()
        {
            var (ok, msg) = MonitorTargetStore.AddPubgPreset(Settings);
            Message = msg; MessageKey = ok ? "Success" : "Danger";
            RefreshProfiles();
            var opt = GameOptions.FirstOrDefault(o => o.Id == "pubg");
            if (opt != null) opt.IsEnabled = true;
            if (ok && _main.Bypass.GetStatus().IsRunning)
            {
                var strat = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                if (strat != null)
                {
                    var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                    var res = _main.Bypass.GetStatus().ServiceState == ServiceState.Running
                        ? await _main.Bypass.InstallServiceAsync(strat, mode)
                        : await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                    Message += " " + res.Message; _main.Home.RefreshStatus();
                }
            }
        }

        private async Task FixSelectedGamesAsync()
        {
            // применяет все отмеченные галочками игры
            var enabled = GameOptions.Where(o => o.IsEnabled).ToList();
            if (enabled.Count == 0) { Message = "Отметь галочками игры которые хочешь обходить"; MessageKey = "Warning"; return; }
            var totalAdded = 0;
            try
            {
                var path = Path.Combine(Settings.EnginePath, "lists", "list-general-user.txt");
                StrategyParser.EnsureUserLists(Settings.EnginePath);
                var lines = File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet() : new HashSet<string>();
                using var writer = File.AppendText(path);
                foreach (var opt in enabled)
                    foreach (var d in opt.Domains)
                        if (!lines.Contains(d.ToLowerInvariant())) { writer.WriteLine(d); lines.Add(d.ToLowerInvariant()); totalAdded++; }
                writer.Flush();
                Message = $"Готово — добавлено {totalAdded} доменов для {enabled.Count} игр. Профиль «{SelectedProfile?.Name}» активен. Перезапусти обход.";
                MessageKey = "Success";
                if (_main.Bypass.GetStatus().IsRunning)
                {
                    var strat = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                    if (strat != null)
                    {
                        var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                        var res = _main.Bypass.GetStatus().ServiceState == ServiceState.Running
                            ? await _main.Bypass.InstallServiceAsync(strat, mode)
                            : await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                        Message += " " + res.Message; _main.Home.RefreshStatus();
                    }
                }
            }
            catch (Exception ex) { Message = "Ошибка: " + ex.Message; MessageKey = "Danger"; }
        }

        private async Task ApplyGamingTweaksAsync()
        {
            if (!Shell.IsAdmin()) { Message = "Нужны права администратора"; MessageKey = "Danger"; return; }
            var (ok, msg) = await GamingNetworkOptimizer.ApplyTweaksAsync();
            Message = msg; MessageKey = ok ? "Success" : "Danger";
            RefreshGamingStatus();
        }

        private async Task RevertGamingTweaksAsync()
        {
            if (!Shell.IsAdmin()) { Message = "Нужны права администратора"; MessageKey = "Danger"; return; }
            var (ok, msg) = await GamingNetworkOptimizer.RevertTweaksAsync();
            Message = msg; MessageKey = ok ? "Success" : "Danger";
            RefreshGamingStatus();
        }
    }
}
