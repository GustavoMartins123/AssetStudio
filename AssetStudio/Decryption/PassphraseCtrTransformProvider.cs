#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio
{
    public sealed class PassphraseCtrTransformProvider : IStreamTransformProvider
    {
        private const int ProbeLength = 512;
        private const int MaxCandidateChars = 128;
        private const int MaxCandidates = 2_000_000;
        private const int MaxFailedAttempts = 6;
        private const int SequentialCandidateLimit = 2048;
        private static readonly byte[] ExpectedPlain = Encoding.ASCII.GetBytes("UnityFS\0\0\0\0\0" + "5.x.");
        private static readonly string[] BundleExtensions = { ".bundle", ".enc", ".unity3d", ".ab", "" };
        private static readonly ConcurrentDictionary<string, string?> SourceKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ProjectState> Projects = new(StringComparer.OrdinalIgnoreCase);

        public string Id => "PassphraseCtr";
        public int Priority => 350;

        public static void ClearCache()
        {
            SourceKeys.Clear();
            Projects.Clear();
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
            if (initialHeader.Length < 64 || stream.Length < 64 || !stream.CanRead || !stream.CanSeek || string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length)
                || initialHeader.StartsWith(DecryptionHelper.EncryptionMagicBytes)
                || LooksLikePlainText(initialHeader.Slice(0, Math.Min(64, initialHeader.Length))))
            {
                return null;
            }

            string fileName = Path.GetFileNameWithoutExtension(filePath);
            if (fileName.Length == 0 || fileName.Length > MaxCandidateChars)
            {
                return null;
            }

            string fullPath = Path.GetFullPath(filePath);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string sourceKey = SourceKeys.GetOrAdd(directory, static d => FindSourceKey(d)) ?? string.Empty;
            if (sourceKey.Length == 0)
            {
                return null;
            }

            ProjectState state = Projects.GetOrAdd(sourceKey, static k => new ProjectState(k));
            byte[] salt = Encoding.UTF8.GetBytes(fileName);
            var cipherHead = new byte[16];
            initialHeader.Slice(0, 16).CopyTo(cipherHead);

            string? passphrase = state.Passphrase ?? state.Discover(fullPath, salt, cipherHead);
            if (passphrase == null)
            {
                return null;
            }

            byte[] key = LegacyKeyDerivation.Pbkdf1Sha1(Encoding.UTF8.GetBytes(passphrase), salt, 100, 16);
            int probe = (int)Math.Min(ProbeLength, initialHeader.Length);
            Span<byte> decoded = stackalloc byte[ProbeLength];
            initialHeader.Slice(0, probe).CopyTo(decoded);
            AesCtr.XorLittleEndian(decoded.Slice(0, probe), key, 0);

            if (!UnityHeaderProbe.TryParseUnityBundleHeader(decoded.Slice(0, probe), 0, stream.Length, out _, out _, out _, out _, requireStrictValidation: true))
            {
                return null;
            }

            long range = ResolveRange(state, stream, decoded.Slice(0, probe), key);
            if (range < -1)
            {
                return null;
            }

            return new CipherTransformStream(
                stream,
                StreamCipherAlgorithm.AesCtr,
                key,
                ivOrNonce: new byte[16],
                rangeLength: range,
                counterStart: 1,
                counterLittleEndian: true);
        }

        private static long ResolveRange(ProjectState state, Stream stream, ReadOnlySpan<byte> header, byte[] key)
        {
            lock (state.Sync)
            {
                if (state.RangeExact)
                {
                    return state.Range;
                }
            }

            UnityFsRangeProbe.Result result = UnityFsRangeProbe.Detect(stream, header, key);
            lock (state.Sync)
            {
                if (!result.Valid)
                {
                    return -2;
                }

                state.MinRange = Math.Max(state.MinRange, result.FirstPassing > 0 ? result.FirstPassing : stream.Length);
                if (!result.Whole)
                {
                    state.MaxRange = Math.Min(state.MaxRange, result.LastPassing);
                }

                if (state.MinRange >= state.MaxRange)
                {
                    state.RangeExact = true;
                    state.Range = state.MinRange;
                }

                if (state.RangeExact)
                {
                    return state.Range;
                }

                return stream.Length <= state.MinRange ? -1 : -3;
            }
        }

        private static string? FindSourceKey(string directory)
        {
            string? current = directory;
            for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
            {
                string managed = Path.Combine(current, "Managed");
                string metadata = Path.Combine(current, "il2cpp_data", "Metadata", "global-metadata.dat");
                bool hasManaged = Directory.Exists(managed);
                bool hasMetadata = File.Exists(metadata);
                if (hasManaged || hasMetadata)
                {
                    return (hasManaged ? "M:" + managed : string.Empty) + (hasMetadata ? "|I:" + metadata : string.Empty);
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        private static bool IsBundleLikeFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            foreach (string candidate in BundleExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool MatchesPassphrase(string passphrase, byte[] salt, byte[] cipherHead)
        {
            if (passphrase.Length == 0 || passphrase.Length > MaxCandidateChars || salt.Length > 1024)
            {
                return false;
            }

            int passwordLength = Encoding.UTF8.GetByteCount(passphrase);
            if (passwordLength == 0)
            {
                return false;
            }

            Span<byte> input = stackalloc byte[2048];
            input = input.Slice(0, passwordLength + salt.Length);
            Encoding.UTF8.GetBytes(passphrase, input);
            salt.AsSpan().CopyTo(input.Slice(passwordLength));

            Span<byte> hash = stackalloc byte[20];
            Span<byte> next = stackalloc byte[20];
            SHA1.HashData(input, hash);
            for (int i = 1; i < 100; i++)
            {
                SHA1.HashData(hash, next);
                next.CopyTo(hash);
            }

            var key = new byte[16];
            hash.Slice(0, 16).CopyTo(key);
            Span<byte> keystream = stackalloc byte[16];
            AesCtr.FillKeystream(key, 0, keystream);
            for (int i = 0; i < 16; i++)
            {
                byte plain = (byte)(cipherHead[i] ^ keystream[i]);
                if (i == 11)
                {
                    if (plain is < 1 or > 12)
                    {
                        return false;
                    }
                }
                else if (plain != ExpectedPlain[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string? Search(IReadOnlyList<string> candidates, byte[] salt, byte[] cipherHead)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count <= SequentialCandidateLimit)
            {
                foreach (string candidate in candidates)
                {
                    if (MatchesPassphrase(candidate, salt, cipherHead))
                    {
                        return candidate;
                    }
                }

                return null;
            }

            string? found = null;
            Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (i, loopState) =>
            {
                if (found != null)
                {
                    loopState.Stop();
                    return;
                }

                if (MatchesPassphrase(candidates[i], salt, cipherHead))
                {
                    found = candidates[i];
                    loopState.Stop();
                }
            });
            return found;
        }

        private static (List<string> Primary, List<string> Secondary) CollectCandidates(string sourceKey)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var primary = new List<string>();
            var secondary = new List<string>();
            foreach (string part in sourceKey.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (part.StartsWith("M:", StringComparison.Ordinal))
                    {
                        foreach (string dll in OrderAssemblyPaths(Directory.EnumerateFiles(part[2..], "*.dll", SearchOption.TopDirectoryOnly)))
                        {
                            AddCandidates(primary, secondary, seen, ReadUtf16Strings(File.ReadAllBytes(dll)));
                        }
                    }
                    else if (part.StartsWith("I:", StringComparison.Ordinal))
                    {
                        AddCandidates(primary, secondary, seen, ReadMetadataLiterals(File.ReadAllBytes(part[2..])));
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                if (primary.Count + secondary.Count >= MaxCandidates)
                {
                    break;
                }
            }

            return (primary, secondary);
        }

        private static IEnumerable<string> OrderAssemblyPaths(IEnumerable<string> paths)
        {
            return paths.OrderBy(static p => IsFrameworkAssembly(p) ? 1 : 0)
                .ThenBy(static p => p, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsFrameworkAssembly(string path)
        {
            string name = Path.GetFileName(path);
            return name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Mono", StringComparison.OrdinalIgnoreCase)
                || name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddCandidates(List<string> primary, List<string> secondary, HashSet<string> seen, IEnumerable<string> candidates)
        {
            foreach (string candidate in candidates)
            {
                if (primary.Count + secondary.Count >= MaxCandidates)
                {
                    return;
                }

                if (candidate.Length is 0 or > MaxCandidateChars || !seen.Add(candidate))
                {
                    continue;
                }

                bool token = candidate.Length >= 3;
                foreach (char c in candidate)
                {
                    if (c < 0x21 || c > 0x7E)
                    {
                        token = false;
                        break;
                    }
                }

                (token ? primary : secondary).Add(candidate);
            }
        }

        public static IEnumerable<string> ReadUtf16Strings(byte[] data)
        {
            int i = 0;
            while (i + 1 < data.Length)
            {
                if (IsPrintable(data[i]) && data[i + 1] == 0)
                {
                    int start = i;
                    while (i + 1 < data.Length && IsPrintable(data[i]) && data[i + 1] == 0)
                    {
                        i += 2;
                    }

                    yield return Encoding.Unicode.GetString(data, start, i - start);
                }
                else
                {
                    i++;
                }
            }
        }

        public static IEnumerable<string> ReadMetadataLiterals(byte[] data)
        {
            if (data.Length < 0x20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0xFAB11BAFu)
            {
                yield break;
            }

            bool triplets = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) >= 38;
            int literalOffset = ReadInt32(data, 8);
            int literalSize = ReadInt32(data, 12);
            int dataOffset = ReadInt32(data, triplets ? 20 : 16);
            int dataSize = ReadInt32(data, triplets ? 24 : 20);
            if (literalOffset <= 0 || literalSize <= 0 || dataOffset <= 0 || dataSize <= 0
                || (long)literalOffset + literalSize > data.Length || (long)dataOffset + dataSize > data.Length)
            {
                yield break;
            }

            int entrySize = triplets ? 4 : 8;
            for (int i = 0; i + entrySize <= literalSize; i += entrySize)
            {
                int first = ReadInt32(data, literalOffset + i);
                int index = triplets ? first : ReadInt32(data, literalOffset + i + 4);
                int length = triplets
                    ? (i + entrySize < literalSize ? ReadInt32(data, literalOffset + i + entrySize) - first : dataSize - first)
                    : first;
                if (index >= 0 && length > 0 && length <= 4096 && index + length <= dataSize)
                {
                    yield return Encoding.UTF8.GetString(data, dataOffset + index, length);
                }
            }
        }

        private static int ReadInt32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

        private static bool IsPrintable(byte value) => value >= 0x20 && value <= 0x7E;

        private static bool LooksLikePlainText(ReadOnlySpan<byte> sample)
        {
            if (sample.IsEmpty)
            {
                return false;
            }

            for (int i = 0; i < sample.Length; i++)
            {
                byte b = sample[i];
                if (b is not ((>= 0x20 and <= 0x7E) or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class ProjectState
        {
            private readonly string sourceKey;
            private readonly HashSet<string> attemptedFiles = new(StringComparer.OrdinalIgnoreCase);
            private (List<string> Primary, List<string> Secondary)? candidates;
            public readonly object Sync = new();
            public string? Passphrase;
            public bool RangeExact;
            public long Range = -1;
            public long MinRange;
            public long MaxRange = long.MaxValue;

            public ProjectState(string sourceKey)
            {
                this.sourceKey = sourceKey;
            }

            public string? Discover(string filePath, byte[] salt, byte[] cipherHead)
            {
                lock (Sync)
                {
                    if (Passphrase != null || !IsBundleLikeFile(filePath))
                    {
                        return Passphrase;
                    }

                    if (attemptedFiles.Count >= MaxFailedAttempts || !attemptedFiles.Add(filePath))
                    {
                        return Passphrase;
                    }

                    try
                    {
                        candidates ??= CollectCandidates(sourceKey);
                        Passphrase = Search(candidates.Value.Primary, salt, cipherHead) ?? Search(candidates.Value.Secondary, salt, cipherHead);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    return Passphrase;
                }
            }
        }
    }

    internal static class AesCtr
    {
        [ThreadStatic] private static Aes? threadAes;

        public static void FillKeystream(byte[] key, long blockIndex, Span<byte> keystream)
        {
            Aes aes = threadAes ??= Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            Span<byte> counter = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(counter, (ulong)blockIndex + 1);
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
        }

        public static void XorLittleEndian(Span<byte> buffer, byte[] key, long startOffset)
        {
            long block = startOffset / 16;
            int offsetInBlock = (int)(startOffset % 16);
            Span<byte> keystream = stackalloc byte[16];
            int done = 0;
            while (done < buffer.Length)
            {
                FillKeystream(key, block, keystream);
                int count = Math.Min(16 - offsetInBlock, buffer.Length - done);
                for (int i = 0; i < count; i++)
                {
                    buffer[done + i] ^= keystream[offsetInBlock + i];
                }

                done += count;
                block++;
                offsetInBlock = 0;
            }
        }
    }
}
