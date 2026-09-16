using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _enginePath;
        private int _themeIndex;
        private string _status = "Изменения сохраняются автоматически";

        public SettingsViewModel(MainViewModel main)
        {
            _main = main;
            _enginePath = main.Settings.EnginePath;
            _themeIndex = (int)main.Settings.Theme;
            LastBackupText = GetBackupText();

            BrowseEnginePathCommand = new RelayCommand(BrowseEnginePath);
            DetectEngineCommand = new RelayCommand(DetectEngine);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            OpenListsFolderCommand = new RelayCommand(() => Shell.OpenFolder(Path.Combine(Settings.EnginePath, "lists")));
            EditUserListCommand = new RelayCommand(param => EditUserList(param as string ?? ""));
            OpenSettingsFileCommand = new RelayCommand(() => Shell.OpenInNotepad(AppPaths.SettingsFile));
            OpenLogsFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.LogDir));
            OpenBackupFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.BackupDir));
            ResetSettingsCommand = new RelayCommand(ResetSettings);
            ValidateEngineCommand = new RelayCommand(ValidateEngine);
            OpenRepoCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl));
            OpenHostsCommand = new RelayCommand(() => Shell.OpenInNotepad(EngineService.SystemHostsPath));
        }

        public AppSettings Settings => _main.Settings;

        public string[] ThemeOptions { get; } = { "Как в системе", "Тёмная", "Светлая" };

        public string EnginePath
        {
            get => _enginePath;
            set
            {
                if (!Set(ref _enginePath, value)) return;
                Settings.EnginePath = value;
                SettingsStore.Save(Settings);
                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
                Raise(nameof(EngineInfoText));
                Raise(nameof(EngineReady));
            }
        }

        public bool EngineReady => EngineService.IsEngineReady(Settings.EnginePath);

        public string EngineInfoText
        {
            get
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                var strategies = _main.Strategies.Items.Count;
                var versionText = string.IsNullOrWhiteSpace(version) ? "версия неизвестна" : "версия " + version;
                return EngineService.IsEngineReady(Settings.EnginePath)
                    ? $"Движок найден: {versionText}, стратегий: {strategies}"
                    : "В этой папке нет winws.exe — движок не готов к работе";
            }
        }

        public int ThemeIndex
        {
            get => _themeIndex;
            set
            {
                if (!Set(ref _themeIndex, value)) return;
                Settings.Theme = (ThemeMode)Math.Clamp(value, 0, 2);
                ThemeService.Apply(Settings.Theme);
                SettingsStore.Save(Settings);
                _main.Notify(nameof(MainViewModel.ThemeText));
                Status = "Тема применена: " + ThemeService.ModeText(Settings.Theme);
            }
        }

        public bool CloseToTray
        {
            get => Settings.CloseToTray;
            set { Settings.CloseToTray = value; OnSettingChanged(); }
        }

        public bool StartMinimized
        {
            get => Settings.StartMinimized;
            set { Settings.StartMinimized = value; OnSettingChanged(); }
        }

        public bool AutoStartBypass
        {
            get => Settings.AutoStartBypass;
            set { Settings.AutoStartBypass = value; OnSettingChanged(); }
        }

        public bool StopBypassOnExit
        {
            get => Settings.StopBypassOnExit;
            set { Settings.StopBypassOnExit = value; OnSettingChanged(); }
        }

        public bool ShowWinwsConsole
        {
            get => Settings.ShowWinwsConsole;
            set { Settings.ShowWinwsConsole = value; OnSettingChanged(); }
        }

        public bool AutoCheckEngineUpdates
        {
            get => Settings.AutoCheckEngineUpdates;
            set { Settings.AutoCheckEngineUpdates = value; OnSettingChanged(); }
        }

        public bool IncludePrerelease
        {
            get => Settings.IncludePrerelease;
            set { Settings.IncludePrerelease = value; OnSettingChanged(); }
        }

        public bool PreserveUserDataOnUpdate
        {
            get => Settings.PreserveUserDataOnUpdate;
            set { Settings.PreserveUserDataOnUpdate = value; OnSettingChanged(); }
        }

        public bool ConfirmOnStop
        {
            get => Settings.ConfirmOnStop;
            set { Settings.ConfirmOnStop = value; OnSettingChanged(); }
        }

        public bool UseGameFilterOnStart
        {
            get => Settings.UseGameFilterOnStart;
            set { Settings.UseGameFilterOnStart = value; OnSettingChanged(); }
        }

        /// <summary>Автозапуск приложения: планировщик задач (нужны права администратора).</summary>
        public bool RunAtStartup
        {
            get => Settings.RunAtStartup;
            set
            {
                Settings.RunAtStartup = value;
                OnSettingChanged();
                ApplyAutostart(value);
            }
        }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public string LastBackupText { get; }

        public string SettingsFilePath => AppPaths.SettingsFile;
        public string LogsPath => AppPaths.LogDir;
        public string BackupPath => AppPaths.BackupDir;

        public ICommand BrowseEnginePathCommand { get; }
        public ICommand DetectEngineCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand OpenListsFolderCommand { get; }
        public ICommand EditUserListCommand { get; }
        public ICommand OpenSettingsFileCommand { get; }
        public ICommand OpenLogsFolderCommand { get; }
        public ICommand OpenBackupFolderCommand { get; }
        public ICommand ResetSettingsCommand { get; }
        public ICommand ValidateEngineCommand { get; }
        public ICommand OpenRepoCommand { get; }
        public ICommand OpenHostsCommand { get; }

        public void Reload()
        {
            _enginePath = Settings.EnginePath;
            Raise(nameof(EnginePath));
            _themeIndex = (int)Settings.Theme;
            Raise(nameof(ThemeIndex));
            Raise(nameof(EngineInfoText));
            Raise(nameof(EngineReady));
        }

        private void OnSettingChanged()
        {
            SettingsStore.Save(Settings);
            Status = "Настройки сохранены";
            Raise(nameof(CloseToTray));
            Raise(nameof(StartMinimized));
            Raise(nameof(AutoStartBypass));
            Raise(nameof(StopBypassOnExit));
            Raise(nameof(ShowWinwsConsole));
            Raise(nameof(AutoCheckEngineUpdates));
            Raise(nameof(IncludePrerelease));
            Raise(nameof(PreserveUserDataOnUpdate));
            Raise(nameof(ConfirmOnStop));
            Raise(nameof(UseGameFilterOnStart));
            Raise(nameof(RunAtStartup));
        }

        private void BrowseEnginePath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку с движком zapret (там, где лежат bin и lists)",
                InitialDirectory = Directory.Exists(Settings.EnginePath) ? Settings.EnginePath : AppPaths.DefaultEngine
            };

            if (dialog.ShowDialog() != true) return;

            EnginePath = dialog.FolderName;
            Status = EngineReady ? "Движок найден" : "В выбранной папке нет bin\\winws.exe";
        }

        /// <summary>Ищет уже распакованную сборку zapret рядом с приложением и в стандартных местах.</summary>
        private void DetectEngine()
        {
            var candidates = new List<string>
            {
                AppPaths.DefaultEngine,
                Path.Combine(AppContext.BaseDirectory, "engine"),
                AppContext.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };

            try
            {
                var downloads = candidates[^1];
                if (Directory.Exists(downloads))
                {
                    candidates.AddRange(Directory.GetDirectories(downloads, "zapret*")
                        .OrderByDescending(Directory.GetLastWriteTime));
                }
            }
            catch { }

            var found = candidates.FirstOrDefault(EngineService.IsEngineReady);
            if (found != null)
            {
                EnginePath = found;
                Status = "Найден движок: " + found;
                return;
            }

            // Пробуем поискать вложенные папки вида zapret-discord-youtube-1.10.2
            foreach (var root in candidates.Where(Directory.Exists))
            {
                try
                {
                    var nested = Directory.GetDirectories(root, "zapret*").FirstOrDefault(EngineService.IsEngineReady);
                    if (nested != null)
                    {
                        EnginePath = nested;
                        Status = "Найден движок: " + nested;
                        return;
                    }
                }
                catch { }
            }

            Status = "Готовый движок не найден — скачайте его на странице «Обновления»";
        }

        private void EditUserList(string which)
        {
            var file = which switch
            {
                "exclude" => "list-exclude-user.txt",
                "ipset" => "ipset-exclude-user.txt",
                _ => "list-general-user.txt"
            };
            var path = Path.Combine(Settings.EnginePath, "lists", file);
            Shell.OpenInNotepad(path);
            Status = "Изменения в списках применяются после перезапуска обхода";
        }

        private void ValidateEngine()
        {
            if (EngineService.IsEngineReady(Settings.EnginePath))
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                Status = $"Движок готов к работе (версия {version})";
            }
            else
            {
                Status = "Не найдены bin\\winws.exe или WinDivert64.sys";
            }
            Raise(nameof(EngineInfoText));
            Raise(nameof(EngineReady));
        }

        private void ResetSettings()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Сбросить все настройки приложения к значениям по умолчанию?",
                "Сброс настроек", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var fresh = new AppSettings();
            _main.Settings.EnginePath = fresh.EnginePath;
            _main.Settings.Theme = fresh.Theme;
            _main.Settings.CloseToTray = false;
            _main.Settings.StartMinimized = false;
            _main.Settings.AutoStartBypass = false;
            _main.Settings.StopBypassOnExit = false;
            _main.Settings.ShowWinwsConsole = false;
            _main.Settings.AutoCheckEngineUpdates = true;
            _main.Settings.IncludePrerelease = false;
            _main.Settings.PreserveUserDataOnUpdate = true;
            _main.Settings.ConfirmOnStop = false;

            SettingsStore.Save(_main.Settings);
            Reload();
            ThemeService.Apply(_main.Settings.Theme);
            Status = "Настройки сброшены";
        }

        private void ApplyAutostart(bool enabled)
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exe)) return;

                if (enabled)
                {
                    var result = Shell.Run("schtasks", new[]
                    {
                        "/create", "/tn", "ZapretGUI", "/tr", "\"" + exe + "\"",
                        "/sc", "onlogon", "/rl", "highest", "/f"
                    }, 20000);

                    Status = result.Ok
                        ? "Автозапуск включён (планировщик задач: ZapretGUI)"
                        : "Не удалось включить автозапуск: " + result.All;
                }
                else
                {
                    Shell.Run("schtasks", new[] { "/delete", "/tn", "ZapretGUI", "/f" }, 20000);
                    Status = "Автозапуск отключён";
                }
            }
            catch (Exception ex)
            {
                Status = "Ошибка настройки автозапуска: " + ex.Message;
            }
        }

        private static string GetBackupText()
        {
            try
            {
                var backups = Directory.GetFiles(AppPaths.BackupDir)
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(1)
                    .ToList();
                return backups.Count == 0
                    ? "Резервных копий пока нет"
                    : "Последняя копия: " + Path.GetFileName(backups[0]);
            }
            catch { return "Резервных копий пока нет"; }
        }
    }
}
