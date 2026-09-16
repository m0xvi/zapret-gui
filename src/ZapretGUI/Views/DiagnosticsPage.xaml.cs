using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class DiagnosticsPage : UserControl
    {
        public DiagnosticsPage(DiagnosticsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
