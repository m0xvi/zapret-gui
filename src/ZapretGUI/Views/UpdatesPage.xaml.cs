using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class UpdatesPage : UserControl
    {
        public UpdatesPage(UpdatesViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void BackToSettings_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is ViewModels.UpdatesViewModel vm)
            {
                var main = (System.Windows.Application.Current.MainWindow as MainWindow)?.DataContext as ViewModels.MainViewModel;
                main?.Navigate("settings");
            }
        }
    }
}
