using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AssetStudio.Avalonia;

public static class MessageBox
{
    private static readonly object ActiveLock = new();
    private static MessageBoxWindow? _activeInstance;

    public static void Show(Window? owner, string text, string title = "Message")
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (ActiveLock)
            {
                if (_activeInstance != null)
                {
                    _activeInstance.ViewModel.AppendMessage(text);
                    return;
                }

                var viewModel = MessageBoxViewModel.CreateOk(
                    message: text,
                    title: title,
                    showCopyButton: true,
                    useMonospaceLines: true,
                    dispatchToUi: action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));

                var window = new MessageBoxWindow(viewModel)
                {
                    WindowStartupLocation = owner != null && owner.IsVisible
                        ? WindowStartupLocation.CenterOwner
                        : WindowStartupLocation.CenterScreen
                };

                _activeInstance = window;
                window.Closed += (_, _) =>
                {
                    lock (ActiveLock)
                    {
                        if (ReferenceEquals(_activeInstance, window))
                        {
                            _activeInstance = null;
                        }
                    }
                };

                if (owner != null && owner.IsVisible)
                {
                    window.ShowDialog(owner);
                }
                else
                {
                    window.Show();
                }
            }
        });
    }

    public static async Task<MessageBoxResult> ShowDialogAsync(Window owner, MessageBoxViewModel viewModel)
    {
        var window = new MessageBoxWindow(viewModel);
        var result = await window.ShowDialog<MessageBoxResult?>(owner);
        return result ?? MessageBoxResult.None;
    }

    public static async Task<bool> ShowOkCancelAsync(
        Window owner,
        string text,
        string title = "Confirm",
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        var viewModel = MessageBoxViewModel.CreateOkCancel(text, title, okLabel, cancelLabel);
        var result = await ShowDialogAsync(owner, viewModel);
        return result == MessageBoxResult.Ok;
    }

    public static async Task<bool> ShowYesNoAsync(
        Window owner,
        string text,
        string title = "Confirm",
        string yesLabel = "Yes",
        string noLabel = "No")
    {
        var viewModel = MessageBoxViewModel.CreateYesNo(text, title, yesLabel, noLabel);
        var result = await ShowDialogAsync(owner, viewModel);
        return result == MessageBoxResult.Yes;
    }

    public static async Task<bool> ShowConfirmationAsync(
        Window owner,
        string text,
        string title = "Confirm",
        string confirmLabel = "OK",
        string cancelLabel = "Cancel",
        bool isDestructive = false)
    {
        var viewModel = MessageBoxViewModel.CreateOkCancel(
            message: text,
            title: title,
            okLabel: confirmLabel,
            cancelLabel: cancelLabel,
            isDestructive: isDestructive);
        var result = await ShowDialogAsync(owner, viewModel);
        return result == MessageBoxResult.Ok;
    }

    public static async Task<RiskyLoadChoice> ShowRiskyLoadAsync(
        Window owner,
        string message,
        string title = "Large Unity project detected")
    {
        var viewModel = MessageBoxViewModel.CreateRiskyLoad(message, title);
        await ShowDialogAsync(owner, viewModel);
        return viewModel.RiskyLoadResult;
    }

    public static async Task<MemoryPressureResult> ShowMemoryPressureWarningAsync(
        Window owner,
        string message,
        string title = "Memory pressure warning")
    {
        var viewModel = MessageBoxViewModel.CreateMemoryPressureWarning(message, title);
        await ShowDialogAsync(owner, viewModel);
        return viewModel.MemoryPressureChoiceResult;
    }
}
