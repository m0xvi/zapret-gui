using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class DeepCheckPage : UserControl
    {
        public DeepCheckPage(DeepCheckViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
