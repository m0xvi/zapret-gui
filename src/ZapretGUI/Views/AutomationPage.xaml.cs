using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    /// <summary>
    /// Раздел «Автоматизация» (v1.19.0): автозапуск, присмотр за обходом, расписание и сети.
    /// DataContext — тот же SettingsViewModel, что у страницы «Настройки»: настройки переехали
    /// из вкладок по месту, логика сохранения осталась одна.
    /// </summary>
    public partial class AutomationPage : UserControl
    {
        public AutomationPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
