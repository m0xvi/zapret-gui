using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ZapretGui.ViewModels;

namespace ZapretGui.Core
{
    public sealed class ProviderTelemetryDump
    {
        public string Title { get; init; } = "Zapret GUI — Полная сетевая телеметрия и профиль провайдера";
        public string FormatVersion { get; init; } = "2.0";
        public DateTime GeneratedAt { get; init; } = DateTime.Now;
        public string AppVersion { get; init; } = "1.3.7";
        public string OsVersion { get; init; } = Environment.OSVersion.ToString();
        public bool IsAdmin { get; init; }
        public bool IsPortable { get; init; }
        public string EnginePath { get; init; } = "";
        public string EngineVersion { get; init; } = "";

        public ProviderContextTelemetry Provider { get; init; } = new();
        public ActiveConfigurationTelemetry Configuration { get; init; } = new();
        public SystemServicesTelemetry SystemServices { get; init; } = new();
        public DomainListsTelemetry DomainLists { get; init; } = new();
        public List<StrategyTelemetryItem> StrategyTestMatrix { get; init; } = new();
        public List<AutoTunerTelemetryItem> AutoTunerResults { get; init; } = new();
        public List<MonitorTargetTelemetryItem> ActiveProbes { get; init; } = new();
        public List<string> RecentLogs { get; init; } = new();
    }

    public sealed class ProviderContextTelemetry
    {
        public string Name { get; init; } = "";
        public string Asn { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string Source { get; init; } = "";
        public string CheckedAt { get; init; } = "";
    }

    public sealed class ActiveConfigurationTelemetry
    {
        public string SelectedStrategy { get; init; } = "";
        public string BypassState { get; init; } = "";
        public int Pid { get; init; }
        public string Uptime { get; init; } = "";
        public string GameFilterMode { get; init; } = "";
        public string IpsetMode { get; init; } = "";
        public bool SafeMode { get; init; }
        public bool AutoCheckEngineUpdates { get; init; }
    }

    public sealed class SystemServicesTelemetry
    {
        public bool BfeRunning { get; init; }
        public bool WinDivertInstalled { get; init; }
        public bool ZapretServiceInstalled { get; init; }
        public string ZapretServiceStatus { get; init; } = "";
        public bool TcpTimestampsEnabled { get; init; }
        public List<string> DnsServers { get; init; } = new();
    }

    public sealed class DomainListsTelemetry
    {
        public int ListGeneralLines { get; init; }
        public int ListYoutubeLines { get; init; }
        public int ListDiscordLines { get; init; }
        public int ListGeneralUserLines { get; init; }
        public int ListYoutubeUserLines { get; init; }
        public int ListDiscordUserLines { get; init; }
        public int IpsetAllLines { get; init; }
        public int IpsetDiscordLines { get; init; }
    }

    public sealed class StrategyTelemetryItem
    {
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public bool IsRecommended { get; init; }
        public string TestState { get; init; } = "";
        public int PassedCount { get; init; }
        public int TotalChecks { get; init; }
        public double ElapsedSeconds { get; init; }
        public string Description { get; init; } = "";
        public List<string> Args { get; init; } = new();
        public List<ConnectionCheckTelemetryItem> Checks { get; init; } = new();
    }

    public sealed class ConnectionCheckTelemetryItem
    {
        public string Title { get; init; } = "";
        public string Host { get; init; } = "";
        public bool Ok { get; init; }
        public long Milliseconds { get; init; }
        public string Details { get; init; } = "";
    }

    public sealed class AutoTunerTelemetryItem
    {
        public int StepNumber { get; init; }
        public string CandidateName { get; init; } = "";
        public string Title { get; init; } = "";
        public int Score { get; init; }
        public double SuccessRate { get; init; }
        public long AvgRttMs { get; init; }
        public string Details { get; init; } = "";
        public List<string> Args { get; init; } = new();
    }

    public sealed class MonitorTargetTelemetryItem
    {
        public string Name { get; init; } = "";
        public string Url { get; init; } = "";
        public string Host { get; init; } = "";
        public bool Enabled { get; init; }
        public bool LastOk { get; init; }
        public long LastLatencyMs { get; init; }
    }

    /// <summary>
    /// Сервис сбора, сериализации и полного экспорта телеметрии, результатов тестов и профиля провайдера.
    /// </summary>
    public static class ProviderTelemetryExporter
    {
        public static ProviderTelemetryDump Collect(MainViewModel main)
        {
            var status = main.Bypass.GetStatus();
            var enginePath = main.Settings.EnginePath;
            var listsDir = Path.Combine(enginePath ?? "", "lists");

            var dnsList = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var props = nic.GetIPProperties();
                        foreach (var dns in props.DnsAddresses)
                        {
                            var s = dns.ToString();
                            if (!dnsList.Contains(s)) dnsList.Add(s);
                        }
                    }
                }
            }
            catch { }

            int CountLines(string fileName)
            {
                try
                {
                    var p = Path.Combine(listsDir, fileName);
                    return File.Exists(p) ? File.ReadAllLines(p).Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")) : 0;
                }
                catch { return 0; }
            }

            var strategyItems = new List<StrategyTelemetryItem>();
            foreach (var strat in main.Strategies.Items)
            {
                var tr = strat.TestResult;
                var checks = new List<ConnectionCheckTelemetryItem>();
                if (tr?.Checks != null)
                {
                    foreach (var c in tr.Checks)
                    {
                        checks.Add(new ConnectionCheckTelemetryItem
                        {
                            Title = c.Title,
                            Host = c.Host,
                            Ok = c.Ok,
                            Milliseconds = c.Milliseconds,
                            Details = c.Details
                        });
                    }
                }

                strategyItems.Add(new StrategyTelemetryItem
                {
                    Name = strat.Name,
                    Category = strat.Category,
                    IsRecommended = strat.IsRecommended,
                    TestState = strat.TestState.ToString(),
                    PassedCount = tr?.PassedCount ?? 0,
                    TotalChecks = tr?.Checks?.Count ?? 0,
                    ElapsedSeconds = tr?.Elapsed.TotalSeconds ?? 0,
                    Description = strat.Description,
                    Args = strat.Args,
                    Checks = checks
                });
            }

            var autoTunerItems = new List<AutoTunerTelemetryItem>();
            if (main.StrategiesPage?.AutoTuningResults != null)
            {
                foreach (var res in main.StrategiesPage.AutoTuningResults)
                {
                    autoTunerItems.Add(new AutoTunerTelemetryItem
                    {
                        StepNumber = res.StepNumber,
                        CandidateName = res.CandidateName,
                        Title = res.MutationDescription,
                        Score = res.Score,
                        SuccessRate = res.SuccessRate,
                        AvgRttMs = res.AvgRttMs,
                        Details = res.Details,
                        Args = res.Args
                    });
                }
            }

            var activeProbes = new List<MonitorTargetTelemetryItem>();
            foreach (var t in ConnectionTester.GetEffectiveTargets(main.Settings))
            {
                activeProbes.Add(new MonitorTargetTelemetryItem
                {
                    Name = t.Name,
                    Url = t.Url,
                    Host = t.Host,
                    Enabled = t.Enabled,
                    LastOk = t.LastResult?.Ok ?? false,
                    LastLatencyMs = t.LastResult?.Milliseconds ?? 0
                });
            }

            var logEntries = AppLog.Entries.TakeLast(80).Select(e => e.ToString()).ToList();

            var prov = main.Settings.ProviderContext ?? new ProviderContext();

            return new ProviderTelemetryDump
            {
                AppVersion = typeof(ProviderTelemetryExporter).Assembly.GetName().Version?.ToString(3) ?? "1.3.7",
                IsAdmin = Shell.IsAdmin(),
                IsPortable = AppPaths.IsPortableMode,
                EnginePath = enginePath,
                EngineVersion = EngineService.GetInstalledVersion(enginePath) ?? "не определена",
                Provider = new ProviderContextTelemetry
                {
                    Name = prov.Name,
                    Asn = prov.Asn,
                    Confidence = prov.Confidence.ToString(),
                    Source = prov.Source,
                    CheckedAt = prov.CheckedAtText
                },
                Configuration = new ActiveConfigurationTelemetry
                {
                    SelectedStrategy = main.Settings.SelectedStrategy,
                    BypassState = status.State.ToString(),
                    Pid = status.Pid,
                    Uptime = status.UptimeText,
                    GameFilterMode = EngineService.GetGameFilterMode(enginePath).ToString(),
                    IpsetMode = EngineService.GetIpsetMode(enginePath).ToString(),
                    SafeMode = main.Settings.SafeMode,
                    AutoCheckEngineUpdates = main.Settings.AutoCheckEngineUpdates
                },
                SystemServices = new SystemServicesTelemetry
                {
                    BfeRunning = WinServices.IsBfeRunning(),
                    WinDivertInstalled = WinServices.IsWinDivertInstalled(),
                    ZapretServiceInstalled = status.ServiceInstalled,
                    ZapretServiceStatus = status.ServiceState.ToString(),
                    TcpTimestampsEnabled = WinServices.AreTcpTimestampsEnabled(),
                    DnsServers = dnsList
                },
                DomainLists = new DomainListsTelemetry
                {
                    ListGeneralLines = CountLines("list-general.txt"),
                    ListYoutubeLines = CountLines("list-youtube.txt"),
                    ListDiscordLines = CountLines("list-discord.txt"),
                    ListGeneralUserLines = CountLines("list-general-user.txt"),
                    ListYoutubeUserLines = CountLines("list-youtube-user.txt"),
                    ListDiscordUserLines = CountLines("list-discord-user.txt"),
                    IpsetAllLines = CountLines("ipset-all.txt"),
                    IpsetDiscordLines = CountLines("ipset-discord.txt")
                },
                StrategyTestMatrix = strategyItems,
                AutoTunerResults = autoTunerItems,
                ActiveProbes = activeProbes,
                RecentLogs = logEntries
            };
        }

        public static string GenerateJson(ProviderTelemetryDump dump)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            return JsonSerializer.Serialize(dump, options);
        }

        public static string GenerateMarkdown(ProviderTelemetryDump dump)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 📊 Полный диагностический отчёт и телеметрия провайдера");
            sb.AppendLine($"*Сформировано: {dump.GeneratedAt:yyyy-MM-dd HH:mm:ss} | Zapret GUI v{dump.AppVersion}*");
            sb.AppendLine();

            sb.AppendLine("## 1. Профиль провайдера и системы");
            sb.AppendLine($"- **Провайдер:** {(string.IsNullOrWhiteSpace(dump.Provider.Name) ? "Не указан" : dump.Provider.Name)} (ASN: {(string.IsNullOrWhiteSpace(dump.Provider.Asn) ? "Не указан" : dump.Provider.Asn)})");
            sb.AppendLine($"- **ОС:** {dump.OsVersion} | Права администратора: {(dump.IsAdmin ? "Да" : "Нет")} | Портативный режим: {(dump.IsPortable ? "Да" : "Нет")}");
            sb.AppendLine($"- **Службы:** BFE: {(dump.SystemServices.BfeRunning ? "OK" : "Остановлен")}, WinDivert: {(dump.SystemServices.WinDivertInstalled ? "Установлен" : "Нет")}, TCP Timestamps: {(dump.SystemServices.TcpTimestampsEnabled ? "Включены" : "Выключены")}");
            sb.AppendLine($"- **DNS серверы:** {(dump.SystemServices.DnsServers.Count > 0 ? string.Join(", ", dump.SystemServices.DnsServers) : "Автоматически (DHCP)")}");
            sb.AppendLine($"- **Текущее состояние обхода:** {dump.Configuration.BypassState} (Стратегия: «{dump.Configuration.SelectedStrategy}», GameFilter: {dump.Configuration.GameFilterMode}, IPSET: {dump.Configuration.IpsetMode})");
            sb.AppendLine();

            sb.AppendLine("## 2. Списки доменов и IP (Количество активных записей)");
            sb.AppendLine($"- `list-general.txt`: {dump.DomainLists.ListGeneralLines} доменов | `list-youtube.txt`: {dump.DomainLists.ListYoutubeLines} | `list-discord.txt`: {dump.DomainLists.ListDiscordLines}");
            sb.AppendLine($"- Пользовательские: `list-general-user`: {dump.DomainLists.ListGeneralUserLines} | `list-youtube-user`: {dump.DomainLists.ListYoutubeUserLines} | `list-discord-user`: {dump.DomainLists.ListDiscordUserLines}");
            sb.AppendLine($"- IP-фильтры: `ipset-all.txt`: {dump.DomainLists.IpsetAllLines} сетей | `ipset-discord.txt`: {dump.DomainLists.IpsetDiscordLines}");
            sb.AppendLine();

            var tested = dump.StrategyTestMatrix.Where(s => s.TotalChecks > 0).ToList();
            var recommended = tested.Where(s => s.IsRecommended || (s.TotalChecks > 0 && (double)s.PassedCount / s.TotalChecks >= 0.65)).ToList();
            var partial = tested.Where(s => !recommended.Contains(s) && s.PassedCount > 0).ToList();
            var blocked = tested.Where(s => s.PassedCount == 0).ToList();

            sb.AppendLine($"## 3. Результаты тестирования стратегий каталога (Всего протестировано: {tested.Count})");
            if (recommended.Count > 0)
            {
                sb.AppendLine("### 🟢 РАБОЧИЕ СТРАТЕГИИ (Рекомендуемые):");
                foreach (var r in recommended)
                {
                    sb.AppendLine($"- **{r.Name}** [{r.Category}] — `{r.PassedCount}/{r.TotalChecks}` OK за {r.ElapsedSeconds:F1} с");
                    sb.AppendLine($"  - *Параметры:* {r.Description}");
                    if (r.Checks.Count > 0)
                    {
                        var checkSummary = string.Join(" · ", r.Checks.Select(c => $"{c.Title}: {(c.Ok ? $"OK ({c.Milliseconds} мс)" : "FAIL")}"));
                        sb.AppendLine($"  - *Эндпоинты:* {checkSummary}");
                    }
                }
                sb.AppendLine();
            }

            if (partial.Count > 0)
            {
                sb.AppendLine("### 🟡 ЧАСТИЧНО РАБОЧИЕ СТРАТЕГИИ:");
                foreach (var p in partial)
                {
                    sb.AppendLine($"- **{p.Name}** [{p.Category}] — `{p.PassedCount}/{p.TotalChecks}` OK ({p.Description})");
                }
                sb.AppendLine();
            }

            if (blocked.Count > 0)
            {
                sb.AppendLine("### 🔴 ПОЛНОСТЬЮ ЗАБЛОКИРОВАННЫЕ СТРАТЕГИИ:");
                foreach (var b in blocked)
                {
                    sb.AppendLine($"- **{b.Name}** [{b.Category}] — `0/{b.TotalChecks}` ({b.Description})");
                }
                sb.AppendLine();
            }

            if (dump.AutoTunerResults.Count > 0)
            {
                sb.AppendLine($"## 4. Результаты интеллектуального автоподбора ({dump.AutoTunerResults.Count} гипотез)");
                var bestAuto = dump.AutoTunerResults.OrderByDescending(a => a.Score).FirstOrDefault();
                if (bestAuto != null)
                {
                    sb.AppendLine($"> 🏆 **Лучший синтезированный вариант:** #{bestAuto.StepNumber} «{bestAuto.Title}» — Скоринг: {bestAuto.Score}/100, Доступность: {bestAuto.SuccessRate:0}%, Пинг: ~{bestAuto.AvgRttMs} мс");
                }
                sb.AppendLine();
                sb.AppendLine("| # | Описание гипотезы | Успех | Пинг | Балл | Контрольные точки |");
                sb.AppendLine("|---|-------------------|-------|------|------|-------------------|");
                foreach (var step in dump.AutoTunerResults.OrderBy(a => a.StepNumber))
                {
                    sb.AppendLine($"| #{step.StepNumber} | {step.Title} | {step.SuccessRate:0}% | {step.AvgRttMs} мс | {step.Score}/100 | {step.Details} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## 5. Выводы по характеристикам ТСПУ / DPI оператора");
            sb.AppendLine("- **Ключевой фактор обхода:** " + (recommended.Any(r => r.Description.Contains("ts")) ? "Подмена TCP меток времени (`fooling ts` / `badsum,ts`)" : "Разделение пакетов"));
            sb.AppendLine("- **Эффективное количество повторов:** " + (recommended.Any(r => r.Description.Contains("11")) ? "11 повторов (repeats=11)" : "6 повторов"));
            sb.AppendLine("- **Рекомендуемая позиция разделения:** " + (recommended.Any(r => r.Description.Contains("split-pos 1")) ? "split-pos=1" : "sniext / midsld"));
            sb.AppendLine();

            sb.AppendLine("---");
            sb.AppendLine("*Отчёт сформирован автоматически для передачи разработчику или анализа в чате.*");

            return sb.ToString();
        }

        public static async Task<(bool Ok, string Message, string? FilePath)> CreateDiagnosticZipArchiveAsync(
            ProviderTelemetryDump dump, string targetZipPath)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "ZapretGUI_Telemetry_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                var mdPath = Path.Combine(tempDir, "provider_telemetry_report.md");
                var jsonPath = Path.Combine(tempDir, "telemetry_dump.json");
                var logPath = Path.Combine(tempDir, "recent_activity.log");

                await File.WriteAllTextAsync(mdPath, GenerateMarkdown(dump), Encoding.UTF8).ConfigureAwait(false);
                await File.WriteAllTextAsync(jsonPath, GenerateJson(dump), Encoding.UTF8).ConfigureAwait(false);
                await File.WriteAllLinesAsync(logPath, dump.RecentLogs, Encoding.UTF8).ConfigureAwait(false);

                if (File.Exists(targetZipPath)) File.Delete(targetZipPath);
                ZipFile.CreateFromDirectory(tempDir, targetZipPath, CompressionLevel.Optimal, false);

                try { Directory.Delete(tempDir, true); } catch { }

                return (true, "Диагностический архив успешно создан", targetZipPath);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка создания архива: " + ex.Message, null);
            }
        }
    }
}
