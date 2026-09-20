using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZapretGui.Core;

namespace ZapretGui.CoreLogicHarness
{
    internal static class Program
    {
        private static int Main()
        {
            var checks = new (string Name, Action Test)[]
            {
                ("Домен превращается в HTTPS URL", DomainBecomesHttps),
                ("URL сохраняет порт и хост", UrlKeepsPort),
                ("Неподдерживаемая схема отклоняется", UnsupportedSchemeRejected),
                ("Пустой адрес отклоняется", EmptyAddressRejected),
                ("Встроенная цель помечается как встроенная", BuiltInIsMarked),
                ("Версии сравниваются численно", VersionsCompareNumerically),
                ("Признаки стратегии нормализуются", StrategyFeaturesAreNormalized),
                ("StrategyParser сохраняет контракт .bat и плейсхолдеры", StrategyParserKeepsBatContract),
                ("Аргументы обхода корректно раскрывают game filter", BypassArgumentsKeepGameFilter),
                ("Контекст провайдера не подменяется endpoint-ом", ProviderContextAndCandidateAreSafe),
                ("Генератор ограничивает варианты и не меняет основу", CandidateGeneratorIsBounded),
                ("Score кандидата учитывает повторяемость", CandidateEvaluationScoresStability),
                ("История кандидата сохраняет признаки и результаты", CandidateHistoryKeepsCatalogData),
                ("История провайдера влияет только на порядок эвристики", ProviderHistoryRanksSources),
                ("Откат движка восстанавливает только файлы резервной копии", EngineRollbackIsSafe),
                ("Заблокированный WinDivert пропускается с предупреждением", LockedDriverIsSkippedSafely),
                ("Updater GUI принимает только безопасный репозиторий", GuiRepositoryValidationIsStrict),
                ("Updater GUI выбирает только проверяемый portable EXE", GuiPortableAssetSelectionIsStrict),
                ("Согласованность движка отмечает драйвер, списки и стратегии", EngineConsistencyIsChecked),
                ("Разбор состояния службы устойчив к языку Windows", ServiceStateParsingIsLocalized),
                ("Безопасная остановка служб тестируется без реального обхода", SafeServiceStopIsIsolated),
                ("Кэш состояния служб различает актуальные и остаточные службы", ServiceHealthCacheIsExplicit),
                ("Режимы ipset сохраняют и восстанавливают список", IpsetModesRoundTrip),
                ("Объединение hosts сохраняет пользовательские строки", HostsMergeKeepsUserLines),
                ("Готовность движка проверяется по обязательным файлам", EngineReadinessIsChecked),
                ("Готовность требует явного завершения мастера", ReadinessRequiresWizard),
                ("Безопасный режим не меняет готовность и отключает автозапуск", SafeModeKeepsReadinessExplicit),
                ("Отмена DPI-проверки не запускает сетевые пробы", DpiCancellationIsPrompt),
                ("Снимок DPI сохраняет этапы, контрольный endpoint и повторы", DpiSnapshotKeepsStages),
                ("Контракт IsSuitable сохраняет совместимость с двухресурсным smoke", StrategySuitabilitySmokeContract),
                ("DPI-score и классификация freeze соответствуют формуле", DpiProbeScoreAndFreezeClassification),
                ("Score кандидата приоритизирует эмпирические результаты проверок", EmpiricalCandidateEvaluationScoreOrdering),
                ("Диагностический отчёт сериализуется вместе с журналом восстановления", DiagnosticsReportRoundTrips),
                ("Списки доменов автоматически наполняются эталонными записями", DomainListsAutoSeedingWorks),
                ("Менеджер фейковых бинарных нагрузок и Voice RTC эндпоинты работают", VoiceRtcProberAndFakeBinManagerWork)
            };

            foreach (var check in checks)
            {
                try
                {
                    check.Test();
                    Console.WriteLine("✓ " + check.Name);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("✗ " + check.Name + ": " + ex.Message);
                    return 1;
                }
            }

            Console.WriteLine("Все smoke-проверки чистой логики прошли.");
            return 0;
        }

        private static void DomainBecomesHttps()
        {
            Assert(MonitorTarget.TryCreate("example.com", null, out var target, out _), "домен не принят");
            var parsed = target ?? throw new InvalidOperationException("цель не создана");
            Assert(parsed.Url == "https://example.com/", "неверный URL");
            Assert(parsed.Host == "example.com" && parsed.Port == 443, "неверные host/port");
        }

        private static void UrlKeepsPort()
        {
            Assert(MonitorTarget.TryCreate("http://localhost:8080/status", "Локальная проверка", out var target, out _), "URL не принят");
            var parsed = target ?? throw new InvalidOperationException("цель не создана");
            Assert(parsed.Name == "Локальная проверка", "имя не сохранено");
            Assert(parsed.Port == 8080 && parsed.Host == "localhost", "порт или host потерян");
        }

        private static void UnsupportedSchemeRejected()
        {
            Assert(!MonitorTarget.TryCreate("ftp://example.com/file", null, out _, out var error), "ftp принят");
            Assert(error.Contains("корректный URL", StringComparison.OrdinalIgnoreCase), "непонятная ошибка схемы");
        }

        private static void EmptyAddressRejected()
        {
            Assert(!MonitorTarget.TryCreate("  ", null, out _, out var error), "пустой адрес принят");
            Assert(error.Contains("Введите URL", StringComparison.OrdinalIgnoreCase), "непонятная ошибка пустого адреса");
        }

        private static void BuiltInIsMarked()
        {
            var target = MonitorTarget.CreateBuiltIn("GitHub", "https://github.com/");
            Assert(target.IsBuiltIn && target.Enabled, "встроенная цель помечена неправильно");
        }

        private static void VersionsCompareNumerically()
        {
            Assert(EngineService.CompareVersions("1.10.2", "1.9.9") > 0, "1.10.2 ошибочно меньше 1.9.9");
            Assert(EngineService.CompareVersions("v1.10.2", "1.10.2") == 0, "префикс v изменяет сравнение");
            Assert(EngineService.CompareVersions("1.10.2", "1.10.2a") < 0, "суффикс версии сравнивается неверно");
        }

        private static void StrategyFeaturesAreNormalized()
        {
            var strategy = new StrategyInfo
            {
                Name = "smoke",
                Args = new System.Collections.Generic.List<string>
                {
                    "--wf-tcp=443", "--wf-udp=443", "--dpi-desync=fake",
                    "--dpi-desync-split-pos=1", "--dpi-desync-split-seqovl=1",
                    "--dpi-desync-ttl=3", "--dpi-desync-multisplit=2",
                    "--dpi-desync-fake-tls=example.com", "--dpi-desync-fake-quic=example.com", "--hostlist=lists/general.txt",
                    "--ipset=lists/ipset.txt"
                },
                UsesFakeTls = true
            };

            var features = StrategyFeatureAnalyzer.Analyze(strategy);
            Assert(features.DesyncModes.Contains("fake", StringComparer.OrdinalIgnoreCase), "режим desync не найден");
            Assert(features.SplitParameterCount == 3, "split-параметры посчитаны неверно");
            Assert(features.SplitPositions.Contains("1") && features.SeqOvlValues.Contains("1"), "позиция split или seqovl потеряны");
            Assert(features.TtlValues.Contains("3") && features.UsesMultisplit, "TTL или multisplit потеряны");
            Assert(features.UsesFakeTls && features.UsesFakeQuic && features.UsesHostlist && features.UsesIpSet, "признаки TLS/QUIC/hostlist/ipset потеряны");
            Assert(features.UsesTcpFilter && features.UsesUdpFilter, "фильтры TCP/UDP потеряны");
            Assert(features.ParameterNames.Contains("--dpi-desync-fake-quic"), "дополнительный параметр потерян");
            Assert(features.TunableArguments.Length == 7, "изменяемые параметры классифицированы неверно");
            Assert(features.FixedArguments.Contains("--wf-tcp=443"), "фиксированный фильтр TCP потерян");
            Assert(!string.IsNullOrWhiteSpace(features.Fingerprint), "fingerprint пустой");
        }

        private static void StrategyParserKeepsBatContract()
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapretGUI-parser-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "service.bat"),
                    @"start ""service"" ""%~dp0bin\winws.exe"" --wf-tcp=443");
                File.WriteAllText(Path.Combine(root, "general (FAKE TLS AUTO).bat"),
                    @"@echo off
start ""zapret"" /min ""%BIN%winws.exe"" --wf-tcp=443 ^
  --dpi-desync=fake ^
  --dpi-desync-fake-tls=example.com ^
  --hostlist=%LISTS%general.txt ^
  --wf-udp={GameFilterUDP}
");

                var strategies = StrategyParser.LoadAll(root);
                Assert(strategies.Count == 1, "service.bat ошибочно попал в каталог стратегий");
                var strategy = strategies[0];
                Assert(strategy.Category == "FAKE TLS AUTO" && !strategy.IsRecommended,
                    "категория определена неверно или стратегия предвзято помечена как рекомендуемая без проверки");
                Assert(strategy.Args.Any(arg => arg.Contains("--dpi-desync=fake", StringComparison.Ordinal)),
                    "аргумент desync потерян");
                Assert(strategy.Args.Any(arg => arg.Contains("{GameFilterUDP}", StringComparison.Ordinal)),
                    "плейсхолдер game filter потерян");
                Assert(strategy.UsesFakeTls && strategy.UsesGameFilter, "признаки стратегии не определены");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void BypassArgumentsKeepGameFilter()
        {
            var strategy = new StrategyInfo
            {
                Name = "filters",
                Args = new System.Collections.Generic.List<string>
                {
                    "--wf-tcp={GameFilterTCP}",
                    "--wf-udp={GameFilterUDP}",
                    "--dpi-desync=fake"
                }
            };

            var both = BypassArgumentBuilder.Build(strategy, GameFilterMode.TcpAndUdp);
            Assert(both.SequenceEqual(new[] { "--wf-tcp=1024-65535", "--wf-udp=1024-65535", "--dpi-desync=fake" }),
                "режим TCP+UDP раскрыт неверно");
            var tcp = BypassArgumentBuilder.Build(strategy, GameFilterMode.TcpOnly);
            Assert(tcp[0].EndsWith("1024-65535", StringComparison.Ordinal) && tcp[1].EndsWith("12", StringComparison.Ordinal),
                "режим TCP раскрыт неверно");
            var disabled = BypassArgumentBuilder.Build(strategy, GameFilterMode.Disabled);
            Assert(disabled[0].EndsWith("12", StringComparison.Ordinal) && disabled[1].EndsWith("12", StringComparison.Ordinal),
                "отключённый game filter раскрыт неверно");
            Assert(strategy.Args[0].Contains("{GameFilterTCP}", StringComparison.Ordinal),
                "исходная стратегия была изменена при подстановке");
        }

        private static void ProviderContextAndCandidateAreSafe()
        {
            var provider = new ProviderContext
            {
                Name = "Тестовый провайдер",
                Asn = "AS64500",
                Source = ProviderContextSource.UserInput,
                CheckedAt = DateTime.UtcNow,
                Confidence = 70
            };
            var strategy = new StrategyInfo
            {
                Name = "manual",
                Args = new System.Collections.Generic.List<string> { "--wf-tcp=443", "--dpi-desync=fake" }
            };

            var candidate = StrategyCandidateFactory.CreatePreview(strategy, provider);
            Assert(candidate.ProviderText.Contains("Тестовый провайдер", StringComparison.Ordinal), "провайдер не попал в предпросмотр");
            Assert(candidate.ProviderText.Contains("AS64500", StringComparison.Ordinal), "ASN не попал в предпросмотр");
            Assert(candidate.ArgsText == strategy.ShortArgs, "предпросмотр изменил аргументы стратегии");
            Assert(candidate.Summary.Contains("не запускался", StringComparison.OrdinalIgnoreCase), "предпросмотр обещает запуск");

            var saved = SavedStrategyCandidate.From(candidate);
            var restored = saved.ToCandidate();
            var restoredStrategy = saved.ToStrategyInfo();
            Assert(restored.Fingerprint == candidate.Fingerprint, "сохранённый кандидат изменил fingerprint");
            Assert(restored.ArgsText == candidate.ArgsText, "сохранённый кандидат изменил аргументы");
            Assert(restoredStrategy.Category == "АВТОКОНСТРУКТОР" && restoredStrategy.Name.Contains(saved.Id[..8], StringComparison.Ordinal),
                "сохранённый кандидат не получил отдельное имя стратегии");

            var unknown = StrategyCandidateFactory.CreatePreview(strategy, new ProviderContext());
            Assert(unknown.ProviderText.Contains("Провайдер не указан", StringComparison.Ordinal), "неизвестный провайдер не обозначен");

            var tokenStrategy = new StrategyInfo
            {
                Name = "game-filter",
                Args = new System.Collections.Generic.List<string> { "--wf-tcp={GameFilterTCP}", "--wf-udp={GameFilterUDP}" }
            };
            var finalArgs = StrategyCandidateFactory.CreatePreview(tokenStrategy, provider, GameFilterMode.TcpOnly);
            Assert(!finalArgs.ArgsText.Contains("{GameFilter", StringComparison.Ordinal), "плейсхолдер game filter остался в предпросмотре");
            Assert(finalArgs.ArgsText.Contains("1024-65535", StringComparison.Ordinal) && finalArgs.ArgsText.Contains("12", StringComparison.Ordinal),
                "game filter не раскрыт в итоговых аргументах");
        }

        private static void CandidateGeneratorIsBounded()
        {
            var first = new StrategyInfo
            {
                Name = "first",
                IsRecommended = true,
                Args = new System.Collections.Generic.List<string>
                {
                    "--wf-tcp=443", "--dpi-desync=fake", "--dpi-desync-split-pos=1"
                }
            };
            var second = new StrategyInfo
            {
                Name = "second",
                IsRecommended = true,
                Args = new System.Collections.Generic.List<string>
                {
                    "--wf-tcp=443", "--dpi-desync=split2", "--dpi-desync-split-pos=2"
                }
            };

            var original = first.ShortArgs;
            var result = StrategyCandidateGenerator.Generate(
                new[] { first, second }, new ProviderContext(), GameFilterMode.Disabled,
                new StrategyCandidateGenerationOptions { MaxCandidates = 3, MaxVariantsPerSource = 3 });

            Assert(result.Candidates.Count <= 3, "лимит кандидатов нарушен");
            Assert(result.Candidates.Count > 0, "кандидаты не созданы");
            Assert(result.Candidates.Select(candidate => candidate.Fingerprint).Distinct().Count() == result.Candidates.Count,
                "дубликаты кандидатов не удалены");
            Assert(result.Candidates.Any(candidate => candidate.MutationDescription.Contains("split-pos", StringComparison.Ordinal)),
                "вариант split-pos не создан");
            Assert(first.ShortArgs == original, "исходная стратегия была изменена");

            second.IsRecommended = false;
            var fallback = StrategyCandidateGenerator.Generate(
                new[] { second }, new ProviderContext(), GameFilterMode.Disabled,
                new StrategyCandidateGenerationOptions { MaxCandidates = 2 });
            Assert(fallback.UsedUntestedFallback, "не отмечен fallback для нетестированной основы");
        }

        private static void CandidateEvaluationScoresStability()
        {
            var candidate = new StrategyCandidate
            {
                Name = "Тестовый кандидат",
                Args = new System.Collections.Generic.List<string> { "--wf-tcp=443" }
            };
            var evaluation = new StrategyCandidateEvaluation(candidate);
            evaluation.MarkTesting(1, 2);
            evaluation.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidate.Name },
                Started = true,
                Checks = new System.Collections.Generic.List<ConnectionCheck>
                {
                    new ConnectionCheck { Title = "YouTube", Ok = true },
                    new ConnectionCheck { Title = "Discord", Ok = true }
                }
            }, 1, 2);
            evaluation.MarkTesting(2, 2);
            evaluation.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidate.Name },
                Started = true,
                Checks = new System.Collections.Generic.List<ConnectionCheck>
                {
                    new ConnectionCheck { Title = "YouTube", Ok = true },
                    new ConnectionCheck { Title = "Discord", Ok = true }
                }
            }, 2, 2);
            evaluation.Complete();

            Assert(evaluation.IsStable && evaluation.SuccessfulRepeats == 2, "стабильность не рассчитана");
            Assert(evaluation.Score > 0 && evaluation.ScoreText == evaluation.Score.ToString(), "score кандидата рассчитан неверно");
        }

        private static void CandidateHistoryKeepsCatalogData()
        {
            var provider = new ProviderContext
            {
                Name = "Исторический провайдер",
                Asn = "AS64501",
                Source = ProviderContextSource.UserInput,
                Confidence = 80
            };
            var candidate = StrategyCandidateFactory.CreatePreview(
                new StrategyInfo
                {
                    Name = "history-source",
                    Args = new System.Collections.Generic.List<string> { "--wf-tcp=443", "--dpi-desync=fake" }
                }, provider);
            var evaluation = new StrategyCandidateEvaluation(candidate);
            evaluation.MarkTesting(1, 1);
            evaluation.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidate.Name },
                Started = true,
                Elapsed = TimeSpan.FromMilliseconds(125),
                Checks = new System.Collections.Generic.List<ConnectionCheck>
                {
                    new ConnectionCheck { Title = "YouTube", Ok = true, Milliseconds = 80 },
                    new ConnectionCheck { Title = "Discord", Ok = false, Milliseconds = 125, Details = "HTTP 403" }
                }
            }, 1, 1);
            evaluation.Complete();

            var record = StrategyEvaluationHistoryRecord.FromEvaluation(evaluation);
            Assert(record.SourceStrategy == "history-source", "источник не сохранён");
            Assert(record.FeaturesSummary.Contains("desync", StringComparison.OrdinalIgnoreCase), "признаки не сохранены");
            Assert(record.RepeatCount == 1 && record.PassedChecks == 1, "результаты не сохранены");
            Assert(record.Attempts.Count == 1 && record.FailureReasons.Contains("HTTP 403", StringComparison.Ordinal),
                "причина отказа не сохранена");
            Assert(record.AverageElapsedMilliseconds == 125, "задержка не сохранена");
        }

        private static void ProviderHistoryRanksSources()
        {
            var provider = new ProviderContext
            {
                Name = "Провайдер для эвристики",
                Asn = "AS64502",
                Source = ProviderContextSource.UserInput,
                Confidence = 60
            };
            var first = new StrategyInfo
            {
                Name = "first-source",
                IsRecommended = true,
                Args = new System.Collections.Generic.List<string> { "--wf-tcp=443", "--dpi-desync=fake" }
            };
            var second = new StrategyInfo
            {
                Name = "second-source",
                IsRecommended = true,
                Args = new System.Collections.Generic.List<string> { "--wf-tcp=443", "--dpi-desync=split2" }
            };
            var history = new StrategyEvaluationHistoryRecord
            {
                SourceStrategy = second.Name,
                Provider = provider,
                Score = 50000,
                SuccessfulRepeats = 2,
                CompletedAtUtc = DateTime.UtcNow
            };

            var result = StrategyCandidateGenerator.Generate(
                new[] { first, second }, provider, GameFilterMode.Disabled,
                new StrategyCandidateGenerationOptions { MaxCandidates = 1, MaxVariantsPerSource = 1 },
                history: new[] { history });
            Assert(result.ProviderHeuristicApplied, "эвристика провайдера не отмечена");
            Assert(result.Candidates.Count == 1 && result.Candidates[0].SourceStrategy == second.Name,
                "история провайдера не повлияла на порядок источников");

            var localFirst = new StrategyInfo
            {
                Name = "local-tls-source",
                IsRecommended = true,
                Args = new System.Collections.Generic.List<string>
                {
                    "--wf-tcp=443", "--dpi-desync=fake", "--dpi-desync-fake-tls=example.com"
                }
            };
            var localSecond = new StrategyInfo
            {
                Name = "local-split-source",
                IsRecommended = true,
                Args = new System.Collections.Generic.List<string> { "--wf-tcp=443", "--dpi-desync=split2" }
            };
            var localResult = StrategyCandidateGenerator.Generate(
                new[] { localSecond, localFirst }, new ProviderContext(), GameFilterMode.Disabled,
                new StrategyCandidateGenerationOptions { MaxCandidates = 1, MaxVariantsPerSource = 1 },
                localProfile: new NetworkObservationSnapshot
                {
                    CreatedAt = DateTime.UtcNow,
                    HasDpiFreeze = true,
                    HasTlsError = true,
                    Summary = "возможное зависание DPI · ошибка TLS"
                });
            Assert(localResult.LocalProfileApplied && localResult.Candidates.Count == 1 &&
                   localResult.Candidates[0].SourceStrategy == localFirst.Name,
                "локальный профиль не приоритизировал подходящие признаки");
        }

        private static void EngineRollbackIsSafe()
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapretGUI-engine-" + Guid.NewGuid().ToString("N"));
            var backupRoot = Path.Combine(Path.GetTempPath(), "ZapretGUI-backup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "bin"));
                Directory.CreateDirectory(backupRoot);
                File.WriteAllText(Path.Combine(root, "bin", "winws.exe"), "new");
                File.WriteAllText(Path.Combine(root, "bin", "added.dll"), "added");
                File.WriteAllText(Path.Combine(backupRoot, "winws.exe"), "old");
                File.WriteAllText(Path.Combine(root, ".gui-engine-version"), "new-version");

                var backup = new EngineBackupInfo
                {
                    BackupRoot = backupRoot,
                    EngineRoot = root,
                    PreviousVersion = "old-version",
                    InstalledVersion = "new-version",
                    HadVersionMarker = true,
                    PreviousMarker = "old-version",
                    ExistingFiles = new System.Collections.Generic.List<string> { "bin/winws.exe" },
                    NewFiles = new System.Collections.Generic.List<string> { "bin/added.dll" }
                };
                Directory.CreateDirectory(Path.Combine(backupRoot, "bin"));
                File.Move(Path.Combine(backupRoot, "winws.exe"), Path.Combine(backupRoot, "bin", "winws.exe"), true);

                var result = EngineService.RollbackEngine(backup, root);
                Assert(result.Ok, "откат движка завершился ошибкой");
                Assert(result.RestoredFiles == 1 && result.RemovedNewFiles == 1,
                    "отчёт отката не содержит количества изменённых файлов");
                Assert(File.ReadAllText(Path.Combine(root, "bin", "winws.exe")) == "old", "старый файл не восстановлен");
                Assert(!File.Exists(Path.Combine(root, "bin", "added.dll")), "новый файл не удалён");
                Assert(File.ReadAllText(Path.Combine(root, ".gui-engine-version")) == "old-version", "маркер версии не восстановлен");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, recursive: true);
            }
        }

        private static void LockedDriverIsSkippedSafely()
        {
            var source = Path.Combine(Path.GetTempPath(), "ZapretGUI-copy-source-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(Path.GetTempPath(), "ZapretGUI-copy-destination-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(source, "bin"));
                Directory.CreateDirectory(Path.Combine(destination, "bin"));
                var driver = Path.Combine(destination, "bin", "WinDivert64.sys");
                File.WriteAllText(Path.Combine(source, "bin", "WinDivert64.sys"), "new-driver");
                File.WriteAllText(driver, "old-driver");

                var result = EngineFileUpdater.Copy(
                    source,
                    destination,
                    preserveUserData: true,
                    isUserFile: _ => false,
                    isDriverFile: path => Path.GetFileName(path).Equals("WinDivert64.sys", StringComparison.OrdinalIgnoreCase),
                    getIpsetMode: () => IpsetMode.Loaded,
                    isFileLocked: path => string.Equals(path, driver, StringComparison.OrdinalIgnoreCase));

                Assert(result.UpdatedFiles == 0 && result.SkippedFiles == 1,
                    "заблокированный драйвер не был пропущен");
                Assert(result.Warnings.Count == 1 && result.Warnings[0].Contains("заблокирован", StringComparison.OrdinalIgnoreCase),
                    "для заблокированного драйвера не создано предупреждение");
                Assert(File.ReadAllText(driver) == "old-driver", "заблокированный драйвер был перезаписан");
            }
            finally
            {
                if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            }
        }

        private static void GuiRepositoryValidationIsStrict()
        {
            Assert(GuiUpdateService.TryParseRepository("m0xvi/zapret-gui", out var repository) &&
                   repository == "m0xvi/zapret-gui", "официальный формат owner/repository не принят");
            Assert(GuiUpdateService.TryParseRepository("https://github.com/m0xvi/zapret-gui/", out repository) &&
                   repository == "m0xvi/zapret-gui", "ссылка GitHub не нормализована");
            Assert(!GuiUpdateService.TryParseRepository("https://evil.example/update", out _),
                "внешний host ошибочно принят как репозиторий обновлений");
            Assert(!GuiUpdateService.TryParseRepository("m0xvi/../other", out _),
                "путь с traversal ошибочно принят как репозиторий");
            Assert(GuiUpdateService.IsSafeReleaseUrl(
                       "https://github.com/m0xvi/zapret-gui/releases/download/v1.2.1/ZapretGUI-1.2.1-win-x64-portable.exe",
                       repository), "ссылка на официальный asset отклонена");
            Assert(!GuiUpdateService.IsSafeReleaseUrl(
                       "https://example.com/m0xvi/zapret-gui/releases/download/v1.2.1/app.exe",
                       repository), "внешняя ссылка на asset ошибочно разрешена");
        }

        private static void GuiPortableAssetSelectionIsStrict()
        {
            var valid = new GuiReleaseAsset
            {
                Name = "ZapretGUI-1.2.1-win-x64-portable.exe",
                Size = 123,
                DownloadUrl = "https://github.com/m0xvi/zapret-gui/releases/download/v1.2.1/ZapretGUI-1.2.1-win-x64-portable.exe",
                Digest = "sha256:" + new string('a', 64)
            };
            var release = new GuiReleaseInfo { Assets = new List<GuiReleaseAsset>
            {
                new() { Name = "ZapretGUI-1.2.1-win-x64-portable.exe", Size = 123, DownloadUrl = valid.DownloadUrl },
                valid,
                new() { Name = "ZapretGUI-1.2.1-win-x64-net8.zip", Size = 123, Digest = valid.Digest }
            }};
            Assert(ReferenceEquals(release.PortableAsset, valid), "выбран неподтверждаемый или неподходящий asset");
            Assert(!GuiUpdateService.IsSupportedReleaseAsset(new GuiReleaseAsset
            {
                Name = "ZapretGUI-1.2.1-win-x64-portable.exe",
                Size = 123,
                DownloadUrl = valid.DownloadUrl
            }), "asset без digest ошибочно признан безопасным");
        }

        private static void EngineConsistencyIsChecked()
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapretGUI-consistency-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "bin"));
                Directory.CreateDirectory(Path.Combine(root, "lists"));
                File.WriteAllText(Path.Combine(root, "bin", "winws.exe"), "test");
                File.WriteAllText(Path.Combine(root, "bin", "WinDivert64.sys"), "test");
                File.WriteAllText(Path.Combine(root, "bin", "WinDivert.dll"), "test");
                File.WriteAllText(Path.Combine(root, "lists", "general.txt"), "test");
                var report = EngineConsistencyChecker.Check(root);
                Assert(report.Checks.Any(check => check.Name == "WinDivert.dll" && check.IsOk), "DLL не проверяется");
                Assert(report.Checks.Any(check => check.Name == "Списки движка" && check.IsOk), "списки не проверяются");
                Assert(report.Checks.Any(check => check.Name == "Версии движка и WinDivert" && !check.IsOk), "отсутствие проверки версий не отмечено");
                Assert(report.Checks.Any(check => check.Name == "Стратегии" && !check.IsOk), "отсутствие стратегий не отмечено");
                Assert(!report.IsConsistent, "неполный комплект ошибочно признан согласованным");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void ServiceStateParsingIsLocalized()
        {
            Assert(WinServices.ParseServiceState("SERVICE_NAME: zapret\n        STATE              : 4  RUNNING", 0) == ServiceState.Running,
                "английское состояние RUNNING не разобрано");
            Assert(WinServices.ParseServiceState("ИМЯ_СЛУЖБЫ: zapret\n        СОСТОЯНИЕ          : 1  ОСТАНОВЛЕНА", 0) == ServiceState.Stopped,
                "локализованное состояние не разобрано");
            Assert(WinServices.ParseServiceState("[SC] OpenService FAILED 1060", 1060) == ServiceState.NotInstalled,
                "отсутствующая служба не распознана");
            Assert(WinServices.ParseServiceState("неизвестный вывод", 1) == ServiceState.NotInstalled,
                "ошибка sc.exe не распознана");
        }

        private static void SafeServiceStopIsIsolated()
        {
            var states = new System.Collections.Generic.Dictionary<string, ServiceState>
            {
                [WinServices.ZapretService] = ServiceState.Running,
                [WinServices.WinDivertService] = ServiceState.StopPending,
                [WinServices.WinDivert14Service] = ServiceState.NotInstalled
            };
            var stopCalls = new System.Collections.Generic.List<string>();
            var queryCalls = new System.Collections.Generic.Dictionary<string, int>();
            var report = WinServices.StopForEngineUpdateAsync(
                name =>
                {
                    queryCalls[name] = queryCalls.TryGetValue(name, out var count) ? count + 1 : 1;
                    if (name == WinServices.WinDivertService && queryCalls[name] > 1)
                        states[name] = ServiceState.Stopped;
                    return states[name];
                },
                name =>
                {
                    stopCalls.Add(name);
                    states[name] = ServiceState.Stopped;
                    return new ShellResult();
                }).GetAwaiter().GetResult();

            Assert(stopCalls.SequenceEqual(new[] { WinServices.ZapretService }),
                "безопасная остановка вызвала Stop для уже ожидающей службы");
            Assert(states[WinServices.ZapretService] == ServiceState.Stopped &&
                   states[WinServices.WinDivertService] == ServiceState.Stopped,
                "симулятор служб не перешёл в остановленное состояние");
            Assert(report.Any(line => line.Contains("zapret", StringComparison.OrdinalIgnoreCase) && line.Contains("остановлена")),
                "отчёт об остановке zapret не сохранён");
            Assert(report.Any(line => line.Contains("WinDivert14", StringComparison.OrdinalIgnoreCase) && line.Contains("не установлена")),
                "отсутствующая служба не отражена в отчёте");
        }

        private static void ServiceHealthCacheIsExplicit()
        {
            var fresh = new ServiceHealthSnapshot
            {
                CheckedAtUtc = DateTime.UtcNow,
                Bfe = ServiceState.Running,
                WinDivert = ServiceState.NotInstalled,
                WinDivert14 = ServiceState.NotInstalled
            };
            Assert(!fresh.IsStale, "свежий снимок помечен устаревшим");
            Assert(!fresh.BfeNeedsRecovery, "работающая BFE требует восстановления");
            Assert(!fresh.HasWinDivertLeftovers, "отсутствующие службы отмечены как остатки");
            Assert(fresh.BfeText == "работает" && fresh.WinDivertText == "не установлена",
                "локализация состояния службы потеряна");

            var stale = new ServiceHealthSnapshot
            {
                CheckedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                Bfe = ServiceState.Stopped,
                WinDivert = ServiceState.Running,
                WinDivert14 = ServiceState.NotInstalled
            };
            Assert(stale.IsStale && stale.BfeNeedsRecovery && stale.HasWinDivertLeftovers,
                "устаревший проблемный снимок не отмечен явно");
            Assert(ServiceHealthSnapshot.FormatState(ServiceState.Unknown) == "состояние неизвестно",
                "неизвестное состояние не локализовано");
        }

        private static void IpsetModesRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapretGUI-ipset-" + Guid.NewGuid().ToString("N"));
            try
            {
                var lists = Path.Combine(root, "lists");
                Directory.CreateDirectory(lists);
                Assert(EngineService.GetIpsetMode(root) == IpsetMode.Loaded,
                    "отсутствующий ipset ошибочно не считается загруженным");

                var file = Path.Combine(lists, "ipset-all.txt");
                File.WriteAllText(file, "");
                Assert(EngineService.GetIpsetMode(root) == IpsetMode.Any, "пустой ipset не распознан как any");
                File.WriteAllText(file, "198.51.100.0/24\n");
                Assert(EngineService.GetIpsetMode(root) == IpsetMode.Loaded, "загруженный ipset не распознан");

                Assert(EngineService.SetIpsetMode(root, IpsetMode.None), "режим none не включился");
                Assert(EngineService.GetIpsetMode(root) == IpsetMode.None, "режим none не сохранился");
                Assert(EngineService.SetIpsetMode(root, IpsetMode.Any), "режим any не включился");
                Assert(EngineService.GetIpsetMode(root) == IpsetMode.Any, "режим any не сохранился");
                Assert(EngineService.SetIpsetMode(root, IpsetMode.Loaded), "исходный ipset не восстановился");
                Assert(EngineService.GetIpsetMode(root) == IpsetMode.Loaded &&
                       File.ReadAllText(file).Contains("198.51.100.0/24", StringComparison.Ordinal),
                    "исходное содержимое ipset не восстановилось");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void HostsMergeKeepsUserLines()
        {
            var current = new[]
            {
                "127.0.0.1 localhost",
                "203.0.113.10 user-owned.example",
                "# ==== Zapret GUI (Flowseal/zapret-discord-youtube) begin ====",
                "198.51.100.10 old.example",
                "# ==== Zapret GUI (Flowseal/zapret-discord-youtube) end ===="
            };
            var merged = EngineService.MergeHosts(current, new[] { "198.51.100.20 new.example", "203.0.113.10 user-owned.example" });
            var text = string.Join("\n", merged);
            Assert(text.Contains("127.0.0.1 localhost", StringComparison.Ordinal), "системная строка hosts потеряна");
            Assert(text.Contains("203.0.113.10 user-owned.example", StringComparison.Ordinal), "пользовательская строка hosts потеряна");
            Assert(text.Contains("198.51.100.20 new.example", StringComparison.Ordinal), "новая строка hosts не добавлена");
            Assert(!text.Contains("198.51.100.10 old.example", StringComparison.Ordinal), "старый блок hosts не удалён");
            Assert(text.Split("198.51.100.20 new.example", StringSplitOptions.None).Length == 2,
                "новая строка hosts добавлена несколько раз");
        }

        private static void EngineReadinessIsChecked()
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapretGUI-harness-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "bin"));
                Assert(!EngineService.IsEngineReady(root), "пустая папка признана готовым движком");
                File.WriteAllText(Path.Combine(root, "bin", "winws.exe"), "test");
                File.WriteAllText(Path.Combine(root, "bin", "WinDivert64.sys"), "test");
                Assert(EngineService.IsEngineReady(root), "обязательные файлы не распознаны");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void ReadinessRequiresWizard()
        {
            var settings = new AppSettings
            {
                FirstLaunchWizardCompleted = false,
                SelectedStrategy = "main"
            };
            var readiness = ReadinessEvaluator.Evaluate(settings, isAdmin: true, engineReady: true, strategyCount: 2);
            Assert(readiness.Status.Contains("мастер", StringComparison.OrdinalIgnoreCase), "незавершённый мастер не обозначен");
            Assert(!readiness.IsReady && readiness.Key == "Warning", "мастер ошибочно признан готовым");
        }

        private static void SafeModeKeepsReadinessExplicit()
        {
            var settings = new AppSettings
            {
                FirstLaunchWizardCompleted = true,
                SafeMode = true,
                AutoStartBypass = false,
                SelectedStrategy = "main"
            };
            var readiness = ReadinessEvaluator.Evaluate(settings, isAdmin: true, engineReady: true, strategyCount: 1);
            Assert(readiness.IsReady && readiness.Status.Contains("безопасный", StringComparison.OrdinalIgnoreCase),
                "безопасный режим не отражён в готовности");
            Assert(!ReadinessEvaluator.AutomaticActionsAllowed(settings),
                "безопасный режим разрешил автоматические действия");
            Assert(readiness.Details.Contains("только после явного", StringComparison.OrdinalIgnoreCase),
                "описание безопасного режима недостаточно явно");
        }

        private static void DpiCancellationIsPrompt()
        {
            using var cancellation = new System.Threading.CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                _ = DpiCheckerService.RunAsync("example.com", null, cancellation.Token)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("отменённая DPI-проверка завершилась без отмены");
            }
            catch (OperationCanceledException)
            {
                // Ожидаемый путь: до сетевого запроса управление возвращается сразу.
            }
        }

        private static void DpiSnapshotKeepsStages()
        {
            var control = new DpiTargetResult
            {
                Id = "CONTROL",
                Provider = "Контрольный endpoint",
                Host = "example.com",
                IsControlEndpoint = true,
                AttemptCount = 2,
                Probes = new System.Collections.Generic.List<DpiProbeResult>
                {
                    new DpiProbeResult { TestName = "DNS", ProbeKind = "DNS", Status = "ОТВЕТ", Details = "получен адрес" },
                    new DpiProbeResult { TestName = "TLS 1.3", ProbeKind = "HTTPS", Status = "ОТВЕТ", Details = "HTTP 204", Attempt = 2 }
                }
            };
            var snapshot = new DpiCheckSnapshot
            {
                CreatedAt = DateTime.Now,
                TargetsTotal = 1,
                TargetsTested = 1,
                SuiteSource = "локальный кэш suite.v2.json",
                SuiteLoadedAt = DateTime.Now.AddMinutes(-2),
                ControlResult = control,
                Results = new System.Collections.Generic.List<DpiTargetResult> { control }
            };
            var observation = NetworkObservationSnapshot.From(snapshot.Results, null, snapshot.CreatedAt, control);
            Assert(observation.Summary.Contains("Нормализованные", StringComparison.OrdinalIgnoreCase),
                "сетевое наблюдение не сформировано");
            var json = JsonSerializer.Serialize(snapshot);
            var restored = JsonSerializer.Deserialize<DpiCheckSnapshot>(json)
                ?? throw new InvalidOperationException("снимок DPI не десериализован");
            Assert(restored.ControlResult?.IsControlEndpoint == true && restored.ControlResult.AttemptCount == 2,
                "контрольный endpoint или число повторов потеряны");
            Assert(restored.SuiteSource.Contains("кэш", StringComparison.OrdinalIgnoreCase),
                "источник набора не сохранён");
        }

        private static void StrategySuitabilitySmokeContract()
        {
            var twoChecks = new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = "smoke-2" },
                Started = true,
                Checks = new[]
                {
                    new ConnectionCheck { Title = "YouTube", Ok = true },
                    new ConnectionCheck { Title = "Discord", Ok = true }
                }
            };
            Assert(twoChecks.IsSuitable, "двухресурсный smoke-тест должен быть пригодным");

            var extendedThree = new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = "extended-3" },
                Started = true,
                Checks = new[]
                {
                    new ConnectionCheck { Title = "YouTube", Ok = true },
                    new ConnectionCheck { Title = "Discord", Ok = true },
                    new ConnectionCheck { Title = "Google", Ok = true }
                }
            };
            Assert(extendedThree.IsSuitable, "расширенный тест с 3 успешными ресурсами должен быть пригодным");

            var extendedOnlyTwo = new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = "extended-fail" },
                Started = true,
                Checks = new[]
                {
                    new ConnectionCheck { Title = "YouTube", Ok = true },
                    new ConnectionCheck { Title = "Discord", Ok = true },
                    new ConnectionCheck { Title = "Google", Ok = false },
                    new ConnectionCheck { Title = "Cloudflare", Ok = false }
                }
            };
            Assert(!extendedOnlyTwo.IsSuitable, "расширенный тест с 2 из 4 успешных не должен быть пригодным");
        }

        private static void DpiProbeScoreAndFreezeClassification()
        {
            var probeSuccess = new DpiProbeResult { TestName = "TLS 1.2", ProbeKind = "HTTPS", Status = "ОТВЕТ" };
            var probeFreeze = new DpiProbeResult { TestName = "TLS 1.3", ProbeKind = "HTTPS", Status = "ВОЗМОЖНА БЛОКИРОВКА", PossibleDpiFreeze = true, TimedOut = true };
            var probeError = new DpiProbeResult { TestName = "HTTP/1.1", ProbeKind = "HTTPS", Status = "ОШИБКА", PossibleDpiFreeze = false };

            var probes = new List<DpiProbeResult> { probeSuccess, probeFreeze, probeError };
            var freezes = probes.Count(p => p.PossibleDpiFreeze);
            var failed = probes.Count(p => p.Status != "ОТВЕТ");
            var successful = probes.Count - failed;
            var score = successful * 10 - failed * 20 - freezes * 100;
            Assert(score == -130, "формула DPI-score рассчитана неверно");
        }

        private static void EmpiricalCandidateEvaluationScoreOrdering()
        {
            var candidateA = new StrategyCandidate { Name = "Candidate A", Args = new List<string> { "--wf-tcp=443" } };
            var candidateB = new StrategyCandidate { Name = "Candidate B", Args = new List<string> { "--wf-tcp=443", "--dpi-desync=fake" } };

            var evalA = new StrategyCandidateEvaluation(candidateA);
            evalA.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidateA.Name },
                Started = true,
                Elapsed = TimeSpan.FromMilliseconds(50),
                Checks = new[] { new ConnectionCheck { Title = "YouTube", Ok = true }, new ConnectionCheck { Title = "Discord", Ok = true } }
            }, 1, 2);
            evalA.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidateA.Name },
                Started = true,
                Elapsed = TimeSpan.FromMilliseconds(50),
                Checks = new[] { new ConnectionCheck { Title = "YouTube", Ok = true }, new ConnectionCheck { Title = "Discord", Ok = true } }
            }, 2, 2);
            evalA.Complete();

            var evalB = new StrategyCandidateEvaluation(candidateB);
            evalB.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidateB.Name },
                Started = true,
                Elapsed = TimeSpan.FromMilliseconds(50),
                Checks = new[] { new ConnectionCheck { Title = "YouTube", Ok = true }, new ConnectionCheck { Title = "Discord", Ok = false } }
            }, 1, 2);
            evalB.AddResult(new StrategyTestResult
            {
                Strategy = new StrategyInfo { Name = candidateB.Name },
                Started = true,
                Elapsed = TimeSpan.FromMilliseconds(50),
                Checks = new[] { new ConnectionCheck { Title = "YouTube", Ok = true }, new ConnectionCheck { Title = "Discord", Ok = false } }
            }, 2, 2);
            evalB.Complete();

            Assert(evalA.Score > evalB.Score, "кандидат со 100% стабильностью и прохождением проверок должен иметь больший score");
            Assert(evalA.IsStable && !evalB.IsStable, "стабильность кандидатов должна различаться");
        }

        private static void DiagnosticsReportRoundTrips()
        {
            var report = new DiagnosticsExportReport
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Readiness = new DiagnosticsExportReadiness
                {
                    Status = "Готово · безопасный режим",
                    Details = "только после явного действия",
                    Key = "Success"
                },
                RecoveryHistory = new System.Collections.Generic.List<RecoveryJournalEntry>
                {
                    new RecoveryJournalEntry
                    {
                        StartedAt = DateTime.Now.AddSeconds(-2),
                        FinishedAt = DateTime.Now,
                        Operation = "Проверка стратегии",
                        BeforeState = "RunningStandalone",
                        AfterState = "RunningStandalone",
                        Restored = true,
                        Actions = "временный процесс остановлен; прежняя стратегия восстановлена"
                    }
                }
            };
            var json = JsonSerializer.Serialize(report);
            var restored = JsonSerializer.Deserialize<DiagnosticsExportReport>(json)
                ?? throw new InvalidOperationException("диагностический отчёт не десериализован");
            Assert(restored.RecoveryHistory.Count == 1 && restored.RecoveryHistory[0].Restored,
                "журнал восстановления не попал в отчёт");
            Assert(restored.Readiness.Key == "Success" && restored.ReportType.Contains("диагностический", StringComparison.OrdinalIgnoreCase),
                "метаданные отчёта потеряны");
        }

        private static void DomainListsAutoSeedingWorks()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ZapretGUI-seed-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                DomainListUpdater.EnsureSeeded(tempDir);

                var ytFile = Path.Combine(tempDir, "lists", "list-youtube.txt");
                var dsFile = Path.Combine(tempDir, "lists", "list-discord.txt");
                var genFile = Path.Combine(tempDir, "lists", "list-general.txt");
                var googFile = Path.Combine(tempDir, "lists", "list-google.txt");

                Assert(File.Exists(ytFile), "list-youtube.txt не создан");
                Assert(File.Exists(dsFile), "list-discord.txt не создан");
                Assert(File.Exists(genFile), "list-general.txt не создан");
                Assert(File.Exists(googFile), "list-google.txt не создан");

                var ytLines = File.ReadAllLines(ytFile);
                var dsLines = File.ReadAllLines(dsFile);
                var googLines = File.ReadAllLines(googFile);
                Assert(ytLines.Contains("i.ytimg.com") && ytLines.Contains("googlevideo.com"), "list-youtube.txt не содержит i.ytimg.com или googlevideo.com");
                Assert(dsLines.Contains("cdn.discordapp.com") && dsLines.Contains("gateway.discord.gg"), "list-discord.txt не содержит cdn.discordapp.com или gateway.discord.gg");
                Assert(googLines.Contains("googleapis.com") && googLines.Contains("googlevideo.com"), "list-google.txt не содержит googleapis.com или googlevideo.com");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }

        private static void VoiceRtcProberAndFakeBinManagerWork()
        {
            Assert(DiscordVoiceRtcProber.VoiceEndpoints.Length >= 4, "Список голосовых шлюзов пуст");
            var tempDir = Path.Combine(Path.GetTempPath(), "ZapretGUI-bin-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                FakeBinManager.EnsureDefaultPayloads(tempDir);
                var payloads = FakeBinManager.GetAvailablePayloads(tempDir);
                Assert(payloads.Count >= 1, "Эталонный bin фейк не создан");
                Assert(payloads.Any(p => p.FileName.Contains("quic")), "QUIC фейк не распознан");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
