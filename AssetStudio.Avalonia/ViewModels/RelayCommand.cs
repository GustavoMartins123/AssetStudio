using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter = null)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public void Execute(object? parameter = null)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _execute();
        }

        public void NotifyCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Predicate<T?>? _canExecute;

        public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter = null)
        {
            if (!RelayCommandParameterHelper.TryConvertParameter<T>(parameter, out var typedParameter))
            {
                return false;
            }

            return _canExecute?.Invoke(typedParameter) ?? true;
        }

        public void Execute(object? parameter = null)
        {
            if (!RelayCommandParameterHelper.TryConvertParameter<T>(parameter, out var typedParameter))
            {
                return;
            }

            if (_canExecute != null && !_canExecute(typedParameter))
            {
                return;
            }

            _execute(typedParameter);
        }

        public void NotifyCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class AsyncRelayCommand : ViewModelBase, ICommand
    {
        private readonly Func<CancellationToken, Task> _execute;
        private readonly Func<bool>? _canExecute;
        private readonly bool _allowConcurrentExecutions;
        private readonly Action<Exception>? _onException;
        private readonly object _syncRoot = new();
        private readonly List<CancellationTokenSource> _activeCts = new();
        private int _runningCount;
        private bool _isRunning;

        public AsyncRelayCommand(
            Func<CancellationToken, Task> execute,
            Func<bool>? canExecute = null,
            bool allowConcurrentExecutions = false,
            Action<Exception>? onException = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
            _allowConcurrentExecutions = allowConcurrentExecutions;
            _onException = onException;
        }

        public AsyncRelayCommand(
            Func<Task> execute,
            Func<bool>? canExecute = null,
            bool allowConcurrentExecutions = false,
            Action<Exception>? onException = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = _ => execute();
            _canExecute = canExecute;
            _allowConcurrentExecutions = allowConcurrentExecutions;
            _onException = onException;
        }

        public event EventHandler? CanExecuteChanged;

        public bool AllowConcurrentExecutions => _allowConcurrentExecutions;

        public bool IsRunning
        {
            get => _isRunning;
            private set => SetProperty(ref _isRunning, value);
        }

        public bool CanExecute(object? parameter = null)
        {
            lock (_syncRoot)
            {
                if (!_allowConcurrentExecutions && _runningCount > 0)
                {
                    return false;
                }
            }

            return _canExecute?.Invoke() ?? true;
        }

        public async void Execute(object? parameter = null)
        {
            try
            {
                await ExecuteAsync(parameter);
            }
            catch (Exception ex)
            {
                if (_onException != null)
                {
                    _onException(ex);
                }
            }
        }

        public async Task ExecuteAsync(object? parameter = null)
        {
            if (_canExecute != null && !_canExecute())
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_syncRoot)
            {
                if (!_allowConcurrentExecutions && _runningCount > 0)
                {
                    return;
                }

                _runningCount++;
                cts = new CancellationTokenSource();
                _activeCts.Add(cts);
                IsRunning = true;
            }

            NotifyCanExecuteChanged();

            try
            {
                await _execute(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_onException != null)
                {
                    _onException(ex);
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                lock (_syncRoot)
                {
                    _activeCts.Remove(cts);
                    _runningCount = Math.Max(0, _runningCount - 1);
                    IsRunning = _runningCount > 0;
                }

                cts.Dispose();
                NotifyCanExecuteChanged();
            }
        }

        public void Cancel()
        {
            CancellationTokenSource[] snapshot;
            lock (_syncRoot)
            {
                if (_activeCts.Count == 0)
                {
                    return;
                }

                snapshot = _activeCts.ToArray();
            }

            for (var i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void NotifyCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class AsyncRelayCommand<T> : ViewModelBase, ICommand
    {
        private readonly Func<T?, CancellationToken, Task> _execute;
        private readonly Predicate<T?>? _canExecute;
        private readonly bool _allowConcurrentExecutions;
        private readonly Action<Exception>? _onException;
        private readonly object _syncRoot = new();
        private readonly List<CancellationTokenSource> _activeCts = new();
        private int _runningCount;
        private bool _isRunning;

        public AsyncRelayCommand(
            Func<T?, CancellationToken, Task> execute,
            Predicate<T?>? canExecute = null,
            bool allowConcurrentExecutions = false,
            Action<Exception>? onException = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
            _allowConcurrentExecutions = allowConcurrentExecutions;
            _onException = onException;
        }

        public AsyncRelayCommand(
            Func<T?, Task> execute,
            Predicate<T?>? canExecute = null,
            bool allowConcurrentExecutions = false,
            Action<Exception>? onException = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = (parameter, _) => execute(parameter);
            _canExecute = canExecute;
            _allowConcurrentExecutions = allowConcurrentExecutions;
            _onException = onException;
        }

        public event EventHandler? CanExecuteChanged;

        public bool AllowConcurrentExecutions => _allowConcurrentExecutions;

        public bool IsRunning
        {
            get => _isRunning;
            private set => SetProperty(ref _isRunning, value);
        }

        public bool CanExecute(object? parameter = null)
        {
            if (!RelayCommandParameterHelper.TryConvertParameter<T>(parameter, out var typedParameter))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_allowConcurrentExecutions && _runningCount > 0)
                {
                    return false;
                }
            }

            return _canExecute?.Invoke(typedParameter) ?? true;
        }

        public async void Execute(object? parameter = null)
        {
            if (!RelayCommandParameterHelper.TryConvertParameter<T>(parameter, out var typedParameter))
            {
                return;
            }

            try
            {
                await ExecuteAsync(typedParameter);
            }
            catch (Exception ex)
            {
                if (_onException != null)
                {
                    _onException(ex);
                }
            }
        }

        public async Task ExecuteAsync(T? parameter = default)
        {
            if (_canExecute != null && !_canExecute(parameter))
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_syncRoot)
            {
                if (!_allowConcurrentExecutions && _runningCount > 0)
                {
                    return;
                }

                _runningCount++;
                cts = new CancellationTokenSource();
                _activeCts.Add(cts);
                IsRunning = true;
            }

            NotifyCanExecuteChanged();

            try
            {
                await _execute(parameter, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_onException != null)
                {
                    _onException(ex);
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                lock (_syncRoot)
                {
                    _activeCts.Remove(cts);
                    _runningCount = Math.Max(0, _runningCount - 1);
                    IsRunning = _runningCount > 0;
                }

                cts.Dispose();
                NotifyCanExecuteChanged();
            }
        }

        public void Cancel()
        {
            CancellationTokenSource[] snapshot;
            lock (_syncRoot)
            {
                if (_activeCts.Count == 0)
                {
                    return;
                }

                snapshot = _activeCts.ToArray();
            }

            for (var i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void NotifyCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static class RelayCommandParameterHelper
    {
        public static bool TryConvertParameter<T>(object? parameter, out T? typedParameter)
        {
            if (parameter is null)
            {
                typedParameter = default;
                return true;
            }

            if (parameter is T directValue)
            {
                typedParameter = directValue;
                return true;
            }

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            if (targetType.IsEnum)
            {
                if (parameter is string enumText && Enum.TryParse(targetType, enumText, ignoreCase: true, out var parsedEnum) && parsedEnum is T typedEnum)
                {
                    typedParameter = typedEnum;
                    return true;
                }

                try
                {
                    var underlying = Enum.GetUnderlyingType(targetType);
                    var numeric = Convert.ChangeType(parameter, underlying, CultureInfo.InvariantCulture);
                    if (numeric != null && Enum.ToObject(targetType, numeric) is T convertedEnum)
                    {
                        typedParameter = convertedEnum;
                        return true;
                    }
                }
                catch
                {
                }

                typedParameter = default;
                return false;
            }

            if (parameter is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
            {
                try
                {
                    var converted = Convert.ChangeType(parameter, targetType, CultureInfo.InvariantCulture);
                    if (converted is T typedConverted)
                    {
                        typedParameter = typedConverted;
                        return true;
                    }
                }
                catch
                {
                }
            }

            typedParameter = default;
            return false;
        }
    }
}
