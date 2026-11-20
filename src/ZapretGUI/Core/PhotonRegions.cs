using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    /// <summary>Результат проверки одного региона Photon.</summary>
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
    /// v2.2.6 / v2.2.7: R.E.P.O. ходит через Photon relay, а не по P2P.
    ///
    /// Главная причина «clienttimeout» и пинга 800 мс на европейских серверах — не блокировка,
    /// а сам обход: диапазон адресов Photon (<c>216.120.180.0/23</c>) присутствует в
    /// <c>lists\ipset-all.txt</c> из сборки Flowseal. Любой профиль игрового фильтра с широким
    /// TCP-диапазоном (1024-65535) заставляет winws десинхронизировать трафик Photon по
    /// <c>--filter-tcp=… --ipset=ipset-all.txt --dpi-desync-any-protocol=1</c>. Игровой канал
    /// Photon после такого фрагментируется, ответы приходят с задержкой, а клиент Photon
    /// выкидывает игрока по таймауту.
    ///
    /// Поэтому адреса Photon выносятся в <c>lists\ipset-exclude-user.txt</c>: это исключение
    /// действует на ВСЕ ipset-блоки (и TCP, и UDP, любые порты) и не зависит от выбранного профиля.
    /// </summary>
    public static class PhotonRegions
    {
        /// <summary>Узлы Photon, которые реально существуют в DNS (проверено 2026-10).</summary>
        public static readonly IReadOnlyList<(string Name, string Host)> Regions = new List<(string, string)>
        {
            ("Европа", "ns-eu.photonengine.io"),
            ("США (восток)", "ns-us.photonengine.io"),
            ("США (запад)", "ns-usw.photonengine.io"),
            ("Япония", "ns-jp.photonengine.io"),
            ("По умолчанию", "ns.photonengine.io")
        };

        /// <summary>IP-диапазоны Photon (relay/name servers). Из этих сетей приходят все регионы.</summary>
        public static readonly string[] IpRanges = { "216.120.180.0/23" };

        /// <summary>Домены Photon/ExitGames: сюда же относятся легаси-хосты ExitGames.</summary>
        public static readonly string[] BypassHosts =
        {
            "photonengine.io",
            "photonengine.com",
            "exitgames.com",
            "exitgamescloud.com"
        };

        /// <summary>Порты игрового трафика Photon и Steam P2P — обход их трогать не должен.</summary>
        public static readonly string[] GamePorts = { "4530-4533", "5055-5058", "9090-9093", "27015-27200" };

        public const string IpsetExcludeFileName = "ipset-exclude-user.txt";
        private const string Marker = "# zapret-gui: Photon (R.E.P.O.) — управляется автоматически";

        /// <summary>Создаёт/обновляет исключения Photon и (опционально) маршрут доменов Photon.</summary>
        public static (bool Ok, string Message) Ensure(AppSettings? settings)
        {
            if (settings == null) return (false, "Настройки недоступны");
            if (string.IsNullOrWhiteSpace(settings.EnginePath))
                return (false, "Движок не установлен — списки пока некуда записать.");

            try
            {
                var lists = Path.Combine(settings.EnginePath, "lists");
                AppPaths.EnsureDir(lists);
                StrategyParser.EnsureUserLists(settings.EnginePath);

                var ipsetExclude = Path.Combine(lists, IpsetExcludeFileName);
                EnsureIpsetExclusions(ipsetExclude);

                var generalUser = Path.Combine(lists, "list-general-user.txt");
                var excludeUser = Path.Combine(lists, "list-exclude-user.txt");
                if (settings.PhotonThroughBypass)
                {
                    AddHosts(generalUser, BypassHosts, "# Пользовательские домены");
                    RemoveHosts(excludeUser, BypassHosts);
                }
                else
                {
                    RemoveHosts(generalUser, BypassHosts);
                    AddHosts(excludeUser, BypassHosts, "# Домены-исключения (не обрабатывать)");
                }

                var state = DescribeCoverage(settings);
                return (true, state);
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Photon] Не удалось обновить списки: " + ex.Message);
                return (false, "Не удалось обновить списки Photon: " + ex.Message);
            }
        }

        /// <summary>Добавляет диапазоны Photon в ipset-exclude-user.txt (идемпотентно).</summary>
        private static void EnsureIpsetExclusions(string path)
        {
            var existing = File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet()
                : new HashSet<string>();
            var missing = IpRanges.Where(r => !existing.Contains(r.ToLowerInvariant())).ToList();
            if (missing.Count == 0) return;

            if (!File.Exists(path))
                File.WriteAllText(path, Marker + Environment.NewLine, new System.Text.UTF8Encoding(false));
            else
                File.AppendAllText(path, Environment.NewLine + Marker + Environment.NewLine, new System.Text.UTF8Encoding(false));

            File.AppendAllLines(path, missing, new System.Text.UTF8Encoding(false));
            AppLog.Info($"[Photon] Диапазоны Photon исключены из ipset-фильтра: {string.Join(", ", missing)}");
        }

        /// <summary>Попадают ли порты игры в обход при выбранном профиле и режиме фильтра.</summary>
        public static bool PortsTouchedByBypass(AppSettings? settings)
        {
            if (settings == null) return false;
            var mode = EngineService.GetGameFilterMode(settings.EnginePath);
            if (mode == GameFilterMode.Disabled) return false;
            var tcp = GameFilterPortConfig.ResolveTcpPortString(mode, settings.GameFilterProfileId, settings.CustomGameFilterTcpPorts);
            var udp = GameFilterPortConfig.ResolveUdpPortString(mode, settings.GameFilterProfileId, settings.CustomGameFilterUdpPorts);
            // Профиль считается безопасным, если ни один игровой порт Photon не попал ни в TCP-, ни в UDP-диапазон.
            foreach (var range in GamePorts)
            {
                var (from, to) = ParseRange(range);
                for (var port = from; port <= to; port++)
                {
                    if (mode is GameFilterMode.TcpAndUdp or GameFilterMode.TcpOnly && PortInRanges(tcp, port)) return true;
                    if (mode is GameFilterMode.TcpAndUdp or GameFilterMode.UdpOnly && PortInRanges(udp, port)) return true;
                }
            }
            return false;
        }

        private static (int From, int To) ParseRange(string range)
        {
            var parts = range.Split('-');
            var from = int.TryParse(parts[0], out var a) ? a : 0;
            var to = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : from;
            return (from, to);
        }

        private static bool PortInRanges(string ranges, int port)
        {
            if (string.IsNullOrWhiteSpace(ranges)) return false;
            foreach (var part in ranges.Split(','))
            {
                var item = part.Trim();
                if (item.Length == 0) continue;
                var (from, to) = ParseRange(item);
                if (port >= from && port <= to) return true;
            }
            return false;
        }

        /// <summary>Проверяет, попадают ли адреса Photon в ipset обхода и исключены ли они.</summary>
        public static bool IpRangesExcluded(AppSettings? settings)
        {
            try
            {
                if (settings == null || string.IsNullOrWhiteSpace(settings.EnginePath)) return false;
                var path = Path.Combine(settings.EnginePath, "lists", IpsetExcludeFileName);
                if (!File.Exists(path)) return false;
                var lines = File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
                return IpRanges.All(r => lines.Contains(r.ToLowerInvariant()));
            }
            catch { return false; }
        }

        /// <summary>Есть ли диапазон Photon в ipset-all (то есть обход в принципе может его обработать).</summary>
        public static bool PhotonRangePresentInIpset(AppSettings? settings)
        {
            try
            {
                if (settings == null || string.IsNullOrWhiteSpace(settings.EnginePath)) return false;
                var path = Path.Combine(settings.EnginePath, "lists", "ipset-all.txt");
                if (!File.Exists(path)) return false;
                return File.ReadLines(path).Any(l => l.Trim().StartsWith("216.120.180.", StringComparison.Ordinal));
            }
            catch { return false; }
        }

        /// <summary>Короткая строка состояния для интерфейса: сначала про порты, потом про адреса Photon.</summary>
        public static string DescribeCoverage(AppSettings? settings)
        {
            if (settings == null) return "Настройки недоступны.";
            if (!IpRangesExcluded(settings))
            {
                if (PhotonRangePresentInIpset(settings))
                    return "⚠ Адреса Photon есть в ipset обхода и НЕ исключены — обход может ломать игру. Нажмите «Настроить R.E.P.O.».";
                return "Списки движка не найдены — исключения Photon появятся после установки движка.";
            }

            var mode = EngineService.GetGameFilterMode(settings.EnginePath);
            if (PortsTouchedByBypass(settings))
                return "⚠ Порты R.E.P.O. (4530-4533, 5055-5058, 9090-9093, 27015-27200) сейчас в обходе — " +
                       "выберите профиль «R.E.P.O.» или режим без UDP/без TCP.";
            if (mode == GameFilterMode.Disabled)
                return "Обход не трогает порты игр, адреса Photon исключены из ipset — R.E.P.O. идёт напрямую.";
            return "Адреса Photon исключены из обхода (216.120.180.0/23), игровые порты не в обходе — игра не должна ловить clienttimeout.";
        }

        /// <summary>
        /// Трогает ли обход трафик Photon прямо сейчас.
        ///
        /// Раньше проверялось только «диапазон есть в ipset-all и не исключён». Этого мало: при
        /// игровом режиме «Только TCP» обход работает по TCP-блокам, а адреса Photon из ipset
        /// как раз под них и попадают. Поэтому учитываем режим фильтра: при «Только UDP» TCP
        /// Photon не важен, при «TCP + UDP» и «Только TCP» — важен.
        /// </summary>
        public static bool BypassTouchesPhoton(AppSettings? settings)
        {
            if (settings == null || !PhotonRangePresentInIpset(settings) || IpRangesExcluded(settings)) return false;
            var mode = EngineService.GetGameFilterMode(settings.EnginePath);
            return mode is GameFilterMode.TcpAndUdp or GameFilterMode.TcpOnly;
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
            var notFound = false;
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
                        continue;
                    }
                    await connect.ConfigureAwait(false);
                    var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                    if (ms < best) best = ms;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
                {
                    // «Запрошенное имя верно, но данные запрошенного типа не найдены» — внятно объясняем.
                    notFound = true;
                    break;
                }
                catch (SocketException)
                {
                    // тайм-аут/отказ — просто следующая попытка
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }
                if (best < 120) break;
            }

            if (best != long.MaxValue)
                return new PhotonRegionResult { Name = name, Host = host, Ok = true, Milliseconds = best, Details = $"{host} · {best} мс" };
            return new PhotonRegionResult
            {
                Name = name,
                Host = host,
                Ok = false,
                Details = notFound ? "узла нет в DNS" : "нет ответа"
            };
        }

        private static void RemoveHosts(string path, IEnumerable<string> hosts)
        {
            if (!File.Exists(path)) return;
            var drop = hosts.Select(h => h.ToLowerInvariant()).ToHashSet();
            var lines = File.ReadAllLines(path);
            var kept = lines.Where(l =>
            {
                var value = l.Trim().ToLowerInvariant();
                return value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal) || !drop.Contains(value);
            }).ToList();
            if (kept.Count == lines.Length) return;
            File.WriteAllLines(path, kept, new System.Text.UTF8Encoding(false));
            AppLog.Info($"[Photon] Убраны домены Photon из {Path.GetFileName(path)}");
        }

        private static void AddHosts(string path, IEnumerable<string> hosts, string header)
        {
            var existing = File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).ToHashSet()
                : new HashSet<string>();
            var missing = hosts.Where(h => !existing.Contains(h.ToLowerInvariant())).ToList();
            if (missing.Count == 0) return;
            if (!File.Exists(path))
                File.AppendAllText(path, header + Environment.NewLine, new System.Text.UTF8Encoding(false));
            File.AppendAllLines(path, missing, new System.Text.UTF8Encoding(false));
            AppLog.Info($"[Photon] Добавлены домены Photon: {string.Join(", ", missing)}");
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
