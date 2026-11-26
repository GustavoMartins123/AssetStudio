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
        internal CapturedCipher(byte[] key, bool ivFromStreamPrefix, byte[] fixedIv, byte[] mask, int verifiedBytes, int zeroBlocksAfterMask)
        {
            Key = key;
            IvFromStreamPrefix = ivFromStreamPrefix;
            FixedIv = fixedIv;
            Mask = mask;
            VerifiedBytes = verifiedBytes;
            ZeroBlocksAfterMask = zeroBlocksAfterMask;
        }

        public byte[] Key { get; }
        public bool IvFromStreamPrefix { get; }
        public byte[] FixedIv { get; }
        public byte[] Mask { get; }
        public int VerifiedBytes { get; }
        public int ZeroBlocksAfterMask { get; }
    }

    public static class RuntimeCipherRecovery
    {
        private const int MinimumVerifiedBytes = 64;
        private const int MinimumEvidenceBytes = 16;

        public static bool TryRecover(byte[] key, IReadOnlyList<string> sampleFiles, IReadOnlyList<PlaintextBundleDump> dumps, out CapturedCipher? cipher)
        {
            cipher = null;
            if (key.Length is not (16 or 24 or 32) || sampleFiles.Count == 0 || dumps.Count == 0)
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

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            CapturedCipher? best = null;
            long bestScore = -1;
            foreach (PlaintextBundleDump dump in dumps.OrderByDescending(item => item.Data.Length))
            {
                if (!GameMemoryCapture.TryReadBundleHeader(dump.Data, out long declaredSize, out _))
                {
                    continue;
                }

                foreach ((string path, long length) in lengths)
                {
                    for (int mode = 0; mode < 2; mode++)
                    {
                        bool ivPrefix = mode == 1;
                        int offset = ivPrefix ? 16 : 0;
                        long cipherLength = length - offset;
                        long padding = cipherLength - declaredSize;
                        if (padding is < 1 or > 16 || cipherLength % 16 != 0)
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

                        CapturedCipher? candidate = TryDerive(decryptor, key, ivPrefix, dump.Data, declaredSize, head);
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

        public static bool IsMaskConfirmed(CapturedCipher cipher) => cipher.VerifiedBytes >= MinimumVerifiedBytes || cipher.ZeroBlocksAfterMask > 0;

        private static bool IsTrustworthy(CapturedCipher cipher) => cipher.VerifiedBytes >= MinimumEvidenceBytes || cipher.ZeroBlocksAfterMask > 0;

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
            byte[] iv = cipher.FixedIv;
            long skip = 0;
            if (cipher.IvFromStreamPrefix)
            {
                if (raw.Length < 48)
                {
                    return false;
                }

                iv = new byte[16];
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

                skip = 16;
            }

            var cipherStream = new CipherTransformStream(
                raw,
                StreamCipherAlgorithm.AesCbc,
                cipher.Key,
                iv,
                headerSkipBytes: skip,
                rangeLength: -1,
                pkcs7Padding: true)
            {
                LeaveBaseStreamOpen = true,
            };
            decoded = cipher.Mask.Length > 0 ? new HeaderMaskStream(cipherStream, cipher.Mask) : cipherStream;
            return true;
        }

        private static CapturedCipher? TryDerive(ICryptoTransform decryptor, byte[] key, bool ivPrefix, byte[] plain, long declaredSize, byte[] head)
        {
            int offset = ivPrefix ? 16 : 0;
            int usable = (int)Math.Min(declaredSize, plain.Length);
            int availableBlocks = (head.Length - offset) / 16;
            int blocks = Math.Min(usable / 16, availableBlocks);
            if (blocks < 2)
            {
                return null;
            }

            var mask = new byte[blocks * 16];
            var iv = new byte[16];
            if (ivPrefix)
            {
                Array.Copy(head, 0, iv, 0, 16);
            }

            var block = new byte[16];
            var decrypted = new byte[16];
            for (int i = 0; i < blocks; i++)
            {
                Array.Copy(head, offset + i * 16, block, 0, 16);
                decryptor.TransformBlock(block, 0, 16, decrypted, 0);
                for (int m = 0; m < 16; m++)
                {
                    int p = i * 16 + m;
                    byte previous = i == 0 ? iv[m] : head[offset + (i - 1) * 16 + m];
                    mask[p] = (byte)(plain[p] ^ decrypted[m] ^ previous);
                }

                if (i == 0 && !ivPrefix)
                {
                    Array.Copy(mask, 0, iv, 0, 16);
                    Array.Clear(mask, 0, 16);
                }
            }

            int first = 0;
            while (first < blocks && IsZeroBlock(mask, first))
            {
                first++;
            }

            int maskBlocks = first >= blocks ? 0 : first;
            while (maskBlocks < blocks && !IsZeroBlock(mask, maskBlocks))
            {
                maskBlocks++;
            }

            int zeroBlocksAfterMask = 0;
            while (maskBlocks + zeroBlocksAfterMask < blocks && IsZeroBlock(mask, maskBlocks + zeroBlocksAfterMask))
            {
                zeroBlocksAfterMask++;
            }

            int verified = CountVerifiedBytes(key, iv, offset, mask, maskBlocks, head, plain, usable, availableBlocks);
            return new CapturedCipher(key, ivPrefix, iv, mask.AsSpan(0, maskBlocks * 16).ToArray(), verified, zeroBlocksAfterMask);
        }

        private static bool IsZeroBlock(byte[] mask, int block)
        {
            for (int i = block * 16; i < block * 16 + 16; i++)
            {
                if (mask[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static int CountVerifiedBytes(byte[] key, byte[] iv, int offset, byte[] mask, int maskBlocks, byte[] head, byte[] plain, int usable, int availableBlocks)
        {
            int length = Math.Min((usable + 15) / 16, availableBlocks) * 16;
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var transform = aes.CreateDecryptor();
            var output = new byte[length];
            transform.TransformBlock(head, offset, length, output, 0);
            int masked = Math.Min(maskBlocks * 16, length);
            for (int i = 0; i < masked; i++)
            {
                output[i] ^= mask[i];
            }

            int verified = 0;
            int limit = Math.Min(usable, length);
            while (masked + verified < limit && output[masked + verified] == plain[masked + verified])
            {
                verified++;
            }

            return verified;
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
    }
}
