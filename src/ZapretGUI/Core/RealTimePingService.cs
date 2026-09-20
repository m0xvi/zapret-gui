using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class RealTimePingSnapshot
    {
        public long YouTubeMs { get; init; } = -1;
        public long DiscordMs { get; init; } = -1;
        public long GitHubMs { get; init; } = -1;
        public DateTime Timestamp { get; init; } = DateTime.Now;

        public bool HasData => YouTubeMs >= 0 || DiscordMs >= 0 || GitHubMs >= 0;

        public double AverageLatency
        {
            get
            {
                var valid = new List<long>();
                if (YouTubeMs >= 0) valid.Add(YouTubeMs);
                if (DiscordMs >= 0) valid.Add(DiscordMs);
                if (GitHubMs >= 0) valid.Add(GitHubMs);
                if (valid.Count == 0) return -1;
                double sum = 0;
                foreach (var v in valid) sum += v;
                return Math.Round(sum / valid.Count);
            }
        }

        public string SummaryText
        {
            get
            {
                if (!HasData) return "RTT: проверка…";
                var parts = new List<string>();
                if (YouTubeMs >= 0) parts.Add($"YT {YouTubeMs}ms");
                if (DiscordMs >= 0) parts.Add($"DC {DiscordMs}ms");
                if (GitHubMs >= 0) parts.Add($"GH {GitHubMs}ms");
                return parts.Count == 0 ? "RTT: нет связи" : string.Join(" · ", parts);
            }
        }

        public string StatusKey
        {
            get
            {
                if (!HasData) return "Muted";
                var avg = AverageLatency;
                if (avg < 0) return "Danger";
                if (avg <= 90) return "Success";
                if (avg <= 220) return "Warning";
                return "Danger";
            }
        }

        public string TooltipText
        {
            get
            {
                var yt = YouTubeMs >= 0 ? $"{YouTubeMs} мс (OK)" : "таймаут / недоступен";
                var dc = DiscordMs >= 0 ? $"{DiscordMs} мс (OK)" : "таймаут / недоступен";
                var gh = GitHubMs >= 0 ? $"{GitHubMs} мс (OK)" : "таймаут / недоступен";
                return $"Живая задержка сети (RTT):\n• YouTube: {yt}\n• Discord: {dc}\n• GitHub: {gh}\nОбновлено: {Timestamp:HH:mm:ss}";
            }
        }
    }

    /// <summary>
    /// Лёгкая асинхронная проверка RTT (Round Trip Time) до ключевых сетевых узлов.
    /// </summary>
    public static class RealTimePingService
    {
        public static async Task<RealTimePingSnapshot> ProbeAsync(CancellationToken ct = default)
        {
            var ytTask = PingHostAsync("www.youtube.com", 443, ct);
            var dcTask = PingHostAsync("discord.com", 443, ct);
            var ghTask = PingHostAsync("github.com", 443, ct);

            await Task.WhenAll(ytTask, dcTask, ghTask).ConfigureAwait(false);

            return new RealTimePingSnapshot
            {
                YouTubeMs = await ytTask.ConfigureAwait(false),
                DiscordMs = await dcTask.ConfigureAwait(false),
                GitHubMs = await ghTask.ConfigureAwait(false),
                Timestamp = DateTime.Now
            };
        }

        private static async Task<long> PingHostAsync(string host, int port, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(2800));

                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch
            {
                return -1;
            }
        }
    }
}
