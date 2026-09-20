using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        private string _providerName = "";
        private string _providerAsn = "";
        private int _providerConfidenceIndex;
        private string _status = "Изменения сохраняются автоматически";
        private bool _isProviderLookupBusy;

        public SettingsViewModel(MainViewModel main)
        {
            _main = main;
            _enginePath = main.Settings.EnginePath;
            _themeIndex = (int)main.Settings.Theme;
            SyncProviderDraft();
            LastBackupText = GetBackupText();

            BrowseEnginePathCommand = new RelayCommand(BrowseEnginePath);
            DetectEngineCommand = new RelayCommand(DetectEngine);
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            OpenSettingsFileCommand = new RelayCommand(() => Shell.OpenInNotepad(AppPaths.SettingsFile));
            OpenLogsFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.LogDir));
            OpenBackupFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.BackupDir));
            ResetSettingsCommand = new RelayCommand(ResetSettings);
            ValidateEngineCommand = new RelayCommand(ValidateEngine);
            OpenRepoCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl));
            OpenHostsCommand = new RelayCommand(() => Shell.OpenInNotepad(EngineService.SystemHostsPath));
            SaveProviderContextCommand = new RelayCommand(SaveProviderContext);
            ClearProviderContextCommand = new RelayCommand(ClearProviderContext);
            LookupProviderCommand = new AsyncRelayCommand(LookupProviderAsync, () => !IsProviderLookupBusy);
            RerunFirstLaunchWizardCommand = new RelayCommand(RerunFirstLaunchWizard);
        }

        public AppSettings Settings => _main.Settings;

        private ProviderContext Provider => Settings.ProviderContext ??= new ProviderContext();

        public string ProviderName
        {
            get => _providerName;
            set => Set(ref _providerName, value ?? "");
        }

        public string ProviderAsn
        {
            get => _providerAsn;
            set => Set(ref _providerAsn, value ?? "");
        }

        public string[] ProviderConfidenceOptions { get; } = { "Не указана", "Низкая", "Средняя", "Высокая" };

        public int ProviderConfidenceIndex
        {
            get => _providerConfidenceIndex;
            set => Set(ref _providerConfidenceIndex, Math.Clamp(value, 0, 3));
        }

        public string ProviderSourceText => Provider.SourceText;
        public string ProviderCheckedAtText => Provider.CheckedAt.HasValue
            ? "Проверено: " + Provider.CheckedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "Время проверки не указано";
        public string ProviderContextText => Provider.DisplayText;
        public bool HasProviderContext => Provider.IsKnown;

        public bool IsProviderLookupBusy
        {
            get => _isProviderLookupBusy;
            private set
            {
                if (Set(ref _isProviderLookupBusy, value))
                    (LookupProviderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ICommand SaveProviderContextCommand { get; }
        public ICommand ClearProviderContextCommand { get; }
        public ICommand LookupProviderCommand { get; }

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

        public int ZoomPercent
        {
            get => Math.Clamp(Settings.InterfaceZoomPercent, 80, 140);
            set
            {
                Settings.InterfaceZoomPercent = Math.Clamp(value, 80, 140);
                SettingsStore.Save(Settings);
                _main.Notify(nameof(MainViewModel.InterfaceZoom));
                Raise(nameof(ZoomPercent));
                Status = "Масштаб интерфейса: " + Settings.InterfaceZoomPercent + "%";
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
            set
            {
                Settings.AutoStartBypass = Settings.SafeMode ? false : value;
                OnSettingChanged();
            }
        }

        public bool SafeMode
        {
            get => Settings.SafeMode;
            set
            {
                Settings.SafeMode = value;
                if (value) Settings.AutoStartBypass = false;
                OnSettingChanged();
                _main.RefreshReadiness();
            }
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

        public bool AutoTestStrategiesOnFirstLaunch
        {
            get => Settings.AutoTestStrategiesOnFirstLaunch;
            set { Settings.AutoTestStrategiesOnFirstLaunch = value; OnSettingChanged(); }
        }

        public bool AutoDiagnoseOnFirstLaunch
        {
            get => Settings.AutoDiagnoseOnFirstLaunch;
            set { Settings.AutoDiagnoseOnFirstLaunch = value; OnSettingChanged(); }
        }

        public bool ResourceMonitoringEnabled
        {
            get => _main.Monitoring.ResourceMonitoringEnabled;
            set => _main.Monitoring.ResourceMonitoringEnabled = value;
        }

        public bool AutoRecoverStrategy
        {
            get => _main.Monitoring.AutoRecoverStrategy;
            set => _main.Monitoring.AutoRecoverStrategy = value;
        }

        public bool MonitorNotificationsEnabled
        {
            get => Settings.MonitorNotificationsEnabled;
            set { Settings.MonitorNotificationsEnabled = value; OnSettingChanged(); }
        }

        public int MonitoringIntervalMinutes
        {
            get => _main.Monitoring.MonitoringIntervalMinutes;
            set => _main.Monitoring.MonitoringIntervalMinutes = value;
        }

        public bool WatchdogEnabled
        {
            get => Settings.WatchdogEnabled;
            set
            {
                Settings.WatchdogEnabled = value;
                OnSettingChanged();
                if (value && !Settings.SafeMode) _main.Watchdog.Start();
                else _main.Watchdog.Stop();
            }
        }

        public int WatchdogIntervalSeconds
        {
            get => Settings.WatchdogIntervalSeconds;
            set { Settings.WatchdogIntervalSeconds = Math.Clamp(value, 5, 120); OnSettingChanged(); }
        }

        public bool WatchdogAutoRestart
        {
            get => Settings.WatchdogAutoRestart;
            set { Settings.WatchdogAutoRestart = value; OnSettingChanged(); }
        }

        public bool WatchdogNotifyUser
        {
            get => Settings.WatchdogNotifyUser;
            set { Settings.WatchdogNotifyUser = value; OnSettingChanged(); }
        }

        public bool RealTimePingEnabled
        {
            get => Settings.RealTimePingEnabled;
            set
            {
                Settings.RealTimePingEnabled = value;
                OnSettingChanged();
                _main.Notify(nameof(MainViewModel.RealTimePingVisible));
            }
        }

        public int RealTimePingIntervalSeconds
        {
            get => Settings.RealTimePingIntervalSeconds;
            set { Settings.RealTimePingIntervalSeconds = Math.Clamp(value, 3, 120); OnSettingChanged(); }
        }

        public int StartupDelaySeconds
        {
            get => Settings.StartupDelaySeconds;
            set { Settings.StartupDelaySeconds = Math.Clamp(value, 0, 60); OnSettingChanged(); }
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
        public ICommand OpenSettingsFileCommand { get; }
        public ICommand OpenLogsFolderCommand { get; }
        public ICommand OpenBackupFolderCommand { get; }
        public ICommand ResetSettingsCommand { get; }
        public ICommand ValidateEngineCommand { get; }
        public ICommand OpenRepoCommand { get; }
        public ICommand OpenHostsCommand { get; }
        public ICommand RerunFirstLaunchWizardCommand { get; }

        public void Reload()
        {
            _enginePath = Settings.EnginePath;
            Raise(nameof(EnginePath));
            _themeIndex = (int)Settings.Theme;
            SyncProviderDraft();
            Raise(nameof(ThemeIndex));
            Raise(nameof(ZoomPercent));
            RaiseProviderContext();
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
            Raise(nameof(SafeMode));
            Raise(nameof(StopBypassOnExit));
            Raise(nameof(ShowWinwsConsole));
            Raise(nameof(AutoCheckEngineUpdates));
            Raise(nameof(IncludePrerelease));
            Raise(nameof(PreserveUserDataOnUpdate));
            Raise(nameof(ConfirmOnStop));
            Raise(nameof(UseGameFilterOnStart));
            Raise(nameof(AutoTestStrategiesOnFirstLaunch));
            Raise(nameof(AutoDiagnoseOnFirstLaunch));
            Raise(nameof(ResourceMonitoringEnabled));
            Raise(nameof(AutoRecoverStrategy));
            Raise(nameof(MonitorNotificationsEnabled));
            Raise(nameof(MonitoringIntervalMinutes));
            Raise(nameof(WatchdogEnabled));
            Raise(nameof(WatchdogIntervalSeconds));
            Raise(nameof(WatchdogAutoRestart));
            Raise(nameof(WatchdogNotifyUser));
            Raise(nameof(RealTimePingEnabled));
            Raise(nameof(RealTimePingIntervalSeconds));
            Raise(nameof(StartupDelaySeconds));
            Raise(nameof(RunAtStartup));
            Raise(nameof(ProviderName));
            Raise(nameof(ProviderAsn));
            Raise(nameof(ProviderConfidenceIndex));
            Raise(nameof(ProviderSourceText));
            Raise(nameof(ProviderCheckedAtText));
            Raise(nameof(ProviderContextText));
            Raise(nameof(HasProviderContext));
        }

        private void SaveProviderContext()
        {
            var name = _providerName.Trim();
            var asn = _providerAsn.Trim();
            if (name.Length > 120)
            {
                Status = "Название провайдера слишком длинное";
                return;
            }

            if (asn.Length > 0 && !Regex.IsMatch(asn, @"^(?:AS)?\d{1,10}$", RegexOptions.IgnoreCase))
            {
                Status = "ASN должен иметь вид AS12345 или 12345";
                return;
            }

            if (asn.Length > 0 && !asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                asn = "AS" + asn;

            var known = name.Length > 0 || asn.Length > 0;
            Settings.ProviderContext = new ProviderContext
            {
                Name = name,
                Asn = asn.ToUpperInvariant(),
                Source = known ? ProviderContextSource.UserInput : ProviderContextSource.Unknown,
                CheckedAt = known ? DateTime.UtcNow : null,
                Confidence = known ? ProviderConfidenceValue(_providerConfidenceIndex) : 0
            };
            SyncProviderDraft();
            SettingsStore.Save(Settings);
            Status = known ? "Контекст провайдера сохранён" : "Провайдерский контекст очищен";
            RaiseProviderContext();
        }

        private async Task LookupProviderAsync()
        {
            var answer = System.Windows.MessageBox.Show(
                "Разрешить запрос к внешнему сервису ipapi.co? Сервис увидит ваш внешний IP и может определить ASN/организацию. Полученные данные будут сохранены с пометкой «внешний источник».",
                "Определение провайдера через внешний сервис", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            IsProviderLookupBusy = true;
            Status = "Запрашиваю ASN через внешний сервис…";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretGUI/1.2");
                using var response = await client.GetAsync("https://ipapi.co/json/").ConfigureAwait(true);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                var root = document.RootElement;
                var name = ReadJsonString(root, "org");
                if (string.IsNullOrWhiteSpace(name)) name = ReadJsonString(root, "isp");
                var asn = ReadJsonString(root, "asn").ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(asn) && !asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                    asn = "AS" + asn;

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(asn))
                {
                    Status = "Внешний сервис не вернул ASN или название организации";
                    return;
                }

                Settings.ProviderContext = new ProviderContext
                {
                    Name = name.Trim(),
                    Asn = asn.Trim(),
                    Source = ProviderContextSource.ExternalService,
                    CheckedAt = DateTime.UtcNow,
                    Confidence = 70
                };
                SyncProviderDraft();
                SettingsStore.Save(Settings);
                Status = "Провайдерский контекст получен из внешнего источника и сохранён";
                RaiseProviderContext();
            }
            catch (Exception ex)
            {
                Status = "Не удалось получить провайдерский контекст: " + ex.Message;
            }
            finally
            {
                IsProviderLookupBusy = false;
            }
        }

        private static string ReadJsonString(JsonElement root, string property)
            => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private void ClearProviderContext()
        {
            Settings.ProviderContext = new ProviderContext();
            SyncProviderDraft();
            SettingsStore.Save(Settings);
            Status = "Провайдерский контекст очищен";
            RaiseProviderContext();
        }

        private void SyncProviderDraft()
        {
            var context = Provider;
            _providerName = context.Name;
            _providerAsn = context.Asn;
            _providerConfidenceIndex = context.Confidence switch { >= 90 => 3, >= 60 => 2, > 0 => 1, _ => 0 };
        }

        private static int ProviderConfidenceValue(int index)
            => index switch { 3 => 95, 2 => 70, 1 => 35, _ => 0 };

        private void RaiseProviderContext()
        {
            Raise(nameof(ProviderName));
            Raise(nameof(ProviderAsn));
            Raise(nameof(ProviderConfidenceIndex));
            Raise(nameof(ProviderSourceText));
            Raise(nameof(ProviderCheckedAtText));
            Raise(nameof(ProviderContextText));
            Raise(nameof(HasProviderContext));
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
            _main.Settings.InterfaceZoomPercent = 100;
            _main.Settings.CloseToTray = false;
            _main.Settings.StartMinimized = false;
            _main.Settings.AutoStartBypass = false;
            _main.Settings.SafeMode = false;
            _main.Settings.FirstLaunchWizardCompleted = false;
            _main.Settings.StopBypassOnExit = false;
            _main.Settings.ShowWinwsConsole = false;
            _main.Settings.AutoCheckEngineUpdates = true;
            _main.Settings.IncludePrerelease = false;
            _main.Settings.PreserveUserDataOnUpdate = true;
            _main.Settings.ConfirmOnStop = false;
            _main.Settings.AutoTestStrategiesOnFirstLaunch = true;
            _main.Settings.AutoDiagnoseOnFirstLaunch = true;
            _main.Settings.StrategyTestsCompleted = false;
            _main.Settings.FirstLaunchDiagnosticsCompleted = false;
            _main.Settings.PreviousSelectedStrategy = "";
            _main.Settings.ResourceMonitoringEnabled = false;
            _main.Settings.AutoRecoverStrategy = true;
            _main.Settings.MonitorNotificationsEnabled = true;
            _main.Settings.ResourceMonitoringIntervalMinutes = 15;
            _main.Settings.MonitorTargets.Clear();
            _main.Settings.ProviderContext = new ProviderContext();

            SettingsStore.Save(_main.Settings);
            _main.Monitoring.Reload();
            Reload();
            ThemeService.Apply(_main.Settings.Theme);
            Status = "Настройки сброшены";
        }

        private void RerunFirstLaunchWizard()
        {
            var answer = System.Windows.MessageBox.Show(
                "Запустить мастер первого запуска? Вы сможете снова пройти все шаги настройки по порядку.",
                "Мастер настройки", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            _main.Settings.FirstLaunchWizardCompleted = false;
            SettingsStore.Save(_main.Settings);
            _main.FirstLaunch.Reset();
            _main.Navigate("first-run");
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
