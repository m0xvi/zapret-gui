using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class ConnectionCheck
    {
        public string Title { get; init; } = "";
        public string Host { get; init; } = "";
        public bool Ok { get; init; }
        public long Milliseconds { get; init; }
        public string Details { get; init; } = "";
        public ResourceResultKind Kind { get; init; }

        /// <summary>TCP-соединение установилось. Если да, а ресурс «недоступен» — проблема в TLS/HTTP,
        /// а не в отсутствии интернета: браузер в этот момент может работать через QUIC.</summary>
        public bool TcpOk { get; init; }

        /// <summary>Короткая причина отказа. Раньше UI показывал только «нет соединения» и было
        /// непонятно, что именно не прошло: DNS, TCP, TLS или HTTPS.</summary>
        public string CauseText => Ok ? "" : Kind switch
        {
            ResourceResultKind.DnsError => "DNS не ответил",
            ResourceResultKind.Timeout => TcpOk ? "HTTPS не ответил" : "TCP не подключился",
            ResourceResultKind.TcpError => TcpOk ? "HTTPS не открылся" : "TCP закрыт",
            ResourceResultKind.TlsError => "TLS блокируется",
            ResourceResultKind.HttpError => "ошибка HTTPS",
            _ => "причина неизвестна"
        };

        public string StatusText => Ok ? $"OK · {Milliseconds} мс" : "нет соединения · " + CauseText;
        public string SeverityKey => Ok ? "Success" : "Danger";
    }

    /// <summary>Быстрая проверка доступности ресурсов (DNS → TCP → HTTPS).</summary>
    public static class ConnectionTester
    {
        /// <summary>Единый список целей: то, что пользователь видит и настраивает в «Проверка → Ресурсы».
        /// До v2.2.4 здесь был отдельный список из 8 адресов, который расходился с фоновой проверкой.</summary>
        public static List<MonitorTarget> GetEffectiveTargets(AppSettings? settings = null)
        {
            var list = new List<MonitorTarget>();
            if (settings?.MonitorTargets != null)
            {
                foreach (var target in settings.MonitorTargets.Where(t => t.Enabled))
                {
                    if (list.Any(x => x.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    list.Add(target);
                }
            }

            if (list.Count == 0)
                list.AddRange(MonitorTargetStore.BuiltInTargets);

            // Доп цели из utils/targets.txt (если включено и файл есть) — как в Flowseal test zapret.ps1
            if (settings != null && settings.UseTargetsTxtForStrategyTest && !string.IsNullOrWhiteSpace(settings.EnginePath))
            {
                try
                {
                    foreach (var extra in TargetsTxtLoader.LoadFromEngine(settings.EnginePath))
                    {
                        if (!list.Any(x => x.Host.Equals(extra.Host, StringComparison.OrdinalIgnoreCase)))
                            list.Add(extra);
                    }
                }
                catch { }
            }

            return list;
        }

        public static IReadOnlyList<MonitorTarget> GetTargetsTxtExtras(AppSettings? settings)
        {
            if (settings == null || !settings.UseTargetsTxtForStrategyTest || string.IsNullOrWhiteSpace(settings.EnginePath)) return Array.Empty<MonitorTarget>();
            try { return TargetsTxtLoader.LoadFromEngine(settings.EnginePath); } catch { return Array.Empty<MonitorTarget>(); }
        }

        public static Task<List<ConnectionCheck>> RunAsync(CancellationToken ct = default)
            => RunAsync(GetEffectiveTargets(), ct);

        public static Task<List<ConnectionCheck>> RunAsync(CancellationToken ct,
            IProgress<string>? progress)
            => RunAsync(GetEffectiveTargets(), ct, progress);

        public static Task<List<ConnectionCheck>> RunAsync(AppSettings? settings,
            CancellationToken ct, IProgress<string>? progress)
            => RunAsync(GetEffectiveTargets(settings), ct, progress);

        public static async Task<List<ConnectionCheck>> RunAsync(IEnumerable<MonitorTarget> targets,
            CancellationToken ct = default, IProgress<string>? progress = null)
        {
            var list = targets.Where(t => t.Enabled).ToList();
            ct.ThrowIfCancellationRequested();
            progress?.Report($"CONNECTION_TOTAL:{list.Count}");
            var completed = 0;
            // Проверки независимы: параллельный запуск сокращает время проверки нескольких адресов.
            var checks = await Task.WhenAll(list.Select(async target =>
            {
                var result = await CheckAsync(target, ct).ConfigureAwait(false);
                var number = Interlocked.Increment(ref completed);
                progress?.Report($"CONNECTION_PROGRESS:{number}/{Math.Max(1, list.Count)} — {target.Name}");
                return result;
            })).ConfigureAwait(false);
            return checks.ToList();
        }

        private static async Task<ConnectionCheck> CheckAsync(MonitorTarget target, CancellationToken ct)
        {
            var probe = await ResourceProbe.CheckAsync(target, ct).ConfigureAwait(false);
            return new ConnectionCheck
            {
                Title = target.Name,
                Host = target.Host,
                Ok = probe.Ok,
                Milliseconds = probe.Milliseconds,
                Kind = probe.Kind,
                TcpOk = probe.TcpOk,
                Details = probe.Details
            };
        }
    }
}
