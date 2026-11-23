#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace AssetStudio
{
    public sealed record DetectedCipherCandidate(
        StreamCipherAlgorithm? CipherAlgorithm,
        string SchemeId,
        string DisplayName,
        double Confidence,
        string Evidence,
        long SuggestedSkipBytes = 0,
        long SuggestedRangeStart = 0,
        long SuggestedRangeLength = -1,
        bool? SuggestedPkcs7 = null,
        bool SuggestedIvFromPrefix = false);

    public readonly struct StreamEncryptionBarrier
    {
        public StreamEncryptionBarrier(
            string providerId,
            string filePath,
            string reason,
            double confidence,
            string headerPrefixHex)
            : this(providerId, filePath, reason, confidence, headerPrefixHex, candidateAlgorithms: null)
        {
        }

        public StreamEncryptionBarrier(
            string providerId,
            string filePath,
            string reason,
            double confidence,
            string headerPrefixHex,
            IReadOnlyList<DetectedCipherCandidate>? candidateAlgorithms)
        {
            ProviderId = providerId ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            Reason = reason ?? string.Empty;
            Confidence = confidence;
            HeaderPrefixHex = headerPrefixHex ?? string.Empty;
            CandidateAlgorithms = candidateAlgorithms;
        }

        public string ProviderId { get; }
        public string FilePath { get; }
        public string Reason { get; }
        public double Confidence { get; }
        public string HeaderPrefixHex { get; }
        public IReadOnlyList<DetectedCipherCandidate>? CandidateAlgorithms { get; }
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

        public static bool IsNonStreamDiagnosticHint(string? providerHint)
        {
            if (string.IsNullOrWhiteSpace(providerHint))
            {
                return false;
            }

            string trimmed = providerHint.Trim();
            return string.Equals(trimmed, "Il2CppGlobalMetadata", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "CustomCatalogSchema", StringComparison.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<DetectedCipherCandidate> AnalyzeExplicitProviderHint(
            string? providerHint,
            string? reasonHint = null)
        {
            if (IsNonStreamDiagnosticHint(providerHint))
            {
                return Array.Empty<DetectedCipherCandidate>();
            }

            var candidates = new List<DetectedCipherCandidate>();
            TryBuildExplicitHintCandidates(
                providerHint,
                reasonHint,
                ReadOnlySpan<byte>.Empty,
                totalStreamLength: 0,
                rangeStart: 0,
                rangeLength: -1,
                candidates);
            return candidates;
        }

        public static IReadOnlyList<DetectedCipherCandidate> AnalyzeEncryptionCandidates(
            ReadOnlySpan<byte> probeBuffer,
            long totalStreamLength,
            string? filePath,
            string? providerHint = null,
            string? reasonHint = null)
        {
            if (IsNonStreamDiagnosticHint(providerHint))
            {
                return Array.Empty<DetectedCipherCandidate>();
            }

            var candidates = new List<DetectedCipherCandidate>();

            // 1. Deterministic single match: Block 512 "Encryption" magic header at offset 0
            if (probeBuffer.Length >= 26 && MatchesAt(probeBuffer, 0, DecryptionHelper.EncryptionMagicBytes))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.Block512,
                    "block512",
                    "Block 512",
                    0.98,
                    "Block 512 'Encryption' magic header (26-byte prefix)",
                    SuggestedSkipBytes: 26));
                return candidates;
            }

            // 2. Deterministic single match: offset-shifted valid Unity bundle header
            if (probeBuffer.Length >= 32
                && !TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _)
                && TryFindOffsetUnityBundleHeader(probeBuffer, totalStreamLength, out int bundleOffset, out string sig, out _, out _)
                && bundleOffset > 0)
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.None,
                    "none",
                    "Offset Skip (None)",
                    0.96,
                    $"Valid {sig} header detected at byte offset {bundleOffset}",
                    SuggestedSkipBytes: bundleOffset));
                return candidates;
            }

            // Detect structural clues from probeBuffer (partial header range / duplicate 16B ECB blocks)
            bool hasPartialRange = TryDetectPartialEncryptionRange(probeBuffer, out long partialRangeLength);
            if (!hasPartialRange
                && string.Equals(providerHint, "PartialHeaderEncryptedSerializedFile", StringComparison.OrdinalIgnoreCase))
            {
                hasPartialRange = true;
                partialRangeLength = 0x400;
            }

            long effectiveRangeStart = 0;
            long effectiveRangeLength = hasPartialRange ? partialRangeLength : -1;

            // 3. Explicit provider or catalog hint naming one or more schemes
            if (TryBuildExplicitHintCandidates(
                providerHint,
                reasonHint,
                probeBuffer,
                totalStreamLength,
                effectiveRangeStart,
                effectiveRangeLength,
                candidates))
            {
                return candidates;
            }

            // 4. Structural partial-header encryption narrowing
            if (hasPartialRange)
            {
                int checkLen = (int)Math.Min(partialRangeLength, probeBuffer.Length);
                bool rangeAligned16 = (partialRangeLength % 16) == 0;
                bool duplicateBlocksInRange = rangeAligned16 && checkLen >= 64 && HasDuplicate16ByteBlocks(probeBuffer.Slice(0, checkLen));

                if (duplicateBlocksInRange)
                {
                    candidates.Add(new DetectedCipherCandidate(
                        StreamCipherAlgorithm.AesEcb,
                        "aes-ecb",
                        "AES-ECB",
                        0.88,
                        $"Partial header encryption (0x0..0x{partialRangeLength:X}, {partialRangeLength}B) with repeated 16-byte ciphertext blocks",
                        SuggestedRangeStart: 0,
                        SuggestedRangeLength: partialRangeLength,
                        SuggestedPkcs7: false));
                }

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.Xor,
                    "xor",
                    "XOR",
                    duplicateBlocksInRange ? 0.80 : 0.84,
                    $"Partial header encryption boundary detected (0x0..0x{partialRangeLength:X}, {partialRangeLength}B) — XOR candidate",
                    SuggestedRangeStart: 0,
                    SuggestedRangeLength: partialRangeLength));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCtr,
                    "aes-ctr",
                    "AES-CTR",
                    0.82,
                    $"Partial header encryption boundary detected (0x0..0x{partialRangeLength:X}, {partialRangeLength}B) — stream cipher candidate",
                    SuggestedRangeStart: 0,
                    SuggestedRangeLength: partialRangeLength));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.ChaCha20,
                    "chacha20",
                    "ChaCha20",
                    0.80,
                    $"Partial header encryption boundary detected (0x0..0x{partialRangeLength:X}, {partialRangeLength}B) — stream cipher candidate",
                    SuggestedRangeStart: 0,
                    SuggestedRangeLength: partialRangeLength));

                if (rangeAligned16)
                {
                    candidates.Add(new DetectedCipherCandidate(
                        StreamCipherAlgorithm.AesCbc,
                        "aes-cbc",
                        "AES-CBC",
                        0.78,
                        $"Partial header encryption boundary detected (0x0..0x{partialRangeLength:X}, {partialRangeLength}B, 16-byte aligned)",
                        SuggestedRangeStart: 0,
                        SuggestedRangeLength: partialRangeLength,
                        SuggestedPkcs7: false));
                }

                return candidates;
            }

            // 5. Duplicate 16-byte ciphertext blocks on 16-byte aligned stream -> AES-ECB ranked first
            bool knownLength = totalStreamLength > 0;
            bool is16ByteAligned = knownLength && (totalStreamLength % 16) == 0;
            if ((is16ByteAligned || (!knownLength && probeBuffer.Length >= 64))
                && probeBuffer.Length >= 64
                && HasDuplicate16ByteBlocks(probeBuffer))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesEcb,
                    "aes-ecb",
                    "AES-ECB",
                    0.88,
                    "16-byte aligned ciphertext contains repeated 16-byte blocks (AES-ECB indicator)",
                    SuggestedPkcs7: false));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCbc,
                    "aes-cbc",
                    "AES-CBC",
                    0.72,
                    "16-byte aligned stream (secondary AES-CBC block cipher candidate)",
                    SuggestedPkcs7: true));

                return candidates;
            }

            // 6. Full-stream alignment narrowing
            if (is16ByteAligned)
            {
                string cbcEvidence = totalStreamLength >= 32
                    ? $"16-byte aligned full stream ({totalStreamLength}B) — compatible with AES-CBC (fixed or 16B prefix IV)"
                    : $"16-byte aligned full stream ({totalStreamLength}B) — AES-CBC block cipher candidate";

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCbc,
                    "aes-cbc",
                    "AES-CBC",
                    0.78,
                    cbcEvidence,
                    SuggestedPkcs7: true));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCtr,
                    "aes-ctr",
                    "AES-CTR",
                    0.72,
                    "High-entropy encrypted stream compatible with AES-CTR"));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.ChaCha20,
                    "chacha20",
                    "ChaCha20",
                    0.70,
                    "High-entropy encrypted stream compatible with ChaCha20"));

                return candidates;
            }

            if (knownLength)
            {
                // Non-16-byte-aligned full stream excludes full-stream AES-CBC/AES-ECB
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCtr,
                    "aes-ctr",
                    "AES-CTR",
                    0.80,
                    $"Unaligned stream length ({totalStreamLength}B, not a multiple of 16) — excludes full-stream block ciphers; AES-CTR candidate"));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.ChaCha20,
                    "chacha20",
                    "ChaCha20",
                    0.78,
                    $"Unaligned stream length ({totalStreamLength}B, not a multiple of 16) — ChaCha20 stream cipher candidate"));

                bool lowerEntropy = probeBuffer.Length >= 64 && HasLowerByteEntropy(probeBuffer);
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.Xor,
                    "xor",
                    "XOR",
                    lowerEntropy ? 0.74 : 0.64,
                    lowerEntropy
                        ? $"Unaligned stream ({totalStreamLength}B) with non-uniform byte distribution — repeating/stream XOR candidate"
                        : $"Unaligned stream ({totalStreamLength}B) — fallback XOR stream candidate"));

                return candidates;
            }

            // 7. Fallback when neither probe bytes nor stream length are available (e.g. generic barrier metadata)
            if (!string.IsNullOrWhiteSpace(providerHint)
                || !string.IsNullOrWhiteSpace(reasonHint)
                || !string.IsNullOrWhiteSpace(filePath))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCbc,
                    "aes-cbc",
                    "AES-CBC",
                    0.75,
                    "Generic encrypted container — AES-CBC candidate",
                    SuggestedPkcs7: true));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCtr,
                    "aes-ctr",
                    "AES-CTR",
                    0.70,
                    "Generic encrypted container — AES-CTR candidate"));

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.ChaCha20,
                    "chacha20",
                    "ChaCha20",
                    0.68,
                    "Generic encrypted container — ChaCha20 candidate"));
            }

            return candidates;
        }

        private static bool TryBuildExplicitHintCandidates(
            string? providerHint,
            string? reasonHint,
            ReadOnlySpan<byte> probeBuffer,
            long totalStreamLength,
            long rangeStart,
            long rangeLength,
            List<DetectedCipherCandidate> candidates)
        {
            string combined = string.IsNullOrWhiteSpace(providerHint)
                ? (reasonHint ?? string.Empty)
                : (string.IsNullOrWhiteSpace(reasonHint) ? providerHint : $"{providerHint} {reasonHint}");

            if (string.IsNullOrWhiteSpace(combined))
            {
                return false;
            }

            // Block 512 explicit provider / catalog hint
            if (combined.Contains("Block512", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("EncryptionMagic", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("'Encryption' magic", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.Block512,
                    "block512",
                    "Block 512",
                    0.98,
                    $"Explicit Block 512 hint ({providerHint ?? "catalog"})",
                    SuggestedSkipBytes: 26));
                return true;
            }

            string hintLabel = !string.IsNullOrWhiteSpace(providerHint) ? providerHint! : "catalog hint";

            if (combined.Contains("chacha", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.ChaCha20,
                    "chacha20",
                    "ChaCha20",
                    0.95,
                    $"Provider/catalog hint identifies ChaCha20 ('{hintLabel}')",
                    SuggestedRangeStart: rangeStart,
                    SuggestedRangeLength: rangeLength));
            }

            if (combined.Contains("aes-ctr", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("aesctr", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("aes_ctr", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCtr,
                    "aes-ctr",
                    "AES-CTR",
                    0.95,
                    $"Provider/catalog hint identifies AES-CTR ('{hintLabel}')",
                    SuggestedRangeStart: rangeStart,
                    SuggestedRangeLength: rangeLength));
            }

            if (combined.Contains("aes-ecb", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("aesecb", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("aes_ecb", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesEcb,
                    "aes-ecb",
                    "AES-ECB",
                    0.95,
                    $"Provider/catalog hint identifies AES-ECB ('{hintLabel}')",
                    SuggestedRangeStart: rangeStart,
                    SuggestedRangeLength: rangeLength,
                    SuggestedPkcs7: false));
            }

            if (combined.Contains("aes-cbc", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("aescbc", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("aes_cbc", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCbc,
                    "aes-cbc",
                    "AES-CBC",
                    0.95,
                    $"Provider/catalog hint identifies AES-CBC ('{hintLabel}')",
                    SuggestedRangeStart: rangeStart,
                    SuggestedRangeLength: rangeLength,
                    SuggestedPkcs7: rangeLength < 0));
            }

            // Avoid matching the generic "without a recognizable UnityFS signature or repeating XOR key" barrier reason as a positive XOR hint
            bool mentionsNegativeXor = combined.Contains("without a recognizable", StringComparison.OrdinalIgnoreCase)
                && combined.Contains("repeating XOR key", StringComparison.OrdinalIgnoreCase);

            if (!mentionsNegativeXor && ContainsCipherToken(combined, "xor"))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.Xor,
                    "xor",
                    "XOR",
                    0.94,
                    $"Provider/catalog hint identifies XOR ('{hintLabel}')",
                    SuggestedRangeStart: rangeStart,
                    SuggestedRangeLength: rangeLength));
            }

            if (combined.Contains("offsetbundle", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("offsetskip", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("offset-skip", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("headerskip", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.None,
                    "none",
                    "Offset Skip (None)",
                    0.92,
                    $"Provider/catalog hint identifies header offset skip ('{hintLabel}')"));
            }

            if (candidates.Count > 0)
            {
                return true;
            }

            // Generic "AES" hint without explicit mode (CBC/CTR/ECB)
            if (ContainsCipherToken(combined, "aes"))
            {
                bool knownLength = totalStreamLength > 0;
                bool is16Aligned = !knownLength || (totalStreamLength % 16) == 0 || rangeLength > 0;
                bool hasDup = probeBuffer.Length >= 64 && is16Aligned && HasDuplicate16ByteBlocks(probeBuffer);

                if (hasDup)
                {
                    candidates.Add(new DetectedCipherCandidate(
                        StreamCipherAlgorithm.AesEcb,
                        "aes-ecb",
                        "AES-ECB",
                        0.92,
                        $"Provider/catalog hint identifies AES ('{hintLabel}') + repeated 16B blocks",
                        SuggestedRangeStart: rangeStart,
                        SuggestedRangeLength: rangeLength,
                        SuggestedPkcs7: false));
                }

                if (is16Aligned)
                {
                    candidates.Add(new DetectedCipherCandidate(
                        StreamCipherAlgorithm.AesCbc,
                        "aes-cbc",
                        "AES-CBC",
                        hasDup ? 0.84 : 0.90,
                        $"Provider/catalog hint identifies AES ('{hintLabel}') — CBC mode candidate",
                        SuggestedRangeStart: rangeStart,
                        SuggestedRangeLength: rangeLength,
                        SuggestedPkcs7: rangeLength < 0));
                }

                candidates.Add(new DetectedCipherCandidate(
                    StreamCipherAlgorithm.AesCtr,
                    "aes-ctr",
                    "AES-CTR",
                    is16Aligned ? 0.85 : 0.92,
                    $"Provider/catalog hint identifies AES ('{hintLabel}') — CTR mode candidate",
                    SuggestedRangeStart: rangeStart,
                    SuggestedRangeLength: rangeLength));

                if (!hasDup && is16Aligned)
                {
                    candidates.Add(new DetectedCipherCandidate(
                        StreamCipherAlgorithm.AesEcb,
                        "aes-ecb",
                        "AES-ECB",
                        0.76,
                        $"Provider/catalog hint identifies AES ('{hintLabel}') — ECB mode candidate",
                        SuggestedRangeStart: rangeStart,
                        SuggestedRangeLength: rangeLength,
                        SuggestedPkcs7: false));
                }

                return true;
            }

            return false;
        }

        private static bool ContainsCipherToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
            {
                return false;
            }

            int start = 0;
            while (start <= text.Length - token.Length)
            {
                int idx = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return false;
                }

                bool leftOk = idx == 0
                    || !char.IsLetter(text[idx - 1])
                    || (char.IsUpper(text[idx]) && char.IsLower(text[idx - 1]));

                int afterIdx = idx + token.Length;
                bool rightOk = afterIdx >= text.Length
                    || !char.IsLetter(text[afterIdx])
                    || (char.IsUpper(text[afterIdx]) && char.IsLower(text[afterIdx - 1]))
                    || (char.IsUpper(text[idx]) && char.IsUpper(text[afterIdx - 1]) && char.IsUpper(text[afterIdx])
                        && (afterIdx + 1 < text.Length && char.IsLower(text[afterIdx + 1])));

                if (leftOk && rightOk)
                {
                    return true;
                }

                start = idx + 1;
            }

            return false;
        }

        private static bool TryDetectPartialEncryptionRange(ReadOnlySpan<byte> probeBuffer, out long rangeLength)
        {
            rangeLength = -1;
            if (probeBuffer.Length < 512)
            {
                return false;
            }

            // Check 128-byte partial prefix encryption (0x000..0x07F encrypted, 0x080+ structured zeros)
            int nonZeroFirst128 = CountNonZeroBytes(probeBuffer.Slice(0, 128));
            if (nonZeroFirst128 >= 112)
            {
                int zerosSecond128 = 128 - CountNonZeroBytes(probeBuffer.Slice(128, 128));
                int zerosNext256 = 256 - CountNonZeroBytes(probeBuffer.Slice(256, 256));
                if (zerosSecond128 >= 48 && zerosNext256 >= 96)
                {
                    rangeLength = 128;
                    return true;
                }
            }

            // Scan 256-byte pages for transition from encrypted header to zero-heavy structured pages
            int firstPageNonZero = CountNonZeroBytes(probeBuffer.Slice(0, 256));
            if (firstPageNonZero < 210)
            {
                return false;
            }

            for (int pageOffset = 256; pageOffset + 256 <= probeBuffer.Length; pageOffset += 256)
            {
                int zeros = 256 - CountNonZeroBytes(probeBuffer.Slice(pageOffset, 256));
                if (zeros >= 96)
                {
                    rangeLength = pageOffset;
                    return true;
                }
            }

            return false;
        }

        private static bool HasDuplicate16ByteBlocks(ReadOnlySpan<byte> buffer)
        {
            int blockCount = buffer.Length / 16;
            if (blockCount < 2)
            {
                return false;
            }

            var seen = new HashSet<(ulong Lo, ulong Hi)>();
            for (int i = 0; i < blockCount; i++)
            {
                var block = buffer.Slice(i * 16, 16);
                byte first = block[0];
                bool allSame = true;
                for (int j = 1; j < 16; j++)
                {
                    if (block[j] != first)
                    {
                        allSame = false;
                        break;
                    }
                }

                if (allSame)
                {
                    continue;
                }

                ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(0, 8));
                ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8, 8));
                if (!seen.Add((lo, hi)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLowerByteEntropy(ReadOnlySpan<byte> buffer)
        {
            int sampleLen = Math.Min(buffer.Length, 1024);
            if (sampleLen < 64)
            {
                return false;
            }

            Span<int> counts = stackalloc int[256];
            int maxFreq = 0;
            for (int i = 0; i < sampleLen; i++)
            {
                int c = ++counts[buffer[i]];
                if (c > maxFreq)
                {
                    maxFreq = c;
                }
            }

            // Uniform random data over 64..1024 bytes rarely exceeds ~4% for a single byte;
            // structured or weakly obfuscated payloads exhibit strong frequency spikes or low Shannon entropy.
            if (sampleLen >= 256 && maxFreq * 25 >= sampleLen) // >= 4% single byte frequency
            {
                return true;
            }

            double entropy = 0.0;
            double invLen = 1.0 / sampleLen;
            for (int i = 0; i < 256; i++)
            {
                if (counts[i] > 0)
                {
                    double p = counts[i] * invLen;
                    entropy -= p * Math.Log2(p);
                }
            }

            double threshold = sampleLen >= 512 ? 7.0 : 6.4;
            return entropy < threshold;
        }

        public static bool TryDetectEncryptionBarrier(
            ReadOnlySpan<byte> probeBuffer,
            long totalStreamLength,
            string? filePath,
            out StreamEncryptionBarrier barrier)
        {
            barrier = default;
            if (string.IsNullOrWhiteSpace(filePath) || probeBuffer.Length < 26)
            {
                return false;
            }

            // 0. Check for unresolved Block 512 "Encryption" magic header
            if (MatchesAt(probeBuffer, 0, DecryptionHelper.EncryptionMagicBytes))
            {
                string prefixHex = FormatHexPrefix(probeBuffer, 16);
                var candidates = AnalyzeEncryptionCandidates(probeBuffer, totalStreamLength, filePath, "Block512MissingCatalog");
                barrier = new StreamEncryptionBarrier(
                    providerId: "Block512MissingCatalog",
                    filePath: filePath,
                    reason: $"Block 512 'Encryption' container header detected (prefix: {prefixHex}), but no matching Addressables catalog entry was found to derive the decryption token.",
                    confidence: 0.98,
                    headerPrefixHex: prefixHex,
                    candidateAlgorithms: candidates);
                return true;
            }

            if (probeBuffer.Length < 32)
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
                    var candidates = AnalyzeEncryptionCandidates(probeBuffer, totalStreamLength, filePath, "VfsChunkContainer");
                    barrier = new StreamEncryptionBarrier(
                        providerId: "VfsChunkContainer",
                        filePath: filePath,
                        reason: $"File is a proprietary {kindLabel} outside UnityFS format (header: {prefixHex}).",
                        confidence: 0.95,
                        headerPrefixHex: prefixHex,
                        candidateAlgorithms: candidates);
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
                    var candidates = AnalyzeEncryptionCandidates(probeBuffer, totalStreamLength, filePath, "PartialHeaderEncryptedSerializedFile");
                    barrier = new StreamEncryptionBarrier(
                        providerId: "PartialHeaderEncryptedSerializedFile",
                        filePath: filePath,
                        reason: $"SerializedFile header (0x0000..0x03FF) appears encrypted (prefix: {prefixHex}) while subsequent pages (0x0400+) contain structured serialized data.",
                        confidence: 0.94,
                        headerPrefixHex: prefixHex,
                        candidateAlgorithms: candidates);
                    return true;
                }
            }

            // 3. Check for encrypted .enc / .bundle.enc containers (high-entropy or ECB repeated-block non-Unity, non-media payloads)
            if (string.Equals(ext, ".enc", StringComparison.OrdinalIgnoreCase)
                && totalStreamLength >= 128
                && !TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _)
                && !IsSerializedFileHeader(probeBuffer, totalStreamLength)
                && !HasKnownPlainMediaOrArchiveSignature(probeBuffer))
            {
                int headerCheckLen = Math.Min(probeBuffer.Length, 256);
                var headerSlice = probeBuffer.Slice(0, headerCheckLen);
                int nonZero = CountNonZeroBytes(headerSlice);
                int distinct = CountDistinctBytes(headerSlice);
                bool looksHighEntropy = distinct >= Math.Min(96, headerCheckLen / 2);
                bool looksEcbCiphertext = (totalStreamLength % 16) == 0
                    && HasDuplicate16ByteBlocks(probeBuffer)
                    && CountHighBitBytes(headerSlice) >= headerCheckLen / 8;
                if (nonZero >= (headerCheckLen * 7) / 8 && (looksHighEntropy || looksEcbCiphertext))
                {
                    string prefixHex = FormatHexPrefix(probeBuffer, 16);
                    var candidates = AnalyzeEncryptionCandidates(probeBuffer, totalStreamLength, filePath, "EncryptedBundleContainer");
                    barrier = new StreamEncryptionBarrier(
                        providerId: "EncryptedBundleContainer",
                        filePath: filePath,
                        reason: $"Encrypted container ({fileName}) uses a non-standard cipher without a recognizable UnityFS header (prefix: {prefixHex}).",
                        confidence: 0.92,
                        headerPrefixHex: prefixHex,
                        candidateAlgorithms: candidates);
                    return true;
                }
            }

            // 4. Check for cipher-encrypted .bundle files (high-entropy or ECB repeated-block encrypted header/blocksInfo prefix)
            if (string.Equals(ext, ".bundle", StringComparison.OrdinalIgnoreCase)
                && totalStreamLength >= 128
                && probeBuffer.Length >= 128
                && !TryParseUnityBundleHeader(probeBuffer, 0, totalStreamLength, out _, out _, out _, out _)
                && !IsSerializedFileHeader(probeBuffer, totalStreamLength)
                && !HasKnownPlainMediaOrArchiveSignature(probeBuffer)
                && !TryFindOffsetUnityBundleHeader(probeBuffer, totalStreamLength, out _, out _, out _, out _)
                && !TryDetectRepeatingXorUnityFsKey(probeBuffer, totalStreamLength, out _, out _, out _))
            {
                var header128 = probeBuffer.Slice(0, 128);
                int nonZero128 = CountNonZeroBytes(header128);
                int distinct128 = CountDistinctBytes(header128);
                bool looksHighEntropy = distinct128 >= 72;
                bool looksEcbCiphertext = (totalStreamLength % 16) == 0
                    && HasDuplicate16ByteBlocks(probeBuffer)
                    && CountHighBitBytes(header128) >= 16;
                if (nonZero128 >= 112 && (looksHighEntropy || looksEcbCiphertext))
                {
                    string prefixHex = FormatHexPrefix(probeBuffer, 16);
                    var candidates = AnalyzeEncryptionCandidates(probeBuffer, totalStreamLength, filePath, "EncryptedUnityBundle");
                    barrier = new StreamEncryptionBarrier(
                        providerId: "EncryptedUnityBundle",
                        filePath: filePath,
                        reason: $"Bundle header appears cipher-encrypted without a recognizable UnityFS signature or repeating XOR key (prefix: {prefixHex}).",
                        confidence: 0.90,
                        headerPrefixHex: prefixHex,
                        candidateAlgorithms: candidates);
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

        private static int CountHighBitBytes(ReadOnlySpan<byte> span)
        {
            int count = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if ((span[i] & 0x80) != 0)
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
