#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public interface IStreamTransformProvider
    {
        string Id { get; }
        int Priority { get; }
        Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader);
    }

    public sealed class DecryptionProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("fileNamePattern")]
        public string? FileNamePattern { get; set; }

        [JsonPropertyName("magicHex")]
        public string? MagicHex { get; set; }

        [JsonPropertyName("magicOffset")]
        public int MagicOffset { get; set; }

        [JsonPropertyName("skipBytes")]
        public long SkipBytes { get; set; }

        [JsonPropertyName("xorKeyHex")]
        public string? XorKeyHex { get; set; }

        [JsonPropertyName("rangeStart")]
        public long RangeStart { get; set; }

        [JsonPropertyName("rangeLength")]
        public long RangeLength { get; set; } = -1;
    }

    public sealed class DeclarativeProfileTransformProvider : IStreamTransformProvider
    {
        private readonly Func<IReadOnlyList<DecryptionProfile>> getProfiles;

        public DeclarativeProfileTransformProvider(Func<IReadOnlyList<DecryptionProfile>> getProfiles)
        {
            this.getProfiles = getProfiles ?? throw new ArgumentNullException(nameof(getProfiles));
        }

        public string Id => "DeclarativeProfile";
        public int Priority => 200;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            var profiles = getProfiles();
            if (profiles.Count == 0)
            {
                return null;
            }

            string fileName = Path.GetFileName(filePath);
            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (!MatchesProfile(profile, fileName, initialHeader, stream.Length))
                {
                    continue;
                }

                byte[]? xorKey = TryParseHexBytes(profile.XorKeyHex);
                if (xorKey != null && xorKey.Length > 0)
                {
                    return new XorTransformStream(
                        stream,
                        xorKey,
                        headerSkipBytes: Math.Max(0, profile.SkipBytes),
                        rangeStart: Math.Max(0, profile.RangeStart),
                        rangeLength: profile.RangeLength);
                }

                if (profile.SkipBytes > 0 && profile.SkipBytes < stream.Length)
                {
                    return new SubStream(stream, profile.SkipBytes, stream.Length - profile.SkipBytes, leaveOpen: false);
                }
            }

            return null;
        }

        private static bool MatchesProfile(
            DecryptionProfile profile,
            string fileName,
            ReadOnlySpan<byte> initialHeader,
            long streamLength)
        {
            if (profile.SkipBytes < 0 || profile.SkipBytes >= streamLength)
            {
                return false;
            }

            bool hasMatcher = false;

            if (!string.IsNullOrWhiteSpace(profile.FileNamePattern))
            {
                hasMatcher = true;
                if (!MatchesWildcard(fileName, profile.FileNamePattern!))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.MagicHex))
            {
                hasMatcher = true;
                byte[]? magic = TryParseHexBytes(profile.MagicHex);
                if (magic == null || magic.Length == 0 || !UnityHeaderProbe.MatchesAt(initialHeader, profile.MagicOffset, magic))
                {
                    return false;
                }
            }

            return hasMatcher;
        }

        private static bool MatchesWildcard(string fileName, string pattern)
            => FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true);

        internal static byte[]? TryParseHexBytes(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            Span<char> cleaned = stackalloc char[hex.Length];
            int len = 0;
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                if (!char.IsWhiteSpace(c) && c != '-')
                {
                    cleaned[len++] = c;
                }
            }

            if (len == 0 || (len & 1) != 0)
            {
                return null;
            }

            try
            {
                return Convert.FromHexString(cleaned.Slice(0, len));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public sealed class OffsetHeaderSkipTransformProvider : IStreamTransformProvider
    {
        public string Id => "OffsetHeaderSkip";
        public int Priority => 300;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 32)
            {
                return null;
            }

            // Do not skip if offset 0 is already a valid Unity bundle or SerializedFile
            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length))
            {
                return null;
            }

            if (!UnityHeaderProbe.TryFindOffsetUnityBundleHeader(initialHeader, stream.Length, out int offset, out _, out _, out _)
                || offset <= 0
                || offset >= stream.Length)
            {
                return null;
            }

            return new SubStream(stream, offset, stream.Length - offset, leaveOpen: false);
        }
    }

    public sealed class KnownPlaintextXorTransformProvider : IStreamTransformProvider
    {
        public string Id => "KnownPlaintextXor";
        public int Priority => 400;

        public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
        {
            if (initialHeader.Length < 32)
            {
                return null;
            }

            if (UnityHeaderProbe.TryParseUnityBundleHeader(initialHeader, 0, stream.Length, out _, out _, out _, out _)
                || UnityHeaderProbe.IsSerializedFileHeader(initialHeader, stream.Length))
            {
                return null;
            }

            if (!UnityHeaderProbe.TryDetectRepeatingXorUnityFsKey(initialHeader, stream.Length, out byte[] xorKey, out _, out _))
            {
                return null;
            }

            return new XorTransformStream(stream, xorKey);
        }
    }
}
