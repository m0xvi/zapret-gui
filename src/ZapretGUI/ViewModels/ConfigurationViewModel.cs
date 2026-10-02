using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    /// <summary>Строка сводки «что применено сейчас» — шаг порядка настройки (docs/SETUP_ORDER.md).</summary>
    public sealed class ConfigSummaryRow
    {
        public int Step { get; init; }
        public string Title { get; init; } = "";
        public string Value { get; init; } = "";
        public string Key { get; init; } = "Info";
        public string Section { get; init; } = "";
        public string StepText => $"{Step}";
    }

    /// <summary>Вариант режима ipset для рабочего стола.</summary>
    public sealed class IpsetOption
    {
        public IpsetMode Mode { get; init; }
        public string Title { get; init; } = "";
        public string Hint { get; init; } = "";
    }

    /// <summary>Вариант DNS для рабочего стола: первый — «не менять».</summary>
    public sealed class DnsOption
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public DnsProfile? Profile { get; init; }
    }

    /// <summary>
    /// Рабочий стол настройщика (v1.29.3): в «Эксперте» на одном экране — что применено сейчас
    /// (в порядке настройки), возможность поправить и применить, история снимков и откат.
    /// Ничего не дублирует: перебор стратегий, SNI-пул и ipset-списки остаются в своих разделах,
    /// рабочий стол их показывает и связывает.
    /// </summary>
    public sealed class ConfigurationViewModel : ObservableObject
    {
        private readonly MainViewModel _main;

        public ConfigurationViewModel(MainViewModel main)
        {
            _main = main;

            ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy);
            RevertCommand = new AsyncRelayCommand(() => RevertAsync(null), () => !IsBusy && History.Count > 0);
            ExportCommand = new RelayCommand(ExportSnapshot, () => !IsBusy);
            RestoreSnapshotCommand = new AsyncRelayCommand(RestoreSnapshotAsync, () => !IsBusy && SelectedSnapshot != null);
            RefreshCommand = new RelayCommand(ReloadAll);
            OpenSectionCommand = new RelayCommand(parameter => OpenSection(parameter as string ?? ""));
            SaveAsPresetCommand = new RelayCommand(SaveAsPreset, () => !IsBusy);
            ApplyPresetCommand = new AsyncRelayCommand(ApplySelectedPresetAsync, () => !IsBusy && SelectedPreset != null);
            DeletePresetCommand = new RelayCommand(DeleteSelectedPreset, () => !IsBusy && SelectedPreset != null);
            ExportPresetCommand = new RelayCommand(ExportSelectedPreset, () => !IsBusy && SelectedPreset != null);
            ExportAllPresetsCommand = new RelayCommand(ExportAllPresets, () => !IsBusy && Presets.Count > 0);
            ImportPresetsCommand = new RelayCommand(ImportPresets, () => !IsBusy);
            RevertToWorkingCommand = new AsyncRelayCommand(RevertToWorkingAsync, () => !IsBusy && History.Any(h => h.BypassWasRunning));
            OpenProfilesCommand = new RelayCommand(() => _main.Navigate("profiles"));
            EnableExpertModeCommand = new RelayCommand(() => { if (_main.SimpleMode) _main.ToggleExpertMode(); });
            OpenStrategiesCommand = new RelayCommand(() => _main.Navigate("strategies"));
            OpenNetworkCommand = new RelayCommand(() => _main.Navigate("network"));
            OpenSniPoolCommand = new RelayCommand(() => _main.Navigate("strategies"));

            foreach (var profile in GameFilterPortConfig.PredefinedProfiles) GameFilterProfiles.Add(profile);

            IpsetModes.Add(new IpsetOption { Mode = IpsetMode.Loaded, Title = "loaded — обычный (список сетей)", Hint = "Обход работает по списку ipset-all.txt. Рекомендуется." });
            IpsetModes.Add(new IpsetOption { Mode = IpsetMode.None, Title = "none — ipset выключен", Hint = "Обрабатывается только указанный доменами трафик; сети не перехватываются." });
            IpsetModes.Add(new IpsetOption { Mode = IpsetMode.Any, Title = "any — перехватывать все IP", Hint = "Самый широкий режим: перехват всех адресов, включая служебные. Может ломать локальную сеть." });

            _main.PropertyChanged += OnMainPropertyChanged;
            ReloadAll();
        }

        // ------------------------------------------------------------------ Сводка

        public ObservableCollection<ConfigSummaryRow> SummaryRows { get; } = new();

        // ------------------------------------------------------------------ Правка

        public ObservableCollection<StrategyInfo> StrategyOptions => _main.Strategies.Items;

        private StrategyInfo? _selectedStrategy;
        public StrategyInfo? SelectedStrategy
        {
            get => _selectedStrategy;
            set { if (Set(ref _selectedStrategy, value)) RaiseChangeSummary(); }
        }

        public ObservableCollection<GameFilterProfile> GameFilterProfiles { get; } = new();

        private GameFilterProfile? _selectedGameFilter;
        public GameFilterProfile? SelectedGameFilter
        {
            get => _selectedGameFilter;
            set { if (Set(ref _selectedGameFilter, value)) RaiseChangeSummary(); }
        }

        private string _tcpPorts = "";
        public string TcpPorts { get => _tcpPorts; set { if (Set(ref _tcpPorts, value)) RaiseChangeSummary(); } }

        private string _udpPorts = "";
        public string UdpPorts { get => _udpPorts; set { if (Set(ref _udpPorts, value)) RaiseChangeSummary(); } }

        private string _excludedPorts = "";
        public string ExcludedPorts { get => _excludedPorts; set { if (Set(ref _excludedPorts, value)) RaiseChangeSummary(); } }

        public ObservableCollection<string> SniOptions { get; } = new();

        private string _selectedSni = "";
        public string SelectedSni { get => _selectedSni; set { if (Set(ref _selectedSni, value)) RaiseChangeSummary(); } }

        private string _youtubeSniOverride = "";
        public string YoutubeSniOverride { get => _youtubeSniOverride; set { if (Set(ref _youtubeSniOverride, value)) RaiseChangeSummary(); } }

        private bool _autoSniRotation;
        public bool AutoSniRotation { get => _autoSniRotation; set { if (Set(ref _autoSniRotation, value)) RaiseChangeSummary(); } }

        public ObservableCollection<IpsetOption> IpsetModes { get; } = new();

        private IpsetOption? _selectedIpset;
        public IpsetOption? SelectedIpset { get => _selectedIpset; set { if (Set(ref _selectedIpset, value)) RaiseChangeSummary(); } }

        public ObservableCollection<DnsOption> DnsOptions { get; } = new();

        private DnsOption? _selectedDns;
        public DnsOption? SelectedDns { get => _selectedDns; set { if (Set(ref _selectedDns, value)) RaiseChangeSummary(); } }

        private bool _disableQuicFake;
        public bool DisableQuicFake { get => _disableQuicFake; set { if (Set(ref _disableQuicFake, value)) RaiseChangeSummary(); } }

        private bool _preferIPv4;
        public bool PreferIPv4 { get => _preferIPv4; set { if (Set(ref _preferIPv4, value)) RaiseChangeSummary(); } }

        private bool _useDoh;
        public bool UseDoh { get => _useDoh; set { if (Set(ref _useDoh, value)) RaiseChangeSummary(); } }

        // ------------------------------------------------------------------ История

        public ObservableCollection<ConfigurationSnapshot> History { get; } = new();

        private ConfigurationSnapshot? _selectedSnapshot;
        public ConfigurationSnapshot? SelectedSnapshot
        {
            get => _selectedSnapshot;
            set
            {
                if (!Set(ref _selectedSnapshot, value)) return;
                (RestoreSnapshotCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool HasHistory => History.Count > 0;

        // ------------------------------------------------------------------ Состояние

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                Raise(nameof(IsIdle));
                (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RevertCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RestoreSnapshotCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsIdle => !IsBusy;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set { if (Set(ref _statusText, value)) Raise(nameof(HasStatus)); }
        }

        public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

        private string _statusKey = "Info";
        public string StatusKey { get => _statusKey; private set => Set(ref _statusKey, value); }

        public bool IsSimpleMode => _main.SimpleMode;
        public bool IsExpertMode => _main.ExpertMode;

        /// <summary>Одной строкой: что изменится, если нажать «Применить».</summary>
        private string _changeSummary = "";
        public string ChangeSummary { get => _changeSummary; private set => Set(ref _changeSummary, value); }

        /// <summary>Режим ipset, прочитанный из движка при загрузке (не читаем файл на каждое изменение поля).</summary>
        private IpsetMode _currentIpsetMode = IpsetMode.Loaded;

        public string IpsetStatusText { get; private set; } = "";
        public string DnsStatusText { get; private set; } = "";
        public string StrategyStatusText { get; private set; } = "";

        // ------------------------------------------------------------------ Команды

        /// <summary>Пресеты конфигурации: полный набор параметров, в отличие от профилей (стратегия + DNS + сеть).</summary>
        public ObservableCollection<ConfigurationPreset> Presets { get; } = new();

        private ConfigurationPreset? _selectedPreset;
        public ConfigurationPreset? SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (!Set(ref _selectedPreset, value)) return;
                (ApplyPresetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DeletePresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExportPresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool HasPresets => Presets.Count > 0;

        /// <summary>Имя текущей сети — чтобы было видно, для какой сети делается пресет.</summary>
        public string CurrentNetworkName { get; private set; } = "";

        public ICommand SaveAsPresetCommand { get; }
        public ICommand ApplyPresetCommand { get; }
        public ICommand DeletePresetCommand { get; }
        public ICommand ExportPresetCommand { get; }
        public ICommand ExportAllPresetsCommand { get; }
        public ICommand ImportPresetsCommand { get; }
        public ICommand RevertToWorkingCommand { get; }
        public ICommand OpenProfilesCommand { get; }

        public ICommand ApplyCommand { get; }
        public ICommand RevertCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand RestoreSnapshotCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenSectionCommand { get; }
        public ICommand EnableExpertModeCommand { get; }
        public ICommand OpenStrategiesCommand { get; }

        /// <summary>Переход к сценарию «под мою сеть» (v1.30.0).</summary>
        public ICommand OpenNetworkCommand { get; }
        public ICommand OpenSniPoolCommand { get; }

        // ------------------------------------------------------------------ Загрузка

        public void ReloadAll()
        {
            ReloadFromSettings();
            ReloadHistory();
            ReloadPresets();
            ReloadNetworkName();
            BuildSummary();
        }

        private void ReloadPresets()
        {
            Presets.Clear();
            foreach (var preset in ConfigurationPresetStore.Load()) Presets.Add(preset);
            SelectedPreset = Presets.FirstOrDefault();
            Raise(nameof(HasPresets));
            (ExportAllPresetsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void ReloadNetworkName()
        {
            try
            {
                var identity = NetworkDetector.GetCurrentIdentity();
                CurrentNetworkName = identity.IsValid ? identity.DisplayName : "сеть не определена";
            }
            catch { CurrentNetworkName = "сеть не определена"; }
            Raise(nameof(CurrentNetworkName));
        }

        private void ReloadFromSettings()
        {
            var settings = _main.Settings;

            var strategies = _main.Strategies.Items;
            SelectedStrategy = strategies.FirstOrDefault(s => s.Name.Equals(settings.SelectedStrategy, StringComparison.OrdinalIgnoreCase))
                               ?? strategies.FirstOrDefault(s => s.IsRecommended)
                               ?? strategies.FirstOrDefault();

            SelectedGameFilter = GameFilterProfiles.FirstOrDefault(p => p.Id == settings.GameFilterProfileId) ?? GameFilterProfiles.FirstOrDefault();
            TcpPorts = settings.CustomGameFilterTcpPorts;
            UdpPorts = settings.CustomGameFilterUdpPorts;
            ExcludedPorts = settings.CustomExcludedPorts;

            var sni = new List<string>();
            foreach (var s in new[] { "www.google.com", "www.microsoft.com", "www.cloudflare.com", "yandex.ru", "gosuslugi.ru" })
                if (!sni.Contains(s)) sni.Add(s);
            foreach (var s in settings.CustomSniList.Where(s => !string.IsNullOrWhiteSpace(s)))
                if (!sni.Contains(s)) sni.Add(s);
            if (!string.IsNullOrWhiteSpace(settings.SelectedFakeSni) && !sni.Contains(settings.SelectedFakeSni)) sni.Add(settings.SelectedFakeSni);
            SniOptions.Clear();
            foreach (var s in sni) SniOptions.Add(s);
            SelectedSni = settings.SelectedFakeSni;
            YoutubeSniOverride = settings.YoutubeSniOverride;
            AutoSniRotation = settings.AutoSniRotationEnabled;

            _currentIpsetMode = EngineService.GetIpsetMode(settings.EnginePath);
            var ipset = _currentIpsetMode;
            SelectedIpset = IpsetModes.FirstOrDefault(i => i.Mode == ipset) ?? IpsetModes[0];

            DnsOptions.Clear();
            DnsOptions.Add(new DnsOption { Id = "", Title = "(не менять) — оставить DNS как есть" });
            foreach (var p in DnsManagementService.PredefinedProfiles)
                DnsOptions.Add(new DnsOption { Id = p.Id, Title = p.Name, Profile = p });
            foreach (var p in settings.CustomDnsProfiles)
                DnsOptions.Add(new DnsOption { Id = p.Id, Title = p.Name + " (свой)", Profile = p });
            SelectedDns = DnsOptions[0];

            DisableQuicFake = settings.DisableQuicFake;
            PreferIPv4 = settings.PreferIPv4ForBypass;
            UseDoh = settings.UseDohForBlockedHosts;

            IpsetStatusText = ipset switch
            {
                IpsetMode.None => "ipset: none — перехват только по доменам",
                IpsetMode.Any => "ipset: any — перехват всех адресов",
                _ => "ipset: loaded — перехват по списку сетей"
            };
            DnsStatusText = DnsManagementService.GetCurrentDnsSummary();
            var status = _main.Bypass.GetStatus();
            StrategyStatusText = status.IsRunning
                ? $"обход запущен · {(string.IsNullOrWhiteSpace(status.ServiceStrategy) ? status.StrategyName : status.ServiceStrategy)}"
                : "обход не запущен";

            Raise(nameof(IpsetStatusText));
            Raise(nameof(DnsStatusText));
            Raise(nameof(StrategyStatusText));
            RaiseChangeSummary();
        }

        private void ReloadHistory()
        {
            History.Clear();
            foreach (var item in ConfigurationSnapshotStore.Load()) History.Add(item);
            SelectedSnapshot = History.FirstOrDefault();
            Raise(nameof(HasHistory));
            (RevertCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RestoreSnapshotCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RevertToWorkingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void BuildSummary()
        {
            var settings = _main.Settings;
            var status = _main.Bypass.GetStatus();
            var engineReady = EngineService.IsEngineReady(settings.EnginePath);
            var ipset = EngineService.GetIpsetMode(settings.EnginePath);

            SummaryRows.Clear();
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 1,
                Title = "Права и система",
                Value = Shell.IsAdmin() ? "администратор — можно всё" : "не администратор — служба и драйвер потребуют перезапуска",
                Key = Shell.IsAdmin() ? "Success" : "Warning",
                Section = "checks"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 2,
                Title = "Движок",
                Value = engineReady ? "готов: bin, lists и .bat на месте" : "не готов — установите движок",
                Key = engineReady ? "Success" : "Danger",
                Section = "updates"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 3,
                Title = "Цели проверки",
                Value = $"{Math.Max(settings.MonitorTargets.Count, 1)} целей · свои сайты — в «Списках»",
                Key = "Info",
                Section = "checks"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 4,
                Title = "Способ обхода",
                Value = status.IsRunning
                    ? $"применён «{(string.IsNullOrWhiteSpace(status.ServiceStrategy) ? status.StrategyName : status.ServiceStrategy)}» · обход работает"
                    : (string.IsNullOrWhiteSpace(settings.SelectedStrategy) ? "не выбран" : $"выбран «{settings.SelectedStrategy}», обход не запущен"),
                Key = status.IsRunning ? "Success" : "Warning",
                Section = "strategies"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 5,
                Title = "Перехват: ipset, порты, игры",
                Value = $"ipset {ipset.ToString().ToLowerInvariant()} · {GameFilterProfiles.FirstOrDefault(p => p.Id == settings.GameFilterProfileId)?.Name ?? "игровой фильтр не задан"}",
                Key = ipset == IpsetMode.Any ? "Warning" : "Info",
                Section = "network"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 6,
                Title = "DNS и hosts",
                Value = DnsManagementService.GetCurrentDnsSummary(),
                Key = "Info",
                Section = "network"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 7,
                Title = "Поведение без вас",
                Value = BuildBehaviourText(),
                Key = "Info",
                Section = "automation"
            });
            SummaryRows.Add(new ConfigSummaryRow
            {
                Step = 8,
                Title = "Проверка и фиксация",
                Value = HasHistory ? $"есть откат ({History.Count} снимков) — можно экспериментировать" : "отката пока нет: нажмите «Применить», чтобы создать снимок",
                Key = HasHistory ? "Success" : "Info",
                Section = "diagnostics"
            });
        }

        private string BuildBehaviourText()
        {
            var parts = new List<string>();
            parts.Add(_main.Settings.WatchdogEnabled ? "Watchdog включён" : "Watchdog выключен");
            parts.Add(_main.Settings.AutoSwitchStrategyEnabled ? "автосмена стратегии включена" : "автосмена стратегии выключена");
            parts.Add(_main.Settings.ScheduleEnabled ? "расписание включено" : "расписания нет");
            return string.Join(" · ", parts);
        }

        private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ExpertMode))
            {
                Raise(nameof(IsSimpleMode));
                Raise(nameof(IsExpertMode));
            }
        }

        private void RaiseChangeSummary()
        {
            var settings = _main.Settings;
            var changes = new List<string>();
            if (SelectedStrategy != null && !SelectedStrategy.Name.Equals(settings.SelectedStrategy, StringComparison.OrdinalIgnoreCase))
                changes.Add("способ обхода → " + SelectedStrategy.Name);
            if (SelectedGameFilter != null && SelectedGameFilter.Id != settings.GameFilterProfileId)
                changes.Add("игровой фильтр → " + SelectedGameFilter.Name);
            if ((SelectedIpset?.Mode ?? IpsetMode.Loaded) != _currentIpsetMode)
                changes.Add("ipset → " + (SelectedIpset?.Mode.ToString().ToLowerInvariant() ?? "loaded"));
            if (SelectedDns != null && !string.IsNullOrWhiteSpace(SelectedDns.Id)) changes.Add("DNS → " + SelectedDns.Title);
            if (!string.Equals(SelectedSni, settings.SelectedFakeSni, StringComparison.Ordinal)) changes.Add("SNI → " + SelectedSni);
            if (DisableQuicFake != settings.DisableQuicFake) changes.Add(DisableQuicFake ? "QUIC fake выключить" : "QUIC fake включить");
            if (PreferIPv4 != settings.PreferIPv4ForBypass) changes.Add(PreferIPv4 ? "приоритет IPv4 включить" : "приоритет IPv4 выключить");
            if (UseDoh != settings.UseDohForBlockedHosts) changes.Add(UseDoh ? "DoH включить" : "DoH выключить");
            if ((AutoSniRotation != settings.AutoSniRotationEnabled)) changes.Add(AutoSniRotation ? "ротацию SNI включить" : "ротацию SNI выключить");
            if (TcpPorts != settings.CustomGameFilterTcpPorts || UdpPorts != settings.CustomGameFilterUdpPorts || ExcludedPorts != settings.CustomExcludedPorts)
                changes.Add("порты игрового фильтра");

            ChangeSummary = changes.Count == 0
                ? "Изменений нет — текущая конфигурация уже применена."
                : "Будет применено: " + string.Join(", ", changes) + ".";
        }

        // ------------------------------------------------------------------ Применение

        /// <summary>Снимок текущего состояния — его кладём в историю перед любым изменением.</summary>
        private ConfigurationSnapshot SnapshotNow(string note) => ConfigurationSnapshotStore.Capture(
            _main.Settings,
            EngineService.GetIpsetMode(_main.Settings.EnginePath).ToString().ToLowerInvariant(),
            SelectedDns?.Id ?? "",
            note,
            _main.Bypass.GetStatus().IsRunning);

        private async Task ApplyAsync()
        {
            if (IsBusy) return;
            // Снимок «как было» — именно он станет точкой отката.
            var before = SnapshotNow("перед применением");

            IsBusy = true;
            StatusText = "Применяю конфигурацию…";
            StatusKey = "Info";
            try
            {
                var settings = _main.Settings;
                if (SelectedStrategy != null) settings.SelectedStrategy = SelectedStrategy.Name;
                if (SelectedGameFilter != null) settings.GameFilterProfileId = SelectedGameFilter.Id;
                settings.CustomGameFilterTcpPorts = TcpPorts;
                settings.CustomGameFilterUdpPorts = UdpPorts;
                settings.CustomExcludedPorts = ExcludedPorts;
                settings.SelectedFakeSni = SelectedSni;
                settings.YoutubeSniOverride = YoutubeSniOverride;
                settings.AutoSniRotationEnabled = AutoSniRotation;
                settings.DisableQuicFake = DisableQuicFake;
                settings.PreferIPv4ForBypass = PreferIPv4;
                settings.UseDohForBlockedHosts = UseDoh;
                SettingsStore.Save(settings);

                var notes = new List<string>();
                if (SelectedIpset != null && EngineService.SetIpsetMode(settings.EnginePath, SelectedIpset.Mode))
                {
                    _currentIpsetMode = SelectedIpset.Mode;
                    notes.Add("ipset " + SelectedIpset.Mode.ToString().ToLowerInvariant());
                }

                var applied = await ApplyToEngineAsync(notes);
                var dnsApplied = await ApplyDnsAsync(notes);

                var ok = applied.Ok && dnsApplied.Ok;
                ConfigurationSnapshotStore.Push(before);
                ReloadAll();

                StatusText = ok
                    ? "Готово: " + (notes.Count > 0 ? string.Join(", ", notes) + ". " : "") + "Точка отката сохранена."
                    : "Часть изменений не применилась: " + applied.Message + (dnsApplied.Message.Length > 0 ? " · " + dnsApplied.Message : "");
                StatusKey = ok ? "Success" : "Warning";
                _main.Home.RefreshStatus();
                _main.BypassCenter.RefreshAll();
            }
            catch (Exception ex)
            {
                StatusText = "Не удалось применить: " + ex.Message;
                StatusKey = "Danger";
                AppLog.Warn("[Рабочий стол] " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Откат: последний снимок истории (или выбранный вручную).</summary>
        private async Task RevertAsync(ConfigurationSnapshot? snapshot)
        {
            var target = snapshot ?? History.FirstOrDefault();
            if (target == null)
            {
                StatusText = "Откатывать нечего: снимков пока нет.";
                StatusKey = "Warning";
                return;
            }

            if (IsBusy) return;
            IsBusy = true;
            StatusText = $"Возвращаю состояние от {target.TimeText}…";
            StatusKey = "Info";
            try
            {
                var before = SnapshotNow("перед откатом");
                ConfigurationSnapshotStore.Push(before);

                var settings = _main.Settings;
                settings.SelectedStrategy = target.Strategy;
                settings.GameFilterProfileId = target.GameFilterProfileId;
                settings.CustomGameFilterTcpPorts = target.GameFilterTcpPorts;
                settings.CustomGameFilterUdpPorts = target.GameFilterUdpPorts;
                settings.CustomExcludedPorts = target.ExcludedPorts;
                settings.SelectedFakeSni = target.FakeSni;
                settings.AutoSniRotationEnabled = target.AutoSniRotation;
                settings.YoutubeSniOverride = target.YoutubeSniOverride;
                settings.DisableQuicFake = target.DisableQuicFake;
                settings.PreferIPv4ForBypass = target.PreferIPv4;
                settings.UseDohForBlockedHosts = target.UseDoh;
                SettingsStore.Save(settings);

                var notes = new List<string> { "состояние от " + target.TimeText };
                if (!string.IsNullOrWhiteSpace(target.IpsetMode) && Enum.TryParse<IpsetMode>(target.IpsetMode, true, out var mode))
                {
                    if (EngineService.SetIpsetMode(settings.EnginePath, mode)) notes.Add("ipset " + target.IpsetMode);
                }

                // Выбираем стратегию-цель: она же применяется к движку.
                _main.Strategies.Refresh();
                SelectedStrategy = _main.Strategies.Find(target.Strategy) ?? _main.Strategies.Items.FirstOrDefault();
                var applied = await ApplyToEngineAsync(notes);

                ConfigurationSnapshotStore.Remove(target);
                ReloadAll();

                StatusText = applied.Ok
                    ? "Откат выполнен: " + string.Join(", ", notes) + "."
                    : "Откат выполнен частично: " + applied.Message;
                StatusKey = applied.Ok ? "Success" : "Warning";
                _main.Home.RefreshStatus();
                _main.BypassCenter.RefreshAll();
            }
            catch (Exception ex)
            {
                StatusText = "Не удалось откатить: " + ex.Message;
                StatusKey = "Danger";
                AppLog.Warn("[Рабочий стол] откат: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private Task RestoreSnapshotAsync() => RevertAsync(SelectedSnapshot);

        private async Task<(bool Ok, string Message)> ApplyToEngineAsync(List<string> notes)
        {
            if (SelectedStrategy == null) return (true, "");
            var mode = EngineService.GetGameFilterMode(_main.Settings.EnginePath);
            var status = _main.Bypass.GetStatus();
            var result = status.ServiceState == ServiceState.Running
                ? await _main.Bypass.InstallServiceAsync(SelectedStrategy, mode)
                : await _main.Bypass.SwitchToStrategyAsync(SelectedStrategy, mode, _main.Settings.ShowWinwsConsole);
            if (result.Ok) notes.Add("способ обхода «" + SelectedStrategy.Name + "»");
            return (result.Ok, result.Message);
        }

        private async Task<(bool Ok, string Message)> ApplyDnsAsync(List<string> notes)
        {
            if (SelectedDns?.Profile == null) return (true, "");
            var (ok, message) = await DnsManagementService.ApplyDnsProfileAsync(SelectedDns.Profile);
            if (ok) notes.Add("DNS «" + SelectedDns.Profile.Name + "»");
            return (ok, ok ? "" : message);
        }

        private void ExportSnapshot()
        {
            var snapshot = SnapshotNow("экспорт");
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт конфигурации обхода",
                FileName = $"zapret-config-{DateTime.Now:yyyyMMdd-HHmm}.json",
                Filter = "JSON-файл конфигурации (*.json)|*.json|Все файлы (*.*)|*.*",
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog() != true) return;

            var saved = ConfigurationSnapshotStore.ExportToFile(snapshot, dialog.FileName);
            StatusText = saved.Length > 0 ? "Конфигурация сохранена: " + saved : "Не удалось сохранить файл конфигурации.";
            StatusKey = saved.Length > 0 ? "Success" : "Danger";
        }

        // ------------------------------------------------------------------ Пресеты и откат

        /// <summary>Сохранить текущую конфигурацию как именованный пресет (спрашиваем только имя).</summary>
        private void SaveAsPreset()
        {
            var dialog = new Views.InputDialog("Сохранить пресет", "Название пресета",
                secondaryPrompt: "Описание (необязательно)", initialValue: "Моя сеть")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;

            var preset = new ConfigurationPreset
            {
                Name = dialog.Value,
                Description = dialog.SecondaryValue,
                Config = SnapshotNow("пресет")
            };
            ConfigurationPresetStore.Upsert(preset);
            ReloadPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)) ?? Presets.FirstOrDefault();
            StatusText = $"Пресет «{preset.Name}» сохранён: {preset.Config.SummaryText}.";
            StatusKey = "Success";
        }

        /// <summary>Применить пресет: значения попадают в поля, затем идёт обычный путь «Применить».</summary>
        private async Task ApplySelectedPresetAsync()
        {
            var preset = SelectedPreset;
            if (preset == null) return;

            var config = preset.Config;
            _main.Strategies.Refresh();
            SelectedStrategy = _main.Strategies.Find(config.Strategy) ?? _main.Strategies.Items.FirstOrDefault();
            SelectedGameFilter = GameFilterProfiles.FirstOrDefault(p => p.Id == config.GameFilterProfileId) ?? SelectedGameFilter;
            TcpPorts = config.GameFilterTcpPorts;
            UdpPorts = config.GameFilterUdpPorts;
            ExcludedPorts = config.ExcludedPorts;
            SelectedSni = string.IsNullOrWhiteSpace(config.FakeSni) ? SelectedSni : config.FakeSni;
            AutoSniRotation = config.AutoSniRotation;
            YoutubeSniOverride = config.YoutubeSniOverride;
            SelectedIpset = IpsetModes.FirstOrDefault(i => string.Equals(i.Mode.ToString(), config.IpsetMode, StringComparison.OrdinalIgnoreCase)) ?? SelectedIpset;
            DisableQuicFake = config.DisableQuicFake;
            PreferIPv4 = config.PreferIPv4;
            UseDoh = config.UseDoh;

            await ApplyAsync();
            if (StatusKey == "Success")
                StatusText = $"Пресет «{preset.Name}» применён. " + StatusText;
        }

        private void DeleteSelectedPreset()
        {
            var preset = SelectedPreset;
            if (preset == null) return;
            ConfigurationPresetStore.Remove(preset);
            ReloadPresets();
            StatusText = $"Пресет «{preset.Name}» удалён.";
            StatusKey = "Info";
        }

        private void ExportSelectedPreset()
        {
            var preset = SelectedPreset;
            if (preset == null) return;
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт пресета",
                FileName = $"zapret-preset-{SafeName(preset.Name)}.json",
                Filter = "JSON-файл пресета (*.json)|*.json|Все файлы (*.*)|*.*",
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog() != true) return;
            var saved = ConfigurationPresetStore.ExportToFile(new[] { preset }, dialog.FileName);
            StatusText = saved.Length > 0 ? "Пресет сохранён: " + saved : "Не удалось сохранить пресет.";
            StatusKey = saved.Length > 0 ? "Success" : "Danger";
        }

        private void ExportAllPresets()
        {
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт всех пресетов",
                FileName = $"zapret-presets-{DateTime.Now:yyyyMMdd}.json",
                Filter = "JSON-файл пресетов (*.json)|*.json|Все файлы (*.*)|*.*",
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog() != true) return;
            var saved = ConfigurationPresetStore.ExportToFile(Presets, dialog.FileName);
            StatusText = saved.Length > 0 ? $"Выгружено пресетов: {Presets.Count} → {saved}" : "Не удалось сохранить файл.";
            StatusKey = saved.Length > 0 ? "Success" : "Danger";
        }

        private void ImportPresets()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Импорт пресетов",
                Filter = "JSON-файл пресетов (*.json)|*.json|Все файлы (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return;
            var (ok, message) = ConfigurationPresetStore.ImportFromFile(dialog.FileName);
            ReloadPresets();
            StatusText = message;
            StatusKey = ok ? "Success" : "Danger";
        }

        /// <summary>Одна кнопка «вернуть предыдущую рабочую»: последний снимок, при котором обход работал.</summary>
        private async Task RevertToWorkingAsync()
        {
            var target = History.FirstOrDefault(h => h.BypassWasRunning);
            if (target == null)
            {
                StatusText = "Пока нет снимка, при котором обход точно работал.";
                StatusKey = "Warning";
                return;
            }
            await RevertAsync(target);
            if (StatusKey == "Success")
                StatusText = $"Вернулись к рабочей конфигурации от {target.TimeText}. " + StatusText;
        }

        private static string SafeName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
            return clean.Length == 0 ? "preset" : clean;
        }

        private void OpenSection(string section)
        {
            switch (section)
            {
                case "checks":
                    _main.Diagnostics.OpenSystemSubTab();
                    _main.Navigate("diagnostics");
                    break;
                case "updates":
                    _main.Navigate("updates");
                    break;
                case "strategies":
                    _main.Navigate("strategies");
                    break;
                case "user-lists":
                    _main.Navigate("user-lists");
                    break;
                case "bypass":
                    _main.Navigate("bypass-center");
                    break;
                case "automation":
                    _main.Navigate("automation");
                    break;
                case "network":
                    _main.Navigate("network");
                    break;
                default:
                    _main.Navigate("diagnostics");
                    break;
            }
        }
    }
}
