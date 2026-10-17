using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class DnsProfile
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string PrimaryServer { get; init; } = "";
        public string SecondaryServer { get; init; } = "";
        public string DohUrl { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsDhcp => string.IsNullOrWhiteSpace(PrimaryServer);

        public string DisplayTitle => IsDhcp ? "Автоматический DNS (DHCP)" : $"{Name} ({PrimaryServer})";
    }

    public sealed class DnsTestResult
    {
        public bool Ok { get; init; }
        public long Milliseconds { get; init; }
        public string ResolvedIp { get; init; } = "";
        public string Message { get; init; } = "";
    }

    public sealed class DnsHijackEntry
    {
        public string Domain { get; init; } = "";
        public string[] SystemIps { get; init; } = Array.Empty<string>();
        public string[] DohIps { get; init; } = Array.Empty<string>();
        public bool IsHijacked { get; init; }
        public string Details { get; init; } = "";
        public string StatusKey => IsHijacked ? "Danger" : "Success";
        public string StatusText => IsHijacked ? "Подмена" : "OK";
        public string SystemIpsText => SystemIps.Length == 0 ? "—" : string.Join(", ", SystemIps);
        public string DohIpsText => DohIps.Length == 0 ? "—" : string.Join(", ", DohIps);
    }

    public sealed class DnsHijackReport
    {
        public List<DnsHijackEntry> Entries { get; init; } = new();
        public bool HasHijack => Entries.Any(e => e.IsHijacked);
        public string Summary => HasHijack
            ? $"Обнаружена подмена DNS у провайдера ({Entries.Count(e=>e.IsHijacked)} из {Entries.Count} доменов)"
            : $"Подмены DNS не обнаружено ({Entries.Count} доменов проверено)";
        public string StatusKey => HasHijack ? "Danger" : "Success";
    }

    /// <summary>
    /// Управление DNS-серверами и безопасным DNS (DoH) для активного сетевого интерфейса.
    /// </summary>
    public static class DnsManagementService
    {
        public static readonly IReadOnlyList<DnsProfile> PredefinedProfiles = new List<DnsProfile>
        {
            new()
            {
                Id = "cloudflare",
                Name = "Cloudflare DNS",
                PrimaryServer = "1.1.1.1",
                SecondaryServer = "1.0.0.1",
                DohUrl = "https://cloudflare-dns.com/dns-query",
                Description = "Ультрабыстрый, приватный DNS без цензуры и подмены провайдером."
            },
            new()
            {
                Id = "quad9",
                Name = "Quad9 DNS",
                PrimaryServer = "9.9.9.9",
                SecondaryServer = "149.112.112.112",
                DohUrl = "https://dns.quad9.net/dns-query",
                Description = "Защищённый DNS с блокировкой вредоносных сайтов и фишинга."
            },
            new()
            {
                Id = "google",
                Name = "Google Public DNS",
                PrimaryServer = "8.8.8.8",
                SecondaryServer = "8.8.4.4",
                DohUrl = "https://dns.google/dns-query",
                Description = "Глобальный надёжный DNS-сервер от компании Google."
            },
            new()
            {
                Id = "yandex",
                Name = "Яндекс DNS",
                PrimaryServer = "77.88.8.8",
                SecondaryServer = "77.88.8.1",
                DohUrl = "https://dns.yandex.ru/dns-query",
                Description = "Быстрый DNS с серверами в России и минимальным пингом."
            },
            new()
            {
                Id = "dhcp",
                Name = "Автоматический DNS (DHCP)",
                PrimaryServer = "",
                SecondaryServer = "",
                DohUrl = "",
                Description = "Получать адреса DNS автоматически от вашего роутера или провайдера."
            }
        };

        public static void FlushDnsCache()
        {
            try
            {
                Shell.Run("ipconfig.exe", new[] { "/flushdns" });
            }
            catch
            {
                // Игнорируем ошибки вызова в изолированных средах
            }
        }

        public static NetworkInterface? GetActiveInterface()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                  nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                  !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                                  !nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses.Count > 0)
                    .ThenByDescending(nic => nic.Speed)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static string GetCurrentDnsSummary()
        {
            try
            {
                var nic = GetActiveInterface();
                if (nic == null) return "Сетевой адаптер не найден";
                var servers = nic.GetIPProperties().DnsAddresses
                    .Where(addr => addr.AddressFamily == AddressFamily.InterNetwork)
                    .Select(addr => addr.ToString())
                    .ToList();

                if (servers.Count == 0) return $"{nic.Name}: DHCP (авто)";
                return $"{nic.Name}: {string.Join(", ", servers)}";
            }
            catch (Exception ex)
            {
                return "Ошибка чтения DNS: " + ex.Message;
            }
        }

        public static async Task<(bool Ok, string Message)> ApplyDnsProfileAsync(DnsProfile profile)
        {
            var nic = GetActiveInterface();
            if (nic == null) return (false, "Не найден активный сетевой адаптер.");

            var adapterName = nic.Name;
            try
            {
                if (profile.IsDhcp)
                {
                    var res = await Task.Run(() => Shell.Run("netsh.exe", new[] { "interface", "ip", "set", "dns", $"name={adapterName}", "dhcp" })).ConfigureAwait(false);
                    await Task.Run(() => Shell.Run("ipconfig.exe", new[] { "/flushdns" })).ConfigureAwait(false);
                    return res.Ok
                        ? (true, $"DNS адаптера «{adapterName}» переключён в режим DHCP (автоматически). Кэш очищен.")
                        : (false, $"Ошибка netsh: {res.StdErr}");
                }
                else
                {
                    var res1 = await Task.Run(() => Shell.Run("netsh.exe", new[] { "interface", "ip", "set", "dns", $"name={adapterName}", "static", profile.PrimaryServer })).ConfigureAwait(false);
                    if (!res1.Ok)
                        return (false, $"Не удалось установить основной DNS: {res1.StdErr}");

                    if (!string.IsNullOrWhiteSpace(profile.SecondaryServer))
                    {
                        await Task.Run(() => Shell.Run("netsh.exe", new[] { "interface", "ip", "add", "dns", $"name={adapterName}", profile.SecondaryServer, "index=2" })).ConfigureAwait(false);
                    }

                    await Task.Run(() => Shell.Run("ipconfig.exe", new[] { "/flushdns" })).ConfigureAwait(false);
                    return (true, $"Установлен {profile.Name} ({profile.PrimaryServer}) для адаптера «{adapterName}». Кэш DNS очищен.");
                }
            }
            catch (Exception ex)
            {
                return (false, "Ошибка применения DNS: " + ex.Message);
            }
        }

        public static async Task<DnsTestResult> TestDnsServerAsync(string dnsServerIp, string domain = "www.youtube.com")
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(dnsServerIp))
                {
                    var hostEntry = await Dns.GetHostEntryAsync(domain).ConfigureAwait(false);
                    sw.Stop();
                    var ip = hostEntry.AddressList.FirstOrDefault()?.ToString() ?? "—";
                    return new DnsTestResult
                    {
                        Ok = true,
                        Milliseconds = sw.ElapsedMilliseconds,
                        ResolvedIp = ip,
                        Message = $"OK · {sw.ElapsedMilliseconds} мс · {ip}"
                    };
                }

                using var client = new UdpClient();
                client.Client.ReceiveTimeout = 2500;
                client.Client.SendTimeout = 2500;

                // Быстрая проверка TCP/UDP доступности DNS сервера
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(dnsServerIp, 53, cts.Token).ConfigureAwait(false);
                sw.Stop();

                return new DnsTestResult
                {
                    Ok = true,
                    Milliseconds = sw.ElapsedMilliseconds,
                    ResolvedIp = dnsServerIp,
                    Message = $"Сервер отвечает · {sw.ElapsedMilliseconds} мс"
                };
            }
            catch (Exception ex)
            {
                return new DnsTestResult
                {
                    Ok = false,
                    Message = "Таймаут или недоступен: " + ex.Message
                };
            }
        }

        public static List<string> GetSystemDnsServers()
        {
            try
            {
                var nic = GetActiveInterface();
                if (nic == null) return new List<string>();
                return nic.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        public static async Task<DnsHijackReport> CheckHijackAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var domains = new[] { "www.youtube.com", "discord.com", "github.com", "www.google.com" };
            var entries = new List<DnsHijackEntry>();

            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-json");

            foreach (var domain in domains)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Проверяю {domain}…");
                string[] systemIps = Array.Empty<string>();
                string[] dohIps = Array.Empty<string>();
                var hijacked = false;
                var details = "";

                try
                {
                    // Системный резолв (через текущий DNS)
                    try
                    {
                        var sys = await Dns.GetHostAddressesAsync(domain, ct).ConfigureAwait(false);
                        systemIps = sys.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray();
                    }
                    catch (Exception ex)
                    {
                        details = "системный DNS: " + ex.Message + "; ";
                    }

                    // DoH резолв через Cloudflare (эталон)
                    try
                    {
                        var url = $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(domain)}&type=A";
                        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.Array)
                            {
                                var list = new List<string>();
                                foreach (var el in ans.EnumerateArray())
                                {
                                    if (el.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                                    {
                                        var ip = d.GetString();
                                        if (!string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                                            list.Add(ip);
                                    }
                                }
                                dohIps = list.ToArray();
                            }
                        }
                        else
                        {
                            details += $"DoH HTTP {(int)resp.StatusCode}; ";
                        }
                    }
                    catch (Exception ex)
                    {
                        details += "DoH: " + ex.Message + "; ";
                    }

                    // Анализ
                    if (systemIps.Length == 0 && dohIps.Length > 0)
                    {
                        hijacked = true;
                        details += "системный DNS не вернул адреса, а DoH вернул";
                    }
                    else if (systemIps.Length > 0 && dohIps.Length > 0)
                    {
                        var sysSet = new HashSet<string>(systemIps);
                        var dohSet = new HashSet<string>(dohIps);
                        if (!sysSet.Overlaps(dohSet))
                        {
                            // Проверяем характерные признаки подмены: приватные IP или один и тот же блок
                            var privateSys = systemIps.Any(ip => ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("127.") || ip == "0.0.0.0");
                            if (privateSys || systemIps.Length == 1)
                            {
                                hijacked = true;
                                details += "адреса не совпадают и системный похож на заглушку";
                            }
                            else
                            {
                                // Разные легитимные CDN — не считаем hijack, но отметим расхождение
                                details += "адреса различаются (возможно CDN/Geo), подмена не подтверждена";
                            }
                        }
                        else
                        {
                            details += "адреса совпадают";
                        }
                    }
                    else if (systemIps.Length == 0 && dohIps.Length == 0)
                    {
                        details += "оба резолва не вернули адресов";
                    }
                    else
                    {
                        details += "частичный результат";
                    }
                }
                catch (Exception ex)
                {
                    details += "ошибка: " + ex.Message;
                }

                entries.Add(new DnsHijackEntry
                {
                    Domain = domain,
                    SystemIps = systemIps,
                    DohIps = dohIps,
                    IsHijacked = hijacked,
                    Details = details.Trim()
                });
            }

            return new DnsHijackReport { Entries = entries };
        }
    }
}
