using System;
using System.IO;
using System.Text.Json;

namespace ZapretGui.Core
{
    /// <summary>
    /// Последнее read-only состояние BFE и остаточных служб WinDivert.
    /// Кэш нужен мастеру первого запуска, чтобы показать результат до новой проверки,
    /// но никогда не используется как разрешение на автоматическое исправление.
    /// </summary>
    public sealed class ServiceHealthSnapshot
    {
        public DateTime CheckedAtUtc { get; init; }
        public ServiceState Bfe { get; init; } = ServiceState.Unknown;
        public ServiceState WinDivert { get; init; } = ServiceState.Unknown;
        public ServiceState WinDivert14 { get; init; } = ServiceState.Unknown;

        public bool IsStale => CheckedAtUtc == default ||
            DateTime.UtcNow - CheckedAtUtc > TimeSpan.FromMinutes(15);

        public bool BfeNeedsRecovery => Bfe != ServiceState.Running;

        public bool HasWinDivertLeftovers =>
            WinDivert != ServiceState.NotInstalled || WinDivert14 != ServiceState.NotInstalled;

        public string CheckedText => CheckedAtUtc == default
            ? "Состояние ещё не проверялось"
            : (IsStale ? "Сохранено ранее · устарело: " : "Актуально · проверено: ") +
              CheckedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        public string BfeText => FormatState(Bfe);
        public string WinDivertText => FormatState(WinDivert);
        public string WinDivert14Text => FormatState(WinDivert14);

        public string BfeKey => Bfe == ServiceState.Running ? "Success" : "Warning";
        public string WinDivertKey => HasWinDivertLeftovers ? "Warning" : "Success";

        public static string FormatState(ServiceState state) => state switch
        {
            ServiceState.Running => "работает",
            ServiceState.Stopped => "остановлена",
            ServiceState.StartPending => "запускается",
            ServiceState.StopPending => "останавливается",
            ServiceState.Paused => "приостановлена",
            ServiceState.NotInstalled => "не установлена",
            _ => "состояние неизвестно"
        };
    }

    /// <summary>Хранит только последний снимок служебного состояния, без команд и секретов.</summary>
    public static class ServiceHealthCache
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static ServiceHealthSnapshot? Load()
        {
            try
            {
                if (!File.Exists(AppPaths.ServiceHealthCacheFile)) return null;
                return JsonSerializer.Deserialize<ServiceHealthSnapshot>(
                    File.ReadAllText(AppPaths.ServiceHealthCacheFile), Options);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось прочитать кэш BFE/WinDivert: " + ex.Message);
                return null;
            }
        }

        public static void Save(ServiceHealthSnapshot snapshot)
        {
            try
            {
                var temporary = AppPaths.ServiceHealthCacheFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
                if (File.Exists(AppPaths.ServiceHealthCacheFile))
                    File.Replace(temporary, AppPaths.ServiceHealthCacheFile, null);
                else
                    File.Move(temporary, AppPaths.ServiceHealthCacheFile);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось сохранить кэш BFE/WinDivert: " + ex.Message);
            }
        }

        /// <summary>Получает состояния без запуска, изменения или удаления служб.</summary>
        public static ServiceHealthSnapshot Capture()
        {
            var snapshot = new ServiceHealthSnapshot
            {
                CheckedAtUtc = DateTime.UtcNow,
                Bfe = SafeQuery("BFE"),
                WinDivert = SafeQuery(WinServices.WinDivertService),
                WinDivert14 = SafeQuery(WinServices.WinDivert14Service)
            };
            Save(snapshot);
            return snapshot;
        }

        private static ServiceState SafeQuery(string name)
        {
            try { return WinServices.Query(name); }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось проверить службу {name}: {ex.Message}");
                return ServiceState.Unknown;
            }
        }
    }
}
