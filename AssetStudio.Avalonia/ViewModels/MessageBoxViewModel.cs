using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using AssetStudio;
using AssetStudio.Avalonia;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum MessageBoxVariant
    {
        Ok,
        OkCancel,
        YesNo,
        RiskyLoad,
        MemoryPressure
    }

    public enum MessageBoxResult
    {
        None,
        Ok,
        Cancel,
        Yes,
        No,
        Primary,
        Secondary
    }

    public sealed class MessageBoxViewModel : ViewModelBase
    {
        private readonly object _syncRoot = new();
        private readonly StringBuilder _contentBuilder = new();
        private readonly List<string> _pendingLines = new();
        private readonly Action<Action> _dispatchToUi;
        private readonly RelayCommand _primaryCommand;
        private readonly RelayCommand _secondaryCommand;
        private readonly RelayCommand _cancelCommand;
        private readonly AsyncRelayCommand _copyCommand;

        private string _title;
        private string _message;
        private int _errorCount = 1;
        private bool _updatePending;
        private MessageBoxResult _result = MessageBoxResult.None;
        private RiskyLoadChoice _riskyLoadResult = RiskyLoadChoice.Cancel;
        private MemoryPressureResult _memoryPressureChoiceResult = MemoryPressureResult.Cancel;

        public MessageBoxViewModel(
            string message,
            string title = "Message",
            MessageBoxVariant variant = MessageBoxVariant.Ok,
            string? primaryButtonLabel = null,
            string? secondaryButtonLabel = null,
            string? cancelButtonLabel = null,
            bool showCopyButton = false,
            bool useMonospaceLines = false,
            bool isPrimaryBold = false,
            bool isPrimaryDestructive = false,
            double dialogWidth = 560,
            double dialogHeight = 300,
            double dialogMinWidth = 400,
            double dialogMinHeight = 220,
            bool canResize = true,
            Action<Action>? dispatchToUi = null,
            Func<string, Task>? copyToClipboardAsync = null)
        {
            Variant = variant;
            _title = title ?? "Message";
            _message = message ?? string.Empty;
            _dispatchToUi = dispatchToUi ?? (static action => action());
            CopyToClipboardAsync = copyToClipboardAsync;

            ShowCopyButton = showCopyButton;
            UseMonospaceLines = useMonospaceLines;
            IsPrimaryBold = isPrimaryBold;
            IsPrimaryDestructive = isPrimaryDestructive;
            DialogWidth = dialogWidth;
            DialogHeight = dialogHeight;
            DialogMinWidth = dialogMinWidth;
            DialogMinHeight = dialogMinHeight;
            CanResize = canResize;

            Lines = new ObservableCollection<string>();
            _contentBuilder.Append(_message);
            foreach (var line in SplitLines(_message))
            {
                Lines.Add(line);
            }

            switch (variant)
            {
                case MessageBoxVariant.Ok:
                    PrimaryButtonLabel = primaryButtonLabel ?? "OK";
                    ShowPrimaryButton = true;
                    SecondaryButtonLabel = string.Empty;
                    ShowSecondaryButton = false;
                    CancelButtonLabel = string.Empty;
                    ShowCancelButton = false;
                    break;

                case MessageBoxVariant.OkCancel:
                    PrimaryButtonLabel = primaryButtonLabel ?? "OK";
                    ShowPrimaryButton = true;
                    SecondaryButtonLabel = string.Empty;
                    ShowSecondaryButton = false;
                    CancelButtonLabel = cancelButtonLabel ?? "Cancel";
                    ShowCancelButton = true;
                    break;

                case MessageBoxVariant.YesNo:
                    PrimaryButtonLabel = primaryButtonLabel ?? "Yes";
                    ShowPrimaryButton = true;
                    SecondaryButtonLabel = secondaryButtonLabel ?? "No";
                    ShowSecondaryButton = true;
                    CancelButtonLabel = string.Empty;
                    ShowCancelButton = false;
                    break;

                case MessageBoxVariant.RiskyLoad:
                    PrimaryButtonLabel = primaryButtonLabel ?? "Load in Safe/Lazy Mode (Recommended)";
                    ShowPrimaryButton = true;
                    SecondaryButtonLabel = secondaryButtonLabel ?? "Load anyway (Eager)";
                    ShowSecondaryButton = true;
                    CancelButtonLabel = cancelButtonLabel ?? "Cancel";
                    ShowCancelButton = true;
                    break;

                case MessageBoxVariant.MemoryPressure:
                    PrimaryButtonLabel = primaryButtonLabel ?? "Ignore and continue";
                    ShowPrimaryButton = true;
                    SecondaryButtonLabel = secondaryButtonLabel ?? "Stop and keep loaded";
                    ShowSecondaryButton = true;
                    CancelButtonLabel = cancelButtonLabel ?? "Cancel loading";
                    ShowCancelButton = true;
                    break;

                default:
                    PrimaryButtonLabel = primaryButtonLabel ?? "OK";
                    ShowPrimaryButton = true;
                    SecondaryButtonLabel = string.Empty;
                    ShowSecondaryButton = false;
                    CancelButtonLabel = string.Empty;
                    ShowCancelButton = false;
                    break;
            }

            _primaryCommand = new RelayCommand(ExecutePrimary, () => ShowPrimaryButton);
            _secondaryCommand = new RelayCommand(ExecuteSecondary, () => ShowSecondaryButton);
            _cancelCommand = new RelayCommand(ExecuteCancel, () => ShowCancelButton);
            _copyCommand = new AsyncRelayCommand(ExecuteCopyAsync, () => ShowCopyButton);
        }

        public event Action<MessageBoxResult>? RequestClose;

        public event Action? LinesAppended;

        public Func<string, Task>? CopyToClipboardAsync { get; set; }

        public MessageBoxVariant Variant { get; }

        public ObservableCollection<string> Lines { get; }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value ?? string.Empty);
        }

        public string Message
        {
            get => _message;
            private set => SetProperty(ref _message, value ?? string.Empty);
        }

        public int ErrorCount
        {
            get => _errorCount;
            private set => SetProperty(ref _errorCount, value);
        }

        public bool UseMonospaceLines { get; }

        public bool UseWrappedMessage => !UseMonospaceLines;

        public bool ShowCopyButton { get; }

        public string CopyButtonLabel => "Copy";

        public bool ShowPrimaryButton { get; }

        public string PrimaryButtonLabel { get; }

        public bool IsPrimaryBold { get; }

        public bool IsPrimaryDestructive { get; }

        public bool ShowSecondaryButton { get; }

        public string SecondaryButtonLabel { get; }

        public bool ShowCancelButton { get; }

        public string CancelButtonLabel { get; }

        public double DialogWidth { get; }

        public double DialogHeight { get; }

        public double DialogMinWidth { get; }

        public double DialogMinHeight { get; }

        public bool CanResize { get; }

        public MessageBoxResult Result
        {
            get => _result;
            private set => SetProperty(ref _result, value);
        }

        public RiskyLoadChoice RiskyLoadResult
        {
            get => _riskyLoadResult;
            private set => SetProperty(ref _riskyLoadResult, value);
        }

        public MemoryPressureResult MemoryPressureChoiceResult
        {
            get => _memoryPressureChoiceResult;
            private set => SetProperty(ref _memoryPressureChoiceResult, value);
        }

        public ICommand PrimaryCommand => _primaryCommand;

        public ICommand SecondaryCommand => _secondaryCommand;

        public ICommand CancelCommand => _cancelCommand;

        public ICommand CopyCommand => _copyCommand;

        public static MessageBoxViewModel CreateOk(
            string message,
            string title = "Message",
            bool showCopyButton = true,
            bool useMonospaceLines = true,
            Action<Action>? dispatchToUi = null,
            Func<string, Task>? copyToClipboardAsync = null)
        {
            return new MessageBoxViewModel(
                message: message,
                title: title,
                variant: MessageBoxVariant.Ok,
                primaryButtonLabel: "OK",
                showCopyButton: showCopyButton,
                useMonospaceLines: useMonospaceLines,
                dialogWidth: 650,
                dialogHeight: 450,
                dialogMinWidth: 400,
                dialogMinHeight: 250,
                canResize: true,
                dispatchToUi: dispatchToUi,
                copyToClipboardAsync: copyToClipboardAsync);
        }

        public static MessageBoxViewModel CreateOkCancel(
            string message,
            string title = "Confirm",
            string okLabel = "OK",
            string cancelLabel = "Cancel",
            bool isDestructive = false)
        {
            return new MessageBoxViewModel(
                message: message,
                title: title,
                variant: MessageBoxVariant.OkCancel,
                primaryButtonLabel: okLabel,
                cancelButtonLabel: cancelLabel,
                showCopyButton: false,
                useMonospaceLines: false,
                isPrimaryDestructive: isDestructive,
                dialogWidth: 560,
                dialogHeight: 300,
                dialogMinWidth: 460,
                dialogMinHeight: 240,
                canResize: false);
        }

        public static MessageBoxViewModel CreateYesNo(
            string message,
            string title = "Confirm",
            string yesLabel = "Yes",
            string noLabel = "No")
        {
            return new MessageBoxViewModel(
                message: message,
                title: title,
                variant: MessageBoxVariant.YesNo,
                primaryButtonLabel: yesLabel,
                secondaryButtonLabel: noLabel,
                showCopyButton: false,
                useMonospaceLines: false,
                dialogWidth: 520,
                dialogHeight: 260,
                dialogMinWidth: 420,
                dialogMinHeight: 200,
                canResize: false);
        }

        public static MessageBoxViewModel CreateRiskyLoad(
            string message,
            string title = "Large Unity project detected")
        {
            return new MessageBoxViewModel(
                message: message,
                title: title,
                variant: MessageBoxVariant.RiskyLoad,
                primaryButtonLabel: "Load in Safe/Lazy Mode (Recommended)",
                secondaryButtonLabel: "Load anyway (Eager)",
                cancelButtonLabel: "Cancel",
                showCopyButton: false,
                useMonospaceLines: false,
                isPrimaryBold: true,
                dialogWidth: 640,
                dialogHeight: 440,
                dialogMinWidth: 540,
                dialogMinHeight: 340,
                canResize: true);
        }

        public static MessageBoxViewModel CreateMemoryPressureWarning(
            string message,
            string title = "Memory pressure warning")
        {
            return new MessageBoxViewModel(
                message: message,
                title: title,
                variant: MessageBoxVariant.MemoryPressure,
                primaryButtonLabel: "Ignore and continue",
                secondaryButtonLabel: "Stop and keep loaded",
                cancelButtonLabel: "Cancel loading",
                showCopyButton: false,
                useMonospaceLines: false,
                isPrimaryBold: true,
                dialogWidth: 600,
                dialogHeight: 240,
                dialogMinWidth: 450,
                dialogMinHeight: 180,
                canResize: true);
        }

        public void AppendMessage(string message)
        {
            string divider;
            int nextCount;
            string fullText;
            bool shouldSchedule;

            lock (_syncRoot)
            {
                _errorCount++;
                nextCount = _errorCount;
                divider = $"\n\n-----------------------------------------\n\nError {nextCount}:\n{message}";
                _contentBuilder.Append(divider);
                fullText = _contentBuilder.ToString();

                foreach (var line in SplitLines(divider))
                {
                    _pendingLines.Add(line);
                }

                shouldSchedule = !_updatePending;
                if (shouldSchedule)
                {
                    _updatePending = true;
                }
            }

            if (shouldSchedule)
            {
                _dispatchToUi(() => FlushPendingLines(nextCount, fullText));
            }
            else
            {
                _dispatchToUi(() =>
                {
                    ErrorCount = nextCount;
                    Title = $"Errors ({nextCount})";
                    Message = fullText;
                });
            }
        }

        public Task CopyMessageAsync() => ExecuteCopyAsync();

        private void FlushPendingLines(int latestErrorCount, string latestFullText)
        {
            List<string> linesToAdd;
            lock (_syncRoot)
            {
                linesToAdd = new List<string>(_pendingLines);
                _pendingLines.Clear();
                _updatePending = false;
                latestErrorCount = _errorCount;
                latestFullText = _contentBuilder.ToString();
            }

            ErrorCount = latestErrorCount;
            Title = $"Errors ({latestErrorCount})";
            Message = latestFullText;

            foreach (var line in linesToAdd)
            {
                Lines.Add(line);
            }

            if (linesToAdd.Count > 0)
            {
                LinesAppended?.Invoke();
            }
        }

        private async Task ExecuteCopyAsync()
        {
            if (CopyToClipboardAsync == null)
            {
                return;
            }

            string text;
            lock (_syncRoot)
            {
                text = _contentBuilder.ToString();
            }

            await CopyToClipboardAsync(text);
        }

        private void ExecutePrimary()
        {
            switch (Variant)
            {
                case MessageBoxVariant.Ok:
                case MessageBoxVariant.OkCancel:
                    Result = MessageBoxResult.Ok;
                    break;
                case MessageBoxVariant.YesNo:
                    Result = MessageBoxResult.Yes;
                    break;
                case MessageBoxVariant.RiskyLoad:
                    Result = MessageBoxResult.Primary;
                    RiskyLoadResult = RiskyLoadChoice.LazyLoad;
                    break;
                case MessageBoxVariant.MemoryPressure:
                    Result = MessageBoxResult.Primary;
                    MemoryPressureChoiceResult = MemoryPressureResult.Continue;
                    break;
                default:
                    Result = MessageBoxResult.Primary;
                    break;
            }

            RequestClose?.Invoke(Result);
        }

        private void ExecuteSecondary()
        {
            switch (Variant)
            {
                case MessageBoxVariant.YesNo:
                    Result = MessageBoxResult.No;
                    break;
                case MessageBoxVariant.RiskyLoad:
                    Result = MessageBoxResult.Secondary;
                    RiskyLoadResult = RiskyLoadChoice.EagerLoad;
                    break;
                case MessageBoxVariant.MemoryPressure:
                    Result = MessageBoxResult.Secondary;
                    MemoryPressureChoiceResult = MemoryPressureResult.StopAndKeep;
                    break;
                default:
                    Result = MessageBoxResult.Secondary;
                    break;
            }

            RequestClose?.Invoke(Result);
        }

        private void ExecuteCancel()
        {
            Result = MessageBoxResult.Cancel;
            RiskyLoadResult = RiskyLoadChoice.Cancel;
            MemoryPressureChoiceResult = MemoryPressureResult.Cancel;
            RequestClose?.Invoke(Result);
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        }
    }
}
