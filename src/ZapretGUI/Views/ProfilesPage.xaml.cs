using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage(ProfilesViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
