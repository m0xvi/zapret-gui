using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public string StatusText => Ok ? $"OK · {Milliseconds} мс" : "нет соединения";
        public string SeverityKey => Ok ? "Success" : "Danger";
    }

    /// <summary>Быстрая проверка доступности ресурсов (TCP-подключение + HTTPS-запрос).</summary>
    public static class ConnectionTester
    {
        public static Task<List<ConnectionCheck>> RunAsync(CancellationToken ct = default)
            => RunAsync(new[]
            {
                // Набор шире прежних трёх ресурсов и ближе к standard mode
                // из Flowseal test zapret.ps1: несколько независимых CDN/доменов.
                MonitorTarget.CreateBuiltIn("Discord", "https://discord.com/api/v9/gateway"),
                MonitorTarget.CreateBuiltIn("Discord CDN", "https://cdn.discordapp.com"),
                MonitorTarget.CreateBuiltIn("Discord Gateway", "https://gateway.discord.gg"),
                MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204"),
                MonitorTarget.CreateBuiltIn("YouTube image", "https://i.ytimg.com"),
                MonitorTarget.CreateBuiltIn("Google", "https://www.google.com"),
                MonitorTarget.CreateBuiltIn("Cloudflare", "https://www.cloudflare.com"),
                MonitorTarget.CreateBuiltIn("GitHub (обновления)", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt")
            }, ct);

        public static Task<List<ConnectionCheck>> RunAsync(CancellationToken ct,
            IProgress<string>? progress)
            => RunAsync(new[]
            {
                MonitorTarget.CreateBuiltIn("Discord", "https://discord.com/api/v9/gateway"),
                MonitorTarget.CreateBuiltIn("Discord CDN", "https://cdn.discordapp.com"),
                MonitorTarget.CreateBuiltIn("Discord Gateway", "https://gateway.discord.gg"),
                MonitorTarget.CreateBuiltIn("YouTube", "https://www.youtube.com/generate_204"),
                MonitorTarget.CreateBuiltIn("YouTube image", "https://i.ytimg.com"),
                MonitorTarget.CreateBuiltIn("Google", "https://www.google.com"),
                MonitorTarget.CreateBuiltIn("Cloudflare", "https://www.cloudflare.com"),
                MonitorTarget.CreateBuiltIn("GitHub (обновления)", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt")
            }, ct, progress);

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
                Details = probe.Details
            };
        }

        private static async Task<ConnectionCheck> CheckAsync(string title, string host, string url, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, 443);
                var completed = await Task.WhenAny(connect, Task.Delay(5000, ct)).ConfigureAwait(false);
                if (completed != connect || !client.Connected)
                {
                    return new ConnectionCheck
                    {
                        Title = title, Host = host, Ok = false, Milliseconds = watch.ElapsedMilliseconds,
                        Details = "TCP-подключение к порту 443 не установлено"
                    };
                }

                var tcpMs = watch.ElapsedMilliseconds;

                using var handler = new System.Net.Http.HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var response = await http.GetAsync(url, ct).ConfigureAwait(false);

                return new ConnectionCheck
                {
                    Title = title,
                    Host = host,
                    Ok = (int)response.StatusCode < 500,
                    Milliseconds = watch.ElapsedMilliseconds,
                    Details = $"TCP {tcpMs} мс · HTTP {(int)response.StatusCode}"
                };
            }
            catch (Exception ex)
            {
                return new ConnectionCheck
                {
                    Title = title, Host = host, Ok = false,
                    Milliseconds = watch.ElapsedMilliseconds,
                    Details = ex is TaskCanceledException ? "Превышено время ожидания" : ex.Message
                };
            }
        }
    }
}
