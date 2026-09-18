using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ZapretGui.Core;

namespace ZapretGui.ViewModels
{
    public sealed class UpdatesViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private CancellationTokenSource? _cts;

        private string _latestVersion = "";
        private GuiReleaseInfo? _guiRelease;
        private bool _guiUpdateAvailable;
        private string _guiUpdateStatus = "Проверка обновлений GUI ещё не выполнялась";
        private string _releaseTitle = "";
        private string _releaseDate = "";
        private string _releaseNotes = "";
        private string _releaseUrl = "";
        private bool _updateAvailable;
        private bool _isBusy;
        private double _progress;
        private bool _indeterminate;
        private string _status = "Нажмите «Проверить обновления»";
        private string _hostsStatus = "Не проверялось";
        private bool _hostsNeedsUpdate;
        private string _hostsFile = "";
        private DateTime? _lastCheck;
        private string _message = "";
        private string _messageKey = "Info";
        private string _operationResultTitle = "";
        private string _operationResultDetails = "";
        private string _operationResultKey = "Info";
        private bool _operationResultVisible;
        private EngineConsistencyReport _consistency = new();
        private EngineBackupInfo? _selectedEngineBackup;

        public UpdatesViewModel(MainViewModel main)
        {
            _main = main;

            CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
            CheckGuiUpdateCommand = new AsyncRelayCommand(CheckGuiUpdateAsync, () => !IsBusy);
            UpdateGuiCommand = new AsyncRelayCommand(UpdateGuiAsync, () => !IsBusy && GuiUpdateAvailable);
            UpdateEngineCommand = new AsyncRelayCommand(UpdateEngineAsync, () => !IsBusy && HasEnginePackage && UpdateAvailable);
            RollbackEngineCommand = new AsyncRelayCommand(RollbackEngineAsync,
                () => !IsBusy && SelectedEngineBackup != null);
            UpdateIpsetCommand = new AsyncRelayCommand(UpdateIpsetAsync, () => !IsBusy);
            CheckHostsCommand = new AsyncRelayCommand(CheckHostsAsync, () => !IsBusy);
            ApplyHostsCommand = new RelayCommand(ApplyHosts, () => !IsBusy && _hostsFile.Length > 0);
            OpenReleasePageCommand = new RelayCommand(() => Shell.OpenUrl(string.IsNullOrEmpty(_releaseUrl) ? EngineService.RepoUrl + "/releases/latest" : _releaseUrl));
            OpenRepoCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl));
            OpenDownloadsPageCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl + "/releases/latest"));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
            OpenLogsCommand = new RelayCommand(() => _main.Navigate("logs"));
            RefreshEngineBackups();
            RefreshConsistency();
        }

        public AppSettings Settings => _main.Settings;

        public string EngineVersion
        {
            get
            {
                var version = EngineService.ReadVersion(Settings.EnginePath);
                return string.IsNullOrWhiteSpace(version) ? "не установлен" : version;
            }
        }

        public string EnginePathText => Settings.EnginePath;
        public string GuiCurrentVersion => GuiUpdateService.CurrentVersion;
        public string GuiLatestVersion => _guiRelease?.Tag ?? "—";
        public string GuiReleaseTitle => _guiRelease?.Title ?? "";
        public string GuiReleaseDate => _guiRelease == null ? "" : "Опубликован: " + _guiRelease.PublishedText;
        public string GuiUpdateStatus
        {
            get => _guiUpdateStatus;
            private set => Set(ref _guiUpdateStatus, value);
        }

        public bool GuiUpdateAvailable
        {
            get => _guiUpdateAvailable;
            private set
            {
                if (Set(ref _guiUpdateAvailable, value))
                {
                    Raise(nameof(GuiUpdateBadgeText));
                    Raise(nameof(GuiUpdateButtonText));
                    (UpdateGuiCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string GuiUpdateBadgeText => _guiRelease == null
            ? "Проверка не выполнялась"
            : GuiUpdateAvailable ? "Доступно обновление " + GuiLatestVersion : "Установлена актуальная версия";

        public string GuiUpdateButtonText => GuiUpdateAvailable ? "Скачать и перезапустить" : "Обновление не требуется";

        public ObservableCollection<EngineBackupInfo> AvailableEngineBackups { get; } = new();

        public EngineBackupInfo? SelectedEngineBackup
        {
            get => _selectedEngineBackup;
            set
            {
                if (Set(ref _selectedEngineBackup, value))
                    (RollbackEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string EngineBackupText => AvailableEngineBackups.Count == 0
            ? "Сохранённых копий обновлений пока нет"
            : $"Доступно копий: {AvailableEngineBackups.Count}";

        public EngineConsistencyReport Consistency => _consistency;
        public string ConsistencyText => _consistency.Summary;
        public string ConsistencyKey => _consistency.IsConsistent ? "Success" : "Warning";

        public string LatestVersion => string.IsNullOrEmpty(_latestVersion) ? "—" : _latestVersion;

        public string LastCheckText => _lastCheck.HasValue
            ? "Последняя проверка: " + _lastCheck.Value.ToString("dd.MM.yyyy HH:mm")
            : "Проверка ещё не выполнялась";

        public string ReleaseTitle
        {
            get => _releaseTitle;
            private set => Set(ref _releaseTitle, value);
        }

        public string ReleaseDate
        {
            get => _releaseDate;
            private set => Set(ref _releaseDate, value);
        }

        public string ReleaseNotes
        {
            get => _releaseNotes;
            private set => Set(ref _releaseNotes, value);
        }

        public bool HasRelease => !string.IsNullOrEmpty(_latestVersion);

        public bool UpdateAvailable
        {
            get => _updateAvailable;
            private set
            {
                if (Set(ref _updateAvailable, value))
                {
                    Raise(nameof(UpdateBadgeText));
                    Raise(nameof(UpdateEngineButtonText));
                    (UpdateEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasEnginePackage => _assets.Count > 0;

        public string UpdateBadgeText => UpdateAvailable ? "Доступно обновление " + LatestVersion : "Установлена актуальная версия";
        public string UpdateEngineButtonText => UpdateAvailable ? "Скачать и установить" : "Установлена актуальная версия";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(ProgressVisible));
                    (CheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckGuiUpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (UpdateGuiCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (UpdateEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (UpdateIpsetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckHostsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyHostsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RollbackEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                if (Set(ref _progress, value))
                    Raise(nameof(ProgressPercentText));
            }
        }

        public bool Indeterminate
        {
            get => _indeterminate;
            private set
            {
                if (Set(ref _indeterminate, value))
                    Raise(nameof(ProgressPercentText));
            }
        }

        public string ProgressPercentText => Indeterminate ? "" : $"{Progress:0}%";

        public bool ProgressVisible => IsBusy;

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public string HostsStatus
        {
            get => _hostsStatus;
            private set => Set(ref _hostsStatus, value);
        }

        public bool HostsNeedsUpdate
        {
            get => _hostsNeedsUpdate;
            private set
            {
                if (Set(ref _hostsNeedsUpdate, value))
                {
                    Raise(nameof(HostsStatusKey));
                    (ApplyHostsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string HostsStatusKey => _hostsNeedsUpdate ? "Warning" : "Success";

        public string Message
        {
            get => _message;
            private set
            {
                if (Set(ref _message, value)) Raise(nameof(MessageVisible));
            }
        }

        public bool MessageVisible => !string.IsNullOrWhiteSpace(_message);

        public string OperationResultTitle
        {
            get => _operationResultTitle;
            private set => Set(ref _operationResultTitle, value);
        }

        public string OperationResultDetails
        {
            get => _operationResultDetails;
            private set => Set(ref _operationResultDetails, value);
        }

        public string OperationResultKey
        {
            get => _operationResultKey;
            private set => Set(ref _operationResultKey, value);
        }

        public bool OperationResultVisible
        {
            get => _operationResultVisible;
            private set => Set(ref _operationResultVisible, value);
        }

        public string MessageKey
        {
            get => _messageKey;
            private set => Set(ref _messageKey, value);
        }

        /// <summary>Кэш последней известной версии — используется бейджем на главной странице.</summary>
        public string LastKnownLatest
        {
            get => _latestVersion;
            set
            {
                _latestVersion = value ?? "";
                UpdateAvailable = !string.IsNullOrEmpty(_latestVersion) &&
                    (EngineService.CompareVersions(_latestVersion, EngineService.ReadVersion(Settings.EnginePath)) > 0 ||
                     !EngineService.IsEngineReady(Settings.EnginePath));
                Raise(nameof(LatestVersion));
                Raise(nameof(HasRelease));
            }
        }

        public string ReleasesText { get; private set; } = "";

        public ICommand CheckCommand { get; }
        public ICommand CheckGuiUpdateCommand { get; }
        public ICommand UpdateGuiCommand { get; }
        public ICommand UpdateEngineCommand { get; }
        public ICommand RollbackEngineCommand { get; }
        public ICommand UpdateIpsetCommand { get; }
        public ICommand CheckHostsCommand { get; }
        public ICommand ApplyHostsCommand { get; }
        public ICommand OpenReleasePageCommand { get; }
        public ICommand OpenRepoCommand { get; }
        public ICommand OpenDownloadsPageCommand { get; }
        public ICommand OpenEngineFolderCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenLogsCommand { get; }

        private readonly ObservableCollection<ReleaseAsset> _assets = new();

        // ------------------------------------------------------------------ логика

        /// <summary>Дешёвое обновление бейджей (без сети) — вызывается таймером.</summary>
        public void RefreshBadge()
        {
            Raise(nameof(EngineVersion));
            if (!string.IsNullOrEmpty(_latestVersion))
                UpdateAvailable = EngineService.CompareVersions(_latestVersion, EngineService.ReadVersion(Settings.EnginePath)) > 0
                    || !EngineService.IsEngineReady(Settings.EnginePath);
            Raise(nameof(UpdateBadgeText));
            Raise(nameof(UpdateEngineButtonText));
        }

        public async Task CheckGuiUpdateAsync()
            => await CheckGuiUpdateAsync(silent: false);

        public async Task CheckGuiUpdateAsync(bool silent)
        {
            if (IsBusy) return;
            IsBusy = true;
            Indeterminate = true;
            if (!silent) Status = "Проверяю обновление GUI на GitHub…";
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                _guiRelease = await GuiUpdateService.GetLatestReleaseAsync(
                    Settings.GuiRepo, Settings.IncludePrerelease, _cts.Token);
                Raise(nameof(GuiLatestVersion));
                Raise(nameof(GuiReleaseTitle));
                Raise(nameof(GuiReleaseDate));

                if (_guiRelease == null)
                {
                    GuiUpdateAvailable = false;
                    GuiUpdateStatus = "Не удалось получить релиз GUI. Обновление не выполнялось.";
                    if (!silent) SetMessage(GuiUpdateStatus, "Warning");
                    return;
                }

                var comparison = EngineService.CompareVersions(
                    _guiRelease.Tag, GuiUpdateService.CurrentVersion);
                GuiUpdateAvailable = comparison > 0 && _guiRelease.PortableAsset != null;
                GuiUpdateStatus = _guiRelease.PortableAsset == null
                    ? "В релизе нет проверяемого portable EXE с SHA-256."
                    : GuiUpdateAvailable
                        ? $"Доступна версия {_guiRelease.Tag}. Перед заменой exe будет создана резервная копия."
                        : $"Установлена актуальная версия {GuiUpdateService.CurrentVersion}.";
                if (!silent) SetMessage(GuiUpdateStatus, GuiUpdateAvailable ? "Info" : "Success");
            }
            catch (OperationCanceledException)
            {
                GuiUpdateStatus = "Проверка обновления GUI отменена.";
                if (!silent) SetMessage(GuiUpdateStatus, "Warning");
            }
            catch (Exception ex)
            {
                GuiUpdateStatus = "Ошибка проверки обновления GUI: " + ex.Message;
                if (!silent) SetMessage(GuiUpdateStatus, "Warning");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
                Indeterminate = false;
            }
        }

        private async Task UpdateGuiAsync()
        {
            if (_guiRelease == null || !GuiUpdateAvailable)
            {
                await CheckGuiUpdateAsync();
                if (_guiRelease == null || !GuiUpdateAvailable) return;
            }

            var confirmation = System.Windows.MessageBox.Show(
                $"Скачать Zapret GUI {_guiRelease.Tag} и перезапустить приложение?\n\n" +
                "Файл будет проверен по SHA-256 из GitHub API. Текущий exe сохранится в резервной копии.",
                "Обновление Zapret GUI", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);
            if (confirmation != System.Windows.MessageBoxResult.Yes) return;

            IsBusy = true;
            Progress = 0;
            Indeterminate = true;
            Status = "Скачиваю безопасное обновление GUI…";
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                var result = await GuiUpdateService.DownloadAndScheduleAsync(
                    _guiRelease, new Progress<ProgressInfo>(ApplyProgress), _cts.Token);
                GuiUpdateStatus = result.Message;
                SetMessage(result.Message, result.Ok ? "Success" : "Danger");
                if (result.Ok)
                {
                    Status = "Обновление подготовлено. Закрываю приложение…";
                    await Task.Delay(300);
                    if (System.Windows.Application.Current is App app) app.ShutdownApp();
                }
            }
            catch (OperationCanceledException)
            {
                GuiUpdateStatus = "Загрузка обновления GUI отменена.";
                SetMessage(GuiUpdateStatus, "Warning");
            }
            catch (Exception ex)
            {
                GuiUpdateStatus = "Ошибка обновления GUI: " + ex.Message;
                SetMessage(GuiUpdateStatus, "Danger");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
                Indeterminate = false;
            }
        }

        public async Task CheckAsync()
        {
            IsBusy = true;
            Indeterminate = true;
            Status = "Проверяю обновления на GitHub…";
            Message = "";
            _assets.Clear();
            UpdateAvailable = false;
            Raise(nameof(HasEnginePackage));
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            try
            {
                var release = await EngineService.GetLatestReleaseAsync(Settings.IncludePrerelease, _cts.Token);
                _lastCheck = DateTime.Now;
                Raise(nameof(LastCheckText));

                if (release == null)
                {
                    Status = "Не удалось получить информацию о релизах";
                    SetMessage("GitHub недоступен. Проверьте соединение (кнопка «Диагностика»). " +
                               "Обновление можно установить вручную со страницы релизов.", "Danger");
                    return;
                }

                _assets.Clear();
                foreach (var asset in release.Assets) _assets.Add(asset);

                LastKnownLatest = release.Tag;
                ReleaseTitle = string.IsNullOrEmpty(release.Title) ? release.Tag : $"{release.Title} ({release.Tag})";
                ReleaseDate = "Опубликован: " + release.PublishedText + (release.Prerelease ? " · pre-release" : "");
                ReleaseNotes = string.IsNullOrWhiteSpace(release.Body)
                    ? "Описание изменений отсутствует в релизе."
                    : Trim(release.Body, 4000);
                _releaseUrl = release.HtmlUrl;

                var current = EngineService.ReadVersion(Settings.EnginePath);
                var compare = EngineService.CompareVersions(release.Tag, current);
                UpdateAvailable = compare > 0 || !EngineService.IsEngineReady(Settings.EnginePath);

                Raise(nameof(HasRelease));
                Raise(nameof(HasEnginePackage));
                Raise(nameof(ReleasesText));
                (UpdateEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

                Status = UpdateAvailable
                    ? $"Доступна версия {release.Tag} (установлена {current})"
                    : $"Установлена последняя версия {current}";

                if (UpdateAvailable)
                    SetMessage($"Доступна версия движка {release.Tag}. Файлы релиза: {release.AssetsText}", "Info");
            }
            catch (OperationCanceledException)
            {
                Status = "Проверка отменена";
            }
            catch (Exception ex)
            {
                Status = "Ошибка проверки обновлений";
                SetMessage(ex.Message, "Danger");
            }
            finally
            {
                IsBusy = false;
                Indeterminate = false;
            }
        }

        /// <summary>
        /// Автоустановка движка при первом запуске: если bin\winws.exe отсутствует,
        /// скачивает свежий релиз Flowseal. Возвращает true, если движок готов к работе.
        /// </summary>
        public async Task<bool> EnsureEngineInstalledAsync()
        {
            if (EngineService.IsEngineReady(Settings.EnginePath))
                return true;

            if (IsBusy)
                return EngineService.IsEngineReady(Settings.EnginePath);

            if (!Shell.IsAdmin())
            {
                AppLog.Warn("Движок не установлен, а для его установки нужны права администратора");
                SetMessage("Движок не установлен. Перезапустите приложение от администратора " +
                           "и нажмите «Скачать и установить».", "Warning");
                return false;
            }

            AppLog.Info("Движок не найден — скачиваю актуальный релиз автоматически");
            IsBusy = true;
            Progress = 0;
            Indeterminate = true;
            Status = "Движок не найден. Скачиваю актуальную версию…";
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            try
            {
                var release = await EngineService.GetLatestReleaseAsync(Settings.IncludePrerelease, _cts.Token);
                if (release == null)
                {
                    Status = "Не удалось скачать движок";
                    SetMessage("Не удалось получить релиз с GitHub. Проверьте соединение " +
                               "и нажмите «Скачать и установить» вручную.", "Danger");
                    return false;
                }

                var progress = new Progress<ProgressInfo>(ApplyProgress);
                var result = await EngineService.DownloadAndInstallAsync(
                    release, Settings.EnginePath, Settings, progress, _cts.Token);

                Status = result.Message;
                ShowUpdateResult(result);
                SetMessage(result.Message + (result.Ok
                    ? $". Обновлено файлов: {result.UpdatedFiles}, сохранено пользовательских: {result.SkippedFiles}."
                    : ""), result.Ok ? "Success" : "Danger");

                if (result.Ok)
                {
                    _main.Home.ReloadFromEngine();
                    _main.StrategiesPage.Refresh();
                    RefreshEngineBackups();
                    RefreshConsistency();
                    Raise(nameof(EngineVersion));
                }

                return result.Ok;
            }
            catch (OperationCanceledException)
            {
                Status = "Установка движка отменена";
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("Автоустановка движка не удалась: " + ex.Message);
                SetMessage("Не удалось установить движок: " + ex.Message, "Danger");
                return false;
            }
            finally
            {
                IsBusy = false;
                Indeterminate = false;
            }
        }

        private void ShowUpdateResult(EngineUpdateResult result)
        {
            var integrity = result.IntegrityVerified
                ? "SHA-256: подтверждён по данным GitHub"
                : "SHA-256: не подтверждён";
            var backup = result.BackupCreated
                ? $"Резервная копия: создана ({result.BackupFileCount} файлов)"
                : "Резервная копия: не создана";
            var restore = result.FilesRestoredAfterFailure
                ? "Предыдущие файлы восстановлены после ошибки."
                : "";
            var warnings = result.Warnings.Count == 0
                ? ""
                : "Предупреждения: " + string.Join(" ", result.Warnings);
            OperationResultTitle = result.Ok ? "Результат обновления движка" : "Обновление движка завершилось с ошибкой";
            OperationResultDetails = string.Join("\n", new[]
            {
                string.IsNullOrWhiteSpace(result.PreviousVersion) ? "Версия до операции: неизвестна" : "Версия до операции: " + result.PreviousVersion,
                string.IsNullOrWhiteSpace(result.Version) ? "Версия после операции: не установлена" : "Версия после операции: " + result.Version,
                integrity,
                $"Файлы: изменено {result.UpdatedFiles}, сохранено пользовательских {result.SkippedFiles}",
                backup,
                restore,
                warnings
            }.Where(text => !string.IsNullOrWhiteSpace(text)));
            OperationResultKey = result.Ok
                ? (result.Warnings.Count > 0 || !result.IntegrityVerified ? "Warning" : "Success")
                : (result.FilesRestoredAfterFailure ? "Warning" : "Danger");
            OperationResultVisible = true;
        }

        private void ShowRollbackResult(EngineRollbackResult result)
        {
            OperationResultTitle = result.Ok ? "Результат отката движка" : "Откат движка завершился с ошибкой";
            OperationResultDetails = result.Ok
                ? string.Join("\n", new[]
                {
                    "Восстановлена версия: " + (string.IsNullOrWhiteSpace(result.RestoredVersion) ? "предыдущее состояние" : result.RestoredVersion),
                    $"Восстановлено файлов: {result.RestoredFiles}",
                    $"Удалено файлов новой версии: {result.RemovedNewFiles}",
                    "Пользовательские файлы вне резервной копии не затронуты."
                })
                : result.Message;
            OperationResultKey = result.Ok ? "Success" : "Danger";
            OperationResultVisible = true;
        }

        private async Task UpdateEngineAsync()
        {
            if (!Shell.IsAdmin())
            {
                SetMessage("Для установки движка нужны права администратора.", "Danger");
                return;
            }

            IsBusy = true;
            Progress = 0;
            Indeterminate = true;
            Status = "Подготовка обновления…";
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            try
            {
                var release = await EngineService.GetLatestReleaseAsync(Settings.IncludePrerelease, _cts.Token);
                if (release == null)
                {
                    SetMessage("Не удалось получить релиз с GitHub.", "Danger");
                    return;
                }

                var installedVersion = EngineService.ReadVersion(Settings.EnginePath);
                if (EngineService.IsEngineReady(Settings.EnginePath) &&
                    EngineService.CompareVersions(release.Tag, installedVersion) <= 0)
                {
                    UpdateAvailable = false;
                    Status = $"Установлена последняя версия {installedVersion}";
                    OperationResultTitle = "Проверка обновления завершена";
                    OperationResultDetails = $"Установлена актуальная версия {installedVersion}. Файлы не изменялись, резервная копия не создавалась.";
                    OperationResultKey = "Success";
                    OperationResultVisible = true;
                    SetMessage("Обновление не требуется: установлена актуальная версия.", "Success");
                    return;
                }

                // КРИТИЧНО для стабильности Windows: перед перезаписью файлов останавливаем
                // обход, завершаем winws.exe и выгружаем драйвер WinDivert из ядра —
                // иначе замена WinDivert64.sys «на лету» заканчивается синим экраном (BSOD).
                Status = "Останавливаю обход и выгружаю драйвер WinDivert…";
                var wasRunning = await _main.Bypass.PrepareForEngineUpdateAsync(_cts.Token);

                var progress = new Progress<ProgressInfo>(ApplyProgress);
                var result = await EngineService.DownloadAndInstallAsync(
                    release, Settings.EnginePath, Settings, progress, _cts.Token);

                Status = result.Message;
                ShowUpdateResult(result);
                var key = !result.Ok ? "Danger"
                    : result.Warnings.Count > 0 ? "Warning"
                    : "Success";
                var integrityText = result.Ok && result.IntegrityVerified
                    ? " Архив проверен по SHA-256 из GitHub."
                    : "";
                SetMessage(result.Message + (result.Ok
                    ? $". Обновлено файлов: {result.UpdatedFiles}, сохранено пользовательских: {result.SkippedFiles}."
                    : "") + integrityText, key);

                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
                RefreshEngineBackups();
                RefreshConsistency();
                Raise(nameof(EngineVersion));

                // Если обход работал — возвращаем его к жизни с обновлённым движком
                if (result.Ok && wasRunning)
                {
                    var strategy = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                    if (strategy != null)
                    {
                        Status = "Перезапускаю обход с обновлённым движком…";
                        var restart = await _main.Bypass.StartAsync(strategy,
                            EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
                        SetMessage(restart.Message, restart.Ok ? "Success" : "Warning");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Status = "Обновление отменено";
            }
            catch (Exception ex)
            {
                Status = "Ошибка обновления движка";
                SetMessage(ex.Message, "Danger");
            }
            finally
            {
                IsBusy = false;
                Indeterminate = false;
            }
        }

        public void RefreshConsistency()
        {
            _consistency = EngineConsistencyChecker.Check(Settings.EnginePath);
            Raise(nameof(Consistency));
            Raise(nameof(ConsistencyText));
            Raise(nameof(ConsistencyKey));
        }

        public void RefreshEngineBackups()
        {
            AvailableEngineBackups.Clear();
            foreach (var backup in EngineService.GetAvailableBackups(Settings.EnginePath))
                AvailableEngineBackups.Add(backup);
            SelectedEngineBackup = AvailableEngineBackups.FirstOrDefault();
            Raise(nameof(EngineBackupText));
            (RollbackEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private async Task RollbackEngineAsync()
        {
            var backup = SelectedEngineBackup;
            if (backup == null) return;
            if (!Shell.IsAdmin())
            {
                SetMessage("Для отката движка нужны права администратора.", "Danger");
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                $"Вернуть движок к состоянию до обновления {backup.InstalledVersion}? Файлы пользователя и кандидаты не будут затронуты.",
                "Откат движка", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            IsBusy = true;
            Indeterminate = true;
            Status = "Останавливаю обход перед откатом движка…";
            var before = _main.Bypass.GetStatus();
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                var wasRunning = await _main.Bypass.PrepareForEngineUpdateAsync(_cts.Token);
                var result = EngineService.RollbackEngine(backup, Settings.EnginePath);
                Status = result.Message;
                ShowRollbackResult(result);
                if (!result.Ok)
                {
                    SetMessage(result.Message + " Обход оставлен остановленным для безопасности.", "Danger");
                    return;
                }

                Settings.EngineVersion = backup.PreviousVersion;
                SettingsStore.Save(Settings);
                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
                Raise(nameof(EngineVersion));
                RefreshEngineBackups();
                RefreshConsistency();
                SetMessage(result.Message, "Success");

                if (wasRunning)
                {
                    if (before.State == BypassState.RunningService)
                    {
                        var service = WinServices.Start(WinServices.ZapretService);
                        var running = await Shell.WaitForAsync(
                            () => WinServices.Query(WinServices.ZapretService) == ServiceState.Running,
                            15000);
                        if (!service.Ok || !running)
                            SetMessage(result.Message + " Службу не удалось запустить автоматически.", "Warning");
                    }
                    else
                    {
                        var strategy = _main.Strategies.Find(Settings.SelectedStrategy) ?? _main.Strategies.Recommended;
                        if (strategy != null)
                        {
                            var restart = await _main.Bypass.StartAsync(strategy,
                                EngineService.GetGameFilterMode(Settings.EnginePath), Settings.ShowWinwsConsole);
                            if (!restart.Ok) SetMessage(result.Message + " Обход не удалось восстановить: " + restart.Message, "Warning");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SetMessage("Откат движка отменён. Обход оставлен остановленным для безопасности.", "Warning");
            }
            catch (Exception ex)
            {
                SetMessage("Ошибка отката движка: " + ex.Message, "Danger");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
                Indeterminate = false;
            }
        }

        private void ApplyProgress(ProgressInfo info)
        {
            Indeterminate = info.IsIndeterminate;
            if (!info.IsIndeterminate) Progress = info.Percent;
            Status = info.Status;
        }

        private async Task UpdateIpsetAsync()
        {
            IsBusy = true;
            Indeterminate = true;
            Status = "Обновляю список ipset-all.txt…";
            try
            {
                var ok = await EngineService.UpdateIpsetAsync(Settings.EnginePath);
                Status = ok ? "Список ipset обновлён" : "Не удалось обновить список ipset";
                SetMessage(ok
                    ? "Список ipset-all.txt обновлён из репозитория. Перезапустите обход для применения."
                    : "Ошибка обновления списка ipset. Смотрите журнал.", ok ? "Success" : "Danger");
                _main.Home.ReloadFromEngine();
            }
            finally
            {
                IsBusy = false;
                Indeterminate = false;
            }
        }

        private async Task CheckHostsAsync()
        {
            IsBusy = true;
            Indeterminate = true;
            Status = "Проверяю файл hosts…";
            try
            {
                var result = await EngineService.CheckHostsAsync();
                _hostsFile = result.TempFile;
                HostsNeedsUpdate = result.NeedsUpdate;
                HostsStatus = result.Ok
                    ? result.Message + $" (строк в файле репозитория: {result.LineCount})"
                    : result.Message;
                SetMessage(result.NeedsUpdate
                    ? "Файл hosts требует обновления — нажмите «Применить обновление hosts». " +
                      "Починяет веб-версию Telegram и голосовой чат Discord."
                    : "Файл hosts актуален.", result.NeedsUpdate ? "Warning" : "Success");
            }
            finally
            {
                IsBusy = false;
                Indeterminate = false;
            }
        }

        private void ApplyHosts()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Файл hosts будет изменён для добавления записей из официального списка движка. Это системное действие требует администратора. Применить?",
                "Подтверждение изменения hosts", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var (ok, message) = EngineService.ApplyHosts(_hostsFile);
            SetMessage(message, ok ? "Success" : "Danger");
            if (ok) HostsNeedsUpdate = false;
        }

        private void SetMessage(string message, string key)
        {
            MessageKey = key;
            Message = message;
        }

        public void RefreshTheme()
        {
            Raise(nameof(MessageKey));
            Raise(nameof(HostsStatusKey));
        }

        private static string Trim(string text, int max)
            => text.Length <= max ? text : text.Substring(0, max) + "\n…";
    }
}
