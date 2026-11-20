using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        private const int ReadBusyTimeoutSeconds = 5;
        private static readonly object WriteGate = new object();
        private readonly string _cacheDir;
        private readonly HashSet<string> _initializedDbs = new();
        private readonly object _initLock = new object();

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

        public string GetFolderSignature(ProjectScanResult scanResult)
        {
            return $"{scanResult.TotalFiles}_{scanResult.TotalBytes}_{scanResult.UnityBundleCount}";
        }

        internal string GetDbPath(string folderPath)
        {
            var folderKey = GetFolderCacheKey(folderPath);
            return Path.Combine(_cacheDir, $"project_index_{folderKey}.db");
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
                    // Force close any pooled connections to this database first
                    using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                    {
                        SqliteConnection.ClearPool(conn);
                    }
                    using (var readConn = new SqliteConnection(BuildReadConnectionString(dbPath)))
                    {
                        SqliteConnection.ClearPool(readConn);
                    }

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

        private void EnsureInitialized(string folderPath)
        {
            var dbPath = GetDbPath(folderPath);
            lock (_initLock)
            {
                if (_initializedDbs.Contains(dbPath))
                {
                    return;
                }

                InitializeDatabase(folderPath);
                _initializedDbs.Add(dbPath);
            }
        }

        private static string BuildReadConnectionString(string dbPath)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = true,
                DefaultTimeout = ReadBusyTimeoutSeconds
            }.ToString();
        }

        private SqliteConnection CreateConnection(string folderPath)
        {
            var dbPath = GetDbPath(folderPath);
            var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.DefaultTimeout = 60;
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 60000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private SqliteConnection CreateReadConnection(string folderPath)
        {
            var connectionString = BuildReadConnectionString(GetDbPath(folderPath));
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = $@"
                PRAGMA query_only = ON;
                PRAGMA busy_timeout = {ReadBusyTimeoutSeconds * 1000};
                PRAGMA temp_store = MEMORY;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private void InitializeDatabase(string folderPath)
        {
            try
            {
                lock (WriteGate)
                {
                    using (var conn = CreateConnection(folderPath))
                    {
                        using (var pragma = conn.CreateCommand())
                        {
                            pragma.CommandText = @"
                                PRAGMA journal_mode = WAL;
                                PRAGMA synchronous = NORMAL;
                                PRAGMA wal_autocheckpoint = 1000;";
                            pragma.ExecuteNonQuery();
                        }

                        if (ShouldRebuildSchema(conn))
                        {
                            RebuildSchema(conn);
                        }

                        CreateSchema(conn);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize SQLite database cache for {folderPath}: {ex.Message}", ex);
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

        private void ExecuteProjectReadQuery(
            string folderPath,
            string signature,
            string errorContext,
            Action<SqliteConnection, SqliteTransaction, long> query)
        {
            EnsureInitialized(folderPath);
            try
            {
                using var conn = CreateReadConnection(folderPath);
                using var transaction = conn.BeginTransaction(deferred: true);
                var projectId = FindProjectId(conn, transaction, folderPath, signature);
                if (projectId == null)
                {
                    return;
                }

                query(conn, transaction, projectId.Value);
            }
            catch (Exception ex)
            {
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
