using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Что удалось узнать о сети пользователя (v1.30.0).</summary>
    public sealed class ProviderInfo
    {
        public string Name { get; init; } = "";
        public string Asn { get; init; } = "";
        public string Ip { get; init; } = "";
        public string Source { get; init; } = "";
        public bool Ok => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Asn);

        public string DisplayText
        {
            get
            {
                if (!Ok) return "не определено";
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Name)) parts.Add(Name);
                if (!string.IsNullOrWhiteSpace(Asn)) parts.Add(Asn);
                if (!string.IsNullOrWhiteSpace(Ip)) parts.Add("IP " + Ip);
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Определение провайдера и ASN по внешнему IP (v1.30.0). Несколько независимых сервисов:
    /// если сеть режет один — пробуем следующий, как в апдейтере. Ничего не пишет в настройки сам:
    /// результат возвращает вызывающий.
    /// </summary>
    public static class ProviderDetectionService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretGUI/1.30");
            return client;
        }

        /// <summary>Пробует несколько источников по очереди; возвращает первый успешный результат.</summary>
        public static async Task<ProviderInfo> DetectAsync(CancellationToken ct = default)
        {
            var attempts = new (string Title, Func<Task<ProviderInfo>> Probe)[]
            {
                ("ipwho.is", () => ProbeIpWhoIsAsync(ct)),
                ("ipinfo.io", () => ProbeIpInfoAsync(ct)),
                ("api.ipify.org", () => ProbeIpifyAsync(ct))
            };

            foreach (var (title, probe) in attempts)
            {
                try
                {
                    var info = await probe().ConfigureAwait(false);
                    if (info.Ok)
                    {
                        AppLog.Info($"[Сеть] Провайдер определён через {title}: {info.DisplayText}");
                        return info;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"[Сеть] Источник {title} недоступен: {ex.Message}");
                }
            }

            AppLog.Warn("[Сеть] Не удалось определить провайдера: все источники недоступны");
            return new ProviderInfo();
        }

        private static async Task<ProviderInfo> ProbeIpWhoIsAsync(CancellationToken ct)
        {
            var json = await Http.GetStringAsync("https://ipwho.is/", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return new ProviderInfo();

            var ip = GetString(root, "ip");
            var name = "";
            var asn = "";
            if (root.TryGetProperty("connection", out var connection))
            {
                name = GetString(connection, "isp");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(connection, "org");
                asn = GetString(connection, "asn");
            }
            return new ProviderInfo { Name = name, Asn = asn, Ip = ip, Source = "ipwho.is" };
        }

        private static async Task<ProviderInfo> ProbeIpInfoAsync(CancellationToken ct)
        {
            var json = await Http.GetStringAsync("https://ipinfo.io/json", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ip = GetString(root, "ip");
            var org = GetString(root, "org");   // формат «AS12345 Название провайдера»
            var name = org;
            var asn = "";
            if (!string.IsNullOrWhiteSpace(org) && org.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            {
                var space = org.IndexOf(' ');
                if (space > 0)
                {
                    asn = org.Substring(2, space - 2);
                    name = org.Substring(space + 1).Trim();
                }
            }
            return new ProviderInfo { Name = name, Asn = asn, Ip = ip, Source = "ipinfo.io" };
        }

        private static async Task<ProviderInfo> ProbeIpifyAsync(CancellationToken ct)
        {
            var json = await Http.GetStringAsync("https://api.ipify.org?format=json", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var ip = GetString(doc.RootElement, "ip");
            return new ProviderInfo { Ip = ip, Source = "api.ipify.org" };
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
    }
}
