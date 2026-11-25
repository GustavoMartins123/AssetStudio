using System;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Controls;

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
    public int WebpQuality { get; set; } = 90;
    public bool WebpLossless { get; set; }
    public bool ConvertAudio { get; set; } = true;
    public bool RestoreExtensionName { get; set; } = true;
    public bool OpenAfterExport { get; set; } = true;
    public bool ExportSkins { get; set; } = true;
    public bool ExportAnimations { get; set; } = true;
    public bool ExportAnimationsSplit { get; set; } = false;

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool SplitAnimations
    {
        get => ExportAnimationsSplit;
        set => ExportAnimationsSplit = value;
    }

    public bool ExportBlendShape { get; set; } = true;
    public bool CastToBone { get; set; }
    public bool CombineMeshes { get; set; } = false;
    public decimal BoneSize { get; set; } = 10m;
    public decimal ScaleFactor { get; set; } = 1m;
    public ModelExportFormat ModelFormat { get; set; } = ModelExportFormat.Glb;
    public int FbxVersion { get; set; } = 3;
    public int FbxFormat { get; set; } = 0;
    public bool ExportUnityMeta { get; set; } = true;
    public bool ExportUnityAnimClips { get; set; } = true;

    public ExportOptionsState Clone() => (ExportOptionsState)MemberwiseClone();

    public void CopyFrom(ExportOptionsState other)
    {
        AssetGrouping = other.AssetGrouping;
        ConvertTexture = other.ConvertTexture;
        ConvertTextureFormat = other.ConvertTextureFormat;
        WebpQuality = other.WebpQuality;
        WebpLossless = other.WebpLossless;
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
        ModelFormat = other.ModelFormat;
        FbxVersion = other.FbxVersion;
        FbxFormat = other.FbxFormat;
        ExportUnityMeta = other.ExportUnityMeta;
        ExportUnityAnimClips = other.ExportUnityAnimClips;
    }
}

public sealed partial class ExportOptionsWindow : Window
{
    public ExportOptionsWindow()
        : this(new ExportOptionsState())
    {
    }

    public ExportOptionsWindow(ExportOptionsState state)
        : this(new ExportOptionsViewModel(state))
    {
    }

    public ExportOptionsWindow(ExportOptionsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public ExportOptionsViewModel ViewModel { get; }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }

    private void OnRequestClose(bool saved)
    {
        Close(saved ? ViewModel.Result ?? ViewModel.TargetState : null);
    }
}
