using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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

    /// <summary>Управление списками доменов, IP и параметрами фильтрации трафика.</summary>
    public sealed class UserListsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _selectedListKey = "general";
        private string _status = "Изменения сохраняются только после нажатия «Сохранить список».";
        private string? _selectedEntry;

        public UserListsViewModel(MainViewModel main)
        {
            _main = main;
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

            AddEntryCommand = new RelayCommand(AddEntry);
            EditEntryCommand = new RelayCommand(EditEntry);
            RemoveEntryCommand = new RelayCommand(RemoveEntry, _ => SelectedEntry != null);
            SaveCommand = new RelayCommand(Save);
            ReloadCommand = new RelayCommand(LoadEntries);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(ListsFolder));
            RestartBypassCommand = new AsyncRelayCommand(RestartBypassAsync);
            LoadEntries();
        }

        public AppSettings Settings => _main.Settings;
        public UserListOption[] ListOptions { get; }
        public ObservableCollection<string> Entries { get; } = new();

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

        public ICommand AddEntryCommand { get; }
        public ICommand EditEntryCommand { get; }
        public ICommand RemoveEntryCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ReloadCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand RestartBypassCommand { get; }

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
                    ? "Список пуст. Добавьте первую запись отдельным окном."
                    : $"Записей: {Entries.Count}. Изменения ещё не сохранены.";
            }
            catch (Exception ex)
            {
                Status = "Не удалось прочитать список: " + ex.Message;
            }
            Raise(nameof(HasEntries));
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

            var value = dialog.Value;
            if (Entries.Any(entry => entry.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "Такая запись уже есть в текущем списке.";
                return;
            }

            Entries.Add(value);
            SelectedEntry = value;
            Status = "Запись добавлена в редактор. Нажмите «Сохранить список».";
            Raise(nameof(HasEntries));
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

            var newValue = dialog.Value;
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
            Status = "Запись удалена из редактора. Нажмите «Сохранить список».";
            Raise(nameof(HasEntries));
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(ListsFolder);
                File.WriteAllLines(ListPath, Entries.Where(entry => !string.IsNullOrWhiteSpace(entry)));
                Status = $"Список сохранён: {SelectedList.FileName}. Перезапустите обход, чтобы применить изменения.";
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
                Status = "Обход выключен. Включите его на странице «Обзор».";
                return;
            }

            var strategy = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
            if (strategy == null)
            {
                Status = "Стратегия не выбрана.";
                return;
            }

            Status = "Перезапускаю обход с обновлёнными списками и фильтрами…";
            var result = await _main.Bypass.StartAsync(strategy,
                EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
            Status = result.Message;
            _main.Home.RefreshStatus();
        }
    }
}
