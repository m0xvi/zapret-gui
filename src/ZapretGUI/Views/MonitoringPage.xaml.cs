using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class MonitoringPage : UserControl
    {
        public MonitoringPage(MonitoringViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
