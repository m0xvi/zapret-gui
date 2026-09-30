using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZapretGui.Core;
using ZapretGui.ViewModels;

namespace ZapretGui.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly Dictionary<string, UserControl> _pages = new();
        private TrayIcon? _tray;
        private MiniOverlayWindow? _overlayWindow;
        private bool _reallyClosing;

        public MainWindow(MainViewModel viewModel)
        {
            _vm = viewModel;
            InitializeComponent();
            AddHandler(Mouse.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(MainWindow_PreviewMouseWheel), true);

            DataContext = _vm;

            _pages["home"] = new HomePage(_vm.Home);
            _pages["bypass-center"] = new BypassCenterPage { DataContext = _vm.BypassCenter };
            _pages["first-run"] = new FirstLaunchPage(_vm.FirstLaunch);
            _pages["strategies"] = new StrategiesPage(_vm.StrategiesPage);
            _pages["updates"] = new UpdatesPage(_vm.Updates);
            _pages["diagnostics"] = new DiagnosticsPage(_vm.Diagnostics);
            _pages["deep-check"] = new DeepCheckPage(_vm.DeepCheck);
            _pages["dpi"] = new DpiPage(_vm.Diagnostics);
            _pages["user-lists"] = new UserListsPage(_vm.UserLists);
            _pages["profiles"] = new ProfilesPage(_vm.Profiles);
            _pages["settings"] = new SettingsPage(_vm.SettingsPage);
            _pages["automation"] = new AutomationPage(_vm.SettingsPage);
            _pages["about"] = new AboutPage(_vm);

            _vm.NavChanged += ShowPage;
            ShowPage(_vm.SelectedNavKey);

            Loaded += (_, __) =>
            {
                _vm.Hotkeys.Register(this);
            };

            // Esc закрывает глобальный оверлей (ошибка → Dismiss, иначе отмена/скрытие)
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape && _vm.GlobalOverlay.IsVisible)
                {
                    if (_vm.GlobalOverlay.HasError)
                        _vm.GlobalOverlay.DismissCommand.Execute(null);
                    else if (_vm.GlobalOverlay.CanCancel)
                        _vm.GlobalOverlay.CancelCommand.Execute(null);
                    else
                        _vm.GlobalOverlay.Hide();
                    e.Handled = true;
                }
            };

            _vm.RequestToggleOverlay += () => Dispatcher.Invoke(ToggleMiniOverlayWindow);

            _vm.Home.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(HomeViewModel.StatusText) or nameof(HomeViewModel.IsRunning) or nameof(HomeViewModel.IsGameRunning) or nameof(HomeViewModel.ActiveGameName))
                {
                    UpdateTrayStatus();
                    _vm.MiniOverlay.Refresh();
                }
            };

            SetupTray();
            _vm.Monitoring.NotificationRequested += text => Dispatcher.Invoke(() =>
                _tray?.ShowBalloon("Мониторинг ресурсов", text));
            _vm.WatchdogNotificationRequested += text => Dispatcher.Invoke(() =>
                _tray?.ShowBalloon("Zapret", text));

            // 1.17.24: автозапуск бесшовно без окон, корректно для службы (проблема «установлена, но остановлена»)
            var shouldAutoStartBypass = _vm.Settings.FirstLaunchWizardCompleted &&
                !_vm.Settings.SafeMode &&
                _vm.Settings.AutoStartBypass &&
                !string.IsNullOrEmpty(_vm.Settings.SelectedStrategy) &&
                _vm.Strategies.Find(_vm.Settings.SelectedStrategy) != null;
            // Если служба уже установлена — убеждаемся что она Running даже если AutoStartBypass выключен (Windows должна была запустить, но могла упасть)
            var serviceInstalledAtStartup = _vm.Bypass.IsServiceInstalled();
            if ((shouldAutoStartBypass || serviceInstalledAtStartup) && !_vm.Settings.SafeMode)
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var delay = Math.Clamp(_vm.Settings.StartupDelaySeconds, 0, 60);
                        if (delay > 0)
                        {
                            AppLog.Info($"[Startup] Отложенный запуск: ожидание {delay} сек...");
                            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delay));
                        }

                        var strategy = _vm.Strategies.Find(_vm.Settings.SelectedStrategy) ?? _vm.Strategies.Recommended;
                        if (strategy == null)
                        {
                            AppLog.Warn("[Startup] Стратегия для автозапуска не найдена");
                            return;
                        }

                        var status = _vm.Bypass.GetStatus();
                        // Если служба установлена — стартуем именно службу, а не standalone процесс
                        if (status.ServiceState != ServiceState.NotInstalled)
                        {
                            if (status.ServiceState != ServiceState.Running)
                            {
                                AppLog.Info($"[Startup] Служба в состоянии {status.ServiceState} — запускаю бесшовно");
                                // Пробуем обычный старт, если не поможет — переустановка бесшовно
                                var started = false;
                                try
                                {
                                    var r = WinServices.Start(WinServices.ZapretService);
                                    AppLog.Info($"[Startup] sc start result: ok={r.Ok}");
                                    started = await Shell.WaitForAsync(() => WinServices.Query(WinServices.ZapretService) == ServiceState.Running, 12000);
                                    if (!started)
                                    {
                                        AppLog.Warn("[Startup] sc start не помог — переустанавливаю службу с выбранной стратегией");
                                        var res = await _vm.Bypass.InstallServiceAsync(strategy, EngineService.GetGameFilterMode(_vm.Settings.EnginePath));
                                        AppLog.Info($"[Startup] InstallService: {res.Message}");
                                        _vm.Home.ShowInfo(res.Message);
                                    }
                                    else
                                    {
                                        AppLog.Info("[Startup] Служба успешно запущена");
                                        _vm.Home.ShowInfo("Служба zapret запущена");
                                    }
                                }
                                catch (Exception ex) { AppLog.Warn("[Startup] Ошибка запуска службы: " + ex.Message); }
                            }
                            else
                            {
                                AppLog.Info("[Startup] Служба уже запущена — автозапуск не нужен");
                            }
                        }
                        else if (shouldAutoStartBypass)
                        {
                            // Службы нет, но AutoStart включён — запускаем standalone без вопроса
                            var result = await _vm.Bypass.StartAsync(strategy,
                                EngineService.GetGameFilterMode(_vm.Settings.EnginePath), _vm.Settings.ShowWinwsConsole);
                            AppLog.Info($"[Startup] Standalone старт: {result.Message}");
                            _vm.Home.ShowInfo(result.Message);
                        }

                        _vm.Home.RefreshStatus();
                        UpdateTrayStatus();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("[Startup] Ошибка автозапуска: " + ex.Message);
                    }
                }));
            }

            // Миграция v1.22.0: нестандартные экспертные параметры → один баллун с предложением
            // включить режим «Эксперт» (docs/IA_REDESIGN.md §7, правило 4; без модальных окон)
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(9000);
                    if (_vm.Settings.SafeMode) return;
                    if (!_vm.ShouldSuggestExpertMode()) return;
                    _vm.Settings.ExpertModeHintShown = true;
                    SettingsStore.Save(_vm.Settings);
                    await Dispatcher.InvokeAsync(() => _tray?.ShowBalloon("Режим «Эксперт»",
                        "У вас настроены продвинутые параметры (SNI, стратегии для отдельных сайтов). " +
                        "Включить режим «Эксперт», чтобы они были видны? Переключатель — в шапке окна или Ctrl+Shift+E."));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Ошибка подсказки режима «Эксперт»: " + ex.Message);
                }
            });

            // Проверка конфликта со старым запретом — с задержкой, чтобы сначала
            // отработали автоустановка движка и автозапуск обхода
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(6000);
                    if (!_vm.Settings.FirstLaunchWizardCompleted || _vm.Settings.SafeMode) return;
                    if (_vm.Settings.LegacyZapretDismissed) return;
                    var info = LegacyZapret.Detect(_vm.Settings);
                    if (!info.HasConflict) return;
                    await Dispatcher.InvokeAsync(() => ShowLegacyDialog(info));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Ошибка проверки старого запрета: " + ex.Message);
                }
            });
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
                        // Запуск из трея без вопроса — бесшовно (1.17.24)
                        var result = await _vm.Bypass.StartAsync(strategy,
                            EngineService.GetGameFilterMode(_vm.Settings.EnginePath), _vm.Settings.ShowWinwsConsole);
                        _tray?.ShowBalloon("Zapret GUI", result.Message);
                    }
                    _vm.Home.RefreshStatus();
                    UpdateTrayStatus();
                });
                _tray.ToggleGameModeRequested += () => Dispatcher.Invoke(() =>
                {
                    _vm.ToggleGameMode();
                    UpdateTrayStatus();
                    _tray?.ShowBalloon("Игровой режим", _vm.Settings.GameModeActive
                        ? "🎮 Игровой режим активирован (UDP трафик исключён)"
                        : "🎮 Игровой режим отключён");
                });
                _tray.ToggleMiniOverlayRequested += () => Dispatcher.Invoke(ToggleMiniOverlayWindow);
                _tray.SelectProfileRequested += profile => Dispatcher.Invoke(async () =>
                {
                    var (ok, msg) = await ProfileManager.ApplyProfileAsync(profile, _vm.Settings, _vm.Bypass, _vm.Strategies);
                    _tray?.ShowBalloon("Профиль настроек", msg);
                    _vm.Home.RefreshStatus();
                    UpdateTrayStatus();
                });
                _tray.SelectStrategyRequested += name => Dispatcher.Invoke(async () =>
                {
                    var strat = _vm.Strategies.Find(name);
                    if (strat != null)
                    {
                        _vm.Settings.SelectedStrategy = name;
                        SettingsStore.Save(_vm.Settings);
                        if (_vm.Bypass.GetStatus().IsRunning)
                        {
                            var res = await _vm.Bypass.SwitchToStrategyAsync(strat,
                                EngineService.GetGameFilterMode(_vm.Settings.EnginePath), _vm.Settings.ShowWinwsConsole);
                            _tray?.ShowBalloon("Смена стратегии", res.Message);
                        }
                        _vm.Home.RefreshStatus();
                        UpdateTrayStatus();
                    }
                });
                _tray.SelectDnsRequested += profile => Dispatcher.Invoke(async () =>
                {
                    var res = await DnsManagementService.ApplyDnsProfileAsync(profile);
                    _tray?.ShowBalloon("Безопасный DNS", res.Message);
                });
                _tray.OpenLogsRequested += () => Dispatcher.Invoke(() =>
                {
                    ShowFromTray();
                    _vm.Navigate("logs");
                });
                _tray.ExitRequested += () => Dispatcher.Invoke(() =>
                {
                    _reallyClosing = true;
                    Close();
                });

                UpdateTrayStatus();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось создать иконку в трее: " + ex.Message);
            }
        }

        public void UpdateTrayStatus()
        {
            if (_tray == null) return;
            var status = _vm.Bypass.GetStatus();
            var ping = _vm.RealTimePing?.HasData == true ? _vm.RealTimePing.SummaryText : null;
            _tray.UpdateState(status.IsRunning, _vm.Settings.SelectedStrategy, status.StateText, ping, _vm.ActiveGameName, _vm.Settings.GameModeActive);
            _tray.PopulateStrategiesMenu(_vm.Strategies.Items, _vm.Settings.SelectedStrategy);
            _tray.PopulateProfilesMenu(_vm.Profiles.Profiles);
        }

        public void ToggleMiniOverlayWindow()
        {
            if (_overlayWindow == null)
            {
                _overlayWindow = new MiniOverlayWindow(_vm.MiniOverlay);
                _vm.MiniOverlay.RequestOpenMain += () => Dispatcher.Invoke(ShowFromTray);
                _vm.MiniOverlay.RequestCloseOverlay += () => Dispatcher.Invoke(() => _overlayWindow?.Hide());
            }

            if (_overlayWindow.IsVisible)
            {
                _overlayWindow.Hide();
            }
            else
            {
                _overlayWindow.Show();
                _overlayWindow.Activate();
                _vm.MiniOverlay.Refresh();
            }
        }

        /// <summary>Диалог конфликта со старым запретом при старте + выполнение выбора.</summary>
        private async void ShowLegacyDialog(LegacyInstallInfo info)
        {
            try
            {
                var dialog = new LegacyZapretDialog(info) { Owner = this };
                var accepted = dialog.ShowDialog() == true;
                var choice = accepted ? dialog.Choice : LegacyChoice.Later;
                if (dialog.DontAskChecked || choice == LegacyChoice.DontAsk)
                {
                    _vm.Settings.LegacyZapretDismissed = true;
                    SettingsStore.Save(_vm.Settings);
                    return;
                }
                if (choice == LegacyChoice.Later || choice == LegacyChoice.None) return;

                if (choice == LegacyChoice.ImportAndTakeOver)
                {
                    var (ok, message, strategyName) = LegacyZapret.ImportUserData(
                        info.ForeignRoot, _vm.Settings.EnginePath, info.StrategyName);
                    _vm.Home.ShowInfo(message);
                    if (ok && !string.IsNullOrEmpty(strategyName) && _vm.Strategies.Find(strategyName) != null)
                    {
                        _vm.Settings.SelectedStrategy = strategyName;
                        SettingsStore.Save(_vm.Settings);
                    }
                    _vm.Home.ReloadFromEngine();
                }

                var stopped = await LegacyZapret.StopLegacyAsync(info);
                AppLog.Info(stopped.Message);

                if (choice == LegacyChoice.StopOnly)
                {
                    _vm.Home.ShowInfo(stopped.Message);
                    _vm.Home.RefreshStatus();
                    return;
                }

                // TakeOver / Import: запускаем обход через GUI
                if (!EngineService.IsEngineReady(_vm.Settings.EnginePath))
                {
                    _vm.Home.ShowWarning("Старый запрет выключен. Установите движок в «Настройках» → «Обновления».");
                    _vm.Navigate("updates");
                    return;
                }

                var strategy = _vm.Strategies.Find(_vm.Settings.SelectedStrategy) ?? _vm.Strategies.Recommended;
                if (strategy == null)
                {
                    _vm.Home.RefreshStatus();
                    return;
                }

                var result = await _vm.Bypass.StartAsync(strategy,
                    EngineService.GetGameFilterMode(_vm.Settings.EnginePath), _vm.Settings.ShowWinwsConsole);
                _vm.Home.ShowInfo(result.Message);
                _vm.Home.RefreshStatus();
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка разрешения конфликта со старым запретом: " + ex.Message);
            }
        }

        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var viewer = FindScrollableViewer(e.OriginalSource as DependencyObject, e.Delta);
            if (viewer == null) return;

            var offset = viewer.VerticalOffset;
            var change = -e.Delta / 120d * 48d;
            var next = Math.Clamp(offset + change, 0d, viewer.ScrollableHeight);
            if (Math.Abs(next - offset) < 0.1) return;

            viewer.ScrollToVerticalOffset(next);
            e.Handled = true;
        }

        private ScrollViewer? FindScrollableViewer(DependencyObject? source, int wheelDelta)
        {
            ScrollViewer? fallback = null;
            var current = source;
            while (current != null)
            {
                if (current is ScrollViewer viewer)
                {
                    fallback ??= viewer;
                    var movingDown = wheelDelta < 0;
                    var canMove = movingDown
                        ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.1
                        : viewer.VerticalOffset > 0.1;
                    if (canMove) return viewer;
                }

                current = GetParent(current);
            }

            if (PageScroll != null && PageScroll.ScrollableHeight > 0)
            {
                var movingDown = wheelDelta < 0;
                var canMove = movingDown
                    ? PageScroll.VerticalOffset < PageScroll.ScrollableHeight - 0.1
                    : PageScroll.VerticalOffset > 0.1;
                if (canMove) return PageScroll;
            }

            return fallback;
        }

        private static DependencyObject? GetParent(DependencyObject source)
        {
            if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(source);
            if (source is FrameworkContentElement content)
                return content.Parent;
            return LogicalTreeHelper.GetParent(source);
        }

        private void ShowPage(string key)
        {
            if (!_pages.TryGetValue(key, out var page)) return;
            PageHost.Content = page;
            // Не переносим горизонтальное положение предыдущей страницы на новую.
            // При увеличенном масштабе прокрутка всё равно остаётся доступной вручную.
            PageScroll.ScrollToHome();
            PageScroll.ScrollToTop();
            PlayPageTransition(page);
            AppLog.Debug("Открыта страница: " + key);

            if (key == "strategies") _vm.StrategiesPage.Refresh();
            if (key == "home") _vm.Home.RefreshStatus();
        }

        private static void PlayPageTransition(UIElement page)
        {
            page.Opacity = 0;
            var slide = new TranslateTransform(12, 0);
            page.RenderTransform = slide;
            page.RenderTransformOrigin = new System.Windows.Point(0.02, 0);

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0,
                TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
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
            _vm.Hotkeys.Unregister();
            _vm.CloseTaskbarMetricsWindow();
            _overlayWindow?.CloseDirectly();
            _tray?.Dispose();

            if (Application.Current is App app) app.ShutdownApp();
        }
    }
}
