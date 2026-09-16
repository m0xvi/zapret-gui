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

        public UpdatesViewModel(MainViewModel main)
        {
            _main = main;

            CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
            UpdateEngineCommand = new AsyncRelayCommand(UpdateEngineAsync, () => !IsBusy && HasEnginePackage);
            UpdateIpsetCommand = new AsyncRelayCommand(UpdateIpsetAsync, () => !IsBusy);
            CheckHostsCommand = new AsyncRelayCommand(CheckHostsAsync, () => !IsBusy);
            ApplyHostsCommand = new RelayCommand(ApplyHosts, () => !IsBusy && _hostsFile.Length > 0);
            OpenReleasePageCommand = new RelayCommand(() => Shell.OpenUrl(string.IsNullOrEmpty(_releaseUrl) ? EngineService.RepoUrl + "/releases/latest" : _releaseUrl));
            OpenRepoCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl));
            OpenDownloadsPageCommand = new RelayCommand(() => Shell.OpenUrl(EngineService.RepoUrl + "/releases/latest"));
            OpenEngineFolderCommand = new RelayCommand(() => Shell.OpenFolder(Settings.EnginePath));
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
            OpenLogsCommand = new RelayCommand(() => _main.Navigate("logs"));
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
                    (UpdateEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasEnginePackage => _assets.Count > 0;

        public string UpdateBadgeText => UpdateAvailable ? "Доступно обновление " + LatestVersion : "Установлена актуальная версия";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    Raise(nameof(ProgressVisible));
                    (CheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (UpdateEngineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (UpdateIpsetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (CheckHostsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyHostsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        public bool Indeterminate
        {
            get => _indeterminate;
            private set => Set(ref _indeterminate, value);
        }

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
                Raise(nameof(LatestVersion));
                Raise(nameof(HasRelease));
            }
        }

        public string ReleasesText { get; private set; } = "";

        public ICommand CheckCommand { get; }
        public ICommand UpdateEngineCommand { get; }
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
            Raise(nameof(UpdateBadgeText));
        }

        public async Task CheckAsync()
        {
            IsBusy = true;
            Indeterminate = true;
            Status = "Проверяю обновления на GitHub…";
            Message = "";
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
                SetMessage(result.Message + (result.Ok
                    ? $". Обновлено файлов: {result.UpdatedFiles}, сохранено пользовательских: {result.SkippedFiles}."
                    : ""), result.Ok ? "Success" : "Danger");

                if (result.Ok)
                {
                    _main.Home.ReloadFromEngine();
                    _main.StrategiesPage.Refresh();
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

                // КРИТИЧНО для стабильности Windows: перед перезаписью файлов останавливаем
                // обход, завершаем winws.exe и выгружаем драйвер WinDivert из ядра —
                // иначе замена WinDivert64.sys «на лету» заканчивается синим экраном (BSOD).
                Status = "Останавливаю обход и выгружаю драйвер WinDivert…";
                var wasRunning = await _main.Bypass.PrepareForEngineUpdateAsync(_cts.Token);

                var progress = new Progress<ProgressInfo>(ApplyProgress);
                var result = await EngineService.DownloadAndInstallAsync(
                    release, Settings.EnginePath, Settings, progress, _cts.Token);

                Status = result.Message;
                var key = !result.Ok ? "Danger"
                    : result.Warnings.Count > 0 ? "Warning"
                    : "Success";
                SetMessage(result.Message + (result.Ok
                    ? $". Обновлено файлов: {result.UpdatedFiles}, сохранено пользовательских: {result.SkippedFiles}."
                    : ""), key);

                _main.Home.ReloadFromEngine();
                _main.StrategiesPage.Refresh();
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
            var (ok, message) = EngineService.ApplyHosts(_hostsFile);
            SetMessage(message, ok ? "Success" : "Danger");
            if (ok) HostsNeedsUpdate = false;
        }

        private void SetMessage(string message, string key)
        {
            MessageKey = key;
            Message = message;
        }

        private static string Trim(string text, int max)
            => text.Length <= max ? text : text.Substring(0, max) + "\n…";
    }
}
