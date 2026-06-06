#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio
{
    public class AssetHandle
    {
        private static readonly ConcurrentDictionary<string, string> SourceHashCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

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
            var normalizedPath = NormalizeSourcePath(originalPath);
            var key = string.IsNullOrEmpty(normalizedPath)
                ? serializedFileName ?? string.Empty
                : $"{normalizedPath}\u001f{serializedFileName ?? string.Empty}";

            return SourceHashCache.GetOrAdd(key, ComputeSourceHash);
        }

        private static string ComputeSourceHash(string key)
        {
#if NET5_0_OR_GREATER
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
#else
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString(0, 16);
            }
#endif
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
