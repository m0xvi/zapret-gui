using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class HomePage : UserControl
    {
        public HomePage(HomeViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
