using System;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class ResourceReader
    {
        private static volatile bool s_enableXorInversionHeuristic;

        /// <summary>
        /// Enables the legacy heuristic that XOR-inverts (0xFF) resource payloads when more than 50%
        /// of the first 1024 bytes equal 0x01. Disabled by default to prevent corrupting legitimate
        /// dark R8/Alpha8 textures and binary payloads.
        /// </summary>
        public static bool EnableXorInversionHeuristic
        {
            get => s_enableXorInversionHeuristic;
            set => s_enableXorInversionHeuristic = value;
        }

        private bool needSearch;
        private string path;
        private SerializedFile assetsFile;
        private long offset;
        private long size;
        private BinaryReader reader;
        private readonly byte[] rawData;
        private bool isEmbedded;

        public int Size { get => (int)size; }

        public ResourceReader(string path, SerializedFile assetsFile, long offset, long size)
        {
            needSearch = true;
            this.path = NormalizeResourcePath(path);
            this.assetsFile = assetsFile;
            this.offset = offset;
            this.size = size;
        }

        public ResourceReader(byte[] data)
        {
            rawData = data ?? Array.Empty<byte>();
            offset = 0;
            size = rawData.Length;
        }

        public ResourceReader(BinaryReader reader, long offset, long size)
        {
            if (reader is ObjectReader objReader)
            {
                this.assetsFile = objReader.assetsFile;
                this.isEmbedded = true;
            }
            else
            {
                this.reader = reader;
            }
            this.offset = offset;
            this.size = size;
        }

        private BinaryReader GetReader(out bool shouldDispose)
        {
            shouldDispose = false;
            if (needSearch)
            {
                var normalizedPath = NormalizePath(path);
                var resourceFileName = Path.GetFileName(normalizedPath);

                if (assetsFile != null && (string.Equals(resourceFileName, assetsFile.fileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedPath, assetsFile.fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    needSearch = false;
                    reader = assetsFile.reader;
                    return CloneIfFileReader(reader, out shouldDispose);
                }

                if (assetsFile.assetsManager.resourceFileReaders.TryGetValue(normalizedPath, out reader) && CanReadRange(reader))
                {
                    needSearch = false;
                    return CloneIfFileReader(reader, out shouldDispose);
                }
                if (TryGetResourceReaderBySuffix(normalizedPath, out reader))
                {
                    needSearch = false;
                    return CloneIfFileReader(reader, out shouldDispose);
                }
                if (!HasDirectory(normalizedPath)
                    && assetsFile.assetsManager.resourceFileReaders.TryGetValue(resourceFileName, out reader)
                    && CanReadRange(reader))
                {
                    needSearch = false;
                    return CloneIfFileReader(reader, out shouldDispose);
                }

                var resourceFilePath = ResolveResourceFilePath(resourceFileName, normalizedPath);
                if (resourceFilePath != null)
                {
                    var resolvedKey = NormalizePath(Path.GetFullPath(resourceFilePath));
                    needSearch = false;
                    var fileReader = new FileReader(resourceFilePath);
                    if (!HasDirectory(normalizedPath) && !assetsFile.assetsManager.resourceFileReaders.ContainsKey(resourceFileName))
                    {
                        assetsFile.assetsManager.RegisterResourceFileReader(fileReader, normalizedPath, resolvedKey, resourceFileName);
                    }
                    else
                    {
                        assetsFile.assetsManager.RegisterResourceFileReader(fileReader, normalizedPath, resolvedKey);
                    }
                    reader = fileReader;
                    return CloneIfFileReader(reader, out shouldDispose);
                }
                throw new FileNotFoundException($"Can't find the resource file {path}");
            }
            else
            {
                if (isEmbedded && assetsFile != null)
                {
                    shouldDispose = true;
                    return assetsFile.reader.Clone();
                }
                return CloneIfFileReader(reader, out shouldDispose);
            }
        }

        private bool TryGetResourceReaderBySuffix(string normalizedPath, out BinaryReader reader)
        {
            reader = null;
            if (string.IsNullOrEmpty(normalizedPath) || assetsFile?.assetsManager == null)
            {
                return false;
            }

            BinaryReader matchedReader = null;
            foreach (var pair in assetsFile.assetsManager.resourceFileReaders)
            {
                var key = NormalizePath(pair.Key);
                if (!key.EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) || !CanReadRange(pair.Value))
                {
                    continue;
                }

                if (matchedReader == null || ReferenceEquals(matchedReader, pair.Value))
                {
                    matchedReader = pair.Value;
                    continue;
                }

                return false;
            }

            reader = matchedReader;
            return reader != null;
        }

        private static BinaryReader CloneIfFileReader(BinaryReader binaryReader, out bool shouldDispose)
        {
            if (binaryReader is FileReader fileReader)
            {
                shouldDispose = true;
                return fileReader.Clone();
            }

            shouldDispose = false;
            return binaryReader;
        }

        private string ResolveResourceFilePath(string resourceFileName, string normalizedPath)
        {
            var searchRoots = GetSearchRoots();
            if (searchRoots.Length == 0 || string.IsNullOrEmpty(normalizedPath))
            {
                return null;
            }

            bool hasValidFileName = !string.IsNullOrEmpty(resourceFileName)
                && resourceFileName != "."
                && resourceFileName != "..";

            // Confine rooted paths strictly inside the allowed search roots to block arbitrary host file reads.
            if (Path.IsPathRooted(normalizedPath))
            {
                if (IsPathWithinAnyRoot(normalizedPath, searchRoots))
                {
                    if (File.Exists(normalizedPath))
                    {
                        return Path.GetFullPath(normalizedPath);
                    }

                    var dir = Path.GetDirectoryName(normalizedPath);
                    if (hasValidFileName && dir != null && Directory.Exists(dir))
                    {
                        var matchedFile = Directory.EnumerateFiles(dir)
                            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), resourceFileName, StringComparison.OrdinalIgnoreCase));
                        if (matchedFile != null && IsPathWithinAnyRoot(matchedFile, searchRoots))
                        {
                            return Path.GetFullPath(matchedFile);
                        }
                    }
                }
            }

            foreach (var root in searchRoots)
            {
                if (!Path.IsPathRooted(normalizedPath))
                {
                    var directPath = Path.Combine(root, normalizedPath);
                    if (IsPathWithinRoot(directPath, root))
                    {
                        if (File.Exists(directPath))
                        {
                            return Path.GetFullPath(directPath);
                        }

                        var directDir = Path.GetDirectoryName(directPath);
                        if (directDir != null && Directory.Exists(directDir))
                        {
                            var fileToFind = Path.GetFileName(directPath);
                            var matchedFile = Directory.EnumerateFiles(directDir)
                                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileToFind, StringComparison.OrdinalIgnoreCase));
                            if (matchedFile != null && IsPathWithinRoot(matchedFile, root))
                            {
                                return Path.GetFullPath(matchedFile);
                            }
                        }
                    }
                }

                if (!hasValidFileName)
                {
                    continue;
                }

                var resourceFilePath = Path.Combine(root, resourceFileName);
                if (IsPathWithinRoot(resourceFilePath, root) && File.Exists(resourceFilePath))
                {
                    return Path.GetFullPath(resourceFilePath);
                }

                if (Directory.Exists(root))
                {
                    var matchedFile = Directory.EnumerateFiles(root)
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), resourceFileName, StringComparison.OrdinalIgnoreCase));
                    if (matchedFile != null && IsPathWithinRoot(matchedFile, root))
                    {
                        return Path.GetFullPath(matchedFile);
                    }

                    var findFiles = ImportHelper.GetFilesSafe(root, "*", true)
                        .Where(f => string.Equals(Path.GetFileName(f), resourceFileName, StringComparison.OrdinalIgnoreCase)
                            && IsPathWithinRoot(f, root))
                        .ToArray();

                    var suffixMatches = findFiles
                        .Where(x => NormalizePath(x).EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (suffixMatches.Length == 1)
                    {
                        return Path.GetFullPath(suffixMatches[0]);
                    }
                    if (suffixMatches.Length > 1)
                    {
                        throw new IOException($"Found multiple resource files matching {path}");
                    }
                    if (findFiles.Length == 1)
                    {
                        return Path.GetFullPath(findFiles[0]);
                    }
                }
            }

            // Fallback for renamed companion files (like level77 + level77.resS / level77.assets.resS)
            var assetsFileDir = Path.GetDirectoryName(assetsFile.fullName);
            if (hasValidFileName && !string.IsNullOrEmpty(assetsFileDir))
            {
                var assetsFileNameWithoutExt = Path.GetFileNameWithoutExtension(assetsFile.fullName);
                var resourceExtension = Path.GetExtension(resourceFileName); // e.g. ".resS" or ".resource"
                if (!string.IsNullOrEmpty(resourceExtension))
                {
                    string[] possibleNames = {
                        assetsFileNameWithoutExt + resourceExtension,
                        assetsFileNameWithoutExt + ".assets" + resourceExtension,
                        assetsFileNameWithoutExt + (resourceExtension.Equals(".resS", StringComparison.OrdinalIgnoreCase) ? ".resource" : ".resS"),
                        assetsFileNameWithoutExt + ".assets" + (resourceExtension.Equals(".resS", StringComparison.OrdinalIgnoreCase) ? ".resource" : ".resS")
                    };

                    foreach (var name in possibleNames)
                    {
                        var companionPath = Path.Combine(assetsFileDir, name);
                        if (IsPathWithinAnyRoot(companionPath, searchRoots) && File.Exists(companionPath))
                        {
                            return Path.GetFullPath(companionPath);
                        }
                    }
                }
            }

            return null;
        }

        private string[] GetSearchRoots()
        {
            var candidates = new[]
            {
                Path.GetDirectoryName(assetsFile.fullName),
                Path.GetDirectoryName(assetsFile.originalPath),
                assetsFile.assetsManager?.ProjectRoot
            };

            return candidates
                .Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x))
                .Select(x =>
                {
                    try { return Path.GetFullPath(x); }
                    catch { return null; }
                })
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsPathWithinAnyRoot(string candidatePath, string[] roots)
        {
            foreach (var root in roots)
            {
                if (IsPathWithinRoot(candidatePath, root))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPathWithinRoot(string candidatePath, string rootDir)
        {
            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(rootDir))
            {
                return false;
            }

            try
            {
                var fullCandidate = Path.GetFullPath(candidatePath);
                var fullRoot = Path.GetFullPath(rootDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return fullCandidate.StartsWith(fullRoot, comparison);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string value)
        {
            return value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private static string NormalizeResourcePath(string value)
        {
            if (value == null)
            {
                return null;
            }

            const string archivePrefix = "archive:/";
            if (value.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(archivePrefix.Length);
                var slash = value.IndexOf('/');
                if (slash >= 0)
                {
                    value = value.Substring(slash + 1);
                }
            }

            return value;
        }

        private static bool HasDirectory(string value)
        {
            return !string.IsNullOrEmpty(Path.GetDirectoryName(value));
        }

        private bool CanReadRange(BinaryReader binaryReader)
        {
            try
            {
                return offset >= 0 && size >= 0 && binaryReader.BaseStream.Length >= offset + size;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static int ReadExact(BinaryReader binaryReader, byte[] buffer, int bufferOffset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = binaryReader.Read(buffer, bufferOffset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }
            return totalRead;
        }

        private static bool ShouldApplyXorInversionHeuristic(ReadOnlySpan<byte> data)
        {
            int sampleSize = Math.Min(data.Length, 1024);
            if (sampleSize == 0)
            {
                return false;
            }

            int count01 = 0;
            for (int i = 0; i < sampleSize; i++)
            {
                if (data[i] == 0x01)
                {
                    count01++;
                }
            }

            return count01 * 2 > sampleSize;
        }

        private static void ApplyXorInversionInPlace(Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= 0xFF;
            }
        }

        public byte[] GetData()
        {
            if (rawData != null)
            {
                var copy = new byte[size];
                Buffer.BlockCopy(rawData, 0, copy, 0, (int)size);
                return copy;
            }

            var binaryReader = GetReader(out var shouldDispose);
            try
            {
                binaryReader.BaseStream.Position = offset;
                var data = binaryReader.ReadBytes((int)size);
                if (data.Length != size)
                {
                    throw new EndOfStreamException($"Unable to read {size} bytes from resource {path} at offset {offset}. Read {data.Length} bytes.");
                }

                if (EnableXorInversionHeuristic && ShouldApplyXorInversionHeuristic(data))
                {
                    ApplyXorInversionInPlace(data);
                }

                return data;
            }
            finally
            {
                if (shouldDispose)
                {
                    binaryReader.Dispose();
                }
            }
        }

        public void GetData(byte[] buff)
        {
            if (buff == null)
            {
                throw new ArgumentNullException(nameof(buff));
            }
            if (size < 0 || size > buff.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(buff), $"Destination buffer ({buff.Length} bytes) is smaller than resource size ({size} bytes).");
            }

            int toRead = (int)size;
            if (rawData != null)
            {
                Buffer.BlockCopy(rawData, 0, buff, 0, toRead);
                return;
            }

            var binaryReader = GetReader(out var shouldDispose);
            try
            {
                binaryReader.BaseStream.Position = offset;
                var read = ReadExact(binaryReader, buff, 0, toRead);
                if (read != toRead)
                {
                    throw new EndOfStreamException($"Unable to read {size} bytes from resource {path} at offset {offset}. Read {read} bytes.");
                }

                // Inspect and transform only the 'toRead' bytes actually read — buff may be oversized from BigArrayPool.
                var payloadSpan = buff.AsSpan(0, toRead);
                if (EnableXorInversionHeuristic && ShouldApplyXorInversionHeuristic(payloadSpan))
                {
                    ApplyXorInversionInPlace(payloadSpan);
                }
            }
            finally
            {
                if (shouldDispose)
                {
                    binaryReader.Dispose();
                }
            }
        }

        public ReadSession OpenReadSession()
        {
            if (rawData != null)
            {
                return new ReadSession(rawData, size, path);
            }

            var binaryReader = GetReader(out var shouldDispose);
            return new ReadSession(binaryReader, shouldDispose, offset, size, path);
        }

        public void ReadSegment(long segmentOffset, byte[] buffer, int bufferOffset, int count)
        {
            using (var session = OpenReadSession())
            {
                session.ReadSegment(segmentOffset, buffer, bufferOffset, count);
            }
        }

        public sealed class ReadSession : IDisposable
        {
            private readonly byte[] rawData;
            private BinaryReader binaryReader;
            private readonly bool shouldDispose;
            private readonly long baseOffset;
            private readonly long size;
            private readonly string resourcePath;
            private bool disposed;

            public long Size => size;

            internal ReadSession(byte[] rawData, long size, string resourcePath)
            {
                this.rawData = rawData;
                this.size = size;
                this.resourcePath = resourcePath;
            }

            internal ReadSession(BinaryReader binaryReader, bool shouldDispose, long baseOffset, long size, string resourcePath)
            {
                this.binaryReader = binaryReader ?? throw new ArgumentNullException(nameof(binaryReader));
                this.shouldDispose = shouldDispose;
                this.baseOffset = baseOffset;
                this.size = size;
                this.resourcePath = resourcePath;
            }

            public int ReadSegment(long segmentOffset, byte[] buff, int buffOffset, int count)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(ReadSession));
                }
                if (buff == null)
                {
                    throw new ArgumentNullException(nameof(buff));
                }
                if (buffOffset < 0 || count < 0 || buffOffset > buff.Length - count)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }
                if (segmentOffset < 0 || size < count || segmentOffset > size - count)
                {
                    throw new EndOfStreamException($"Unable to read {count} bytes from resource {resourcePath} at segment offset {segmentOffset} (size {size}).");
                }
                if (count == 0)
                {
                    return 0;
                }

                if (rawData != null)
                {
                    Buffer.BlockCopy(rawData, (int)segmentOffset, buff, buffOffset, count);
                    return count;
                }

                var targetPosition = baseOffset + segmentOffset;
                var stream = binaryReader.BaseStream;
                if (stream.Position != targetPosition)
                {
                    stream.Position = targetPosition;
                }

                var read = ReadExact(binaryReader, buff, buffOffset, count);
                if (read != count)
                {
                    throw new EndOfStreamException($"Unable to read {count} bytes from resource {resourcePath} at segment offset {segmentOffset}. Read {read} bytes.");
                }
                return read;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                if (shouldDispose)
                {
                    binaryReader?.Dispose();
                }
                binaryReader = null;
            }
        }

        public void WriteData(string path)
        {
            if (rawData != null)
            {
                using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    writer.Write(rawData, 0, (int)size);
                }
                return;
            }

            var binaryReader = GetReader(out var shouldDispose);
            try
            {
                binaryReader.BaseStream.Position = offset;
                using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    binaryReader.BaseStream.CopyTo(writer, size);
                }
            }
            finally
            {
                if (shouldDispose)
                {
                    binaryReader.Dispose();
                }
            }
        }
    }
}
