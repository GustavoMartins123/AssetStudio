#nullable enable
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AssetStudio
{
    /// <summary>
    /// Compact 12-byte value key replacing full concatenated UniqueID strings in ProjectIndex dictionaries.
    /// Packed to 4-byte alignment so (int SourceOriginId, long PathID) occupies exactly 12 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly record struct AssetHandleKey(int SourceOriginId, long PathID)
    {
        public const int SizeInBytes = 12;

        public int SourceOriginId { get; init; } = SourceOriginId;
        public long PathID { get; init; } = PathID;

        public bool IsEmpty => SourceOriginId <= 0 && PathID == 0;

        public override int GetHashCode()
        {
            return HashCode.Combine(SourceOriginId, PathID);
        }

        /// <summary>
        /// Zero-allocation parser for AssetHandle.UniqueID format "{SerializedFileName}#{SourceHash}#{PathID}".
        /// Handles '#' characters inside file names as well as empty source hashes.
        /// </summary>
        public static bool TryParseUniqueId(
            ReadOnlySpan<char> uniqueId,
            out ReadOnlySpan<char> fileName,
            out ReadOnlySpan<char> sourceHash,
            out long pathId)
        {
            return TryParseUniqueIdWithPrefix(
                uniqueId,
                out _,
                out fileName,
                out sourceHash,
                out pathId,
                out _);
        }

        public static bool TryParseUniqueId(
            string? uniqueId,
            out ReadOnlySpan<char> fileName,
            out ReadOnlySpan<char> sourceHash,
            out long pathId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                fileName = default;
                sourceHash = default;
                pathId = 0;
                return false;
            }

            return TryParseUniqueId(uniqueId.AsSpan(), out fileName, out sourceHash, out pathId);
        }

        internal static bool TryParseUniqueIdWithPrefix(
            ReadOnlySpan<char> uniqueId,
            out ReadOnlySpan<char> originPrefix,
            out ReadOnlySpan<char> fileName,
            out ReadOnlySpan<char> sourceHash,
            out long pathId,
            out bool isCanonicalPathId)
        {
            originPrefix = default;
            fileName = default;
            sourceHash = default;
            pathId = 0;
            isCanonicalPathId = false;

            if (uniqueId.IsEmpty)
            {
                return false;
            }

            int lastHash = uniqueId.LastIndexOf('#');
            if (lastHash <= 0 || lastHash >= uniqueId.Length - 1)
            {
                return false;
            }

            ReadOnlySpan<char> pathIdSpan = uniqueId.Slice(lastHash + 1);
            if (!long.TryParse(pathIdSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out pathId))
            {
                return false;
            }

            ReadOnlySpan<char> prefix = uniqueId.Slice(0, lastHash);
            int secondLastHash = prefix.LastIndexOf('#');
            if (secondLastHash <= 0)
            {
                return false;
            }

            ReadOnlySpan<char> candidateFileName = prefix.Slice(0, secondLastHash);
            if (candidateFileName.IsEmpty || candidateFileName.IsWhiteSpace())
            {
                return false;
            }

            ReadOnlySpan<char> candidateHash = prefix.Slice(secondLastHash + 1);
            for (int i = 0; i < candidateHash.Length; i++)
            {
                if (char.IsWhiteSpace(candidateHash[i]))
                {
                    return false;
                }
            }

            originPrefix = prefix;
            fileName = candidateFileName;
            sourceHash = candidateHash;
            isCanonicalPathId = IsCanonicalInt64Span(pathIdSpan, pathId);
            return true;
        }

        private static bool IsCanonicalInt64Span(ReadOnlySpan<char> span, long parsedValue)
        {
            if (span.IsEmpty)
            {
                return false;
            }

            if (parsedValue == 0)
            {
                return span.Length == 1 && span[0] == '0';
            }

            if (parsedValue > 0)
            {
                return span[0] >= '1' && span[0] <= '9';
            }

            return span.Length >= 2 && span[0] == '-' && span[1] >= '1' && span[1] <= '9';
        }
    }
}
