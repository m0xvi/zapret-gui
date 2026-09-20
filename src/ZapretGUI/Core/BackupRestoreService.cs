using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ZapretGui.Core
{
    public sealed class BackupArchiveInfo
    {
        public string FilePath { get; init; } = "";
        public string FileName => Path.GetFileName(FilePath);
        public DateTime Created { get; init; }
        public long SizeBytes { get; init; }
        public string SizeDisplay => SizeBytes < 1024 ? $"{SizeBytes} B" : $"{SizeBytes / 1024.0:F1} KB";
        public string Note { get; init; } = "";
    }

    /// <summary>
    /// Сервис полного резервного копирования, восстановления и очистки системы при переносе.
    /// </summary>
    public static class BackupRestoreService
    {
        public static async Task<(bool Ok, string Message, string FilePath)> CreateFullBackupArchiveAsync(
            string enginePath, string? destinationPath = null, string? note = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    var targetFile = destinationPath ??
                        Path.Combine(AppPaths.BackupDir, $"ZapretGUI_FullBackup_{timestamp}.zip");

                    var tempDir = Path.Combine(AppPaths.TempDir, "backup_" + Guid.NewGuid().ToString("N"));
                    AppPaths.EnsureDir(tempDir);

                    try
                    {
                        // 1. Копируем настройки и профили
                        if (File.Exists(AppPaths.SettingsFile))
                            File.Copy(AppPaths.SettingsFile, Path.Combine(tempDir, "settings.json"), true);

                        if (File.Exists(AppPaths.ProfilesFile))
                            File.Copy(AppPaths.ProfilesFile, Path.Combine(tempDir, "profiles.json"), true);

                        // 2. Копируем сохранённых кандидатов стратегий
                        if (Directory.Exists(AppPaths.CandidatesDir))
                        {
                            var candDest = Path.Combine(tempDir, "candidates");
                            AppPaths.EnsureDir(candDest);
                            foreach (var f in Directory.GetFiles(AppPaths.CandidatesDir, "*.json"))
                                File.Copy(f, Path.Combine(candDest, Path.GetFileName(f)), true);
                        }

                        // 3. Копируем пользовательские списки из движка
                        var listsDir = Path.Combine(enginePath, "lists");
                        if (Directory.Exists(listsDir))
                        {
                            var listsDest = Path.Combine(tempDir, "lists");
                            AppPaths.EnsureDir(listsDest);
                            foreach (var f in Directory.GetFiles(listsDir, "*.*"))
                            {
                                var name = Path.GetFileName(f);
                                if (name.Contains("-user", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("list-exclude.txt", StringComparison.OrdinalIgnoreCase) ||
                                    name.StartsWith("custom", StringComparison.OrdinalIgnoreCase))
                                {
                                    File.Copy(f, Path.Combine(listsDest, name), true);
                                }
                            }
                        }

                        // 4. Метаданные архива
                        var manifest = new
                        {
                            CreatedAt = DateTime.UtcNow,
                            AppVersion = "1.2.9",
                            Note = note ?? "Полный бэкап настроек и списков Zapret GUI"
                        };
                        File.WriteAllText(Path.Combine(tempDir, "manifest.json"),
                            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                        if (File.Exists(targetFile)) File.Delete(targetFile);
                        ZipFile.CreateFromDirectory(tempDir, targetFile, CompressionLevel.Optimal, false);

                        AppLog.Info($"[Backup] Создан полный архив конфигурации: {targetFile}");
                        return (true, $"Резервная копия успешно создана: {Path.GetFileName(targetFile)}", targetFile);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("[Backup] Ошибка создания бэкапа: " + ex.Message);
                    return (false, "Ошибка создания архива: " + ex.Message, "");
                }
            }).ConfigureAwait(false);
        }

        public static async Task<(bool Ok, string Message)> RestoreFullBackupArchiveAsync(string zipPath, string enginePath)
        {
            if (!File.Exists(zipPath))
                return (false, "Файл резервной копии не найден.");

            return await Task.Run(async () =>
            {
                try
                {
                    // 1. Создаем предварительную точку отката
                    await CreateFullBackupArchiveAsync(enginePath, null, "Точка отката перед восстановлением").ConfigureAwait(false);

                    var tempExtract = Path.Combine(AppPaths.TempDir, "restore_" + Guid.NewGuid().ToString("N"));
                    AppPaths.EnsureDir(tempExtract);

                    try
                    {
                        ZipFile.ExtractToDirectory(zipPath, tempExtract, true);

                        // Восстанавливаем settings.json
                        var extractedSettings = Path.Combine(tempExtract, "settings.json");
                        if (File.Exists(extractedSettings))
                        {
                            File.Copy(extractedSettings, AppPaths.SettingsFile, true);
                        }

                        // Восстанавливаем profiles.json
                        var extractedProfiles = Path.Combine(tempExtract, "profiles.json");
                        if (File.Exists(extractedProfiles))
                        {
                            File.Copy(extractedProfiles, AppPaths.ProfilesFile, true);
                        }

                        // Восстанавливаем кандидатов
                        var extractedCandidates = Path.Combine(tempExtract, "candidates");
                        if (Directory.Exists(extractedCandidates))
                        {
                            AppPaths.EnsureDir(AppPaths.CandidatesDir);
                            foreach (var f in Directory.GetFiles(extractedCandidates, "*.json"))
                                File.Copy(f, Path.Combine(AppPaths.CandidatesDir, Path.GetFileName(f)), true);
                        }

                        // Восстанавливаем пользовательские списки
                        var extractedLists = Path.Combine(tempExtract, "lists");
                        var targetLists = Path.Combine(enginePath, "lists");
                        if (Directory.Exists(extractedLists) && Directory.Exists(targetLists))
                        {
                            foreach (var f in Directory.GetFiles(extractedLists, "*.*"))
                                File.Copy(f, Path.Combine(targetLists, Path.GetFileName(f)), true);
                        }

                        AppLog.Info($"[Backup] Конфигурация успешно восстановлена из {Path.GetFileName(zipPath)}");
                        return (true, $"Конфигурация успешно восстановлена из архива «{Path.GetFileName(zipPath)}». Рекомендуется перезапустить приложение.");
                    }
                    finally
                    {
                        try { Directory.Delete(tempExtract, true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("[Backup] Ошибка восстановления: " + ex.Message);
                    return (false, "Ошибка восстановления конфигурации: " + ex.Message);
                }
            }).ConfigureAwait(false);
        }

        public static List<BackupArchiveInfo> GetBackupHistory()
        {
            var list = new List<BackupArchiveInfo>();
            try
            {
                if (Directory.Exists(AppPaths.BackupDir))
                {
                    foreach (var file in Directory.GetFiles(AppPaths.BackupDir, "*.zip"))
                    {
                        var fi = new FileInfo(file);
                        list.Add(new BackupArchiveInfo
                        {
                            FilePath = file,
                            Created = fi.LastWriteTime,
                            SizeBytes = fi.Length,
                            Note = file.Contains("FullBackup") ? "Полная копия (.zip)" : "Резервная копия (.zip)"
                        });
                    }
                }
            }
            catch { }

            return list.OrderByDescending(b => b.Created).ToList();
        }

        public static bool DeleteBackup(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Очистка всех системных следов перед переносом приложения на другое устройство или флешку.
        /// </summary>
        public static async Task<(bool Ok, string Message)> PerformFullSystemCleanupAsync(BypassController bypass)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // 1. Остановка обхода
                    if (bypass.GetStatus().IsRunning)
                    {
                        await bypass.StopAsync().ConfigureAwait(false);
                    }

                    // 2. Удаление службы Windows и драйверов WinDivert
                    await bypass.RemoveServiceAsync().ConfigureAwait(false);
                    Shell.Run("sc.exe", new[] { "stop", "WinDivert" });
                    Shell.Run("sc.exe", new[] { "delete", "WinDivert" });
                    Shell.Run("sc.exe", new[] { "stop", "zapret" });
                    Shell.Run("sc.exe", new[] { "delete", "zapret" });

                    // 3. Удаление автозапуска из планировщика задач
                    Shell.Run("schtasks.exe", new[] { "/delete", "/tn", "ZapretGUI", "/f" });

                    // 4. Сброс DNS к DHCP
                    var dhcp = new DnsProfile { Id = "dhcp", Name = "Автоматический DNS (DHCP)" };
                    await DnsManagementService.ApplyDnsProfileAsync(dhcp).ConfigureAwait(false);

                    // 5. Очистка временных файлов
                    try
                    {
                        if (Directory.Exists(AppPaths.TempDir))
                            Directory.Delete(AppPaths.TempDir, true);
                    }
                    catch { }

                    AppLog.Info("[Cleanup] Полная очистка системных следов Zapret и WinDivert выполнена.");
                    return (true, "Системные службы zapret и WinDivert удалены, DNS сброшен в DHCP, автозапуск отключён. Приложение готово к переносу.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("[Cleanup] Ошибка при очистке системы: " + ex.Message);
                    return (false, "Ошибка очистки: " + ex.Message);
                }
            }).ConfigureAwait(false);
        }
    }
}
