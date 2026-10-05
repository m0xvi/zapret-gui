using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class LogsPage : UserControl
    {
        public LogsPage(LogsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.ScrollToEndRequested += ScrollToEnd;
        }

        private void ScrollToEnd()
        {
            var scrollViewer = FindScrollViewer(LogList);
            scrollViewer?.ScrollToEnd();
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer) return viewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
