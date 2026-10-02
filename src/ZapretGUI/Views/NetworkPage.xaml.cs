using System.Windows.Controls;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    /// <summary>
    /// «Под мою сеть» (v1.30.0): шесть шагов под провайдера — провайдер и ASN, перехват (ipset),
    /// порты, TCP-таймстемпы, DNS/DoH, IPv4/IPv6. Проверки идут по порядку, правки значений —
    /// на рабочем столе настройщика. Логика — в NetworkProfileViewModel.
    /// </summary>
    public partial class NetworkPage : UserControl
    {
        public NetworkPage(NetworkProfileViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
