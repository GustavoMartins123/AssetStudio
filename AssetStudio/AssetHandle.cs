#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AssetStudio
{
    /// <summary>
    /// Interned per-file origin descriptor shared across standalone AssetHandle instances.
    /// Avoids repeating 4+ string references and recomputing SHA-256 source hashes on every handle.
    /// </summary>
    public sealed class AssetHandleOrigin
    {
        public static readonly AssetHandleOrigin Empty = new AssetHandleOrigin(
            serializedFileName: string.Empty,
            originalPath: string.Empty,
            sourceHash: string.Empty,
            unityVersion: string.Empty,
            uniqueIdFileName: "unknown");

        private static readonly ConcurrentDictionary<(string SerializedFileName, string OriginalPath, string SourceHash, string UnityVersion, string UniqueIdFileName), AssetHandleOrigin> OriginPool =
            new ConcurrentDictionary<(string, string, string, string, string), AssetHandleOrigin>();

        private static readonly ConcurrentDictionary<string, int> KeyOriginIdPool =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        private static int s_nextKeyOriginId;

        [ThreadStatic]
        private static AssetHandleOrigin? t_lastOrigin;

        public string SerializedFileName { get; }
        public string OriginalPath { get; }
        public string SourceHash { get; }
        public string UnityVersion { get; }
        public string UniqueIdFileName { get; }
        public string UniqueIdPrefix { get; }
        public int KeyOriginId { get; }

        private AssetHandleOrigin(
            string serializedFileName,
            string originalPath,
            string sourceHash,
            string unityVersion,
            string uniqueIdFileName)
        {
            SerializedFileName = serializedFileName ?? string.Empty;
            OriginalPath = originalPath ?? string.Empty;
            SourceHash = sourceHash ?? string.Empty;
            UnityVersion = unityVersion ?? string.Empty;
            UniqueIdFileName = string.IsNullOrWhiteSpace(uniqueIdFileName) ? "unknown" : uniqueIdFileName;
            UniqueIdPrefix = string.Concat(UniqueIdFileName, "#", SourceHash);
            KeyOriginId = (SerializedFileName.Length == 0
                && OriginalPath.Length == 0
                && SourceHash.Length == 0
                && UnityVersion.Length == 0
                && string.Equals(UniqueIdFileName, "unknown", StringComparison.Ordinal))
                ? 0
                : GetOrAddKeyOriginId(UniqueIdPrefix);
        }

        internal static int GetOrAddKeyOriginId(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return 0;
            }

            if (KeyOriginIdPool.TryGetValue(prefix, out int existing))
            {
                return existing;
            }

            return KeyOriginIdPool.GetOrAdd(prefix, static _ => Interlocked.Increment(ref s_nextKeyOriginId));
        }

        public static AssetHandleOrigin Intern(SerializedFile? file)
        {
            if (file == null)
            {
                return Empty;
            }

            string? sourcePath = !string.IsNullOrWhiteSpace(file.originalPath)
                ? file.originalPath
                : file.fullName;
            string sourceHash = AssetHandle.BuildSourceHash(file.fileName, sourcePath);
            return Intern(file.fileName, file.originalPath, sourceHash, file.unityVersion);
        }

        internal static AssetHandleOrigin InternFromSpans(
            string serializedFileName,
            string originalPath,
            ReadOnlySpan<char> sourceHashSpan,
            string unityVersion,
            ReadOnlySpan<char> uniqueIdFileNameSpan)
        {
            AssetHandleOrigin? last = t_lastOrigin;
            if (last != null
                && sourceHashSpan.SequenceEqual(last.SourceHash.AsSpan())
                && uniqueIdFileNameSpan.SequenceEqual(last.UniqueIdFileName.AsSpan()))
            {
                if (string.Equals(last.SerializedFileName, serializedFileName, StringComparison.Ordinal)
                    && string.Equals(last.OriginalPath, originalPath, StringComparison.Ordinal)
                    && string.Equals(last.UnityVersion, unityVersion, StringComparison.Ordinal))
                {
                    return last;
                }

                return Intern(serializedFileName, originalPath, last.SourceHash, unityVersion, last.UniqueIdFileName);
            }

            string uidFileName = uniqueIdFileNameSpan.SequenceEqual(serializedFileName.AsSpan())
                ? serializedFileName
                : uniqueIdFileNameSpan.ToString();
            string sourceHash = sourceHashSpan.ToString();
            return Intern(serializedFileName, originalPath, sourceHash, unityVersion, uidFileName);
        }

        public static AssetHandleOrigin Intern(
            string? serializedFileName,
            string? originalPath,
            string? sourceHash = null,
            string? unityVersion = null,
            string? uniqueIdFileName = null)
        {
            string normFile = serializedFileName ?? string.Empty;
            string normPath = originalPath ?? string.Empty;
            string normVer = unityVersion ?? string.Empty;
            string normHash = sourceHash ?? (normFile.Length == 0 && normPath.Length == 0
                ? string.Empty
                : AssetHandle.BuildSourceHash(normFile, normPath));
            string normUidFile = string.IsNullOrWhiteSpace(uniqueIdFileName)
                ? (string.IsNullOrWhiteSpace(normFile) ? "unknown" : normFile)
                : uniqueIdFileName;

            if (normFile.Length == 0
                && normPath.Length == 0
                && normHash.Length == 0
                && normVer.Length == 0
                && string.Equals(normUidFile, "unknown", StringComparison.Ordinal))
            {
                return Empty;
            }

            AssetHandleOrigin? last = t_lastOrigin;
            if (last != null
                && string.Equals(last.SerializedFileName, normFile, StringComparison.Ordinal)
                && string.Equals(last.OriginalPath, normPath, StringComparison.Ordinal)
                && string.Equals(last.SourceHash, normHash, StringComparison.Ordinal)
                && string.Equals(last.UnityVersion, normVer, StringComparison.Ordinal)
                && string.Equals(last.UniqueIdFileName, normUidFile, StringComparison.Ordinal))
            {
                return last;
            }

            var key = (normFile, normPath, normHash, normVer, normUidFile);
            AssetHandleOrigin result = OriginPool.GetOrAdd(
                key,
                static k => new AssetHandleOrigin(k.Item1, k.Item2, k.Item3, k.Item4, k.Item5));
            t_lastOrigin = result;
            return result;
        }

        internal static void ClearCache()
        {
            t_lastOrigin = null;
            OriginPool.Clear();
            KeyOriginIdPool.Clear();
        }
    }

    public class AssetHandle
    {
        private static readonly ConcurrentDictionary<string, string> SourceHashCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // Raw (originalPath, serializedFileName) -> hash. Only populated for fully-qualified or empty paths so the
        // cached result can never depend on the process current directory.
        private static readonly ConcurrentDictionary<(string, string), string> RawSourceHashCache = new ConcurrentDictionary<(string, string), string>();

        private AssetIndexArena? _arena;
        private int _arenaIndex = -1;
        private int _arenaGeneration;

        private AssetHandleOrigin _origin = AssetHandleOrigin.Empty;
        private string? _customUniqueId = string.Empty;
        private long _uniqueIdPathId;
        private bool _hasCanonicalUniqueId;

        private string _name = string.Empty;
        private ClassIDType _type;
        private string _container = string.Empty;
        private SerializedFile? _sourceFile;
        private long _pathId;
        private long _byteStart;
        private long _byteSize;
        private Object? _realObject;
        private object? _tag;

        public AssetHandle()
        {
        }

        internal AssetHandle(AssetIndexArena arena, int arenaIndex, int arenaGeneration)
        {
            _arena = arena;
            _arenaIndex = arenaIndex;
            _arenaGeneration = arenaGeneration;
            _customUniqueId = null;
        }

        public static AssetHandle CreateCanonical(
            AssetHandleOrigin origin,
            long pathId,
            string? name = null,
            ClassIDType type = ClassIDType.UnknownType,
            string? container = null,
            long byteStart = 0,
            long byteSize = 0,
            SerializedFile? sourceFile = null,
            Object? realObject = null,
            object? tag = null)
        {
            return new AssetHandle
            {
                _origin = origin ?? AssetHandleOrigin.Empty,
                _uniqueIdPathId = pathId,
                _hasCanonicalUniqueId = true,
                _customUniqueId = null,
                _pathId = pathId,
                _name = name ?? string.Empty,
                _type = type,
                _container = container ?? string.Empty,
                _byteStart = byteStart,
                _byteSize = byteSize,
                _sourceFile = sourceFile,
                _realObject = realObject,
                _tag = tag
            };
        }

        public static AssetHandle CreateWithOrigin(
            AssetHandleOrigin origin,
            string? uniqueId,
            long pathId,
            string? name = null,
            ClassIDType type = ClassIDType.UnknownType,
            string? container = null,
            long byteStart = 0,
            long byteSize = 0,
            SerializedFile? sourceFile = null,
            Object? realObject = null,
            object? tag = null)
        {
            var resolvedOrigin = origin ?? AssetHandleOrigin.Empty;
            var handle = new AssetHandle
            {
                _origin = resolvedOrigin,
                _pathId = pathId,
                _name = name ?? string.Empty,
                _type = type,
                _container = container ?? string.Empty,
                _byteStart = byteStart,
                _byteSize = byteSize,
                _sourceFile = sourceFile,
                _realObject = realObject,
                _tag = tag
            };

            if (!string.IsNullOrEmpty(uniqueId)
                && AssetHandleKey.TryParseUniqueIdWithPrefix(
                    uniqueId.AsSpan(),
                    out ReadOnlySpan<char> originPrefix,
                    out _,
                    out _,
                    out long parsedPathId,
                    out bool isCanonicalPathId)
                && isCanonicalPathId
                && originPrefix.SequenceEqual(resolvedOrigin.UniqueIdPrefix.AsSpan()))
            {
                handle._uniqueIdPathId = parsedPathId;
                handle._hasCanonicalUniqueId = true;
                handle._customUniqueId = null;
            }
            else
            {
                handle.UniqueID = uniqueId ?? string.Empty;
            }

            return handle;
        }

        public int ArenaIndex => IsArenaBound(out _) ? _arenaIndex : -1;

        public AssetHandleOrigin Origin
        {
            get
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    SourceOriginRecord record = arena.GetOrigin(_arenaIndex);
                    return AssetHandleOrigin.Intern(
                        record.SerializedFileName,
                        record.OriginalPath,
                        record.SourceHash,
                        record.UnityVersion,
                        record.UniqueIdFileName);
                }

                return _origin;
            }
        }

        public bool HasUniqueId
        {
            get
            {
                if (IsArenaBound(out _))
                {
                    return true;
                }

                return _hasCanonicalUniqueId || !string.IsNullOrEmpty(_customUniqueId);
            }
        }

        public bool TryGetKey(out AssetHandleKey key)
        {
            if (IsArenaBound(out AssetIndexArena arena))
            {
                key = arena.GetKey(_arenaIndex);
                return true;
            }

            if (_hasCanonicalUniqueId && _customUniqueId == null)
            {
                key = new AssetHandleKey(_origin.KeyOriginId, _uniqueIdPathId);
                return true;
            }

            if (!string.IsNullOrEmpty(_customUniqueId))
            {
                key = new AssetHandleKey(AssetHandleOrigin.GetOrAddKeyOriginId(_customUniqueId), _pathId);
                return true;
            }

            key = default;
            return false;
        }

        public string UniqueID
        {
            get
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    return arena.GetUniqueId(_arenaIndex);
                }

                if (_customUniqueId != null)
                {
                    return _customUniqueId;
                }

                if (_hasCanonicalUniqueId)
                {
                    return string.Concat(_origin.UniqueIdPrefix, "#", _uniqueIdPathId.ToString(CultureInfo.InvariantCulture));
                }

                return string.Empty;
            }
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetUniqueId(_arenaIndex, value);
                    return;
                }

                if (string.IsNullOrEmpty(value))
                {
                    _hasCanonicalUniqueId = false;
                    _customUniqueId = string.Empty;
                    return;
                }

                if (AssetHandleKey.TryParseUniqueIdWithPrefix(
                    value.AsSpan(),
                    out _,
                    out ReadOnlySpan<char> fileNameSpan,
                    out ReadOnlySpan<char> sourceHashSpan,
                    out long parsedPathId,
                    out bool isCanonicalPathId) && isCanonicalPathId)
                {
                    _origin = AssetHandleOrigin.InternFromSpans(
                        _origin.SerializedFileName,
                        _origin.OriginalPath,
                        sourceHashSpan,
                        _origin.UnityVersion,
                        fileNameSpan);
                    _uniqueIdPathId = parsedPathId;
                    _hasCanonicalUniqueId = true;
                    _customUniqueId = null;
                }
                else
                {
                    _hasCanonicalUniqueId = false;
                    _customUniqueId = value;
                }
            }
        }

        public string Name
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetName(_arenaIndex) : _name;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetName(_arenaIndex, value);
                    return;
                }

                _name = value ?? string.Empty;
            }
        }

        public ClassIDType Type
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetType(_arenaIndex) : _type;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetType(_arenaIndex, value);
                    return;
                }

                _type = value;
            }
        }

        public string TypeString => Type.ToString();

        public string Container
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetContainer(_arenaIndex) : _container;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetContainer(_arenaIndex, value);
                    return;
                }

                _container = value ?? string.Empty;
            }
        }

        // Origins
        public string OriginalPath
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetOriginalPath(_arenaIndex) : _origin.OriginalPath;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetOriginalPath(_arenaIndex, value);
                    return;
                }

                string normPath = value ?? string.Empty;
                if (string.Equals(_origin.OriginalPath, normPath, StringComparison.Ordinal))
                {
                    return;
                }

                _origin = AssetHandleOrigin.Intern(
                    _origin.SerializedFileName,
                    normPath,
                    _hasCanonicalUniqueId ? _origin.SourceHash : string.Empty,
                    _origin.UnityVersion,
                    _hasCanonicalUniqueId ? _origin.UniqueIdFileName : null);
            }
        }

        public string SerializedFileName
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetSerializedFileName(_arenaIndex) : _origin.SerializedFileName;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetSerializedFileName(_arenaIndex, value);
                    return;
                }

                string normFile = value ?? string.Empty;
                if (string.Equals(_origin.SerializedFileName, normFile, StringComparison.Ordinal))
                {
                    return;
                }

                _origin = AssetHandleOrigin.Intern(
                    normFile,
                    _origin.OriginalPath,
                    _hasCanonicalUniqueId ? _origin.SourceHash : string.Empty,
                    _origin.UnityVersion,
                    _hasCanonicalUniqueId ? _origin.UniqueIdFileName : null);
            }
        }

        public string UnityVersion
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetUnityVersion(_arenaIndex) : _origin.UnityVersion;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetUnityVersion(_arenaIndex, value);
                    return;
                }

                string normVer = value ?? string.Empty;
                if (string.Equals(_origin.UnityVersion, normVer, StringComparison.Ordinal))
                {
                    return;
                }

                _origin = AssetHandleOrigin.Intern(
                    _origin.SerializedFileName,
                    _origin.OriginalPath,
                    _origin.SourceHash,
                    normVer,
                    _origin.UniqueIdFileName);
            }
        }

        public SerializedFile? SourceFile
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetSourceFile(_arenaIndex) : _sourceFile;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetSourceFile(_arenaIndex, value);
                    return;
                }

                _sourceFile = value;
            }
        }

        public long PathID
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetPathId(_arenaIndex) : _pathId;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetPathId(_arenaIndex, value);
                    return;
                }

                _pathId = value;
            }
        }

        public long ByteStart
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetByteStart(_arenaIndex) : _byteStart;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetByteStart(_arenaIndex, value);
                    return;
                }

                _byteStart = value;
            }
        }

        public long ByteSize
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetByteSize(_arenaIndex) : _byteSize;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetByteSize(_arenaIndex, value);
                    return;
                }

                _byteSize = value;
            }
        }

        public bool IsMaterialized => RealObject != null;

        public Object? RealObject
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetRealObject(_arenaIndex) : _realObject;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetRealObject(_arenaIndex, value);
                    return;
                }

                _realObject = value;
            }
        }

        public object? Tag
        {
            get => IsArenaBound(out AssetIndexArena arena) ? arena.GetTag(_arenaIndex) : _tag;
            set
            {
                if (IsArenaBound(out AssetIndexArena arena))
                {
                    arena.SetTag(_arenaIndex, value);
                    return;
                }

                _tag = value;
            }
        }

        public string DisplayType => GetDisplayType();

        internal bool TryGetCanonicalIdentity(out AssetHandleOrigin origin, out long uniqueIdPathId)
        {
            if (_hasCanonicalUniqueId && _customUniqueId == null)
            {
                origin = _origin;
                uniqueIdPathId = _uniqueIdPathId;
                return true;
            }

            origin = AssetHandleOrigin.Empty;
            uniqueIdPathId = 0;
            return false;
        }

        internal bool IsBoundTo(AssetIndexArena arena, int generation, out int index)
        {
            if (ReferenceEquals(_arena, arena) && _arenaGeneration == generation && _arenaIndex >= 0)
            {
                index = _arenaIndex;
                return true;
            }

            index = -1;
            return false;
        }

        internal void BindToArena(AssetIndexArena arena, int index, int generation)
        {
            _arena = arena;
            _arenaIndex = index;
            _arenaGeneration = generation;

            // Release local reference fields so bound handles carry zero duplicate string overhead
            _origin = AssetHandleOrigin.Empty;
            _customUniqueId = null;
            _hasCanonicalUniqueId = false;
            _name = string.Empty;
            _container = string.Empty;
            _sourceFile = null;
            _realObject = null;
            _tag = null;
        }

        private bool IsArenaBound(out AssetIndexArena arena)
        {
            AssetIndexArena? current = _arena;
            if (current != null && _arenaIndex >= 0 && current.Generation == _arenaGeneration)
            {
                arena = current;
                return true;
            }

            arena = null!;
            return false;
        }

        public static string BuildUniqueID(SerializedFile file, long pathID)
        {
            if (file == null)
            {
                return BuildUniqueID(string.Empty, string.Empty, pathID);
            }

            var sourcePath = !string.IsNullOrWhiteSpace(file.originalPath)
                ? file.originalPath
                : file.fullName;
            return BuildUniqueID(file.fileName, sourcePath, pathID);
        }

        public static string BuildUniqueID(string serializedFileName, string? originalPath, long pathID)
        {
            var fileName = string.IsNullOrWhiteSpace(serializedFileName)
                ? "unknown"
                : serializedFileName;
            return $"{fileName}#{BuildSourceHash(serializedFileName, originalPath)}#{pathID}";
        }

        public static string BuildSourceHash(string serializedFileName, string? originalPath)
        {
            var rawPath = originalPath ?? string.Empty;
            var rawName = serializedFileName ?? string.Empty;
            var cacheable = string.IsNullOrWhiteSpace(rawPath) || Path.IsPathFullyQualified(rawPath);
            if (cacheable && RawSourceHashCache.TryGetValue((rawPath, rawName), out var cached))
            {
                return cached;
            }

            string hash;
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                hash = SourceHashCache.GetOrAdd(rawName, static k => ComputeSourceHash(k.AsSpan()));
            }
            else if (TryGetNormalizedPathSpan(originalPath, out var normalizedFull))
            {
                if (normalizedFull.IsEmpty)
                {
                    hash = SourceHashCache.GetOrAdd(rawName, static k => ComputeSourceHash(k.AsSpan()));
                }
                else
                {
                    var lookup = SourceHashCache.GetAlternateLookup<ReadOnlySpan<char>>();
                    var keyLen = normalizedFull.Length + 1 + rawName.Length;
                    char[]? rentedChars = null;
                    Span<char> keyBuf = keyLen <= 512
                        ? stackalloc char[512]
                        : (rentedChars = ArrayPool<char>.Shared.Rent(keyLen));
                    try
                    {
                        var written = normalizedFull.ToUpperInvariant(keyBuf);
                        if (written == normalizedFull.Length)
                        {
                            keyBuf[written] = '\u001f';
                            rawName.AsSpan().CopyTo(keyBuf.Slice(written + 1));
                            var keySpan = keyBuf.Slice(0, written + 1 + rawName.Length);
                            if (!lookup.TryGetValue(keySpan, out hash!))
                            {
                                hash = ComputeSourceHash(keySpan);
                                if (!lookup.TryAdd(keySpan, hash) && lookup.TryGetValue(keySpan, out var existing))
                                {
                                    hash = existing;
                                }
                            }
                        }
                        else
                        {
                            var key = $"{NormalizeSourcePath(originalPath)}\u001f{rawName}";
                            hash = SourceHashCache.GetOrAdd(key, static k => ComputeSourceHash(k.AsSpan()));
                        }
                    }
                    finally
                    {
                        if (rentedChars != null)
                        {
                            ArrayPool<char>.Shared.Return(rentedChars);
                        }
                    }
                }
            }
            else
            {
                var normalizedPath = NormalizeSourcePath(originalPath);
                var key = string.IsNullOrEmpty(normalizedPath)
                    ? rawName
                    : $"{normalizedPath}\u001f{rawName}";
                hash = SourceHashCache.GetOrAdd(key, static k => ComputeSourceHash(k.AsSpan()));
            }

            if (cacheable)
            {
                RawSourceHashCache.TryAdd((rawPath, rawName), hash);
            }

            return hash;
        }

        public static void ClearSourceHashCache()
        {
            SourceHashCache.Clear();
            RawSourceHashCache.Clear();
            AssetHandleOrigin.ClearCache();
        }

        private static string ComputeSourceHash(ReadOnlySpan<char> key)
        {
            // First 8 bytes of SHA-256 as 16 lowercase hex chars (persisted in UniqueIDs / SQLite cache).
            var maxBytes = Encoding.UTF8.GetMaxByteCount(key.Length);
            byte[]? rented = null;
            Span<byte> utf8Buf = maxBytes <= 1024
                ? stackalloc byte[1024]
                : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
            try
            {
                var written = Encoding.UTF8.GetBytes(key, utf8Buf);
                Span<byte> digest = stackalloc byte[32];
                SHA256.HashData(utf8Buf.Slice(0, written), digest);
                return Convert.ToHexStringLower(digest.Slice(0, 8));
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        private static bool TryGetNormalizedPathSpan(string path, out ReadOnlySpan<char> trimmedFullPath)
        {
            try
            {
                var span = Path.GetFullPath(path).AsSpan();
                while (!span.IsEmpty && (span[^1] == Path.DirectorySeparatorChar || span[^1] == Path.AltDirectorySeparatorChar))
                {
                    span = span.Slice(0, span.Length - 1);
                }

                trimmedFullPath = span;
                return true;
            }
            catch
            {
                trimmedFullPath = default;
                return false;
            }
        }

        private static string NormalizeSourcePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
            }
            catch
            {
                return path!.Replace('\\', '/').Trim().ToUpperInvariant();
            }
        }

        private string GetDisplayType()
        {
            var display = TypeString;
            if (Type == ClassIDType.PrefabInstance)
            {
                display = "Prefab (Composite)";
            }
            else if (Type == ClassIDType.GameObject)
            {
                display = "GameObject (Scene Node)";
            }
            return display;
        }

        public static string? TryReadObjectName(ObjectReader reader)
        {
            if (reader == null) return null;
            try
            {
                reader.Reset();
                // 1. Simula leitura do Object base
                if (reader.platform == BuildTarget.NoTarget)
                {
                    reader.ReadUInt32(); // m_ObjectHideFlags
                }

                // Dependendo do tipo, o layout varia:
                switch (reader.type)
                {
                    case ClassIDType.GameObject:
                        {
                            // EditorExtension base
                            if (reader.platform == BuildTarget.NoTarget)
                            {
                                SkipPPtr(reader); // m_PrefabParentObject
                                SkipPPtr(reader); // m_PrefabInternal
                            }
                            int componentsSize = reader.ReadInt32();
                            for (int i = 0; i < componentsSize; i++)
                            {
                                if ((reader.version[0] == 5 && reader.version[1] < 5) || reader.version[0] < 5)
                                {
                                    reader.ReadInt32(); // first
                                }
                                SkipPPtr(reader); // component PPtr
                            }
                            reader.ReadInt32(); // m_Layer
                            return reader.ReadAlignedString();
                        }
                    case ClassIDType.MonoBehaviour:
                        {
                            // Behaviour -> Component -> EditorExtension
                            if (reader.platform == BuildTarget.NoTarget)
                            {
                                SkipPPtr(reader); // m_PrefabParentObject
                                SkipPPtr(reader); // m_PrefabInternal
                            }
                            SkipPPtr(reader); // Component m_GameObject
                            reader.ReadByte(); // Behaviour m_Enabled
                            reader.AlignStream();
                            SkipPPtr(reader); // MonoBehaviour m_Script
                            return reader.ReadAlignedString();
                        }
                    case ClassIDType.MonoScript:
                        {
                            // NamedObject : EditorExtension : Object
                            if (reader.platform == BuildTarget.NoTarget)
                            {
                                SkipPPtr(reader); // m_PrefabParentObject
                                SkipPPtr(reader); // m_PrefabInternal
                            }
                            return reader.ReadAlignedString();
                        }
                    default:
                        {
                            // Para NamedObject subclasses genéricas
                            if (reader.platform == BuildTarget.NoTarget)
                            {
                                SkipPPtr(reader); // m_PrefabParentObject
                                SkipPPtr(reader); // m_PrefabInternal
                            }
                            return reader.ReadAlignedString();
                        }
                }
            }
            catch
            {
                // Fallback silencioso
                return null;
            }
        }

        private static void SkipPPtr(ObjectReader reader)
        {
            reader.ReadInt32(); // m_FileID
            if (reader.m_Version < SerializedFileFormatVersion.Unknown_14)
            {
                reader.ReadInt32();
            }
            else
            {
                reader.ReadInt64();
            }
        }
    }

    public interface IAssetHandleTag
    {
        void ClearAsset();
    }
}
