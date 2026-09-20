using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZapretGui.Core
{
    public sealed class FakePayloadInfo
    {
        public string FileName { get; init; } = "";
        public string FullPath { get; init; } = "";
        public long FileSizeBytes { get; init; }
        public string PayloadType { get; init; } = "UDP/QUIC";
        public string Description { get; init; } = "";
        public bool IsActiveForDiscordVoice { get; set; }
        public bool IsActiveForQuic { get; set; }
    }

    /// <summary>
    /// Менеджер бинарных фейковых нагрузок (bin/*.bin) для desync UDP (Discord Voice, GameFilter UDP, QUIC)
    /// и TLS ClientHello.
    /// </summary>
    public static class FakeBinManager
    {
        public static List<FakePayloadInfo> GetAvailablePayloads(string engineRoot)
        {
            var list = new List<FakePayloadInfo>();
            if (string.IsNullOrWhiteSpace(engineRoot)) return list;

            var binDir = Path.Combine(engineRoot, "bin");
            if (!Directory.Exists(binDir)) return list;

            try
            {
                var files = Directory.GetFiles(binDir, "*.bin", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    var fi = new FileInfo(file);
                    var type = DetectPayloadType(name);
                    var desc = DescribePayload(name);

                    list.Add(new FakePayloadInfo
                    {
                        FileName = name,
                        FullPath = file,
                        FileSizeBytes = fi.Length,
                        PayloadType = type,
                        Description = desc
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка сканирования bin/*.bin: " + ex.Message);
            }

            return list.OrderBy(p => p.PayloadType).ThenBy(p => p.FileName).ToList();
        }

        private static string DetectPayloadType(string fileName)
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.Contains("quic")) return "QUIC (UDP 443)";
            if (lower.Contains("tls") || lower.Contains("clienthello")) return "TLS ClientHello (TCP 443)";
            if (lower.Contains("discord") || lower.Contains("voice")) return "Discord Voice (UDP 50000-65535)";
            return "Универсальный UDP/TCP";
        }

        private static string DescribePayload(string fileName)
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.Contains("google")) return "Фейковый пакет Google (www.google.com)";
            if (lower.Contains("sferum")) return "Фейковый пакет Сферум (www.sferum.ru)";
            if (lower.Contains("v3nilla")) return "Оптимизированный фейк для Discord Voice от V3nilla";
            if (lower.Contains("discord")) return "Специальный фейк для голосового трафика Discord";
            return $"Бинарная сигнатура ({fileName})";
        }

        /// <summary>
        /// Гарантирует наличие базовых эталонных фейков в bin/ при повреждении комплекта.
        /// </summary>
        public static void EnsureDefaultPayloads(string engineRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(engineRoot)) return;
                var binDir = Path.Combine(engineRoot, "bin");
                AppPaths.EnsureDir(binDir);

                var defaultQuic = Path.Combine(binDir, "quic_initial_www_google_com.bin");
                if (!File.Exists(defaultQuic) || new FileInfo(defaultQuic).Length == 0)
                {
                    // Минимальный 1200-байтный QUIC Initial кадр
                    var quicData = new byte[1200];
                    quicData[0] = 0xC0; // Initial packet type
                    quicData[1] = 0x00; quicData[2] = 0x00; quicData[3] = 0x01; // Version 1
                    File.WriteAllBytes(defaultQuic, quicData);
                    AppLog.Info("[FakeBinManager] Создан эталонный quic_initial_www_google_com.bin");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("[FakeBinManager] Не удалось создать базовый фейк: " + ex.Message);
            }
        }
    }
}
