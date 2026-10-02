using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage(ProfilesViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        /// <summary>«Профили и копии» — часть «Настроек» с v1.21.0: отдельного пункта меню больше нет.</summary>
        private void BackToSettings_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var main = (System.Windows.Application.Current.MainWindow as MainWindow)?.DataContext as MainViewModel;
            main?.Navigate("settings");
        }
    }
}
