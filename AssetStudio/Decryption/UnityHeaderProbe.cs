#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace AssetStudio
{
    public readonly struct StreamEncryptionBarrier
    {
        public StreamEncryptionBarrier(
            string providerId,
            string filePath,
            string reason,
            double confidence,
            string headerPrefixHex)
        {
            ProviderId = providerId ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            Reason = reason ?? string.Empty;
            Confidence = confidence;
            HeaderPrefixHex = headerPrefixHex ?? string.Empty;
        }

        public string ProviderId { get; }
        public string FilePath { get; }
        public string Reason { get; }
        public double Confidence { get; }
        public string HeaderPrefixHex { get; }
    }

    public static class UnityHeaderProbe
    {
        public const int DefaultProbeBufferSize = 4096;
        private static readonly byte[] UnityFsMagicWithNull = { (byte)'U', (byte)'n', (byte)'i', (byte)'t', (byte)'y', (byte)'F', (byte)'S', 0 };
        private static readonly string[] KnownBundleSignatures = { "UnityFS", "UnityWeb", "UnityRaw", "UnityArchive" };

        public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buffer, int offset)
            => BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));

        public static long ReadInt64BigEndian(ReadOnlySpan<byte> buffer, int offset)
            => BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(offset, 8));

        public static bool MatchesAt(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> pattern)
        {
            if (offset < 0 || pattern.IsEmpty || buffer.Length - offset < pattern.Length)
            {
                return false;
            }

            return buffer.Slice(offset, pattern.Length).SequenceEqual(pattern);
        }

        public static string ReadNullTerminatedAscii(ReadOnlySpan<byte> buffer, int offset, int maxLength, out int nextOffset)
        {
            nextOffset = offset;
            if (offset < 0 || offset >= buffer.Length || maxLength <= 0)
            {
                return string.Empty;
            }

            int limit = Math.Min(buffer.Length, offset + maxLength);
            int end = offset;
            while (end < limit && buffer[end] != 0)
            {
                end++;
            }

            string result = Encoding.ASCII.GetString(buffer.Slice(offset, end - offset));
            nextOffset = end < buffer.Length && buffer[end] == 0 ? end + 1 : end;
            return result;
        }

        public static void XorBytes( byte[] destination, int destinationOffset, byte[] key, int keyOffset, int count)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated && count >= Vector<byte>.Count)
            {
                int vectorSize = Vector<byte>.Count;
                int vectorEnd = count - (count % vectorSize);
                for (; i < vectorEnd; i += vectorSize)
                {
                    var left = new Vector<byte>(destination, destinationOffset + i);
                    var right = new Vector<byte>(key, keyOffset + i);
                    (left ^ right).CopyTo(destination, destinationOffset + i);
                }
            }

            for (; i < count; i++)
            {
                destination[destinationOffset + i] ^= key[keyOffset + i];
            }
        }

        public static void XorBytesRepeating(Span<byte> buffer, long streamOffset, ReadOnlySpan<byte> repeatingKey)
        {
            if (buffer.IsEmpty || repeatingKey.IsEmpty)
            {
                return;
            }

            int keyLen = repeatingKey.Length;
            if (keyLen == 1)
            {
                byte k = repeatingKey[0];
                if (k == 0)
                {
                    return;
                }

                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] ^= k;
                }
                return;
            }

            int keyIndex = (int)(streamOffset % keyLen);
            if (keyIndex < 0)
            {
                keyIndex += keyLen;
            }

            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] ^= repeatingKey[keyIndex];
                keyIndex++;
                if (keyIndex == keyLen)
                {
                    keyIndex = 0;
                }
            }
        }

        public static bool IsSerializedFileHeader(ReadOnlySpan<byte> header, long actualFileSize)
        {
            if (actualFileSize < 20 || header.Length < 20)
            {
                return false;
            }

            uint metadataSize = ReadUInt32BigEndian(header, 0);
            long fileSize = ReadUInt32BigEndian(header, 4);
            uint version = ReadUInt32BigEndian(header, 8);
            long dataOffset = ReadUInt32BigEndian(header, 12);

            if (version == 0 || version > 64)
            {
                return false;
            }

            if (version >= 22)
            {
                if (actualFileSize < 48 || header.Length < 40)
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

        public static bool TryParseUnityBundleHeader(
            ReadOnlySpan<byte> buffer,
            int offset,
            long totalStreamLength,
            out string signature,
            out uint version,
            out string unityVersion,
            out long declaredBundleSize,
            bool requireStrictValidation = false)
        {
            signature = string.Empty;
            version = 0;
            unityVersion = string.Empty;
            declaredBundleSize = 0;

            if (offset < 0 || buffer.Length - offset < 8)
            {
                return false;
            }

            var slice = buffer.Slice(offset);
            string candidateSig = ReadNullTerminatedAscii(slice, 0, 16, out int pos);
            bool knownSig = false;
            for (int i = 0; i < KnownBundleSignatures.Length; i++)
            {
                if (string.Equals(candidateSig, KnownBundleSignatures[i], StringComparison.Ordinal))
                {
                    knownSig = true;
                    break;
                }
            }

            if (!knownSig || pos <= candidateSig.Length)
            {
                return false;
            }

            if (!requireStrictValidation && offset == 0)
            {
                signature = candidateSig;
                if (pos + 4 <= slice.Length)
                {
                    version = ReadUInt32BigEndian(slice, pos);
                    pos += 4;
                    string verStr = ReadNullTerminatedAscii(slice, pos, 32, out _);
                    if (!string.IsNullOrEmpty(verStr))
                    {
                        unityVersion = verStr;
                    }
                }
                return true;
            }

            if (pos + 4 > slice.Length)
            {
                return false;
            }

            uint candidateVersion = ReadUInt32BigEndian(slice, pos);
            if (candidateVersion == 0 || candidateVersion > 12)
            {
                return false;
            }
            pos += 4;

            string playerVer = ReadNullTerminatedAscii(slice, pos, 32, out pos);
            string engineRev = ReadNullTerminatedAscii(slice, pos, 32, out pos);
            if (!IsPrintableVersionToken(playerVer) || !IsPrintableVersionToken(engineRev))
            {
                return false;
            }

            if (string.Equals(candidateSig, "UnityFS", StringComparison.Ordinal))
            {
                if (pos + 16 > slice.Length)
                {
                    return false;
                }

                long bundleSize = ReadInt64BigEndian(slice, pos);
                long expectedRemaining = totalStreamLength - offset;
                if (bundleSize <= 0)
                {
                    return false;
                }

                if (totalStreamLength > 0 && bundleSize != expectedRemaining && bundleSize != totalStreamLength)
                {
                    return false;
                }

                declaredBundleSize = bundleSize;
            }

            signature = candidateSig;
            version = candidateVersion;
            unityVersion = playerVer;
            return true;
        }

        public static bool TryFindOffsetUnityBundleHeader(
            ReadOnlySpan<byte> probeBuffer,
            long totalStreamLength,
            out int bundleOffset,
            out string signature,
            out uint version,
            out string unityVersion)
        {
            bundleOffset = 0;
            signature = string.Empty;
            version = 0;
            unityVersion = string.Empty;

            if (probeBuffer.Length < 32)
            {
                return false;
            }

            int maxSearch = probeBuffer.Length - 26;
            for (int offset = 1; offset <= maxSearch; offset++)
            {
                if (probeBuffer[offset] != (byte)'U')
                {
                    continue;
                }

                if (TryParseUnityBundleHeader(probeBuffer, offset, totalStreamLength, out signature, out version, out unityVersion, out _, requireStrictValidation: true))
                {
                    bundleOffset = offset;
                    return true;
                }
            }

            return false;
        }

        public static bool TryDetectRepeatingXorUnityFsKey(
            ReadOnlySpan<byte> header,
            long totalStreamLength,
            out byte[] xorKey,
            out uint version,
            out string unityVersion)
        {
            xorKey = Array.Empty<byte>();
            version = 0;
            unityVersion = string.Empty;

            if (header.Length < 32)
            {
                return false;
            }

            Span<byte> candidateKey4 = stackalloc byte[4];
            bool anyNonZero = false;
            for (int i = 0; i < 4; i++)
            {
                byte k = (byte)(header[i] ^ UnityFsMagicWithNull[i]);
                byte kSecondWord = (byte)(header[i + 4] ^ UnityFsMagicWithNull[i + 4]);
                if (k != kSecondWord)
                {
                    return false;
                }

                candidateKey4[i] = k;
                if (k != 0)
                {
                    anyNonZero = true;
                }
            }

            if (!anyNonZero)
            {
                return false;
            }

            int sampleLen = Math.Min(header.Length, 128);
            Span<byte> decoded = stackalloc byte[sampleLen];
            header.Slice(0, sampleLen).CopyTo(decoded);
            XorBytesRepeating(decoded, 0, candidateKey4);

            if (!TryParseUnityBundleHeader(decoded, 0, totalStreamLength, out _, out version, out unityVersion, out _, requireStrictValidation: true))
            {
                return false;
            }

            if (candidateKey4[0] == candidateKey4[1] && candidateKey4[1] == candidateKey4[2] && candidateKey4[2] == candidateKey4[3])
            {
                xorKey = new[] { candidateKey4[0] };
            }
            else if (candidateKey4[0] == candidateKey4[2] && candidateKey4[1] == candidateKey4[3])
            {
                xorKey = new[] { candidateKey4[0], candidateKey4[1] };
            }
            else
            {
                xorKey = candidateKey4.ToArray();
            }

            return true;
        }

        public static bool TryDetectEncryptionBarrier(
            ReadOnlySpan<byte> probeBuffer,
            long totalStreamLength,
            string? filePath,
            out StreamEncryptionBarrier barrier)
        {
            barrier = default;
            if (string.IsNullOrWhiteSpace(filePath) || probeBuffer.Length < 32)
            {
                return false;
            }

            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            // 1. Check for external VFS block/chunk container files (.blc / .chk)
            if (string.Equals(ext, ".blc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".chk", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _)
                    && !IsSerializedFileHeader(probeBuffer, totalStreamLength))
                {
                    string prefixHex = FormatHexPrefix(probeBuffer, 16);
                    string kindLabel = string.Equals(ext, ".blc", StringComparison.OrdinalIgnoreCase)
                        ? "VFS block index (.blc)"
                        : "VFS data chunk (.chk)";
                    barrier = new StreamEncryptionBarrier(
                        providerId: "VfsChunkContainer",
                        filePath: filePath,
                        reason: $"File is a proprietary {kindLabel} outside UnityFS format (header: {prefixHex}).",
                        confidence: 0.95,
                        headerPrefixHex: prefixHex);
                    return true;
                }
            }

            // 2. Check for partially encrypted SerializedFile headers (high-entropy 0x0000..0x00FF followed by structured zero-padded pages at >= 0x0400)
            if (IsStandardSerializedAssetFileName(fileName, ext)
                && totalStreamLength >= 0x1000
                && probeBuffer.Length >= 0x800
                && !IsSerializedFileHeader(probeBuffer, totalStreamLength)
                && !TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _))
            {
                int nonZeroInHeader = CountNonZeroBytes(probeBuffer.Slice(0, 256));
                int maxZerosInLaterPage = 0;
                for (int pageOffset = 0x400; pageOffset + 256 <= probeBuffer.Length; pageOffset += 256)
                {
                    int zeros = 256 - CountNonZeroBytes(probeBuffer.Slice(pageOffset, 256));
                    if (zeros > maxZerosInLaterPage)
                    {
                        maxZerosInLaterPage = zeros;
                    }
                }

                if (nonZeroInHeader >= 220 && maxZerosInLaterPage >= 96)
                {
                    string prefixHex = FormatHexPrefix(probeBuffer, 16);
                    barrier = new StreamEncryptionBarrier(
                        providerId: "PartialHeaderEncryptedSerializedFile",
                        filePath: filePath,
                        reason: $"SerializedFile header (0x0000..0x03FF) appears encrypted (prefix: {prefixHex}) while subsequent pages (0x0400+) contain structured serialized data.",
                        confidence: 0.94,
                        headerPrefixHex: prefixHex);
                    return true;
                }
            }

            return false;
        }

        private static bool IsStandardSerializedAssetFileName(string fileName, string ext)
        {
            if (string.Equals(ext, ".assets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(fileName, "globalgamemanagers", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "mainData", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "unity default resources", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "unity_builtin_extra", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(ext))
            {
                for (int i = 5; i < fileName.Length; i++)
                {
                    if (!char.IsDigit(fileName[i]))
                    {
                        return false;
                    }
                }
                return fileName.Length > 5;
            }

            return false;
        }

        private static int CountNonZeroBytes(ReadOnlySpan<byte> span)
        {
            int count = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] != 0)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsPrintableVersionToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c > 0x7E)
                {
                    return false;
                }
            }
            return true;
        }

        public static string FormatHexPrefix(ReadOnlySpan<byte> buffer, int maxBytes)
        {
            int count = Math.Min(buffer.Length, maxBytes);
            if (count <= 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(count * 3 - 1);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(buffer[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
