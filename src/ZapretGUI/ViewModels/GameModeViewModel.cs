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
            ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, () => !IsRestartingBypass);
            ApplyGamingTweaksCommand = new AsyncRelayCommand(ApplyGamingTweaksAsync);
            RevertGamingTweaksCommand = new AsyncRelayCommand(RevertGamingTweaksAsync);
            FixPhasmophobiaCommand = new AsyncRelayCommand(FixPhasmophobiaAsync);
            FixPubgCommand = new AsyncRelayCommand(FixPubgAsync);
            FixSelectedGamesCommand = new AsyncRelayCommand(FixSelectedGamesAsync);
            ApplySteamBypassCommand = new AsyncRelayCommand(ApplySteamBypassAsync);
            CheckPhotonRegionsCommand = new AsyncRelayCommand(CheckPhotonRegionsAsync, () => !IsCheckingPhotonRegions);
            RestartBypassCommand = new AsyncRelayCommand(RestartBypassAsync, () => !IsRestartingBypass);
            FixPhotonCommand = new AsyncRelayCommand(FixPhotonAsync, () => !IsRestartingBypass);
            SetupRepoCommand = new AsyncRelayCommand(SetupRepoAsync, () => !IsRestartingBypass);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            RefreshCommand = new RelayCommand(Refresh);
            DismissMessageCommand = new RelayCommand(() => { Message = ""; MessageKey = "Info"; });

            RefreshProfiles();
            RefreshGamingStatus();
            RefreshDetectionStatus();
            if (Settings.SteamBypassEnabled) { try { SteamBypass.Ensure(Settings); } catch { } }
            try { PhotonRegions.Ensure(Settings); } catch { }
            if (_main.GameDetector != null) _main.GameDetector.GameStatusChanged += (_, __) => RefreshDetectionStatus();
        }

        private static IEnumerable<GameBypassOption> CreateOptions()
        {
            yield return new GameBypassOption(
                "discord_voice", "🎙️ Discord Voice", "Discord 50000-65535 — безопасно, не трогает игры. Рекомендуется всем.",
                new[] { "discord.com", "discordapp.com", "discord.gg" }, "discord_voice", "🎙️");
            yield return new GameBypassOption(
                "steam_cs2", "🎯 Steam / CS2 / Dota 2", "Valve Source Engine (27000-27100) — исключает из обхода, убирает loss/choke.",
                new[] { "steampowered.com", "steamcommunity.com", "steamserver.net" }, "steam_cs2", "🎯");
            yield return new GameBypassOption(
                "repo", "🧩 R.E.P.O. — фикс clienttimeout",
                "Игра ходит через Photon relay. Профиль исключает порты Photon (4530-4533, 5055-5058, 9090-9093) по TCP и UDP и Steam P2P — обход не трогает трафик игры.",
                Array.Empty<string>(), "repo", "🧩");
            yield return new GameBypassOption(
                "phasmophobia", "👻 Phasmophobia", "Steam P2P 27015-27200 + Photon (4530,5055). EU требует исключения — включи галочку и профиль «Phasmophobia».",
                new[] { "kineticgames.co.uk", "photonengine.io", "ns.photonengine.io", "exitgames.com", "exitgamescloud.com", "steamcommunity.com" }, "phasmophobia", "👻");
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
            set
            {
                if (Settings.GameModeActive == value) return;
                Settings.GameModeActive = value;
                SettingsStore.Save(Settings);
                _main.ApplyGameFilterState();
                Raise(nameof(GameModeActive));
                Raise(nameof(GameModeStatusText));
                Raise(nameof(GameModeBadge));
                Raise(nameof(DetectionStatusText));
                Raise(nameof(DetectionStatusKey));
                Raise(nameof(GameFilterIndex));
                Raise(nameof(FilterModeHint));
                Raise(nameof(RepoStateText));
                Raise(nameof(RepoStateKey));
                // Тумблер меняет режим игрового фильтра — без перезапуска winws правка не действует.
                if (value && _main.Bypass.GetStatus().IsRunning)
                    _ = RestartAfterChangeAsync("Игровой режим включён");
                else
                    Message = value
                        ? "Игровой режим включён: для игр UDP пропускается без обхода. Перезапустите обход, если он уже работает."
                        : "Игровой режим выключен. Перезапустите обход, если он уже работает.";
                MessageKey = "Info";
            }
        }
        public string GameModeStatusText => Settings.GameModeActive ? "Игровой режим ВКЛ — UDP игр идёт напрямую, без обхода" : "Игровой режим ВЫКЛ — обход работает как обычно";
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
                // Раньше здесь оставался включённым «Игровой режим» — и он молча перебивал выбор
                // при следующем применении настроек (см. MainViewModel.ApplyGameFilterState).
                if (Settings.GameModeActive)
                {
                    Settings.GameModeActive = false;
                    SettingsStore.Save(Settings);
                    Raise(nameof(GameModeActive));
                    Raise(nameof(GameModeBadge));
                    Raise(nameof(GameModeStatusText));
                }
                Raise(nameof(GameFilterIndex));
                Raise(nameof(FilterModeHint));
                Raise(nameof(RepoStateText));
                Raise(nameof(RepoStateKey));
                if (_main.Bypass.GetStatus().IsRunning) _ = RestartAfterChangeAsync("Режим фильтра изменён");
                else { Message = value == 0 ? "Игровой фильтр выключен — обход не трогает порты игр." : $"Режим фильтра: {mode}. Запустите обход, чтобы применить."; MessageKey = "Info"; }
            }
        }
        public string[] GameFilterOptions { get; } = { "Выключен (не трогать игры)", "TCP + UDP (максимум обхода)", "Только TCP", "Только UDP" };

        /// <summary>Что значит выбранный режим — рядом с выпадающим списком, без догадок.</summary>
        public string FilterModeHint
        {
            get
            {
                if (Settings.GameModeActive)
                    return "Сейчас активен «Игровой режим» со страницы выше: он сам ставит «Только TCP» и перебивает выбор в этом списке. Выключите тумблер, если хотите задать режим вручную.";
                return GameFilterIndex switch
                {
                    1 => "TCP и UDP всех портов профиля обрабатываются обходом. Для R.E.P.O. не подходит: игра ходит по UDP.",
                    2 => "Обход трогает только TCP. UDP-трафик игр (Photon, Steam P2P) идёт напрямую — для R.E.P.O. это нормальный вариант.",
                    3 => "Обход трогает только UDP, TCP не трогает. Для игр с Photon плохо: ломает игровой UDP.",
                    _ => "Обход не трогает порты игр вообще. Для R.E.P.O. это самый безопасный вариант."
                };
            }
        }

        /// <summary>Цвет подсказки режима: предупреждаем, когда выбранный режим вредит игровому UDP.</summary>
        public string FilterModeKey
        {
            get
            {
                if (Settings.GameModeActive) return "Warning";
                return GameFilterIndex switch { 1 => "Warning", 3 => "Warning", 2 => "Success", _ => "Muted" };
            }
        }

        /// <summary>Подсказка рядом с кнопкой «Применить профиль»: что произойдёт и почему кнопка может быть неактивна.</summary>
        public string ApplyProfileHint
        {
            get
            {
                if (SelectedProfile == null) return "Сначала выберите профиль";
                var status = _main.Bypass.GetStatus();
                if (status.IsRunning) return "Профиль применится сразу: обход будет перезапущен.";
                return "Обход выключен — профиль сохранится и применится при запуске обхода.";
            }
        }

        /// <summary>Перезапуск обхода после смены настройки: раньше правка сохранялась, но winws работал со старыми портами.</summary>
        private async Task RestartAfterChangeAsync(string reason)
        {
            Message = reason + " — перезапускаю обход…";
            MessageKey = "Info";
            var res = await _main.RestartBypassAsync();
            Message = res.Ok ? reason + " — " + res.Message : reason + " — " + res.Message;
            MessageKey = res.Ok ? "Success" : "Warning";
            Refresh();
        }

        // ---- Перезапуск обхода со страницы (v2.2.7) ----
        private bool _isRestartingBypass;
        public bool IsRestartingBypass
        {
            get => _isRestartingBypass;
            private set
            {
                if (Set(ref _isRestartingBypass, value))
                {
                    (RestartBypassCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (FixPhotonCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (SetupRepoCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    Raise(nameof(RestartBypassHint));
                    Raise(nameof(RestartBypassVisible));
                }
            }
        }

        /// <summary>Кнопка «Перезапустить обход» доступна только когда есть что перезапускать,
        /// а причина недоступности всегда написана рядом (требование: мёртвых кнопок нет).</summary>
        public bool RestartBypassVisible => true;
        public string RestartBypassHint
        {
            get
            {
                if (IsRestartingBypass) return "Перезапускаю обход…";
                if (!Shell.IsAdmin()) return "Нужны права администратора";
                if (!_main.Bypass.GetStatus().IsRunning) return "Обход выключен — сначала включите на «Обзоре»";
                var strat = _main.Strategies.Find(Settings.SelectedStrategy);
                if (strat == null) return "Стратегия не выбрана — откройте «Стратегии»";
                return "Применить профиль портов и списки к работающему обходу";
            }
        }

        private async Task RestartBypassAsync()
        {
            if (IsRestartingBypass) return;
            IsRestartingBypass = true;
            Message = "Перезапускаю обход с текущим профилем портов…";
            MessageKey = "Info";
            try
            {
                // Единая точка перезапуска — та же, что в выдвижной сводке.
                var res = await _main.RestartBypassAsync();
                Message = res.Message;
                MessageKey = res.Ok ? "Success" : "Danger";
            }
            finally
            {
                IsRestartingBypass = false;
                Raise(nameof(RepoStateText));
                Raise(nameof(RepoStateKey));
                Raise(nameof(PhotonCoverageText));
                Raise(nameof(PhotonCoverageKey));
            }
        }

        /// <summary>Одна кнопка «Настроить R.E.P.O.»: профиль портов, безопасный режим фильтра,
        /// исключение адресов Photon из ipset и перезапуск обхода.</summary>
        private async Task FixPhotonAsync()
        {
            IsRestartingBypass = true;
            try
            {
                var setup = await ApplyRepoSetupAsync();
                Message = $"R.E.P.O. настроен: профиль портов «R.E.P.O.»{setup.ModeNote}. {setup.PhotonNote}";
                MessageKey = setup.Ok ? "Success" : "Warning";
                RefreshProfiles();
                if (!setup.Ok) return;
                if (_main.Bypass.GetStatus().IsRunning) await RestartAfterChangeAsync("R.E.P.O. настроен");
                else Message += " Обход выключен — настройки применятся при запуске.";
            }
            finally
            {
                IsRestartingBypass = false;
                Refresh();
            }
        }

        /// <summary>Явная кнопка из карточки Photon: делает ровно то же, что галочка R.E.P.O.</summary>
        private Task SetupRepoAsync() => FixPhotonAsync();

        /// <summary>
        /// Настройка R.E.P.O. в одном месте: профиль портов «R.E.P.O.», режим фильтра без UDP
        /// (иначе порты профиля не важны — Photon всё равно попадёт под обход), Photon напрямую
        /// и его адреса в ipset-exclude. Возвращает пояснения для интерфейса.
        /// </summary>
        private Task<(bool Ok, string ModeNote, string PhotonNote)> ApplyRepoSetupAsync()
        {
            var notes = new System.Text.StringBuilder();
            var modeNote = "";

            // 1. Профиль портов
            if (Settings.GameFilterProfileId != "repo")
            {
                Settings.GameFilterProfileId = "repo";
                SettingsStore.Save(Settings);
                RefreshProfiles();
            }

            // 2. Режим фильтра: «Только UDP» для R.E.P.O. — прямой вред (обход бьёт по UDP Photon),
            //    поэтому в этом случае аккуратно переводим на «Только TCP».
            var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
            if (mode == GameFilterMode.UdpOnly)
            {
                mode = GameFilterMode.TcpOnly;
                EngineService.SetGameFilterMode(Settings.EnginePath, mode);
                modeNote = "; режим фильтра переключён с «только UDP» на «только TCP» (UDP Photon теперь не трогается)";
            }
            else if (mode == GameFilterMode.Disabled)
            {
                modeNote = "; фильтр выключен, обход вообще не трогает порты игр";
            }
            else if (mode == GameFilterMode.TcpOnly)
            {
                modeNote = "; режим «только TCP» — игровой UDP идёт напрямую";
            }
            else
            {
                modeNote = "; режим «TCP + UDP», порты Photon и Steam P2P исключены";
            }

            // «Игровой режим» сам ставит «Только TCP», но при следующем применении настроек
            // может вернуть прежний режим — поэтому для R.E.P.O. его выключаем.
            if (Settings.GameModeActive)
            {
                Settings.GameModeActive = false;
                SettingsStore.Save(Settings);
                Raise(nameof(GameModeActive));
                Raise(nameof(GameModeBadge));
                Raise(nameof(GameModeStatusText));
                notes.Append("Тумблер «Игровой режим» выключен: он перебивал режим фильтра. ");
            }

            // 3. Photon напрямую + адреса Photon в ipset-exclude
            Settings.PhotonThroughBypass = false;
            SettingsStore.Save(Settings);
            if (Settings.SteamBypassEnabled) { try { SteamBypass.Ensure(Settings); } catch { } }
            var (ok, msg) = PhotonRegions.Ensure(Settings);
            PhotonRoutingStatus = msg;
            Raise(nameof(PhotonThroughBypass));
            Raise(nameof(PhotonCoverageText));
            Raise(nameof(PhotonCoverageKey));
            Raise(nameof(GameFilterIndex));
            Raise(nameof(FilterModeHint));
            Raise(nameof(RepoStateText));
            Raise(nameof(RepoStateKey));
            return Task.FromResult((ok, modeNote, notes + msg));
        }

        // ---- R.E.P.O. / Photon (v2.2.6) ----
        /// <summary>Вести TCP-трафик Photon через обход. По умолчанию напрямую: если у провайдера
        /// режется TLS до узлов Photon, включение убирает задержку и «недоступный» регион.</summary>
        public bool PhotonThroughBypass
        {
            get => Settings.PhotonThroughBypass;
            set
            {
                if (Settings.PhotonThroughBypass == value) return;
                Settings.PhotonThroughBypass = value;
                SettingsStore.Save(Settings);
                var (ok, msg) = PhotonRegions.Ensure(Settings);
                PhotonRoutingStatus = msg;
                Raise(nameof(PhotonCoverageText));
                Raise(nameof(PhotonCoverageKey));
                Message = msg;
                MessageKey = ok ? "Info" : "Danger";
                Raise(nameof(PhotonThroughBypass));
            }
        }

        /// <summary>Попадают ли адреса Photon в ipset обхода — главная причина clienttimeout.</summary>
        public string PhotonCoverageText => PhotonRegions.DescribeCoverage(Settings);
        public string PhotonCoverageKey => PhotonRegions.BypassTouchesPhoton(Settings) ? "Danger"
            : PhotonRegions.IpRangesExcluded(Settings) ? "Success" : "Muted";

        private string _photonRoutingStatus = "";
        public string PhotonRoutingStatus { get => _photonRoutingStatus; private set => Set(ref _photonRoutingStatus, value); }

        /// <summary>Понятное состояние: трогает ли обход игровой трафик R.E.P.O. прямо сейчас.</summary>
        public string RepoStateText
        {
            get
            {
                var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                if (PhotonRegions.PortsTouchedByBypass(Settings))
                    return "⚠ Порты R.E.P.O. сейчас в обходе — нажмите «Настроить R.E.P.O.», иначе пинг и таймауты останутся.";
                if (mode is GameFilterMode.Disabled or GameFilterMode.TcpOnly)
                    return "Обход не обрабатывает UDP — трафик Photon и так идёт напрямую.";
                if (string.Equals(Settings.GameFilterProfileId, "repo", StringComparison.OrdinalIgnoreCase))
                    return "Профиль «R.E.P.O.» активен: UDP Photon и Steam P2P не трогаются.";
                var profile = GameFilterPortConfig.GetProfileById(Settings.GameFilterProfileId);
                return $"Профиль «{profile.Name}» обрабатывает UDP {profile.UdpPorts} — включите галочку R.E.P.O. выше, чтобы убрать вмешательство.";
            }
        }
        public string RepoStateKey
        {
            get
            {
                if (PhotonRegions.PortsTouchedByBypass(Settings)) return "Danger";
                var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                if (mode is GameFilterMode.Disabled or GameFilterMode.TcpOnly) return "Success";
                return string.Equals(Settings.GameFilterProfileId, "repo", StringComparison.OrdinalIgnoreCase) ? "Success" : "Warning";
            }
        }

        public ObservableCollection<PhotonRegionResult> PhotonRegionResults { get; } = new();

        private string _photonRegionHint = "Нажмите «Проверить регионы» — покажу реальное время ответа узлов Photon.";
        public string PhotonRegionHint { get => _photonRegionHint; private set => Set(ref _photonRegionHint, value); }

        private bool _isCheckingPhotonRegions;
        public bool IsCheckingPhotonRegions
        {
            get => _isCheckingPhotonRegions;
            private set { if (Set(ref _isCheckingPhotonRegions, value)) (CheckPhotonRegionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); }
        }

        public AsyncRelayCommand CheckPhotonRegionsCommand { get; }

        private async Task CheckPhotonRegionsAsync()
        {
            if (IsCheckingPhotonRegions) return;
            IsCheckingPhotonRegions = true;
            PhotonRegionHint = "Измеряю узлы Photon…";
            try
            {
                var results = await PhotonRegions.CheckAsync();
                PhotonRegionResults.Clear();
                foreach (var r in results) PhotonRegionResults.Add(r);
                PhotonRegionHint = PhotonRegions.DescribeBest(results);
                Message = PhotonRegionHint;
                MessageKey = "Info";
            }
            catch (Exception ex)
            {
                PhotonRegionHint = "Не удалось проверить регионы: " + ex.Message;
            }
            finally
            {
                IsCheckingPhotonRegions = false;
            }
        }

        // ---- Steam: магазин и сообщество (v2.2.5) ----
        /// <summary>Вести магазин/сообщество Steam через обход. В сборке Flowseal домены Steam специально
        /// исключены из общего списка (чтобы не тормозили загрузки), из-за этого страницы грузились медленно.</summary>
        public bool SteamBypassEnabled
        {
            get => Settings.SteamBypassEnabled;
            set
            {
                if (Settings.SteamBypassEnabled == value) return;
                Settings.SteamBypassEnabled = value;
                SettingsStore.Save(Settings);
                var (ok, msg) = SteamBypass.Ensure(Settings);
                SteamBypassStatus = msg;
                Message = ok
                    ? (value ? "Steam: страницы пойдут через обход. Перезапустите обход для применения."
                             : "Обход Steam выключен — списки убраны.")
                    : msg;
                MessageKey = ok ? (value ? "Success" : "Info") : "Danger";
                Raise(nameof(SteamBypassEnabled));
                Raise(nameof(SteamBypassStateText));
            }
        }

        public string SteamBypassStateText => SteamBypass.Describe(Settings);

        /// <summary>Второй рычаг для Steam: клиент ходит по HTTP/3 (QUIC), а обход подделывает QUIC-пакеты,
        /// из-за чего браузер Steam повторяет рукопожатие. Отключение fake-quic убирает эту задержку.</summary>
        public bool DisableQuicFake
        {
            get => Settings.DisableQuicFake;
            set
            {
                if (Settings.DisableQuicFake == value) return;
                Settings.DisableQuicFake = value;
                SettingsStore.Save(Settings);
                Raise(nameof(DisableQuicFake));
                Message = value
                    ? "Подделка QUIC выключена. Перезапустите обход, чтобы Steam перестал ждать HTTP/3."
                    : "Подделка QUIC включена (нужна для YouTube). Перезапустите обход.";
                MessageKey = "Info";
            }
        }
        public string SteamBypassHint => "Включает магазин и сообщество Steam в обход. Загрузки игр, картинки и видео (CDN) остаются напрямую — скорость скачивания не меняется.";

        private string _steamBypassStatus = "";
        public string SteamBypassStatus { get => _steamBypassStatus; private set { if (Set(ref _steamBypassStatus, value)) Raise(nameof(SteamBypassStatusVisible)); } }
        public bool SteamBypassStatusVisible => !string.IsNullOrWhiteSpace(SteamBypassStatus);

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
        public ICommand ApplySteamBypassCommand { get; }
        public ICommand RestartBypassCommand { get; }
        public ICommand FixPhotonCommand { get; }
        public ICommand SetupRepoCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand DismissMessageCommand { get; }

        public void Refresh()
        {
            RefreshProfiles();
            RefreshGamingStatus();
            RefreshDetectionStatus();
            Raise(nameof(GameFilterIndex));
            Raise(nameof(RepoStateText));
            Raise(nameof(RepoStateKey));
            Raise(nameof(PhotonCoverageText));
            Raise(nameof(PhotonCoverageKey));
            Raise(nameof(RestartBypassHint));
            Raise(nameof(FilterModeHint));
            Raise(nameof(FilterModeKey));
            Raise(nameof(ApplyProfileHint));
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

        /// <summary>Тумблер переключили не с этой страницы (горячая клавиша, бейдж игры, мини-оверлей) — обновляем тексты.</summary>
        public void NotifyGameModeChanged()
        {
            Raise(nameof(GameModeActive));
            Raise(nameof(GameModeBadge));
            Raise(nameof(GameModeStatusText));
            Raise(nameof(DetectionStatusText));
            Raise(nameof(DetectionStatusKey));
            Raise(nameof(GameFilterIndex));
            Raise(nameof(FilterModeHint));
            Raise(nameof(FilterModeKey));
            Raise(nameof(RepoStateText));
            Raise(nameof(RepoStateKey));
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
                // Единая точка перезапуска: учитывает службу, режим и подсказки об ошибках.
                var res = await _main.RestartBypassAsync();
                Message = $"Профиль «{SelectedProfile.Name}»: {res.Message}";
                MessageKey = res.Ok ? "Success" : "Warning";
            }
            else
            {
                Message = $"Профиль «{SelectedProfile.Name}» сохранён. Запустите обход, чтобы применить."; MessageKey = "Success";
            }
            Raise(nameof(GameModeStatusText));
            Raise(nameof(ApplyProfileHint));
            Raise(nameof(FilterModeKey));
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
                    // для Phasmophobia EU: Photon-хосты должны исключаться из DPI (иначе wss ломается), добавляем их в exclude
                    if (opt.Id == "phasmophobia")
                    {
                        try
                        {
                            var excludePath = Path.Combine(Settings.EnginePath, "lists", "list-exclude-user.txt");
                            StrategyParser.EnsureUserLists(Settings.EnginePath);
                            var exLines = File.Exists(excludePath) ? File.ReadAllLines(excludePath).Select(l => l.Trim().ToLowerInvariant()).ToHashSet() : new HashSet<string>();
                            var photonHosts = new[] { "photonengine.io", "ns.photonengine.io", "ns-eu.photonengine.io", "exitgamescloud.com", "app.exitgamescloud.com", "app-eu.exitgamescloud.com", "euw1.exitgamescloud.com", "eu.exitgamescloud.com" };
                            using var exWriter = File.AppendText(excludePath);
                            foreach (var h in photonHosts)
                                if (!exLines.Contains(h.ToLowerInvariant())) exWriter.WriteLine(h);
                            exWriter.Flush();
                        }
                        catch { }
                    }
                    opt.Status = added > 0 ? $"✅ +{added} доменов" : "уже в списке";

                    // R.E.P.O.: игровой трафик идёт по UDP Photon, домены тут не нужны — важен профиль портов.
                    if (opt.Id == "repo")
                    {
                        opt.Status = "✅ профиль R.E.P.O.";
                        // Одна галочка = весь сценарий: профиль портов, безопасный режим фильтра,
                        // исключение Photon из ipset и перезапуск обхода, если он работает.
                        var setup = await ApplyRepoSetupAsync();
                        Message = $"R.E.P.O. настроен: профиль портов «R.E.P.O.»{setup.ModeNote}. {setup.PhotonNote}";
                        MessageKey = setup.Ok ? "Success" : "Warning";
                        RefreshProfiles();
                        Raise(nameof(GameFilterIndex));
                        Raise(nameof(GameOptions));
                        Raise(nameof(RepoStateText));
                        Raise(nameof(RepoStateKey));
                        Raise(nameof(PhotonCoverageText));
                        Raise(nameof(PhotonCoverageKey));
                        if (!setup.Ok) return;
                        if (_main.Bypass.GetStatus().IsRunning) await RestartAfterChangeAsync("R.E.P.O. настроен");
                        return;
                    }
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

        /// <summary>Создаёт списки Steam и перезапускает обход, если он уже работает.</summary>
        private async Task ApplySteamBypassAsync()
        {
            var (ok, msg) = SteamBypass.Ensure(Settings);
            SteamBypassStatus = msg;
            Message = msg;
            MessageKey = ok ? "Success" : "Danger";
            if (!ok) return;

            if (_main.Bypass.GetStatus().IsRunning)
            {
                var strat = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                if (strat != null)
                {
                    var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
                    var res = _main.Bypass.GetStatus().ServiceState == ServiceState.Running
                        ? await _main.Bypass.InstallServiceAsync(strat, mode)
                        : await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
                    Message = msg + " " + res.Message;
                    MessageKey = res.Ok ? "Success" : "Warning";
                    _main.Home.RefreshStatus();
                }
            }
            else
            {
                Message = msg + " Обход выключен — списки применятся при следующем запуске.";
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

                // Профиль портов берём из последней отмеченной игры: иначе «Сохранить и перезапустить»
                // применял домены, но оставлял чужой профиль (например, Discord вместо R.E.P.O.).
                var profileOption = enabled.LastOrDefault(o => !string.IsNullOrWhiteSpace(o.ProfileId));
                if (profileOption != null && Settings.GameFilterProfileId != profileOption.ProfileId)
                {
                    Settings.GameFilterProfileId = profileOption.ProfileId;
                    SettingsStore.Save(Settings);
                    SelectedProfile = GameFilterPortConfig.GetProfileById(profileOption.ProfileId);
                }
                var routingNote = "";
                if (enabled.Any(o => o.Id == "repo"))
                {
                    var setup = await ApplyRepoSetupAsync();
                    routingNote = " " + setup.PhotonNote + setup.ModeNote;
                }
                Message = $"Готово — добавлено {totalAdded} доменов для {enabled.Count} игр. Профиль «{SelectedProfile?.Name}» активен.{routingNote} " +
                          "Перезапусти обход.";
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
