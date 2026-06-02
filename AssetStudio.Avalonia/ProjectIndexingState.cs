using System;
using System.Collections.Generic;

namespace AssetStudio.Avalonia;

internal sealed class ProjectIndexingState
{
    public string Status { get; init; } = string.Empty;
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int PendingFiles { get; init; }
    public double PercentComplete { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
    public string LastReadFile { get; init; } = string.Empty;
    public DateTime? StartedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public List<string> ReadFiles { get; } = new();
}
