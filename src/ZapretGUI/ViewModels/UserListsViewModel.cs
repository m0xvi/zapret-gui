using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;
using ZapretGui.Views;

namespace ZapretGui.ViewModels
{
    public sealed class UserListOption
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Управление списками доменов, IP-фильтрацией и безопасным DNS (DoH).
    /// </summary>
    public sealed class UserListsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _selectedListKey = "general";
        private string _status = "Изменения сохраняются только после нажатия «Сохранить список».";
        private string? _selectedEntry;
        private string _searchText = "";
        private string _quickDomainInput = "";
        private string _bulkPasteText = "";
        private int _selectedSubTabIndex;
        private bool _hasUnsavedChanges;
        private List<string> _originalEntries = new();
        private string? _editingEntry;
        private string _editingText = "";
        private HashSet<string> _duplicateSet = new(StringComparer.OrdinalIgnoreCase);
        private bool _isUpdatingLists;
        private string _domainListUpdateResultText = "";
        private string _domainListUpdateResultKey = "Info";

        // DNS
        private DnsProfile _selectedDnsProfile;
        private string _currentSystemDnsText = "";
        private string _dnsTestStatusText = "";
        private string _dnsTestStatusKey = "Info";
        private bool _isTestingDns;
        private DnsHijackReport? _hijackReport;
        private bool _isCheckingHijack;
        private string _hijackSummary = "";
        private string _hijackSummaryKey = "Info";

        public UserListsViewModel(MainViewModel main)
        {
            _main = main;
            SubTabs = new[] { "Редактор списков", "Безопасный DNS (DoH)", "Режимы фильтрации" };
            DnsProfiles = DnsManagementService.PredefinedProfiles;
            _selectedDnsProfile = DnsProfiles.FirstOrDefault() ?? DnsProfiles[0];

            ListOptions = new[]
            {
                new UserListOption
                {
                    Key = "general",
                    Title = "Пользовательские домены (list-general-user.txt)",
                    FileName = "list-general-user.txt",
                    Description = "Ваши личные домены и IP для обхода. Сохраняются при обновлениях движка."
                },
                new UserListOption
                {
                    Key = "discord",
                    Title = "Пользовательские Discord (list-discord-user.txt)",
                    FileName = "list-discord-user.txt",
                    Description = "Дополнительные домены Discord (голосовые серверы, шлюзы)."
                },
                new UserListOption
                {
                    Key = "youtube",
                    Title = "Пользовательские YouTube (list-youtube-user.txt)",
                    FileName = "list-youtube-user.txt",
                    Description = "Дополнительные домены YouTube и Google Video."
                },
                new UserListOption
                {
                    Key = "exclude",
                    Title = "Исключения из обхода (list-exclude-user.txt)",
                    FileName = "list-exclude-user.txt",
                    Description = "Домены и IP, к которым обход не должен применяться."
                },
                new UserListOption
                {
                    Key = "ipset",
                    Title = "Исключения IP-фильтра (ipset-exclude-user.txt)",
                    FileName = "ipset-exclude-user.txt",
                    Description = "IP-адреса, которые исключаются из фильтра ipset."
                },
                new UserListOption
                {
                    Key = "built-in-general",
                    Title = "Встроенный список Flowseal (list-general.txt)",
                    FileName = "list-general.txt",
                    Description = "Основной список заблокированных сайтов от автора сборки Flowseal."
                },
                new UserListOption
                {
                    Key = "built-in-discord",
                    Title = "Встроенный список Discord (list-discord.txt)",
                    FileName = "list-discord.txt",
                    Description = "Встроенный список доменов Discord от автора сборки."
                },
                new UserListOption
                {
                    Key = "built-in-youtube",
                    Title = "Встроенный список YouTube (list-youtube.txt)",
                    FileName = "list-youtube.txt",
                    Description = "Встроенный список доменов YouTube от автора сборки."
                }
            };

            EntriesView = CollectionViewSource.GetDefaultView(Entries);
            EntriesView.Filter = FilterEntry;
            if (Entries is System.Collections.Specialized.INotifyCollectionChanged incc)
                incc.CollectionChanged += (_, _) => { Raise(nameof(IsFilteredEmpty)); Raise(nameof(IsListEmpty)); Raise(nameof(EmptyStateText)); Raise(nameof(FilteredCountText)); Raise(nameof(CountText)); };

            ClearSearchCommand = new RelayCommand(() => SearchText = "");
            CopyFingerprintHintCommand = new RelayCommand(() => { try { System.Windows.Clipboard.SetText(ListPath); Status = $"Путь скопирован: {ListPath}"; } catch {} });
            BulkAddCommand = new RelayCommand(BulkAdd, () => !string.IsNullOrWhiteSpace(BulkPasteText) && CanEditCurrentList);

            AddEntryCommand = new RelayCommand(AddEntry, () => CanEditCurrentList);
            QuickAddDomainCommand = new RelayCommand(QuickAddDomain, () => !string.IsNullOrWhiteSpace(QuickDomainInput) && CanEditCurrentList);
            EditEntryCommand = new RelayCommand(EditEntry, _ => CanEditCurrentList);
            RemoveEntryCommand = new RelayCommand(RemoveEntry, _ => SelectedEntry != null && CanEditCurrentList);
            SortEntriesCommand = new RelayCommand(SortEntries, () => Entries.Count > 1 && CanEditCurrentList);
            DeduplicateEntriesCommand = new RelayCommand(DeduplicateEntries, () => Entries.Count > 0 && CanEditCurrentList);
            OpenInNotepadCommand = new RelayCommand(() => Shell.OpenInNotepad(ListPath));
            ExportListCommand = new RelayCommand(ExportList, () => Entries.Count > 0);
            ImportListCommand = new RelayCommand(ImportList, () => CanEditCurrentList);
            SaveCommand = new RelayCommand(Save, () => HasUnsavedChanges && CanEditCurrentList);
            CancelChangesCommand = new RelayCommand(CancelChanges, () => HasUnsavedChanges);
            ReloadCommand = new RelayCommand(LoadEntries);
            StartInlineEditCommand = new RelayCommand(StartInlineEdit, p => p is string && CanEditCurrentList);
            CommitEditCommand = new RelayCommand(CommitInlineEdit, () => IsEditing && !string.IsNullOrWhiteSpace(EditingText));
            CancelEditCommand = new RelayCommand(CancelInlineEdit, () => IsEditing);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(ListsFolder));
            RestartBypassCommand = new AsyncRelayCommand(RestartBypassAsync);
            UpdateListsFromGithubCommand = new AsyncRelayCommand(UpdateListsFromGithubAsync, () => !IsUpdatingLists);

            // DNS команды
            ApplyDnsProfileCommand = new AsyncRelayCommand(ApplyDnsProfileAsync, () => SelectedDnsProfile != null);
            ResetDnsToDhcpCommand = new AsyncRelayCommand(ResetDnsToDhcpAsync);
            TestDnsServerCommand = new AsyncRelayCommand(TestDnsServerAsync, () => !IsTestingDns);
            RefreshCurrentDnsCommand = new RelayCommand(RefreshCurrentDns);
            CheckHijackCommand = new AsyncRelayCommand(CheckHijackAsync, () => !IsCheckingHijack);
            ApplySecureDnsCommand = new AsyncRelayCommand(ApplySecureDnsAsync, () => HijackReport?.HasHijack == true);
            ApplyGameFilterPortsCommand = new AsyncRelayCommand(ApplyGameFilterPortsAsync);

            RefreshCurrentDns();
            LoadEntries();
        }

        public AppSettings Settings => _main.Settings;
        public string[] SubTabs { get; }

        public int SelectedSubTabIndex
        {
            get => _selectedSubTabIndex;
            set
            {
                if (Set(ref _selectedSubTabIndex, Math.Clamp(value, 0, SubTabs.Length - 1)))
                {
                    Raise(nameof(IsEditorTabVisible));
                    Raise(nameof(IsDnsTabVisible));
                    Raise(nameof(IsFiltersTabVisible));
                }
            }
        }

        public bool IsEditorTabVisible => SelectedSubTabIndex == 0;
        public bool IsDnsTabVisible => SelectedSubTabIndex == 1;
        public bool IsFiltersTabVisible => SelectedSubTabIndex == 2;

        public UserListOption[] ListOptions { get; }
        public ObservableCollection<string> Entries { get; } = new();
        public ICollectionView EntriesView { get; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value ?? ""))
                {
                    EntriesView.Refresh();
                    Raise(nameof(CountText));
                    Raise(nameof(FilteredCountText));
                    Raise(nameof(IsFilteredEmpty));
                    Raise(nameof(IsListEmpty));
                    Raise(nameof(EmptyStateText));
                }
            }
        }

        public string QuickDomainInput
        {
            get => _quickDomainInput;
            set
            {
                if (Set(ref _quickDomainInput, value ?? ""))
                {
                    (QuickAddDomainCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string BulkPasteText
        {
            get => _bulkPasteText;
            set
            {
                if (Set(ref _bulkPasteText, value ?? ""))
                {
                    (BulkAddCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    Raise(nameof(BulkLineCountText));
                    Raise(nameof(BulkDetailedCountText));
                    Raise(nameof(BulkPreviewItems));
                    Raise(nameof(BulkHasPreview));
                }
            }
        }

        public string BulkLineCountText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BulkPasteText)) return "Вставьте список доменов (по одному в строке)";
                var lines = BulkPasteText.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries).Length;
                return $"{lines} строк готово к добавлению";
            }
        }

        public string BulkDetailedCountText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BulkPasteText)) return "Поддерживаются https://, пробелы и пустые строки — дубликаты пропускаются";
                var lines = BulkPasteText.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
                var added = 0; var skipped = 0; var invalid = 0;
                foreach (var raw in lines)
                {
                    var clean = CleanDomain(raw);
                    if (string.IsNullOrWhiteSpace(clean)) { skipped++; continue; }
                    if (!IsValidListEntry(clean)) { invalid++; continue; }
                    if (Entries.Any(e => e.Equals(clean, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                    added++;
                }
                return $"{lines.Length} строк • Новых: {added} • Дубликатов: {skipped} • Невалидных: {invalid}";
            }
        }

        public IEnumerable<string> BulkPreviewItems
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BulkPasteText)) return Array.Empty<string>();
                var lines = BulkPasteText.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
                var res = new List<string>();
                foreach (var raw in lines)
                {
                    if (res.Count >= 8) break;
                    var clean = CleanDomain(raw);
                    if (string.IsNullOrWhiteSpace(clean)) continue;
                    if (!IsValidListEntry(clean)) continue;
                    if (Entries.Any(e => e.Equals(clean, StringComparison.OrdinalIgnoreCase))) continue;
                    if (res.Contains(clean, StringComparer.OrdinalIgnoreCase)) continue;
                    res.Add(clean);
                }
                return res;
            }
        }

        public bool BulkHasPreview => BulkPreviewItems.Any();

        public string CountText
        {
            get
            {
                var total = Entries.Count;
                var filtered = EntriesView.Cast<object>().Count();
                return filtered == total ? $"Всего: {total}" : $"Показано: {filtered} из {total}";
            }
        }

        public string FilteredCountText => $"{EntriesView.Cast<object>().Count()} из {Entries.Count}";
        public bool IsFilteredEmpty => Entries.Count > 0 && EntriesView.IsEmpty;
        public bool IsListEmpty => Entries.Count == 0 && string.IsNullOrWhiteSpace(SearchText);
        public string EmptyStateText => IsFilteredEmpty ? "Ничего не найдено — измените запрос или нажмите «Сбросить поиск»." : "Список пуст. Добавьте первый домен через поле ввода.";


        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set
            {
                if (Set(ref _hasUnsavedChanges, value))
                {
                    Raise(nameof(SaveVisible));
                    (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CancelChangesCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool SaveVisible => HasUnsavedChanges;
        public bool IsBuiltInList => SelectedListKey.StartsWith("built-in", StringComparison.OrdinalIgnoreCase);
        public bool CanEditCurrentList => !IsBuiltInList;

        public string? EditingEntry
        {
            get => _editingEntry;
            set
            {
                if (Set(ref _editingEntry, value))
                {
                    Raise(nameof(IsEditing));
                    (CommitEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CancelEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string EditingText
        {
            get => _editingText;
            set
            {
                if (Set(ref _editingText, value ?? ""))
                    (CommitEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsEditing => !string.IsNullOrEmpty(EditingEntry);

        public string FileInfoText
        {
            get
            {
                try
                {
                    if (!File.Exists(ListPath)) return "Файл ещё не создан";
                    var info = new FileInfo(ListPath);
                    return $"{info.Length} байт · изменён {info.LastWriteTime:dd.MM.yyyy HH:mm} · {Entries.Count} строк";
                }
                catch { return ""; }
            }
        }

        public int DuplicateCount => _duplicateSet.Count;
        public int InvalidCount => Entries.Count(e => !IsValidListEntry(e));

        public bool HasDuplicates => DuplicateCount > 0;
        public bool HasInvalid => InvalidCount > 0;


        public string[] GameFilterOptions { get; } = { "Выключен (только стандартные порты)", "TCP + UDP (игры и сервисы, порты > 1023)", "Только TCP", "Только UDP" };
        public string[] IpsetOptions { get; } = { "По списку ipset-all.txt (рекомендуется)", "Все IP / Any (максимальный охват)", "Без фильтрации IP / None (все адреса)" };

        public IReadOnlyList<GameFilterProfile> GameFilterProfiles => GameFilterPortConfig.PredefinedProfiles;

        public GameFilterProfile SelectedGameFilterProfile
        {
            get => GameFilterPortConfig.GetProfileById(Settings.GameFilterProfileId);
            set
            {
                if (value != null)
                {
                    Settings.GameFilterProfileId = value.Id;
                    if (value.Id != "custom")
                    {
                        Settings.CustomGameFilterTcpPorts = value.TcpPorts;
                        Settings.CustomGameFilterUdpPorts = value.UdpPorts;
                        Settings.CustomExcludedPorts = value.ExcludedPorts;
                    }
                    SettingsStore.Save(Settings);
                    Raise(nameof(SelectedGameFilterProfile));
                    Raise(nameof(IsCustomGameFilterSelected));
                    Raise(nameof(CustomGameFilterTcpPorts));
                    Raise(nameof(CustomGameFilterUdpPorts));
                    Raise(nameof(CustomExcludedPorts));
                    Status = $"Выбран профиль GameFilter: «{value.Name}».";
                }
            }
        }

        public bool IsCustomGameFilterSelected => Settings.GameFilterProfileId == "custom";

        public string CustomGameFilterTcpPorts
        {
            get => Settings.CustomGameFilterTcpPorts;
            set
            {
                Settings.CustomGameFilterTcpPorts = value ?? "1024-65535";
                SettingsStore.Save(Settings);
                Raise(nameof(CustomGameFilterTcpPorts));
            }
        }

        public string CustomGameFilterUdpPorts
        {
            get => Settings.CustomGameFilterUdpPorts;
            set
            {
                Settings.CustomGameFilterUdpPorts = value ?? "50000-65535";
                SettingsStore.Save(Settings);
                Raise(nameof(CustomGameFilterUdpPorts));
            }
        }

        public string CustomExcludedPorts
        {
            get => Settings.CustomExcludedPorts;
            set
            {
                Settings.CustomExcludedPorts = value ?? "";
                SettingsStore.Save(Settings);
                Raise(nameof(CustomExcludedPorts));
            }
        }

        public int GameFilterIndex
        {
            get => EngineService.GetGameFilterMode(Settings.EnginePath) switch
            {
                GameFilterMode.TcpAndUdp => 1,
                GameFilterMode.TcpOnly => 2,
                GameFilterMode.UdpOnly => 3,
                _ => 0
            };
            set
            {
                var mode = value switch
                {
                    1 => GameFilterMode.TcpAndUdp,
                    2 => GameFilterMode.TcpOnly,
                    3 => GameFilterMode.UdpOnly,
                    _ => GameFilterMode.Disabled
                };
                EngineService.SetGameFilterMode(Settings.EnginePath, mode);
                Raise(nameof(GameFilterIndex));
                Status = "Режим игрового фильтра изменён. Перезапустите обход для применения.";
            }
        }

        public int IpsetModeIndex
        {
            get => EngineService.GetIpsetMode(Settings.EnginePath) switch
            {
                IpsetMode.Any => 1,
                IpsetMode.None => 2,
                _ => 0
            };
            set
            {
                var mode = value switch
                {
                    1 => IpsetMode.Any,
                    2 => IpsetMode.None,
                    _ => IpsetMode.Loaded
                };
                EngineService.SetIpsetMode(Settings.EnginePath, mode);
                Raise(nameof(IpsetModeIndex));
                Status = "Режим ipset изменён. Перезапустите обход для применения.";
            }
        }

        public string SelectedListKey
        {
            get => _selectedListKey;
            set
            {
                if (!Set(ref _selectedListKey, value ?? "general")) return;
                LoadEntries();
                Raise(nameof(SelectedList));
                Raise(nameof(ListPath));
                Raise(nameof(HasEntries));
                Raise(nameof(IsBuiltInList));
                Raise(nameof(CanEditCurrentList));
                Raise(nameof(FileInfoText));
                (AddEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (QuickAddDomainCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (EditEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RemoveEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SortEntriesCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DeduplicateEntriesCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ImportListCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (StartInlineEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public UserListOption SelectedList => ListOptions.FirstOrDefault(option => option.Key == SelectedListKey)
            ?? ListOptions[0];
        public string ListPath => Path.Combine(Settings.EnginePath, "lists", SelectedList.FileName);
        public string ListsFolder => Path.Combine(Settings.EnginePath, "lists");
        public bool HasEntries => Entries.Count > 0;

        public string? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (!Set(ref _selectedEntry, value)) return;
                (RemoveEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public bool IsUpdatingLists
        {
            get => _isUpdatingLists;
            private set
            {
                if (Set(ref _isUpdatingLists, value))
                    (UpdateListsFromGithubCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string DomainListUpdateResultText
        {
            get => _domainListUpdateResultText;
            private set => Set(ref _domainListUpdateResultText, value);
        }

        public string DomainListUpdateResultKey
        {
            get => _domainListUpdateResultKey;
            private set => Set(ref _domainListUpdateResultKey, value);
        }

        // ------------------------------------------------------------------ DNS
        public IReadOnlyList<DnsProfile> DnsProfiles { get; }

        public DnsProfile SelectedDnsProfile
        {
            get => _selectedDnsProfile;
            set
            {
                if (Set(ref _selectedDnsProfile, value))
                {
                    Raise(nameof(SelectedDnsProfile));
                    (ApplyDnsProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string CurrentSystemDnsText
        {
            get => _currentSystemDnsText;
            private set => Set(ref _currentSystemDnsText, value);
        }

        public string DnsTestStatusText
        {
            get => _dnsTestStatusText;
            private set => Set(ref _dnsTestStatusText, value);
        }

        public string DnsTestStatusKey
        {
            get => _dnsTestStatusKey;
            private set => Set(ref _dnsTestStatusKey, value);
        }

        public bool IsTestingDns
        {
            get => _isTestingDns;
            private set
            {
                if (Set(ref _isTestingDns, value))
                    (TestDnsServerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public DnsHijackReport? HijackReport
        {
            get => _hijackReport;
            private set
            {
                if (Set(ref _hijackReport, value))
                {
                    Raise(nameof(HijackReportVisible));
                    Raise(nameof(HijackSummary));
                    Raise(nameof(HijackSummaryKey));
                    (ApplySecureDnsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HijackReportVisible => HijackReport != null;

        public string HijackSummary
        {
            get => _hijackSummary;
            private set => Set(ref _hijackSummary, value);
        }

        public string HijackSummaryKey
        {
            get => _hijackSummaryKey;
            private set => Set(ref _hijackSummaryKey, value);
        }

        public bool IsCheckingHijack
        {
            get => _isCheckingHijack;
            private set
            {
                if (Set(ref _isCheckingHijack, value))
                {
                    (CheckHijackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    Raise(nameof(IsCheckingHijack));
                }
            }
        }

        public string HijackCheckedAtText
        {
            get
            {
                if (Settings.LastDnsHijackCheckedAt == null) return "Ещё не проверялось";
                return $"Последняя проверка: {Settings.LastDnsHijackCheckedAt:dd.MM.yyyy HH:mm} · {Settings.LastDnsHijackSummary}";
            }
        }


        public ICommand ClearSearchCommand { get; }
        public ICommand CopyFingerprintHintCommand { get; }
        public ICommand BulkAddCommand { get; }
        public ICommand AddEntryCommand { get; }
        public ICommand QuickAddDomainCommand { get; }
        public ICommand EditEntryCommand { get; }
        public ICommand RemoveEntryCommand { get; }
        public ICommand SortEntriesCommand { get; }
        public ICommand DeduplicateEntriesCommand { get; }
        public ICommand OpenInNotepadCommand { get; }
        public ICommand ExportListCommand { get; }
        public ICommand ImportListCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelChangesCommand { get; }
        public ICommand ReloadCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RestartBypassCommand { get; }
        public ICommand UpdateListsFromGithubCommand { get; }
        public ICommand ApplyGameFilterPortsCommand { get; }
        public ICommand StartInlineEditCommand { get; }
        public ICommand CommitEditCommand { get; }
        public ICommand CancelEditCommand { get; }

        public ICommand ApplyDnsProfileCommand { get; }
        public ICommand ResetDnsToDhcpCommand { get; }
        public ICommand TestDnsServerCommand { get; }
        public ICommand RefreshCurrentDnsCommand { get; }
        public ICommand CheckHijackCommand { get; }
        public ICommand ApplySecureDnsCommand { get; }

        private bool FilterEntry(object item)
        {
            if (item is not string str) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return str.IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool IsValidListEntry(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return false;
            var s = entry.Trim();
            if (s.Length < 3 || s.Length > 253) return false;
            if (s.Contains(' ') || s.Contains('\t')) return false;
            // Разрешаем домены, IP, префиксы с * и / для сетей
            if (s.StartsWith(".") || s.EndsWith(".") || s.Contains("..")) return false;
            // Только допустимые символы
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' || ch == '*' || ch == '/' || ch == ':') continue;
                return false;
            }
            return true;
        }

        public bool IsDuplicate(string entry)
        {
            return _duplicateSet.Contains(entry);
        }

        private void RebuildDuplicateSet()
        {
            _duplicateSet = Entries.GroupBy(e => e.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Raise(nameof(DuplicateCount));
            Raise(nameof(HasDuplicates));
            Raise(nameof(InvalidCount));
            Raise(nameof(HasInvalid));
        }

        private void MarkDirty()
        {
            var isDirty = !_originalEntries.SequenceEqual(Entries, StringComparer.OrdinalIgnoreCase);
            HasUnsavedChanges = isDirty;
            RebuildDuplicateSet();
            Raise(nameof(FileInfoText));
            Raise(nameof(HasUnsavedChanges));
        }

        private void LoadEntries()
        {
            Entries.Clear();
            SelectedEntry = null;
            EditingEntry = null;
            try
            {
                if (File.Exists(ListPath))
                {
                    foreach (var line in File.ReadLines(ListPath)
                                 .Select(line => line.Trim())
                                 .Where(line => line.Length > 0))
                        Entries.Add(line);
                }
                _originalEntries = Entries.ToList();
                HasUnsavedChanges = false;
                Status = IsBuiltInList
                    ? $"Встроенный список (только чтение): {Entries.Count} записей."
                    : Entries.Count == 0
                        ? "Список пуст. Добавьте первый домен через поле ввода."
                        : $"Записей: {Entries.Count}. Файл: {SelectedList.FileName}";
                RebuildDuplicateSet();
            }
            catch (Exception ex)
            {
                Status = "Не удалось прочитать список: " + ex.Message;
            }
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            Raise(nameof(FilteredCountText));
            Raise(nameof(IsFilteredEmpty));
            Raise(nameof(IsListEmpty));
            Raise(nameof(EmptyStateText));
            Raise(nameof(FileInfoText));
            RaiseCommands();
        }

        private void QuickAddDomain()
        {
            if (!CanEditCurrentList) return;
            if (string.IsNullOrWhiteSpace(QuickDomainInput)) return;
            var clean = CleanDomain(QuickDomainInput);
            if (string.IsNullOrWhiteSpace(clean)) return;

            if (!IsValidListEntry(clean))
            {
                Status = $"«{clean}» имеет недопустимый формат (разрешены a-z, 0-9, . - _ * / :).";
                return;
            }

            if (Entries.Any(entry => entry.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                Status = $"«{clean}» уже есть в текущем списке.";
                return;
            }

            Entries.Insert(0, clean);
            SelectedEntry = clean;
            QuickDomainInput = "";
            MarkDirty();
            // Автосохранение для быстрого ввода остаётся, но помечаем как dirty → сразу сохраняем
            Save();
            Status = $"Домен «{clean}» успешно добавлен и сохранён в {SelectedList.FileName}!";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
        }

        private void BulkAdd()
        {
            if (!CanEditCurrentList || string.IsNullOrWhiteSpace(BulkPasteText)) return;
            var lines = BulkPasteText.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
            var isLarge = lines.Length > 50;
            if (isLarge) try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Массовое добавление", $"Обработка {lines.Length} строк — не закрывайте окно", "Проверка доменов…", 0, true, false)); } catch {}
            var added = 0; var skipped = 0; var invalid = 0;
            foreach (var raw in lines)
            {
                var clean = CleanDomain(raw);
                if (string.IsNullOrWhiteSpace(clean)) { skipped++; continue; }
                if (!IsValidListEntry(clean)) { invalid++; continue; }
                if (Entries.Any(e => e.Equals(clean, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                Entries.Add(clean);
                added++;
            }
            if (added > 0) { MarkDirty(); Save(); }
            BulkPasteText = "";
            Status = $"Пачкой добавлено {added}, пропущено дубликатов {skipped}, невалидных {invalid} — {(added>0?"сохранено":"")}.";
            Raise(nameof(HasEntries)); Raise(nameof(CountText)); RaiseCommands();
            if (isLarge) try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private static string CleanDomain(string input)
        {
            var s = input.Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
            var slash = s.IndexOf('/');
            if (slash >= 0) s = s[..slash];
            var colon = s.IndexOf(':');
            if (colon >= 0) s = s[..colon];
            return s.Trim();
        }

        private void AddEntry()
        {
            if (!CanEditCurrentList) return;
            var dialog = new InputDialog(
                "Добавить запись в список",
                "Введите домен или IP-адрес:")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;

            var value = CleanDomain(dialog.Value);
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!IsValidListEntry(value))
            {
                Status = $"«{value}» — недопустимый формат.";
                return;
            }
            if (Entries.Any(entry => entry.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "Такая запись уже есть в текущем списке.";
                return;
            }

            Entries.Add(value);
            SelectedEntry = value;
            MarkDirty();
            Status = "Запись добавлена в редактор. Нажмите «Сохранить список».";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
        }

        private void EditEntry(object? parameter)
        {
            if (!CanEditCurrentList) return;
            var oldValue = parameter as string ?? SelectedEntry;
            if (string.IsNullOrWhiteSpace(oldValue)) return;

            // Если уже редактируем inline — используем его, иначе диалог
            if (IsEditing && EditingEntry == oldValue)
            {
                EditingText = oldValue;
                return;
            }

            var dialog = new InputDialog(
                "Изменить запись",
                "Измените домен или IP-адрес:",
                initialValue: oldValue)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;

            var newValue = CleanDomain(dialog.Value);
            if (string.IsNullOrWhiteSpace(newValue)) return;
            if (!IsValidListEntry(newValue))
            {
                Status = $"«{newValue}» — недопустимый формат.";
                return;
            }
            if (Entries.Any(entry => !entry.Equals(oldValue, StringComparison.OrdinalIgnoreCase) &&
                                     entry.Equals(newValue, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "Такая запись уже есть в текущем списке.";
                return;
            }

            var index = Entries.IndexOf(oldValue);
            if (index < 0) return;
            Entries[index] = newValue;
            SelectedEntry = newValue;
            MarkDirty();
            Status = "Запись изменена в редакторе. Нажмите «Сохранить список».";
        }

        private void RemoveEntry(object? parameter)
        {
            if (!CanEditCurrentList) return;
            var value = parameter as string ?? SelectedEntry;
            if (string.IsNullOrWhiteSpace(value)) return;
            if (EditingEntry == value) CancelInlineEdit();
            Entries.Remove(value);
            SelectedEntry = null;
            MarkDirty();
            Status = $"Запись «{value}» удалена из редактора. Нажмите «Сохранить список».";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
        }

        private void SortEntries()
        {
            if (!CanEditCurrentList) return;
            var sorted = Entries.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            Entries.Clear();
            foreach (var s in sorted) Entries.Add(s);
            MarkDirty();
            Status = "Список отсортирован по алфавиту (A-Z). Нажмите «Сохранить список».";
        }

        private void DeduplicateEntries()
        {
            if (!CanEditCurrentList) return;
            var initial = Entries.Count;
            var distinct = Entries
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Entries.Clear();
            foreach (var s in distinct) Entries.Add(s);
            var removed = initial - distinct.Count;
            MarkDirty();
            Status = removed > 0
                ? $"Удалено дубликатов и пустых строк: {removed}. Нажмите «Сохранить список»."
                : "Дубликатов не найдено.";
            Raise(nameof(CountText));
        }

        private void ExportList()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
                    FileName = SelectedList.FileName
                };
                if (dialog.ShowDialog() != true) return;
                File.WriteAllLines(dialog.FileName, Entries);
                Status = "Список экспортирован в файл: " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                Status = "Ошибка экспорта: " + ex.Message;
            }
        }

        private void ImportList()
        {
            if (!CanEditCurrentList) return;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                var added = 0;
                var skippedInvalid = 0;
                foreach (var raw in File.ReadLines(dialog.FileName))
                {
                    var line = CleanDomain(raw);
                    if (line.Length == 0) continue;
                    if (!IsValidListEntry(line)) { skippedInvalid++; continue; }
                    if (!Entries.Any(e => e.Equals(line, StringComparison.OrdinalIgnoreCase)))
                    {
                        Entries.Add(line);
                        added++;
                    }
                }

                if (added > 0) MarkDirty();
                Status = $"Импортировано {added} новых записей{(skippedInvalid>0? $", пропущено невалидных: {skippedInvalid}":"")}. {(added>0?"Нажмите «Сохранить список».":"")}";
                Raise(nameof(HasEntries));
                Raise(nameof(CountText));
                RaiseCommands();
            }
            catch (Exception ex)
            {
                Status = "Ошибка импорта: " + ex.Message;
            }
        }

        private void Save()
        {
            if (IsBuiltInList) return;
            try
            {
                Directory.CreateDirectory(ListsFolder);
                // Валидация перед сохранением: отфильтруем пустые, проверим формат
                var invalid = Entries.Where(e => !IsValidListEntry(e)).ToList();
                if (invalid.Count > 0)
                {
                    Status = $"Есть невалидные записи ({invalid.Count}): {string.Join(", ", invalid.Take(3))}{(invalid.Count>3?"…":"")} — исправьте перед сохранением.";
                    return;
                }
                File.WriteAllLines(ListPath, Entries);
                _originalEntries = Entries.ToList();
                HasUnsavedChanges = false;
                RebuildDuplicateSet();
                Status = $"Список «{SelectedList.FileName}» успешно сохранён ({Entries.Count} записей). Перезапустите обход для применения.";
                Raise(nameof(FileInfoText));
            }
            catch (Exception ex)
            {
                Status = "Не удалось сохранить список: " + ex.Message;
            }
        }

        private void CancelChanges()
        {
            Entries.Clear();
            foreach (var s in _originalEntries) Entries.Add(s);
            HasUnsavedChanges = false;
            EditingEntry = null;
            RebuildDuplicateSet();
            Status = "Несохранённые изменения отменены.";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            Raise(nameof(FileInfoText));
            RaiseCommands();
        }

        private void StartInlineEdit(object? parameter)
        {
            if (!CanEditCurrentList) return;
            var value = parameter as string ?? SelectedEntry;
            if (string.IsNullOrWhiteSpace(value)) return;
            EditingEntry = value;
            EditingText = value;
        }

        private void CommitInlineEdit()
        {
            if (!IsEditing || string.IsNullOrWhiteSpace(EditingText)) return;
            var oldValue = EditingEntry!;
            var newValue = CleanDomain(EditingText);
            if (string.IsNullOrWhiteSpace(newValue) || !IsValidListEntry(newValue))
            {
                Status = $"«{EditingText}» — недопустимый формат.";
                return;
            }
            if (!oldValue.Equals(newValue, StringComparison.OrdinalIgnoreCase) &&
                Entries.Any(e => !e.Equals(oldValue, StringComparison.OrdinalIgnoreCase) && e.Equals(newValue, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "Такая запись уже есть в списке.";
                return;
            }
            var idx = Entries.IndexOf(oldValue);
            if (idx >= 0) Entries[idx] = newValue;
            EditingEntry = null;
            SelectedEntry = newValue;
            MarkDirty();
            Status = "Запись изменена. Нажмите «Сохранить список».";
        }

        private void CancelInlineEdit()
        {
            EditingEntry = null;
            EditingText = "";
        }

        private async Task ApplyGameFilterPortsAsync()
        {
            SettingsStore.Save(Settings);
            var mode = EngineService.GetGameFilterMode(Settings.EnginePath);
            var udpPort = GameFilterPortConfig.ResolveUdpPortString(mode, Settings.GameFilterProfileId, Settings.CustomGameFilterUdpPorts);
            Status = $"Настройки GameFilter сохранены (UDP: {udpPort}).";
            _main.Home.ShowSuccess($"✅ GameFilter: порты обновлены (UDP: {udpPort}).");
            if (_main.Bypass.GetStatus().IsRunning)
            {
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("GameFilter", "Перезапуск обхода для применения портов…", udpPort, 0, true, false)); } catch {}
                await RestartBypassAsync();
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        private async Task RestartBypassAsync()
        {
            var status = _main.Bypass.GetStatus();
            if (!status.IsRunning)
            {
                Status = "Обход в данный момент выключен. Запустите его на главной странице.";
                return;
            }

            Status = "Перезапускаю обход для применения обновлённых списков…";
            var strat = _main.Strategies.Find(status.StrategyName) ?? _main.Strategies.Recommended;
            if (strat == null) return;
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Перезапуск обхода", Status, strat.Name, 0, true, false)); } catch {}

            var res = await _main.Bypass.SwitchToStrategyAsync(strat, EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
            Status = res.Ok ? "Обход успешно перезапущен с новыми списками!" : "Ошибка перезапуска: " + res.Message;
            _main.Home.RefreshStatus();
            if (!res.Ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Перезапуск — ошибка", res.Message)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private async Task UpdateListsFromGithubAsync()
        {
            if (IsUpdatingLists) return;
            IsUpdatingLists = true;
            DomainListUpdateResultText = "Загружаю свежие списки с GitHub…";
            DomainListUpdateResultKey = "Info";
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Обновление списков", DomainListUpdateResultText, "Загрузка с GitHub…", 0, true, false)); } catch {}

            try
            {
                var progress = new Progress<string>(msg => { DomainListUpdateResultText = msg; try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update(msg, "Списки…", null, true)); } catch {} });
                var res = await DomainListUpdater.UpdateAllAsync(Settings.EnginePath, progress);
                DomainListUpdateResultText = res.Message;
                DomainListUpdateResultKey = res.Ok ? "Success" : "Danger";
                if (res.Ok)
                {
                    LoadEntries();
                }
                if (!res.Ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Списки — ошибка", res.Message)); } catch {} return; }
            }
            catch (Exception ex)
            {
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("Списки — ошибка", ex.Message)); } catch {}
                throw;
            }
            finally
            {
                IsUpdatingLists = false;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        // ------------------------------------------------------------------ DNS методы
        public void RefreshCurrentDns()
        {
            CurrentSystemDnsText = DnsManagementService.GetCurrentDnsSummary();
            Raise(nameof(CurrentSystemDnsText));
        }

        private async Task ApplyDnsProfileAsync()
        {
            if (SelectedDnsProfile == null) return;
            DnsTestStatusText = $"Применяю {SelectedDnsProfile.Name} к сетевому адаптеру…";
            DnsTestStatusKey = "Info";
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Настройка DNS", DnsTestStatusText, SelectedDnsProfile.PrimaryServer ?? "", 0, true, false)); } catch {}

            var result = await DnsManagementService.ApplyDnsProfileAsync(SelectedDnsProfile);
            DnsTestStatusText = result.Message;
            DnsTestStatusKey = result.Ok ? "Success" : "Danger";
            RefreshCurrentDns();
            if (!result.Ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("DNS — ошибка", result.Message)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private async Task ResetDnsToDhcpAsync()
        {
            var dhcpProfile = DnsProfiles.FirstOrDefault(p => p.IsDhcp) ?? new DnsProfile { Id = "dhcp", Name = "Автоматический DNS (DHCP)" };
            DnsTestStatusText = "Сбрасываю DNS на автоматический режим (DHCP)…";
            DnsTestStatusKey = "Info";
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Сброс DNS", DnsTestStatusText, "DHCP…", 0, true, false)); } catch {}

            var result = await DnsManagementService.ApplyDnsProfileAsync(dhcpProfile);
            DnsTestStatusText = result.Message;
            DnsTestStatusKey = result.Ok ? "Success" : "Danger";
            RefreshCurrentDns();
            if (!result.Ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("DNS — ошибка", result.Message)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private async Task TestDnsServerAsync()
        {
            if (IsTestingDns) return;
            IsTestingDns = true;
            DnsTestStatusText = "Тестирую скорость ответа и резолвинг DNS…";
            DnsTestStatusKey = "Info";
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Тест DNS", DnsTestStatusText, SelectedDnsProfile?.PrimaryServer ?? "", 0, true, false)); } catch {}

            try
            {
                var ip = SelectedDnsProfile?.PrimaryServer ?? "";
                var res = await DnsManagementService.TestDnsServerAsync(ip, "www.youtube.com");
                DnsTestStatusText = res.Ok
                    ? $"DNS проверен успешно: задержка {res.Milliseconds} мс (резолв youtube.com -> {res.ResolvedIp})"
                    : $"Ошибка DNS: {res.Message}";
                DnsTestStatusKey = res.Ok ? "Success" : "Danger";
                if (!res.Ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("DNS тест — ошибка", res.Message)); } catch {} return; }
            }
            catch (Exception ex)
            {
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("DNS тест — ошибка", ex.Message)); } catch {}
                throw;
            }
            finally
            {
                IsTestingDns = false;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        private async Task CheckHijackAsync()
        {
            if (IsCheckingHijack) return;
            IsCheckingHijack = true;
            HijackSummary = "Проверяю DNS на подмену (сравнение системный vs Cloudflare DoH)…";
            HijackSummaryKey = "Info";
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Проверка подмены DNS", HijackSummary, "Сравнение с Cloudflare DoH…", 0, true, false)); } catch {}
            try
            {
                var progress = new Progress<string>(msg => { HijackSummary = msg; try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Update(msg, "DNS hijack…", null, true)); } catch {} });
                var report = await DnsManagementService.CheckHijackAsync(progress);
                HijackReport = report;
                HijackSummary = report.Summary;
                HijackSummaryKey = report.StatusKey;
                Settings.LastDnsHijackSummary = report.Summary;
                Settings.LastDnsHijackCheckedAt = DateTime.Now;
                SettingsStore.Save(Settings);
                Raise(nameof(HijackCheckedAtText));
            }
            catch (Exception ex)
            {
                HijackSummary = "Ошибка проверки подмены: " + ex.Message;
                HijackSummaryKey = "Danger";
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("DNS hijack — ошибка", ex.Message)); } catch {}
                return;
            }
            finally
            {
                IsCheckingHijack = false;
                try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
            }
        }

        private async Task ApplySecureDnsAsync()
        {
            if (HijackReport == null || !HijackReport.HasHijack) return;
            var cloudflare = DnsManagementService.PredefinedProfiles.FirstOrDefault(p => p.Id == "cloudflare");
            if (cloudflare == null) return;
            DnsTestStatusText = "Обнаружена подмена — применяю защищённый Cloudflare DNS…";
            DnsTestStatusKey = "Warning";
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Show("Защита DNS", DnsTestStatusText, "Cloudflare…", 0, true, false)); } catch {}
            var res = await DnsManagementService.ApplyDnsProfileAsync(cloudflare);
            DnsTestStatusText = res.Message;
            DnsTestStatusKey = res.Ok ? "Success" : "Danger";
            RefreshCurrentDns();
            if (res.Ok) SelectedDnsProfile = cloudflare;
            if (!res.Ok) { try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.ShowError("DNS — ошибка", res.Message)); } catch {} return; }
            try { System.Windows.Application.Current?.Dispatcher?.Invoke(() => _main.GlobalOverlay.Hide()); } catch {}
        }

        private void RaiseCommands()
        {
            (ClearSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AddEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (QuickAddDomainCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SortEntriesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeduplicateEntriesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportListCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelChangesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ImportListCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartInlineEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
