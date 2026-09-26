using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class SniCandidate
    {
        public string Domain { get; init; } = "";
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public int Tier { get; init; } = 1;
        public bool Recommended { get; init; }

        public string DisplayText => $"{Domain} ({Title})";
    }

    public sealed class SniTestResult
    {
        public string Domain { get; init; } = "";
        public string Category { get; init; } = "";
        public bool Ok { get; init; }
        public long LatencyMs { get; init; } = -1;
        public int Score { get; init; }
        public string Status { get; init; } = "";
        public string Details { get; init; } = "";
        public DateTime TestedAt { get; init; } = DateTime.Now;
        public string StatusKey => Ok ? (LatencyMs < 60 ? "Success" : "Warning") : "Danger";
        public string DisplaySummary => Ok ? $"{Domain} · {LatencyMs} мс (OK)" : $"{Domain} · сбой / блок ТСПУ";
    }

    /// <summary>
    /// Менеджер пула TLS SNI фейков, автоподбор оптимального домена и ротация против блокировок ТСПУ (PR #17063, #16507).
    /// </summary>
    public static class SniFakePoolManager
    {
        public static readonly IReadOnlyList<SniCandidate> PredefinedSniPool = new List<SniCandidate>
        {
            // Tier 1: Высочайший траст ТСПУ (Государственные сервисы, банки, ключевые домены РФ)
            new() { Domain = "gosuslugi.ru", Title = "Госуслуги РФ", Category = "Государственные РФ", Tier = 1, Recommended = true },
            new() { Domain = "sberbank.ru", Title = "СберБанк", Category = "Банки РФ", Tier = 1, Recommended = true },
            new() { Domain = "vtb.ru", Title = "Банк ВТБ", Category = "Банки РФ", Tier = 1 },
            new() { Domain = "mos.ru", Title = "Портал Москвы", Category = "Государственные РФ", Tier = 1 },
            new() { Domain = "nalog.gov.ru", Title = "ФНС России", Category = "Государственные РФ", Tier = 1 },
            new() { Domain = "cbr.ru", Title = "Банк России", Category = "Государственные РФ", Tier = 1 },
            new() { Domain = "tinkoff.ru", Title = "Т-Банк", Category = "Банки РФ", Tier = 1 },
            new() { Domain = "yandex.ru", Title = "Яндекс", Category = "Поисковые / Сервисы", Tier = 1, Recommended = true },
            new() { Domain = "vk.com", Title = "ВКонтакте", Category = "Социальные сети", Tier = 1, Recommended = true },
            new() { Domain = "mail.ru", Title = "Mail.ru", Category = "Поисковые / Сервисы", Tier = 1 },
            new() { Domain = "ozon.ru", Title = "Ozon", Category = "Маркетплейсы", Tier = 1 },
            new() { Domain = "wildberries.ru", Title = "Wildberries", Category = "Маркетплейсы", Tier = 1 },

            // Tier 2: Глобальные доверенные CDN и платформы
            new() { Domain = "cloudflare.com", Title = "Cloudflare CDN", Category = "Глобальные CDN", Tier = 2, Recommended = true },
            new() { Domain = "fastly.com", Title = "Fastly CDN", Category = "Глобальные CDN", Tier = 2 },
            new() { Domain = "akamai.com", Title = "Akamai CDN", Category = "Глобальные CDN", Tier = 2 },
            new() { Domain = "microsoft.com", Title = "Microsoft", Category = "Облачные сервисы", Tier = 2 },
            new() { Domain = "wikipedia.org", Title = "Википедия", Category = "Энциклопедии", Tier = 2 },
            new() { Domain = "archive.org", Title = "Internet Archive", Category = "Архивы", Tier = 2 },
            new() { Domain = "github.com", Title = "GitHub", Category = "Разработка", Tier = 2 },
            // Tier 2+: Google/YouTube — критичны для регионов с жёсткой блокировкой YouTube (см. отчёт 24.09: youtube.com FAIL везде)
            new() { Domain = "google.com", Title = "Google", Category = "Поисковые / Сервисы", Tier = 2, Recommended = true },
            new() { Domain = "www.google.com", Title = "Google WWW", Category = "Поисковые / Сервисы", Tier = 2 },
            new() { Domain = "googlevideo.com", Title = "Google Video CDN", Category = "Видео CDN", Tier = 2 },
            new() { Domain = "youtube.com", Title = "YouTube", Category = "Видео", Tier = 2 },
            new() { Domain = "yt3.ggpht.com", Title = "YouTube Images", Category = "Видео", Tier = 2 },
            new() { Domain = "lh3.googleusercontent.com", Title = "Google User Content (превью)", Category = "Видео", Tier = 2 },
            new() { Domain = "i.ytimg.com", Title = "YouTube Thumbnails", Category = "Видео", Tier = 2 },
            new() { Domain = "vk.com", Title = "ВКонтакте (доп)", Category = "Социальные сети", Tier = 2 }
        };

        /// <summary>
        /// Выполняет тестовое TLS-рукопожатие с указанным SNI для проверки реакции ТСПУ.
        /// </summary>
        public static async Task<SniTestResult> TestSniAsync(string sni, int timeoutMs = 2500, CancellationToken ct = default)
        {
            sni = sni.Trim().ToLowerInvariant();
            var candidate = PredefinedSniPool.FirstOrDefault(c => string.Equals(c.Domain, sni, StringComparison.OrdinalIgnoreCase));
            var category = candidate?.Category ?? "Пользовательский";

            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(timeoutMs);

                await client.ConnectAsync(sni, 443, linkedCts.Token).ConfigureAwait(false);

                using var sslStream = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) => true);
                
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = sni,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                };

                await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token).ConfigureAwait(false);
                sw.Stop();

                long rtt = sw.ElapsedMilliseconds;
                int score = (int)(1000 - rtt);
                if (candidate?.Tier == 1) score += 200;

                return new SniTestResult
                {
                    Domain = sni,
                    Category = category,
                    Ok = true,
                    LatencyMs = rtt,
                    Score = score,
                    Status = $"OK · {rtt} мс",
                    Details = $"TLS {sslStream.SslProtocol} успешно завершён без вмешательства ТСПУ"
                };
            }
            catch (OperationCanceledException)
            {
                return new SniTestResult
                {
                    Domain = sni,
                    Category = category,
                    Ok = false,
                    LatencyMs = -1,
                    Score = -100,
                    Status = "Таймаут ТСПУ (сброс пакета)",
                    Details = "Сервер или ТСПУ не ответили на TLS ClientHello"
                };
            }
            catch (Exception ex)
            {
                return new SniTestResult
                {
                    Domain = sni,
                    Category = category,
                    Ok = false,
                    LatencyMs = -1,
                    Score = -200,
                    Status = "Сбой соединения",
                    Details = ex.Message
                };
            }
        }

        /// <summary>
        /// Тестирует пул SNI фейков и ранжирует их по скорости и стабильности.
        /// </summary>
        public static async Task<List<SniTestResult>> TestPoolAsync(
            IEnumerable<string>? customList = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var targets = customList?.Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList()
                          ?? PredefinedSniPool.Select(c => c.Domain).ToList();

            var results = new List<SniTestResult>();
            int index = 0;
            int total = targets.Count;

            using var semaphore = new SemaphoreSlim(4, 4);
            var tasks = targets.Select(async domain =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var res = await TestSniAsync(domain, 2500, ct).ConfigureAwait(false);
                    lock (results)
                    {
                        results.Add(res);
                        index++;
                        progress?.Report($"Проверка SNI {index}/{total}: {domain} → {res.Status}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return results
                .OrderByDescending(r => r.Ok)
                .ThenByDescending(r => r.Score)
                .ThenBy(r => r.LatencyMs)
                .ToList();
        }

        /// <summary>
        /// Подставляет выбранный SNI в список аргументов winws (--dpi-desync-fake-tls-mod=sni=...).
        /// </summary>
        public static List<string> ApplySniOverride(IEnumerable<string> args, string targetSni)
        {
            if (string.IsNullOrWhiteSpace(targetSni)) return args.ToList();
            targetSni = targetSni.Trim();

            var result = new List<string>();
            bool sniReplaced = false;

            foreach (var arg in args)
            {
                if (arg.StartsWith("--dpi-desync-fake-tls-mod=", StringComparison.OrdinalIgnoreCase))
                {
                    // Заменяем sni=... внутри mod
                    if (arg.Contains("sni="))
                    {
                        var parts = arg.Split(new[] { "sni=" }, StringSplitOptions.None);
                        var prefix = parts[0];
                        var rest = parts[1];
                        var commaIdx = rest.IndexOf(',');
                        var suffix = commaIdx >= 0 ? rest.Substring(commaIdx) : "";
                        result.Add($"{prefix}sni={targetSni}{suffix}");
                    }
                    else
                    {
                        result.Add($"{arg},sni={targetSni}");
                    }
                    sniReplaced = true;
                }
                else
                {
                    result.Add(arg);
                }
            }

            // Если не было mod, но есть fake-tls, добавляем mod
            if (!sniReplaced && result.Any(a => a.Contains("fake-tls", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add($"--dpi-desync-fake-tls-mod=sni={targetSni}");
            }

            return result;
        }
    }
}
