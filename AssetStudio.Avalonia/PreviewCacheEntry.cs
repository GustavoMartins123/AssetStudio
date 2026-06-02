namespace AssetStudio.Avalonia;

internal sealed class PreviewCacheEntry
{
    public PreviewCacheEntry(string payloadHash, string payloadPath, long byteSize)
    {
        PayloadHash = payloadHash;
        PayloadPath = payloadPath;
        ByteSize = byteSize;
    }

    public string PayloadHash { get; }
    public string PayloadPath { get; }
    public long ByteSize { get; }
}
