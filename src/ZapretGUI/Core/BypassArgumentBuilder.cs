using System.Collections.Generic;
using System.Linq;

namespace ZapretGui.Core
{
    /// <summary>Чистая подстановка game filter перед запуском winws.exe.</summary>
    public static class BypassArgumentBuilder
    {
        public static List<string> Build(StrategyInfo strategy, GameFilterMode gameFilter)
        {
            var tcp = gameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.TcpOnly
                ? "1024-65535" : "12";
            var udp = gameFilter is GameFilterMode.TcpAndUdp or GameFilterMode.UdpOnly
                ? "1024-65535" : "12";

            return strategy.Args
                .Select(argument => argument
                    .Replace(StrategyParser.GameFilterTcpToken, tcp)
                    .Replace(StrategyParser.GameFilterUdpToken, udp))
                .ToList();
        }
    }
}
