using System;

namespace AssetStudio.Avalonia.ViewModels
{
    public enum AppThemePreference
    {
        Default,
        Light,
        Dark
    }

    public sealed class WindowOptionsViewModel : ViewModelBase
    {
        private bool _displayAll;
        private bool _enablePreview = true;
        private bool _displayInfo = true;
        private bool _useDiskDecompressionCache = true;
        private bool _showErrorMessage;
        private string _specifyUnityVersion = string.Empty;
        private AppThemePreference _selectedTheme = AppThemePreference.Default;

        public WindowOptionsViewModel()
        {
            SetThemeDefaultCommand = new RelayCommand(() => SelectedTheme = AppThemePreference.Default);
            SetThemeLightCommand = new RelayCommand(() => SelectedTheme = AppThemePreference.Light);
            SetThemeDarkCommand = new RelayCommand(() => SelectedTheme = AppThemePreference.Dark);
        }

        public event Action? SettingsChanged;

        public event Action<AppThemePreference>? ThemeChanged;

        public event Action<bool>? DisplayAllChanged;

        public event Action<bool>? EnablePreviewChanged;

        public event Action<bool>? DisplayInfoChanged;

        public event Action<bool>? UseDiskDecompressionCacheChanged;

        public event Action<bool>? ShowErrorMessageChanged;

        public event Action<string>? SpecifyUnityVersionChanged;

        public RelayCommand SetThemeDefaultCommand { get; }

        public RelayCommand SetThemeLightCommand { get; }

        public RelayCommand SetThemeDarkCommand { get; }

        public bool DisplayAll
        {
            get => _displayAll;
            set
            {
                if (SetProperty(ref _displayAll, value))
                {
                    DisplayAllChanged?.Invoke(value);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public bool EnablePreview
        {
            get => _enablePreview;
            set
            {
                if (SetProperty(ref _enablePreview, value))
                {
                    EnablePreviewChanged?.Invoke(value);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public bool DisplayInfo
        {
            get => _displayInfo;
            set
            {
                if (SetProperty(ref _displayInfo, value))
                {
                    DisplayInfoChanged?.Invoke(value);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public bool UseDiskDecompressionCache
        {
            get => _useDiskDecompressionCache;
            set
            {
                if (SetProperty(ref _useDiskDecompressionCache, value))
                {
                    UseDiskDecompressionCacheChanged?.Invoke(value);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public bool ShowErrorMessage
        {
            get => _showErrorMessage;
            set
            {
                if (SetProperty(ref _showErrorMessage, value))
                {
                    ShowErrorMessageChanged?.Invoke(value);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public string SpecifyUnityVersion
        {
            get => _specifyUnityVersion;
            set
            {
                var normalized = value ?? string.Empty;
                if (SetProperty(ref _specifyUnityVersion, normalized))
                {
                    SpecifyUnityVersionChanged?.Invoke(normalized);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public AppThemePreference SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (SetProperty(ref _selectedTheme, value))
                {
                    OnPropertyChanged(nameof(IsThemeDefault));
                    OnPropertyChanged(nameof(IsThemeLight));
                    OnPropertyChanged(nameof(IsThemeDark));
                    ThemeChanged?.Invoke(value);
                    SettingsChanged?.Invoke();
                }
            }
        }

        public bool IsThemeDefault
        {
            get => _selectedTheme == AppThemePreference.Default;
            set
            {
                if (value)
                {
                    SelectedTheme = AppThemePreference.Default;
                }
                else
                {
                    OnPropertyChanged();
                }
            }
        }

        public bool IsThemeLight
        {
            get => _selectedTheme == AppThemePreference.Light;
            set
            {
                if (value)
                {
                    SelectedTheme = AppThemePreference.Light;
                }
                else
                {
                    OnPropertyChanged();
                }
            }
        }

        public bool IsThemeDark
        {
            get => _selectedTheme == AppThemePreference.Dark;
            set
            {
                if (value)
                {
                    SelectedTheme = AppThemePreference.Dark;
                }
                else
                {
                    OnPropertyChanged();
                }
            }
        }

        public void LoadFromSettings(AvaloniaAppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            DisplayAll = settings.DisplayAll;
            EnablePreview = settings.EnablePreview;
            DisplayInfo = settings.DisplayInfo;
            UseDiskDecompressionCache = settings.UseDiskDecompressionCache;
            ShowErrorMessage = settings.ShowErrorMessage;
            SpecifyUnityVersion = settings.SpecifyUnityVersion ?? string.Empty;
            SelectedTheme = ParseThemePreference(settings.SelectedTheme);
        }

        public void ApplyToSettings(AvaloniaAppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            settings.DisplayAll = DisplayAll;
            settings.EnablePreview = EnablePreview;
            settings.DisplayInfo = DisplayInfo;
            settings.UseDiskDecompressionCache = UseDiskDecompressionCache;
            settings.ShowErrorMessage = ShowErrorMessage;
            settings.SpecifyUnityVersion = SpecifyUnityVersion;
            settings.SelectedTheme = FormatThemePreference(SelectedTheme);
        }

        public static AppThemePreference ParseThemePreference(string? themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName))
            {
                return AppThemePreference.Default;
            }

            if (string.Equals(themeName.Trim(), "Light", StringComparison.OrdinalIgnoreCase))
            {
                return AppThemePreference.Light;
            }

            if (string.Equals(themeName.Trim(), "Dark", StringComparison.OrdinalIgnoreCase))
            {
                return AppThemePreference.Dark;
            }

            return AppThemePreference.Default;
        }

        public static string FormatThemePreference(AppThemePreference theme)
        {
            return theme switch
            {
                AppThemePreference.Light => "Light",
                AppThemePreference.Dark => "Dark",
                _ => "Default"
            };
        }
    }
}
