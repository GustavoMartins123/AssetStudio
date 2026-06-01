using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia
{
    public class SQLiteProjectIndexCache
    {
        private readonly string _dbPath;

        public SQLiteProjectIndexCache()
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetStudio", "IndexCache");
            Directory.CreateDirectory(cacheDir);
            _dbPath = Path.Combine(cacheDir, "project_index.db");
            InitializeDatabase();
        }

        private SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private void InitializeDatabase()
        {
            try
            {
                using (var conn = CreateConnection())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            PRAGMA journal_mode = WAL;
                            PRAGMA synchronous = NORMAL;

                            CREATE TABLE IF NOT EXISTS Projects (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                FolderPath TEXT NOT NULL,
                                SignatureHash TEXT NOT NULL,
                                TotalFiles INTEGER NOT NULL,
                                TotalBytes INTEGER NOT NULL,
                                UnityBundleCount INTEGER NOT NULL,
                                LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                                UnityVersion TEXT
                            );

                            CREATE TABLE IF NOT EXISTS AssetHandles (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                                UniqueID TEXT NOT NULL,
                                Name TEXT NOT NULL,
                                Type INTEGER NOT NULL,
                                Container TEXT,
                                OriginalPath TEXT,
                                SerializedFileName TEXT,
                                PathID INTEGER NOT NULL,
                                ByteStart INTEGER NOT NULL,
                                ByteSize INTEGER NOT NULL
                            );

                            CREATE INDEX IF NOT EXISTS idx_projects_path ON Projects(FolderPath);
                            CREATE INDEX IF NOT EXISTS idx_handles_project ON AssetHandles(ProjectId);
                            DROP INDEX IF EXISTS idx_handles_unique;
                        ";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize SQLite database cache: {ex.Message}", ex);
            }
        }

        public string GetFolderSignature(ProjectScanResult scanResult)
        {
            return $"{scanResult.TotalFiles}_{scanResult.TotalBytes}_{scanResult.UnityBundleCount}";
        }

        public List<AssetHandle>? LoadIndexCache(string folderPath, string signature)
        {
            try
            {
                using (var conn = CreateConnection())
                {
                    long? projectId = null;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Id FROM Projects WHERE FolderPath = @path AND SignatureHash = @signature LIMIT 1";
                        cmd.Parameters.AddWithValue("@path", folderPath);
                        cmd.Parameters.AddWithValue("@signature", signature);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                projectId = reader.GetInt64(0);
                            }
                        }
                    }

                    if (projectId == null)
                    {
                        return null;
                    }

                    // Update LastIndexed timestamp
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE Projects SET LastIndexed = CURRENT_TIMESTAMP WHERE Id = @id";
                        cmd.Parameters.AddWithValue("@id", projectId.Value);
                        cmd.ExecuteNonQuery();
                    }

                    var handles = new List<AssetHandle>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            SELECT UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize
                            FROM AssetHandles
                            WHERE ProjectId = @projectId";
                        cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                handles.Add(new AssetHandle
                                {
                                    UniqueID = reader.GetString(0),
                                    Name = reader.GetString(1),
                                    Type = (ClassIDType)reader.GetInt32(2),
                                    Container = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                    OriginalPath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                    SerializedFileName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                                    PathID = reader.GetInt64(6),
                                    ByteStart = reader.GetInt64(7),
                                    ByteSize = reader.GetInt64(8)
                                });
                            }
                        }
                    }
                    return handles;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load SQLite index cache: {ex.Message}");
                return null;
            }
        }

        public void SaveIndexCache(string folderPath, string signature, ProjectScanResult scanResult, string unityVersion, IEnumerable<AssetHandle> handles)
        {
            try
            {
                using (var conn = CreateConnection())
                {
                    using (var transaction = conn.BeginTransaction())
                    {
                        // 1. Delete old project entry (which deletes handles cascadingly)
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "DELETE FROM Projects WHERE FolderPath = @path";
                            cmd.Parameters.AddWithValue("@path", folderPath);
                            cmd.Transaction = transaction;
                            cmd.ExecuteNonQuery();
                        }

                        // 2. Insert new project entry
                        long projectId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                INSERT INTO Projects (FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                                VALUES (@path, @signature, @totalFiles, @totalBytes, @unityBundles, @unityVer);
                                SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("@path", folderPath);
                            cmd.Parameters.AddWithValue("@signature", signature);
                            cmd.Parameters.AddWithValue("@totalFiles", scanResult.TotalFiles);
                            cmd.Parameters.AddWithValue("@totalBytes", scanResult.TotalBytes);
                            cmd.Parameters.AddWithValue("@unityBundles", scanResult.UnityBundleCount);
                            cmd.Parameters.AddWithValue("@unityVer", unityVersion ?? (object)DBNull.Value);
                            cmd.Transaction = transaction;
                            var scalarResult = cmd.ExecuteScalar();
                            projectId = scalarResult != null ? Convert.ToInt64(scalarResult) : 0L;
                        }

                        // 3. Insert handles in batch using parameterized query
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                INSERT INTO AssetHandles (ProjectId, UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize)
                                VALUES (@projectId, @uniqueId, @name, @type, @container, @originalPath, @serializedFile, @pathId, @byteStart, @byteSize)";
                            cmd.Transaction = transaction;

                            var pProjectId = cmd.Parameters.Add("@projectId", SqliteType.Integer);
                            var pUniqueId = cmd.Parameters.Add("@uniqueId", SqliteType.Text);
                            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
                            var pType = cmd.Parameters.Add("@type", SqliteType.Integer);
                            var pContainer = cmd.Parameters.Add("@container", SqliteType.Text);
                            var pOriginalPath = cmd.Parameters.Add("@originalPath", SqliteType.Text);
                            var pSerializedFile = cmd.Parameters.Add("@serializedFile", SqliteType.Text);
                            var pPathId = cmd.Parameters.Add("@pathId", SqliteType.Integer);
                            var pByteStart = cmd.Parameters.Add("@byteStart", SqliteType.Integer);
                            var pByteSize = cmd.Parameters.Add("@byteSize", SqliteType.Integer);

                            pProjectId.Value = projectId;

                            foreach (var h in handles)
                            {
                                pUniqueId.Value = h.UniqueID ?? string.Empty;
                                pName.Value = h.Name ?? string.Empty;
                                pType.Value = (int)h.Type;
                                pContainer.Value = h.Container ?? (object)DBNull.Value;
                                pOriginalPath.Value = h.OriginalPath ?? (object)DBNull.Value;
                                pSerializedFile.Value = h.SerializedFileName ?? (object)DBNull.Value;
                                pPathId.Value = h.PathID;
                                pByteStart.Value = h.ByteStart;
                                pByteSize.Value = h.ByteSize;

                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                        Logger.Info($"Saved index cache in SQLite for: {folderPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save SQLite index cache: {ex.Message}");
            }
        }

        public void DeleteIndexCache(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var fullPath = GetFullPathOrOriginal(folderPath);

            try
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        DELETE FROM AssetHandles
                        WHERE ProjectId IN (
                            SELECT Id FROM Projects
                            WHERE FolderPath = @path OR FolderPath = @fullPath
                        )";
                    cmd.Parameters.AddWithValue("@path", folderPath);
                    cmd.Parameters.AddWithValue("@fullPath", fullPath);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM Projects WHERE FolderPath = @path OR FolderPath = @fullPath";
                    cmd.Parameters.AddWithValue("@path", folderPath);
                    cmd.Parameters.AddWithValue("@fullPath", fullPath);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to delete SQLite index cache: {ex.Message}");
            }
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
    }
}
