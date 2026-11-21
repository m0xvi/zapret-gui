using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace ZapretGui.Core
{
    /// <summary>
    /// Интервальная дельта байтов одного выбранного процесса и сетевого потока.
    /// Записываются только адреса/порты и размеры, никогда не содержимое пакетов.
    /// </summary>
    public sealed class NetworkTrafficDelta
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = "";
        public string Protocol { get; init; } = "";
        public string LocalEndpoint { get; init; } = "";
        public string RemoteEndpoint { get; init; } = "";
        public long BytesSent { get; init; }
        public long BytesReceived { get; init; }
    }

    /// <summary>
    /// Слушает короткий ETW-сеанс Windows Kernel Network. События ETW системные, но в память
    /// агрегируются только PID из выбранного пользователем списка, и ни один пакетный payload
    /// не читается и не сохраняется.
    /// </summary>
    public sealed class ProcessNetworkEtwCapture : IDisposable
    {
        private readonly ConcurrentDictionary<int, string> _selectedProcesses = new();
        private readonly ConcurrentDictionary<string, FlowCounter> _flows = new(StringComparer.OrdinalIgnoreCase);
        private TraceEventSession? _session;
        private Task? _readerTask;
        private string? _readerError;

        public string? ReaderError => Volatile.Read(ref _readerError);

        public ProcessNetworkEtwCapture(IReadOnlyDictionary<int, string> selectedProcesses)
        {
            UpdateSelectedProcesses(selectedProcesses);
        }

        public void Start()
        {
            if (_selectedProcesses.IsEmpty)
                throw new InvalidOperationException("Среди выбранных приложений нет запущенных процессов.");

            var session = new TraceEventSession("ZapretGUI-Network-" + Guid.NewGuid().ToString("N"))
            {
                StopOnDispose = true
            };
            try
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var kernel = session.Source.Kernel;
                kernel.TcpIpSend += data => Add(data.ProcessID, "TCP", data.saddr.ToString(), data.sport,
                    data.daddr.ToString(), data.dport, data.size, sent: true);
                kernel.TcpIpRecv += data => Add(data.ProcessID, "TCP", data.daddr.ToString(), data.dport,
                    data.saddr.ToString(), data.sport, data.size, sent: false);
                kernel.UdpIpSend += data => Add(data.ProcessID, "UDP", data.saddr.ToString(), data.sport,
                    data.daddr.ToString(), data.dport, data.size, sent: true);
                kernel.UdpIpRecv += data => Add(data.ProcessID, "UDP", data.daddr.ToString(), data.dport,
                    data.saddr.ToString(), data.sport, data.size, sent: false);

                _session = session;
                var source = session.Source;
                _readerTask = Task.Factory.StartNew(() =>
                {
                    try { source.Process(); }
                    catch (ObjectDisposedException) { }
                    catch (Exception ex) { Interlocked.Exchange(ref _readerError, ex.Message); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public void UpdateSelectedProcesses(IReadOnlyDictionary<int, string> processes)
        {
            var current = processes ?? new Dictionary<int, string>();
            foreach (var pid in _selectedProcesses.Keys)
                if (!current.ContainsKey(pid)) _selectedProcesses.TryRemove(pid, out _);
            foreach (var pair in current)
                _selectedProcesses[pair.Key] = pair.Value;
        }

        public IReadOnlyList<NetworkTrafficDelta> DrainDeltas()
        {
            var result = new List<NetworkTrafficDelta>();
            foreach (var flow in _flows.Values)
            {
                var (sent, received) = flow.Drain();
                if (sent == 0 && received == 0) continue;
                result.Add(new NetworkTrafficDelta
                {
                    ProcessId = flow.ProcessId,
                    ProcessName = flow.ProcessName,
                    Protocol = flow.Protocol,
                    LocalEndpoint = flow.LocalEndpoint,
                    RemoteEndpoint = flow.RemoteEndpoint,
                    BytesSent = sent,
                    BytesReceived = received
                });
            }
            return result;
        }

        private void Add(int processId, string protocol, string localAddress, int localPort,
            string remoteAddress, int remotePort, int size, bool sent)
        {
            if (size <= 0 || !_selectedProcesses.TryGetValue(processId, out var processName)) return;
            var local = FormatEndpoint(localAddress, localPort);
            var remote = FormatEndpoint(remoteAddress, remotePort);
            var key = $"{processId}|{protocol}|{local}|{remote}";
            var flow = _flows.GetOrAdd(key, _ => new FlowCounter(processId, processName, protocol, local, remote));
            flow.Add(size, sent);
        }

        private static string FormatEndpoint(string address, int port)
            => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";

        public void Dispose()
        {
            var session = Interlocked.Exchange(ref _session, null);
            try { session?.Dispose(); } catch { }
            try { _readerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _readerTask = null;
        }

        private sealed class FlowCounter
        {
            private long _sent;
            private long _received;

            public int ProcessId { get; }
            public string ProcessName { get; }
            public string Protocol { get; }
            public string LocalEndpoint { get; }
            public string RemoteEndpoint { get; }

            public FlowCounter(int processId, string processName, string protocol, string localEndpoint, string remoteEndpoint)
            {
                ProcessId = processId;
                ProcessName = processName;
                Protocol = protocol;
                LocalEndpoint = localEndpoint;
                RemoteEndpoint = remoteEndpoint;
            }

            public void Add(int bytes, bool sent)
            {
                if (sent) Interlocked.Add(ref _sent, bytes);
                else Interlocked.Add(ref _received, bytes);
            }

            public (long Sent, long Received) Drain()
                => (Interlocked.Exchange(ref _sent, 0), Interlocked.Exchange(ref _received, 0));
        }
    }
}
