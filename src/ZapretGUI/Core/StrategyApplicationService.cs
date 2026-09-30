using System;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretGui.Core
{
    /// <summary>
    /// Централизованная логика применения стратегий (P2 1.6.10).
    /// Объединяет проверки администратора, диалога legacy, подтверждения
    /// и бесшовного переключения, чтобы не дублировать их в каждом ViewModel.
    /// </summary>
    public static class StrategyApplicationService
    {
        public static bool EnsureAdmin(string action, Action<string, string> showError)
        {
            if (Shell.IsAdmin()) return true;
            showError("Требуются права администратора",
                $"Для {action} нужны права администратора. Нажмите «Перезапустить от админа» на странице Обзор.");
            return false;
        }

        public static bool Confirm(string title, string text)
        {
            return MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        /// <summary>
        /// Бесшовно применяет стратегию с учётом текущего режима (служба/процесс/выключен).
        /// Возвращает OperationResult запуска/переключения.
        /// </summary>
        public static async Task<OperationResult> ApplyAsync(
            BypassController bypass,
            StrategyInfo strategy,
            GameFilterMode mode,
            bool showConsole)
        {
            var status = bypass.GetStatus();
            if (status.ServiceState == ServiceState.Running
                || status.ServiceState == ServiceState.StartPending
                || status.ServiceState == ServiceState.StopPending)
            {
                AppLog.SvcInfo($"[StrategyApply] Служба {status.ServiceStrategy} → {strategy.Name} (InstallService)");
                return await bypass.InstallServiceAsync(strategy, mode).ConfigureAwait(false);
            }
            if (status.IsRunning)
            {
                AppLog.SvcInfo($"[StrategyApply] Процесс {status.StrategyName} → {strategy.Name} (Switch)");
                return await bypass.SwitchToStrategyAsync(strategy, mode, showConsole).ConfigureAwait(false);
            }
            return await bypass.StartAsync(strategy, mode, showConsole).ConfigureAwait(false);
        }

        /// <summary>
        /// Проверяет состояние службы и возвращает pending-флаг для UI.
        /// </summary>
        public static bool IsServicePending(ServiceState state)
        {
            return state == ServiceState.StartPending || state == ServiceState.StopPending;
        }

        public static string ServicePendingText(ServiceState state)
        {
            return state switch
            {
                ServiceState.StartPending => "Служба запускается…",
                ServiceState.StopPending => "Служба останавливается…",
                _ => ""
            };
        }
    }
}
