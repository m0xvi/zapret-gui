using System.Collections.Generic;
using System.Linq;

namespace ZapretGui.Core
{
    /// <summary>Чистая подстановка game filter и TLS SNI перед запуском winws.exe.</summary>
    public static class BypassArgumentBuilder
    {
        public static List<string> Build(StrategyInfo strategy, GameFilterMode gameFilter)
        {
            return Build(strategy, gameFilter, null, null, null, null);
        }

        public static List<string> Build(
            StrategyInfo strategy,
            GameFilterMode gameFilter,
            string? gameFilterProfileId = null,
            string? customTcpPorts = null,
            string? customUdpPorts = null,
            string? sniOverride = null)
        {
            var tcp = GameFilterPortConfig.ResolveTcpPortString(gameFilter, gameFilterProfileId, customTcpPorts);
            var udp = GameFilterPortConfig.ResolveUdpPortString(gameFilter, gameFilterProfileId, customUdpPorts);

            var expandedArgs = strategy.Args
                .Select(argument => argument
                    .Replace(StrategyParser.GameFilterTcpToken, tcp)
                    .Replace(StrategyParser.GameFilterUdpToken, udp))
                .ToList();

            if (!string.IsNullOrWhiteSpace(sniOverride))
            {
                expandedArgs = SniFakePoolManager.ApplySniOverride(expandedArgs, sniOverride);
            }

            return expandedArgs;
        }
    }
}
