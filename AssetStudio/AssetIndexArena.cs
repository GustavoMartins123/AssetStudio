#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Deduplicated per-file origin metadata stored once per file in AssetIndexArena.
    /// </summary>
    public sealed class SourceOriginRecord
    {
        public static readonly SourceOriginRecord Empty = new SourceOriginRecord(
            originId: 0,
            keyOriginId: 0,
            serializedFileName: string.Empty,
            originalPath: string.Empty,
            sourceHash: string.Empty,
            unityVersion: string.Empty,
            uniqueIdFileName: "unknown",
            uniqueIdPrefix: "unknown#");

        public int OriginId { get; }
        public int KeyOriginId { get; }
        public string SerializedFileName { get; }
        public string OriginalPath { get; }
        public string SourceHash { get; }
        public string UnityVersion { get; }
        public string UniqueIdFileName { get; }
        public string UniqueIdPrefix { get; }

        public SourceOriginRecord(
            int originId,
            int keyOriginId,
            string serializedFileName,
            string originalPath,
            string sourceHash,
            string unityVersion,
            string uniqueIdFileName,
            string uniqueIdPrefix)
        {
            OriginId = originId;
            KeyOriginId = keyOriginId;
            SerializedFileName = serializedFileName ?? string.Empty;
            OriginalPath = originalPath ?? string.Empty;
            SourceHash = sourceHash ?? string.Empty;
            UnityVersion = unityVersion ?? string.Empty;
            UniqueIdFileName = string.IsNullOrWhiteSpace(uniqueIdFileName) ? "unknown" : uniqueIdFileName;
            UniqueIdPrefix = uniqueIdPrefix ?? $"{UniqueIdFileName}#{SourceHash}";
        }
    }

    /// <summary>
    /// Struct-of-Arrays (SoA) columnar arena for indexed assets with origin deduplication
    /// and a packed UTF-8 byte-slab string pool (directory prefix + leaf splitting).
    /// Eliminates per-asset managed object and string overhead in Gen2 while keeping O(1) lookups.
    /// </summary>
    public sealed class AssetIndexArena
    {
        private const int DefaultInitialCapacity = 256;

        private readonly object _syncRoot = new object();

        // Parallel primitive SoA columns
        private int[] _originIds;
        private long[] _pathIds;
        private long[] _byteStarts;
        private long[] _byteSizes;
        private int[] _classIds;
        private int[] _nameIds;
        private int[] _containerDirIds;
        private int[] _containerLeafIds;
        private int _count;
        private int _generation;

        // Interned origin tables
        private readonly List<SourceOriginRecord> _origins;
        private readonly Dictionary<string, int> _keyOriginIdByPrefix;
        private readonly Dictionary<(string SerializedFileName, string OriginalPath, string SourceHash, string UnityVersion, string UniqueIdFileName), int> _originIdByTuple;
        private SourceOriginRecord _lastOrigin = SourceOriginRecord.Empty;

        // Packed UTF-8 string pool (stores directory prefixes, container leaves, and asset names without keeping System.String objects alive)
        private readonly Utf8StringPool _stringPool;

        // Compact 12-byte key -> arenaIndex lookup
        private readonly Dictionary<AssetHandleKey, int> _indexByKey;

        // OriginId -> arenaIndices & SerializedFileName -> OriginIds for O(1) GetHandlesForFile
        private readonly List<List<int>> _indicesByOriginId;
        private readonly Dictionary<string, List<int>> _originIdsByFileName;

        // Zero-allocation primitive int ring-buffer for pending indices
        private int[] _pendingQueue;
        private int _pendingHead;
        private int _pendingTail;
        private int _pendingCount;

        // Sparse runtime attachments (null until needed; 0 bytes when unused)
        private Dictionary<int, SerializedFile>? _sourceFilesByIndex;
        private Dictionary<int, Object>? _realObjectsByIndex;
        private Dictionary<int, object>? _tagsByIndex;
        private Dictionary<int, string>? _customUniqueIdsByIndex;
        private Dictionary<string, int>? _indexByCustomUniqueId;

        public AssetIndexArena(int initialCapacity = DefaultInitialCapacity)
        {
            int cap = Math.Max(16, initialCapacity);
            _originIds = new int[cap];
            _pathIds = new long[cap];
            _byteStarts = new long[cap];
            _byteSizes = new long[cap];
            _classIds = new int[cap];
            _nameIds = new int[cap];
            _containerDirIds = new int[cap];
            _containerLeafIds = new int[cap];

            _origins = new List<SourceOriginRecord>(32) { SourceOriginRecord.Empty };
            _keyOriginIdByPrefix = new Dictionary<string, int>(32, StringComparer.Ordinal);
            _originIdByTuple = new Dictionary<(string, string, string, string, string), int>(32);

            _stringPool = new Utf8StringPool(initialStringCapacity: Math.Max(64, cap), initialByteCapacity: Math.Max(1024, cap * 24));
            _indexByKey = new Dictionary<AssetHandleKey, int>(cap);
            _indicesByOriginId = new List<List<int>>(32) { new List<int>(0) };
            _originIdsByFileName = new Dictionary<string, List<int>>(32, StringComparer.Ordinal);

            _pendingQueue = new int[cap];
        }

        public int Count
        {
            get
            {
                lock (_syncRoot)
                {
                    return _count;
                }
            }
        }

        public int OriginCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _origins.Count - 1;
                }
            }
        }

        public int InternedStringCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _stringPool.Count;
                }
            }
        }

        internal int Generation
        {
            get
            {
                lock (_syncRoot)
                {
                    return _generation;
                }
            }
        }

        public int AddHandle(AssetHandle handle)
        {
            if (handle == null || !handle.HasUniqueId)
            {
                return -1;
            }

            lock (_syncRoot)
            {
                if (handle.IsBoundTo(this, _generation, out int boundIndex))
                {
                    EnqueuePendingUnlocked(boundIndex);
                    return boundIndex;
                }

                string serializedFileName = handle.SerializedFileName;
                string originalPath = handle.OriginalPath;
                string unityVersion = handle.UnityVersion;
                long pathId = handle.PathID;
                long byteStart = handle.ByteStart;
                long byteSize = handle.ByteSize;
                ClassIDType type = handle.Type;
                string name = handle.Name;
                string container = handle.Container;
                SerializedFile? sourceFile = handle.SourceFile;
                Object? realObject = handle.RealObject;
                object? tag = handle.Tag;

                int index;
                if (handle.TryGetCanonicalIdentity(out AssetHandleOrigin handleOrigin, out long uniqueIdPathId))
                {
                    SourceOriginRecord origin = GetOrAddOriginUnlocked(
                        serializedFileName,
                        originalPath,
                        handleOrigin.SourceHash,
                        unityVersion,
                        handleOrigin.UniqueIdFileName,
                        handleOrigin.UniqueIdPrefix);

                    string? customUid = uniqueIdPathId != pathId ? handle.UniqueID : null;
                    var key = new AssetHandleKey(origin.KeyOriginId, uniqueIdPathId);
                    index = UpsertRecordUnlocked(
                        key,
                        origin,
                        pathId,
                        byteStart,
                        byteSize,
                        type,
                        name.AsSpan(),
                        container.AsSpan(),
                        sourceFile,
                        realObject,
                        tag,
                        customUid);
                }
                else
                {
                    string rawUniqueId = handle.UniqueID;
                    if (string.IsNullOrEmpty(rawUniqueId))
                    {
                        return -1;
                    }

                    index = UpsertCustomOrParsedUniqueIdUnlocked(
                        rawUniqueId,
                        serializedFileName,
                        originalPath,
                        unityVersion,
                        pathId,
                        byteStart,
                        byteSize,
                        type,
                        name.AsSpan(),
                        container.AsSpan(),
                        sourceFile,
                        realObject,
                        tag);
                }

                handle.BindToArena(this, index, _generation);
                return index;
            }
        }

        public int AddRecord(
            string? serializedFileName,
            string? originalPath,
            long pathId,
            string? name,
            ClassIDType type,
            string? container = null,
            long byteStart = 0,
            long byteSize = 0,
            string? sourceHash = null,
            string? unityVersion = null,
            SerializedFile? sourceFile = null,
            Object? realObject = null,
            object? tag = null,
            string? customUniqueId = null)
        {
            string normFile = serializedFileName ?? string.Empty;
            string normPath = originalPath ?? string.Empty;
            string normVersion = unityVersion ?? string.Empty;
            string normName = name ?? string.Empty;
            string normContainer = container ?? string.Empty;

            lock (_syncRoot)
            {
                if (!string.IsNullOrEmpty(customUniqueId))
                {
                    return UpsertCustomOrParsedUniqueIdUnlocked(
                        customUniqueId,
                        normFile,
                        normPath,
                        normVersion,
                        pathId,
                        byteStart,
                        byteSize,
                        type,
                        normName.AsSpan(),
                        normContainer.AsSpan(),
                        sourceFile,
                        realObject,
                        tag);
                }

                string effectiveHash = string.IsNullOrEmpty(sourceHash)
                    ? AssetHandle.BuildSourceHash(normFile, normPath)
                    : sourceHash;
                string uniqueIdFileName = string.IsNullOrWhiteSpace(normFile) ? "unknown" : normFile;

                SourceOriginRecord origin = GetOrAddOriginUnlocked(
                    normFile,
                    normPath,
                    effectiveHash,
                    normVersion,
                    uniqueIdFileName,
                    uniqueIdPrefix: null);

                var key = new AssetHandleKey(origin.KeyOriginId, pathId);
                return UpsertRecordUnlocked(
                    key,
                    origin,
                    pathId,
                    byteStart,
                    byteSize,
                    type,
                    normName.AsSpan(),
                    normContainer.AsSpan(),
                    sourceFile,
                    realObject,
                    tag,
                    customUniqueId: null);
            }
        }

        public bool TryGetIndex(AssetHandleKey key, out int index)
        {
            lock (_syncRoot)
            {
                return _indexByKey.TryGetValue(key, out index);
            }
        }

        public bool TryGetIndexByUniqueId(string? uniqueId, out int index)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                index = -1;
                return false;
            }

            return TryGetIndexByUniqueId(uniqueId.AsSpan(), out index);
        }

        public bool TryGetIndexByUniqueId(ReadOnlySpan<char> uniqueId, out int index)
        {
            if (uniqueId.IsEmpty)
            {
                index = -1;
                return false;
            }

            lock (_syncRoot)
            {
                if (AssetHandleKey.TryParseUniqueIdWithPrefix(
                    uniqueId,
                    out ReadOnlySpan<char> originPrefix,
                    out _,
                    out _,
                    out long parsedPathId,
                    out bool isCanonicalPathId) && isCanonicalPathId)
                {
                    var prefixLookup = _keyOriginIdByPrefix.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (prefixLookup.TryGetValue(originPrefix, out int keyOriginId))
                    {
                        var key = new AssetHandleKey(keyOriginId, parsedPathId);
                        if (_indexByKey.TryGetValue(key, out index))
                        {
                            return true;
                        }
                    }
                }

                if (_indexByCustomUniqueId != null && _indexByCustomUniqueId.Count > 0)
                {
                    var customLookup = _indexByCustomUniqueId.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (customLookup.TryGetValue(uniqueId, out index))
                    {
                        return true;
                    }
                }

                index = -1;
                return false;
            }
        }

        public AssetHandleKey GetKey(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                SourceOriginRecord origin = _origins[_originIds[index]];
                return new AssetHandleKey(origin.KeyOriginId, _pathIds[index]);
            }
        }

        public ClassIDType GetType(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return (ClassIDType)_classIds[index];
            }
        }

        public long GetPathId(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _pathIds[index];
            }
        }

        public long GetByteStart(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _byteStarts[index];
            }
        }

        public long GetByteSize(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _byteSizes[index];
            }
        }

        public string GetName(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _stringPool.GetString(_nameIds[index]);
            }
        }

        public string GetContainer(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _stringPool.GetConcatenatedString(_containerDirIds[index], _containerLeafIds[index]);
            }
        }

        public string GetSerializedFileName(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _origins[_originIds[index]].SerializedFileName;
            }
        }

        public string GetOriginalPath(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _origins[_originIds[index]].OriginalPath;
            }
        }

        public string GetSourceHash(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _origins[_originIds[index]].SourceHash;
            }
        }

        public string GetUnityVersion(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _origins[_originIds[index]].UnityVersion;
            }
        }

        public SourceOriginRecord GetOrigin(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return _origins[_originIds[index]];
            }
        }

        public SerializedFile? GetSourceFile(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                if (_sourceFilesByIndex != null && _sourceFilesByIndex.TryGetValue(index, out SerializedFile? file))
                {
                    return file;
                }
                return null;
            }
        }

        public Object? GetRealObject(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                if (_realObjectsByIndex != null && _realObjectsByIndex.TryGetValue(index, out Object? obj))
                {
                    return obj;
                }
                return null;
            }
        }

        public object? GetTag(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                if (_tagsByIndex != null && _tagsByIndex.TryGetValue(index, out object? tag))
                {
                    return tag;
                }
                return null;
            }
        }

        public string GetUniqueId(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                if (_customUniqueIdsByIndex != null && _customUniqueIdsByIndex.TryGetValue(index, out string? customUid))
                {
                    return customUid;
                }

                SourceOriginRecord origin = _origins[_originIds[index]];
                return string.Concat(origin.UniqueIdPrefix, "#", _pathIds[index].ToString(CultureInfo.InvariantCulture));
            }
        }

        public AssetHandle MaterializeHandle(int index)
        {
            lock (_syncRoot)
            {
                ValidateIndexUnlocked(index);
                return new AssetHandle(this, index, _generation);
            }
        }

        internal bool TryMaterializeHandle(int index, out AssetHandle? handle)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count)
                {
                    handle = null;
                    return false;
                }

                handle = new AssetHandle(this, index, _generation);
                return true;
            }
        }

        public int[] GetIndicesForFile(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return Array.Empty<int>();
            }

            lock (_syncRoot)
            {
                if (!_originIdsByFileName.TryGetValue(fileName, out List<int>? originIds) || originIds.Count == 0)
                {
                    return Array.Empty<int>();
                }

                if (originIds.Count == 1)
                {
                    List<int> list = _indicesByOriginId[originIds[0]];
                    return list.Count == 0 ? Array.Empty<int>() : list.ToArray();
                }

                int total = 0;
                for (int i = 0; i < originIds.Count; i++)
                {
                    total += _indicesByOriginId[originIds[i]].Count;
                }

                if (total == 0)
                {
                    return Array.Empty<int>();
                }

                int[] result = new int[total];
                int offset = 0;
                for (int i = 0; i < originIds.Count; i++)
                {
                    List<int> list = _indicesByOriginId[originIds[i]];
                    list.CopyTo(result, offset);
                    offset += list.Count;
                }

                return result;
            }
        }

        public int[] DrainPendingIndices(int maxCount = int.MaxValue)
        {
            if (maxCount <= 0)
            {
                return Array.Empty<int>();
            }

            lock (_syncRoot)
            {
                int toDrain = Math.Min(maxCount, _pendingCount);
                if (toDrain <= 0)
                {
                    return Array.Empty<int>();
                }

                int[] result = new int[toDrain];
                for (int i = 0; i < toDrain; i++)
                {
                    result[i] = DequeuePendingUnlocked();
                }

                return result;
            }
        }

        public bool TryDequeuePendingIndex(out int index)
        {
            lock (_syncRoot)
            {
                if (_pendingCount <= 0)
                {
                    index = -1;
                    return false;
                }

                index = DequeuePendingUnlocked();
                return true;
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _count = 0;
                _generation++;

                _origins.Clear();
                _origins.Add(SourceOriginRecord.Empty);
                _keyOriginIdByPrefix.Clear();
                _originIdByTuple.Clear();
                _lastOrigin = SourceOriginRecord.Empty;

                _stringPool.Clear();
                _indexByKey.Clear();

                _indicesByOriginId.Clear();
                _indicesByOriginId.Add(new List<int>(0));
                _originIdsByFileName.Clear();

                _pendingHead = 0;
                _pendingTail = 0;
                _pendingCount = 0;

                _sourceFilesByIndex = null;
                _realObjectsByIndex = null;
                _tagsByIndex = null;
                _customUniqueIdsByIndex = null;
                _indexByCustomUniqueId = null;
            }
        }

        public long EstimateMemoryBytes()
        {
            lock (_syncRoot)
            {
                long bytes = 0;
                bytes += (long)_originIds.Length * sizeof(int);
                bytes += (long)_pathIds.Length * sizeof(long);
                bytes += (long)_byteStarts.Length * sizeof(long);
                bytes += (long)_byteSizes.Length * sizeof(long);
                bytes += (long)_classIds.Length * sizeof(int);
                bytes += (long)_nameIds.Length * sizeof(int);
                bytes += (long)_containerDirIds.Length * sizeof(int);
                bytes += (long)_containerLeafIds.Length * sizeof(int);

                bytes += _stringPool.EstimateMemoryBytes();
                bytes += (long)_indexByKey.Count * 24;
                bytes += (long)_pendingQueue.Length * sizeof(int);

                for (int i = 0; i < _indicesByOriginId.Count; i++)
                {
                    bytes += (long)_indicesByOriginId[i].Capacity * sizeof(int);
                }

                for (int i = 1; i < _origins.Count; i++)
                {
                    SourceOriginRecord o = _origins[i];
                    bytes += 64 + (o.SerializedFileName.Length + o.OriginalPath.Length + o.SourceHash.Length + o.UniqueIdPrefix.Length) * sizeof(char);
                }

                return bytes;
            }
        }

        // Internal write-through mutators called by Arena-bound AssetHandle instances

        internal void SetName(int index, string? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                _nameIds[index] = _stringPool.GetOrAdd((value ?? string.Empty).AsSpan());
            }
        }

        internal void SetContainer(int index, string? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                SplitAndInternContainerUnlocked((value ?? string.Empty).AsSpan(), out int dirId, out int leafId);
                _containerDirIds[index] = dirId;
                _containerLeafIds[index] = leafId;
            }
        }

        internal void SetType(int index, ClassIDType value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                _classIds[index] = (int)value;
            }
        }

        internal void SetPathId(int index, long value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                long oldPathId = _pathIds[index];
                if (oldPathId == value) return;

                SourceOriginRecord origin = _origins[_originIds[index]];
                if (_customUniqueIdsByIndex == null || !_customUniqueIdsByIndex.ContainsKey(index))
                {
                    var oldKey = new AssetHandleKey(origin.KeyOriginId, oldPathId);
                    if (_indexByKey.TryGetValue(oldKey, out int existing) && existing == index)
                    {
                        _indexByKey.Remove(oldKey);
                    }
                    _indexByKey[new AssetHandleKey(origin.KeyOriginId, value)] = index;
                }

                _pathIds[index] = value;
            }
        }

        internal void SetByteStart(int index, long value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                _byteStarts[index] = value;
            }
        }

        internal void SetByteSize(int index, long value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                _byteSizes[index] = value;
            }
        }

        internal void SetOriginalPath(int index, string? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                string newPath = value ?? string.Empty;
                SourceOriginRecord current = _origins[_originIds[index]];
                if (string.Equals(current.OriginalPath, newPath, StringComparison.Ordinal))
                {
                    return;
                }

                SourceOriginRecord updated = GetOrAddOriginUnlocked(
                    current.SerializedFileName,
                    newPath,
                    current.SourceHash,
                    current.UnityVersion,
                    current.UniqueIdFileName,
                    current.UniqueIdPrefix);

                MoveIndexOriginUnlocked(index, current.OriginId, updated.OriginId);
            }
        }

        internal void SetSerializedFileName(int index, string? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                string newFile = value ?? string.Empty;
                SourceOriginRecord current = _origins[_originIds[index]];
                if (string.Equals(current.SerializedFileName, newFile, StringComparison.Ordinal))
                {
                    return;
                }

                SourceOriginRecord updated = GetOrAddOriginUnlocked(
                    newFile,
                    current.OriginalPath,
                    current.SourceHash,
                    current.UnityVersion,
                    current.UniqueIdFileName,
                    current.UniqueIdPrefix);

                MoveIndexOriginUnlocked(index, current.OriginId, updated.OriginId);
            }
        }

        internal void SetUnityVersion(int index, string? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                string newVer = value ?? string.Empty;
                SourceOriginRecord current = _origins[_originIds[index]];
                if (string.Equals(current.UnityVersion, newVer, StringComparison.Ordinal))
                {
                    return;
                }

                SourceOriginRecord updated = GetOrAddOriginUnlocked(
                    current.SerializedFileName,
                    current.OriginalPath,
                    current.SourceHash,
                    newVer,
                    current.UniqueIdFileName,
                    current.UniqueIdPrefix);

                MoveIndexOriginUnlocked(index, current.OriginId, updated.OriginId);
            }
        }

        internal void SetUniqueId(int index, string? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                string newUid = value ?? string.Empty;
                if (_customUniqueIdsByIndex != null && _customUniqueIdsByIndex.TryGetValue(index, out string? oldCustom))
                {
                    _indexByCustomUniqueId?.Remove(oldCustom);
                    _customUniqueIdsByIndex.Remove(index);
                }

                SourceOriginRecord current = _origins[_originIds[index]];
                var oldKey = new AssetHandleKey(current.KeyOriginId, _pathIds[index]);
                if (_indexByKey.TryGetValue(oldKey, out int existing) && existing == index)
                {
                    _indexByKey.Remove(oldKey);
                }

                if (AssetHandleKey.TryParseUniqueIdWithPrefix(
                    newUid.AsSpan(),
                    out _,
                    out ReadOnlySpan<char> fileNameSpan,
                    out ReadOnlySpan<char> hashSpan,
                    out long parsedPathId,
                    out bool isCanonicalPathId) && isCanonicalPathId)
                {
                    string fn = fileNameSpan.ToString();
                    string sh = hashSpan.ToString();
                    SourceOriginRecord updated = GetOrAddOriginUnlocked(
                        current.SerializedFileName,
                        current.OriginalPath,
                        sh,
                        current.UnityVersion,
                        fn,
                        uniqueIdPrefix: null);

                    MoveIndexOriginUnlocked(index, current.OriginId, updated.OriginId);
                    _indexByKey[new AssetHandleKey(updated.KeyOriginId, parsedPathId)] = index;
                    if (parsedPathId != _pathIds[index])
                    {
                        (_customUniqueIdsByIndex ??= new Dictionary<int, string>())[index] = newUid;
                        (_indexByCustomUniqueId ??= new Dictionary<string, int>(StringComparer.Ordinal))[newUid] = index;
                    }
                }
                else
                {
                    (_customUniqueIdsByIndex ??= new Dictionary<int, string>())[index] = newUid;
                    (_indexByCustomUniqueId ??= new Dictionary<string, int>(StringComparer.Ordinal))[newUid] = index;
                    int syntheticKeyOriginId = GetOrAddKeyOriginIdUnlocked(newUid);
                    _indexByKey[new AssetHandleKey(syntheticKeyOriginId, _pathIds[index])] = index;
                }
            }
        }

        internal void SetSourceFile(int index, SerializedFile? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                if (value != null)
                {
                    (_sourceFilesByIndex ??= new Dictionary<int, SerializedFile>())[index] = value;
                }
                else if (_sourceFilesByIndex != null)
                {
                    _sourceFilesByIndex.Remove(index);
                    if (_sourceFilesByIndex.Count == 0)
                    {
                        _sourceFilesByIndex = null;
                    }
                }
            }
        }

        internal void SetRealObject(int index, Object? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                if (value != null)
                {
                    (_realObjectsByIndex ??= new Dictionary<int, Object>())[index] = value;
                }
                else if (_realObjectsByIndex != null)
                {
                    _realObjectsByIndex.Remove(index);
                    if (_realObjectsByIndex.Count == 0)
                    {
                        _realObjectsByIndex = null;
                    }
                }
            }
        }

        internal void SetTag(int index, object? value)
        {
            lock (_syncRoot)
            {
                if ((uint)index >= (uint)_count) return;
                if (value != null)
                {
                    (_tagsByIndex ??= new Dictionary<int, object>())[index] = value;
                }
                else if (_tagsByIndex != null)
                {
                    _tagsByIndex.Remove(index);
                    if (_tagsByIndex.Count == 0)
                    {
                        _tagsByIndex = null;
                    }
                }
            }
        }

        // Private helpers (caller holds _syncRoot)

        private int UpsertCustomOrParsedUniqueIdUnlocked(
            string rawUniqueId,
            string serializedFileName,
            string originalPath,
            string unityVersion,
            long pathId,
            long byteStart,
            long byteSize,
            ClassIDType type,
            ReadOnlySpan<char> name,
            ReadOnlySpan<char> container,
            SerializedFile? sourceFile,
            Object? realObject,
            object? tag)
        {
            if (AssetHandleKey.TryParseUniqueIdWithPrefix(
                rawUniqueId.AsSpan(),
                out _,
                out ReadOnlySpan<char> fileNameSpan,
                out ReadOnlySpan<char> sourceHashSpan,
                out long parsedPathId,
                out bool isCanonicalPathId) && isCanonicalPathId)
            {
                string fn = fileNameSpan.SequenceEqual(serializedFileName.AsSpan())
                    ? serializedFileName
                    : fileNameSpan.ToString();
                string sh = sourceHashSpan.ToString();

                SourceOriginRecord origin = GetOrAddOriginUnlocked(
                    serializedFileName,
                    originalPath,
                    sh,
                    unityVersion,
                    fn,
                    uniqueIdPrefix: null);

                var key = new AssetHandleKey(origin.KeyOriginId, parsedPathId);
                string? customUid = parsedPathId != pathId ? rawUniqueId : null;
                return UpsertRecordUnlocked(
                    key,
                    origin,
                    pathId,
                    byteStart,
                    byteSize,
                    type,
                    name,
                    container,
                    sourceFile,
                    realObject,
                    tag,
                    customUid);
            }
            else
            {
                string uniqueIdFileName = string.IsNullOrWhiteSpace(serializedFileName) ? rawUniqueId : serializedFileName;
                SourceOriginRecord origin = GetOrAddOriginUnlocked(
                    serializedFileName,
                    originalPath,
                    sourceHash: string.Empty,
                    unityVersion,
                    uniqueIdFileName,
                    uniqueIdPrefix: rawUniqueId);

                var key = new AssetHandleKey(origin.KeyOriginId, pathId);
                return UpsertRecordUnlocked(
                    key,
                    origin,
                    pathId,
                    byteStart,
                    byteSize,
                    type,
                    name,
                    container,
                    sourceFile,
                    realObject,
                    tag,
                    customUniqueId: rawUniqueId);
            }
        }

        private int UpsertRecordUnlocked(
            AssetHandleKey key,
            SourceOriginRecord origin,
            long pathId,
            long byteStart,
            long byteSize,
            ClassIDType type,
            ReadOnlySpan<char> name,
            ReadOnlySpan<char> container,
            SerializedFile? sourceFile,
            Object? realObject,
            object? tag,
            string? customUniqueId)
        {
            int nameId = _stringPool.GetOrAdd(name);
            SplitAndInternContainerUnlocked(container, out int dirId, out int leafId);

            int index;
            bool isExisting = false;
            if (!string.IsNullOrEmpty(customUniqueId)
                && _indexByCustomUniqueId != null
                && _indexByCustomUniqueId.TryGetValue(customUniqueId, out index))
            {
                isExisting = true;
                SourceOriginRecord oldOrigin = _origins[_originIds[index]];
                var oldKey = new AssetHandleKey(oldOrigin.KeyOriginId, _pathIds[index]);
                if (oldKey != key && _indexByKey.TryGetValue(oldKey, out int mappedIdx) && mappedIdx == index)
                {
                    _indexByKey.Remove(oldKey);
                }
                _indexByKey[key] = index;
            }
            else if (_indexByKey.TryGetValue(key, out index))
            {
                isExisting = true;
            }

            if (isExisting)
            {
                int oldOriginId = _originIds[index];
                if (oldOriginId != origin.OriginId)
                {
                    MoveIndexOriginUnlocked(index, oldOriginId, origin.OriginId);
                }
            }
            else
            {
                index = _count++;
                EnsureRowCapacityUnlocked(_count);
                _indexByKey[key] = index;
                _originIds[index] = origin.OriginId;
                _indicesByOriginId[origin.OriginId].Add(index);
            }

            _pathIds[index] = pathId;
            _byteStarts[index] = byteStart;
            _byteSizes[index] = byteSize;
            _classIds[index] = (int)type;
            _nameIds[index] = nameId;
            _containerDirIds[index] = dirId;
            _containerLeafIds[index] = leafId;

            if (!string.IsNullOrEmpty(customUniqueId))
            {
                (_customUniqueIdsByIndex ??= new Dictionary<int, string>())[index] = customUniqueId;
                (_indexByCustomUniqueId ??= new Dictionary<string, int>(StringComparer.Ordinal))[customUniqueId] = index;
            }
            else if (isExisting && _customUniqueIdsByIndex != null && _customUniqueIdsByIndex.TryGetValue(index, out string? oldCustom))
            {
                _customUniqueIdsByIndex.Remove(index);
                _indexByCustomUniqueId?.Remove(oldCustom);
            }

            if (sourceFile != null)
            {
                (_sourceFilesByIndex ??= new Dictionary<int, SerializedFile>())[index] = sourceFile;
            }
            else if (isExisting && _sourceFilesByIndex != null)
            {
                _sourceFilesByIndex.Remove(index);
            }

            if (realObject != null)
            {
                (_realObjectsByIndex ??= new Dictionary<int, Object>())[index] = realObject;
            }
            else if (isExisting && _realObjectsByIndex != null)
            {
                _realObjectsByIndex.Remove(index);
            }

            if (tag != null)
            {
                (_tagsByIndex ??= new Dictionary<int, object>())[index] = tag;
            }
            else if (isExisting && _tagsByIndex != null)
            {
                _tagsByIndex.Remove(index);
            }

            EnqueuePendingUnlocked(index);
            return index;
        }

        private void MoveIndexOriginUnlocked(int index, int oldOriginId, int newOriginId)
        {
            if (oldOriginId == newOriginId)
            {
                return;
            }

            if (oldOriginId > 0 && oldOriginId < _indicesByOriginId.Count)
            {
                _indicesByOriginId[oldOriginId].Remove(index);
            }

            _originIds[index] = newOriginId;
            _indicesByOriginId[newOriginId].Add(index);
        }

        private SourceOriginRecord GetOrAddOriginUnlocked(
            string serializedFileName,
            string originalPath,
            string sourceHash,
            string unityVersion,
            string uniqueIdFileName,
            string? uniqueIdPrefix)
        {
            string normFile = serializedFileName ?? string.Empty;
            string normPath = originalPath ?? string.Empty;
            string normHash = sourceHash ?? string.Empty;
            string normVer = unityVersion ?? string.Empty;
            string normUidFile = string.IsNullOrWhiteSpace(uniqueIdFileName) ? "unknown" : uniqueIdFileName;

            SourceOriginRecord last = _lastOrigin;
            if (last.OriginId > 0
                && string.Equals(last.SerializedFileName, normFile, StringComparison.Ordinal)
                && string.Equals(last.OriginalPath, normPath, StringComparison.Ordinal)
                && string.Equals(last.SourceHash, normHash, StringComparison.Ordinal)
                && string.Equals(last.UnityVersion, normVer, StringComparison.Ordinal)
                && string.Equals(last.UniqueIdFileName, normUidFile, StringComparison.Ordinal))
            {
                return last;
            }

            var tupleKey = (normFile, normPath, normHash, normVer, normUidFile);
            if (_originIdByTuple.TryGetValue(tupleKey, out int existingOriginId))
            {
                SourceOriginRecord found = _origins[existingOriginId];
                _lastOrigin = found;
                return found;
            }

            string prefix = uniqueIdPrefix ?? string.Concat(normUidFile, "#", normHash);
            int keyOriginId = GetOrAddKeyOriginIdUnlocked(prefix);
            int newOriginId = _origins.Count;

            var record = new SourceOriginRecord(
                newOriginId,
                keyOriginId,
                normFile,
                normPath,
                normHash,
                normVer,
                normUidFile,
                prefix);

            _origins.Add(record);
            _originIdByTuple[tupleKey] = newOriginId;
            _indicesByOriginId.Add(new List<int>(64));

            if (!string.IsNullOrEmpty(normFile))
            {
                if (!_originIdsByFileName.TryGetValue(normFile, out List<int>? fileOriginIds))
                {
                    fileOriginIds = new List<int>(2);
                    _originIdsByFileName[normFile] = fileOriginIds;
                }
                fileOriginIds.Add(newOriginId);
            }

            _lastOrigin = record;
            return record;
        }

        private int GetOrAddKeyOriginIdUnlocked(string prefix)
        {
            if (!_keyOriginIdByPrefix.TryGetValue(prefix, out int keyOriginId))
            {
                keyOriginId = _keyOriginIdByPrefix.Count + 1;
                _keyOriginIdByPrefix[prefix] = keyOriginId;
            }

            return keyOriginId;
        }

        private void SplitAndInternContainerUnlocked(ReadOnlySpan<char> container, out int dirId, out int leafId)
        {
            if (container.IsEmpty)
            {
                dirId = 0;
                leafId = 0;
                return;
            }

            int lastSlash = container.LastIndexOfAny('/', '\\');
            if (lastSlash < 0)
            {
                dirId = 0;
                leafId = _stringPool.GetOrAdd(container);
                return;
            }

            dirId = _stringPool.GetOrAdd(container.Slice(0, lastSlash + 1));
            leafId = _stringPool.GetOrAdd(container.Slice(lastSlash + 1));
        }

        private void EnsureRowCapacityUnlocked(int required)
        {
            if (required <= _originIds.Length)
            {
                return;
            }

            int newCap = _originIds.Length * 2;
            while (newCap < required)
            {
                newCap *= 2;
            }

            Array.Resize(ref _originIds, newCap);
            Array.Resize(ref _pathIds, newCap);
            Array.Resize(ref _byteStarts, newCap);
            Array.Resize(ref _byteSizes, newCap);
            Array.Resize(ref _classIds, newCap);
            Array.Resize(ref _nameIds, newCap);
            Array.Resize(ref _containerDirIds, newCap);
            Array.Resize(ref _containerLeafIds, newCap);
        }

        private void EnqueuePendingUnlocked(int index)
        {
            if (_pendingCount == _pendingQueue.Length)
            {
                int newCap = _pendingQueue.Length * 2;
                int[] next = new int[newCap];
                if (_pendingHead < _pendingTail)
                {
                    Array.Copy(_pendingQueue, _pendingHead, next, 0, _pendingCount);
                }
                else
                {
                    int firstPart = _pendingQueue.Length - _pendingHead;
                    Array.Copy(_pendingQueue, _pendingHead, next, 0, firstPart);
                    Array.Copy(_pendingQueue, 0, next, firstPart, _pendingTail);
                }

                _pendingQueue = next;
                _pendingHead = 0;
                _pendingTail = _pendingCount;
            }

            _pendingQueue[_pendingTail] = index;
            _pendingTail = (_pendingTail + 1) % _pendingQueue.Length;
            _pendingCount++;
        }

        private int DequeuePendingUnlocked()
        {
            int value = _pendingQueue[_pendingHead];
            _pendingHead = (_pendingHead + 1) % _pendingQueue.Length;
            _pendingCount--;
            return value;
        }

        private void ValidateIndexUnlocked(int index)
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_count}).");
            }
        }

        /// <summary>
        /// Packed UTF-8 byte-slab string pool with open-chained hash table.
        /// Deduplicates strings via ReadOnlySpan&lt;char&gt; without retaining managed System.String objects on the GC heap.
        /// </summary>
        private sealed class Utf8StringPool
        {
            private byte[] _bytes;
            private int _byteCount;

            private int[] _offsets;
            private int[] _lengths;
            private int[] _hashes;
            private int[] _next;
            private int[] _buckets;
            private int _count;

            public int Count => _count;

            public Utf8StringPool(int initialStringCapacity, int initialByteCapacity)
            {
                int strCap = NextPowerOfTwo(Math.Max(64, initialStringCapacity));
                _bytes = new byte[Math.Max(1024, initialByteCapacity)];
                _offsets = new int[strCap];
                _lengths = new int[strCap];
                _hashes = new int[strCap];
                _next = new int[strCap];
                _buckets = new int[strCap];
                Array.Fill(_buckets, -1);

                // ID 0 is always string.Empty
                _offsets[0] = 0;
                _lengths[0] = 0;
                _hashes[0] = 0;
                _next[0] = -1;
                _count = 1;
            }

            public int GetOrAdd(ReadOnlySpan<char> chars)
            {
                if (chars.IsEmpty)
                {
                    return 0;
                }

                int maxByteCount = Encoding.UTF8.GetMaxByteCount(chars.Length);
                byte[]? rented = null;
                Span<byte> utf8Buf = maxByteCount <= 512
                    ? stackalloc byte[512]
                    : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

                try
                {
                    int byteLen = Encoding.UTF8.GetBytes(chars, utf8Buf);
                    ReadOnlySpan<byte> utf8Span = utf8Buf.Slice(0, byteLen);
                    int hash = ComputeUtf8Hash(utf8Span);
                    int bucketMask = _buckets.Length - 1;
                    int bucket = hash & bucketMask;

                    for (int id = _buckets[bucket]; id != -1; id = _next[id])
                    {
                        if (_hashes[id] == hash
                            && _lengths[id] == byteLen
                            && _bytes.AsSpan(_offsets[id], byteLen).SequenceEqual(utf8Span))
                        {
                            return id;
                        }
                    }

                    if (_count + 1 > (_buckets.Length * 3) >> 2)
                    {
                        Rehash(_buckets.Length * 2);
                        bucket = hash & (_buckets.Length - 1);
                    }

                    int newId = _count++;
                    EnsureStringCapacity(newId + 1);
                    EnsureByteCapacity(_byteCount + byteLen);

                    utf8Span.CopyTo(_bytes.AsSpan(_byteCount, byteLen));
                    _offsets[newId] = _byteCount;
                    _lengths[newId] = byteLen;
                    _hashes[newId] = hash;
                    _byteCount += byteLen;

                    _next[newId] = _buckets[bucket];
                    _buckets[bucket] = newId;
                    return newId;
                }
                finally
                {
                    if (rented != null)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            public string GetString(int id)
            {
                if (id <= 0 || id >= _count)
                {
                    return string.Empty;
                }

                int len = _lengths[id];
                if (len <= 0)
                {
                    return string.Empty;
                }

                return Encoding.UTF8.GetString(_bytes, _offsets[id], len);
            }

            public string GetConcatenatedString(int prefixId, int leafId)
            {
                if (prefixId <= 0)
                {
                    return GetString(leafId);
                }

                if (leafId <= 0)
                {
                    return GetString(prefixId);
                }

                ReadOnlySpan<byte> prefixBytes = _bytes.AsSpan(_offsets[prefixId], _lengths[prefixId]);
                ReadOnlySpan<byte> leafBytes = _bytes.AsSpan(_offsets[leafId], _lengths[leafId]);

                int maxChars = prefixBytes.Length + leafBytes.Length;
                char[]? rented = null;
                Span<char> charBuf = maxChars <= 512
                    ? stackalloc char[512]
                    : (rented = ArrayPool<char>.Shared.Rent(maxChars));

                try
                {
                    int writtenPrefix = Encoding.UTF8.GetChars(prefixBytes, charBuf);
                    int writtenLeaf = Encoding.UTF8.GetChars(leafBytes, charBuf.Slice(writtenPrefix));
                    return new string(charBuf.Slice(0, writtenPrefix + writtenLeaf));
                }
                finally
                {
                    if (rented != null)
                    {
                        ArrayPool<char>.Shared.Return(rented);
                    }
                }
            }

            public void Clear()
            {
                _byteCount = 0;
                Array.Fill(_buckets, -1);
                _offsets[0] = 0;
                _lengths[0] = 0;
                _hashes[0] = 0;
                _next[0] = -1;
                _count = 1;
            }

            public long EstimateMemoryBytes()
            {
                long total = _bytes.Length;
                total += (long)_offsets.Length * sizeof(int);
                total += (long)_lengths.Length * sizeof(int);
                total += (long)_hashes.Length * sizeof(int);
                total += (long)_next.Length * sizeof(int);
                total += (long)_buckets.Length * sizeof(int);
                return total;
            }

            private void EnsureStringCapacity(int required)
            {
                if (required <= _offsets.Length)
                {
                    return;
                }

                int newCap = _offsets.Length * 2;
                while (newCap < required)
                {
                    newCap *= 2;
                }

                Array.Resize(ref _offsets, newCap);
                Array.Resize(ref _lengths, newCap);
                Array.Resize(ref _hashes, newCap);
                Array.Resize(ref _next, newCap);
            }

            private void EnsureByteCapacity(int required)
            {
                if (required <= _bytes.Length)
                {
                    return;
                }

                int newCap = _bytes.Length * 2;
                while (newCap < required)
                {
                    newCap *= 2;
                }

                Array.Resize(ref _bytes, newCap);
            }

            private void Rehash(int newBucketCount)
            {
                int[] newBuckets = new int[newBucketCount];
                Array.Fill(newBuckets, -1);
                int mask = newBucketCount - 1;

                for (int id = 1; id < _count; id++)
                {
                    int bucket = _hashes[id] & mask;
                    _next[id] = newBuckets[bucket];
                    newBuckets[bucket] = id;
                }

                _buckets = newBuckets;
            }

            private static int ComputeUtf8Hash(ReadOnlySpan<byte> bytes)
            {
                unchecked
                {
                    uint hash = 2166136261u;
                    ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes);
                    for (int i = 0; i < words.Length; i++)
                    {
                        hash = (hash ^ words[i]) * 16777619u;
                    }

                    int tailOffset = words.Length * sizeof(uint);
                    for (int i = tailOffset; i < bytes.Length; i++)
                    {
                        hash = (hash ^ bytes[i]) * 16777619u;
                    }

                    hash ^= hash >> 16;
                    hash *= 0x85ebca6bu;
                    hash ^= hash >> 13;
                    return (int)(hash & 0x7FFFFFFFu);
                }
            }

            private static int NextPowerOfTwo(int value)
            {
                int p = 1;
                while (p < value)
                {
                    p <<= 1;
                }
                return p;
            }
        }
    }
}
