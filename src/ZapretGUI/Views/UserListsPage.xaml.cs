using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class UserListsPage : UserControl
    {
        public UserListsPage(UserListsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
