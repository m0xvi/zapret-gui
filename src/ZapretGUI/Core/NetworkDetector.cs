using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace ZapretGui.Core
{
    /// <summary>Идентификатор текущей сети (SSID/шлюз/интерфейс) для привязки профилей.</summary>
    public sealed class NetworkIdentity
    {
        public string Fingerprint { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Ssid { get; init; } = "";
        public string Gateway { get; init; } = "";
        public string InterfaceName { get; init; } = "";
        public string InterfaceDescription { get; init; } = "";
        public bool IsWifi => !string.IsNullOrWhiteSpace(Ssid);
        public bool IsValid => !string.IsNullOrWhiteSpace(Fingerprint);
    }

    public static class NetworkDetector
    {
        public static NetworkIdentity GetCurrentIdentity()
        {
            try
            {
                var nic = GetActiveInterface();
                var gateway = "";
                var ifaceName = "";
                var ifaceDesc = "";
                if (nic != null)
                {
                    ifaceName = nic.Name;
                    ifaceDesc = nic.Description;
                    var gw = nic.GetIPProperties().GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (gw != null) gateway = gw.Address.ToString();
                }

                var ssid = TryGetWifiSsid();
                var display = "";
                var fingerprint = "";

                if (!string.IsNullOrWhiteSpace(ssid))
                {
                    display = $"Wi-Fi: {ssid}";
                    if (!string.IsNullOrWhiteSpace(gateway)) display += $" · {gateway}";
                    fingerprint = $"wifi:{ssid.ToLowerInvariant()}|gw:{gateway}|if:{ifaceName}";
                }
                else if (!string.IsNullOrWhiteSpace(gateway))
                {
                    var kind = nic?.NetworkInterfaceType.ToString() ?? "Сеть";
                    if (kind == "Ethernet") kind = "Ethernet";
                    display = $"{kind}: {ifaceName} · {gateway}";
                    fingerprint = $"eth:{gateway}|if:{ifaceName}";
                }
                else if (nic != null)
                {
                    display = $"{nic.NetworkInterfaceType}: {ifaceName}";
                    fingerprint = $"if:{ifaceName}|desc:{ifaceDesc}".ToLowerInvariant();
                }
                else
                {
                    return new NetworkIdentity { Fingerprint = "", DisplayName = "Сеть не определена" };
                }

                // Нормализуем отпечаток: нижний регистр, без пробелов
                fingerprint = fingerprint.Trim().ToLowerInvariant();
                display = display.Trim();
                return new NetworkIdentity
                {
                    Fingerprint = fingerprint,
                    DisplayName = display,
                    Ssid = ssid ?? "",
                    Gateway = gateway,
                    InterfaceName = ifaceName,
                    InterfaceDescription = ifaceDesc
                };
            }
            catch (Exception ex)
            {
                AppLog.Debug("[NetworkDetector] Ошибка определения сети: " + ex.Message);
                return new NetworkIdentity { Fingerprint = "", DisplayName = "Ошибка определения сети" };
            }
        }

        public static NetworkInterface? GetActiveInterface()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                                  && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                  && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses.Count > 0)
                    .ThenByDescending(nic => nic.Speed)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string? TryGetWifiSsid()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(866)
                };
                // Fallback to UTF8 if 866 unavailable
                try { psi.StandardOutputEncoding = System.Text.Encoding.GetEncoding(866); } catch { psi.StandardOutputEncoding = System.Text.Encoding.UTF8; }

                using var proc = Process.Start(psi);
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                if (string.IsNullOrWhiteSpace(output)) return null;

                // Парсим SSID : MyWifi (англ и рус локализации)
                var match = Regex.Match(output, @"SSID\s*:\s*(.+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var ssid = match.Groups[1].Value.Trim();
                    // Отсекаем BSSID линию (у netsh есть "BSSID" после SSID, но regex хватает первую)
                    // Если SSID пустой или "не подключено" — игнорируем
                    if (!string.IsNullOrWhiteSpace(ssid)
                        && !ssid.Equals("не подключено", StringComparison.OrdinalIgnoreCase)
                        && !ssid.Equals("disconnected", StringComparison.OrdinalIgnoreCase)
                        && ssid.Length > 1)
                        return ssid;
                }
                return null;
            }
            catch { return null; }
        }
    }
}
