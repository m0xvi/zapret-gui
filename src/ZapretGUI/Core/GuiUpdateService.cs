using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class GuiReleaseAsset
    {
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public string DownloadUrl { get; init; } = "";
        public string Digest { get; init; } = "";
        public string SizeText => Size <= 0 ? "" : (Size / 1024.0 / 1024.0).ToString("0.0") + " МБ";
    }

    public sealed class GuiReleaseInfo
    {
        public string Repository { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Title { get; init; } = "";
        public string HtmlUrl { get; init; } = "";
        public DateTime? PublishedAt { get; init; }
        public bool Prerelease { get; init; }
        public List<GuiReleaseAsset> Assets { get; init; } = new();
        public GuiReleaseAsset? PortableAsset => Assets.FirstOrDefault(GuiUpdateService.IsSupportedReleaseAsset);
        public string PublishedText => PublishedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "неизвестно";
    }

    public sealed class GuiUpdateResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public bool IntegrityVerified { get; init; }
        public bool BackupCreated { get; init; }
        public bool RestartScheduled { get; init; }
        public string Version { get; init; } = "";
    }

    /// <summary>План обновления, который читает временный процесс-установщик.</summary>
    public sealed class GuiUpdatePlan
    {
        public string TargetPath { get; set; } = "";
        public string StagedPath { get; set; } = "";
        public string HelperPath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public string PersistentBackupPath { get; set; } = "";
        public string ExpectedSha256 { get; set; } = "";
        public long ExpectedSize { get; set; }
        public string Version { get; set; } = "";
        public int ParentProcessId { get; set; }
        public string State { get; set; } = "Prepared";
    }

    /// <summary>
    /// Безопасное самообновление GUI.
    /// Новый exe сначала скачивается во временную папку и проверяется по digest SHA-256
    /// из GitHub API. Замена выполняется отдельным процессом после закрытия GUI, поэтому
    /// работающий exe не перезаписывается и при ошибке сохраняется резервная копия.
    /// </summary>
    public static class GuiUpdateService
    {
        public const string DefaultRepository = "m0xvi/zapret-gui";
        private const string ApplyArgument = "--apply-gui-update";
        private const string PortableSuffix = "-win-x64-portable.exe";
        private const int ParentWaitTimeoutMs = 30000;

        private static readonly HttpClient Http = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string PlanFile => AppPaths.GuiUpdatePlanFile;

        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var executable = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                    {
                        var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
                        if (!string.IsNullOrWhiteSpace(version)) return version.Split('+')[0];
                    }
                }
                catch { }

                return typeof(GuiUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretGUI", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static bool TryParseRepository(string? value, out string repository)
        {
            repository = "";
            if (string.IsNullOrWhiteSpace(value)) value = DefaultRepository;
            value = value.Trim().Trim('/');
            if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("https://github.com/".Length).Trim('/');

            var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(ch =>
                    !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))))
                return false;

            repository = parts[0] + "/" + parts[1];
            return true;
        }

        public static bool IsSupportedReleaseAsset(GuiReleaseAsset asset)
            => asset.Name.StartsWith("ZapretGUI-", StringComparison.OrdinalIgnoreCase)
               && asset.Name.EndsWith(PortableSuffix, StringComparison.OrdinalIgnoreCase)
               && asset.Size > 0
               && !string.IsNullOrWhiteSpace(asset.DownloadUrl)
               && !string.IsNullOrWhiteSpace(asset.Digest);

        public static async Task<GuiReleaseInfo?> GetLatestReleaseAsync(
            string? configuredRepository, bool includePrerelease, CancellationToken ct = default)
        {
            if (!TryParseRepository(configuredRepository, out var repository))
            {
                AppLog.Warn("Некорректный репозиторий обновлений GUI: " + configuredRepository);
                return null;
            }

            var releasesUrl = "https://api.github.com/repos/" + repository + "/releases?per_page=20";
            try
            {
                using var response = await Http.GetAsync(releasesUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"GitHub API обновлений GUI вернул {(int)response.StatusCode}");
                    return null;
                }

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                    var prerelease = item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
                    if (prerelease && !includePrerelease) continue;

                    var release = new GuiReleaseInfo
                    {
                        Repository = repository,
                        Tag = GetString(item, "tag_name"),
                        Title = GetString(item, "name"),
                        HtmlUrl = GetString(item, "html_url"),
                        Prerelease = prerelease,
                        PublishedAt = ParseDate(item, "published_at")
                    };

                    if (item.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            release.Assets.Add(new GuiReleaseAsset
                            {
                                Name = GetString(asset, "name"),
                                Size = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                                DownloadUrl = GetString(asset, "browser_download_url"),
                                Digest = GetString(asset, "digest")
                            });
                        }
                    }

                    return release;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось получить релиз обновления GUI: " + ex.Message);
            }

            return null;
        }

        public static async Task<GuiUpdateResult> DownloadAndScheduleAsync(
            GuiReleaseInfo release, IProgress<ProgressInfo>? progress = null,
            CancellationToken ct = default)
        {
            var asset = release.PortableAsset;
            if (asset == null)
                return Failure("В релизе нет проверяемого portable EXE с digest SHA-256.");
            if (!TryParseRepository(release.Repository, out var repository) ||
                !IsSafeReleaseUrl(asset.DownloadUrl, repository))
                return Failure("Ссылка на EXE не принадлежит указанному GitHub-репозиторию.");

            // Надёжное определение текущего exe: MainModule может быть пустым в single-file / при запуске из dll
            var target = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
                target = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                try { target = System.Reflection.Assembly.GetEntryAssembly()?.Location; } catch {}
            }
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                try { target = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch {}
            }
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                // Последняя попытка — база приложения (для dotnet --roll-forward)
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    var candidate = Path.Combine(baseDir.TrimEnd(Path.DirectorySeparatorChar), "ZapretGUI.exe");
                    if (File.Exists(candidate)) target = candidate;
                    else
                    {
                        var dllCandidate = Path.Combine(baseDir.TrimEnd(Path.DirectorySeparatorChar), "ZapretGUI.dll");
                        if (File.Exists(dllCandidate)) target = dllCandidate;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                AppLog.Warn($"[GuiUpdate] Не удалось определить exe: ProcessPath={Environment.ProcessPath}, MainModule={Process.GetCurrentProcess().MainModule?.FileName}, EntryLocation={System.Reflection.Assembly.GetEntryAssembly()?.Location}");
                return Failure("Не удалось определить текущий exe для обновления.");
            }
            // Single-file публикуется как dll+exe, но Location может указывать на dll — нормализуем к exe
            if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var exeCandidate = Path.ChangeExtension(target, ".exe");
                if (File.Exists(exeCandidate)) target = exeCandidate;
            }
            var targetFileName = Path.GetFileNameWithoutExtension(target);
            if (!targetFileName.StartsWith("ZapretGUI", StringComparison.OrdinalIgnoreCase))
                return Failure("Самообновление доступно только для ZapretGUI.exe (текущий файл: " + Path.GetFileName(target) + "). Переименуйте файл в ZapretGUI.exe или скачайте обновление вручную со страницы релиза.");

            var updateId = Guid.NewGuid().ToString("N");
            var updateDirectory = Path.Combine(AppPaths.TempDir, "gui-update-" + updateId);
            var staged = Path.Combine(updateDirectory, asset.Name);
            var helperDirectory = Path.Combine(updateDirectory, "helper");
            var helper = Path.Combine(helperDirectory, Path.GetFileName(target));
            var backup = target + ".zapretgui-update-backup";
            var persistentBackup = Path.Combine(
                AppPaths.GuiBackupDir,
                Path.GetFileNameWithoutExtension(target) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".exe");
            var plan = new GuiUpdatePlan
            {
                TargetPath = Path.GetFullPath(target),
                StagedPath = Path.GetFullPath(staged),
                HelperPath = Path.GetFullPath(helper),
                BackupPath = Path.GetFullPath(backup),
                PersistentBackupPath = Path.GetFullPath(persistentBackup),
                ExpectedSha256 = NormalizeDigest(asset.Digest),
                ExpectedSize = asset.Size,
                Version = release.Tag,
                ParentProcessId = Environment.ProcessId
            };

            try
            {
                AppLog.Info($"[GuiUpdate] Подготовка обновления {release.Tag} из {asset.DownloadUrl} в {staged}");
                Directory.CreateDirectory(updateDirectory);
                Directory.CreateDirectory(AppPaths.GuiBackupDir);
                Directory.CreateDirectory(helperDirectory);
                progress?.Report(new ProgressInfo { Percent = 0, Status = "Скачиваю обновление GUI" });
                await DownloadFileAsync(asset.DownloadUrl, staged, asset.Size, progress, ct).ConfigureAwait(false);
                progress?.Report(new ProgressInfo { Percent = -1, Status = "Проверяю SHA-256 обновления GUI" });
                await VerifyDigestAsync(staged, plan.ExpectedSha256, ct).ConfigureAwait(false);

                Directory.CreateDirectory(AppPaths.GuiBackupDir);
                Directory.CreateDirectory(helperDirectory);
                CopyHelperFiles(target, helperDirectory);
                SavePlan(plan);

                progress?.Report(new ProgressInfo { Percent = -1, Status = "Готовлю безопасный перезапуск GUI" });
                AppLog.Info($"[GuiUpdate] Запускаю helper: {helper} {ApplyArgument} {PlanFile}");
                var startInfo = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = $"{ApplyArgument} \"{PlanFile}\"",
                    WorkingDirectory = helperDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                try
                {
                    var helperProcess = Process.Start(startInfo);
                    if (helperProcess == null)
                        throw new InvalidOperationException("Не удалось запустить временный процесс обновления (Process.Start вернул null).");
                    AppLog.Info("[GuiUpdate] Helper запущен, ожидаю перезапуск");
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    // Пользователь нажал "Нет" в диалоге UAC
                    AppLog.Warn("[GuiUpdate] Пользователь отклонил UAC при запуске helper: " + ex.Message);
                    throw new OperationCanceledException("Обновление отменено — требуются права администратора. Нажмите «Да» в диалоге UAC или запустите Zapret GUI от имени администратора и повторите.", ex);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    AppLog.Error("[GuiUpdate] Ошибка запуска helper (Win32): " + ex.Message);
                    throw new InvalidOperationException("Не удалось запустить helper с правами администратора: " + ex.Message + ". Попробуйте запустить Zapret GUI от имени администратора.", ex);
                }

                return new GuiUpdateResult
                {
                    Ok = true,
                    Message = $"Обновление GUI до {release.Tag} подготовлено. Приложение перезапустится после закрытия.",
                    IntegrityVerified = true,
                    BackupCreated = true,
                    RestartScheduled = true,
                    Version = release.Tag
                };
            }
            catch (OperationCanceledException ex)
            {
                CleanupUpdateFiles(updateDirectory, PlanFile);
                // Если это отмена UAC, показываем понятное сообщение
                if (ex.Message.Contains("UAC") || ex.Message.Contains("администратора"))
                    return Failure(ex.Message);
                return Failure("Загрузка обновления GUI отменена.");
            }
            catch (Exception ex)
            {
                CleanupUpdateFiles(updateDirectory, PlanFile);
                AppLog.Error("Не удалось подготовить обновление GUI: " + ex.ToString());
                // Даём подсказку для ручной установки
                var hint = ex is InvalidDataException || ex is FileNotFoundException || ex is System.Net.Http.HttpRequestException
                    ? " Попробуйте скачать обновление вручную со страницы релиза."
                    : "";
                return Failure("Не удалось подготовить обновление GUI: " + ex.Message + hint);
            }
        }

        public static bool IsUpdaterMode(IReadOnlyList<string> args)
            => args.Count >= 2 && string.Equals(args[0], ApplyArgument, StringComparison.OrdinalIgnoreCase);

        /// <summary>Вызывается до создания окна в копии exe-установщика.</summary>
        public static int RunUpdaterMode(IReadOnlyList<string> args)
        {
            if (!IsUpdaterMode(args)) return -1;
            try
            {
                var planPath = Path.GetFullPath(args[1]);
                var plan = LoadPlan(planPath);
                if (!ValidatePlan(plan)) return 1;
                WaitForParent(plan.ParentProcessId);
                ApplyPlan(plan, planPath);
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Error("Процесс обновления GUI завершился с ошибкой: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Восстанавливает старый exe после аварии между двумя операциями замены.
        /// Вызывается при обычном запуске и не выполняет скачивание или системные действия.
        /// </summary>
        public static void RecoverInterruptedUpdate()
        {
            try
            {
                if (!File.Exists(PlanFile)) return;
                var plan = LoadPlan(PlanFile);
                if (!ValidatePlan(plan)) return;

                var targetExists = File.Exists(plan.TargetPath);
                var backupExists = File.Exists(plan.BackupPath);
                if (targetExists && ComputeSha256(plan.TargetPath) == plan.ExpectedSha256)
                {
                    if (backupExists) File.Delete(plan.BackupPath);
                    TryDelete(plan.StagedPath);
                    TryDeleteDirectory(Path.GetDirectoryName(plan.HelperPath));
                    TryDelete(PlanFile);
                    return;
                }

                if (targetExists && backupExists && string.Equals(plan.State, "Applying", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(plan.TargetPath);
                    File.Move(plan.BackupPath, plan.TargetPath, true);
                    TryDelete(plan.StagedPath);
                    TryDeleteDirectory(Path.GetDirectoryName(plan.HelperPath));
                    TryDelete(PlanFile);
                    AppLog.Warn("После прерванного обновления GUI восстановлена предыдущая версия exe.");
                    return;
                }

                if (!targetExists && backupExists)
                {
                    File.Move(plan.BackupPath, plan.TargetPath, true);
                    TryDelete(plan.StagedPath);
                    TryDeleteDirectory(Path.GetDirectoryName(plan.HelperPath));
                    TryDelete(PlanFile);
                    AppLog.Warn("После прерванного обновления GUI восстановлена предыдущая версия exe.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось проверить прерванное обновление GUI: " + ex.Message);
            }
        }

        private static void ApplyPlan(GuiUpdatePlan plan, string planPath)
        {
            if (!File.Exists(plan.StagedPath) || !File.Exists(plan.TargetPath))
                throw new FileNotFoundException("Не найден staged exe или текущий exe.");
            if (ComputeSha256(plan.StagedPath) != plan.ExpectedSha256)
                throw new InvalidDataException("SHA-256 staged exe не совпадает с планом обновления.");

            Directory.CreateDirectory(Path.GetDirectoryName(plan.PersistentBackupPath)!);
            File.Copy(plan.TargetPath, plan.PersistentBackupPath, true);
            PruneGuiBackups(plan.PersistentBackupPath);
            plan.State = "Applying";
            SavePlan(plan);

            try
            {
                TryDelete(plan.BackupPath);
                try
                {
                    // File.Replace даёт атомарную замену на одном томе и сохраняет старый exe.
                    File.Replace(plan.StagedPath, plan.TargetPath, plan.BackupPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithRollback(plan);
                }
                catch (IOException)
                {
                    // Временная папка может находиться на другом томе — используем
                    // откатируемую пару Move в той же папке, не оставляя пустой target.
                    ReplaceWithRollback(plan);
                }

                if (ComputeSha256(plan.TargetPath) != plan.ExpectedSha256)
                    throw new InvalidDataException("SHA-256 установленного exe не совпадает с релизом.");

                plan.State = "Replaced";
                SavePlan(plan);
                var started = Process.Start(new ProcessStartInfo(plan.TargetPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(plan.TargetPath)!
                });
                if (started == null) throw new InvalidOperationException("Новый GUI не запустился.");
                Thread.Sleep(2000);
                if (started.HasExited) throw new InvalidOperationException("Новый GUI завершился сразу после запуска.");

                TryDelete(plan.BackupPath);
                TryDelete(plan.StagedPath);
                TryDeleteDirectory(Path.GetDirectoryName(plan.HelperPath));
                TryDelete(planPath);
            }
            catch
            {
                RestoreAfterFailedReplace(plan);
                throw;
            }
        }

        private static void ReplaceWithRollback(GuiUpdatePlan plan)
        {
            File.Move(plan.TargetPath, plan.BackupPath, true);
            try
            {
                File.Move(plan.StagedPath, plan.TargetPath, true);
            }
            catch
            {
                if (!File.Exists(plan.TargetPath) && File.Exists(plan.BackupPath))
                    File.Move(plan.BackupPath, plan.TargetPath, true);
                throw;
            }
        }

        private static void RestoreAfterFailedReplace(GuiUpdatePlan plan)
        {
            try
            {
                if (File.Exists(plan.BackupPath))
                {
                    TryDelete(plan.TargetPath);
                    File.Move(plan.BackupPath, plan.TargetPath, true);
                }
                TryDelete(plan.StagedPath);
                TryDeleteDirectory(Path.GetDirectoryName(plan.HelperPath));
                TryDelete(PlanFile);
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось восстановить старый exe GUI: " + ex.Message);
            }
        }

        private static void CopyHelperFiles(string target, string helperDirectory)
        {
            var sourceDirectory = Path.GetDirectoryName(target)!;
            var executableName = Path.GetFileName(target);
            File.Copy(target, Path.Combine(helperDirectory, executableName), true);
            var baseName = Path.GetFileNameWithoutExtension(target);
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var source = Path.Combine(sourceDirectory, baseName + suffix);
                if (File.Exists(source)) File.Copy(source, Path.Combine(helperDirectory, baseName + suffix), true);
            }
        }

        private static void WaitForParent(int processId)
        {
            if (processId <= 0 || processId == Environment.ProcessId) return;
            try
            {
                using var parent = Process.GetProcessById(processId);
                if (!parent.WaitForExit(ParentWaitTimeoutMs))
                    throw new TimeoutException("Предыдущий GUI не завершился за отведённое время.");
            }
            catch (ArgumentException) { }
        }

        private static bool ValidatePlan(GuiUpdatePlan plan)
        {
            if (string.IsNullOrWhiteSpace(plan.TargetPath) || string.IsNullOrWhiteSpace(plan.StagedPath) ||
                string.IsNullOrWhiteSpace(plan.HelperPath) || string.IsNullOrWhiteSpace(plan.BackupPath) ||
                string.IsNullOrWhiteSpace(plan.ExpectedSha256)) return false;
            if (plan.ExpectedSha256.Length != 64 || plan.ExpectedSha256.Any(c => !Uri.IsHexDigit(c))) return false;
            if (!string.Equals(Path.GetExtension(plan.TargetPath), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Path.GetFileNameWithoutExtension(plan.TargetPath).StartsWith("ZapretGUI", StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsUnder(plan.StagedPath, AppPaths.TempDir) || !IsUnder(plan.HelperPath, AppPaths.TempDir)) return false;
            if (string.Equals(Path.GetFullPath(plan.TargetPath), Path.GetFullPath(plan.HelperPath), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static bool IsUnder(string path, string root)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static void SavePlan(GuiUpdatePlan plan)
        {
            var temp = PlanFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(plan, JsonOptions));
            if (File.Exists(PlanFile)) File.Replace(temp, PlanFile, null);
            else File.Move(temp, PlanFile);
        }

        private static GuiUpdatePlan LoadPlan(string path)
            => JsonSerializer.Deserialize<GuiUpdatePlan>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException("Пустой план обновления GUI.");

        public static bool IsSafeReleaseUrl(string url, string repository)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
            return uri.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDigest(string digest)
        {
            var separator = digest.IndexOf(':');
            if (separator >= 0) digest = digest[(separator + 1)..];
            return digest.Trim().ToLowerInvariant();
        }

        private static async Task VerifyDigestAsync(string path, string expected, CancellationToken ct)
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 обновления GUI не совпадает с digest GitHub.");
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static async Task DownloadFileAsync(string url, string path, long expectedSize,
            IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedSize;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (total > 0) progress?.Report(new ProgressInfo
                {
                    Percent = received * 100.0 / total,
                    Status = $"Скачано {received / 1024.0 / 1024.0:0.0} из {total / 1024.0 / 1024.0:0.0} МБ"
                });
            }
            if (expectedSize > 0 && received != expectedSize)
                throw new InvalidDataException($"Размер обновления не совпадает: получено {received}, ожидалось {expectedSize} байт");
        }

        private static DateTime? ParseDate(JsonElement item, string property)
            => item.TryGetProperty(property, out var value) && DateTime.TryParse(value.GetString(), out var date)
                ? date : null;

        private static string GetString(JsonElement item, string property)
            => item.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

        private static GuiUpdateResult Failure(string message) => new() { Message = message };

        private static void PruneGuiBackups(string keepPath)
        {
            try
            {
                var backups = Directory.GetFiles(AppPaths.GuiBackupDir, "ZapretGUI-*.exe")
                    .Where(path => !string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .ToList();
                foreach (var path in backups.Skip(2)) TryDelete(path);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось ограничить резервные копии GUI: " + ex.Message);
            }
        }

        private static void CleanupUpdateFiles(string updateDirectory, string planPath)
        {
            TryDelete(planPath);
            TryDeleteDirectory(updateDirectory);
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string? path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
