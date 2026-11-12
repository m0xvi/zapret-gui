using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Голосовой узел, к которому Discord подключался в последний раз.</summary>
    public sealed class DiscordVoiceEndpointInfo
    {
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public string Source { get; init; } = "";
        public DateTime SeenAt { get; init; } = DateTime.Now;

        public string AddressText => Port > 0 ? $"{Host}:{Port}" : Host;
        public string SeenAtText => SeenAt.ToString("HH:mm:ss");
        public string Details => $"Последний узел: {AddressText} · {Source} · {SeenAtText}";
    }

    public sealed class DiscordVoiceResetResult
    {
        public bool Ok { get; init; }
        public bool DiscordClosed { get; init; }
        public bool DiscordRestarted { get; init; }
        public bool CacheCleared { get; init; }
        public bool DnsFlushed { get; init; }
        public string Message { get; init; } = "";
        public List<string> Steps { get; init; } = new();
    }

    /// <summary>
    /// Сброс залипшей голосовой сессии Discord.
    ///
    /// Почему это нужно: Discord подключает голос к конкретному edge-узлу на конкретном UDP-порту
    /// и запоминает эту пару на всё время сессии. Если до пары «IP:порт» нет маршрута
    /// (WSAENETUNREACH — «Не установлен маршрут»), переподключение в тот же канал возвращает
    /// клиента на тот же мёртвый узел — помогает только новая сессия (новый канал, смена региона)
    /// либо полный перезапуск клиента (v2.2.1).
    /// </summary>
    public static class DiscordVoiceSession
    {
        private static readonly string[] AppDataRoots = { "discord", "discordcanary", "discordptb", "discorddevelopment" };

        private static readonly string[] DiscordProcessNames =
        {
            "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment"
        };

        /// <summary>Кэш, который влияет на RTC/сеть. Сессии и токены (IndexedDB, Local Storage) не трогаем —
        /// иначе Discord попросит войти заново.</summary>
        private static readonly string[] VoiceCacheFolders = { "Cache", "Code Cache", "GPUCache", "DawnCache" };

        private static readonly Regex EndpointRegex = new(
            @"([a-z0-9][a-z0-9\-]*\.discord\.media)(?::(\d{2,5}))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Достаёт из текста (лог, leveldb) первый голосовой узел Discord.</summary>
        public static bool TryExtractEndpoint(string? text, out string host, out int port)
        {
            host = "";
            port = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var match = EndpointRegex.Match(text);
            if (!match.Success) return false;

            host = match.Groups[1].Value.ToLowerInvariant();
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var parsed) &&
                parsed is > 0 and < 65536)
                port = parsed;
            return host.Length > 0;
        }

        /// <summary>
        /// Ищет последний голосовой узел: сначала в свежих логах Discord, затем в Local Storage (leveldb).
        /// Только чтение — ничего не меняет на диске.
        /// </summary>
        public static DiscordVoiceEndpointInfo? FindLastEndpoint(string? appDataOverride = null)
        {
            var appData = appDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new List<DiscordVoiceEndpointInfo>();

            foreach (var root in AppDataRoots)
            {
                var baseDirectory = Path.Combine(appData, root);
                if (!Directory.Exists(baseDirectory)) continue;

                candidates.AddRange(ScanLogs(Path.Combine(baseDirectory, "logs")));
                candidates.AddRange(ScanLevelDb(Path.Combine(baseDirectory, "Local Storage", "leveldb")));
            }

            return candidates.OrderByDescending(c => c.SeenAt).FirstOrDefault();
        }

        private static IEnumerable<DiscordVoiceEndpointInfo> ScanLogs(string logsDirectory)
        {
            var results = new List<DiscordVoiceEndpointInfo>();
            if (!Directory.Exists(logsDirectory)) return results;

            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(logsDirectory)
                    .GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(6)
                    .ToList();
            }
            catch { return results; }

            foreach (var file in files)
            {
                var text = ReadTail(file.FullName, 4 * 1024 * 1024);
                if (TryExtractEndpoint(text, out var host, out var port))
                    results.Add(new DiscordVoiceEndpointInfo
                    {
                        Host = host,
                        Port = port,
                        Source = "журнал Discord",
                        SeenAt = file.LastWriteTime
                    });
            }
            return results;
        }

        private static IEnumerable<DiscordVoiceEndpointInfo> ScanLevelDb(string levelDbDirectory)
        {
            var results = new List<DiscordVoiceEndpointInfo>();
            if (!Directory.Exists(levelDbDirectory)) return results;

            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(levelDbDirectory)
                    .GetFiles("*")
                    .Where(f => f.Extension is ".ldb" or ".log" || f.Name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(6)
                    .ToList();
            }
            catch { return results; }

            foreach (var file in files)
            {
                var text = ReadTail(file.FullName, 4 * 1024 * 1024);
                if (TryExtractEndpoint(text, out var host, out var port))
                    results.Add(new DiscordVoiceEndpointInfo
                    {
                        Host = host,
                        Port = port,
                        Source = "кэш Local Storage",
                        SeenAt = file.LastWriteTime
                    });
            }
            return results;
        }

        /// <summary>Читает хвост файла: leveldb и логи бывают большими, а нужные строки — в конце.</summary>
        private static string ReadTail(string path, int maxBytes)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = stream.Length;
                var start = Math.Max(0, length - maxBytes);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[length - start];
                var read = stream.Read(buffer, 0, buffer.Length);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Полный сброс голосовой сессии: закрыть Discord, снести RTC/сетевой кэш, сбросить DNS
        /// и запустить клиент заново. Сессии и настройки не затрагиваются.
        /// </summary>
        public static async Task<DiscordVoiceResetResult> ResetAsync(
            IProgress<string>? progress = null, bool restartDiscord = true)
        {
            var steps = new List<string>();
            var closed = false;
            var restarted = false;
            var cacheCleared = false;
            var dnsFlushed = false;
            string? discordExe = null;

            progress?.Report("Закрываю Discord…");
            try
            {
                foreach (var name in DiscordProcessNames)
                {
                    Process[] processes;
                    try { processes = Process.GetProcessesByName(name); }
                    catch { continue; }

                    foreach (var process in processes)
                    {
                        try
                        {
                            if (discordExe == null)
                            {
                                var path = process.MainModule?.FileName;
                                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) discordExe = path;
                            }
                            process.Kill(true);
                            process.WaitForExit(1500);
                            closed = true;
                        }
                        catch (Exception ex)
                        {
                            steps.Add($"Не удалось закрыть {name}: {ex.Message}");
                        }
                        finally { process.Dispose(); }
                    }
                }
                steps.Add(closed ? "Discord закрыт — сокеты и голосовая сессия освобождены." : "Discord не был запущен.");
                if (closed) await Task.Delay(400).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                steps.Add("Ошибка при закрытии Discord: " + ex.Message);
            }

            progress?.Report("Чищу RTC-кэш…");
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                foreach (var root in AppDataRoots)
                {
                    var baseDirectory = Path.Combine(appData, root);
                    if (!Directory.Exists(baseDirectory)) continue;
                    foreach (var folder in VoiceCacheFolders)
                    {
                        var target = Path.Combine(baseDirectory, folder);
                        if (!Directory.Exists(target)) continue;
                        try
                        {
                            Directory.Delete(target, true);
                            cacheCleared = true;
                        }
                        catch (Exception ex)
                        {
                            steps.Add($"Кэш {folder} занят: {ex.Message}");
                        }
                    }
                }
                if (cacheCleared) steps.Add("Кэш RTC/шейдеров удалён — клиент соберёт WebRTC-пайплайн заново.");
            }
            catch (Exception ex)
            {
                steps.Add("Ошибка очистки кэша: " + ex.Message);
            }

            progress?.Report("Сбрасываю DNS-кэш…");
            try
            {
                DnsManagementService.FlushDnsCache();
                dnsFlushed = true;
                steps.Add("DNS-кэш сброшен (ipconfig /flushdns).");
            }
            catch (Exception ex)
            {
                steps.Add("Не удалось сбросить DNS: " + ex.Message);
            }

            if (restartDiscord)
            {
                progress?.Report("Запускаю Discord…");
                restarted = StartDiscord(discordExe, steps);
            }

            var message = restarted
                ? "Голосовая сессия Discord сброшена. Войди в канал заново — клиент выберет новый узел."
                : closed
                    ? "Discord закрыт, кэш и DNS сброшены. Запусти Discord вручную и войди в канал."
                    : "Кэш и DNS сброшены. Discord не был запущен — можно запускать.";

            return new DiscordVoiceResetResult
            {
                Ok = cacheCleared || dnsFlushed || closed,
                DiscordClosed = closed,
                DiscordRestarted = restarted,
                CacheCleared = cacheCleared,
                DnsFlushed = dnsFlushed,
                Message = message,
                Steps = steps
            };
        }

        private static bool StartDiscord(string? executable, List<string> steps)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                {
                    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                    steps.Add($"Discord запущен ({Path.GetFileName(executable)}).");
                    return true;
                }

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var folder in new[] { "Discord", "DiscordCanary", "DiscordPTB" })
                {
                    var update = Path.Combine(localAppData, folder, "Update.exe");
                    if (!File.Exists(update)) continue;
                    Process.Start(new ProcessStartInfo(update)
                    {
                        Arguments = $"--processStart {folder}.exe",
                        UseShellExecute = true
                    });
                    steps.Add($"Discord запущен через {folder}\\Update.exe.");
                    return true;
                }
                steps.Add("Не нашёл Discord.exe — запусти клиент вручную.");
            }
            catch (Exception ex)
            {
                steps.Add("Не удалось запустить Discord: " + ex.Message);
            }
            return false;
        }
    }
}
