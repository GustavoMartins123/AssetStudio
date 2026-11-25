#nullable enable
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public sealed class CompositeStringKeyTransformProvider : IStreamTransformProvider
    {
        private const int MinFragmentLength = 4;
        private const int MinStrongFragmentLength = 12;
        private const int MaxFragmentLength = 30;
        private const int MaxKeyLength = 32;
        private const int MaxAssetFileBytes = 64 << 20;
        private const int MaxAssetTotalBytes = 256 << 20;
        private const int MaxAssetFragments = 2_000_000;
        private const int MaxLiteralFragments = 500_000;
        private const int MinRevisionLength = 7;
        private const int MaxRevisionLength = 24;
        private const int MaxProbeHits = 4;
        private const int SearchTimeoutSeconds = 20;
        private const long SearchCandidateBudget = 60_000_000;
        private const int MaxSiblingsChecked = 64;
        private const int ProbedCipherBytes = 96;
        private const int MaxBlocksInfoSize = 16 * 1024 * 1024;
        private const int MaxProbedBlockCompressed = 4 * 1024 * 1024;
        private const int MaxProbedBlockUncompressed = 16 * 1024 * 1024;
        private const uint BlocksInfoAtTheEnd = 0x80;
        private const uint BlocksInfoNeedPaddingAtStart = 0x200;

        private static readonly byte[] BundlePrefix = Encoding.ASCII.GetBytes("UnityFS\0");
        private static readonly byte[] PlayerVersion = Encoding.ASCII.GetBytes("5.x.x\0");
        private static readonly CipherConfig[] Configs =
        {
            new CipherConfig(256, 32),
            new CipherConfig(128, 32),
            new CipherConfig(128, 16),
            new CipherConfig(256, 16),
            new CipherConfig(256, 24),
            new CipherConfig(192, 32),
            new CipherConfig(192, 24),
            new CipherConfig(192, 16),
            new CipherConfig(128, 24),
        };
        private static readonly List<byte[]> NoFragments = new();
        private static readonly ConcurrentDictionary<string, ProjectState> Projects = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<bool>> SiblingChecks = new(StringComparer.OrdinalIgnoreCase);

        public string Id => "CompositeStringKey";
        public int Priority => 360;

        public static void ClearCache()
        {
            Projects.Clear();
            SiblingChecks.Clear();
        }

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            try
            {
                return TryTransformCore(stream, filePath, initialHeader);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
            {
                return null;
            }
        }

        private Stream? TryTransformCore(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 64 || stream.Length < ProbedCipherBytes + 32 || !stream.CanRead || !stream.CanSeek || string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length)
                || initialHeader.StartsWith(DecryptionHelper.EncryptionMagicBytes)
                || DecryptionSources.LooksLikePlainText(initialHeader.Slice(0, 64)))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(filePath);
            string? sourceKey = DecryptionSources.GetSourceKey(Path.GetDirectoryName(fullPath) ?? string.Empty);
            if (sourceKey == null)
            {
                return null;
            }

            ProjectState state = Projects.GetOrAdd(sourceKey, static _ => new ProjectState());
            Solution? solution = Volatile.Read(ref state.Solution);
            if (solution == null)
            {
                if (Volatile.Read(ref state.Searched) || !IsEligible(fullPath, stream.Length, initialHeader))
                {
                    return null;
                }

                lock (state.Sync)
                {
                    solution = state.Solution;
                    if (solution == null && !state.Searched)
                    {
                        solution = Discover(fullPath, stream, sourceKey);
                        state.Solution = solution;
                        state.Searched = true;
                    }
                }

                if (solution == null)
                {
                    return null;
                }
            }

            return Open(stream, solution);
        }

        private static bool IsEligible(string fullPath, long length, ReadOnlySpan<byte> initialHeader)
        {
            if ((length % 16 != 0 && length % 24 != 0) || !DecryptionSources.IsBundleLikeFile(fullPath))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            byte[] prefix = initialHeader.Slice(0, 16).ToArray();
            string cacheKey = directory + "|" + Convert.ToHexString(prefix);
            return SiblingChecks.GetOrAdd(cacheKey, _ => new Lazy<bool>(() => HasSiblingWithSamePrefix(directory, fullPath, prefix))).Value;
        }

        private static bool HasSiblingWithSamePrefix(string directory, string selfPath, byte[] prefix)
        {
            try
            {
                string extension = Path.GetExtension(selfPath);
                int checkedCount = 0;
                foreach (string candidate in Directory.EnumerateFiles(directory))
                {
                    if (string.Equals(candidate, selfPath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(Path.GetExtension(candidate), extension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (SharesPrefix(candidate, prefix))
                    {
                        return true;
                    }

                    if (++checkedCount >= MaxSiblingsChecked)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }

        private static bool SharesPrefix(string path, byte[] prefix)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> buffer = stackalloc byte[16];
                int total = 0;
                while (total < prefix.Length)
                {
                    int read = stream.Read(buffer.Slice(total, prefix.Length - total));
                    if (read <= 0)
                    {
                        return false;
                    }

                    total += read;
                }

                return buffer.SequenceEqual(prefix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static Solution? Discover(string filePath, Stream stream, string sourceKey)
        {
            Fragments fragments = LoadFragments(sourceKey);
            if (fragments.All.Items.Length < 2)
            {
                return null;
            }

            var head = new byte[ProbedCipherBytes];
            stream.Position = 0;
            int total = 0;
            while (total < head.Length)
            {
                int read = stream.Read(head, total, head.Length - total);
                if (read <= 0)
                {
                    return null;
                }

                total += read;
            }

            return new SearchContext(head, stream.Length, fragments, stream, sourceKey).Run();
        }

        private static Fragments LoadFragments(string sourceKey)
        {
            var literalValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in sourceKey.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (part.StartsWith("M:", StringComparison.Ordinal))
                    {
                        foreach (string dll in DecryptionSources.OrderAssemblyPaths(Directory.EnumerateFiles(part[2..], "*.dll", SearchOption.TopDirectoryOnly)))
                        {
                            foreach (string value in PassphraseCtrTransformProvider.ReadUtf16Strings(File.ReadAllBytes(dll)))
                            {
                                if (value.Length <= MaxFragmentLength)
                                {
                                    literalValues.Add(value);
                                }
                            }
                        }
                    }
                    else if (part.StartsWith("I:", StringComparison.Ordinal))
                    {
                        foreach (string value in PassphraseCtrTransformProvider.ReadMetadataLiterals(File.ReadAllBytes(part[2..])))
                        {
                            if (value.Length <= MaxFragmentLength)
                            {
                                literalValues.Add(value);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            var assetValues = new HashSet<string>(StringComparer.Ordinal);
            string? dataDirectory = DecryptionSources.GetDataDirectory(sourceKey);
            if (dataDirectory != null)
            {
                try
                {
                    CollectAssetRuns(dataDirectory, assetValues);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            FragmentSet assets = FragmentSet.FromStrings(assetValues, MaxAssetFragments);
            FragmentSet literals = FragmentSet.FromStrings(literalValues, MaxLiteralFragments);
            return new Fragments(assets, literals, FragmentSet.FromBytes(assets.Items.Concat(literals.Items), MaxAssetFragments + MaxLiteralFragments));
        }

        private static void CollectAssetRuns(string dataDirectory, HashSet<string> target)
        {
            long total = 0;
            foreach (string path in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (target.Count >= MaxAssetFragments)
                {
                    break;
                }

                string name = Path.GetFileName(path);
                if (!name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".sharedAssets", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long length;
                try
                {
                    length = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (length > MaxAssetFileBytes || total + length > MaxAssetTotalBytes)
                {
                    continue;
                }

                total += length;
                byte[] buffer = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    int read = 0;
                    while (read < (int)length)
                    {
                        int count = stream.Read(buffer, read, (int)length - read);
                        if (count <= 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    CollectRuns(buffer.AsSpan(0, read), target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        private static void CollectRuns(ReadOnlySpan<byte> data, HashSet<string> target)
        {
            for (int i = 0; i < data.Length;)
            {
                if (data[i] is < 0x20 or > 0x7E)
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < data.Length && data[i] is >= 0x20 and <= 0x7E)
                {
                    i++;
                }

                int length = i - start;
                if (length is >= MinFragmentLength and <= MaxFragmentLength && target.Count < MaxAssetFragments)
                {
                    target.Add(Encoding.ASCII.GetString(data.Slice(start, length)));
                }
            }
        }

        private static Stream? Open(Stream stream, Solution solution)
        {
            stream.Position = 0;
            Stream decoded = CreateStream(stream, solution.Key, solution.Iv, solution.BlockSizeBits, solution.Pkcs7);
            long length = decoded.Length;
            if (length >= 26)
            {
                var header = new byte[(int)Math.Min(512, length)];
                if (ReadExactly(decoded, 0, header)
                    && UnityHeaderProbe.TryParseUnityBundleHeader(header, 0, length, out _, out _, out _, out _, requireStrictValidation: true))
                {
                    decoded.Position = 0;
                    return decoded;
                }
            }

            ReleaseWrapper(decoded);
            return null;
        }

        private static Stream CreateStream(Stream baseStream, byte[] key, byte[] iv, int blockSizeBits, bool pkcs7Padding)
        {
            return blockSizeBits == 128
                ? new CipherTransformStream(baseStream, StreamCipherAlgorithm.AesCbc, key, iv, headerSkipBytes: 0, rangeLength: -1, pkcs7Padding: pkcs7Padding)
                : new RijndaelCbcTransformStream(baseStream, new RijndaelBlockCipher(key, blockSizeBits), iv, 0, pkcs7Padding);
        }

        private static void ReleaseWrapper(Stream wrapper)
        {
            if (wrapper is RijndaelCbcTransformStream rijndael)
            {
                rijndael.LeaveBaseStreamOpen = true;
            }
            else if (wrapper is CipherTransformStream cipher)
            {
                cipher.LeaveBaseStreamOpen = true;
            }

            wrapper.Dispose();
        }

        private static bool ReadExactly(Stream stream, long offset, Span<byte> destination)
        {
            stream.Position = offset;
            int total = 0;
            while (total < destination.Length)
            {
                int read = stream.Read(destination.Slice(total));
                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }

        private static bool IsVersionChar(byte value)
            => value is ((>= (byte)'0' and <= (byte)'9') or (byte)'.' or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z'));

        private static bool IsAsciiPrintable(string value)
        {
            foreach (char c in value)
            {
                if (c < 0x20 || c > 0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        private static uint ReadUInt32BE(byte[] buffer, int offset)
            => ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

        private static ulong ReadUInt64BE(byte[] buffer, int offset)
            => ((ulong)ReadUInt32BE(buffer, offset) << 32) | ReadUInt32BE(buffer, offset + 4);

        private static byte[]? ReadPrefix(string path, int count)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[count];
                int total = 0;
                while (total < count)
                {
                    int read = stream.Read(buffer, total, count - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                return total >= 26 ? buffer.AsSpan(0, total).ToArray() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string? ReadSerializedRevision(string path)
        {
            byte[]? header = ReadPrefix(path, 64);
            if (header == null)
            {
                return null;
            }

            uint version = ReadUInt32BE(header, 8);
            return version is 0 or > 64 ? null : ReadVersionToken(header, version >= 22 ? 24 : 20);
        }

        private static string? ReadBundleRevision(string path)
        {
            byte[]? header = ReadPrefix(path, 160);
            if (header == null || !header.AsSpan(0, 8).SequenceEqual(BundlePrefix))
            {
                return null;
            }

            string? engineVersion = ReadVersionToken(header, 12);
            return engineVersion == null ? null : ReadVersionToken(header, 12 + engineVersion.Length + 1);
        }

        private static string? ReadVersionToken(byte[] data, int position)
        {
            int end = position;
            while (end < data.Length && data[end] != 0 && end - position <= 32 && IsVersionChar(data[end]))
            {
                end++;
            }

            int length = end - position;
            return length is >= 3 and <= 32 && end < data.Length && data[end] == 0
                ? Encoding.ASCII.GetString(data, position, length)
                : null;
        }

        private readonly struct CipherConfig
        {
            public readonly int BlockSizeBits;
            public readonly int KeyLength;

            public CipherConfig(int blockSizeBits, int keyLength)
            {
                BlockSizeBits = blockSizeBits;
                KeyLength = keyLength;
            }
        }

        private sealed class Probe
        {
            public readonly int RevisionLength;
            public readonly int Offset;
            public readonly int Length;
            public readonly ulong[] Values;
            public readonly int ZeroOffset;
            public readonly int InfoOffset;
            public readonly int CharPrefix;

            public Probe(int revisionLength, int offset, int length, ulong[] values, int zeroOffset, int infoOffset, int charPrefix)
            {
                RevisionLength = revisionLength;
                Offset = offset;
                Length = length;
                Values = values;
                ZeroOffset = zeroOffset;
                InfoOffset = infoOffset;
                CharPrefix = charPrefix;
            }
        }

        private sealed class ProjectState
        {
            public readonly object Sync = new();
            public Solution? Solution;
            public bool Searched;
        }

        private sealed class Solution
        {
            public readonly byte[] Key;
            public readonly byte[] Iv;
            public readonly int BlockSizeBits;
            public readonly bool Pkcs7;

            public Solution(byte[] key, byte[] iv, int blockSizeBits, bool pkcs7)
            {
                Key = key;
                Iv = iv;
                BlockSizeBits = blockSizeBits;
                Pkcs7 = pkcs7;
            }
        }

        private sealed class Scratch
        {
            public readonly uint[] Schedule = new uint[RijndaelBlockCipher.MaxKeyScheduleWords];
            public readonly byte[] Key = new byte[MaxKeyLength];
            public readonly byte[] Block = new byte[MaxKeyLength];
            public readonly Probe[] Hits = new Probe[MaxProbeHits];
            public int Tested;
        }

        private sealed class FragmentSet
        {
            public readonly byte[][] Items;
            public readonly List<byte[]>?[] ByLength = new List<byte[]>?[MaxKeyLength + 1];
            public readonly List<byte[]>?[][] ByClassLength =
            {
                new List<byte[]>?[MaxKeyLength + 1],
                new List<byte[]>?[MaxKeyLength + 1],
                new List<byte[]>?[MaxKeyLength + 1],
            };

            private FragmentSet(IEnumerable<byte[]> values, int limit)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var items = new List<byte[]>();
                foreach (byte[] value in values)
                {
                    if (items.Count >= limit)
                    {
                        break;
                    }

                    if (value.Length is < MinFragmentLength or > MaxFragmentLength || !seen.Add(Encoding.ASCII.GetString(value)))
                    {
                        continue;
                    }

                    (ByLength[value.Length] ??= new List<byte[]>()).Add(value);
                    (ByClassLength[ClassOf(value)][value.Length] ??= new List<byte[]>()).Add(value);
                    items.Add(value);
                }

                Items = items.ToArray();
            }

            public static int ClassOf(byte[] fragment)
            {
                if (fragment.Length < 8)
                {
                    return 2;
                }

                bool digit = false;
                bool letter = false;
                foreach (byte b in fragment)
                {
                    if (b is >= (byte)'0' and <= (byte)'9')
                    {
                        digit = true;
                    }
                    else if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
                    {
                        letter = true;
                    }
                    else
                    {
                        return 2;
                    }
                }

                return fragment.Length >= MinStrongFragmentLength && digit && letter ? 0 : 1;
            }

            public static FragmentSet FromStrings(IEnumerable<string> values, int limit)
                => new(values.Where(IsAsciiPrintable).Select(value => Encoding.ASCII.GetBytes(value)), limit);

            public static FragmentSet FromBytes(IEnumerable<byte[]> values, int limit)
                => new(values, limit);
        }

        private sealed class Fragments
        {
            public readonly FragmentSet Assets;
            public readonly FragmentSet Literals;
            public readonly FragmentSet All;

            public Fragments(FragmentSet assets, FragmentSet literals, FragmentSet all)
            {
                Assets = assets;
                Literals = literals;
                All = all;
            }
        }

        private sealed class SearchContext
        {
            private readonly byte[] head;
            private readonly long fileSize;
            private readonly Fragments fragments;
            private readonly Stream stream;
            private readonly string sourceKey;
            private readonly Stopwatch clock = new();
            private readonly Probe[] probes128;
            private readonly Probe[] probes192;
            private readonly Probe[] probes256;
            private readonly object acceptSync = new();
            private string? revision;
            private bool revisionProbed;
            private Solution? solution;

            public SearchContext(byte[] head, long fileSize, Fragments fragments, Stream stream, string sourceKey)
            {
                this.head = head;
                this.fileSize = fileSize;
                this.fragments = fragments;
                this.stream = stream;
                this.sourceKey = sourceKey;
                probes128 = BuildProbes(16);
                probes192 = BuildProbes(24);
                probes256 = BuildProbes(32);
            }

            public Solution? Run()
            {
                clock.Start();
                for (int c = 0; c < Configs.Length; c++)
                {
                    for (int order = 0; order < 3; order++)
                    {
                        for (int phase = 0; phase < PhasePairs.Length; phase++)
                        {
                            int[] pairs = PhasePairs[phase];
                            for (int p = 0; p < pairs.Length; p += 2)
                            {
                                Search(Configs[c], order, pairs[p], pairs[p + 1]);
                                if (solution != null || tested > SearchCandidateBudget || clock.Elapsed.TotalSeconds > SearchTimeoutSeconds)
                                {
                                    return solution;
                                }
                            }
                        }
                    }
                }

                return solution;
            }

            private static readonly int[][] PhasePairs =
            {
                new[] { 0, 0 },
                new[] { 0, 1, 1, 0 },
                new[] { 0, 2, 2, 0, 1, 1 },
                new[] { 1, 2, 2, 1, 2, 2 },
            };

            private static int[] GetLengthOrder(int keyLength)
            {
                var order = new List<int>();
                int half = keyLength / 2;
                for (int distance = 0; half + distance <= keyLength - MinFragmentLength || half - distance >= MinFragmentLength; distance++)
                {
                    if (half + distance <= keyLength - MinFragmentLength)
                    {
                        order.Add(half + distance);
                    }

                    if (distance > 0 && half - distance >= MinFragmentLength)
                    {
                        order.Add(half - distance);
                    }
                }

                return order.ToArray();
            }

            private long tested;

            private void Search(CipherConfig config, int order, int leftClass, int rightClass)
            {
                List<byte[]>?[] leftByLength = (order == 0 ? fragments.Assets : fragments.Literals).ByClassLength[leftClass];
                List<byte[]>?[] rightByLength = (order == 1 ? fragments.Assets : fragments.Literals).ByClassLength[rightClass];
                Probe[] probes = config.BlockSizeBits == 128 ? probes128 : config.BlockSizeBits == 192 ? probes192 : probes256;
                int maxCharPrefix = probes.Length == 0 ? 0 : probes[probes.Length - 1].CharPrefix;
                byte[] head = this.head;
                var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
                foreach (int leftLength in GetLengthOrder(config.KeyLength))
                {
                    List<byte[]>? lefts = leftByLength[leftLength];
                    List<byte[]>? rights = rightByLength[config.KeyLength - leftLength];
                    if (lefts == null || rights == null)
                    {
                        continue;
                    }

                    Solution? found = null;
                    Parallel.For(0, lefts.Count, options,
                        () => new Scratch(),
                        (int i, ParallelLoopState loopState, Scratch scratch) =>
                        {
                            if (found != null || solution != null)
                            {
                                loopState.Stop();
                                return scratch;
                            }

                            if ((i & 15) == 0)
                            {
                                Interlocked.Add(ref tested, scratch.Tested);
                                scratch.Tested = 0;
                                if (tested > SearchCandidateBudget || clock.Elapsed.TotalSeconds > SearchTimeoutSeconds)
                                {
                                    loopState.Stop();
                                    return scratch;
                                }
                            }

                            byte[] left = lefts[i];
                            Probe[] hits = scratch.Hits;
                            for (int j = 0; j < rights.Count; j++)
                            {
                                byte[] right = rights[j];
                                int count = Test(scratch, left, right, config, probes, maxCharPrefix, head, hits);
                                scratch.Tested++;
                                if (count == 0)
                                {
                                    continue;
                                }

                                Solution? accepted = Accept(left, right, config, hits, count);
                                if (accepted != null)
                                {
                                    found = accepted;
                                    loopState.Stop();
                                    return scratch;
                                }
                            }

                            return scratch;
                        },
                        scratch => Interlocked.Add(ref tested, scratch.Tested));
                    if (found != null)
                    {
                        return;
                    }
                }
            }

            private static int Test(Scratch scratch, byte[] left, byte[] right, CipherConfig config, Probe[] probes, int maxCharPrefix, byte[] head, Probe[] hits)
            {
                byte[] key = scratch.Key;
                Buffer.BlockCopy(left, 0, key, 0, left.Length);
                Buffer.BlockCopy(right, 0, key, left.Length, right.Length);
                int bits = config.BlockSizeBits;
                int rounds = RijndaelBlockCipher.ExpandDecryptKey(key.AsSpan(0, config.KeyLength), bits, scratch.Schedule);
                int blockSize = bits / 8;
                byte[] block = scratch.Block;
                int previous = 0;
                if (blockSize == 16)
                {
                    RijndaelBlockCipher.DecryptBlock(scratch.Schedule, bits, rounds, head.AsSpan(16, 16), block);
                    if ((block[0] ^ head[0]) != (byte)'x' || (block[1] ^ head[1]) != 0)
                    {
                        return 0;
                    }

                    previous = 16;
                }

                RijndaelBlockCipher.DecryptBlock(scratch.Schedule, bits, rounds, head.AsSpan(previous + blockSize, blockSize), block);
                for (int i = 0; i < blockSize; i++)
                {
                    block[i] ^= head[previous + i];
                }

                return MatchProbes(probes, maxCharPrefix, block, hits);
            }

            private static int MatchProbes(Probe[] probes, int maxCharPrefix, byte[] block, Probe[] hits)
            {
                int chars = 0;
                while (chars < maxCharPrefix && IsVersionChar(block[chars]))
                {
                    chars++;
                }

                int count = 0;
                for (int i = 0; i < probes.Length; i++)
                {
                    Probe probe = probes[i];
                    if (probe.CharPrefix > chars)
                    {
                        continue;
                    }

                    ulong value = 0;
                    for (int k = 0; k < probe.Length; k++)
                    {
                        value = (value << 8) | block[probe.Offset + k];
                    }

                    ulong[] values = probe.Values;
                    int matched = 0;
                    while (matched < values.Length && values[matched] != value)
                    {
                        matched++;
                    }

                    if (matched == values.Length
                        || (probe.ZeroOffset >= 0 && block[probe.ZeroOffset] != 0)
                        || (probe.InfoOffset >= 0 && (block[probe.InfoOffset] != 0 || block[probe.InfoOffset + 4] != 0)))
                    {
                        continue;
                    }

                    hits[count++] = probe;
                    if (count == hits.Length)
                    {
                        break;
                    }
                }

                return count;
            }

            private Probe[] BuildProbes(int blockSize)
            {
                int blockStart = blockSize * (blockSize == 16 ? 2 : 1);
                var probes = new List<Probe>();
                for (int revisionLength = MinRevisionLength; revisionLength <= MaxRevisionLength; revisionLength++)
                {
                    int sizePosition = 19 + revisionLength;
                    int low = Math.Max(blockStart, sizePosition);
                    int high = Math.Min(blockStart + blockSize, sizePosition + 8);
                    int visible = high - low;
                    if (visible < 2)
                    {
                        continue;
                    }

                    var values = new List<ulong>();
                    for (int pad = 0; pad <= blockSize; pad++)
                    {
                        ulong value = (ulong)(fileSize - pad) >> (8 * (sizePosition + 8 - high));
                        if (visible < 8)
                        {
                            value &= (1UL << (8 * visible)) - 1;
                        }

                        if (!values.Contains(value))
                        {
                            values.Add(value);
                        }
                    }

                    probes.Add(new Probe(
                        revisionLength,
                        low - blockStart,
                        visible,
                        values.ToArray(),
                        sizePosition - 1 >= blockStart ? sizePosition - 1 - blockStart : -1,
                        visible < 4 ? sizePosition + 8 - blockStart : -1,
                        sizePosition - 1 >= blockStart ? sizePosition - 1 - blockStart : 0));
                }

                return probes.ToArray();
            }

            private Solution? Accept(byte[] left, byte[] right, CipherConfig config, Probe[] hits, int hitCount)
            {
                lock (acceptSync)
                {
                    if (solution != null)
                    {
                        return solution;
                    }

                    var key = new byte[config.KeyLength];
                    Buffer.BlockCopy(left, 0, key, 0, left.Length);
                    Buffer.BlockCopy(right, 0, key, left.Length, right.Length);
                    int blockSize = config.BlockSizeBits / 8;
                    Span<uint> schedule = stackalloc uint[RijndaelBlockCipher.MaxKeyScheduleWords];
                    int rounds = RijndaelBlockCipher.ExpandDecryptKey(key, config.BlockSizeBits, schedule);
                    var first = new byte[blockSize];
                    RijndaelBlockCipher.DecryptBlock(schedule, config.BlockSizeBits, rounds, head.AsSpan(0, blockSize), first);
                    for (int h = 0; h < hitCount; h++)
                    {
                        foreach (byte[] iv in BuildIvCandidates(blockSize, first, hits[h].RevisionLength))
                        {
                            foreach (bool pkcs7 in new[] { true, false })
                            {
                                if (Validate(key, iv, config, pkcs7))
                                {
                                    solution = new Solution(key, iv, config.BlockSizeBits, pkcs7);
                                    return solution;
                                }
                            }
                        }
                    }

                    return null;
                }
            }

            private List<byte[]> BuildIvCandidates(int blockSize, byte[] first, int revisionLength)
            {
                var candidates = new List<byte[]>();
                byte[]? composite = FindCompositeIv(blockSize, first, revisionLength);
                if (composite != null)
                {
                    candidates.Add(composite);
                    return candidates;
                }

                string? known = FindRevision();
                string text = known != null && known.Length == revisionLength
                    ? known
                    : new string('0', revisionLength - 7) + "0.0.0f1";
                foreach (int pad in blockSize == 16 ? new[] { 1 } : new[] { 1, blockSize })
                {
                    foreach (int formatVersion in new[] { 7, 6 })
                    {
                        var iv = new byte[blockSize];
                        for (int i = 0; i < 8; i++)
                        {
                            iv[i] = (byte)(first[i] ^ BundlePrefix[i]);
                        }

                        for (int i = 8; i < 12; i++)
                        {
                            iv[i] = i == 11 ? (byte)(first[i] ^ formatVersion) : first[i];
                        }

                        for (int i = 12; i < 18 && i < blockSize; i++)
                        {
                            iv[i] = (byte)(first[i] ^ PlayerVersion[i - 12]);
                        }

                        ulong size = (ulong)(fileSize - pad);
                        for (int i = 18; i < blockSize; i++)
                        {
                            int index = i - 18;
                            byte plain;
                            if (index < revisionLength)
                            {
                                plain = (byte)text[index];
                            }
                            else if (index == revisionLength)
                            {
                                plain = 0;
                            }
                            else
                            {
                                plain = (byte)(size >> (8 * (8 + revisionLength - index)));
                            }

                            iv[i] = (byte)(first[i] ^ plain);
                        }

                        if (!candidates.Any(existing => existing.AsSpan().SequenceEqual(iv)))
                        {
                            candidates.Add(iv);
                        }
                    }
                }

                return candidates;
            }

            private byte[]? FindCompositeIv(int blockSize, byte[] first, int revisionLength)
            {
                List<byte[]>?[] byLength = fragments.All.ByLength;
                foreach (byte[] single in byLength[blockSize] ?? NoFragments)
                {
                    if (IsConsistentIv(single, first, revisionLength))
                    {
                        return (byte[])single.Clone();
                    }
                }

                for (int leftLength = MinFragmentLength; leftLength <= blockSize - MinFragmentLength; leftLength++)
                {
                    List<byte[]>? lefts = byLength[leftLength];
                    List<byte[]>? rights = byLength[blockSize - leftLength];
                    if (lefts == null || rights == null)
                    {
                        continue;
                    }

                    foreach (byte[] left in lefts)
                    {
                        if (!MatchesKnownBytes(left, 0, blockSize, first))
                        {
                            continue;
                        }

                        foreach (byte[] right in rights)
                        {
                            if (!MatchesKnownBytes(right, leftLength, blockSize, first))
                            {
                                continue;
                            }

                            var iv = new byte[blockSize];
                            Buffer.BlockCopy(left, 0, iv, 0, left.Length);
                            Buffer.BlockCopy(right, 0, iv, left.Length, right.Length);
                            if (IsConsistentIv(iv, first, revisionLength))
                            {
                                return iv;
                            }
                        }
                    }
                }

                return null;
            }

            private static bool MatchesKnownBytes(byte[] fragment, int position, int blockSize, byte[] first)
            {
                for (int j = 0; j < fragment.Length; j++)
                {
                    int i = position + j;
                    if (i < 8)
                    {
                        if (fragment[j] != (first[i] ^ BundlePrefix[i]))
                        {
                            return false;
                        }
                    }
                    else if (i >= 12 && i < 18 && i < blockSize && fragment[j] != (first[i] ^ PlayerVersion[i - 12]))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool IsConsistentIv(ReadOnlySpan<byte> iv, ReadOnlySpan<byte> first, int revisionLength)
            {
                for (int i = 0; i < 8; i++)
                {
                    if ((iv[i] ^ first[i]) != BundlePrefix[i])
                    {
                        return false;
                    }
                }

                uint formatVersion = ((uint)(iv[8] ^ first[8]) << 24) | ((uint)(iv[9] ^ first[9]) << 16)
                    | ((uint)(iv[10] ^ first[10]) << 8) | (uint)(iv[11] ^ first[11]);
                if (formatVersion is < 1 or > 12)
                {
                    return false;
                }

                for (int i = 12; i < 18 && i < iv.Length; i++)
                {
                    if ((iv[i] ^ first[i]) != PlayerVersion[i - 12])
                    {
                        return false;
                    }
                }

                int sizePosition = 19 + revisionLength;
                for (int i = 18; i < iv.Length && i <= 18 + revisionLength; i++)
                {
                    byte plain = (byte)(iv[i] ^ first[i]);
                    if (i - 18 < revisionLength ? !IsVersionChar(plain) : plain != 0)
                    {
                        return false;
                    }
                }

                for (int pad = 0; pad <= iv.Length; pad++)
                {
                    ulong size = (ulong)(fileSize - pad);
                    bool matches = true;
                    for (int i = Math.Max(sizePosition, 18); i < iv.Length; i++)
                    {
                        if ((iv[i] ^ first[i]) != (byte)(size >> (8 * (7 - (i - sizePosition)))))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        return true;
                    }
                }

                return iv.Length <= sizePosition;
            }

            private string? FindRevision()
            {
                if (!revisionProbed)
                {
                    revisionProbed = true;
                    string? dataDirectory = DecryptionSources.GetDataDirectory(sourceKey);
                    if (dataDirectory != null)
                    {
                        revision = ReadSerializedRevision(Path.Combine(dataDirectory, "globalgamemanagers"))
                            ?? ReadBundleRevision(Path.Combine(dataDirectory, "data.unity3d"));
                    }
                }

                return revision;
            }

            private bool Validate(byte[] key, byte[] iv, CipherConfig config, bool pkcs7)
            {
                Stream decoded = CreateStream(stream, key, iv, config.BlockSizeBits, pkcs7);
                try
                {
                    return ValidateDecoded(decoded);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
                {
                    return false;
                }
                finally
                {
                    ReleaseWrapper(decoded);
                }
            }

            private static bool ValidateDecoded(Stream stream)
            {
                long length = stream.Length;
                var header = new byte[(int)Math.Min(1024, length)];
                if (!ReadExactly(stream, 0, header)
                    || !UnityHeaderProbe.TryParseUnityBundleHeader(header, 0, length, out _, out uint formatVersion, out _, out _, requireStrictValidation: true))
                {
                    return false;
                }

                int position = 12;
                for (int token = 0; token < 2; token++)
                {
                    int end = header.AsSpan(position).IndexOf((byte)0);
                    if (end < 0)
                    {
                        return false;
                    }

                    position += end + 1;
                }

                if (position + 20 > header.Length)
                {
                    return false;
                }

                ulong size = ReadUInt64BE(header, position);
                uint compressed = ReadUInt32BE(header, position + 8);
                uint uncompressed = ReadUInt32BE(header, position + 12);
                uint flags = ReadUInt32BE(header, position + 16);
                position += 20;
                if (formatVersion >= 7)
                {
                    position = (position + 15) & ~15;
                }
                if (size != (ulong)length || compressed == 0 || uncompressed == 0 || compressed > MaxBlocksInfoSize)
                {
                    return false;
                }

                bool atEnd = (flags & BlocksInfoAtTheEnd) != 0;
                long blocksInfoOffset = atEnd ? length - compressed : position;
                long dataStart = atEnd ? position : position + compressed;
                if ((flags & BlocksInfoNeedPaddingAtStart) != 0)
                {
                    dataStart = (dataStart + 15) & ~15;
                }

                if (blocksInfoOffset < 0 || blocksInfoOffset + compressed > length || dataStart < 0 || dataStart > length)
                {
                    return false;
                }

                var compressedInfo = new byte[compressed];
                if (!ReadExactly(stream, blocksInfoOffset, compressedInfo))
                {
                    return false;
                }

                byte[] info;
                switch (flags & 0x3F)
                {
                    case 0:
                        if (compressedInfo.Length != uncompressed)
                        {
                            return false;
                        }

                        info = compressedInfo;
                        break;

                    case 2:
                    case 3:
                        if (!Lz4BlockValidator.IsValid(compressedInfo, (int)uncompressed))
                        {
                            return false;
                        }

                        info = new byte[uncompressed];
                        try
                        {
                            if (LZ4Codec.Decode(compressedInfo, info) != info.Length)
                            {
                                return false;
                            }
                        }
                        catch
                        {
                            return false;
                        }

                        break;

                    default:
                        return false;
                }

                if (info.Length < 24)
                {
                    return false;
                }

                int blockCount = (int)ReadUInt32BE(info, 16);
                if (blockCount <= 0 || blockCount > 1_000_000 || 20L + blockCount * 10L + 4 > info.Length)
                {
                    return false;
                }

                int directoryOffset = 20 + blockCount * 10;
                int nodeCount = (int)ReadUInt32BE(info, directoryOffset);
                if (nodeCount <= 0 || nodeCount > 100_000)
                {
                    return false;
                }

                bool hasFirstNode = directoryOffset + 24 <= info.Length
                    && (ReadUInt32BE(info, directoryOffset + 20) & 4u) != 0
                    && ReadUInt64BE(info, directoryOffset + 4) == 0;
                long firstNodeSize = hasFirstNode ? (long)ReadUInt64BE(info, directoryOffset + 12) : 0;

                long cursor = dataStart;
                uint firstUncompressed = 0, firstCompressed = 0, firstFlags = 0;
                for (int i = 0; i < blockCount; i++)
                {
                    int entry = 20 + i * 10;
                    uint uncompressedSize = ReadUInt32BE(info, entry);
                    uint compressedSize = ReadUInt32BE(info, entry + 4);
                    uint blockFlags = (uint)(info[entry + 8] << 8 | info[entry + 9]);
                    if (compressedSize == 0 || uncompressedSize == 0 || cursor + compressedSize > length)
                    {
                        return false;
                    }

                    if (i == 0)
                    {
                        firstUncompressed = uncompressedSize;
                        firstCompressed = compressedSize;
                        firstFlags = blockFlags;
                    }

                    cursor += compressedSize;
                }

                long paddingEnd = atEnd ? blocksInfoOffset : length;
                if (cursor > paddingEnd || paddingEnd - cursor > MaxBlocksInfoSize || !IsZeroPadding(stream, cursor, paddingEnd))
                {
                    return false;
                }

                long nodeSize = hasFirstNode && firstNodeSize >= 48 && firstNodeSize <= int.MaxValue ? firstNodeSize : -1;
                return ValidateFirstBlock(stream, dataStart, firstCompressed, firstUncompressed, firstFlags, nodeSize);
            }

            private static bool ValidateFirstBlock(Stream stream, long offset, uint compressed, uint uncompressed, uint flags, long nodeSize)
            {
                uint type = flags & 0x3F;
                if (type == 0)
                {
                    if (compressed != uncompressed)
                    {
                        return false;
                    }

                    var raw = new byte[(int)Math.Min(compressed, 128u)];
                    return ReadExactly(stream, offset, raw) && (nodeSize < 0 || UnityHeaderProbe.IsSerializedFileHeader(raw, nodeSize));
                }

                if (type is not (2 or 3))
                {
                    return true;
                }

                if (compressed > MaxProbedBlockCompressed || uncompressed > MaxProbedBlockUncompressed)
                {
                    return true;
                }

                var compressedBuffer = new byte[compressed];
                if (!ReadExactly(stream, offset, compressedBuffer) || !Lz4BlockValidator.IsValid(compressedBuffer, (int)uncompressed))
                {
                    return false;
                }

                if (nodeSize < 0)
                {
                    return true;
                }

                var uncompressedBuffer = new byte[uncompressed];
                try
                {
                    return LZ4Codec.Decode(compressedBuffer, uncompressedBuffer) == (int)uncompressed
                        && UnityHeaderProbe.IsSerializedFileHeader(uncompressedBuffer, nodeSize);
                }
                catch
                {
                    return false;
                }
            }

            private static bool IsZeroPadding(Stream stream, long start, long end)
            {
                var buffer = new byte[8192];
                while (start < end)
                {
                    int count = (int)Math.Min(buffer.Length, end - start);
                    if (!ReadExactly(stream, start, buffer.AsSpan(0, count)))
                    {
                        return false;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] != 0)
                        {
                            return false;
                        }
                    }

                    start += count;
                }

                return true;
            }
        }
    }
}
