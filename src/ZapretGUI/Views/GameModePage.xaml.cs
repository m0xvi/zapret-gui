using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class GameModePage : UserControl
    {
        public GameModePage(GameModeViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        public GameModePage()
        {
            InitializeComponent();
        }
    }
}
