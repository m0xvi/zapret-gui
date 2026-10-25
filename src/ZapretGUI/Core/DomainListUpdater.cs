using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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
    /// Автоматическое и ручное обновление списков доменов и IP из официального репозитория Flowseal,
    /// а также автоматическое первичное наполнение (auto-seeding) при пустых списках.
    /// Поддерживает форматы и структуру Flowseal 1.10.2, 1.10.3 и новее.
    /// </summary>
    public static class DomainListUpdater
    {
        private static readonly (string FileName, string Url, string[] FallbackDomains)[] ListFiles = new[]
        {
            ("list-general.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-general.txt", DefaultDomainLists.GeneralDomains),
            ("list-discord.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-discord.txt", DefaultDomainLists.DiscordDomains),
            ("list-youtube.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-youtube.txt", DefaultDomainLists.YoutubeDomains),
            ("list-google.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-google.txt", DefaultDomainLists.GoogleDomains),
            ("ipset-all.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/ipset-all.txt", Array.Empty<string>()),
            ("ipset-discord.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/ipset-discord.txt", Array.Empty<string>())
        };

        private static IEnumerable<string> GetMirrors(string primaryUrl)
        {
            yield return primaryUrl;
            if (primaryUrl.Contains("raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/"))
            {
                var tail = primaryUrl.Split("zapret-discord-youtube/main/")[1];
                yield return "https://cdn.jsdelivr.net/gh/Flowseal/zapret-discord-youtube@main/" + tail;
                yield return "https://raw.fastgit.org/Flowseal/zapret-discord-youtube/main/" + tail;
                yield return "https://ghproxy.com/" + primaryUrl;
            }
        }

        /// <summary>
        /// Проверяет наличие и наполненность списков. Если какой-либо файл пуст или отсутствует,
        /// автоматически заполняет его эталонным набором доменов.
        /// </summary>
        public static void EnsureSeeded(string enginePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(enginePath)) return;
                var listsDir = Path.Combine(enginePath, "lists");
                AppPaths.EnsureDir(listsDir);

                foreach (var (fileName, _, fallback) in ListFiles)
                {
                    if (fallback.Length == 0) continue;
                    var filePath = Path.Combine(listsDir, fileName);
                    var needsSeed = !File.Exists(filePath) ||
                                    File.ReadAllLines(filePath).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")) < 5;

                    if (needsSeed)
                    {
                        var content = string.Join(Environment.NewLine, fallback) + Environment.NewLine;
                        File.WriteAllText(filePath, content, Encoding.UTF8);
                        AppLog.Info($"[ListUpdater] Файл {fileName} автоматически наполнен эталонным набором ({fallback.Length} доменов)");
                    }
                }

                // Миграция для превью YouTube за рубежом ( issue: не грузятся превью с любой стратегией в другой стране)
                // Добавляем критичные хосты превью, если их нет в существующих списках — иначе у пользователя из другой точки мира превью ytimg/lh3/ggpht не попадут под --hostlist
                try { EnsureCriticalPreviewDomains(listsDir); } catch (Exception ex) { AppLog.Warn("[ListUpdater] Миграция превью: " + ex.Message); }
            }
            catch (Exception ex)
            {
                AppLog.Warn("[ListUpdater] Ошибка начального наполнения списков: " + ex.Message);
            }
        }

        private static void EnsureCriticalPreviewDomains(string listsDir)
        {
            var critical = new[] { "lh3.googleusercontent.com", "lh4.googleusercontent.com", "lh5.googleusercontent.com", "i9.ytimg.com", "ytimg.l.google.com", "yt3.ggpht.com", "yt4.ggpht.com", "i.ytimg.com", "googlevideo.com" };
            var targets = new[] { "list-youtube.txt", "list-general.txt" };
            foreach (var fileName in targets)
            {
                var path = Path.Combine(listsDir, fileName);
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = critical.Where(d => !lines.Contains(d)).ToArray();
                if (missing.Length > 0)
                {
                    File.AppendAllLines(path, missing, Encoding.UTF8);
                    AppLog.Info($"[ListUpdater] Миграция превью: добавлено {missing.Length} доменов в {fileName}: {string.Join(", ", missing)}");
                }
            }
        }

        public static async Task<DomainListUpdateResult> UpdateAllAsync(string enginePath, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var startedAt = DateTime.UtcNow;
            var listsDir = Path.Combine(enginePath ?? "", "lists");
            AppPaths.EnsureDir(listsDir);

            // Сначала гарантируем базовую наполненность
            EnsureSeeded(enginePath ?? "");

            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            var updated = 0;
            var totalDomains = 0;

            try
            {
                foreach (var (fileName, url, fallback) in ListFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Загружаю {fileName}…");

                    bool downloaded = false;
                    foreach (var mirror in GetMirrors(url))
                    {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(12));
                        var response = await http.GetAsync(mirror, cts.Token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(content) && content.Length > 20)
                            {
                                var targetPath = Path.Combine(listsDir, fileName);

                                if (File.Exists(targetPath))
                                {
                                    var bakPath = Path.Combine(AppPaths.BackupDir, $"{fileName}.bak");
                                    AppPaths.EnsureDir(AppPaths.BackupDir);
                                    File.Copy(targetPath, bakPath, true);
                                }

                                await File.WriteAllTextAsync(targetPath, content, Encoding.UTF8, ct).ConfigureAwait(false);
                                updated++;

                                var linesCount = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                                totalDomains += linesCount;
                                if (!mirror.Equals(url, StringComparison.OrdinalIgnoreCase)) AppLog.Info($"[ListUpdater] {fileName} получен через зеркало {mirror}");
                                downloaded = true;
                                break;
                            }
                        }
                        else { AppLog.Warn($"[ListUpdater] {mirror} вернул {(int)response.StatusCode}"); }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { AppLog.Warn($"[ListUpdater] Таймаут {mirror} для {fileName}"); continue; }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"[ListUpdater] Сетевая ошибка {mirror} для {fileName}: {ex.Message}");
                    }
                    }
                    if (downloaded) continue;

                    // Если файл отсутствует или пуст, а сеть не ответила — используем fallback
                    var currentPath = Path.Combine(listsDir, fileName);
                    if ((!File.Exists(currentPath) || File.ReadAllLines(currentPath).Length < 2) && fallback.Length > 0)
                    {
                        File.WriteAllText(currentPath, string.Join(Environment.NewLine, fallback) + Environment.NewLine, Encoding.UTF8);
                        updated++;
                        totalDomains += fallback.Length;
                    }
                }

                var elapsed = DateTime.UtcNow - startedAt;
                var msg = updated > 0
                    ? $"Списки успешно обновлены: актуализировано {updated} файлов, всего {totalDomains} записей за {elapsed.TotalSeconds:0.0} с."
                    : "Списки уже актуальны.";

                return new DomainListUpdateResult
                {
                    Ok = updated > 0,
                    UpdatedFilesCount = updated,
                    TotalDomainsCount = totalDomains,
                    Message = msg,
                    Elapsed = elapsed
                };
            }
            catch (Exception ex)
            {
                return new DomainListUpdateResult
                {
                    Ok = false,
                    Message = "Ошибка обновления списков: " + ex.Message,
                    Elapsed = DateTime.UtcNow - startedAt
                };
            }
        }
    }
}
