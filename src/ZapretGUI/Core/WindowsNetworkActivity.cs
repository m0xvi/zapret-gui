using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ZapretGui.Core
{
    /// <summary>Один активный сокет из таблиц Windows IP Helper. Payload не читается.</summary>
    public sealed class NetworkProcessOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;
        public string ProcessName { get; }
        public string DisplayName => ProcessName + ".exe" + (IsRunning ? "" : " · сейчас не запущен");
        public bool IsRunning { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }

        public NetworkProcessOption(string processName, bool isRunning, bool isSelected)
        {
            ProcessName = WindowsNetworkActivity.NormalizeProcessName(processName);
            IsRunning = isRunning;
            _isSelected = isSelected;
        }
    }

    public sealed class ObservedSocket
    {
        public string ProcessName { get; init; } = "";
        public int ProcessId { get; init; }
        public string Protocol { get; init; } = "";
        public string LocalEndpoint { get; init; } = "";
        public string RemoteEndpoint { get; init; } = "—";
        public string State { get; init; } = "";
        public string Key => $"{ProcessId}|{Protocol}|{LocalEndpoint}|{RemoteEndpoint}";
    }

    /// <summary>
    /// Снимок TCP-соединений и UDP-сокетов выбранных приложений через штатный Windows IP Helper API.
    /// Не устанавливает драйверы и не захватывает/не расшифровывает пакеты.
    /// </summary>
    public static class WindowsNetworkActivity
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int ErrorInsufficientBuffer = 122;
        private const int TcpTableOwnerPidAll = 5;
        private const int UdpTableOwnerPid = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr udpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily, int tableClass, uint reserved);

        public static string NormalizeProcessName(string? name)
        {
            var value = (name ?? "").Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 4);
            return value.Trim().ToLowerInvariant();
        }

        public static IReadOnlyList<ObservedSocket> Capture(IEnumerable<string> selectedProcessNames)
        {
            var selected = (selectedProcessNames ?? Array.Empty<string>())
                .Select(NormalizeProcessName).Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) return Array.Empty<ObservedSocket>();

            var results = new List<ObservedSocket>();
            var namesByPid = new Dictionary<int, string>();
            CaptureTcpFamily(AfInet, false, selected, namesByPid, results);
            CaptureTcpFamily(AfInet6, true, selected, namesByPid, results);
            CaptureUdpFamily(AfInet, false, selected, namesByPid, results);
            CaptureUdpFamily(AfInet6, true, selected, namesByPid, results);
            return results;
        }

        /// <summary>Кандидаты для выбора в интерфейсе: процессы с окном или активным TCP/UDP сокетом.</summary>
        public static IReadOnlyList<string> GetCandidateProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var self = Process.GetCurrentProcess();
                var currentSession = self.SessionId;
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || process.SessionId != currentSession) continue;
                        if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.ProcessName))
                            names.Add(process.ProcessName);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }

            // Автоматически добавляем приложения, у которых сейчас есть сетевые сокеты, даже если у них нет окна.
            try
            {
                var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CaptureTcpFamily(AfInet, false, null, new Dictionary<int, string>(), new List<ObservedSocket>(), all);
                CaptureTcpFamily(AfInet6, true, null, new Dictionary<int, string>(), new List<ObservedSocket>(), all);
                CaptureUdpFamily(AfInet, false, null, new Dictionary<int, string>(), new List<ObservedSocket>(), all);
                CaptureUdpFamily(AfInet6, true, null, new Dictionary<int, string>(), new List<ObservedSocket>(), all);
                names.UnionWith(all);
            }
            catch { }

            return names.Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void CaptureTcpFamily(int family, bool ipv6, HashSet<string>? selected,
            Dictionary<int, string> namesByPid, List<ObservedSocket> results, HashSet<string>? processNames = null)
        {
            using var table = QueryTable(tcp: true, family, out var error);
            if (table == null)
            {
                if (error != 0 && error != 232) throw new InvalidOperationException($"GetExtendedTcpTable ({family}) вернул {error}");
                return;
            }

            var rows = Marshal.ReadInt32(table.Handle);
            var rowSize = ipv6 ? 56 : 24;
            var offset = 4;
            for (var index = 0; index < rows; index++, offset += rowSize)
            {
                var row = IntPtr.Add(table.Handle, offset);
                var state = Marshal.ReadInt32(row, ipv6 ? 48 : 0);
                var localAddress = ipv6
                    ? ReadIpv6(row, 0, unchecked((uint)Marshal.ReadInt32(row, 16)))
                    : ReadIpv4(Marshal.ReadInt32(row, 4));
                var localPort = ReadPort(Marshal.ReadInt32(row, ipv6 ? 20 : 8));
                var remoteAddress = ipv6
                    ? ReadIpv6(row, 24, unchecked((uint)Marshal.ReadInt32(row, 40)))
                    : ReadIpv4(Marshal.ReadInt32(row, 12));
                var remotePort = ReadPort(Marshal.ReadInt32(row, ipv6 ? 44 : 16));
                var pid = Marshal.ReadInt32(row, ipv6 ? 52 : 20);
                var processName = GetProcessName(pid, namesByPid);
                if (processNames != null) processNames.Add(processName);
                if (selected != null && !selected.Contains(NormalizeProcessName(processName))) continue;

                results.Add(new ObservedSocket
                {
                    ProcessName = processName,
                    ProcessId = pid,
                    Protocol = "TCP",
                    LocalEndpoint = FormatEndpoint(localAddress, localPort),
                    RemoteEndpoint = FormatEndpoint(remoteAddress, remotePort),
                    State = TcpStateText(state)
                });
            }
        }

        private static void CaptureUdpFamily(int family, bool ipv6, HashSet<string>? selected,
            Dictionary<int, string> namesByPid, List<ObservedSocket> results, HashSet<string>? processNames = null)
        {
            using var table = QueryTable(tcp: false, family, out var error);
            if (table == null)
            {
                if (error != 0 && error != 232) throw new InvalidOperationException($"GetExtendedUdpTable ({family}) вернул {error}");
                return;
            }

            var rows = Marshal.ReadInt32(table.Handle);
            var rowSize = ipv6 ? 28 : 12;
            var offset = 4;
            for (var index = 0; index < rows; index++, offset += rowSize)
            {
                var row = IntPtr.Add(table.Handle, offset);
                var localAddress = ipv6
                    ? ReadIpv6(row, 0, unchecked((uint)Marshal.ReadInt32(row, 16)))
                    : ReadIpv4(Marshal.ReadInt32(row, 0));
                var localPort = ReadPort(Marshal.ReadInt32(row, ipv6 ? 20 : 4));
                var pid = Marshal.ReadInt32(row, ipv6 ? 24 : 8);
                var processName = GetProcessName(pid, namesByPid);
                if (processNames != null) processNames.Add(processName);
                if (selected != null && !selected.Contains(NormalizeProcessName(processName))) continue;

                results.Add(new ObservedSocket
                {
                    ProcessName = processName,
                    ProcessId = pid,
                    Protocol = "UDP",
                    LocalEndpoint = FormatEndpoint(localAddress, localPort),
                    RemoteEndpoint = "—",
                    State = "сокет открыт (удалённый узел Windows не показывает)"
                });
            }
        }

        private static SafeTable? QueryTable(bool tcp, int family, out uint error)
        {
            uint size = 0;
            error = tcp
                ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpTableOwnerPid, 0);
            if (error == 232) return null; // ERROR_NO_DATA
            if (error != ErrorInsufficientBuffer && error != 0) return null;
            if (size < 4) return null;

            var buffer = Marshal.AllocHGlobal(checked((int)size));
            error = tcp
                ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);
            if (error != 0)
            {
                Marshal.FreeHGlobal(buffer);
                return null;
            }
            return new SafeTable(buffer);
        }

        private static string GetProcessName(int pid, Dictionary<int, string> cache)
        {
            if (cache.TryGetValue(pid, out var found)) return found;
            try
            {
                using var process = Process.GetProcessById(pid);
                found = process.ProcessName;
            }
            catch { found = "pid-" + pid; }
            cache[pid] = found;
            return found;
        }

        private static IPAddress ReadIpv4(int address)
            => new(BitConverter.GetBytes(address));

        private static IPAddress ReadIpv6(IntPtr row, int offset, uint scopeId)
        {
            var bytes = new byte[16];
            Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, bytes.Length);
            return new IPAddress(bytes, scopeId);
        }

        private static int ReadPort(int networkOrderValue)
            => unchecked((ushort)IPAddress.NetworkToHostOrder((short)(networkOrderValue & 0xffff)));

        private static string FormatEndpoint(IPAddress address, int port)
            => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{address}:{port}";

        private static string TcpStateText(int state) => state switch
        {
            1 => "CLOSED",
            2 => "LISTEN",
            3 => "SYN-SENT",
            4 => "SYN-RECEIVED",
            5 => "ESTABLISHED",
            6 => "FIN-WAIT-1",
            7 => "FIN-WAIT-2",
            8 => "CLOSE-WAIT",
            9 => "CLOSING",
            10 => "LAST-ACK",
            11 => "TIME-WAIT",
            12 => "DELETE-TCB",
            _ => "STATE-" + state
        };

        private sealed class SafeTable : IDisposable
        {
            public IntPtr Handle { get; }
            public SafeTable(IntPtr handle) => Handle = handle;
            public void Dispose()
            {
                if (Handle != IntPtr.Zero) Marshal.FreeHGlobal(Handle);
            }
        }
    }
}
