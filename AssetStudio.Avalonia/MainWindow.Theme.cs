using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using System;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow : Window
    {
        private void InitializeTheme()
        {
            ApplyTheme(appSettings.SelectedTheme);

            if (themeDefault != null) themeDefault.IsChecked = appSettings.SelectedTheme == "Default";
            if (themeLight != null) themeLight.IsChecked = appSettings.SelectedTheme == "Light";
            if (themeDark != null) themeDark.IsChecked = appSettings.SelectedTheme == "Dark";
        }

        private void ThemeDefault_Click(object? sender, RoutedEventArgs e)
        {
            SetThemeOption("Default");
        }

        private void ThemeLight_Click(object? sender, RoutedEventArgs e)
        {
            SetThemeOption("Light");
        }

        private void ThemeDark_Click(object? sender, RoutedEventArgs e)
        {
            SetThemeOption("Dark");
        }

        private void SetThemeOption(string themeName)
        {
            appSettings.SelectedTheme = themeName;
            SaveAppSettings();

            if (themeDefault != null) themeDefault.IsChecked = themeName == "Default";
            if (themeLight != null) themeLight.IsChecked = themeName == "Light";
            if (themeDark != null) themeDark.IsChecked = themeName == "Dark";

            ApplyTheme(themeName);

            if (currentTextAssetPreview != null && TextAssetPreviewPanel != null && TextAssetPreviewPanel.IsVisible)
            {
                BuildTextAssetDialogueCards(currentTextAssetPreview);
                SetTextAssetPreviewMode(currentPreviewMode);
            }
        }

        private void ApplyTheme(string themeName)
        {
            if (Application.Current is { } app)
            {
                if (themeName == "Dark")
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                else if (themeName == "Light")
                    app.RequestedThemeVariant = ThemeVariant.Light;
                else
                    app.RequestedThemeVariant = ThemeVariant.Default;
            }
        }
    }
}
