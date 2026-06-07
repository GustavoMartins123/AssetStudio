namespace AssetStudio.Avalonia;

internal sealed class LazyConnectionBuildResult
{
    public LazyConnectionBuildResult(SemanticAssetRelations relations, LazyConnectionBuildDiagnostics diagnostics)
    {
        Relations = relations;
        Diagnostics = diagnostics;
    }

    public SemanticAssetRelations Relations { get; }
    public LazyConnectionBuildDiagnostics Diagnostics { get; }
}
