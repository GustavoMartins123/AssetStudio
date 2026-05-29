using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AssetStudio
{
    public sealed class ProjectScanResult
    {
        public string RootPath { get; internal set; }
        public int TotalFiles { get; internal set; }
        public long TotalBytes { get; internal set; }
        public int UnityBundleCount { get; internal set; }
        public int SerializedFileCount { get; internal set; }
        public int ResourceFileCount { get; internal set; }
        public int OtherFileCount { get; internal set; }
        public int ErrorCount { get; internal set; }
        public List<string> SampleUnityBundles { get; } = new List<string>();

        public bool IsRisky => UnityBundleCount >= ProjectScanner.RiskyUnityBundleCount;
    }

    public static class ProjectScanner
    {
        public const int RiskyUnityBundleCount = 1000;
        private const int MaxSamples = 20;

        public static ProjectScanResult ScanFolder(string path, CancellationToken cancellationToken = default)
        {
            var result = new ProjectScanResult
            {
                RootPath = Path.GetFullPath(path)
            };

            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.TotalFiles++;

                try
                {
                    var info = new FileInfo(file);
                    result.TotalBytes += info.Length;

                    switch (DetectFileKind(file, info.Length))
                    {
                        case ProjectFileKind.UnityBundle:
                            result.UnityBundleCount++;
                            if (result.SampleUnityBundles.Count < MaxSamples)
                            {
                                result.SampleUnityBundles.Add(file);
                            }
                            break;
                        case ProjectFileKind.SerializedFile:
                            result.SerializedFileCount++;
                            break;
                        case ProjectFileKind.ResourceFile:
                            result.ResourceFileCount++;
                            break;
                        default:
                            result.OtherFileCount++;
                            break;
                    }
                }
                catch (IOException)
                {
                    result.ErrorCount++;
                }
                catch (UnauthorizedAccessException)
                {
                    result.ErrorCount++;
                }
            }

            return result;
        }

        private static ProjectFileKind DetectFileKind(string path, long fileSize)
        {
            if (fileSize < 4)
            {
                return ProjectFileKind.Other;
            }

            var header = new byte[Math.Min(64, fileSize)];
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var read = stream.Read(header, 0, header.Length);
                if (StartsWithAscii(header, read, "UnityFS")
                    || StartsWithAscii(header, read, "UnityWeb")
                    || StartsWithAscii(header, read, "UnityRaw")
                    || StartsWithAscii(header, read, "UnityArchive"))
                {
                    return ProjectFileKind.UnityBundle;
                }

                if (IsSerializedFileHeader(header, read, fileSize))
                {
                    return ProjectFileKind.SerializedFile;
                }
            }

            var extension = Path.GetExtension(path);
            if (extension.Equals(".resS", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".resource", StringComparison.OrdinalIgnoreCase))
            {
                return ProjectFileKind.ResourceFile;
            }

            return ProjectFileKind.Other;
        }

        private static bool StartsWithAscii(byte[] bytes, int length, string value)
        {
            if (length < value.Length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (bytes[i] != (byte)value[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSerializedFileHeader(byte[] header, int length, long actualFileSize)
        {
            if (length < 20)
            {
                return false;
            }

            var metadataSize = ReadUInt32BigEndian(header, 0);
            var fileSize = ReadUInt32BigEndian(header, 4);
            var version = ReadUInt32BigEndian(header, 8);
            var dataOffset = ReadUInt32BigEndian(header, 12);

            if (version >= 22)
            {
                if (length < 48)
                {
                    return false;
                }

                metadataSize = ReadUInt32BigEndian(header, 20);
                fileSize = ReadInt64BigEndian(header, 24);
                dataOffset = ReadInt64BigEndian(header, 32);
            }

            return metadataSize > 0
                && fileSize == actualFileSize
                && dataOffset > 0
                && dataOffset <= actualFileSize;
        }

        private static long ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((long)bytes[offset] << 24)
                | ((long)bytes[offset + 1] << 16)
                | ((long)bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }

        private static long ReadInt64BigEndian(byte[] bytes, int offset)
        {
            return ((long)bytes[offset] << 56)
                | ((long)bytes[offset + 1] << 48)
                | ((long)bytes[offset + 2] << 40)
                | ((long)bytes[offset + 3] << 32)
                | ((long)bytes[offset + 4] << 24)
                | ((long)bytes[offset + 5] << 16)
                | ((long)bytes[offset + 6] << 8)
                | bytes[offset + 7];
        }

        private enum ProjectFileKind
        {
            Other,
            UnityBundle,
            SerializedFile,
            ResourceFile
        }
    }
}
