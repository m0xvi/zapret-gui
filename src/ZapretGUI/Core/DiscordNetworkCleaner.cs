using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class DiscordCacheDirectoryInfo
    {
        public string EditionName { get; init; } = "";
        public string Path { get; init; } = "";
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public string FormattedSize => FormatBytes(TotalBytes);

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} ГБ";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} КБ";
            return $"{bytes} Б";
        }
    }

    public sealed class DiscordCleanOptions
    {
        public bool CloseDiscordProcesses { get; set; } = true;
        public bool RestartDiscordAfterClean { get; set; } = false;
        public bool ResetNetworkStack { get; set; } = true;
        public bool DeepNetworkReset { get; set; } = false;
    }

    public sealed class DiscordCleanSummary
    {
        public bool Ok { get; init; } = true;
        public int TerminatedProcesses { get; init; }
        public int CleanedDirectories { get; init; }
        public int DeletedFiles { get; init; }
        public long FreedBytes { get; init; }
        public string FreedBytesText => DiscordCacheDirectoryInfo.FormatBytes(FreedBytes);
        public bool DnsFlushed { get; init; }
        public bool ArpCleared { get; init; }
        public bool NetBiosFlushed { get; init; }
        public bool TcpTimestampsEnsured { get; init; }
        public bool TcpAutoTuningEnsured { get; init; }
        public bool ProxyReset { get; init; }
        public bool DiscordRestarted { get; init; }
        public List<string> Steps { get; init; } = new();
        public string Message { get; init; } = "";
        public DateTime CleanedAt { get; init; } = DateTime.Now;
        public string CleanedAtText => CleanedAt.ToString("HH:mm:ss");
    }

    /// <summary>
    /// Комплексная очистка кэша всех редакций Discord и оптимизация сетевого стека Windows (DNS, ARP, NetBIOS, TCP Timestamps).
    /// Устраняет зависания «Подключение к RTC», ошибки маршрутизации WebRTC и сбои голосового соединения (Issues #10114, PR #16169, #15962).
    /// </summary>
    public static class DiscordNetworkCleaner
    {
        private static readonly string[] DiscordProcessNames = new[]
        {
            "Discord",
            "DiscordCanary",
            "DiscordPTB",
            "DiscordDevelopment",
            "DiscordHookHelper",
            "DiscordHookHelper64"
        };

        private static readonly string[] CacheFolderNames = new[]
        {
            "Cache",
            "Code Cache",
            "GPUCache",
            "DawnCache",
            "blob_storage",
            "Session Storage",
            "IndexedDB",
            "Network",
            "Crashpad"
        };

        /// <summary>
        /// Возвращает список папок с кэшем всех установленных редакций Discord и суммарный размер.
        /// </summary>
        public static (long TotalBytes, int TotalFiles, List<DiscordCacheDirectoryInfo> Editions) GetDetailedCacheStatus(string? customAppData = null)
        {
            var appData = customAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var editions = new List<DiscordCacheDirectoryInfo>();
            long totalBytes = 0;
            int totalFiles = 0;

            var targets = new (string Name, string Folder)[]
            {
                ("Discord Stable", "discord"),
                ("Discord Canary", "discordcanary"),
                ("Discord PTB", "discordptb"),
                ("Discord Development", "discorddevelopment")
            };

            foreach (var target in targets)
            {
                var dirPath = Path.Combine(appData, target.Folder);
                if (!Directory.Exists(dirPath)) continue;

                long editionBytes = 0;
                int editionFiles = 0;

                foreach (var cacheName in CacheFolderNames)
                {
                    var cacheDir = Path.Combine(dirPath, cacheName);
                    if (!Directory.Exists(cacheDir)) continue;

                    try
                    {
                        var files = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
                        editionFiles += files.Length;
                        foreach (var f in files)
                        {
                            try
                            {
                                editionBytes += new FileInfo(f).Length;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (editionFiles > 0 || Directory.Exists(dirPath))
                {
                    editions.Add(new DiscordCacheDirectoryInfo
                    {
                        EditionName = target.Name,
                        Path = dirPath,
                        TotalBytes = editionBytes,
                        FileCount = editionFiles
                    });
                    totalBytes += editionBytes;
                    totalFiles += editionFiles;
                }
            }

            return (totalBytes, totalFiles, editions);
        }

        /// <summary>
        /// Выполняет 1-клик очистку кэша Discord и сброс сетевого стека Windows.
        /// </summary>
        public static async Task<DiscordCleanSummary> CleanAsync(DiscordCleanOptions? options = null, IProgress<string>? progress = null)
        {
            options ??= new DiscordCleanOptions();
            var steps = new List<string>();
            int terminatedCount = 0;
            string? discordExePathToRestart = null;

            // 1. Остановка процессов Discord для снятия файловых блокировок
            if (options.CloseDiscordProcesses)
            {
                progress?.Report("Проверка и закрытие процессов Discord…");
                try
                {
                    foreach (var procName in DiscordProcessNames)
                    {
                        var procs = Process.GetProcessesByName(procName);
                        if (procs.Length > 0)
                        {
                            if (discordExePathToRestart == null)
                            {
                                try
                                {
                                    var mainModule = procs[0].MainModule;
                                    if (mainModule != null && File.Exists(mainModule.FileName))
                                    {
                                        discordExePathToRestart = mainModule.FileName;
                                    }
                                }
                                catch { }
                            }

                            foreach (var p in procs)
                            {
                                try
                                {
                                    p.Kill(true);
                                    p.WaitForExit(1500);
                                    terminatedCount++;
                                }
                                catch { }
                                finally { p.Dispose(); }
                            }
                        }
                    }

                    if (terminatedCount > 0)
                    {
                        steps.Add($"Закрыто {terminatedCount} процессов Discord для снятия блокировок файлов.");
                        // Небольшая пауза, чтобы Windows освободила файловые дескрипторы
                        await Task.Delay(300).ConfigureAwait(false);
                    }
                    else
                    {
                        steps.Add("Активных процессов Discord не обнаружено.");
                    }
                }
                catch (Exception ex)
                {
                    steps.Add($"Предупреждение при остановке Discord: {ex.Message}");
                }
            }

            // 2. Очистка кэша всех редакций Discord
            progress?.Report("Очистка папок кэша (RTC, Media, GPU Shader, Dawn, Storage)…");
            int deletedFiles = 0;
            int cleanedDirs = 0;
            long freedBytes = 0;

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var tempPath = Path.GetTempPath();

                var appDataFolders = new[] { "discord", "discordcanary", "discordptb", "discorddevelopment" };

                foreach (var folder in appDataFolders)
                {
                    var root = Path.Combine(appData, folder);
                    if (!Directory.Exists(root)) continue;

                    foreach (var cacheName in CacheFolderNames)
                    {
                        var target = Path.Combine(root, cacheName);
                        if (!Directory.Exists(target)) continue;

                        cleanedDirs++;
                        try
                        {
                            var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
                            foreach (var file in files)
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    var len = fi.Length;
                                    fi.Delete();
                                    deletedFiles++;
                                    freedBytes += len;
                                }
                                catch { }
                            }

                            // Удаляем пустые вложенные папки
                            try
                            {
                                foreach (var sub in Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                                {
                                    try { Directory.Delete(sub, false); } catch { }
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                }

                // Очистка WebRTC логов в LocalAppData
                foreach (var localFolder in new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" })
                {
                    var localRoot = Path.Combine(localAppData, localFolder);
                    if (!Directory.Exists(localRoot)) continue;

                    try
                    {
                        foreach (var logFile in Directory.GetFiles(localRoot, "*.log", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                var fi = new FileInfo(logFile);
                                var len = fi.Length;
                                fi.Delete();
                                deletedFiles++;
                                freedBytes += len;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                steps.Add($"Очищено {deletedFiles} файлов кэша ({cleanedDirs} каталогов), освобождено {DiscordCacheDirectoryInfo.FormatBytes(freedBytes)}.");
            }
            catch (Exception ex)
            {
                steps.Add($"Ошибка при очистке кэша: {ex.Message}");
            }

            // 3. Сброс сетевого стека Windows
            bool dnsFlushed = false;
            bool arpCleared = false;
            bool netBiosFlushed = false;
            bool tcpTimestampsEnsured = false;
            bool tcpAutoTuningEnsured = false;
            bool proxyReset = false;

            if (options.ResetNetworkStack)
            {
                progress?.Report("Сброс кэша DNS и ARP-таблицы маршрутизации…");

                // DNS
                try
                {
                    DnsManagementService.FlushDnsCache();
                    dnsFlushed = true;
                    steps.Add("DNS-кэш Windows успешно сброшен (ipconfig /flushdns).");
                }
                catch { }

                // ARP
                try
                {
                    var res = await Task.Run(() => Shell.RunCmdHidden("arp -d *")).ConfigureAwait(false);
                    if (!res.Ok)
                    {
                        await Task.Run(() => Shell.RunCmdHidden("netsh interface ip delete arpcache")).ConfigureAwait(false);
                    }
                    arpCleared = true;
                    steps.Add("ARP-кэш шлюзов очищен (удалены устаревшие MAC-привязки).");
                }
                catch { }

                // NetBIOS
                try
                {
                    await Task.Run(() => Shell.RunCmdHidden("nbtstat -R")).ConfigureAwait(false);
                    netBiosFlushed = true;
                    steps.Add("NetBIOS кэш имён обновлён (nbtstat -R).");
                }
                catch { }

                // WinHTTP Proxy
                try
                {
                    await Task.Run(() => Shell.Run("netsh.exe", new[] { "winhttp", "reset", "proxy" })).ConfigureAwait(false);
                    proxyReset = true;
                    steps.Add("Системный WinHTTP прокси сброшен в прямое подключение.");
                }
                catch { }

                // TCP Timestamps
                try
                {
                    WinServices.EnsureTcpTimestamps();
                    tcpTimestampsEnsured = true;
                    steps.Add("Метки времени TCP (Timestamps) включены для WinDivert.");
                }
                catch { }

                // TCP Auto-Tuning
                try
                {
                    await Task.Run(() => Shell.Run("netsh.exe", new[] { "interface", "tcp", "set", "global", "autotuninglevel=normal" })).ConfigureAwait(false);
                    tcpAutoTuningEnsured = true;
                    steps.Add("TCP Auto-Tuning установлен в режим normal.");
                }
                catch { }
            }

            // 4. Опциональный перезапуск Discord
            bool discordRestarted = false;
            if (options.RestartDiscordAfterClean && terminatedCount > 0)
            {
                progress?.Report("Перезапуск Discord…");
                try
                {
                    if (discordExePathToRestart != null && File.Exists(discordExePathToRestart))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = discordExePathToRestart,
                            UseShellExecute = true
                        });
                        discordRestarted = true;
                        steps.Add($"Discord перезапущен ({Path.GetFileName(discordExePathToRestart)}).");
                    }
                    else
                    {
                        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var defaultUpdate = Path.Combine(localAppData, "Discord", "Update.exe");
                        if (File.Exists(defaultUpdate))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = defaultUpdate,
                                Arguments = "--processStart Discord.exe",
                                UseShellExecute = true
                            });
                            discordRestarted = true;
                            steps.Add("Discord перезапущен через Update.exe.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    steps.Add($"Не удалось автоматически запустить Discord: {ex.Message}");
                }
            }

            var summary = new DiscordCleanSummary
            {
                Ok = true,
                TerminatedProcesses = terminatedCount,
                CleanedDirectories = cleanedDirs,
                DeletedFiles = deletedFiles,
                FreedBytes = freedBytes,
                DnsFlushed = dnsFlushed,
                ArpCleared = arpCleared,
                NetBiosFlushed = netBiosFlushed,
                TcpTimestampsEnsured = tcpTimestampsEnsured,
                TcpAutoTuningEnsured = tcpAutoTuningEnsured,
                ProxyReset = proxyReset,
                DiscordRestarted = discordRestarted,
                Steps = steps,
                Message = $"Очистка завершена: удалено {deletedFiles} файлов кэша ({DiscordCacheDirectoryInfo.FormatBytes(freedBytes)}), сброшен DNS/ARP, сетевой стек оптимизирован."
            };

            return summary;
        }

        /// <summary>
        /// Глубокий сброс всего сетевого стека Windows (Winsock, IP, Proxy, DNS, ARP).
        /// </summary>
        public static async Task<List<string>> DeepNetworkStackResetAsync(IProgress<string>? progress = null)
        {
            var report = new List<string>();
            var commands = new (string Cmd, string Desc)[]
            {
                ("netsh winsock reset", "Сброс каталога Winsock"),
                ("netsh int ip reset all", "Сброс стека TCP/IP"),
                ("netsh winhttp reset proxy", "Сброс системного WinHTTP прокси"),
                ("ipconfig /flushdns", "Сброс кэша DNS-клиента"),
                ("arp -d *", "Очистка ARP-таблицы"),
                ("nbtstat -R", "Очистка NetBIOS кэша"),
                ("netsh interface tcp set global timestamps=enabled", "Включение меток времени TCP Timestamps"),
                ("netsh interface tcp set global autotuninglevel=normal", "Восстановление TCP Auto-Tuning")
            };

            for (int i = 0; i < commands.Length; i++)
            {
                var (cmd, desc) = commands[i];
                progress?.Report($"Выполнение: {desc} ({i + 1}/{commands.Length})…");
                var result = await Task.Run(() => Shell.RunCmd(cmd)).ConfigureAwait(false);
                report.Add($"{desc} ({cmd}) → {(result.Ok ? "OK" : "Ошибка: " + result.StdErr)}");
            }

            report.Add("⚠️ Для полного вступления сетевых настроек в силу рекомендуется перезагрузить компьютер.");
            return report;
        }
    }
}
