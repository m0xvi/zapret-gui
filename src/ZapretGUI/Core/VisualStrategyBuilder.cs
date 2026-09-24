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
            bool useIpSet,
            int repeats = 0,
            string? youtubeSni = null,
            bool disableQuicFake = false)
        {
            var args = new List<string>();
            var listsDir = Path.Combine(enginePath ?? "", "lists");
            var binDir = Path.Combine(enginePath ?? "", "bin");
            var ipsetAll = Path.Combine(listsDir, "ipset-all.txt");
            var ipsetDiscord = Path.Combine(listsDir, "ipset-discord.txt");
            var tlsFakeGoogle = Path.Combine(binDir, "tls_clienthello_www_google_com.bin");
            var quicFakeGoogle = Path.Combine(binDir, "quic_initial_www_google_com.bin");

            var udpPorts = useGameUdp ? "443,50000-65535" : "443";
            args.Add("--wf-tcp=80,443");
            args.Add($"--wf-udp={udpPorts}");

            // Блок 1: TCP 80,443 (HTTP / HTTPS / Discord Gateway / YouTube Web)
            args.Add("--filter-tcp=80,443");
            if (useHostlist)
            {
                var builtInLists = new[] { "list-general.txt", "list-youtube.txt", "list-discord.txt" };
                foreach (var listName in builtInLists)
                {
                    var fullPath = Path.Combine(listsDir, listName);
                    if (File.Exists(fullPath))
                    {
                        args.Add($"--hostlist={fullPath}");
                    }
                }

                var userLists = new[] { "list-general-user.txt", "list-youtube-user.txt", "list-discord-user.txt" };
                foreach (var listName in userLists)
                {
                    var fullPath = Path.Combine(listsDir, listName);
                    if (File.Exists(fullPath))
                    {
                        args.Add($"--hostlist-domains={fullPath}");
                    }
                }
            }

            var mode = string.IsNullOrWhiteSpace(desyncMode) ? "fake,split2" : desyncMode.Trim();
            if (mode != "none")
            {
                args.Add($"--dpi-desync={mode}");
            }

            var pos = string.IsNullOrWhiteSpace(splitPos) ? "none" : splitPos.Trim();
            if (pos != "none" && (mode.Contains("split") || mode.Contains("disorder") || mode.Contains("fake")))
            {
                args.Add($"--dpi-desync-split-pos={pos}");
            }

            var fool = string.IsNullOrWhiteSpace(fooling) ? "none" : fooling.Trim();
            if (fool != "none")
            {
                args.Add($"--dpi-desync-fooling={fool}");
            }

            if (repeats > 0)
            {
                args.Add($"--dpi-desync-repeats={repeats}");
            }

            if (!string.IsNullOrWhiteSpace(ttl) && ttl != "auto" && int.TryParse(ttl, out var ttlVal))
            {
                args.Add($"--dpi-desync-ttl={ttlVal}");
            }
            else
            {
                args.Add("--dpi-desync-autottl=2");
            }

            if (useMultisplit && (mode.Contains("split") || mode.Contains("disorder")))
            {
                args.Add("--dpi-desync-split-seqovl=1");
            }

            if (mode.Contains("fake"))
            {
                if (File.Exists(tlsFakeGoogle))
                {
                    args.Add($"--dpi-desync-fake-tls={tlsFakeGoogle}");
                }

                var sni = string.IsNullOrWhiteSpace(fakeSni) ? "none" : fakeSni.Trim();
                if (sni != "none")
                {
                    args.Add($"--dpi-desync-fake-tls-mod=sni={sni}");
                }

                args.Add("--ip-id=zero");
            }

            // Блок 2: UDP 443 (QUIC / YouTube) — улучшено для YouTube-регионов: отдельный SNI и возможность отключить QUIC fake
            args.Add("--new");
            args.Add("--filter-udp=443");
            if (useHostlist)
            {
                // Для YouTube-трафика приоритет youtube-спискам
                var youtubePriority = new[] { "list-youtube.txt", "list-youtube-user.txt", "list-general.txt", "list-general-user.txt" };
                foreach (var listName in youtubePriority)
                {
                    var fullPath = Path.Combine(listsDir, listName);
                    if (File.Exists(fullPath))
                    {
                        args.Add(listName.Contains("youtube") ? $"--hostlist={fullPath}" : $"--hostlist-domains={fullPath}");
                    }
                }
                // Discord отдельно не нужен в QUIC блоке
            }
            args.Add("--dpi-desync=fake");
            args.Add("--dpi-desync-repeats=6");
            var effectiveYoutubeSni = string.IsNullOrWhiteSpace(youtubeSni) ? fakeSni : youtubeSni;
            if (!disableQuicFake && File.Exists(quicFakeGoogle))
            {
                args.Add($"--dpi-desync-fake-quic={quicFakeGoogle}");
                if (!string.IsNullOrWhiteSpace(effectiveYoutubeSni) && effectiveYoutubeSni != "none")
                    args.Add($"--dpi-desync-fake-quic-mod=sni={effectiveYoutubeSni}");
            }
            else if (disableQuicFake)
            {
                // QUIC отключён для теста в строгих регионах
                args.Add("--dpi-desync-fooling=badseq");
            }
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
                if (useIpSet)
                {
                    if (File.Exists(ipsetDiscord))
                    {
                        args.Add($"--ipset={ipsetDiscord}");
                    }
                    else if (File.Exists(ipsetAll))
                    {
                        args.Add($"--ipset={ipsetAll}");
                    }
                }
                args.Add("--dpi-desync=fake");
                args.Add("--dpi-desync-any-protocol=1");
                args.Add("--dpi-desync-repeats=3");
            }

            return args;
        }
    }
}
