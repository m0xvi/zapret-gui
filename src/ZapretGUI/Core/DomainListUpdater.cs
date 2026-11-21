using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class DomainListUpdateResult
    {
        public bool Ok { get; init; }
        public int UpdatedFilesCount { get; init; }
        public int TotalDomainsCount { get; init; }
        public string Message { get; init; } = "";
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>
    /// Обновляет поддерживаемые списки Flowseal, проверяя формат до записи.
    /// Запрещено сохранять HTML-страницы прокси/ошибок в списки winws.
    /// </summary>
    public static class DomainListUpdater
    {
        private const string Repository = "Flowseal/zapret-discord-youtube";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object SeedLock = new();
        private static readonly SemaphoreSlim UpdateLock = new(1, 1);
        private static readonly string[] SafeIpSetExclusions =
        {
            "0.0.0.0/8", "10.0.0.0/8", "127.0.0.0/8", "172.16.0.0/12",
            "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "100.64.0.0/10",
            "::1", "fc00::/7", "fe80::/10"
        };

        // Flowseal удалил отдельные списки YouTube/Discord и ipset-discord. Их поддерживаем
        // локальными безопасными наборами; основной IPset теперь находится в .service/ipset-service.txt.
        private static readonly (string FileName, string Url, string[] Fallback)[] ListFiles =
        {
            ("list-general.txt", $"https://raw.githubusercontent.com/{Repository}/main/lists/list-general.txt", DefaultDomainLists.GeneralDomains),
            ("list-discord.txt", "", DefaultDomainLists.DiscordDomains),
            ("list-youtube.txt", "", DefaultDomainLists.YoutubeDomains),
            ("list-google.txt", $"https://raw.githubusercontent.com/{Repository}/main/lists/list-google.txt", DefaultDomainLists.GoogleDomains),
            ("list-exclude.txt", $"https://raw.githubusercontent.com/{Repository}/main/lists/list-exclude.txt", Array.Empty<string>()),
            ("ipset-all.txt", $"https://raw.githubusercontent.com/{Repository}/main/.service/ipset-service.txt", Array.Empty<string>()),
            ("ipset-discord.txt", "", Array.Empty<string>()),
            ("ipset-exclude.txt", $"https://raw.githubusercontent.com/{Repository}/main/lists/ipset-exclude.txt", Array.Empty<string>())
        };

        private static IEnumerable<string> GetMirrors(string primaryUrl)
        {
            yield return primaryUrl;
            if (!Uri.TryCreate(primaryUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                yield break;

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 4)
            {
                var owner = segments[0];
                var repository = segments[1];
                var reference = segments[2];
                var path = string.Join("/", segments.Skip(3));
                yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repository}@{reference}/{path}";
                yield return $"https://api.github.com/repos/{owner}/{repository}/contents/{path}?ref={Uri.EscapeDataString(reference)}";
            }
        }

        /// <summary>Проверяет текстовый формат списка до записи на диск.</summary>
        public static bool IsValidListContent(string fileName, string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            var trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (trimmed.StartsWith("<", StringComparison.Ordinal) ||
                trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("GitHub Proxy", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("{", StringComparison.Ordinal))
                return false;

            var records = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('\uFEFF'))
                .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
                .ToArray();

            if (fileName.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase))
                return records.Length > 0 && records.All(IsIpNetwork);

            if (records.Length == 0)
                return string.Equals(fileName, "list-exclude.txt", StringComparison.OrdinalIgnoreCase);
            return records.All(IsDomainEntry);
        }

        private static bool IsIpNetwork(string line)
        {
            var value = line.Trim();
            var slash = value.IndexOf('/');
            var addressPart = slash >= 0 ? value.Substring(0, slash) : value;
            if (addressPart.Length == 0 || !addressPart.All(ch => char.IsAsciiHexDigit(ch) || ch is ':' or '.')) return false;
            if (!IPAddress.TryParse(addressPart, out var address)) return false;
            if (slash < 0) return true;
            var prefixText = value.Substring(slash + 1);
            if (value.IndexOf('/', slash + 1) >= 0 || prefixText.Length == 0 ||
                !prefixText.All(char.IsAsciiDigit) || !int.TryParse(prefixText, out var prefix)) return false;
            var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            return prefix >= 0 && prefix <= maxPrefix;
        }

        private static bool IsDomainEntry(string line)
        {
            var value = line.Trim();
            if (value.StartsWith('^')) value = value.Substring(1);
            if (value.StartsWith("*.", StringComparison.Ordinal)) value = value.Substring(2);
            if (value.EndsWith('$')) value = value.Substring(0, value.Length - 1);
            if (value.Length is 0 or > 253 || !value.Contains('.')) return false;
            return value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '*');
        }

        private static bool TryRestoreBackup(string enginePath, string fileName, out string content)
        {
            var candidates = new[]
            {
                Path.Combine(enginePath, "lists", fileName + ".backup"),
                Path.Combine(enginePath, "lists", fileName + ".bak"),
                Path.Combine(AppPaths.BackupDir, fileName + ".bak")
            };
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var saved = File.ReadAllText(candidate, Encoding.UTF8);
                    if (!IsValidListContent(fileName, saved)) continue;
                    content = saved;
                    return true;
                }
                catch { }
            }
            content = "";
            return false;
        }

        private static void WriteAtomically(string targetPath, string content)
        {
            var tempPath = targetPath + ".download-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, content, Utf8NoBom);
                File.Move(tempPath, targetPath, true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static string SafeFallback(string fileName)
        {
            if (string.Equals(fileName, "ipset-exclude.txt", StringComparison.OrdinalIgnoreCase))
                return string.Join(Environment.NewLine, SafeIpSetExclusions) + Environment.NewLine;
            if (fileName.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase))
                return "203.0.113.113/32" + Environment.NewLine;
            return "# No valid upstream list available; this file is intentionally empty." + Environment.NewLine;
        }

        /// <summary>
        /// Проверяет кэшированные файлы при старте. Повреждённый HTML восстанавливает из
        /// резервной копии, затем из встроенного списка, а для IPset — в безопасный режим.
        /// </summary>
        public static void EnsureSeeded(string enginePath)
        {
            lock (SeedLock) EnsureSeededCore(enginePath);
        }

        private static void EnsureSeededCore(string enginePath)
        {
            if (string.IsNullOrWhiteSpace(enginePath)) return;
            var listsDir = Path.Combine(enginePath, "lists");
            try { AppPaths.EnsureDir(listsDir); }
            catch (Exception ex) { AppLog.Warn("[ListUpdater] Не удалось открыть каталог списков: " + ex.Message); return; }

            foreach (var (fileName, _, fallback) in ListFiles)
            {
                var filePath = Path.Combine(listsDir, fileName);
                try
                {
                    var current = File.Exists(filePath) ? File.ReadAllText(filePath, Encoding.UTF8) : "";
                    var valid = IsValidListContent(fileName, current);
                    var recordCount = current.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Count(line => !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith(';'));
                    if (valid && (fallback.Length == 0 || recordCount >= 5)) continue;

                    string replacement;
                    if (fallback.Length > 0)
                    {
                        replacement = string.Join(Environment.NewLine, fallback) + Environment.NewLine;
                        AppLog.Warn($"[ListUpdater] {fileName} пуст или повреждён; восстановлен встроенный список ({fallback.Length} доменов)");
                    }
                    else if (TryRestoreBackup(enginePath, fileName, out var backup))
                    {
                        replacement = backup;
                        AppLog.Warn($"[ListUpdater] {fileName} повреждён; восстановлен из резервной копии");
                    }
                    else
                    {
                        replacement = SafeFallback(fileName);
                        AppLog.Warn($"[ListUpdater] {fileName} пуст или повреждён; включён безопасный резервный список");
                    }
                    WriteAtomically(filePath, replacement);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[ListUpdater] Не удалось проверить/восстановить {fileName}: {ex.Message}");
                }
            }

            try { EnsureCriticalPreviewDomains(listsDir); }
            catch (Exception ex) { AppLog.Warn("[ListUpdater] Миграция превью: " + ex.Message); }
        }

        private static void EnsureCriticalPreviewDomains(string listsDir)
        {
            var critical = new[] { "lh3.googleusercontent.com", "lh4.googleusercontent.com", "lh5.googleusercontent.com", "i9.ytimg.com", "ytimg.l.google.com", "yt3.ggpht.com", "yt4.ggpht.com", "i.ytimg.com", "googlevideo.com" };
            var targets = new[] { "list-youtube.txt", "list-general.txt" };
            foreach (var fileName in targets)
            {
                var path = Path.Combine(listsDir, fileName);
                if (!File.Exists(path)) continue;
                var content = File.ReadAllText(path, Encoding.UTF8);
                if (!IsValidListContent(fileName, content)) continue;
                var lines = content.Split('\n').Select(line => line.Trim().TrimStart('\uFEFF'))
                    .Where(line => line.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = critical.Where(domain => !lines.Contains(domain)).ToArray();
                if (missing.Length == 0) continue;
                WriteAtomically(path, content.TrimEnd('\r', '\n') + Environment.NewLine +
                    string.Join(Environment.NewLine, missing) + Environment.NewLine);
                AppLog.Info($"[ListUpdater] Миграция превью: добавлено {missing.Length} доменов в {fileName}: {string.Join(", ", missing)}");
            }
        }

        private static async Task<string> ReadMirrorContentAsync(HttpClient http, string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ZapretGUI");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (Uri.TryCreate(url, UriKind.Absolute, out var apiUri) && apiUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && content.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("content", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("GitHub API не вернул поле content");
                var base64 = string.Concat((encoded.GetString() ?? "").Where(ch => !char.IsWhiteSpace(ch)));
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            return content;
        }

        public static async Task<DomainListUpdateResult> UpdateAllAsync(string enginePath, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            try
            {
                await UpdateLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new DomainListUpdateResult { Ok = false, Message = "Обновление списков отменено.", Elapsed = TimeSpan.Zero };
            }

            try
            {
                return await UpdateAllCoreAsync(enginePath, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                UpdateLock.Release();
            }
        }

        private static async Task<DomainListUpdateResult> UpdateAllCoreAsync(string enginePath, IProgress<string>? progress, CancellationToken ct)
        {
            var startedAt = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(enginePath))
                return new DomainListUpdateResult { Ok = false, Message = "Путь к движку не задан.", Elapsed = DateTime.UtcNow - startedAt };

            var listsDir = Path.Combine(enginePath, "lists");
            try { AppPaths.EnsureDir(listsDir); }
            catch (Exception ex)
            {
                return new DomainListUpdateResult { Ok = false, Message = "Не удалось открыть каталог списков: " + ex.Message, Elapsed = DateTime.UtcNow - startedAt };
            }

            EnsureSeeded(enginePath);
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var updated = 0;
            var totalRecords = 0;
            var failed = new List<string>();

            try
            {
                foreach (var (fileName, url, _) in ListFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(url)) continue; // встроенные/удалённые upstream-списки не запрашиваем
                    progress?.Report($"Загружаю {fileName}…");
                    var downloaded = false;

                    foreach (var mirror in GetMirrors(url))
                    {
                        try
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            cts.CancelAfter(TimeSpan.FromSeconds(12));
                            var content = await ReadMirrorContentAsync(http, mirror, cts.Token).ConfigureAwait(false);
                            if (!IsValidListContent(fileName, content))
                            {
                                AppLog.Warn($"[ListUpdater] {mirror} вернул не список {fileName}; ответ отброшен, существующий файл не меняю");
                                continue;
                            }

                            var targetPath = Path.Combine(listsDir, fileName);
                            var oldContent = File.Exists(targetPath) ? File.ReadAllText(targetPath, Encoding.UTF8) : "";
                            if (IsValidListContent(fileName, oldContent))
                            {
                                var backupPath = Path.Combine(AppPaths.BackupDir, fileName + ".bak");
                                AppPaths.EnsureDir(AppPaths.BackupDir);
                                File.Copy(targetPath, backupPath, true);
                            }

                            WriteAtomically(targetPath, content);

                            updated++;
                            totalRecords += content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Count(line => !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith(';'));
                            if (!mirror.Equals(url, StringComparison.OrdinalIgnoreCase))
                                AppLog.Info($"[ListUpdater] {fileName} получен через зеркало {mirror}");
                            downloaded = true;
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            AppLog.Warn($"[ListUpdater] Таймаут {mirror} для {fileName}");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"[ListUpdater] Не удалось получить {fileName} с {mirror}: {ex.Message}");
                        }
                    }

                    if (!downloaded)
                    {
                        failed.Add(fileName);
                        AppLog.Warn($"[ListUpdater] {fileName} не обновлён; сохранён последний проверенный вариант");
                    }
                }

                try { EnsureCriticalPreviewDomains(listsDir); }
                catch (Exception ex) { AppLog.Warn("[ListUpdater] Не удалось применить миграцию доменов YouTube: " + ex.Message); }

                var elapsed = DateTime.UtcNow - startedAt;
                var message = failed.Count == 0
                    ? $"Списки обновлены: {updated} файлов, {totalRecords} записей за {elapsed.TotalSeconds:0.0} с."
                    : $"Обновлено {updated} файлов ({totalRecords} записей), не удалось обновить: {string.Join(", ", failed)}. Проверенные локальные копии сохранены.";
                return new DomainListUpdateResult
                {
                    Ok = failed.Count == 0 && updated > 0,
                    UpdatedFilesCount = updated,
                    TotalDomainsCount = totalRecords,
                    Message = message,
                    Elapsed = elapsed
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new DomainListUpdateResult { Ok = false, Message = "Обновление списков отменено.", Elapsed = DateTime.UtcNow - startedAt };
            }
            catch (Exception ex)
            {
                return new DomainListUpdateResult { Ok = false, Message = "Ошибка обновления списков: " + ex.Message, Elapsed = DateTime.UtcNow - startedAt };
            }
        }
    }
}
