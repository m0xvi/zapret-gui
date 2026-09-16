using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGui.Core
{
    public enum DiagStatus { Ok, Warning, Error, Info }

    public sealed class DiagnosticItem
    {
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
        public string FixHint { get; init; } = "";
        public DiagStatus Status { get; init; }

        public string Icon => Status switch
        {
            DiagStatus.Ok => "✓",
            DiagStatus.Warning => "!",
            DiagStatus.Error => "✕",
            _ => "i"
        };

        public string SeverityKey => Status switch
        {
            DiagStatus.Ok => "Success",
            DiagStatus.Warning => "Warning",
            DiagStatus.Error => "Danger",
            _ => "Info"
        };
    }

    /// <summary>Проверки из service.bat -&gt; Run Diagnostics, переписанные на C#.</summary>
    public static class DiagnosticsService
    {
        public static async Task<List<DiagnosticItem>> RunAsync(AppSettings settings, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var items = new List<DiagnosticItem>();
            var engineRoot = settings.EnginePath;

            void Step(string text) => progress?.Report(text);

            // 1. Права администратора
            Step("Проверка прав администратора");
            var isAdmin = Shell.IsAdmin();
            items.Add(new DiagnosticItem
            {
                Title = "Права администратора",
                Status = isAdmin ? DiagStatus.Ok : DiagStatus.Error,
                Details = isAdmin ? "Приложение запущено от имени администратора" : "Нет прав администратора",
                FixHint = isAdmin ? "" : "Закройте приложение и запустите exe от имени администратора (или перезапустите через кнопку в шапке)"
            });

            // 2. Файлы движка
            Step("Проверка файлов движка");
            var winws = Path.Combine(engineRoot, "bin", "winws.exe");
            var divert64 = Path.Combine(engineRoot, "bin", "WinDivert64.sys");
            var divertDll = Path.Combine(engineRoot, "bin", "WinDivert.dll");
            var haveBin = File.Exists(winws) && File.Exists(divert64) && File.Exists(divertDll);
            items.Add(new DiagnosticItem
            {
                Title = "Файлы движка (bin)",
                Status = haveBin ? DiagStatus.Ok : DiagStatus.Error,
                Details = haveBin
                    ? $"winws.exe, WinDivert64.sys и WinDivert.dll на месте ({engineRoot})"
                    : "Не найдены winws.exe / WinDivert64.sys / WinDivert.dll",
                FixHint = haveBin ? "" : "Скачайте движок на странице «Обновления» или укажите верную папку в настройках"
            });

            // 3. Кириллица и спецсимволы в пути
            Step("Проверка пути к движку");
            var badPath = Regex.IsMatch(engineRoot, @"[^\x00-\x7F]");
            items.Add(new DiagnosticItem
            {
                Title = "Путь к движку без спецсимволов",
                Status = badPath ? DiagStatus.Warning : DiagStatus.Ok,
                Details = engineRoot,
                FixHint = badPath ? "В пути есть кириллица или спецсимволы — перенесите движок в C:\\Zapret или %LOCALAPPDATA%\\ZapretGUI\\engine" : ""
            });

            // 4. OneDrive в пути
            Step("Проверка синхронизации OneDrive");
            var oneDriveIssue = engineRoot.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
            items.Add(new DiagnosticItem
            {
                Title = "Опасные папки (OneDrive)",
                Status = oneDriveIssue ? DiagStatus.Warning : DiagStatus.Ok,
                Details = oneDriveIssue ? "Папка движка синхронизируется OneDrive" : "Путь не пересекается с OneDrive",
                FixHint = oneDriveIssue ? "Синхронизация может блокировать WinDivert64.sys. Перенесите папку движка" : ""
            });

            // 5. BFE
            Step("Проверка службы Base Filtering Engine");
            var bfe = WinServices.Query("BFE");
            items.Add(new DiagnosticItem
            {
                Title = "Служба BFE (Base Filtering Engine)",
                Status = bfe == ServiceState.Running ? DiagStatus.Ok : DiagStatus.Error,
                Details = "Состояние: " + bfe,
                FixHint = bfe == ServiceState.Running ? "" : "Запустите: sc start BFE (или через services.msc)"
            });

            // 6. TCP timestamps
            Step("Проверка TCP timestamps");
            var tcp = Shell.Run("netsh", new[] { "interface", "tcp", "show", "global" }, 10000).All;
            var timestampsOk = false;
            foreach (var line in tcp.Split('\n'))
            {
                if (line.IndexOf("timestamps", StringComparison.OrdinalIgnoreCase) < 0) continue;
                timestampsOk = line.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0
                               && line.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) < 0;
                break;
            }
            items.Add(new DiagnosticItem
            {
                Title = "TCP timestamps включены",
                Status = timestampsOk ? DiagStatus.Ok : DiagStatus.Warning,
                Details = "Нужны для части стратегий обхода",
                FixHint = timestampsOk ? "" : "Включите командой: netsh interface tcp set global timestamps=enabled"
            });

            // 7. Прокси
            Step("Проверка прокси");
            var proxyEnabled = IsProxyEnabled();
            items.Add(new DiagnosticItem
            {
                Title = "Системный прокси",
                Status = proxyEnabled ? DiagStatus.Warning : DiagStatus.Ok,
                Details = proxyEnabled ? "В системе включён прокси-сервер" : "Прокси не используется",
                FixHint = proxyEnabled ? "Прокси может конфликтовать с обходом. Отключите его, если сайты не открываются" : ""
            });

            // 8. VPN-адаптеры
            Step("Проверка VPN-адаптеров");
            var adapters = await Task.Run(() => GetUpAdapters(), ct).ConfigureAwait(false);
            var vpn = adapters.Where(a => Regex.IsMatch(a, "vpn|tun|tap|wireguard|openvpn|outline|amnezia|wintun",
                RegexOptions.IgnoreCase)).ToList();
            items.Add(new DiagnosticItem
            {
                Title = "VPN-адаптеры",
                Status = vpn.Count > 0 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = vpn.Count > 0 ? "Активны: " + string.Join(", ", vpn) : "Активных VPN-адаптеров нет",
                FixHint = vpn.Count > 0 ? "VPN меняет маршрутизацию и часто ломает zapret. Отключите VPN для проверки" : ""
            });

            // 9. Конфликтующие службы
            Step("Проверка конфликтующих служб");
            var conflicts = new List<string>();
            var allServices = await Task.Run(
                () => Shell.Run("sc.exe", new[] { "query", "type=", "service", "state=", "all" }, 20000).All, ct)
                .ConfigureAwait(false);

            foreach (var pair in new[]
                     {
                         ("AdguardSvc.exe", "Adguard"), ("Killer", "Killer (Intel)"),
                         ("Intel Connectivity Network Service", "Intel Connectivity"),
                         ("Check Point", "Check Point"), ("SmartByte", "SmartByte")
                     })
            {
                if (allServices.IndexOf(pair.Item1, StringComparison.OrdinalIgnoreCase) >= 0) conflicts.Add(pair.Item2);
            }
            items.Add(new DiagnosticItem
            {
                Title = "Конфликтующие службы",
                Status = conflicts.Count > 0 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = conflicts.Count > 0 ? "Найдены: " + string.Join(", ", conflicts) : "Конфликтов не найдено",
                FixHint = conflicts.Count > 0 ? "Эти службы перехватывают трафик. При проблемах отключите их для теста" : ""
            });

            // 10. Другие обходы
            Step("Проверка других обходов DPI");
            var others = new List<string>();
            foreach (var name in new[] { "goodbyedpi", "GoodbyeDPI", "dpitunnel", "TgWsProxy", "tg-ws-proxy", "byedpi" })
            {
                if (Shell.IsProcessRunning(name)) others.Add(name);
            }
            var winwsPaths = await Task.Run(() => GetWinwsPaths(), ct).ConfigureAwait(false);
            var foreignWinws = winwsPaths
                .Where(p => !p.StartsWith(engineRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            items.Add(new DiagnosticItem
            {
                Title = "Другие программы обхода",
                Status = others.Count > 0 || foreignWinws.Count > 0 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = others.Count > 0 || foreignWinws.Count > 0
                    ? "Найдены: " + string.Join(", ", others.Concat(foreignWinws.Select(p => "winws: " + p)))
                    : "Посторонних обходов не найдено",
                FixHint = others.Count > 0 || foreignWinws.Count > 0
                    ? "Одновременно должен работать только один обход — иначе WinDivert конфликтует"
                    : ""
            });

            // 11. WinDivert службы
            Step("Проверка остаточных служб WinDivert");
            var divertServices = new[] { "WinDivert", "WinDivert14" }
                .Where(s => WinServices.Query(s) != ServiceState.NotInstalled)
                .ToList();
            items.Add(new DiagnosticItem
            {
                Title = "Остаточные службы WinDivert",
                Status = divertServices.Count > 0 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = divertServices.Count > 0 ? "Найдены: " + string.Join(", ", divertServices) : "Остатков нет",
                FixHint = divertServices.Count > 0 ? "Нажмите «Полностью удалить службы» — они мешают новому запуску" : ""
            });

            // 12. Служба zapret
            Step("Проверка службы zapret");
            var zapretState = WinServices.Query(WinServices.ZapretService);
            var strategyName = WinServices.GetInstalledStrategyName();
            items.Add(new DiagnosticItem
            {
                Title = "Служба zapret",
                Status = zapretState == ServiceState.Running ? DiagStatus.Ok : DiagStatus.Info,
                Details = zapretState == ServiceState.NotInstalled
                    ? "Служба не установлена (обход запускается вручную)"
                    : $"Состояние: {zapretState}" + (strategyName.Length > 0 ? $", стратегия: {strategyName}" : ""),
                FixHint = zapretState == ServiceState.StopPending
                    ? "Служба зависла в STOP_PENDING — обычно из-за конфликта с другим обходом. Запустите «Полностью удалить службы»"
                    : ""
            });

            // 13. hosts
            Step("Проверка файла hosts");
            var hostsLines = EngineService.ReadSystemHosts();
            var hostsHasGithub = hostsLines.Any(l => l.Contains("raw.githubusercontent.com"));
            items.Add(new DiagnosticItem
            {
                Title = "Файл hosts (веб-Telegram и голосовой Discord)",
                Status = hostsHasGithub ? DiagStatus.Ok : DiagStatus.Info,
                Details = hostsHasGithub
                    ? "Строки GitHub добавлены в hosts"
                    : "Строки из репозитория в hosts не найдены",
                FixHint = hostsHasGithub ? "" : "Обновите hosts на странице «Обновления» — это помогает работе веб-версии Telegram и голосового чата Discord"
            });

            // 14. Кэш Discord
            Step("Проверка кэша Discord");
            var cacheSize = EngineService.GetDiscordCacheSize();
            items.Add(new DiagnosticItem
            {
                Title = "Кэш Discord",
                Status = cacheSize > 300L * 1024 * 1024 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = cacheSize > 0 ? $"{cacheSize / 1024.0 / 1024.0:0.0} МБ" : "Кэш не найден",
                FixHint = cacheSize > 300L * 1024 * 1024 ? "Большой кэш может мешать подключению — очистите его кнопкой ниже" : ""
            });

            return items;
        }

        private static bool IsProxyEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                var enabled = key?.GetValue("ProxyEnable");
                return enabled is int i && i != 0;
            }
            catch { return false; }
        }

        private static List<string> GetUpAdapters()
        {
            var result = new List<string>();
            var output = Shell.Run("powershell",
                new[] { "-NoProfile", "-Command", "Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object -ExpandProperty Name" },
                20000).All;

            foreach (var line in output.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0 && !name.StartsWith("-", StringComparison.Ordinal)) result.Add(name);
            }
            return result;
        }

        private static List<string> GetWinwsPaths()
        {
            var result = new List<string>();
            var output = Shell.Run("powershell",
                new[] { "-NoProfile", "-Command", "(Get-Process winws -ErrorAction SilentlyContinue).Path" },
                20000).All;

            foreach (var line in output.Split('\n'))
            {
                var path = line.Trim();
                if (path.Length > 2) result.Add(path);
            }
            return result;
        }

        /// <summary>Сброс сетевых настроек (как в README репозитория).</summary>
        public static List<string> ResetNetwork()
        {
            var report = new List<string>();
            var commands = new[]
            {
                "netsh winsock reset",
                "netsh int ip reset all",
                "netsh winhttp reset proxy",
                "ipconfig /flushdns"
            };

            foreach (var command in commands)
            {
                var result = Shell.RunCmd(command);
                report.Add($"{command} → {(result.Ok ? "готово" : "ошибка")}");
            }
            report.Add("Требуется перезагрузка компьютера");
            return report;
        }
    }
}
