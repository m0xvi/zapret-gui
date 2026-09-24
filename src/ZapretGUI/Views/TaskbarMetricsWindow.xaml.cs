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
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdatePosition();
            // Обновляем позицию при изменении разрешения
            SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(UpdatePosition);
        }

        public void UpdatePosition()
        {
            try
            {
                var workArea = SystemParameters.WorkArea;
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                // Позиция прямо над таскбаром, справа (как в MSI Afterburner)
                Left = workArea.Right - Width - 8;
                // Если таскбар снизу — WorkArea.Bottom < ScreenHeight, ставим над ним
                // Если таскбар сверху/сбоку — всё равно ставим у нижнего края WorkArea
                Top = workArea.Bottom - Height - 4;
                // На случай если таскбар сверху — корректируем
                if (Top < workArea.Top) Top = workArea.Top + 4;
                if (Left < workArea.Left) Left = workArea.Left + 4;
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // Скрываем, но не закрываем полностью — пользователь может снова включить в настройках
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
            // Делаем окно некликабельным для фокуса, но кликабельным для кнопки закрытия
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
