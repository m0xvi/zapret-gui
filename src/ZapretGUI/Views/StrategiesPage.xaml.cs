using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class StrategiesPage : UserControl
    {
        public StrategiesPage(StrategiesViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
