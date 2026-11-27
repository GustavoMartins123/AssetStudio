using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio.Avalonia.Services;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        public List<AssetHandle>? LoadIndexCache(string folderPath, string signature)
        {
            return LoadIndexCacheCore(folderPath, signature, allowMmapSidecar: true);
        }

        internal List<AssetHandle>? LoadIndexCacheFromSqliteOnly(string folderPath, string signature)
        {
            return LoadIndexCacheCore(folderPath, signature, allowMmapSidecar: false);
        }

        public bool LoadIndexCacheIntoProjectIndex(
            string folderPath,
            string signature,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? sourceFiles)
        {
            return LoadIndexCacheIntoProjectIndexCore(
                folderPath,
                signature,
                targetIndex,
                allowMmapSidecar: true,
                out sourceFiles,
                out _);
        }

        internal bool LoadIndexCacheIntoProjectIndex(
            string folderPath,
            string signature,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? sourceFiles,
            out bool hasMissingOriginalPaths)
        {
            return LoadIndexCacheIntoProjectIndexCore(
                folderPath,
                signature,
                targetIndex,
                allowMmapSidecar: true,
                out sourceFiles,
                out hasMissingOriginalPaths);
        }

        internal bool LoadIndexCacheIntoProjectIndexFromSqliteOnly(
            string folderPath,
            string signature,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? sourceFiles)
        {
            return LoadIndexCacheIntoProjectIndexCore(
                folderPath,
                signature,
                targetIndex,
                allowMmapSidecar: false,
                out sourceFiles,
                out _);
        }

        private bool LoadIndexCacheIntoProjectIndexCore(
            string folderPath,
            string signature,
            ProjectIndex targetIndex,
            bool allowMmapSidecar,
            out List<SourceFileMetadata>? sourceFiles,
            out bool hasMissingOriginalPaths)
        {
            ArgumentNullException.ThrowIfNull(targetIndex);
            sourceFiles = null;
            hasMissingOriginalPaths = false;
            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out var fromCache);
                    if (projectId == null)
                    {
                        return false;
                    }

                    if (fromCache)
                    {
                        var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                        if (verifiedProjectId == null)
                        {
                            return false;
                        }

                        projectId = verifiedProjectId;
                    }

                    var indexingStatus = LoadProjectIndexingStatus(conn, projectId.Value);
                    if (IsIncompleteIndexCacheStatus(indexingStatus))
                    {
                        return false;
                    }

                    if (allowMmapSidecar)
                    {
                        if (MmapProjectIndexSidecar.TryLoadIntoArena(
                            idxPath,
                            signature,
                            SemanticSchemaVersion,
                            targetIndex,
                            out var mmapSourceFiles,
                            out hasMissingOriginalPaths))
                        {
                            if (targetIndex.Count == 0)
                            {
                                sourceFiles = null;
                                hasMissingOriginalPaths = false;
                                return false;
                            }

                            sourceFiles = mmapSourceFiles;
                            return true;
                        }

                        if (File.Exists(idxPath))
                        {
                            TryRecoverCorruptSidecar(
                                idxPath,
                                "Mmap .idx sidecar failed validation or load while SQLite index was valid; removed corrupt/stale sidecar and fell back to SQLite.");
                        }
                    }

                    var loaded = LoadAssetRecordsIntoProjectIndex(
                        conn,
                        projectId.Value,
                        targetIndex,
                        out var sqliteSourceFiles,
                        out hasMissingOriginalPaths,
                        out var incomplete);
                    if (incomplete || loaded == 0)
                    {
                        targetIndex.Clear();
                        sourceFiles = null;
                        hasMissingOriginalPaths = false;
                        return false;
                    }

                    sourceFiles = sqliteSourceFiles;
                    return true;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                targetIndex.Clear();
                sourceFiles = null;
                hasMissingOriginalPaths = false;
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while loading index into arena: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return false;
            }
            catch (Exception ex)
            {
                targetIndex.Clear();
                sourceFiles = null;
                hasMissingOriginalPaths = false;
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to load SQLite index cache into arena: {ex.Message}");
                return false;
            }
        }

        private static int LoadAssetRecordsIntoProjectIndex(
            SqliteConnection conn,
            long projectId,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata> sourceFiles,
            out bool hasMissingOriginalPaths,
            out bool incomplete)
        {
            sourceFiles = new List<SourceFileMetadata>();
            hasMissingOriginalPaths = false;

            var indexingStatus = LoadProjectIndexingStatus(conn, projectId);
            if (IsIncompleteIndexCacheStatus(indexingStatus))
            {
                incomplete = true;
                return 0;
            }

            incomplete = false;
            var sourceOrigins = LoadSourceFileOriginsForProject(conn, projectId, sourceFiles);
            var loadedCount = 0;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                FROM Assets
                WHERE ProjectId = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);

            long lastSourceFileId = long.MinValue;
            var lastOrigin = AssetHandleOrigin.Empty;
            var lastOriginMissingPath = true;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                AssetHandleOrigin origin;
                bool originMissingPath;
                if (reader.IsDBNull(4))
                {
                    origin = AssetHandleOrigin.Empty;
                    originMissingPath = true;
                }
                else
                {
                    var srcId = reader.GetInt64(4);
                    if (srcId != lastSourceFileId)
                    {
                        lastSourceFileId = srcId;
                        if (sourceOrigins.TryGetValue(srcId, out var resolved))
                        {
                            lastOrigin = resolved;
                            lastOriginMissingPath = string.IsNullOrWhiteSpace(resolved.OriginalPath);
                        }
                        else
                        {
                            lastOrigin = AssetHandleOrigin.Empty;
                            lastOriginMissingPath = true;
                        }
                    }

                    origin = lastOrigin;
                    originMissingPath = lastOriginMissingPath;
                }

                if (originMissingPath)
                {
                    hasMissingOriginalPaths = true;
                }

                var uniqueId = reader.GetString(0);
                var name = reader.GetString(1);
                var type = (ClassIDType)reader.GetInt32(2);
                var container = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var pathId = reader.GetInt64(5);
                var byteStart = reader.GetInt64(6);
                var byteSize = reader.GetInt64(7);

                string? customUniqueId = uniqueId;
                if (!ReferenceEquals(origin, AssetHandleOrigin.Empty)
                    && !string.IsNullOrEmpty(uniqueId)
                    && IsCanonicalUniqueIdForOrigin(uniqueId.AsSpan(), origin.UniqueIdPrefix.AsSpan(), pathId))
                {
                    customUniqueId = null;
                }

                targetIndex.AddRecord(
                    serializedFileName: origin.SerializedFileName,
                    originalPath: origin.OriginalPath,
                    pathId: pathId,
                    name: name,
                    type: type,
                    container: container,
                    byteStart: byteStart,
                    byteSize: byteSize,
                    sourceHash: origin.SourceHash,
                    unityVersion: origin.UnityVersion,
                    customUniqueId: customUniqueId);
                loadedCount++;
            }

            return loadedCount;
        }

        private static bool IsCanonicalUniqueIdForOrigin(
            ReadOnlySpan<char> uniqueId,
            ReadOnlySpan<char> originPrefix,
            long pathId)
        {
            if (uniqueId.Length <= originPrefix.Length + 1 || uniqueId[originPrefix.Length] != '#')
            {
                return false;
            }

            if (!uniqueId.Slice(0, originPrefix.Length).SequenceEqual(originPrefix))
            {
                return false;
            }

            Span<char> pathIdBuf = stackalloc char[32];
            if (!pathId.TryFormat(pathIdBuf, out var written, default, System.Globalization.CultureInfo.InvariantCulture))
            {
                return false;
            }

            return uniqueId.Length == originPrefix.Length + 1 + written
                && uniqueId.Slice(originPrefix.Length + 1).SequenceEqual(pathIdBuf.Slice(0, written));
        }

        private List<AssetHandle>? LoadIndexCacheCore(string folderPath, string signature, bool allowMmapSidecar)
        {
            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out var fromCache);
                    if (projectId == null)
                    {
                        return null;
                    }

                    if (fromCache)
                    {
                        var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                        if (verifiedProjectId == null)
                        {
                            return null;
                        }

                        projectId = verifiedProjectId;
                    }

                    var indexingStatus = LoadProjectIndexingStatus(conn, projectId.Value);
                    if (IsIncompleteIndexCacheStatus(indexingStatus))
                    {
                        return null;
                    }

                    if (allowMmapSidecar)
                    {
                        if (MmapProjectIndexSidecar.TryLoadHandles(idxPath, signature, SemanticSchemaVersion, out var mmapHandles))
                        {
                            return mmapHandles.Count == 0 ? null : mmapHandles;
                        }

                        if (File.Exists(idxPath))
                        {
                            TryRecoverCorruptSidecar(
                                idxPath,
                                "Mmap .idx sidecar failed validation or load while SQLite index was valid; removed corrupt/stale sidecar and fell back to SQLite.");
                        }
                    }

                    var handles = LoadAssetHandlesForProject(conn, projectId.Value, out var incomplete);
                    if (incomplete)
                    {
                        return null;
                    }

                    return handles.Count == 0 ? null : handles;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while loading index cache: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return null;
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to load SQLite index cache: {ex.Message}");
                return null;
            }
        }

        private static List<AssetHandle> LoadAssetHandlesForProject(SqliteConnection conn, long projectId, out bool incomplete)
        {
            var indexingStatus = LoadProjectIndexingStatus(conn, projectId);
            if (IsIncompleteIndexCacheStatus(indexingStatus))
            {
                incomplete = true;
                return new List<AssetHandle>();
            }

            incomplete = false;
            var sourceFiles = LoadSourceFileOriginsForProject(conn, projectId);
            var handles = new List<AssetHandle>();
            using (var cmd = conn.CreateCommand())
            {
                // Schema v13: handles live once in the clustered Assets table (PRIMARY KEY (ProjectId, UniqueID));
                // origins are resolved from the per-project SourceFiles map so path strings are shared, not duplicated.
                cmd.CommandText = @"
                    SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                    FROM Assets
                    WHERE ProjectId = @projectId";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var origin = !reader.IsDBNull(4) && sourceFiles.TryGetValue(reader.GetInt64(4), out var resolved)
                            ? resolved
                            : AssetHandleOrigin.Empty;
                        handles.Add(AssetHandle.CreateWithOrigin(
                            origin,
                            reader.GetString(0),
                            reader.GetInt64(5),
                            reader.GetString(1),
                            (ClassIDType)reader.GetInt32(2),
                            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            reader.GetInt64(6),
                            reader.GetInt64(7)));
                    }
                }
            }

            return handles;
        }

        private static Dictionary<long, AssetHandleOrigin> LoadSourceFileOriginsForProject(
            SqliteConnection conn,
            long projectId,
            List<SourceFileMetadata>? sourceFiles = null)
        {
            var result = new Dictionary<long, AssetHandleOrigin>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, SerializedFileName, OriginalPath
                FROM SourceFiles
                WHERE ProjectId = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var serializedFileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var originalPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                sourceFiles?.Add(new SourceFileMetadata(serializedFileName, originalPath));
                result[reader.GetInt64(0)] = AssetHandleOrigin.Intern(serializedFileName, originalPath);
            }

            return result;
        }

        public bool SaveIndexCache(
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            string unityVersion,
            IEnumerable<AssetHandle> handles,
            bool preserveSemanticRelations = false,
            Action<int, int, string>? progress = null,
            bool markIndexingCompleted = false)
        {
            return SaveIndexCacheCore(
                folderPath,
                signature,
                scanResult,
                unityVersion,
                handles,
                preserveSemanticRelations,
                progress,
                markIndexingCompleted,
                beforeSidecarValidateHook: null);
        }

        internal bool SaveIndexCacheCore(
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            string unityVersion,
            IEnumerable<AssetHandle> handles,
            bool preserveSemanticRelations,
            Action<int, int, string>? progress,
            bool markIndexingCompleted,
            Action<string>? beforeSidecarValidateHook)
        {
            var dbPath = GetDbPath(folderPath);
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            var stagedIdxPath = string.Empty;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                EnsureInitialized(folderPath);
                stagedIdxPath = string.Empty;
                try
                {
                    var handleCollection = handles as IReadOnlyCollection<AssetHandle> ?? handles?.ToList() ?? new List<AssetHandle>();
                    // Write every handle once into the unified Assets table plus source file fingerprint metadata.
                    var totalWork = Math.Max(1, handleCollection.Count + Math.Max(1, scanResult.TotalFiles));
                    var reporter = new ThrottledProgressReporter(totalWork, progress);

                    // Sort & deduplicate once by UniqueID (Ordinal) so both the WITHOUT ROWID SQLite B-tree insert
                    // and the post-commit mmap .idx sidecar write reuse the same ordered list without holding a
                    // large sidecar buffer in LOH memory or dirtying disk pages during the SQLite transaction.
                    reporter.Report("Sorting & deduplicating handles", force: true);
                    var orderedHandles = MmapProjectIndexSidecar.NormalizeAndDeduplicateHandles(handleCollection);

                    reporter.Report("Waiting for SQLite writer", force: true);
                    lock (WriteGate)
                    {
                        reporter.Report("Opening SQLite connection", force: true);
                        using (var conn = CreateConnectionByDbPath(dbPath))
                        {
                            InvalidateProjectIdCacheForDb(dbPath);
                            long committedProjectId;
                            var scanLookup = BuildScanFileLookup(folderPath, scanResult, _caseSensitivePaths);

                            SetSynchronousMode(conn, "OFF");
                            SetForeignKeysMode(conn, enabled: false);
                            try
                            {
                                using (var transaction = conn.BeginTransaction())
                                {
                                    reporter.Report("Preparing SQLite project row", force: true);
                                    var projectId = EnsureProject(
                                        conn,
                                        transaction,
                                        folderPath,
                                        signature,
                                        scanResult,
                                        _caseSensitivePaths,
                                        out var signatureChanged,
                                        unityVersion);

                                    var existingSources = LoadStoredSourceFiles(conn, transaction, projectId);
                                    var canApplyIncremental = signatureChanged
                                        && existingSources.Count > 0
                                        && scanResult.Files.Count > 0
                                        && HasV16SourceFileMetadata(existingSources);

                                    if (canApplyIncremental)
                                    {
                                        reporter.Report("Computing incremental bundle delta", force: true);
                                        PartitionStoredSourcesForDelta(
                                            folderPath,
                                            existingSources,
                                            scanLookup,
                                            _caseSensitivePaths,
                                            out var unchangedSourceIds,
                                            out var dirtySourceIds,
                                            out var dirtyRatio);

                                        // Only switch to O(1) table recreate + ordered bulk insert for very large changes
                                        // where row-by-row DELETE/UPSERT would rewrite hundreds of thousands of rows.
                                        var useBulkRecreate = orderedHandles.Count >= 250_000 && dirtyRatio > 0.40;
                                        if (unchangedSourceIds.Count > 0 && !useBulkRecreate)
                                        {
                                            var requiresFullSemanticClear = !preserveSemanticRelations && dirtyRatio > 0.25;
                                            DeleteDirtySourceRowsAndAffectedData(
                                                conn,
                                                transaction,
                                                projectId,
                                                dirtySourceIds,
                                                preserveSemanticRelations,
                                                requiresFullSemanticClear);

                                            // Keep unchanged SourceFiles (preserving SourceFiles.Id) and unchanged Assets;
                                            // upsert incoming handles for modified/added files and prune any removed handles.
                                            UpsertSourceFilesAndAssetsIncremental(
                                                conn,
                                                transaction,
                                                projectId,
                                                folderPath,
                                                scanLookup,
                                                existingSources,
                                                unchangedSourceIds,
                                                orderedHandles,
                                                _caseSensitivePaths,
                                                reporter.Advance);
                                        }
                                        else
                                        {
                                            DeleteDirtyPreviewCacheEntries(conn, transaction, projectId, dirtySourceIds);
                                            reporter.Report("Clearing previous index rows", force: true);
                                            ClearIndexTablesForProject(conn, transaction, projectId, preserveSemanticRelations);
                                            InsertSourceFilesAndAssets(conn, transaction, projectId, folderPath, scanLookup, orderedHandles, _caseSensitivePaths, reporter.Advance, isAlreadyOrderedByUniqueId: true, isFreshInsert: true);
                                        }
                                    }
                                    else
                                    {
                                        // Always invalidate stale preview cache entries for modified/replaced/removed sources,
                                        // even if an earlier progress update already updated Projects.SignatureHash.
                                        InvalidateStalePreviewsForChangedProject(
                                            conn,
                                            transaction,
                                            projectId,
                                            folderPath,
                                            scanResult,
                                            signatureChanged,
                                            existingSources,
                                            scanLookup);

                                        reporter.Report("Clearing previous index rows", force: true);
                                        ClearIndexTablesForProject(conn, transaction, projectId, preserveSemanticRelations);

                                        // Single write pass into the unified clustered Assets table (no secondary indexes to rebuild).
                                        InsertSourceFilesAndAssets(conn, transaction, projectId, folderPath, scanLookup, orderedHandles, _caseSensitivePaths, reporter.Advance, isAlreadyOrderedByUniqueId: true, isFreshInsert: true);
                                    }

                                    if (markIndexingCompleted)
                                    {
                                        var completedFiles = Math.Max(1, scanResult.TotalFiles);
                                        SaveProjectIndexingState(
                                            conn,
                                            transaction,
                                            projectId,
                                            "completed",
                                            completedFiles,
                                            completedFiles,
                                            "SQLite index cache saved",
                                            completed: true);
                                    }

                                    // Remove any prior .idx right before SQLite commit so a crash between commit and
                                    // sidecar publish can never leave a stale .idx paired with updated DB rows.
                                    MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);

                                    reporter.Report("Committing SQLite transaction", force: true);
                                    transaction.Commit();
                                    committedProjectId = projectId;
                                }
                            }
                            finally
                            {
                                SetForeignKeysMode(conn, enabled: true);
                                SetSynchronousMode(conn, "NORMAL");
                            }

                            CacheProjectId(dbPath, folderPath, signature, committedProjectId);
                            NotifyDatabaseMutated(dbPath);
                            reporter.Report("Analyzing SQLite statistics", force: true);
                            TryAnalyzeDatabase(conn, "index cache");
                            reporter.Report("Checkpointing SQLite WAL", force: true);
                            TryCheckpointWal(conn, "index cache");

                            reporter.Report("Staging mmap .idx sidecar", force: true);
                            var stagedOk = MmapProjectIndexSidecar.TryStageSidecar(
                                idxPath,
                                signature,
                                SemanticSchemaVersion,
                                orderedHandles,
                                _caseSensitivePaths,
                                out stagedIdxPath,
                                out _,
                                beforeSidecarValidateHook,
                                isAlreadyNormalizedAndOrdered: true);
                            reporter.Report("Publishing mmap .idx sidecar cache", force: true);
                            if (stagedOk)
                            {
                                if (!MmapProjectIndexSidecar.TryPublishStagedSidecar(stagedIdxPath, idxPath))
                                {
                                    MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                                    Logger.Warning($"Failed to publish verified mmap .idx sidecar for '{folderPath}'; deleted stale sidecar.");
                                }
                                else
                                {
                                    stagedIdxPath = string.Empty;
                                }
                            }
                            else
                            {
                                MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                                Logger.Warning($"Post-commit mmap .idx sidecar staging failed for '{folderPath}'; SQLite cache remains valid.");
                            }

                            MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
                            reporter.MarkCompleted("SQLite index cache saved");
                            reporter.Report("SQLite index cache saved", force: true);
                            Logger.Info($"Saved index cache in SQLite for: {folderPath}");
                            return true;
                        }
                    }
                }
                catch (SqliteException ex) when (IsSqliteCorruptionException(ex) && attempt == 0)
                {
                    MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                    TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while saving index cache: {ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                    RecordClassifiedException("SqliteIndex", dbPath, ex);
                    Logger.Warning($"Failed to save SQLite index cache: {ex.Message}");
                    return false;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(stagedIdxPath))
                    {
                        MmapProjectIndexSidecar.TryDeleteFileQuietly(stagedIdxPath);
                    }

                    MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
                }
            }

            return false;
        }

        internal sealed class StoredSourceFileEntry
        {
            public long Id { get; init; }
            public string SerializedFileName { get; init; } = string.Empty;
            public string OriginalPath { get; init; } = string.Empty;
            public string RelativePath { get; init; } = string.Empty;
            public long FileSize { get; init; }
            public long LastWriteUtcTicks { get; init; }
            public ulong ContentSampleHash { get; init; }
            public ProjectFileKind FileKind { get; init; }
        }

        internal sealed class IncrementalProjectDeltaPlan
        {
            public long ProjectId { get; init; }
            public string PreviousSignature { get; init; } = string.Empty;
            public bool CanApplyIncremental { get; init; }
            public bool RequiresFullSemanticRebuild { get; init; }
            public double DirtyRatio { get; init; }
            public List<long> UnchangedSourceFileIds { get; } = new();
            public List<long> DirtySourceFileIds { get; } = new();
            public List<string> UnchangedFilePaths { get; } = new();
            public List<string> FilesToReindex { get; } = new();
            public List<string> DeletedFilePaths { get; } = new();
            public List<string> AffectedSemanticFilePaths { get; } = new();
        }

        internal IncrementalProjectDeltaPlan PlanIncrementalDelta(string folderPath, ProjectScanResult newScanResult)
        {
            ArgumentNullException.ThrowIfNull(newScanResult);
            if (string.IsNullOrWhiteSpace(folderPath) || newScanResult.Files.Count == 0)
            {
                return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            if (!File.Exists(dbPath))
            {
                return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
            }

            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var projectId = FindLatestProjectId(conn, null, folderPath, _caseSensitivePaths);
                    if (projectId == null)
                    {
                        return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
                    }

                    var status = LoadProjectIndexingStatus(conn, projectId.Value);
                    if (IsIncompleteIndexCacheStatus(status))
                    {
                        return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
                    }

                    string prevSig;
                    using (var sigCmd = conn.CreateCommand())
                    {
                        sigCmd.CommandText = "SELECT SignatureHash FROM Projects WHERE Id = @id LIMIT 1;";
                        sigCmd.Parameters.AddWithValue("@id", projectId.Value);
                        prevSig = sigCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                    }

                    var storedSources = LoadStoredSourceFiles(conn, null, projectId.Value);
                    if (storedSources.Count == 0 || !HasV16SourceFileMetadata(storedSources))
                    {
                        return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
                    }

                    var scanLookup = BuildScanFileLookup(folderPath, newScanResult, _caseSensitivePaths);
                    PartitionStoredSourcesForDelta(
                        folderPath,
                        storedSources,
                        scanLookup,
                        _caseSensitivePaths,
                        out var unchangedIds,
                        out var dirtyIds,
                        out var dirtyRatio);

                    var plan = new IncrementalProjectDeltaPlan
                    {
                        ProjectId = projectId.Value,
                        PreviousSignature = prevSig,
                        CanApplyIncremental = unchangedIds.Count > 0,
                        RequiresFullSemanticRebuild = dirtyRatio > 0.25,
                        DirtyRatio = dirtyRatio
                    };
                    plan.UnchangedSourceFileIds.AddRange(unchangedIds);
                    plan.DirtySourceFileIds.AddRange(dirtyIds);

                    var seenUnchangedPaths = new HashSet<string>(_pathComparer);
                    var seenStoredRelPaths = new HashSet<string>(_pathComparer);
                    var seenReindexPaths = new HashSet<string>(_pathComparer);
                    var seenDeletedPaths = new HashSet<string>(_pathComparer);

                    foreach (var src in storedSources)
                    {
                        var relKey = ResolveStoredSourceRelativeKey(folderPath, src, _caseSensitivePaths);
                        seenStoredRelPaths.Add(relKey);

                        if (unchangedIds.Contains(src.Id))
                        {
                            if (!string.IsNullOrWhiteSpace(src.OriginalPath) && seenUnchangedPaths.Add(src.OriginalPath))
                            {
                                plan.UnchangedFilePaths.Add(src.OriginalPath);
                            }
                        }
                        else if (scanLookup.ByRelativeKey.TryGetValue(relKey, out var currentFile))
                        {
                            if (seenReindexPaths.Add(currentFile.File.Path))
                            {
                                plan.FilesToReindex.Add(currentFile.File.Path);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(src.OriginalPath) && seenDeletedPaths.Add(src.OriginalPath))
                        {
                            plan.DeletedFilePaths.Add(src.OriginalPath);
                        }
                    }

                    foreach (var file in newScanResult.Files)
                    {
                        var relKey = NormalizeRelativeManifestPath(scanLookup.RootFullPath, file.Path, _caseSensitivePaths);
                        if (!seenStoredRelPaths.Contains(relKey)
                            && (file.Kind == ProjectFileKind.UnityBundle || file.Kind == ProjectFileKind.SerializedFile)
                            && seenReindexPaths.Add(file.Path))
                        {
                            plan.FilesToReindex.Add(file.Path);
                        }
                    }

                    // Collect the exact set of files whose semantic relations are affected (reindexed files +
                    // unchanged files that reference a dirty source via SourceFileExternals) when an incremental
                    // semantic refresh is feasible.
                    if (!plan.RequiresFullSemanticRebuild)
                    {
                        var seenAffectedSemantic = new HashSet<string>(_pathComparer);
                        for (var i = 0; i < plan.FilesToReindex.Count; i++)
                        {
                            if (seenAffectedSemantic.Add(plan.FilesToReindex[i]))
                            {
                                plan.AffectedSemanticFilePaths.Add(plan.FilesToReindex[i]);
                            }
                        }

                        if (dirtyIds.Count > 0)
                        {
                            CollectDependentSemanticSourcePaths(
                                conn,
                                projectId.Value,
                                storedSources,
                                dirtyIds,
                                unchangedIds,
                                seenAffectedSemantic,
                                plan.AffectedSemanticFilePaths);
                        }
                    }

                    return plan;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while planning incremental index delta: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
            }
            catch (Exception ex)
            {
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to plan incremental index delta: {ex.Message}");
                return new IncrementalProjectDeltaPlan { CanApplyIncremental = false };
            }
        }

        internal bool TryLoadUnchangedAssetsForIncrementalUpdate(
            string folderPath,
            IncrementalProjectDeltaPlan plan,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? unchangedSourceFiles)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(targetIndex);
            unchangedSourceFiles = null;

            if (!plan.CanApplyIncremental || plan.UnchangedSourceFileIds.Count == 0)
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var unchangedSet = new HashSet<long>(plan.UnchangedSourceFileIds);
                    var allSources = new List<SourceFileMetadata>();
                    var origins = LoadSourceFileOriginsForProject(conn, plan.ProjectId, allSources);
                    var filteredSources = new List<SourceFileMetadata>(unchangedSet.Count);
                    var unchangedOrigins = new Dictionary<long, AssetHandleOrigin>(unchangedSet.Count);
                    var allowedSourcePairs = new HashSet<(string SerializedFileName, string OriginalPath)>(
                        _caseSensitivePaths ? SourceFileTupleComparer.Ordinal : SourceFileTupleComparer.OrdinalIgnoreCase);
                    foreach (var id in unchangedSet)
                    {
                        if (origins.TryGetValue(id, out var origin))
                        {
                            unchangedOrigins[id] = origin;
                            filteredSources.Add(new SourceFileMetadata(origin.SerializedFileName, origin.OriginalPath));
                            allowedSourcePairs.Add((origin.SerializedFileName, origin.OriginalPath));
                        }
                    }

                    // Fast path: hydrate unchanged handles straight from the verified mmap .idx sidecar
                    // matching plan.PreviousSignature (orders of magnitude faster than SQLite row scanning on 7M+ assets).
                    var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
                    if (!string.IsNullOrWhiteSpace(plan.PreviousSignature)
                        && MmapProjectIndexSidecar.TryLoadFilteredIntoArena(
                            idxPath,
                            plan.PreviousSignature,
                            SemanticSchemaVersion,
                            allowedSourcePairs,
                            targetIndex,
                            out var mmapFilteredSources))
                    {
                        unchangedSourceFiles = mmapFilteredSources ?? filteredSources;
                        return targetIndex.Count > 0;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                        FROM Assets
                        WHERE ProjectId = @projectId";
                    cmd.Parameters.AddWithValue("@projectId", plan.ProjectId);

                    var loaded = 0;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(4))
                        {
                            continue;
                        }

                        var srcId = reader.GetInt64(4);
                        if (!unchangedOrigins.TryGetValue(srcId, out var origin))
                        {
                            continue;
                        }

                        var uniqueId = reader.GetString(0);
                        var name = reader.GetString(1);
                        var type = (ClassIDType)reader.GetInt32(2);
                        var container = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                        var pathId = reader.GetInt64(5);
                        var byteStart = reader.GetInt64(6);
                        var byteSize = reader.GetInt64(7);

                        string? customUniqueId = uniqueId;
                        if (!ReferenceEquals(origin, AssetHandleOrigin.Empty)
                            && !string.IsNullOrEmpty(uniqueId)
                            && IsCanonicalUniqueIdForOrigin(uniqueId.AsSpan(), origin.UniqueIdPrefix.AsSpan(), pathId))
                        {
                            customUniqueId = null;
                        }

                        targetIndex.AddRecord(
                            serializedFileName: origin.SerializedFileName,
                            originalPath: origin.OriginalPath,
                            pathId: pathId,
                            name: name,
                            type: type,
                            container: container,
                            byteStart: byteStart,
                            byteSize: byteSize,
                            sourceHash: origin.SourceHash,
                            unityVersion: origin.UnityVersion,
                            customUniqueId: customUniqueId);
                        loaded++;
                    }

                    unchangedSourceFiles = filteredSources;
                    return loaded > 0;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                targetIndex.Clear();
                unchangedSourceFiles = null;
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while loading unchanged assets for incremental update: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return false;
            }
            catch (Exception ex)
            {
                targetIndex.Clear();
                unchangedSourceFiles = null;
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to load unchanged assets for incremental update: {ex.Message}");
                return false;
            }
        }

        internal bool SaveIncrementalIndexDelta(
            string folderPath,
            string newSignature,
            ProjectScanResult scanResult,
            string unityVersion,
            IncrementalProjectDeltaPlan deltaPlan,
            IReadOnlyCollection<AssetHandle> updatedHandles,
            IReadOnlyCollection<AssetHandle>? allMergedHandles = null,
            bool markIndexingCompleted = false,
            Action<int, int, string>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(deltaPlan);
            ArgumentNullException.ThrowIfNull(scanResult);

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var idxPath = MmapProjectIndexSidecar.GetSidecarPath(dbPath);
            var stagedIdxPath = string.Empty;
            try
            {
                var deltaHandlesList = updatedHandles?.ToList() ?? new List<AssetHandle>();
                var hasLargeAssetDelta = allMergedHandles != null
                    && allMergedHandles.Count >= 250_000
                    && deltaHandlesList.Count >= 100_000
                    && deltaHandlesList.Count >= allMergedHandles.Count * 0.40;

                // Pre-normalize merged handles once outside WriteGate when using fast O(1) recreate + ordered bulk insert.
                List<AssetHandle>? preOrderedMergedHandles = null;
                if (hasLargeAssetDelta)
                {
                    var totalBulkWork = Math.Max(1, allMergedHandles!.Count + Math.Max(1, scanResult.TotalFiles));
                    var prepReporter = new ThrottledProgressReporter(totalBulkWork, progress);
                    prepReporter.Report("Sorting & deduplicating handles", force: true);
                    preOrderedMergedHandles = MmapProjectIndexSidecar.NormalizeAndDeduplicateHandles(allMergedHandles);
                }

                var totalWork = preOrderedMergedHandles != null
                    ? Math.Max(1, preOrderedMergedHandles.Count + Math.Max(1, scanResult.TotalFiles))
                    : Math.Max(1, deltaHandlesList.Count + deltaPlan.DirtySourceFileIds.Count + 2);
                var reporter = new ThrottledProgressReporter(totalWork, progress);

                reporter.Report("Waiting for SQLite writer", force: true);
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    InvalidateProjectIdCacheForDb(dbPath);
                    var scanLookup = BuildScanFileLookup(folderPath, scanResult, _caseSensitivePaths);
                    long committedProjectId;
                    var usedBulkRecreate = false;

                    SetSynchronousMode(conn, "OFF");
                    // Disable FK enforcement inside this atomic transaction so deleting dirty SourceFiles
                    // does NOT trigger an O(M * N) full-table scan of unindexed Assets(SourceFileId) for
                    // ON DELETE SET NULL; all dirty Assets/Externals/Scenes/Previews are explicitly deleted here.
                    SetForeignKeysMode(conn, enabled: false);
                    try
                    {
                        using (var transaction = conn.BeginTransaction())
                        {
                            reporter.Report("Preparing SQLite project row", force: true);
                            var projectId = EnsureProject(
                                conn,
                                transaction,
                                folderPath,
                                newSignature,
                                scanResult,
                                _caseSensitivePaths,
                                out _,
                                unityVersion);

                            usedBulkRecreate = preOrderedMergedHandles != null
                                && IsSingleProjectDatabase(conn, transaction, projectId);

                            if (usedBulkRecreate)
                            {
                                reporter.Report("Clearing stale preview & semantic rows", force: true);
                                DeleteDirtyPreviewCacheEntries(conn, transaction, projectId, deltaPlan.DirtySourceFileIds);
                                if (!deltaPlan.RequiresFullSemanticRebuild && deltaPlan.DirtySourceFileIds.Count > 0)
                                {
                                    DeleteDirtySourceRowsAndAffectedData(
                                        conn,
                                        transaction,
                                        projectId,
                                        deltaPlan.DirtySourceFileIds,
                                        preserveSemanticRelations: false,
                                        requiresFullSemanticClear: false,
                                        skipAssetsAndSourceFilesDelete: true,
                                        skipPreviewDelete: true,
                                        reportStage: stage => reporter.Report(stage, force: true));
                                }

                                reporter.Report("Resetting index tables (O(1))", force: true);
                                RecreateIndexTablesInTransaction(
                                    conn,
                                    transaction,
                                    includeSemanticTables: deltaPlan.RequiresFullSemanticRebuild);

                                InsertSourceFilesAndAssets(
                                    conn,
                                    transaction,
                                    projectId,
                                    folderPath,
                                    scanLookup,
                                    preOrderedMergedHandles!,
                                    _caseSensitivePaths,
                                    reporter.Advance,
                                    isAlreadyOrderedByUniqueId: true,
                                    isFreshInsert: true);
                            }
                            else
                            {
                                reporter.Report("Updating changed index rows", force: true);
                                DeleteDirtySourceRowsAndAffectedData(
                                    conn,
                                    transaction,
                                    projectId,
                                    deltaPlan.DirtySourceFileIds,
                                    preserveSemanticRelations: false,
                                    requiresFullSemanticClear: deltaPlan.RequiresFullSemanticRebuild,
                                    reportStage: stage => reporter.Report(stage, force: true));

                                if (deltaHandlesList.Count > 0)
                                {
                                    InsertSourceFilesAndAssets(
                                        conn,
                                        transaction,
                                        projectId,
                                        folderPath,
                                        scanLookup,
                                        deltaHandlesList,
                                        _caseSensitivePaths,
                                        reporter.Advance);
                                }
                            }

                            if (markIndexingCompleted)
                            {
                                var completedFiles = Math.Max(1, scanResult.TotalFiles);
                                SaveProjectIndexingState(
                                    conn,
                                    transaction,
                                    projectId,
                                    "completed",
                                    completedFiles,
                                    completedFiles,
                                    "Incremental SQLite index cache saved",
                                    completed: true);
                            }

                            MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                            reporter.Report("Committing incremental SQLite delta", force: true);
                            transaction.Commit();
                            committedProjectId = projectId;
                        }
                    }
                    finally
                    {
                        SetForeignKeysMode(conn, enabled: true);
                        SetSynchronousMode(conn, "NORMAL");
                    }

                    CacheProjectId(dbPath, folderPath, newSignature, committedProjectId);
                    NotifyDatabaseMutated(dbPath);

                    reporter.Report("Publishing updated .idx sidecar", force: true);
                    var mergedHandlesForSidecar = preOrderedMergedHandles
                        ?? allMergedHandles
                        ?? LoadAssetHandlesForProject(conn, committedProjectId, out _);
                    var stagedOk = MmapProjectIndexSidecar.TryStageSidecar(
                        idxPath,
                        newSignature,
                        SemanticSchemaVersion,
                        mergedHandlesForSidecar,
                        _caseSensitivePaths,
                        out stagedIdxPath,
                        out _,
                        beforeValidateHook: null,
                        isAlreadyNormalizedAndOrdered: preOrderedMergedHandles != null);

                    if (stagedOk && MmapProjectIndexSidecar.TryPublishStagedSidecar(stagedIdxPath, idxPath))
                    {
                        stagedIdxPath = string.Empty;
                    }
                    else
                    {
                        MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                        Logger.Warning($"Failed to publish incremental mmap .idx sidecar for '{folderPath}'; deleted stale sidecar.");
                    }

                    MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
                    reporter.Report("Checkpointing SQLite WAL", force: true);
                    if (usedBulkRecreate)
                    {
                        TryAnalyzeDatabase(conn, "incremental index cache");
                    }
                    else
                    {
                        TryOptimizeDatabase(conn, "incremental index cache");
                    }
                    TryCheckpointWal(conn, "incremental index cache");
                    reporter.MarkCompleted("Incremental SQLite index cache saved");
                    reporter.Report("Incremental SQLite index cache saved", force: true);
                    return true;
                }
            }
            catch (SqliteException ex) when (IsSqliteCorruptionException(ex))
            {
                MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                TryRecoverCorruptDatabase(dbPath, $"SQLite corruption while saving incremental index delta: {ex.Message}", ex);
                EnsureInitializedByDbPath(dbPath);
                return false;
            }
            catch (Exception ex)
            {
                MmapProjectIndexSidecar.TryDeleteFileQuietly(idxPath);
                RecordClassifiedException("SqliteIndex", dbPath, ex);
                Logger.Warning($"Failed to save incremental SQLite index delta: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(stagedIdxPath))
                {
                    MmapProjectIndexSidecar.TryDeleteFileQuietly(stagedIdxPath);
                }

                MmapProjectIndexSidecar.CleanupOrphanedTempSidecars(idxPath);
            }
        }

        private static string? LoadProjectIndexingStatus(SqliteConnection conn, long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Status
                FROM ProjectIndexingState
                WHERE ProjectId = @projectId
                LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return cmd.ExecuteScalar()?.ToString();
        }

        private static bool IsIncompleteIndexCacheStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            return string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "saving_index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
        }

        private static long EnsureProject(
            SqliteConnection conn,
            SqliteTransaction transaction,
            string folderPath,
            string signature,
            ProjectScanResult scanResult,
            bool caseSensitivePaths,
            out bool signatureChanged,
            string? unityVersion = null)
        {
            var folderPathKey = NormalizeFolderPathKey(folderPath, caseSensitivePaths);
            var sigVersion = signature.StartsWith("v2:", StringComparison.Ordinal) ? 2 : 1;

            var existingProjectId = FindProjectId(conn, transaction, folderPath, signature, caseSensitivePaths);
            if (existingProjectId != null)
            {
                signatureChanged = false;
                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"
                    UPDATE Projects
                    SET FolderPath = @folderPath,
                        FolderPathKey = @folderPathKey,
                        SignatureVersion = @signatureVersion,
                        TotalFiles = @totalFiles,
                        TotalBytes = @totalBytes,
                        UnityBundleCount = @unityBundles,
                        UnityVersion = @unityVersion,
                        LastIndexed = CURRENT_TIMESTAMP
                    WHERE Id = @projectId";
                updateCmd.Parameters.AddWithValue("@projectId", existingProjectId.Value);
                updateCmd.Parameters.AddWithValue("@folderPath", folderPath);
                updateCmd.Parameters.AddWithValue("@folderPathKey", folderPathKey);
                updateCmd.Parameters.AddWithValue("@signatureVersion", sigVersion);
                updateCmd.Parameters.AddWithValue("@totalFiles", scanResult.TotalFiles);
                updateCmd.Parameters.AddWithValue("@totalBytes", scanResult.TotalBytes);
                updateCmd.Parameters.AddWithValue("@unityBundles", scanResult.UnityBundleCount);
                updateCmd.Parameters.AddWithValue("@unityVersion", unityVersion ?? string.Empty);
                updateCmd.ExecuteNonQuery();
                return existingProjectId.Value;
            }

            // PERF-03: Preserve the existing Projects row (and its ProjectId + valid PreviewCacheEntries)
            // in-place on signature mismatch instead of cascading DELETE FROM Projects.
            var previousProjectId = FindLatestProjectId(conn, transaction, folderPath, caseSensitivePaths);
            if (previousProjectId != null)
            {
                signatureChanged = true;

                // Clean up any duplicate legacy rows for the same FolderPathKey while preserving previousProjectId.
                using (var dedupCmd = conn.CreateCommand())
                {
                    dedupCmd.Transaction = transaction;
                    dedupCmd.CommandText = @"
                        DELETE FROM Projects
                        WHERE FolderPathKey = @folderPathKey
                          AND Id <> @keepId";
                    dedupCmd.Parameters.AddWithValue("@folderPathKey", folderPathKey);
                    dedupCmd.Parameters.AddWithValue("@keepId", previousProjectId.Value);
                    dedupCmd.ExecuteNonQuery();
                }

                using var updateInPlaceCmd = conn.CreateCommand();
                updateInPlaceCmd.Transaction = transaction;
                updateInPlaceCmd.CommandText = @"
                    UPDATE Projects
                    SET FolderPath = @folderPath,
                        FolderPathKey = @folderPathKey,
                        SignatureHash = @signature,
                        SignatureVersion = @signatureVersion,
                        TotalFiles = @totalFiles,
                        TotalBytes = @totalBytes,
                        UnityBundleCount = @unityBundles,
                        UnityVersion = COALESCE(NULLIF(@unityVersion, ''), UnityVersion),
                        LastIndexed = CURRENT_TIMESTAMP
                    WHERE Id = @projectId";
                updateInPlaceCmd.Parameters.AddWithValue("@projectId", previousProjectId.Value);
                updateInPlaceCmd.Parameters.AddWithValue("@folderPath", folderPath);
                updateInPlaceCmd.Parameters.AddWithValue("@folderPathKey", folderPathKey);
                updateInPlaceCmd.Parameters.AddWithValue("@signature", signature);
                updateInPlaceCmd.Parameters.AddWithValue("@signatureVersion", sigVersion);
                updateInPlaceCmd.Parameters.AddWithValue("@totalFiles", scanResult.TotalFiles);
                updateInPlaceCmd.Parameters.AddWithValue("@totalBytes", scanResult.TotalBytes);
                updateInPlaceCmd.Parameters.AddWithValue("@unityBundles", scanResult.UnityBundleCount);
                updateInPlaceCmd.Parameters.AddWithValue("@unityVersion", unityVersion ?? string.Empty);
                updateInPlaceCmd.ExecuteNonQuery();
                return previousProjectId.Value;
            }

            signatureChanged = false;
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT INTO Projects (FolderPath, FolderPathKey, SignatureHash, SignatureVersion, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                VALUES (@path, @folderPathKey, @signature, @signatureVersion, @totalFiles, @totalBytes, @unityBundles, @unityVersion);
                SELECT last_insert_rowid();";
            insertCmd.Parameters.AddWithValue("@path", folderPath);
            insertCmd.Parameters.AddWithValue("@folderPathKey", folderPathKey);
            insertCmd.Parameters.AddWithValue("@signature", signature);
            insertCmd.Parameters.AddWithValue("@signatureVersion", sigVersion);
            insertCmd.Parameters.AddWithValue("@totalFiles", scanResult.TotalFiles);
            insertCmd.Parameters.AddWithValue("@totalBytes", scanResult.TotalBytes);
            insertCmd.Parameters.AddWithValue("@unityBundles", scanResult.UnityBundleCount);
            insertCmd.Parameters.AddWithValue("@unityVersion", unityVersion ?? string.Empty);
            return Convert.ToInt64(insertCmd.ExecuteScalar());
        }

        private static bool IsSingleProjectDatabase(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM Projects WHERE Id <> @projectId);";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
        }

        private static void ClearIndexTablesForProject(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            bool preserveSemanticRelations)
        {
            // Fast O(1) transactional table reset for single-project per-folder databases (avoids walking
            // millions of WITHOUT ROWID rows with DELETE ... WHERE and writing gigabytes to the WAL).
            if (IsSingleProjectDatabase(conn, transaction, projectId))
            {
                RecreateIndexTablesInTransaction(
                    conn,
                    transaction,
                    includeSemanticTables: !preserveSemanticRelations);
                return;
            }

            var tables = preserveSemanticRelations
                ? new[]
                {
                    "Scenes",
                    "SourceFileExternals",
                    "Assets",
                    "SourceFiles"
                }
                : new[]
                {
                    "Scenes",
                    "SourceFileExternals",
                    "AnimationClipTargets",
                    "CompositeAssetParts",
                    "CompositeAssets",
                    "MeshMaterialResolvedCache",
                    "MaterialCache",
                    "MaterialTextures",
                    "MeshMaterials",
                    "MeshRenderers",
                    "ModelGroupMeshes",
                    "ModelGroups",
                    "AssetEdges",
                    "Assets",
                    "SourceFiles"
                };

            foreach (var table in tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table} WHERE ProjectId = @projectId";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.ExecuteNonQuery();
            }
        }

        private readonly struct ScanFileLookup
        {
            public ScanFileLookup(
                string rootFullPath,
                Dictionary<string, (ScannedFileInfo File, string RelativePath)> byFullKey,
                Dictionary<string, (ScannedFileInfo File, string RelativePath)> byRelativeKey)
            {
                RootFullPath = rootFullPath;
                ByFullKey = byFullKey;
                ByRelativeKey = byRelativeKey;
            }

            public string RootFullPath { get; }
            public Dictionary<string, (ScannedFileInfo File, string RelativePath)> ByFullKey { get; }
            public Dictionary<string, (ScannedFileInfo File, string RelativePath)> ByRelativeKey { get; }
        }

        private static ScanFileLookup BuildScanFileLookup(string folderPath, ProjectScanResult scanResult, bool caseSensitivePaths)
        {
            var comparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var rootSource = !string.IsNullOrWhiteSpace(scanResult.RootPath) ? scanResult.RootPath : folderPath;
            var rootFull = string.IsNullOrWhiteSpace(rootSource)
                ? string.Empty
                : TrimTrailingDirectorySeparatorsPreservingRoot(GetFullPathOrOriginal(rootSource));

            var byFullKey = new Dictionary<string, (ScannedFileInfo, string)>(scanResult.Files.Count, comparer);
            var byRelativeKey = new Dictionary<string, (ScannedFileInfo, string)>(scanResult.Files.Count, comparer);

            for (var i = 0; i < scanResult.Files.Count; i++)
            {
                var file = scanResult.Files[i];
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                var relNormalized = NormalizeRelativeManifestPath(rootFull, file.Path, caseSensitive: true);
                var relLookupKey = NormalizeRelativeManifestPath(rootFull, file.Path, caseSensitivePaths);
                var fullKey = NormalizeFolderPathKey(file.Path, caseSensitivePaths);
                byFullKey[fullKey] = (file, relNormalized);
                byRelativeKey[relLookupKey] = (file, relNormalized);
            }

            return new ScanFileLookup(rootFull, byFullKey, byRelativeKey);
        }

        private static bool TryResolveScanFileMetadata(
            ScanFileLookup lookup,
            string originalPath,
            bool caseSensitivePaths,
            out ScannedFileInfo fileInfo,
            out string relativePath)
        {
            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                var fullKey = NormalizeFolderPathKey(originalPath, caseSensitivePaths);
                if (lookup.ByFullKey.TryGetValue(fullKey, out var entry))
                {
                    fileInfo = entry.File;
                    relativePath = entry.RelativePath;
                    return true;
                }

                var relKey = NormalizeRelativeManifestPath(lookup.RootFullPath, originalPath, caseSensitivePaths);
                if (lookup.ByRelativeKey.TryGetValue(relKey, out entry))
                {
                    fileInfo = entry.File;
                    relativePath = entry.RelativePath;
                    return true;
                }
            }

            fileInfo = null!;
            relativePath = string.IsNullOrWhiteSpace(originalPath)
                ? string.Empty
                : NormalizeRelativeManifestPath(lookup.RootFullPath, originalPath, caseSensitive: true);
            return false;
        }

        private static List<StoredSourceFileEntry> LoadStoredSourceFiles(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            long projectId)
        {
            var result = new List<StoredSourceFileEntry>();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id, SerializedFileName, OriginalPath, RelativePath, FileSize, LastWriteUtcTicks, ContentSampleHash, FileKind
                FROM SourceFiles
                WHERE ProjectId = @projectId";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new StoredSourceFileEntry
                {
                    Id = reader.GetInt64(0),
                    SerializedFileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    OriginalPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    RelativePath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    FileSize = reader.IsDBNull(4) ? 0L : reader.GetInt64(4),
                    LastWriteUtcTicks = reader.IsDBNull(5) ? 0L : reader.GetInt64(5),
                    ContentSampleHash = reader.IsDBNull(6) ? 0UL : unchecked((ulong)reader.GetInt64(6)),
                    FileKind = reader.IsDBNull(7) ? ProjectFileKind.Other : (ProjectFileKind)reader.GetInt32(7)
                });
            }

            return result;
        }

        private static bool HasV16SourceFileMetadata(IReadOnlyList<StoredSourceFileEntry> storedSources)
        {
            for (var i = 0; i < storedSources.Count; i++)
            {
                var s = storedSources[i];
                if (!string.IsNullOrEmpty(s.RelativePath) || s.FileSize > 0 || s.LastWriteUtcTicks > 0 || s.ContentSampleHash != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ResolveStoredSourceRelativeKey(string folderPath, StoredSourceFileEntry source, bool caseSensitivePaths)
        {
            if (!string.IsNullOrWhiteSpace(source.RelativePath))
            {
                var rel = source.RelativePath.Replace('\\', '/');
                return caseSensitivePaths ? rel : rel.ToUpperInvariant();
            }

            var rootFull = string.IsNullOrWhiteSpace(folderPath)
                ? string.Empty
                : TrimTrailingDirectorySeparatorsPreservingRoot(GetFullPathOrOriginal(folderPath));
            return NormalizeRelativeManifestPath(rootFull, source.OriginalPath, caseSensitivePaths);
        }

        private static void PartitionStoredSourcesForDelta(
            string folderPath,
            IReadOnlyList<StoredSourceFileEntry> storedSources,
            ScanFileLookup scanLookup,
            bool caseSensitivePaths,
            out HashSet<long> unchangedSourceIds,
            out List<long> dirtySourceIds,
            out double dirtyRatio)
        {
            unchangedSourceIds = new HashSet<long>();
            dirtySourceIds = new List<long>();
            var comparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var matchedScanRelKeys = new HashSet<string>(comparer);

            for (var i = 0; i < storedSources.Count; i++)
            {
                var src = storedSources[i];
                var relKey = ResolveStoredSourceRelativeKey(folderPath, src, caseSensitivePaths);
                var hasMetadata = !string.IsNullOrEmpty(src.RelativePath) || src.FileSize > 0 || src.LastWriteUtcTicks > 0 || src.ContentSampleHash != 0;

                if (hasMetadata
                    && scanLookup.ByRelativeKey.TryGetValue(relKey, out var current)
                    && current.File.Size == src.FileSize
                    && current.File.LastWriteTimeUtcTicks == src.LastWriteUtcTicks
                    && current.File.ContentSampleHash == src.ContentSampleHash
                    && current.File.Kind == src.FileKind)
                {
                    unchangedSourceIds.Add(src.Id);
                    matchedScanRelKeys.Add(relKey);
                }
                else
                {
                    dirtySourceIds.Add(src.Id);
                    if (scanLookup.ByRelativeKey.ContainsKey(relKey))
                    {
                        matchedScanRelKeys.Add(relKey);
                    }
                }
            }

            var addedAssetFiles = 0;
            foreach (var kvp in scanLookup.ByRelativeKey)
            {
                if (!matchedScanRelKeys.Contains(kvp.Key)
                    && (kvp.Value.File.Kind == ProjectFileKind.UnityBundle || kvp.Value.File.Kind == ProjectFileKind.SerializedFile))
                {
                    addedAssetFiles++;
                }
            }

            var denominator = Math.Max(1, storedSources.Count);
            dirtyRatio = (double)(dirtySourceIds.Count + addedAssetFiles) / denominator;
        }

        private void InvalidateStalePreviewsForChangedProject(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string folderPath,
            ProjectScanResult scanResult,
            bool signatureChanged,
            IReadOnlyList<StoredSourceFileEntry>? existingSourcesOverride = null,
            ScanFileLookup? scanLookupOverride = null)
        {
            var existingSources = existingSourcesOverride ?? LoadStoredSourceFiles(conn, transaction, projectId);
            if (existingSources.Count > 0)
            {
                if (HasV16SourceFileMetadata(existingSources))
                {
                    var scanLookup = scanLookupOverride ?? BuildScanFileLookup(folderPath, scanResult, _caseSensitivePaths);
                    PartitionStoredSourcesForDelta(
                        folderPath,
                        existingSources,
                        scanLookup,
                        _caseSensitivePaths,
                        out _,
                        out var dirtySourceIds,
                        out _);

                    if (dirtySourceIds.Count > 0)
                    {
                        DeleteDirtyPreviewCacheEntries(conn, transaction, projectId, dirtySourceIds);
                    }
                }
                else if (signatureChanged)
                {
                    var allExistingSourceIds = existingSources.Select(static s => s.Id).ToList();
                    DeleteDirtyPreviewCacheEntries(conn, transaction, projectId, allExistingSourceIds);
                }
            }
            else if (signatureChanged)
            {
                using var delAllCmd = conn.CreateCommand();
                delAllCmd.Transaction = transaction;
                delAllCmd.CommandText = "DELETE FROM PreviewCacheEntries WHERE ProjectId = @projectId;";
                delAllCmd.Parameters.AddWithValue("@projectId", projectId);
                delAllCmd.ExecuteNonQuery();
            }
        }

        private static void CollectDependentSemanticSourcePaths(
            SqliteConnection conn,
            long projectId,
            IReadOnlyList<StoredSourceFileEntry> storedSources,
            IReadOnlyList<long> dirtySourceIds,
            HashSet<long> unchangedSourceIds,
            HashSet<string> seenAffectedPaths,
            List<string> affectedPaths)
        {
            if (dirtySourceIds.Count == 0 || unchangedSourceIds.Count == 0)
            {
                return;
            }

            var dirtySet = new HashSet<long>(dirtySourceIds);
            var dirtyFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirtyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceById = new Dictionary<long, StoredSourceFileEntry>(storedSources.Count);
            for (var i = 0; i < storedSources.Count; i++)
            {
                var src = storedSources[i];
                sourceById[src.Id] = src;
                if (dirtySet.Contains(src.Id))
                {
                    if (!string.IsNullOrEmpty(src.SerializedFileName))
                    {
                        dirtyFileNames.Add(src.SerializedFileName);
                    }
                    if (!string.IsNullOrEmpty(src.OriginalPath))
                    {
                        dirtyPaths.Add(src.OriginalPath);
                    }
                    if (!string.IsNullOrEmpty(src.RelativePath))
                    {
                        dirtyPaths.Add(src.RelativePath);
                    }
                }
            }

            if (dirtyFileNames.Count == 0 && dirtyPaths.Count == 0)
            {
                return;
            }

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT SourceFileId, ExternalFileName, ExternalPathName
                    FROM SourceFileExternals
                    WHERE ProjectId = @projectId;";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var srcId = reader.GetInt64(0);
                    if (!unchangedSourceIds.Contains(srcId))
                    {
                        continue;
                    }

                    var extName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var extPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var referencesDirty = (!string.IsNullOrEmpty(extName) && dirtyFileNames.Contains(extName))
                        || (!string.IsNullOrEmpty(extPath) && dirtyPaths.Contains(extPath));
                    if (referencesDirty
                        && sourceById.TryGetValue(srcId, out var dependentSrc)
                        && !string.IsNullOrWhiteSpace(dependentSrc.OriginalPath)
                        && seenAffectedPaths.Add(dependentSrc.OriginalPath))
                    {
                        affectedPaths.Add(dependentSrc.OriginalPath);
                    }
                }
            }
            catch (SqliteException)
            {
                // Older DB without SourceFileExternals; safely skip dependent path collection.
            }
        }

        private static bool HasAnyPreviewCacheEntries(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            long projectId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM PreviewCacheEntries WHERE ProjectId = @projectId LIMIT 1);";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }

        private static void DeleteDirtyPreviewCacheEntries(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyCollection<long> dirtySourceIds)
        {
            if (dirtySourceIds.Count == 0 || !HasAnyPreviewCacheEntries(conn, transaction, projectId))
            {
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE TEMP TABLE IF NOT EXISTS _delta_dirty_preview_sources (Id INTEGER PRIMARY KEY);
                DELETE FROM temp._delta_dirty_preview_sources;";
            cmd.ExecuteNonQuery();

            using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT OR IGNORE INTO temp._delta_dirty_preview_sources (Id) VALUES (@id);";
                var pId = insertCmd.Parameters.Add("@id", SqliteType.Integer);
                foreach (var id in dirtySourceIds)
                {
                    pId.Value = id;
                    insertCmd.ExecuteNonQuery();
                }
            }

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = transaction;
                delCmd.CommandText = @"
                    DELETE FROM PreviewCacheEntries
                    WHERE ProjectId = @projectId
                      AND AssetUniqueID IN (
                          SELECT UniqueID
                          FROM Assets
                          WHERE ProjectId = @projectId
                            AND SourceFileId IN (SELECT Id FROM temp._delta_dirty_preview_sources)
                      );
                    DROP TABLE IF EXISTS temp._delta_dirty_preview_sources;";
                delCmd.Parameters.AddWithValue("@projectId", projectId);
                delCmd.ExecuteNonQuery();
            }
        }

        private static void DeleteDirtySourceRowsAndAffectedData(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyCollection<long> dirtySourceIds,
            bool preserveSemanticRelations,
            bool requiresFullSemanticClear,
            bool skipAssetsAndSourceFilesDelete = false,
            bool skipPreviewDelete = false,
            Action<string>? reportStage = null)
        {
            if (dirtySourceIds.Count == 0 && (!requiresFullSemanticClear || preserveSemanticRelations))
            {
                return;
            }

            var needsPreviewCleanup = !skipPreviewDelete
                && dirtySourceIds.Count > 0
                && HasAnyPreviewCacheEntries(conn, transaction, projectId);
            var needsTargetedSemanticCleanup = !preserveSemanticRelations
                && !requiresFullSemanticClear
                && dirtySourceIds.Count > 0;

            using (var setupCmd = conn.CreateCommand())
            {
                setupCmd.Transaction = transaction;
                setupCmd.CommandText = @"
                    CREATE TEMP TABLE IF NOT EXISTS _delta_dirty_sources (Id INTEGER PRIMARY KEY);
                    CREATE TEMP TABLE IF NOT EXISTS _delta_semantic_sources (Id INTEGER PRIMARY KEY);
                    CREATE TEMP TABLE IF NOT EXISTS _delta_dirty_assets (UniqueID TEXT PRIMARY KEY) WITHOUT ROWID;
                    CREATE TEMP TABLE IF NOT EXISTS _delta_semantic_assets (UniqueID TEXT PRIMARY KEY) WITHOUT ROWID;
                    DELETE FROM temp._delta_dirty_sources;
                    DELETE FROM temp._delta_semantic_sources;
                    DELETE FROM temp._delta_dirty_assets;
                    DELETE FROM temp._delta_semantic_assets;";
                setupCmd.ExecuteNonQuery();
            }

            try
            {
                if (dirtySourceIds.Count > 0)
                {
                    using (var insertSrcCmd = conn.CreateCommand())
                    {
                        insertSrcCmd.Transaction = transaction;
                        insertSrcCmd.CommandText = "INSERT OR IGNORE INTO temp._delta_dirty_sources (Id) VALUES (@id);";
                        var pId = insertSrcCmd.Parameters.Add("@id", SqliteType.Integer);
                        foreach (var id in dirtySourceIds)
                        {
                            pId.Value = id;
                            insertSrcCmd.ExecuteNonQuery();
                        }
                    }

                    // Only scan Assets to build dirty/semantic UniqueID sets when previews or targeted semantic cleanup actually need them.
                    if (needsPreviewCleanup)
                    {
                        reportStage?.Invoke("Collecting stale preview entries");
                        using var collectDirtyCmd = conn.CreateCommand();
                        collectDirtyCmd.Transaction = transaction;
                        collectDirtyCmd.CommandText = @"
                            INSERT OR IGNORE INTO temp._delta_dirty_assets (UniqueID)
                            SELECT UniqueID
                            FROM Assets
                            WHERE ProjectId = @projectId
                              AND SourceFileId IN (SELECT Id FROM temp._delta_dirty_sources);";
                        collectDirtyCmd.Parameters.AddWithValue("@projectId", projectId);
                        collectDirtyCmd.ExecuteNonQuery();
                    }

                    if (needsTargetedSemanticCleanup)
                    {
                        reportStage?.Invoke("Collecting affected semantic relations");
                        using var collectSemCmd = conn.CreateCommand();
                        collectSemCmd.Transaction = transaction;
                        collectSemCmd.CommandText = @"
                            INSERT OR IGNORE INTO temp._delta_semantic_sources (Id)
                            SELECT Id FROM temp._delta_dirty_sources;

                            -- Include unchanged SourceFiles that reference any dirty source file via m_Externals.
                            INSERT OR IGNORE INTO temp._delta_semantic_sources (Id)
                            SELECT DISTINCT ext.SourceFileId
                            FROM SourceFileExternals ext
                            JOIN SourceFiles dirty
                              ON dirty.ProjectId = @projectId
                             AND dirty.Id IN (SELECT Id FROM temp._delta_dirty_sources)
                            WHERE ext.ProjectId = @projectId
                              AND (
                                  (ext.ExternalFileName <> '' AND ext.ExternalFileName = dirty.SerializedFileName)
                                  OR (ext.ExternalPathName <> '' AND (ext.ExternalPathName = dirty.OriginalPath OR ext.ExternalPathName = dirty.RelativePath))
                              );

                            INSERT OR IGNORE INTO temp._delta_semantic_assets (UniqueID)
                            SELECT UniqueID
                            FROM Assets
                            WHERE ProjectId = @projectId
                              AND SourceFileId IN (SELECT Id FROM temp._delta_semantic_sources);";
                        collectSemCmd.Parameters.AddWithValue("@projectId", projectId);
                        collectSemCmd.ExecuteNonQuery();
                    }

                    // 1. Delete dirty PreviewCacheEntries (preserving valid entries for unchanged bundles!)
                    if (needsPreviewCleanup)
                    {
                        using var delPrevCmd = conn.CreateCommand();
                        delPrevCmd.Transaction = transaction;
                        delPrevCmd.CommandText = @"
                            DELETE FROM PreviewCacheEntries
                            WHERE ProjectId = @projectId
                              AND AssetUniqueID IN (SELECT UniqueID FROM temp._delta_dirty_assets);";
                        delPrevCmd.Parameters.AddWithValue("@projectId", projectId);
                        delPrevCmd.ExecuteNonQuery();
                    }

                    // 2. Delete dirty SourceFileExternals and Scenes
                    if (!skipAssetsAndSourceFilesDelete)
                    {
                        using var delDirectCmd = conn.CreateCommand();
                        delDirectCmd.Transaction = transaction;
                        delDirectCmd.CommandText = @"
                            DELETE FROM SourceFileExternals
                            WHERE ProjectId = @projectId
                              AND SourceFileId IN (SELECT Id FROM temp._delta_dirty_sources);

                            DELETE FROM Scenes
                            WHERE ProjectId = @projectId
                              AND EntrySourceFileId IN (SELECT Id FROM temp._delta_dirty_sources);";
                        delDirectCmd.Parameters.AddWithValue("@projectId", projectId);
                        delDirectCmd.ExecuteNonQuery();
                    }
                }

                // 3. Clean semantic tables (O(1) recreate when >25% dirty on single-project DB, or targeted dirty+dependent edges)
                if (!preserveSemanticRelations)
                {
                    if (requiresFullSemanticClear)
                    {
                        reportStage?.Invoke("Resetting semantic relation tables");
                        if (IsSingleProjectDatabase(conn, transaction, projectId))
                        {
                            RecreateSemanticTablesInTransaction(conn, transaction, dropExistingFirst: true, createSecondaryIndexes: true);
                        }
                        else
                        {
                            var semanticTables = new[]
                            {
                                "AnimationClipTargets",
                                "CompositeAssetParts",
                                "CompositeAssets",
                                "MeshMaterialResolvedCache",
                                "MaterialCache",
                                "MaterialTextures",
                                "MeshMaterials",
                                "MeshRenderers",
                                "ModelGroupMeshes",
                                "ModelGroups",
                                "AssetEdges"
                            };
                            foreach (var table in semanticTables)
                            {
                                using var clearSemCmd = conn.CreateCommand();
                                clearSemCmd.Transaction = transaction;
                                clearSemCmd.CommandText = $"DELETE FROM {table} WHERE ProjectId = @projectId;";
                                clearSemCmd.Parameters.AddWithValue("@projectId", projectId);
                                clearSemCmd.ExecuteNonQuery();
                            }
                        }
                    }
                    else if (needsTargetedSemanticCleanup)
                    {
                        reportStage?.Invoke("Pruning affected semantic relations");
                        using var delSemCmd = conn.CreateCommand();
                        delSemCmd.Transaction = transaction;
                        delSemCmd.CommandText = @"
                            DELETE FROM AssetEdges
                            WHERE ProjectId = @projectId
                              AND (SourceAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR TargetAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM MeshRenderers
                            WHERE ProjectId = @projectId
                              AND (MeshAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR RendererAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM MeshMaterials
                            WHERE ProjectId = @projectId
                              AND (MeshAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR MaterialAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR RendererAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM MaterialTextures
                            WHERE ProjectId = @projectId
                              AND (MaterialAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR TextureAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM MaterialCache
                            WHERE ProjectId = @projectId
                              AND (MaterialAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR MainTextureAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM MeshMaterialResolvedCache
                            WHERE ProjectId = @projectId
                              AND (MeshAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR MaterialAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM CompositeAssetParts
                            WHERE ProjectId = @projectId
                              AND (CompositeAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR MeshAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM CompositeAssets
                            WHERE ProjectId = @projectId
                              AND (CompositeAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR RootGameObjectAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM ModelGroupMeshes
                            WHERE ProjectId = @projectId
                              AND (GroupAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR MeshAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM ModelGroups
                            WHERE ProjectId = @projectId
                              AND (GroupAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR RootGameObjectAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));

                            DELETE FROM AnimationClipTargets
                            WHERE ProjectId = @projectId
                              AND (ClipAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR AvatarAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets)
                                OR MeshAssetUniqueID IN (SELECT UniqueID FROM temp._delta_semantic_assets));";
                        delSemCmd.Parameters.AddWithValue("@projectId", projectId);
                        delSemCmd.ExecuteNonQuery();
                    }
                }

                // 4. Delete dirty Assets and dirty SourceFiles
                if (!skipAssetsAndSourceFilesDelete && dirtySourceIds.Count > 0)
                {
                    reportStage?.Invoke("Removing replaced index rows");
                    using var delAssetsCmd = conn.CreateCommand();
                    delAssetsCmd.Transaction = transaction;
                    delAssetsCmd.CommandText = @"
                        DELETE FROM Assets
                        WHERE ProjectId = @projectId
                          AND SourceFileId IN (SELECT Id FROM temp._delta_dirty_sources);

                        DELETE FROM SourceFiles
                        WHERE ProjectId = @projectId
                          AND Id IN (SELECT Id FROM temp._delta_dirty_sources);";
                    delAssetsCmd.Parameters.AddWithValue("@projectId", projectId);
                    delAssetsCmd.ExecuteNonQuery();
                }
            }
            finally
            {
                using var dropCmd = conn.CreateCommand();
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = @"
                    DROP TABLE IF EXISTS temp._delta_semantic_assets;
                    DROP TABLE IF EXISTS temp._delta_dirty_assets;
                    DROP TABLE IF EXISTS temp._delta_semantic_sources;
                    DROP TABLE IF EXISTS temp._delta_dirty_sources;";
                dropCmd.ExecuteNonQuery();
            }
        }

        private static void UpsertSourceFilesAndAssetsIncremental(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string folderPath,
            ScanFileLookup scanLookup,
            IReadOnlyList<StoredSourceFileEntry> existingSources,
            HashSet<long> unchangedSourceIds,
            IReadOnlyCollection<AssetHandle> handles,
            bool caseSensitivePaths,
            Action<string>? advanceProgress)
        {
            var comparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var unchangedSourceKeys = new HashSet<string>(comparer);
            foreach (var src in existingSources)
            {
                if (unchangedSourceIds.Contains(src.Id))
                {
                    unchangedSourceKeys.Add(GetSourceFileKey(src.SerializedFileName, src.OriginalPath));
                }
            }

            // Only insert/upsert handles whose source file is added or modified; unchanged SourceFiles and
            // Assets stay untouched in SQLite preserving their exact row IDs and pages.
            var deltaHandles = new List<AssetHandle>();
            foreach (var h in handles)
            {
                if (h == null)
                {
                    continue;
                }

                var key = GetSourceFileKey(h.SerializedFileName ?? string.Empty, h.OriginalPath ?? string.Empty);
                if (!unchangedSourceKeys.Contains(key))
                {
                    deltaHandles.Add(h);
                }
            }

            if (deltaHandles.Count > 0)
            {
                InsertSourceFilesAndAssets(conn, transaction, projectId, folderPath, scanLookup, deltaHandles, caseSensitivePaths, advanceProgress);
            }
        }

        private sealed class SourceFileTupleComparer : IEqualityComparer<(string SerializedFileName, string OriginalPath)>
        {
            public static readonly SourceFileTupleComparer Ordinal = new(StringComparer.Ordinal);
            public static readonly SourceFileTupleComparer OrdinalIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

            private readonly StringComparer _comparer;

            private SourceFileTupleComparer(StringComparer comparer) => _comparer = comparer;

            public bool Equals((string SerializedFileName, string OriginalPath) x, (string SerializedFileName, string OriginalPath) y) =>
                _comparer.Equals(x.SerializedFileName, y.SerializedFileName)
                && _comparer.Equals(x.OriginalPath, y.OriginalPath);

            public int GetHashCode((string SerializedFileName, string OriginalPath) obj) =>
                HashCode.Combine(_comparer.GetHashCode(obj.SerializedFileName), _comparer.GetHashCode(obj.OriginalPath));
        }

        private static void InsertSourceFilesAndAssets(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string folderPath,
            ScanFileLookup scanLookup,
            IReadOnlyCollection<AssetHandle> handles,
            bool caseSensitivePaths,
            Action<string>? advanceProgress = null,
            bool isAlreadyOrderedByUniqueId = false,
            bool isFreshInsert = false)
        {
            if (handles.Count == 0)
            {
                return;
            }

            var tupleComparer = caseSensitivePaths ? SourceFileTupleComparer.Ordinal : SourceFileTupleComparer.OrdinalIgnoreCase;
            var sourceCounts = new Dictionary<(string SerializedFileName, string OriginalPath), int>(tupleComparer);
            var sourceList = new List<(string SerializedFileName, string OriginalPath)>();
            {
                string? lastSfn = null;
                string? lastOrig = null;
                var lastKey = (string.Empty, string.Empty);
                var runCount = 0;

                foreach (var h in handles)
                {
                    var sfn = h.SerializedFileName ?? string.Empty;
                    var orig = h.OriginalPath ?? string.Empty;
                    if (ReferenceEquals(sfn, lastSfn) && ReferenceEquals(orig, lastOrig))
                    {
                        runCount++;
                        continue;
                    }

                    if (runCount > 0)
                    {
                        if (sourceCounts.TryGetValue(lastKey, out var existing))
                        {
                            sourceCounts[lastKey] = existing + runCount;
                        }
                        else
                        {
                            sourceCounts[lastKey] = runCount;
                            sourceList.Add(lastKey);
                        }
                    }

                    lastSfn = sfn;
                    lastOrig = orig;
                    lastKey = (sfn, orig);
                    runCount = 1;
                }

                if (runCount > 0)
                {
                    if (sourceCounts.TryGetValue(lastKey, out var existing))
                    {
                        sourceCounts[lastKey] = existing + runCount;
                    }
                    else
                    {
                        sourceCounts[lastKey] = runCount;
                        sourceList.Add(lastKey);
                    }
                }
            }

            var sourceFileIds = new Dictionary<(string SerializedFileName, string OriginalPath), long>(sourceList.Count, tupleComparer);
            var sourceGroupIndex = 0;
            using (var upsertSourceCmd = conn.CreateCommand())
            {
                upsertSourceCmd.Transaction = transaction;
                upsertSourceCmd.CommandText = @"
                    INSERT INTO SourceFiles (
                        ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount,
                        RelativePath, FileSize, LastWriteUtcTicks, ContentSampleHash, FileKind)
                    VALUES (
                        @projectId, @serializedFileName, @originalPath, '', @objectCount,
                        @relativePath, @fileSize, @lastWriteUtcTicks, @contentSampleHash, @fileKind)
                    ON CONFLICT(ProjectId, SerializedFileName, OriginalPath)
                    DO UPDATE SET
                        ObjectCount = excluded.ObjectCount,
                        LastSeen = CURRENT_TIMESTAMP,
                        RelativePath = excluded.RelativePath,
                        FileSize = excluded.FileSize,
                        LastWriteUtcTicks = excluded.LastWriteUtcTicks,
                        ContentSampleHash = excluded.ContentSampleHash,
                        FileKind = excluded.FileKind
                    RETURNING Id";
                var pSrcProjectId = upsertSourceCmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pSerializedFileName = upsertSourceCmd.Parameters.Add("@serializedFileName", SqliteType.Text);
                var pOriginalPath = upsertSourceCmd.Parameters.Add("@originalPath", SqliteType.Text);
                var pObjectCount = upsertSourceCmd.Parameters.Add("@objectCount", SqliteType.Integer);
                var pRelativePath = upsertSourceCmd.Parameters.Add("@relativePath", SqliteType.Text);
                var pFileSize = upsertSourceCmd.Parameters.Add("@fileSize", SqliteType.Integer);
                var pLastWriteTicks = upsertSourceCmd.Parameters.Add("@lastWriteUtcTicks", SqliteType.Integer);
                var pSampleHash = upsertSourceCmd.Parameters.Add("@contentSampleHash", SqliteType.Integer);
                var pFileKind = upsertSourceCmd.Parameters.Add("@fileKind", SqliteType.Integer);
                pSrcProjectId.Value = projectId;

                for (var i = 0; i < sourceList.Count; i++)
                {
                    var key = sourceList[i];
                    pSerializedFileName.Value = key.SerializedFileName;
                    pOriginalPath.Value = key.OriginalPath;
                    pObjectCount.Value = sourceCounts[key];

                    if (TryResolveScanFileMetadata(scanLookup, key.OriginalPath, caseSensitivePaths, out var scanned, out var relPath))
                    {
                        pRelativePath.Value = relPath;
                        pFileSize.Value = scanned.Size;
                        pLastWriteTicks.Value = scanned.LastWriteTimeUtcTicks;
                        pSampleHash.Value = unchecked((long)scanned.ContentSampleHash);
                        pFileKind.Value = (int)scanned.Kind;
                    }
                    else
                    {
                        pRelativePath.Value = relPath;
                        pFileSize.Value = 0L;
                        pLastWriteTicks.Value = 0L;
                        pSampleHash.Value = 0L;
                        pFileKind.Value = (int)ProjectFileKind.Other;
                    }

                    var id = upsertSourceCmd.ExecuteScalar();
                    if (id != null && id != DBNull.Value)
                    {
                        sourceFileIds[key] = Convert.ToInt64(id);
                    }

                    sourceGroupIndex++;
                    advanceProgress?.Invoke($"Saving source files: {sourceGroupIndex:N0}/{sourceList.Count:N0}");
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = isFreshInsert
                    ? @"
                        INSERT OR REPLACE INTO Assets (ProjectId, SourceFileId, UniqueID, Name, Type, Container, PathID, ByteStart, ByteSize)
                        VALUES (@projectId, @sourceFileId, @uniqueId, @name, @type, @container, @pathId, @byteStart, @byteSize)"
                    : @"
                        INSERT INTO Assets (ProjectId, SourceFileId, UniqueID, Name, Type, Container, PathID, ByteStart, ByteSize)
                        VALUES (@projectId, @sourceFileId, @uniqueId, @name, @type, @container, @pathId, @byteStart, @byteSize)
                        ON CONFLICT(ProjectId, UniqueID)
                        DO UPDATE SET
                            SourceFileId = excluded.SourceFileId,
                            Name = excluded.Name,
                            Type = excluded.Type,
                            Container = excluded.Container,
                            PathID = excluded.PathID,
                            ByteStart = excluded.ByteStart,
                            ByteSize = excluded.ByteSize";

                var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
                var pSourceFileId = cmd.Parameters.Add("@sourceFileId", SqliteType.Integer);
                var pUniqueId = cmd.Parameters.Add("@uniqueId", SqliteType.Text);
                var pName = cmd.Parameters.Add("@name", SqliteType.Text);
                var pType = cmd.Parameters.Add("@type", SqliteType.Integer);
                var pContainer = cmd.Parameters.Add("@container", SqliteType.Text);
                var pPathId = cmd.Parameters.Add("@pathId", SqliteType.Integer);
                var pByteStart = cmd.Parameters.Add("@byteStart", SqliteType.Integer);
                var pByteSize = cmd.Parameters.Add("@byteSize", SqliteType.Integer);

                pProjectId.Value = projectId;
                var assetRowIndex = 0;
                // Insert in PRIMARY KEY order so the WITHOUT ROWID B-tree is filled by sequential appends
                // instead of random page splits; reuse already-ordered list from sidecar staging when available.
                IEnumerable<AssetHandle> orderedForInsert = isAlreadyOrderedByUniqueId
                    ? handles
                    : handles.OrderBy(h => h.UniqueID ?? string.Empty, StringComparer.Ordinal);

                string? lastSfn = null;
                string? lastOrig = null;
                object lastSourceFileIdValue = DBNull.Value;
                foreach (var handle in orderedForInsert)
                {
                    var sfn = handle.SerializedFileName ?? string.Empty;
                    var orig = handle.OriginalPath ?? string.Empty;
                    if (!ReferenceEquals(sfn, lastSfn) || !ReferenceEquals(orig, lastOrig))
                    {
                        lastSfn = sfn;
                        lastOrig = orig;
                        lastSourceFileIdValue = sourceFileIds.TryGetValue((sfn, orig), out var sourceFileId)
                            ? sourceFileId
                            : DBNull.Value;
                    }

                    pSourceFileId.Value = lastSourceFileIdValue;
                    pUniqueId.Value = handle.UniqueID ?? string.Empty;
                    pName.Value = handle.Name ?? string.Empty;
                    pType.Value = (int)handle.Type;
                    pContainer.Value = handle.Container ?? string.Empty;
                    pPathId.Value = handle.PathID;
                    pByteStart.Value = handle.ByteStart;
                    pByteSize.Value = handle.ByteSize;
                    cmd.ExecuteNonQuery();
                    assetRowIndex++;
                    advanceProgress?.Invoke($"Saving asset rows: {assetRowIndex:N0}/{handles.Count:N0}");
                }
            }
        }

        private static string GetSourceFileKey(string serializedFileName, string originalPath)
        {
            return $"{serializedFileName}\u001f{originalPath}";
        }
    }
}
