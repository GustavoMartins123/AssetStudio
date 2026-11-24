using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
        private readonly bool _caseSensitivePaths;
        private readonly StringComparer _pathComparer;
        private readonly StringComparison _pathComparison;
        private readonly HashSet<string> _initializedDbs;
        private readonly object _initLock = new object();
        private readonly ConcurrentDictionary<string, long> _projectIdCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CachedSemanticCacheStatus> _semanticStatusCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ReadConnectionPoolBucket> _readConnectionPools;
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

        internal SQLiteProjectIndexCache(string cacheDirectory, bool? caseSensitivePaths = null)
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory))
            {
                throw new ArgumentException("Cache directory must not be empty.", nameof(cacheDirectory));
            }

            _cacheDir = cacheDirectory;
            _caseSensitivePaths = caseSensitivePaths ?? OperatingSystem.IsLinux();
            _pathComparer = _caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            _pathComparison = _caseSensitivePaths ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            _initializedDbs = new HashSet<string>(_pathComparer);
            _readConnectionPools = new ConcurrentDictionary<string, ReadConnectionPoolBucket>(_pathComparer);
            Directory.CreateDirectory(_cacheDir);
        }

        internal bool CaseSensitivePaths => _caseSensitivePaths;
        internal long PhysicalReadConnectionsOpened => Interlocked.Read(ref _physicalReadConnectionsOpened);
        internal long ReadPoolHits => Interlocked.Read(ref _readPoolHits);

        private sealed class CachedFolderSignature
        {
            public readonly object Sync = new();
            public string? CaseInsensitiveSignature;
            public string? CaseSensitiveSignature;
            public int FileCount;
            public int TotalFiles;
            public long TotalBytes;
            public int UnityBundleCount;
            public int SerializedFileCount;
            public int ResourceFileCount;
            public int EncryptedFileCount;
            public int OtherFileCount;
            public int ErrorCount;
            public string? DecryptionContextDigest;
            public string? RootPath;

            public bool MatchesHeader(ProjectScanResult sr) =>
                FileCount == sr.Files.Count
                && TotalFiles == sr.TotalFiles
                && TotalBytes == sr.TotalBytes
                && UnityBundleCount == sr.UnityBundleCount
                && SerializedFileCount == sr.SerializedFileCount
                && ResourceFileCount == sr.ResourceFileCount
                && EncryptedFileCount == sr.EncryptedFileCount
                && OtherFileCount == sr.OtherFileCount
                && ErrorCount == sr.ErrorCount
                && string.Equals(DecryptionContextDigest, sr.DecryptionContextDigest, StringComparison.Ordinal)
                && string.Equals(RootPath, sr.RootPath, StringComparison.Ordinal);

            public void CaptureHeader(ProjectScanResult sr)
            {
                FileCount = sr.Files.Count;
                TotalFiles = sr.TotalFiles;
                TotalBytes = sr.TotalBytes;
                UnityBundleCount = sr.UnityBundleCount;
                SerializedFileCount = sr.SerializedFileCount;
                ResourceFileCount = sr.ResourceFileCount;
                EncryptedFileCount = sr.EncryptedFileCount;
                OtherFileCount = sr.OtherFileCount;
                ErrorCount = sr.ErrorCount;
                DecryptionContextDigest = sr.DecryptionContextDigest;
                RootPath = sr.RootPath;
            }
        }

        private static readonly ConditionalWeakTable<ProjectScanResult, CachedFolderSignature> s_folderSignatureCache = new();

        public string GetFolderSignature(ProjectScanResult scanResult)
        {
            return ComputeFolderSignature(scanResult, _caseSensitivePaths);
        }

        internal static string ComputeFolderSignature(ProjectScanResult scanResult, bool caseSensitive)
        {
            ArgumentNullException.ThrowIfNull(scanResult);

            var cacheEntry = s_folderSignatureCache.GetOrCreateValue(scanResult);
            lock (cacheEntry.Sync)
            {
                if (cacheEntry.MatchesHeader(scanResult))
                {
                    var cached = caseSensitive ? cacheEntry.CaseSensitiveSignature : cacheEntry.CaseInsensitiveSignature;
                    if (cached != null)
                    {
                        return cached;
                    }
                }
                else
                {
                    cacheEntry.CaseInsensitiveSignature = null;
                    cacheEntry.CaseSensitiveSignature = null;
                    cacheEntry.CaptureHeader(scanResult);
                }
            }

            var computed = ComputeFolderSignatureUncached(scanResult, caseSensitive);

            lock (cacheEntry.Sync)
            {
                if (!cacheEntry.MatchesHeader(scanResult))
                {
                    cacheEntry.CaseInsensitiveSignature = null;
                    cacheEntry.CaseSensitiveSignature = null;
                    cacheEntry.CaptureHeader(scanResult);
                }

                if (caseSensitive)
                {
                    cacheEntry.CaseSensitiveSignature = computed;
                }
                else
                {
                    cacheEntry.CaseInsensitiveSignature = computed;
                }
            }

            return computed;
        }

        private static string ComputeFolderSignatureUncached(ProjectScanResult scanResult, bool caseSensitive)
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var utf8Buf = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                var utf8Pos = 0;

                void FlushUtf8()
                {
                    if (utf8Pos > 0)
                    {
                        hasher.AppendData(utf8Buf.AsSpan(0, utf8Pos));
                        utf8Pos = 0;
                    }
                }

                void AppendText(string text)
                {
                    if (text.Length == 0)
                    {
                        return;
                    }

                    var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
                    if (maxBytes > utf8Buf.Length)
                    {
                        FlushUtf8();
                        ArrayPool<byte>.Shared.Return(utf8Buf);
                        utf8Buf = ArrayPool<byte>.Shared.Rent(maxBytes);
                    }
                    else if (utf8Pos + maxBytes > utf8Buf.Length)
                    {
                        FlushUtf8();
                    }

                    utf8Pos += Encoding.UTF8.GetBytes(text.AsSpan(), utf8Buf.AsSpan(utf8Pos));
                }

                void AppendInt64(long value)
                {
                    Span<char> chars = stackalloc char[32];
                    if (value.TryFormat(chars, out var written, default, CultureInfo.InvariantCulture))
                    {
                        if (utf8Pos + written > utf8Buf.Length)
                        {
                            FlushUtf8();
                        }

                        utf8Pos += Encoding.UTF8.GetBytes(chars.Slice(0, written), utf8Buf.AsSpan(utf8Pos));
                    }
                }

                void AppendHexUInt64(ulong value)
                {
                    Span<char> chars = stackalloc char[16];
                    if (value.TryFormat(chars, out var written, "x16", CultureInfo.InvariantCulture))
                    {
                        if (utf8Pos + written > utf8Buf.Length)
                        {
                            FlushUtf8();
                        }

                        utf8Pos += Encoding.UTF8.GetBytes(chars.Slice(0, written), utf8Buf.AsSpan(utf8Pos));
                    }
                }

                AppendText("manifest-v2\0C|");
                AppendInt64(scanResult.TotalFiles);
                AppendText("|");
                AppendInt64(scanResult.TotalBytes);
                AppendText("|");
                AppendInt64(scanResult.UnityBundleCount);
                AppendText("|");
                AppendInt64(scanResult.SerializedFileCount);
                AppendText("|");
                AppendInt64(scanResult.ResourceFileCount);
                AppendText("|");
                AppendInt64(scanResult.EncryptedFileCount);
                AppendText("|");
                AppendInt64(scanResult.OtherFileCount);
                AppendText("|");
                AppendInt64(scanResult.ErrorCount);
                AppendText("\0D|");
                AppendText(scanResult.DecryptionContextDigest ?? string.Empty);
                AppendText("\0");

                if (scanResult.Files.Count > 0)
                {
                    var rootFull = string.IsNullOrWhiteSpace(scanResult.RootPath)
                        ? string.Empty
                        : TrimTrailingDirectorySeparatorsPreservingRoot(GetFullPathOrOriginal(scanResult.RootPath));

                    var entries = new List<(string RelativePath, long Size, int Kind, long LastWriteTicks, ulong SampleHash, string AppliedProvider)>(scanResult.Files.Count);
                    for (var i = 0; i < scanResult.Files.Count; i++)
                    {
                        var file = scanResult.Files[i];
                        var relPath = NormalizeRelativeManifestPath(rootFull, file.Path, caseSensitive);
                        entries.Add((
                            relPath,
                            file.Size,
                            (int)file.Kind,
                            file.LastWriteTimeUtcTicks,
                            file.ContentSampleHash,
                            file.AppliedDecryptionProvider ?? string.Empty));
                    }

                    entries.Sort(static (a, b) =>
                    {
                        var cmp = string.CompareOrdinal(a.RelativePath, b.RelativePath);
                        if (cmp != 0) return cmp;
                        cmp = a.Size.CompareTo(b.Size);
                        if (cmp != 0) return cmp;
                        cmp = a.Kind.CompareTo(b.Kind);
                        if (cmp != 0) return cmp;
                        cmp = a.LastWriteTicks.CompareTo(b.LastWriteTicks);
                        if (cmp != 0) return cmp;
                        cmp = a.SampleHash.CompareTo(b.SampleHash);
                        if (cmp != 0) return cmp;
                        return string.CompareOrdinal(a.AppliedProvider, b.AppliedProvider);
                    });

                    for (var i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        AppendText("F|");
                        AppendText(e.RelativePath);
                        AppendText("|");
                        AppendInt64(e.Size);
                        AppendText("|");
                        AppendInt64(e.Kind);
                        AppendText("|");
                        AppendInt64(e.LastWriteTicks);
                        AppendText("|");
                        AppendHexUInt64(e.SampleHash);
                        AppendText("|");
                        AppendText(e.AppliedProvider);
                        AppendText("\0");
                    }
                }

                FlushUtf8();
                var hex = Convert.ToHexStringLower(hasher.GetHashAndReset());
                if (scanResult.EncryptedFileCount > 0)
                {
                    return $"v2:{hex}_e{scanResult.EncryptedFileCount}";
                }

                return $"v2:{hex}";
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(utf8Buf);
            }
        }

        internal static string NormalizeRelativeManifestPath(string rootFullPath, string? filePath, bool caseSensitive)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            string relative;
            if (!string.IsNullOrWhiteSpace(rootFullPath))
            {
                // Fast path when filePath is already a canonical descendant of rootFullPath (standard ScanFolder output),
                // avoiding tens of thousands of Path.GetFullPath + Path.GetRelativePath allocations.
                var prefixComparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (filePath.Length > rootFullPath.Length
                    && filePath.StartsWith(rootFullPath, prefixComparison)
                    && (filePath[rootFullPath.Length] == Path.DirectorySeparatorChar || filePath[rootFullPath.Length] == Path.AltDirectorySeparatorChar)
                    && filePath.IndexOf("..", rootFullPath.Length, StringComparison.Ordinal) < 0)
                {
                    relative = filePath.Substring(rootFullPath.Length + 1);
                }
                else
                {
                    try
                    {
                        relative = Path.GetRelativePath(rootFullPath, GetFullPathOrOriginal(filePath));
                    }
                    catch
                    {
                        relative = filePath;
                    }
                }
            }
            else
            {
                relative = filePath;
            }

            var normalized = relative.Contains('\\') ? relative.Replace('\\', '/') : relative;
            return caseSensitive ? normalized : normalized.ToUpperInvariant();
        }

        internal string GetDbPath(string folderPath)
        {
            var folderKey = GetFolderCacheKey(folderPath, _caseSensitivePaths);
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

        internal static string GetFolderCacheKey(string folderPath)
        {
            return GetFolderCacheKey(folderPath, OperatingSystem.IsLinux());
        }

        internal static string GetFolderCacheKey(string folderPath, bool caseSensitive)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            var normalized = NormalizeFolderPathKey(folderPath, caseSensitive);
            var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexStringLower(hashBytes);
        }

        internal static string TrimTrailingDirectorySeparatorsPreservingRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? path.Substring(0, 1) : trimmed;
        }

        internal static string NormalizeFolderPathKey(string folderPath, bool caseSensitive)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            var normalized = TrimTrailingDirectorySeparatorsPreservingRoot(GetFullPathOrOriginal(folderPath));
            return caseSensitive ? normalized : normalized.ToUpperInvariant();
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

        private string NormalizeProjectPathForCache(string folderPath)
        {
            return NormalizeFolderPathKey(folderPath, _caseSensitivePaths);
        }

        private string BuildProjectIdCacheKey(string dbPath, string folderPath, string signature)
        {
            var normalizedDb = _caseSensitivePaths ? dbPath : dbPath.ToUpperInvariant();
            return $"{normalizedDb}\u001f{NormalizeProjectPathForCache(folderPath)}\u001f{signature}";
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
            var normalizedDb = _caseSensitivePaths ? dbPath : dbPath.ToUpperInvariant();
            var prefix = normalizedDb + "\u001f";
            foreach (var key in _projectIdCache.Keys)
            {
                if (key.StartsWith(prefix, _pathComparison))
                {
                    _projectIdCache.TryRemove(key, out _);
                }
            }

            InvalidateSemanticStatusCacheForDb(dbPath);
        }

        private void InvalidateSemanticStatusCacheForDb(string dbPath)
        {
            var normalizedDb = _caseSensitivePaths ? dbPath : dbPath.ToUpperInvariant();
            var prefix = normalizedDb + "\u001f";
            foreach (var key in _semanticStatusCache.Keys)
            {
                if (key.StartsWith(prefix, _pathComparison))
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
            var projectId = FindProjectId(conn, transaction, folderPath, signature, _caseSensitivePaths);
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
            var projectId = FindProjectId(conn, transaction, folderPath, signature, _caseSensitivePaths);
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

                        if (TryUpgradeOrRebuildSchema(conn, _caseSensitivePaths))
                        {
                            InvalidateProjectIdCacheForDb(dbPath);
                            NotifyDatabaseMutated(dbPath);
                            InvalidateReadConnectionPool(dbPath);
                            MmapProjectIndexSidecar.DeleteSidecarFiles(dbPath);
                        }

                        CreateSchema(conn, _caseSensitivePaths);
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
            return FindProjectId(conn, transaction, folderPath, signature, OperatingSystem.IsLinux());
        }

        private static long? FindProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath, string signature, bool caseSensitivePaths)
        {
            var folderPathKey = NormalizeFolderPathKey(folderPath, caseSensitivePaths);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id FROM Projects
                WHERE FolderPathKey = @folderPathKey
                  AND SignatureHash = @signature
                ORDER BY LastIndexed DESC, Id DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue("@folderPathKey", folderPathKey);
            cmd.Parameters.AddWithValue("@signature", signature);
            var id = cmd.ExecuteScalar();
            return id == null ? null : Convert.ToInt64(id);
        }

        private static long? FindLatestProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath)
        {
            return FindLatestProjectId(conn, transaction, folderPath, OperatingSystem.IsLinux());
        }

        private static long? FindLatestProjectId(SqliteConnection conn, SqliteTransaction? transaction, string folderPath, bool caseSensitivePaths)
        {
            var folderPathKey = NormalizeFolderPathKey(folderPath, caseSensitivePaths);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Id
                FROM Projects
                WHERE FolderPathKey = @folderPathKey
                ORDER BY LastIndexed DESC, Id DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue("@folderPathKey", folderPathKey);
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
