using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ZapretGui.Core
{
    /// <summary>Базовый класс для ViewModel с уведомлением об изменениях.</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => Raise(name);

        protected void Raise(string? name)
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(() => handler(this, new PropertyChangedEventArgs(name)));
            else
                handler(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Устанавливает поле и уведомляет, если значение изменилось.</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>Синхронная команда.</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : new Func<object?, bool>(_ => canExecute())) { }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object? parameter)
        {
            try { _execute(parameter); }
            catch (Exception ex) { AppLog.Error("Ошибка выполнения команды: " + ex); }
        }

        public void RaiseCanExecuteChanged() => Dispatch(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));

        internal static void Dispatch(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(action);
            else
                action();
        }
    }

    /// <summary>Асинхронная команда с защитой от повторного запуска.</summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : new Func<object?, bool>(_ => canExecute())) { }

        public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool IsRunning => _isRunning;

        public bool CanExecute(object? parameter) => !_isRunning && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter)
        {
            if (_isRunning) return;
            _isRunning = true;
            RaiseCanExecuteChanged();
            try { await _execute(parameter); }
            catch (Exception ex) { AppLog.Error("Ошибка выполнения команды: " + ex); }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => RelayCommand.Dispatch(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}
