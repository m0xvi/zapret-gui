using System;
using System.Windows;
using System.Windows.Interop;

namespace ZapretGui.Views
{
    public partial class TaskbarMetricsWindow : Window
    {
        public TaskbarMetricsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, _) => UpdatePosition();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdatePosition();
            SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(UpdatePosition);
        }

        public void UpdatePosition()
        {
            try
            {
                var workArea = SystemParameters.WorkArea;
                var w = ActualWidth > 0 ? ActualWidth : Width;
                if (double.IsNaN(w) || w <= 0) w = 170;
                var h = ActualHeight > 0 ? ActualHeight : Height;
                if (double.IsNaN(h) || h <= 0) h = 120;
                // Вертикальная панель над треем справа, как в MSI Afterburner
                Left = workArea.Right - w - 8;
                Top = workArea.Bottom - h - 4;
                if (Top < workArea.Top) Top = workArea.Top + 4;
                if (Left < workArea.Left) Left = workArea.Left + 4;
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.Settings.ToolbarMetricsEnabled = false;
                Core.SettingsStore.Save(vm.Settings);
                vm.RefreshToolbarMetrics();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, extendedStyle | NativeMethods.WS_EX_TOOLWINDOW);
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern int GetWindowLong(IntPtr hwnd, int index);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        }

        private static class SystemEvents
        {
            public static event EventHandler? DisplaySettingsChanged;
            static SystemEvents()
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, e) => DisplaySettingsChanged?.Invoke(null, e);
            }
        }
    }
}
