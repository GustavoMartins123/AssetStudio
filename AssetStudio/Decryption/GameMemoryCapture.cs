#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public sealed class GameMemoryAccessException : Exception
    {
        public GameMemoryAccessException(int nativeErrorCode)
            : base($"Unable to read the game process memory (native error {nativeErrorCode}).")
        {
            NativeErrorCode = nativeErrorCode;
        }

        public int NativeErrorCode { get; }
    }

    public enum CapturedCipherKind
    {
        Aes,
        Rijndael256,
    }

    public enum CapturedCipherMode
    {
        Cbc,
        Ecb,
        Ctr,
    }

    public sealed class CapturedKey
    {
        internal CapturedKey(byte[] key, CapturedCipherKind kind, CapturedCipherMode mode, bool padding, uint counterStart = 0, bool counterLittleEndian = false)
        {
            Key = key;
            Kind = kind;
            Mode = mode;
            Padding = padding;
            CounterStart = counterStart;
            CounterLittleEndian = counterLittleEndian;
        }

        public byte[] Key { get; }
        public CapturedCipherKind Kind { get; }
        public CapturedCipherMode Mode { get; }
        public bool Padding { get; }
        public uint CounterStart { get; }
        public bool CounterLittleEndian { get; }
        public int BlockSize => Kind == CapturedCipherKind.Aes ? 16 : 32;
        public string Id => $"{Kind}|{Mode}|{Padding}|{CounterStart}|{CounterLittleEndian}|{Convert.ToHexString(Key)}";
        public string DisplayName => (Kind == CapturedCipherKind.Aes ? "AES" : "Rijndael-256") + "-" + Mode.ToString().ToUpperInvariant() + (Padding ? " (PKCS7)" : string.Empty);
    }

    public sealed class PlaintextBundleDump
    {
        internal PlaintextBundleDump(byte[] data, long declaredSize, string revision, long address)
        {
            Data = data;
            DeclaredSize = declaredSize;
            Revision = revision;
            Address = address;
            Id = declaredSize + "-" + Convert.ToHexString(SHA256.HashData(data.AsSpan(0, IdentityBytes)));
        }

        public byte[] Data { get; }
        public long DeclaredSize { get; }
        public string Revision { get; }
        public long Address { get; }
        public string Id { get; }

        internal const int IdentityBytes = 16;

        public static PlaintextBundleDump? FromMemory(byte[] data, long address = 0)
        {
            ArgumentNullException.ThrowIfNull(data);
            return GameMemoryCapture.TryReadBundleHeader(data, out long declaredSize, out string revision)
                ? new PlaintextBundleDump(data, declaredSize, revision, address)
                : null;
        }
    }

    public sealed class FileTail
    {
        internal FileTail(byte[] bytes, long length)
        {
            Bytes = bytes;
            Length = length;
        }

        public byte[] Bytes { get; }
        public long Length { get; }
    }

    public sealed class KnownPlainProbe
    {
        internal KnownPlainProbe(PlaintextBundleDump dump, string filePath, int offset, bool padding, int blockIndex, byte[] ivBlock, byte[] cipherBlock, byte[] previousCipherBlock, byte[] plainBlock)
        {
            Dump = dump;
            FilePath = filePath;
            Offset = offset;
            Padding = padding;
            BlockIndex = blockIndex;
            IvBlock = ivBlock;
            CipherBlock = cipherBlock;
            PreviousCipherBlock = previousCipherBlock;
            PlainBlock = plainBlock;
        }

        public PlaintextBundleDump Dump { get; }
        public string FilePath { get; }
        public int Offset { get; }
        public bool Padding { get; }
        public int BlockIndex { get; }
        public byte[] IvBlock { get; }
        public byte[] CipherBlock { get; }
        public byte[] PreviousCipherBlock { get; }
        public byte[] PlainBlock { get; }
    }

    public sealed class KeyValidationContext
    {
        public const int MaximumProbes = 24;
        private const int MaximumTails = 8;

        internal KeyValidationContext(IReadOnlyList<FileTail> tails, IReadOnlyList<KnownPlainProbe> probes)
        {
            Tails = tails;
            Probes = probes;
        }

        public IReadOnlyList<FileTail> Tails { get; }
        public IReadOnlyList<KnownPlainProbe> Probes { get; }

        public static KeyValidationContext Create(IReadOnlyList<FileTail> tails, IReadOnlyList<string> sampleFiles, IReadOnlyList<PlaintextBundleDump>? dumps)
        {
            var probes = new List<KnownPlainProbe>();
            if (dumps != null)
            {
                foreach (PlaintextBundleDump dump in dumps.OrderByDescending(static item => item.Data.Length))
                {
                    if (probes.Count >= MaximumProbes)
                    {
                        break;
                    }

                    int lastBlock = (int)Math.Min(dump.DeclaredSize, dump.Data.Length) / 16 - 1;
                    if (lastBlock < 2)
                    {
                        continue;
                    }

                    foreach (string file in sampleFiles)
                    {
                        long length;
                        try
                        {
                            length = new FileInfo(file).Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        foreach (int offset in new[] { 0, 16 })
                        {
                            long padding = length - offset - dump.DeclaredSize;
                            if (padding is < 0 or > 16 || (length - offset) % 16 != 0)
                            {
                                continue;
                            }

                            foreach (int blockIndex in new[] { 256, 16, lastBlock }.Distinct())
                            {
                                if (blockIndex > lastBlock || probes.Count >= MaximumProbes)
                                {
                                    continue;
                                }

                                try
                                {
                                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                    var cipher = new byte[32];
                                    stream.Seek(offset + (blockIndex - 1) * 16L, SeekOrigin.Begin);
                                    stream.ReadExactly(cipher);
                                    var iv = new byte[16];
                                    if (offset == 16)
                                    {
                                        stream.Seek(0, SeekOrigin.Begin);
                                        stream.ReadExactly(iv);
                                    }

                                    var plain = new byte[16];
                                    dump.Data.AsSpan(blockIndex * 16, 16).CopyTo(plain);
                                    probes.Add(new KnownPlainProbe(dump, file, offset, padding > 0, blockIndex, iv, cipher.AsSpan(16, 16).ToArray(), cipher.AsSpan(0, 16).ToArray(), plain));
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
                                {
                                }
                            }
                        }
                    }
                }
            }

            return new KeyValidationContext(tails, probes);
        }

        public static List<FileTail> LoadTails(IEnumerable<string> files, int maxTails = MaximumTails)
        {
            var tails = new List<FileTail>();
            var ordered = new List<(string Path, long Length)>();
            foreach (string file in files)
            {
                try
                {
                    ordered.Add((file, new FileInfo(file).Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach ((string path, long length) in ordered.OrderByDescending(static item => item.Length))
            {
                if (tails.Count >= maxTails)
                {
                    break;
                }

                if (length < 64 || length % 16 != 0)
                {
                    continue;
                }

                try
                {
                    var bytes = new byte[64];
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(-64, SeekOrigin.End);
                    stream.ReadExactly(bytes);
                    tails.Add(new FileTail(bytes, length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return tails;
        }
    }

    public sealed class GameMemoryScanResult
    {
        internal GameMemoryScanResult(IReadOnlyList<CapturedKey> keys, IReadOnlyList<PlaintextBundleDump> dumps)
        {
            Keys = keys;
            Dumps = dumps;
        }

        public IReadOnlyList<CapturedKey> Keys { get; }
        public IReadOnlyList<PlaintextBundleDump> Dumps { get; }
    }

    public static class GameMemoryCapture
    {
        public const int DefaultDumpBytes = 65536;
        private const int ChunkSize = 4 << 20;
        private const int OverlapBytes = 256;
        private const int MaximumWindowArrayBytes = 64;
        private const int MaximumTextLength = 64;
        private const ulong MaximumClassPointer = 0x7FFFFFFFFFFFUL;
        private const int AesBlockSize = 16;

        [ThreadStatic]
        private static Aes? aes;

        [ThreadStatic]
        private static byte[]? aesKey16;

        [ThreadStatic]
        private static byte[]? aesKey24;

        [ThreadStatic]
        private static byte[]? aesKey32;

        [ThreadStatic]
        private static uint[]? rijndaelSchedule;

        public static bool TryReadBundleHeader(ReadOnlySpan<byte> data, out long declaredSize, out string revision)
        {
            declaredSize = 0;
            revision = string.Empty;
            if (data.Length < 32 || !data.StartsWith("UnityFS\0"u8))
            {
                return false;
            }

            uint version = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
            if (version is 0 or > 30 || !data.Slice(12).StartsWith("5.x.x\0"u8))
            {
                return false;
            }

            int end = 18;
            while (end < data.Length && data[end] != 0 && end - 18 <= 32)
            {
                end++;
            }

            int revisionLength = end - 18;
            if (end + 9 > data.Length || revisionLength is 0 or > 32)
            {
                return false;
            }

            if (data.Slice(18, revisionLength).IndexOfAnyExcept("0123456789.abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"u8) >= 0)
            {
                return false;
            }

            ulong size = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(end + 1, 8));
            if (size == 0 || size > 1UL << 40)
            {
                return false;
            }

            declaredSize = (long)size;
            revision = Encoding.ASCII.GetString(data.Slice(18, revisionLength));
            return true;
        }

        public static void ScanBuffer(ReadOnlySpan<byte> data, KeyValidationContext context, int maxDumpBytes, IDictionary<string, CapturedKey> keys, IDictionary<string, PlaintextBundleDump> dumps, long address = 0)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (data.Length < 64 || data.IndexOfAnyExcept((byte)0) < 0)
            {
                return;
            }

            if (context.Tails.Count > 0 || context.Probes.Count > 0)
            {
                ScanManagedArrays(data, context, keys);
                ScanManagedStrings(data, context, keys);
            }

            if (maxDumpBytes > 0)
            {
                ScanPlaintextBundles(data, maxDumpBytes, dumps, address);
            }
        }

        public static GameMemoryScanResult ScanProcess(
            int processId,
            KeyValidationContext context,
            int maxDumpBytes = DefaultDumpBytes,
            CancellationToken cancellationToken = default,
            long minimumAddress = 0,
            long maximumAddress = long.MaxValue)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!ProcessMemoryReader.IsSupported)
            {
                throw new PlatformNotSupportedException("Capturing keys from a running game is only available on Windows and Linux.");
            }

            var keys = new ConcurrentDictionary<string, CapturedKey>();
            var dumps = new ConcurrentDictionary<string, PlaintextBundleDump>();
            using ProcessMemoryHandle? process = ProcessMemoryReader.Open(processId, out int errorCode);
            if (process == null)
            {
                throw new GameMemoryAccessException(errorCode);
            }

            var chunks = ProcessMemoryReader.EnumerateReadableChunks(process, ChunkSize);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2), CancellationToken = cancellationToken };
            Parallel.ForEach(chunks, options, chunk =>
            {
                if (chunk.Address + chunk.Size <= minimumAddress || chunk.Address >= maximumAddress)
                {
                    return;
                }

                int length = (int)Math.Min(chunk.Size + OverlapBytes, chunk.RegionEnd - chunk.Address);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (ProcessMemoryReader.TryRead(process, chunk.Address, buffer, length))
                    {
                        ScanBuffer(buffer.AsSpan(0, length), context, maxDumpBytes, keys, dumps, chunk.Address);
                    }
                    else
                    {
                        for (long offset = 0; offset < chunk.Size; offset += 65536)
                        {
                            int piece = (int)Math.Min(65536, chunk.Size - offset);
                            if (ProcessMemoryReader.TryRead(process, chunk.Address + offset, buffer, piece))
                            {
                                ScanBuffer(buffer.AsSpan(0, piece), context, maxDumpBytes, keys, dumps, chunk.Address + offset);
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });

            return new GameMemoryScanResult(
                keys.Values.OrderBy(static key => key.Key.Length).ThenBy(static key => key.Id, StringComparer.Ordinal).ToArray(),
                dumps.Values.OrderByDescending(static dump => dump.Data.Length).ThenBy(static dump => dump.Address).ToArray());
        }

        internal static CapturedKey? Identify(ReadOnlySpan<byte> key, KeyValidationContext context, bool exhaustive, int requiredMatches)
        {
            if (key.Length is not (16 or 24 or 32) || requiredMatches <= 0)
            {
                return null;
            }

            int cbc = 0;
            int ecb = 0;
            int eligible = 0;
            if (context.Tails.Count >= requiredMatches)
            {
                Aes cipher = GetAes(key);
                Span<byte> last = stackalloc byte[AesBlockSize];
                foreach (FileTail tail in context.Tails)
                {
                    cipher.DecryptEcb(tail.Bytes.AsSpan(48, AesBlockSize), last, PaddingMode.None);
                    eligible++;
                    if (HasValidPadding(last))
                    {
                        ecb++;
                    }

                    for (int i = 0; i < AesBlockSize; i++)
                    {
                        last[i] ^= tail.Bytes[32 + i];
                    }

                    if (HasValidPadding(last))
                    {
                        cbc++;
                    }
                }

                if (eligible >= requiredMatches)
                {
                    if (cbc >= requiredMatches)
                    {
                        return new CapturedKey(key.ToArray(), CapturedCipherKind.Aes, CapturedCipherMode.Cbc, true);
                    }

                    if (ecb >= requiredMatches)
                    {
                        return new CapturedKey(key.ToArray(), CapturedCipherKind.Aes, CapturedCipherMode.Ecb, true);
                    }
                }
            }

            if (!exhaustive)
            {
                return null;
            }

            CapturedKey? probed = IdentifyByProbes(key, context);
            if (probed != null)
            {
                return probed;
            }

            return IdentifyRijndael(key, context, requiredMatches);
        }

        private static CapturedKey? IdentifyByProbes(ReadOnlySpan<byte> key, KeyValidationContext context)
        {
            if (context.Probes.Count == 0)
            {
                return null;
            }

            Aes cipher = GetAes(key);
            Span<byte> block = stackalloc byte[AesBlockSize];
            Span<byte> counter = stackalloc byte[AesBlockSize];
            foreach (KnownPlainProbe probe in context.Probes)
            {
                cipher.DecryptEcb(probe.CipherBlock, block, PaddingMode.None);
                if (probe.Offset == 0 && block.SequenceEqual(probe.PlainBlock))
                {
                    return new CapturedKey(key.ToArray(), CapturedCipherKind.Aes, CapturedCipherMode.Ecb, probe.Padding);
                }

                for (int i = 0; i < AesBlockSize; i++)
                {
                    block[i] ^= probe.PreviousCipherBlock[i];
                }

                if (block.SequenceEqual(probe.PlainBlock))
                {
                    return new CapturedKey(key.ToArray(), CapturedCipherKind.Aes, CapturedCipherMode.Cbc, probe.Padding);
                }

                if (probe.Offset != 16 || probe.Padding)
                {
                    continue;
                }

                for (int variant = 0; variant < 4; variant++)
                {
                    uint start = (uint)(variant & 1);
                    bool littleEndian = (variant & 2) != 0;
                    BuildCounterBlock(probe.IvBlock, (ulong)probe.BlockIndex + start, littleEndian, counter);
                    cipher.EncryptEcb(counter, block, PaddingMode.None);
                    for (int i = 0; i < AesBlockSize; i++)
                    {
                        block[i] ^= probe.CipherBlock[i];
                    }

                    if (block.SequenceEqual(probe.PlainBlock))
                    {
                        return new CapturedKey(key.ToArray(), CapturedCipherKind.Aes, CapturedCipherMode.Ctr, false, start, littleEndian);
                    }
                }
            }

            return null;
        }

        private static CapturedKey? IdentifyRijndael(ReadOnlySpan<byte> key, KeyValidationContext context, int requiredMatches)
        {
            rijndaelSchedule ??= new uint[RijndaelBlockCipher.MaxKeyScheduleWords];
            int rounds = RijndaelBlockCipher.ExpandDecryptKey(key, 256, rijndaelSchedule);
            if (rounds < 0 || context.Tails.Count < requiredMatches)
            {
                return null;
            }

            const int blockSize = 32;
            Span<byte> block = stackalloc byte[blockSize];
            int cbc = 0;
            int ecb = 0;
            int eligible = 0;
            foreach (FileTail tail in context.Tails)
            {
                if (tail.Length % blockSize != 0)
                {
                    continue;
                }

                RijndaelBlockCipher.DecryptBlock(rijndaelSchedule, 256, rounds, tail.Bytes.AsSpan(32, blockSize), block);
                eligible++;
                if (HasValidPadding(block))
                {
                    ecb++;
                }

                for (int i = 0; i < blockSize; i++)
                {
                    block[i] ^= tail.Bytes[i];
                }

                if (HasValidPadding(block))
                {
                    cbc++;
                }
            }

            if (eligible < requiredMatches)
            {
                return null;
            }

            if (cbc >= requiredMatches)
            {
                return new CapturedKey(key.ToArray(), CapturedCipherKind.Rijndael256, CapturedCipherMode.Cbc, true);
            }

            return ecb >= requiredMatches
                ? new CapturedKey(key.ToArray(), CapturedCipherKind.Rijndael256, CapturedCipherMode.Ecb, true)
                : null;
        }

        internal static void BuildCounterBlock(ReadOnlySpan<byte> iv, ulong addition, bool littleEndian, Span<byte> target)
        {
            iv.CopyTo(target);
            ulong carry = addition;
            if (littleEndian)
            {
                for (int i = 0; i < target.Length && carry != 0; i++)
                {
                    ulong sum = target[i] + (carry & 0xFFUL);
                    target[i] = (byte)sum;
                    carry = (carry >> 8) + (sum >> 8);
                }
            }
            else
            {
                for (int i = target.Length - 1; i >= 0 && carry != 0; i--)
                {
                    ulong sum = target[i] + (carry & 0xFFUL);
                    target[i] = (byte)sum;
                    carry = (carry >> 8) + (sum >> 8);
                }
            }
        }

        private static Aes GetAes(ReadOnlySpan<byte> key)
        {
            aes ??= Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            if (key.Length == 16)
            {
                aesKey16 ??= new byte[16];
                key.CopyTo(aesKey16);
                aes.Key = aesKey16;
            }
            else if (key.Length == 24)
            {
                aesKey24 ??= new byte[24];
                key.CopyTo(aesKey24);
                aes.Key = aesKey24;
            }
            else
            {
                aesKey32 ??= new byte[32];
                key.CopyTo(aesKey32);
                aes.Key = aesKey32;
            }

            return aes;
        }

        private static bool HasValidPadding(ReadOnlySpan<byte> block)
        {
            int pad = block[block.Length - 1];
            if (pad < 1 || pad > block.Length)
            {
                return false;
            }

            for (int i = block.Length - pad; i < block.Length; i++)
            {
                if (block[i] != pad)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ScanManagedArrays(ReadOnlySpan<byte> data, KeyValidationContext context, IDictionary<string, CapturedKey> keys)
        {
            int required = Math.Min(2, context.Tails.Count);
            int strict = context.Tails.Count;
            for (int offset = 32; offset + 16 <= data.Length; offset += 8)
            {
                long length = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 8, 8));
                if (length < 16 || length > MaximumWindowArrayBytes)
                {
                    continue;
                }

                if (BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 16, 8)) != 0
                    || BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 24, 8)) != 0)
                {
                    continue;
                }

                long classPointer = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 32, 8));
                if (classPointer <= 0 || (ulong)classPointer > MaximumClassPointer || offset + length > data.Length)
                {
                    continue;
                }

                ReadOnlySpan<byte> bytes = data.Slice(offset, (int)length);
                if (length is 16 or 24 or 32)
                {
                    Add(keys, Identify(bytes, context, true, required));
                }

                AddTextCandidates(bytes, context, keys, required);
                if (length > 32)
                {
                    for (int start = 0; start + 16 <= length; start += 8)
                    {
                        for (int size = 32; size >= 16; size -= 8)
                        {
                            if (start + size <= length)
                            {
                                Add(keys, Identify(bytes.Slice(start, size), context, false, strict));
                            }
                        }
                    }
                }
            }
        }

        private static void ScanManagedStrings(ReadOnlySpan<byte> data, KeyValidationContext context, IDictionary<string, CapturedKey> keys)
        {
            int required = Math.Min(2, context.Tails.Count);
            Span<byte> chars = stackalloc byte[MaximumTextLength];
            Span<byte> decoded = stackalloc byte[32];
            for (int offset = 20; offset + 32 <= data.Length; offset += 8)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset - 4, 4));
                if (length < 16 || length > MaximumTextLength)
                {
                    continue;
                }

                long classPointer = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 20, 8));
                if (classPointer <= 0 || (ulong)classPointer > MaximumClassPointer || offset + (long)length * 2 > data.Length)
                {
                    continue;
                }

                bool ascii = true;
                for (int i = 0; i < length; i++)
                {
                    if (data[offset + i * 2 + 1] != 0)
                    {
                        ascii = false;
                        break;
                    }

                    chars[i] = data[offset + i * 2];
                }

                if (!ascii)
                {
                    continue;
                }

                ReadOnlySpan<byte> text = chars.Slice(0, length);
                if (length is 16 or 24 or 32)
                {
                    Add(keys, Identify(text, context, true, required));
                }

                if (length is 32 or 48 or 64 && TryDecodeHex(text, decoded, out int written))
                {
                    Add(keys, Identify(decoded.Slice(0, written), context, true, required));
                }

                if (length is 22 or 24 or 32 or 43 or 44 && TryDecodeBase64(text, decoded, out written) && written is 16 or 24 or 32)
                {
                    Add(keys, Identify(decoded.Slice(0, written), context, true, required));
                }
            }
        }

        private static void AddTextCandidates(ReadOnlySpan<byte> bytes, KeyValidationContext context, IDictionary<string, CapturedKey> keys, int required)
        {
            foreach (byte b in bytes)
            {
                if (b < 0x20 || b > 0x7E)
                {
                    return;
                }
            }

            Span<byte> decoded = stackalloc byte[32];
            if (bytes.Length is 32 or 48 or 64 && TryDecodeHex(bytes, decoded, out int written))
            {
                Add(keys, Identify(decoded.Slice(0, written), context, true, required));
            }

            if (bytes.Length is 24 or 32 or 44 && TryDecodeBase64(bytes, decoded, out written) && written is 16 or 24 or 32)
            {
                Add(keys, Identify(decoded.Slice(0, written), context, true, required));
            }
        }

        private static void Add(IDictionary<string, CapturedKey> keys, CapturedKey? key)
        {
            if (key != null)
            {
                keys[key.Id] = key;
            }
        }

        private static bool TryDecodeHex(ReadOnlySpan<byte> text, Span<byte> output, out int written)
        {
            written = 0;
            for (int i = 0; i + 1 < text.Length; i += 2)
            {
                int high = HexValue(text[i]);
                int low = HexValue(text[i + 1]);
                if (high < 0 || low < 0 || written >= output.Length)
                {
                    return false;
                }

                output[written++] = (byte)(high << 4 | low);
            }

            return written > 0;
        }

        private static int HexValue(byte value)
        {
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                return value - '0';
            }

            if (value is >= (byte)'a' and <= (byte)'f')
            {
                return value - 'a' + 10;
            }

            return value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + 10 : -1;
        }

        private static bool TryDecodeBase64(ReadOnlySpan<byte> text, Span<byte> output, out int written)
        {
            written = 0;
            uint accumulator = 0;
            int bits = 0;
            int padding = 0;
            for (int i = 0; i < text.Length; i++)
            {
                byte value = text[i];
                if (value == (byte)'=')
                {
                    padding++;
                    continue;
                }

                if (padding > 0)
                {
                    return false;
                }

                int digit = Base64Value(value);
                if (digit < 0)
                {
                    return false;
                }

                accumulator = (accumulator << 6) | (uint)digit;
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    if (written >= output.Length)
                    {
                        return false;
                    }

                    output[written++] = (byte)(accumulator >> bits);
                }
            }

            return written > 0 && (padding == 0 || text.Length % 4 == 0);
        }

        private static int Base64Value(byte value)
        {
            if (value is >= (byte)'A' and <= (byte)'Z')
            {
                return value - 'A';
            }

            if (value is >= (byte)'a' and <= (byte)'z')
            {
                return value - 'a' + 26;
            }

            if (value is >= (byte)'0' and <= (byte)'9')
            {
                return value - '0' + 52;
            }

            return value == (byte)'+' ? 62 : value == (byte)'/' ? 63 : -1;
        }

        private static void ScanPlaintextBundles(ReadOnlySpan<byte> data, int maxDumpBytes, IDictionary<string, PlaintextBundleDump> dumps, long address)
        {
            int position = 0;
            while (position + 48 <= data.Length)
            {
                int found = data.Slice(position).IndexOf("UnityFS\0"u8);
                if (found < 0)
                {
                    break;
                }

                int at = position + found;
                position = at + 8;
                if (at + 48 > data.Length || !TryReadBundleHeader(data.Slice(at), out long declaredSize, out string revision))
                {
                    continue;
                }

                int available = Math.Min(maxDumpBytes, data.Length - at);
                if (available < PlaintextBundleDump.IdentityBytes)
                {
                    continue;
                }

                var dump = new PlaintextBundleDump(data.Slice(at, available).ToArray(), declaredSize, revision, address + at);
                if (!dumps.TryGetValue(dump.Id, out PlaintextBundleDump? existing) || existing.Data.Length < dump.Data.Length)
                {
                    dumps[dump.Id] = dump;
                }
            }
        }
    }
}
