using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis;

public enum ProjectAnalysisStatus
{
    Pending = 0,
    Running = 1,
    Ready = 2,
    Failed = 3
}

public enum UnityScriptingBackend
{
    Unknown = 0,
    Mono = 1,
    Il2Cpp = 2
}

public sealed class ProjectAnalysisOptions
{
    public bool ForceRefresh { get; set; }
    public bool AllowExternalIl2CppDumper { get; set; }
    public string? Il2CppDumperExecutablePath { get; set; }
    public string? CustomIl2CppArtifactsDirectory { get; set; }
    public TimeSpan ExternalToolTimeout { get; set; } = TimeSpan.FromMinutes(3);
}

public sealed class ProjectAnalysisReport
{
    public string ProjectRoot { get; set; } = string.Empty;
    public string ProjectHash { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public ProjectAnalysisStatus Status { get; set; } = ProjectAnalysisStatus.Pending;
    public DateTime? AnalyzedAtUtc { get; set; }
    public string UnityVersion { get; set; } = string.Empty;
    public UnityScriptingBackend ScriptingBackend { get; set; } = UnityScriptingBackend.Unknown;
    public bool HasGameAssembly { get; set; }
    public string GameAssemblyPath { get; set; } = string.Empty;
    public bool HasGlobalMetadata { get; set; }
    public string GlobalMetadataPath { get; set; } = string.Empty;
    public int? Il2CppMetadataVersion { get; set; }
    public bool Il2CppMetadataObfuscated { get; set; }
    public bool HasManagedAssemblies { get; set; }
    public int ManagedAssemblyCount { get; set; }
    public bool HasBackupManagedAssemblies { get; set; }
    public string BackupManagedDirectory { get; set; } = string.Empty;
    public bool HasAddressables { get; set; }
    public List<string> AddressablesCatalogPaths { get; set; } = new();
    public bool Il2CppDumperArtifactsDetected { get; set; }
    public bool ExternalIl2CppDumperExecuted { get; set; }
    public List<string> Il2CppDumperArtifactPaths { get; set; } = new();
    public int Il2CppScriptCount { get; set; }
    public int AddressablesBundleCount { get; set; }
    public int DecryptionHintCount { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();

    public string BuildRuntimeBadge()
    {
        var parts = new List<string>();
        switch (ScriptingBackend)
        {
            case UnityScriptingBackend.Il2Cpp:
                var il2CppLabel = Il2CppMetadataVersion.HasValue
                    ? $"IL2CPP v{Il2CppMetadataVersion.Value}"
                    : (Il2CppMetadataObfuscated ? "IL2CPP (obfuscated metadata)" : "IL2CPP");
                if (HasBackupManagedAssemblies)
                {
                    il2CppLabel = ManagedAssemblyCount > 0
                        ? $"{il2CppLabel} + Backup Managed DLLs ({ManagedAssemblyCount})"
                        : $"{il2CppLabel} + Backup Managed DLLs";
                }
                parts.Add(il2CppLabel);
                break;
            case UnityScriptingBackend.Mono:
                parts.Add(ManagedAssemblyCount > 0
                    ? $"Mono ({ManagedAssemblyCount} DLLs)"
                    : "Mono");
                break;
        }

        if (!string.IsNullOrWhiteSpace(UnityVersion))
        {
            parts.Add($"Unity {UnityVersion}");
        }

        if (HasAddressables)
        {
            parts.Add("Addressables");
        }

        return parts.Count > 0 ? string.Join(" \u2022 ", parts) : "Runtime not detected";
    }

    public string BuildConciseSummary()
    {
        if (Status == ProjectAnalysisStatus.Failed)
        {
            return string.IsNullOrWhiteSpace(ErrorMessage)
                ? "Analysis failed."
                : $"Analysis failed: {ErrorMessage}";
        }

        if (Status == ProjectAnalysisStatus.Pending)
        {
            return "Analysis pending.";
        }

        if (Status == ProjectAnalysisStatus.Running)
        {
            return "Analyzing project runtime and catalogs...";
        }

        var parts = new List<string> { BuildRuntimeBadge() };
        if (Il2CppScriptCount > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Il2CppScriptCount:N0} IL2CPP symbols"));
        }

        if (AddressablesBundleCount > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{AddressablesBundleCount:N0} bundles"));
        }

        if (DecryptionHintCount > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{DecryptionHintCount:N0} decryption hint{(DecryptionHintCount == 1 ? string.Empty : "s")}"));
        }

        return string.Join(" | ", parts);
    }

    public ProjectAnalysisReport Clone()
    {
        return new ProjectAnalysisReport
        {
            ProjectRoot = ProjectRoot,
            ProjectHash = ProjectHash,
            Signature = Signature,
            Status = Status,
            AnalyzedAtUtc = AnalyzedAtUtc,
            UnityVersion = UnityVersion,
            ScriptingBackend = ScriptingBackend,
            HasGameAssembly = HasGameAssembly,
            GameAssemblyPath = GameAssemblyPath,
            HasGlobalMetadata = HasGlobalMetadata,
            GlobalMetadataPath = GlobalMetadataPath,
            Il2CppMetadataVersion = Il2CppMetadataVersion,
            Il2CppMetadataObfuscated = Il2CppMetadataObfuscated,
            HasManagedAssemblies = HasManagedAssemblies,
            ManagedAssemblyCount = ManagedAssemblyCount,
            HasBackupManagedAssemblies = HasBackupManagedAssemblies,
            BackupManagedDirectory = BackupManagedDirectory,
            HasAddressables = HasAddressables,
            AddressablesCatalogPaths = AddressablesCatalogPaths.ToList(),
            Il2CppDumperArtifactsDetected = Il2CppDumperArtifactsDetected,
            ExternalIl2CppDumperExecuted = ExternalIl2CppDumperExecuted,
            Il2CppDumperArtifactPaths = Il2CppDumperArtifactPaths.ToList(),
            Il2CppScriptCount = Il2CppScriptCount,
            AddressablesBundleCount = AddressablesBundleCount,
            DecryptionHintCount = DecryptionHintCount,
            ErrorMessage = ErrorMessage,
            Warnings = Warnings.ToList()
        };
    }
}

public sealed class ProjectAnalysisSummary
{
    public ProjectAnalysisStatus Status { get; set; } = ProjectAnalysisStatus.Pending;
    public string UnityVersion { get; set; } = string.Empty;
    public UnityScriptingBackend ScriptingBackend { get; set; } = UnityScriptingBackend.Unknown;
    public int? Il2CppMetadataVersion { get; set; }
    public bool HasAddressables { get; set; }
    public bool Il2CppDumperArtifactsDetected { get; set; }
    public int ManagedAssemblyCount { get; set; }
    public int Il2CppScriptCount { get; set; }
    public int AddressablesBundleCount { get; set; }
    public int DecryptionHintCount { get; set; }
    public DateTime? AnalyzedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public string RuntimeBadge { get; set; } = string.Empty;
    public string SummaryText { get; set; } = string.Empty;

    public static ProjectAnalysisSummary FromReport(ProjectAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ProjectAnalysisSummary
        {
            Status = report.Status,
            UnityVersion = report.UnityVersion,
            ScriptingBackend = report.ScriptingBackend,
            Il2CppMetadataVersion = report.Il2CppMetadataVersion,
            HasAddressables = report.HasAddressables,
            Il2CppDumperArtifactsDetected = report.Il2CppDumperArtifactsDetected,
            ManagedAssemblyCount = report.ManagedAssemblyCount,
            Il2CppScriptCount = report.Il2CppScriptCount,
            AddressablesBundleCount = report.AddressablesBundleCount,
            DecryptionHintCount = report.DecryptionHintCount,
            AnalyzedAtUtc = report.AnalyzedAtUtc,
            ErrorMessage = report.ErrorMessage,
            RuntimeBadge = report.BuildRuntimeBadge(),
            SummaryText = report.BuildConciseSummary()
        };
    }

    public ProjectAnalysisSummary Clone()
    {
        return new ProjectAnalysisSummary
        {
            Status = Status,
            UnityVersion = UnityVersion,
            ScriptingBackend = ScriptingBackend,
            Il2CppMetadataVersion = Il2CppMetadataVersion,
            HasAddressables = HasAddressables,
            Il2CppDumperArtifactsDetected = Il2CppDumperArtifactsDetected,
            ManagedAssemblyCount = ManagedAssemblyCount,
            Il2CppScriptCount = Il2CppScriptCount,
            AddressablesBundleCount = AddressablesBundleCount,
            DecryptionHintCount = DecryptionHintCount,
            AnalyzedAtUtc = AnalyzedAtUtc,
            ErrorMessage = ErrorMessage,
            RuntimeBadge = RuntimeBadge,
            SummaryText = SummaryText
        };
    }
}

public sealed class Il2CppScriptIndex
{
    public string ProjectRoot { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> SourceArtifacts { get; set; } = new();
    public int ScriptMethodCount { get; set; }
    public int ScriptMetadataCount { get; set; }
    public int ScriptMetadataMethodCount { get; set; }
    public int ScriptStringCount { get; set; }
    public int AddressCount { get; set; }
    public int DumpTypeCount { get; set; }
    public int MonoBehaviourCount { get; set; }
    public int ScriptableObjectCount { get; set; }
    public int HeaderStructCount { get; set; }
    public int HeaderTypedefCount { get; set; }
    public bool EntriesTruncated { get; set; }
    public List<Il2CppTypeEntry> Types { get; set; } = new();
    public List<Il2CppScriptSymbolEntry> SampleScriptSymbols { get; set; } = new();

    public int ComputeTotalScriptCount()
    {
        var total = ScriptMethodCount + ScriptMetadataCount + ScriptMetadataMethodCount + DumpTypeCount;
        if (total == 0 && HeaderStructCount > 0)
        {
            return HeaderStructCount;
        }

        return total;
    }
}

public sealed class Il2CppTypeEntry
{
    public string Namespace { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseType { get; set; } = string.Empty;
    public int? TypeDefIndex { get; set; }
    public int FieldCount { get; set; }
    public bool IsMonoBehaviour { get; set; }
    public bool IsScriptableObject { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class Il2CppScriptSymbolEntry
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ulong Address { get; set; }
}

public sealed class AddressablesIndex
{
    public string ProjectRoot { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<AddressablesCatalogSummary> Catalogs { get; set; } = new();
    public int TotalBundleCount { get; set; }
    public int LocalBundleCount { get; set; }
    public int RemoteBundleCount { get; set; }
    public List<string> Providers { get; set; } = new();
    public List<AddressablesBundleEntry> Bundles { get; set; } = new();
}

public sealed class AddressablesCatalogSummary
{
    public string CatalogPath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string LocatorId { get; set; } = string.Empty;
    public int ProviderCount { get; set; }
    public int InternalIdCount { get; set; }
    public int EntryCount { get; set; }
    public int BundleCount { get; set; }
    public bool HasKeyData { get; set; }
    public bool HasBucketData { get; set; }
    public bool HasEntryData { get; set; }
    public bool HasExtraData { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class AddressablesBundleEntry
{
    public string CatalogPath { get; set; } = string.Empty;
    public string InternalId { get; set; } = string.Empty;
    public string BundleFileName { get; set; } = string.Empty;
    public string BundleName { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Crc { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public bool IsRemote { get; set; }
    public bool UsesNonStandardProvider { get; set; }
}

public sealed class DecryptionHintsReport
{
    public string ProjectRoot { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DecryptionHintEntry> Hints { get; set; } = new();
}

public sealed class DecryptionHintEntry
{
    public string Provider { get; set; } = string.Empty;
    public string CatalogPath { get; set; } = string.Empty;
    public string Bundle { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string MetadataSummary { get; set; } = string.Empty;
}

public sealed class ProjectAnalysisResult
{
    public ProjectAnalysisReport Report { get; init; } = new();
    public Il2CppScriptIndex ScriptIndex { get; init; } = new();
    public AddressablesIndex AddressablesIndex { get; init; } = new();
    public DecryptionHintsReport DecryptionHints { get; init; } = new();
    public bool FromCache { get; init; }
}

public sealed class ProjectRuntimeDetectionResult
{
    public string ProjectRoot { get; set; } = string.Empty;
    public string ProjectHash { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public UnityScriptingBackend ScriptingBackend { get; set; } = UnityScriptingBackend.Unknown;
    public bool HasGameAssembly { get; set; }
    public string GameAssemblyPath { get; set; } = string.Empty;
    public bool HasGlobalMetadata { get; set; }
    public string GlobalMetadataPath { get; set; } = string.Empty;
    public int? Il2CppMetadataVersion { get; set; }
    public bool Il2CppMetadataObfuscated { get; set; }
    public string? Il2CppMetadataAnomalyReason { get; set; }
    public bool HasManagedAssemblies { get; set; }
    public int ManagedAssemblyCount { get; set; }
    public bool HasBackupManagedAssemblies { get; set; }
    public string BackupManagedDirectory { get; set; } = string.Empty;
    public bool HasAddressables { get; set; }
    public List<string> AddressablesCatalogPaths { get; set; } = new();
    public List<string> Il2CppDumperArtifactPaths { get; set; } = new();
    public List<StreamEncryptionBarrier> EncryptionBarriers { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
