#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    internal static class DecryptionSources
    {
        private static readonly string[] BundleExtensions = { ".bundle", ".enc", ".unity3d", ".ab", "" };
        private static readonly ConcurrentDictionary<string, string?> SourceKeys = new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            SourceKeys.Clear();
        }

        public static string? GetSourceKey(string directory)
            => SourceKeys.GetOrAdd(directory, static d => FindSourceKey(d));

        public static string? FindSourceKey(string directory)
        {
            string? current = directory;
            for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
            {
                string managed = Path.Combine(current, "Managed");
                string metadata = Path.Combine(current, "il2cpp_data", "Metadata", "global-metadata.dat");
                bool hasManaged = Directory.Exists(managed);
                bool hasMetadata = File.Exists(metadata);
                if (hasManaged || hasMetadata)
                {
                    return (hasManaged ? "M:" + managed : string.Empty) + (hasMetadata ? "|I:" + metadata : string.Empty);
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        public static bool HasMetadata(string sourceKey)
            => sourceKey.Contains("|I:", StringComparison.Ordinal) || sourceKey.StartsWith("I:", StringComparison.Ordinal);

        public static string? GetMetadataPath(string sourceKey)
            => GetPart(sourceKey, 'I');

        public static string? GetManagedDirectory(string sourceKey)
            => GetPart(sourceKey, 'M');

        public static string? GetDataDirectory(string sourceKey)
        {
            string? managed = GetManagedDirectory(sourceKey);
            if (managed != null)
            {
                return Path.GetDirectoryName(managed);
            }

            string? metadata = GetMetadataPath(sourceKey);
            return metadata == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(metadata)));
        }

        private static string? GetPart(string sourceKey, char kind)
        {
            string prefix = kind + ":";
            foreach (string part in sourceKey.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return part[2..];
                }
            }

            return null;
        }

        public static bool IsBundleLikeFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            foreach (string candidate in BundleExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool LooksLikePlainText(ReadOnlySpan<byte> sample)
        {
            if (sample.IsEmpty)
            {
                return false;
            }

            for (int i = 0; i < sample.Length; i++)
            {
                byte b = sample[i];
                if (b is not ((>= 0x20 and <= 0x7E) or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    return false;
                }
            }

            return true;
        }

        public static IEnumerable<string> OrderAssemblyPaths(IEnumerable<string> paths)
        {
            return paths.OrderBy(static p => IsFrameworkAssembly(p) ? 1 : 0)
                .ThenBy(static p => p, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsFrameworkAssembly(string path)
        {
            string name = Path.GetFileName(path);
            return name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Mono", StringComparison.OrdinalIgnoreCase)
                || name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase);
        }
    }
}
