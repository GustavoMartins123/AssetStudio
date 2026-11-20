using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using AssetStudio;
using System;
using System.Globalization;

namespace AssetStudio.Avalonia;

public enum AssetGroupOption
{
    TypeName,
    Container,
    SourceFile,
    None
}

public sealed class ExportOptionsState
{
    public AssetGroupOption AssetGrouping { get; set; } = AssetGroupOption.TypeName;
    public bool ConvertTexture { get; set; } = true;
    public ImageFormat ConvertTextureFormat { get; set; } = ImageFormat.Png;
    public bool ConvertAudio { get; set; } = true;
    public bool RestoreExtensionName { get; set; } = true;
    public bool OpenAfterExport { get; set; } = true;
    public bool ExportSkins { get; set; } = true;
    public bool ExportAnimations { get; set; } = true;
    public bool ExportAnimationsSplit { get; set; } = false;
    public bool ExportBlendShape { get; set; } = true;
    public bool CastToBone { get; set; }
    public bool CombineMeshes { get; set; } = false;
    public decimal BoneSize { get; set; } = 10m;
    public decimal ScaleFactor { get; set; } = 1m;
    public int FbxVersion { get; set; } = 3;
    public int FbxFormat { get; set; } = 1;
    public bool ExportUnityMeta { get; set; } = true;
    public bool ExportUnityAnimClips { get; set; } = true;

    public ExportOptionsState Clone() => (ExportOptionsState)MemberwiseClone();

    public void CopyFrom(ExportOptionsState other)
    {
        AssetGrouping = other.AssetGrouping;
        ConvertTexture = other.ConvertTexture;
        ConvertTextureFormat = other.ConvertTextureFormat;
        ConvertAudio = other.ConvertAudio;
        RestoreExtensionName = other.RestoreExtensionName;
        OpenAfterExport = other.OpenAfterExport;
        ExportSkins = other.ExportSkins;
        ExportAnimations = other.ExportAnimations;
        ExportAnimationsSplit = other.ExportAnimationsSplit;
        ExportBlendShape = other.ExportBlendShape;
        CastToBone = other.CastToBone;
        CombineMeshes = other.CombineMeshes;
        BoneSize = other.BoneSize;
        ScaleFactor = other.ScaleFactor;
        FbxVersion = other.FbxVersion;
        FbxFormat = other.FbxFormat;
        ExportUnityMeta = other.ExportUnityMeta;
        ExportUnityAnimClips = other.ExportUnityAnimClips;
    }
}

public sealed class ExportOptionsWindow : Window
{
    private readonly ExportOptionsState state;
    private readonly ComboBox assetGrouping = new();
    private readonly CheckBox convertTexture = new() { Content = "Convert texture" };
    private readonly ComboBox convertTextureFormat = new();
    private readonly CheckBox convertAudio = new() { Content = "Convert AudioClip to WAV(PCM)" };
    private readonly CheckBox restoreExtensionName = new() { Content = "Restore extension name" };
    private readonly CheckBox openAfterExport = new() { Content = "Open after export" };
    private readonly CheckBox exportSkins = new() { Content = "Export skins" };
    private readonly CheckBox exportAnimations = new() { Content = "Export animations" };
    private readonly CheckBox exportAnimationsSplit = new() { Content = "Export animations as separate FBX files" };
    private readonly CheckBox exportBlendShape = new() { Content = "Export blend shapes" };
    private readonly CheckBox castToBone = new() { Content = "Cast to bone" };
    private readonly CheckBox combineMeshes = new() { Content = "Combine meshes into single mesh" };
    private readonly TextBox boneSize = new() { Width = 90 };
    private readonly TextBox scaleFactor = new() { Width = 90 };
    private readonly ComboBox fbxVersion = new();
    private readonly ComboBox fbxFormat = new();
    private readonly CheckBox exportUnityMeta = new() { Content = "Export Unity humanoid .meta" };
    private readonly CheckBox exportUnityAnimClips = new() { Content = "Export Unity native .anim clips" };

    public ExportOptionsWindow(ExportOptionsState state)
    {
        this.state = state;
        Title = "Export options";
        Width = 420;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        assetGrouping.ItemsSource = new[] { "Type name", "Container path", "Source file", "Do not group" };
        convertTextureFormat.ItemsSource = Enum.GetNames<ImageFormat>();
        fbxVersion.ItemsSource = new[] { "FBX 6.1", "FBX 7.1", "FBX 7.2", "FBX 7.3", "FBX 7.4", "FBX 7.5" };
        fbxFormat.ItemsSource = new[] { "Binary", "ASCII" };

        LoadState();

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Asset grouping" });
        panel.Children.Add(assetGrouping);
        panel.Children.Add(convertTexture);
        panel.Children.Add(Labeled("Texture format", convertTextureFormat));
        panel.Children.Add(convertAudio);
        panel.Children.Add(restoreExtensionName);
        panel.Children.Add(openAfterExport);
        panel.Children.Add(Separator());
        panel.Children.Add(new TextBlock { Text = "FBX" });
        panel.Children.Add(exportSkins);
        panel.Children.Add(exportAnimations);
        panel.Children.Add(exportAnimationsSplit);
        panel.Children.Add(exportBlendShape);
        panel.Children.Add(castToBone);
        panel.Children.Add(combineMeshes);
        panel.Children.Add(Labeled("Bone size", boneSize));
        panel.Children.Add(Labeled("Scale factor", scaleFactor));
        panel.Children.Add(Labeled("FBX version", fbxVersion));
        panel.Children.Add(Labeled("FBX format", fbxFormat));
        panel.Children.Add(Separator());
        panel.Children.Add(new TextBlock { Text = "Unity Integration" });
        panel.Children.Add(exportUnityMeta);
        panel.Children.Add(exportUnityAnimClips);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var ok = new Button { Content = "OK", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        ok.Click += Ok_Click;
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel };
    }

    private void LoadState()
    {
        assetGrouping.SelectedIndex = Math.Clamp((int)state.AssetGrouping, 0, 3);
        convertTexture.IsChecked = state.ConvertTexture;
        convertTextureFormat.SelectedItem = state.ConvertTextureFormat.ToString();
        convertAudio.IsChecked = state.ConvertAudio;
        restoreExtensionName.IsChecked = state.RestoreExtensionName;
        openAfterExport.IsChecked = state.OpenAfterExport;
        exportSkins.IsChecked = state.ExportSkins;
        exportAnimations.IsChecked = state.ExportAnimations;
        exportAnimationsSplit.IsChecked = state.ExportAnimationsSplit;
        exportBlendShape.IsChecked = state.ExportBlendShape;
        castToBone.IsChecked = state.CastToBone;
        combineMeshes.IsChecked = state.CombineMeshes;
        boneSize.Text = state.BoneSize.ToString(CultureInfo.InvariantCulture);
        scaleFactor.Text = state.ScaleFactor.ToString(CultureInfo.InvariantCulture);
        fbxVersion.SelectedIndex = state.FbxVersion;
        fbxFormat.SelectedIndex = state.FbxFormat;
        exportUnityMeta.IsChecked = state.ExportUnityMeta;
        exportUnityAnimClips.IsChecked = state.ExportUnityAnimClips;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        state.AssetGrouping = (AssetGroupOption)Math.Clamp(assetGrouping.SelectedIndex, 0, 3);
        state.ConvertTexture = convertTexture.IsChecked == true;
        state.ConvertTextureFormat = Enum.Parse<ImageFormat>((string?)convertTextureFormat.SelectedItem ?? ImageFormat.Png.ToString());
        state.ConvertAudio = convertAudio.IsChecked == true;
        state.RestoreExtensionName = restoreExtensionName.IsChecked == true;
        state.OpenAfterExport = openAfterExport.IsChecked == true;
        state.ExportSkins = exportSkins.IsChecked == true;
        state.ExportAnimations = exportAnimations.IsChecked == true;
        state.ExportAnimationsSplit = exportAnimationsSplit.IsChecked == true;
        state.ExportBlendShape = exportBlendShape.IsChecked == true;
        state.CastToBone = castToBone.IsChecked == true;
        state.CombineMeshes = combineMeshes.IsChecked == true;
        state.BoneSize = ParseDecimal(boneSize.Text, state.BoneSize);
        state.ScaleFactor = ParseDecimal(scaleFactor.Text, state.ScaleFactor);
        state.FbxVersion = Math.Max(0, fbxVersion.SelectedIndex);
        state.FbxFormat = Math.Max(0, fbxFormat.SelectedIndex);
        state.ExportUnityMeta = exportUnityMeta.IsChecked == true;
        state.ExportUnityAnimClips = exportUnityAnimClips.IsChecked == true;
        Close(state);
    }

    private static decimal ParseDecimal(string? text, decimal fallback)
    {
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static Control Labeled(string label, Control control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(control);
        return panel;
    }

    private static Control Separator()
    {
        return new Border { Height = 1, Margin = new Thickness(0, 4), Background = global::Avalonia.Media.Brushes.Gray };
    }
}
