using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public static async Task<List<ConnectionCheck>> RunAsync(CancellationToken ct = default)
        {
            var targets = new (string Title, string Host, string Url)[]
            {
                ("YouTube", "www.youtube.com", "https://www.youtube.com/generate_204"),
                ("Discord", "discord.com", "https://discord.com/api/v9/gateway"),
                ("GitHub (обновления)", "raw.githubusercontent.com", "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt")
            };

            var results = new List<ConnectionCheck>();
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await CheckAsync(target.Title, target.Host, target.Url, ct).ConfigureAwait(false));
            }
            return results;
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
