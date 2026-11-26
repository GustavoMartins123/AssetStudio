using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.Services.Scripting;

public enum Il2CppMetadataProtectionKind
{
    NoneOrMissing = 0,
    StandardClassic = 1,
    CompactUnity6 = 2,
    SimpleXorRecoverable = 3,
    HeaderShiftedOrCustomLayout = 4,
    HighEntropyEncrypted = 5,
    InvalidOrTruncated = 6
}

public enum Il2CppSymbolObfuscationKind
{
    Unknown = 0,
    Clean = 1,
    PartiallyObfuscated = 2,
    HeavilyObfuscated = 3
}

public sealed record NativeBinaryStringOffset(
    string Pattern,
    long FileOffset,
    string SectionName);

public sealed record NativeBinarySectionSummary(
    string Name,
    long RawOffset,
    long RawSize,
    double Entropy,
    bool IsHighEntropy);

public sealed class NativeBinaryProbeResult
{
    public string BinaryPath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public bool IsPeBinary { get; init; }
    public bool IsElfBinary { get; init; }
    public bool HasHighEntropySection { get; init; }
    public IReadOnlyList<NativeBinarySectionSummary> Sections { get; init; } = Array.Empty<NativeBinarySectionSummary>();
    public IReadOnlyList<NativeBinaryStringOffset> MetadataStringOffsets { get; init; } = Array.Empty<NativeBinaryStringOffset>();
    public IReadOnlyList<string> Indicators { get; init; } = Array.Empty<string>();
}

public sealed class Il2CppMetadataAuditRequest
{
    public string? MetadataPath { get; init; }
    public string? GameAssemblyPath { get; init; }
    public string? ProjectRoot { get; init; }
    public bool? HasBackupManagedDlls { get; init; }
    public int LoadedMonoScriptCount { get; init; }
    public int LoadedTypeTreeCount { get; init; }
}

public sealed class Il2CppMetadataAuditReport
{
    public string MetadataPath { get; init; } = string.Empty;
    public string GameAssemblyPath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public string RawMagicHex { get; init; } = string.Empty;
    public int RawVersion { get; init; }
    public Il2CppMetadataProtectionKind ProtectionKind { get; init; }
    public Il2CppSymbolObfuscationKind SymbolObfuscationKind { get; init; }
    public double HeaderEntropy { get; init; }
    public double BodyEntropy { get; init; }
    public double HeaderZeroByteRatio { get; init; }
    public string RecoveredXorKeyHex { get; init; } = string.Empty;
    public byte[]? RecoveredXorKeyBytes { get; init; }
    public bool RecoveredXorAppliesToWholeFile { get; init; }
    public int DetectedMetadataVersion { get; init; }
    public int RecoveredMetadataVersion { get; init; }
    public int AssemblyCount { get; init; }
    public int TypeCount { get; init; }
    public int GameAssemblyCount { get; init; }
    public int GameTypeCount { get; init; }
    public int ObfuscatedGameTypeCount { get; init; }
    public double ObfuscatedTypeRatio { get; init; }
    public IReadOnlyList<string> SampleObfuscatedNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BinaryIndicators { get; init; } = Array.Empty<string>();
    public IReadOnlyList<NativeBinaryStringOffset> MetadataStringOffsetsInBinary { get; init; } = Array.Empty<NativeBinaryStringOffset>();
    public IReadOnlyList<NativeBinarySectionSummary> BinarySections { get; init; } = Array.Empty<NativeBinarySectionSummary>();
    public bool HasHighEntropyBinarySection { get; init; }
    public bool HasBackupManagedDlls { get; init; }
    public int LoadedMonoScriptCount { get; init; }
    public int LoadedTypeTreeCount { get; init; }
    public string DiagnosticDetail { get; init; } = string.Empty;
    public IReadOnlyList<string> RecommendedNextSteps { get; init; } = Array.Empty<string>();
    public long ElapsedMs { get; init; }

    public string ShortStatusBadge => ProtectionKind switch
    {
        Il2CppMetadataProtectionKind.NoneOrMissing => "No IL2CPP Metadata",
        Il2CppMetadataProtectionKind.StandardClassic => string.Create(CultureInfo.InvariantCulture, $"IL2CPP v{DetectedMetadataVersion} ({SymbolObfuscationKind})"),
        Il2CppMetadataProtectionKind.CompactUnity6 => string.Create(CultureInfo.InvariantCulture, $"IL2CPP v{DetectedMetadataVersion} Compact ({SymbolObfuscationKind})"),
        Il2CppMetadataProtectionKind.SimpleXorRecoverable => string.Create(CultureInfo.InvariantCulture, $"IL2CPP v{DetectedMetadataVersion} XOR-Recoverable"),
        Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout => "IL2CPP Custom Layout",
        Il2CppMetadataProtectionKind.HighEntropyEncrypted => "IL2CPP Encrypted",
        Il2CppMetadataProtectionKind.InvalidOrTruncated => "IL2CPP Invalid/Truncated",
        _ => "IL2CPP Audit"
    };

    public string FormatSummaryReport()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("// =========================================================================");
        sb.AppendLine("// IL2CPP Static Metadata & Obfuscation Audit Report");
        sb.AppendLine("// Read-only static inspection (zero code execution, zero memory probing)");
        sb.AppendLine("// =========================================================================");
        sb.AppendLine();

        sb.AppendLine("[Target]");
        sb.AppendLine($"  Metadata File      : {(string.IsNullOrWhiteSpace(MetadataPath) ? "(not found)" : MetadataPath)}");
        if (FileSizeBytes > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  File Size          : {FileSizeBytes:N0} bytes ({FileSizeBytes / (1024.0 * 1024.0):F2} MB)"));
        }
        sb.AppendLine($"  Native Binary      : {(string.IsNullOrWhiteSpace(GameAssemblyPath) ? "(not found)" : GameAssemblyPath)}");
        sb.AppendLine($"  Backup Managed DLLs: {(HasBackupManagedDlls ? "Detected (full C# decompilation available)" : "Not detected")}");
        if (LoadedMonoScriptCount > 0 || LoadedTypeTreeCount > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Loaded Assets Info : {LoadedMonoScriptCount:N0} MonoScript(s), {LoadedTypeTreeCount:N0} TypeTree schema(s)"));
        }
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Audit Time         : {ElapsedMs:N0} ms"));
        sb.AppendLine();

        sb.AppendLine("[Metadata Protection Classification]");
        sb.AppendLine($"  Classification     : {ProtectionKind}");
        if (!string.IsNullOrWhiteSpace(RawMagicHex))
        {
            sb.AppendLine($"  Raw Magic          : {RawMagicHex} (expected 0xFAB11BAF)");
        }
        if (ProtectionKind != Il2CppMetadataProtectionKind.NoneOrMissing && FileSizeBytes >= 8)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Raw Version Field  : {RawVersion}"));
        }
        if (DetectedMetadataVersion > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Detected Version   : v{DetectedMetadataVersion}"));
        }
        if (!string.IsNullOrWhiteSpace(RecoveredXorKeyHex))
        {
            var scope = RecoveredXorAppliesToWholeFile ? "whole file" : "body tables";
            sb.AppendLine($"  Recovered XOR Key  : {RecoveredXorKeyHex} ({scope})");
        }
        if (ProtectionKind != Il2CppMetadataProtectionKind.NoneOrMissing && FileSizeBytes > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Header Entropy     : {HeaderEntropy:F3} bits/byte (first 4 KB)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Body Entropy       : {BodyEntropy:F3} bits/byte (sampled 64 KB)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Header 0x00 Ratio  : {HeaderZeroByteRatio * 100.0:F2}%"));
        }
        if (!string.IsNullOrWhiteSpace(DiagnosticDetail))
        {
            sb.AppendLine($"  Diagnostic Detail  : {DiagnosticDetail}");
        }
        sb.AppendLine();

        sb.AppendLine("[Symbol & Assembly Analysis]");
        sb.AppendLine($"  Symbol Obfuscation : {SymbolObfuscationKind}");
        if (AssemblyCount > 0 || TypeCount > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Total Assemblies   : {AssemblyCount:N0} ({GameAssemblyCount:N0} game assembly/assemblies)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Total Types        : {TypeCount:N0} ({GameTypeCount:N0} top-level game types)"));
            if (GameTypeCount > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Obfuscated Ratio   : {ObfuscatedTypeRatio * 100.0:F1}% ({ObfuscatedGameTypeCount:N0} / {GameTypeCount:N0} game types)"));
            }
            if (SampleObfuscatedNames.Count > 0)
            {
                sb.AppendLine($"  Sample Obfuscated  : {string.Join(", ", SampleObfuscatedNames)}");
            }
        }
        else
        {
            sb.AppendLine("  Symbols            : Unavailable (metadata missing, encrypted, or unreadable)");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(GameAssemblyPath)
            || BinarySections.Count > 0
            || MetadataStringOffsetsInBinary.Count > 0
            || BinaryIndicators.Count > 0)
        {
            sb.AppendLine("[Native Binary Static Probe]");
            if (BinarySections.Count > 0)
            {
                var secList = BinarySections.Select(static s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{s.Name} (H={s.Entropy:F2}{(s.IsHighEntropy ? "!" : string.Empty)})"));
                sb.AppendLine($"  PE Sections        : {string.Join(", ", secList)}");
                sb.AppendLine($"  High-Entropy Code  : {(HasHighEntropyBinarySection ? "Yes (>= 7.5 bits/byte)" : "No")}");
            }
            if (MetadataStringOffsetsInBinary.Count > 0)
            {
                sb.AppendLine("  global-metadata.dat References:");
                foreach (var hit in MetadataStringOffsetsInBinary)
                {
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    - Offset 0x{hit.FileOffset:X8} [{hit.SectionName}] ({hit.Pattern})"));
                }
            }
            if (BinaryIndicators.Count > 0)
            {
                sb.AppendLine("  Detected Indicators:");
                foreach (var indicator in BinaryIndicators)
                {
                    sb.AppendLine($"    - {indicator}");
                }
            }
            else
            {
                sb.AppendLine("  Detected Indicators: None");
            }
            sb.AppendLine();
        }

        sb.AppendLine("[Recommended Next Steps]");
        if (RecommendedNextSteps.Count == 0)
        {
            sb.AppendLine("  1. No additional action required.");
        }
        else
        {
            for (var i = 0; i < RecommendedNextSteps.Count; i++)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {i + 1}. {RecommendedNextSteps[i]}"));
            }
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Strictly static, read-only auditor for Unity IL2CPP <c>global-metadata.dat</c> and native binaries.
/// Classifies metadata protection (classic, Unity 6 compact, repeating-XOR recoverable, custom layout,
/// high-entropy encrypted), evaluates game symbol obfuscation ratios, and performs bounded read-only
/// string/PE-section scans without ever executing code or touching process memory.
/// </summary>
public sealed class Il2CppMetadataAuditService
{
    private const int MinMetadataFileBytes = 208;
    private const int HeaderSampleBytes = 4096;
    private const int BodySampleBytes = 65536;
    private const double HighEntropyEncryptedThreshold = 7.4;
    private const double HighEntropyBinarySectionThreshold = 7.5;
    private const int MaxObfuscatedSamples = 10;
    private const int MaxBinaryScanBytes = 64 * 1024 * 1024;
    private const int BinaryScanChunkBytes = 256 * 1024;
    private const int BinaryScanOverlapBytes = 64;
    private const int MaxMetadataOffsetHits = 8;

    private static readonly byte[] ExpectedMagicBytes = { 0xAF, 0x1B, 0xB1, 0xFA };
    private static readonly int[] CandidateClassicVersions = { 29, 31, 24, 27, 28, 30, 25, 26 };
    private static readonly int[] CandidateCompactVersions = { 39, 107 };
    private static readonly int[] AllCandidateVersions = { 29, 31, 24, 27, 28, 30, 25, 26, 39, 107 };
    private static readonly int[] CommonClassicHeaderSizes = { 256, 264, 272 };

    private static readonly HashSet<string> BenignShortIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "UI", "AI", "FX", "VR", "AR", "XR", "IO", "ID", "DB", "HP", "MP", "XP", "OS", "IP", "IK", "UV", "2D", "3D"
    };

    private static readonly byte[][] PlaintextMetadataMarkers =
    {
        Encoding.ASCII.GetBytes(".dll\0"),
        Encoding.ASCII.GetBytes("mscorlib"),
        Encoding.ASCII.GetBytes("Assembly-CSharp"),
        Encoding.ASCII.GetBytes("UnityEngine"),
        Encoding.ASCII.GetBytes("<Module>")
    };

    private readonly record struct BinaryNeedleSpec(string Label, string Pattern, byte[] Bytes, bool RecordOffsets);

    private static readonly BinaryNeedleSpec[] BinaryNeedles =
    {
        new("Metadata filename reference 'global-metadata.dat'", "global-metadata.dat", Encoding.ASCII.GetBytes("global-metadata.dat"), RecordOffsets: true),
        new("IL2CPP MetadataLoader symbol/string", "MetadataLoader", Encoding.ASCII.GetBytes("MetadataLoader"), RecordOffsets: false),
        new("IL2CPP runtime entry 'il2cpp_init'", "il2cpp_init", Encoding.ASCII.GetBytes("il2cpp_init"), RecordOffsets: false),
        new("IL2CPP MetadataCache symbol/string", "il2cpp::vm::MetadataCache", Encoding.ASCII.GetBytes("il2cpp::vm::MetadataCache"), RecordOffsets: false),
        new("IL2CPP codegen marker 'il2cpp_codegen'", "il2cpp_codegen", Encoding.ASCII.GetBytes("il2cpp_codegen"), RecordOffsets: false),
        new("Obfuz marker detected", "Obfuz", Encoding.ASCII.GetBytes("Obfuz"), RecordOffsets: false),
        new("BeeByte obfuscator marker detected", "BeeByte", Encoding.ASCII.GetBytes("BeeByte"), RecordOffsets: false),
        new("HybridCLR hot-update runtime marker detected", "HybridCLR", Encoding.ASCII.GetBytes("HybridCLR"), RecordOffsets: false),
        new("UPX packer marker detected", "UPX0", Encoding.ASCII.GetBytes("UPX0"), RecordOffsets: false),
        new("VMProtect marker '.vmp0' detected", ".vmp0", Encoding.ASCII.GetBytes(".vmp0"), RecordOffsets: false),
        new("VMProtect marker '.vmp1' detected", ".vmp1", Encoding.ASCII.GetBytes(".vmp1"), RecordOffsets: false),
        new("Themida protector marker '.themida' detected", ".themida", Encoding.ASCII.GetBytes(".themida"), RecordOffsets: false)
    };

    private readonly Il2CppMetadataReader _metadataReader;

    public Il2CppMetadataAuditService(Il2CppMetadataReader? metadataReader = null)
    {
        _metadataReader = metadataReader ?? new Il2CppMetadataReader();
    }

    public Il2CppMetadataAuditReport Audit(
        string? metadataPath,
        string? gameAssemblyPath = null,
        string? projectRoot = null,
        bool? hasBackupManagedDlls = null,
        int loadedMonoScriptCount = 0,
        int loadedTypeTreeCount = 0,
        CancellationToken cancellationToken = default)
    {
        return Audit(
            new Il2CppMetadataAuditRequest
            {
                MetadataPath = metadataPath,
                GameAssemblyPath = gameAssemblyPath,
                ProjectRoot = projectRoot,
                HasBackupManagedDlls = hasBackupManagedDlls,
                LoadedMonoScriptCount = loadedMonoScriptCount,
                LoadedTypeTreeCount = loadedTypeTreeCount
            },
            cancellationToken);
    }

    public Il2CppMetadataAuditReport Audit(
        Il2CppMetadataAuditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveMetadataPath = ResolveExistingPath(request.MetadataPath);
        var effectiveBinaryPath = ResolveExistingPath(request.GameAssemblyPath)
            ?? (!string.IsNullOrWhiteSpace(effectiveMetadataPath)
                ? ScriptSourceDiscovery.TryFindGameAssemblyNearMetadata(effectiveMetadataPath)
                : null);

        var hasBackupManaged = request.HasBackupManagedDlls
            ?? (!string.IsNullOrWhiteSpace(request.ProjectRoot)
                && ProjectRuntimeDetector.DiscoverBackupManagedDirectories(request.ProjectRoot).Count > 0);

        NativeBinaryProbeResult? binaryProbe = null;
        if (!string.IsNullOrWhiteSpace(effectiveBinaryPath) && File.Exists(effectiveBinaryPath))
        {
            binaryProbe = ProbeNativeBinary(effectiveBinaryPath, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(effectiveMetadataPath) || !File.Exists(effectiveMetadataPath))
        {
            var missingSteps = BuildRecommendedNextSteps(
                Il2CppMetadataProtectionKind.NoneOrMissing,
                Il2CppSymbolObfuscationKind.Unknown,
                detectedVersion: 0,
                recoveredXorKeyHex: string.Empty,
                obfuscatedRatio: 0,
                hasBackupManaged,
                request.LoadedTypeTreeCount,
                binaryProbe);

            sw.Stop();
            return new Il2CppMetadataAuditReport
            {
                MetadataPath = request.MetadataPath ?? string.Empty,
                GameAssemblyPath = effectiveBinaryPath ?? string.Empty,
                ProtectionKind = Il2CppMetadataProtectionKind.NoneOrMissing,
                SymbolObfuscationKind = Il2CppSymbolObfuscationKind.Unknown,
                HasBackupManagedDlls = hasBackupManaged,
                LoadedMonoScriptCount = request.LoadedMonoScriptCount,
                LoadedTypeTreeCount = request.LoadedTypeTreeCount,
                BinaryIndicators = binaryProbe?.Indicators ?? Array.Empty<string>(),
                MetadataStringOffsetsInBinary = binaryProbe?.MetadataStringOffsets ?? Array.Empty<NativeBinaryStringOffset>(),
                BinarySections = binaryProbe?.Sections ?? Array.Empty<NativeBinarySectionSummary>(),
                HasHighEntropyBinarySection = binaryProbe?.HasHighEntropySection ?? false,
                DiagnosticDetail = "No global-metadata.dat file was found at the target path.",
                RecommendedNextSteps = missingSteps,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        long fileSize;
        byte[] headerSample;
        byte[] bodySample;
        using (var stream = new FileStream(effectiveMetadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
        {
            fileSize = stream.Length;
            var headerLen = (int)Math.Min(fileSize, HeaderSampleBytes);
            headerSample = ReadExactAt(stream, 0, headerLen);

            var bodyLen = (int)Math.Min(fileSize, BodySampleBytes);
            var bodyOffset = Math.Max(0L, (fileSize - bodyLen) / 2);
            bodySample = ReadExactAt(stream, bodyOffset, bodyLen);
        }

        var headerEntropy = ComputeShannonEntropy(headerSample);
        var bodyEntropy = ComputeShannonEntropy(bodySample);
        var zeroRatio = ComputeZeroByteRatio(headerSample);
        var rawMagic = headerSample.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(headerSample.AsSpan(0, 4)) : 0u;
        var rawMagicHex = headerSample.Length >= 4 ? $"0x{rawMagic:X8}" : string.Empty;
        var rawVersion = headerSample.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(headerSample.AsSpan(4, 4)) : 0;

        if (fileSize < MinMetadataFileBytes)
        {
            var truncatedSteps = BuildRecommendedNextSteps(
                Il2CppMetadataProtectionKind.InvalidOrTruncated,
                Il2CppSymbolObfuscationKind.Unknown,
                detectedVersion: 0,
                recoveredXorKeyHex: string.Empty,
                obfuscatedRatio: 0,
                hasBackupManaged,
                request.LoadedTypeTreeCount,
                binaryProbe);

            sw.Stop();
            return new Il2CppMetadataAuditReport
            {
                MetadataPath = effectiveMetadataPath,
                GameAssemblyPath = effectiveBinaryPath ?? string.Empty,
                FileSizeBytes = fileSize,
                RawMagicHex = rawMagicHex,
                RawVersion = rawVersion,
                ProtectionKind = Il2CppMetadataProtectionKind.InvalidOrTruncated,
                SymbolObfuscationKind = Il2CppSymbolObfuscationKind.Unknown,
                HeaderEntropy = headerEntropy,
                BodyEntropy = bodyEntropy,
                HeaderZeroByteRatio = zeroRatio,
                HasBackupManagedDlls = hasBackupManaged,
                LoadedMonoScriptCount = request.LoadedMonoScriptCount,
                LoadedTypeTreeCount = request.LoadedTypeTreeCount,
                BinaryIndicators = binaryProbe?.Indicators ?? Array.Empty<string>(),
                MetadataStringOffsetsInBinary = binaryProbe?.MetadataStringOffsets ?? Array.Empty<NativeBinaryStringOffset>(),
                BinarySections = binaryProbe?.Sections ?? Array.Empty<NativeBinarySectionSummary>(),
                HasHighEntropyBinarySection = binaryProbe?.HasHighEntropySection ?? false,
                DiagnosticDetail = string.Create(CultureInfo.InvariantCulture, $"File is only {fileSize:N0} bytes (minimum IL2CPP header is {MinMetadataFileBytes} bytes)."),
                RecommendedNextSteps = truncatedSteps,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        Il2CppMetadataProtectionKind protectionKind;
        byte[]? recoveredXorKey = null;
        var recoveredXorWholeFile = false;
        var detectedVersion = 0;
        var recoveredVersion = 0;
        var diagnosticDetail = string.Empty;
        Il2CppMetadataLoadOutcome? readableOutcome = null;

        if (rawMagic == Il2CppMetadataReader.MetadataSanity)
        {
            var directOutcome = _metadataReader.InspectMetadata(effectiveMetadataPath, cancellationToken);
            if (directOutcome.Success)
            {
                readableOutcome = directOutcome;
                detectedVersion = directOutcome.MetadataVersion;
                if (directOutcome.DetectedBodyXorKey is { Length: > 0 } bodyKey)
                {
                    protectionKind = Il2CppMetadataProtectionKind.SimpleXorRecoverable;
                    recoveredXorKey = bodyKey.ToArray();
                    recoveredXorWholeFile = false;
                    recoveredVersion = directOutcome.MetadataVersion;
                    diagnosticDetail = $"Header magic is plaintext, and body tables are masked with a recoverable {bodyKey.Length}-byte repeating XOR key.";
                }
                else if (directOutcome.IsCompactLayout)
                {
                    protectionKind = Il2CppMetadataProtectionKind.CompactUnity6;
                    diagnosticDetail = $"Unity 6 compact metadata (v{directOutcome.MetadataVersion}) parsed directly.";
                }
                else
                {
                    protectionKind = Il2CppMetadataProtectionKind.StandardClassic;
                    diagnosticDetail = directOutcome.DetectedBodyOffsetDelta != 0
                        ? $"Classic metadata (v{directOutcome.MetadataVersion}) parsed directly with +{directOutcome.DetectedBodyOffsetDelta} byte body shift."
                        : $"Classic metadata (v{directOutcome.MetadataVersion}) parsed directly.";
                }
            }
            else
            {
                protectionKind = Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout;
                diagnosticDetail = string.IsNullOrWhiteSpace(directOutcome.DiagnosticMessage)
                    ? "Starts with 0xFAB11BAF magic, but header or table offsets deviate from standard IL2CPP layout."
                    : directOutcome.DiagnosticMessage;
            }
        }
        else
        {
            // Magic mismatch: probe repeating 1..32-byte XOR keys first, strictly validated via Il2CppMetadataReader.
            if (TryRecoverRepeatingXorKey(
                    effectiveMetadataPath,
                    fileSize,
                    headerSample,
                    bodySample,
                    cancellationToken,
                    out var candidateKey,
                    out var xorOutcome))
            {
                protectionKind = Il2CppMetadataProtectionKind.SimpleXorRecoverable;
                recoveredXorKey = candidateKey;
                recoveredXorWholeFile = true;
                readableOutcome = xorOutcome;
                detectedVersion = xorOutcome.MetadataVersion;
                recoveredVersion = xorOutcome.MetadataVersion;
                diagnosticDetail = $"Whole-file {candidateKey.Length}-byte repeating XOR mask recovered and validated against metadata tables (v{xorOutcome.MetadataVersion}).";
            }
            else if (HasShiftedMagicOrPlaintextMetadataMarkers(headerSample, bodySample, out var shiftHint))
            {
                protectionKind = Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout;
                diagnosticDetail = shiftHint;
            }
            else if (headerEntropy >= HighEntropyEncryptedThreshold || bodyEntropy >= HighEntropyEncryptedThreshold)
            {
                protectionKind = Il2CppMetadataProtectionKind.HighEntropyEncrypted;
                diagnosticDetail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Magic mismatch ({rawMagicHex}), repeating XOR recovery failed, and Shannon entropy is high (header {headerEntropy:F2}, body {bodyEntropy:F2} bits/byte).");
            }
            else
            {
                protectionKind = Il2CppMetadataProtectionKind.InvalidOrTruncated;
                diagnosticDetail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Magic mismatch ({rawMagicHex}) with low entropy ({headerEntropy:F2} bits/byte) and no recognizable IL2CPP metadata structures.");
            }
        }

        // Symbol obfuscation analysis whenever metadata is readable (raw or XOR-recovered)
        var symbolKind = Il2CppSymbolObfuscationKind.Unknown;
        var assemblyCount = 0;
        var typeCount = 0;
        var gameAssemblyCount = 0;
        var gameTypeCount = 0;
        var obfuscatedGameTypeCount = 0;
        var obfuscatedRatio = 0.0;
        IReadOnlyList<string> sampleObfuscated = Array.Empty<string>();

        if (readableOutcome != null && readableOutcome.Success)
        {
            assemblyCount = readableOutcome.ImageCount;
            typeCount = readableOutcome.TypeCount;
            var catalogXorKey = recoveredXorWholeFile ? recoveredXorKey : null;
            var catalog = _metadataReader.TryReadCatalog(effectiveMetadataPath, catalogXorKey, cancellationToken);
            if (catalog != null && catalog.Images.Count > 0)
            {
                var symbolStats = AnalyzeCatalogSymbols(catalog);
                gameAssemblyCount = symbolStats.GameAssemblyCount;
                gameTypeCount = symbolStats.GameTypeCount;
                obfuscatedGameTypeCount = symbolStats.ObfuscatedGameTypeCount;
                obfuscatedRatio = symbolStats.ObfuscatedRatio;
                symbolKind = symbolStats.Kind;
                sampleObfuscated = symbolStats.SampleNames;
            }
        }

        var xorHex = recoveredXorKey != null ? FormatHexKey(recoveredXorKey) : string.Empty;
        var steps = BuildRecommendedNextSteps(
            protectionKind,
            symbolKind,
            detectedVersion,
            xorHex,
            obfuscatedRatio,
            hasBackupManaged,
            request.LoadedTypeTreeCount,
            binaryProbe);

        sw.Stop();
        return new Il2CppMetadataAuditReport
        {
            MetadataPath = effectiveMetadataPath,
            GameAssemblyPath = effectiveBinaryPath ?? string.Empty,
            FileSizeBytes = fileSize,
            RawMagicHex = rawMagicHex,
            RawVersion = rawVersion,
            ProtectionKind = protectionKind,
            SymbolObfuscationKind = symbolKind,
            HeaderEntropy = headerEntropy,
            BodyEntropy = bodyEntropy,
            HeaderZeroByteRatio = zeroRatio,
            RecoveredXorKeyHex = xorHex,
            RecoveredXorKeyBytes = recoveredXorKey,
            RecoveredXorAppliesToWholeFile = recoveredXorWholeFile,
            DetectedMetadataVersion = detectedVersion,
            RecoveredMetadataVersion = recoveredVersion,
            AssemblyCount = assemblyCount,
            TypeCount = typeCount,
            GameAssemblyCount = gameAssemblyCount,
            GameTypeCount = gameTypeCount,
            ObfuscatedGameTypeCount = obfuscatedGameTypeCount,
            ObfuscatedTypeRatio = obfuscatedRatio,
            SampleObfuscatedNames = sampleObfuscated,
            BinaryIndicators = binaryProbe?.Indicators ?? Array.Empty<string>(),
            MetadataStringOffsetsInBinary = binaryProbe?.MetadataStringOffsets ?? Array.Empty<NativeBinaryStringOffset>(),
            BinarySections = binaryProbe?.Sections ?? Array.Empty<NativeBinarySectionSummary>(),
            HasHighEntropyBinarySection = binaryProbe?.HasHighEntropySection ?? false,
            HasBackupManagedDlls = hasBackupManaged,
            LoadedMonoScriptCount = request.LoadedMonoScriptCount,
            LoadedTypeTreeCount = request.LoadedTypeTreeCount,
            DiagnosticDetail = diagnosticDetail,
            RecommendedNextSteps = steps,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public static double ComputeShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0.0;
        }

        Span<int> counts = stackalloc int[256];
        for (var i = 0; i < data.Length; i++)
        {
            counts[data[i]]++;
        }

        double entropy = 0.0;
        double invLen = 1.0 / data.Length;
        for (var i = 0; i < 256; i++)
        {
            var c = counts[i];
            if (c == 0)
            {
                continue;
            }

            var p = c * invLen;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    public static double ComputeZeroByteRatio(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0.0;
        }

        var zeros = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                zeros++;
            }
        }

        return zeros / (double)data.Length;
    }

    public static string FormatHexKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(key.Length * 3 - 1);
        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(key[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static NativeBinaryProbeResult ProbeNativeBinary(
        string binaryPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            return new NativeBinaryProbeResult { BinaryPath = binaryPath ?? string.Empty };
        }

        var fullPath = Path.GetFullPath(binaryPath);
        var sections = new List<NativeBinarySectionSummary>();
        var indicators = new List<string>();
        var indicatorSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offsets = new List<NativeBinaryStringOffset>();
        var hasHighEntropySection = false;
        var isPe = false;
        var isElf = false;
        long fileSize;

        void AddIndicator(string text)
        {
            if (!string.IsNullOrWhiteSpace(text) && indicatorSet.Add(text))
            {
                indicators.Add(text);
            }
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        fileSize = stream.Length;
        if (fileSize == 0)
        {
            return new NativeBinaryProbeResult { BinaryPath = fullPath, FileSizeBytes = 0 };
        }

        var magicPrefix = ReadExactAt(stream, 0, (int)Math.Min(fileSize, 4));
        if (magicPrefix.Length >= 4
            && magicPrefix[0] == 0x7F
            && magicPrefix[1] == (byte)'E'
            && magicPrefix[2] == (byte)'L'
            && magicPrefix[3] == (byte)'F')
        {
            isElf = true;
        }

        var peSectionRanges = new List<(string Name, long Offset, long Size, int Priority)>();
        if (magicPrefix.Length >= 2 && magicPrefix[0] == (byte)'M' && magicPrefix[1] == (byte)'Z')
        {
            try
            {
                stream.Position = 0;
                using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
                var peHeaders = peReader.PEHeaders;
                if (peHeaders.PEHeader != null)
                {
                    isPe = true;
                    foreach (var sec in peHeaders.SectionHeaders)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long rawOff = sec.PointerToRawData;
                        long rawSize = sec.SizeOfRawData;
                        if (rawOff < 0 || rawSize < 0 || rawOff >= fileSize)
                        {
                            rawSize = 0;
                        }
                        else if (rawOff + rawSize > fileSize)
                        {
                            rawSize = fileSize - rawOff;
                        }

                        var secEntropy = 0.0;
                        if (rawSize > 0)
                        {
                            var sampleLen = (int)Math.Min(rawSize, BodySampleBytes);
                            var sampleOff = rawOff + Math.Max(0L, (rawSize - sampleLen) / 2);
                            var sampleBytes = ReadExactAt(stream, sampleOff, sampleLen);
                            secEntropy = ComputeShannonEntropy(sampleBytes);
                        }

                        var isHigh = rawSize >= 256 && secEntropy >= HighEntropyBinarySectionThreshold;
                        if (isHigh)
                        {
                            hasHighEntropySection = true;
                            AddIndicator(string.Create(
                                CultureInfo.InvariantCulture,
                                $"High-entropy PE section '{sec.Name}' ({secEntropy:F2} bits/byte) — possible native code packing/virtualization"));
                        }

                        sections.Add(new NativeBinarySectionSummary(sec.Name, rawOff, rawSize, secEntropy, isHigh));
                        CheckKnownPackerSectionName(sec.Name, AddIndicator);

                        if (rawSize > 0)
                        {
                            var priority = GetSectionScanPriority(sec.Name);
                            peSectionRanges.Add((sec.Name, rawOff, rawSize, priority));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Non-standard or truncated PE header; fall back safely to raw bounded streaming scan.
            }
        }

        var foundNeedleFlags = new bool[BinaryNeedles.Length];
        long totalScanned = 0;

        if (isPe && peSectionRanges.Count > 0)
        {
            peSectionRanges.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
            foreach (var range in peSectionRanges)
            {
                if (totalScanned >= MaxBinaryScanBytes)
                {
                    break;
                }

                // Executable code sections (.text) rarely hold ASCII metadata paths once data sections already matched.
                if (range.Priority >= 2 && offsets.Count > 0)
                {
                    continue;
                }

                var sectionCap = range.Priority >= 2 ? 8 * 1024 * 1024 : MaxBinaryScanBytes;
                var budget = Math.Min(Math.Min(range.Size, sectionCap), MaxBinaryScanBytes - totalScanned);
                ScanStreamRangeForNeedles(
                    stream,
                    range.Offset,
                    budget,
                    range.Name,
                    foundNeedleFlags,
                    offsets,
                    AddIndicator,
                    cancellationToken);
                totalScanned += budget;
            }
        }
        else
        {
            var budget = Math.Min(fileSize, MaxBinaryScanBytes);
            var regionName = isElf ? "ELF" : "raw";
            ScanStreamRangeForNeedles(
                stream,
                0,
                budget,
                regionName,
                foundNeedleFlags,
                offsets,
                AddIndicator,
                cancellationToken);
        }

        return new NativeBinaryProbeResult
        {
            BinaryPath = fullPath,
            FileSizeBytes = fileSize,
            IsPeBinary = isPe,
            IsElfBinary = isElf,
            HasHighEntropySection = hasHighEntropySection,
            Sections = sections,
            MetadataStringOffsets = offsets,
            Indicators = indicators
        };
    }

    private static int GetSectionScanPriority(string sectionName)
    {
        if (sectionName.Equals(".rdata", StringComparison.OrdinalIgnoreCase)
            || sectionName.Equals(".rodata", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (sectionName.Equals(".data", StringComparison.OrdinalIgnoreCase)
            || sectionName.Equals("il2cpp", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (sectionName.Equals(".text", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static void CheckKnownPackerSectionName(string sectionName, Action<string> addIndicator)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return;
        }

        if (sectionName.StartsWith(".vmp", StringComparison.OrdinalIgnoreCase))
        {
            addIndicator($"PE section '{sectionName}' detected (VMProtect marker)");
        }
        else if (sectionName.StartsWith(".themida", StringComparison.OrdinalIgnoreCase)
            || sectionName.StartsWith(".winlice", StringComparison.OrdinalIgnoreCase))
        {
            addIndicator($"PE section '{sectionName}' detected (Themida/WinLicense marker)");
        }
        else if (sectionName.StartsWith("UPX", StringComparison.OrdinalIgnoreCase)
            || sectionName.Equals(".upx", StringComparison.OrdinalIgnoreCase))
        {
            addIndicator($"PE section '{sectionName}' detected (UPX packer marker)");
        }
        else if (sectionName.StartsWith(".enigma", StringComparison.OrdinalIgnoreCase))
        {
            addIndicator($"PE section '{sectionName}' detected (Enigma Protector marker)");
        }
        else if (sectionName.Contains("obfuz", StringComparison.OrdinalIgnoreCase))
        {
            addIndicator($"PE section '{sectionName}' detected (Obfuz marker)");
        }
    }

    private static void ScanStreamRangeForNeedles(
        Stream stream,
        long startOffset,
        long length,
        string sectionName,
        bool[] foundNeedleFlags,
        List<NativeBinaryStringOffset> metadataOffsets,
        Action<string> addIndicator,
        CancellationToken cancellationToken)
    {
        if (length <= 0 || startOffset < 0 || startOffset >= stream.Length)
        {
            return;
        }

        var endOffset = Math.Min(stream.Length, startOffset + length);
        var buffer = new byte[BinaryScanChunkBytes + BinaryScanOverlapBytes];
        var pos = startOffset;
        var carry = 0;

        while (pos < endOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(BinaryScanChunkBytes, endOffset - pos);
            stream.Position = pos;
            var read = ReadUpTo(stream, buffer, carry, toRead);
            if (read <= 0)
            {
                break;
            }

            var totalLen = carry + read;
            var chunkFileStart = pos - carry;
            var span = buffer.AsSpan(0, totalLen);

            for (var n = 0; n < BinaryNeedles.Length; n++)
            {
                var spec = BinaryNeedles[n];
                if (!spec.RecordOffsets && foundNeedleFlags[n])
                {
                    continue;
                }

                if (spec.RecordOffsets && metadataOffsets.Count >= MaxMetadataOffsetHits && foundNeedleFlags[n])
                {
                    continue;
                }

                var searchPos = 0;
                while (searchPos < span.Length)
                {
                    var idx = span.Slice(searchPos).IndexOf(spec.Bytes);
                    if (idx < 0)
                    {
                        break;
                    }

                    var matchInChunk = searchPos + idx;
                    var absoluteOffset = chunkFileStart + matchInChunk;
                    foundNeedleFlags[n] = true;
                    addIndicator(spec.Label);

                    if (spec.RecordOffsets)
                    {
                        if (metadataOffsets.Count < MaxMetadataOffsetHits
                            && !metadataOffsets.Any(h => h.FileOffset == absoluteOffset))
                        {
                            metadataOffsets.Add(new NativeBinaryStringOffset(spec.Pattern, absoluteOffset, sectionName));
                        }
                        searchPos = matchInChunk + spec.Bytes.Length;
                        if (metadataOffsets.Count >= MaxMetadataOffsetHits)
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            pos += read;
            if (pos < endOffset)
            {
                carry = Math.Min(BinaryScanOverlapBytes, totalLen);
                Buffer.BlockCopy(buffer, totalLen - carry, buffer, 0, carry);
            }
            else
            {
                carry = 0;
            }
        }
    }

    private bool TryRecoverRepeatingXorKey(
        string metadataPath,
        long fileSize,
        byte[] headerSample,
        byte[] bodySample,
        CancellationToken cancellationToken,
        out byte[] recoveredKey,
        out Il2CppMetadataLoadOutcome verifiedOutcome)
    {
        recoveredKey = Array.Empty<byte>();
        verifiedOutcome = new Il2CppMetadataLoadOutcome();

        if (headerSample.Length < 16)
        {
            return false;
        }

        // 1. Key length 1
        {
            var k0 = (byte)(headerSample[0] ^ ExpectedMagicBytes[0]);
            if (k0 != 0
                && (headerSample[1] ^ k0) == ExpectedMagicBytes[1]
                && (headerSample[2] ^ k0) == ExpectedMagicBytes[2]
                && (headerSample[3] ^ k0) == ExpectedMagicBytes[3])
            {
                var key = new[] { k0 };
                if (TryValidateWholeFileXorCandidate(metadataPath, headerSample, fileSize, key, cancellationToken, out verifiedOutcome))
                {
                    recoveredKey = key;
                    return true;
                }
            }
        }

        // 2. Key length 2
        {
            var k0 = (byte)(headerSample[0] ^ ExpectedMagicBytes[0]);
            var k1 = (byte)(headerSample[1] ^ ExpectedMagicBytes[1]);
            if ((k0 != 0 || k1 != 0)
                && (headerSample[2] ^ k0) == ExpectedMagicBytes[2]
                && (headerSample[3] ^ k1) == ExpectedMagicBytes[3])
            {
                var key = new[] { k0, k1 };
                if (TryValidateWholeFileXorCandidate(metadataPath, headerSample, fileSize, key, cancellationToken, out verifiedOutcome))
                {
                    recoveredKey = key;
                    return true;
                }
            }
        }

        // 3. Key length 4
        {
            var key = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                key[i] = (byte)(headerSample[i] ^ ExpectedMagicBytes[i]);
            }

            if (HasAnyNonZero(key)
                && TryValidateWholeFileXorCandidate(metadataPath, headerSample, fileSize, key, cancellationToken, out verifiedOutcome))
            {
                recoveredKey = key;
                return true;
            }
        }

        // 4. Key length 8 (bytes 0..3 from magic, bytes 5..7 from version upper zero bytes, byte 4 from candidate versions)
        {
            var key = new byte[8];
            for (var i = 0; i < 4; i++)
            {
                key[i] = (byte)(headerSample[i] ^ ExpectedMagicBytes[i]);
            }
            key[5] = headerSample[5];
            key[6] = headerSample[6];
            key[7] = headerSample[7];

            var decodedHeaderSize = ReadXorInt32(headerSample, 8, key);
            if (decodedHeaderSize >= 200 && decodedHeaderSize <= 1024 && decodedHeaderSize <= fileSize)
            {
                foreach (var version in AllCandidateVersions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isCompact = version is 39 or 107;
                    if (isCompact)
                    {
                        if (decodedHeaderSize != (version == 39 ? 380 : 392))
                        {
                            continue;
                        }
                    }
                    else if ((decodedHeaderSize - 8) % 8 != 0)
                    {
                        continue;
                    }

                    key[4] = (byte)(headerSample[4] ^ (byte)version);
                    if (!HasAnyNonZero(key))
                    {
                        continue;
                    }

                    if (TryValidateWholeFileXorCandidate(metadataPath, headerSample, fileSize, key, cancellationToken, out verifiedOutcome))
                    {
                        recoveredKey = key.ToArray();
                        return true;
                    }
                }
            }
        }

        // 5. Key lengths 16 and 32
        using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        foreach (var period in new[] { 16, 32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryRecoverLongRepeatingXorKey(
                    stream,
                    metadataPath,
                    fileSize,
                    headerSample,
                    bodySample,
                    period,
                    cancellationToken,
                    out recoveredKey,
                    out verifiedOutcome))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryRecoverLongRepeatingXorKey(
        FileStream stream,
        string metadataPath,
        long fileSize,
        byte[] headerSample,
        byte[] bodySample,
        int period,
        CancellationToken cancellationToken,
        out byte[] recoveredKey,
        out Il2CppMetadataLoadOutcome verifiedOutcome)
    {
        recoveredKey = Array.Empty<byte>();
        verifiedOutcome = new Il2CppMetadataLoadOutcome();

        if (headerSample.Length < Math.Min(fileSize, 208))
        {
            return false;
        }

        var key = new byte[period];
        for (var i = 0; i < 4; i++)
        {
            key[i] = (byte)(headerSample[i] ^ ExpectedMagicBytes[i]);
        }
        key[5] = headerSample[5];
        key[6] = headerSample[6];
        key[7] = headerSample[7];
        key[10] = headerSample[10];
        key[11] = headerSample[11];

        // 5a. Classic v24..31 probing:
        // Byte offset 160 (pairs[19] = typeDefinitions) and 96 (pairs[11] = fields) and 32 (pairs[3] = events)
        // are ALL aligned to mod-16 AND mod-32 lanes 0..7, so key[0..7] decodes them directly!
        if (headerSample.Length >= 168)
        {
            foreach (var version in CandidateClassicVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                key[4] = (byte)(headerSample[4] ^ (byte)version);

                var tdOff = ReadXorInt32(headerSample, 160, key);
                var tdSize = ReadXorInt32(headerSample, 164, key);
                if (tdOff < 200 || tdSize <= 0 || (long)tdOff + tdSize > fileSize)
                {
                    continue;
                }

                var eventsOff = ReadXorInt32(headerSample, 32, key);
                var eventsSize = ReadXorInt32(headerSample, 36, key);
                var fieldsOff = ReadXorInt32(headerSample, 96, key);
                var fieldsSize = ReadXorInt32(headerSample, 100, key);
                if (eventsOff < 0 || eventsSize < 0 || (long)eventsOff + eventsSize > fileSize
                    || fieldsOff < 0 || fieldsSize < 0 || (long)fieldsOff + fieldsSize > fileSize)
                {
                    continue;
                }

                var binarySamples = new List<(long FileOffset, byte[] Bytes)>(4)
                {
                    (tdOff, ReadExactAt(stream, tdOff, (int)Math.Min(tdSize, BodySampleBytes)))
                };
                if (fieldsSize >= 12)
                {
                    binarySamples.Add((fieldsOff, ReadExactAt(stream, fieldsOff, Math.Min(fieldsSize, 16 * 1024))));
                }
                if (period == 16)
                {
                    var methodsOff = ReadXorInt32(headerSample, 48, key);
                    var methodsSize = ReadXorInt32(headerSample, 52, key);
                    if (methodsOff >= 200 && methodsSize >= 32 && (long)methodsOff + methodsSize <= fileSize)
                    {
                        binarySamples.Add((methodsOff, ReadExactAt(stream, methodsOff, Math.Min(methodsSize, 16 * 1024))));
                    }
                }

                // Build candidate headerSizes (common classic header sizes + frequency-inferred candidate)
                var headerSizeCandidates = new List<int>(CommonClassicHeaderSizes);
                var roughKey = (byte[])key.Clone();
                SolveUnknownLanes(
                    roughKey,
                    startLane: 8,
                    period,
                    headerSample,
                    headerRegionLen: (int)Math.Min(headerSample.Length, 256),
                    binarySamples,
                    stringSampleOffset: 0,
                    stringSample: Array.Empty<byte>());
                var roughHdrSize = ReadXorInt32(headerSample, 8, roughKey);
                if (roughHdrSize >= 200
                    && roughHdrSize <= 1024
                    && (roughHdrSize - 8) % 8 == 0
                    && roughHdrSize <= fileSize
                    && !headerSizeCandidates.Contains(roughHdrSize))
                {
                    headerSizeCandidates.Add(roughHdrSize);
                }

                foreach (var hdrSize in headerSizeCandidates)
                {
                    if (hdrSize > fileSize || hdrSize > headerSample.Length)
                    {
                        continue;
                    }

                    var candidate = (byte[])key.Clone();
                    candidate[8] = (byte)(headerSample[8] ^ (byte)(hdrSize & 0xFF));
                    candidate[9] = (byte)(headerSample[9] ^ (byte)((hdrSize >> 8) & 0xFF));
                    candidate[10] = headerSample[10];
                    candidate[11] = headerSample[11];

                    long strOff = 0;
                    int strLen = 0;
                    if (period == 16)
                    {
                        // pairs[2].Offset is at byte 24..27 -> lanes 8..11 (exact!), eventsOff is pairs[3].Offset (exact!)
                        var sOff = ReadXorInt32(headerSample, 24, candidate);
                        if (sOff >= hdrSize && eventsOff > sOff && eventsOff <= fileSize)
                        {
                            strOff = sOff;
                            strLen = (int)Math.Min(eventsOff - sOff, 32 * 1024);
                        }
                    }

                    if (strLen <= 0)
                    {
                        // Pass 1 without strings to locate pairs[2] (strings slice at bytes 24..31)
                        SolveUnknownLanes(
                            candidate,
                            startLane: 12,
                            period,
                            headerSample,
                            hdrSize,
                            binarySamples,
                            stringSampleOffset: 0,
                            stringSample: Array.Empty<byte>());

                        var pass1StrOff = ReadXorInt32(headerSample, 24, candidate);
                        var pass1StrSize = ReadXorInt32(headerSample, 28, candidate);
                        if (pass1StrOff >= hdrSize && pass1StrSize >= period * 2 && (long)pass1StrOff + pass1StrSize <= fileSize)
                        {
                            strOff = pass1StrOff;
                            strLen = Math.Min(pass1StrSize, 32 * 1024);
                        }
                        else if (eventsOff > hdrSize && eventsOff <= fileSize)
                        {
                            strOff = hdrSize;
                            strLen = (int)Math.Min(eventsOff - hdrSize, 32 * 1024);
                        }
                    }

                    var stringBytes = strLen > 0 ? ReadExactAt(stream, strOff, strLen) : Array.Empty<byte>();
                    SolveUnknownLanes(
                        candidate,
                        startLane: 12,
                        period,
                        headerSample,
                        hdrSize,
                        binarySamples,
                        strOff,
                        stringBytes);

                    if (HasAnyNonZero(candidate)
                        && TryValidateWholeFileXorCandidate(metadataPath, headerSample, fileSize, candidate, cancellationToken, out verifiedOutcome))
                    {
                        recoveredKey = candidate;
                        return true;
                    }
                }
            }
        }

        // 5b. Compact Unity 6 (v39 / v107) probing:
        // CompactTable.Strings is section 2 at bytes 32..43 -> lanes 0..11, which are ALL known algebraically!
        foreach (var version in CandidateCompactVersions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedHeaderSize = version == 39 ? 380 : 392;
            if (fileSize < expectedHeaderSize || headerSample.Length < expectedHeaderSize)
            {
                continue;
            }

            var candidate = (byte[])key.Clone();
            candidate[4] = (byte)(headerSample[4] ^ (byte)version);
            candidate[8] = (byte)(headerSample[8] ^ (byte)(expectedHeaderSize & 0xFF));
            candidate[9] = (byte)(headerSample[9] ^ (byte)((expectedHeaderSize >> 8) & 0xFF));
            candidate[10] = headerSample[10];
            candidate[11] = headerSample[11];

            var strOff = ReadXorInt32(headerSample, 32, candidate);
            var strSize = ReadXorInt32(headerSample, 36, candidate);
            if (strOff < expectedHeaderSize || strSize <= 0 || (long)strOff + strSize > fileSize)
            {
                continue;
            }

            var strSampleLen = Math.Min(strSize, 32 * 1024);
            var strSample = ReadExactAt(stream, strOff, strSampleLen);
            var bodyOffset = Math.Max(0L, (fileSize - bodySample.Length) / 2);
            var binarySamples = new List<(long FileOffset, byte[] Bytes)>(1)
            {
                (bodyOffset, bodySample)
            };

            SolveUnknownLanes(
                candidate,
                startLane: 12,
                period,
                headerSample,
                expectedHeaderSize,
                binarySamples,
                strOff,
                strSample);

            if (HasAnyNonZero(candidate)
                && TryValidateWholeFileXorCandidate(metadataPath, headerSample, fileSize, candidate, cancellationToken, out verifiedOutcome))
            {
                recoveredKey = candidate;
                return true;
            }
        }

        return false;
    }

    private static void SolveUnknownLanes(
        byte[] key,
        int startLane,
        int period,
        byte[] headerSample,
        int headerRegionLen,
        List<(long FileOffset, byte[] Bytes)> binarySamples,
        long stringSampleOffset,
        byte[] stringSample)
    {
        var hdrHist = new int[256];
        var binHist = new int[256];
        var strHist = new int[256];
        var effectiveHdrLen = Math.Min(headerSample.Length, headerRegionLen);

        for (var lane = startLane; lane < period; lane++)
        {
            Array.Clear(hdrHist);
            Array.Clear(binHist);
            Array.Clear(strHist);

            var hdrCount = 0;
            for (var pos = lane; pos < effectiveHdrLen; pos += period)
            {
                if (pos >= 8)
                {
                    hdrHist[headerSample[pos]]++;
                    hdrCount++;
                }
            }

            var binCount = 0;
            for (var s = 0; s < binarySamples.Count; s++)
            {
                var (bOff, bBuf) = binarySamples[s];
                var first = (int)((period - ((bOff - lane) % period + period) % period) % period);
                for (var i = first; i < bBuf.Length; i += period)
                {
                    binHist[bBuf[i]]++;
                    binCount++;
                }
            }

            var strCount = 0;
            if (stringSample.Length > 0)
            {
                var first = (int)((period - ((stringSampleOffset - lane) % period + period) % period) % period);
                for (var i = first; i < stringSample.Length; i += period)
                {
                    strHist[stringSample[i]]++;
                    strCount++;
                }
            }

            var isInt32MsbLane = (lane % 4) == 3;
            var bestK = 0;
            var bestScore = double.NegativeInfinity;

            for (var k = 0; k < 256; k++)
            {
                var score = 0.0;

                if (binCount > 0)
                {
                    score += (binHist[k] / (double)binCount) * 6.0;
                }

                if (hdrCount > 0)
                {
                    var hdrZeros = hdrHist[k] / (double)hdrCount;
                    score += hdrZeros * 4.0;

                    if (isInt32MsbLane)
                    {
                        var negativeMsbCount = 0;
                        for (var b = 0; b < 256; b++)
                        {
                            var cnt = hdrHist[b];
                            if (cnt > 0 && (b ^ k) >= 0x80)
                            {
                                negativeMsbCount += cnt;
                            }
                        }

                        if (negativeMsbCount > 0)
                        {
                            score -= 50.0 * (negativeMsbCount / (double)hdrCount);
                        }
                    }
                }

                if (strCount >= 4)
                {
                    var validCount = 0;
                    var identCount = 0;
                    for (var b = 0; b < 256; b++)
                    {
                        var cnt = strHist[b];
                        if (cnt == 0)
                        {
                            continue;
                        }

                        var d = b ^ k;
                        if (d == 0
                            || (d >= 'A' && d <= 'Z')
                            || (d >= 'a' && d <= 'z')
                            || (d >= '0' && d <= '9')
                            || d == '_' || d == '.' || d == '<' || d == '>' || d == '`' || d == '$')
                        {
                            validCount += cnt;
                            identCount += cnt;
                        }
                        else if (d >= 0x20 && d <= 0x7E)
                        {
                            validCount += cnt;
                        }
                    }

                    var validRatio = validCount / (double)strCount;
                    if (validRatio < 0.90)
                    {
                        score -= 100.0 * (1.0 - validRatio);
                    }
                    else
                    {
                        score += validRatio * 12.0 + (identCount / (double)strCount) * 3.0;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestK = k;
                }
            }

            key[lane] = (byte)bestK;
        }
    }

    private bool TryValidateWholeFileXorCandidate(
        string metadataPath,
        byte[] headerSample,
        long fileSize,
        byte[] candidateKey,
        CancellationToken cancellationToken,
        out Il2CppMetadataLoadOutcome outcome)
    {
        outcome = new Il2CppMetadataLoadOutcome();
        var version = ReadXorInt32(headerSample, 4, candidateKey);
        if (version < Il2CppMetadataReader.MinLegacyVersion || version > 107)
        {
            return false;
        }

        var headerSize = ReadXorInt32(headerSample, 8, candidateKey);
        if (headerSize < 200 || headerSize > 1024 || headerSize > fileSize)
        {
            return false;
        }

        var inspect = _metadataReader.InspectMetadata(metadataPath, candidateKey, cancellationToken);
        if (!inspect.Success)
        {
            return false;
        }

        outcome = inspect;
        return true;
    }

    private static int ReadXorInt32(byte[] buffer, int offset, byte[] key)
    {
        var period = key.Length;
        var b0 = (byte)(buffer[offset] ^ key[offset % period]);
        var b1 = (byte)(buffer[offset + 1] ^ key[(offset + 1) % period]);
        var b2 = (byte)(buffer[offset + 2] ^ key[(offset + 2) % period]);
        var b3 = (byte)(buffer[offset + 3] ^ key[(offset + 3) % period]);
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    private static bool HasAnyNonZero(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasShiftedMagicOrPlaintextMetadataMarkers(
        byte[] headerSample,
        byte[] bodySample,
        out string detail)
    {
        detail = string.Empty;
        var magicPos = headerSample.AsSpan().IndexOf(ExpectedMagicBytes);
        if (magicPos > 0)
        {
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"IL2CPP magic 0xFAB11BAF detected at non-zero header offset 0x{magicPos:X} (shifted header / custom prefix).");
            return true;
        }

        foreach (var marker in PlaintextMetadataMarkers)
        {
            if (headerSample.AsSpan().IndexOf(marker) >= 0 || bodySample.AsSpan().IndexOf(marker) >= 0)
            {
                var label = Encoding.ASCII.GetString(marker).TrimEnd('\0');
                detail = $"Unrecognized header magic, but plaintext metadata marker '{label}' is visible in file contents (custom/shifted layout).";
                return true;
            }
        }

        return false;
    }

    private readonly record struct SymbolAuditStats(
        int GameAssemblyCount,
        int GameTypeCount,
        int ObfuscatedGameTypeCount,
        double ObfuscatedRatio,
        Il2CppSymbolObfuscationKind Kind,
        IReadOnlyList<string> SampleNames);

    private static SymbolAuditStats AnalyzeCatalogSymbols(MetadataCatalogResult catalog)
    {
        var gameTypes = new List<MetadataCatalogType>(256);
        var gameAssemblyCount = 0;

        for (var i = 0; i < catalog.Images.Count; i++)
        {
            var img = catalog.Images[i];
            if (!ScriptProjectAssemblyClassifier.IsGameAssembly(img.ImageName))
            {
                continue;
            }

            gameAssemblyCount++;
            for (var t = 0; t < img.Types.Count; t++)
            {
                var type = img.Types[t];
                if (string.IsNullOrWhiteSpace(type.ClassName)
                    || string.Equals(type.ClassName, "<Module>", StringComparison.Ordinal))
                {
                    continue;
                }

                gameTypes.Add(type);
            }
        }

        if (gameTypes.Count == 0)
        {
            return new SymbolAuditStats(gameAssemblyCount, 0, 0, 0.0, Il2CppSymbolObfuscationKind.Unknown, Array.Empty<string>());
        }

        // Count 1-2 character short names (excluding known acronyms like UI, AI, FX) to detect ProGuard/short-name renaming.
        var shortNameCount = 0;
        for (var i = 0; i < gameTypes.Count; i++)
        {
            var baseName = StripGenericArity(gameTypes[i].ClassName);
            if (IsSuspiciousShortName(baseName))
            {
                shortNameCount++;
            }
        }

        var flagShortNames = shortNameCount >= 3 && (shortNameCount / (double)gameTypes.Count) >= 0.15;

        var obfuscatedCount = 0;
        var samples = new List<string>(MaxObfuscatedSamples);
        var sampleSeen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < gameTypes.Count; i++)
        {
            var type = gameTypes[i];
            var baseName = StripGenericArity(type.ClassName);
            var isObfuscated = IsLikelyObfuscatedIdentifier(baseName, flagShortNames)
                || HasObfuscatedNamespaceSegment(type.Namespace);

            if (isObfuscated)
            {
                obfuscatedCount++;
                if (samples.Count < MaxObfuscatedSamples)
                {
                    var display = string.IsNullOrWhiteSpace(type.Namespace)
                        ? type.ClassName
                        : $"{type.Namespace}.{type.ClassName}";
                    if (sampleSeen.Add(display))
                    {
                        samples.Add(display);
                    }
                }
            }
        }

        var ratio = obfuscatedCount / (double)gameTypes.Count;
        var kind = ratio < 0.05
            ? Il2CppSymbolObfuscationKind.Clean
            : ratio <= 0.35
                ? Il2CppSymbolObfuscationKind.PartiallyObfuscated
                : Il2CppSymbolObfuscationKind.HeavilyObfuscated;

        return new SymbolAuditStats(gameAssemblyCount, gameTypes.Count, obfuscatedCount, ratio, kind, samples);
    }

    internal static bool IsLikelyObfuscatedIdentifier(string name, bool flagShortNames = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // 1. Non-ASCII / invisible / Unicode control / private-use / symbol noise
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch < 0x20 || ch == 0x7F || ch > 0x7E)
            {
                return true;
            }

            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.Surrogate
                or UnicodeCategory.OtherSymbol)
            {
                return true;
            }
        }

        // 2. Short 1..2 char names when cohort has high ratio of them
        if (flagShortNames && IsSuspiciousShortName(name))
        {
            return true;
        }

        // 3. Confusable homoglyph patterns: O0O0O0, l1l1l1, I1I1I1, IlIlIl
        if (name.Length >= 4 && IsHomoglyphConfusable(name))
        {
            return true;
        }

        // 4. All-uppercase gibberish (e.g. BeeByte 11-char uppercase names like ABCDEFGHIJK / ECLJAGIGDDM)
        if (name.Length >= 8 && IsAllAsciiUpperLetters(name))
        {
            if (name.Length >= 10)
            {
                return true;
            }

            var (vowelRatio, maxConsonantRun) = MeasureVowelAndConsonantRun(name);
            if (maxConsonantRun >= 4 || vowelRatio < 0.18 || vowelRatio > 0.65)
            {
                return true;
            }
        }

        // 5. Pure hex-like identifiers of length >= 8 containing both digits and hex letters (e.g. a3f90c2e, _0x1a2b3c4d)
        if (IsHexLikeObfuscatedName(name))
        {
            return true;
        }

        // 6. Random base62 mixed-case+digit gibberish (e.g. xK9mP2qR7z)
        if (name.Length >= 7 && IsRandomBase62Gibberish(name))
        {
            return true;
        }

        return false;
    }

    private static bool HasObfuscatedNamespaceSegment(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns))
        {
            return false;
        }

        var start = 0;
        for (var i = 0; i <= ns.Length; i++)
        {
            if (i == ns.Length || ns[i] == '.')
            {
                if (i > start)
                {
                    var seg = ns.Substring(start, i - start);
                    if (IsLikelyObfuscatedIdentifier(seg, flagShortNames: false))
                    {
                        return true;
                    }
                }
                start = i + 1;
            }
        }

        return false;
    }

    private static bool IsSuspiciousShortName(string name)
    {
        if (name.Length == 0 || name.Length > 2)
        {
            return false;
        }

        return !BenignShortIdentifiers.Contains(name);
    }

    private static bool IsHomoglyphConfusable(string name)
    {
        var allO0 = true;
        var allI1l = true;
        var hasDistinctO0 = false;
        var hasDistinctI1l = false;
        var firstChar = name[0];

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch != 'O' && ch != 'o' && ch != '0' && ch != '_')
            {
                allO0 = false;
            }
            else if (ch != firstChar)
            {
                hasDistinctO0 = true;
            }

            if (ch != 'I' && ch != 'l' && ch != '1' && ch != '_')
            {
                allI1l = false;
            }
            else if (ch != firstChar)
            {
                hasDistinctI1l = true;
            }
        }

        return (allO0 && (hasDistinctO0 || name.Length >= 5))
            || (allI1l && (hasDistinctI1l || name.Length >= 5));
    }

    private static bool IsAllAsciiUpperLetters(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch < 'A' || ch > 'Z')
            {
                return false;
            }
        }
        return true;
    }

    private static (double VowelRatio, int MaxConsonantRun) MeasureVowelAndConsonantRun(string upper)
    {
        var vowels = 0;
        var run = 0;
        var maxRun = 0;

        for (var i = 0; i < upper.Length; i++)
        {
            var ch = upper[i];
            var isVowel = ch is 'A' or 'E' or 'I' or 'O' or 'U';
            if (isVowel)
            {
                vowels++;
                run = 0;
            }
            else
            {
                run++;
                if (run > maxRun)
                {
                    maxRun = run;
                }
            }
        }

        return (vowels / (double)upper.Length, maxRun);
    }

    private static bool IsHexLikeObfuscatedName(string name)
    {
        var start = 0;
        if (name.StartsWith("_0x", StringComparison.OrdinalIgnoreCase))
        {
            start = 3;
        }
        else if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            start = 2;
        }
        else if (name.Length >= 10 && name[1] == '_' && char.IsLetter(name[0]))
        {
            start = 2;
        }

        var len = name.Length - start;
        if (len < 8)
        {
            return false;
        }

        var hasDigit = false;
        var hasHexAlpha = false;
        for (var i = start; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch >= '0' && ch <= '9')
            {
                hasDigit = true;
            }
            else if ((ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))
            {
                hasHexAlpha = true;
            }
            else
            {
                return false;
            }
        }

        return hasDigit && hasHexAlpha;
    }

    private static bool IsRandomBase62Gibberish(string name)
    {
        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;
        var transitions = 0;
        var prevClass = -1;
        var lowerRun = 0;
        var maxLowerRun = 0;

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            int cls;
            if (ch >= 'A' && ch <= 'Z')
            {
                hasUpper = true;
                cls = 0;
                lowerRun = 0;
            }
            else if (ch >= 'a' && ch <= 'z')
            {
                hasLower = true;
                cls = 1;
                lowerRun++;
                if (lowerRun > maxLowerRun)
                {
                    maxLowerRun = lowerRun;
                }
            }
            else if (ch >= '0' && ch <= '9')
            {
                hasDigit = true;
                cls = 2;
                lowerRun = 0;
            }
            else
            {
                return false;
            }

            if (prevClass >= 0 && cls != prevClass)
            {
                transitions++;
            }
            prevClass = cls;
        }

        return hasUpper && hasLower && hasDigit && transitions >= 4 && maxLowerRun <= 2;
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        var angle = name.IndexOf('<');
        var cut = tick >= 0 && angle >= 0 ? Math.Min(tick, angle) : Math.Max(tick, angle);
        return cut > 0 ? name.Substring(0, cut) : name;
    }

    private static IReadOnlyList<string> BuildRecommendedNextSteps(
        Il2CppMetadataProtectionKind protectionKind,
        Il2CppSymbolObfuscationKind symbolKind,
        int detectedVersion,
        string recoveredXorKeyHex,
        double obfuscatedRatio,
        bool hasBackupManagedDlls,
        int loadedTypeTreeCount,
        NativeBinaryProbeResult? binaryProbe)
    {
        var steps = new List<string>(6);

        if (hasBackupManagedDlls)
        {
            steps.Add("Backup Managed DLLs detected — full C# decompilation is available directly without relying on IL2CPP metadata.");
        }

        switch (protectionKind)
        {
            case Il2CppMetadataProtectionKind.NoneOrMissing:
                steps.Add("No global-metadata.dat was found. If this is a Mono build, inspect Managed DLLs directly; for IL2CPP builds, use 'Scripts Folder...' to select the folder containing global-metadata.dat.");
                break;
            case Il2CppMetadataProtectionKind.StandardClassic:
                steps.Add(string.Create(CultureInfo.InvariantCulture, $"Metadata is readable directly (classic IL2CPP v{detectedVersion}) — type, field, property, and method signatures are available in the built-in Script viewer."));
                break;
            case Il2CppMetadataProtectionKind.CompactUnity6:
                steps.Add(string.Create(CultureInfo.InvariantCulture, $"Metadata is readable directly (Unity 6 compact v{detectedVersion}) — built-in reader handles compact metadata tables natively."));
                break;
            case Il2CppMetadataProtectionKind.SimpleXorRecoverable:
                steps.Add(string.Create(CultureInfo.InvariantCulture, $"Repeating XOR mask recovered statically (key: {recoveredXorKeyHex}, v{detectedVersion}) — Il2CppMetadataReader can decode metadata tables using this key."));
                break;
            case Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout:
                steps.Add("Metadata uses a shifted or custom header/table layout. Inspect MetadataLoader / GlobalMetadata parsing in Ghidra/IDA, or use Unity TypeTree fallback for MonoBehaviour serialized fields.");
                break;
            case Il2CppMetadataProtectionKind.HighEntropyEncrypted:
                steps.Add("High-entropy encrypted metadata — inspect MetadataLoader::LoadMetadataFile near the 'global-metadata.dat' string reference in Ghidra/IDA to understand runtime decryption, or rely on Unity TypeTree fallback for MonoBehaviour fields.");
                break;
            case Il2CppMetadataProtectionKind.InvalidOrTruncated:
                steps.Add("The target file is truncated or does not contain valid IL2CPP metadata. Verify file integrity or choose the correct project directory.");
                break;
        }

        if (symbolKind == Il2CppSymbolObfuscationKind.HeavilyObfuscated)
        {
            steps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Heavy symbol obfuscation detected ({obfuscatedRatio * 100.0:F1}% of game types). Use MonoBehaviour TypeTree fields, asset 'Used By' cross-references, and Ghidra RVA analysis to reconstruct original roles."));
        }
        else if (symbolKind == Il2CppSymbolObfuscationKind.PartiallyObfuscated)
        {
            steps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Partial symbol obfuscation detected ({obfuscatedRatio * 100.0:F1}% of game types). Unity lifecycle methods and serialized fields generally remain readable."));
        }

        if (binaryProbe != null && binaryProbe.MetadataStringOffsets.Count > 0)
        {
            var first = binaryProbe.MetadataStringOffsets[0];
            steps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"In Ghidra/IDA, check cross-references to 'global-metadata.dat' at file offset 0x{first.FileOffset:X} ({first.SectionName})."));
        }

        if (loadedTypeTreeCount > 0
            && protectionKind is Il2CppMetadataProtectionKind.HighEntropyEncrypted or Il2CppMetadataProtectionKind.HeaderShiftedOrCustomLayout)
        {
            steps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{loadedTypeTreeCount:N0} Unity TypeTree schema(s) are loaded and can reconstruct serialized MonoBehaviour fields even while metadata is unreadable."));
        }

        return steps;
    }

    private static string? ResolveExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadExactAt(Stream stream, long offset, int length)
    {
        if (length <= 0 || offset < 0 || offset >= stream.Length)
        {
            return Array.Empty<byte>();
        }

        var clamped = (int)Math.Min(length, stream.Length - offset);
        var buf = new byte[clamped];
        stream.Position = offset;
        var read = ReadUpTo(stream, buf, 0, clamped);
        if (read == clamped)
        {
            return buf;
        }

        var resized = new byte[read];
        Buffer.BlockCopy(buf, 0, resized, 0, read);
        return resized;
    }

    private static int ReadUpTo(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
