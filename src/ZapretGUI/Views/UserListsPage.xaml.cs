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

        /// <summary>«Списки» — часть раздела «Обход» с v1.21.0: отдельного пункта меню больше нет.</summary>
        private void BackToBypass_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var main = (System.Windows.Application.Current.MainWindow as MainWindow)?.DataContext as MainViewModel;
            main?.Navigate("bypass-center");
        }

        private void EntriesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DataContext is not UserListsViewModel vm) return;
            var src = e.OriginalSource as System.Windows.DependencyObject;
            var item = src != null ? FindItem(src) : null;
            var entry = item?.DataContext as string ?? vm.SelectedEntry;
            if (string.IsNullOrWhiteSpace(entry)) return;
            if (vm.EditEntryCommand.CanExecute(entry))
                vm.EditEntryCommand.Execute(entry);
        }

        private static System.Windows.Controls.ListBoxItem? FindItem(System.Windows.DependencyObject src)
        {
            var cur = src;
            while (cur != null)
            {
                if (cur is System.Windows.Controls.ListBoxItem it) return it;
                cur = cur is System.Windows.Media.Visual || cur is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(cur)
                    : (cur as System.Windows.FrameworkContentElement)?.Parent;
            }
            return null;
        }
    }
}
