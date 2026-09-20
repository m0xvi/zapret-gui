using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace ZapretGui.Core
{
    /// <summary>Описание стратегии обхода (один .bat файл из папки движка).</summary>
    public enum StrategyTestState { NotTested, Testing, Passed, Failed }

    public sealed class StrategyInfo : INotifyPropertyChanged
    {
        private StrategyTestState _testState;
        private string _testStatusText = "не проверено";
        private string _testStatusKey = "Muted";
        private StrategyTestResult? _testResult;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";

        /// <summary>Аргументы winws.exe. Плейсхолдеры %BIN%/%LISTS% уже раскрыты,
        /// {GameFilterTCP}/{GameFilterUDP} подставляются при запуске.</summary>
        public List<string> Args { get; set; } = new();

        public string Category { get; set; } = "БАЗОВАЯ";

        private bool _isRecommended;
        public bool IsRecommended
        {
            get => _isRecommended || TestResult?.IsSuitable == true || TestState == StrategyTestState.Passed;
            set
            {
                if (_isRecommended == value) return;
                _isRecommended = value;
                Raise();
            }
        }

        public bool UsesFakeTls { get; set; }
        public bool UsesFakeQuic { get; set; }
        public bool UsesSplit { get; set; }
        public bool UsesGameFilter { get; set; }

        /// <summary>Читаемое описание того, что делает стратегия.</summary>
        public string Description { get; set; } = "";

        public string ShortArgs => Args.Count == 0 ? "" : string.Join(" ", Args);

        public string ArgsPreview
        {
            get
            {
                var text = ShortArgs;
                return text.Length > 400 ? text.Substring(0, 400) + " …" : text;
            }
        }

        public StrategyTestState TestState
        {
            get => _testState;
            private set
            {
                if (_testState == value) return;
                _testState = value;
                Raise();
                Raise(nameof(IsTesting));
            }
        }

        public bool IsTesting => TestState == StrategyTestState.Testing;
        public string TestStatusText
        {
            get => _testStatusText;
            private set
            {
                if (_testStatusText == value) return;
                _testStatusText = value;
                Raise();
            }
        }

        public string TestStatusKey
        {
            get => _testStatusKey;
            private set
            {
                if (_testStatusKey == value) return;
                _testStatusKey = value;
                Raise();
            }
        }

        public StrategyTestResult? TestResult => _testResult;

        public void SetTestStarted()
        {
            _testResult = null;
            TestState = StrategyTestState.Testing;
            TestStatusText = "проверяю…";
            TestStatusKey = "Warning";
            Raise(nameof(TestResult));
        }

        public void RefreshTheme()
        {
            Raise(nameof(TestStatusKey));
        }

        public void SetTestResult(StrategyTestResult result)
        {
            _testResult = result;
            TestState = result.IsSuitable ? StrategyTestState.Passed : StrategyTestState.Failed;
            TestStatusText = result.Started
                ? $"{result.PassedCount}/{result.Checks.Count} · {result.ElapsedText}"
                : "не запустилась";
            TestStatusKey = result.IsSuitable ? "Success" : result.Started && result.PassedCount > 0 ? "Warning" : "Danger";
            Raise(nameof(TestResult));
            Raise(nameof(IsRecommended));
        }

        private void Raise([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Разбирает .bat файлы со стратегиями ровно так же, как это делает service.bat:
    /// берёт строку с winws.exe, склеивает продолжения по "^^" и вытаскивает аргументы.
    /// Благодаря этому новые стратегии из официального репозитория появляются в GUI автоматически.
    /// </summary>
    public static class StrategyParser
    {
        public const string GameFilterTcpToken = "{GameFilterTCP}";
        public const string GameFilterUdpToken = "{GameFilterUDP}";

        public static List<StrategyInfo> LoadAll(string engineRoot)
        {
            var result = new List<StrategyInfo>();
            if (!Directory.Exists(engineRoot)) return result;

            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(engineRoot, "*.bat", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось прочитать папку стратегий: " + ex.Message);
                return result;
            }

            var ordered = files
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileNameWithoutExtension(f), NaturalStringComparer.Instance)
                .ToList();

            foreach (var file in ordered)
            {
                var strategy = Parse(file, engineRoot);
                if (strategy != null) result.Add(strategy);
            }
            return result;
        }

        public static StrategyInfo? Parse(string batPath, string engineRoot)
        {
            try
            {
                var lines = File.ReadAllLines(batPath, DetectEncoding(batPath));
                var command = ExtractCommand(lines);
                if (string.IsNullOrWhiteSpace(command)) return null;

                var tokens = Tokenize(command);
                var bin = Path.Combine(engineRoot, "bin") + Path.DirectorySeparatorChar;
                var lists = Path.Combine(engineRoot, "lists") + Path.DirectorySeparatorChar;

                var args = new List<string>();
                foreach (var token in tokens)
                {
                    var expanded = token
                        .Replace("%~dp0", engineRoot + Path.DirectorySeparatorChar)
                        .Replace("%BIN%", bin)
                        .Replace("%LISTS%", lists)
                        .Replace("%GameFilterTCP%", GameFilterTcpToken)
                        .Replace("%GameFilterUDP%", GameFilterUdpToken);

                    // %GameFilter% в некоторых сборках = "оба протокола"
                    if (expanded.Contains("%GameFilter%"))
                        expanded = expanded.Replace("%GameFilter%", GameFilterTcpToken + "," + GameFilterUdpToken);

                    if (expanded.StartsWith("--", StringComparison.Ordinal) || !expanded.Contains("%"))
                        args.Add(expanded);
                    else
                        args.Add(expanded); // неизвестная переменная — оставляем как есть, winws сообщит об ошибке
                }

                var name = Path.GetFileNameWithoutExtension(batPath);
                var info = new StrategyInfo
                {
                    Name = name,
                    FileName = Path.GetFileName(batPath),
                    FullPath = batPath,
                    Args = args,
                    Category = DetectCategory(name)
                };

                Analyse(info);
                return info;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось разобрать стратегию {Path.GetFileName(batPath)}: {ex.Message}");
                return null;
            }
        }

        private static Encoding DetectEncoding(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return new UTF8Encoding(true);
            }
            catch { }
            return new UTF8Encoding(false);
        }

        /// <summary>Склеивает строку с winws.exe и её продолжения (символ ^ в конце строки).</summary>
        private static string? ExtractCommand(string[] lines)
        {
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0) return null;

            var sb = new StringBuilder();
            for (int i = start; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                bool cont = line.EndsWith("^", StringComparison.Ordinal);
                if (cont) line = line.Substring(0, line.Length - 1);
                sb.Append(line).Append(' ');
                if (!cont && i > start) break;
                if (!cont && i == start)
                {
                    // однострочный запуск — продолжаем, если следующая строка начинается с "--"
                    if (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("--", StringComparison.Ordinal)) continue;
                    break;
                }
            }

            var cmd = sb.ToString();
            var idx = cmd.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            cmd = cmd.Substring(idx + "winws.exe".Length).TrimStart();

            // В строке запуска путь к winws.exe заключён в кавычки:
            // start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=...
            // поэтому закрывающую кавычку и возможные экранирования в начале нужно убрать,
            // иначе токенизатор посчитает её открывающей и склеит всю команду в один аргумент.
            while (cmd.Length > 0 && (cmd[0] == '"' || cmd[0] == '^'))
                cmd = cmd.Substring(1).TrimStart();

            return cmd;
        }

        /// <summary>Токенизация командной строки cmd.exe (кавычки и экранирование ^).</summary>
        public static List<string> Tokenize(string command)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];

                if (c == '^' && i + 1 < command.Length && "^!\"&|<>()".IndexOf(command[i + 1]) >= 0)
                {
                    sb.Append(command[i + 1]);
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }

                sb.Append(c);
            }

            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        private static string DetectCategory(string name)
        {
            if (name.Contains("FAKE TLS AUTO", StringComparison.OrdinalIgnoreCase)) return "FAKE TLS AUTO";
            if (name.Contains("SIMPLE FAKE", StringComparison.OrdinalIgnoreCase)) return "SIMPLE FAKE";
            if (name.Contains("EXP", StringComparison.OrdinalIgnoreCase)) return "EXP";
            if (name.Contains("(ALT", StringComparison.OrdinalIgnoreCase)) return "ALT";
            return "БАЗОВАЯ";
        }

        private static void Analyse(StrategyInfo info)
        {
            var joined = string.Join(" ", info.Args);
            info.UsesFakeTls = joined.Contains("dpi-desync-fake-tls", StringComparison.OrdinalIgnoreCase);
            info.UsesFakeQuic = joined.Contains("dpi-desync-fake-quic", StringComparison.OrdinalIgnoreCase);
            info.UsesSplit = joined.Contains("multisplit", StringComparison.OrdinalIgnoreCase)
                             || joined.Contains("multidisorder", StringComparison.OrdinalIgnoreCase)
                             || joined.Contains("split-pos", StringComparison.OrdinalIgnoreCase);
            info.UsesGameFilter = joined.Contains(GameFilterTcpToken) || joined.Contains(GameFilterUdpToken);

            var parts = new List<string>();

            var desync = ExtractValue(info.Args, "--dpi-desync=");
            if (!string.IsNullOrEmpty(desync))
            {
                if (desync.Contains("fake")) parts.Add("подмена пакетов (fake)");
                if (desync.Contains("multisplit")) parts.Add("multisplit");
                if (desync.Contains("multidisorder")) parts.Add("multidisorder");
                if (desync.Contains("split2")) parts.Add("split2");
            }

            var sni = ExtractSubValue(info.Args, "--dpi-desync-fake-tls-mod=", "sni=");
            if (!string.IsNullOrEmpty(sni)) parts.Add("фейк TLS с SNI " + sni);
            else if (info.UsesFakeTls) parts.Add("фейк TLS");

            if (info.UsesFakeQuic) parts.Add("фейк QUIC (UDP 443)");

            var splitPos = ExtractValue(info.Args, "--dpi-desync-split-pos=");
            if (!string.IsNullOrEmpty(splitPos)) parts.Add("split-pos " + splitPos);

            var repeats = ExtractValue(info.Args, "--dpi-desync-repeats=");
            if (!string.IsNullOrEmpty(repeats)) parts.Add("повторы " + repeats);

            var fooling = ExtractValue(info.Args, "--dpi-desync-fooling=");
            if (!string.IsNullOrEmpty(fooling)) parts.Add("fooling " + fooling);

            if (joined.Contains("--dpi-desync-any-protocol=1", StringComparison.OrdinalIgnoreCase))
                parts.Add("все протоколы (игры)");

            if (joined.Contains("--ip-id=zero", StringComparison.OrdinalIgnoreCase))
                parts.Add("ip-id=zero");

            if (joined.Contains("discord.media", StringComparison.OrdinalIgnoreCase))
                parts.Add("Discord (голос/медиа)");

            info.Description = parts.Count > 0
                ? string.Join(" · ", parts.Distinct())
                : "Не удалось определить параметры стратегии";
        }

        private static string? ExtractValue(IEnumerable<string> args, string prefix)
        {
            foreach (var a in args)
            {
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length);
            }
            return null;
        }

        private static string? ExtractSubValue(IEnumerable<string> args, string prefix, string subKey)
        {
            var value = ExtractValue(args, prefix);
            if (string.IsNullOrEmpty(value)) return null;
            foreach (var part in value.Split(','))
            {
                if (part.StartsWith(subKey, StringComparison.OrdinalIgnoreCase))
                    return part.Substring(subKey.Length);
            }
            return null;
        }

        /// <summary>Создаёт пользовательские списки, если их нет (аналог load_user_lists из service.bat).</summary>
        public static void EnsureUserLists(string engineRoot)
        {
            try
            {
                var lists = Path.Combine(engineRoot, "lists");
                AppPaths.EnsureDir(lists);

                var ipsetExcludeUser = Path.Combine(lists, "ipset-exclude-user.txt");
                if (!File.Exists(ipsetExcludeUser)) File.WriteAllText(ipsetExcludeUser, "203.0.113.113/32" + Environment.NewLine);

                var listGeneralUser = Path.Combine(lists, "list-general-user.txt");
                if (!File.Exists(listGeneralUser))
                    File.WriteAllText(listGeneralUser,
                        "# Никогда не оставляйте этот файл пустым" + Environment.NewLine +
                        "# Добавляйте сюда свои домены (по одному в строке)" + Environment.NewLine +
                        "domain.example.abc" + Environment.NewLine);

                var listExcludeUser = Path.Combine(lists, "list-exclude-user.txt");
                if (!File.Exists(listExcludeUser))
                    File.WriteAllText(listExcludeUser,
                        "# Домены-исключения (не обрабатывать)" + Environment.NewLine +
                        "domain.example.abc" + Environment.NewLine);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось создать пользовательские списки: " + ex.Message);
            }
        }
    }

    /// <summary>Сортировка "по-человечески": general (ALT2) идёт раньше general (ALT10).</summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var nx = long.Parse(x.Substring(si, i - si));
                    var ny = long.Parse(y.Substring(sj, j - sj));
                    if (nx != ny) return nx.CompareTo(ny);
                }
                else
                {
                    int cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
