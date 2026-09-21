using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class ProfilesViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private UserProfile? _selectedProfile;
        private BackupArchiveInfo? _selectedBackup;
        private string _statusText = "Готово к работе";
        private string _statusKey = "Info";
        private bool _isBusy;
        private string _currentNetworkDisplay = "";
        private string _currentNetworkFingerprint = "";
        private string _autoSwitchStatus = "";

        public ProfilesViewModel(MainViewModel main)
        {
            _main = main;

            ApplyProfileCommand = new AsyncRelayCommand(ApplySelectedProfileAsync, () => SelectedProfile != null && !IsBusy);
            CreateProfileFromCurrentCommand = new RelayCommand(CreateProfileFromCurrent);
            DuplicateProfileCommand = new RelayCommand(DuplicateSelectedProfile, () => SelectedProfile != null);
            DeleteProfileCommand = new RelayCommand(DeleteSelectedProfile, () => SelectedProfile != null && !SelectedProfile.IsBuiltIn);
            ExportProfileCommand = new RelayCommand(ExportSelectedProfile, () => SelectedProfile != null);
            ImportProfileCommand = new RelayCommand(ImportProfile);

            CreateFullBackupCommand = new AsyncRelayCommand(CreateFullBackupAsync, () => !IsBusy);
            RestoreBackupCommand = new AsyncRelayCommand(RestoreSelectedBackupAsync, () => SelectedBackup != null && !IsBusy);
            DeleteBackupCommand = new RelayCommand(DeleteSelectedBackup, () => SelectedBackup != null);
            RefreshBackupHistoryCommand = new RelayCommand(RefreshBackupHistory);
            CleanSystemCommand = new AsyncRelayCommand(CleanSystemAsync, () => !IsBusy);

            BindToCurrentNetworkCommand = new RelayCommand(BindSelectedToCurrentNetwork, () => SelectedProfile != null);
            UnbindNetworkCommand = new RelayCommand(UnbindSelectedNetwork, () => SelectedProfile != null && SelectedProfile.IsNetworkBound);
            RefreshNetworkCommand = new RelayCommand(RefreshNetwork);

            Reload();
            RefreshNetwork();
        }

        public AppSettings Settings => _main.Settings;
        public ObservableCollection<UserProfile> Profiles { get; } = new();
        public ObservableCollection<BackupArchiveInfo> BackupHistory { get; } = new();

        public string[] SubTabs { get; } = { "Профили конфигураций", "Резервные копии (ZIP)", "Портативный режим & Перенос" };

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (Set(ref _selectedTabIndex, Math.Clamp(value, 0, 2)))
                {
                    Raise(nameof(IsProfilesTabVisible));
                    Raise(nameof(IsBackupsTabVisible));
                    Raise(nameof(IsPortableTabVisible));
                }
            }
        }

        public bool IsProfilesTabVisible => SelectedTabIndex == 0;
        public bool IsBackupsTabVisible => SelectedTabIndex == 1;
        public bool IsPortableTabVisible => SelectedTabIndex == 2;

        public bool IsPortableMode => AppPaths.IsPortableMode;

        public string PortableModeStatusText => IsPortableMode
            ? "Активен портативный режим (все данные хранятся локально в папке приложения)"
            : "Стандартный режим Windows (настройки в %APPDATA%, движок в %LOCALAPPDATA%)";

        public UserProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (Set(ref _selectedProfile, value))
                {
                    (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (DuplicateProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (DeleteProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ExportProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (BindToCurrentNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (UnbindNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public BackupArchiveInfo? SelectedBackup
        {
            get => _selectedBackup;
            set
            {
                if (Set(ref _selectedBackup, value))
                {
                    (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (DeleteBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        public string StatusKey
        {
            get => _statusKey;
            private set => Set(ref _statusKey, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CreateFullBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CleanSystemCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string CurrentNetworkDisplay
        {
            get => _currentNetworkDisplay;
            private set => Set(ref _currentNetworkDisplay, value);
        }

        public string CurrentNetworkFingerprint
        {
            get => _currentNetworkFingerprint;
            private set => Set(ref _currentNetworkFingerprint, value);
        }

        public string AutoSwitchStatus
        {
            get => _autoSwitchStatus;
            private set => Set(ref _autoSwitchStatus, value);
        }

        public bool AutoSwitchOnNetworkChange
        {
            get => Settings.AutoSwitchProfileOnNetworkChange;
            set
            {
                if (Settings.AutoSwitchProfileOnNetworkChange == value) return;
                Settings.AutoSwitchProfileOnNetworkChange = value;
                SettingsStore.Save(Settings);
                Raise(nameof(AutoSwitchOnNetworkChange));
                StatusText = value ? "Автопереключение профилей при смене сети включено" : "Автопереключение при смене сети выключено";
                StatusKey = "Success";
                _main.NotifyAutoSwitchChanged();
            }
        }

        public bool AutoSwitchOnFailure
        {
            get => Settings.AutoSwitchProfileOnFailure;
            set
            {
                if (Settings.AutoSwitchProfileOnFailure == value) return;
                Settings.AutoSwitchProfileOnFailure = value;
                SettingsStore.Save(Settings);
                Raise(nameof(AutoSwitchOnFailure));
                StatusText = value ? "Автопереключение при сбое стратегии включено" : "Автопереключение при сбое выключено";
                StatusKey = "Success";
            }
        }

        public ICommand ApplyProfileCommand { get; }
        public ICommand CreateProfileFromCurrentCommand { get; }
        public ICommand DuplicateProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand ExportProfileCommand { get; }
        public ICommand ImportProfileCommand { get; }

        public ICommand CreateFullBackupCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }
        public ICommand RefreshBackupHistoryCommand { get; }
        public ICommand CleanSystemCommand { get; }
        public ICommand BindToCurrentNetworkCommand { get; }
        public ICommand UnbindNetworkCommand { get; }
        public ICommand RefreshNetworkCommand { get; }

        public void Reload()
        {
            Profiles.Clear();
            var list = ProfileManager.LoadProfiles();
            foreach (var p in list) Profiles.Add(p);
            SelectedProfile = Profiles.FirstOrDefault();

            RefreshBackupHistory();
        }

        public void RefreshBackupHistory()
        {
            BackupHistory.Clear();
            var history = BackupRestoreService.GetBackupHistory();
            foreach (var b in history) BackupHistory.Add(b);
            SelectedBackup = BackupHistory.FirstOrDefault();
        }

        private async Task ApplySelectedProfileAsync()
        {
            if (SelectedProfile == null) return;
            IsBusy = true;
            StatusText = $"Применяю профиль «{SelectedProfile.Name}»…";
            StatusKey = "Info";

            try
            {
                var (ok, msg) = await ProfileManager.ApplyProfileAsync(
                    SelectedProfile, Settings, _main.Bypass, _main.Strategies);

                StatusText = msg;
                StatusKey = ok ? "Success" : "Danger";
                _main.Home.RefreshStatus();
                _main.SettingsPage.Reload();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void CreateProfileFromCurrent()
        {
            var dialog = new Views.InputDialog(
                "Создать профиль",
                "Введите название для нового профиля на основе текущих настроек:")
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() != true) return;
            var name = (dialog.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            var profile = ProfileManager.CreateFromCurrentSettings(Settings, name, "Пользовательский профиль");
            Profiles.Add(profile);
            ProfileManager.SaveProfiles(Profiles);
            SelectedProfile = profile;
            StatusText = $"Профиль «{name}» создан";
            StatusKey = "Success";
        }

        private void DuplicateSelectedProfile()
        {
            if (SelectedProfile == null) return;

            var copy = new UserProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"{SelectedProfile.Name} (копия)",
                Description = SelectedProfile.Description,
                StrategyName = SelectedProfile.StrategyName,
                GameFilter = SelectedProfile.GameFilter,
                DnsProfileId = SelectedProfile.DnsProfileId,
                GameModeActive = SelectedProfile.GameModeActive,
                WatchdogEnabled = SelectedProfile.WatchdogEnabled,
                RealTimePingEnabled = SelectedProfile.RealTimePingEnabled,
                ProviderName = SelectedProfile.ProviderName,
                ProviderAsn = SelectedProfile.ProviderAsn,
                NetworkFingerprint = "",
                NetworkDisplayName = "",
                NetworkBoundAt = null,
                CreatedAt = DateTime.UtcNow,
                IsBuiltIn = false
            };

            Profiles.Add(copy);
            ProfileManager.SaveProfiles(Profiles);
            SelectedProfile = copy;
            StatusText = $"Создана копия профиля «{copy.Name}»";
            StatusKey = "Success";
        }

        private void DeleteSelectedProfile()
        {
            if (SelectedProfile == null || SelectedProfile.IsBuiltIn) return;

            var confirm = MessageBox.Show(
                $"Удалить профиль «{SelectedProfile.Name}»?",
                "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var toRemove = SelectedProfile;
            Profiles.Remove(toRemove);
            ProfileManager.SaveProfiles(Profiles);
            SelectedProfile = Profiles.FirstOrDefault();
            StatusText = $"Профиль «{toRemove.Name}» удалён";
            StatusKey = "Info";
        }

        private void ExportSelectedProfile()
        {
            if (SelectedProfile == null) return;

            var dialog = new SaveFileDialog
            {
                Title = "Экспорт профиля",
                FileName = $"ZapretProfile_{SelectedProfile.Name}.json",
                Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            var (ok, msg) = ProfileManager.ExportProfile(SelectedProfile, dialog.FileName);
            StatusText = msg;
            StatusKey = ok ? "Success" : "Danger";
        }

        private void ImportProfile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Импорт профиля",
                Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            var (ok, msg, profile) = ProfileManager.ImportProfile(dialog.FileName);
            if (ok && profile != null)
            {
                Profiles.Add(profile);
                ProfileManager.SaveProfiles(Profiles);
                SelectedProfile = profile;
            }
            StatusText = msg;
            StatusKey = ok ? "Success" : "Danger";
        }

        private async Task CreateFullBackupAsync()
        {
            IsBusy = true;
            StatusText = "Создаю полный архив конфигурации и списков…";
            StatusKey = "Info";

            try
            {
                var (ok, msg, _) = await BackupRestoreService.CreateFullBackupArchiveAsync(
                    Settings.EnginePath, null, "Ручной полный бэкап через GUI");

                StatusText = msg;
                StatusKey = ok ? "Success" : "Danger";
                RefreshBackupHistory();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RestoreSelectedBackupAsync()
        {
            if (SelectedBackup == null) return;

            var confirm = MessageBox.Show(
                $"Восстановить конфигурацию из архива «{SelectedBackup.FileName}»?\nТекущие настройки будут предварительно сохранены в точку отката.",
                "Восстановление бэкапа", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusText = $"Восстанавливаю конфигурацию из «{SelectedBackup.FileName}»…";
            StatusKey = "Info";

            try
            {
                var (ok, msg) = await BackupRestoreService.RestoreFullBackupArchiveAsync(
                    SelectedBackup.FilePath, Settings.EnginePath);

                StatusText = msg;
                StatusKey = ok ? "Success" : "Danger";
                Reload();
                _main.Home.ReloadFromEngine();
                _main.SettingsPage.Reload();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void DeleteSelectedBackup()
        {
            if (SelectedBackup == null) return;

            var confirm = MessageBox.Show(
                $"Удалить файл бэкапа «{SelectedBackup.FileName}»?",
                "Удаление бэкапа", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            BackupRestoreService.DeleteBackup(SelectedBackup.FilePath);
            RefreshBackupHistory();
            StatusText = "Файл бэкапа удалён";
            StatusKey = "Info";
        }

        private async Task CleanSystemAsync()
        {
            var confirm = MessageBox.Show(
                "Выполнить полную очистку системных следов перед переносом на флешку?\n\nБудут остановлены и удалены службы zapret и WinDivert, удалена задача автозапуска и сброшен DNS в режим DHCP.",
                "Очистка системы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusText = "Выполняю системную очистку служб и настроек…";
            StatusKey = "Info";

            try
            {
                var (ok, msg) = await BackupRestoreService.PerformFullSystemCleanupAsync(_main.Bypass);
                StatusText = msg;
                StatusKey = ok ? "Success" : "Danger";
                _main.Home.RefreshStatus();
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void RefreshNetwork()
        {
            try
            {
                var id = NetworkDetector.GetCurrentIdentity();
                CurrentNetworkDisplay = id.DisplayName;
                CurrentNetworkFingerprint = id.Fingerprint;
                AutoSwitchStatus = _main.ProfileAutoSwitch?.LastReason ?? "Готов к отслеживанию сети";
                Raise(nameof(CurrentNetworkDisplay));
                Raise(nameof(CurrentNetworkFingerprint));
                Raise(nameof(AutoSwitchStatus));
            }
            catch (Exception ex)
            {
                CurrentNetworkDisplay = "Ошибка: " + ex.Message;
            }
        }

        private void BindSelectedToCurrentNetwork()
        {
            if (SelectedProfile == null) return;
            var id = NetworkDetector.GetCurrentIdentity();
            if (!id.IsValid)
            {
                StatusText = "Не удалось определить текущую сеть для привязки";
                StatusKey = "Danger";
                return;
            }
            SelectedProfile.NetworkFingerprint = id.Fingerprint;
            SelectedProfile.NetworkDisplayName = id.DisplayName;
            SelectedProfile.NetworkBoundAt = DateTime.UtcNow;
            ProfileManager.SaveProfiles(Profiles);
            RefreshNetwork();
            Raise(nameof(SelectedProfile));
            (UnbindNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            StatusText = $"Профиль «{SelectedProfile.Name}» привязан к сети «{id.DisplayName}»";
            StatusKey = "Success";
            // Обновляем отображение в списке
            var idx = Profiles.IndexOf(SelectedProfile);
            if (idx >= 0) { Profiles[idx] = SelectedProfile; }
            Reload();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == SelectedProfile.Id);
        }

        private void UnbindSelectedNetwork()
        {
            if (SelectedProfile == null || !SelectedProfile.IsNetworkBound) return;
            SelectedProfile.NetworkFingerprint = "";
            SelectedProfile.NetworkDisplayName = "";
            SelectedProfile.NetworkBoundAt = null;
            ProfileManager.SaveProfiles(Profiles);
            Raise(nameof(SelectedProfile));
            (UnbindNetworkCommand as RelayCommand)?.RaiseCanExecuteChanged();
            StatusText = $"Привязка профиля «{SelectedProfile.Name}» к сети снята";
            StatusKey = "Info";
            Reload();
        }
    }
}
