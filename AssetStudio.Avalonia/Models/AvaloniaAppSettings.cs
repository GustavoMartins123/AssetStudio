using System;
using System.IO;
using System.Text.Json;

namespace AssetStudio.Avalonia;

public sealed class AvaloniaAppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AssetStudio",
        "avalonia-settings.json");

    public string LoadFolderPath { get; set; } = string.Empty;
    public string ExportFolderPath { get; set; } = string.Empty;
    public string ProjectRoot { get; set; } = string.Empty;
    public string SpecifyUnityVersion { get; set; } = string.Empty;
    public bool ShowErrorMessage { get; set; } = false;
    public bool DisplayAll { get; set; } = false;
    public bool DisplayInfo { get; set; } = true;
    public bool EnablePreview { get; set; } = true;
    public double AvatarPreviewBoneScale { get; set; } = 1.0;
    public double AvatarPreviewMeshDensityPercent { get; set; } = 15.0;
    public string ModelPreviewViewPreset { get; set; } = nameof(MeshPreviewViewPreset.Auto);
    public ExportOptionsState ExportOptions { get; set; } = new();
    public string SelectedTheme { get; set; } = "Default";

    public static AvaloniaAppSettings Load()
    {
        return ProjectManagerStore.Shared.LoadGlobalSettings();
    }

    internal static AvaloniaAppSettings LoadLegacyJson()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AvaloniaAppSettings>(File.ReadAllText(SettingsPath)) ?? new AvaloniaAppSettings();
            }
        }
        catch
        {
        }

        return new AvaloniaAppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
