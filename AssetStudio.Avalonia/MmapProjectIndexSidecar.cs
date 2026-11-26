using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AssetStudio.Avalonia
{
    public readonly record struct SourceFileMetadata(string SerializedFileName, string OriginalPath);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct MmapIndexHeader
    {
        public readonly ulong Magic;
        public readonly uint FormatVersion;
        public readonly int SchemaVersion;
        public readonly ulong PayloadChecksum;
        public readonly int RecordCount;
        public readonly int SourceFileCount;
        public readonly long SourceFilesOffset;
        public readonly long SourceFilesLength;
        public readonly long RecordsOffset;
        public readonly long RecordsLength;
        public readonly long StringPoolOffset;
        public readonly long StringPoolLength;
        public readonly int SignatureOffset;
        public readonly int SignatureLength;
        public readonly ulong SignatureHash64;
        public readonly long TotalFileBytes;
        public readonly long Reserved;

        public MmapIndexHeader(
            ulong magic,
            uint formatVersion,
            int schemaVersion,
            ulong payloadChecksum,
            int recordCount,
            int sourceFileCount,
            long sourceFilesOffset,
            long sourceFilesLength,
            long recordsOffset,
            long recordsLength,
            long stringPoolOffset,
            long stringPoolLength,
            int signatureOffset,
            int signatureLength,
            ulong signatureHash64,
            long totalFileBytes,
            long reserved = 0)
        {
            Magic = magic;
            FormatVersion = formatVersion;
            SchemaVersion = schemaVersion;
            PayloadChecksum = payloadChecksum;
            RecordCount = recordCount;
            SourceFileCount = sourceFileCount;
            SourceFilesOffset = sourceFilesOffset;
            SourceFilesLength = sourceFilesLength;
            RecordsOffset = recordsOffset;
            RecordsLength = recordsLength;
            StringPoolOffset = stringPoolOffset;
            StringPoolLength = stringPoolLength;
            SignatureOffset = signatureOffset;
            SignatureLength = signatureLength;
            SignatureHash64 = signatureHash64;
            TotalFileBytes = totalFileBytes;
            Reserved = reserved;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct CompactSourceFileRecord
    {
        public readonly int SerializedFileNameOffset;
        public readonly int SerializedFileNameLength;
        public readonly int OriginalPathOffset;
        public readonly int OriginalPathLength;

        public CompactSourceFileRecord(
            int serializedFileNameOffset,
            int serializedFileNameLength,
            int originalPathOffset,
            int originalPathLength)
        {
            SerializedFileNameOffset = serializedFileNameOffset;
            SerializedFileNameLength = serializedFileNameLength;
            OriginalPathOffset = originalPathOffset;
            OriginalPathLength = originalPathLength;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct CompactAssetRecord
    {
        public readonly long PathID;
        public readonly long ByteStart;
        public readonly long ByteSize;
        public readonly int SourceFileIndex;
        public readonly int ClassID;
        public readonly int UniqueIdOffset;
        public readonly int UniqueIdLength;
        public readonly int NameOffset;
        public readonly int NameLength;
        public readonly int ContainerOffset;
        public readonly int ContainerLength;

        public CompactAssetRecord(
            long pathId,
            long byteStart,
            long byteSize,
            int sourceFileIndex,
            int classId,
            int uniqueIdOffset,
            int uniqueIdLength,
            int nameOffset,
            int nameLength,
            int containerOffset,
            int containerLength)
        {
            PathID = pathId;
            ByteStart = byteStart;
            ByteSize = byteSize;
            SourceFileIndex = sourceFileIndex;
            ClassID = classId;
            UniqueIdOffset = uniqueIdOffset;
            UniqueIdLength = uniqueIdLength;
            NameOffset = nameOffset;
            NameLength = nameLength;
            ContainerOffset = containerOffset;
            ContainerLength = containerLength;
        }
    }

    public sealed unsafe class MmapProjectIndexSnapshot : IDisposable
    {
        private FileStream? _fileStream;
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private byte* _basePtr;
        private int _fileLength;
        private bool _ptrAcquired;
        private int _disposed;

        internal MmapProjectIndexSnapshot(
            FileStream fileStream,
            MemoryMappedFile mmf,
            MemoryMappedViewAccessor accessor,
            byte* basePtr,
            int fileLength,
            MmapIndexHeader header,
            string signatureHash)
        {
            _fileStream = fileStream;
            _mmf = mmf;
            _accessor = accessor;
            _basePtr = basePtr;
            _fileLength = fileLength;
            _ptrAcquired = true;
            Header = header;
            SignatureHash = signatureHash;
        }

        public MmapIndexHeader Header { get; }
        public string SignatureHash { get; }
        public int RecordCount => Header.RecordCount;
        public int SourceFileCount => Header.SourceFileCount;

        public ReadOnlySpan<byte> FileBytes
        {
            get
            {
                EnsureNotDisposed();
                return new ReadOnlySpan<byte>(_basePtr, _fileLength);
            }
        }

        public ReadOnlySpan<CompactSourceFileRecord> SourceFiles
        {
            get
            {
                EnsureNotDisposed();
                if (Header.SourceFileCount == 0)
                {
                    return ReadOnlySpan<CompactSourceFileRecord>.Empty;
                }

                var slice = new ReadOnlySpan<byte>(_basePtr + (int)Header.SourceFilesOffset, (int)Header.SourceFilesLength);
                return MemoryMarshal.Cast<byte, CompactSourceFileRecord>(slice);
            }
        }

        public ReadOnlySpan<CompactAssetRecord> Records
        {
            get
            {
                EnsureNotDisposed();
                if (Header.RecordCount == 0)
                {
                    return ReadOnlySpan<CompactAssetRecord>.Empty;
                }

                var slice = new ReadOnlySpan<byte>(_basePtr + (int)Header.RecordsOffset, (int)Header.RecordsLength);
                return MemoryMarshal.Cast<byte, CompactAssetRecord>(slice);
            }
        }

        public ReadOnlySpan<byte> StringPool
        {
            get
            {
                EnsureNotDisposed();
                if (Header.StringPoolLength == 0)
                {
                    return ReadOnlySpan<byte>.Empty;
                }

                return new ReadOnlySpan<byte>(_basePtr + (int)Header.StringPoolOffset, (int)Header.StringPoolLength);
            }
        }

        public ReadOnlySpan<byte> GetUtf8Span(int offset, int length)
        {
            if (length == 0)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            var pool = StringPool;
            if ((uint)offset > (uint)pool.Length || (uint)length > (uint)(pool.Length - offset))
            {
                throw new InvalidDataException("String pool slice exceeds bounds.");
            }

            return pool.Slice(offset, length);
        }

        public string GetString(int offset, int length)
        {
            if (length == 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(GetUtf8Span(offset, length));
        }

        public List<AssetHandle> MaterializeHandles()
        {
            EnsureNotDisposed();
            var recordCount = Header.RecordCount;
            if (recordCount == 0)
            {
                return new List<AssetHandle>();
            }

            var stringPool = StringPool;
            var poolLen = stringPool.Length;
            var sourceRecords = SourceFiles;
            var origins = new AssetHandleOrigin[sourceRecords.Length];
            var originPrefixUtf8 = new byte[sourceRecords.Length][];

            for (var i = 0; i < sourceRecords.Length; i++)
            {
                ref readonly var src = ref sourceRecords[i];
                ValidateSlice(src.SerializedFileNameOffset, src.SerializedFileNameLength, poolLen);
                ValidateSlice(src.OriginalPathOffset, src.OriginalPathLength, poolLen);

                var serializedFileName = src.SerializedFileNameLength > 0
                    ? Encoding.UTF8.GetString(stringPool.Slice(src.SerializedFileNameOffset, src.SerializedFileNameLength))
                    : string.Empty;
                var originalPath = src.OriginalPathLength > 0
                    ? Encoding.UTF8.GetString(stringPool.Slice(src.OriginalPathOffset, src.OriginalPathLength))
                    : string.Empty;
                var origin = AssetHandleOrigin.Intern(serializedFileName, originalPath);
                origins[i] = origin;
                originPrefixUtf8[i] = Encoding.UTF8.GetBytes(origin.UniqueIdPrefix);
            }

            var records = Records;
            var stringCache = new DecodedStringSlot[2048];
            var handles = new List<AssetHandle>(records.Length);
            CollectionsMarshal.SetCount(handles, records.Length);
            var handleSpan = CollectionsMarshal.AsSpan(handles);
            Span<byte> pathIdUtf8Scratch = stackalloc byte[32];

            for (var i = 0; i < records.Length; i++)
            {
                ref readonly var rec = ref records[i];
                ValidateSlice(rec.UniqueIdOffset, rec.UniqueIdLength, poolLen);

                var name = DecodePooledString(stringPool, poolLen, stringCache, rec.NameOffset, rec.NameLength);
                var container = DecodePooledString(stringPool, poolLen, stringCache, rec.ContainerOffset, rec.ContainerLength);

                var hasOrigin = (uint)rec.SourceFileIndex < (uint)origins.Length;
                var origin = hasOrigin ? origins[rec.SourceFileIndex] : AssetHandleOrigin.Empty;
                var prefixBytes = hasOrigin ? originPrefixUtf8[rec.SourceFileIndex] : Array.Empty<byte>();
                var uidBytes = rec.UniqueIdLength > 0
                    ? stringPool.Slice(rec.UniqueIdOffset, rec.UniqueIdLength)
                    : ReadOnlySpan<byte>.Empty;

                if (hasOrigin && IsCanonicalUniqueIdUtf8(uidBytes, prefixBytes, rec.PathID, pathIdUtf8Scratch))
                {
                    handleSpan[i] = AssetHandle.CreateCanonical(
                        origin,
                        rec.PathID,
                        name,
                        (ClassIDType)rec.ClassID,
                        container,
                        rec.ByteStart,
                        rec.ByteSize);
                }
                else
                {
                    var uniqueId = uidBytes.Length > 0
                        ? Encoding.UTF8.GetString(uidBytes)
                        : string.Empty;

                    handleSpan[i] = AssetHandle.CreateWithOrigin(
                        origin,
                        uniqueId,
                        rec.PathID,
                        name,
                        (ClassIDType)rec.ClassID,
                        container,
                        rec.ByteStart,
                        rec.ByteSize);
                }
            }

            return handles;
        }

        public int PopulateArena(ProjectIndex targetIndex, out List<SourceFileMetadata> sourceFiles)
        {
            return PopulateArena(targetIndex, out sourceFiles, out _);
        }

        internal int PopulateArena(
            ProjectIndex targetIndex,
            out List<SourceFileMetadata> sourceFiles,
            out bool hasMissingOriginalPaths)
        {
            ArgumentNullException.ThrowIfNull(targetIndex);
            EnsureNotDisposed();

            var stringPool = StringPool;
            var poolLen = stringPool.Length;
            var sourceRecords = SourceFiles;
            sourceFiles = new List<SourceFileMetadata>(sourceRecords.Length);
            hasMissingOriginalPaths = false;

            var recordCount = Header.RecordCount;
            var origins = new AssetHandleOrigin[sourceRecords.Length];
            var originPrefixUtf8 = new byte[sourceRecords.Length][];
            var sourceHasMissingOriginalPath = new bool[sourceRecords.Length];

            for (var i = 0; i < sourceRecords.Length; i++)
            {
                ref readonly var src = ref sourceRecords[i];
                ValidateSlice(src.SerializedFileNameOffset, src.SerializedFileNameLength, poolLen);
                ValidateSlice(src.OriginalPathOffset, src.OriginalPathLength, poolLen);

                var serializedFileName = src.SerializedFileNameLength > 0
                    ? Encoding.UTF8.GetString(stringPool.Slice(src.SerializedFileNameOffset, src.SerializedFileNameLength))
                    : string.Empty;
                var originalPath = src.OriginalPathLength > 0
                    ? Encoding.UTF8.GetString(stringPool.Slice(src.OriginalPathOffset, src.OriginalPathLength))
                    : string.Empty;

                sourceFiles.Add(new SourceFileMetadata(serializedFileName, originalPath));
                sourceHasMissingOriginalPath[i] = string.IsNullOrWhiteSpace(originalPath);
                var origin = AssetHandleOrigin.Intern(serializedFileName, originalPath);
                origins[i] = origin;
                originPrefixUtf8[i] = Encoding.UTF8.GetBytes(origin.UniqueIdPrefix);
            }

            if (recordCount == 0)
            {
                return 0;
            }

            var records = Records;
            var stringCache = new DecodedStringSlot[16384];
            Span<byte> pathIdUtf8Scratch = stackalloc byte[32];

            var lastSourceIndex = int.MinValue;
            var lastHasOrigin = false;
            var lastOrigin = AssetHandleOrigin.Empty;
            var lastPrefixBytes = Array.Empty<byte>();
            var lastOriginMissingPath = true;

            for (var i = 0; i < records.Length; i++)
            {
                ref readonly var rec = ref records[i];
                ValidateSlice(rec.UniqueIdOffset, rec.UniqueIdLength, poolLen);

                var name = DecodePooledString16384(stringPool, poolLen, stringCache, rec.NameOffset, rec.NameLength);
                var container = DecodePooledString16384(stringPool, poolLen, stringCache, rec.ContainerOffset, rec.ContainerLength);

                var srcIndex = rec.SourceFileIndex;
                if (srcIndex != lastSourceIndex)
                {
                    lastSourceIndex = srcIndex;
                    lastHasOrigin = (uint)srcIndex < (uint)origins.Length;
                    if (lastHasOrigin)
                    {
                        lastOrigin = origins[srcIndex];
                        lastPrefixBytes = originPrefixUtf8[srcIndex];
                        lastOriginMissingPath = sourceHasMissingOriginalPath[srcIndex];
                    }
                    else
                    {
                        lastOrigin = AssetHandleOrigin.Empty;
                        lastPrefixBytes = Array.Empty<byte>();
                        lastOriginMissingPath = true;
                    }
                }

                if (lastOriginMissingPath)
                {
                    hasMissingOriginalPaths = true;
                }

                var uidBytes = rec.UniqueIdLength > 0
                    ? stringPool.Slice(rec.UniqueIdOffset, rec.UniqueIdLength)
                    : ReadOnlySpan<byte>.Empty;

                if (lastHasOrigin && IsCanonicalUniqueIdUtf8(uidBytes, lastPrefixBytes, rec.PathID, pathIdUtf8Scratch))
                {
                    targetIndex.AddRecord(
                        serializedFileName: lastOrigin.SerializedFileName,
                        originalPath: lastOrigin.OriginalPath,
                        pathId: rec.PathID,
                        name: name,
                        type: (ClassIDType)rec.ClassID,
                        container: container,
                        byteStart: rec.ByteStart,
                        byteSize: rec.ByteSize,
                        sourceHash: lastOrigin.SourceHash,
                        unityVersion: lastOrigin.UnityVersion);
                }
                else
                {
                    var uniqueId = uidBytes.Length > 0
                        ? Encoding.UTF8.GetString(uidBytes)
                        : string.Empty;

                    targetIndex.AddRecord(
                        serializedFileName: lastOrigin.SerializedFileName,
                        originalPath: lastOrigin.OriginalPath,
                        pathId: rec.PathID,
                        name: name,
                        type: (ClassIDType)rec.ClassID,
                        container: container,
                        byteStart: rec.ByteStart,
                        byteSize: rec.ByteSize,
                        sourceHash: lastOrigin.SourceHash,
                        unityVersion: lastOrigin.UnityVersion,
                        customUniqueId: uniqueId);
                }
            }

            return records.Length;
        }

        internal int PopulateArenaFiltered(
            ProjectIndex targetIndex,
            HashSet<(string SerializedFileName, string OriginalPath)> allowedSources,
            out List<SourceFileMetadata> sourceFiles)
        {
            ArgumentNullException.ThrowIfNull(targetIndex);
            ArgumentNullException.ThrowIfNull(allowedSources);
            EnsureNotDisposed();

            var stringPool = StringPool;
            var poolLen = stringPool.Length;
            var sourceRecords = SourceFiles;
            sourceFiles = new List<SourceFileMetadata>(allowedSources.Count);

            var recordCount = Header.RecordCount;
            var origins = new AssetHandleOrigin[sourceRecords.Length];
            var originPrefixUtf8 = new byte[sourceRecords.Length][];
            var sourceAllowed = new bool[sourceRecords.Length];
            var matchedSourceCount = 0;

            for (var i = 0; i < sourceRecords.Length; i++)
            {
                ref readonly var src = ref sourceRecords[i];
                ValidateSlice(src.SerializedFileNameOffset, src.SerializedFileNameLength, poolLen);
                ValidateSlice(src.OriginalPathOffset, src.OriginalPathLength, poolLen);

                var serializedFileName = src.SerializedFileNameLength > 0
                    ? Encoding.UTF8.GetString(stringPool.Slice(src.SerializedFileNameOffset, src.SerializedFileNameLength))
                    : string.Empty;
                var originalPath = src.OriginalPathLength > 0
                    ? Encoding.UTF8.GetString(stringPool.Slice(src.OriginalPathOffset, src.OriginalPathLength))
                    : string.Empty;

                if (!allowedSources.Contains((serializedFileName, originalPath)))
                {
                    continue;
                }

                sourceAllowed[i] = true;
                matchedSourceCount++;
                sourceFiles.Add(new SourceFileMetadata(serializedFileName, originalPath));
                var origin = AssetHandleOrigin.Intern(serializedFileName, originalPath);
                origins[i] = origin;
                originPrefixUtf8[i] = Encoding.UTF8.GetBytes(origin.UniqueIdPrefix);
            }

            if (recordCount == 0 || matchedSourceCount == 0)
            {
                return 0;
            }

            var records = Records;
            var stringCache = new DecodedStringSlot[16384];
            Span<byte> pathIdUtf8Scratch = stackalloc byte[32];

            var lastSourceIndex = int.MinValue;
            var lastAllowed = false;
            var lastOrigin = AssetHandleOrigin.Empty;
            var lastPrefixBytes = Array.Empty<byte>();
            var addedCount = 0;

            for (var i = 0; i < records.Length; i++)
            {
                ref readonly var rec = ref records[i];
                var srcIndex = rec.SourceFileIndex;
                if (srcIndex != lastSourceIndex)
                {
                    lastSourceIndex = srcIndex;
                    lastAllowed = (uint)srcIndex < (uint)sourceAllowed.Length && sourceAllowed[srcIndex];
                    if (lastAllowed)
                    {
                        lastOrigin = origins[srcIndex];
                        lastPrefixBytes = originPrefixUtf8[srcIndex];
                    }
                    else
                    {
                        lastOrigin = AssetHandleOrigin.Empty;
                        lastPrefixBytes = Array.Empty<byte>();
                    }
                }

                if (!lastAllowed)
                {
                    continue;
                }

                ValidateSlice(rec.UniqueIdOffset, rec.UniqueIdLength, poolLen);

                var name = DecodePooledString16384(stringPool, poolLen, stringCache, rec.NameOffset, rec.NameLength);
                var container = DecodePooledString16384(stringPool, poolLen, stringCache, rec.ContainerOffset, rec.ContainerLength);
                var uidBytes = rec.UniqueIdLength > 0
                    ? stringPool.Slice(rec.UniqueIdOffset, rec.UniqueIdLength)
                    : ReadOnlySpan<byte>.Empty;

                if (IsCanonicalUniqueIdUtf8(uidBytes, lastPrefixBytes, rec.PathID, pathIdUtf8Scratch))
                {
                    targetIndex.AddRecord(
                        serializedFileName: lastOrigin.SerializedFileName,
                        originalPath: lastOrigin.OriginalPath,
                        pathId: rec.PathID,
                        name: name,
                        type: (ClassIDType)rec.ClassID,
                        container: container,
                        byteStart: rec.ByteStart,
                        byteSize: rec.ByteSize,
                        sourceHash: lastOrigin.SourceHash,
                        unityVersion: lastOrigin.UnityVersion);
                }
                else
                {
                    var uniqueId = uidBytes.Length > 0
                        ? Encoding.UTF8.GetString(uidBytes)
                        : string.Empty;

                    targetIndex.AddRecord(
                        serializedFileName: lastOrigin.SerializedFileName,
                        originalPath: lastOrigin.OriginalPath,
                        pathId: rec.PathID,
                        name: name,
                        type: (ClassIDType)rec.ClassID,
                        container: container,
                        byteStart: rec.ByteStart,
                        byteSize: rec.ByteSize,
                        sourceHash: lastOrigin.SourceHash,
                        unityVersion: lastOrigin.UnityVersion,
                        customUniqueId: uniqueId);
                }

                addedCount++;
            }

            return addedCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCanonicalUniqueIdUtf8(
            ReadOnlySpan<byte> uidBytes,
            ReadOnlySpan<byte> prefixBytes,
            long pathId,
            Span<byte> pathIdUtf8Scratch)
        {
            if (uidBytes.Length <= prefixBytes.Length + 1 || uidBytes[prefixBytes.Length] != (byte)'#')
            {
                return false;
            }

            if (!pathId.TryFormat(pathIdUtf8Scratch, out var written, default, System.Globalization.CultureInfo.InvariantCulture))
            {
                return false;
            }

            return uidBytes.Length == prefixBytes.Length + 1 + written
                && uidBytes.Slice(0, prefixBytes.Length).SequenceEqual(prefixBytes)
                && uidBytes.Slice(prefixBytes.Length + 1).SequenceEqual(pathIdUtf8Scratch.Slice(0, written));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ValidateSlice(int offset, int length, int poolLen)
        {
            if ((uint)offset > (uint)poolLen || (uint)length > (uint)(poolLen - offset))
            {
                throw new InvalidDataException("Corrupt string pool offset/length in .idx sidecar.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string DecodePooledString(
            ReadOnlySpan<byte> stringPool,
            int poolLen,
            DecodedStringSlot[] cache,
            int offset,
            int length)
        {
            if (length == 0)
            {
                return string.Empty;
            }

            ValidateSlice(offset, length, poolLen);

            var slotIndex = (int)(((uint)offset * 2654435761u) >> 21);
            ref var slot = ref cache[slotIndex];
            if (slot.Offset == offset && slot.Length == length && slot.Value != null)
            {
                return slot.Value;
            }

            var value = Encoding.UTF8.GetString(stringPool.Slice(offset, length));
            slot.Offset = offset;
            slot.Length = length;
            slot.Value = value;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string DecodePooledString16384(
            ReadOnlySpan<byte> stringPool,
            int poolLen,
            DecodedStringSlot[] cache,
            int offset,
            int length)
        {
            if (length == 0)
            {
                return string.Empty;
            }

            ValidateSlice(offset, length, poolLen);

            var slotIndex = (int)(((uint)offset * 2654435761u) >> 18);
            ref var slot = ref cache[slotIndex];
            if (slot.Offset == offset && slot.Length == length && slot.Value != null)
            {
                return slot.Value;
            }

            var value = Encoding.UTF8.GetString(stringPool.Slice(offset, length));
            slot.Offset = offset;
            slot.Length = length;
            slot.Value = value;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureNotDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _basePtr = null;
            if (_ptrAcquired && _accessor != null)
            {
                try
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                catch
                {
                }

                _ptrAcquired = false;
            }

            try
            {
                _accessor?.Dispose();
            }
            catch
            {
            }

            _accessor = null;

            try
            {
                _mmf?.Dispose();
            }
            catch
            {
            }

            _mmf = null;

            try
            {
                _fileStream?.Dispose();
            }
            catch
            {
            }

            _fileStream = null;
        }

        private struct DecodedStringSlot
        {
            public int Offset;
            public int Length;
            public string? Value;
        }
    }

    public static class MmapProjectIndexSidecar
    {
        // Little-endian "ASIDX001"
        public const ulong MagicValue = 0x3130305844495341UL;
        public const uint CurrentFormatVersion = 1;
        public static readonly int HeaderSize = Unsafe.SizeOf<MmapIndexHeader>();
        public static readonly int SourceRecordSize = Unsafe.SizeOf<CompactSourceFileRecord>();
        public static readonly int AssetRecordSize = Unsafe.SizeOf<CompactAssetRecord>();

        public static string GetSidecarPath(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                throw new ArgumentException("Database path must not be empty.", nameof(dbPath));
            }

            return Path.ChangeExtension(dbPath, ".idx");
        }

        public static string GetTempSidecarPath(string idxPath)
        {
            return idxPath + ".tmp";
        }

        public static string CreateUniqueTempSidecarPath(string idxPath)
        {
            return $"{idxPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        }

        public static void DeleteSidecarFiles(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return;
            }

            var idxPath = GetSidecarPath(dbPath);
            TryDeleteFileQuietly(idxPath);
            CleanupOrphanedTempSidecars(idxPath);
            TryDeleteFileQuietly(dbPath + ".idx");
            CleanupOrphanedTempSidecars(dbPath + ".idx");
        }

        public static void CleanupOrphanedTempSidecars(string idxPath)
        {
            if (string.IsNullOrWhiteSpace(idxPath))
            {
                return;
            }

            TryDeleteFileQuietly(GetTempSidecarPath(idxPath));
            try
            {
                var dir = Path.GetDirectoryName(idxPath);
                var baseName = Path.GetFileName(idxPath);
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(baseName) || !Directory.Exists(dir))
                {
                    return;
                }

                foreach (var orphan in Directory.EnumerateFiles(dir, baseName + ".*.tmp", SearchOption.TopDirectoryOnly))
                {
                    TryDeleteFileQuietly(orphan);
                }
            }
            catch
            {
            }
        }

        internal static void TryDeleteFileQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        public static bool TryWriteSidecar(
            string idxPath,
            string signatureHash,
            int schemaVersion,
            IReadOnlyCollection<AssetHandle> handles)
        {
            return TryWriteSidecar(
                idxPath,
                signatureHash,
                schemaVersion,
                handles,
                caseSensitivePaths: OperatingSystem.IsLinux(),
                beforeValidateHook: null);
        }

        internal static bool TryWriteSidecar(
            string idxPath,
            string signatureHash,
            int schemaVersion,
            IReadOnlyCollection<AssetHandle> handles,
            bool caseSensitivePaths,
            Action<string>? beforeValidateHook = null)
        {
            if (!TryStageSidecar(idxPath, signatureHash, schemaVersion, handles, caseSensitivePaths, out var tempPath, beforeValidateHook))
            {
                CleanupOrphanedTempSidecars(idxPath);
                return false;
            }

            try
            {
                return TryPublishStagedSidecar(tempPath, idxPath);
            }
            finally
            {
                TryDeleteFileQuietly(tempPath);
                CleanupOrphanedTempSidecars(idxPath);
            }
        }

        internal static bool TryStageSidecar(
            string idxPath,
            string signatureHash,
            int schemaVersion,
            IReadOnlyCollection<AssetHandle> handles,
            bool caseSensitivePaths,
            out string stagedTempPath,
            Action<string>? beforeValidateHook = null)
        {
            return TryStageSidecar(
                idxPath,
                signatureHash,
                schemaVersion,
                handles,
                caseSensitivePaths,
                out stagedTempPath,
                out _,
                beforeValidateHook);
        }

        private sealed class SourcePairComparer : IEqualityComparer<(string SerializedFileName, string OriginalPath)>
        {
            public static readonly SourcePairComparer Ordinal = new(StringComparer.Ordinal);
            public static readonly SourcePairComparer OrdinalIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

            private readonly StringComparer _comparer;

            private SourcePairComparer(StringComparer comparer) => _comparer = comparer;

            public bool Equals((string SerializedFileName, string OriginalPath) x, (string SerializedFileName, string OriginalPath) y) =>
                _comparer.Equals(x.SerializedFileName, y.SerializedFileName)
                && _comparer.Equals(x.OriginalPath, y.OriginalPath);

            public int GetHashCode((string SerializedFileName, string OriginalPath) obj) =>
                HashCode.Combine(_comparer.GetHashCode(obj.SerializedFileName), _comparer.GetHashCode(obj.OriginalPath));
        }

        internal static bool TryStageSidecar(
            string idxPath,
            string signatureHash,
            int schemaVersion,
            IReadOnlyCollection<AssetHandle> handles,
            bool caseSensitivePaths,
            out string stagedTempPath,
            out List<AssetHandle> orderedHandlesOut,
            Action<string>? beforeValidateHook = null,
            bool isAlreadyNormalizedAndOrdered = false)
        {
            stagedTempPath = string.Empty;
            orderedHandlesOut = new List<AssetHandle>();
            if (string.IsNullOrWhiteSpace(idxPath))
            {
                return false;
            }

            var tempPath = CreateUniqueTempSidecarPath(idxPath);
            try
            {
                var dir = Path.GetDirectoryName(idxPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Order by UniqueID (Ordinal) and collapse duplicate UniqueIDs to match SQLite PRIMARY KEY (ProjectId, UniqueID)
                var orderedHandles = isAlreadyNormalizedAndOrdered && handles is List<AssetHandle> preOrderedList
                    ? preOrderedList
                    : NormalizeAndDeduplicateHandles(handles);
                orderedHandlesOut = orderedHandles;

                // Intern source files using OS-aware path comparison without allocating interpolated strings per handle.
                var pairComparer = caseSensitivePaths ? SourcePairComparer.Ordinal : SourcePairComparer.OrdinalIgnoreCase;
                var sourceIndexByKey = new Dictionary<(string SerializedFileName, string OriginalPath), int>(pairComparer);
                var sourceEntries = new List<(string SerializedFileName, string OriginalPath)>();
                var handleSourceIndices = new int[orderedHandles.Count];
                string? lastSfn = null;
                string? lastOrig = null;
                int lastSourceIdx = -1;
                for (var i = 0; i < orderedHandles.Count; i++)
                {
                    var h = orderedHandles[i];
                    var sfn = h.SerializedFileName ?? string.Empty;
                    var orig = h.OriginalPath ?? string.Empty;
                    if (ReferenceEquals(sfn, lastSfn) && ReferenceEquals(orig, lastOrig))
                    {
                        handleSourceIndices[i] = lastSourceIdx;
                        continue;
                    }

                    var key = (sfn, orig);
                    if (!sourceIndexByKey.TryGetValue(key, out var srcIdx))
                    {
                        srcIdx = sourceEntries.Count;
                        sourceIndexByKey[key] = srcIdx;
                        sourceEntries.Add(key);
                    }

                    lastSfn = sfn;
                    lastOrig = orig;
                    lastSourceIdx = srcIdx;
                    handleSourceIndices[i] = srcIdx;
                }

                var stringPool = new StringPoolBuilder(Math.Max(256, orderedHandles.Count * 48));
                var (sigOffset, sigLength) = stringPool.AddDeduplicated(signatureHash ?? string.Empty);
                var signatureUtf8 = Encoding.UTF8.GetBytes(signatureHash ?? string.Empty);
                var signatureHash64 = ComputeXxHash64(signatureUtf8);

                var sourceRecords = new CompactSourceFileRecord[sourceEntries.Count];
                for (var i = 0; i < sourceEntries.Count; i++)
                {
                    var (sfn, orig) = sourceEntries[i];
                    var (sfnOff, sfnLen) = stringPool.AddDeduplicated(sfn);
                    var (origOff, origLen) = stringPool.AddDeduplicated(orig);
                    sourceRecords[i] = new CompactSourceFileRecord(sfnOff, sfnLen, origOff, origLen);
                }

                var assetRecords = new CompactAssetRecord[orderedHandles.Count];
                for (var i = 0; i < orderedHandles.Count; i++)
                {
                    var h = orderedHandles[i];
                    var sourceIdx = handleSourceIndices[i];

                    // UniqueID is unique per record after deduplication; write directly without dictionary overhead
                    var (uidOff, uidLen) = stringPool.AddDirect(h.UniqueID ?? string.Empty);
                    var (nameOff, nameLen) = stringPool.AddDeduplicated(h.Name ?? string.Empty);
                    var (contOff, contLen) = stringPool.AddDeduplicated(h.Container ?? string.Empty);

                    assetRecords[i] = new CompactAssetRecord(
                        pathId: h.PathID,
                        byteStart: h.ByteStart,
                        byteSize: h.ByteSize,
                        sourceFileIndex: sourceIdx,
                        classId: (int)h.Type,
                        uniqueIdOffset: uidOff,
                        uniqueIdLength: uidLen,
                        nameOffset: nameOff,
                        nameLength: nameLen,
                        containerOffset: contOff,
                        containerLength: contLen);
                }

                long sourceFilesOffset = HeaderSize;
                long sourceFilesLength = (long)sourceRecords.Length * SourceRecordSize;
                long recordsOffset = sourceFilesOffset + sourceFilesLength;
                long recordsLength = (long)assetRecords.Length * AssetRecordSize;
                long stringPoolOffset = recordsOffset + recordsLength;
                long stringPoolLength = stringPool.Length;
                long totalFileBytes = stringPoolOffset + stringPoolLength;

                if (totalFileBytes > int.MaxValue)
                {
                    return false;
                }

                var fileBuffer = new byte[(int)totalFileBytes];
                var fileSpan = fileBuffer.AsSpan();

                if (sourceRecords.Length > 0)
                {
                    MemoryMarshal.AsBytes(sourceRecords.AsSpan())
                        .CopyTo(fileSpan.Slice((int)sourceFilesOffset, (int)sourceFilesLength));
                }

                if (assetRecords.Length > 0)
                {
                    MemoryMarshal.AsBytes(assetRecords.AsSpan())
                        .CopyTo(fileSpan.Slice((int)recordsOffset, (int)recordsLength));
                }

                if (stringPool.Length > 0)
                {
                    stringPool.WrittenSpan.CopyTo(fileSpan.Slice((int)stringPoolOffset, (int)stringPoolLength));
                }

                var provisionalHeader = new MmapIndexHeader(
                    magic: MagicValue,
                    formatVersion: CurrentFormatVersion,
                    schemaVersion: schemaVersion,
                    payloadChecksum: 0UL,
                    recordCount: assetRecords.Length,
                    sourceFileCount: sourceRecords.Length,
                    sourceFilesOffset: sourceFilesOffset,
                    sourceFilesLength: sourceFilesLength,
                    recordsOffset: recordsOffset,
                    recordsLength: recordsLength,
                    stringPoolOffset: stringPoolOffset,
                    stringPoolLength: stringPoolLength,
                    signatureOffset: sigOffset,
                    signatureLength: sigLength,
                    signatureHash64: signatureHash64,
                    totalFileBytes: totalFileBytes);

                MemoryMarshal.Write(fileSpan.Slice(0, HeaderSize), in provisionalHeader);
                var checksum = ComputeFileChecksum(fileSpan);

                var finalHeader = new MmapIndexHeader(
                    magic: MagicValue,
                    formatVersion: CurrentFormatVersion,
                    schemaVersion: schemaVersion,
                    payloadChecksum: checksum,
                    recordCount: assetRecords.Length,
                    sourceFileCount: sourceRecords.Length,
                    sourceFilesOffset: sourceFilesOffset,
                    sourceFilesLength: sourceFilesLength,
                    recordsOffset: recordsOffset,
                    recordsLength: recordsLength,
                    stringPoolOffset: stringPoolOffset,
                    stringPoolLength: stringPoolLength,
                    signatureOffset: sigOffset,
                    signatureLength: sigLength,
                    signatureHash64: signatureHash64,
                    totalFileBytes: totalFileBytes);

                MemoryMarshal.Write(fileSpan.Slice(0, HeaderSize), in finalHeader);

                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
                {
                    fs.Write(fileSpan);
                    fs.Flush(flushToDisk: beforeValidateHook != null);
                }

                if (beforeValidateHook != null)
                {
                    beforeValidateHook.Invoke(tempPath);
                    if (!ValidateFileImage(tempPath, signatureHash ?? string.Empty, schemaVersion, out _))
                    {
                        Logger.Warning($"Staged mmap .idx sidecar failed pre-publish validation: '{tempPath}'");
                        TryDeleteFileQuietly(tempPath);
                        return false;
                    }
                }
                else
                {
                    // Checksum was already computed over the exact buffer written above; verify written file length without re-reading hundreds of MB.
                    var writtenLength = new FileInfo(tempPath).Length;
                    if (writtenLength != totalFileBytes)
                    {
                        Logger.Warning($"Staged mmap .idx sidecar length mismatch ({writtenLength} != {totalFileBytes}): '{tempPath}'");
                        TryDeleteFileQuietly(tempPath);
                        return false;
                    }
                }

                stagedTempPath = tempPath;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to stage mmap .idx sidecar '{idxPath}': {ex.Message}");
                TryDeleteFileQuietly(tempPath);
                return false;
            }
        }

        internal static bool TryPublishStagedSidecar(string stagedTempPath, string idxPath)
        {
            if (string.IsNullOrWhiteSpace(stagedTempPath) || string.IsNullOrWhiteSpace(idxPath) || !File.Exists(stagedTempPath))
            {
                return false;
            }

            try
            {
                File.Move(stagedTempPath, idxPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to publish mmap .idx sidecar '{idxPath}': {ex.Message}");
                TryDeleteFileQuietly(stagedTempPath);
                return false;
            }
        }

        public static bool ValidateFileImage(
            string filePath,
            string expectedSignature,
            int expectedSchemaVersion,
            out MmapIndexHeader header)
        {
            header = default;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(filePath);
                if (info.Length < HeaderSize || info.Length > int.MaxValue)
                {
                    return false;
                }

                var bytes = File.ReadAllBytes(filePath);
                return ValidateFileImage(bytes, expectedSignature ?? string.Empty, expectedSchemaVersion, out header);
            }
            catch
            {
                header = default;
                return false;
            }
        }

        public static bool TryLoadHandles(
            string idxPath,
            string expectedSignature,
            int expectedSchemaVersion,
            out List<AssetHandle> handles)
        {
            handles = new List<AssetHandle>();
            if (!TryOpenSnapshot(idxPath, expectedSignature, expectedSchemaVersion, out var snapshot) || snapshot == null)
            {
                return false;
            }

            try
            {
                using (snapshot)
                {
                    handles = snapshot.MaterializeHandles();
                    return true;
                }
            }
            catch
            {
                handles = new List<AssetHandle>();
                return false;
            }
        }

        public static bool TryLoadIntoArena(
            string idxPath,
            string expectedSignature,
            int expectedSchemaVersion,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? sourceFiles)
        {
            return TryLoadIntoArena(idxPath, expectedSignature, expectedSchemaVersion, targetIndex, out sourceFiles, out _);
        }

        internal static bool TryLoadIntoArena(
            string idxPath,
            string expectedSignature,
            int expectedSchemaVersion,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? sourceFiles,
            out bool hasMissingOriginalPaths)
        {
            ArgumentNullException.ThrowIfNull(targetIndex);
            sourceFiles = null;
            hasMissingOriginalPaths = false;

            if (!TryOpenSnapshot(idxPath, expectedSignature, expectedSchemaVersion, out var snapshot) || snapshot == null)
            {
                return false;
            }

            try
            {
                using (snapshot)
                {
                    snapshot.PopulateArena(targetIndex, out var loadedSources, out hasMissingOriginalPaths);
                    sourceFiles = loadedSources;
                    return true;
                }
            }
            catch
            {
                targetIndex.Clear();
                sourceFiles = null;
                hasMissingOriginalPaths = false;
                return false;
            }
        }

        internal static bool TryLoadFilteredIntoArena(
            string idxPath,
            string expectedSignature,
            int expectedSchemaVersion,
            HashSet<(string SerializedFileName, string OriginalPath)> allowedSources,
            ProjectIndex targetIndex,
            out List<SourceFileMetadata>? sourceFiles)
        {
            ArgumentNullException.ThrowIfNull(targetIndex);
            sourceFiles = null;

            if (string.IsNullOrWhiteSpace(expectedSignature)
                || allowedSources == null
                || allowedSources.Count == 0
                || !TryOpenSnapshot(idxPath, expectedSignature, expectedSchemaVersion, out var snapshot)
                || snapshot == null)
            {
                return false;
            }

            try
            {
                using (snapshot)
                {
                    var added = snapshot.PopulateArenaFiltered(targetIndex, allowedSources, out var loadedSources);
                    if (added <= 0)
                    {
                        targetIndex.Clear();
                        sourceFiles = null;
                        return false;
                    }

                    sourceFiles = loadedSources;
                    return true;
                }
            }
            catch
            {
                targetIndex.Clear();
                sourceFiles = null;
                return false;
            }
        }

        public static unsafe bool TryOpenSnapshot(
            string idxPath,
            string expectedSignature,
            int expectedSchemaVersion,
            out MmapProjectIndexSnapshot? snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(idxPath) || !File.Exists(idxPath))
            {
                return false;
            }

            FileStream? fileStream = null;
            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? accessor = null;
            var ptrAcquired = false;

            try
            {
                fileStream = new FileStream(
                    idxPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.RandomAccess);

                var fileLengthLong = fileStream.Length;
                if (fileLengthLong < HeaderSize || fileLengthLong > int.MaxValue)
                {
                    fileStream.Dispose();
                    return false;
                }

                var fileLength = (int)fileLengthLong;
                mmf = MemoryMappedFile.CreateFromFile(
                    fileStream,
                    mapName: null,
                    capacity: fileLength,
                    MemoryMappedFileAccess.Read,
                    HandleInheritability.None,
                    leaveOpen: false);

                accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
                byte* rawPtr = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
                ptrAcquired = true;
                var basePtr = rawPtr + accessor.PointerOffset;

                var fileSpan = new ReadOnlySpan<byte>(basePtr, fileLength);
                if (!ValidateFileImage(fileSpan, expectedSignature ?? string.Empty, expectedSchemaVersion, out var header))
                {
                    ReleaseResources(ref ptrAcquired, ref accessor, ref mmf, ref fileStream);
                    return false;
                }

                snapshot = new MmapProjectIndexSnapshot(
                    fileStream,
                    mmf,
                    accessor,
                    basePtr,
                    fileLength,
                    header,
                    expectedSignature ?? string.Empty);
                return true;
            }
            catch
            {
                ReleaseResources(ref ptrAcquired, ref accessor, ref mmf, ref fileStream);
                snapshot = null;
                return false;
            }
        }

        private static void ReleaseResources(
            ref bool ptrAcquired,
            ref MemoryMappedViewAccessor? accessor,
            ref MemoryMappedFile? mmf,
            ref FileStream? fileStream)
        {
            if (ptrAcquired && accessor != null)
            {
                try
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                catch
                {
                }

                ptrAcquired = false;
            }

            try
            {
                accessor?.Dispose();
            }
            catch
            {
            }

            accessor = null;

            try
            {
                mmf?.Dispose();
            }
            catch
            {
            }

            mmf = null;

            try
            {
                fileStream?.Dispose();
            }
            catch
            {
            }

            fileStream = null;
        }

        public static bool ValidateFileImage(
            ReadOnlySpan<byte> fileSpan,
            string expectedSignature,
            int expectedSchemaVersion,
            out MmapIndexHeader header)
        {
            header = default;
            if (fileSpan.Length < HeaderSize)
            {
                return false;
            }

            header = MemoryMarshal.Read<MmapIndexHeader>(fileSpan);
            if (header.Magic != MagicValue
                || header.FormatVersion != CurrentFormatVersion
                || header.SchemaVersion != expectedSchemaVersion
                || header.TotalFileBytes != fileSpan.Length
                || header.RecordCount < 0
                || header.SourceFileCount < 0)
            {
                return false;
            }

            long expectedSourceFilesOffset = HeaderSize;
            long expectedSourceFilesLength = (long)header.SourceFileCount * SourceRecordSize;
            long expectedRecordsOffset = expectedSourceFilesOffset + expectedSourceFilesLength;
            long expectedRecordsLength = (long)header.RecordCount * AssetRecordSize;
            long expectedStringPoolOffset = expectedRecordsOffset + expectedRecordsLength;

            if (header.SourceFilesOffset != expectedSourceFilesOffset
                || header.SourceFilesLength != expectedSourceFilesLength
                || header.RecordsOffset != expectedRecordsOffset
                || header.RecordsLength != expectedRecordsLength
                || header.StringPoolOffset != expectedStringPoolOffset
                || header.StringPoolLength < 0
                || expectedStringPoolOffset + header.StringPoolLength != fileSpan.Length)
            {
                return false;
            }

            if ((uint)header.SignatureOffset > (uint)header.StringPoolLength
                || (uint)header.SignatureLength > (uint)(header.StringPoolLength - header.SignatureOffset))
            {
                return false;
            }

            var expectedSigUtf8 = Encoding.UTF8.GetBytes(expectedSignature);
            if (header.SignatureLength != expectedSigUtf8.Length
                || header.SignatureHash64 != ComputeXxHash64(expectedSigUtf8))
            {
                return false;
            }

            var storedSigSpan = fileSpan.Slice((int)header.StringPoolOffset + header.SignatureOffset, header.SignatureLength);
            if (!storedSigSpan.SequenceEqual(expectedSigUtf8))
            {
                return false;
            }

            var actualChecksum = ComputeFileChecksum(fileSpan);
            return actualChecksum == header.PayloadChecksum;
        }

        public static ulong ComputeFileChecksum(ReadOnlySpan<byte> fileSpan)
        {
            // PayloadChecksum sits at bytes 16..24 inside MmapIndexHeader.
            var seed = ComputeXxHash64(fileSpan.Slice(0, 16), 0x9E3779B97F4A7C15UL);
            return ComputeXxHash64(fileSpan.Slice(24), seed);
        }

        public static ulong ComputeXxHash64(ReadOnlySpan<byte> data, ulong seed = 0)
        {
            return XxHash64.Compute(data, seed);
        }

        internal static List<AssetHandle> NormalizeAndDeduplicateHandles(IReadOnlyCollection<AssetHandle> handles)
        {
            if (handles == null || handles.Count == 0)
            {
                return new List<AssetHandle>();
            }

            var ordered = handles
                .Where(static h => h != null)
                .OrderBy(static h => h.UniqueID ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            var writeIndex = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var currentId = current.UniqueID ?? string.Empty;
                if (i + 1 < ordered.Count && string.Equals(currentId, ordered[i + 1].UniqueID ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                ordered[writeIndex++] = current;
            }

            if (writeIndex < ordered.Count)
            {
                ordered.RemoveRange(writeIndex, ordered.Count - writeIndex);
            }

            return ordered;
        }

        private sealed class StringPoolBuilder
        {
            private readonly Dictionary<string, (int Offset, int Length)> _internMap = new(StringComparer.Ordinal);
            private byte[] _buffer;
            private int _length;

            public StringPoolBuilder(int initialCapacity)
            {
                _buffer = new byte[Math.Max(64, initialCapacity)];
            }

            public int Length => _length;
            public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

            public (int Offset, int Length) AddDeduplicated(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return (0, 0);
                }

                if (_internMap.TryGetValue(value, out var existing))
                {
                    return existing;
                }

                var entry = AddDirect(value);
                _internMap[value] = entry;
                return entry;
            }

            public (int Offset, int Length) AddDirect(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return (0, 0);
                }

                var maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
                EnsureCapacity(_length + maxByteCount);
                var bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), _buffer.AsSpan(_length));
                var offset = _length;
                _length += bytesWritten;
                return (offset, bytesWritten);
            }

            private void EnsureCapacity(int required)
            {
                if (required <= _buffer.Length)
                {
                    return;
                }

                var newCap = Math.Max(required, _buffer.Length * 2);
                Array.Resize(ref _buffer, newCap);
            }
        }
    }
}
