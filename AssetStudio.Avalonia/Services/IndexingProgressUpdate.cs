using System.Collections.Generic;

namespace AssetStudio.Avalonia.Services
{
    public sealed class IndexingProgressUpdate
    {
        public string Status { get; init; } = string.Empty;
        public int TotalFiles { get; init; }
        public int ProcessedFiles { get; init; }
        public int PendingFiles { get; init; }
        public double PercentComplete { get; init; }
        public string CurrentFile { get; init; } = string.Empty;
        public string LastReadFile { get; init; } = string.Empty;
        public IReadOnlyList<string> NewlyReadFiles { get; init; } = System.Array.Empty<string>();
    }
}
