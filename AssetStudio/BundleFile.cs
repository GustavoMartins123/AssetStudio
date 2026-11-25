#nullable enable
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace AssetStudio
{
    [Flags]
    public enum ArchiveFlags
    {
        CompressionTypeMask = 0x3f,
        BlocksAndDirectoryInfoCombined = 0x40,
        BlocksInfoAtTheEnd = 0x80,
        OldWebPluginCompatibility = 0x100,
        BlockInfoNeedPaddingAtStart = 0x200
    }

    [Flags]
    public enum StorageBlockFlags
    {
        CompressionTypeMask = 0x3f,
        Streamed = 0x40
    }

    public enum CompressionType
    {
        None,
        Lzma,
        Lz4,
        Lz4HC,
        Lzham
    }

    public class BundleFile : IDisposable
    {
        public static bool LowMemoryMode { get; set; } = true;
        public static long LowMemoryThreshold { get; set; } = 200L * 1024 * 1024;
        public static string TemporaryDirectory { get; set; } = "";
        public static string CacheDirectory { get; set; } = "";
        public const int MaxUncompressedBlocksInfoSize = 128 * 1024 * 1024; // 128 MiB safety cap
        public const int MinParallelBlockCount = 4;
        private const int PersistentCacheSampleBytes = 1024 * 1024;
        private const long PersistentCacheFullSignatureThreshold = 8L * 1024 * 1024;
        private const ulong PersistentCacheHashSeed = 0x9E3779B97F4A7C15UL;

        public class Header
        {
            public string signature = "";
            public uint version;
            public string unityVersion = "";
            public string unityRevision = "";
            public long size;
            public uint compressedBlocksInfoSize;
            public uint uncompressedBlocksInfoSize;
            public ArchiveFlags flags;
        }

        public class StorageBlock
        {
            public uint compressedSize;
            public uint uncompressedSize;
            public StorageBlockFlags flags;
        }

        public class Node
        {
            public long offset;
            public long size;
            public uint flags;
            public string path = "";
        }

        public Header m_Header = new Header();
        private StorageBlock[] m_BlocksInfo = Array.Empty<StorageBlock>();
        private Node[] m_DirectoryInfo = Array.Empty<Node>();
        private readonly bool allowParallelDecompression;
        private readonly long? lowMemoryThresholdOverride;

        public StreamFile[] fileList = Array.Empty<StreamFile>();
        public Stream? BlocksStream { get; set; }
        public bool LoadedFromDecompressionCache { get; private set; }
        public string? CachedBlocksPath { get; private set; }
        public bool UsedParallelDecompression { get; private set; }
        public bool UsedPartialDecompression { get; private set; }
        public int DecompressedBlockCount { get; private set; }

        public BundleFile(FileReader reader)
            : this(reader, null, allowParallelDecompression: true, lowMemoryThresholdOverride: null, serializedFilesOnly: false)
        {
        }

        public BundleFile(FileReader reader, DecompressedBundleCache? decompressionCache)
            : this(reader, decompressionCache, allowParallelDecompression: true, lowMemoryThresholdOverride: null, serializedFilesOnly: false)
        {
        }

        public BundleFile(
            FileReader reader,
            DecompressedBundleCache? decompressionCache,
            bool allowParallelDecompression,
            long? lowMemoryThresholdOverride = null,
            bool serializedFilesOnly = false)
        {
            this.allowParallelDecompression = allowParallelDecompression;
            this.lowMemoryThresholdOverride = lowMemoryThresholdOverride;
            m_Header = new Header();
            m_Header.signature = reader.ReadStringToNull();
            m_Header.version = reader.ReadUInt32();
            m_Header.unityVersion = reader.ReadStringToNull();
            m_Header.unityRevision = reader.ReadStringToNull();
            switch (m_Header.signature)
            {
                case "UnityArchive":
                    throw new NotSupportedException("UnityArchive format is not supported.");
                case "UnityWeb":
                case "UnityRaw":
                    {
                        if (m_Header.version == 6)
                        {
                            goto case "UnityFS";
                        }
                        ReadHeaderAndBlocksInfo(reader);
                        if (decompressionCache != null
                            && HasCompressedBlocks()
                            && !string.IsNullOrEmpty(reader.FullPath)
                            && File.Exists(reader.FullPath))
                        {
                            var uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
                            var cacheEntry = decompressionCache.GetOrExtractBlocks(
                                reader.FullPath,
                                uncompressedSizeSum,
                                tempStream => ReadBlocksAndDirectory(reader, tempStream));
                            if (cacheEntry.FromCache)
                            {
                                using var cachedStream = new FileStream(
                                    cacheEntry.BlocksFilePath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete);
                                ReadDirectoryInfoOnly(cachedStream);
                            }
                            LoadedFromDecompressionCache = cacheEntry.FromCache;
                            CachedBlocksPath = cacheEntry.BlocksFilePath;
                            ReadFilesFromDiskPath(cacheEntry.BlocksFilePath, cacheEntry.UncompressedSize);
                            break;
                        }
                        var blocksStream = CreateBlocksStream(reader.FullPath, out var isCached, out var tempCachePath, out var targetCachePath);
                        try
                        {
                            if (!isCached)
                            {
                                ReadBlocksAndDirectory(reader, blocksStream);
                                if (!string.IsNullOrEmpty(targetCachePath) && !string.IsNullOrEmpty(tempCachePath))
                                {
                                    blocksStream.Flush();
                                    blocksStream.Dispose();

                                    bool useExisting = false;
                                    try
                                    {
                                        if (File.Exists(targetCachePath))
                                        {
                                            useExisting = true;
                                        }
                                        else
                                        {
                                            File.Move(tempCachePath, targetCachePath);
                                        }
                                    }
                                    catch (IOException)
                                    {
                                        if (File.Exists(targetCachePath))
                                        {
                                            useExisting = true;
                                        }
                                        else
                                        {
                                            throw;
                                        }
                                    }

                                    if (useExisting)
                                    {
                                        try
                                        {
                                            File.Delete(tempCachePath);
                                        }
                                        catch
                                        {
                                            // Ignore failure to delete temp file
                                        }
                                    }

                                    blocksStream = new FileStream(targetCachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                }
                            }
                            else
                            {
                                ReadDirectoryInfoOnly(blocksStream);
                            }
                            ReadFiles(blocksStream, reader.FullPath);
                        }
                        catch
                        {
                            blocksStream.Dispose();
                            if (!isCached && !string.IsNullOrEmpty(tempCachePath))
                            {
                                try
                                {
                                    if (File.Exists(tempCachePath))
                                    {
                                        File.Delete(tempCachePath);
                                    }
                                }
                                catch
                                {
                                    // Ignore cleanup errors
                                }
                            }
                            throw;
                        }
                        if (blocksStream is MemoryStream)
                        {
                            blocksStream.Dispose();
                        }
                        else
                        {
                            BlocksStream = blocksStream;
                        }
                        break;
                    }
                case "UnityFS":
                    {
                        ReadHeader(reader);
                        ReadBlocksInfoAndDirectory(reader);
                        if (serializedFilesOnly && TryReadSerializedNodesPartial(reader))
                        {
                            break;
                        }
                        if (decompressionCache != null
                            && HasCompressedBlocks()
                            && !string.IsNullOrEmpty(reader.FullPath)
                            && File.Exists(reader.FullPath))
                        {
                            var uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
                            var cachedNodes = BuildCachedNodes(m_DirectoryInfo);
                            var cacheEntry = decompressionCache.GetOrExtractBlocks(
                                reader.FullPath,
                                uncompressedSizeSum,
                                tempStream => ReadBlocks(reader, tempStream),
                                cachedNodes);
                            LoadedFromDecompressionCache = cacheEntry.FromCache;
                            CachedBlocksPath = cacheEntry.BlocksFilePath;
                            ReadFilesFromDiskPath(cacheEntry.BlocksFilePath, cacheEntry.UncompressedSize);
                            break;
                        }
                        var blocksStream = CreateBlocksStream(reader.FullPath, out var isCached, out var tempCachePath, out var targetCachePath);
                        try
                        {
                            if (!isCached)
                            {
                                ReadBlocks(reader, blocksStream);
                                if (!string.IsNullOrEmpty(targetCachePath) && !string.IsNullOrEmpty(tempCachePath))
                                {
                                    blocksStream.Flush();
                                    blocksStream.Dispose();

                                    bool useExisting = false;
                                    try
                                    {
                                        if (File.Exists(targetCachePath))
                                        {
                                            useExisting = true;
                                        }
                                        else
                                        {
                                            File.Move(tempCachePath, targetCachePath);
                                        }
                                    }
                                    catch (IOException)
                                    {
                                        if (File.Exists(targetCachePath))
                                        {
                                            useExisting = true;
                                        }
                                        else
                                        {
                                            throw;
                                        }
                                    }

                                    if (useExisting)
                                    {
                                        try
                                        {
                                            File.Delete(tempCachePath);
                                        }
                                        catch
                                        {
                                            // Ignore failure to delete temp file
                                        }
                                    }

                                    blocksStream = new FileStream(targetCachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                }
                            }
                            ReadFiles(blocksStream, reader.FullPath);
                        }
                        catch
                        {
                            blocksStream.Dispose();
                            if (!isCached && !string.IsNullOrEmpty(tempCachePath))
                            {
                                try
                                {
                                    if (File.Exists(tempCachePath))
                                    {
                                        File.Delete(tempCachePath);
                                    }
                                }
                                catch
                                {
                                    // Ignore cleanup errors
                                }
                            }
                            throw;
                        }
                        if (blocksStream is MemoryStream)
                        {
                            blocksStream.Dispose();
                        }
                        else
                        {
                            BlocksStream = blocksStream;
                        }
                        break;
                    }
                default:
                    throw new NotSupportedException($"Unsupported bundle signature '{m_Header.signature}'.");
            }
        }

        private bool HasCompressedBlocks()
        {
            if (m_Header.signature == "UnityWeb")
            {
                return true;
            }

            if (m_BlocksInfo == null)
            {
                return false;
            }

            for (int i = 0; i < m_BlocksInfo.Length; i++)
            {
                var compressionType = (CompressionType)(m_BlocksInfo[i].flags & StorageBlockFlags.CompressionTypeMask);
                if (compressionType != CompressionType.None)
                {
                    return true;
                }
            }

            return false;
        }

        private static CachedBundleNode[] BuildCachedNodes(Node[] directoryInfo)
        {
            var result = new CachedBundleNode[directoryInfo.Length];
            for (int i = 0; i < directoryInfo.Length; i++)
            {
                var node = directoryInfo[i];
                result[i] = new CachedBundleNode
                {
                    Path = node.path,
                    Offset = node.offset,
                    Size = node.size,
                    Flags = node.flags
                };
            }

            return result;
        }

        private void ReadFilesFromDiskPath(string cachedBlocksPath, long blocksLength)
        {
            fileList = DecompressedBundleCache.CreateStreamFiles(cachedBlocksPath, blocksLength, m_DirectoryInfo);
        }

        private void ReadHeaderAndBlocksInfo(EndianBinaryReader reader)
        {
            if (m_Header.version >= 4)
            {
                var hash = reader.ReadBytes(16);
                var crc = reader.ReadUInt32();
            }
            var minimumStreamedBytes = reader.ReadUInt32();
            m_Header.size = reader.ReadUInt32();
            var numberOfLevelsToDownloadBeforeStreaming = reader.ReadUInt32();
            var levelCount = reader.ReadInt32();
            if (levelCount < 0 || (long)levelCount * 8 > reader.BaseStream.Length - reader.Position)
            {
                throw new InvalidDataException($"Invalid bundle level count {levelCount}.");
            }
            m_BlocksInfo = levelCount > 0 ? new StorageBlock[1] : Array.Empty<StorageBlock>();
            for (int i = 0; i < levelCount; i++)
            {
                var storageBlock = new StorageBlock()
                {
                    compressedSize = reader.ReadUInt32(),
                    uncompressedSize = reader.ReadUInt32(),
                };
                if (i == levelCount - 1)
                {
                    m_BlocksInfo[0] = storageBlock;
                }
            }
            if (m_Header.version >= 2)
            {
                var completeFileSize = reader.ReadUInt32();
            }
            if (m_Header.version >= 3)
            {
                var fileInfoHeaderSize = reader.ReadUInt32();
            }
            reader.Position = m_Header.size;
        }

        private Stream CreateBlocksStream(string path, out bool isCached, out string? tempCachePath, out string? targetCachePath)
        {
            isCached = false;
            tempCachePath = null;
            targetCachePath = null;
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
            // Virtual paths (e.g. the second bundle stored inside one file) have no on-disk identity to key a cache entry on.
            if (!string.IsNullOrEmpty(CacheDirectory) && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    var fileInfo = new FileInfo(path);
                    var cachedFileName = CreatePersistentBlocksCacheFileName(path, fileInfo.Length);
                    var cachedFilePath = Path.Combine(CacheDirectory, cachedFileName);

                    Directory.CreateDirectory(CacheDirectory);

                    if (File.Exists(cachedFilePath))
                    {
                        var cacheFileInfo = new FileInfo(cachedFilePath);
                        if (cacheFileInfo.Length == uncompressedSizeSum)
                        {
                            isCached = true;
                            return new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        }
                        else
                        {
                            try
                            {
                                File.Delete(cachedFilePath);
                            }
                            catch
                            {
                                // Ignore if locked
                            }
                        }
                    }

                    targetCachePath = cachedFilePath;
                    tempCachePath = cachedFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    return new FileStream(tempCachePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to use persistent cache for {path}: {ex.Message}. Falling back to default stream.");
                }
            }

            Stream blocksStream;
            if (ShouldUseTemporaryStream(uncompressedSizeSum, lowMemoryThresholdOverride))
            {
                /*var memoryMappedFile = MemoryMappedFile.CreateNew(null, uncompressedSizeSum);
                assetsDataStream = memoryMappedFile.CreateViewStream();*/
                blocksStream = CreateTemporaryStream(path, "blocks");
            }
            else
            {
                blocksStream = new MemoryStream((int)uncompressedSizeSum);
            }
            return blocksStream;
        }

        private void ReadDirectoryInfoOnly(Stream blocksStream)
        {
            blocksStream.Position = 0;
            var blocksReader = new EndianBinaryReader(blocksStream);
            var nodesCount = blocksReader.ReadInt32();
            if (nodesCount < 0 || (long)nodesCount * 9 > blocksStream.Length - blocksStream.Position)
            {
                throw new InvalidDataException($"Invalid bundle directory node count {nodesCount}.");
            }
            m_DirectoryInfo = new Node[nodesCount];
            for (int i = 0; i < nodesCount; i++)
            {
                m_DirectoryInfo[i] = new Node
                {
                    path = blocksReader.ReadStringToNull(),
                    offset = blocksReader.ReadUInt32(),
                    size = blocksReader.ReadUInt32()
                };
            }
        }

        internal static bool ShouldUseTemporaryStream(long size, long? thresholdOverride = null)
        {
            long threshold = thresholdOverride ?? LowMemoryThreshold;
            return size >= int.MaxValue || ((LowMemoryMode || thresholdOverride.HasValue) && size >= threshold);
        }

        internal static FileStream CreateTemporaryStream(string sourcePath, string kind)
        {
            var tempDirectory = GetTemporaryDirectory();
            Directory.CreateDirectory(tempDirectory);
            var fileName = $"{SanitizeTempFilePart(Path.GetFileName(sourcePath))}.{SanitizeTempFilePart(kind)}.{Guid.NewGuid():N}.tmp";
            var tempPath = Path.Combine(tempDirectory, fileName);
            return new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.DeleteOnClose);
        }

        private static string GetTemporaryDirectory()
        {
            if (!string.IsNullOrWhiteSpace(TemporaryDirectory))
            {
                return TemporaryDirectory;
            }

            var environmentPath = Environment.GetEnvironmentVariable("ASSETSTUDIO_TEMP_DIR");
            if (!string.IsNullOrWhiteSpace(environmentPath))
            {
                return environmentPath;
            }

            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localData))
            {
                return Path.Combine(localData, "AssetStudio", "Temp");
            }

            return Path.Combine(Path.GetTempPath(), "AssetStudio");
        }

        private static string SanitizeTempFilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "bundle";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (invalidChars.Contains(chars[i]))
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        private static string CreatePersistentBlocksCacheFileName(string path, long sourceLength)
        {
            var sourceSignature = ComputeSourceContentSignature(path, sourceLength);
            return $"{SanitizeTempFilePart(Path.GetFileName(path))}_{sourceLength}_{sourceSignature}.blocks";
        }

        public static string ComputeSourceContentSignature(string path, long sourceLength)
        {
            var hash = FastHash64.Combine(PersistentCacheHashSeed, (ulong)sourceLength);
            var buffer = ArrayPool<byte>.Shared.Rent(PersistentCacheSampleBytes);
            var fileOptions = sourceLength <= PersistentCacheFullSignatureThreshold
                ? FileOptions.SequentialScan
                : FileOptions.RandomAccess;
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                fileOptions);
            try
            {
                if (sourceLength <= PersistentCacheFullSignatureThreshold)
                {
                    hash = AddSample(hash, handle, buffer, 0, sourceLength);
                }
                else
                {
                    hash = AddSample(hash, handle, buffer, 0, PersistentCacheSampleBytes);
                    hash = AddSample(
                        hash,
                        handle,
                        buffer,
                        Math.Max(0, sourceLength - PersistentCacheSampleBytes),
                        PersistentCacheSampleBytes);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return "s1_" + hash.ToString("x16");
        }

        private static ulong AddSample(ulong hash, SafeFileHandle handle, byte[] buffer, long offset, long bytesToRead)
        {
            if (bytesToRead <= 0)
            {
                return hash;
            }

            var currentOffset = offset;
            var remaining = bytesToRead;
            hash = FastHash64.Combine(hash, (ulong)offset);
            while (remaining > 0)
            {
                var readSize = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = RandomAccess.Read(handle, buffer.AsSpan(0, readSize), currentOffset);
                if (bytesRead <= 0)
                {
                    break;
                }

                hash = FastHash64.Hash(buffer.AsSpan(0, bytesRead), hash);
                currentOffset += bytesRead;
                remaining -= bytesRead;
            }

            return hash;
        }

        private static void ReadExact(EndianBinaryReader reader, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = reader.Read(buffer, totalRead, count - totalRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of bundle stream: expected {count} bytes, read {totalRead} bytes.");
                }
                totalRead += read;
            }
        }

        private void ReadBlocksAndDirectory(EndianBinaryReader reader, Stream blocksStream)
        {
            var isCompressed = m_Header.signature == "UnityWeb";
            foreach (var blockInfo in m_BlocksInfo)
            {
                if (blockInfo.compressedSize > int.MaxValue)
                {
                    throw new InvalidDataException($"Bundle block compressed size {blockInfo.compressedSize} exceeds maximum buffer size.");
                }
                var compressedSize = (int)blockInfo.compressedSize;
                if (isCompressed)
                {
                    var compressedBytes = BigArrayPool<byte>.Shared.Rent(compressedSize);
                    try
                    {
                        ReadExact(reader, compressedBytes, compressedSize);
                        using (var memoryStream = new MemoryStream(compressedBytes, 0, compressedSize))
                        {
                            using (var decompressStream = SevenZipHelper.StreamDecompress(memoryStream))
                            {
                                decompressStream.CopyTo(blocksStream);
                            }
                        }
                    }
                    finally
                    {
                        BigArrayPool<byte>.Shared.Return(compressedBytes);
                    }
                }
                else
                {
                    var buffer = BigArrayPool<byte>.Shared.Rent(compressedSize);
                    try
                    {
                        ReadExact(reader, buffer, compressedSize);
                        blocksStream.Write(buffer, 0, compressedSize);
                    }
                    finally
                    {
                        BigArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            blocksStream.Position = 0;
            var blocksReader = new EndianBinaryReader(blocksStream);
            var nodesCount = blocksReader.ReadInt32();
            if (nodesCount < 0 || (long)nodesCount * 9 > blocksStream.Length - blocksStream.Position)
            {
                throw new InvalidDataException($"Invalid bundle directory node count {nodesCount}.");
            }
            m_DirectoryInfo = new Node[nodesCount];
            for (int i = 0; i < nodesCount; i++)
            {
                m_DirectoryInfo[i] = new Node
                {
                    path = blocksReader.ReadStringToNull(),
                    offset = blocksReader.ReadUInt32(),
                    size = blocksReader.ReadUInt32()
                };
            }
        }

        public void ReadFiles(Stream blocksStream, string path)
        {
            var streamLength = blocksStream.Length;
            foreach (var node in m_DirectoryInfo)
            {
                if (node.offset < 0 || node.size < 0 || node.offset > streamLength - node.size)
                {
                    throw new InvalidDataException($"Invalid bundle node '{node.path}' range (offset {node.offset}, size {node.size}, stream length {streamLength}).");
                }
            }

            fileList = new StreamFile[m_DirectoryInfo.Length];
            if (blocksStream is MemoryStream memStream)
            {
                byte[] sharedBuffer;
                try
                {
                    sharedBuffer = memStream.GetBuffer();
                }
                catch (UnauthorizedAccessException)
                {
                    sharedBuffer = memStream.ToArray();
                }

                for (int i = 0; i < m_DirectoryInfo.Length; i++)
                {
                    var node = m_DirectoryInfo[i];
                    if (node.offset > int.MaxValue || node.size > int.MaxValue || node.offset > sharedBuffer.Length - node.size)
                    {
                        throw new InvalidDataException($"Bundle node '{node.path}' exceeds in-memory buffer bounds.");
                    }
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = node.path;
                    file.fileName = Path.GetFileName(node.path.Replace('\\', '/'));
                    
                    // Directly wrap the shared buffer! Zero memory copy, zero allocation!
                    file.stream = new MemoryStream(sharedBuffer, (int)node.offset, (int)node.size, writable: false, publiclyVisible: true);
                }
            }
            else if (blocksStream is FileStream fileStream)
            {
                var tempFilePath = fileStream.Name;
                using var sharedHandle = new SharedFileHandle(tempFilePath);
                for (int i = 0; i < m_DirectoryInfo.Length; i++)
                {
                    var node = m_DirectoryInfo[i];
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = node.path;
                    file.fileName = Path.GetFileName(node.path.Replace('\\', '/'));

                    // Directly wrap in a SubStream sharing one ref-counted SafeFileHandle!
                    // Zero disk copy! Zero extra temp files! Zero duplicate OS handles!
                    file.stream = new SubStream(sharedHandle, node.offset, node.size);
                }
            }
            else
            {
                // Fallback for custom streams
                for (int i = 0; i < m_DirectoryInfo.Length; i++)
                {
                    var node = m_DirectoryInfo[i];
                    if (node.size > int.MaxValue)
                    {
                        throw new InvalidDataException($"Bundle node '{node.path}' size {node.size} exceeds MemoryStream limit.");
                    }
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = node.path;
                    file.fileName = Path.GetFileName(node.path.Replace('\\', '/'));
                    file.stream = new MemoryStream((int)node.size);
                    
                    blocksStream.Position = node.offset;
                    blocksStream.CopyTo(file.stream, node.size);
                    file.stream.Position = 0;
                }
            }
        }

        private void ReadHeader(EndianBinaryReader reader)
        {
            m_Header.size = reader.ReadInt64();
            m_Header.compressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.uncompressedBlocksInfoSize = reader.ReadUInt32();
            m_Header.flags = (ArchiveFlags)reader.ReadUInt32();
            if (m_Header.signature != "UnityFS")
            {
                reader.ReadByte();
            }
        }

        private void ReadBlocksInfoAndDirectory(EndianBinaryReader reader)
        {
            byte[] blocksInfoBytes;
            if (m_Header.version >= 7)
            {
                reader.AlignStream(16);
            }
            if (m_Header.compressedBlocksInfoSize > int.MaxValue || m_Header.compressedBlocksInfoSize > reader.BaseStream.Length)
            {
                throw new InvalidDataException($"Invalid compressed blocks info size {m_Header.compressedBlocksInfoSize} (file length {reader.BaseStream.Length}).");
            }
            if (m_Header.uncompressedBlocksInfoSize > MaxUncompressedBlocksInfoSize)
            {
                throw new InvalidDataException($"Invalid uncompressed blocks info size {m_Header.uncompressedBlocksInfoSize} (exceeds safety limit of {MaxUncompressedBlocksInfoSize} bytes).");
            }
            var compressedBlocksInfoSize = (int)m_Header.compressedBlocksInfoSize;
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
            {
                var position = reader.Position;
                reader.Position = reader.BaseStream.Length - compressedBlocksInfoSize;
                blocksInfoBytes = reader.ReadBytes(compressedBlocksInfoSize);
                reader.Position = position;
            }
            else //0x40 BlocksAndDirectoryInfoCombined
            {
                if (compressedBlocksInfoSize > reader.BaseStream.Length - reader.Position)
                {
                    throw new EndOfStreamException($"Compressed blocks info size {compressedBlocksInfoSize} exceeds remaining bundle stream.");
                }
                blocksInfoBytes = reader.ReadBytes(compressedBlocksInfoSize);
            }
            if (blocksInfoBytes.Length != compressedBlocksInfoSize)
            {
                throw new EndOfStreamException($"Unexpected end of bundle while reading blocks info: expected {compressedBlocksInfoSize}, got {blocksInfoBytes.Length}.");
            }
            MemoryStream blocksInfoUncompresseddStream;
            var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
            var compressionType = (CompressionType)(m_Header.flags & ArchiveFlags.CompressionTypeMask);
            switch (compressionType)
            {
                case CompressionType.None:
                    {
                        blocksInfoUncompresseddStream = new MemoryStream(blocksInfoBytes);
                        break;
                    }
                case CompressionType.Lzma:
                    {
                        blocksInfoUncompresseddStream = new MemoryStream((int)(uncompressedSize));
                        using (var blocksInfoCompressedStream = new MemoryStream(blocksInfoBytes))
                        {
                            SevenZipHelper.StreamDecompress(blocksInfoCompressedStream, blocksInfoUncompresseddStream, m_Header.compressedBlocksInfoSize, m_Header.uncompressedBlocksInfoSize);
                        }
                        blocksInfoUncompresseddStream.Position = 0;
                        break;
                    }
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                    {
                        var uncompressedBytes = new byte[uncompressedSize];
                        var numWrite = LZ4Codec.Decode(blocksInfoBytes, uncompressedBytes);
                        if (numWrite != uncompressedSize)
                        {
                            throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                        }
                        blocksInfoUncompresseddStream = new MemoryStream(uncompressedBytes);
                        break;
                    }
                default:
                    {
                        if (!IsZstdFrame(blocksInfoBytes, blocksInfoBytes.Length))
                        {
                            throw new IOException($"Unsupported compression type {compressionType}");
                        }

                        var uncompressedBytes = new byte[uncompressedSize];
                        DecompressZstdExact(null, blocksInfoBytes, blocksInfoBytes.Length, uncompressedBytes, (int)uncompressedSize);
                        blocksInfoUncompresseddStream = new MemoryStream(uncompressedBytes);
                        break;
                    }
            }
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompresseddStream))
            {
                var uncompressedDataHash = blocksInfoReader.ReadBytes(16);
                var blocksInfoCount = blocksInfoReader.ReadInt32();
                if (blocksInfoCount < 0 || (long)blocksInfoCount * 10 > blocksInfoUncompresseddStream.Length - blocksInfoUncompresseddStream.Position)
                {
                    throw new InvalidDataException($"Invalid bundle blocks info count {blocksInfoCount}.");
                }
                m_BlocksInfo = new StorageBlock[blocksInfoCount];
                for (int i = 0; i < blocksInfoCount; i++)
                {
                    m_BlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = blocksInfoReader.ReadUInt32(),
                        compressedSize = blocksInfoReader.ReadUInt32(),
                        flags = (StorageBlockFlags)blocksInfoReader.ReadUInt16()
                    };
                }

                var nodesCount = blocksInfoReader.ReadInt32();
                if (nodesCount < 0 || (long)nodesCount * 21 > blocksInfoUncompresseddStream.Length - blocksInfoUncompresseddStream.Position)
                {
                    throw new InvalidDataException($"Invalid bundle directory node count {nodesCount}.");
                }
                m_DirectoryInfo = new Node[nodesCount];
                for (int i = 0; i < nodesCount; i++)
                {
                    m_DirectoryInfo[i] = new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull(),
                    };
                }
            }
            if ((m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                reader.AlignStream(16);
            }
        }

        private void ReadBlocks(EndianBinaryReader reader, Stream blocksStream)
        {
            DecompressedBlockCount = m_BlocksInfo.Length;
            if (allowParallelDecompression && TryReadBlocksParallel(reader, blocksStream))
            {
                UsedParallelDecompression = true;
                return;
            }

            UsedParallelDecompression = false;
            ReadBlocksSequential(reader, blocksStream);
        }

        private bool TryReadSerializedNodesPartial(EndianBinaryReader reader)
        {
            if (m_DirectoryInfo == null || m_DirectoryInfo.Length == 0 || m_BlocksInfo == null || m_BlocksInfo.Length == 0)
            {
                return false;
            }

            if (!reader.BaseStream.CanSeek)
            {
                return false;
            }

            if (!TryBuildBlockOffsets(
                m_BlocksInfo,
                out var compressedOffsets,
                out var uncompressedOffsets,
                out _,
                out var totalUncompressedSize))
            {
                return false;
            }

            if (totalUncompressedSize <= 0)
            {
                return false;
            }

            // Select SerializedFile nodes (flag 0x4). If no node carries flag 0x4, fall back to
            // excluding known streaming resource extensions (.resS / .resource).
            bool anyFlag4 = false;
            for (int i = 0; i < m_DirectoryInfo.Length; i++)
            {
                if ((m_DirectoryInfo[i].flags & 4u) != 0)
                {
                    anyFlag4 = true;
                    break;
                }
            }

            var selectedNodes = new System.Collections.Generic.List<Node>(m_DirectoryInfo.Length);
            long totalSelectedBytes = 0;
            for (int i = 0; i < m_DirectoryInfo.Length; i++)
            {
                var node = m_DirectoryInfo[i];
                bool include = anyFlag4
                    ? (node.flags & 4u) != 0
                    : !IsStreamingResourceNodePath(node.path);

                if (!include)
                {
                    continue;
                }

                if (node.offset < 0 || node.size < 0 || node.offset > totalUncompressedSize - node.size || node.size > int.MaxValue)
                {
                    return false;
                }

                totalSelectedBytes = checked(totalSelectedBytes + node.size);
                selectedNodes.Add(node);
            }

            if (selectedNodes.Count == 0 || selectedNodes.Count == m_DirectoryInfo.Length)
            {
                return false;
            }

            // Avoid exceeding the low-memory threshold in-memory when the SerializedFile payload itself is huge.
            if (ShouldUseTemporaryStream(totalSelectedBytes, lowMemoryThresholdOverride))
            {
                return false;
            }

            int blockCount = m_BlocksInfo.Length;
            var neededBlocks = new bool[blockCount];
            int neededBlockCount = 0;

            for (int n = 0; n < selectedNodes.Count; n++)
            {
                var node = selectedNodes[n];
                if (node.size == 0)
                {
                    continue;
                }

                long nodeStart = node.offset;
                long nodeEnd = node.offset + node.size;
                int startBlock = FindBlockIndexForOffset(uncompressedOffsets, m_BlocksInfo, nodeStart);
                for (int b = startBlock; b < blockCount; b++)
                {
                    long blockStart = uncompressedOffsets[b];
                    if (blockStart >= nodeEnd)
                    {
                        break;
                    }

                    long blockEnd = blockStart + m_BlocksInfo[b].uncompressedSize;
                    if (blockEnd <= nodeStart)
                    {
                        continue;
                    }

                    if (!neededBlocks[b])
                    {
                        neededBlocks[b] = true;
                        neededBlockCount++;
                    }
                }
            }

            if (neededBlockCount >= blockCount)
            {
                return false;
            }

            long baseCompressedOffset = reader.Position;
            var nodeBuffers = new byte[selectedNodes.Count][];
            for (int n = 0; n < selectedNodes.Count; n++)
            {
                var size = (int)selectedNodes[n].size;
                nodeBuffers[n] = size == 0 ? Array.Empty<byte>() : new byte[size];
            }

            byte[]? compressedBuffer = null;
            byte[]? uncompressedBuffer = null;
            try
            {
                for (int b = 0; b < blockCount; b++)
                {
                    if (!neededBlocks[b])
                    {
                        continue;
                    }

                    var blockInfo = m_BlocksInfo[b];
                    int compressedSize = (int)blockInfo.compressedSize;
                    int uncompressedSize = (int)blockInfo.uncompressedSize;
                    if (uncompressedSize == 0)
                    {
                        continue;
                    }

                    long targetPos = baseCompressedOffset + compressedOffsets[b];
                    if (reader.Position != targetPos)
                    {
                        reader.Position = targetPos;
                    }

                    if (uncompressedBuffer == null || uncompressedBuffer.Length < uncompressedSize)
                    {
                        if (uncompressedBuffer != null)
                        {
                            var oldBuf = uncompressedBuffer;
                            uncompressedBuffer = null;
                            BigArrayPool<byte>.Shared.Return(oldBuf);
                        }
                        uncompressedBuffer = BigArrayPool<byte>.Shared.Rent(uncompressedSize);
                    }

                    var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                    switch (compressionType)
                    {
                        case CompressionType.None:
                            ReadExact(reader, uncompressedBuffer, uncompressedSize);
                            break;
                        case CompressionType.Lz4:
                        case CompressionType.Lz4HC:
                            if (compressedBuffer == null || compressedBuffer.Length < compressedSize)
                            {
                                if (compressedBuffer != null)
                                {
                                    var oldComp = compressedBuffer;
                                    compressedBuffer = null;
                                    BigArrayPool<byte>.Shared.Return(oldComp);
                                }
                                compressedBuffer = BigArrayPool<byte>.Shared.Rent(compressedSize);
                            }

                            ReadExact(reader, compressedBuffer, compressedSize);
                            var numWrite = LZ4Codec.Decode(compressedBuffer, 0, compressedSize, uncompressedBuffer, 0, uncompressedSize);
                            if (numWrite != uncompressedSize)
                            {
                                throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                            }
                            break;
                        default:
                            return false;
                    }

                    long blockStart = uncompressedOffsets[b];
                    long blockEnd = blockStart + uncompressedSize;
                    for (int n = 0; n < selectedNodes.Count; n++)
                    {
                        var node = selectedNodes[n];
                        if (node.size == 0)
                        {
                            continue;
                        }

                        long nodeStart = node.offset;
                        long nodeEnd = nodeStart + node.size;
                        if (nodeEnd <= blockStart || nodeStart >= blockEnd)
                        {
                            continue;
                        }

                        long overlapStart = Math.Max(blockStart, nodeStart);
                        long overlapEnd = Math.Min(blockEnd, nodeEnd);
                        int copyLen = (int)(overlapEnd - overlapStart);
                        if (copyLen > 0)
                        {
                            Buffer.BlockCopy(
                                uncompressedBuffer,
                                (int)(overlapStart - blockStart),
                                nodeBuffers[n],
                                (int)(overlapStart - nodeStart),
                                copyLen);
                        }
                    }
                }
            }
            finally
            {
                if (compressedBuffer != null)
                {
                    BigArrayPool<byte>.Shared.Return(compressedBuffer);
                }
                if (uncompressedBuffer != null)
                {
                    BigArrayPool<byte>.Shared.Return(uncompressedBuffer);
                }
            }

            var partialFiles = new StreamFile[selectedNodes.Count];
            for (int n = 0; n < selectedNodes.Count; n++)
            {
                var node = selectedNodes[n];
                partialFiles[n] = new StreamFile
                {
                    path = node.path,
                    fileName = Path.GetFileName(node.path.Replace('\\', '/')),
                    stream = new MemoryStream(nodeBuffers[n], 0, nodeBuffers[n].Length, writable: false, publiclyVisible: true)
                };
            }

            fileList = partialFiles;
            UsedPartialDecompression = true;
            UsedParallelDecompression = false;
            DecompressedBlockCount = neededBlockCount;
            return true;
        }

        private static int FindBlockIndexForOffset(long[] uncompressedOffsets, StorageBlock[] blocksInfo, long targetOffset)
        {
            int lo = 0;
            int hi = uncompressedOffsets.Length - 1;
            int best = 0;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                long start = uncompressedOffsets[mid];
                if (start <= targetOffset)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // Back up past any zero-length trailing blocks sharing the same start offset.
            while (best > 0 && uncompressedOffsets[best] == targetOffset && blocksInfo[best].uncompressedSize == 0)
            {
                best--;
            }

            return best;
        }

        private static bool IsStreamingResourceNodePath(string? nodePath)
        {
            if (string.IsNullOrEmpty(nodePath))
            {
                return false;
            }

            return nodePath.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || nodePath.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryBuildBlockOffsets(
            StorageBlock[] blocksInfo,
            out long[] compressedOffsets,
            out long[] uncompressedOffsets,
            out long totalCompressedSize,
            out long totalUncompressedSize)
        {
            compressedOffsets = Array.Empty<long>();
            uncompressedOffsets = Array.Empty<long>();
            totalCompressedSize = 0;
            totalUncompressedSize = 0;

            if (blocksInfo == null || blocksInfo.Length == 0)
            {
                return false;
            }

            var compOffsets = new long[blocksInfo.Length];
            var uncompOffsets = new long[blocksInfo.Length];
            long runningCompressed = 0;
            long runningUncompressed = 0;

            for (int i = 0; i < blocksInfo.Length; i++)
            {
                var block = blocksInfo[i];
                var compressionType = (CompressionType)(block.flags & StorageBlockFlags.CompressionTypeMask);
                if (compressionType != CompressionType.None
                    && compressionType != CompressionType.Lz4
                    && compressionType != CompressionType.Lz4HC)
                {
                    return false;
                }

                if (block.compressedSize > int.MaxValue || block.uncompressedSize > int.MaxValue)
                {
                    return false;
                }

                if (compressionType == CompressionType.None && block.compressedSize != block.uncompressedSize)
                {
                    return false;
                }

                compOffsets[i] = runningCompressed;
                uncompOffsets[i] = runningUncompressed;
                runningCompressed = checked(runningCompressed + block.compressedSize);
                runningUncompressed = checked(runningUncompressed + block.uncompressedSize);
            }

            compressedOffsets = compOffsets;
            uncompressedOffsets = uncompOffsets;
            totalCompressedSize = runningCompressed;
            totalUncompressedSize = runningUncompressed;
            return true;
        }

        private bool TryReadBlocksParallel(EndianBinaryReader reader, Stream blocksStream)
        {
            int blockCount = m_BlocksInfo.Length;
            if (blockCount < MinParallelBlockCount)
            {
                return false;
            }

            if (!TryBuildBlockOffsets(
                m_BlocksInfo,
                out var compressedOffsets,
                out var uncompressedOffsets,
                out var totalCompressedSize,
                out var totalUncompressedSize))
            {
                return false;
            }

            if (totalUncompressedSize <= 0)
            {
                return false;
            }

            var sourceStream = reader.BaseStream;
            if (!sourceStream.CanSeek)
            {
                return false;
            }

            long baseCompressedOffset = reader.Position;
            if (baseCompressedOffset < 0)
            {
                return false;
            }

            long sourceLength = sourceStream.Length;

            SafeFileHandle? srcHandle = null;
            long srcFileBaseOffset = 0;
            byte[]? srcBuffer = null;
            long srcBufferBaseOffset = 0;
            long srcBufferAvailable = 0;
            byte[]? rentedSrcBuffer = null;

            if (sourceStream is FileStream srcFileStream)
            {
                srcHandle = srcFileStream.SafeFileHandle;
                srcFileBaseOffset = baseCompressedOffset;
            }
            else if (sourceStream is SubStream srcSubStream && srcSubStream.SafeFileHandle is SafeFileHandle subHandle)
            {
                srcHandle = subHandle;
                srcFileBaseOffset = srcSubStream.Offset + baseCompressedOffset;
            }
            else if (sourceStream is MemoryStream srcMemStream)
            {
                if (srcMemStream.TryGetBuffer(out ArraySegment<byte> srcSeg) && srcSeg.Array != null)
                {
                    srcBuffer = srcSeg.Array;
                    srcBufferBaseOffset = srcSeg.Offset + baseCompressedOffset;
                    srcBufferAvailable = Math.Max(0L, srcMemStream.Length - baseCompressedOffset);
                }
                else if (totalCompressedSize <= int.MaxValue)
                {
                    long available = Math.Max(0L, srcMemStream.Length - baseCompressedOffset);
                    int toRead = (int)Math.Min(totalCompressedSize, available);
                    rentedSrcBuffer = BigArrayPool<byte>.Shared.Rent(Math.Max(1, toRead));
                    srcMemStream.Position = baseCompressedOffset;
                    int totalRead = 0;
                    while (totalRead < toRead)
                    {
                        int r = srcMemStream.Read(rentedSrcBuffer, totalRead, toRead - totalRead);
                        if (r <= 0)
                        {
                            break;
                        }
                        totalRead += r;
                    }
                    srcBuffer = rentedSrcBuffer;
                    srcBufferBaseOffset = 0;
                    srcBufferAvailable = totalRead;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            try
            {
                byte[]? dstBuffer = null;
                int dstBufferBaseOffset = 0;
                SafeFileHandle? dstHandle = null;

                if (blocksStream is MemoryStream dstMemStream)
                {
                    if (totalUncompressedSize > int.MaxValue || dstMemStream.Position != 0)
                    {
                        return false;
                    }

                    dstMemStream.SetLength(totalUncompressedSize);
                    if (!dstMemStream.TryGetBuffer(out ArraySegment<byte> dstSeg) || dstSeg.Array == null)
                    {
                        return false;
                    }

                    dstBuffer = dstSeg.Array;
                    dstBufferBaseOffset = dstSeg.Offset;
                }
                else if (blocksStream is FileStream dstFileStream)
                {
                    if (dstFileStream.Position != 0)
                    {
                        return false;
                    }

                    dstFileStream.Flush();
                    if (baseCompressedOffset <= sourceLength - totalCompressedSize)
                    {
                        dstFileStream.SetLength(totalUncompressedSize);
                    }
                    dstHandle = dstFileStream.SafeFileHandle;
                }
                else
                {
                    return false;
                }

                var blocksInfo = m_BlocksInfo;
                var blockExceptions = new Exception?[blockCount];
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, blockCount))
                };

                Parallel.For<ParallelBlockScratch>(
                    0,
                    blockCount,
                    parallelOptions,
                    () => new ParallelBlockScratch(),
                    (i, loopState, scratch) =>
                    {
                        try
                        {
                            var blockInfo = blocksInfo[i];
                            var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                            int compressedSize = (int)blockInfo.compressedSize;
                            int uncompressedSize = (int)blockInfo.uncompressedSize;
                            long compOffset = compressedOffsets[i];
                            long uncompOffset = uncompressedOffsets[i];

                            if (compressionType == CompressionType.None)
                            {
                                if (compressedSize == 0)
                                {
                                    return scratch;
                                }

                                if (srcBuffer != null)
                                {
                                    if (compOffset > srcBufferAvailable - compressedSize)
                                    {
                                        int available = (int)Math.Max(0L, srcBufferAvailable - compOffset);
                                        throw new EndOfStreamException($"Unexpected end of bundle stream while copying uncompressed block: {compressedSize - available} bytes remaining.");
                                    }

                                    ReadOnlySpan<byte> srcSpan = srcBuffer.AsSpan((int)(srcBufferBaseOffset + compOffset), compressedSize);
                                    if (dstBuffer != null)
                                    {
                                        srcSpan.CopyTo(dstBuffer.AsSpan(dstBufferBaseOffset + (int)uncompOffset, uncompressedSize));
                                    }
                                    else
                                    {
                                        RandomAccess.Write(dstHandle!, srcSpan, uncompOffset);
                                    }
                                }
                                else if (dstBuffer != null)
                                {
                                    Span<byte> destSpan = dstBuffer.AsSpan(dstBufferBaseOffset + (int)uncompOffset, uncompressedSize);
                                    ReadExactRandomAccessForCopy(srcHandle!, destSpan, srcFileBaseOffset + compOffset);
                                }
                                else
                                {
                                    int chunkSize = Math.Min(compressedSize, 1024 * 1024);
                                    byte[] chunkBuf = scratch.EnsureCompressedBuffer(chunkSize);
                                    int remaining = compressedSize;
                                    long readOffset = srcFileBaseOffset + compOffset;
                                    long writeOffset = uncompOffset;
                                    while (remaining > 0)
                                    {
                                        int toRead = Math.Min(chunkBuf.Length, remaining);
                                        int read = RandomAccess.Read(srcHandle!, chunkBuf.AsSpan(0, toRead), readOffset);
                                        if (read <= 0)
                                        {
                                            throw new EndOfStreamException($"Unexpected end of bundle stream while copying uncompressed block: {remaining} bytes remaining.");
                                        }
                                        RandomAccess.Write(dstHandle!, chunkBuf.AsSpan(0, read), writeOffset);
                                        readOffset += read;
                                        writeOffset += read;
                                        remaining -= read;
                                    }
                                }
                            }
                            else
                            {
                                ReadOnlySpan<byte> compressedSpan;
                                if (srcBuffer != null)
                                {
                                    if (compOffset > srcBufferAvailable - compressedSize)
                                    {
                                        int actualRead = (int)Math.Max(0L, srcBufferAvailable - compOffset);
                                        throw new EndOfStreamException($"Unexpected end of bundle stream: expected {compressedSize} bytes, read {actualRead} bytes.");
                                    }
                                    compressedSpan = srcBuffer.AsSpan((int)(srcBufferBaseOffset + compOffset), compressedSize);
                                }
                                else
                                {
                                    byte[] compBuf = scratch.EnsureCompressedBuffer(compressedSize);
                                    Span<byte> compSpan = compBuf.AsSpan(0, compressedSize);
                                    ReadExactRandomAccess(srcHandle!, compSpan, srcFileBaseOffset + compOffset);
                                    compressedSpan = compSpan;
                                }

                                if (dstBuffer != null)
                                {
                                    Span<byte> destSpan = dstBuffer.AsSpan(dstBufferBaseOffset + (int)uncompOffset, uncompressedSize);
                                    int numWrite = LZ4Codec.Decode(compressedSpan, destSpan);
                                    if (numWrite != uncompressedSize)
                                    {
                                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                    }
                                }
                                else
                                {
                                    byte[] uncompBuf = scratch.EnsureUncompressedBuffer(uncompressedSize);
                                    Span<byte> destSpan = uncompBuf.AsSpan(0, uncompressedSize);
                                    int numWrite = LZ4Codec.Decode(compressedSpan, destSpan);
                                    if (numWrite != uncompressedSize)
                                    {
                                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                    }
                                    RandomAccess.Write(dstHandle!, destSpan, uncompOffset);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            blockExceptions[i] = ex;
                            loopState.Break();
                        }

                        return scratch;
                    },
                    scratch => scratch.Dispose());

                for (int i = 0; i < blockCount; i++)
                {
                    var ex = blockExceptions[i];
                    if (ex != null)
                    {
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }

                reader.Position = baseCompressedOffset + totalCompressedSize;
                blocksStream.Position = 0;
                return true;
            }
            finally
            {
                if (rentedSrcBuffer != null)
                {
                    BigArrayPool<byte>.Shared.Return(rentedSrcBuffer);
                }
            }
        }

        private static void ReadExactRandomAccess(SafeFileHandle handle, Span<byte> destination, long fileOffset)
        {
            int totalRead = 0;
            int count = destination.Length;
            while (totalRead < count)
            {
                int read = RandomAccess.Read(handle, destination.Slice(totalRead), fileOffset + totalRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of bundle stream: expected {count} bytes, read {totalRead} bytes.");
                }
                totalRead += read;
            }
        }

        private static void ReadExactRandomAccessForCopy(SafeFileHandle handle, Span<byte> destination, long fileOffset)
        {
            int totalRead = 0;
            int count = destination.Length;
            while (totalRead < count)
            {
                int read = RandomAccess.Read(handle, destination.Slice(totalRead), fileOffset + totalRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of bundle stream while copying uncompressed block: {count - totalRead} bytes remaining.");
                }
                totalRead += read;
            }
        }

        private sealed class ParallelBlockScratch : IDisposable
        {
            private byte[]? compressedBuffer;
            private byte[]? uncompressedBuffer;

            public byte[] EnsureCompressedBuffer(int minSize)
            {
                int required = Math.Max(1, minSize);
                if (compressedBuffer == null || compressedBuffer.Length < required)
                {
                    if (compressedBuffer != null)
                    {
                        var old = compressedBuffer;
                        compressedBuffer = null;
                        BigArrayPool<byte>.Shared.Return(old);
                    }
                    compressedBuffer = BigArrayPool<byte>.Shared.Rent(required);
                }
                return compressedBuffer;
            }

            public byte[] EnsureUncompressedBuffer(int minSize)
            {
                int required = Math.Max(1, minSize);
                if (uncompressedBuffer == null || uncompressedBuffer.Length < required)
                {
                    if (uncompressedBuffer != null)
                    {
                        var old = uncompressedBuffer;
                        uncompressedBuffer = null;
                        BigArrayPool<byte>.Shared.Return(old);
                    }
                    uncompressedBuffer = BigArrayPool<byte>.Shared.Rent(required);
                }
                return uncompressedBuffer;
            }

            public void Dispose()
            {
                if (compressedBuffer != null)
                {
                    var old = compressedBuffer;
                    compressedBuffer = null;
                    BigArrayPool<byte>.Shared.Return(old);
                }
                if (uncompressedBuffer != null)
                {
                    var old = uncompressedBuffer;
                    uncompressedBuffer = null;
                    BigArrayPool<byte>.Shared.Return(old);
                }
            }
        }

        private void ReadBlocksSequential(EndianBinaryReader reader, Stream blocksStream)
        {
            byte[]? compressedBuffer = null;
            byte[]? uncompressedBuffer = null;
            ZstdSharp.Decompressor? zstdDecompressor = null;
            try
            {
                foreach (var blockInfo in m_BlocksInfo)
                {
                    var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                    switch (compressionType)
                    {
                        case CompressionType.None:
                            {
                                CopyExact(reader.BaseStream, blocksStream, blockInfo.compressedSize);
                                break;
                            }
                        case CompressionType.Lzma:
                            {
                                SevenZipHelper.StreamDecompress(reader.BaseStream, blocksStream, blockInfo.compressedSize, blockInfo.uncompressedSize);
                                break;
                            }
                        case CompressionType.Lz4:
                        case CompressionType.Lz4HC:
                            {
                                if (blockInfo.compressedSize > int.MaxValue || blockInfo.uncompressedSize > int.MaxValue)
                                {
                                    throw new InvalidDataException($"LZ4 block size exceeds buffer limit (compressed {blockInfo.compressedSize}, uncompressed {blockInfo.uncompressedSize}).");
                                }
                                var compressedSize = (int)blockInfo.compressedSize;
                                if (compressedBuffer == null || compressedBuffer.Length < compressedSize)
                                {
                                    if (compressedBuffer != null)
                                    {
                                        var oldBuffer = compressedBuffer;
                                        compressedBuffer = null;
                                        BigArrayPool<byte>.Shared.Return(oldBuffer);
                                    }
                                    compressedBuffer = BigArrayPool<byte>.Shared.Rent(compressedSize);
                                }
                                ReadExact(reader, compressedBuffer, compressedSize);

                                var uncompressedSize = (int)blockInfo.uncompressedSize;
                                if (uncompressedBuffer == null || uncompressedBuffer.Length < uncompressedSize)
                                {
                                    if (uncompressedBuffer != null)
                                    {
                                        var oldBuffer = uncompressedBuffer;
                                        uncompressedBuffer = null;
                                        BigArrayPool<byte>.Shared.Return(oldBuffer);
                                    }
                                    uncompressedBuffer = BigArrayPool<byte>.Shared.Rent(uncompressedSize);
                                }

                                var numWrite = LZ4Codec.Decode(compressedBuffer, 0, compressedSize, uncompressedBuffer, 0, uncompressedSize);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                                }
                                blocksStream.Write(uncompressedBuffer, 0, uncompressedSize);
                                break;
                            }
                        default:
                            {
                                // Custom engine builds may store Zstandard frames under a compression id Unity does not define.
                                if (blockInfo.compressedSize < 4
                                    || blockInfo.compressedSize > int.MaxValue
                                    || blockInfo.uncompressedSize > int.MaxValue)
                                {
                                    throw new IOException($"Unsupported compression type {compressionType}");
                                }

                                var compressedSize = (int)blockInfo.compressedSize;
                                if (compressedBuffer == null || compressedBuffer.Length < compressedSize)
                                {
                                    if (compressedBuffer != null)
                                    {
                                        var oldBuffer = compressedBuffer;
                                        compressedBuffer = null;
                                        BigArrayPool<byte>.Shared.Return(oldBuffer);
                                    }
                                    compressedBuffer = BigArrayPool<byte>.Shared.Rent(compressedSize);
                                }
                                ReadExact(reader, compressedBuffer, compressedSize);
                                if (!IsZstdFrame(compressedBuffer, compressedSize))
                                {
                                    throw new IOException($"Unsupported compression type {compressionType}");
                                }

                                var uncompressedSize = (int)blockInfo.uncompressedSize;
                                if (uncompressedBuffer == null || uncompressedBuffer.Length < uncompressedSize)
                                {
                                    if (uncompressedBuffer != null)
                                    {
                                        var oldBuffer = uncompressedBuffer;
                                        uncompressedBuffer = null;
                                        BigArrayPool<byte>.Shared.Return(oldBuffer);
                                    }
                                    uncompressedBuffer = BigArrayPool<byte>.Shared.Rent(uncompressedSize);
                                }

                                zstdDecompressor ??= new ZstdSharp.Decompressor();
                                DecompressZstdExact(zstdDecompressor, compressedBuffer, compressedSize, uncompressedBuffer, uncompressedSize);
                                blocksStream.Write(uncompressedBuffer, 0, uncompressedSize);
                                break;
                            }
                    }
                }
            }
            finally
            {
                zstdDecompressor?.Dispose();
                if (compressedBuffer != null)
                {
                    BigArrayPool<byte>.Shared.Return(compressedBuffer);
                }
                if (uncompressedBuffer != null)
                {
                    BigArrayPool<byte>.Shared.Return(uncompressedBuffer);
                }
            }
            blocksStream.Position = 0;
        }

        public readonly record struct BundleSegment(int Index, long Offset, long Size);

        private const int MaxBundleSegmentsPerFile = 4096;
        private const int BundleSegmentSearchWindow = 64 * 1024;
        private static readonly byte[] UnityFsSignatureBytes = "UnityFS\0"u8.ToArray();

        /// <summary>
        /// Finds UnityFS bundles stored back to back after <paramref name="firstBundle"/> in the same stream
        /// (BUNDLE-02). A normal bundle declares its own length in the header, so nothing is read unless bytes
        /// remain after it; only the gap right after each bundle is searched, and only bundle headers are read.
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<BundleSegment> FindFollowingSegments(Stream source, BundleFile firstBundle)
        {
            var segments = new System.Collections.Generic.List<BundleSegment>();
            if (firstBundle.m_Header.signature != "UnityFS" || !source.CanSeek)
            {
                return segments;
            }

            long length = source.Length;
            long next = firstBundle.m_Header.size;
            long originalPosition = source.Position;
            try
            {
                for (int index = 1; index <= MaxBundleSegmentsPerFile; index++)
                {
                    if (next <= 0 || next > length - UnityFsSignatureBytes.Length)
                    {
                        break;
                    }

                    long start = FindUnityFsSignature(source, next, Math.Min(length, next + BundleSegmentSearchWindow));
                    if (start < 0
                        || !TryReadUnityFsBundleSize(source, start, out long size)
                        || size > length - start)
                    {
                        break;
                    }

                    segments.Add(new BundleSegment(index, start, size));
                    next = start + size;
                }
            }
            finally
            {
                source.Position = originalPosition;
            }

            return segments;
        }

        /// <summary>
        /// Opens one bundle stored inside a larger file as a window over that file (no copy). The reader gets a
        /// virtual path so per-file caches never confuse it with the first bundle of the same file.
        /// </summary>
        public static FileReader OpenSegmentReader(FileReader parent, BundleSegment segment)
        {
            var source = parent.BaseStream;
            Stream view = source is FileStream fileStream
                ? new SubStream(fileStream.Name, segment.Offset, segment.Size)
                : source is SubStream { SharedHandle: not null } parentView
                    ? new SubStream(parentView.SharedHandle, parentView.Offset + segment.Offset, segment.Size)
                    : new SubStream(source, segment.Offset, segment.Size, syncRoot: source, leaveOpen: true);
            return new FileReader($"{parent.FullPath}#bundle{segment.Index}@0x{segment.Offset:X}", view);
        }

        private static long FindUnityFsSignature(Stream source, long start, long end)
        {
            int windowLength = (int)Math.Max(0L, end - start);
            if (windowLength < UnityFsSignatureBytes.Length)
            {
                return -1;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(windowLength);
            try
            {
                source.Position = start;
                int read = 0;
                while (read < windowLength)
                {
                    int n = source.Read(buffer, read, windowLength - read);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }

                int index = buffer.AsSpan(0, read).IndexOf(UnityFsSignatureBytes);
                return index >= 0 ? start + index : -1;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool TryReadUnityFsBundleSize(Stream source, long offset, out long size)
        {
            size = 0;
            Span<byte> header = stackalloc byte[256];
            source.Position = offset;
            int read = source.Read(header);
            if (read < UnityFsSignatureBytes.Length + 4)
            {
                return false;
            }

            var span = header.Slice(0, read);
            int pos = UnityFsSignatureBytes.Length;
            uint version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos, 4));
            pos += 4;
            if (version < 6 || version > 16)
            {
                return false;
            }

            for (int field = 0; field < 2; field++)
            {
                int terminator = span.Slice(pos).IndexOf((byte)0);
                if (terminator < 0)
                {
                    return false;
                }
                pos += terminator + 1;
            }

            if (pos + 8 > span.Length)
            {
                return false;
            }

            size = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(span.Slice(pos, 8));
            return size > pos;
        }

        private const uint ZstdFrameMagic = 0xFD2FB528;

        /// <summary>
        /// Recognizes a Zstandard frame by its magic number. Zstd blocks are only ever detected this way,
        /// never from the compression id alone, because Unity does not define an id for Zstd.
        /// </summary>
        public static bool IsZstdFrame(byte[] data, int length)
        {
            return length >= 4
                && data.Length >= 4
                && ((uint)data[0] | ((uint)data[1] << 8) | ((uint)data[2] << 16) | ((uint)data[3] << 24)) == ZstdFrameMagic;
        }

        public static bool TryDecompressZstd(byte[] source, int uncompressedSize, out byte[] uncompressed)
        {
            uncompressed = Array.Empty<byte>();
            if (uncompressedSize < 0 || !IsZstdFrame(source, source.Length))
            {
                return false;
            }

            try
            {
                var buffer = new byte[uncompressedSize];
                DecompressZstdExact(null, source, source.Length, buffer, uncompressedSize);
                uncompressed = buffer;
                return true;
            }
            catch (Exception ex) when (ex is IOException or ZstdSharp.ZstdException or ArgumentException)
            {
                return false;
            }
        }

        private static void DecompressZstdExact(ZstdSharp.Decompressor? decompressor, byte[] source, int sourceLength, byte[] destination, int destinationLength)
        {
            int written;
            try
            {
                if (decompressor != null)
                {
                    written = decompressor.Unwrap(source, 0, sourceLength, destination, 0, destinationLength);
                }
                else
                {
                    using var oneShot = new ZstdSharp.Decompressor();
                    written = oneShot.Unwrap(source, 0, sourceLength, destination, 0, destinationLength);
                }
            }
            catch (ZstdSharp.ZstdException ex)
            {
                throw new IOException($"Zstd decompression error: {ex.Message}", ex);
            }

            if (written != destinationLength)
            {
                throw new IOException($"Zstd decompression error, write {written} bytes but expected {destinationLength} bytes");
            }
        }

        private static void CopyExact(Stream source, Stream destination, long byteCount)
        {
            if (byteCount <= 0)
            {
                return;
            }

            var bufferSize = (int)Math.Min(byteCount, 1024 * 1024);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                long remaining = byteCount;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = source.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of bundle stream while copying uncompressed block: {remaining} bytes remaining.");
                    }

                    destination.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            if (fileList != null)
            {
                for (int i = 0; i < fileList.Length; i++)
                {
                    fileList[i]?.stream?.Dispose();
                }
            }
            BlocksStream?.Dispose();
            BlocksStream = null;
        }
    }
}
