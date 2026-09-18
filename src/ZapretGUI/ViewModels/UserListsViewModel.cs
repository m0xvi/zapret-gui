using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    /// <summary>Редактор пользовательских списков без открытия блокнота.</summary>
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
                    Title = "Домены для обхода",
                    FileName = "list-general-user.txt",
                    Description = "Домены и IP, которые нужно передать обходу. По одному значению в строке."
                },
                new UserListOption
                {
                    Key = "exclude",
                    Title = "Исключения из обхода",
                    FileName = "list-exclude-user.txt",
                    Description = "Домены и IP, которые не нужно обрабатывать обходом."
                },
                new UserListOption
                {
                    Key = "ipset",
                    Title = "Исключения IP-фильтра",
                    FileName = "ipset-exclude-user.txt",
                    Description = "IP-адреса, которые нужно исключить из фильтра ipset."
                }
            };

            AddEntryCommand = new RelayCommand(AddEntry);
            EditEntryCommand = new RelayCommand(EditEntry);
            RemoveEntryCommand = new RelayCommand(RemoveEntry, _ => SelectedEntry != null);
            SaveCommand = new RelayCommand(Save);
            ReloadCommand = new RelayCommand(LoadEntries);
            OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(ListsFolder));
            LoadEntries();
        }

        public AppSettings Settings => _main.Settings;
        public UserListOption[] ListOptions { get; }
        public ObservableCollection<string> Entries { get; } = new();

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
    }
}
