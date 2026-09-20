using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ZapretGui.Core;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class MiniOverlayWindow : Window
    {
        private readonly MiniOverlayViewModel _vm;
        private bool _isExplicitClose;

        public MiniOverlayWindow(MiniOverlayViewModel vm)
        {
            _vm = vm;
            InitializeComponent();
            DataContext = _vm;

            Loaded += OnLoaded;
            LocationChanged += OnLocationChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_vm.Settings.MiniOverlayLeft >= 0 && _vm.Settings.MiniOverlayTop >= 0)
            {
                var screenWidth = SystemParameters.VirtualScreenWidth;
                var screenHeight = SystemParameters.VirtualScreenHeight;

                if (_vm.Settings.MiniOverlayLeft < screenWidth - 50 && _vm.Settings.MiniOverlayTop < screenHeight - 50)
                {
                    Left = _vm.Settings.MiniOverlayLeft;
                    Top = _vm.Settings.MiniOverlayTop;
                    return;
                }
            }

            // Позиция по умолчанию: правый нижний угол экрана
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Bottom - Height - 24;
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            if (IsLoaded && WindowState == WindowState.Normal)
            {
                _vm.Settings.MiniOverlayLeft = Left;
                _vm.Settings.MiniOverlayTop = Top;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        public void CloseDirectly()
        {
            _isExplicitClose = true;
            Close();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!_isExplicitClose)
            {
                e.Cancel = true;
                Hide();
                _vm.Settings.MiniOverlayEnabled = false;
                SettingsStore.Save(_vm.Settings);
            }
        }
    }
}
