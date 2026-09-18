using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZapretGui.Core
{
    public sealed class EngineFileCopyResult
    {
        public int UpdatedFiles { get; init; }
        public int SkippedFiles { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Копирует файлы движка с безопасной политикой для пользовательских списков
    /// и загруженного WinDivert. Проверки занятости передаются снаружи, поэтому
    /// поведение заблокированного драйвера можно проверять без реального драйвера Windows.
    /// </summary>
    public static class EngineFileUpdater
    {
        public static EngineFileCopyResult Copy(
            string source,
            string engineRoot,
            bool preserveUserData,
            Func<string, bool> isUserFile,
            Func<string, bool> isDriverFile,
            Func<IpsetMode> getIpsetMode,
            Func<string, bool> isFileLocked)
        {
            var updated = 0;
            var skipped = 0;
            var warnings = new List<string>();
            Directory.CreateDirectory(engineRoot);

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(engineRoot, relative);

                if (preserveUserData && isUserFile(relative) && File.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                var normalized = relative.Replace('/', '\\');
                if (normalized.Equals("lists\\ipset-all.txt", StringComparison.OrdinalIgnoreCase) &&
                    getIpsetMode() != IpsetMode.Loaded)
                {
                    TryCopy(file, Path.Combine(engineRoot, "lists", "ipset-all.txt.backup"));
                    skipped++;
                    continue;
                }

                AppPaths.EnsureDir(Path.GetDirectoryName(destination)!);

                if (isDriverFile(relative) && File.Exists(destination) && isFileLocked(destination))
                {
                    var warning = $"Файл {relative} заблокирован ядром Windows (драйвер WinDivert не выгрузился) — пропущен. " +
                                  "Перезагрузите Windows и повторите обновление для замены драйвера.";
                    AppLog.Warn(warning);
                    if (!warnings.Contains(warning, StringComparer.OrdinalIgnoreCase)) warnings.Add(warning);
                    skipped++;
                    continue;
                }

                try
                {
                    File.Copy(file, destination, true);
                    updated++;
                }
                catch (IOException ex) when (isDriverFile(relative))
                {
                    var warning = $"Не удалось заменить {relative} (файл занят): {ex.Message}. " +
                                  "Перезагрузите Windows и повторите обновление.";
                    AppLog.Warn(warning);
                    if (!warnings.Contains(warning, StringComparer.OrdinalIgnoreCase)) warnings.Add(warning);
                    skipped++;
                }
                catch (IOException)
                {
                    try
                    {
                        System.Threading.Thread.Sleep(1000);
                        File.Copy(file, destination, true);
                        updated++;
                    }
                    catch (Exception retryEx)
                    {
                        AppLog.Warn($"Пропущен занятый файл {relative}: {retryEx.Message}");
                        skipped++;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    AppLog.Warn($"Нет доступа для записи {relative}: {ex.Message}");
                    skipped++;
                }
            }

            return new EngineFileCopyResult
            {
                UpdatedFiles = updated,
                SkippedFiles = skipped,
                Warnings = warnings
            };
        }

        private static void TryCopy(string source, string destination)
        {
            try
            {
                AppPaths.EnsureDir(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
            catch { }
        }
    }
}
