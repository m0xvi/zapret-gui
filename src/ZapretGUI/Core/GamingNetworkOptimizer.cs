using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGui.Core
{
    public sealed class GamingOptimizationStatus
    {
        public bool IsOptimized { get; init; }
        public bool TcpNoDelayEnabled { get; init; }
        public bool TcpAckFrequencyEnabled { get; init; }
        public bool MultimediaThrottlingDisabled { get; init; }
        public string Summary { get; init; } = "";
    }

    /// <summary>
    /// Оптимизация сетевых задержек Windows для онлайн-игр (отключение алгоритма Nagle и задержки ACK).
    /// </summary>
    public static class GamingNetworkOptimizer
    {
        private const string MultimediaKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

        [SupportedOSPlatform("windows")]
        public static GamingOptimizationStatus CheckStatus()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new GamingOptimizationStatus { Summary = "Доступно только в Windows" };
            }

            try
            {
                var nic = DnsManagementService.GetActiveInterface();
                var adapterId = nic?.Id ?? "";

                var tcpNoDelay = false;
                var tcpAck = false;

                if (!string.IsNullOrEmpty(adapterId))
                {
                    using var ifKey = Registry.LocalMachine.OpenSubKey($@"{InterfacesKey}\{adapterId}");
                    if (ifKey != null)
                    {
                        var ndVal = ifKey.GetValue("TCPNoDelay");
                        var ackVal = ifKey.GetValue("TcpAckFrequency");
                        tcpNoDelay = ndVal is int nd && nd == 1;
                        tcpAck = ackVal is int ack && ack == 1;
                    }
                }

                var mmThrottling = false;
                using var mmKey = Registry.LocalMachine.OpenSubKey(MultimediaKey);
                if (mmKey != null)
                {
                    var ntVal = mmKey.GetValue("NetworkThrottlingIndex");
                    var srVal = mmKey.GetValue("SystemResponsiveness");
                    mmThrottling = (ntVal is int nt && (uint)nt == 0xFFFFFFFF) && (srVal is int sr && sr == 0);
                }

                var isAll = tcpNoDelay && tcpAck && mmThrottling;
                var summary = isAll
                    ? "Сетевые твики активны: Nagle отключён, минимальный пинг и задержка ACK"
                    : (tcpNoDelay || tcpAck || mmThrottling)
                        ? "Твики применены частично"
                        : "Сетевые параметры Windows по умолчанию";

                return new GamingOptimizationStatus
                {
                    IsOptimized = isAll,
                    TcpNoDelayEnabled = tcpNoDelay,
                    TcpAckFrequencyEnabled = tcpAck,
                    MultimediaThrottlingDisabled = mmThrottling,
                    Summary = summary
                };
            }
            catch (Exception ex)
            {
                return new GamingOptimizationStatus
                {
                    Summary = "Не удалось прочитать параметры реестра: " + ex.Message
                };
            }
        }

        public static async Task<(bool Ok, string Message)> ApplyTweaksAsync()
        {
            if (!OperatingSystem.IsWindows())
                return (false, "Оптимизация доступна только в Windows.");

            if (!Shell.IsAdmin())
                return (false, "Для изменения параметров сети требуются права администратора.");

            return await Task.Run(() =>
            {
                try
                {
                    var nic = DnsManagementService.GetActiveInterface();
                    if (nic == null) return (false, "Активный сетевой интерфейс не найден.");

                    var adapterId = nic.Id;

                    // 1. Применяем TCPNoDelay и TcpAckFrequency к интерфейсу
                    using (var ifKey = Registry.LocalMachine.CreateSubKey($@"{InterfacesKey}\{adapterId}"))
                    {
                        if (ifKey != null)
                        {
                            ifKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                            ifKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                            ifKey.SetValue("TcpDelAckTicks", 0, RegistryValueKind.DWord);
                        }
                    }

                    // 2. Отключаем мультимедийное троттлирование сети
                    using (var mmKey = Registry.LocalMachine.CreateSubKey(MultimediaKey))
                    {
                        if (mmKey != null)
                        {
                            mmKey.SetValue("NetworkThrottlingIndex", -1, RegistryValueKind.DWord); // 0xFFFFFFFF
                            mmKey.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
                        }
                    }

                    AppLog.Info($"[GamingOptimizer] Игровые твики сети успешно применены к адаптеру «{nic.Name}» ({adapterId})");
                    return (true, $"Игровая оптимизация сети успешно применена к «{nic.Name}» (минимальный инпут-лаг пакетов).");
                }
                catch (Exception ex)
                {
                    AppLog.Error("[GamingOptimizer] Ошибка применения твиков: " + ex.Message);
                    return (false, "Ошибка записи реестра: " + ex.Message);
                }
            }).ConfigureAwait(false);
        }

        public static async Task<(bool Ok, string Message)> RevertTweaksAsync()
        {
            if (!OperatingSystem.IsWindows())
                return (false, "Очистка доступна только в Windows.");

            if (!Shell.IsAdmin())
                return (false, "Для изменения параметров сети требуются права администратора.");

            return await Task.Run(() =>
            {
                try
                {
                    var nic = DnsManagementService.GetActiveInterface();
                    if (nic != null)
                    {
                        using var ifKey = Registry.LocalMachine.OpenSubKey($@"{InterfacesKey}\{nic.Id}", true);
                        if (ifKey != null)
                        {
                            try { ifKey.DeleteValue("TCPNoDelay"); } catch { }
                            try { ifKey.DeleteValue("TcpAckFrequency"); } catch { }
                            try { ifKey.DeleteValue("TcpDelAckTicks"); } catch { }
                        }
                    }

                    using var mmKey = Registry.LocalMachine.OpenSubKey(MultimediaKey, true);
                    if (mmKey != null)
                    {
                        try { mmKey.SetValue("NetworkThrottlingIndex", 10, RegistryValueKind.DWord); } catch { }
                        try { mmKey.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord); } catch { }
                    }

                    AppLog.Info("[GamingOptimizer] Сетевые параметры Windows сброшены к стандартным.");
                    return (true, "Параметры сети успешно сброшены к стандартным значениям Windows.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("[GamingOptimizer] Ошибка сброса параметров: " + ex.Message);
                    return (false, "Ошибка сброса: " + ex.Message);
                }
            }).ConfigureAwait(false);
        }
    }
}
