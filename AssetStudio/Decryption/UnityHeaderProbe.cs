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

        private static readonly byte[] UnityFsPrefix5xx = Encoding.ASCII.GetBytes("5.x.");

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

            // 1. Fast path: 1-, 2-, or 4-byte repeating key (first 4 bytes of "UnityFS\0" match next 4 bytes)
            Span<byte> candidateKey4 = stackalloc byte[4];
            bool matches4 = true;
            bool anyNonZero4 = false;
            for (int i = 0; i < 4; i++)
            {
                byte k = (byte)(header[i] ^ UnityFsMagicWithNull[i]);
                byte kSecondWord = (byte)(header[i + 4] ^ UnityFsMagicWithNull[i + 4]);
                if (k != kSecondWord)
                {
                    matches4 = false;
                    break;
                }

                candidateKey4[i] = k;
                if (k != 0)
                {
                    anyNonZero4 = true;
                }
            }

            if (matches4 && anyNonZero4
                && TryValidateXorUnityFsCandidate(header, totalStreamLength, candidateKey4, out version, out unityVersion))
            {
                xorKey = ReduceRepeatingKey(candidateKey4);
                return true;
            }

            // 2. 8-byte repeating key derived directly from "UnityFS\0"
            Span<byte> candidateKey8 = stackalloc byte[8];
            bool anyNonZero8 = false;
            for (int i = 0; i < 8; i++)
            {
                byte k = (byte)(header[i] ^ UnityFsMagicWithNull[i]);
                candidateKey8[i] = k;
                if (k != 0)
                {
                    anyNonZero8 = true;
                }
            }

            if (anyNonZero8
                && TryValidateXorUnityFsCandidate(header, totalStreamLength, candidateKey8, out version, out unityVersion))
            {
                xorKey = ReduceRepeatingKey(candidateKey8);
                return true;
            }

            // 3. 16-byte repeating key derived from "UnityFS\0" + big-endian format version (6..8) + "5.x."
            if (header.Length >= 36)
            {
                Span<byte> candidateKey16 = stackalloc byte[16];
                ReadOnlySpan<byte> candidateVersions = stackalloc byte[] { 8, 7, 6 };
                for (int vIdx = 0; vIdx < candidateVersions.Length; vIdx++)
                {
                    byte fmtVer = candidateVersions[vIdx];
                    bool anyNonZero16 = false;
                    for (int i = 0; i < 8; i++)
                    {
                        byte k = (byte)(header[i] ^ UnityFsMagicWithNull[i]);
                        candidateKey16[i] = k;
                        if (k != 0) anyNonZero16 = true;
                    }

                    candidateKey16[8] = header[8];
                    candidateKey16[9] = header[9];
                    candidateKey16[10] = header[10];
                    candidateKey16[11] = (byte)(header[11] ^ fmtVer);
                    for (int i = 0; i < 4; i++)
                    {
                        byte k = (byte)(header[12 + i] ^ UnityFsPrefix5xx[i]);
                        candidateKey16[12 + i] = k;
                    }
                    for (int i = 8; i < 16; i++)
                    {
                        if (candidateKey16[i] != 0) anyNonZero16 = true;
                    }

                    if (!anyNonZero16)
                    {
                        continue;
                    }

                    // Verify trailing "x\0" of "5.x.x\0" at [16..17] before full header validation
                    if ((byte)(header[16] ^ candidateKey16[0]) != (byte)'x'
                        || (byte)(header[17] ^ candidateKey16[1]) != 0)
                    {
                        continue;
                    }

                    if (TryValidateXorUnityFsCandidate(header, totalStreamLength, candidateKey16, out version, out unityVersion))
                    {
                        xorKey = ReduceRepeatingKey(candidateKey16);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryValidateXorUnityFsCandidate(
            ReadOnlySpan<byte> header,
            long totalStreamLength,
            ReadOnlySpan<byte> candidateKey,
            out uint version,
            out string unityVersion)
        {
            int sampleLen = Math.Min(header.Length, 128);
            Span<byte> decoded = stackalloc byte[sampleLen];
            header.Slice(0, sampleLen).CopyTo(decoded);
            XorBytesRepeating(decoded, 0, candidateKey);

            return TryParseUnityBundleHeader(
                decoded,
                0,
                totalStreamLength,
                out _,
                out version,
                out unityVersion,
                out _,
                requireStrictValidation: true);
        }

        private static byte[] ReduceRepeatingKey(ReadOnlySpan<byte> key)
        {
            for (int period = 1; period < key.Length; period++)
            {
                if (key.Length % period != 0)
                {
                    continue;
                }

                bool repeats = true;
                for (int i = period; i < key.Length; i++)
                {
                    if (key[i] != key[i % period])
                    {
                        repeats = false;
                        break;
                    }
                }

                if (repeats)
                {
                    return key.Slice(0, period).ToArray();
                }
            }

            return key.ToArray();
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

            // 3. Check for encrypted .enc / .bundle.enc containers (high-entropy non-Unity, non-media payloads)
            if (string.Equals(ext, ".enc", StringComparison.OrdinalIgnoreCase)
                && totalStreamLength >= 128
                && !TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _)
                && !IsSerializedFileHeader(probeBuffer, totalStreamLength)
                && !HasKnownPlainMediaOrArchiveSignature(probeBuffer))
            {
                int headerCheckLen = Math.Min(probeBuffer.Length, 256);
                int nonZero = CountNonZeroBytes(probeBuffer.Slice(0, headerCheckLen));
                int distinct = CountDistinctBytes(probeBuffer.Slice(0, headerCheckLen));
                if (nonZero >= (headerCheckLen * 7) / 8 && distinct >= Math.Min(96, headerCheckLen / 2))
                {
                    string prefixHex = FormatHexPrefix(probeBuffer, 16);
                    barrier = new StreamEncryptionBarrier(
                        providerId: "EncryptedBundleContainer",
                        filePath: filePath,
                        reason: $"Encrypted container ({fileName}) uses a non-standard cipher without a recognizable UnityFS header (prefix: {prefixHex}).",
                        confidence: 0.92,
                        headerPrefixHex: prefixHex);
                    return true;
                }
            }

            // 4. Check for partial-header-encrypted .bundle files (high-entropy encrypted header/blocksInfo prefix
            //    followed by zero-padded or structured payload pages)
            if (string.Equals(ext, ".bundle", StringComparison.OrdinalIgnoreCase)
                && totalStreamLength >= 512
                && probeBuffer.Length >= 512
                && !TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _)
                && !IsSerializedFileHeader(probeBuffer, totalStreamLength)
                && !HasKnownPlainMediaOrArchiveSignature(probeBuffer)
                && !TryFindOffsetUnityBundleHeader(probeBuffer, totalStreamLength, out _, out _, out _, out _)
                && !TryDetectRepeatingXorUnityFsKey(probeBuffer, totalStreamLength, out _, out _, out _))
            {
                int nonZero128 = CountNonZeroBytes(probeBuffer.Slice(0, 128));
                int distinct128 = CountDistinctBytes(probeBuffer.Slice(0, 128));
                if (nonZero128 >= 112 && distinct128 >= 72)
                {
                    string prefixHex = FormatHexPrefix(probeBuffer, 16);
                    barrier = new StreamEncryptionBarrier(
                        providerId: "EncryptedUnityBundle",
                        filePath: filePath,
                        reason: $"Bundle header appears cipher-encrypted without a recognizable UnityFS signature or repeating XOR key (prefix: {prefixHex}).",
                        confidence: 0.90,
                        headerPrefixHex: prefixHex);
                    return true;
                }
            }

            return false;
        }

        private static bool HasKnownPlainMediaOrArchiveSignature(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 4)
            {
                return false;
            }

            // Common unencrypted container/media headers (CRID, @UTF, AFS2, RIFF, OggS, ZIP, GZIP, JSON)
            if ((buf[0] == 'C' && buf[1] == 'R' && buf[2] == 'I' && buf[3] == 'D')
                || (buf[0] == '@' && buf[1] == 'U' && buf[2] == 'T' && buf[3] == 'F')
                || (buf[0] == 'A' && buf[1] == 'F' && buf[2] == 'S' && buf[3] == '2')
                || (buf[0] == 'R' && buf[1] == 'I' && buf[2] == 'F' && buf[3] == 'F')
                || (buf[0] == 'O' && buf[1] == 'g' && buf[2] == 'g' && buf[3] == 'S')
                || (buf[0] == 'P' && buf[1] == 'K' && (buf[2] == 3 || buf[2] == 5))
                || (buf[0] == 0x1F && buf[1] == 0x8B))
            {
                return true;
            }

            return false;
        }

        private static int CountDistinctBytes(ReadOnlySpan<byte> span)
        {
            Span<byte> seen = stackalloc byte[32];
            int count = 0;
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                int idx = b >> 3;
                byte bit = (byte)(1 << (b & 7));
                if ((seen[idx] & bit) == 0)
                {
                    seen[idx] |= bit;
                    count++;
                }
            }
            return count;
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
