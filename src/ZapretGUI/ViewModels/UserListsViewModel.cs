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
        private int _selectedSubTabIndex;
        private bool _isUpdatingLists;
        private string _domainListUpdateResultText = "";
        private string _domainListUpdateResultKey = "Info";

        // DNS
        private DnsProfile _selectedDnsProfile;
        private string _currentSystemDnsText = "";
        private string _dnsTestStatusText = "";
        private string _dnsTestStatusKey = "Info";
        private bool _isTestingDns;

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

            AddEntryCommand = new RelayCommand(AddEntry);
            QuickAddDomainCommand = new RelayCommand(QuickAddDomain, () => !string.IsNullOrWhiteSpace(QuickDomainInput));
            EditEntryCommand = new RelayCommand(EditEntry);
            RemoveEntryCommand = new RelayCommand(RemoveEntry, _ => SelectedEntry != null);
            SortEntriesCommand = new RelayCommand(SortEntries, () => Entries.Count > 1);
            DeduplicateEntriesCommand = new RelayCommand(DeduplicateEntries, () => Entries.Count > 0);
            OpenInNotepadCommand = new RelayCommand(() => Shell.OpenInNotepad(ListPath));
            ExportListCommand = new RelayCommand(ExportList, () => Entries.Count > 0);
            ImportListCommand = new RelayCommand(ImportList);
            SaveCommand = new RelayCommand(Save);
            ReloadCommand = new RelayCommand(LoadEntries);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(ListsFolder));
            RestartBypassCommand = new AsyncRelayCommand(RestartBypassAsync);
            UpdateListsFromGithubCommand = new AsyncRelayCommand(UpdateListsFromGithubAsync, () => !IsUpdatingLists);

            // DNS команды
            ApplyDnsProfileCommand = new AsyncRelayCommand(ApplyDnsProfileAsync, () => SelectedDnsProfile != null);
            ResetDnsToDhcpCommand = new AsyncRelayCommand(ResetDnsToDhcpAsync);
            TestDnsServerCommand = new AsyncRelayCommand(TestDnsServerAsync, () => !IsTestingDns);
            RefreshCurrentDnsCommand = new RelayCommand(RefreshCurrentDns);

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

        public string CountText
        {
            get
            {
                var total = Entries.Count;
                var filtered = EntriesView.Cast<object>().Count();
                return filtered == total ? $"Всего: {total}" : $"Показано: {filtered} из {total}";
            }
        }

        public string[] GameFilterOptions { get; } = { "Выключен (только стандартные порты)", "TCP + UDP (игры и сервисы, порты > 1023)", "Только TCP", "Только UDP" };
        public string[] IpsetOptions { get; } = { "По списку ipset-all.txt (рекомендуется)", "Все IP / Any (максимальный охват)", "Без фильтрации IP / None (все адреса)" };

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
        public ICommand ReloadCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RestartBypassCommand { get; }
        public ICommand UpdateListsFromGithubCommand { get; }

        public ICommand ApplyDnsProfileCommand { get; }
        public ICommand ResetDnsToDhcpCommand { get; }
        public ICommand TestDnsServerCommand { get; }
        public ICommand RefreshCurrentDnsCommand { get; }

        private bool FilterEntry(object item)
        {
            if (item is not string str) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return str.IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LoadEntries()
        {
            Entries.Clear();
            SelectedEntry = null;
            try
            {
                if (File.Exists(ListPath))
                {
                    foreach (var line in File.ReadLines(ListPath)
                                 .Select(line => line.Trim())
                                 .Where(line => line.Length > 0))
                        Entries.Add(line);
                }
                Status = Entries.Count == 0
                    ? "Список пуст. Добавьте первый домен через поле ввода."
                    : $"Записей: {Entries.Count}. Файл: {SelectedList.FileName}";
            }
            catch (Exception ex)
            {
                Status = "Не удалось прочитать список: " + ex.Message;
            }
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
        }

        private void QuickAddDomain()
        {
            if (string.IsNullOrWhiteSpace(QuickDomainInput)) return;
            var clean = CleanDomain(QuickDomainInput);
            if (string.IsNullOrWhiteSpace(clean)) return;

            if (Entries.Any(entry => entry.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                Status = $"«{clean}» уже есть в текущем списке.";
                return;
            }

            Entries.Insert(0, clean);
            SelectedEntry = clean;
            QuickDomainInput = "";
            Save();
            Status = $"Домен «{clean}» успешно добавлен и сохранён в {SelectedList.FileName}!";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
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
            var dialog = new InputDialog(
                "Добавить запись в список",
                "Введите домен или IP-адрес:")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true) return;

            var value = CleanDomain(dialog.Value);
            if (string.IsNullOrWhiteSpace(value)) return;
            if (Entries.Any(entry => entry.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "Такая запись уже есть в текущем списке.";
                return;
            }

            Entries.Add(value);
            SelectedEntry = value;
            Status = "Запись добавлена в редактор. Нажмите «Сохранить список».";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
        }

        private void EditEntry(object? parameter)
        {
            var oldValue = parameter as string ?? SelectedEntry;
            if (string.IsNullOrWhiteSpace(oldValue)) return;

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
            Status = "Запись изменена в редакторе. Нажмите «Сохранить список».";
        }

        private void RemoveEntry(object? parameter)
        {
            var value = parameter as string ?? SelectedEntry;
            if (string.IsNullOrWhiteSpace(value)) return;
            Entries.Remove(value);
            SelectedEntry = null;
            Status = $"Запись «{value}» удалена из редактора. Нажмите «Сохранить список».";
            Raise(nameof(HasEntries));
            Raise(nameof(CountText));
            RaiseCommands();
        }

        private void SortEntries()
        {
            var sorted = Entries.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            Entries.Clear();
            foreach (var s in sorted) Entries.Add(s);
            Status = "Список отсортирован по алфавиту (A-Z). Нажмите «Сохранить список».";
        }

        private void DeduplicateEntries()
        {
            var initial = Entries.Count;
            var distinct = Entries
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Entries.Clear();
            foreach (var s in distinct) Entries.Add(s);
            var removed = initial - distinct.Count;
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
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                var added = 0;
                foreach (var line in File.ReadLines(dialog.FileName).Select(CleanDomain).Where(s => s.Length > 0))
                {
                    if (!Entries.Any(e => e.Equals(line, StringComparison.OrdinalIgnoreCase)))
                    {
                        Entries.Add(line);
                        added++;
                    }
                }

                Status = $"Импортировано {added} новых записей. Нажмите «Сохранить список».";
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
            try
            {
                Directory.CreateDirectory(ListsFolder);
                File.WriteAllLines(ListPath, Entries);
                Status = $"Список «{SelectedList.FileName}» успешно сохранён ({Entries.Count} записей). Перезапустите обход для применения.";
            }
            catch (Exception ex)
            {
                Status = "Не удалось сохранить список: " + ex.Message;
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

            var res = await _main.Bypass.StartAsync(strat, EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
            Status = res.Ok ? "Обход успешно перезапущен с новыми списками!" : "Ошибка перезапуска: " + res.Message;
            _main.Home.RefreshStatus();
        }

        private async Task UpdateListsFromGithubAsync()
        {
            if (IsUpdatingLists) return;
            IsUpdatingLists = true;
            DomainListUpdateResultText = "Загружаю свежие списки с GitHub…";
            DomainListUpdateResultKey = "Info";

            try
            {
                var progress = new Progress<string>(msg => DomainListUpdateResultText = msg);
                var res = await DomainListUpdater.UpdateAllAsync(Settings.EnginePath, progress);
                DomainListUpdateResultText = res.Message;
                DomainListUpdateResultKey = res.Ok ? "Success" : "Danger";
                if (res.Ok)
                {
                    LoadEntries();
                }
            }
            finally
            {
                IsUpdatingLists = false;
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

            var (ok, message) = await DnsManagementService.ApplyDnsProfileAsync(SelectedDnsProfile);
            DnsTestStatusText = message;
            DnsTestStatusKey = ok ? "Success" : "Danger";
            RefreshCurrentDns();
        }

        private async Task ResetDnsToDhcpAsync()
        {
            var dhcpProfile = DnsProfiles.FirstOrDefault(p => p.IsDhcp) ?? new DnsProfile { IsDhcp = true };
            DnsTestStatusText = "Сбрасываю DNS на автоматический режим (DHCP)…";
            DnsTestStatusKey = "Info";

            var (ok, message) = await DnsManagementService.ApplyDnsProfileAsync(dhcpProfile);
            DnsTestStatusText = message;
            DnsTestStatusKey = ok ? "Success" : "Danger";
            RefreshCurrentDns();
        }

        private async Task TestDnsServerAsync()
        {
            if (IsTestingDns) return;
            IsTestingDns = true;
            DnsTestStatusText = "Тестирую скорость ответа и резолвинг DNS…";
            DnsTestStatusKey = "Info";

            try
            {
                var ip = SelectedDnsProfile?.PrimaryServer ?? "";
                var res = await DnsManagementService.TestDnsServerAsync(ip, "www.youtube.com");
                DnsTestStatusText = res.Ok
                    ? $"DNS проверен успешно: задержка {res.Milliseconds} мс (резолв youtube.com -> {res.ResolvedIp})"
                    : $"Ошибка DNS: {res.Message}";
                DnsTestStatusKey = res.Ok ? "Success" : "Danger";
            }
            finally
            {
                IsTestingDns = false;
            }
        }

        private void RaiseCommands()
        {
            (AddEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (QuickAddDomainCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (EditEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SortEntriesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeduplicateEntriesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportListCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
