using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
    /// Автоматическое и ручное обновление списков доменов и IP из официального репозитория Flowseal.
    /// </summary>
    public static class DomainListUpdater
    {
        private static readonly (string FileName, string Url)[] ListFiles = new[]
        {
            ("list-general.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-general.txt"),
            ("list-discord.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-discord.txt"),
            ("list-youtube.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/list-youtube.txt"),
            ("ipset-all.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/ipset-all.txt"),
            ("ipset-discord.txt", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/ipset-discord.txt")
        };

        public static async Task<DomainListUpdateResult> UpdateAllAsync(string enginePath, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var startedAt = DateTime.UtcNow;
            var listsDir = Path.Combine(enginePath, "lists");
            if (!Directory.Exists(listsDir))
            {
                Directory.CreateDirectory(listsDir);
            }

            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            var updated = 0;
            var totalDomains = 0;

            try
            {
                foreach (var (fileName, url) in ListFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Загружаю {fileName}…");

                    var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        AppLog.Warn($"[ListUpdater] Не удалось скачать {fileName}: HTTP {response.StatusCode}");
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var targetPath = Path.Combine(listsDir, fileName);

                    // Создание резервной копии перед перезаписью
                    if (File.Exists(targetPath))
                    {
                        var bakPath = Path.Combine(AppPaths.BackupDir, $"{fileName}.bak");
                        AppPaths.EnsureDir(AppPaths.BackupDir);
                        File.Copy(targetPath, bakPath, true);
                    }

                    await File.WriteAllTextAsync(targetPath, content, ct).ConfigureAwait(false);
                    updated++;

                    var linesCount = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    totalDomains += linesCount;
                }

                var elapsed = DateTime.UtcNow - startedAt;
                var msg = updated > 0
                    ? $"Списки успешно обновлены: загружено {updated} файлов, всего {totalDomains} записей за {elapsed.TotalSeconds:0.0} с."
                    : "Не удалось загрузить списки доменов с GitHub.";

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
