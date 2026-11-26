#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AssetStudio
{
    public sealed class CapturedCipher
    {
        internal CapturedCipher(CapturedKey key, bool ivFromStreamPrefix, byte[] fixedIv, byte[] mask, int verifiedBytes, int zeroBlocksAfterMask)
        {
            Key = key;
            IvFromStreamPrefix = ivFromStreamPrefix;
            FixedIv = fixedIv;
            Mask = mask;
            VerifiedBytes = verifiedBytes;
            ZeroBlocksAfterMask = zeroBlocksAfterMask;
        }

        public CapturedKey Key { get; }
        public bool IvFromStreamPrefix { get; }
        public byte[] FixedIv { get; }
        public byte[] Mask { get; }
        public int VerifiedBytes { get; }
        public int ZeroBlocksAfterMask { get; }
    }

    public static class RuntimeCipherRecovery
    {
        private const int MinimumEvidenceBytes = 64;

        public static bool TryRecover(CapturedKey key, IReadOnlyList<string> sampleFiles, IReadOnlyList<PlaintextBundleDump> dumps, out CapturedCipher? cipher)
        {
            cipher = null;
            if (sampleFiles.Count == 0 || dumps.Count == 0)
            {
                return false;
            }

            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in sampleFiles)
            {
                try
                {
                    lengths[file] = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (lengths.Count == 0)
            {
                return false;
            }

            using var state = new CipherState(key);
            int[] offsets = key.Mode == CapturedCipherMode.Ecb ? new[] { 0 } : key.Mode == CapturedCipherMode.Ctr ? new[] { 16 } : new[] { 0, key.BlockSize };
            CapturedCipher? best = null;
            long bestScore = -1;
            foreach (PlaintextBundleDump dump in dumps.OrderByDescending(static item => item.Data.Length))
            {
                if (!GameMemoryCapture.TryReadBundleHeader(dump.Data, out long declaredSize, out _))
                {
                    continue;
                }

                foreach ((string path, long length) in lengths)
                {
                    foreach (int offset in offsets)
                    {
                        long padding = length - offset - declaredSize;
                        if ((key.Padding ? padding is < 1 or > 16 : padding != 0) || (length - offset) % key.BlockSize != 0)
                        {
                            continue;
                        }

                        byte[] head;
                        try
                        {
                            head = ReadHead(path, (int)Math.Min(length, dump.Data.Length + 64L));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        CapturedCipher? candidate = TryDerive(state, key, offset > 0, dump.Data, declaredSize, head);
                        if (candidate == null || !IsTrustworthy(candidate))
                        {
                            continue;
                        }

                        long score = (long)candidate.VerifiedBytes * 64 + Math.Min(candidate.ZeroBlocksAfterMask, 64);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }
            }

            cipher = best;
            return best != null;
        }

        public static bool IsMaskConfirmed(CapturedCipher cipher) => cipher.VerifiedBytes >= MinimumEvidenceBytes || cipher.ZeroBlocksAfterMask > 0;

        private static bool IsTrustworthy(CapturedCipher cipher) => cipher.VerifiedBytes >= cipher.Key.BlockSize || cipher.ZeroBlocksAfterMask > 0;

        public static int ValidateOnFiles(CapturedCipher cipher, IReadOnlyList<string> files, out int checkedCount, int maxFiles = 8)
        {
            int valid = 0;
            checkedCount = 0;
            foreach (string file in files)
            {
                if (checkedCount >= maxFiles)
                {
                    break;
                }

                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (!TryOpenDecoded(stream, cipher, out Stream decoded))
                    {
                        continue;
                    }

                    using (decoded)
                    {
                        checkedCount++;
                        if (DeclarativeProfileTransformProvider.ValidateDecryptedUnityHeader(decoded, out _))
                        {
                            valid++;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
                {
                }
            }

            return valid;
        }

        public static bool TryOpenDecoded(Stream raw, CapturedCipher cipher, out Stream decoded)
        {
            decoded = Stream.Null;
            CapturedKey key = cipher.Key;
            int blockSize = key.BlockSize;
            byte[] iv = cipher.FixedIv;
            long skip = 0;
            if (cipher.IvFromStreamPrefix)
            {
                if (raw.Length < blockSize * 3)
                {
                    return false;
                }

                iv = new byte[blockSize];
                raw.Position = 0;
                int read = 0;
                while (read < iv.Length)
                {
                    int count = raw.Read(iv, read, iv.Length - read);
                    if (count <= 0)
                    {
                        return false;
                    }

                    read += count;
                }

                skip = blockSize;
            }

            Stream cipherStream;
            if (key.Kind == CapturedCipherKind.Rijndael256)
            {
                cipherStream = new RijndaelCbcTransformStream(raw, new RijndaelBlockCipher(key.Key, 256), iv, headerSkipBytes: skip, pkcs7Padding: key.Padding)
                {
                    LeaveBaseStreamOpen = true,
                };
            }
            else
            {
                cipherStream = new CipherTransformStream(
                    raw,
                    key.Mode == CapturedCipherMode.Cbc ? StreamCipherAlgorithm.AesCbc
                        : key.Mode == CapturedCipherMode.Ecb ? StreamCipherAlgorithm.AesEcb
                        : StreamCipherAlgorithm.AesCtr,
                    key.Key,
                    key.Mode == CapturedCipherMode.Ecb ? null : iv,
                    headerSkipBytes: skip,
                    rangeLength: -1,
                    pkcs7Padding: key.Padding,
                    counterStart: key.CounterStart,
                    counterLittleEndian: key.CounterLittleEndian)
                {
                    LeaveBaseStreamOpen = true,
                };
            }

            decoded = cipher.Mask.Length > 0 ? new HeaderMaskStream(cipherStream, cipher.Mask) : cipherStream;
            return true;
        }

        private static CapturedCipher? TryDerive(CipherState state, CapturedKey key, bool ivPrefix, byte[] plain, long declaredSize, byte[] head)
        {
            int blockSize = key.BlockSize;
            int offset = ivPrefix ? blockSize : 0;
            int usable = (int)Math.Min(declaredSize, plain.Length);
            int availableBlocks = (head.Length - offset) / blockSize;
            int blocks = Math.Min(usable / blockSize, availableBlocks);
            if (blocks < 2)
            {
                return null;
            }

            var mask = new byte[blocks * blockSize];
            var iv = new byte[blockSize];
            if (ivPrefix)
            {
                Array.Copy(head, 0, iv, 0, blockSize);
            }

            for (int i = 0; i < blocks; i++)
            {
                ComputeUnmasked(state, key, head, offset, i, iv);
                for (int m = 0; m < blockSize; m++)
                {
                    mask[i * blockSize + m] = (byte)(plain[i * blockSize + m] ^ state.Block[m]);
                }

                if (i == 0 && key.Mode == CapturedCipherMode.Cbc && !ivPrefix)
                {
                    Array.Copy(mask, 0, iv, 0, blockSize);
                    Array.Clear(mask, 0, blockSize);
                }
            }

            int first = 0;
            while (first < blocks && IsZeroBlock(mask, first, blockSize))
            {
                first++;
            }

            int maskBlocks = first >= blocks ? 0 : first;
            while (maskBlocks < blocks && !IsZeroBlock(mask, maskBlocks, blockSize))
            {
                maskBlocks++;
            }

            int zeroBlocksAfterMask = 0;
            while (maskBlocks + zeroBlocksAfterMask < blocks && IsZeroBlock(mask, maskBlocks + zeroBlocksAfterMask, blockSize))
            {
                zeroBlocksAfterMask++;
            }

            int verified = 0;
            bool matching = true;
            for (int i = maskBlocks; i < blocks && matching; i++)
            {
                ComputeUnmasked(state, key, head, offset, i, iv);
                int limit = Math.Min(blockSize, usable - i * blockSize);
                for (int m = 0; m < limit; m++)
                {
                    if (state.Block[m] != plain[i * blockSize + m])
                    {
                        matching = false;
                        break;
                    }

                    verified++;
                }
            }

            return new CapturedCipher(key, ivPrefix, iv, mask.AsSpan(0, maskBlocks * blockSize).ToArray(), verified, zeroBlocksAfterMask);
        }

        private static void ComputeUnmasked(CipherState state, CapturedKey key, byte[] head, int offset, int index, byte[] iv)
        {
            int blockSize = key.BlockSize;
            ReadOnlySpan<byte> cipher = head.AsSpan(offset + index * blockSize, blockSize);
            Span<byte> output = state.Block;
            switch (key.Mode)
            {
                case CapturedCipherMode.Cbc:
                {
                    DecryptBlock(state, key, cipher, output);
                    ReadOnlySpan<byte> previous = index == 0 ? iv : head.AsSpan(offset + (index - 1) * blockSize, blockSize);
                    for (int i = 0; i < blockSize; i++)
                    {
                        output[i] ^= previous[i];
                    }

                    break;
                }

                case CapturedCipherMode.Ecb:
                {
                    DecryptBlock(state, key, cipher, output);
                    break;
                }

                default:
                {
                    GameMemoryCapture.BuildCounterBlock(iv, (ulong)index + key.CounterStart, key.CounterLittleEndian, state.Counter);
                    state.Aes!.EncryptEcb(state.Counter, output, PaddingMode.None);
                    for (int i = 0; i < blockSize; i++)
                    {
                        output[i] ^= cipher[i];
                    }

                    break;
                }
            }
        }

        private static void DecryptBlock(CipherState state, CapturedKey key, ReadOnlySpan<byte> cipher, Span<byte> output)
        {
            if (key.Kind == CapturedCipherKind.Rijndael256)
            {
                RijndaelBlockCipher.DecryptBlock(state.Schedule, 256, state.RijndaelRounds, cipher, output);
            }
            else
            {
                state.Aes!.DecryptEcb(cipher, output, PaddingMode.None);
            }
        }

        private static bool IsZeroBlock(byte[] mask, int block, int blockSize)
        {
            for (int i = block * blockSize; i < (block + 1) * blockSize; i++)
            {
                if (mask[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] ReadHead(string path, int count)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int size = (int)Math.Min(count, stream.Length);
            var buffer = new byte[size];
            int total = 0;
            while (total < size)
            {
                int read = stream.Read(buffer, total, size - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total == size ? buffer : buffer.AsSpan(0, total).ToArray();
        }

        private sealed class CipherState : IDisposable
        {
            public CipherState(CapturedKey key)
            {
                Block = new byte[key.BlockSize];
                Counter = new byte[16];
                if (key.Kind == CapturedCipherKind.Rijndael256)
                {
                    Schedule = new uint[RijndaelBlockCipher.MaxKeyScheduleWords];
                    RijndaelRounds = RijndaelBlockCipher.ExpandDecryptKey(key.Key, 256, Schedule);
                    return;
                }

                Aes = Aes.Create();
                Aes.Mode = CipherMode.ECB;
                Aes.Padding = PaddingMode.None;
                Aes.Key = key.Key;
            }

            public Aes? Aes { get; }
            public uint[] Schedule { get; } = Array.Empty<uint>();
            public int RijndaelRounds { get; }
            public byte[] Block { get; }
            public byte[] Counter { get; }

            public void Dispose()
            {
                Aes?.Dispose();
            }
        }
    }
}
