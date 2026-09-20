using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    }
}
