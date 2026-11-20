using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public sealed class PreviewCacheStats
    {
        public PreviewCacheStats(
            int entryCount,
            long totalBytes,
            string? oldestLastAccessedUtc,
            string? newestLastAccessedUtc)
        {
            EntryCount = entryCount;
            TotalBytes = totalBytes;
            OldestLastAccessedUtc = oldestLastAccessedUtc;
            NewestLastAccessedUtc = newestLastAccessedUtc;
        }

        public int EntryCount { get; }
        public long TotalBytes { get; }
        public string? OldestLastAccessedUtc { get; }
        public string? NewestLastAccessedUtc { get; }
    }

    public sealed class PreviewCacheEvictionResult
    {
        public PreviewCacheEvictionResult(
            int evictedEntryCount,
            long evictedBytes,
            int deletedFileCount,
            int failedFileDeleteCount,
            int remainingEntryCount,
            long remainingBytes)
        {
            EvictedEntryCount = evictedEntryCount;
            EvictedBytes = evictedBytes;
            DeletedFileCount = deletedFileCount;
            FailedFileDeleteCount = failedFileDeleteCount;
            RemainingEntryCount = remainingEntryCount;
            RemainingBytes = remainingBytes;
        }

        public int EvictedEntryCount { get; }
        public long EvictedBytes { get; }
        public int DeletedFileCount { get; }
        public int FailedFileDeleteCount { get; }
        public int RemainingEntryCount { get; }
        public long RemainingBytes { get; }
    }

    public partial class SQLiteProjectIndexCache
    {
        public const long DefaultPreviewCacheMaxTotalBytes = 512L * 1024 * 1024;
        public const int DefaultPreviewCacheMaxEntryCount = 5000;
        private const double PreviewCacheEvictionHighWaterRatio = 0.85;
        private static long s_lastPreviewCacheTimestampTicks;

        public long PreviewCacheMaxTotalBytes { get; set; } = DefaultPreviewCacheMaxTotalBytes;
        public int PreviewCacheMaxEntryCount { get; set; } = DefaultPreviewCacheMaxEntryCount;

        internal bool TryLoadPreviewCacheEntry(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            out PreviewCacheEntry? entry)
        {
            entry = LoadPreviewCacheEntry(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters);
            return entry != null;
        }

        internal PreviewCacheEntry? LoadPreviewCacheEntry(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters)
        {
            if (string.IsNullOrWhiteSpace(assetUniqueId) || string.IsNullOrWhiteSpace(previewKind))
            {
                return null;
            }

            EnsureInitialized(folderPath);
            try
            {
                long? hitEntryId = null;
                PreviewCacheEntry? entry = null;

                using (var conn = CreateReadConnection(folderPath))
                {
                    var projectId = FindProjectId(conn, null, folderPath, signature);
                    if (projectId == null)
                    {
                        return null;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT Id, PayloadHash, PayloadPath, ByteSize
                        FROM PreviewCacheEntries
                        WHERE ProjectId = @projectId
                          AND AssetUniqueID = @assetUniqueId
                          AND PreviewKind = @previewKind
                          AND AlgorithmVersion = @algorithmVersion
                          AND Parameters = @parameters
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                    cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@previewKind", previewKind);
                    cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                    cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);

                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        hitEntryId = reader.GetInt64(0);
                        entry = new PreviewCacheEntry(
                            reader.GetString(1),
                            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            reader.GetInt64(3));
                    }
                }

                if (hitEntryId.HasValue)
                {
                    TryTouchPreviewCacheEntryLastAccessed(folderPath, hitEntryId.Value);
                }

                return entry;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load preview cache entry from SQLite: {ex.Message}");
                return null;
            }
        }

        internal void SavePreviewCacheEntry(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            string payloadHash,
            string payloadPath,
            long byteSize)
        {
            if (string.IsNullOrWhiteSpace(assetUniqueId)
                || string.IsNullOrWhiteSpace(previewKind)
                || string.IsNullOrWhiteSpace(payloadHash)
                || string.IsNullOrWhiteSpace(payloadPath))
            {
                return;
            }

            EnsureInitialized(folderPath);
            var effectiveByteSize = ResolvePayloadByteSize(payloadPath, byteSize);
            var nowUtc = NextMonotonicUtcTimestamp();
            List<string>? filesToDelete = null;

            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnection(folderPath);
                    using var transaction = conn.BeginTransaction();
                    var projectId = FindProjectId(conn, transaction, folderPath, signature);
                    if (projectId == null)
                    {
                        return;
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT INTO PreviewCacheEntries (
                                ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters,
                                PayloadHash, PayloadPath, ByteSize, UpdatedUtc, LastAccessedUtc)
                            VALUES (
                                @projectId, @assetUniqueId, @previewKind, @algorithmVersion, @parameters,
                                @payloadHash, @payloadPath, @byteSize, @nowUtc, @nowUtc)
                            ON CONFLICT(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters)
                            DO UPDATE SET
                                PayloadHash = excluded.PayloadHash,
                                PayloadPath = excluded.PayloadPath,
                                ByteSize = excluded.ByteSize,
                                UpdatedUtc = excluded.UpdatedUtc,
                                LastAccessedUtc = excluded.LastAccessedUtc,
                                LastAccessed = CURRENT_TIMESTAMP";
                        cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                        cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                        cmd.Parameters.AddWithValue("@previewKind", previewKind);
                        cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                        cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);
                        cmd.Parameters.AddWithValue("@payloadHash", payloadHash);
                        cmd.Parameters.AddWithValue("@payloadPath", payloadPath);
                        cmd.Parameters.AddWithValue("@byteSize", effectiveByteSize);
                        cmd.Parameters.AddWithValue("@nowUtc", nowUtc);
                        cmd.ExecuteNonQuery();
                    }

                    long? savedEntryId = null;
                    using (var idCmd = conn.CreateCommand())
                    {
                        idCmd.Transaction = transaction;
                        idCmd.CommandText = @"
                            SELECT Id
                            FROM PreviewCacheEntries
                            WHERE ProjectId = @projectId
                              AND AssetUniqueID = @assetUniqueId
                              AND PreviewKind = @previewKind
                              AND AlgorithmVersion = @algorithmVersion
                              AND Parameters = @parameters
                            LIMIT 1";
                        idCmd.Parameters.AddWithValue("@projectId", projectId.Value);
                        idCmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                        idCmd.Parameters.AddWithValue("@previewKind", previewKind);
                        idCmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                        idCmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);
                        var rawId = idCmd.ExecuteScalar();
                        if (rawId != null && rawId != DBNull.Value)
                        {
                            savedEntryId = Convert.ToInt64(rawId);
                        }
                    }

                    filesToDelete = EvictPreviewCacheEntriesInTransaction(
                        conn,
                        transaction,
                        PreviewCacheMaxTotalBytes,
                        PreviewCacheMaxEntryCount,
                        protectedEntryId: savedEntryId,
                        out _,
                        out _,
                        out _,
                        out _);

                    transaction.Commit();
                }

                if (filesToDelete != null && filesToDelete.Count > 0)
                {
                    DeleteEvictedPreviewFiles(filesToDelete, out _, out _);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save preview cache entry to SQLite: {ex.Message}");
            }
        }

        public PreviewCacheEvictionResult EvictPreviewCache(
            long maxTotalBytes = DefaultPreviewCacheMaxTotalBytes,
            int maxEntryCount = DefaultPreviewCacheMaxEntryCount)
        {
            if (maxTotalBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Max total bytes must be non-negative.");
            }

            if (maxEntryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntryCount), "Max entry count must be non-negative.");
            }

            var totalEvictedEntries = 0;
            long totalEvictedBytes = 0;
            var totalDeletedFiles = 0;
            var totalFailedDeletes = 0;
            var totalRemainingEntries = 0;
            long totalRemainingBytes = 0;

            foreach (var dbPath in EnumerateCacheDatabasePaths())
            {
                var result = EvictPreviewCacheByDbPath(dbPath, maxTotalBytes, maxEntryCount);
                totalEvictedEntries += result.EvictedEntryCount;
                totalEvictedBytes += result.EvictedBytes;
                totalDeletedFiles += result.DeletedFileCount;
                totalFailedDeletes += result.FailedFileDeleteCount;
                totalRemainingEntries += result.RemainingEntryCount;
                totalRemainingBytes += result.RemainingBytes;
            }

            return new PreviewCacheEvictionResult(
                totalEvictedEntries,
                totalEvictedBytes,
                totalDeletedFiles,
                totalFailedDeletes,
                totalRemainingEntries,
                totalRemainingBytes);
        }

        public PreviewCacheEvictionResult EvictPreviewCache(
            string folderPath,
            long maxTotalBytes = DefaultPreviewCacheMaxTotalBytes,
            int maxEntryCount = DefaultPreviewCacheMaxEntryCount)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("Folder path must not be empty.", nameof(folderPath));
            }

            if (maxTotalBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Max total bytes must be non-negative.");
            }

            if (maxEntryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntryCount), "Max entry count must be non-negative.");
            }

            return EvictPreviewCacheByDbPath(GetDbPath(folderPath), maxTotalBytes, maxEntryCount);
        }

        public PreviewCacheStats GetPreviewCacheStats()
        {
            var totalEntries = 0;
            long totalBytes = 0;
            string? oldest = null;
            string? newest = null;

            foreach (var dbPath in EnumerateCacheDatabasePaths())
            {
                var stats = GetPreviewCacheStatsByDbPath(dbPath);
                totalEntries += stats.EntryCount;
                totalBytes += stats.TotalBytes;

                if (!string.IsNullOrEmpty(stats.OldestLastAccessedUtc)
                    && (oldest == null || string.CompareOrdinal(stats.OldestLastAccessedUtc, oldest) < 0))
                {
                    oldest = stats.OldestLastAccessedUtc;
                }

                if (!string.IsNullOrEmpty(stats.NewestLastAccessedUtc)
                    && (newest == null || string.CompareOrdinal(stats.NewestLastAccessedUtc, newest) > 0))
                {
                    newest = stats.NewestLastAccessedUtc;
                }
            }

            return new PreviewCacheStats(totalEntries, totalBytes, oldest, newest);
        }

        public PreviewCacheStats GetPreviewCacheStats(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("Folder path must not be empty.", nameof(folderPath));
            }

            return GetPreviewCacheStatsByDbPath(GetDbPath(folderPath));
        }

        internal PreviewCacheEvictionResult EvictPreviewCacheEntries(
            string folderPath,
            long maxTotalBytes = DefaultPreviewCacheMaxTotalBytes,
            int maxEntryCount = DefaultPreviewCacheMaxEntryCount)
        {
            return EvictPreviewCache(folderPath, maxTotalBytes, maxEntryCount);
        }

        private PreviewCacheEvictionResult EvictPreviewCacheByDbPath(
            string dbPath,
            long maxTotalBytes,
            int maxEntryCount)
        {
            if (!File.Exists(dbPath))
            {
                return new PreviewCacheEvictionResult(0, 0, 0, 0, 0, 0);
            }

            EnsureInitializedByDbPath(dbPath);
            List<string> filesToDelete;
            int evictedEntries;
            long evictedBytes;
            int remainingEntries;
            long remainingBytes;

            try
            {
                lock (WriteGate)
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    using var transaction = conn.BeginTransaction();
                    filesToDelete = EvictPreviewCacheEntriesInTransaction(
                        conn,
                        transaction,
                        maxTotalBytes,
                        maxEntryCount,
                        protectedEntryId: null,
                        out evictedEntries,
                        out evictedBytes,
                        out remainingEntries,
                        out remainingBytes);
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to evict preview cache entries for '{dbPath}': {ex.Message}");
                return new PreviewCacheEvictionResult(0, 0, 0, 0, 0, 0);
            }

            DeleteEvictedPreviewFiles(filesToDelete, out var deletedFiles, out var failedDeletes);

            return new PreviewCacheEvictionResult(
                evictedEntries,
                evictedBytes,
                deletedFiles,
                failedDeletes,
                remainingEntries,
                remainingBytes);
        }

        private PreviewCacheStats GetPreviewCacheStatsByDbPath(string dbPath)
        {
            if (!File.Exists(dbPath))
            {
                return new PreviewCacheStats(0, 0, null, null);
            }

            EnsureInitializedByDbPath(dbPath);
            try
            {
                using var conn = CreateReadConnectionByDbPath(dbPath);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        COUNT(*),
                        COALESCE(SUM(ByteSize), 0),
                        MIN(NULLIF(LastAccessedUtc, '')),
                        MAX(NULLIF(LastAccessedUtc, ''))
                    FROM PreviewCacheEntries";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    var bytes = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                    var oldest = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var newest = reader.IsDBNull(3) ? null : reader.GetString(3);
                    return new PreviewCacheStats(count, bytes, oldest, newest);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to read preview cache stats for '{dbPath}': {ex.Message}");
            }

            return new PreviewCacheStats(0, 0, null, null);
        }

        private static List<string> EvictPreviewCacheEntriesInTransaction(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long maxTotalBytes,
            int maxEntryCount,
            long? protectedEntryId,
            out int evictedEntries,
            out long evictedBytes,
            out int remainingEntries,
            out long remainingBytes)
        {
            evictedEntries = 0;
            evictedBytes = 0;
            remainingEntries = 0;
            remainingBytes = 0;
            var filesToDelete = new List<string>();

            using (var statsCmd = conn.CreateCommand())
            {
                statsCmd.Transaction = transaction;
                statsCmd.CommandText = @"
                    SELECT COUNT(*), COALESCE(SUM(ByteSize), 0)
                    FROM PreviewCacheEntries";
                using var reader = statsCmd.ExecuteReader();
                if (reader.Read())
                {
                    remainingEntries = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    remainingBytes = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                }
            }

            if (remainingBytes <= maxTotalBytes && remainingEntries <= maxEntryCount)
            {
                return filesToDelete;
            }

            var targetBytes = maxTotalBytes <= 0
                ? 0L
                : Math.Max(0L, (long)Math.Floor(maxTotalBytes * PreviewCacheEvictionHighWaterRatio));
            var targetCount = maxEntryCount <= 0
                ? 0
                : Math.Max(1, (int)Math.Floor(maxEntryCount * PreviewCacheEvictionHighWaterRatio));

            var entriesToDelete = new List<(long Id, string? PayloadPath, long ByteSize)>();
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = @"
                    SELECT Id, PayloadPath, ByteSize
                    FROM PreviewCacheEntries
                    ORDER BY LastAccessedUtc ASC, UpdatedUtc ASC, Id ASC";
                using var reader = selectCmd.ExecuteReader();
                while (reader.Read() && (remainingBytes > targetBytes || remainingEntries > targetCount))
                {
                    var id = reader.GetInt64(0);
                    if (protectedEntryId.HasValue && id == protectedEntryId.Value && maxTotalBytes > 0 && maxEntryCount > 0)
                    {
                        continue;
                    }

                    var path = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var size = Math.Max(0L, reader.GetInt64(2));
                    entriesToDelete.Add((id, path, size));
                    remainingEntries--;
                    remainingBytes = Math.Max(0L, remainingBytes - size);
                    evictedEntries++;
                    evictedBytes += size;
                }
            }

            if (entriesToDelete.Count == 0)
            {
                return filesToDelete;
            }

            var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM PreviewCacheEntries WHERE Id = @id";
                var idParam = deleteCmd.Parameters.Add("@id", SqliteType.Integer);

                for (int i = 0; i < entriesToDelete.Count; i++)
                {
                    idParam.Value = entriesToDelete[i].Id;
                    deleteCmd.ExecuteNonQuery();

                    var payloadPath = entriesToDelete[i].PayloadPath;
                    if (!string.IsNullOrWhiteSpace(payloadPath))
                    {
                        candidatePaths.Add(payloadPath);
                    }
                }
            }

            if (candidatePaths.Count > 0)
            {
                using var refCheckCmd = conn.CreateCommand();
                refCheckCmd.Transaction = transaction;
                refCheckCmd.CommandText = "SELECT 1 FROM PreviewCacheEntries WHERE PayloadPath = @path LIMIT 1";
                var pathParam = refCheckCmd.Parameters.Add("@path", SqliteType.Text);

                foreach (var candidatePath in candidatePaths)
                {
                    pathParam.Value = candidatePath;
                    var stillReferenced = refCheckCmd.ExecuteScalar();
                    if (stillReferenced == null || stillReferenced == DBNull.Value)
                    {
                        filesToDelete.Add(candidatePath);
                    }
                }
            }

            return filesToDelete;
        }

        private void TryTouchPreviewCacheEntryLastAccessed(string folderPath, long entryId)
        {
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(WriteGate, TimeSpan.FromSeconds(2), ref lockTaken);
                if (!lockTaken)
                {
                    return;
                }

                var nowUtc = NextMonotonicUtcTimestamp();
                using var conn = CreateConnection(folderPath);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE PreviewCacheEntries
                    SET LastAccessedUtc = @nowUtc,
                        LastAccessed = CURRENT_TIMESTAMP
                    WHERE Id = @id";
                cmd.Parameters.AddWithValue("@nowUtc", nowUtc);
                cmd.Parameters.AddWithValue("@id", entryId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to update preview cache LastAccessedUtc (tolerated): {ex.Message}");
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(WriteGate);
                }
            }
        }

        private static void DeleteEvictedPreviewFiles(
            List<string> filePaths,
            out int deletedFileCount,
            out int failedFileDeleteCount)
        {
            deletedFileCount = 0;
            failedFileDeleteCount = 0;

            for (int i = 0; i < filePaths.Count; i++)
            {
                var filePath = filePaths[i];
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        deletedFileCount++;
                    }
                }
                catch (Exception ex)
                {
                    failedFileDeleteCount++;
                    Logger.Warning($"Failed to delete evicted preview cache payload '{filePath}': {ex.Message}");
                }
            }
        }

        private static long ResolvePayloadByteSize(string payloadPath, long fallbackByteSize)
        {
            var effectiveByteSize = Math.Max(0L, fallbackByteSize);
            try
            {
                if (File.Exists(payloadPath))
                {
                    effectiveByteSize = new FileInfo(payloadPath).Length;
                }
            }
            catch
            {
                // Fall back to caller-provided size
            }

            return effectiveByteSize;
        }

        private static string NextMonotonicUtcTimestamp()
        {
            while (true)
            {
                var last = Interlocked.Read(ref s_lastPreviewCacheTimestampTicks);
                var now = DateTime.UtcNow.Ticks;
                var next = now > last ? now : last + 1;
                if (Interlocked.CompareExchange(ref s_lastPreviewCacheTimestampTicks, next, last) == last)
                {
                    return new DateTime(next, DateTimeKind.Utc)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
                }
            }
        }
    }
}
