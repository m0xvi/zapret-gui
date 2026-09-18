using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Сохранённая проверка диагностики и её результаты.</summary>
    public sealed class DiagnosticsSnapshot
    {
        public DateTime CreatedAt { get; init; }
        public string Summary { get; init; } = "";
        public List<DiagnosticItem> Items { get; init; } = new();
    }

    /// <summary>Одна проверка TCP 16–20 КБ на узле из набора DPI-проверки.</summary>
    public sealed class DpiProbeResult
    {
        public string TestName { get; init; } = "";
        public string ProbeKind { get; init; } = "";
        public string Route { get; init; } = "Прямое соединение";
        public int? HttpStatusCode { get; init; }
        public long UploadedBytes { get; init; }
        public long DownloadedBytes { get; init; }
        public long Milliseconds { get; init; }
        public bool TimedOut { get; init; }
        public bool PossibleDpiFreeze { get; init; }
        public bool PossibleDnsSpoof { get; init; }
        public string Status { get; init; } = "";
        public string Details { get; init; } = "";
        public int Attempt { get; init; } = 1;
        public string StatusKey => PossibleDpiFreeze || PossibleDnsSpoof ? "Warning" : Status == "ОТВЕТ" ? "Success" : "Danger";
    }

    /// <summary>Результаты DPI-проверки одного CDN/серверного узла.</summary>
    public sealed class DpiTargetResult
    {
        public string Id { get; init; } = "";
        public string Provider { get; init; } = "";
        public string Country { get; init; } = "";
        public string Host { get; init; } = "";
        public bool IsControlEndpoint { get; init; }
        public int AttemptCount { get; init; } = 1;
        public List<DpiProbeResult> Probes { get; init; } = new();

        public bool HasPossibleFreeze => Probes.Any(p => p.PossibleDpiFreeze);
        public bool HasSuspiciousProbe => Probes.Any(p => p.PossibleDpiFreeze || p.PossibleDnsSpoof);
        public string StatusKey => HasSuspiciousProbe ? "Warning" : "Success";
        public string DisplayName => $"{Country} {Provider} · {Host}";
    }

    public sealed partial class NetworkObservationSnapshot
    {
        public static NetworkObservationSnapshot From(
            IReadOnlyList<DpiTargetResult> results, ResourceDiagnosisResult? comparison,
            DateTime? createdAt = null, DpiTargetResult? controlResult = null)
        {
            var allResults = controlResult == null
                ? results
                : results.Concat(new[] { controlResult }).ToList();
            var probes = allResults.SelectMany(result => result.Probes).ToList();
            var dnsError = probes.Any(probe => probe.ProbeKind == "DNS" &&
                !probe.Status.Equals("ОТВЕТ", StringComparison.OrdinalIgnoreCase));
            var tcpTimeout = probes.Any(probe => probe.ProbeKind == "TCP" && probe.TimedOut);
            var tlsError = probes.Any(probe => probe.ProbeKind == "HTTPS" &&
                probe.TestName.Contains("TLS", StringComparison.OrdinalIgnoreCase) &&
                probe.Status.Equals("ОШИБКА", StringComparison.OrdinalIgnoreCase));
            var httpError = probes.Any(probe => probe.ProbeKind == "HTTPS" && probe.HttpStatusCode is >= 400);
            var freeze = probes.Any(probe => probe.PossibleDpiFreeze);
            var latency = probes.Where(probe => probe.Milliseconds > 0)
                .Select(probe => probe.Milliseconds)
                .DefaultIfEmpty()
                .Average();
            var improves = comparison?.Kind == ResourceDiagnosisKind.BypassHelps;
            var signs = new List<string>();
            if (dnsError) signs.Add("ошибка DNS");
            if (tcpTimeout) signs.Add("тайм-аут TCP");
            if (tlsError) signs.Add("ошибка TLS");
            if (httpError) signs.Add("ошибка HTTP");
            if (freeze) signs.Add("возможное зависание DPI");
            if (improves) signs.Add("обход улучшает результат");

            return new NetworkObservationSnapshot
            {
                CreatedAt = createdAt ?? DateTime.Now,
                HasDnsError = dnsError,
                HasTcpTimeout = tcpTimeout,
                HasTlsError = tlsError,
                HasHttpError = httpError,
                HasDpiFreeze = freeze,
                BypassImprovesResult = improves,
                AverageLatencyMs = (long)latency,
                Summary = signs.Count == 0
                    ? "Нормализованные признаки ошибок не обнаружены"
                    : string.Join(" · ", signs)
            };
        }
    }

    /// <summary>Итог работы набора DPI-проверок.</summary>
    public sealed class DpiCheckSnapshot
    {
        public DateTime CreatedAt { get; init; }
        public int TargetsTotal { get; init; }
        public int TargetsTested { get; init; }
        public string Summary { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public string SuiteSource { get; init; } = "";
        public DateTime? SuiteLoadedAt { get; init; }
        public List<DpiTargetResult> Results { get; init; } = new();
        public DpiTargetResult? ControlResult { get; init; }
        public NetworkObservationSnapshot Observation { get; set; } = new();
        public ResourceDiagnosisResult? BypassComparison { get; set; }
    }

    internal sealed class DiagnosticsHistoryFile
    {
        public List<DiagnosticsSnapshot> Diagnostics { get; set; } = new();
        public List<DpiCheckSnapshot> DpiChecks { get; set; } = new();
    }

    /// <summary>Небольшой локальный журнал последних результатов диагностики.</summary>
    public static class DiagnosticsHistoryStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static string FilePath => Path.Combine(AppPaths.AppData, "diagnostics-history.json");

        public static DiagnosticsSnapshot? LoadLastDiagnostics()
            => Load().Diagnostics.LastOrDefault();

        public static DpiCheckSnapshot? LoadLastDpiCheck()
            => Load().DpiChecks.LastOrDefault();

        public static void SaveDiagnostics(IReadOnlyList<DiagnosticItem> items, string summary)
        {
            var history = Load();
            history.Diagnostics.Add(new DiagnosticsSnapshot
            {
                CreatedAt = DateTime.Now,
                Summary = summary,
                Items = items.ToList()
            });
            Trim(history.Diagnostics);
            Save(history);
        }

        public static void SaveDpiCheck(DpiCheckSnapshot snapshot)
        {
            var history = Load();
            history.DpiChecks.Add(snapshot);
            Trim(history.DpiChecks);
            Save(history);
        }

        private static DiagnosticsHistoryFile Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new DiagnosticsHistoryFile();
                return JsonSerializer.Deserialize<DiagnosticsHistoryFile>(File.ReadAllText(FilePath), Options)
                    ?? new DiagnosticsHistoryFile();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать историю диагностики: " + ex.Message);
                return new DiagnosticsHistoryFile();
            }
        }

        private static void Save(DiagnosticsHistoryFile history)
        {
            try
            {
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(history, Options));
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить историю диагностики: " + ex.Message);
            }
        }

        private static void Trim<T>(List<T> list)
        {
            const int max = 10;
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
        }
    }

    /// <summary>
    /// Упрощённый аналог DPI checkers из test zapret.ps1: отправляет случайный буфер
    /// через HTTP/1.1 и TLS 1.2/1.3 и ищет тайм-аут после передачи большого запроса.
    /// Результат говорит о возможном паттерне DPI, но не называет интернет-провайдера.
    /// </summary>
    public static class DpiCheckerService
    {
        private const string SuiteUrl = "https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json";
        private const int TimeoutSeconds = 5;
        private const int PayloadBytes = 65536;
        private const int MaxTargets = 12;
        private const int MaxSuiteTargets = 34;
        // Flowseal запускает DPI targets параллельно пулом 8–16 workers. Это сохраняет
        // длительность и нагрузку оригинального теста, но не превращает результаты
        // разных endpoint-ов в последовательный «быстрый smoke test».
        private static int MaxParallel => Math.Clamp(Environment.ProcessorCount * 2, 8, 16);
        private const int MaxAttempts = 2;
        private const string ControlEndpointHost = "example.com";

        private sealed class SuiteEntry
        {
            public string Id { get; set; } = "";
            public string Provider { get; set; } = "";
            public string Country { get; set; } = "";
            public string Host { get; set; } = "";
        }

        private sealed class LoadedSuite
        {
            public List<SuiteEntry> Entries { get; init; } = new();
            public string Source { get; init; } = "";
            public DateTime LoadedAt { get; init; }
        }

        public static async Task<DpiCheckSnapshot> RunAsync(string? customHost = null,
            IProgress<string>? progress = null, CancellationToken ct = default, int? maxTargets = null)
        {
            var created = DateTime.Now;
            List<SuiteEntry> suite;
            var suiteSource = "пользовательский узел";
            DateTime? suiteLoadedAt = null;
            try
            {
                if (string.IsNullOrWhiteSpace(customHost))
                {
                    var loaded = await LoadSuiteAsync(ct).ConfigureAwait(false);
                    suite = loaded.Entries;
                    suiteSource = loaded.Source;
                    suiteLoadedAt = loaded.LoadedAt;
                }
                else
                {
                    suite = new List<SuiteEntry>
                    {
                        new() { Id = "CUSTOM", Provider = "Пользовательский узел", Country = "", Host = customHost.Trim() }
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DpiCheckSnapshot
                {
                    CreatedAt = created,
                    Summary = "Набор DPI-проверки недоступен",
                    ErrorMessage = ex.Message,
                    SuiteSource = "ошибка загрузки"
                };
            }

            var targetLimit = Math.Clamp(maxTargets ?? MaxTargets, 1, MaxSuiteTargets);
            var selected = suite.Take(targetLimit).ToList();
            var progressTotal = selected.Count + 1; // последний шаг — контрольный endpoint
            progress?.Report($"DPI_TOTAL:{progressTotal}");
            var payload = new byte[PayloadBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
            var results = new DpiTargetResult[selected.Count];
            using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);
            var tasks = selected.Select(async (entry, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    progress?.Report($"Проверяю DPI: {index + 1} из {selected.Count} — {entry.Provider}");
                    results[index] = await CheckTargetWithRetriesAsync(entry, false, progress, ct, payload).ConfigureAwait(false);
                    progress?.Report($"DPI_PROGRESS:{index + 1}/{progressTotal} — завершён {entry.Provider}");
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            var complete = results.Where(r => r != null).ToList()!;

            progress?.Report("Проверяю контрольный endpoint — example.com");
            var control = await CheckTargetWithRetriesAsync(
                new SuiteEntry
                {
                    Id = "CONTROL",
                    Provider = "Контрольный endpoint",
                    Country = "",
                    Host = ControlEndpointHost
                }, true, progress, ct, payload).ConfigureAwait(false);
            progress?.Report($"DPI_PROGRESS:{progressTotal}/{progressTotal} — контрольный endpoint завершён");
            var allResults = complete.Concat(new[] { control }).ToList();
            var freezes = allResults.Sum(r => r.Probes.Count(p => p.PossibleDpiFreeze && p.ProbeKind == "HTTPS"));
            var spoofed = allResults.Sum(r => r.Probes.Count(p => p.PossibleDnsSpoof));
            var blocked = allResults.Sum(r => r.Probes.Count(p => p.PossibleDpiFreeze || p.PossibleDnsSpoof));
            var summary = freezes > 0
                ? $"Возможный паттерн DPI 16–20 КБ найден в {freezes} HTTPS-проверках; всего подозрительных проб: {blocked}"
                : spoofed > 0
                    ? $"DNS вернул подозрительные локальные адреса в {spoofed} пробах; возможна подмена, нужна проверка другим DNS"
                    : blocked > 0
                        ? $"Есть признаки сетевой блокировки или тайм-аутов: подозрительных проб {blocked}"
                        : "Паттерн DPI 16–20 КБ, подмена DNS и тайм-ауты не обнаружены на проверенных узлах";
            return new DpiCheckSnapshot
            {
                CreatedAt = created,
                TargetsTotal = suite.Count,
                TargetsTested = complete.Count,
                Results = complete,
                ControlResult = control,
                SuiteSource = suiteSource,
                SuiteLoadedAt = suiteLoadedAt,
                Summary = summary
            };
        }

        private static async Task<LoadedSuite> LoadSuiteAsync(CancellationToken ct)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
                using var response = await client.GetAsync(SuiteUrl, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var entries = JsonSerializer.Deserialize<List<SuiteEntry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<SuiteEntry>();
                ValidateSuite(entries);
                SaveSuiteCache(entries);
                return new LoadedSuite
                {
                    Entries = entries,
                    Source = "внешний suite.v2.json",
                    LoadedAt = DateTime.Now
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception networkError)
            {
                var cached = LoadSuiteCache();
                if (cached.Entries.Count > 0)
                {
                    AppLog.Warn("Внешний DPI-suite недоступен, использую локальный кэш: " + networkError.Message);
                    return cached;
                }
                throw new InvalidOperationException(
                    "Не удалось загрузить DPI-suite и локальный кэш отсутствует: " + networkError.Message,
                    networkError);
            }
        }

        private static void ValidateSuite(List<SuiteEntry> entries)
        {
            entries.RemoveAll(entry => string.IsNullOrWhiteSpace(entry.Host));
            if (entries.Count == 0)
                throw new InvalidOperationException("В DPI-suite нет корректных endpoint-ов");
        }

        private static void SaveSuiteCache(List<SuiteEntry> entries)
        {
            try
            {
                var temporary = AppPaths.DpiSuiteCacheFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
                if (File.Exists(AppPaths.DpiSuiteCacheFile))
                    File.Replace(temporary, AppPaths.DpiSuiteCacheFile, null);
                else
                    File.Move(temporary, AppPaths.DpiSuiteCacheFile);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить кэш DPI-suite: " + ex.Message);
            }
        }

        private static LoadedSuite LoadSuiteCache()
        {
            try
            {
                if (!File.Exists(AppPaths.DpiSuiteCacheFile)) return new LoadedSuite();
                var entries = JsonSerializer.Deserialize<List<SuiteEntry>>(
                    File.ReadAllText(AppPaths.DpiSuiteCacheFile)) ?? new List<SuiteEntry>();
                ValidateSuite(entries);
                return new LoadedSuite
                {
                    Entries = entries,
                    Source = "локальный кэш suite.v2.json",
                    LoadedAt = File.GetLastWriteTime(AppPaths.DpiSuiteCacheFile)
                };
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать кэш DPI-suite: " + ex.Message);
                return new LoadedSuite();
            }
        }

        private static async Task<DpiTargetResult> CheckTargetWithRetriesAsync(
            SuiteEntry entry, bool isControl, IProgress<string>? progress, CancellationToken ct,
            byte[] payload)
        {
            DpiTargetResult? last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1)
                    progress?.Report($"Повторная проба {attempt} из {MaxAttempts}: {entry.Host}");
                var result = await CheckTargetAsync(entry, payload, ct).ConfigureAwait(false);
                last = new DpiTargetResult
                {
                    Id = result.Id,
                    Provider = result.Provider,
                    Country = result.Country,
                    Host = result.Host,
                    IsControlEndpoint = isControl,
                    AttemptCount = attempt,
                    Probes = result.Probes
                };
                if (!NeedsRetry(result)) break;
            }
            return last ?? new DpiTargetResult
            {
                Id = entry.Id,
                Provider = entry.Provider,
                Country = entry.Country,
                Host = entry.Host,
                IsControlEndpoint = isControl
            };
        }

        private static bool NeedsRetry(DpiTargetResult result)
            => result.Probes.Any(probe => probe.Status != "ОТВЕТ" || probe.TimedOut);

        private static async Task<DpiTargetResult> CheckTargetAsync(SuiteEntry entry,
            byte[] payload, CancellationToken ct)
        {
            var dns = await ProbeDnsAsync(entry.Host, ct).ConfigureAwait(false);
            var tcp = await ProbeTcpAsync(entry.Host, ct).ConfigureAwait(false);
            var transportReady = tcp.Status.Equals("ОТВЕТ", StringComparison.OrdinalIgnoreCase);
            var probes = new List<DpiProbeResult>
            {
                dns,
                tcp,
                // Аналог HTTP-теста из test zapret.ps1: HTTP/1.1 с обычным согласованием TLS.
                await ProbeHttpsAsync(entry.Host, "HTTP/1.1", payload, SslProtocols.None, transportReady, ct).ConfigureAwait(false),
                await ProbeHttpsAsync(entry.Host, "TLS 1.2", payload, SslProtocols.Tls12, transportReady, ct).ConfigureAwait(false),
                await ProbeHttpsAsync(entry.Host, "TLS 1.3", payload, SslProtocols.Tls13, transportReady, ct).ConfigureAwait(false)
            };
            return new DpiTargetResult
            {
                Id = entry.Id,
                Provider = entry.Provider,
                Country = entry.Country,
                Host = entry.Host,
                Probes = probes
            };
        }

        private static async Task<DpiProbeResult> ProbeDnsAsync(string host, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            try
            {
                var lookup = Dns.GetHostAddressesAsync(host);
                var completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), ct))
                    .ConfigureAwait(false);
                if (completed != lookup)
                {
                    ct.ThrowIfCancellationRequested();
                    return ProbeFailure("DNS", "Тайм-аут DNS", started, possibleBlock: false);
                }

                var addresses = await lookup.ConfigureAwait(false);
                var suspicious = addresses.Any(IsSuspiciousAddress);
                return new DpiProbeResult
                {
                    TestName = "DNS",
                    ProbeKind = "DNS",
                    Milliseconds = Elapsed(started),
                    PossibleDnsSpoof = suspicious,
                    Status = suspicious ? "ВОЗМОЖНА ПОДМЕНА DNS" : addresses.Length > 0 ? "ОТВЕТ" : "ОШИБКА",
                    Details = addresses.Length == 0
                        ? "DNS не вернул адреса"
                        : suspicious
                            ? $"получены локальные/приватные адреса ({addresses.Length}); возможна подмена DNS"
                            : $"получено адресов: {addresses.Length}"
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ProbeFailure("DNS", "Тайм-аут DNS", started, possibleBlock: false);
            }
            catch (Exception ex)
            {
                return ProbeFailure("DNS", "Ошибка DNS: " + Short(ex.Message), started, possibleBlock: false);
            }
        }

        private static async Task<DpiProbeResult> ProbeTcpAsync(string host, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var tcp = new TcpClient();
                var connect = tcp.ConnectAsync(host, 443);
                var completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), ct))
                    .ConfigureAwait(false);
                if (completed != connect)
                {
                    ct.ThrowIfCancellationRequested();
                    return ProbeFailure("TCP", "Тайм-аут TCP-подключения к порту 443", started, possibleBlock: false);
                }

                await connect.ConfigureAwait(false);
                return new DpiProbeResult
                {
                    TestName = "TCP 443",
                    ProbeKind = "TCP",
                    Milliseconds = Elapsed(started),
                    Status = "ОТВЕТ",
                    Details = "TCP-подключение установлено"
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ProbeFailure("TCP 443", "Тайм-аут TCP-подключения к порту 443", started, possibleBlock: false);
            }
            catch (Exception ex)
            {
                return ProbeFailure("TCP 443", "Ошибка TCP: " + Short(ex.Message), started, possibleBlock: false);
            }
        }

        private static async Task<DpiProbeResult> ProbeHttpsAsync(string host, string testName,
            byte[] payload, SslProtocols protocols, bool transportReady, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var handler = new HttpClientHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    SslProtocols = protocols
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + host)
                {
                    Version = new Version(1, 1),
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new ByteArrayContent(payload)
                };
                request.Headers.Range = new RangeHeaderValue(0, PayloadBytes - 1);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var response = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return new DpiProbeResult
                {
                    TestName = testName,
                    ProbeKind = "HTTPS",
                    HttpStatusCode = (int)response.StatusCode,
                    UploadedBytes = payload.Length,
                    DownloadedBytes = body.LongLength,
                    Milliseconds = Elapsed(started),
                    Status = "ОТВЕТ",
                    Details = $"HTTP {(int)response.StatusCode}, получено {body.LongLength} байт"
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ProbeFailure(testName, "Тайм-аут HTTPS после успешного TCP", started,
                    possibleBlock: transportReady, uploadedBytes: transportReady ? payload.Length : 0);
            }
            catch (Exception ex)
            {
                return ProbeFailure(testName, "Ошибка HTTPS: " + Short(ex.Message), started, possibleBlock: false,
                    uploadedBytes: payload.Length);
            }
        }

        private static DpiProbeResult ProbeFailure(string name, string details, DateTime started,
            bool possibleBlock, long uploadedBytes = 0)
            => new()
            {
                TestName = name,
                ProbeKind = name.StartsWith("DNS", StringComparison.Ordinal) ? "DNS" : name.StartsWith("TCP", StringComparison.Ordinal) ? "TCP" : "HTTPS",
                UploadedBytes = uploadedBytes,
                Milliseconds = Elapsed(started),
                TimedOut = details.Contains("Тайм-аут", StringComparison.OrdinalIgnoreCase),
                PossibleDpiFreeze = possibleBlock,
                Status = possibleBlock ? "ВОЗМОЖНА БЛОКИРОВКА" : "ОШИБКА",
                Details = details
            };

        private static long Elapsed(DateTime started)
            => (long)(DateTime.UtcNow - started).TotalMilliseconds;

        private static bool IsSuspiciousAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
                return true;
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc) return true;
            return bytes.Length == 4 && (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
        }

        private static string Short(string text)
            => text.Length > 180 ? text.Substring(0, 180) : text;
    }
}
