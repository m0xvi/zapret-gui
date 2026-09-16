using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZapretGui.Core;

namespace StrategyParserHarness
{
    /// <summary>
    /// Проверяет разбор стратегий на реальных .bat-файлах движка zapret.
    /// Ищет: нераскрытые плейсхолдеры (%BIN%, %LISTS%, %~dp0%, %GameFilterTCP%),
    /// остатки экранирования (^) и кавычки внутри аргументов.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var engineRoot = args.Length > 0 ? args[0] : "/tmp/engine";

            if (!Directory.Exists(engineRoot))
            {
                Console.Error.WriteLine($"Папка движка не найдена: {engineRoot}");
                Console.Error.WriteLine("Скачайте релиз zapret-discord-youtube, распакуйте и передайте путь аргументом.");
                return 2;
            }

            var strategies = StrategyParser.LoadAll(engineRoot);
            var batCount = Directory.GetFiles(engineRoot, "*.bat", SearchOption.TopDirectoryOnly).Length;

            Console.WriteLine($"Папка движка: {engineRoot}");
            Console.WriteLine($"Файлов .bat: {batCount} (из них service*.bat исключаются)");
            Console.WriteLine($"Разобрано стратегий: {strategies.Count}");
            Console.WriteLine();

            var problems = new List<string>();

            foreach (var strategy in strategies)
            {
                Console.WriteLine($"=== {strategy.Name}  [{strategy.Category}]" +
                                  (strategy.IsRecommended ? "  ★" : ""));
                Console.WriteLine($"    {strategy.Description}");
                Console.WriteLine($"    аргументов: {strategy.Args.Count}" +
                                  (strategy.UsesGameFilter ? "  (использует GameFilter)" : ""));

                if (strategy.Args.Count == 0)
                    problems.Add($"{strategy.Name}: аргументы не извлечены");
                if (strategy.Args.Count < 10)
                    problems.Add($"{strategy.Name}: подозрительно мало аргументов ({strategy.Args.Count})");

                foreach (var argument in strategy.Args)
                {
                    if (argument.Contains("%BIN%") || argument.Contains("%LISTS%") ||
                        argument.Contains("%~dp0") || argument.Contains("%GameFilter"))
                        problems.Add($"{strategy.Name}: не раскрыт плейсхолдер: {argument}");

                    if (argument.StartsWith("^", StringComparison.Ordinal) ||
                        argument.EndsWith("^", StringComparison.Ordinal))
                        problems.Add($"{strategy.Name}: остался символ экранирования: {argument}");

                    if (argument.Contains('"'))
                        problems.Add($"{strategy.Name}: осталась кавычка: {argument}");
                }
            }

            // Эвристика: рабочие стратегии содержат --dpi-desync и --wf-tcp/--wf-udp
            foreach (var strategy in strategies.Where(s => s.Args.Count > 0))
            {
                var joined = string.Join(" ", strategy.Args);
                if (!joined.Contains("--dpi-desync", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{strategy.Name}: нет ни одного --dpi-desync*");
            }

            Console.WriteLine();
            Console.WriteLine("Список имён стратегий:");
            Console.WriteLine("  " + string.Join(" | ", strategies.Select(s => s.Name)));

            Console.WriteLine();
            if (problems.Count == 0)
            {
                Console.WriteLine("✓ Проблем не найдено: разбор корректен, плейсхолдеры раскрыты.");
                Console.WriteLine("  Ожидаемые показатели для релиза 1.10.2: 22 стратегии, 82–101 аргумент.");
                return 0;
            }

            Console.WriteLine($"✗ Найдено проблем: {problems.Count}");
            foreach (var problem in problems.Take(40)) Console.WriteLine("  - " + problem);
            if (problems.Count > 40) Console.WriteLine($"  … и ещё {problems.Count - 40}");
            return 1;
        }
    }
}
