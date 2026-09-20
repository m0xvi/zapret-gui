using System;
using System.Collections.Generic;
using System.IO;

namespace ZapretGui.Core
{
    /// <summary>
    /// Конструктор аргументов winws.exe для ручной и полуавтоматической настройки стратегий.
    /// </summary>
    public static class VisualStrategyBuilder
    {
        public static List<string> BuildArgs(
            string enginePath,
            string desyncMode,
            string splitPos,
            string fakeSni,
            string ttl,
            string fooling,
            bool useMultisplit,
            bool useGameUdp,
            bool useHostlist,
            bool useIpSet)
        {
            var args = new List<string>();
            var listsDir = Path.Combine(enginePath ?? "", "lists");
            var listGeneral = Path.Combine(listsDir, "list-general.txt");
            var ipsetAll = Path.Combine(listsDir, "ipset-all.txt");

            var udpPorts = useGameUdp ? "443,50000-65535" : "443";
            args.Add("--wf-tcp=80,443");
            args.Add($"--wf-udp={udpPorts}");

            // Блок 1: TCP 80,443 (HTTP / HTTPS / Discord Gateway / YouTube Web)
            args.Add("--filter-tcp=80,443");
            if (useHostlist && File.Exists(listGeneral))
            {
                args.Add($"--hostlist=\"{listGeneral}\"");
            }
            if (useIpSet && File.Exists(ipsetAll))
            {
                args.Add($"--ipset=\"{ipsetAll}\"");
            }

            var mode = string.IsNullOrWhiteSpace(desyncMode) ? "split2" : desyncMode.Trim();
            if (mode != "none")
            {
                args.Add($"--dpi-desync={mode}");
            }

            var pos = string.IsNullOrWhiteSpace(splitPos) ? "midsld" : splitPos.Trim();
            if (!string.IsNullOrWhiteSpace(pos) && pos != "none")
            {
                args.Add($"--dpi-desync-split-pos={pos}");
            }

            var fool = string.IsNullOrWhiteSpace(fooling) ? "badsum" : fooling.Trim();
            if (!string.IsNullOrWhiteSpace(fool) && fool != "none")
            {
                args.Add($"--dpi-desync-fooling={fool}");
            }

            if (!string.IsNullOrWhiteSpace(ttl) && ttl != "auto" && int.TryParse(ttl, out var ttlVal))
            {
                args.Add($"--dpi-desync-ttl={ttlVal}");
            }
            else
            {
                args.Add("--dpi-desync-autottl=2");
            }

            if (useMultisplit)
            {
                args.Add("--dpi-desync-split-seqovl=1");
            }

            var sni = string.IsNullOrWhiteSpace(fakeSni) ? "www.google.com" : fakeSni.Trim();
            if (!string.IsNullOrWhiteSpace(sni) && sni != "none")
            {
                args.Add($"--dpi-desync-fake-tls-mod=sni={sni}");
            }

            // Блок 2: UDP 443 (QUIC / YouTube)
            args.Add("--new");
            args.Add("--filter-udp=443");
            if (useHostlist && File.Exists(listGeneral))
            {
                args.Add($"--hostlist=\"{listGeneral}\"");
            }
            args.Add("--dpi-desync=fake");
            args.Add("--dpi-desync-repeats=6");
            if (!string.IsNullOrWhiteSpace(ttl) && ttl != "auto" && int.TryParse(ttl, out var ttlUdp))
            {
                args.Add($"--dpi-desync-ttl={ttlUdp}");
            }
            else
            {
                args.Add("--dpi-desync-autottl=2");
            }

            // Блок 3: Игровой UDP фильтр (голос Discord и игры на UDP 50000-65535)
            if (useGameUdp)
            {
                args.Add("--new");
                args.Add("--filter-udp=50000-65535");
                args.Add("--dpi-desync=fake");
                args.Add("--dpi-desync-any-protocol=1");
                args.Add("--dpi-desync-repeats=3");
            }

            return args;
        }
    }
}
