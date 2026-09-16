using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        // Защита от бесконечных окон с ошибками: запоминаем последний показанный диалог
        private static DateTime _lastErrorDialogAt = DateTime.MinValue;
        private static string _lastErrorText = "";
        private static int _suppressedErrors;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                AppLog.Error("Необработанная ошибка: " + args.ExceptionObject);

            // Ошибки забытых фоновых задач — только в журнал, без окон и падения приложения
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Error("Необработанная ошибка фоновой задачи: " + args.Exception);
                args.SetObserved();
            };

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

            // При первом запуске движка ещё нет — ставим его автоматически,
            // затем (если включено в настройках) тихо проверяем обновления.
            var viewModel = _viewModel;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2500);

                    // EnsureEngineInstalledAsync трогает UI-коллекции, поэтому выполняем
                    // его на UI-потоке, а через Unwrap() дожидаемся именно bool-результата
                    // (без Unwrap await вернул бы Task<bool> вместо bool).
                    var installed = await window.Dispatcher.InvokeAsync(
                        () => viewModel.Updates.EnsureEngineInstalledAsync()).Task.Unwrap();
                    if (installed && settings.AutoCheckEngineUpdates)
                        await window.Dispatcher.InvokeAsync(async () => await viewModel.Updates.CheckAsync());
                }
                catch (Exception ex)
                {
                    AppLog.Error("Ошибка фоновой задачи при запуске: " + ex.Message);
                }
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
            var text = e.Exception.GetType().Name + ": " + e.Exception.Message;
            AppLog.Error("Ошибка интерфейса: " + e.Exception);

            // Защита от бесконечных окон: повторяющиеся и частые ошибки гасим —
            // диалог показываем не чаще раза в 10 секунд и только для нового текста.
            // Все ошибки при этом сохраняются в журнале (страница «Журнал»).
            var now = DateTime.Now;
            var isDuplicate = string.Equals(text, _lastErrorText, StringComparison.Ordinal);
            var tooSoon = (now - _lastErrorDialogAt).TotalSeconds < 10;

            if (isDuplicate || tooSoon)
            {
                _suppressedErrors++;
                AppLog.Warn($"Повторяющаяся ошибка интерфейса подавлена (всего подавлено: {_suppressedErrors}): {text}");
                e.Handled = true;
                return;
            }

            _lastErrorDialogAt = now;
            _lastErrorText = text;

            var suffix = _suppressedErrors > 0
                ? $"\n\n(До этого было подавлено повторяющихся ошибок: {_suppressedErrors}.)"
                : "";
            _suppressedErrors = 0;

            try
            {
                MessageBox.Show(
                    "Произошла ошибка:\n\n" + e.Exception.Message +
                    "\n\nПодробности записаны в журнал (страница «Журнал»)." + suffix,
                    "Zapret GUI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception dialogEx)
            {
                AppLog.Error("Не удалось показать окно ошибки: " + dialogEx.Message);
            }

            e.Handled = true;
        }
    }
}
