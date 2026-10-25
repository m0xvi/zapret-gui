using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ZapretGui.Views
{
    public partial class TaskbarMetricsWindow : Window
    {
        private bool _isResizing;
        private Point _resizeStartPoint;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private bool _positionLoaded;

        public TaskbarMetricsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            LocationChanged += OnLocationChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Восстанавливаем позицию/размер из настроек
            try
            {
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    var s = vm.Settings;
                    if (s.TaskbarMetricsWidth > 80) Width = s.TaskbarMetricsWidth;
                    if (s.TaskbarMetricsHeight > 40) { Height = s.TaskbarMetricsHeight; SizeToContent = SizeToContent.Manual; }
                    if (s.TaskbarMetricsLeft >= 0 && s.TaskbarMetricsTop >= 0)
                    {
                        Left = s.TaskbarMetricsLeft;
                        Top = s.TaskbarMetricsTop;
                        _positionLoaded = true;
                    }
                    else
                    {
                        UpdatePosition();
                        _positionLoaded = true;
                    }
                }
                else
                {
                    UpdatePosition();
                }
            }
            catch { UpdatePosition(); }

            SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(() =>
            {
                if (!_positionLoaded) UpdatePosition();
            });
        }

        public void UpdatePosition()
        {
            try
            {
                // Только если не задана ручная позиция
                if (DataContext is ViewModels.MainViewModel vm && vm.Settings.TaskbarMetricsLeft >= 0) return;
                var workArea = SystemParameters.WorkArea;
                var w = ActualWidth > 0 ? ActualWidth : Width;
                if (double.IsNaN(w) || w <= 0) w = 170;
                var h = ActualHeight > 0 ? ActualHeight : Height;
                if (double.IsNaN(h) || h <= 0) h = 80;
                if (SizeToContent == SizeToContent.Height) h = ActualHeight > 0 ? ActualHeight : 80;
                Left = workArea.Right - w - 8;
                Top = workArea.Bottom - h - 4;
                if (Top < workArea.Top) Top = workArea.Top + 4;
                if (Left < workArea.Left) Left = workArea.Left + 4;
            }
            catch { }
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                // Не перетаскиваем если клик по кнопке закрытия или grip
                var src = e.OriginalSource as DependencyObject;
                // Если кликнули по кнопке — не драгаем
                if (src != null)
                {
                    var parent = src;
                    while (parent != null)
                    {
                        if (parent is System.Windows.Controls.Button) return;
                        parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
                    }
                }
                try { DragMove(); } catch { }
                SavePosition();
            }
            else if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
            {
                // Двойной клик — сброс к авто-позиции над треем
                ResetPosition();
            }
        }

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _isResizing = true;
            _resizeStartPoint = PointToScreen(e.GetPosition(this));
            _resizeStartWidth = ActualWidth;
            _resizeStartHeight = ActualHeight;
            if (SizeToContent == SizeToContent.Height) SizeToContent = SizeToContent.Manual;
            ResizeGrip.CaptureMouse();
            e.Handled = true;
            MouseMove += OnResizingMouseMove;
            MouseLeftButtonUp += OnResizingMouseUp;
        }

        private void OnResizingMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;
            var cur = PointToScreen(e.GetPosition(this));
            var dx = cur.X - _resizeStartPoint.X;
            var dy = cur.Y - _resizeStartPoint.Y;
            var newW = Math.Clamp(_resizeStartWidth + dx, MinWidth, MaxWidth);
            var newH = Math.Clamp(_resizeStartHeight + dy, MinHeight, MaxHeight);
            Width = newW;
            Height = newH;
        }

        private void OnResizingMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isResizing) return;
            _isResizing = false;
            ResizeGrip.ReleaseMouseCapture();
            MouseMove -= OnResizingMouseMove;
            MouseLeftButtonUp -= OnResizingMouseUp;
            SavePosition();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_positionLoaded) return;
            // Авто-позиция не обновляем при ручной установке
            SavePosition();
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            if (!_positionLoaded) return;
            SavePosition();
        }

        private void SavePosition()
        {
            try
            {
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    vm.Settings.TaskbarMetricsLeft = Left;
                    vm.Settings.TaskbarMetricsTop = Top;
                    vm.Settings.TaskbarMetricsWidth = ActualWidth > 0 ? ActualWidth : Width;
                    if (SizeToContent != SizeToContent.Height) vm.Settings.TaskbarMetricsHeight = ActualHeight > 0 ? ActualHeight : Height;
                    else vm.Settings.TaskbarMetricsHeight = -1;
                    Core.SettingsStore.Save(vm.Settings);
                }
            }
            catch { }
        }

        private void ResetPosition()
        {
            try
            {
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    vm.Settings.TaskbarMetricsLeft = -1;
                    vm.Settings.TaskbarMetricsTop = -1;
                    vm.Settings.TaskbarMetricsHeight = -1;
                    SizeToContent = SizeToContent.Height;
                    Core.SettingsStore.Save(vm.Settings);
                    UpdatePosition();
                }
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
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
