using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class FirstLaunchPage : UserControl
    {
        public FirstLaunchPage(FirstLaunchViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
