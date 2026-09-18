using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGui.Core
{
    /// <summary>Проверки из service.bat -&gt; Run Diagnostics, переписанные на C#, плюс автоисправления.</summary>
    public static class DiagnosticsService
    {
        public static async Task<List<DiagnosticItem>> RunAsync(AppSettings settings, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var items = new List<DiagnosticItem>();
            var engineRoot = settings.EnginePath;
            const int stepCount = 14;
            var stepNumber = 0;

            void Step(string text)
            {
                stepNumber++;
                progress?.Report($"DIAGNOSTICS_PROGRESS:{stepNumber}/{stepCount} — {text}");
            }

            // 1. Права администратора
            Step("Проверка прав администратора");
            var isAdmin = Shell.IsAdmin();
            items.Add(new DiagnosticItem
            {
                Title = "Права администратора",
                Status = isAdmin ? DiagStatus.Ok : DiagStatus.Error,
                Details = isAdmin ? "Приложение запущено от имени администратора" : "Нет прав администратора",
                FixHint = isAdmin ? "" : "Без прав администратора не работают службы, WinDivert и правка hosts",
                FixId = isAdmin ? "" : "admin",
                FixLabel = "Перезапустить от админа"
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
                FixHint = haveBin ? "" : "Скачайте движок из официального репозитория одной кнопкой",
                FixId = haveBin ? "" : "engine",
                FixLabel = "Скачать движок"
            });

            // 3. Кириллица и спецсимволы в пути
            Step("Проверка пути к движку");
            var badPath = Regex.IsMatch(engineRoot, @"[^\x00-\x7F]");
            items.Add(new DiagnosticItem
            {
                Title = "Путь к движку без спецсимволов",
                Status = badPath ? DiagStatus.Warning : DiagStatus.Ok,
                Details = engineRoot,
                FixHint = badPath ? "Кириллица в пути ломает часть стратегий и скриптов" : "",
                FixId = badPath ? "movengine" : "",
                FixLabel = "Перенести движок"
            });

            // 4. OneDrive в пути
            Step("Проверка синхронизации OneDrive");
            var oneDriveIssue = engineRoot.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
            items.Add(new DiagnosticItem
            {
                Title = "Опасные папки (OneDrive)",
                Status = oneDriveIssue ? DiagStatus.Warning : DiagStatus.Ok,
                Details = oneDriveIssue ? "Папка движка синхронизируется OneDrive" : "Путь не пересекается с OneDrive",
                FixHint = oneDriveIssue ? "Синхронизация может блокировать WinDivert64.sys" : "",
                FixId = oneDriveIssue ? "movengine" : "",
                FixLabel = "Перенести движок"
            });

            // 5. BFE и остаточные WinDivert: один read-only снимок сохраняется для мастера.
            Step("Проверка служб BFE и WinDivert");
            var serviceHealth = ServiceHealthCache.Capture();
            var bfe = serviceHealth.Bfe;
            items.Add(new DiagnosticItem
            {
                Title = "Служба BFE (Base Filtering Engine)",
                Status = bfe == ServiceState.Running ? DiagStatus.Ok : DiagStatus.Error,
                Details = "Состояние: " + bfe,
                FixHint = bfe == ServiceState.Running ? "" : "BFE нужна драйверу WinDivert для перехвата трафика",
                FixId = bfe == ServiceState.Running ? "" : "bfe",
                FixLabel = "Запустить BFE"
            });

            // 6. TCP timestamps
            Step("Проверка TCP timestamps");
            var tcpState = WinServices.GetTcpTimestampsState();
            items.Add(new DiagnosticItem
            {
                Title = "TCP timestamps включены",
                Status = tcpState.Enabled ? DiagStatus.Ok : DiagStatus.Warning,
                Details = tcpState.Details.Length > 0
                    ? tcpState.Details
                    : "Нужны для части стратегий обхода",
                FixHint = tcpState.Enabled
                    ? ""
                    : "Windows оставила TCP timestamps выключенными — повторите включение и проверьте результат",
                FixId = tcpState.Enabled ? "" : "timestamps",
                FixLabel = "Включить"
            });

            // 7. Прокси
            Step("Проверка прокси");
            var proxyEnabled = IsProxyEnabled();
            items.Add(new DiagnosticItem
            {
                Title = "Системный прокси",
                Status = proxyEnabled ? DiagStatus.Warning : DiagStatus.Ok,
                Details = proxyEnabled ? "В системе включён прокси-сервер" : "Прокси не используется",
                FixHint = proxyEnabled ? "Прокси может конфликтовать с обходом. Отключите его, если сайты не открываются" : "",
                FixId = proxyEnabled ? "proxy" : "",
                FixLabel = "Отключить прокси"
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
                FixHint = vpn.Count > 0 ? "VPN меняет маршрутизацию и часто ломает zapret. Отключите VPN для проверки (вручную — отключать чужое подключение автоматически опасно)" : ""
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
                FixHint = conflicts.Count > 0 ? "Эти службы перехватывают трафик. Кнопка остановит их для теста" : "",
                FixId = conflicts.Count > 0 ? "conflicts" : "",
                FixLabel = "Остановить службы"
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
                    : "",
                FixId = others.Count > 0 || foreignWinws.Count > 0 ? "others" : "",
                FixLabel = "Остановить чужие обходы"
            });

            // 11. WinDivert службы
            Step("Проверка остаточных служб WinDivert");
            var divertServices = new[]
                {
                    (Name: WinServices.WinDivertService, State: serviceHealth.WinDivert),
                    (Name: WinServices.WinDivert14Service, State: serviceHealth.WinDivert14)
                }
                .Where(s => s.State != ServiceState.NotInstalled)
                .Select(s => s.Name + " (" + ServiceHealthSnapshot.FormatState(s.State) + ")")
                .ToList();
            items.Add(new DiagnosticItem
            {
                Title = "Остаточные службы WinDivert",
                Status = divertServices.Count > 0 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = divertServices.Count > 0 ? "Найдены: " + string.Join(", ", divertServices) : "Остатков нет",
                FixHint = divertServices.Count > 0 ? "Остатки мешают новому запуску — удаляются одной кнопкой" : "",
                FixId = divertServices.Count > 0 ? "divert" : "",
                FixLabel = "Удалить остатки"
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
                    ? "Служба зависла в STOP_PENDING — обычно из-за конфликта с другим обходом"
                    : "",
                FixId = zapretState == ServiceState.StopPending ? "zapretstuck" : "",
                FixLabel = "Удалить службы"
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
                FixHint = hostsHasGithub ? "" : "Помогает работе веб-версии Telegram и голосового чата Discord",
                FixId = hostsHasGithub ? "" : "hosts",
                FixLabel = "Обновить hosts"
            });

            // 14. Кэш Discord
            Step("Проверка кэша Discord");
            var cacheSize = EngineService.GetDiscordCacheSize();
            items.Add(new DiagnosticItem
            {
                Title = "Кэш Discord",
                Status = cacheSize > 300L * 1024 * 1024 ? DiagStatus.Warning : DiagStatus.Ok,
                Details = cacheSize > 0 ? $"{cacheSize / 1024.0 / 1024.0:0.0} МБ" : "Кэш не найден",
                FixHint = cacheSize > 300L * 1024 * 1024 ? "Большой кэш может мешать подключению" : "",
                FixId = cacheSize > 300L * 1024 * 1024 ? "cache" : "",
                FixLabel = "Очистить кэш"
            });

            return items;
        }

        // ---------------------------------------------------------------- автоисправления

        /// <summary>Включает автозапуск и запускает службу BFE (нужна драйверу WinDivert).</summary>
        public static (bool Ok, string Message) FixBfe()
        {
            try
            {
                if (!Shell.IsAdmin())
                    return (false, "Нужны права администратора");

                var state = WinServices.Query("BFE");
                if (state == ServiceState.Running)
                    return (true, "Служба BFE уже запущена");
                if (state == ServiceState.NotInstalled)
                    return (false, "Служба BFE не найдена в Windows");

                var config = Shell.Run("sc.exe", new[] { "config", "BFE", "start=", "auto" }, 20000);
                if (!config.Ok)
                    return (false, "Не удалось включить автозапуск BFE: " + FormatShellError(config));

                var start = WinServices.Start("BFE");
                var running = Shell.WaitForAsync(
                    () => WinServices.Query("BFE") == ServiceState.Running, 15000)
                    .GetAwaiter().GetResult();

                AppLog.Info("Служба BFE: " + (running ? "запущена" : "не запустилась"));
                if (running)
                    return (true, "Служба BFE запущена");

                var stateAfter = WinServices.Query("BFE");
                var detail = start.All.Length > 0 ? FormatShellError(start) : "нет ответа от sc.exe";
                return (false, $"BFE не запустилась (состояние: {stateAfter}). {detail}. Откройте services.msc и проверьте зависимости службы.");
            }
            catch (Exception ex)
            {
                return (false, "Ошибка запуска BFE: " + ex.Message);
            }
        }

        private static string FormatShellError(ShellResult result)
        {
            var output = result.All.Replace(Environment.NewLine, " ").Trim();
            return output.Length > 500 ? output.Substring(0, 500) : output;
        }

        /// <summary>Отключает системный прокси (реестр + уведомление системы).</summary>
        public static (bool Ok, string Message) DisableProxy()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (key == null) return (false, "Не найден раздел реестра с настройками прокси");

                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                RefreshInternetSettings();

                AppLog.Info("Системный прокси отключён через приложение");
                return (true, "Системный прокси отключён");
            }
            catch (Exception ex)
            {
                return (false, "Не удалось отключить прокси: " + ex.Message);
            }
        }

        /// <summary>Останавливает службы, перехватывающие трафик (Adguard, Killer и т. п.).</summary>
        public static (bool Ok, string Message) StopConflictingServices()
        {
            try
            {
                var output = Shell.Run("sc.exe",
                    new[] { "query", "type=", "service", "state=", "all" }, 20000).All;

                var markers = new[] { "AdguardSvc", "Adguard", "Killer", "Intel Connectivity", "Check Point", "SmartByte" };
                var stopped = new List<string>();

                // Вывод sc query — блоки «SERVICE_NAME: …» с пустыми строками между ними
                foreach (var block in output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!markers.Any(m => block.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (block.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var nameLine = block.Split('\n')
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => l.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase));
                    if (nameLine == null) continue;

                    var colon = nameLine.IndexOf(':');
                    if (colon < 0) continue;
                    var serviceName = nameLine.Substring(colon + 1).Trim();
                    if (serviceName.Length == 0) continue;

                    var result = WinServices.Stop(serviceName);
                    if (result.Ok) stopped.Add(serviceName);
                }

                if (stopped.Count == 0)
                    return (false, "Запущенных конфликтующих служб не найдено (возможно, они уже остановлены)");

                AppLog.Info("Остановлены конфликтующие службы: " + string.Join(", ", stopped));
                return (true, "Остановлены службы: " + string.Join(", ", stopped));
            }
            catch (Exception ex)
            {
                return (false, "Не удалось остановить службы: " + ex.Message);
            }
        }

        /// <summary>Завершает чужие обходы DPI (goodbyedpi и др.) и чужие winws.exe.</summary>
        public static (bool Ok, string Message) StopForeignBypass(string engineRoot)
        {
            try
            {
                var stopped = new List<string>();
                foreach (var name in new[] { "goodbyedpi", "GoodbyeDPI", "dpitunnel", "TgWsProxy", "tg-ws-proxy", "byedpi" })
                {
                    if (!Shell.IsProcessRunning(name)) continue;
                    Shell.KillProcess(name);
                    stopped.Add(name);
                }

                var ownBin = Path.Combine(engineRoot, "bin");
                var killedWinws = 0;
                foreach (var (pid, path) in LegacyZapret.GetWinwsProcesses())
                {
                    var dir = Path.GetDirectoryName(path) ?? "";
                    if (dir.Equals(ownBin, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(pid);
                        process.Kill(true);
                        killedWinws++;
                    }
                    catch { }
                }
                if (killedWinws > 0) stopped.Add($"winws.exe (чужих: {killedWinws})");

                if (stopped.Count == 0)
                    return (false, "Чужие обходы не найдены (возможно, уже завершены)");

                AppLog.Info("Завершены чужие обходы: " + string.Join(", ", stopped));
                return (true, "Завершены: " + string.Join(", ", stopped));
            }
            catch (Exception ex)
            {
                return (false, "Не удалось завершить чужие обходы: " + ex.Message);
            }
        }

        /// <summary>Удаляет остаточные службы WinDivert/WinDivert14 (когда обход остановлен).</summary>
        public static (bool Ok, string Message) RemoveDivertLeftovers()
        {
            try
            {
                if (Shell.IsProcessRunning("winws") ||
                    WinServices.Query(WinServices.ZapretService) == ServiceState.Running)
                    return (false, "Сначала остановите обход — драйвер сейчас используется");

                var removed = new List<string>();
                foreach (var name in new[] { WinServices.WinDivertService, WinServices.WinDivert14Service })
                {
                    if (!WinServices.Exists(name)) continue;
                    WinServices.Delete(name);
                    removed.Add(name);
                }

                if (removed.Count == 0)
                    return (false, "Остаточных служб не найдено");

                AppLog.Info("Удалены остаточные службы: " + string.Join(", ", removed));
                return (true, "Удалены службы: " + string.Join(", ", removed));
            }
            catch (Exception ex)
            {
                return (false, "Не удалось удалить службы: " + ex.Message);
            }
        }

        /// <summary>
        /// Переносит движок в безопасную папку без кириллицы и OneDrive.
        /// Старая папка остаётся на месте (удалите вручную, когда убедитесь, что всё работает).
        /// </summary>
        public static (bool Ok, string Message) MoveEngineToSafePath(AppSettings settings)
        {
            try
            {
                if (Shell.IsProcessRunning("winws") ||
                    WinServices.Query(WinServices.ZapretService) == ServiceState.Running)
                    return (false, "Сначала остановите обход — файлы движка сейчас заняты");

                var current = settings.EnginePath;
                var target = AppPaths.DefaultEngine;
                if (Regex.IsMatch(target, @"[^\x00-\x7F]") || target.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
                    target = @"C:\ZapretGUI-engine";
                if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
                    target = @"C:\ZapretGUI-engine";

                if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0)
                    return (false, $"Папка {target} уже существует и не пуста — перенесите движок вручную");

                AppLog.Info($"Переношу движок: {current} → {target}");
                CopyDirectory(current, target);

                if (!EngineService.IsEngineReady(target))
                    return (false, "Копирование завершилось, но winws.exe в новой папке не найден");

                settings.EnginePath = target;
                SettingsStore.Save(settings);

                AppLog.Info("Движок перенесён, путь обновлён в настройках");
                return (true, $"Движок перенесён в {target}. Старая папка оставлена — удалите её вручную.");
            }
            catch (Exception ex)
            {
                return (false, "Не удалось перенести движок: " + ex.Message);
            }
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

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        private static void RefreshInternetSettings()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
                InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
            }
            catch { }
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    }
}
