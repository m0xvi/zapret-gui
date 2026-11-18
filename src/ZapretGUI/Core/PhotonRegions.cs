using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Результат проверки одного региона Photon (пинг = время TCP-рукопожатия).</summary>
    public sealed class PhotonRegionResult
    {
        public string Name { get; init; } = "";
        public string Host { get; init; } = "";
        public bool Ok { get; init; }
        public long Milliseconds { get; init; }
        public string Details { get; init; } = "";

        public string StatusText => Ok ? $"{Milliseconds} мс" : Details;
        public string SeverityKey => !Ok ? "Muted" : Milliseconds < 120 ? "Success" : Milliseconds < 250 ? "Warning" : "Danger";
    }

    /// <summary>
    /// v2.2.6: R.E.P.O. работает не по P2P, а через Photon relay — регион выбирается в самой игре,
    /// а «пинг» на экране регионов считает клиент Photon. Цифры там бывают сильно завышены
    /// (у части провайдеров европейский узел отвечает 800 мс), из-за чего игрок уходит на US.
    /// Здесь мы меряем реальное время ответа до узлов Photon и подсказываем, какой регион выбрать.
    /// </summary>
    public static class PhotonRegions
    {
        /// <summary>Узлы Photon по регионам (как в списке регионов R.E.P.O.).</summary>
        public static readonly IReadOnlyList<(string Name, string Host)> Regions = new List<(string, string)>
        {
            ("Европа", "ns-eu.photonengine.io"),
            ("США (восток)", "ns-us.photonengine.io"),
            ("США (запад)", "ns-usw.photonengine.io"),
            ("Япония", "ns-jp.photonengine.io"),
            ("Азия", "ns-asia.photonengine.io"),
            ("Южная Америка", "ns-sa.photonengine.io"),
            ("Австралия", "ns-au.photonengine.io"),
            ("Южная Африка", "ns-za.photonengine.io")
        };

        /// <summary>Домены Photon/ExitGames: их можно вести через обход, если провайдер режет TLS.</summary>
        public static readonly string[] BypassHosts =
        {
            "photonengine.io",
            "photonengine.com",
            "exitgames.com",
            "exitgamescloud.com"
        };

        /// <summary>Порты игрового трафика Photon (UDP relay) и Steam P2P — их обход трогать не должен.</summary>
        public static readonly string[] GamePorts = { "4530-4533", "5055-5058", "9090-9091", "27015-27200" };

        /// <summary>
        /// Приводит списки доменов в соответствие с выбором пользователя:
        /// «через обход» — Photon попадает в list-general-user.txt и убирается из исключений;
        /// «напрямую» — наоборот, домены уходят в list-exclude-user.txt, чтобы обход их не трогал.
        /// Так игровой TCP-трафик Photon не остаётся в подвешенном состоянии после смены настройки.
        /// </summary>
        public static (bool Ok, string Message) ApplyRouting(AppSettings settings)
        {
            if (settings == null) return (false, "Настройки недоступны");
            if (string.IsNullOrWhiteSpace(settings.EnginePath))
                return (false, "Движок не установлен — списки пока некуда записать.");

            try
            {
                var lists = System.IO.Path.Combine(settings.EnginePath, "lists");
                AppPaths.EnsureDir(lists);
                StrategyParser.EnsureUserLists(settings.EnginePath);

                var generalUser = System.IO.Path.Combine(lists, "list-general-user.txt");
                var excludeUser = System.IO.Path.Combine(lists, "list-exclude-user.txt");

                if (settings.PhotonThroughBypass)
                {
                    AddHosts(generalUser, BypassHosts, "# Пользовательские домены");
                    RemoveHosts(excludeUser, BypassHosts);
                    return (true, "Photon идёт через обход: перезапустите обход для применения.");
                }

                RemoveHosts(generalUser, BypassHosts);
                AddHosts(excludeUser, BypassHosts, "# Домены-исключения (не обрабатывать)");
                return (true, "Photon идёт напрямую: обход не трогает его трафик.");
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Photon] Не удалось обновить списки: " + ex.Message);
                return (false, "Не удалось обновить списки: " + ex.Message);
            }
        }

        private static void RemoveHosts(string path, IEnumerable<string> hosts)
        {
            if (!System.IO.File.Exists(path)) return;
            var drop = hosts.Select(h => h.ToLowerInvariant()).ToHashSet();
            var lines = System.IO.File.ReadAllLines(path);
            var kept = lines.Where(l =>
            {
                var value = l.Trim().ToLowerInvariant();
                return value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal) || !drop.Contains(value);
            }).ToList();
            if (kept.Count == lines.Length) return;
            System.IO.File.WriteAllLines(path, kept, new System.Text.UTF8Encoding(false));
            AppLog.Info($"[Photon] Убраны домены Photon из {System.IO.Path.GetFileName(path)}");
        }

        private static void AddHosts(string path, IEnumerable<string> hosts, string header)
        {
            var existing = System.IO.File.Exists(path)
                ? System.IO.File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet()
                : new HashSet<string>();
            var missing = hosts.Where(h => !existing.Contains(h.ToLowerInvariant())).ToList();
            if (missing.Count == 0) return;
            if (!System.IO.File.Exists(path))
                System.IO.File.AppendAllText(path, header + Environment.NewLine, new System.Text.UTF8Encoding(false));
            System.IO.File.AppendAllLines(path, missing, new System.Text.UTF8Encoding(false));
            AppLog.Info($"[Photon] Добавлены домены Photon: {string.Join(", ", missing)}");
        }

        /// <summary>Меряет время TCP-подключения к узлам Photon (3 попытки, берём лучшее).</summary>
        public static async Task<List<PhotonRegionResult>> CheckAsync(CancellationToken ct = default)
        {
            var tasks = Regions.Select(r => CheckOneAsync(r.Name, r.Host, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results
                .OrderByDescending(r => r.Ok)
                .ThenBy(r => r.Ok ? r.Milliseconds : long.MaxValue)
                .ToList();
        }

        private static async Task<PhotonRegionResult> CheckOneAsync(string name, string host, CancellationToken ct)
        {
            long best = long.MaxValue;
            string error = "";
            for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
            {
                var started = DateTime.UtcNow;
                try
                {
                    using var client = new TcpClient();
                    var connect = client.ConnectAsync(host, 443);
                    var completed = await Task.WhenAny(connect, Task.Delay(4000, ct)).ConfigureAwait(false);
                    if (completed != connect)
                    {
                        ct.ThrowIfCancellationRequested();
                        error = "нет ответа";
                        continue;
                    }
                    await connect.ConfigureAwait(false);
                    var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                    if (ms < best) best = ms;
                }
                catch (SocketException ex)
                {
                    error = Short(ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = Short(ex.Message);
                }
                if (best < 120) break; // уже хороший результат — не тратим время на повторы
            }

            if (best == long.MaxValue)
                return new PhotonRegionResult { Name = name, Host = host, Ok = false, Details = error.Length > 0 ? error : "недоступен" };

            return new PhotonRegionResult { Name = name, Host = host, Ok = true, Milliseconds = best, Details = $"{host} · {best} мс" };
        }

        private static string Short(string text)
        {
            text ??= "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 60 ? text : text.Substring(0, 57) + "…";
        }

        /// <summary>Строка-подсказка: какой регион выбрать в игре и что это значит.</summary>
        public static string DescribeBest(IReadOnlyList<PhotonRegionResult>? results)
        {
            var reachable = (results ?? Array.Empty<PhotonRegionResult>()).Where(r => r.Ok).OrderBy(r => r.Milliseconds).ToList();
            if (reachable.Count == 0)
                return "Не удалось измерить ни один узел Photon — проверьте интернет и DNS.";

            var best = reachable[0];
            var europe = reachable.FirstOrDefault(r => r.Name == "Европа");
            if (europe == null)
                return $"Лучший регион: {best.Name} · {best.Milliseconds} мс. Европа не ответила — выбирайте её только если в игре пинг стабильный.";
            if (europe.Name == best.Name)
                return $"Лучший регион: Европа · {best.Milliseconds} мс. Выбирайте «Европа» в игре.";
            return $"Быстрее всего {best.Name} · {best.Milliseconds} мс, Европа отвечает {europe.Milliseconds} мс. " +
                   "Выбирайте регион с меньшим числом — цифры в игре могут быть завышены.";
        }
    }
}
