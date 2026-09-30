using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class LogsPage : UserControl
    {
        private LogsViewModel? _viewModel;

        /// <summary>Страница журнала как отдельный экран (пункт меню «Журнал»).</summary>
        public LogsPage(LogsViewModel viewModel) : this()
        {
            Attach(viewModel);
        }

        /// <summary>Журнал внутри вкладки «Проверки → Журнал»: DataContext приходит от родителя.</summary>
        public LogsPage()
        {
            InitializeComponent();
            DataContextChanged += (_, e) => Attach(e.NewValue as LogsViewModel);
        }

        /// <summary>Кнопка «Назад в Настройки» нужна только на отдельной странице журнала.</summary>
        public static readonly DependencyProperty ShowBackButtonProperty = DependencyProperty.Register(
            nameof(ShowBackButton), typeof(bool), typeof(LogsPage), new PropertyMetadata(true));

        public bool ShowBackButton
        {
            get => (bool)GetValue(ShowBackButtonProperty);
            set => SetValue(ShowBackButtonProperty, value);
        }

        private void Attach(LogsViewModel? viewModel)
        {
            if (ReferenceEquals(_viewModel, viewModel)) return;

            if (_viewModel != null) _viewModel.ScrollToEndRequested -= ScrollToEnd;
            _viewModel = viewModel;
            if (_viewModel == null) return;

            DataContext = _viewModel;
            _viewModel.ScrollToEndRequested += ScrollToEnd;
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
