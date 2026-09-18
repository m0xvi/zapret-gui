namespace ZapretGui.Core
{
    /// <summary>Чистое состояние готовности, используемое главным экраном и мастером.</summary>
    public sealed class ReadinessSnapshot
    {
        public string Status { get; init; } = "";
        public string Details { get; init; } = "";
        public string Key { get; init; } = "Warning";
        public bool IsReady => Key == "Success";
    }

    public static class ReadinessEvaluator
    {
        public static bool AutomaticActionsAllowed(AppSettings settings) => !settings.SafeMode;

        public static ReadinessSnapshot Evaluate(AppSettings settings, bool isAdmin,
            bool engineReady, int strategyCount)
        {
            if (!isAdmin)
                return new ReadinessSnapshot
                {
                    Status = "Требуются права администратора",
                    Details = "Чтение настроек и диагностика доступны, но запуск обхода, службы и системные исправления потребуют перезапуска от администратора.",
                    Key = "Danger"
                };
            if (!settings.FirstLaunchWizardCompleted)
                return new ReadinessSnapshot
                {
                    Status = "Мастер первого запуска не завершён",
                    Details = "Откройте «Первый запуск»: мастер проведёт по правам, движку, диагностике, стратегии и ручному режиму.",
                    Key = "Warning"
                };
            if (!engineReady)
                return new ReadinessSnapshot
                {
                    Status = "Движок не установлен",
                    Details = "Скачайте официальный движок на странице «Обновления» или через мастер первого запуска.",
                    Key = "Danger"
                };
            if (strategyCount == 0)
                return new ReadinessSnapshot
                {
                    Status = "Стратегии не найдены",
                    Details = "Проверьте путь к движку и наличие .bat-файлов стратегий.",
                    Key = "Warning"
                };
            if (string.IsNullOrWhiteSpace(settings.SelectedStrategy))
                return new ReadinessSnapshot
                {
                    Status = "Стратегия не выбрана",
                    Details = "Выберите стратегию на странице «Стратегии».",
                    Key = "Warning"
                };

            return new ReadinessSnapshot
            {
                Status = settings.SafeMode ? "Готово · безопасный режим" : "Готово к работе",
                Details = settings.SafeMode
                    ? "Обход не запускается автоматически, служба и сеть меняются только после явного действия пользователя."
                    : "Движок, стратегии и выбранный профиль готовы. Служба не изменяется автоматически.",
                Key = "Success"
            };
        }
    }
}
