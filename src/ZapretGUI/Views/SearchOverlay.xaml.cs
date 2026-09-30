using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    /// <summary>
    /// Оверлей поиска `Ctrl+K` (этап 7). Вся логика — в <see cref="SearchViewModel"/>; здесь только
    /// фокус строки ввода и клавиши, которые не относятся к бизнес-логике.
    /// </summary>
    public partial class SearchOverlay : UserControl
    {
        public SearchOverlay()
        {
            InitializeComponent();
        }

        private SearchViewModel? ViewModel => DataContext as SearchViewModel;

        /// <summary>Ставит курсор в строку поиска при открытии оверлея (вызывает MainWindow).</summary>
        public void FocusInput()
        {
            // Фокус ставим после раскладки: элемент только что стал видимым.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                QueryBox.Focus();
                QueryBox.CaretIndex = QueryBox.Text.Length;
            }));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsOpen)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    viewModel.Close();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    viewModel.ExecuteSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Down:
                    viewModel.MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    viewModel.MoveSelection(-1);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Клик по затемнению закрывает поиск.</summary>
        private void Scrim_Click(object sender, MouseButtonEventArgs e) => ViewModel?.Close();

        /// <summary>Клик по строке выдачи открывает место (клавиатура — Enter в MainWindow).</summary>
        private void Result_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: SearchResultItem item })
                ViewModel?.Execute(item);
        }
    }
}
