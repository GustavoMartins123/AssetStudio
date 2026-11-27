using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ExportOutcomeStatus
    {
        Completed,
        Partial,
        Failed,
        Cancelled
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ExportItemOutcome
    {
        Exported,
        Skipped,
        Partial,
        Failed,
        Cancelled
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ExportDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class ExportDiagnostic
    {
        public ExportDiagnosticSeverity Severity { get; init; } = ExportDiagnosticSeverity.Warning;
        public ExportItemOutcome Outcome { get; init; } = ExportItemOutcome.Skipped;
        public string Code { get; init; } = string.Empty;
        public string AssetName { get; init; } = string.Empty;
        public string AssetType { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string FieldOrResource { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? LimitKind { get; init; }
        public int? LimitValue { get; init; }
        public int? TotalCount { get; init; }
        public int? OmittedCount { get; init; }
    }

    public sealed class ExportBuildIdentity
    {
        public string Version { get; init; } = "0.20.0";
        public string CommitHash { get; init; } = string.Empty;
        public string BuildChannel { get; init; } = "dev";
        public int CommitsAhead { get; init; }

        public string DisplayVersion =>
            string.Equals(BuildChannel, "release", StringComparison.OrdinalIgnoreCase)
                ? Version
                : (!string.IsNullOrEmpty(CommitHash)
                    ? (CommitsAhead > 0 ? $"{Version}-dev.{CommitsAhead}+{CommitHash}" : $"{Version}-dev+{CommitHash}")
                    : $"{Version}-dev");

        public static ExportBuildIdentity Resolve(Assembly? assembly = null)
        {
            var asm = assembly ?? typeof(ExportBuildIdentity).Assembly;
            var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = NormalizeVersion(infoVersion, asm.GetName().Version);

            string commit = string.Empty;
            string channel = string.Empty;
            int commitsAhead = 0;

            foreach (var meta in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(meta.Key, "AssetStudioCommit", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(meta.Value))
                {
                    commit = meta.Value.Trim();
                }
                else if (string.Equals(meta.Key, "AssetStudioBuildChannel", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(meta.Value))
                {
                    channel = meta.Value.Trim().ToLowerInvariant();
                }
                else if (string.Equals(meta.Key, "AssetStudioCommitsAhead", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(meta.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ahead)
                    && ahead > 0)
                {
                    commitsAhead = ahead;
                }
            }

            if (string.IsNullOrEmpty(channel))
            {
                channel = string.IsNullOrEmpty(commit) ? "release" : "dev";
            }

            return new ExportBuildIdentity
            {
                Version = version,
                CommitHash = commit,
                BuildChannel = channel,
                CommitsAhead = commitsAhead
            };
        }

        private static string NormalizeVersion(string? infoVersion, Version? assemblyVersion)
        {
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                var trimmed = infoVersion.Trim();
                var plus = trimmed.IndexOf('+');
                if (plus >= 0)
                {
                    trimmed = trimmed[..plus].Trim();
                }
                if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1]))
                {
                    trimmed = trimmed[1..];
                }
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            if (assemblyVersion != null)
            {
                return assemblyVersion.Revision > 0
                    ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}"
                    : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
            }

            return "0.20.0";
        }
    }

    /// <summary>
    /// Shared structured export report contract used across GUI batch export, Unity project/prefab export,
    /// and AssetStudio.CLI so exported, skipped, partial, failed, and cancelled outcomes are never conflated.
    /// </summary>
    public sealed class ExportReport
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public int SchemaVersion { get; init; } = 1;
        public string Operation { get; init; } = string.Empty;
        public ExportOutcomeStatus Status { get; init; } = ExportOutcomeStatus.Completed;
        public string ExporterVersion { get; init; } = string.Empty;
        public string CommitHash { get; init; } = string.Empty;
        public string BuildChannel { get; init; } = string.Empty;
        public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        public long ElapsedMs { get; init; }
        public string InputPath { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public bool DryRun { get; init; }
        public SortedDictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);

        public int ExportedCount { get; init; }
        public int SkippedCount { get; init; }
        public int PartialCount { get; init; }
        public int FailedCount { get; init; }
        public int CancelledCount { get; init; }

        public List<string> MissingDependencies { get; init; } = new();
        public List<ExportDiagnostic> Diagnostics { get; init; } = new();

        public static ExportOutcomeStatus ComputeOutcomeStatus(
            int exportedCount,
            int skippedCount,
            int partialCount,
            int failedCount,
            int cancelledCount = 0,
            bool wasCancelled = false,
            bool hasFatalError = false)
        {
            if (wasCancelled || cancelledCount > 0)
            {
                return ExportOutcomeStatus.Cancelled;
            }

            if (hasFatalError || (failedCount > 0 && exportedCount == 0 && partialCount == 0))
            {
                return ExportOutcomeStatus.Failed;
            }

            if (failedCount > 0 || partialCount > 0)
            {
                return ExportOutcomeStatus.Partial;
            }

            _ = skippedCount;
            return ExportOutcomeStatus.Completed;
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public void WriteJson(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Report file path must not be empty.", nameof(filePath));
            }

            var fullPath = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmpPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tmpPath, ToJson(), Utf8NoBom);
                File.Move(tmpPath, fullPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
                catch
                {
                }
                throw;
            }
        }

        public string FormatSummaryLine()
        {
            var sb = new StringBuilder();
            sb.Append(DryRun ? "Dry-run " : "Export ");
            sb.Append(Status.ToString().ToLowerInvariant());
            sb.Append(": ");
            sb.Append(ExportedCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(DryRun ? " planned" : " exported");
            sb.Append(", ");
            sb.Append(SkippedCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(" skipped");
            if (PartialCount > 0)
            {
                sb.Append(", ");
                sb.Append(PartialCount.ToString(CultureInfo.InvariantCulture));
                sb.Append(" partial");
            }
            if (FailedCount > 0)
            {
                sb.Append(", ");
                sb.Append(FailedCount.ToString(CultureInfo.InvariantCulture));
                sb.Append(" failed");
            }
            if (CancelledCount > 0)
            {
                sb.Append(", ");
                sb.Append(CancelledCount.ToString(CultureInfo.InvariantCulture));
                sb.Append(" cancelled");
            }
            if (MissingDependencies.Count > 0)
            {
                sb.Append(" (");
                sb.Append(MissingDependencies.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(" missing dependencies)");
            }
            sb.Append('.');
            return sb.ToString();
        }
    }
}
