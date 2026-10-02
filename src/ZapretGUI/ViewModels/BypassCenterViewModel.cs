using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class DnsStrategyMatrixEntry : ObservableObject
    {
        private string _status = "не проверено";
        private string _statusKey = "Muted";
        public DnsStrategyMatrixEntry(string strategy, string dns, string dnsIp)
        {
            Strategy = strategy; Dns = dns; DnsIp = dnsIp;
        }
        public string Strategy { get; }
        public string Dns { get; }
        public string DnsIp { get; }
        public int Passed { get; set; }
        public int Total { get; set; } = 16;
        public double SuccessRate => Total == 0 ? 0 : Passed * 100.0 / Total;
        public long AvgMs { get; set; }
        public bool IsBest { get; set; }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string StatusKey { get => _statusKey; set => Set(ref _statusKey, value); }
        public string Summary => $"{Passed}/{Total} · {AvgMs} мс";
    }

    public sealed class BypassCenterViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private CancellationTokenSource? _matrixCts;
        private bool _isMatrixRunning;
        private double _matrixProgress;
        private double _matrixMax = 100;
        private string _matrixText = "";
        private string _matrixPercent = "0%";
        private string _matrixSummary = "Готов к проверке — нажми «Проверить всё»";
        private string _matrixBestText = "—";
        private DnsStrategyMatrixEntry? _selectedMatrixEntry;
        private int _selectedSubTab;

        public BypassCenterViewModel(MainViewModel main)
        {
            _main = main;
            _main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ExpertMode)) OnExpertModeChanged();
            };
            MatrixResults = new ObservableCollection<DnsStrategyMatrixEntry>();
            DnsProfiles = DnsManagementService.PredefinedProfiles;
            SelectedDnsProfile = DnsProfiles.FirstOrDefault(p => p.Id == "cloudflare") ?? DnsProfiles[0];

            RefreshCommand = new RelayCommand(RefreshAll);
            ApplyDnsCommand = new AsyncRelayCommand(ApplyDnsAsync);
            CheckHijackCommand = new AsyncRelayCommand(CheckHijackAsync);
            UpdateHostsCommand = new AsyncRelayCommand(UpdateHostsAsync);
            UpdateIpsetCommand = new AsyncRelayCommand(UpdateIpsetAsync);
            EnsureListsCommand = new RelayCommand(EnsureLists);
            TestSeamlessNowCommand = new AsyncRelayCommand(TestSeamlessNowAsync, () => _main.Bypass.GetStatus().IsRunning && !IsMatrixRunning);
            RunMatrixCommand = new AsyncRelayCommand(RunMatrixAsync, () => !IsMatrixRunning && Strategies.Items.Count > 0);
            CancelMatrixCommand = new RelayCommand(CancelMatrix, () => IsMatrixRunning);
            ApplyBestCommand = new AsyncRelayCommand(ApplyBestAsync, () => !IsMatrixRunning && MatrixResults.Any(r => r.Passed > 0));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            ApplyStrategyCommand = new AsyncRelayCommand(ApplySelectedStrategyAsync, () => SelectedStrategy != null && !IsMatrixRunning);
            DismissMessageCommand = new RelayCommand(() => { Message = ""; MessageKey = "Info"; });
            OpenListsCommand = new RelayCommand(() => _main.Navigate("user-lists"));
            OpenWorkbenchCommand = new RelayCommand(() => _main.Navigate("configuration"));
            OpenNetworkCommand = new RelayCommand(() => _main.Navigate("network"));
            // «Подбор» в «Простом» режиме пустовал: матрица стратегия × DNS — экспертный блок (v1.28.0).
            // Вместо пустого экрана показываем пояснение и кнопку включения режима.
            EnableExpertModeCommand = new RelayCommand(() => { if (_main.SimpleMode) _main.ToggleExpertMode(); });
            OpenSystemCheckCommand = new RelayCommand(() =>
            {
                _main.Diagnostics.OpenSystemSubTab(); // «Система» в разделе «Проверки» (в «Простом» — быстрая проверка)
                _main.Navigate("diagnostics");
            });

            RefreshAll();
            _main.Bypass.GetStatus(); // warm
            try { _main.Strategies.PropertyChanged += (_, __) => RefreshStrategies(); } catch { }
        }

        // ===== Подвкладки раздела «Обход» (v1.18.0) =====
        // Страница больше не «свалка всё в одном»: статус всегда сверху, дальше — 6 подвкладок.
        // Логика и команды те же, что были на одной странице, — менялась только раскладка.

        private static readonly string[] AllBypassTabs = { "🎯 Стратегия", "🧠 Подбор", "🌐 DNS", "📋 Списки", "🔥 Сложные сайты", "🛠 Дополнительно" };
        private static readonly string[] SimpleBypassTabs = { "🎯 Стратегия", "🧠 Подбор", "🌐 DNS", "📋 Списки", "🔥 Сложные сайты" };
        private static readonly int[] ExpertTabMap = { 0, 1, 2, 3, 4, 5 };
        private static readonly int[] SimpleTabMap = { 0, 1, 2, 3, 4 };

        /// <summary>Видимые подвкладки: в «Простом» режиме «Дополнительно» скрыта целиком (docs/IA_REDESIGN.md §7).</summary>
        public string[] BypassTabs => ExpertMode ? AllBypassTabs : SimpleBypassTabs;

        /// <summary>Индекс выбранной подвкладки в видимом списке (часть вкладок может быть скрыта режимом).</summary>
        public int VisibleSubTab
        {
            get
            {
                var index = Array.IndexOf(VisibleIndexMap, _selectedSubTab);
                return index < 0 ? 0 : index;
            }
            set
            {
                if (value >= 0 && value < VisibleIndexMap.Length) SelectedSubTab = VisibleIndexMap[value];
            }
        }

        private int[] VisibleIndexMap => ExpertMode ? ExpertTabMap : SimpleTabMap;

        public string BypassTabHintText => SelectedSubTab switch
        {
            0 => "Выбор способа обхода из каталога движка • применяется бесшовно (служба/процесс сохраняется)",
            1 => "Подбор: 4 шага (аудит → сайты → стратегии → рекомендация) и перебор каждой стратегии с каждым DNS",
            2 => "DNS-профили, применение и проверка подмены",
            3 => "Списки доменов, hosts и ipset • полный редактор — на странице «Списки»",
            4 => "Тонкая настройка под YouTube/Discord и фильтр трафика игр",
            5 => "Служба Windows, папка движка и переход к опасным операциям",
            _ => ""
        };

        public int SelectedSubTab
        {
            get => _selectedSubTab;
            set
            {
                if (Set(ref _selectedSubTab, Math.Clamp(value, 0, 5)))
                {
                    Raise(nameof(BypassTabHintText));
                    Raise(nameof(VisibleSubTab));
                    Raise(nameof(IsStrategyTabSelected));
                    Raise(nameof(IsPickTabSelected));
                    Raise(nameof(IsDnsTabSelected));
                    Raise(nameof(IsListsTabSelected));
                    Raise(nameof(IsHardTabSelected));
                    Raise(nameof(IsAdvancedTabSelected));
                }
            }
        }

        public bool IsStrategyTabSelected { get => _selectedSubTab == 0; set { if (value) SelectedSubTab = 0; } }
        public bool IsPickTabSelected { get => _selectedSubTab == 1; set { if (value) SelectedSubTab = 1; } }
        public bool IsDnsTabSelected { get => _selectedSubTab == 2; set { if (value) SelectedSubTab = 2; } }
        public bool IsListsTabSelected { get => _selectedSubTab == 3; set { if (value) SelectedSubTab = 3; } }
        public bool IsHardTabSelected { get => _selectedSubTab == 4; set { if (value) SelectedSubTab = 4; } }
        public bool IsAdvancedTabSelected { get => _selectedSubTab == 5; set { if (value) SelectedSubTab = 5; } }


        public AppSettings Settings => _main.Settings;
        public StrategyStore Strategies => _main.Strategies;

        /// <summary>Главная: оттуда в «Обход → Дополнительно» переехали управление службой и статус соединения.
        /// Логика остаётся одна — в HomeViewModel, здесь только проксирование для биндингов.</summary>
        public HomeViewModel Home => _main.Home;
        public ObservableCollection<DnsStrategyMatrixEntry> MatrixResults { get; }
        public IReadOnlyList<DnsProfile> DnsProfiles { get; }

        private DnsProfile? _selectedDnsProfile;
        public DnsProfile? SelectedDnsProfile
        {
            get => _selectedDnsProfile;
            set { if (Set(ref _selectedDnsProfile, value)) Raise(nameof(CanApplyDns)); }
        }
        public bool CanApplyDns => SelectedDnsProfile != null;

        private StrategyInfo? _selectedStrategy;
        public StrategyInfo? SelectedStrategy
        {
            get => _selectedStrategy;
            set { if (Set(ref _selectedStrategy, value)) (ApplyStrategyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); }
        }

        public string CurrentDnsText => DnsManagementService.GetCurrentDnsSummary();
        public string HostsStatusText => GetHostsStatus();
        public string IpsetStatusText => GetIpsetStatus();
        public string ListsStatusText => GetListsStatus();
        public string EngineStatusText => EngineService.IsEngineReady(Settings.EnginePath) ? $"Готов · {EngineService.ReadVersion(Settings.EnginePath)} · стратегий {Strategies.Items.Count}" : "Не готов — скачай движок";
        public string BypassStatusText => _main.Home.StatusText;
        public string BypassStatusKey => _main.Home.StatusKey;

        // YouTube heavy - прокси в Settings
        public bool DisableQuicFake { get => Settings.DisableQuicFake; set { Settings.DisableQuicFake = value; SettingsStore.Save(Settings); Raise(nameof(DisableQuicFake)); } }
        public string YoutubeSniOverride { get => Settings.YoutubeSniOverride; set { Settings.YoutubeSniOverride = (value ?? "").Trim(); SettingsStore.Save(Settings); Raise(nameof(YoutubeSniOverride)); } }
        public List<string> YoutubeSniOptions { get; } = new() { "", "google.com", "www.google.com", "googlevideo.com", "youtube.com", "yt3.ggpht.com", "cloudflare.com" };
        public bool PreferIPv4 { get => Settings.PreferIPv4ForBypass; set { Settings.PreferIPv4ForBypass = value; SettingsStore.Save(Settings); Raise(nameof(PreferIPv4)); } }
        public bool UseDohForBlocked { get => Settings.UseDohForBlockedHosts; set { Settings.UseDohForBlockedHosts = value; SettingsStore.Save(Settings); Raise(nameof(UseDohForBlocked)); } }

        // Seamless proxy
        public bool SeamlessEnabled { get => Settings.SeamlessFailoverEnabled; set { Settings.SeamlessFailoverEnabled = value; SettingsStore.Save(Settings); _main.NotifySeamlessChanged(); Raise(nameof(SeamlessEnabled)); Raise(nameof(SeamlessStatus)); } }
        public string SeamlessStatus => _main.SeamlessStatusText;
        public string SeamlessStatusKey => _main.SeamlessStatusKey;

        /// <summary>Уведомить интерфейс об изменении главного выключателя автосмены.</summary>
        public void NotifyAutoSwitchChanged() => Raise(nameof(AutoSwitchStrategyEnabled));

        /// <summary>Разрешена ли автоматическая смена стратегии (главный выключатель, v1.28.3).</summary>
        public bool AutoSwitchStrategyEnabled
        {
            get => Settings.AutoSwitchStrategyEnabled;
            set
            {
                if (Settings.AutoSwitchStrategyEnabled == value) return;
                Settings.AutoSwitchStrategyEnabled = value;
                SettingsStore.Save(Settings);
                _main.NotifySeamlessChanged();
                Raise(nameof(AutoSwitchStrategyEnabled));
            }
        }

        // Matrix
        public bool IsMatrixRunning { get => _isMatrixRunning; private set { if (Set(ref _isMatrixRunning, value)) { (RunMatrixCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (CancelMatrixCommand as RelayCommand)?.RaiseCanExecuteChanged(); (ApplyBestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (TestSeamlessNowCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } } }
        public double MatrixProgress { get => _matrixProgress; private set => Set(ref _matrixProgress, value); }
        public double MatrixMax { get => _matrixMax; private set => Set(ref _matrixMax, value); }
        public string MatrixText { get => _matrixText; private set => Set(ref _matrixText, value); }
        public string MatrixPercent { get => _matrixPercent; private set => Set(ref _matrixPercent, value); }
        public string MatrixSummary { get => _matrixSummary; private set => Set(ref _matrixSummary, value); }
        public string MatrixBestText { get => _matrixBestText; private set => Set(ref _matrixBestText, value); }
        public DnsStrategyMatrixEntry? SelectedMatrixEntry { get => _selectedMatrixEntry; set => Set(ref _selectedMatrixEntry, value); }

        public ICommand RefreshCommand { get; }

        /// <summary>Рабочий стол настройщика (v1.29.3): конфигурация целиком, история и откат.</summary>
        public ICommand OpenWorkbenchCommand { get; }

        /// <summary>Сценарий «под мою сеть» (v1.30.0): провайдер, перехват, порты, таймстемпы, DNS, IPv4/IPv6.</summary>
        public ICommand OpenNetworkCommand { get; }
        public ICommand ApplyDnsCommand { get; }
        public ICommand CheckHijackCommand { get; }
        public ICommand UpdateHostsCommand { get; }
        public ICommand UpdateIpsetCommand { get; }
        public ICommand EnsureListsCommand { get; }
        public ICommand TestSeamlessNowCommand { get; }
        public ICommand RunMatrixCommand { get; }
        public ICommand CancelMatrixCommand { get; }
        public ICommand ApplyBestCommand { get; }
        public ICommand OpenListsCommand { get; }
        public ICommand OpenSystemCheckCommand { get; }

        /// <summary>Включить режим «Эксперт» из «Подбора»: матрица стратегия × DNS скрыта в «Простом».</summary>
        public ICommand EnableExpertModeCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand ApplyStrategyCommand { get; }

        public ICommand DismissMessageCommand { get; }
        private string _message = "";
        public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(MessageVisible)); } }
        public bool MessageVisible => !string.IsNullOrWhiteSpace(Message);
        private string _messageKey = "Info";
        public string MessageKey { get => _messageKey; private set => Set(ref _messageKey, value); }

        public void RefreshAll()
        {
            RefreshStrategies();
            Raise(nameof(CurrentDnsText));
            Raise(nameof(HostsStatusText));
            Raise(nameof(IpsetStatusText));
            Raise(nameof(ListsStatusText));
            Raise(nameof(EngineStatusText));
            Raise(nameof(BypassStatusText));
            Raise(nameof(BypassStatusKey));
            Raise(nameof(SeamlessStatus));
            Raise(nameof(SeamlessStatusKey));
        }

        private void RefreshStrategies()
        {
            if (Strategies.Items.Count > 0 && SelectedStrategy == null)
                SelectedStrategy = Strategies.Items.FirstOrDefault(s => s.Name == Settings.SelectedStrategy) ?? Strategies.Items.FirstOrDefault();
            Raise(nameof(EngineStatusText));
            (RunMatrixCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private string GetHostsStatus()
        {
            try
            {
                var lines = EngineService.ReadSystemHosts();
                var has = lines.Any(l => l.Contains("raw.githubusercontent.com"));
                return has ? "hosts: актуальны (есть GitHub)" : "hosts: требуют обновления";
            }
            catch { return "hosts: ошибка чтения"; }
        }
        private string GetIpsetStatus()
        {
            try
            {
                var mode = EngineService.GetIpsetMode(Settings.EnginePath);
                var all = Path.Combine(Settings.EnginePath, "lists", "ipset-all.txt");
                var cnt = File.Exists(all) ? File.ReadAllLines(all).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")) : 0;
                return $"ipset: {mode} · {cnt} сетей · discord {File.Exists(Path.Combine(Settings.EnginePath, "lists", "ipset-discord.txt"))}";
            }
            catch { return "ipset: ошибка"; }
        }
        private string GetListsStatus()
        {
            try
            {
                var root = Settings.EnginePath;
                int g = CountLines(Path.Combine(root, "lists", "list-general.txt"));
                int y = CountLines(Path.Combine(root, "lists", "list-youtube.txt"));
                int d = CountLines(Path.Combine(root, "lists", "list-discord.txt"));
                int gu = CountLines(Path.Combine(root, "lists", "list-general-user.txt"));
                int yu = CountLines(Path.Combine(root, "lists", "list-youtube-user.txt"));
                int du = CountLines(Path.Combine(root, "lists", "list-discord-user.txt"));
                return $"Списки: general {g}/+{gu} · youtube {y}/+{yu} · discord {d}/+{du}";
            }
            catch { return "Списки: ошибка"; }
        }
        private static int CountLines(string p) => File.Exists(p) ? File.ReadAllLines(p).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")) : 0;

        private async Task ApplyDnsAsync()
        {
            if (SelectedDnsProfile == null) return;
            var (ok, msg) = await DnsManagementService.ApplyDnsProfileAsync(SelectedDnsProfile);
            Message = msg; MessageKey = ok ? "Success" : "Danger";
            Raise(nameof(CurrentDnsText));
            AppLog.Info($"[Центр] DNS → {SelectedDnsProfile.Name}: {msg}");
        }
        private async Task CheckHijackAsync()
        {
            Message = "Проверяю подмену DNS…"; MessageKey = "Info";
            var rep = await DnsManagementService.CheckHijackAsync();
            Message = rep.Summary + " · " + string.Join(" | ", rep.Entries.Select(e => $"{e.Domain} {e.StatusText}"));
            MessageKey = rep.StatusKey;
        }
        private async Task UpdateHostsAsync()
        {
            try
            {
                var check = await EngineService.CheckHostsAsync();
                if (!check.NeedsUpdate && !string.IsNullOrWhiteSpace(check.Message) && check.Message.Contains("актуален"))
                {
                    Message = "hosts уже актуален"; MessageKey = "Success";
                    return;
                }
                if (!string.IsNullOrWhiteSpace(check.TempFile) && File.Exists(check.TempFile))
                {
                    var (ok, msg) = EngineService.ApplyHosts(check.TempFile);
                    Message = msg; MessageKey = ok ? "Success" : "Danger";
                    Raise(nameof(HostsStatusText));
                }
                else { Message = check.Message; MessageKey = check.Ok ? "Success" : "Warning"; }
            }
            catch (Exception ex) { Message = "hosts ошибка: " + ex.Message; MessageKey = "Danger"; }
        }
        private async Task UpdateIpsetAsync()
        {
            try
            {
                Message = "Обновляю ipset-all.txt…"; MessageKey = "Info";
                var ok = await EngineService.UpdateIpsetAsync(Settings.EnginePath);
                Message = ok ? "ipset обновлён" : "Не удалось обновить ipset"; MessageKey = ok ? "Success" : "Danger";
                Raise(nameof(IpsetStatusText));
            }
            catch (Exception ex) { Message = ex.Message; MessageKey = "Danger"; }
        }
        private void EnsureLists()
        {
            try { StrategyParser.EnsureUserLists(Settings.EnginePath); DomainListUpdater.EnsureSeeded(Settings.EnginePath); Message = "Списки проверены и дозаполнены"; MessageKey = "Success"; Raise(nameof(ListsStatusText)); }
            catch (Exception ex) { Message = ex.Message; MessageKey = "Danger"; }
        }
        private async Task TestSeamlessNowAsync()
        {
            await _main.SeamlessFailover.CheckNowAsync();
            Raise(nameof(SeamlessStatus));
            Message = _main.SeamlessStatusText; MessageKey = _main.SeamlessStatusKey;
        }
        private async Task ApplySelectedStrategyAsync()
        {
            if (SelectedStrategy == null) return;
            var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
            var status = _main.Bypass.GetStatus();
            System.Threading.Tasks.Task<OperationResult> t;
            if (status.ServiceState == ServiceState.Running) t = _main.Bypass.InstallServiceAsync(SelectedStrategy, mode);
            else t = _main.Bypass.SwitchToStrategyAsync(SelectedStrategy, mode, Settings.ShowWinwsConsole);
            var r = await t;
            Message = r.Message; MessageKey = r.Ok ? "Success" : "Danger";
            _main.Home.RefreshStatus();
            RefreshAll();
        }

        private void CancelMatrix() => _matrixCts?.Cancel();

        /// <summary>
        /// Исчерпывающая проверка: каждая стратегия × каждый DNS профиль (по умолчанию Cloudflare, Quad9, Google, DHCP) = 23×4 ~92 теста.
        /// Для каждой комбинации: применяет DNS, запускает стратегию, гоняет 16 эндпоинтов, считает Passed/Total и пинг. DNS и обход восстанавливаются.
        /// </summary>
        private async Task RunMatrixAsync()
        {
            if (IsMatrixRunning) return;
            var dnsPool = new[] { "cloudflare", "quad9", "google", "dhcp" }
                .Select(id => DnsProfiles.FirstOrDefault(p => p.Id == id)).Where(p => p != null).Cast<DnsProfile>().ToList();
            // Можно также добавить текущую системную, но pool покрывает основное
            var strategies = Strategies.Items.ToList();
            if (strategies.Count == 0) { Message = "Нет стратегий"; MessageKey = "Warning"; return; }

            _matrixCts = new CancellationTokenSource();
            var ct = _matrixCts.Token;
            IsMatrixRunning = true;
            MatrixResults.Clear();
            var total = strategies.Count * dnsPool.Count;
            MatrixMax = total;
            MatrixProgress = 0;
            MatrixPercent = "0%";
            MatrixText = $"Запуск матрицы {strategies.Count} стратегий × {dnsPool.Count} DNS = {total} тестов…";
            MatrixSummary = "Подготовка — сохраняю исходный DNS и состояние обхода…";
            var bestEntry = (DnsStrategyMatrixEntry?)null;
            var originalDns = DnsManagementService.GetSystemDnsServers();
            var originalDnsProfile = DnsProfiles.FirstOrDefault(p => originalDns.Contains(p.PrimaryServer)) ?? DnsProfiles.First(p => p.Id == "dhcp");
            var beforeStatus = _main.Bypass.GetStatus();
            var beforeStrategy = beforeStatus.StrategyName;

            int done = 0;
            try
            {
                foreach (var dns in dnsPool)
                {
                    // Применяем DNS один раз на всю группу стратегий с этим DNS
                    MatrixText = $"Переключаю DNS → {dns.Name} ({dns.PrimaryServer})";
                    var (dnsOk, dnsMsg) = await DnsManagementService.ApplyDnsProfileAsync(dns);
                    AppLog.Info($"[Матрица] DNS {dns.Name}: {dnsMsg}");
                    if (!dnsOk && dns.Id != "dhcp") AppLog.Warn($"[Матрица] не удалось {dns.Name}: {dnsMsg}");
                    await Task.Delay(1200, ct); // дать сети переключиться + flush уже внутри

                    foreach (var strat in strategies)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = new DnsStrategyMatrixEntry(strat.Name, dns.Name, dns.PrimaryServer);
                        entry.Status = "тестирую…"; entry.StatusKey = "Warning";
                        // UI должен быть в Dispatcher
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() => MatrixResults.Add(entry));
                        if (System.Windows.Application.Current?.Dispatcher == null) MatrixResults.Add(entry);

                        MatrixText = $"[{done + 1}/{total}] {strat.Name} × {dns.Name}";
                        MatrixSummary = $"Тестирую {strat.Name} с DNS {dns.Name}…";
                        MatrixProgress = done;
                        MatrixPercent = $"{done * 100 / Math.Max(1, total)}%";

                        var res = await _main.Bypass.TestStrategyAsync(strat, ct);
                        // TestStrategyAsync делает Stop/Start с сохранением, DNS остаётся как есть
                        entry.Passed = res.PassedCount;
                        entry.Total = res.Checks.Count;
                        entry.AvgMs = (long)res.AverageLatencyMs;
                        if (res.IsSuitable && res.PassedCount >= 13) { entry.Status = $"✅ {res.PassedCount}/{res.Checks.Count}"; entry.StatusKey = "Success"; }
                        else if (res.PassedCount >= 8) { entry.Status = $"🟡 {res.PassedCount}/{res.Checks.Count}"; entry.StatusKey = "Warning"; }
                        else { entry.Status = $"❌ {res.PassedCount}/{res.Checks.Count}"; entry.StatusKey = "Danger"; }

                        if (bestEntry == null || entry.Passed > bestEntry.Passed || (entry.Passed == bestEntry.Passed && entry.AvgMs > 0 && entry.AvgMs < bestEntry.AvgMs))
                        {
                            if (bestEntry != null) bestEntry.IsBest = false;
                            entry.IsBest = true;
                            bestEntry = entry;
                            MatrixBestText = $"🏆 {entry.Strategy} × {entry.Dns} — {entry.Passed}/{entry.Total} · {entry.AvgMs} мс";
                        }

                        done++;
                        MatrixProgress = done;
                        MatrixPercent = $"{done * 100 / Math.Max(1, total)}%";
                        // небольшая пауза чтобы не DDoS-ить winws
                        await Task.Delay(400, ct);
                    }
                }

                MatrixSummary = bestEntry != null
                    ? $"Готово — лучший: {bestEntry.Strategy} × {bestEntry.Dns} ({bestEntry.Passed}/{bestEntry.Total})"
                    : "Готово — рабочих комбинаций не нашлось, проверь сеть";
                MatrixText = bestEntry != null ? $"Победитель: {bestEntry.Strategy} с {bestEntry.Dns}" : "Матрица завершена";
                Message = MatrixSummary; MessageKey = bestEntry != null ? "Success" : "Warning";
            }
            catch (OperationCanceledException) { MatrixSummary = "Матрица отменена"; Message = MatrixSummary; MessageKey = "Warning"; }
            catch (Exception ex) { MatrixSummary = "Ошибка матрицы: " + ex.Message; MessageKey = "Danger"; AppLog.Error("[Матрица] " + ex.Message); }
            finally
            {
                // Восстанавливаем DNS и обход
                try
                {
                    MatrixText = "Восстанавливаю исходный DNS и обход…";
                    await DnsManagementService.ApplyDnsProfileAsync(originalDnsProfile);
                    await Task.Delay(800);
                    if (beforeStatus.IsRunning && !string.IsNullOrWhiteSpace(beforeStrategy))
                    {
                        var prev = Strategies.Find(beforeStrategy);
                        if (prev != null)
                        {
                            if (beforeStatus.State == BypassState.RunningService) await _main.Bypass.InstallServiceAsync(prev, EngineService.GetGameFilterMode(Settings.EnginePath));
                            else await _main.Bypass.StartAsync(prev, EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
                        }
                    }
                    else if (beforeStatus.IsRunning) { /* оставили как есть */ }
                    else { await _main.Bypass.StopAsync(); }
                    _main.Home.RefreshStatus();
                }
                catch (Exception ex) { AppLog.Warn("[Матрица] восстановление: " + ex.Message); }
                IsMatrixRunning = false;
                MatrixProgress = done;
                MatrixPercent = $"{done * 100 / Math.Max(1, total)}%";
                Raise(nameof(CurrentDnsText));
                Raise(nameof(HostsStatusText));
                (ApplyBestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private async Task ApplyBestAsync()
        {
            var best = MatrixResults.Where(r => r.IsBest).OrderByDescending(r => r.Passed).ThenBy(r => r.AvgMs).FirstOrDefault()
                       ?? MatrixResults.OrderByDescending(r => r.Passed).ThenBy(r => r.AvgMs).FirstOrDefault();
            if (best == null) return;
            var dns = DnsProfiles.FirstOrDefault(p => p.Name == best.Dns) ?? DnsProfiles[0];
            var strat = Strategies.Find(best.Strategy);
            if (strat == null) { Message = "Стратегия не найдена"; MessageKey = "Danger"; return; }

            Message = $"Применяю лучшее: {best.Strategy} × {best.Dns}…"; MessageKey = "Info";
            var (dnsOk, dnsMsg) = await DnsManagementService.ApplyDnsProfileAsync(dns);
            AppLog.Info($"[Центр] Применяю DNS {dns.Name}: {dnsMsg}");
            var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
            var res = await _main.Bypass.SwitchToStrategyAsync(strat, mode, Settings.ShowWinwsConsole);
            // Если была служба — переустановим
            if (_main.Bypass.GetStatus().ServiceState != ServiceState.NotInstalled)
                res = await _main.Bypass.InstallServiceAsync(strat, mode);
            Message = res.Ok ? $"✅ Применено {best.Strategy} × {best.Dns}: {res.Message}" : res.Message;
            MessageKey = res.Ok ? "Success" : "Danger";
            _main.Home.RefreshStatus();
            Raise(nameof(CurrentDnsText));
            Raise(nameof(BypassStatusText));
        }

        /// <summary>Экспертный режим интерфейса (этап 5): технические блоки видны только в нём.</summary>
        public bool ExpertMode => _main.ExpertMode;

        private void OnExpertModeChanged()
        {
            Raise(nameof(ExpertMode));
            Raise(nameof(BypassTabs));
            Raise(nameof(VisibleSubTab));
            if (!VisibleIndexMap.Contains(_selectedSubTab)) SelectedSubTab = 0;
        }
    }
}
