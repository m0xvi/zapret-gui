using System;
namespace ZapretGui.Core
{
    /// <summary>
    /// Нормализованный снимок сетевых признаков для диагностики и будущего подбора стратегии.
    /// Названия endpoint-провайдеров сюда не переносятся.
    /// </summary>
    public sealed partial class NetworkObservationSnapshot
    {
        public DateTime CreatedAt { get; init; }
        public bool HasDnsError { get; init; }
        public bool HasTcpTimeout { get; init; }
        public bool HasTlsError { get; init; }
        public bool HasHttpError { get; init; }
        public bool HasDpiFreeze { get; init; }
        public bool BypassImprovesResult { get; init; }
        public long AverageLatencyMs { get; init; }
        public string Summary { get; init; } = "";
    }
}
