using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis
{
    public enum BuildDiffContentCheck
    {
        MetadataOnly = 0,
        FullContent = 1
    }

    internal enum BuildDiffContentVerdict
    {
        Identical,
        Different,
        Unverified
    }

    /// <summary>
    /// Compares the real bytes of matched assets between two builds. Whole container files are compared first;
    /// only containers whose bytes differ are opened, and then each object's serialized bytes plus any data it
    /// streams from .resS/.resource files are hashed.
    /// </summary>
    internal sealed class BuildDiffContentVerifier
    {
        private const int CompareBufferSize = 1 << 20;

        private readonly StringComparer _pathComparer;
        private readonly Dictionary<(string Baseline, string Target), bool> _identicalContainers;
        private readonly Dictionary<string, ContainerDigests> _digests;
        private readonly Dictionary<string, string[]> _streamedResourcesByDirectory;

        public BuildDiffContentVerifier(bool caseSensitivePaths)
        {
            _pathComparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            _identicalContainers = new Dictionary<(string, string), bool>();
            _digests = new Dictionary<string, ContainerDigests>(_pathComparer);
            _streamedResourcesByDirectory = new Dictionary<string, string[]>(_pathComparer);
        }

        public int ContainersCompared { get; private set; }
        public int ContainersOpened { get; private set; }

        public BuildDiffContentVerdict Compare(
            BuildDiffAssetSnapshot baseline,
            BuildDiffAssetSnapshot target,
            CancellationToken ct,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(baseline.OriginalPath) || string.IsNullOrWhiteSpace(target.OriginalPath))
            {
                reason = "source file path is not recorded in the index";
                return BuildDiffContentVerdict.Unverified;
            }

            if (!File.Exists(baseline.OriginalPath) || !File.Exists(target.OriginalPath))
            {
                reason = "source file is no longer on disk";
                return BuildDiffContentVerdict.Unverified;
            }

            if (AreContainersIdentical(baseline.OriginalPath, target.OriginalPath, ct))
            {
                return BuildDiffContentVerdict.Identical;
            }

            var baselineDigests = GetDigests(baseline.OriginalPath, ct);
            var targetDigests = GetDigests(target.OriginalPath, ct);
            if (!baselineDigests.TryGet(baseline.SerializedFileName, baseline.PathId, out var baselineDigest, out reason)
                || !targetDigests.TryGet(target.SerializedFileName, target.PathId, out var targetDigest, out reason))
            {
                return BuildDiffContentVerdict.Unverified;
            }

            return baselineDigest == targetDigest ? BuildDiffContentVerdict.Identical : BuildDiffContentVerdict.Different;
        }

        /// <summary>Returns true when both files exist and hold exactly the same bytes.</summary>
        public static bool FilesHaveIdenticalBytes(string baselinePath, string targetPath, CancellationToken ct)
        {
            if (!File.Exists(baselinePath) || !File.Exists(targetPath))
            {
                return false;
            }

            using var a = new FileStream(baselinePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            using var b = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (a.Length != b.Length)
            {
                return false;
            }

            var bufferA = ArrayPool<byte>.Shared.Rent(CompareBufferSize);
            var bufferB = ArrayPool<byte>.Shared.Rent(CompareBufferSize);
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int readA = a.ReadAtLeast(bufferA.AsSpan(0, CompareBufferSize), CompareBufferSize, throwOnEndOfStream: false);
                    int readB = b.ReadAtLeast(bufferB.AsSpan(0, CompareBufferSize), CompareBufferSize, throwOnEndOfStream: false);
                    if (readA != readB)
                    {
                        return false;
                    }

                    if (readA == 0)
                    {
                        return true;
                    }

                    if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bufferA);
                ArrayPool<byte>.Shared.Return(bufferB);
            }
        }

        private bool AreContainersIdentical(string baselinePath, string targetPath, CancellationToken ct)
        {
            var key = (baselinePath, targetPath);
            if (_identicalContainers.TryGetValue(key, out var cached))
            {
                return cached;
            }

            ContainersCompared++;
            bool identical = FilesHaveIdenticalBytes(baselinePath, targetPath, ct)
                && StreamedCompanionsIdentical(baselinePath, targetPath, ct);
            _identicalContainers[key] = identical;
            return identical;
        }

        // A loose serialized file (level0, sharedassets0.assets, ...) streams texture/audio/video data from sibling
        // files such as "sharedassets0.assets.resS" or "sharedassets0.resource"; bundles carry them inside the archive.
        private bool StreamedCompanionsIdentical(string baselinePath, string targetPath, CancellationToken ct)
        {
            var baselineCompanions = ListStreamedCompanions(baselinePath);
            var targetCompanions = ListStreamedCompanions(targetPath);
            if (baselineCompanions.Count != targetCompanions.Count)
            {
                return false;
            }

            foreach (var (name, path) in baselineCompanions)
            {
                if (!targetCompanions.TryGetValue(name, out var targetCompanion)
                    || !FilesHaveIdenticalBytes(path, targetCompanion, ct))
                {
                    return false;
                }
            }

            return true;
        }

        private Dictionary<string, string> ListStreamedCompanions(string containerPath)
        {
            var result = new Dictionary<string, string>(_pathComparer);
            var directory = Path.GetDirectoryName(containerPath);
            if (string.IsNullOrEmpty(directory) || IsArchiveContainer(containerPath))
            {
                return result;
            }

            var prefix = Path.GetFileNameWithoutExtension(containerPath) + ".";
            foreach (var path in GetStreamedResourceFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith(prefix, _pathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    result[name] = path;
                }
            }

            return result;
        }

        // .resS/.resource files of a folder, listed once per folder.
        private string[] GetStreamedResourceFiles(string directory)
        {
            if (_streamedResourcesByDirectory.TryGetValue(directory, out var cached))
            {
                return cached;
            }

            var files = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Where(p => p.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<string>();
            _streamedResourcesByDirectory[directory] = files;
            return files;
        }

        // UnityFS/UnityWeb/UnityRaw/UnityArchive bundles and ZIP-based packages (APK, OBB) carry their streamed data inside.
        private static bool IsArchiveContainer(string path)
        {
            Span<byte> head = stackalloc byte[5];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return (read >= 5 && head.SequenceEqual("Unity"u8))
                || (read >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K');
        }

        private ContainerDigests GetDigests(string containerPath, CancellationToken ct)
        {
            if (_digests.TryGetValue(containerPath, out var cached))
            {
                return cached;
            }

            ContainersOpened++;
            var digests = ContainerDigests.Read(containerPath, ct);
            _digests[containerPath] = digests;
            return digests;
        }

        private sealed class ContainerDigests
        {
            private readonly Dictionary<(string SerializedFileName, long PathId), ulong> _byObject = new();
            private readonly Dictionary<(string SerializedFileName, long PathId), string> _failures = new();
            private string? _error;

            public bool TryGet(string serializedFileName, long pathId, out ulong digest, out string reason)
            {
                var key = (Normalize(serializedFileName), pathId);
                if (_byObject.TryGetValue(key, out digest))
                {
                    reason = string.Empty;
                    return true;
                }

                reason = _failures.TryGetValue(key, out var failure)
                    ? failure
                    : _error ?? "asset was not found when the source file was reopened";
                return false;
            }

            public static ContainerDigests Read(string containerPath, CancellationToken ct)
            {
                var result = new ContainerDigests();
                try
                {
                    using var manager = new AssetsManager
                    {
                        LazyLoading = true,
                        AutoLoadCatalogDependencies = false,
                        AutoLoadExternalDependencies = false
                    };
                    manager.LoadFiles(containerPath);
                    foreach (var file in manager.assetsFileList)
                    {
                        var fileKey = Normalize(file.fileName);
                        for (int i = 0; i < file.m_Objects.Count; i++)
                        {
                            if ((i & 0xFF) == 0)
                            {
                                ct.ThrowIfCancellationRequested();
                            }

                            var info = file.m_Objects[i];
                            try
                            {
                                result._byObject[(fileKey, info.m_PathID)] = HashObject(file, info);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                result._failures[(fileKey, info.m_PathID)] = $"could not read the asset bytes ({ex.Message})";
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result._error = $"source file could not be opened ({ex.Message})";
                }

                return result;
            }

            private static ulong HashObject(SerializedFile file, ObjectInfo info)
            {
                var reader = new ObjectReader(file.reader, file, info);
                reader.Reset();
                int size = checked((int)info.byteSize);
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
                ulong digest;
                try
                {
                    var span = buffer.AsSpan(0, size);
                    reader.BaseStream.ReadExactly(span);
                    digest = XxHash64.Compute(span);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var streamed = ReadStreamedData(reader);
                return streamed == null ? digest : FastHash64.Combine(digest, XxHash64.Compute(streamed));
            }

            private static byte[]? ReadStreamedData(ObjectReader reader)
            {
                if (reader.type is not (ClassIDType.Texture2D or ClassIDType.Texture2DArray or ClassIDType.Cubemap
                    or ClassIDType.AudioClip or ClassIDType.Mesh or ClassIDType.VideoClip))
                {
                    return null;
                }

                reader.Reset();
                var resource = AssetsManager.CreateObjectFromReader(reader) switch
                {
                    Texture2D texture when !string.IsNullOrEmpty(texture.m_StreamData?.path) => texture.image_data,
                    Texture2DArray array when !string.IsNullOrEmpty(array.m_StreamData?.path) => array.image_data,
                    AudioClip clip => clip.m_AudioData,
                    VideoClip video => video.m_VideoData,
                    Mesh mesh when mesh.m_StreamData is { size: > 0 } stream && !string.IsNullOrEmpty(stream.path)
                        => new ResourceReader(stream.path, reader.assetsFile, stream.offset, stream.size),
                    _ => null
                };
                return resource?.GetData();
            }

            private static string Normalize(string? value)
            {
                return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToUpperInvariant();
            }
        }
    }
}
