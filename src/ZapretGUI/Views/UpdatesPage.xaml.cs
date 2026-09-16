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
    }
}
