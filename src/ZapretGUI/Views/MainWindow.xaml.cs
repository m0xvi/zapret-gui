using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ZapretGui.Core;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly Dictionary<string, UserControl> _pages = new();
        private TrayIcon? _tray;
        private bool _reallyClosing;

        public MainWindow(MainViewModel viewModel)
        {
            _vm = viewModel;
            InitializeComponent();

            DataContext = _vm;

            _pages["home"] = new HomePage(_vm.Home);
            _pages["strategies"] = new StrategiesPage(_vm.StrategiesPage);
            _pages["updates"] = new UpdatesPage(_vm.Updates);
            _pages["diagnostics"] = new DiagnosticsPage(_vm.Diagnostics);
            _pages["logs"] = new LogsPage(_vm.Logs);
            _pages["settings"] = new SettingsPage(_vm.SettingsPage);
            _pages["about"] = new AboutPage(_vm);

            _vm.NavChanged += ShowPage;
            ShowPage(_vm.SelectedNavKey);

            SetupTray();

            if (_vm.Settings.AutoStartBypass &&
                !string.IsNullOrEmpty(_vm.Settings.SelectedStrategy) &&
                _vm.Strategies.Find(_vm.Settings.SelectedStrategy) != null)
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var strategy = _vm.Strategies.Find(_vm.Settings.SelectedStrategy);
                    if (strategy == null) return;
                    var result = await _vm.Bypass.StartAsync(strategy,
                        EngineService.GetGameFilterMode(_vm.Settings.EnginePath), _vm.Settings.ShowWinwsConsole);
                    _vm.Home.ShowInfo(result.Message);
                    _vm.Home.RefreshStatus();
                    _tray?.ShowBalloon("Zapret GUI", result.Message);
                }));
            }
        }

        private void SetupTray()
        {
            try
            {
                _tray = new TrayIcon();
                _tray.OpenRequested += () => Dispatcher.Invoke(ShowFromTray);
                _tray.ToggleBypassRequested += () => Dispatcher.Invoke(async () =>
                {
                    var status = _vm.Bypass.GetStatus();
                    if (status.IsRunning)
                    {
                        var result = await _vm.Bypass.StopAsync();
                        _tray?.ShowBalloon("Zapret GUI", result.Message);
                    }
                    else
                    {
                        var strategy = _vm.Strategies.Find(_vm.Settings.SelectedStrategy) ?? _vm.Strategies.Recommended;
                        if (strategy == null)
                        {
                            _tray?.ShowBalloon("Zapret GUI", "Стратегия не выбрана");
                            return;
                        }
                        var result = await _vm.Bypass.StartAsync(strategy,
                            EngineService.GetGameFilterMode(_vm.Settings.EnginePath), _vm.Settings.ShowWinwsConsole);
                        _tray?.ShowBalloon("Zapret GUI", result.Message);
                    }
                    _vm.Home.RefreshStatus();
                });
                _tray.ExitRequested += () => Dispatcher.Invoke(() =>
                {
                    _reallyClosing = true;
                    Close();
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось создать иконку в трее: " + ex.Message);
            }
        }

        private void ShowPage(string key)
        {
            if (!_pages.TryGetValue(key, out var page)) return;
            PageHost.Content = page;
            AppLog.Debug("Открыта страница: " + key);

            if (key == "strategies") _vm.StrategiesPage.Refresh();
            if (key == "home") _vm.Home.RefreshStatus();
        }

        private void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (MaximizeButton != null)
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

            // WindowChrome при разворачивании выходит за границы экрана — компенсируем отступом
            RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_reallyClosing && _vm.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                _tray?.ShowBalloon("Zapret GUI свёрнут в трей",
                    "Обход продолжает работать. Двойной клик по иконке вернёт окно.");
                return;
            }

            base.OnClosing(e);

            try
            {
                var shutdown = _vm.ShutdownAsync();
                shutdown.Wait(6000);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка при остановке обхода: " + ex.Message);
            }

            SettingsStore.Save(_vm.Settings);
            _tray?.Dispose();

            if (Application.Current is App app) app.ShutdownApp();
        }
    }
}
