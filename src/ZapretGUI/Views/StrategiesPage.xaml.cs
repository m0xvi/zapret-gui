using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZapretGui.Core;
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

        private void StrategyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not StrategiesViewModel viewModel) return;
            var item = e.OriginalSource is DependencyObject source ? FindListBoxItem(source) : null;
            var strategy = item?.DataContext as StrategyInfo ?? viewModel.Selected;
            if (strategy == null) return;
            var res = MessageBox.Show(
                $"Применить стратегию «{strategy.Name}»?\n\nПараметры: {strategy.ArgsPreview}\n\n«Да» — применить и запустить (процесс), «Нет» — только сделать основной, «Отмена» — не применять.",
                "Применить стратегию", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel) return;
            if (res == MessageBoxResult.No)
            {
                if (viewModel.SetDefaultCommand.CanExecute(strategy))
                    viewModel.SetDefaultCommand.Execute(strategy);
                return;
            }
            // Да — спросить про службу
            var svc = MessageBox.Show(
                $"Стратегия «{strategy.Name}» будет применена.\n\nУстановить её как службу Windows (автозапуск) или запустить как обычный процесс?\n\n«Да» — как службу, «Нет» — как процесс.",
                "Служба или процесс", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (svc == MessageBoxResult.Yes)
            {
                if (viewModel.InstallServiceCommand.CanExecute(strategy))
                    viewModel.InstallServiceCommand.Execute(strategy);
                else if (viewModel.RunCommand.CanExecute(strategy))
                    viewModel.RunCommand.Execute(strategy);
            }
            else
            {
                if (viewModel.RunCommand.CanExecute(strategy))
                    viewModel.RunCommand.Execute(strategy);
            }
        }

        private void StrategyList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var item = FindListBoxItem(source);
            if (item != null) item.IsSelected = true;
        }

        private void TestStrategyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not StrategiesViewModel viewModel || viewModel.Selected == null) return;
            if (viewModel.TestStrategyCommand.CanExecute(viewModel.Selected))
                viewModel.TestStrategyCommand.Execute(viewModel.Selected);
        }

        private void SetDefaultMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not StrategiesViewModel viewModel) return;
            if (viewModel.SetDefaultCommand.CanExecute(null))
                viewModel.SetDefaultCommand.Execute(null);
        }

        private void CopyStrategyArgsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not StrategiesViewModel viewModel) return;
            if (viewModel.CopyArgsCommand.CanExecute(null))
                viewModel.CopyArgsCommand.Execute(null);
        }

        private static ListBoxItem? FindListBoxItem(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is ListBoxItem item) return item;
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : (current as FrameworkContentElement)?.Parent;
            }
            return null;
        }
    }
}
