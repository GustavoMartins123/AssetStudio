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

    public sealed class PlaintextBundleDump
    {
        internal PlaintextBundleDump(byte[] data, long declaredSize, string revision, long address)
        {
            Data = data;
            DeclaredSize = declaredSize;
            Revision = revision;
            Address = address;
            Id = declaredSize + "-" + Convert.ToHexString(SHA256.HashData(data.AsSpan(0, Math.Min(512, data.Length))));
        }

        public byte[] Data { get; }
        public long DeclaredSize { get; }
        public string Revision { get; }
        public long Address { get; }
        public string Id { get; }

        public static PlaintextBundleDump? FromMemory(byte[] data, long address = 0)
        {
            ArgumentNullException.ThrowIfNull(data);
            return GameMemoryCapture.TryReadBundleHeader(data, out long declaredSize, out string revision)
                ? new PlaintextBundleDump(data, declaredSize, revision, address)
                : null;
        }
    }

    public sealed class GameMemoryScanResult
    {
        internal GameMemoryScanResult(IReadOnlyList<byte[]> keys, IReadOnlyList<PlaintextBundleDump> dumps)
        {
            Keys = keys;
            Dumps = dumps;
        }

        public IReadOnlyList<byte[]> Keys { get; }
        public IReadOnlyList<PlaintextBundleDump> Dumps { get; }
    }

    public static class GameMemoryCapture
    {
        public const int DefaultDumpBytes = 65536;
        public const int TailBytes = 32;
        private const int ChunkSize = 4 << 20;
        private const int OverlapBytes = 256;
        private const int MinimumTailFileBytes = 64;

        [ThreadStatic]
        private static Aes? tailCipher;

        public static byte[] ReadTail(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < MinimumTailFileBytes || stream.Length % 16 != 0)
            {
                return Array.Empty<byte>();
            }

            stream.Seek(-TailBytes, SeekOrigin.End);
            var tail = new byte[TailBytes];
            int total = 0;
            while (total < tail.Length)
            {
                int read = stream.Read(tail, total, tail.Length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total == tail.Length ? tail : Array.Empty<byte>();
        }

        public static List<byte[]> LoadTails(IEnumerable<string> files, int maxTails = 8)
        {
            var tails = new List<byte[]>();
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

            foreach ((string path, _) in ordered.OrderByDescending(item => item.Length))
            {
                if (tails.Count >= maxTails)
                {
                    break;
                }

                try
                {
                    byte[] tail = ReadTail(path);
                    if (tail.Length > 0)
                    {
                        tails.Add(tail);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return tails;
        }

        public static bool IsKeyAccepted(int tailMatches, int tailCount) => tailMatches >= Math.Min(2, tailCount);

        public static int CountTailMatches(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> tails)
        {
            tailCipher ??= Aes.Create();
            tailCipher.Mode = CipherMode.ECB;
            tailCipher.Padding = PaddingMode.None;
            tailCipher.Key = key.ToArray();
            Span<byte> last = stackalloc byte[16];
            int matches = 0;
            for (int t = 0; t < tails.Count; t++)
            {
                byte[] tail = tails[t];
                tailCipher.DecryptEcb(tail.AsSpan(16, 16), last, PaddingMode.None);
                for (int i = 0; i < 16; i++)
                {
                    last[i] ^= tail[i];
                }

                int pad = last[15];
                if (pad is < 1 or > 16)
                {
                    continue;
                }

                bool valid = true;
                for (int i = 16 - pad; i < 16; i++)
                {
                    if (last[i] != pad)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                {
                    matches++;
                }
            }

            return matches;
        }

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

        public static void ScanBuffer(ReadOnlySpan<byte> data, IReadOnlyList<byte[]> tails, int maxDumpBytes, IDictionary<string, byte[]> keys, IDictionary<string, PlaintextBundleDump> dumps)
        {
            ScanBuffer(data, tails, maxDumpBytes, keys, dumps, 0);
        }

        public static void ScanBuffer(ReadOnlySpan<byte> data, IReadOnlyList<byte[]> tails, int maxDumpBytes, IDictionary<string, byte[]> keys, IDictionary<string, PlaintextBundleDump> dumps, long address)
        {
            if (data.Length < 64 || data.IndexOfAnyExcept((byte)0) < 0)
            {
                return;
            }

            if (tails.Count > 0)
            {
                ScanManagedKeyArrays(data, tails, keys);
            }

            if (maxDumpBytes > 0)
            {
                ScanPlaintextBundles(data, maxDumpBytes, dumps, address);
            }
        }

        private static void ScanManagedKeyArrays(ReadOnlySpan<byte> data, IReadOnlyList<byte[]> tails, IDictionary<string, byte[]> keys)
        {
            int threshold = Math.Min(2, tails.Count);
            for (int offset = 32; offset + 32 <= data.Length; offset += 8)
            {
                long length = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 8, 8));
                if (length is not (16 or 24 or 32))
                {
                    continue;
                }

                if (BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 16, 8)) != 0
                    || BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 24, 8)) != 0)
                {
                    continue;
                }

                long classPointer = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset - 32, 8));
                if (classPointer <= 0 || (ulong)classPointer > 0x7FFFFFFFFFFFUL)
                {
                    continue;
                }

                ReadOnlySpan<byte> key = data.Slice(offset, (int)length);
                if (!IsKeyAccepted(CountTailMatches(key, tails), tails.Count))
                {
                    continue;
                }

                keys[Convert.ToHexString(key)] = key.ToArray();
            }
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
                var dump = new PlaintextBundleDump(data.Slice(at, available).ToArray(), declaredSize, revision, address + at);
                if (!dumps.TryGetValue(dump.Id, out PlaintextBundleDump? existing) || existing.Data.Length < dump.Data.Length)
                {
                    dumps[dump.Id] = dump;
                }
            }
        }

        public static GameMemoryScanResult ScanProcess(int processId, IReadOnlyList<byte[]> tails, int maxDumpBytes = DefaultDumpBytes, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Capturing keys from a running game is only available on Windows.");
            }

            var keys = new ConcurrentDictionary<string, byte[]>();
            var dumps = new ConcurrentDictionary<string, PlaintextBundleDump>();
            IntPtr process = ProcessMemoryReader.Open(processId, out int errorCode);
            if (process == IntPtr.Zero)
            {
                throw new GameMemoryAccessException(errorCode);
            }

            try
            {
                var chunks = ProcessMemoryReader.EnumerateReadableChunks(process, ChunkSize);
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2), CancellationToken = cancellationToken };
                Parallel.ForEach(chunks, options, chunk =>
                {
                    int length = (int)Math.Min(chunk.Size + OverlapBytes, chunk.RegionEnd - chunk.Address);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        if (ProcessMemoryReader.TryRead(process, chunk.Address, buffer, length))
                        {
                            ScanBuffer(buffer.AsSpan(0, length), tails, maxDumpBytes, keys, dumps, chunk.Address);
                        }
                        else
                        {
                            for (long offset = 0; offset < chunk.Size; offset += 65536)
                            {
                                int piece = (int)Math.Min(65536, chunk.Size - offset);
                                if (ProcessMemoryReader.TryRead(process, chunk.Address + offset, buffer, piece))
                                {
                                    ScanBuffer(buffer.AsSpan(0, piece), tails, maxDumpBytes, keys, dumps, chunk.Address + offset);
                                }
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                });
            }
            finally
            {
                ProcessMemoryReader.Close(process);
            }

            return new GameMemoryScanResult(
                keys.Values.OrderBy(key => key.Length).ThenBy(key => Convert.ToHexString(key), StringComparer.Ordinal).ToArray(),
                dumps.Values.OrderByDescending(dump => dump.Data.Length).ThenBy(dump => dump.Address).ToArray());
        }
    }
}
