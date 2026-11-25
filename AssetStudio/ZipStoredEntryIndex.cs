#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Locates the raw data of entries stored without compression in a ZIP/APK/OBB/AAB package, so they can be
    /// read in place as a window over the package instead of being copied out (BUNDLE-08). Android builds keep
    /// Unity bundles, serialized files and .resS/.resource files stored uncompressed, so this covers the bulk of
    /// a game's data. Only the central directory and the local headers of looked-up entries are read; ZIP64 is
    /// supported for packages above 4 GiB. Any unexpected layout yields no index and callers fall back to
    /// normal decompression.
    /// </summary>
    public sealed class ZipStoredEntryIndex
    {
        private const uint EndOfCentralDirectorySignature = 0x06054B50;
        private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
        private const uint Zip64LocatorSignature = 0x07064B50;
        private const uint CentralDirectorySignature = 0x02014B50;
        private const uint LocalHeaderSignature = 0x04034B50;
        private const int EndOfCentralDirectorySize = 22;
        private const int MaxCommentLength = ushort.MaxValue;
        private const long MaxCentralDirectorySize = 512L * 1024 * 1024;

        private readonly Stream _source;
        private readonly Dictionary<string, (long LocalHeaderOffset, long Size)> _storedEntries;

        private ZipStoredEntryIndex(Stream source, Dictionary<string, (long LocalHeaderOffset, long Size)> storedEntries)
        {
            _source = source;
            _storedEntries = storedEntries;
        }

        public int StoredEntryCount => _storedEntries.Count;

        public static ZipStoredEntryIndex? TryCreate(Stream source)
        {
            try
            {
                return source.CanSeek && source.CanRead ? Build(source) : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the absolute offset and length of a stored (uncompressed, unencrypted) entry's data.
        /// </summary>
        public bool TryGetStoredData(string fullName, out long dataOffset, out long size)
        {
            dataOffset = 0;
            size = 0;
            if (!_storedEntries.TryGetValue(fullName, out var entry))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[30];
            if (!TryReadAt(_source, entry.LocalHeaderOffset, header)
                || BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature)
            {
                return false;
            }

            long offset = entry.LocalHeaderOffset + 30
                + BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2))
                + BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
            if (offset < 0 || entry.Size > _source.Length - offset)
            {
                return false;
            }

            dataOffset = offset;
            size = entry.Size;
            return true;
        }

        private static ZipStoredEntryIndex? Build(Stream source)
        {
            long length = source.Length;
            if (length < EndOfCentralDirectorySize)
            {
                return null;
            }

            int tailLength = (int)Math.Min(length, EndOfCentralDirectorySize + MaxCommentLength);
            var tail = new byte[tailLength];
            if (!TryReadAt(source, length - tailLength, tail))
            {
                return null;
            }

            int eocd = -1;
            for (int i = tailLength - EndOfCentralDirectorySize; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == EndOfCentralDirectorySignature)
                {
                    eocd = i;
                    break;
                }
            }

            if (eocd < 0)
            {
                return null;
            }

            var record = tail.AsSpan(eocd);
            long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(10, 2));
            long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(12, 4));
            long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4));

            if (entryCount == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
            {
                long eocdAbsolute = length - tailLength + eocd;
                Span<byte> locator = stackalloc byte[20];
                if (eocdAbsolute < 20
                    || !TryReadAt(source, eocdAbsolute - 20, locator)
                    || BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                {
                    return null;
                }

                long zip64RecordOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.Slice(8, 8));
                Span<byte> zip64 = stackalloc byte[56];
                if (!TryReadAt(source, zip64RecordOffset, zip64)
                    || BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndOfCentralDirectorySignature)
                {
                    return null;
                }

                entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.Slice(32, 8));
                directorySize = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.Slice(40, 8));
                directoryOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.Slice(48, 8));
            }

            if (directorySize <= 0
                || directorySize > MaxCentralDirectorySize
                || directoryOffset < 0
                || directoryOffset > length - directorySize
                || entryCount < 0
                || entryCount > directorySize / 46)
            {
                return null;
            }

            var directory = new byte[directorySize];
            if (!TryReadAt(source, directoryOffset, directory))
            {
                return null;
            }

            var stored = new Dictionary<string, (long, long)>((int)entryCount, StringComparer.Ordinal);
            int pos = 0;
            for (long i = 0; i < entryCount; i++)
            {
                if (pos + 46 > directory.Length
                    || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(pos, 4)) != CentralDirectorySignature)
                {
                    return null;
                }

                var header = directory.AsSpan(pos, 46);
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
                ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(10, 2));
                long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
                long uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
                int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(30, 2));
                int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2));
                long localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(42, 4));

                int next = pos + 46 + nameLength + extraLength + commentLength;
                if (next > directory.Length)
                {
                    return null;
                }

                var extra = directory.AsSpan(pos + 46 + nameLength, extraLength);
                if (uncompressedSize == uint.MaxValue || compressedSize == uint.MaxValue || localHeaderOffset == uint.MaxValue)
                {
                    ApplyZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localHeaderOffset);
                }

                bool encrypted = (flags & 0x1) != 0;
                if (method == 0 && !encrypted && compressedSize == uncompressedSize && localHeaderOffset >= 0)
                {
                    // Same decoding as System.IO.Compression.ZipArchive on .NET (UTF-8), so names match its entries.
                    var name = Encoding.UTF8.GetString(directory, pos + 46, nameLength);
                    stored[name] = (localHeaderOffset, uncompressedSize);
                }

                pos = next;
            }

            return new ZipStoredEntryIndex(source, stored);
        }

        private static void ApplyZip64Extra(ReadOnlySpan<byte> extra, ref long uncompressedSize, ref long compressedSize, ref long localHeaderOffset)
        {
            int pos = 0;
            while (pos + 4 <= extra.Length)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(pos, 2));
                ushort size = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(pos + 2, 2));
                if (pos + 4 + size > extra.Length)
                {
                    return;
                }

                if (id == 0x0001)
                {
                    var data = extra.Slice(pos + 4, size);
                    int field = 0;
                    if (uncompressedSize == uint.MaxValue && field + 8 <= data.Length)
                    {
                        uncompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(field, 8));
                        field += 8;
                    }
                    if (compressedSize == uint.MaxValue && field + 8 <= data.Length)
                    {
                        compressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(field, 8));
                        field += 8;
                    }
                    if (localHeaderOffset == uint.MaxValue && field + 8 <= data.Length)
                    {
                        localHeaderOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(field, 8));
                    }
                    return;
                }

                pos += 4 + size;
            }
        }

        private static bool TryReadAt(Stream source, long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > source.Length - destination.Length)
            {
                return false;
            }

            source.Position = offset;
            int read = 0;
            while (read < destination.Length)
            {
                int n = source.Read(destination.Slice(read));
                if (n <= 0)
                {
                    return false;
                }
                read += n;
            }

            return true;
        }
    }
}
