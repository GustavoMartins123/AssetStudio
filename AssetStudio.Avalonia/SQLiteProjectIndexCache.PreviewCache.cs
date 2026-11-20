using System;

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
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
                using var conn = CreateReadConnection(folderPath);
                var projectId = FindProjectId(conn, null, folderPath, signature);
                if (projectId == null)
                {
                    return null;
                }

                PreviewCacheEntry? entry = null;
                using (var cmd = conn.CreateCommand())
                {
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
                        entry = new PreviewCacheEntry(
                            reader.GetString(1),
                            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            reader.GetInt64(3));
                    }
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

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO PreviewCacheEntries (
                            ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize)
                        VALUES (
                            @projectId, @assetUniqueId, @previewKind, @algorithmVersion, @parameters, @payloadHash, @payloadPath, @byteSize)
                        ON CONFLICT(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters)
                        DO UPDATE SET
                            PayloadHash = excluded.PayloadHash,
                            PayloadPath = excluded.PayloadPath,
                            ByteSize = excluded.ByteSize,
                            LastAccessed = CURRENT_TIMESTAMP";
                    cmd.Parameters.AddWithValue("@projectId", projectId.Value);
                    cmd.Parameters.AddWithValue("@assetUniqueId", assetUniqueId);
                    cmd.Parameters.AddWithValue("@previewKind", previewKind);
                    cmd.Parameters.AddWithValue("@algorithmVersion", algorithmVersion);
                    cmd.Parameters.AddWithValue("@parameters", parameters ?? string.Empty);
                    cmd.Parameters.AddWithValue("@payloadHash", payloadHash);
                    cmd.Parameters.AddWithValue("@payloadPath", payloadPath);
                    cmd.Parameters.AddWithValue("@byteSize", byteSize);
                    cmd.ExecuteNonQuery();

                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save preview cache entry to SQLite: {ex.Message}");
            }
        }
    }
}
