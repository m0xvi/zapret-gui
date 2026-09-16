using System.Windows.Controls;
using ZapretGui.Core;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class AboutPage : UserControl
    {
        public AboutPage(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void OpenFlowseal_Click(object sender, System.Windows.RoutedEventArgs e)
            => Shell.OpenUrl(EngineService.RepoUrl);

        private void OpenZapret_Click(object sender, System.Windows.RoutedEventArgs e)
            => Shell.OpenUrl("https://github.com/bol-van/zapret");

        private void OpenLicense_Click(object sender, System.Windows.RoutedEventArgs e)
            => Shell.OpenUrl(EngineService.RepoUrl + "/blob/main/LICENSE.txt");
    }
}
