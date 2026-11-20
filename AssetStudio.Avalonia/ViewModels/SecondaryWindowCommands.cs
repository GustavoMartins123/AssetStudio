using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AssetStudio.Avalonia.ViewModels
{
    internal sealed class SecondaryRelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public SecondaryRelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = _ => execute();
            _canExecute = canExecute == null ? null : _ => canExecute();
        }

        public SecondaryRelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _execute(parameter);
        }

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class SecondaryAsyncCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Func<object?, bool>? _canExecute;
        private readonly bool _allowConcurrentExecution;
        private readonly Action<Exception>? _onException;
        private bool _isExecuting;

        public SecondaryAsyncCommand(
            Func<Task> executeAsync,
            Func<bool>? canExecute = null,
            bool allowConcurrentExecution = false,
            Action<Exception>? onException = null)
        {
            ArgumentNullException.ThrowIfNull(executeAsync);
            _executeAsync = _ => executeAsync();
            _canExecute = canExecute == null ? null : _ => canExecute();
            _allowConcurrentExecution = allowConcurrentExecution;
            _onException = onException;
        }

        public SecondaryAsyncCommand(
            Func<object?, Task> executeAsync,
            Func<object?, bool>? canExecute = null,
            bool allowConcurrentExecution = false,
            Action<Exception>? onException = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
            _allowConcurrentExecution = allowConcurrentExecution;
            _onException = onException;
        }

        public bool IsExecuting => _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            if (_isExecuting && !_allowConcurrentExecution)
            {
                return false;
            }

            return _canExecute?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter)
        {
            _ = ExecuteFireAndForgetAsync(parameter);
        }

        public async Task ExecuteAsync(object? parameter = null)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isExecuting = true;
            NotifyCanExecuteChanged();
            try
            {
                await _executeAsync(parameter);
            }
            finally
            {
                _isExecuting = false;
                NotifyCanExecuteChanged();
            }
        }

        private async Task ExecuteFireAndForgetAsync(object? parameter)
        {
            try
            {
                await ExecuteAsync(parameter);
            }
            catch (Exception ex)
            {
                _onException?.Invoke(ex);
            }
        }

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
