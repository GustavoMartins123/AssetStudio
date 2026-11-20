#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio
{
    public class AssetHandle
    {
        private static readonly ConcurrentDictionary<string, string> SourceHashCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // Raw (originalPath, serializedFileName) -> hash. Only populated for fully-qualified or empty paths so the
        // cached result can never depend on the process current directory.
        private static readonly ConcurrentDictionary<(string, string), string> RawSourceHashCache = new ConcurrentDictionary<(string, string), string>();

        public string UniqueID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ClassIDType Type { get; set; }
        public string TypeString => Type.ToString();
        public string Container { get; set; } = string.Empty;
        
        // Origins
        public string OriginalPath { get; set; } = string.Empty; // bundle/file of origin path
        public string SerializedFileName { get; set; } = string.Empty; // e.g. "level0"
        
        public SerializedFile? SourceFile { get; set; } // Reference to SerializedFile
        public long PathID { get; set; }
        public long ByteStart { get; set; }
        public long ByteSize { get; set; }
        
        public bool IsMaterialized => RealObject != null;
        public Object? RealObject { get; set; }
        public object? Tag { get; set; }

        public string DisplayType => GetDisplayType();

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
