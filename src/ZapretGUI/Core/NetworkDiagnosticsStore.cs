using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretGui.Core
{
    /// <summary>
    /// Минимальная запись диагностики: проверка выбранного ресурса или изменение сокета выбранного
    /// процесса. Содержимое сетевых пакетов и URL-пути не записываются.
    /// </summary>
    public sealed class NetworkObservationRecord
    {
        public DateTime TimeUtc { get; init; } = DateTime.UtcNow;
        public string EventType { get; init; } = "";
        public string Source { get; init; } = "";
        public string TargetName { get; init; } = "";
        public string Host { get; init; } = "";
        public string ProcessName { get; init; } = "";
        public int? ProcessId { get; init; }
        public string Protocol { get; init; } = "";
        public string LocalEndpoint { get; init; } = "";
        public string RemoteEndpoint { get; init; } = "";
        public string ConnectionState { get; init; } = "";
        public long? BytesReceived { get; init; }
        public long? BytesSent { get; init; }
        public int? SampleMilliseconds { get; init; }
        public int? ActiveConnectionCount { get; init; }
        public bool? Ok { get; init; }
        public ResourceResultKind? FailureKind { get; init; }
        public long? Milliseconds { get; init; }
        public int? HttpStatusCode { get; init; }
        public bool? DnsOk { get; init; }
        public bool? TcpOk { get; init; }
        public bool BypassRunning { get; init; }
        public string StrategyName { get; init; } = "";
        public string ServiceState { get; init; } = "";
        public string Details { get; init; } = "";

        public static string CombineDetails(params string?[] parts)
            => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        private static string FormatBytes(long bytes)
            => bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.##} MB" :
                bytes >= 1_000 ? $"{bytes / 1_000.0:0.##} kB" : $"{bytes} B";

        private static string FormatRate(double bytesPerSecond)
            => bytesPerSecond >= 1_000_000 ? $"{bytesPerSecond / 1_000_000.0:0.##} MB/s" :
                bytesPerSecond >= 1_000 ? $"{bytesPerSecond / 1_000.0:0.##} kB/s" : $"{bytesPerSecond:0} B/s";

        public string LineText
        {
            get
            {
                var time = TimeUtc.ToLocalTime().ToString("HH:mm:ss");
                if (EventType == "Проверка")
                {
                    var result = Ok != true ? "ОШИБКА" : HttpStatusCode switch
                    {
                        >= 400 => $"сеть отвечает, HTTP {HttpStatusCode} (содержимое не подтверждено)",
                        >= 300 => $"перенаправление HTTP {HttpStatusCode}",
                        _ => "доступен"
                    };
                    var latency = Milliseconds is long ms ? $" · {ms} мс" : "";
                    var status = HttpStatusCode is int http ? $" · HTTP {http}" : "";
                    var route = BypassRunning ? "обход" + (string.IsNullOrWhiteSpace(StrategyName) ? " включён" : $" · {StrategyName}") : "напрямую";
                    var source = string.IsNullOrWhiteSpace(Source) ? "" : " · " + Source;
                    return $"{time} · {TargetName} ({Host}) · {result} · {route}{source}{latency}{status} · {Details}";
                }
                if (EventType == "Сокет")
                {
                    var remote = string.IsNullOrWhiteSpace(RemoteEndpoint) || RemoteEndpoint == "—" ? "" : $" → {RemoteEndpoint}";
                    var process = string.IsNullOrWhiteSpace(ProcessName) ? "процесс" : ProcessName + ".exe";
                    var route = BypassRunning ? "обход включён" : "напрямую";
                    var state = string.IsNullOrWhiteSpace(ConnectionState) ? "" : $" (последнее состояние: {ConnectionState})";
                    var eventText = string.IsNullOrWhiteSpace(Details) ? "событие" : Details;
                    return $"{time} · {process} · {Protocol} · {eventText}{state} · {LocalEndpoint}{remote} · {route}";
                }
                if (EventType is "Трафик" or "Сводка трафика")
                {
                    var process = string.IsNullOrWhiteSpace(ProcessName) ? "процесс" : ProcessName + ".exe";
                    var endpoint = string.IsNullOrWhiteSpace(RemoteEndpoint) ? "" : $" → {RemoteEndpoint}";
                    var span = SampleMilliseconds is int ms && ms > 0 ? ms / 1000.0 : 0;
                    var received = BytesReceived ?? 0;
                    var sent = BytesSent ?? 0;
                    var rxRate = span > 0 ? $" ({FormatRate(received / span)})" : "";
                    var txRate = span > 0 ? $" ({FormatRate(sent / span)})" : "";
                    var active = ActiveConnectionCount is int count ? $" · открытых сокетов: {count}" : "";
                    var route = BypassRunning ? " · обход" + (string.IsNullOrWhiteSpace(StrategyName) ? " включён" : $" · {StrategyName}") : " · напрямую";
                    return $"{time} · {process} · {Protocol}{endpoint} · ↓ {FormatBytes(received)}{rxRate} / ↑ {FormatBytes(sent)}{txRate}{active}{route} · {Details}";
                }
                return $"{time} · {Details}";
            }
        }
    }

    /// <summary>Локальный кольцевой журнал сетевой диагностики: хранит только последние 24 часа.</summary>
    public static class NetworkDiagnosticsStore
    {
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
        private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
        private const int MaxRecords = 20000;
        private static List<NetworkObservationRecord>? _records;

        public static int CountRecent
        {
            get
            {
                lock (Gate)
                {
                    EnsureLoaded();
                    var count = _records!.Count;
                    Prune(DateTime.UtcNow);
                    if (_records.Count != count) Rewrite();
                    return _records.Count;
                }
            }
        }

        public static IReadOnlyList<NetworkObservationRecord> GetRecent()
        {
            lock (Gate)
            {
                EnsureLoaded();
                var count = _records!.Count;
                Prune(DateTime.UtcNow);
                if (_records.Count != count) Rewrite();
                return _records.OrderByDescending(record => record.TimeUtc).ToArray();
            }
        }

        public static void Add(NetworkObservationRecord record)
        {
            if (record == null) return;
            lock (Gate)
            {
                EnsureLoaded();
                var previousCount = _records!.Count;
                _records.Add(record);
                Prune(DateTime.UtcNow);
                if (_records.Count < previousCount + 1) Rewrite();
                else AppendLast(record);
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _records = new List<NetworkObservationRecord>();
                try { if (File.Exists(AppPaths.NetworkObservationsFile)) File.Delete(AppPaths.NetworkObservationsFile); }
                catch (Exception ex) { AppLog.Warn("Не удалось удалить журнал сетевой диагностики: " + ex.Message); }
            }
        }

        public static IReadOnlyList<NetworkObservationRecord> CreateSanitizedExportPreview()
            => SanitizeForExport(GetRecent());

        public static IReadOnlyList<NetworkObservationRecord> SanitizeForExport(IEnumerable<NetworkObservationRecord> records)
        {
            var snapshot = records.ToArray();
            var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var processes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Alias(Dictionary<string, string> aliases, string value, string prefix)
            {
                if (string.IsNullOrWhiteSpace(value)) return "";
                if (!aliases.TryGetValue(value, out var alias))
                    aliases[value] = alias = prefix + (aliases.Count + 1);
                return alias;
            }

            return snapshot.Select(record =>
            {
                var hostAlias = Alias(hosts, record.Host, "site-");
                var processAlias = Alias(processes, record.ProcessName, "app-");
                var details = record.Details;
                foreach (var pair in hosts.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
                    details = details.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
                foreach (var pair in processes.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
                    details = details.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
                details = Regex.Replace(details, @"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])", "[IP скрыт]");
                details = Regex.Replace(details, @"(?i)(?<![0-9a-f:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.%]{0,39}(?![0-9a-f:])", "[IPv6 скрыт]");
                static string HideEndpoint(string endpoint)
                {
                    var match = Regex.Match(endpoint ?? "", @"(?<port>:\d+)$");
                    return match.Success ? "[адрес скрыт]" + match.Groups["port"].Value :
                        string.IsNullOrWhiteSpace(endpoint) || endpoint == "—" ? endpoint ?? "" : "[адрес скрыт]";
                }
                return new NetworkObservationRecord
                {
                    TimeUtc = record.TimeUtc,
                    EventType = record.EventType,
                    Source = record.Source,
                    TargetName = string.IsNullOrWhiteSpace(record.TargetName) ? "" : Alias(hosts, record.Host, "site-"),
                    Host = hostAlias,
                    ProcessName = processAlias,
                    ProcessId = null,
                    Protocol = record.Protocol,
                    LocalEndpoint = HideEndpoint(record.LocalEndpoint),
                    RemoteEndpoint = HideEndpoint(record.RemoteEndpoint),
                    ConnectionState = record.ConnectionState,
                    BytesReceived = record.BytesReceived,
                    BytesSent = record.BytesSent,
                    SampleMilliseconds = record.SampleMilliseconds,
                    ActiveConnectionCount = record.ActiveConnectionCount,
                    Ok = record.Ok,
                    FailureKind = record.FailureKind,
                    Milliseconds = record.Milliseconds,
                    HttpStatusCode = record.HttpStatusCode,
                    DnsOk = record.DnsOk,
                    TcpOk = record.TcpOk,
                    BypassRunning = record.BypassRunning,
                    StrategyName = record.StrategyName,
                    ServiceState = record.ServiceState,
                    Details = Limit(details, 240)
                };
            }).ToArray();
        }

        public static void ExportTo(string path, bool sanitize = true)
        {
            var snapshot = sanitize ? CreateSanitizedExportPreview() : GetRecent();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private static string Limit(string value, int length)
            => value.Length > length ? value.Substring(0, length) : value;

        private static void EnsureLoaded()
        {
            if (_records != null) return;
            _records = new List<NetworkObservationRecord>();
            try
            {
                var path = AppPaths.NetworkObservationsFile;
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var item = JsonSerializer.Deserialize<NetworkObservationRecord>(line, JsonOptions);
                        if (item != null) _records.Add(item);
                    }
                    catch { }
                }
                var before = _records.Count;
                Prune(DateTime.UtcNow);
                if (_records.Count != before) Rewrite();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось загрузить локальный журнал сетевой диагностики: " + ex.Message);
                _records.Clear();
            }
        }

        public static IReadOnlyList<NetworkObservationRecord> KeepWithinRetention(
            IEnumerable<NetworkObservationRecord> records, DateTime nowUtc)
        {
            var cutoff = nowUtc - Retention;
            var retained = records.Where(record => record.TimeUtc >= cutoff && record.TimeUtc <= nowUtc.AddMinutes(5))
                .OrderBy(record => record.TimeUtc).ToList();
            if (retained.Count > MaxRecords)
                retained.RemoveRange(0, retained.Count - MaxRecords);
            return retained;
        }

        private static void Prune(DateTime nowUtc)
        {
            var retained = KeepWithinRetention(_records!, nowUtc);
            _records!.Clear();
            _records.AddRange(retained);
        }

        private static void AppendLast(NetworkObservationRecord record)
        {
            try
            {
                AppPaths.EnsureDir(Path.GetDirectoryName(AppPaths.NetworkObservationsFile)!);
                File.AppendAllText(AppPaths.NetworkObservationsFile,
                    JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false));
                var info = new FileInfo(AppPaths.NetworkObservationsFile);
                if (info.Length > 16 * 1024 * 1024) Rewrite();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить запись сетевой диагностики: " + ex.Message);
            }
        }

        private static void Rewrite()
        {
            var path = AppPaths.NetworkObservationsFile;
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                AppPaths.EnsureDir(Path.GetDirectoryName(path)!);
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
                {
                    foreach (var record in _records!)
                        writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
                }
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось уплотнить журнал сетевой диагностики: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
