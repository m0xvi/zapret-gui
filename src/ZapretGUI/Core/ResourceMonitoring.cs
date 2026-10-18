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
            // v1.32.5: миграция — добавляем недостающие базовые цели даже если список уже не пуст
            void Ensure(string name, string url)
            {
                var host = new Uri(url).Host;
                if (!settings.MonitorTargets.Any(x => x.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                {
                    var tgt = MonitorTarget.CreateBuiltIn(name, url);
                    if (name == "PUBG") tgt.Enabled = false;
                    settings.MonitorTargets.Add(tgt);
                }
            }
            if (settings.MonitorTargets.Count == 0)
            {
                settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204"));
                settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("Discord", "https://discord.com/api/v9/gateway"));
                settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("GitHub", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt"));
                settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("ChatGPT", "https://chatgpt.com"));
                settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("OpenAI", "https://openai.com"));
                var pubg = MonitorTarget.CreateBuiltIn("PUBG", "https://www.pubg.com");
                pubg.Enabled = false; // выключен по умолчанию чтобы не спамить, включается пресетом
                settings.MonitorTargets.Add(pubg);
                return;
            }
            Ensure("YouTube", "https://www.youtube.com/generate_204");
            Ensure("Discord", "https://discord.com/api/v9/gateway");
            Ensure("GitHub", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt");
            Ensure("ChatGPT", "https://chatgpt.com");
            Ensure("OpenAI", "https://openai.com");
            Ensure("PUBG", "https://www.pubg.com");
            // миграция старого невалидного front.pubg.com -> www.pubg.com
            var oldPubg = settings.MonitorTargets.FirstOrDefault(x => x.Host.Equals("front.pubg.com", StringComparison.OrdinalIgnoreCase));
            if (oldPubg != null)
            {
                oldPubg.Url = "https://www.pubg.com";
                oldPubg.Host = "www.pubg.com";
                oldPubg.Name = "PUBG";
                oldPubg.LastStatusText = "";
                oldPubg.LastDetails = "";
            }
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

        /// <summary>v1.32.5: быстрый пресет для ChatGPT — добавляет все домены OpenAI в обход и мониторинг.</summary>
        public static (bool Ok, string Message) AddChatGptPreset(AppSettings settings)
        {
            var domains = new[]
            {
                "chatgpt.com", "chat.openai.com", "openai.com", "api.openai.com", "auth.openai.com", "auth0.openai.com", "cdn.openai.com", "oaistatic.com", "cdn.oaistatic.com", "oaiusercontent.com"
            };
            var added = 0;
            var skipped = 0;
            try
            {
                var path = System.IO.Path.Combine(settings.EnginePath, "lists", "list-general-user.txt");
                StrategyParser.EnsureUserLists(settings.EnginePath);
                var lines = System.IO.File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
                using var writer = System.IO.File.AppendText(path);
                foreach (var d in domains)
                {
                    if (lines.Contains(d.ToLowerInvariant())) { skipped++; continue; }
                    writer.WriteLine(d);
                    added++;
                }
                // также добавляем в мониторинг если нет
                foreach (var d in new[] { "chatgpt.com", "openai.com" })
                {
                    var url = "https://" + d;
                    var host = d;
                    if (!settings.MonitorTargets.Any(x => x.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    {
                        var name = d == "chatgpt.com" ? "ChatGPT" : "OpenAI";
                        settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn(name, url));
                    }
                }
                SettingsStore.Save(settings);
                var msg = added > 0 ? $"ChatGPT пресет: добавлено {added} доменов в обход, пропущено {skipped}. Перезапустите обход." : $"Все домены ChatGPT уже в списке (пропущено {skipped}). Мониторинг обновлён.";
                return (true, msg);
            }
            catch (Exception ex)
            {
                return (false, "Не удалось применить ChatGPT пресет: " + ex.Message);
            }
        }

        /// <summary>v1.32.6: пресет для PUBG — фикс ошибки 2610.1.1.</summary>
        public static (bool Ok, string Message) AddPubgPreset(AppSettings settings)
        {
            var domains = new[]
            {
                "pubg.com", "krafton.com", "playbattlegrounds.com", "prozone.pubg.com", "prod-live-front.playbattlegrounds.com", "pubg.ru"
            };
            var added = 0;
            var skipped = 0;
            try
            {
                var path = System.IO.Path.Combine(settings.EnginePath, "lists", "list-general-user.txt");
                StrategyParser.EnsureUserLists(settings.EnginePath);
                var lines = System.IO.File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
                using var writer = System.IO.File.AppendText(path);
                foreach (var d in domains)
                {
                    if (lines.Contains(d.ToLowerInvariant())) { skipped++; continue; }
                    writer.WriteLine(d);
                    added++;
                }
                if (!settings.MonitorTargets.Any(x => x.Host.Equals("www.pubg.com", StringComparison.OrdinalIgnoreCase)))
                    settings.MonitorTargets.Add(MonitorTarget.CreateBuiltIn("PUBG", "https://www.pubg.com"));
                settings.GameFilterProfileId = "pubg";
                settings.GameModeActive = false;
                EngineService.SetGameFilterMode(settings.EnginePath, GameFilterMode.TcpAndUdp);
                SettingsStore.Save(settings);
                var msg = added > 0 ? $"PUBG пресет: добавлено {added} доменов, профиль PUBG включён (UDP 7086-7995,10000-10100,17300-17999). Перезапустите обход." : $"PUBG домены уже в списке (пропущено {skipped}), профиль PUBG включён.";
                return (true, msg);
            }
            catch (Exception ex)
            {
                return (false, "Не удалось применить PUBG пресет: " + ex.Message);
            }
        }
    }
}
