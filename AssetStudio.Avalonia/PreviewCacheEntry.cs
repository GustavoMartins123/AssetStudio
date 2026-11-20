namespace AssetStudio.Avalonia;

public sealed class PreviewCacheEntry
{
    public PreviewCacheEntry(string payloadHash, string payloadPath, long byteSize)
        : this(payloadHash, payloadPath, byteSize, payloadBlob: null, payloadCompression: 0, uncompressedByteSize: byteSize)
    {
    }

    public PreviewCacheEntry(
        string payloadHash,
        string payloadPath,
        long byteSize,
        byte[]? payloadBlob,
        int payloadCompression,
        long uncompressedByteSize)
    {
        PayloadHash = payloadHash ?? string.Empty;
        PayloadPath = payloadPath ?? string.Empty;
        ByteSize = byteSize;
        PayloadBlob = payloadBlob;
        PayloadCompression = payloadCompression;
        UncompressedByteSize = uncompressedByteSize > 0 ? uncompressedByteSize : byteSize;
    }

    public string PayloadHash { get; }
    public string PayloadPath { get; }
    public long ByteSize { get; }
    public byte[]? PayloadBlob { get; }
    public int PayloadCompression { get; }
    public long UncompressedByteSize { get; }
    public bool IsInlineBlob => PayloadBlob != null && PayloadBlob.Length > 0;
}

