using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class ReleaseAsset
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string DownloadUrl { get; set; } = "";
        public string SizeText => Size <= 0 ? "" : (Size / 1024.0 / 1024.0).ToString("0.0") + " МБ";
    }

    public sealed class ReleaseInfo
    {
        public string Tag { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
        public string Body { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public bool Prerelease { get; set; }
        public List<ReleaseAsset> Assets { get; set; } = new();

        public string PublishedText => PublishedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "неизвестно";
        public string AssetsText => Assets.Count == 0 ? "" : string.Join(" · ", Assets.Select(a => a.Name));
    }

    public sealed class ProgressInfo
    {
        public double Percent { get; init; }
        public string Status { get; init; } = "";
        public bool IsIndeterminate => Percent < 0;
    }

    public sealed class EngineUpdateResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public int UpdatedFiles { get; init; }
        public int SkippedFiles { get; init; }
        public string Version { get; init; } = "";

        /// <summary>
        /// Нефатальные предупреждения: например, файлы драйвера WinDivert были заблокированы
        /// ядром и пропущены — их замена требует перезагрузки Windows.
        /// </summary>
        public List<string> Warnings { get; init; } = new();
    }

    public sealed class HostsCheckResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public bool NeedsUpdate { get; init; }
        public string TempFile { get; init; } = "";
        public int LineCount { get; init; }
    }

    /// <summary>
    /// Работа с движком zapret: скачивание релизов с Flowseal/zapret-discord-youtube,
    /// обновление с сохранением пользовательских списков, обновление ipset и hosts.
    /// </summary>
    public static class EngineService
    {
        public const string Owner = "Flowseal";
        public const string Repository = "zapret-discord-youtube";
        public const string RepoUrl = "https://github.com/Flowseal/zapret-discord-youtube";

        private const string ApiReleases = "https://api.github.com/repos/" + Owner + "/" + Repository + "/releases";
        private const string RawBase = "https://raw.githubusercontent.com/" + Owner + "/" + Repository + "/main/.service/";

        public static string VersionUrl => RawBase + "version.txt";
        public static string IpsetUrl => RawBase + "ipset-service.txt";
        public static string HostsUrl => RawBase + "hosts";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretGUI", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        // ---------------------------------------------------------------- статус

        /// <summary>Версия установленного движка: сначала маркер GUI, затем service.bat.</summary>
        public static string ReadVersion(string engineRoot)
        {
            try
            {
                var marker = AppPaths.VersionMarker(engineRoot);
                if (File.Exists(marker))
                {
                    var v = File.ReadAllText(marker).Trim();
                    if (v.Length > 0) return v;
                }

                var serviceBat = Path.Combine(engineRoot, "service.bat");
                if (File.Exists(serviceBat))
                {
                    var match = Regex.Match(File.ReadAllText(serviceBat), "LOCAL_VERSION=([^\"\\r\\n]+)");
                    if (match.Success) return match.Groups[1].Value.Trim();
                }
            }
            catch { }
            return "";
        }

        public static void WriteVersion(string engineRoot, string version)
        {
            try
            {
                Directory.CreateDirectory(engineRoot);
                File.WriteAllText(AppPaths.VersionMarker(engineRoot), version.Trim());
            }
            catch (Exception ex) { AppLog.Warn("Не удалось сохранить версию движка: " + ex.Message); }
        }

        public static bool IsEngineReady(string engineRoot)
            => File.Exists(Path.Combine(engineRoot, "bin", "winws.exe"))
               && File.Exists(Path.Combine(engineRoot, "bin", "WinDivert64.sys"));

        // ---------------------------------------------------------------- релизы

        /// <summary>Список релизов с GitHub. При ошибке возвращает пустой список.</summary>
        public static async Task<List<ReleaseInfo>> GetReleasesAsync(CancellationToken ct = default)
        {
            var list = new List<ReleaseInfo>();
            try
            {
                using var response = await Http.GetAsync(ApiReleases + "?per_page=20", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"GitHub API вернул {(int)response.StatusCode}. Использую резервный способ проверки версии.");
                    return list;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

                    var release = new ReleaseInfo
                    {
                        Tag = GetString(item, "tag_name"),
                        Title = GetString(item, "name"),
                        HtmlUrl = GetString(item, "html_url"),
                        Body = GetString(item, "body"),
                        Prerelease = item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()
                    };

                    if (item.TryGetProperty("published_at", out var published) &&
                        DateTime.TryParse(published.GetString(), out var dt))
                        release.PublishedAt = dt;

                    if (item.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            release.Assets.Add(new ReleaseAsset
                            {
                                Name = GetString(asset, "name"),
                                Size = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                                DownloadUrl = GetString(asset, "browser_download_url")
                            });
                        }
                    }

                    list.Add(release);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось получить список релизов: " + ex.Message);
            }
            return list;
        }

        /// <summary>Последний релиз с учётом канала. Если API недоступен — только номер версии.</summary>
        public static async Task<ReleaseInfo?> GetLatestReleaseAsync(bool includePrerelease, CancellationToken ct = default)
        {
            var releases = await GetReleasesAsync(ct).ConfigureAwait(false);
            var latest = releases.FirstOrDefault(r => includePrerelease || !r.Prerelease);
            if (latest != null) return latest;

            var version = await GetLatestVersionTextAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(version)) return null;

            return new ReleaseInfo
            {
                Tag = version,
                Title = version,
                HtmlUrl = RepoUrl + "/releases/tag/" + version
            };
        }

        /// <summary>Легкая проверка версии через .service/version.txt (как делает service.bat).</summary>
        public static async Task<string?> GetLatestVersionTextAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await Http.GetAsync(VersionUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                var text = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                return text.Length == 0 ? null : text;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось получить версию с GitHub: " + ex.Message);
                return null;
            }
        }

        /// <summary>Сравнение версий вида 1.10.2 и 1.9.9d (безопасно для любых форматов).</summary>
        public static int CompareVersions(string? left, string? right)
            => string.CompareOrdinal(NormalizeVersion(left), NormalizeVersion(right));

        private static string NormalizeVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            var parts = new List<string>();
            foreach (Match m in Regex.Matches(version.Trim().TrimStart('v', 'V'), @"(\d+)|([A-Za-z]+)"))
            {
                parts.Add(m.Groups[1].Success
                    ? int.Parse(m.Groups[1].Value).ToString("D6")
                    : m.Groups[2].Value.ToLowerInvariant());
            }
            return string.Join(".", parts);
        }

        // ---------------------------------------------------------------- установка

        public static ReleaseAsset? PickAsset(ReleaseInfo release)
            => release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));

        public static async Task<EngineUpdateResult> DownloadAndInstallAsync(
            ReleaseInfo release, string engineRoot, AppSettings settings,
            IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        {
            var asset = PickAsset(release);
            if (asset == null)
            {
                return new EngineUpdateResult
                {
                    Ok = false,
                    Message = $"В релизе {release.Tag} нет zip/tar.gz архива. Откройте страницу релиза вручную: {release.HtmlUrl}"
                };
            }

            var tempArchive = Path.Combine(AppPaths.TempDir, asset.Name);
            var tempExtract = Path.Combine(AppPaths.TempDir, "extract-" + Guid.NewGuid().ToString("N"));

            try
            {
                progress?.Report(new ProgressInfo { Percent = 0, Status = "Скачивание " + asset.Name });
                await DownloadFileAsync(asset.DownloadUrl, tempArchive, asset.Size, progress, ct).ConfigureAwait(false);

                progress?.Report(new ProgressInfo { Percent = -1, Status = "Распаковка архива" });
                ExtractArchive(tempArchive, tempExtract);

                var contentRoot = ResolveContentRoot(tempExtract);
                var ipsetModeBefore = GetIpsetMode(engineRoot);
                var gameFilterBefore = GetGameFilterMode(engineRoot);

                // Безопасная подготовка: останавливаем обход и выгружаем драйвер WinDivert
                // из ядра ДО перезаписи файлов, иначе замена WinDivert64.sys «на лету» ведёт к BSOD.
                // (Вызывающий код обычно уже вызвал BypassController.PrepareForEngineUpdateAsync —
                // здесь дублируем защиту, чтобы метод был безопасен при любом вызове.)
                progress?.Report(new ProgressInfo { Percent = -1, Status = "Останавливаю обход перед обновлением" });
                await PrepareFilesForUpdateAsync(engineRoot, ct).ConfigureAwait(false);

                progress?.Report(new ProgressInfo { Percent = -1, Status = "Обновление файлов" });
                var (updated, skipped, warnings) = CopyEngine(contentRoot, engineRoot, settings.PreserveUserDataOnUpdate);

                WriteVersion(engineRoot, release.Tag);
                settings.EngineVersion = release.Tag;
                SettingsStore.Save(settings);

                StrategyParser.EnsureUserLists(engineRoot);

                // Возвращаем прежние режимы, чтобы обновление не меняло фильтры пользователя
                if (File.Exists(Path.Combine(engineRoot, "lists", "ipset-all.txt")))
                    SetIpsetMode(engineRoot, ipsetModeBefore);
                SetGameFilterMode(engineRoot, gameFilterBefore);

                progress?.Report(new ProgressInfo { Percent = 100, Status = "Готово" });
                AppLog.Info($"Движок обновлён до {release.Tag}: обновлено {updated} файлов, сохранено {skipped}");

                var message = $"Движок обновлён до версии {release.Tag}";
                if (warnings.Count > 0)
                {
                    message += ". Внимание: " + string.Join(" ", warnings);
                    AppLog.Warn("Обновление завершено с предупреждениями: " + string.Join("; ", warnings));
                }

                return new EngineUpdateResult
                {
                    Ok = true,
                    Message = message,
                    UpdatedFiles = updated,
                    SkippedFiles = skipped,
                    Version = release.Tag,
                    Warnings = warnings
                };
            }
            catch (OperationCanceledException)
            {
                return new EngineUpdateResult { Ok = false, Message = "Загрузка отменена" };
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка обновления движка: " + ex.Message);
                return new EngineUpdateResult { Ok = false, Message = "Ошибка обновления: " + ex.Message };
            }
            finally
            {
                TryDelete(tempArchive);
                TryDeleteDirectory(tempExtract);
            }
        }

        private static async Task DownloadFileAsync(string url, string destination, long expectedSize,
            IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? expectedSize;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(destination);

            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (progress != null && total > 0)
                {
                    var percent = received * 100.0 / total;
                    progress.Report(new ProgressInfo
                    {
                        Percent = percent,
                        Status = $"Скачано {received / 1024.0 / 1024.0:0.0} из {total / 1024.0 / 1024.0:0.0} МБ"
                    });
                }
            }
        }

        public static void ExtractArchive(string archivePath, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, targetDirectory, overwriteFiles: true);
                return;
            }

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                using var file = File.OpenRead(archivePath);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, targetDirectory, overwriteFiles: true);
                return;
            }

            throw new NotSupportedException("Неподдерживаемый формат архива: " + Path.GetFileName(archivePath));
        }

        /// <summary>В архивах Flowseal всё лежит в подпапке вида zapret-discord-youtube-1.10.2.</summary>
        private static string ResolveContentRoot(string extractedDirectory)
        {
            try
            {
                var entries = Directory.GetFileSystemEntries(extractedDirectory);
                if (entries.Length == 1 && Directory.Exists(entries[0]) && !entries[0].EndsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                    return entries[0];
            }
            catch { }
            return extractedDirectory;
        }

        /// <summary>
        /// Внутренняя защита перед перезаписью файлов: завершает winws.exe, останавливает
        /// службы zapret/WinDivert/WinDivert14 и ждёт выгрузки драйвера из ядра Windows.
        /// </summary>
        private static async Task PrepareFilesForUpdateAsync(string engineRoot, CancellationToken ct)
        {
            try
            {
                if (Shell.IsProcessRunning("winws"))
                {
                    AppLog.Info("Завершаю winws.exe перед обновлением файлов движка…");
                    Shell.KillProcess("winws");
                    await Shell.WaitForAsync(() => !Shell.IsProcessRunning("winws"), 8000).ConfigureAwait(false);
                }

                await WinServices.StopForEngineUpdateAsync().ConfigureAwait(false);
                await WinServices.WaitForDriverUnloadAsync(engineRoot).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Подготовка не должна ронять всё обновление: CopyEngine дополнительно
                // пропустит заблокированные файлы с предупреждением вместо падения.
                AppLog.Warn("Не удалось полностью подготовить файлы к обновлению: " + ex.Message);
            }
        }

        private static (int Updated, int Skipped, List<string> Warnings) CopyEngine(
            string source, string engineRoot, bool preserveUserData)
        {
            int updated = 0, skipped = 0;
            var warnings = new List<string>();
            Directory.CreateDirectory(engineRoot);

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(engineRoot, relative);

                if (preserveUserData && IsUserFile(relative) && File.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                if (relative.Equals("lists\\ipset-all.txt", StringComparison.OrdinalIgnoreCase))
                {
                    // Основной список сохраняем как .backup, если пользователь в режиме none/any
                    var mode = GetIpsetMode(engineRoot);
                    var backup = Path.Combine(engineRoot, "lists", "ipset-all.txt.backup");
                    if (mode != IpsetMode.Loaded)
                    {
                        TryCopy(file, backup);
                        skipped++;
                        continue;
                    }
                }

                AppPaths.EnsureDir(Path.GetDirectoryName(destination)!);

                // Файлы ядерного драйвера нельзя перезаписывать «на лету»: если ядро Windows
                // всё ещё держит WinDivert64.sys/WinDivert.dll (служба не выгрузилась),
                // пропускаем их с предупреждением, а не падаем и тем более не провоцируем BSOD.
                if (IsDriverFile(relative) && File.Exists(destination) && WinServices.IsFileLocked(destination))
                {
                    var warn = $"Файл {relative} заблокирован ядром Windows (драйвер WinDivert не выгрузился) — пропущен. " +
                               "Перезагрузите Windows и повторите обновление для замены драйвера.";
                    AppLog.Warn(warn);
                    if (!warnings.Any(w => w.Contains(relative, StringComparison.OrdinalIgnoreCase)))
                        warnings.Add(warn);
                    skipped++;
                    continue;
                }

                try
                {
                    File.Copy(file, destination, true);
                    updated++;
                }
                catch (IOException ex) when (IsDriverFile(relative))
                {
                    var warn = $"Не удалось заменить {relative} (файл занят): {ex.Message}. " +
                               "Перезагрузите Windows и повторите обновление.";
                    AppLog.Warn(warn);
                    if (!warnings.Any(w => w.Contains(relative, StringComparison.OrdinalIgnoreCase)))
                        warnings.Add(warn);
                    skipped++;
                }
                catch (IOException)
                {
                    // Обычный файл занят (антивирус, зависший процесс) — одна повторная попытка
                    // после короткой паузы, затем пропуск с предупреждением вместо падения.
                    try
                    {
                        Thread.Sleep(1000);
                        File.Copy(file, destination, true);
                        updated++;
                    }
                    catch (Exception retryEx)
                    {
                        AppLog.Warn($"Пропущен занятый файл {relative}: {retryEx.Message}");
                        skipped++;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    AppLog.Warn($"Нет доступа для записи {relative}: {ex.Message}");
                    skipped++;
                }
            }

            return (updated, skipped, warnings);
        }

        /// <summary>Файлы ядерного драйвера WinDivert — их замена при загруженном драйвере опасна.</summary>
        private static bool IsDriverFile(string relativePath)
        {
            var name = Path.GetFileName(relativePath);
            return name.Equals("WinDivert64.sys", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("WinDivert32.sys", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("WinDivert.dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUserFile(string relativePath)
        {
            var normalized = relativePath.Replace('/', '\\');
            if (normalized.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase)) return true;
            var preserved = new[]
            {
                "utils\\game_filter.enabled",
                "utils\\check_updates.enabled",
                "lists\\ipset-all.txt",
                "lists\\ipset-all.txt.backup"
            };
            return preserved.Any(p => normalized.Equals(p, StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------- списки и hosts

        public static async Task<bool> UpdateIpsetAsync(string engineRoot, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        {
            try
            {
                progress?.Report(new ProgressInfo { Percent = -1, Status = "Загрузка ipset-all.txt" });
                var text = await Http.GetStringAsync(IpsetUrl, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Пустой ответ сервера");

                var lists = Path.Combine(engineRoot, "lists");
                AppPaths.EnsureDir(lists);
                var mode = GetIpsetMode(engineRoot);

                if (mode == IpsetMode.None)
                {
                    File.WriteAllText(Path.Combine(lists, "ipset-all.txt.backup"), text);
                    AppLog.Info("Список ipset обновлён (сохранён как backup, активен режим none)");
                }
                else if (mode == IpsetMode.Any)
                {
                    File.WriteAllText(Path.Combine(lists, "ipset-all.txt.backup"), text);
                    AppLog.Info("Список ipset обновлён (сохранён как backup, активен режим any)");
                }
                else
                {
                    File.WriteAllText(Path.Combine(lists, "ipset-all.txt"), text);
                    AppLog.Info("Список ipset-all.txt обновлён");
                }

                progress?.Report(new ProgressInfo { Percent = 100, Status = "Готово" });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось обновить список ipset: " + ex.Message);
                return false;
            }
        }

        /// <summary>Проверяет, актуален ли hosts (сравнивает первую и последнюю строку с системным файлом).</summary>
        public static async Task<HostsCheckResult> CheckHostsAsync(CancellationToken ct = default)
        {
            try
            {
                var text = await Http.GetStringAsync(HostsUrl + "?t=" + DateTime.Now.Ticks, ct).ConfigureAwait(false);
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
                if (lines.Count == 0) return new HostsCheckResult { Ok = false, Message = "Пустой файл hosts из репозитория" };

                var tempFile = Path.Combine(AppPaths.TempDir, "hosts.github");
                File.WriteAllText(tempFile, text, new UTF8Encoding(false));

                var systemHosts = ReadSystemHosts();
                var first = lines.First();
                var last = lines.Last();
                var needsUpdate = !systemHosts.Any(l => l.Trim() == first.Trim())
                                  || !systemHosts.Any(l => l.Trim() == last.Trim());

                return new HostsCheckResult
                {
                    Ok = true,
                    NeedsUpdate = needsUpdate,
                    TempFile = tempFile,
                    LineCount = lines.Count,
                    Message = needsUpdate
                        ? "Файл hosts требует обновления (нет строк из репозитория)"
                        : "Файл hosts актуален"
                };
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось проверить hosts: " + ex.Message);
                return new HostsCheckResult { Ok = false, Message = "Ошибка проверки hosts: " + ex.Message };
            }
        }

        public static string SystemHostsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        public static List<string> ReadSystemHosts()
        {
            try
            {
                return File.Exists(SystemHostsPath)
                    ? File.ReadAllLines(SystemHostsPath).ToList()
                    : new List<string>();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать файл hosts: " + ex.Message);
                return new List<string>();
            }
        }

        /// <summary>Применяет скачанный hosts: делает копию, удаляет старый блок zapret и добавляет новый.</summary>
        public static (bool Ok, string Message) ApplyHosts(string downloadedFile)
        {
            const string begin = "# ==== Zapret GUI (Flowseal/zapret-discord-youtube) begin ====";
            const string end = "# ==== Zapret GUI (Flowseal/zapret-discord-youtube) end ====";

            try
            {
                if (!File.Exists(downloadedFile))
                    return (false, "Файл hosts из репозитория не найден");

                var newLines = File.ReadAllLines(downloadedFile)
                    .Where(l => l.Trim().Length > 0)
                    .ToList();

                var current = ReadSystemHosts();
                var backup = Path.Combine(AppPaths.BackupDir, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.bak");
                File.WriteAllLines(backup, current, new UTF8Encoding(false));

                var result = new List<string>();
                var insideBlock = false;
                var newLineSet = new HashSet<string>(newLines.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);

                foreach (var line in current)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Equals(begin, StringComparison.OrdinalIgnoreCase)) { insideBlock = true; continue; }
                    if (trimmed.Equals(end, StringComparison.OrdinalIgnoreCase)) { insideBlock = false; continue; }
                    if (insideBlock) continue;

                    // Убираем строки, которые дублировали бы содержимое из репозитория
                    if (newLineSet.Contains(trimmed)) continue;

                    result.Add(line);
                }

                while (result.Count > 0 && result[^1].Trim().Length == 0) result.RemoveAt(result.Count - 1);

                result.Add("");
                result.Add(begin);
                result.AddRange(newLines);
                result.Add(end);

                File.WriteAllLines(SystemHostsPath, result, new UTF8Encoding(false));
                AppLog.Info($"Файл hosts обновлён, копия сохранена: {backup}");
                return (true, $"Файл hosts обновлён ({newLines.Count} строк). Копия: {Path.GetFileName(backup)}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось обновить hosts: " + ex.Message);
                return (false, "Не удалось обновить hosts: " + ex.Message + " (нужны права администратора)");
            }
        }

        // ---------------------------------------------------------------- режимы ipset / game filter

        public static IpsetMode GetIpsetMode(string engineRoot)
        {
            try
            {
                var file = Path.Combine(engineRoot, "lists", "ipset-all.txt");
                if (!File.Exists(file)) return IpsetMode.Loaded;

                var lines = File.ReadAllLines(file);
                if (lines.Length == 0) return IpsetMode.Any;
                if (lines.Any(l => l.Contains("203.0.113.113/32"))) return IpsetMode.None;
                return IpsetMode.Loaded;
            }
            catch { return IpsetMode.Loaded; }
        }

        /// <summary>Переключение режима ipset (none/loaded/any) — логика как в ipset_switch из service.bat.</summary>
        public static bool SetIpsetMode(string engineRoot, IpsetMode target)
        {
            try
            {
                var listFile = Path.Combine(engineRoot, "lists", "ipset-all.txt");
                var backupFile = listFile + ".backup";
                AppPaths.EnsureDir(Path.GetDirectoryName(listFile)!);

                var current = GetIpsetMode(engineRoot);
                if (current == target) return true;

                if (current == IpsetMode.Loaded && File.Exists(listFile))
                {
                    if (File.Exists(backupFile)) File.Delete(backupFile);
                    File.Move(listFile, backupFile);
                }

                switch (target)
                {
                    case IpsetMode.None:
                        File.WriteAllText(listFile, "203.0.113.113/32" + Environment.NewLine);
                        break;
                    case IpsetMode.Any:
                        File.WriteAllText(listFile, "");
                        break;
                    case IpsetMode.Loaded:
                        if (!File.Exists(backupFile))
                        {
                            AppLog.Warn("Нет резервной копии списка ipset — сначала обновите список из репозитория");
                            return false;
                        }
                        if (File.Exists(listFile)) File.Delete(listFile);
                        File.Move(backupFile, listFile);
                        break;
                }

                AppLog.Info("Режим ipset переключён: " + target);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось переключить режим ipset: " + ex.Message);
                return false;
            }
        }

        public static GameFilterMode GetGameFilterMode(string engineRoot)
        {
            try
            {
                var file = Path.Combine(engineRoot, "utils", "game_filter.enabled");
                if (!File.Exists(file)) return GameFilterMode.Disabled;

                var text = File.ReadAllText(file).Trim().ToLowerInvariant();
                return text switch
                {
                    "all" => GameFilterMode.TcpAndUdp,
                    "tcp" => GameFilterMode.TcpOnly,
                    "udp" => GameFilterMode.UdpOnly,
                    _ => GameFilterMode.TcpAndUdp
                };
            }
            catch { return GameFilterMode.Disabled; }
        }

        public static void SetGameFilterMode(string engineRoot, GameFilterMode mode)
        {
            try
            {
                var dir = Path.Combine(engineRoot, "utils");
                AppPaths.EnsureDir(dir);
                var file = Path.Combine(dir, "game_filter.enabled");

                if (mode == GameFilterMode.Disabled)
                {
                    if (File.Exists(file)) File.Delete(file);
                    return;
                }

                var text = mode switch
                {
                    GameFilterMode.TcpOnly => "tcp",
                    GameFilterMode.UdpOnly => "udp",
                    _ => "all"
                };
                File.WriteAllText(file, text);
            }
            catch (Exception ex) { AppLog.Warn("Не удалось изменить режим игрового фильтра: " + ex.Message); }
        }

        /// <summary>Флаг utils\check_updates.enabled используется .bat-файлами (легаси-поведение).</summary>
        public static bool GetBatAutoUpdateFlag(string engineRoot)
            => File.Exists(Path.Combine(engineRoot, "utils", "check_updates.enabled"));

        public static void SetBatAutoUpdateFlag(string engineRoot, bool enabled)
        {
            try
            {
                var dir = Path.Combine(engineRoot, "utils");
                AppPaths.EnsureDir(dir);
                var file = Path.Combine(dir, "check_updates.enabled");
                if (enabled) File.WriteAllText(file, "ENABLED" + Environment.NewLine);
                else if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) { AppLog.Warn("Не удалось изменить флаг авто-обновления: " + ex.Message); }
        }

        // ---------------------------------------------------------------- утилиты

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static void TryCopy(string source, string destination)
        {
            try
            {
                AppPaths.EnsureDir(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        /// <summary>Размер папки кэша Discord (для кнопки очистки в диагностике).</summary>
        public static long GetDiscordCacheSize()
        {
            try
            {
                var paths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Cache"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Code Cache"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "GPUCache")
                };

                long total = 0;
                foreach (var path in paths)
                {
                    if (!Directory.Exists(path)) continue;
                    total += new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                return total;
            }
            catch { return 0; }
        }

        public static (bool Ok, string Message) ClearDiscordCache()
        {
            try
            {
                var discord = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord");
                if (!Directory.Exists(discord)) return (false, "Папка Discord не найдена");

                if (Shell.IsProcessRunning("Discord"))
                    return (false, "Сначала закройте Discord — кэш занят приложением");

                int removed = 0;
                foreach (var name in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    var path = Path.Combine(discord, name);
                    if (!Directory.Exists(path)) continue;
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); removed++; } catch { }
                    }
                }
                return (true, $"Очищено файлов кэша: {removed}");
            }
            catch (Exception ex)
            {
                return (false, "Не удалось очистить кэш: " + ex.Message);
            }
        }
    }
}
