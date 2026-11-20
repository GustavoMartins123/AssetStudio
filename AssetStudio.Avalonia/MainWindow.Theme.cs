using Avalonia;
using Avalonia.Styling;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private void InitializeTheme()
        {
            ApplyTheme(ViewModel.Options.SelectedTheme);
        }

        private void OnThemeOptionChanged(AppThemePreference theme)
        {
            appSettings.SelectedTheme = WindowOptionsViewModel.FormatThemePreference(theme);
            SaveAppSettings();
            ApplyTheme(theme);

            if (currentTextAssetPreview != null && PreviewSession.HasDialoguePreview)
            {
                BuildTextAssetDialogueCards(currentTextAssetPreview);
                SetTextAssetPreviewMode(currentPreviewMode);
            }
        }

        private void ApplyTheme(AppThemePreference theme)
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = theme switch
                {
                    AppThemePreference.Dark => ThemeVariant.Dark,
                    AppThemePreference.Light => ThemeVariant.Light,
                    _ => ThemeVariant.Default
                };
            }
        }
    }
}
