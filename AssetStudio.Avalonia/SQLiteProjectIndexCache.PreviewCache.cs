using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using K4os.Compression.LZ4;
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
        public const int DefaultPreviewCacheInlineBlobMaxBytes = 128 * 1024;
        public const int PreviewPayloadCompressionNone = 0;
        public const int PreviewPayloadCompressionLz4Pickler = 1;
        private const double PreviewCacheEvictionHighWaterRatio = 0.85;
        private static long s_lastPreviewCacheTimestampTicks;

        public long PreviewCacheMaxTotalBytes { get; set; } = DefaultPreviewCacheMaxTotalBytes;
        public int PreviewCacheMaxEntryCount { get; set; } = DefaultPreviewCacheMaxEntryCount;
        public int PreviewCacheInlineBlobMaxBytes { get; set; } = DefaultPreviewCacheInlineBlobMaxBytes;

        internal static void EnsurePreviewCacheColumns(SqliteConnection conn, SqliteTransaction? transaction = null)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var infoCmd = conn.CreateCommand())
            {
                infoCmd.Transaction = transaction;
                infoCmd.CommandText = "PRAGMA table_info(PreviewCacheEntries);";
                using var reader = infoCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(1))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
            }

            if (columns.Count == 0)
            {
                return;
            }

            if (!columns.Contains("PayloadBlob"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = transaction;
                alterCmd.CommandText = "ALTER TABLE PreviewCacheEntries ADD COLUMN PayloadBlob BLOB;";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("PayloadCompression"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = transaction;
                alterCmd.CommandText = "ALTER TABLE PreviewCacheEntries ADD COLUMN PayloadCompression INTEGER NOT NULL DEFAULT 0;";
                alterCmd.ExecuteNonQuery();
            }

            if (!columns.Contains("UncompressedByteSize"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = transaction;
                alterCmd.CommandText = @"
                    ALTER TABLE PreviewCacheEntries ADD COLUMN UncompressedByteSize INTEGER NOT NULL DEFAULT 0;
                    UPDATE PreviewCacheEntries
                    SET UncompressedByteSize = ByteSize
                    WHERE UncompressedByteSize = 0 AND ByteSize > 0;";
                alterCmd.ExecuteNonQuery();
            }
        }

        private static bool HasPreviewCacheBlobColumns(SqliteConnection conn)
        {
            bool hasPayloadBlob = false;
            bool hasPayloadCompression = false;
            bool hasUncompressedByteSize = false;

            using var infoCmd = conn.CreateCommand();
            infoCmd.CommandText = "PRAGMA table_info(PreviewCacheEntries);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var col = reader.GetString(1);
                if (string.Equals(col, "PayloadBlob", StringComparison.OrdinalIgnoreCase))
                {
                    hasPayloadBlob = true;
                }
                else if (string.Equals(col, "PayloadCompression", StringComparison.OrdinalIgnoreCase))
                {
                    hasPayloadCompression = true;
                }
                else if (string.Equals(col, "UncompressedByteSize", StringComparison.OrdinalIgnoreCase))
                {
                    hasUncompressedByteSize = true;
                }
            }

            return hasPayloadBlob && hasPayloadCompression && hasUncompressedByteSize;
        }

        private static void TryEnsurePreviewCacheColumnsByDbPath(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return;
            }

            var lockTaken = false;
            try
            {
                Monitor.TryEnter(WriteGate, TimeSpan.Zero, ref lockTaken);
                if (!lockTaken)
                {
                    return;
                }

                using var writeConn = CreateConnectionByDbPath(dbPath);
                EnsurePreviewCacheColumns(writeConn);
            }
            catch
            {
                // Read-only DB files on disk are tolerated gracefully without throwing
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(WriteGate);
                }
            }
        }

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
            var dbPath = GetDbPath(folderPath);
            try
            {
                long? hitEntryId = null;
                PreviewCacheEntry? entry = null;

                using (var lease = RentReadConnection(dbPath))
                {
                    var conn = lease.Connection;
                    try
                    {
                        var projectId = TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out var fromCache);
                        if (projectId == null)
                        {
                            return null;
                        }

                        entry = QueryPreviewCacheEntry(conn, dbPath, projectId.Value, assetUniqueId, previewKind, algorithmVersion, parameters, out hitEntryId);
                        if (entry == null && fromCache)
                        {
                            var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                            if (verifiedProjectId.HasValue && verifiedProjectId.Value != projectId.Value)
                            {
                                entry = QueryPreviewCacheEntry(conn, dbPath, verifiedProjectId.Value, assetUniqueId, previewKind, algorithmVersion, parameters, out hitEntryId);
                            }
                        }
                    }
                    catch
                    {
                        lease.MarkUnhealthy();
                        throw;
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
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                Logger.Warning($"Failed to load preview cache entry from SQLite: {ex.Message}");
                return null;
            }
        }

        public bool TryLoadPreviewCacheBytes(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            out byte[]? payloadBytes)
        {
            return TryLoadPreviewCacheBytes(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters,
                out payloadBytes,
                out _);
        }

        public bool TryLoadPreviewCacheBytes(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            out byte[]? payloadBytes,
            out PreviewCacheEntry? entry)
        {
            payloadBytes = null;
            entry = LoadPreviewCacheEntry(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters);
            if (entry == null)
            {
                return false;
            }

            try
            {
                if (entry.PayloadBlob != null && entry.PayloadBlob.Length > 0)
                {
                    if (entry.PayloadCompression == PreviewPayloadCompressionLz4Pickler)
                    {
                        payloadBytes = LZ4Pickler.Unpickle(entry.PayloadBlob);
                    }
                    else if (entry.PayloadCompression == PreviewPayloadCompressionNone)
                    {
                        payloadBytes = entry.PayloadBlob;
                    }
                    else
                    {
                        Logger.Warning($"Unsupported preview cache payload compression '{entry.PayloadCompression}'.");
                        return false;
                    }

                    return payloadBytes != null;
                }

                if (!string.IsNullOrWhiteSpace(entry.PayloadPath) && File.Exists(entry.PayloadPath))
                {
                    payloadBytes = File.ReadAllBytes(entry.PayloadPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to decode preview cache payload bytes: {ex.Message}");
                payloadBytes = null;
            }

            return false;
        }

        public bool TryLoadLatestPreviewCacheBytesByAssetId(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parametersPrefix,
            out byte[]? payloadBytes,
            out PreviewCacheEntry? entry)
        {
            payloadBytes = null;
            entry = null;
            if (string.IsNullOrWhiteSpace(assetUniqueId) || string.IsNullOrWhiteSpace(previewKind))
            {
                return false;
            }

            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                long? hitEntryId = null;
                using (var lease = RentReadConnection(dbPath))
                {
                    var conn = lease.Connection;
                    try
                    {
                        var projectId = TryGetOrResolveProjectId(conn, null, dbPath, folderPath, signature, out var fromCache);
                        if (projectId == null)
                        {
                            return false;
                        }

                        entry = QueryLatestPreviewCacheEntryByPrefix(
                            conn,
                            projectId.Value,
                            assetUniqueId,
                            previewKind,
                            algorithmVersion,
                            parametersPrefix,
                            out hitEntryId);
                        if (entry == null && fromCache)
                        {
                            var verifiedProjectId = ReverifyProjectId(conn, null, dbPath, folderPath, signature);
                            if (verifiedProjectId.HasValue && verifiedProjectId.Value != projectId.Value)
                            {
                                entry = QueryLatestPreviewCacheEntryByPrefix(
                                    conn,
                                    verifiedProjectId.Value,
                                    assetUniqueId,
                                    previewKind,
                                    algorithmVersion,
                                    parametersPrefix,
                                    out hitEntryId);
                            }
                        }
                    }
                    catch
                    {
                        lease.MarkUnhealthy();
                        throw;
                    }
                }

                if (entry == null)
                {
                    return false;
                }

                if (hitEntryId.HasValue)
                {
                    TryTouchPreviewCacheEntryLastAccessed(folderPath, hitEntryId.Value);
                }

                if (entry.PayloadBlob != null && entry.PayloadBlob.Length > 0)
                {
                    if (entry.PayloadCompression == PreviewPayloadCompressionLz4Pickler)
                    {
                        payloadBytes = LZ4Pickler.Unpickle(entry.PayloadBlob);
                    }
                    else if (entry.PayloadCompression == PreviewPayloadCompressionNone)
                    {
                        payloadBytes = entry.PayloadBlob;
                    }
                    else
                    {
                        return false;
                    }

                    return payloadBytes != null;
                }

                if (!string.IsNullOrWhiteSpace(entry.PayloadPath) && File.Exists(entry.PayloadPath))
                {
                    payloadBytes = File.ReadAllBytes(entry.PayloadPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load latest preview cache entry by asset ID from SQLite: {ex.Message}");
                payloadBytes = null;
            }

            return false;
        }

        private static PreviewCacheEntry? QueryLatestPreviewCacheEntryByPrefix(
            SqliteConnection conn,
            long projectId,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parametersPrefix,
            out long? hitEntryId)
        {
            hitEntryId = null;
            var hasBlobColumns = HasPreviewCacheBlobColumns(conn);
            var prefix = parametersPrefix ?? string.Empty;
            var prefixUpper = prefix.Length > 0
                ? prefix.Substring(0, prefix.Length - 1) + (char)(prefix[^1] + 1)
                : string.Empty;

            using var cmd = conn.CreateCommand();
            var rangeFilter = prefix.Length > 0
                ? " AND Parameters >= @paramStart AND Parameters < @paramEnd"
                : string.Empty;
            cmd.CommandText = hasBlobColumns
                ? $@"
                SELECT Id, PayloadHash, PayloadPath, ByteSize, PayloadBlob, PayloadCompression, UncompressedByteSize
                FROM PreviewCacheEntries
                WHERE ProjectId = @projectId
                  AND AssetUniqueID = @assetUniqueId
                  AND PreviewKind = @previewKind
                  AND AlgorithmVersion = @algorithmVersion
                  {rangeFilter}
                ORDER BY LastAccessedUtc DESC, Id DESC
                LIMIT 1"
                : $@"
                SELECT Id, PayloadHash, PayloadPath, ByteSize
                FROM PreviewCacheEntries
                WHERE ProjectId = @projectId
                  AND AssetUniqueID = @assetUniqueId
                  AND PreviewKind = @previewKind
                  AND AlgorithmVersion = @algorithmVersion
                  {rangeFilter}
                ORDER BY LastAccessedUtc DESC, Id DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
            cmd.Parameters.AddWithValue("@previewKind", previewKind);
            cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
            if (prefix.Length > 0)
            {
                cmd.Parameters.AddWithValue("@paramStart", prefix);
                cmd.Parameters.AddWithValue("@paramEnd", prefixUpper);
            }

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            hitEntryId = reader.GetInt64(0);
            var payloadHash = reader.GetString(1);
            var payloadPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var byteSize = reader.GetInt64(3);
            if (!hasBlobColumns)
            {
                return new PreviewCacheEntry(payloadHash, payloadPath, byteSize);
            }

            var payloadBlob = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
            var payloadCompression = reader.IsDBNull(5) ? PreviewPayloadCompressionNone : reader.GetInt32(5);
            var uncompressedByteSize = reader.IsDBNull(6) ? byteSize : reader.GetInt64(6);
            return new PreviewCacheEntry(
                payloadHash,
                payloadPath,
                byteSize,
                payloadBlob,
                payloadCompression,
                uncompressedByteSize);
        }

        public void EnqueuePreviewCachePayload(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            string payloadHash,
            byte[] payloadBytes,
            string? externalFilePathFallback = null)
        {
            EnqueuePreviewCachePayload(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters,
                payloadBytes,
                externalFilePathFallback,
                payloadHash);
        }

        public void EnqueuePreviewCachePayload(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            byte[] payloadBytes,
            string? externalFilePathFallback = null,
            string? payloadHash = null)
        {
            if (Volatile.Read(ref _disposed) != 0
                || string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(assetUniqueId)
                || string.IsNullOrWhiteSpace(previewKind)
                || payloadBytes == null
                || payloadBytes.Length == 0)
            {
                return;
            }

            var key = string.Join("\u001f", folderPath, signature, assetUniqueId, previewKind, algorithmVersion.ToString(CultureInfo.InvariantCulture), parameters ?? string.Empty);
            var item = new PendingPreviewCacheWrite(
                key,
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters ?? string.Empty,
                payloadBytes,
                externalFilePathFallback,
                payloadHash);

            lock (_bgWriteLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (_pendingPreviewCacheWritesByKey.ContainsKey(key))
                {
                    _pendingPreviewCacheWritesByKey[key] = item;
                }
                else
                {
                    while (_pendingPreviewCacheWrites.Count >= MaxPendingPreviewCacheWrites)
                    {
                        var dropped = _pendingPreviewCacheWrites.Dequeue();
                        _pendingPreviewCacheWritesByKey.Remove(dropped.Key);
                    }

                    _pendingPreviewCacheWrites.Enqueue(item);
                    _pendingPreviewCacheWritesByKey[key] = item;
                }

                EnsureBackgroundWriterRunningLocked();
            }
        }

        private bool TrySavePreviewCachePayloadNonBlocking(PendingPreviewCacheWrite item)
        {
            var effectiveHash = string.IsNullOrWhiteSpace(item.PayloadHash)
                ? Convert.ToHexStringLower(SHA256.HashData(item.PayloadBytes))
                : item.PayloadHash;
            var inlineLimit = Math.Max(0, PreviewCacheInlineBlobMaxBytes);
            var uncompressedByteSize = item.PayloadBytes.LongLength;

            if (item.PayloadBytes.Length <= inlineLimit)
            {
                var pickled = LZ4Pickler.Pickle(item.PayloadBytes);
                byte[] storedBytes;
                int compression;
                if (pickled != null && pickled.Length > 0 && pickled.Length < item.PayloadBytes.Length)
                {
                    storedBytes = pickled;
                    compression = PreviewPayloadCompressionLz4Pickler;
                }
                else
                {
                    storedBytes = item.PayloadBytes;
                    compression = PreviewPayloadCompressionNone;
                }

                return TryUpsertPreviewCacheEntryCore(
                    item.FolderPath,
                    item.Signature,
                    item.AssetUniqueId,
                    item.PreviewKind,
                    item.AlgorithmVersion,
                    item.Parameters,
                    effectiveHash,
                    payloadPath: string.Empty,
                    payloadBlob: storedBytes,
                    payloadCompression: compression,
                    byteSize: storedBytes.LongLength,
                    uncompressedByteSize: uncompressedByteSize,
                    lockTimeout: TimeSpan.Zero);
            }

            var targetFilePath = string.IsNullOrWhiteSpace(item.ExternalFilePathFallback)
                ? GetDefaultExternalPreviewPayloadPath(item.FolderPath, effectiveHash, item.PreviewKind)
                : item.ExternalFilePathFallback;

            try
            {
                var directory = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = targetFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tempPath, item.PayloadBytes);
                    File.Move(tempPath, targetFilePath, overwrite: true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to write external preview cache payload file '{targetFilePath}': {ex.Message}");
                return true;
            }

            return TryUpsertPreviewCacheEntryCore(
                item.FolderPath,
                item.Signature,
                item.AssetUniqueId,
                item.PreviewKind,
                item.AlgorithmVersion,
                item.Parameters,
                effectiveHash,
                payloadPath: targetFilePath,
                payloadBlob: null,
                payloadCompression: PreviewPayloadCompressionNone,
                byteSize: uncompressedByteSize,
                uncompressedByteSize: uncompressedByteSize,
                lockTimeout: TimeSpan.Zero);
        }

        private static PreviewCacheEntry? QueryPreviewCacheEntry(
            SqliteConnection conn,
            string dbPath,
            long projectId,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            out long? hitEntryId)
        {
            hitEntryId = null;
            var hasBlobColumns = HasPreviewCacheBlobColumns(conn);
            if (!hasBlobColumns)
            {
                TryEnsurePreviewCacheColumnsByDbPath(dbPath);
                hasBlobColumns = HasPreviewCacheBlobColumns(conn);
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = hasBlobColumns
                ? @"
                SELECT Id, PayloadHash, PayloadPath, ByteSize, PayloadBlob, PayloadCompression, UncompressedByteSize
                FROM PreviewCacheEntries
                WHERE ProjectId = @projectId
                  AND AssetUniqueID = @assetUniqueId
                  AND PreviewKind = @previewKind
                  AND AlgorithmVersion = @algorithmVersion
                  AND Parameters = @parameters
                LIMIT 1"
                : @"
                SELECT Id, PayloadHash, PayloadPath, ByteSize
                FROM PreviewCacheEntries
                WHERE ProjectId = @projectId
                  AND AssetUniqueID = @assetUniqueId
                  AND PreviewKind = @previewKind
                  AND AlgorithmVersion = @algorithmVersion
                  AND Parameters = @parameters
                LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
            cmd.Parameters.AddWithValue("@previewKind", previewKind);
            cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
            cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                hitEntryId = reader.GetInt64(0);
                var payloadHash = reader.GetString(1);
                var payloadPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var byteSize = reader.GetInt64(3);

                if (!hasBlobColumns)
                {
                    return new PreviewCacheEntry(payloadHash, payloadPath, byteSize);
                }

                var payloadBlob = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
                var payloadCompression = reader.IsDBNull(5) ? PreviewPayloadCompressionNone : reader.GetInt32(5);
                var uncompressedByteSize = reader.IsDBNull(6) ? byteSize : reader.GetInt64(6);
                return new PreviewCacheEntry(
                    payloadHash,
                    payloadPath,
                    byteSize,
                    payloadBlob,
                    payloadCompression,
                    uncompressedByteSize);
            }

            return null;
        }

        public void SavePreviewCachePayload(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            byte[] payloadBytes,
            string? externalFilePathFallback = null,
            string? payloadHash = null)
        {
            if (string.IsNullOrWhiteSpace(assetUniqueId)
                || string.IsNullOrWhiteSpace(previewKind)
                || payloadBytes == null
                || payloadBytes.Length == 0)
            {
                return;
            }

            var effectiveHash = string.IsNullOrWhiteSpace(payloadHash)
                ? Convert.ToHexStringLower(SHA256.HashData(payloadBytes))
                : payloadHash;
            var inlineLimit = Math.Max(0, PreviewCacheInlineBlobMaxBytes);
            var uncompressedByteSize = payloadBytes.LongLength;

            if (payloadBytes.Length <= inlineLimit)
            {
                var pickled = LZ4Pickler.Pickle(payloadBytes);
                byte[] storedBytes;
                int compression;
                if (pickled != null && pickled.Length > 0 && pickled.Length < payloadBytes.Length)
                {
                    storedBytes = pickled;
                    compression = PreviewPayloadCompressionLz4Pickler;
                }
                else
                {
                    storedBytes = payloadBytes;
                    compression = PreviewPayloadCompressionNone;
                }

                UpsertPreviewCacheEntryCore(
                    folderPath,
                    signature,
                    assetUniqueId,
                    previewKind,
                    algorithmVersion,
                    parameters,
                    effectiveHash,
                    payloadPath: string.Empty,
                    payloadBlob: storedBytes,
                    payloadCompression: compression,
                    byteSize: storedBytes.LongLength,
                    uncompressedByteSize: uncompressedByteSize);
                return;
            }

            var targetFilePath = string.IsNullOrWhiteSpace(externalFilePathFallback)
                ? GetDefaultExternalPreviewPayloadPath(folderPath, effectiveHash, previewKind)
                : externalFilePathFallback;

            try
            {
                var directory = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = targetFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tempPath, payloadBytes);
                    File.Move(tempPath, targetFilePath, overwrite: true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to write external preview cache payload file '{targetFilePath}': {ex.Message}");
                return;
            }

            UpsertPreviewCacheEntryCore(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters,
                effectiveHash,
                payloadPath: targetFilePath,
                payloadBlob: null,
                payloadCompression: PreviewPayloadCompressionNone,
                byteSize: uncompressedByteSize,
                uncompressedByteSize: uncompressedByteSize);
        }

        public void SavePreviewCachePayload(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            string payloadHash,
            byte[] payloadBytes,
            string? externalFilePathFallback = null)
        {
            SavePreviewCachePayload(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters,
                payloadBytes,
                externalFilePathFallback,
                payloadHash);
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

            var effectiveByteSize = ResolvePayloadByteSize(payloadPath, byteSize);
            UpsertPreviewCacheEntryCore(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters,
                payloadHash,
                payloadPath,
                payloadBlob: null,
                payloadCompression: PreviewPayloadCompressionNone,
                byteSize: effectiveByteSize,
                uncompressedByteSize: effectiveByteSize);
        }

        private void UpsertPreviewCacheEntryCore(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            string payloadHash,
            string payloadPath,
            byte[]? payloadBlob,
            int payloadCompression,
            long byteSize,
            long uncompressedByteSize)
        {
            TryUpsertPreviewCacheEntryCore(
                folderPath,
                signature,
                assetUniqueId,
                previewKind,
                algorithmVersion,
                parameters,
                payloadHash,
                payloadPath,
                payloadBlob,
                payloadCompression,
                byteSize,
                uncompressedByteSize,
                lockTimeout: Timeout.InfiniteTimeSpan);
        }

        private bool TryUpsertPreviewCacheEntryCore(
            string folderPath,
            string signature,
            string assetUniqueId,
            string previewKind,
            int algorithmVersion,
            string parameters,
            string payloadHash,
            string payloadPath,
            byte[]? payloadBlob,
            int payloadCompression,
            long byteSize,
            long uncompressedByteSize,
            TimeSpan lockTimeout)
        {
            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            var nowUtc = NextMonotonicUtcTimestamp();
            List<string>? filesToDelete = null;
            var lockTaken = false;

            try
            {
                if (lockTimeout == Timeout.InfiniteTimeSpan)
                {
                    Monitor.Enter(WriteGate, ref lockTaken);
                }
                else
                {
                    Monitor.TryEnter(WriteGate, lockTimeout, ref lockTaken);
                    if (!lockTaken)
                    {
                        return false;
                    }
                }

                try
                {
                    using var conn = CreateConnectionByDbPath(dbPath);
                    using var transaction = conn.BeginTransaction();
                    EnsurePreviewCacheColumns(conn, transaction);

                    var projectId = FindProjectId(conn, transaction, folderPath, signature);
                    if (projectId == null)
                    {
                        EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                        return true;
                    }

                    CacheProjectId(dbPath, folderPath, signature, projectId.Value);

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT INTO PreviewCacheEntries (
                                ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters,
                                PayloadHash, PayloadPath, PayloadBlob, PayloadCompression,
                                ByteSize, UncompressedByteSize, UpdatedUtc, LastAccessedUtc)
                            VALUES (
                                @projectId, @assetUniqueId, @previewKind, @algorithmVersion, @parameters,
                                @payloadHash, @payloadPath, @payloadBlob, @payloadCompression,
                                @byteSize, @uncompressedByteSize, @nowUtc, @nowUtc)
                            ON CONFLICT(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters)
                            DO UPDATE SET
                                PayloadHash = excluded.PayloadHash,
                                PayloadPath = excluded.PayloadPath,
                                PayloadBlob = excluded.PayloadBlob,
                                PayloadCompression = excluded.PayloadCompression,
                                ByteSize = excluded.ByteSize,
                                UncompressedByteSize = excluded.UncompressedByteSize,
                                UpdatedUtc = excluded.UpdatedUtc,
                                LastAccessedUtc = excluded.LastAccessedUtc,
                                LastAccessed = CURRENT_TIMESTAMP";
                        cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                        cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                        cmd.Parameters.AddWithValue("@previewKind", previewKind);
                        cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                        cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);
                        cmd.Parameters.AddWithValue("@payloadHash", payloadHash);
                        cmd.Parameters.AddWithValue("@payloadPath", payloadPath ?? string.Empty);
                        var blobParam = cmd.Parameters.Add("@payloadBlob", SqliteType.Blob);
                        blobParam.Value = payloadBlob != null ? payloadBlob : DBNull.Value;
                        cmd.Parameters.AddWithValue("@payloadCompression", payloadCompression);
                        cmd.Parameters.AddWithValue("@byteSize", byteSize);
                        cmd.Parameters.AddWithValue("@uncompressedByteSize", uncompressedByteSize);
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
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(WriteGate);
                    }
                }

                if (filesToDelete != null && filesToDelete.Count > 0)
                {
                    DeleteEvictedPreviewFiles(filesToDelete, out _, out _);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save preview cache entry to SQLite: {ex.Message}");
                return true;
            }
        }

        private string GetDefaultExternalPreviewPayloadPath(string folderPath, string payloadHash, string previewKind)
        {
            var safeHash = string.IsNullOrWhiteSpace(payloadHash) ? "empty" : payloadHash.ToLowerInvariant();
            var prefix = safeHash.Length >= 2 ? safeHash.Substring(0, 2) : "00";
            var ext = string.Equals(previewKind, "texture-thumbnail-png", StringComparison.OrdinalIgnoreCase)
                ? ".texturepreview.png"
                : string.Equals(previewKind, "mesh-geometry", StringComparison.OrdinalIgnoreCase)
                    ? ".meshpreview"
                    : ".bin";
            return Path.Combine(_cacheDir, "PreviewPayloads", GetFolderCacheKey(folderPath), prefix, safeHash + ext);
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
                    EnsurePreviewCacheColumns(conn, transaction);
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
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    var hasBlobColumns = HasPreviewCacheBlobColumns(conn);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = hasBlobColumns
                        ? @"
                        SELECT
                            COUNT(*),
                            COALESCE(SUM(CASE WHEN ByteSize > 0 THEN ByteSize ELSE COALESCE(LENGTH(PayloadBlob), 0) END), 0),
                            MIN(NULLIF(LastAccessedUtc, '')),
                            MAX(NULLIF(LastAccessedUtc, ''))
                        FROM PreviewCacheEntries"
                        : @"
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
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
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
                    SELECT COUNT(*), COALESCE(SUM(CASE WHEN ByteSize > 0 THEN ByteSize ELSE COALESCE(LENGTH(PayloadBlob), 0) END), 0)
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
                    SELECT Id, PayloadPath, CASE WHEN ByteSize > 0 THEN ByteSize ELSE COALESCE(LENGTH(PayloadBlob), 0) END
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
                Monitor.TryEnter(WriteGate, TimeSpan.Zero, ref lockTaken);
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
