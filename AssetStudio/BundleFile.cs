#nullable enable
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.IO;
using System.Linq;

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
        private const int PersistentCacheSampleBytes = 1024 * 1024;
        private const long PersistentCacheFullSignatureThreshold = 8L * 1024 * 1024;
        private const ulong PersistentCacheFnvOffsetBasis = 14695981039346656037UL;
        private const ulong PersistentCacheFnvPrime = 1099511628211UL;

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

        public Header m_Header = null!;
        private StorageBlock[] m_BlocksInfo = null!;
        private Node[] m_DirectoryInfo = null!;

        public StreamFile[] fileList = null!;
        public Stream? BlocksStream { get; set; }

        public BundleFile(FileReader reader)
        {
            m_Header = new Header();
            m_Header.signature = reader.ReadStringToNull();
            m_Header.version = reader.ReadUInt32();
            m_Header.unityVersion = reader.ReadStringToNull();
            m_Header.unityRevision = reader.ReadStringToNull();
            switch (m_Header.signature)
            {
                case "UnityArchive":
                    break; //TODO
                case "UnityWeb":
                case "UnityRaw":
                    {
                        if (m_Header.version == 6)
                        {
                            goto case "UnityFS";
                        }
                        ReadHeaderAndBlocksInfo(reader);
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
            }
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
            m_BlocksInfo = new StorageBlock[1];
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
            if (!string.IsNullOrEmpty(CacheDirectory) && !string.IsNullOrEmpty(path))
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
                            return new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                    return new FileStream(tempCachePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to use persistent cache for {path}: {ex.Message}. Falling back to default stream.");
                }
            }

            Stream blocksStream;
            if (ShouldUseTemporaryStream(uncompressedSizeSum))
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

        internal static bool ShouldUseTemporaryStream(long size)
        {
            return size >= int.MaxValue || (LowMemoryMode && size >= LowMemoryThreshold);
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

        private static string ComputeSourceContentSignature(string path, long sourceLength)
        {
            var hash = AddUInt64(PersistentCacheFnvOffsetBasis, (ulong)sourceLength);
            var buffer = ArrayPool<byte>.Shared.Rent(PersistentCacheSampleBytes);
            var fileOptions = sourceLength <= PersistentCacheFullSignatureThreshold
                ? FileOptions.SequentialScan
                : FileOptions.RandomAccess;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                fileOptions);
            try
            {
                if (sourceLength <= PersistentCacheFullSignatureThreshold)
                {
                    hash = AddSample(hash, stream, buffer, 0, sourceLength);
                }
                else
                {
                    hash = AddSample(hash, stream, buffer, 0, PersistentCacheSampleBytes);
                    hash = AddSample(
                        hash,
                        stream,
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

        private static ulong AddSample(ulong hash, FileStream stream, byte[] buffer, long offset, long bytesToRead)
        {
            if (bytesToRead <= 0)
            {
                return hash;
            }

            stream.Position = offset;
            var remaining = bytesToRead;
            hash = AddUInt64(hash, (ulong)offset);
            while (remaining > 0)
            {
                var readSize = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = stream.Read(buffer, 0, readSize);
                if (bytesRead <= 0)
                {
                    break;
                }

                hash = AddInt32(hash, bytesRead);
                hash = AddBytes(hash, buffer, bytesRead);
                remaining -= bytesRead;
            }

            return hash;
        }

        private static ulong AddUInt64(ulong hash, ulong value)
        {
            for (int i = 0; i < sizeof(ulong); i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= PersistentCacheFnvPrime;
            }
            return hash;
        }

        private static ulong AddInt32(ulong hash, int value)
        {
            for (int i = 0; i < sizeof(int); i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= PersistentCacheFnvPrime;
            }
            return hash;
        }

        private static ulong AddBytes(ulong hash, byte[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                hash ^= buffer[i];
                hash *= PersistentCacheFnvPrime;
            }
            return hash;
        }

        private void ReadBlocksAndDirectory(EndianBinaryReader reader, Stream blocksStream)
        {
            var isCompressed = m_Header.signature == "UnityWeb";
            foreach (var blockInfo in m_BlocksInfo)
            {
                var compressedSize = (int)blockInfo.compressedSize;
                if (isCompressed)
                {
                    var compressedBytes = BigArrayPool<byte>.Shared.Rent(compressedSize);
                    reader.Read(compressedBytes, 0, compressedSize);
                    using (var memoryStream = new MemoryStream(compressedBytes, 0, compressedSize))
                    {
                        using (var decompressStream = SevenZipHelper.StreamDecompress(memoryStream))
                        {
                            decompressStream.CopyTo(blocksStream);
                        }
                    }
                    BigArrayPool<byte>.Shared.Return(compressedBytes);
                }
                else
                {
                    var buffer = BigArrayPool<byte>.Shared.Rent(compressedSize);
                    reader.Read(buffer, 0, compressedSize);
                    blocksStream.Write(buffer, 0, compressedSize);
                    BigArrayPool<byte>.Shared.Return(buffer);
                }
            }
            blocksStream.Position = 0;
            var blocksReader = new EndianBinaryReader(blocksStream);
            var nodesCount = blocksReader.ReadInt32();
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
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = node.path;
                    file.fileName = Path.GetFileName(node.path);
                    
                    // Directly wrap the shared buffer! Zero memory copy, zero allocation!
                    file.stream = new MemoryStream(sharedBuffer, (int)node.offset, (int)node.size, false);
                }
            }
            else if (blocksStream is FileStream fileStream)
            {
                var tempFilePath = fileStream.Name;
                for (int i = 0; i < m_DirectoryInfo.Length; i++)
                {
                    var node = m_DirectoryInfo[i];
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = node.path;
                    file.fileName = Path.GetFileName(node.path);

                    // Directly wrap in a SubStream pointing to the single temp file!
                    // Zero disk copy! Zero extra temp files!
                    file.stream = new SubStream(tempFilePath, node.offset, node.size);
                }
            }
            else
            {
                // Fallback for custom streams
                for (int i = 0; i < m_DirectoryInfo.Length; i++)
                {
                    var node = m_DirectoryInfo[i];
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = node.path;
                    file.fileName = Path.GetFileName(node.path);
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
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
            {
                var position = reader.Position;
                reader.Position = reader.BaseStream.Length - m_Header.compressedBlocksInfoSize;
                blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
                reader.Position = position;
            }
            else //0x40 BlocksAndDirectoryInfoCombined
            {
                blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
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
                    throw new IOException($"Unsupported compression type {compressionType}");
            }
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompresseddStream))
            {
                var uncompressedDataHash = blocksInfoReader.ReadBytes(16);
                var blocksInfoCount = blocksInfoReader.ReadInt32();
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
            byte[]? compressedBuffer = null;
            byte[]? uncompressedBuffer = null;
            try
            {
                foreach (var blockInfo in m_BlocksInfo)
                {
                    var compressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                    switch (compressionType)
                    {
                        case CompressionType.None:
                            {
                                reader.BaseStream.CopyTo(blocksStream, blockInfo.compressedSize);
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
                                reader.Read(compressedBuffer, 0, compressedSize);

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
                            throw new IOException($"Unsupported compression type {compressionType}");
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
            blocksStream.Position = 0;
        }

        public void Dispose()
        {
            BlocksStream?.Dispose();
            BlocksStream = null;
        }
    }
}
