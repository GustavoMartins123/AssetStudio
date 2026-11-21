using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache : IDisposable
    {
        private const int WriteBusyTimeoutSeconds = 60;
        private const int ReadBusyTimeoutSeconds = 5;
        private const int DefaultPageSizeBytes = 8192;
        private const int ConnectionCacheSizeKiB = -65536;
        private const long ConnectionMmapSizeBytes = 268435456;
        internal static readonly int MaxIdleReadConnectionsPerDatabase = Math.Clamp(Environment.ProcessorCount, 4, 16);
        private static readonly object WriteGate = new object();
        private static readonly ConcurrentDictionary<string, long> s_dbWriteGenerations =
            new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        private readonly string _cacheDir;
        private readonly HashSet<string> _initializedDbs = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _initLock = new object();
        private readonly ConcurrentDictionary<string, long> _projectIdCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CachedSemanticCacheStatus> _semanticStatusCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ReadConnectionPoolBucket> _readConnectionPools =
            new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        private readonly object _bgWriteLock = new object();
        private readonly Queue<PendingLazySemanticWrite> _pendingLazySemanticWrites = new();
        private readonly Dictionary<string, PendingLazySemanticWrite> _pendingLazySemanticWritesByKey = new(StringComparer.Ordinal);
        private readonly Queue<PendingPreviewCacheWrite> _pendingPreviewCacheWrites = new();
        private readonly Dictionary<string, PendingPreviewCacheWrite> _pendingPreviewCacheWritesByKey = new(StringComparer.Ordinal);
        private bool _bgWriterActive;
        private readonly ManualResetEventSlim _bgWriterIdleEvent = new(initialState: true);
        private const int MaxPendingLazySemanticWrites = 256;
        private const int MaxPendingPreviewCacheWrites = 128;
        private long _physicalReadConnectionsOpened;
        private long _readPoolHits;
        private int _disposed;

        public SQLiteProjectIndexCache()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "IndexCache"))
        {
        }

        internal SQLiteProjectIndexCache(string cacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory))
            {
                throw new ArgumentException("Cache directory must not be empty.", nameof(cacheDirectory));
            }

            _cacheDir = cacheDirectory;
            Directory.CreateDirectory(_cacheDir);
        }

        internal long PhysicalReadConnectionsOpened => Interlocked.Read(ref _physicalReadConnectionsOpened);
        internal long ReadPoolHits => Interlocked.Read(ref _readPoolHits);

        public string GetFolderSignature(ProjectScanResult scanResult)
        {
            return $"{scanResult.TotalFiles}_{scanResult.TotalBytes}_{scanResult.UnityBundleCount}";
        }

        internal string GetDbPath(string folderPath)
        {
            var folderKey = GetFolderCacheKey(folderPath);
            return Path.Combine(_cacheDir, $"project_index_{folderKey}.db");
        }

        internal string GetIdxPath(string folderPath)
        {
            return MmapProjectIndexSidecar.GetSidecarPath(GetDbPath(folderPath));
        }

        public void DeleteIndexCache(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var dbPath = GetDbPath(folderPath);
            var walPath = dbPath + "-wal";
            var shmPath = dbPath + "-shm";

            try
            {
                lock (WriteGate)
                {
                    InvalidateReadConnectionPool(dbPath);
                    ClearConnectionPoolsForDb(dbPath);
                    InvalidateProjectIdCacheForDb(dbPath);
                    NotifyDatabaseMutated(dbPath);
                    MmapProjectIndexSidecar.DeleteSidecarFiles(dbPath);

                    if (File.Exists(dbPath))
                    {
                        File.Delete(dbPath);
                    }
                    if (File.Exists(walPath))
                    {
                        File.Delete(walPath);
                    }
                    if (File.Exists(shmPath))
                    {
                        File.Delete(shmPath);
                    }

                    lock (_initLock)
                    {
                        _initializedDbs.Remove(dbPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete SQLite index cache files for {folderPath}: {ex.Message}");
            }
        }

        private static void ClearConnectionPoolsForDb(string dbPath)
        {
            using (var writeConn = new SqliteConnection(BuildWriteConnectionString(dbPath)))
            {
                SqliteConnection.ClearPool(writeConn);
            }
            using (var defaultConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                SqliteConnection.ClearPool(defaultConn);
            }
            using (var readConn = new SqliteConnection(BuildReadConnectionString(dbPath)))
            {
                SqliteConnection.ClearPool(readConn);
            }
        }

        private static string GetFolderCacheKey(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            var normalized = GetFullPathOrOriginal(folderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexStringLower(hashBytes);
        }

        private static string GetFullPathOrOriginal(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static string NormalizeProjectPathForCache(string folderPath)
        {
            return (folderPath ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }

        private static string BuildProjectIdCacheKey(string dbPath, string folderPath, string signature)
        {
            return $"{dbPath}\u001f{NormalizeProjectPathForCache(folderPath)}\u001f{signature}";
        }

        internal bool TryGetCachedProjectId(string folderPath, string signature, out long projectId)
        {
            var dbPath = GetDbPath(folderPath);
            return _projectIdCache.TryGetValue(BuildProjectIdCacheKey(dbPath, folderPath, signature), out projectId);
        }

        private void CacheProjectId(string dbPath, string folderPath, string signature, long projectId)
        {
            _projectIdCache[BuildProjectIdCacheKey(dbPath, folderPath, signature)] = projectId;
        }

        private void EvictProjectIdCacheEntry(string dbPath, string folderPath, string signature)
        {
            _projectIdCache.TryRemove(BuildProjectIdCacheKey(dbPath, folderPath, signature), out _);
        }

        private void InvalidateProjectIdCacheForDb(string dbPath)
        {
            var prefix = dbPath + "\u001f";
            foreach (var key in _projectIdCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _projectIdCache.TryRemove(key, out _);
                }
            }

            InvalidateSemanticStatusCacheForDb(dbPath);
        }

        private void InvalidateSemanticStatusCacheForDb(string dbPath)
        {
            var prefix = dbPath + "\u001f";
            foreach (var key in _semanticStatusCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _semanticStatusCache.TryRemove(key, out _);
                }
            }
        }

        private void NotifyDatabaseMutated(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return;
            }

            var normalized = NormalizeDbPath(dbPath);
            s_dbWriteGenerations.AddOrUpdate(normalized, 1L, static (_, current) => current + 1L);
            InvalidateSemanticStatusCacheForDb(dbPath);
        }

        private static long GetDatabaseWriteGeneration(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return 0L;
            }

            var normalized = NormalizeDbPath(dbPath);
            return s_dbWriteGenerations.TryGetValue(normalized, out var gen) ? gen : 0L;
        }

        internal readonly struct CachedSemanticCacheStatus
        {
            public CachedSemanticCacheStatus(string indexingStatus, bool hasRelations, long writeGeneration)
            {
                IndexingStatus = indexingStatus ?? string.Empty;
                HasRelations = hasRelations;
                WriteGeneration = writeGeneration;
            }

            public string IndexingStatus { get; }
            public bool HasRelations { get; }
            public long WriteGeneration { get; }
        }

        private sealed class PendingLazySemanticWrite
        {
            public PendingLazySemanticWrite(string key, string folderPath, string signature, SemanticAssetRelations delta)
            {
                Key = key;
                FolderPath = folderPath;
                Signature = signature;
                Delta = delta;
            }

            public string Key { get; }
            public string FolderPath { get; }
            public string Signature { get; }
            public SemanticAssetRelations Delta { get; }
        }

        private sealed class PendingPreviewCacheWrite
        {
            public PendingPreviewCacheWrite(
                string key,
                string folderPath,
                string signature,
                string assetUniqueId,
                string previewKind,
                int algorithmVersion,
                string parameters,
                byte[] payloadBytes,
                string? externalFilePathFallback,
                string? payloadHash)
            {
                Key = key;
                FolderPath = folderPath;
                Signature = signature;
                AssetUniqueId = assetUniqueId;
                PreviewKind = previewKind;
                AlgorithmVersion = algorithmVersion;
                Parameters = parameters;
                PayloadBytes = payloadBytes;
                ExternalFilePathFallback = externalFilePathFallback;
                PayloadHash = payloadHash;
            }

            public string Key { get; }
            public string FolderPath { get; }
            public string Signature { get; }
            public string AssetUniqueId { get; }
            public string PreviewKind { get; }
            public int AlgorithmVersion { get; }
            public string Parameters { get; }
            public byte[] PayloadBytes { get; }
            public string? ExternalFilePathFallback { get; }
            public string? PayloadHash { get; }
        }

        private long? TryGetOrResolveProjectId(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            string dbPath,
            string folderPath,
            string signature,
            out bool fromCache)
        {
            var cacheKey = BuildProjectIdCacheKey(dbPath, folderPath, signature);
            if (_projectIdCache.TryGetValue(cacheKey, out var cachedId))
            {
                fromCache = true;
                return cachedId;
            }

            fromCache = false;
            var projectId = FindProjectId(conn, transaction, folderPath, signature);
            if (projectId.HasValue)
            {
                _projectIdCache[cacheKey] = projectId.Value;
            }

            return projectId;
        }

        private long? ReverifyProjectId(
            SqliteConnection conn,
            SqliteTransaction? transaction,
            string dbPath,
            string folderPath,
            string signature)
        {
            var cacheKey = BuildProjectIdCacheKey(dbPath, folderPath, signature);
            var projectId = FindProjectId(conn, transaction, folderPath, signature);
            if (projectId.HasValue)
            {
                _projectIdCache[cacheKey] = projectId.Value;
            }
            else
            {
                _projectIdCache.TryRemove(cacheKey, out _);
            }

            return projectId;
        }

        internal void EnsureInitialized(string folderPath)
        {
            EnsureInitializedByDbPath(GetDbPath(folderPath));
        }

        private void EnsureInitializedByDbPath(string dbPath)
        {
            lock (_initLock)
            {
                if (_initializedDbs.Contains(dbPath) && File.Exists(dbPath))
                {
                    return;
                }

                InitializeDatabaseByPath(dbPath);
                _initializedDbs.Add(dbPath);
            }
        }

        private IEnumerable<string> EnumerateCacheDatabasePaths()
        {
            if (!Directory.Exists(_cacheDir))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.GetFiles(_cacheDir, "project_index_*.db", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string BuildWriteConnectionString(string dbPath)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = WriteBusyTimeoutSeconds
            }.ToString();
        }

        private static string BuildReadConnectionString(string dbPath, bool pooling = true)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = pooling,
                DefaultTimeout = ReadBusyTimeoutSeconds
            }.ToString();
        }

        internal SqliteConnection CreateConnection(string folderPath)
        {
            return CreateConnectionByDbPath(GetDbPath(folderPath));
        }

        internal static SqliteConnection CreateConnectionByDbPath(string dbPath)
        {
            return CreateConnectionByDbPath(dbPath, initializePageSize: false);
        }

        private static SqliteConnection CreateConnectionByDbPath(string dbPath, bool initializePageSize)
        {
            var conn = new SqliteConnection(BuildWriteConnectionString(dbPath));
            conn.DefaultTimeout = WriteBusyTimeoutSeconds;
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = initializePageSize
                ? $@"
                    PRAGMA page_size = {DefaultPageSizeBytes};
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = {WriteBusyTimeoutSeconds * 1000};
                    PRAGMA synchronous = NORMAL;
                    PRAGMA temp_store = MEMORY;
                    PRAGMA cache_size = {ConnectionCacheSizeKiB};
                    PRAGMA mmap_size = {ConnectionMmapSizeBytes};"
                : $@"
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = {WriteBusyTimeoutSeconds * 1000};
                    PRAGMA synchronous = NORMAL;
                    PRAGMA temp_store = MEMORY;
                    PRAGMA cache_size = {ConnectionCacheSizeKiB};
                    PRAGMA mmap_size = {ConnectionMmapSizeBytes};";
            pragma.ExecuteNonQuery();
            return conn;
        }

        internal SqliteConnection CreateReadConnection(string folderPath)
        {
            return CreateReadConnectionByDbPath(GetDbPath(folderPath));
        }

        internal static SqliteConnection CreateReadConnectionByDbPath(string dbPath)
        {
            return CreateReadConnectionCore(dbPath, pooling: true);
        }

        private static SqliteConnection CreateReadConnectionCore(string dbPath, bool pooling)
        {
            var conn = new SqliteConnection(BuildReadConnectionString(dbPath, pooling));
            try
            {
                conn.Open();
                using var pragma = conn.CreateCommand();
                pragma.CommandText = $@"
                    PRAGMA query_only = ON;
                    PRAGMA busy_timeout = {ReadBusyTimeoutSeconds * 1000};
                    PRAGMA temp_store = MEMORY;
                    PRAGMA cache_size = {ConnectionCacheSizeKiB};
                    PRAGMA mmap_size = {ConnectionMmapSizeBytes};";
                pragma.ExecuteNonQuery();
                return conn;
            }
            catch
            {
                DisposeQuietly(conn);
                throw;
            }
        }

        private static string NormalizeDbPath(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                throw new ArgumentException("Database path must not be empty.", nameof(dbPath));
            }

            return GetFullPathOrOriginal(dbPath.Trim());
        }

        internal int GetIdleReadConnectionCount(string dbPath)
        {
            var normalizedDbPath = NormalizeDbPath(dbPath);
            return _readConnectionPools.TryGetValue(normalizedDbPath, out var bucket)
                ? Volatile.Read(ref bucket.IdleCount)
                : 0;
        }

        internal int GetTotalIdleReadConnectionCount()
        {
            var total = 0;
            foreach (var bucket in _readConnectionPools.Values)
            {
                total += Volatile.Read(ref bucket.IdleCount);
            }

            return total;
        }

        internal long GetReadPoolGeneration(string dbPath)
        {
            var normalizedDbPath = NormalizeDbPath(dbPath);
            return _readConnectionPools.TryGetValue(normalizedDbPath, out var bucket)
                ? Volatile.Read(ref bucket.Generation)
                : 0;
        }

        internal PooledReadConnectionLease RentReadConnection(string dbPath)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var normalizedDbPath = NormalizeDbPath(dbPath);
            var bucket = _readConnectionPools.GetOrAdd(normalizedDbPath, static _ => new ReadConnectionPoolBucket());

            while (true)
            {
                var generation = Volatile.Read(ref bucket.Generation);
                if (!bucket.IdleConnections.TryTake(out var idleConn))
                {
                    break;
                }

                Interlocked.Decrement(ref bucket.IdleCount);

                if (Volatile.Read(ref _disposed) == 0
                    && Volatile.Read(ref bucket.Generation) == generation
                    && idleConn.State == ConnectionState.Open)
                {
                    Interlocked.Increment(ref _readPoolHits);
                    return new PooledReadConnectionLease(this, bucket, idleConn, generation, isPoolHit: true);
                }

                DisposeQuietly(idleConn);
            }

            var openGeneration = Volatile.Read(ref bucket.Generation);
            // Disable Microsoft.Data.Sqlite pooling so our bucket owns deterministic handle disposal on Windows.
            var freshConn = CreateReadConnectionCore(normalizedDbPath, pooling: false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeQuietly(freshConn);
                throw new ObjectDisposedException(nameof(SQLiteProjectIndexCache));
            }

            Interlocked.Increment(ref _physicalReadConnectionsOpened);
            return new PooledReadConnectionLease(this, bucket, freshConn, openGeneration, isPoolHit: false);
        }

        private void ReturnReadConnection(ReadConnectionPoolBucket bucket, SqliteConnection conn, long leaseGeneration, bool healthy)
        {
            Interlocked.Increment(ref bucket.PendingReturns);
            try
            {
                if (!healthy
                    || Volatile.Read(ref _disposed) != 0
                    || conn.State != ConnectionState.Open
                    || Volatile.Read(ref bucket.Generation) != leaseGeneration
                    || !TryReserveIdleSlot(bucket))
                {
                    DisposeQuietly(conn);
                    return;
                }

                bucket.IdleConnections.Add(conn);

                // Re-check after Add so a racing invalidation never leaves an idle handle in the bag.
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref bucket.Generation) != leaseGeneration)
                {
                    DrainBucket(bucket);
                }
            }
            finally
            {
                Interlocked.Decrement(ref bucket.PendingReturns);
            }
        }

        private static bool TryReserveIdleSlot(ReadConnectionPoolBucket bucket)
        {
            while (true)
            {
                var current = Volatile.Read(ref bucket.IdleCount);
                if (current >= MaxIdleReadConnectionsPerDatabase)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref bucket.IdleCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private static void DrainBucket(ReadConnectionPoolBucket bucket)
        {
            while (bucket.IdleConnections.TryTake(out var conn))
            {
                Interlocked.Decrement(ref bucket.IdleCount);
                DisposeQuietly(conn);
            }
        }

        private static void InvalidateBucket(ReadConnectionPoolBucket bucket)
        {
            Interlocked.Increment(ref bucket.Generation);
            DrainBucket(bucket);

            var spin = new SpinWait();
            while (Volatile.Read(ref bucket.PendingReturns) > 0)
            {
                spin.SpinOnce();
            }

            DrainBucket(bucket);
        }

        private bool HasReadConnectionPoolBucket(string dbPath)
        {
            return _readConnectionPools.ContainsKey(NormalizeDbPath(dbPath));
        }

        internal void InvalidateReadConnectionPool(string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                foreach (var bucket in _readConnectionPools.Values)
                {
                    InvalidateBucket(bucket);
                }
            }
            else
            {
                var normalizedDbPath = NormalizeDbPath(dbPath);
                if (_readConnectionPools.TryGetValue(normalizedDbPath, out var bucket))
                {
                    InvalidateBucket(bucket);
                }
            }

            SqliteConnection.ClearAllPools();
        }

        public void ReleaseReadConnections()
        {
            InvalidateReadConnectionPool(dbPath: null);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_bgWriteLock)
            {
                _pendingLazySemanticWrites.Clear();
                _pendingLazySemanticWritesByKey.Clear();
                _pendingPreviewCacheWrites.Clear();
                _pendingPreviewCacheWritesByKey.Clear();
            }

            try
            {
                _bgWriterIdleEvent.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            _bgWriterIdleEvent.Dispose();
            InvalidateReadConnectionPool(dbPath: null);
            GC.SuppressFinalize(this);
        }

        internal bool WaitForBackgroundWritesIdle(TimeSpan timeout)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return true;
            }

            try
            {
                return _bgWriterIdleEvent.Wait(timeout);
            }
            catch
            {
                return true;
            }
        }

        private static void DisposeQuietly(SqliteConnection conn)
        {
            try
            {
                conn.Dispose();
            }
            catch
            {
            }
        }

        internal sealed class ReadConnectionPoolBucket
        {
            public readonly ConcurrentBag<SqliteConnection> IdleConnections = new();
            public int IdleCount;
            public long Generation;
            public int PendingReturns;
        }

        internal struct PooledReadConnectionLease : IDisposable
        {
            private SQLiteProjectIndexCache? _owner;
            private ReadConnectionPoolBucket? _bucket;
            private SqliteConnection? _connection;
            private readonly long _generation;
            private bool _healthy;

            internal PooledReadConnectionLease(
                SQLiteProjectIndexCache owner,
                ReadConnectionPoolBucket bucket,
                SqliteConnection connection,
                long generation,
                bool isPoolHit)
            {
                _owner = owner;
                _bucket = bucket;
                _connection = connection;
                _generation = generation;
                _healthy = true;
                IsPoolHit = isPoolHit;
            }

            public SqliteConnection Connection =>
                _connection ?? throw new ObjectDisposedException(nameof(PooledReadConnectionLease));

            public bool IsPoolHit { get; }
            public long Generation => _generation;

            public void MarkUnhealthy()
            {
                _healthy = false;
            }

            public void Dispose()
            {
                var conn = _connection;
                var owner = _owner;
                var bucket = _bucket;
                if (conn == null || owner == null || bucket == null)
                {
                    return;
                }

                _connection = null;
                _owner = null;
                _bucket = null;
                owner.ReturnReadConnection(bucket, conn, _generation, _healthy);
            }
        }

        private void InitializeDatabaseByPath(string dbPath)
        {
            try
            {
                lock (WriteGate)
                {
                    if (HasReadConnectionPoolBucket(dbPath))
                    {
                        InvalidateReadConnectionPool(dbPath);
                    }

                    var isNewOrEmptyDb = !File.Exists(dbPath) || new FileInfo(dbPath).Length == 0;
                    using (var conn = CreateConnectionByDbPath(dbPath, initializePageSize: isNewOrEmptyDb))
                    {
                        using (var pragma = conn.CreateCommand())
                        {
                            pragma.CommandText = @"
                                PRAGMA journal_mode = WAL;
                                PRAGMA synchronous = NORMAL;
                                PRAGMA wal_autocheckpoint = 1000;";
                            pragma.ExecuteNonQuery();
                        }

                        if (TryUpgradeOrRebuildSchema(conn))
                        {
                            InvalidateProjectIdCacheForDb(dbPath);
                            NotifyDatabaseMutated(dbPath);
                            InvalidateReadConnectionPool(dbPath);
                            MmapProjectIndexSidecar.DeleteSidecarFiles(dbPath);
                        }

                        CreateSchema(conn);
                        TryOptimizeDatabase(conn, "database initialization");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize SQLite database cache at {dbPath}: {ex.Message}", ex);
            }
        }

        internal static void TryOptimizeDatabase(SqliteConnection conn, string context = "database")
        {
            if (conn == null || conn.State != ConnectionState.Open)
            {
                return;
            }

            try
            {
                using (var roCmd = conn.CreateCommand())
                {
                    roCmd.CommandText = "PRAGMA query_only;";
                    var queryOnly = Convert.ToInt64(roCmd.ExecuteScalar() ?? 0L);
                    if (queryOnly != 0)
                    {
                        return;
                    }
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA optimize=0x10002;";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Warning($"SQLite PRAGMA optimize for {context} skipped: {ex.Message}");
            }
        }

        private static void SetSynchronousMode(SqliteConnection conn, string mode)
        {
            if (conn.State != ConnectionState.Open)
            {
                return;
            }

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA synchronous = {mode};";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to set SQLite PRAGMA synchronous = {mode}: {ex.Message}");
            }
        }

        private static void TryAnalyzeDatabase(SqliteConnection conn, string context)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA optimize=0x10002; ANALYZE;";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Warning($"SQLite ANALYZE for {context} failed: {ex.Message}");
            }
        }

        private static bool IsDatabaseBusy(SqliteException ex)
        {
            return ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;
        }

        private static void TryCheckpointWal(SqliteConnection conn, string context)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                // PASSIVE does not wait for active readers. TRUNCATE can hold the
                // only writer for the full busy timeout while previews are reading.
                cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var busy = reader.FieldCount > 0 && !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var logFrames = reader.FieldCount > 1 && !reader.IsDBNull(1) ? reader.GetInt32(1) : 0;
                    var checkpointedFrames = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetInt32(2) : 0;
                    if (busy != 0)
                    {
                        Logger.Warning($"SQLite WAL checkpoint for {context} could not finish now: {checkpointedFrames:N0}/{logFrames:N0} frames checkpointed, busy={busy}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"SQLite WAL checkpoint for {context} failed: {ex.Message}");
            }
        }

        private static long? FindProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath, string signature)
        {
            var path1 = folderPath;
            var path2 = path1.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id FROM Projects
                WHERE (FolderPath = @path1 COLLATE NOCASE OR FolderPath = @path2 COLLATE NOCASE)
                  AND SignatureHash = @signature
                LIMIT 1";
            cmd.Parameters.AddWithValue("@path1", path1);
            cmd.Parameters.AddWithValue("@path2", path2);
            cmd.Parameters.AddWithValue("@signature", signature);
            var id = cmd.ExecuteScalar();
            return id == null ? null : Convert.ToInt64(id);
        }

        private static long? FindLatestProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id
                FROM Projects
                WHERE FolderPath = @path COLLATE NOCASE
                   OR FolderPath = @trimmedPath COLLATE NOCASE
                ORDER BY LastIndexed DESC, Id DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue("@path", folderPath);
            cmd.Parameters.AddWithValue("@trimmedPath", folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var id = cmd.ExecuteScalar();
            return id == null ? null : Convert.ToInt64(id);
        }

        internal void ExecuteProjectReadQuery(
            string folderPath,
            string signature,
            string errorContext,
            Action<SqliteConnection, SqliteTransaction, long> query)
        {
            EnsureInitialized(folderPath);
            var dbPath = GetDbPath(folderPath);
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    using var transaction = conn.BeginTransaction(deferred: true);
                    var projectId = TryGetOrResolveProjectId(conn, transaction, dbPath, folderPath, signature, out _);
                    if (projectId == null)
                    {
                        return;
                    }

                    query(conn, transaction, projectId.Value);
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (Exception ex)
            {
                EvictProjectIdCacheEntry(dbPath, folderPath, signature);
                Logger.Warning($"Failed to load {errorContext} from SQLite: {ex.Message}");
            }
        }

        private static DateTime? ReadNullableDateTime(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            return DateTime.TryParse(reader.GetString(ordinal), out var value)
                ? value
                : null;
        }

        private sealed class ThrottledProgressReporter
        {
            private readonly Action<int, int, string>? _progress;
            private readonly int _minWorkDelta;
            private int _lastReportedWork = -1;
            private long _lastReportedTicks;

            public ThrottledProgressReporter(int totalWork, Action<int, int, string>? progress)
            {
                TotalWork = Math.Max(1, totalWork);
                _progress = progress;
                _minWorkDelta = Math.Max(1, TotalWork / 100000);
            }

            public int TotalWork { get; }
            public int ProcessedWork { get; private set; }
            public string CurrentStage { get; private set; } = string.Empty;

            public void Report(string stage, bool force = false)
            {
                if (_progress == null)
                {
                    return;
                }

                var nowTicks = Stopwatch.GetTimestamp();
                var elapsedMs = _lastReportedTicks == 0
                    ? double.MaxValue
                    : (nowTicks - _lastReportedTicks) * 1000.0 / Stopwatch.Frequency;

                if (!force && ProcessedWork < TotalWork && ProcessedWork - _lastReportedWork < _minWorkDelta && elapsedMs < 100)
                {
                    return;
                }

                _lastReportedWork = ProcessedWork;
                _lastReportedTicks = nowTicks;
                _progress(Math.Clamp(ProcessedWork, 0, TotalWork), TotalWork, stage);
            }

            public void Advance(string stage)
            {
                ProcessedWork++;
                CurrentStage = stage;
                Report(stage);
            }

            public void Skip(int amount, string stage)
            {
                ProcessedWork += amount;
                CurrentStage = stage;
                Report(stage, force: true);
            }

            public void MarkCompleted(string stage)
            {
                ProcessedWork = TotalWork;
                CurrentStage = stage;
            }
        }
    }
}
