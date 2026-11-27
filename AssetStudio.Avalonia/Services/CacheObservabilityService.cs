#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AssetStudio;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia.Services
{
    public enum CacheFailureKind
    {
        None,
        DiskFull,
        ReadOrAccessFailure,
        Corruption,
        InterruptedIndexing,
        StaleSignature,
        BoundaryViolation,
        Unknown
    }

    public enum CacheHealthStatus
    {
        Healthy,
        Empty,
        Degraded,
        Corrupt,
        Inaccessible
    }

    public sealed record CacheFailureDiagnostic(
        CacheFailureKind Kind,
        bool IsRecoverableByCacheRebuild,
        int? ErrorCode,
        string ExceptionType,
        string Summary,
        string RecommendedAction);

    public sealed record CacheDiagnosticEvent(
        DateTime TimestampUtc,
        CacheFailureKind FailureKind,
        string Category,
        string TargetPath,
        string Message,
        bool RecoveredAutomatically,
        string? ExceptionType = null,
        int? ErrorCode = null);

    public sealed record CacheCategoryUsage(
        string Category,
        string RootDirectory,
        int FileCount,
        long TotalBytes);

    public sealed record CacheDatabaseHealthEntry(
        string DatabasePath,
        string SidecarPath,
        long DatabaseBytes,
        long SidecarBytes,
        int PreviewEntryCount,
        long PreviewEntryBytes,
        CacheHealthStatus Status,
        bool IsCorrupt,
        bool IsSidecarCorrupt,
        bool IsInterruptedIndexing,
        string? IndexingStatus,
        string? DiagnosticMessage);

    public sealed class CacheDiskUsageReport
    {
        public DateTime InspectedUtc { get; init; } = DateTime.UtcNow;
        public string IndexCacheRoot { get; init; } = string.Empty;
        public string PreviewCacheRoot { get; init; } = string.Empty;
        public string DecompressedCacheRoot { get; init; } = string.Empty;
        public CacheCategoryUsage SqliteIndexUsage { get; init; } = new("SqliteIndex", string.Empty, 0, 0);
        public CacheCategoryUsage MmapSidecarUsage { get; init; } = new("MmapSidecar", string.Empty, 0, 0);
        public CacheCategoryUsage PreviewPayloadUsage { get; init; } = new("PreviewPayloads", string.Empty, 0, 0);
        public CacheCategoryUsage DecompressedBundleUsage { get; init; } = new("DecompressedBundles", string.Empty, 0, 0);
        public CacheCategoryUsage OrphanTempUsage { get; init; } = new("OrphanTempFiles", string.Empty, 0, 0);
        public int PreviewDatabaseEntryCount { get; init; }
        public long PreviewDatabaseInlineBytes { get; init; }
        public int CorruptDecompressedBundleCount { get; init; }
        public int TotalFileCount { get; init; }
        public long TotalBytes { get; init; }
        public CacheHealthStatus OverallHealth { get; init; } = CacheHealthStatus.Empty;
        public bool HasCorruption { get; init; }
        public bool HasInterruptedIndexing { get; init; }
        public IReadOnlyList<CacheDatabaseHealthEntry> Databases { get; init; } = Array.Empty<CacheDatabaseHealthEntry>();
        public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
        public string HumanReadableSummary { get; init; } = string.Empty;
    }

    public sealed class CacheRecoveryReport
    {
        public bool Success { get; init; }
        public int RecoveredCorruptDatabases { get; init; }
        public int RemovedCorruptSidecars { get; init; }
        public int ResetInterruptedIndexStates { get; init; }
        public int RemovedCorruptDecompressedEntries { get; init; }
        public int RemovedOrphanTempFiles { get; init; }
        public long ReclaimedBytes { get; init; }
        public IReadOnlyList<CacheDiagnosticEvent> Events { get; init; } = Array.Empty<CacheDiagnosticEvent>();
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class SafeCacheClearResult
    {
        public bool Success { get; init; }
        public int DeletedFileCount { get; init; }
        public int DeletedDirectoryCount { get; init; }
        public int SkippedCount { get; init; }
        public int RejectedUnsafePathCount { get; init; }
        public long ReclaimedBytes { get; init; }
        public IReadOnlyList<CacheDiagnosticEvent> Diagnostics { get; init; } = Array.Empty<CacheDiagnosticEvent>();
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// Provides disk-usage observability, exception classification, integrity inspection, automatic recovery,
    /// and strictly confined cleanup across AssetStudio's SQLite index, mmap sidecar, preview, and
    /// decompressed bundle caches without ever touching source project files.
    /// </summary>
    public sealed class CacheObservabilityService
    {
        private const int Win32ErrorDiskFullHResult = unchecked((int)0x80070070);
        private const int Win32ErrorHandleDiskFullHResult = unchecked((int)0x80070027);
        private const int PosixErrorNoSpace = 28;
        private const int SqliteErrorGeneric = 1;
        private const int SqliteErrorPerm = 3;
        private const int SqliteErrorBusy = 5;
        private const int SqliteErrorLocked = 6;
        private const int SqliteErrorReadOnly = 8;
        private const int SqliteErrorIoErr = 10;
        private const int SqliteErrorCorrupt = 11;
        private const int SqliteErrorFull = 13;
        private const int SqliteErrorCantOpen = 14;
        private const int SqliteErrorProtocol = 15;
        private const int SqliteErrorFormat = 24;
        private const int SqliteErrorNotADb = 26;
        private const int SqliteExtendedIoErrShortRead = 522;
        private const int SqliteExtendedIoErrCorruptFs = 7434;

        private readonly bool _caseSensitivePaths;
        private readonly StringComparison _pathComparison;

        public CacheObservabilityService(
            string? indexCacheRoot = null,
            string? previewCacheRoot = null,
            string? decompressedCacheRoot = null,
            bool? caseSensitivePaths = null)
        {
            _caseSensitivePaths = caseSensitivePaths ?? OperatingSystem.IsLinux();
            _pathComparison = _caseSensitivePaths ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            IndexCacheRoot = NormalizeRootPath(string.IsNullOrWhiteSpace(indexCacheRoot)
                ? GetDefaultIndexCacheRoot()
                : indexCacheRoot);
            PreviewCacheRoot = NormalizeRootPath(string.IsNullOrWhiteSpace(previewCacheRoot)
                ? GetDefaultPreviewCacheRoot()
                : previewCacheRoot);
            DecompressedCacheRoot = NormalizeRootPath(string.IsNullOrWhiteSpace(decompressedCacheRoot)
                ? DecompressedBundleCache.GetDefaultCacheBaseDirectory()
                : decompressedCacheRoot);
        }

        public string IndexCacheRoot { get; }
        public string PreviewCacheRoot { get; }
        public string DecompressedCacheRoot { get; }

        public static string GetDefaultIndexCacheRoot()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "AssetStudio", "IndexCache");
            }

            return Path.Combine(Path.GetTempPath(), "AssetStudio", "IndexCache");
        }

        public static string GetDefaultPreviewCacheRoot()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "AssetStudio", "PreviewCache");
            }

            return Path.Combine(Path.GetTempPath(), "AssetStudio", "PreviewCache");
        }

        public static CacheFailureDiagnostic ClassifyException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            var current = UnwrapException(exception);
            var typeName = current.GetType().Name;

            if (IsDiskFullException(current, out var diskFullCode))
            {
                return new CacheFailureDiagnostic(
                    CacheFailureKind.DiskFull,
                    IsRecoverableByCacheRebuild: false,
                    ErrorCode: diskFullCode,
                    ExceptionType: typeName,
                    Summary: $"Insufficient disk space on cache volume: {current.Message}",
                    RecommendedAction: "Free disk space on the cache drive or clear unused AssetStudio cache files.");
            }

            if (current is SqliteException sqliteEx)
            {
                if (IsSqliteCorruption(sqliteEx))
                {
                    return new CacheFailureDiagnostic(
                        CacheFailureKind.Corruption,
                        IsRecoverableByCacheRebuild: true,
                        ErrorCode: sqliteEx.SqliteErrorCode,
                        ExceptionType: typeName,
                        Summary: $"SQLite cache database corruption detected (code {sqliteEx.SqliteErrorCode}): {sqliteEx.Message}",
                        RecommendedAction: "Remove the damaged cache files and rebuild the cache automatically from source assets.");
                }

                if (sqliteEx.SqliteErrorCode is SqliteErrorPerm or SqliteErrorBusy or SqliteErrorLocked or SqliteErrorReadOnly or SqliteErrorIoErr or SqliteErrorCantOpen)
                {
                    return new CacheFailureDiagnostic(
                        CacheFailureKind.ReadOrAccessFailure,
                        IsRecoverableByCacheRebuild: false,
                        ErrorCode: sqliteEx.SqliteErrorCode,
                        ExceptionType: typeName,
                        Summary: $"SQLite cache read/access error (code {sqliteEx.SqliteErrorCode}): {sqliteEx.Message}",
                        RecommendedAction: "Verify cache folder permissions, file locks, or storage device health.");
                }
            }

            if (current is InvalidDataException)
            {
                return new CacheFailureDiagnostic(
                    CacheFailureKind.Corruption,
                    IsRecoverableByCacheRebuild: true,
                    ErrorCode: current.HResult,
                    ExceptionType: typeName,
                    Summary: $"Corrupt or truncated cache payload: {current.Message}",
                    RecommendedAction: "Discard the damaged cache entry and regenerate it from the source asset.");
            }

            if (current is UnauthorizedAccessException or IOException)
            {
                return new CacheFailureDiagnostic(
                    CacheFailureKind.ReadOrAccessFailure,
                    IsRecoverableByCacheRebuild: false,
                    ErrorCode: current.HResult,
                    ExceptionType: typeName,
                    Summary: $"Cache file read or access failure: {current.Message}",
                    RecommendedAction: "Check file permissions, antivirus locks, and storage connectivity for the cache directory.");
            }

            return new CacheFailureDiagnostic(
                CacheFailureKind.Unknown,
                IsRecoverableByCacheRebuild: false,
                ErrorCode: current.HResult,
                ExceptionType: typeName,
                Summary: current.Message,
                RecommendedAction: "Inspect diagnostic logs and verify the cache directory state.");
        }

        internal static bool IsDiskFullException(Exception exception, out int? errorCode)
        {
            errorCode = null;
            for (Exception? cursor = exception; cursor != null; cursor = cursor.InnerException)
            {
                if (cursor is SqliteException sqliteEx
                    && (sqliteEx.SqliteErrorCode == SqliteErrorFull || sqliteEx.SqliteExtendedErrorCode == SqliteErrorFull))
                {
                    errorCode = sqliteEx.SqliteErrorCode;
                    return true;
                }

                if (cursor is IOException ioEx)
                {
                    var hr = ioEx.HResult;
                    var low16 = hr & 0xFFFF;
                    if (hr == Win32ErrorDiskFullHResult
                        || hr == Win32ErrorHandleDiskFullHResult
                        || hr == PosixErrorNoSpace
                        || low16 == 0x0070
                        || low16 == 0x0027
                        || (!string.IsNullOrEmpty(ioEx.Message)
                            && (ioEx.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                                || ioEx.Message.Contains("no space left", StringComparison.OrdinalIgnoreCase)
                                || ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase))))
                    {
                        errorCode = hr;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool IsSqliteCorruption(SqliteException ex)
        {
            if (ex == null)
            {
                return false;
            }

            if (ex.SqliteErrorCode is SqliteErrorCorrupt or SqliteErrorNotADb or SqliteErrorFormat or SqliteErrorProtocol)
            {
                return true;
            }

            if (ex.SqliteErrorCode == SqliteErrorIoErr
                && (ex.SqliteExtendedErrorCode == SqliteExtendedIoErrShortRead
                    || ex.SqliteExtendedErrorCode == SqliteExtendedIoErrCorruptFs
                    || ContainsCorruptionPhrase(ex.Message)))
            {
                return true;
            }

            if (ex.SqliteErrorCode == SqliteErrorGeneric && ContainsCorruptionPhrase(ex.Message))
            {
                return true;
            }

            return false;
        }

        private static bool ContainsCorruptionPhrase(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                || message.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("corrupt", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that <paramref name="candidatePath"/> resolves strictly inside <paramref name="cacheRoot"/>,
        /// rejecting parent traversals, sibling-prefix tricks, filesystem roots, and symlinks/reparse points.
        /// </summary>
        public static bool IsPathStrictlyInsideCacheRoot(
            string cacheRoot,
            string candidatePath,
            bool allowEqualRoot = false,
            bool? caseSensitivePaths = null)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            try
            {
                var caseSensitive = caseSensitivePaths ?? OperatingSystem.IsLinux();
                var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                var fullRoot = SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(Path.GetFullPath(cacheRoot));
                var fsRoot = Path.GetPathRoot(fullRoot);
                if (!string.IsNullOrEmpty(fsRoot))
                {
                    var normalizedFsRoot = SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(fsRoot);
                    if (string.Equals(fullRoot, normalizedFsRoot, comparison))
                    {
                        return false;
                    }
                }

                var fullCandidate = SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(Path.GetFullPath(candidatePath));
                if (string.Equals(fullRoot, fullCandidate, comparison))
                {
                    return allowEqualRoot && !IsReparsePointOrSymlink(fullRoot);
                }

                var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                    ? fullRoot
                    : fullRoot + Path.DirectorySeparatorChar;
                if (!fullCandidate.StartsWith(rootPrefix, comparison))
                {
                    return false;
                }

                if (IsReparsePointOrSymlink(fullRoot))
                {
                    return false;
                }

                // Walk every existing segment inside cacheRoot down to candidatePath to guarantee no reparse point/symlink escapes root.
                var relative = fullCandidate.Substring(rootPrefix.Length);
                var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                var current = fullRoot;
                for (var i = 0; i < segments.Length; i++)
                {
                    current = Path.Combine(current, segments[i]);
                    if (IsReparsePointOrSymlink(current))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public CacheDiskUsageReport InspectDiskUsageAndHealth(SQLiteProjectIndexCache? sqliteCache = null)
        {
            var effectiveIndexRoot = sqliteCache != null
                ? NormalizeRootPath(sqliteCache.CacheDirectory)
                : IndexCacheRoot;

            var sqliteFileCount = 0;
            long sqliteTotalBytes = 0;
            var sidecarFileCount = 0;
            long sidecarTotalBytes = 0;
            var previewPayloadFileCount = 0;
            long previewPayloadTotalBytes = 0;
            var decompressedFileCount = 0;
            long decompressedTotalBytes = 0;
            var orphanTempFileCount = 0;
            long orphanTempTotalBytes = 0;
            var previewDbEntryCount = 0;
            long previewDbInlineBytes = 0;
            var corruptDecompressedCount = 0;

            var databaseEntries = new List<CacheDatabaseHealthEntry>();
            var issues = new List<string>();
            var anyCorruption = false;
            var anyInterrupted = false;
            var anyInaccessible = false;

            // 1. Inspect IndexCacheRoot (SQLite databases, WAL/SHM/journal files, .idx sidecars, inline preview payload folder, orphan .tmp files)
            if (Directory.Exists(effectiveIndexRoot))
            {
                try
                {
                    var dbPaths = new HashSet<string>(_caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

                    foreach (var file in EnumerateFilesSafely(effectiveIndexRoot))
                    {
                        var fileName = Path.GetFileName(file);
                        var fileLen = TryGetFileLength(file);

                        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            orphanTempFileCount++;
                            orphanTempTotalBytes += fileLen;
                            continue;
                        }

                        var relativeToIndex = Path.GetRelativePath(effectiveIndexRoot, file);
                        if (relativeToIndex.StartsWith("PreviewPayloads" + Path.DirectorySeparatorChar, _pathComparison)
                            || relativeToIndex.StartsWith("PreviewPayloads" + Path.AltDirectorySeparatorChar, _pathComparison))
                        {
                            previewPayloadFileCount++;
                            previewPayloadTotalBytes += fileLen;
                            continue;
                        }

                        if (fileName.EndsWith(".idx", StringComparison.OrdinalIgnoreCase))
                        {
                            sidecarFileCount++;
                            sidecarTotalBytes += fileLen;
                            continue;
                        }

                        if (fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                        {
                            sqliteFileCount++;
                            sqliteTotalBytes += fileLen;
                            if (fileName.StartsWith("project_index_", StringComparison.OrdinalIgnoreCase))
                            {
                                dbPaths.Add(file);
                            }
                            continue;
                        }

                        if (fileName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
                            || fileName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
                            || fileName.EndsWith(".db-journal", StringComparison.OrdinalIgnoreCase))
                        {
                            sqliteFileCount++;
                            sqliteTotalBytes += fileLen;
                            continue;
                        }
                    }

                    foreach (var dbPath in dbPaths.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        var entry = InspectSingleDatabase(dbPath);
                        databaseEntries.Add(entry);
                        previewDbEntryCount += entry.PreviewEntryCount;
                        previewDbInlineBytes += entry.PreviewEntryBytes;

                        if (entry.IsCorrupt || entry.IsSidecarCorrupt)
                        {
                            anyCorruption = true;
                        }

                        if (entry.IsInterruptedIndexing)
                        {
                            anyInterrupted = true;
                        }

                        if (entry.Status == CacheHealthStatus.Inaccessible)
                        {
                            anyInaccessible = true;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.DiagnosticMessage))
                        {
                            issues.Add($"{Path.GetFileName(dbPath)}: {entry.DiagnosticMessage}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    anyInaccessible = true;
                    issues.Add($"Unable to inspect index cache directory '{effectiveIndexRoot}': {ClassifyException(ex).Summary}");
                }
            }

            // 2. Inspect PreviewCacheRoot
            if (Directory.Exists(PreviewCacheRoot))
            {
                try
                {
                    foreach (var file in EnumerateFilesSafely(PreviewCacheRoot))
                    {
                        var fileLen = TryGetFileLength(file);
                        if (Path.GetFileName(file).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            orphanTempFileCount++;
                            orphanTempTotalBytes += fileLen;
                        }
                        else
                        {
                            previewPayloadFileCount++;
                            previewPayloadTotalBytes += fileLen;
                        }
                    }
                }
                catch (Exception ex)
                {
                    anyInaccessible = true;
                    issues.Add($"Unable to inspect preview cache directory '{PreviewCacheRoot}': {ClassifyException(ex).Summary}");
                }
            }

            // 3. Inspect DecompressedCacheRoot
            if (Directory.Exists(DecompressedCacheRoot))
            {
                try
                {
                    InspectDecompressedCacheDirectory(
                        DecompressedCacheRoot,
                        ref decompressedFileCount,
                        ref decompressedTotalBytes,
                        ref orphanTempFileCount,
                        ref orphanTempTotalBytes,
                        out var brokenBundleDirs,
                        issues);
                    corruptDecompressedCount = brokenBundleDirs.Count;
                    if (corruptDecompressedCount > 0)
                    {
                        anyCorruption = true;
                    }
                }
                catch (Exception ex)
                {
                    anyInaccessible = true;
                    issues.Add($"Unable to inspect decompressed bundle cache directory '{DecompressedCacheRoot}': {ClassifyException(ex).Summary}");
                }
            }

            if (orphanTempFileCount > 0)
            {
                issues.Add($"Found {orphanTempFileCount:N0} orphaned temporary cache file(s) ({FormatBytes(orphanTempTotalBytes)}).");
            }

            var totalFiles = sqliteFileCount + sidecarFileCount + previewPayloadFileCount + decompressedFileCount + orphanTempFileCount;
            var totalBytes = sqliteTotalBytes + sidecarTotalBytes + previewPayloadTotalBytes + decompressedTotalBytes + orphanTempTotalBytes;

            var overallHealth = anyInaccessible
                ? CacheHealthStatus.Inaccessible
                : anyCorruption
                    ? CacheHealthStatus.Corrupt
                    : anyInterrupted || orphanTempFileCount > 0
                        ? CacheHealthStatus.Degraded
                        : totalFiles == 0
                            ? CacheHealthStatus.Empty
                            : CacheHealthStatus.Healthy;

            var summary = BuildDiskUsageSummary(
                overallHealth,
                totalFiles,
                totalBytes,
                sqliteTotalBytes,
                sidecarTotalBytes,
                previewPayloadTotalBytes,
                previewDbEntryCount,
                previewDbInlineBytes,
                decompressedTotalBytes,
                orphanTempFileCount,
                orphanTempTotalBytes,
                issues.Count);

            return new CacheDiskUsageReport
            {
                InspectedUtc = DateTime.UtcNow,
                IndexCacheRoot = effectiveIndexRoot,
                PreviewCacheRoot = PreviewCacheRoot,
                DecompressedCacheRoot = DecompressedCacheRoot,
                SqliteIndexUsage = new CacheCategoryUsage("SqliteIndex", effectiveIndexRoot, sqliteFileCount, sqliteTotalBytes),
                MmapSidecarUsage = new CacheCategoryUsage("MmapSidecar", effectiveIndexRoot, sidecarFileCount, sidecarTotalBytes),
                PreviewPayloadUsage = new CacheCategoryUsage("PreviewPayloads", PreviewCacheRoot, previewPayloadFileCount, previewPayloadTotalBytes),
                DecompressedBundleUsage = new CacheCategoryUsage("DecompressedBundles", DecompressedCacheRoot, decompressedFileCount, decompressedTotalBytes),
                OrphanTempUsage = new CacheCategoryUsage("OrphanTempFiles", effectiveIndexRoot, orphanTempFileCount, orphanTempTotalBytes),
                PreviewDatabaseEntryCount = previewDbEntryCount,
                PreviewDatabaseInlineBytes = previewDbInlineBytes,
                CorruptDecompressedBundleCount = corruptDecompressedCount,
                TotalFileCount = totalFiles,
                TotalBytes = totalBytes,
                OverallHealth = overallHealth,
                HasCorruption = anyCorruption,
                HasInterruptedIndexing = anyInterrupted,
                Databases = databaseEntries,
                Issues = issues,
                HumanReadableSummary = summary
            };
        }

        public CacheRecoveryReport InspectAndRecoverCache(SQLiteProjectIndexCache? sqliteCache = null)
        {
            var effectiveIndexRoot = sqliteCache != null
                ? NormalizeRootPath(sqliteCache.CacheDirectory)
                : IndexCacheRoot;

            var events = new List<CacheDiagnosticEvent>();
            var recoveredCorruptDbs = 0;
            var removedCorruptSidecars = 0;
            var resetInterruptedStates = 0;
            var removedCorruptDecompressed = 0;
            var removedOrphanTemps = 0;
            long reclaimedBytes = 0;
            var success = true;

            // 1. Inspect & recover SQLite index DBs and sidecars strictly inside effectiveIndexRoot
            if (Directory.Exists(effectiveIndexRoot))
            {
                sqliteCache?.ReleaseReadConnections();
                SqliteConnection.ClearAllPools();

                string[] dbFiles;
                try
                {
                    dbFiles = Directory.GetFiles(effectiveIndexRoot, "project_index_*.db", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    success = false;
                    var diag = ClassifyException(ex);
                    events.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        diag.Kind,
                        "SqliteIndex",
                        effectiveIndexRoot,
                        diag.Summary,
                        RecoveredAutomatically: false,
                        ExceptionType: diag.ExceptionType,
                        ErrorCode: diag.ErrorCode));
                    dbFiles = Array.Empty<string>();
                }

                foreach (var dbPath in dbFiles)
                {
                    if (!IsPathStrictlyInsideCacheRoot(effectiveIndexRoot, dbPath, allowEqualRoot: false, _caseSensitivePaths))
                    {
                        events.Add(new CacheDiagnosticEvent(
                            DateTime.UtcNow,
                            CacheFailureKind.BoundaryViolation,
                            "SqliteIndex",
                            dbPath,
                            "Skipped database file outside configured index cache root.",
                            RecoveredAutomatically: false));
                        continue;
                    }

                    var health = InspectSingleDatabase(dbPath);
                    if (health.IsCorrupt)
                    {
                        SqliteConnection.ClearAllPools();
                        var removedBytes = DeleteDatabaseAndSidecarFilesConfined(effectiveIndexRoot, dbPath, out var deletedAny);
                        if (deletedAny)
                        {
                            recoveredCorruptDbs++;
                            reclaimedBytes += removedBytes;
                            sqliteCache?.ForgetInitializedDatabase(dbPath);
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                CacheFailureKind.Corruption,
                                "SqliteIndex",
                                dbPath,
                                $"Removed corrupt SQLite index database and sidecar ({FormatBytes(removedBytes)}): {health.DiagnosticMessage}",
                                RecoveredAutomatically: true));
                        }
                        else
                        {
                            success = false;
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                CacheFailureKind.ReadOrAccessFailure,
                                "SqliteIndex",
                                dbPath,
                                $"Unable to remove corrupt SQLite index database: {health.DiagnosticMessage}",
                                RecoveredAutomatically: false));
                        }

                        continue;
                    }

                    if (health.IsSidecarCorrupt && File.Exists(health.SidecarPath))
                    {
                        var bytes = TryDeleteSingleFileConfined(effectiveIndexRoot, health.SidecarPath, _caseSensitivePaths, out var deleted);
                        if (deleted)
                        {
                            removedCorruptSidecars++;
                            reclaimedBytes += bytes;
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                CacheFailureKind.Corruption,
                                "MmapSidecar",
                                health.SidecarPath,
                                $"Removed corrupt or mismatched mmap sidecar ({FormatBytes(bytes)}).",
                                RecoveredAutomatically: true));
                        }
                    }

                    if (health.IsInterruptedIndexing)
                    {
                        if (TryResetInterruptedDatabaseState(effectiveIndexRoot, dbPath, _caseSensitivePaths, out var sidecarBytes))
                        {
                            resetInterruptedStates++;
                            reclaimedBytes += sidecarBytes;
                            sqliteCache?.InvalidateReadConnectionPool(dbPath);
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                CacheFailureKind.InterruptedIndexing,
                                "SqliteIndex",
                                dbPath,
                                $"Recovered interrupted indexing state ('{health.IndexingStatus}') and cleared partial index rows.",
                                RecoveredAutomatically: true));
                        }
                    }
                }

                // Clean up orphaned .idx files that have no paired .db file
                try
                {
                    foreach (var idxPath in Directory.GetFiles(effectiveIndexRoot, "project_index_*.idx", SearchOption.TopDirectoryOnly))
                    {
                        var expectedDb = Path.ChangeExtension(idxPath, ".db");
                        if (!File.Exists(expectedDb))
                        {
                            var bytes = TryDeleteSingleFileConfined(effectiveIndexRoot, idxPath, _caseSensitivePaths, out var deleted);
                            if (deleted)
                            {
                                removedCorruptSidecars++;
                                reclaimedBytes += bytes;
                                events.Add(new CacheDiagnosticEvent(
                                    DateTime.UtcNow,
                                    CacheFailureKind.Corruption,
                                    "MmapSidecar",
                                    idxPath,
                                    $"Removed orphaned mmap sidecar without a backing SQLite database ({FormatBytes(bytes)}).",
                                    RecoveredAutomatically: true));
                            }
                        }
                    }
                }
                catch
                {
                    // Best-effort orphan sidecar cleanup
                }
            }

            // 2. Inspect & remove corrupt/incomplete decompressed bundle directories strictly inside DecompressedCacheRoot
            if (Directory.Exists(DecompressedCacheRoot))
            {
                try
                {
                    var dummyFiles = 0;
                    long dummyBytes = 0;
                    var dummyTmpFiles = 0;
                    long dummyTmpBytes = 0;
                    var dummyIssues = new List<string>();
                    InspectDecompressedCacheDirectory(
                        DecompressedCacheRoot,
                        ref dummyFiles,
                        ref dummyBytes,
                        ref dummyTmpFiles,
                        ref dummyTmpBytes,
                        out var brokenBundleDirs,
                        dummyIssues);

                    foreach (var brokenDir in brokenBundleDirs)
                    {
                        if (!IsPathStrictlyInsideCacheRoot(DecompressedCacheRoot, brokenDir, allowEqualRoot: false, _caseSensitivePaths))
                        {
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                CacheFailureKind.BoundaryViolation,
                                "DecompressedBundleCache",
                                brokenDir,
                                "Refused to remove decompressed cache directory outside cache root.",
                                RecoveredAutomatically: false));
                            continue;
                        }

                        var dirBytes = MeasureDirectoryBytesSafely(brokenDir);
                        try
                        {
                            Directory.Delete(brokenDir, recursive: true);
                            removedCorruptDecompressed++;
                            reclaimedBytes += dirBytes;
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                CacheFailureKind.Corruption,
                                "DecompressedBundleCache",
                                brokenDir,
                                $"Removed incomplete or corrupt decompressed bundle cache entry ({FormatBytes(dirBytes)}).",
                                RecoveredAutomatically: true));
                        }
                        catch (Exception ex)
                        {
                            success = false;
                            var diag = ClassifyException(ex);
                            events.Add(new CacheDiagnosticEvent(
                                DateTime.UtcNow,
                                diag.Kind,
                                "DecompressedBundleCache",
                                brokenDir,
                                diag.Summary,
                                RecoveredAutomatically: false,
                                ExceptionType: diag.ExceptionType,
                                ErrorCode: diag.ErrorCode));
                        }
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    var diag = ClassifyException(ex);
                    events.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        diag.Kind,
                        "DecompressedBundleCache",
                        DecompressedCacheRoot,
                        diag.Summary,
                        RecoveredAutomatically: false,
                        ExceptionType: diag.ExceptionType,
                        ErrorCode: diag.ErrorCode));
                }
            }

            // 3. Remove orphaned *.tmp files across all configured cache roots
            foreach (var root in new[] { effectiveIndexRoot, PreviewCacheRoot, DecompressedCacheRoot }.Distinct(_caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafely(root))
                {
                    if (!Path.GetFileName(file).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var bytes = TryDeleteSingleFileConfined(root, file, _caseSensitivePaths, out var deleted);
                    if (deleted)
                    {
                        removedOrphanTemps++;
                        reclaimedBytes += bytes;
                    }
                }
            }

            if (removedOrphanTemps > 0)
            {
                events.Add(new CacheDiagnosticEvent(
                    DateTime.UtcNow,
                    CacheFailureKind.Corruption,
                    "OrphanTempFiles",
                    effectiveIndexRoot,
                    $"Removed {removedOrphanTemps:N0} orphaned temporary cache file(s).",
                    RecoveredAutomatically: true));
            }

            var totalActions = recoveredCorruptDbs + removedCorruptSidecars + resetInterruptedStates + removedCorruptDecompressed + removedOrphanTemps;
            var summary = totalActions == 0
                ? "Cache inspection completed with no repairs needed."
                : $"Recovered caches: {recoveredCorruptDbs} corrupt DB(s), {removedCorruptSidecars} sidecar(s), {resetInterruptedStates} interrupted index(es), {removedCorruptDecompressed} broken bundle cache(s), {removedOrphanTemps} temp file(s); reclaimed {FormatBytes(reclaimedBytes)}.";

            return new CacheRecoveryReport
            {
                Success = success,
                RecoveredCorruptDatabases = recoveredCorruptDbs,
                RemovedCorruptSidecars = removedCorruptSidecars,
                ResetInterruptedIndexStates = resetInterruptedStates,
                RemovedCorruptDecompressedEntries = removedCorruptDecompressed,
                RemovedOrphanTempFiles = removedOrphanTemps,
                ReclaimedBytes = reclaimedBytes,
                Events = events,
                Summary = summary
            };
        }

        public SafeCacheClearResult SafeClearCache(
            SQLiteProjectIndexCache? sqliteCache = null,
            bool clearIndexCache = true,
            bool clearPreviewCache = true,
            bool clearDecompressedCache = true)
        {
            var effectiveIndexRoot = sqliteCache != null
                ? NormalizeRootPath(sqliteCache.CacheDirectory)
                : IndexCacheRoot;

            if (clearIndexCache)
            {
                sqliteCache?.ReleaseReadConnections();
                SqliteConnection.ClearAllPools();
            }

            var deletedFiles = 0;
            var deletedDirs = 0;
            var skipped = 0;
            var rejected = 0;
            long reclaimedBytes = 0;
            var diagnostics = new List<CacheDiagnosticEvent>();

            var rootsToClear = new List<(string Name, string Path)>();
            if (clearIndexCache)
            {
                rootsToClear.Add(("IndexCache", effectiveIndexRoot));
            }
            if (clearPreviewCache)
            {
                rootsToClear.Add(("PreviewCache", PreviewCacheRoot));
            }
            if (clearDecompressedCache)
            {
                rootsToClear.Add(("DecompressedCache", DecompressedCacheRoot));
            }

            foreach (var (name, root) in rootsToClear)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                if (!IsPathStrictlyInsideCacheRoot(root, root, allowEqualRoot: true, _caseSensitivePaths))
                {
                    rejected++;
                    diagnostics.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        CacheFailureKind.BoundaryViolation,
                        name,
                        root,
                        $"Refused to clear unsafe cache root '{root}'.",
                        RecoveredAutomatically: false));
                    continue;
                }

                ClearDirectoryContentsConfined(
                    root,
                    root,
                    name,
                    ref deletedFiles,
                    ref deletedDirs,
                    ref skipped,
                    ref rejected,
                    ref reclaimedBytes,
                    diagnostics);
            }

            if (clearIndexCache)
            {
                sqliteCache?.ClearInitializedDatabaseTracking();
            }

            var success = rejected == 0 && skipped == 0;
            var summary = $"Safely cleared {deletedFiles:N0} cache file(s) and {deletedDirs:N0} folder(s) ({FormatBytes(reclaimedBytes)} freed)"
                + (rejected > 0 ? $"; rejected {rejected:N0} out-of-bounds path(s)" : string.Empty)
                + (skipped > 0 ? $"; skipped {skipped:N0} locked item(s)" : string.Empty)
                + ".";

            return new SafeCacheClearResult
            {
                Success = success,
                DeletedFileCount = deletedFiles,
                DeletedDirectoryCount = deletedDirs,
                SkippedCount = skipped,
                RejectedUnsafePathCount = rejected,
                ReclaimedBytes = reclaimedBytes,
                Diagnostics = diagnostics,
                Summary = summary
            };
        }

        /// <summary>
        /// Safely deletes explicit cache target paths only if every path resolves strictly inside
        /// <paramref name="allowedCacheRoot"/> without crossing symlinks, reparse points, or directory boundaries.
        /// Any path outside <paramref name="allowedCacheRoot"/> is rejected untouched and logged as a boundary violation.
        /// </summary>
        public SafeCacheClearResult SafeClearPaths(
            string allowedCacheRoot,
            IEnumerable<string> targetPaths,
            SQLiteProjectIndexCache? sqliteCache = null)
        {
            ArgumentNullException.ThrowIfNull(targetPaths);

            if (string.IsNullOrWhiteSpace(allowedCacheRoot))
            {
                throw new ArgumentException("Allowed cache root must not be empty.", nameof(allowedCacheRoot));
            }

            sqliteCache?.ReleaseReadConnections();
            SqliteConnection.ClearAllPools();

            var normalizedRoot = NormalizeRootPath(allowedCacheRoot);
            var deletedFiles = 0;
            var deletedDirs = 0;
            var skipped = 0;
            var rejected = 0;
            long reclaimedBytes = 0;
            var diagnostics = new List<CacheDiagnosticEvent>();

            foreach (var rawTarget in targetPaths)
            {
                if (string.IsNullOrWhiteSpace(rawTarget))
                {
                    continue;
                }

                if (!IsPathStrictlyInsideCacheRoot(normalizedRoot, rawTarget, allowEqualRoot: false, _caseSensitivePaths))
                {
                    rejected++;
                    diagnostics.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        CacheFailureKind.BoundaryViolation,
                        "CacheCleanup",
                        rawTarget,
                        $"Refused to delete path outside configured cache root '{normalizedRoot}'.",
                        RecoveredAutomatically: false));
                    continue;
                }

                var fullTarget = Path.GetFullPath(rawTarget);
                if (File.Exists(fullTarget))
                {
                    var bytes = TryDeleteSingleFileConfined(normalizedRoot, fullTarget, _caseSensitivePaths, out var deleted, diagnostics);
                    if (deleted)
                    {
                        deletedFiles++;
                        reclaimedBytes += bytes;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else if (Directory.Exists(fullTarget))
                {
                    ClearDirectoryContentsConfined(
                        normalizedRoot,
                        fullTarget,
                        "CacheCleanup",
                        ref deletedFiles,
                        ref deletedDirs,
                        ref skipped,
                        ref rejected,
                        ref reclaimedBytes,
                        diagnostics);

                    try
                    {
                        if (Directory.Exists(fullTarget) && !Directory.EnumerateFileSystemEntries(fullTarget).Any())
                        {
                            Directory.Delete(fullTarget, recursive: false);
                            deletedDirs++;
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        var diag = ClassifyException(ex);
                        diagnostics.Add(new CacheDiagnosticEvent(
                            DateTime.UtcNow,
                            diag.Kind,
                            "CacheCleanup",
                            fullTarget,
                            diag.Summary,
                            RecoveredAutomatically: false,
                            ExceptionType: diag.ExceptionType,
                            ErrorCode: diag.ErrorCode));
                    }
                }
            }

            var success = rejected == 0 && skipped == 0;
            var summary = $"Deleted {deletedFiles:N0} file(s) and {deletedDirs:N0} directory(ies) ({FormatBytes(reclaimedBytes)} freed)"
                + (rejected > 0 ? $"; rejected {rejected:N0} unsafe path(s)" : string.Empty)
                + ".";

            return new SafeCacheClearResult
            {
                Success = success,
                DeletedFileCount = deletedFiles,
                DeletedDirectoryCount = deletedDirs,
                SkippedCount = skipped,
                RejectedUnsafePathCount = rejected,
                ReclaimedBytes = reclaimedBytes,
                Diagnostics = diagnostics,
                Summary = summary
            };
        }

        public SafeCacheClearResult SafeClearProjectCache(SQLiteProjectIndexCache sqliteCache, string projectFolderPath)
        {
            ArgumentNullException.ThrowIfNull(sqliteCache);
            if (string.IsNullOrWhiteSpace(projectFolderPath))
            {
                throw new ArgumentException("Project folder path must not be empty.", nameof(projectFolderPath));
            }

            var effectiveIndexRoot = NormalizeRootPath(sqliteCache.CacheDirectory);
            var folderKey = SQLiteProjectIndexCache.GetFolderCacheKey(projectFolderPath, sqliteCache.CaseSensitivePaths);
            var scopeKey = ProjectLoadingService.GetDecompressedBundleScopeKey(projectFolderPath, sqliteCache.CaseSensitivePaths);

            var deletedFiles = 0;
            var deletedDirs = 0;
            var skipped = 0;
            var rejected = 0;
            long reclaimedBytes = 0;
            var diagnostics = new List<CacheDiagnosticEvent>();

            // Evict SQLite preview entries & release handles before deleting DB/sidecar files.
            var dbPath = sqliteCache.GetDbPath(projectFolderPath);
            var idxPath = sqliteCache.GetIdxPath(projectFolderPath);
            var indexTargets = new List<string>
            {
                dbPath,
                dbPath + "-wal",
                dbPath + "-shm",
                dbPath + "-journal",
                idxPath,
                MmapProjectIndexSidecar.GetTempSidecarPath(idxPath),
                Path.Combine(effectiveIndexRoot, "PreviewPayloads", folderKey)
            };

            try
            {
                sqliteCache.EvictPreviewCache(projectFolderPath, maxTotalBytes: 0, maxEntryCount: 0);
            }
            catch (Exception ex)
            {
                var diag = ClassifyException(ex);
                diagnostics.Add(new CacheDiagnosticEvent(
                    DateTime.UtcNow,
                    diag.Kind,
                    "PreviewCache",
                    dbPath,
                    diag.Summary,
                    RecoveredAutomatically: false,
                    ExceptionType: diag.ExceptionType,
                    ErrorCode: diag.ErrorCode));
            }

            sqliteCache.InvalidateReadConnectionPool(dbPath);
            SqliteConnection.ClearAllPools();

            var indexResult = SafeClearPaths(effectiveIndexRoot, indexTargets, sqliteCache);
            sqliteCache.ForgetInitializedDatabase(dbPath);
            MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
            AccumulateClearResult(indexResult, ref deletedFiles, ref deletedDirs, ref skipped, ref rejected, ref reclaimedBytes, diagnostics);

            var previewTargets = new[] { Path.Combine(PreviewCacheRoot, folderKey) };
            var previewResult = SafeClearPaths(PreviewCacheRoot, previewTargets);
            AccumulateClearResult(previewResult, ref deletedFiles, ref deletedDirs, ref skipped, ref rejected, ref reclaimedBytes, diagnostics);

            var decompressedTargets = new[]
            {
                Path.Combine(DecompressedCacheRoot, scopeKey),
                Path.Combine(DecompressedCacheRoot, folderKey)
            }.Distinct(_caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            var decompressedResult = SafeClearPaths(DecompressedCacheRoot, decompressedTargets);
            AccumulateClearResult(decompressedResult, ref deletedFiles, ref deletedDirs, ref skipped, ref rejected, ref reclaimedBytes, diagnostics);

            var success = rejected == 0 && skipped == 0;
            return new SafeCacheClearResult
            {
                Success = success,
                DeletedFileCount = deletedFiles,
                DeletedDirectoryCount = deletedDirs,
                SkippedCount = skipped,
                RejectedUnsafePathCount = rejected,
                ReclaimedBytes = reclaimedBytes,
                Diagnostics = diagnostics,
                Summary = $"Cleared project cache: {deletedFiles:N0} file(s), {deletedDirs:N0} folder(s), {FormatBytes(reclaimedBytes)} freed."
            };
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double scaled = bytes;
            var unitIndex = 0;
            while (scaled >= 1024.0 && unitIndex < units.Length - 1)
            {
                scaled /= 1024.0;
                unitIndex++;
            }

            return unitIndex == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} B")
                : string.Create(CultureInfo.InvariantCulture, $"{scaled:0.##} {units[unitIndex]}");
        }

        private CacheDatabaseHealthEntry InspectSingleDatabase(string dbPath)
        {
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            var dbBytes = TryGetFileLength(dbPath)
                + TryGetFileLength(dbPath + "-wal")
                + TryGetFileLength(dbPath + "-shm")
                + TryGetFileLength(dbPath + "-journal");
            var sidecarBytes = TryGetFileLength(idxPath);

            if (!SQLiteProjectIndexCache.HasValidSqliteHeader(dbPath))
            {
                return new CacheDatabaseHealthEntry(
                    dbPath,
                    idxPath,
                    dbBytes,
                    sidecarBytes,
                    PreviewEntryCount: 0,
                    PreviewEntryBytes: 0,
                    Status: CacheHealthStatus.Corrupt,
                    IsCorrupt: true,
                    IsSidecarCorrupt: File.Exists(idxPath),
                    IsInterruptedIndexing: false,
                    IndexingStatus: null,
                    DiagnosticMessage: "SQLite header is truncated or invalid.");
            }

            try
            {
                var connString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 3
                }.ToString();

                using var conn = new SqliteConnection(connString);
                conn.Open();

                if (!SQLiteProjectIndexCache.TryCheckDatabaseQuickIntegrity(conn, out var integrityError))
                {
                    return new CacheDatabaseHealthEntry(
                        dbPath,
                        idxPath,
                        dbBytes,
                        sidecarBytes,
                        PreviewEntryCount: 0,
                        PreviewEntryBytes: 0,
                        Status: CacheHealthStatus.Corrupt,
                        IsCorrupt: true,
                        IsSidecarCorrupt: File.Exists(idxPath),
                        IsInterruptedIndexing: false,
                        IndexingStatus: null,
                        DiagnosticMessage: $"SQLite quick_check failed: {integrityError}");
                }

                string? latestSignature = null;
                string? indexingStatus = null;
                var previewCount = 0;
                long previewBytes = 0;
                var isInterrupted = false;

                if (TableExists(conn, "Projects"))
                {
                    using var projCmd = conn.CreateCommand();
                    projCmd.CommandText = "SELECT Id, SignatureHash FROM Projects ORDER BY LastIndexed DESC, Id DESC LIMIT 1;";
                    using var reader = projCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var projectId = reader.GetInt64(0);
                        latestSignature = reader.IsDBNull(1) ? null : reader.GetString(1);

                        if (TableExists(conn, "ProjectIndexingState"))
                        {
                            using var stateCmd = conn.CreateCommand();
                            stateCmd.CommandText = "SELECT Status FROM ProjectIndexingState WHERE ProjectId = @pid LIMIT 1;";
                            stateCmd.Parameters.AddWithValue("@pid", projectId);
                            indexingStatus = stateCmd.ExecuteScalar()?.ToString();
                            isInterrupted = SQLiteProjectIndexCache.IsInterruptedOrIncompleteIndexingStatus(indexingStatus);
                        }
                    }
                }

                if (TableExists(conn, "PreviewCacheEntries"))
                {
                    using var prevCmd = conn.CreateCommand();
                    prevCmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(ByteSize), 0) FROM PreviewCacheEntries;";
                    using var reader = prevCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        previewCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                        previewBytes = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                    }
                }

                var isSidecarCorrupt = false;
                string? diagnosticMessage = null;
                if (File.Exists(idxPath))
                {
                    var sidecarValid = !string.IsNullOrWhiteSpace(latestSignature)
                        && MmapProjectIndexSidecar.ValidateFileImage(
                            idxPath,
                            latestSignature,
                            SQLiteProjectIndexCache.SemanticSchemaVersion,
                            out _);
                    if (!sidecarValid)
                    {
                        isSidecarCorrupt = true;
                        diagnosticMessage = "Paired mmap .idx sidecar is corrupt, truncated, or mismatched with database signature.";
                    }
                }

                if (isInterrupted)
                {
                    diagnosticMessage = string.IsNullOrEmpty(diagnosticMessage)
                        ? $"Indexing state is incomplete ('{indexingStatus}')."
                        : $"{diagnosticMessage} Indexing state is incomplete ('{indexingStatus}').";
                }

                var status = isSidecarCorrupt
                    ? CacheHealthStatus.Corrupt
                    : isInterrupted
                        ? CacheHealthStatus.Degraded
                        : CacheHealthStatus.Healthy;

                return new CacheDatabaseHealthEntry(
                    dbPath,
                    idxPath,
                    dbBytes,
                    sidecarBytes,
                    previewCount,
                    previewBytes,
                    status,
                    IsCorrupt: false,
                    IsSidecarCorrupt: isSidecarCorrupt,
                    IsInterruptedIndexing: isInterrupted,
                    IndexingStatus: indexingStatus,
                    DiagnosticMessage: diagnosticMessage);
            }
            catch (SqliteException ex) when (IsSqliteCorruption(ex))
            {
                return new CacheDatabaseHealthEntry(
                    dbPath,
                    idxPath,
                    dbBytes,
                    sidecarBytes,
                    PreviewEntryCount: 0,
                    PreviewEntryBytes: 0,
                    Status: CacheHealthStatus.Corrupt,
                    IsCorrupt: true,
                    IsSidecarCorrupt: File.Exists(idxPath),
                    IsInterruptedIndexing: false,
                    IndexingStatus: null,
                    DiagnosticMessage: $"SQLite corruption (code {ex.SqliteErrorCode}): {ex.Message}");
            }
            catch (Exception ex)
            {
                var diag = ClassifyException(ex);
                return new CacheDatabaseHealthEntry(
                    dbPath,
                    idxPath,
                    dbBytes,
                    sidecarBytes,
                    PreviewEntryCount: 0,
                    PreviewEntryBytes: 0,
                    Status: diag.Kind == CacheFailureKind.Corruption ? CacheHealthStatus.Corrupt : CacheHealthStatus.Inaccessible,
                    IsCorrupt: diag.Kind == CacheFailureKind.Corruption,
                    IsSidecarCorrupt: false,
                    IsInterruptedIndexing: false,
                    IndexingStatus: null,
                    DiagnosticMessage: diag.Summary);
            }
        }

        private static bool TableExists(SqliteConnection conn, string tableName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
            cmd.Parameters.AddWithValue("@name", tableName);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }

        private static bool TryResetInterruptedDatabaseState(string indexCacheRoot, string dbPath, bool caseSensitivePaths, out long removedSidecarBytes)
        {
            removedSidecarBytes = 0;
            try
            {
                var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
                if (File.Exists(idxPath))
                {
                    removedSidecarBytes = TryDeleteSingleFileConfined(indexCacheRoot, idxPath, caseSensitivePaths, out _);
                }

                using var conn = SQLiteProjectIndexCache.CreateConnectionByDbPath(dbPath);
                using var tx = conn.BeginTransaction();
                SQLiteProjectIndexCache.DropSemanticStagingTables(conn, tx);

                if (TableExists(conn, "ProjectIndexingState"))
                {
                    using var findCmd = conn.CreateCommand();
                    findCmd.Transaction = tx;
                    findCmd.CommandText = "SELECT ProjectId, Status FROM ProjectIndexingState;";
                    var incompleteIds = new List<long>();
                    using (var reader = findCmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var pid = reader.GetInt64(0);
                            var status = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            if (SQLiteProjectIndexCache.IsInterruptedOrIncompleteIndexingStatus(status))
                            {
                                incompleteIds.Add(pid);
                            }
                        }
                    }

                    foreach (var pid in incompleteIds)
                    {
                        foreach (var table in new[] { "Assets", "SourceFiles", "PreviewCacheEntries", "IndexingReadFiles" })
                        {
                            if (!TableExists(conn, table))
                            {
                                continue;
                            }

                            using var delCmd = conn.CreateCommand();
                            delCmd.Transaction = tx;
                            delCmd.CommandText = $"DELETE FROM {table} WHERE ProjectId = @pid;";
                            delCmd.Parameters.AddWithValue("@pid", pid);
                            delCmd.ExecuteNonQuery();
                        }

                        using var updCmd = conn.CreateCommand();
                        updCmd.Transaction = tx;
                        updCmd.CommandText = @"
                            UPDATE ProjectIndexingState
                            SET Status = 'failed',
                                CurrentFile = 'Recovered from interrupted indexing; rebuild required',
                                CompletedAt = NULL,
                                UpdatedAt = CURRENT_TIMESTAMP
                            WHERE ProjectId = @pid;";
                        updCmd.Parameters.AddWithValue("@pid", pid);
                        updCmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to reset interrupted SQLite index state at '{dbPath}': {ex.Message}");
                return false;
            }
        }

        private void InspectDecompressedCacheDirectory(
            string decompressedRoot,
            ref int bundleFileCount,
            ref long bundleTotalBytes,
            ref int orphanTempFileCount,
            ref long orphanTempTotalBytes,
            out List<string> brokenBundleDirs,
            List<string> issues)
        {
            brokenBundleDirs = new List<string>();

            foreach (var scopeDir in EnumerateDirectoriesSafely(decompressedRoot))
            {
                foreach (var bundleDir in EnumerateDirectoriesSafely(scopeDir))
                {
                    var readyPath = Path.Combine(bundleDir, ".ready");
                    var blocksPath = Path.Combine(bundleDir, "blocks.bin");
                    var manifestPath = Path.Combine(bundleDir, "manifest.json");

                    var dirFiles = EnumerateFilesSafely(bundleDir).ToList();
                    foreach (var f in dirFiles)
                    {
                        var len = TryGetFileLength(f);
                        if (Path.GetFileName(f).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            orphanTempFileCount++;
                            orphanTempTotalBytes += len;
                        }
                        else
                        {
                            bundleFileCount++;
                            bundleTotalBytes += len;
                        }
                    }

                    if (!IsValidDecompressedBundleEntry(readyPath, blocksPath, manifestPath, out var reason))
                    {
                        brokenBundleDirs.Add(bundleDir);
                        issues.Add($"Decompressed bundle entry '{Path.GetFileName(bundleDir)}' is inconsistent: {reason}");
                    }
                }

                // Count any loose files directly inside scopeDir
                foreach (var looseFile in EnumerateTopLevelFilesSafely(scopeDir))
                {
                    var len = TryGetFileLength(looseFile);
                    if (Path.GetFileName(looseFile).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        orphanTempFileCount++;
                        orphanTempTotalBytes += len;
                    }
                    else
                    {
                        bundleFileCount++;
                        bundleTotalBytes += len;
                    }
                }
            }

            foreach (var rootLooseFile in EnumerateTopLevelFilesSafely(decompressedRoot))
            {
                var len = TryGetFileLength(rootLooseFile);
                if (Path.GetFileName(rootLooseFile).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    orphanTempFileCount++;
                    orphanTempTotalBytes += len;
                }
                else
                {
                    bundleFileCount++;
                    bundleTotalBytes += len;
                }
            }
        }

        private static bool IsValidDecompressedBundleEntry(
            string readyPath,
            string blocksPath,
            string manifestPath,
            out string reason)
        {
            if (!File.Exists(readyPath))
            {
                reason = "missing .ready completion marker (interrupted decompression)";
                return false;
            }

            if (!File.Exists(blocksPath) || !File.Exists(manifestPath))
            {
                reason = "missing blocks.bin or manifest.json";
                return false;
            }

            try
            {
                var blocksLength = new FileInfo(blocksPath).Length;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    reason = "manifest.json root is not a JSON object";
                    return false;
                }

                if (!TryGetInt64Property(root, "Version", out var version) || version != 1)
                {
                    reason = "manifest.json version is missing or unsupported";
                    return false;
                }

                if (!TryGetInt64Property(root, "UncompressedSize", out var expectedSize) || expectedSize != blocksLength)
                {
                    reason = $"blocks.bin length ({blocksLength}) does not match manifest UncompressedSize";
                    return false;
                }

                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = $"unreadable manifest.json ({ex.Message})";
                return false;
            }
        }

        private static bool TryGetInt64Property(JsonElement element, string propertyName, out long value)
        {
            value = 0;
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetInt64(out value))
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearDirectoryContentsConfined(
            string allowedRoot,
            string directoryPath,
            string category,
            ref int deletedFiles,
            ref int deletedDirs,
            ref int skipped,
            ref int rejected,
            ref long reclaimedBytes,
            List<CacheDiagnosticEvent> diagnostics)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            if (IsReparsePointOrSymlink(directoryPath))
            {
                rejected++;
                diagnostics.Add(new CacheDiagnosticEvent(
                    DateTime.UtcNow,
                    CacheFailureKind.BoundaryViolation,
                    category,
                    directoryPath,
                    "Refused to traverse symlink or reparse-point directory inside cache root.",
                    RecoveredAutomatically: false));
                return;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(directoryPath);
            }
            catch (Exception ex)
            {
                skipped++;
                var diag = ClassifyException(ex);
                diagnostics.Add(new CacheDiagnosticEvent(
                    DateTime.UtcNow,
                    diag.Kind,
                    category,
                    directoryPath,
                    diag.Summary,
                    RecoveredAutomatically: false,
                    ExceptionType: diag.ExceptionType,
                    ErrorCode: diag.ErrorCode));
                return;
            }

            foreach (var file in files)
            {
                if (!IsPathStrictlyInsideCacheRoot(allowedRoot, file, allowEqualRoot: false, _caseSensitivePaths))
                {
                    rejected++;
                    diagnostics.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        CacheFailureKind.BoundaryViolation,
                        category,
                        file,
                        "Refused to delete file outside cache root or pointing through a symlink.",
                        RecoveredAutomatically: false));
                    continue;
                }

                var bytes = TryDeleteSingleFileConfined(allowedRoot, file, _caseSensitivePaths, out var deleted, diagnostics);
                if (deleted)
                {
                    deletedFiles++;
                    reclaimedBytes += bytes;
                }
                else
                {
                    skipped++;
                }
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(directoryPath);
            }
            catch
            {
                return;
            }

            foreach (var subDir in subDirs)
            {
                if (!IsPathStrictlyInsideCacheRoot(allowedRoot, subDir, allowEqualRoot: false, _caseSensitivePaths))
                {
                    rejected++;
                    diagnostics.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        CacheFailureKind.BoundaryViolation,
                        category,
                        subDir,
                        "Refused to delete subdirectory outside cache root or pointing through a symlink.",
                        RecoveredAutomatically: false));
                    continue;
                }

                ClearDirectoryContentsConfined(
                    allowedRoot,
                    subDir,
                    category,
                    ref deletedFiles,
                    ref deletedDirs,
                    ref skipped,
                    ref rejected,
                    ref reclaimedBytes,
                    diagnostics);

                try
                {
                    if (Directory.Exists(subDir) && !Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        Directory.Delete(subDir, recursive: false);
                        deletedDirs++;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    var diag = ClassifyException(ex);
                    diagnostics.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        diag.Kind,
                        category,
                        subDir,
                        diag.Summary,
                        RecoveredAutomatically: false,
                        ExceptionType: diag.ExceptionType,
                        ErrorCode: diag.ErrorCode));
                }
            }
        }

        private long DeleteDatabaseAndSidecarFilesConfined(string indexRoot, string dbPath, out bool deletedAny)
        {
            deletedAny = false;
            long reclaimed = 0;
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            var candidates = new[]
            {
                dbPath,
                dbPath + "-wal",
                dbPath + "-shm",
                dbPath + "-journal",
                idxPath,
                MmapProjectIndexSidecar.GetTempSidecarPath(idxPath)
            };

            foreach (var file in candidates)
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                reclaimed += TryDeleteSingleFileConfined(indexRoot, file, _caseSensitivePaths, out var deleted);
                if (deleted)
                {
                    deletedAny = true;
                }
            }

            MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
            return reclaimed;
        }

        private static long TryDeleteSingleFileConfined(
            string allowedRoot,
            string filePath,
            bool caseSensitivePaths,
            out bool deleted,
            List<CacheDiagnosticEvent>? diagnostics = null)
        {
            deleted = false;
            if (!IsPathStrictlyInsideCacheRoot(allowedRoot, filePath, allowEqualRoot: false, caseSensitivePaths))
            {
                diagnostics?.Add(new CacheDiagnosticEvent(
                    DateTime.UtcNow,
                    CacheFailureKind.BoundaryViolation,
                    "CacheCleanup",
                    filePath,
                    $"Refused to delete file outside '{allowedRoot}'.",
                    RecoveredAutomatically: false));
                return 0;
            }

            var length = TryGetFileLength(filePath);
            try
            {
                if (!File.Exists(filePath))
                {
                    return 0;
                }

                var attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics?.Add(new CacheDiagnosticEvent(
                        DateTime.UtcNow,
                        CacheFailureKind.BoundaryViolation,
                        "CacheCleanup",
                        filePath,
                        "Refused to delete symlink/reparse point file.",
                        RecoveredAutomatically: false));
                    return 0;
                }

                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                }

                File.Delete(filePath);
                deleted = true;
                return length;
            }
            catch (Exception ex)
            {
                var diag = ClassifyException(ex);
                diagnostics?.Add(new CacheDiagnosticEvent(
                    DateTime.UtcNow,
                    diag.Kind,
                    "CacheCleanup",
                    filePath,
                    diag.Summary,
                    RecoveredAutomatically: false,
                    ExceptionType: diag.ExceptionType,
                    ErrorCode: diag.ErrorCode));
                return 0;
            }
        }

        private static void AccumulateClearResult(
            SafeCacheClearResult part,
            ref int deletedFiles,
            ref int deletedDirs,
            ref int skipped,
            ref int rejected,
            ref long reclaimedBytes,
            List<CacheDiagnosticEvent> diagnostics)
        {
            deletedFiles += part.DeletedFileCount;
            deletedDirs += part.DeletedDirectoryCount;
            skipped += part.SkippedCount;
            rejected += part.RejectedUnsafePathCount;
            reclaimedBytes += part.ReclaimedBytes;
            diagnostics.AddRange(part.Diagnostics);
        }

        private static bool IsReparsePointOrSymlink(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    return (fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null;
                }

                if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    return (di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget != null;
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        private static IEnumerable<string> EnumerateFilesSafely(string rootDir)
        {
            var pending = new Stack<string>();
            pending.Push(rootDir);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (IsReparsePointOrSymlink(current))
                {
                    continue;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch
                {
                    continue;
                }

                for (var i = 0; i < files.Length; i++)
                {
                    if (!IsReparsePointOrSymlink(files[i]))
                    {
                        yield return files[i];
                    }
                }

                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }

                for (var i = 0; i < dirs.Length; i++)
                {
                    if (!IsReparsePointOrSymlink(dirs[i]))
                    {
                        pending.Push(dirs[i]);
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateDirectoriesSafely(string parentDir)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(parentDir);
            }
            catch
            {
                yield break;
            }

            for (var i = 0; i < dirs.Length; i++)
            {
                if (!IsReparsePointOrSymlink(dirs[i]))
                {
                    yield return dirs[i];
                }
            }
        }

        private static IEnumerable<string> EnumerateTopLevelFilesSafely(string parentDir)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(parentDir);
            }
            catch
            {
                yield break;
            }

            for (var i = 0; i < files.Length; i++)
            {
                if (!IsReparsePointOrSymlink(files[i]))
                {
                    yield return files[i];
                }
            }
        }

        private static long MeasureDirectoryBytesSafely(string dir)
        {
            long total = 0;
            foreach (var file in EnumerateFilesSafely(dir))
            {
                total += TryGetFileLength(file);
            }

            return total;
        }

        private static long TryGetFileLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static string NormalizeRootPath(string path)
        {
            try
            {
                return SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(Path.GetFullPath(path));
            }
            catch
            {
                return SQLiteProjectIndexCache.TrimTrailingDirectorySeparatorsPreservingRoot(path);
            }
        }

        private static Exception UnwrapException(Exception ex)
        {
            while (ex is AggregateException agg && agg.InnerExceptions.Count == 1 && agg.InnerException != null)
            {
                ex = agg.InnerException;
            }

            return ex;
        }

        private static string BuildDiskUsageSummary(
            CacheHealthStatus health,
            int totalFiles,
            long totalBytes,
            long sqliteBytes,
            long sidecarBytes,
            long previewPayloadBytes,
            int previewDbEntries,
            long previewDbInlineBytes,
            long decompressedBytes,
            int orphanTempCount,
            long orphanTempBytes,
            int issueCount)
        {
            var sb = new StringBuilder();
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"Cache health: {health} | Total: {FormatBytes(totalBytes)} ({totalFiles:N0} files)"));
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $" | SQLite: {FormatBytes(sqliteBytes)}, Sidecars: {FormatBytes(sidecarBytes)}, Preview files: {FormatBytes(previewPayloadBytes)} ({previewDbEntries:N0} DB entries / {FormatBytes(previewDbInlineBytes)}), Decompressed bundles: {FormatBytes(decompressedBytes)}"));
            if (orphanTempCount > 0)
            {
                sb.Append(string.Create(
                    CultureInfo.InvariantCulture,
                    $", Temp orphans: {orphanTempCount:N0} ({FormatBytes(orphanTempBytes)})"));
            }
            if (issueCount > 0)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $" | Issues: {issueCount:N0}"));
            }

            return sb.ToString();
        }
    }
}
