using System.Windows.Controls;

namespace ZapretGui.Views
{
    public partial class BypassCenterPage : UserControl
    {
        public BypassCenterPage()
        {
            InitializeComponent();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0) return;
            foreach (var cb in FindVisualChildren<System.Windows.Controls.ComboBox>(this))
                if (cb.IsDropDownOpen) cb.IsDropDownOpen = false;
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject dep) where T : System.Windows.DependencyObject
        {
            if (dep == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(dep); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(dep, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child)) yield return c;
            }
        }


    }
}
