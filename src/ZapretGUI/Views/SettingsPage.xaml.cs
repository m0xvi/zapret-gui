using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
