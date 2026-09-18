using System.Linq;
using System.Windows;
using ZapretGui.Core;

namespace ZapretGui.Views
{
    /// <summary>
    /// Диалог разрешения конфликта со старым запуском запрета.
    /// Только спрашивает пользователя — сами действия выполняет вызывающий код.
    /// </summary>
    public partial class LegacyZapretDialog : Window
    {
        public LegacyChoice Choice { get; private set; } = LegacyChoice.Later;

        public bool DontAskChecked => DontAskCheck.IsChecked == true;

        public LegacyZapretDialog(LegacyInstallInfo info)
        {
            InitializeComponent();

            ServiceInfoText.Text = info.IsForeignService
                ? $"Служба zapret: {info.StateText} (чужая — из другой папки)"
                : info.ServiceExists
                    ? "Служба zapret: наша (из папки движка приложения)"
                    : "Служба zapret не установлена";

            ProcessInfoText.Text = info.ForeignWinws.Count > 0
                ? $"Чужие процессы winws.exe: {info.ForeignWinws.Count} " +
                  $"(PID {string.Join(", ", info.ForeignWinws.Select(p => p.Pid))})"
                : "Чужих процессов winws.exe нет";

            StrategyInfoText.Text = "Стратегия старого запуска: " +
                                    (string.IsNullOrEmpty(info.StrategyName) ? "неизвестно" : info.StrategyName);

            FolderInfoText.Text = "Папка старого движка: " +
                                  (string.IsNullOrEmpty(info.ForeignRoot) ? "—" : info.ForeignRoot);
            FolderInfoText.ToolTip = info.ForeignRoot;
        }

        private void TakeOver_Click(object sender, RoutedEventArgs e)
        {
            Choice = LegacyChoice.TakeOver;
            DialogResult = true;
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            Choice = LegacyChoice.ImportAndTakeOver;
            DialogResult = true;
        }

        private void StopOnly_Click(object sender, RoutedEventArgs e)
        {
            Choice = LegacyChoice.StopOnly;
            DialogResult = true;
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            Choice = LegacyChoice.Later;
            DialogResult = true;
        }
    }
}
