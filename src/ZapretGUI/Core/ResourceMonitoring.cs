using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Ресурс, который пользователь хочет видеть в фоне.</summary>
    public sealed class MonitorTarget : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _name = "";
        private string _url = "";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public string Url
        {
            get => _url;
            set
            {
                if (_url == value) return;
                _url = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url)));
            }
        }

        public string Host { get; set; } = "";
        public int Port { get; set; } = 443;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        public bool IsBuiltIn { get; set; }
        public bool IsGame { get; set; }

        private string _lastStatusText = "";
        public string LastStatusText { get => _lastStatusText; set { if (_lastStatusText != value) { _lastStatusText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastStatusText))); } } }

        private string _lastStatusKey = "Info";
        public string LastStatusKey { get => _lastStatusKey; set { if (_lastStatusKey != value) { _lastStatusKey = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastStatusKey))); } } }

        private string _lastDetails = "";
        public string LastDetails { get => _lastDetails; set { if (_lastDetails != value) { _lastDetails = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastDetails))); } } }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static bool TryCreate(string input, string? name, out MonitorTarget? target, out string error)
        {
            target = null;
            error = "";
            var text = (input ?? "").Trim();
            if (text.Length == 0)
            {
                error = "Введите URL или домен";
                return false;
            }

            if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "Нужен корректный URL, например https://discord.com";
                return false;
            }

            target = new MonitorTarget
            {
                Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(),
                Url = uri.ToString(),
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : uri.Scheme == Uri.UriSchemeHttp ? 80 : 443,
                IsBuiltIn = false
            };
            return true;
        }

        public static MonitorTarget CreateBuiltIn(string name, string url)
        {
            TryCreate(url, name, out var target, out _);
            if (target != null)
            {
                target.IsBuiltIn = true;
                return target;
            }
            return new MonitorTarget { Name = name, Url = url, Host = new Uri(url).Host, IsBuiltIn = true };
        }
    }

    public enum ResourceResultKind
    {
        Available,
        Timeout,
        DnsError,
        TcpError,
        TlsError,
        HttpError,
        Unknown
    }

    public enum ResourceDiagnosisKind
    {
        Available,
        BypassHelps,
        StrategyBreaks,
        ProviderOrServerIssue,
        Unknown
    }

    public sealed class ResourceProbeResult
    {
        public MonitorTarget Target { get; init; } = new();
        public bool Ok { get; init; }
        public long Milliseconds { get; init; }
        public int? HttpStatusCode { get; init; }
        public ResourceResultKind Kind { get; init; }
        public bool DnsOk { get; init; }
        public long DnsMilliseconds { get; init; }
        public string DnsDetails { get; init; } = "";
        public string Details { get; init; } = "";

        public string StatusKey => Ok ? "Success" : "Danger";
        public string StatusText => Ok
            ? (Target.IsGame ? $"Пинг · {Milliseconds} мс" : $"Доступен · {Milliseconds} мс")
            : "Недоступен · " + Details;
    }

    public sealed class ResourceDiagnosisResult
    {
        public MonitorTarget Target { get; init; } = new();
        public ResourceProbeResult Direct { get; init; } = new();
        public ResourceProbeResult WithBypass { get; init; } = new();
        public ResourceDiagnosisKind Kind { get; init; }
        public string Level { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string Summary { get; init; } = "";

        public string StatusKey => Kind == ResourceDiagnosisKind.Available ? "Success" :
            Kind == ResourceDiagnosisKind.StrategyBreaks ? "Danger" : "Warning";
    }

    public sealed class DnsProbeResult
    {
        public bool Ok { get; init; }
        public long Milliseconds { get; init; }
        public int AddressCount { get; init; }
        public string Details { get; init; } = "";
    }

    /// <summary>Проверяет DNS, TCP и HTTPS без системного прокси. WinDivert при этом продолжает работать.</summary>
    public static class ResourceProbe
    {
        public static async Task<DnsProbeResult> CheckDnsAsync(MonitorTarget target,
            CancellationToken ct = default)
        {
            var host = string.IsNullOrWhiteSpace(target.Host) ? new Uri(target.Url).Host : target.Host;
            return await CheckDnsAsync(host, ct).ConfigureAwait(false);
        }

        public static async Task<DnsProbeResult> CheckDnsAsync(string host,
            CancellationToken ct = default)
        {
            var started = DateTime.UtcNow;
            try
            {
                var lookup = Dns.GetHostAddressesAsync(host);
                var completed = await Task.WhenAny(lookup, Task.Delay(3000, ct)).ConfigureAwait(false);
                if (completed != lookup)
                {
                    ct.ThrowIfCancellationRequested();
                    return new DnsProbeResult
                    {
                        Milliseconds = Elapsed(started),
                        Details = "тайм-аут DNS"
                    };
                }

                var addresses = await lookup.ConfigureAwait(false);
                return new DnsProbeResult
                {
                    Ok = addresses.Length > 0,
                    Milliseconds = Elapsed(started),
                    AddressCount = addresses.Length,
                    Details = addresses.Length > 0
                        ? $"получено адресов: {addresses.Length}"
                        : "DNS не вернул адреса"
                };
            }
            catch (TaskCanceledException)
            {
                return new DnsProbeResult { Milliseconds = Elapsed(started), Details = "тайм-аут DNS" };
            }
            catch (SocketException ex)
            {
                return new DnsProbeResult { Milliseconds = Elapsed(started), Details = "DNS: " + Short(ex.Message) };
            }
            catch (Exception ex)
            {
                return new DnsProbeResult { Milliseconds = Elapsed(started), Details = "DNS: " + Short(ex.Message) };
            }
        }

        public static async Task<ResourceProbeResult> CheckAsync(MonitorTarget target,
            CancellationToken ct = default)
        {
            var started = DateTime.UtcNow;
            DnsProbeResult? dns = null;
            try
            {
                var host = string.IsNullOrWhiteSpace(target.Host) ? new Uri(target.Url).Host : target.Host;
                dns = await CheckDnsAsync(host, ct).ConfigureAwait(false);
                if (!dns.Ok)
                    return Failure(target, started, ResourceResultKind.DnsError, dns.Details, dns);

                using var tcp = new TcpClient();
                var defaultPort = new Uri(target.Url).Scheme == Uri.UriSchemeHttp ? 80 : 443;
                var connect = tcp.ConnectAsync(host, target.Port <= 0 ? defaultPort : target.Port);
                var completed = await Task.WhenAny(connect, Task.Delay(5000, ct)).ConfigureAwait(false);
                if (completed != connect)
                {
                    ct.ThrowIfCancellationRequested();
                    return Failure(target, started, ResourceResultKind.Timeout, "тайм-аут TCP", dns);
                }
                await connect.ConfigureAwait(false);
                if (target.IsGame)
                {
                    var tcpMilliseconds = Elapsed(started);
                    return new ResourceProbeResult
                    {
                        Target = target,
                        Ok = true,
                        Milliseconds = tcpMilliseconds,
                        Kind = ResourceResultKind.Available,
                        DnsOk = dns.Ok,
                        DnsMilliseconds = dns.Milliseconds,
                        DnsDetails = dns.Details,
                        Details = $"TCP {tcpMilliseconds} мс · DNS {dns.Milliseconds} мс"
                    };
                }

                using var handler = new System.Net.Http.HttpClientHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false
                };
                using var http = new System.Net.Http.HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };
                using var response = await http.GetAsync(target.Url,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                // Любой HTTP-код ответа (< 500) подтверждает, что TLS-рукопожатие и TCP-соединение
                // успешно прошли сквозь ТСПУ и сервер ответил (включая 404/400/426/204 на служебные пути).
                var ok = status > 0 && status < 500;
                return new ResourceProbeResult
                {
                    Target = target,
                    Ok = ok,
                    Milliseconds = Elapsed(started),
                    HttpStatusCode = status,
                    Kind = ok ? ResourceResultKind.Available : ResourceResultKind.HttpError,
                    DnsOk = dns.Ok,
                    DnsMilliseconds = dns.Milliseconds,
                    DnsDetails = dns.Details,
                    Details = $"HTTP {status} · DNS {dns.Milliseconds} мс"
                };
            }
            catch (TaskCanceledException)
            {
                return Failure(target, started, ResourceResultKind.Timeout, "тайм-аут", dns);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                var text = ex.Message;
                var kind = text.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                    ? ResourceResultKind.TlsError
                    : ResourceResultKind.HttpError;
                return Failure(target, started, kind, Short(text), dns);
            }
            catch (SocketException ex)
            {
                return Failure(target, started, ResourceResultKind.TcpError, Short(ex.Message), dns);
            }
            catch (Exception ex)
            {
                return Failure(target, started, ResourceResultKind.Unknown, Short(ex.Message), dns);
            }
        }

        private static ResourceProbeResult Failure(MonitorTarget target, DateTime started,
            ResourceResultKind kind, string details, DnsProbeResult? dns = null)
            => new()
            {
                Target = target,
                Ok = false,
                Milliseconds = Elapsed(started),
                Kind = kind,
                DnsOk = dns?.Ok ?? false,
                DnsMilliseconds = dns?.Milliseconds ?? 0,
                DnsDetails = dns?.Details ?? "DNS не проверен",
                Details = details
            };

        private static long Elapsed(DateTime started)
            => (long)(DateTime.UtcNow - started).TotalMilliseconds;

        private static string Short(string text)
            => text.Length > 180 ? text.Substring(0, 180) : text;
    }

    public static class MonitorTargetStore
    {
        public static void EnsureDefaults(AppSettings settings)
        {
            if (settings.MonitorTargets.Count > 0) return;
            settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204"));
            settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("Discord", "https://discord.com/api/v9/gateway"));
            settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("GitHub", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt"));
        }

        public static (bool Ok, string Message) AddToGeneralList(AppSettings settings, MonitorTarget target)
        {
            try
            {
                var path = System.IO.Path.Combine(settings.EnginePath, "lists", "list-general-user.txt");
                StrategyParser.EnsureUserLists(settings.EnginePath);
                var lines = System.IO.File.ReadAllLines(path).ToList();
                if (lines.Any(line => line.Trim().Equals(target.Host, StringComparison.OrdinalIgnoreCase)))
                    return (true, $"{target.Host} уже есть в list-general-user.txt");
                using var writer = System.IO.File.AppendText(path);
                writer.WriteLine(target.Host);
                return (true, $"{target.Host} добавлен в list-general-user.txt. Перезапустите обход для применения.");
            }
            catch (Exception ex)
            {
                return (false, "Не удалось добавить ресурс в список: " + ex.Message);
            }
        }
    }
}
