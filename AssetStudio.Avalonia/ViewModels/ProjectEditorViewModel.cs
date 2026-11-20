using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using AssetStudio.Avalonia;

namespace AssetStudio.Avalonia.ViewModels
{
    public sealed class ProjectEditorViewModel : ViewModelBase
    {
        private readonly ProjectManagerStore? _store;
        private readonly ManagedProject _project;
        private readonly Func<string, bool> _directoryExists;
        private readonly Func<string, string?> _findProjectIcon;
        private readonly Func<string, string?> _createExecutableIconPreview;
        private readonly SecondaryAsyncCommand _browseCommand;
        private readonly SecondaryAsyncCommand _browseIconCommand;
        private readonly SecondaryRelayCommand _detectIconCommand;
        private readonly SecondaryRelayCommand _saveCommand;
        private readonly SecondaryRelayCommand _cancelCommand;

        private string _name = string.Empty;
        private bool _useAutoName;
        private string _projectRoot = string.Empty;
        private string _iconPath = string.Empty;
        private string? _iconPreviewPath;
        private string _pendingIconSourcePath = string.Empty;
        private string _validationMessage = string.Empty;
        private string _transientStatusMessage = string.Empty;
        private bool _canSave;

        public ProjectEditorViewModel(
            ProjectManagerStore? store,
            ManagedProject? project = null,
            Func<Task<string?>>? pickRootFolderAsync = null,
            Func<Task<string?>>? pickIconFileAsync = null,
            Func<string, bool>? directoryExists = null,
            Func<string, string?>? findProjectIcon = null,
            Func<string, string?>? createExecutableIconPreview = null)
        {
            _store = store;
            IsEditMode = project != null;
            _project = project?.Clone() ?? new ManagedProject();
            PickRootFolderAsync = pickRootFolderAsync;
            PickIconFileAsync = pickIconFileAsync;
            _directoryExists = directoryExists ?? Directory.Exists;
            _findProjectIcon = findProjectIcon ?? (root => _store?.TryFindProjectIcon(root));
            _createExecutableIconPreview = createExecutableIconPreview ?? (path => _store?.TryCreateExecutableIconPreview(path));

            _browseCommand = new SecondaryAsyncCommand(BrowseRootAsync);
            _browseIconCommand = new SecondaryAsyncCommand(BrowseIconAsync);
            _detectIconCommand = new SecondaryRelayCommand(DetectIcon);
            _saveCommand = new SecondaryRelayCommand(Save, () => CanSave);
            _cancelCommand = new SecondaryRelayCommand(Cancel);

            _name = _project.Name;
            _useAutoName = _project.UseAutoName;
            _projectRoot = _project.ProjectRoot;
            _iconPath = _project.IconPath;
            _iconPreviewPath = ResolvePreviewPath(_project.IconPath);

            Validate();
        }

        public event Action<bool>? RequestClose;

        public bool IsEditMode { get; }

        public string WindowTitle => IsEditMode ? "Edit project" : "Add project";

        public Func<Task<string?>>? PickRootFolderAsync { get; set; }

        public Func<Task<string?>>? PickIconFileAsync { get; set; }

        public ManagedProject? Result { get; private set; }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value ?? string.Empty))
                {
                    _transientStatusMessage = string.Empty;
                    Validate();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public bool UseAutoName
        {
            get => _useAutoName;
            set
            {
                if (SetProperty(ref _useAutoName, value))
                {
                    _transientStatusMessage = string.Empty;
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string ProjectRoot
        {
            get => _projectRoot;
            set
            {
                if (SetProperty(ref _projectRoot, value ?? string.Empty))
                {
                    _transientStatusMessage = string.Empty;
                    Validate();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string IconPath
        {
            get => _iconPath;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _iconPath, normalized))
                {
                    _transientStatusMessage = string.Empty;
                    IconPreviewPath = ResolvePreviewPath(normalized);
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string? IconPreviewPath
        {
            get => _iconPreviewPath;
            private set => SetProperty(ref _iconPreviewPath, value);
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            private set
            {
                if (SetProperty(ref _validationMessage, value))
                {
                    OnPropertyChanged(nameof(HasValidationMessage));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

        public bool CanSave
        {
            get => _canSave;
            private set
            {
                if (SetProperty(ref _canSave, value))
                {
                    _saveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string StatusText
        {
            get
            {
                if (!string.IsNullOrEmpty(_transientStatusMessage))
                {
                    return _transientStatusMessage;
                }

                if (!string.IsNullOrEmpty(_validationMessage))
                {
                    return _validationMessage;
                }

                return _useAutoName
                    ? "The project card will use the detected game name after assets are loaded."
                    : "The custom name will stay as typed here.";
            }
        }

        public ICommand BrowseCommand => _browseCommand;

        public ICommand BrowseIconCommand => _browseIconCommand;

        public ICommand DetectIconCommand => _detectIconCommand;

        public ICommand SaveCommand => _saveCommand;

        public ICommand CancelCommand => _cancelCommand;

        public async Task BrowseRootAsync()
        {
            if (PickRootFolderAsync == null)
            {
                return;
            }

            var path = await PickRootFolderAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            ProjectRoot = path;

            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = ExtractFolderName(path);
            }

            if (string.IsNullOrWhiteSpace(IconPath))
            {
                DetectIcon();
            }
        }

        public async Task BrowseIconAsync()
        {
            if (PickIconFileAsync == null)
            {
                return;
            }

            var path = await PickIconFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _pendingIconSourcePath = path;
            IconPath = path;
        }

        public void DetectIcon()
        {
            var root = _projectRoot.Trim();
            if (string.IsNullOrWhiteSpace(root) || HasInvalidPathChars(root) || !_directoryExists(root))
            {
                _transientStatusMessage = "Choose an existing project root before detecting an icon.";
                OnPropertyChanged(nameof(StatusText));
                return;
            }

            var detected = _findProjectIcon(root);
            if (string.IsNullOrWhiteSpace(detected))
            {
                _transientStatusMessage = "No icon-like image was found in this project root.";
                OnPropertyChanged(nameof(StatusText));
                return;
            }

            _transientStatusMessage = string.Empty;
            _pendingIconSourcePath = detected;
            IconPath = detected;
            OnPropertyChanged(nameof(StatusText));
        }

        public void Save()
        {
            Validate();
            if (!CanSave)
            {
                return;
            }

            var name = _name.Trim();
            var root = _projectRoot.Trim();
            var icon = _iconPath.Trim();

            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(root))
            {
                name = ExtractFolderName(root);
            }

            _project.Name = name;
            _project.UseAutoName = _useAutoName;
            _project.ProjectRoot = root;
            if (!string.IsNullOrWhiteSpace(root) && string.IsNullOrWhiteSpace(_project.LastLoadPath))
            {
                _project.LastLoadPath = root;
            }

            if (!string.IsNullOrWhiteSpace(_pendingIconSourcePath))
            {
                _project.PendingIconSourcePath = _pendingIconSourcePath;
            }
            else if (!string.IsNullOrWhiteSpace(icon) && !string.Equals(icon, _project.IconPath, StringComparison.OrdinalIgnoreCase))
            {
                _project.PendingIconSourcePath = icon;
            }

            Result = _project;
            RequestClose?.Invoke(true);
        }

        public void Cancel()
        {
            Result = null;
            RequestClose?.Invoke(false);
        }

        private void Validate()
        {
            var name = _name.Trim();
            var root = _projectRoot.Trim();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(root))
            {
                ValidationMessage = "Set a project name or choose a project root.";
                CanSave = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(root))
            {
                if (HasInvalidPathChars(root) || !_directoryExists(root))
                {
                    ValidationMessage = "Project root folder does not exist or contains invalid characters.";
                    CanSave = false;
                    return;
                }
            }

            ValidationMessage = string.Empty;
            CanSave = true;
        }

        private string? ResolvePreviewPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim();
            if (HasInvalidPathChars(trimmed))
            {
                return null;
            }

            if (string.Equals(Path.GetExtension(trimmed), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return _createExecutableIconPreview(trimmed) ?? trimmed;
            }

            return trimmed;
        }

        private static bool HasInvalidPathChars(string path)
        {
            return path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf('\0') >= 0;
        }

        private static string ExtractFolderName(string path)
        {
            var trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed);
        }
    }
}
