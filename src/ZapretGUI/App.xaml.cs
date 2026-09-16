using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ZapretGui.Core;
using ZapretGui.ViewModels;
using ZapretGui.Views;

namespace ZapretGui
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private MainViewModel? _viewModel;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                AppLog.Error("Необработанная ошибка: " + args.ExceptionObject);

            // Защита от двух одновременно запущенных копий
            _singleInstance = new Mutex(true, "ZapretGUI.SingleInstance", out var isNew);
            if (!isNew)
            {
                MessageBox.Show("Zapret GUI уже запущен. Найдите его в трее рядом с часами.",
                    "Zapret GUI", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            AppLog.Info("=== Zapret GUI запущен ===");
            AppLog.Info($"Права администратора: {(Shell.IsAdmin() ? "да" : "нет")}");

            var settings = SettingsStore.Load();
            ThemeService.Apply(settings.Theme);

            _viewModel = new MainViewModel(settings);
            var window = new MainWindow(_viewModel);

            if (settings.StartMinimized)
            {
                window.ShowInTaskbar = false;
                window.WindowState = WindowState.Minimized;
                window.Show();
                window.Hide();
            }
            else
            {
                window.Show();
            }

            if (settings.AutoCheckEngineUpdates)
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(2500);
                    await window.Dispatcher.InvokeAsync(async () => await _viewModel.Updates.CheckAsync());
                });
        }

        public void ShutdownApp()
        {
            try
            {
                if (_viewModel != null)
                {
                    var task = _viewModel.ShutdownAsync();
                    task.Wait(6000);
                }
                SettingsStore.Save(_viewModel?.Settings ?? new AppSettings());
                AppLog.Info("=== Zapret GUI завершает работу ===");
            }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка при завершении: " + ex.Message);
            }

            try { _singleInstance?.ReleaseMutex(); } catch { }
            Shutdown();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Ошибка интерфейса: " + e.Exception);
            MessageBox.Show(
                "Произошла ошибка:\n\n" + e.Exception.Message +
                "\n\nПодробности записаны в журнал (страница «Журнал»).",
                "Zapret GUI", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
