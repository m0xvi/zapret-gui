using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ZapretGui.Core;

namespace ZapretGui.WindowsIntegrationHarness
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (IsFakeWinwsProcess() || args.Any(arg => string.Equals(arg, "--fake-winws", StringComparison.OrdinalIgnoreCase)))
                return RunFakeWinws(args);

            if (ShouldRunControllerFailure(args))
                return RunControllerFailureIntegration();

            if (ShouldRunController(args))
                return RunControllerIntegration();

            if (ShouldRunRealServices(args))
                return RunRealServiceIntegration();

            if (ShouldRunRealDriverLock(args))
                return RunRealDriverLockIntegration();

            if (!ShouldRun(args))
            {
                Console.WriteLine("Windows integration harness пропущен: нужен --run или ZAPRET_GUI_RUN_WINDOWS_INTEGRATION=1.");
                return 0;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Windows integration harness пропущен вне Windows.");
                return 0;
            }

            var services = ReadServiceList();
            var failures = new List<string>();
            foreach (var name in services)
            {
                try
                {
                    CheckReadOnlyService(name, failures);
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: исключение {ex.Message}");
                }
            }

            // Намеренно не вызываем Stop, Start, Delete, InstallService или winws.exe.
            // Изменяющий систему прогон должен оставаться отдельным ручным сценарием.
            if (failures.Count > 0)
            {
                foreach (var failure in failures) Console.Error.WriteLine("✗ " + failure);
                return 1;
            }

            Console.WriteLine("✓ Read-only Windows-проверка BFE/WinDivert прошла; системные действия не выполнялись.");
            return 0;
        }

        private static bool ShouldRun(string[] args)
            => Array.Exists(args, arg => string.Equals(arg, "--run", StringComparison.OrdinalIgnoreCase))
               || string.Equals(Environment.GetEnvironmentVariable("ZAPRET_GUI_RUN_WINDOWS_INTEGRATION"), "1", StringComparison.Ordinal);

        private static bool ShouldRunController(string[] args)
            => Array.Exists(args, arg => string.Equals(arg, "--run-controller", StringComparison.OrdinalIgnoreCase))
               || string.Equals(Environment.GetEnvironmentVariable("ZAPRET_GUI_RUN_CONTROLLER_INTEGRATION"), "1", StringComparison.Ordinal);

        private static bool ShouldRunControllerFailure(string[] args)
            => Array.Exists(args, arg => string.Equals(arg, "--run-controller-failure", StringComparison.OrdinalIgnoreCase))
               || string.Equals(Environment.GetEnvironmentVariable("ZAPRET_GUI_RUN_CONTROLLER_FAILURE"), "1", StringComparison.Ordinal);

        private static bool ShouldRunRealServices(string[] args)
            => Array.Exists(args, arg => string.Equals(arg, "--run-real-services", StringComparison.OrdinalIgnoreCase))
               || string.Equals(Environment.GetEnvironmentVariable("ZAPRET_GUI_RUN_REAL_SERVICE_INTEGRATION"), "1", StringComparison.Ordinal);

        private static bool ShouldRunRealDriverLock(string[] args)
            => Array.Exists(args, arg => string.Equals(arg, "--run-real-driver-lock", StringComparison.OrdinalIgnoreCase))
               || string.Equals(Environment.GetEnvironmentVariable("ZAPRET_GUI_RUN_REAL_DRIVER_LOCK"), "1", StringComparison.Ordinal);

        private static int RunRealDriverLockIntegration()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Проверка блокировки реального драйвера пропущена вне Windows.");
                return 0;
            }

            if (!string.Equals(
                    Environment.GetEnvironmentVariable("ZAPRET_GUI_REAL_DRIVER_CONFIRMATION"),
                    "RUN-REAL-DRIVER-LOCK-CHECKS",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine("✗ Для проверки блокировки драйвера требуется подтверждение RUN-REAL-DRIVER-LOCK-CHECKS.");
                return 1;
            }

            var service = Environment.GetEnvironmentVariable("ZAPRET_GUI_REAL_DRIVER_SERVICE")?.Trim() ?? "";
            if (!string.Equals(service, WinServices.WinDivertService, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(service, WinServices.WinDivert14Service, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("✗ Для проверки драйвера разрешены только WinDivert и WinDivert14.");
                return 1;
            }

            var driverPath = Environment.GetEnvironmentVariable("ZAPRET_GUI_REAL_DRIVER_PATH")?.Trim() ?? "";
            if (!Path.IsPathFullyQualified(driverPath) ||
                !string.Equals(Path.GetFileName(driverPath), "WinDivert64.sys", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(driverPath))
            {
                Console.Error.WriteLine("✗ Укажите существующий полный путь к bin\\WinDivert64.sys.");
                return 1;
            }

            var binDirectory = Directory.GetParent(driverPath);
            var engineRoot = binDirectory?.Parent;
            if (binDirectory == null ||
                !string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase) ||
                engineRoot == null)
            {
                Console.Error.WriteLine("✗ Путь драйвера должен иметь структуру <движок>\\bin\\WinDivert64.sys.");
                return 1;
            }

            if (Shell.IsProcessRunning("winws"))
            {
                Console.Error.WriteLine("✗ Найден работающий winws.exe — драйвер не трогаем.");
                return 1;
            }

            var failures = new List<string>();
            var initial = WinServices.Query(service);
            if (initial != ServiceState.Stopped)
            {
                Console.Error.WriteLine($"✗ Служба {service} должна быть установлена и остановлена перед тестом; сейчас {initial}.");
                return 1;
            }

            try
            {
                if (WinServices.IsFileLocked(driverPath))
                {
                    failures.Add("драйвер уже заблокирован до запуска тестовой службы");
                }
                else
                {
                    var start = WinServices.Start(service);
                    if (!start.Ok || !WaitForServiceState(service, ServiceState.Running))
                    {
                        failures.Add($"служба {service} не запустилась");
                    }
                    else
                    {
                        var locked = WinServices.IsFileLocked(driverPath);
                        var exclusiveOpen = TryOpenDriverExclusively(driverPath);
                        if (!locked || exclusiveOpen)
                        {
                            failures.Add(
                                $"WinDivert64.sys не стал недоступен для эксклюзивной записи: locked={locked}, exclusiveOpen={exclusiveOpen}");
                        }
                        else
                        {
                            Console.WriteLine($"✓ {service}: загруженный WinDivert64.sys действительно заблокирован ядром Windows.");
                            Console.WriteLine("✓ Обновление должно пропустить этот файл и показать предупреждение до перезагрузки.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add("исключение во время проверки: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (!RestoreRealServiceState(service, ServiceState.Stopped))
                        failures.Add($"не удалось остановить и восстановить службу {service}");
                    else
                        Console.WriteLine($"✓ {service}: служба остановлена, исходное состояние восстановлено.");
                }
                catch (Exception ex)
                {
                    failures.Add("ошибка остановки драйверной службы: " + ex.Message);
                }

                try
                {
                    if (!WinServices.WaitForDriverUnloadAsync(engineRoot.FullName, 30000)
                            .GetAwaiter().GetResult() || WinServices.IsFileLocked(driverPath))
                        failures.Add("WinDivert64.sys остался заблокирован после остановки службы");
                    else
                        Console.WriteLine("✓ WinDivert64.sys снова доступен после выгрузки драйвера.");
                }
                catch (Exception ex)
                {
                    failures.Add("ошибка ожидания выгрузки драйвера: " + ex.Message);
                }
            }

            if (failures.Count > 0)
            {
                foreach (var failure in failures) Console.Error.WriteLine("✗ " + failure);
                return 1;
            }

            Console.WriteLine("✓ Настоящий Windows-тест блокировки драйвера прошёл без изменения файла WinDivert64.sys.");
            return 0;
        }

        private static bool TryOpenDriverExclusively(string path)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static int RunRealServiceIntegration()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Интеграция с реальными службами пропущена вне Windows.");
                return 0;
            }

            if (!string.Equals(
                    Environment.GetEnvironmentVariable("ZAPRET_GUI_REAL_SERVICE_CONFIRMATION"),
                    "RUN-REAL-SERVICE-CHECKS",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine("✗ Для изменения реальных служб требуется подтверждение RUN-REAL-SERVICE-CHECKS.");
                return 1;
            }

            string[] services;
            try
            {
                services = ReadRealServiceList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("✗ Некорректный список реальных служб: " + ex.Message);
                return 1;
            }

            // Не трогаем активный обход: workflow должен запускаться на выделенном
            // runner, где winws.exe и служба zapret заранее остановлены.
            if (Shell.IsProcessRunning("winws"))
            {
                Console.Error.WriteLine("✗ Найден работающий winws.exe — реальные службы не изменяются.");
                return 1;
            }

            var failures = new List<string>();
            foreach (var name in services)
            {
                try
                {
                    CheckRealServiceLifecycle(name, failures);
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: исключение {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                foreach (var failure in failures) Console.Error.WriteLine("✗ " + failure);
                return 1;
            }

            Console.WriteLine("✓ Интеграция с реальными службами прошла; исходные состояния восстановлены.");
            return 0;
        }

        private static string[] ReadRealServiceList()
        {
            var configured = Environment.GetEnvironmentVariable("ZAPRET_GUI_REAL_SERVICES");
            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("ZAPRET_GUI_REAL_SERVICES не задан");

            var result = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (result.Length == 0)
                throw new InvalidOperationException("список служб пуст");

            var allowed = new[]
            {
                "BFE",
                WinServices.ZapretService,
                WinServices.WinDivertService,
                WinServices.WinDivert14Service
            };
            foreach (var name in result)
            {
                if (!allowed.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "разрешены только BFE, zapret, WinDivert и WinDivert14");

                if (string.Equals(name, "BFE", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Environment.GetEnvironmentVariable("ZAPRET_GUI_ALLOW_CRITICAL_SERVICE"), "1", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "для BFE требуется отдельное подтверждение ZAPRET_GUI_ALLOW_CRITICAL_SERVICE=1");
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void CheckRealServiceLifecycle(string name, List<string> failures)
        {
            var initial = WinServices.Query(name);
            if (initial is ServiceState.NotInstalled or ServiceState.Unknown or ServiceState.StartPending
                or ServiceState.StopPending or ServiceState.Paused)
            {
                failures.Add($"{name}: исходное состояние нельзя безопасно проверить ({initial})");
                return;
            }

            try
            {
                var target = initial == ServiceState.Running ? ServiceState.Stopped : ServiceState.Running;
                var action = target == ServiceState.Running
                    ? WinServices.Start(name)
                    : WinServices.Stop(name);
                if (!action.Ok || !WaitForServiceState(name, target))
                {
                    failures.Add($"{name}: не удалось перевести службу в {target}");
                    return;
                }

                Console.WriteLine($"✓ {name}: переход {initial} → {target}");
            }
            finally
            {
                try
                {
                    if (!RestoreRealServiceState(name, initial))
                        failures.Add($"{name}: не удалось восстановить состояние {initial}");
                    else if (WinServices.Query(name) == initial)
                        Console.WriteLine($"✓ {name}: восстановлено состояние {initial}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: ошибка восстановления ({ex.Message})");
                }
            }
        }

        private static bool RestoreRealServiceState(string name, ServiceState expected)
        {
            var current = WinServices.Query(name);
            if (current == expected)
                return true;

            if (current == ServiceState.StartPending && !WaitForServiceState(name, ServiceState.Running))
                return false;
            if (current == ServiceState.StopPending && !WaitForServiceState(name, ServiceState.Stopped))
                return false;

            current = WinServices.Query(name);
            if (current == expected)
                return true;
            if (current is not ServiceState.Running and not ServiceState.Stopped)
                return false;

            var restore = expected == ServiceState.Running
                ? WinServices.Start(name)
                : WinServices.Stop(name);
            return restore.Ok && WaitForServiceState(name, expected);
        }

        private static bool WaitForServiceState(string name, ServiceState expected)
            => Shell.WaitForAsync(() => WinServices.Query(name) == expected, 30000)
                .GetAwaiter().GetResult();

        private static bool IsFakeWinwsProcess()
        {
            var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            return string.Equals(processName, "winws", StringComparison.OrdinalIgnoreCase);
        }

        private static int RunFakeWinws(string[] args)
        {
            if (args.Any(arg => string.Equals(arg, "--integration-fail", StringComparison.OrdinalIgnoreCase)))
                return 7;

            using var wait = new ManualResetEventSlim(false);
            wait.Wait();
            return 0;
        }

        private static int RunControllerIntegration()
            => RunControllerScenario(failureOnly: false);

        private static int RunControllerFailureIntegration()
            => RunControllerScenario(failureOnly: true);

        private static int RunControllerScenario(bool failureOnly)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Controller integration harness пропущен вне Windows.");
                return 0;
            }

            if (Shell.IsProcessRunning("winws"))
            {
                Console.Error.WriteLine("✗ Уже запущен winws.exe — тест отказывается его останавливать.");
                return 1;
            }

            var root = Path.Combine(Path.GetTempPath(), "ZapretGUI-controller-" + Guid.NewGuid().ToString("N"));
            BypassController? controller = null;
            try
            {
                var bin = Path.Combine(root, "bin");
                Directory.CreateDirectory(Path.Combine(root, "lists"));
                Directory.CreateDirectory(bin);
                InstallFakeWinws(bin);

                var settings = new AppSettings
                {
                    EnginePath = root,
                    SelectedStrategy = "до теста",
                    AutoCheckEngineUpdates = false,
                    ShowWinwsConsole = false
                };
                controller = new BypassController(
                    settings,
                    _ => ServiceState.NotInstalled,
                    () => (true, "Тестовый runtime: timestamps не изменялись"));
                if (!failureOnly)
                {
                    var strategy = new StrategyInfo
                    {
                        Name = "integration-success",
                        Args = new List<string> { "/c", "ping", "127.0.0.1", "-n", "60", "-w", "1000" }
                    };

                    var started = controller.StartAsync(strategy, GameFilterMode.Disabled, false,
                        testMode: true).GetAwaiter().GetResult();
                    if (!started.Ok || !Shell.IsProcessRunning("winws"))
                        return FailControllerTest("реальный процесс winws.exe не запустился: " + started.Message);

                    var stopped = controller.StopAsync().GetAwaiter().GetResult();
                    if (!stopped.Ok || Shell.IsProcessRunning("winws"))
                        return FailControllerTest("реальный процесс winws.exe не остановился: " + stopped.Message);
                }

                var failedStrategy = new StrategyInfo
                {
                    Name = "integration-failure",
                    Args = new List<string> { "/c", "exit", "7" }
                };
                var failed = controller.StartAsync(failedStrategy, GameFilterMode.Disabled, false,
                    testMode: true).GetAwaiter().GetResult();
                var failedProcessStillRunning = Shell.IsProcessRunning("winws");
                var stateChanged = settings.SelectedStrategy != "до теста";
                if (failed.Ok || failedProcessStillRunning || stateChanged)
                    return FailControllerTest($"неудачный запуск не восстановил состояние: Ok={failed.Ok}, running={failedProcessStillRunning}, stateChanged={stateChanged}, message={failed.Message}");

                Console.WriteLine(failureOnly
                    ? "✓ Controller failure recovery: состояние после неудачного запуска сохранено."
                    : "✓ Controller integration: запуск и остановка реального дочернего процесса прошли.");
                return 0;
            }
            catch (Exception ex)
            {
                return FailControllerTest(ex.Message);
            }
            finally
            {
                try { controller?.StopCapture(); } catch { }
                if (Shell.IsProcessRunning("winws")) Shell.KillProcess("winws");
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        private static void InstallFakeWinws(string bin)
        {
            var commandShell = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (!File.Exists(commandShell))
                throw new InvalidOperationException("Не найден системный command shell для fake winws.exe");

            // Это безопасный системный процесс с именем winws.exe: он не загружает zapret,
            // WinDivert или драйвер, но позволяет проверить настоящий жизненный цикл процесса.
            File.Copy(commandShell, Path.Combine(bin, "winws.exe"), true);
        }

        private static int FailControllerTest(string message)
        {
            Console.Error.WriteLine("✗ Controller-интеграция: " + message);
            return 1;
        }

        private static string[] ReadServiceList()
        {
            var configured = Environment.GetEnvironmentVariable("ZAPRET_GUI_INTEGRATION_SERVICES");
            if (string.IsNullOrWhiteSpace(configured))
                return new[] { "BFE", WinServices.WinDivertService, WinServices.WinDivert14Service };

            var result = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in result)
            {
                if (!string.Equals(name, "BFE", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, WinServices.WinDivertService, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, WinServices.WinDivert14Service, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Разрешены только BFE, WinDivert и WinDivert14");
            }
            return result;
        }

        private static void CheckReadOnlyService(string name, List<string> failures)
        {
            var raw = Shell.Run("sc.exe", new[] { "query", name }, 15000);
            var parsed = WinServices.ParseServiceState(raw.All, raw.ExitCode);
            var queried = WinServices.Query(name);
            if (parsed != queried)
                failures.Add($"{name}: parser={parsed}, Query={queried}");

            var config = Shell.Run("sc.exe", new[] { "qc", name }, 15000);
            if (queried != ServiceState.NotInstalled && !config.Ok)
                failures.Add($"{name}: sc qc завершился с ошибкой: {config.All}");

            Console.WriteLine($"✓ {name}: {ServiceHealthSnapshot.FormatState(queried)}");
        }
    }
}
