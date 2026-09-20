using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class DiscordVoiceServerCheck
    {
        public string ServerName { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public int Port { get; init; } = 50001;
        public bool UdpOk { get; set; }
        public bool SignalingOk { get; set; }
        public long PingMs { get; set; }
        public int PacketLossPercent { get; set; }
        public string StatusDetails { get; set; } = "";
        public string StatusKey => UdpOk ? "Success" : SignalingOk ? "Warning" : "Danger";
        public string StatusText => UdpOk ? $"RTC OK · {PingMs} мс" : SignalingOk ? "Сигналинг OK · UDP дроп (ТСПУ)" : "Недоступен";
    }

    public sealed class DiscordVoiceRtcReport
    {
        public DateTime CheckedAt { get; init; } = DateTime.Now;
        public bool OverallVoiceReady { get; init; }
        public int PassedCount { get; init; }
        public int TotalCount { get; init; }
        public long AveragePingMs { get; init; }
        public string SummaryText { get; init; } = "";
        public string DiagnosisKey { get; init; } = "Muted";
        public string RecommendationText { get; init; } = "";
        public List<DiscordVoiceServerCheck> Servers { get; init; } = new();
    }

    /// <summary>
    /// Специализированный зонд для диагностики Discord Voice (RTC / WebRTC) и выявления блокировок UDP ТСПУ
    /// (проблема бесконечного «Подключения к RTC» и «Не установлен маршрут»).
    /// </summary>
    public static class DiscordVoiceRtcProber
    {
        public static readonly (string Name, string Host, int Port)[] VoiceEndpoints = new[]
        {
            ("Роттердам (Rotterdam)", "rotterdam.discord.media", 50001),
            ("Франкфурт (Frankfurt)", "frankfurt.discord.media", 50001),
            ("Стокгольм (Stockholm)", "stockholm.discord.media", 50001),
            ("Мадрид (Madrid)", "madrid.discord.media", 50001),
            ("Google STUN (WebRTC)", "stun.l.google.com", 19302)
        };

        /// <summary>
        /// Выполняет комплексную проверку всех ключевых голосовых серверов Discord.
        /// </summary>
        public static async Task<DiscordVoiceRtcReport> RunFullVoiceAuditAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var results = new List<DiscordVoiceServerCheck>();
            var total = VoiceEndpoints.Length;
            var current = 0;

            foreach (var (name, host, port) in VoiceEndpoints)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                progress?.Report($"Проверка голосового шлюза {current}/{total}: {name}…");

                var check = await ProbeVoiceEndpointAsync(name, host, port, ct).ConfigureAwait(false);
                results.Add(check);
            }

            var passed = results.Count(r => r.UdpOk);
            var signalingOnly = results.Count(r => !r.UdpOk && r.SignalingOk);
            var avgPing = passed > 0 ? (long)results.Where(r => r.UdpOk).Average(r => r.PingMs) : 0;

            var overallReady = passed >= 2;
            string diagKey;
            string summary;
            string recommendation;

            if (overallReady)
            {
                diagKey = "Success";
                summary = $"Голосовой стек Discord (RTC/UDP) полностью доступен ({passed}/{total} серверов OK, средний пинг {avgPing} мс).";
                recommendation = "Голосовые каналы и стримы Discord работают штатно без зависания RTC.";
            }
            else if (signalingOnly > 0)
            {
                diagKey = "Danger";
                summary = "Обнаружена блокировка UDP WebRTC со стороны ТСПУ (сигналинг проходит, но UDP-пакеты дропаются).";
                recommendation = "Причина бесконечного «Подключения к RTC»: ТСПУ сбрасывает UDP на портах 50000-65535. Рекомендуется активировать стратегию с параметром --wf-udp=50000-65535, fooling ts и повторами 11 (например, general (ALT9) или general (EXP)).";
            }
            else
            {
                diagKey = "Warning";
                summary = "Связь с голосовыми серверами Discord отсутствует или заблокирован домен discord.media.";
                recommendation = "Проверьте наличие discord.media в list-discord.txt и активность WinDivert.";
            }

            return new DiscordVoiceRtcReport
            {
                CheckedAt = DateTime.Now,
                OverallVoiceReady = overallReady,
                PassedCount = passed,
                TotalCount = total,
                AveragePingMs = avgPing,
                SummaryText = summary,
                DiagnosisKey = diagKey,
                RecommendationText = recommendation,
                Servers = results
            };
        }

        /// <summary>
        /// Зондирует отдельный голосовой шлюз по протоколу STUN / UDP и HTTP сигналингу.
        /// </summary>
        public static async Task<DiscordVoiceServerCheck> ProbeVoiceEndpointAsync(string name, string host, int port, CancellationToken ct = default)
        {
            var check = new DiscordVoiceServerCheck
            {
                ServerName = name,
                Endpoint = host,
                Port = port
            };

            // 1. Проверка разрешения DNS
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (addresses.Length == 0)
                {
                    check.StatusDetails = "DNS не разрешил адрес";
                    return check;
                }
            }
            catch (Exception ex)
            {
                check.StatusDetails = "Ошибка DNS: " + ex.Message;
                return check;
            }

            var targetIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];

            // 2. Быстрая проверка HTTPS сигналинга (TCP 443)
            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(targetIp, 443);
                var done = await Task.WhenAny(connectTask, Task.Delay(2500, ct)).ConfigureAwait(false);
                check.SignalingOk = done == connectTask && tcp.Connected;
            }
            catch
            {
                check.SignalingOk = false;
            }

            // 3. Отправка серии STUN/UDP датаграмм на медиа-порт (50001 или 19302)
            var attempts = 3;
            var successful = 0;
            var rtts = new List<long>();

            for (var i = 0; i < attempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rtt = await SendStunBindingProbeAsync(targetIp, port, ct).ConfigureAwait(false);
                if (rtt >= 0)
                {
                    successful++;
                    rtts.Add(rtt);
                }
                if (i < attempts - 1) await Task.Delay(100, ct).ConfigureAwait(false);
            }

            check.UdpOk = successful > 0;
            check.PacketLossPercent = (int)((attempts - successful) / (double)attempts * 100);
            check.PingMs = rtts.Count > 0 ? (long)rtts.Average() : 0;

            if (check.UdpOk)
            {
                check.StatusDetails = $"UDP ответ получен · RTT {check.PingMs} мс · потери {check.PacketLossPercent}%";
            }
            else if (check.SignalingOk)
            {
                check.StatusDetails = "TCP сигналинг OK · UDP WebRTC сброшен ТСПУ (потери 100%)";
            }
            else
            {
                check.StatusDetails = "Тайм-аут UDP и TCP";
            }

            return check;
        }

        /// <summary>
        /// Формирует стандартный RFC 5389 STUN Binding Request (20 байт) и ожидает ответа.
        /// </summary>
        private static async Task<long> SendStunBindingProbeAsync(IPAddress ip, int port, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 2000;
                udp.Client.SendTimeout = 2000;

                // STUN Binding Request Header:
                // Type: 0x0001 (Binding Request)
                // Length: 0x0000
                // Magic Cookie: 0x2112A442
                // Transaction ID: 12 случайных байт
                var packet = new byte[20];
                packet[0] = 0x00;
                packet[1] = 0x01; // Binding Request
                packet[2] = 0x00;
                packet[3] = 0x00; // Attribute length = 0
                packet[4] = 0x21;
                packet[5] = 0x12;
                packet[6] = 0xA4;
                packet[7] = 0x42; // Magic Cookie

                RandomNumberGenerator.Fill(packet.AsSpan(8, 12));

                var endPoint = new IPEndPoint(ip, port);
                await udp.SendAsync(packet, packet.Length, endPoint).ConfigureAwait(false);

                var receiveTask = udp.ReceiveAsync();
                var completed = await Task.WhenAny(receiveTask, Task.Delay(2000, ct)).ConfigureAwait(false);

                if (completed == receiveTask)
                {
                    var result = await receiveTask.ConfigureAwait(false);
                    sw.Stop();
                    // Ответ получен (STUN Binding Response 0x0101 или любые медиа-байты)
                    if (result.Buffer != null && result.Buffer.Length >= 4)
                    {
                        return sw.ElapsedMilliseconds;
                    }
                    return sw.ElapsedMilliseconds;
                }
            }
            catch { }

            return -1;
        }
    }
}
