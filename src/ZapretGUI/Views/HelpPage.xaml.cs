using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    /// <summary>
    /// «Помощь» (этап 6, docs/IA_REDESIGN.md §3.6) — утилита в подвале меню.
    /// Страница ничего не хранит: все кнопки — переходы и уже существующие команды.
    /// </summary>
    public partial class HelpPage : UserControl
    {
        public HelpPage(HelpViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
