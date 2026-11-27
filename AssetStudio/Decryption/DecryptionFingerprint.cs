#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio
{
    public interface IDecryptionFingerprintSource
    {
        string GetFingerprint();
    }

    public static class DecryptionFingerprints
    {
        public const int UnverifiedPrefixBytes = 64 * 1024;

        public static string? Compute(Stream? transformed, Stream? source, string? providerId)
        {
            if (transformed == null || string.IsNullOrEmpty(providerId))
            {
                return null;
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, 1L);
            Stream? current = transformed;
            while (current != null)
            {
                switch (current)
                {
                    case HeaderMaskStream mask:
                        Append(hash, "mask"u8);
                        Append(hash, mask.Mask);
                        current = mask.InnerStream;
                        break;
                    case CipherTransformStream cipher:
                        Append(hash, "cipher"u8);
                        Append(hash, (long)cipher.Algorithm);
                        Append(hash, cipher.Key);
                        Append(hash, cipher.IvOrNonce);
                        Append(hash, cipher.HeaderSkipBytes);
                        Append(hash, cipher.RangeStart);
                        Append(hash, cipher.RangeLength);
                        Append(hash, cipher.Pkcs7Padding);
                        Append(hash, cipher.CounterStart);
                        Append(hash, cipher.CounterLittleEndian);
                        current = cipher.BaseStream;
                        break;
                    case XorTransformStream xor:
                        Append(hash, "xor"u8);
                        Append(hash, xor.XorKey);
                        Append(hash, xor.HeaderSkipBytes);
                        Append(hash, xor.RangeStart);
                        Append(hash, xor.RangeLength);
                        current = xor.BaseStream;
                        break;
                    case RijndaelCbcTransformStream rijndael:
                        Append(hash, "rijndael"u8);
                        Append(hash, rijndael.Cipher.BlockSize);
                        Append(hash, MemoryMarshal.AsBytes(rijndael.Cipher.EncryptionKeySchedule));
                        Append(hash, rijndael.Iv);
                        Append(hash, rijndael.HeaderSkipBytes);
                        Append(hash, rijndael.Pkcs7Padding);
                        current = rijndael.BaseStream;
                        break;
                    case DecryptedStream block512:
                        Append(hash, "block512"u8);
                        Append(hash, block512.Token);
                        current = block512.BaseStream;
                        break;
                    case SubStream sub:
                        Append(hash, "sub"u8);
                        Append(hash, sub.Offset);
                        Append(hash, sub.Length);
                        current = sub.BaseStream;
                        break;
                    default:
                        if (!ReferenceEquals(current, source))
                        {
                            AppendUnrecognised(hash, current, providerId);
                        }

                        current = null;
                        break;
                }
            }

            return "v1:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void AppendUnrecognised(IncrementalHash hash, Stream stream, string providerId)
        {
            Append(hash, providerId);
            if (stream is IDecryptionFingerprintSource declared && TryGetDeclaredFingerprint(declared, out string declaredFingerprint))
            {
                Append(hash, "declared"u8);
                Append(hash, declaredFingerprint);
                return;
            }

            Append(hash, "unverified"u8);
            Append(hash, stream.GetType().FullName ?? stream.GetType().Name);
            Append(hash, TryReadPrefix(stream));
        }

        private static bool TryGetDeclaredFingerprint(IDecryptionFingerprintSource source, out string fingerprint)
        {
            try
            {
                fingerprint = source.GetFingerprint() ?? string.Empty;
                return fingerprint.Length > 0;
            }
            catch
            {
                fingerprint = string.Empty;
                return false;
            }
        }

        private static byte[] TryReadPrefix(Stream stream)
        {
            try
            {
                if (!stream.CanSeek || stream.Length <= 0)
                {
                    return Array.Empty<byte>();
                }

                long position = stream.Position;
                var buffer = new byte[(int)Math.Min(UnverifiedPrefixBytes, stream.Length)];
                stream.Position = 0;
                int read = 0;
                while (read < buffer.Length)
                {
                    int count = stream.Read(buffer, read, buffer.Length - read);
                    if (count <= 0)
                    {
                        break;
                    }

                    read += count;
                }

                stream.Position = position;
                return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static void Append(IncrementalHash hash, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
            hash.AppendData(length);
            hash.AppendData(data);
        }

        private static void Append(IncrementalHash hash, long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            hash.AppendData(buffer);
        }

        private static void Append(IncrementalHash hash, bool value) => Append(hash, value ? 1L : 0L);

        private static void Append(IncrementalHash hash, string value) => Append(hash, Encoding.UTF8.GetBytes(value));
    }
}
