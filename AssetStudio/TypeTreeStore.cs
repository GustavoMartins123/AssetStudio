using System;
using System.Collections.Concurrent;
using System.IO;

namespace AssetStudio
{
    /// <summary>
    /// TypeTrees that a Unity 6000.5+ build extracted out of its serialized files (ContentBuildFlags.ExtractTypeTree).
    /// The archive is a UnityFS bundle with one entry per TypeTree, named by the 32-hex-digit TypeTree hash, and each
    /// entry holds the same 'tthm' block that a format 23 file stores inline. Serialized files keep only the hash.
    /// </summary>
    public sealed class TypeTreeStore
    {
        private const int HashLength = 16;
        private readonly ConcurrentDictionary<string, TypeTree> trees = new(StringComparer.OrdinalIgnoreCase);

        public int Count => trees.Count;

        public bool TryGet(byte[] typeTreeHash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeTree? tree)
        {
            tree = null;
            return typeTreeHash.Length == HashLength && trees.TryGetValue(Convert.ToHexString(typeTreeHash), out tree);
        }

        /// <summary>True when every entry of the bundle is named by a TypeTree hash and starts with the 'tthm' header.</summary>
        public static bool IsTypeTreeArchive(BundleFile bundle)
        {
            if (bundle.fileList.Length == 0)
            {
                return false;
            }
            foreach (var file in bundle.fileList)
            {
                if (!IsHashName(file.path) || file.stream.Length < 16)
                {
                    return false;
                }
            }
            var first = bundle.fileList[0].stream;
            Span<byte> magic = stackalloc byte[4];
            first.Position = 0;
            int read = first.Read(magic);
            first.Position = 0;
            return read == 4 && magic.SequenceEqual("mhtt"u8);
        }

        /// <summary>Adds every entry of a TypeTree archive and disposes the entry streams. Returns the number of TypeTrees added.</summary>
        public int AddArchive(BundleFile bundle, string sourceName)
        {
            int added = 0;
            foreach (var file in bundle.fileList)
            {
                using var stream = file.stream;
                stream.Position = 0;
                var tree = new TypeTree();
                using (var reader = new EndianBinaryReader(stream, EndianType.LittleEndian))
                {
                    SerializedFile.ReadTypeTreeData(reader, stream.Length, tree, $"{sourceName}/{file.path}");
                }
                if (trees.TryAdd(file.path, tree))
                {
                    added++;
                }
            }
            return added;
        }

        private static bool IsHashName(string name)
        {
            if (name.Length != HashLength * 2)
            {
                return false;
            }
            foreach (var c in name)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
