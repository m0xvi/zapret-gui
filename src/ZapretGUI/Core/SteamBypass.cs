using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZapretGui.Core
{
    /// <summary>
    /// v2.2.5: медленные страницы Steam (магазин, сообщество, профили).
    ///
    /// Причина: в сборке Flowseal домены Steam намеренно убраны из <c>list-general</c>,
    /// чтобы обход не трогал загрузки игр. Из-за этого HTML-страницы магазина
    /// (<c>store.steampowered.com</c>, <c>steamcommunity.com</c>, <c>api.steampowered.com</c>)
    /// идут напрямую и попадают под DPI-замедление — страница «загружается, но долго».
    ///
    /// Решение: свой маленький список для магазина/сообщества, который подмешивается в тот же
    /// TCP-блок обхода, что уже работает для остальных сайтов, плюс явные исключения для
    /// CDN и загрузок (<c>steamstatic.com</c>, <c>steamcontent.com</c>, …), чтобы скорость
    /// скачивания игр не пострадала.
    /// </summary>
    public static class SteamBypass
    {
        /// <summary>Хосты, которые нужно вести через обход (магазин, сообщество, авторизация, API).</summary>
        public static readonly string[] BypassHosts =
        {
            "steampowered.com",
            "steamcommunity.com"
        };

        /// <summary>Хосты, которые обход не должен трогать: картинки, видео и загрузки игр.</summary>
        public static readonly string[] ExcludedHosts =
        {
            "steamstatic.com",
            "steamcontent.com",
            "steamcommunity-a.akamaihd.net",
            "steamcdn-a.akamaihd.net",
            "steamcloud-eu.akamaihd.net",
            "steamserver.net",
            "media.steampowered.com"
        };

        public const string BypassFileName = "steam-bypass.txt";
        public const string ExcludeFileName = "steam-bypass-exclude.txt";
        private const string Marker = "# zapret-gui: управляется автоматически (раздел «Игровой режим»)";

        public static string BypassPath(string enginePath) => Path.Combine(enginePath ?? "", "lists", BypassFileName);
        public static string ExcludePath(string enginePath) => Path.Combine(enginePath ?? "", "lists", ExcludeFileName);

        /// <summary>Короткое описание состояния для карточки в интерфейсе.</summary>
        public static string Describe(AppSettings settings)
            => settings.SteamBypassEnabled
                ? "Магазин и сообщество идут через обход, загрузки и картинки — напрямую."
                : "Выключено: страницы Steam идут напрямую, без обхода (могут грузиться медленно).";

        /// <summary>
        /// Создаёт/обновляет списки Steam и чистит их от лишних исключений.
        /// Вызывается при старте, перед запуском обхода и при переключении тумблера.
        /// </summary>
        public static (bool Ok, string Message) Ensure(AppSettings settings)
        {
            if (settings == null) return (false, "Настройки недоступны");
            if (string.IsNullOrWhiteSpace(settings.EnginePath))
                return (false, "Движок не установлен — списки Steam некуда положить.");

            var lists = Path.Combine(settings.EnginePath, "lists");
            try
            {
                AppPaths.EnsureDir(lists);
                var bypassPath = Path.Combine(lists, BypassFileName);
                var excludePath = Path.Combine(lists, ExcludeFileName);

                if (!settings.SteamBypassEnabled)
                {
                    // Выключили — убираем подмешиваемые списки, чтобы обход их больше не читал.
                    TryDelete(bypassPath);
                    TryDelete(excludePath);
                    return (true, "Обход Steam выключен.");
                }

                StrategyParser.EnsureUserLists(settings.EnginePath);

                WriteManagedFile(bypassPath, BypassHosts);
                WriteManagedFile(excludePath, ExcludedHosts);

                // Ключевой момент: пользовательский exclude мог уже содержать steampowered.com
                // (пресет Phasmophobia/Steam), из-за чего магазин оставался без обхода.
                // А CDN/загрузки, наоборот, должны исключаться всегда — иначе падают скорости.
                var excludeUser = Path.Combine(lists, "list-exclude-user.txt");
                RemoveHosts(excludeUser, BypassHosts);
                AddHosts(excludeUser, ExcludedHosts);

                AppLog.Info($"[Steam] Списки обхода магазина обновлены: {BypassHosts.Length} хостов, " +
                            $"{ExcludedHosts.Length} исключений");
                return (true, "Steam: страницы через обход, загрузки — напрямую.");
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Steam] Не удалось подготовить списки: " + ex.Message);
                return (false, "Не удалось подготовить списки Steam: " + ex.Message);
            }
        }

        /// <summary>
        /// Подмешивает списки Steam в первый TCP-блок обхода, который уже фильтрует по hostlist.
        /// Так используется та же (уже рабочая) схема обхода, что и для остальных сайтов,
        /// и не нужно угадывать параметры desync. Повторный вызов ничего не дублирует.
        /// </summary>
        public static List<string> ApplyToArgs(IReadOnlyList<string> args, string enginePath)
        {
            var result = new List<string>(args ?? Array.Empty<string>());
            if (result.Count == 0 || string.IsNullOrWhiteSpace(enginePath)) return result;

            var lists = Path.Combine(enginePath, "lists");
            var bypassPath = Path.Combine(lists, BypassFileName);
            var excludePath = Path.Combine(lists, ExcludeFileName);

            if (result.Any(a => a.IndexOf(BypassFileName, StringComparison.OrdinalIgnoreCase) >= 0))
                return result;

            var insertAt = FindTcpHostlistBlockEnd(result);
            if (insertAt < 0)
            {
                AppLog.Warn("[Steam] В стратегии нет TCP-блока с hostlist — списки Steam не подмешаны");
                return result;
            }

            result.Insert(insertAt, $"--hostlist-exclude={excludePath}");
            result.Insert(insertAt, $"--hostlist={bypassPath}");
            return result;
        }

        /// <summary>
        /// Конец TCP-блока, который фильтрует по файлу <c>--hostlist=</c> (или -1).
        /// Предпочитаем блок с «общим» списком (list-general) и портами 80/443 —
        /// именно он обрабатывает обычные сайты, значит и магазин Steam обработает так же.
        /// </summary>
        private static int FindTcpHostlistBlockEnd(IReadOnlyList<string> args)
        {
            var bestIndex = -1;
            var bestScore = -1;
            var blockStart = 0;
            for (var i = 0; i <= args.Count; i++)
            {
                var isEnd = i == args.Count || string.Equals(args[i], "--new", StringComparison.Ordinal);
                if (!isEnd) continue;

                var hasTcp = false;
                var hasHostlist = false;
                var score = 0;
                for (var j = blockStart; j < i; j++)
                {
                    var arg = args[j];
                    if (arg.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTcp = true;
                        if (arg.Contains("443", StringComparison.Ordinal)) score += 2;
                        if (arg.Contains("80", StringComparison.Ordinal)) score += 1;
                    }
                    // hostlist-domains — это inline-список, нам нужен файл (--hostlist=)
                    else if (arg.StartsWith("--hostlist=", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHostlist = true;
                        if (arg.Contains("general", StringComparison.OrdinalIgnoreCase)) score += 3;
                    }
                }
                if (hasTcp && hasHostlist && score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
                blockStart = i + 1;
            }
            return bestIndex;
        }

        private static void WriteManagedFile(string path, IEnumerable<string> hosts)
        {
            var content = Marker + Environment.NewLine +
                          string.Join(Environment.NewLine, hosts) + Environment.NewLine;
            try
            {
                if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content) return;
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Steam] Не удалось записать {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static void RemoveHosts(string path, IEnumerable<string> hosts)
        {
            if (!File.Exists(path)) return;
            var drop = hosts.Select(h => h.ToLowerInvariant()).ToHashSet();
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var kept = lines.Where(l =>
            {
                var value = l.Trim().ToLowerInvariant();
                return value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal) || !drop.Contains(value);
            }).ToList();

            if (kept.Count == lines.Length) return;
            File.WriteAllLines(path, kept, new UTF8Encoding(false));
            AppLog.Info($"[Steam] Убраны лишние исключения из {Path.GetFileName(path)}");
        }

        private static void AddHosts(string path, IEnumerable<string> hosts)
        {
            var existing = File.Exists(path)
                ? File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim().ToLowerInvariant()).ToHashSet()
                : new HashSet<string>();
            var missing = hosts.Where(h => !existing.Contains(h.ToLowerInvariant())).ToList();
            if (missing.Count == 0) return;
            if (!File.Exists(path))
                File.AppendAllText(path, "# Домены-исключения (не обрабатывать)" + Environment.NewLine, new UTF8Encoding(false));
            File.AppendAllLines(path, missing, new UTF8Encoding(false));
            AppLog.Info($"[Steam] Добавлены исключения CDN: {string.Join(", ", missing)}");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
