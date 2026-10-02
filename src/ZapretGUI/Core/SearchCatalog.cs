using System;
using System.Collections.Generic;
using System.Linq;

namespace ZapretGui.Core
{
    /// <summary>Тип пункта поискового индекса (этап 7, docs/IA_REDESIGN.md §10.4).</summary>
    public enum SearchKind
    {
        /// <summary>Раздел верхнего уровня или «Помощь» в подвале меню.</summary>
        Section,

        /// <summary>Подраздел внутри раздела (вкладка).</summary>
        Subsection,

        /// <summary>Настройка или отдельный экран настроек.</summary>
        Setting,

        /// <summary>Сценарий «Помощи».</summary>
        Scenario,

        /// <summary>Команда — выполняется сразу, без перехода.</summary>
        Command
    }

    /// <summary>
    /// Один пункт поискового индекса. Пользовательское имя обязательно, `Aliases` — старые термины
    /// и синонимы: «стратегия»/«способ обхода», `watchdog`, `бесшовное`, `матрица`, `SNI`, `DPI`,
    /// `Voice RTC`, `Deep Check`, `аудит`, `бэкап`, `SSID`. Требование §10.4: опытный пользователь
    /// вводит привычное слово и попадает в новое место.
    /// </summary>
    public sealed class SearchEntry
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";

        /// <summary>Человеческий путь: «Проверки → Журнал». Для команд пусто.</summary>
        public string Path { get; init; } = "";

        public SearchKind Kind { get; init; }

        /// <summary>Глиф Segoe MDL2; у подразделов — глиф родительского раздела.</summary>
        public string Icon { get; init; } = "";

        public string[] Aliases { get; init; } = Array.Empty<string>();

        /// <summary>Пункт ведёт в экспертный подраздел: в «Простом» режиме откроется видимая часть раздела.</summary>
        public bool ExpertOnly { get; init; }

        public string KindText => Kind switch
        {
            SearchKind.Section => "Раздел",
            SearchKind.Subsection => "Подраздел",
            SearchKind.Setting => "Настройка",
            SearchKind.Scenario => "Сценарий",
            _ => "Команда"
        };

        /// <summary>Строка-подпись под заголовком: «Подраздел · Проверки → Журнал».</summary>
        public string Meta => string.IsNullOrEmpty(Path) ? KindText : $"{KindText} · {Path}";
    }

    /// <summary>
    /// Поиск по индексу: нормализация, скоринг и порядок выдачи — без UI и без знания о ViewModel.
    ///
    /// Условие совпадения: каждое слово запроса должно найтись в названии, пути или алиасах пункта
    /// (порядок слов не важен). Выше в выдаче — точное начало названия, затем алиасы (старые термины),
    /// затем вхождения внутри слов.
    /// </summary>
    public static class SearchCatalog
    {
        public const int ResultLimit = 12;

        /// <summary>Приводит строку к сравнимому виду: нижний регистр, «ё» → «е», без пунктуации.</summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            var chars = new List<char>(value.Length);
            foreach (var raw in value.Trim().ToLowerInvariant())
            {
                var ch = raw == 'ё' ? 'е' : raw;
                if (char.IsLetterOrDigit(ch)) chars.Add(ch);
                else if (ch is ' ' or '-' or '+' or '/' or '→') chars.Add(' ');
            }

            var normalized = new string(chars.ToArray());
            while (normalized.Contains("  ", StringComparison.Ordinal))
                normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
            return normalized.Trim();
        }

        public static string[] Tokenize(string? query) =>
            Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Сколько «стоит» слово запроса для этого пункта; 0 — слово не найдено.</summary>
        private static int TokenScore(SearchEntry entry, string token)
        {
            var title = Normalize(entry.Title);
            var path = Normalize(entry.Path);

            if (title.StartsWith(token, StringComparison.Ordinal)) return 120;
            if (HasWordStart(title, token)) return 90;
            if (title.Contains(token, StringComparison.Ordinal)) return 70;

            var best = 0;
            foreach (var alias in entry.Aliases)
            {
                var normalized = Normalize(alias);
                if (normalized.Length == 0) continue;
                if (normalized == token) best = Math.Max(best, 110);
                else if (normalized.StartsWith(token, StringComparison.Ordinal)) best = Math.Max(best, 80);
                else if (HasWordStart(normalized, token)) best = Math.Max(best, 60);
                else if (normalized.Contains(token, StringComparison.Ordinal)) best = Math.Max(best, 45);
            }
            if (best > 0) return best;

            if (path.Contains(token, StringComparison.Ordinal)) return 35;
            return 0;
        }

        private static bool HasWordStart(string text, string token)
        {
            var index = text.IndexOf(token, StringComparison.Ordinal);
            while (index >= 0)
            {
                if (index == 0 || text[index - 1] == ' ') return true;
                index = text.IndexOf(token, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>Оценка пункта по всему запросу; 0 — пункт не подходит.</summary>
        public static int Score(SearchEntry entry, string? query)
        {
            var tokens = Tokenize(query);
            if (tokens.Length == 0) return 0;

            var total = 0;
            foreach (var token in tokens)
            {
                var score = TokenScore(entry, token);
                if (score == 0) return 0;   // совпадение по всем словам запроса
                total += score;
            }
            return total;
        }

        /// <summary>Выдача: сначала лучшие совпадения, при равенстве — команды и подразделы, затем по алфавиту.</summary>
        public static List<SearchEntry> Search(IEnumerable<SearchEntry> entries, string? query, int limit = ResultLimit)
        {
            return entries
                .Select(entry => new { Entry = entry, Score = Score(entry, query) })
                .Where(pair => pair.Score > 0)
                .OrderByDescending(pair => pair.Score)
                .ThenByDescending(pair => pair.Entry.Kind == SearchKind.Command)
                .ThenBy(pair => pair.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => pair.Entry)
                .Take(limit)
                .ToList();
        }
    }
}
