using AssetStudio;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AssetStudio.Avalonia;

public sealed class ManagedProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string AutoDetectedName { get; set; } = string.Empty;
    public bool UseAutoName { get; set; } = true;
    public string ProjectRoot { get; set; } = string.Empty;
    public string LastLoadPath { get; set; } = string.Empty;
    public string LastExportPath { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAccessedAtUtc { get; set; }
    public ManagedProjectStats Stats { get; set; } = new();

    public string PendingIconSourcePath { get; set; } = string.Empty;

    public string DisplayName
    {
        get
        {
            if (UseAutoName && !string.IsNullOrWhiteSpace(AutoDetectedName))
            {
                return AutoDetectedName;
            }

            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            if (!string.IsNullOrWhiteSpace(ProjectRoot))
            {
                return Path.GetFileName(ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return "Untitled project";
        }
    }

    public ManagedProject Clone()
    {
        return new ManagedProject
        {
            Id = Id,
            Name = Name,
            AutoDetectedName = AutoDetectedName,
            UseAutoName = UseAutoName,
            ProjectRoot = ProjectRoot,
            LastLoadPath = LastLoadPath,
            LastExportPath = LastExportPath,
            IconPath = IconPath,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            LastAccessedAtUtc = LastAccessedAtUtc,
            Stats = Stats.Clone()
        };
    }
}

public sealed class ManagedProjectStats
{
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public int UnityBundleCount { get; set; }
    public int SerializedFileCount { get; set; }
    public int ResourceFileCount { get; set; }
    public int AssetCount { get; set; }
    public int ExportableAssetCount { get; set; }
    public string LastScanSignature { get; set; } = string.Empty;
    public DateTime? LastScannedAtUtc { get; set; }

    public ManagedProjectStats Clone()
    {
        return new ManagedProjectStats
        {
            TotalFiles = TotalFiles,
            TotalBytes = TotalBytes,
            UnityBundleCount = UnityBundleCount,
            SerializedFileCount = SerializedFileCount,
            ResourceFileCount = ResourceFileCount,
            AssetCount = AssetCount,
            ExportableAssetCount = ExportableAssetCount,
            LastScanSignature = LastScanSignature,
            LastScannedAtUtc = LastScannedAtUtc
        };
    }
}

public sealed class ProjectLaunchContext
{
    public ProjectLaunchContext(ProjectManagerStore store, ManagedProject project, AvaloniaAppSettings settings)
    {
        Store = store;
        Project = project;
        Settings = settings;
    }

    public ProjectManagerStore Store { get; }
    public ManagedProject Project { get; }
    public AvaloniaAppSettings Settings { get; }
}

public sealed class ProjectManagerStore
{
    private const string AppSettingsJsonKey = "app_settings_json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".ico", ".bmp", ".webp" };

    public static ProjectManagerStore Shared { get; } = new();

    private readonly string _dbPath;
    private readonly string _iconsDirectory;

    public ProjectManagerStore()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "ProjectManager");

        Directory.CreateDirectory(baseDirectory);
        _iconsDirectory = Path.Combine(baseDirectory, "Icons");
        Directory.CreateDirectory(_iconsDirectory);
        _dbPath = Path.Combine(baseDirectory, "projects.db");

        InitializeDatabase();
    }

    public string DatabasePath => _dbPath;
    public string IconsDirectory => _iconsDirectory;

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
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS ManagedProjects (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                AutoDetectedName TEXT,
                UseAutoName INTEGER NOT NULL DEFAULT 1,
                ProjectRoot TEXT,
                LastLoadPath TEXT,
                LastExportPath TEXT,
                IconPath TEXT,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                LastAccessedAtUtc TEXT
            );

            CREATE TABLE IF NOT EXISTS ManagedProjectStats (
                ProjectId TEXT PRIMARY KEY REFERENCES ManagedProjects(Id) ON DELETE CASCADE,
                TotalFiles INTEGER NOT NULL DEFAULT 0,
                TotalBytes INTEGER NOT NULL DEFAULT 0,
                UnityBundleCount INTEGER NOT NULL DEFAULT 0,
                SerializedFileCount INTEGER NOT NULL DEFAULT 0,
                ResourceFileCount INTEGER NOT NULL DEFAULT 0,
                AssetCount INTEGER NOT NULL DEFAULT 0,
                ExportableAssetCount INTEGER NOT NULL DEFAULT 0,
                LastScanSignature TEXT,
                LastScannedAtUtc TEXT
            );

            CREATE TABLE IF NOT EXISTS ManagedProjectSettings (
                ProjectId TEXT NOT NULL REFERENCES ManagedProjects(Id) ON DELETE CASCADE,
                Key TEXT NOT NULL,
                Value TEXT,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ProjectId, Key)
            );

            CREATE TABLE IF NOT EXISTS ManagedProjectIcons (
                ProjectId TEXT PRIMARY KEY REFERENCES ManagedProjects(Id) ON DELETE CASCADE,
                SourcePath TEXT,
                StoredFileName TEXT NOT NULL,
                ContentHash TEXT,
                Width INTEGER,
                Height INTEGER,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS GlobalSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_managed_projects_root ON ManagedProjects(ProjectRoot);
            CREATE INDEX IF NOT EXISTS idx_managed_projects_last_accessed ON ManagedProjects(LastAccessedAtUtc);
        ";
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ManagedProject> GetProjects()
    {
        var projects = new List<ManagedProject>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                p.Id,
                p.Name,
                p.AutoDetectedName,
                p.UseAutoName,
                p.ProjectRoot,
                p.LastLoadPath,
                p.LastExportPath,
                p.IconPath,
                p.CreatedAtUtc,
                p.UpdatedAtUtc,
                p.LastAccessedAtUtc,
                s.TotalFiles,
                s.TotalBytes,
                s.UnityBundleCount,
                s.SerializedFileCount,
                s.ResourceFileCount,
                s.AssetCount,
                s.ExportableAssetCount,
                s.LastScanSignature,
                s.LastScannedAtUtc
            FROM ManagedProjects p
            LEFT JOIN ManagedProjectStats s ON s.ProjectId = p.Id
            ORDER BY
                CASE WHEN p.LastAccessedAtUtc IS NULL THEN 1 ELSE 0 END,
                p.LastAccessedAtUtc DESC,
                p.CreatedAtUtc DESC";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public ManagedProject? GetProject(string projectId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                p.Id,
                p.Name,
                p.AutoDetectedName,
                p.UseAutoName,
                p.ProjectRoot,
                p.LastLoadPath,
                p.LastExportPath,
                p.IconPath,
                p.CreatedAtUtc,
                p.UpdatedAtUtc,
                p.LastAccessedAtUtc,
                s.TotalFiles,
                s.TotalBytes,
                s.UnityBundleCount,
                s.SerializedFileCount,
                s.ResourceFileCount,
                s.AssetCount,
                s.ExportableAssetCount,
                s.LastScanSignature,
                s.LastScannedAtUtc
            FROM ManagedProjects p
            LEFT JOIN ManagedProjectStats s ON s.ProjectId = p.Id
            WHERE p.Id = @id
            LIMIT 1";
        cmd.Parameters.AddWithValue("@id", projectId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    public void SaveProject(ManagedProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Id))
        {
            project.Id = Guid.NewGuid().ToString("N");
        }

        var now = DateTime.UtcNow;
        if (project.CreatedAtUtc == default)
        {
            project.CreatedAtUtc = now;
        }
        project.UpdatedAtUtc = now;

        var storedIcon = ResolveProjectIcon(project);
        if (storedIcon != null)
        {
            project.IconPath = storedIcon.StoredPath;
        }

        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO ManagedProjects (
                    Id, Name, AutoDetectedName, UseAutoName, ProjectRoot, LastLoadPath, LastExportPath,
                    IconPath, CreatedAtUtc, UpdatedAtUtc, LastAccessedAtUtc
                )
                VALUES (
                    @id, @name, @autoName, @useAutoName, @projectRoot, @lastLoadPath, @lastExportPath,
                    @iconPath, @createdAt, @updatedAt, @lastAccessed
                )
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    AutoDetectedName = excluded.AutoDetectedName,
                    UseAutoName = excluded.UseAutoName,
                    ProjectRoot = excluded.ProjectRoot,
                    LastLoadPath = excluded.LastLoadPath,
                    LastExportPath = excluded.LastExportPath,
                    IconPath = excluded.IconPath,
                    UpdatedAtUtc = excluded.UpdatedAtUtc,
                    LastAccessedAtUtc = excluded.LastAccessedAtUtc";
            AddProjectParameters(cmd, project);
            cmd.ExecuteNonQuery();
        }

        UpsertStats(conn, transaction, project.Id, project.Stats);

        if (storedIcon != null)
        {
            using var iconCmd = conn.CreateCommand();
            iconCmd.Transaction = transaction;
            iconCmd.CommandText = @"
                INSERT INTO ManagedProjectIcons (
                    ProjectId, SourcePath, StoredFileName, ContentHash, Width, Height, UpdatedAtUtc
                )
                VALUES (@projectId, @sourcePath, @storedFileName, @hash, @width, @height, @updatedAt)
                ON CONFLICT(ProjectId) DO UPDATE SET
                    SourcePath = excluded.SourcePath,
                    StoredFileName = excluded.StoredFileName,
                    ContentHash = excluded.ContentHash,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    UpdatedAtUtc = excluded.UpdatedAtUtc";
            iconCmd.Parameters.AddWithValue("@projectId", project.Id);
            iconCmd.Parameters.AddWithValue("@sourcePath", storedIcon.SourcePath);
            iconCmd.Parameters.AddWithValue("@storedFileName", Path.GetFileName(storedIcon.StoredPath));
            iconCmd.Parameters.AddWithValue("@hash", storedIcon.Hash);
            iconCmd.Parameters.AddWithValue("@width", storedIcon.Width > 0 ? storedIcon.Width : DBNull.Value);
            iconCmd.Parameters.AddWithValue("@height", storedIcon.Height > 0 ? storedIcon.Height : DBNull.Value);
            iconCmd.Parameters.AddWithValue("@updatedAt", ToDb(now));
            iconCmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void RemoveProject(string projectId)
    {
        var project = GetProject(projectId);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ManagedProjects WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();

        if (project != null)
        {
            DeleteStoredIcon(project.IconPath);
        }
    }

    public void TouchProject(string projectId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ManagedProjects SET LastAccessedAtUtc = @now, UpdatedAtUtc = @now WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.Parameters.AddWithValue("@now", ToDb(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    public AvaloniaAppSettings LoadGlobalSettings()
    {
        var json = GetGlobalSetting(AppSettingsJsonKey);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<AvaloniaAppSettings>(json) ?? new AvaloniaAppSettings();
            }
            catch
            {
                return new AvaloniaAppSettings();
            }
        }

        var migrated = AvaloniaAppSettings.LoadLegacyJson();
        SaveGlobalSettings(migrated);
        return migrated;
    }

    public void SaveGlobalSettings(AvaloniaAppSettings settings)
    {
        SaveGlobalSetting(AppSettingsJsonKey, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public AvaloniaAppSettings LoadProjectSettings(ManagedProject project)
    {
        var settings = LoadGlobalSettings();
        var json = GetProjectSetting(project.Id, AppSettingsJsonKey);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AvaloniaAppSettings>(json) ?? settings;
            }
            catch
            {
            }
        }

        settings.ProjectRoot = project.ProjectRoot ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(project.LastLoadPath))
        {
            settings.LoadFolderPath = project.LastLoadPath;
        }
        else if (!string.IsNullOrWhiteSpace(project.ProjectRoot))
        {
            settings.LoadFolderPath = project.ProjectRoot;
        }

        if (!string.IsNullOrWhiteSpace(project.LastExportPath))
        {
            settings.ExportFolderPath = project.LastExportPath;
        }

        return settings;
    }

    public void SaveProjectSettings(string projectId, AvaloniaAppSettings settings)
    {
        var now = DateTime.UtcNow;
        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();
        using (var settingsCmd = conn.CreateCommand())
        {
            settingsCmd.Transaction = transaction;
            settingsCmd.CommandText = @"
                INSERT INTO ManagedProjectSettings (ProjectId, Key, Value, UpdatedAtUtc)
                VALUES (@projectId, @key, @value, @updatedAt)
                ON CONFLICT(ProjectId, Key) DO UPDATE SET
                    Value = excluded.Value,
                    UpdatedAtUtc = excluded.UpdatedAtUtc";
            settingsCmd.Parameters.AddWithValue("@projectId", projectId);
            settingsCmd.Parameters.AddWithValue("@key", AppSettingsJsonKey);
            settingsCmd.Parameters.AddWithValue("@value", JsonSerializer.Serialize(settings, JsonOptions));
            settingsCmd.Parameters.AddWithValue("@updatedAt", ToDb(now));
            settingsCmd.ExecuteNonQuery();
        }

        using (var projectCmd = conn.CreateCommand())
        {
            projectCmd.Transaction = transaction;
            projectCmd.CommandText = @"
                UPDATE ManagedProjects
                SET ProjectRoot = @projectRoot,
                    LastLoadPath = @lastLoadPath,
                    LastExportPath = @lastExportPath,
                    UpdatedAtUtc = @updatedAt
                WHERE Id = @projectId";
            projectCmd.Parameters.AddWithValue("@projectId", projectId);
            projectCmd.Parameters.AddWithValue("@projectRoot", settings.ProjectRoot ?? string.Empty);
            projectCmd.Parameters.AddWithValue("@lastLoadPath", settings.LoadFolderPath ?? string.Empty);
            projectCmd.Parameters.AddWithValue("@lastExportPath", settings.ExportFolderPath ?? string.Empty);
            projectCmd.Parameters.AddWithValue("@updatedAt", ToDb(now));
            projectCmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void UpdateProjectAfterLoad(
        string projectId,
        ProjectScanResult? scanResult,
        string? scanSignature,
        string? autoDetectedName,
        int assetCount,
        int exportableAssetCount)
    {
        var project = GetProject(projectId);
        if (project == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(autoDetectedName))
        {
            project.AutoDetectedName = autoDetectedName.Trim();
        }

        if (scanResult != null)
        {
            project.Stats.TotalFiles = scanResult.TotalFiles;
            project.Stats.TotalBytes = scanResult.TotalBytes;
            project.Stats.UnityBundleCount = scanResult.UnityBundleCount;
            project.Stats.SerializedFileCount = scanResult.SerializedFileCount;
            project.Stats.ResourceFileCount = scanResult.ResourceFileCount;
            project.Stats.LastScanSignature = scanSignature ?? string.Empty;
            project.Stats.LastScannedAtUtc = DateTime.UtcNow;
        }

        project.Stats.AssetCount = assetCount;
        project.Stats.ExportableAssetCount = exportableAssetCount;
        project.UpdatedAtUtc = DateTime.UtcNow;

        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();
        using (var projectCmd = conn.CreateCommand())
        {
            projectCmd.Transaction = transaction;
            projectCmd.CommandText = @"
                UPDATE ManagedProjects
                SET AutoDetectedName = @autoDetectedName,
                    UpdatedAtUtc = @updatedAt
                WHERE Id = @projectId";
            projectCmd.Parameters.AddWithValue("@projectId", projectId);
            projectCmd.Parameters.AddWithValue("@autoDetectedName", project.AutoDetectedName ?? string.Empty);
            projectCmd.Parameters.AddWithValue("@updatedAt", ToDb(project.UpdatedAtUtc));
            projectCmd.ExecuteNonQuery();
        }
        UpsertStats(conn, transaction, project.Id, project.Stats);
        transaction.Commit();
    }

    public string? TryFindProjectIcon(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(projectRoot);
        var rootName = Path.GetFileName(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var commonDirectories = new List<string> { normalizedRoot };

        if (!string.IsNullOrWhiteSpace(rootName))
        {
            commonDirectories.Add(Path.Combine(normalizedRoot, rootName + "_Data"));
            commonDirectories.Add(Path.Combine(normalizedRoot, rootName + "_Data", "Resources"));
        }

        commonDirectories.Add(Path.Combine(normalizedRoot, "Assets"));
        commonDirectories.Add(Path.Combine(normalizedRoot, "ProjectSettings"));

        foreach (var directory in commonDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directMatch = FindIconInDirectory(directory, recursive: false, maxFiles: 200);
            if (directMatch != null)
            {
                return directMatch;
            }
        }

        var executable = TryFindProjectExecutable(normalizedRoot, rootName);
        if (executable != null)
        {
            var previewIcon = TryCreateExecutableIconPreview(executable);
            if (previewIcon != null)
            {
                return previewIcon;
            }
        }

        return FindIconInDirectory(normalizedRoot, recursive: true, maxFiles: 5000);
    }

    public string? TryCreateExecutableIconPreview(string executablePath)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(executablePath)
            || !File.Exists(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var hash = ComputeFileHash(executablePath);
            var previewPath = Path.Combine(_iconsDirectory, "detected-" + hash[..Math.Min(hash.Length, 16)] + ".png");
            if (File.Exists(previewPath))
            {
                return previewPath;
            }

            return TryExtractExecutableIconToPng(executablePath, previewPath) ? previewPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static ManagedProject ReadProject(SqliteDataReader reader)
    {
        return new ManagedProject
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            AutoDetectedName = GetNullableString(reader, 2),
            UseAutoName = reader.GetInt32(3) != 0,
            ProjectRoot = GetNullableString(reader, 4),
            LastLoadPath = GetNullableString(reader, 5),
            LastExportPath = GetNullableString(reader, 6),
            IconPath = GetNullableString(reader, 7),
            CreatedAtUtc = ParseDbDate(reader.GetString(8)) ?? DateTime.UtcNow,
            UpdatedAtUtc = ParseDbDate(reader.GetString(9)) ?? DateTime.UtcNow,
            LastAccessedAtUtc = reader.IsDBNull(10) ? null : ParseDbDate(reader.GetString(10)),
            Stats = new ManagedProjectStats
            {
                TotalFiles = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                TotalBytes = reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                UnityBundleCount = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                SerializedFileCount = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                ResourceFileCount = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                AssetCount = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                ExportableAssetCount = reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                LastScanSignature = GetNullableString(reader, 18),
                LastScannedAtUtc = reader.IsDBNull(19) ? null : ParseDbDate(reader.GetString(19))
            }
        };
    }

    private void AddProjectParameters(SqliteCommand cmd, ManagedProject project)
    {
        cmd.Parameters.AddWithValue("@id", project.Id);
        cmd.Parameters.AddWithValue("@name", project.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("@autoName", project.AutoDetectedName ?? string.Empty);
        cmd.Parameters.AddWithValue("@useAutoName", project.UseAutoName ? 1 : 0);
        cmd.Parameters.AddWithValue("@projectRoot", project.ProjectRoot ?? string.Empty);
        cmd.Parameters.AddWithValue("@lastLoadPath", project.LastLoadPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@lastExportPath", project.LastExportPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@iconPath", project.IconPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@createdAt", ToDb(project.CreatedAtUtc));
        cmd.Parameters.AddWithValue("@updatedAt", ToDb(project.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("@lastAccessed", project.LastAccessedAtUtc.HasValue ? ToDb(project.LastAccessedAtUtc.Value) : DBNull.Value);
    }

    private static void UpsertStats(SqliteConnection conn, SqliteTransaction transaction, string projectId, ManagedProjectStats stats)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO ManagedProjectStats (
                ProjectId, TotalFiles, TotalBytes, UnityBundleCount, SerializedFileCount,
                ResourceFileCount, AssetCount, ExportableAssetCount, LastScanSignature, LastScannedAtUtc
            )
            VALUES (
                @projectId, @totalFiles, @totalBytes, @unityBundleCount, @serializedFileCount,
                @resourceFileCount, @assetCount, @exportableAssetCount, @lastScanSignature, @lastScannedAt
            )
            ON CONFLICT(ProjectId) DO UPDATE SET
                TotalFiles = excluded.TotalFiles,
                TotalBytes = excluded.TotalBytes,
                UnityBundleCount = excluded.UnityBundleCount,
                SerializedFileCount = excluded.SerializedFileCount,
                ResourceFileCount = excluded.ResourceFileCount,
                AssetCount = excluded.AssetCount,
                ExportableAssetCount = excluded.ExportableAssetCount,
                LastScanSignature = excluded.LastScanSignature,
                LastScannedAtUtc = excluded.LastScannedAtUtc";
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@totalFiles", stats.TotalFiles);
        cmd.Parameters.AddWithValue("@totalBytes", stats.TotalBytes);
        cmd.Parameters.AddWithValue("@unityBundleCount", stats.UnityBundleCount);
        cmd.Parameters.AddWithValue("@serializedFileCount", stats.SerializedFileCount);
        cmd.Parameters.AddWithValue("@resourceFileCount", stats.ResourceFileCount);
        cmd.Parameters.AddWithValue("@assetCount", stats.AssetCount);
        cmd.Parameters.AddWithValue("@exportableAssetCount", stats.ExportableAssetCount);
        cmd.Parameters.AddWithValue("@lastScanSignature", stats.LastScanSignature ?? string.Empty);
        cmd.Parameters.AddWithValue("@lastScannedAt", stats.LastScannedAtUtc.HasValue ? ToDb(stats.LastScannedAtUtc.Value) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private StoredProjectIcon? ResolveProjectIcon(ManagedProject project)
    {
        var sourcePath = project.PendingIconSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath)
            && string.IsNullOrWhiteSpace(project.IconPath)
            && !string.IsNullOrWhiteSpace(project.ProjectRoot))
        {
            sourcePath = TryFindProjectIcon(project.ProjectRoot) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        if (string.Equals(Path.GetExtension(sourcePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return TryExtractExecutableIconToStore(project.Id, sourcePath);
        }

        return CopyIconToStore(project.Id, sourcePath);
    }

    private StoredProjectIcon? TryExtractExecutableIconToStore(string projectId, string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var storedPath = Path.Combine(_iconsDirectory, projectId + ".png");
        if (!TryExtractExecutableIconToPng(executablePath, storedPath))
        {
            return null;
        }

        var icon = new StoredProjectIcon
        {
            SourcePath = executablePath,
            StoredPath = storedPath,
            Hash = ComputeFileHash(storedPath)
        };

        try
        {
            var imageInfo = SixLabors.ImageSharp.Image.Identify(storedPath);
            if (imageInfo != null)
            {
                icon.Width = imageInfo.Width;
                icon.Height = imageInfo.Height;
            }
        }
        catch
        {
        }

        return icon;
    }

    private StoredProjectIcon CopyIconToStore(string projectId, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension) || !ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            extension = ".png";
        }

        var storedPath = Path.Combine(_iconsDirectory, projectId + extension.ToLowerInvariant());
        var fullStoredPath = Path.GetFullPath(storedPath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!string.Equals(fullStoredPath, fullSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, storedPath, overwrite: true);
        }

        var icon = new StoredProjectIcon
        {
            SourcePath = sourcePath,
            StoredPath = storedPath,
            Hash = ComputeFileHash(storedPath)
        };

        try
        {
            var imageInfo = SixLabors.ImageSharp.Image.Identify(storedPath);
            if (imageInfo != null)
            {
                icon.Width = imageInfo.Width;
                icon.Height = imageInfo.Height;
            }
        }
        catch
        {
        }

        return icon;
    }

    private void DeleteStoredIcon(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return;
        }

        try
        {
            var fullIconPath = Path.GetFullPath(iconPath);
            var fullIconDirectory = Path.GetFullPath(_iconsDirectory);
            if (fullIconPath.StartsWith(fullIconDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(fullIconPath))
            {
                File.Delete(fullIconPath);
            }
        }
        catch
        {
        }
    }

    private string? FindIconInDirectory(string directory, bool recursive, int maxFiles)
    {
        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var best = Directory.EnumerateFiles(directory, "*.*", option)
                .Take(maxFiles)
                .Where(IsIconCandidate)
                .OrderBy(GetIconCandidateScore)
                .FirstOrDefault();
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryFindProjectExecutable(string projectRoot, string? rootName)
    {
        try
        {
            var executables = Directory.EnumerateFiles(projectRoot, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => GetExecutableCandidateScore(path, rootName))
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            return executables.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int GetExecutableCandidateScore(string path, string? rootName)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(rootName))
        {
            if (string.Equals(fileName, rootName, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (fileName.Contains(rootName, StringComparison.OrdinalIgnoreCase)
                || rootName.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
        }

        return 10;
    }

    private static bool IsIconCandidate(string path)
    {
        var extension = Path.GetExtension(path);
        if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name.Contains("icon", StringComparison.Ordinal)
            || name.Contains("logo", StringComparison.Ordinal)
            || name.Contains("app", StringComparison.Ordinal);
    }

    private static int GetIconCandidateScore(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name is "icon" or "app_icon" or "appicon" or "game_icon" or "gameicon")
        {
            return 0;
        }

        if (name.Contains("icon", StringComparison.Ordinal))
        {
            return 1;
        }

        if (name.Contains("logo", StringComparison.Ordinal))
        {
            return 2;
        }

        return 3;
    }

    private static bool TryExtractExecutableIconToPng(string executablePath, string outputPngPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var icons = new IntPtr[1];
        var ids = new uint[1];
        var extracted = PrivateExtractIcons(executablePath, 0, 256, 256, icons, ids, 1, 0);
        if ((extracted == 0 || icons[0] == IntPtr.Zero)
            && PrivateExtractIcons(executablePath, 0, 64, 64, icons, ids, 1, 0) == 0)
        {
            return false;
        }

        var hIcon = icons[0];
        if (hIcon == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return TrySaveHiconAsPng(hIcon, outputPngPath);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static bool TrySaveHiconAsPng(IntPtr hIcon, string outputPngPath)
    {
        if (!GetIconInfo(hIcon, out var iconInfo))
        {
            return false;
        }

        try
        {
            var bitmapHandle = iconInfo.hbmColor != IntPtr.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
            if (bitmapHandle == IntPtr.Zero)
            {
                return false;
            }

            if (GetObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out var bitmap) == 0)
            {
                return false;
            }

            var width = bitmap.bmWidth;
            var height = iconInfo.hbmColor != IntPtr.Zero ? bitmap.bmHeight : bitmap.bmHeight / 2;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var pixels = new byte[width * height * 4];
            var info = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BiRgb,
                    biSizeImage = (uint)pixels.Length
                }
            };

            var hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (GetDIBits(hdc, bitmapHandle, 0, (uint)height, pixels, ref info, DibRgbColors) == 0)
                {
                    return false;
                }
            }
            finally
            {
                DeleteDC(hdc);
            }

            EnsureVisibleAlpha(pixels);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, width, height);
            image.SaveAsPng(outputPngPath);
            return true;
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmColor);
            }
            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    private static void EnsureVisibleAlpha(byte[] bgraPixels)
    {
        var hasAlpha = false;
        for (int i = 3; i < bgraPixels.Length; i += 4)
        {
            if (bgraPixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (hasAlpha)
        {
            return;
        }

        for (int i = 3; i < bgraPixels.Length; i += 4)
        {
            bgraPixels[i] = 255;
        }
    }

    private string? GetGlobalSetting(string key)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM GlobalSettings WHERE Key = @key LIMIT 1";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SaveGlobalSetting(string key, string value)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GlobalSettings (Key, Value, UpdatedAtUtc)
            VALUES (@key, @value, @updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAtUtc = excluded.UpdatedAtUtc";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@updatedAt", ToDb(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    private string? GetProjectSetting(string projectId, string key)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM ManagedProjectSettings WHERE ProjectId = @projectId AND Key = @key LIMIT 1";
        cmd.Parameters.AddWithValue("@projectId", projectId);
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static string ComputeFileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string GetNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string ToDb(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseDbDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private sealed class StoredProjectIcon
    {
        public string SourcePath { get; init; } = string.Empty;
        public string StoredPath { get; init; } = string.Empty;
        public string Hash { get; init; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
    }

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("Gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out NativeBitmap lpvObject);

    [DllImport("Gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("Gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("Gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        byte[] lpvBits,
        ref BitmapInfo lpbmi,
        uint usage);

    [DllImport("Gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
