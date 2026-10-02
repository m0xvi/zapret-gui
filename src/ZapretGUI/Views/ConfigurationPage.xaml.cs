using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    /// <summary>
    /// Рабочий стол настройщика (v1.29.3): сводка «что применено сейчас» в порядке настройки,
    /// правка параметров обхода, история снимков и откат. Логика — в ConfigurationViewModel.
    /// </summary>
    public partial class ConfigurationPage : UserControl
    {
        public ConfigurationPage(ConfigurationViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
