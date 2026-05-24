using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AssetStudio.Avalonia;

internal static class TextAssetPreviewBuilder
{
    private const int DecodeByteLimit = 1024 * 1024;
    private const int HexPreviewBytes = 4096;
    private const int SanitizedPreviewChars = 20000;
    private const int MaxReadableStrings = 240;

    public static string Build(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fbxHeader))
        {
            sb.Append(fbxHeader);
        }

        int nullBytes = 0;
        int controlBytes = 0;
        int highBitBytes = 0;
        foreach (var value in data)
        {
            if (value == 0)
            {
                nullBytes++;
            }
            if (IsBinaryControlByte(value))
            {
                controlBytes++;
            }
            if (value >= 0x80)
            {
                highBitBytes++;
            }
        }

        int decodeBytes = Math.Min(data.Length, DecodeByteLimit);
        string decoded = decodeBytes > 0 ? Encoding.UTF8.GetString(data, 0, decodeBytes) : string.Empty;
        bool strictUtf8 = TryDecodeUtf8Strict(data, 0, decodeBytes, out _);
        bool likelyBinary = nullBytes > 0 || controlBytes > Math.Max(8, data.Length / 100);
        var readableStrings = ExtractReadableStrings(decoded, MaxReadableStrings);
        if (likelyBinary)
        {
            foreach (var value in ExtractLengthPrefixedUtf8Strings(data, decodeBytes, MaxReadableStrings))
            {
                AddUniqueReadableString(readableStrings, value, MaxReadableStrings);
            }
        }

        sb.AppendLine($"TextAsset: {assetItem.Name}");
        sb.AppendLine($"Bytes: {data.Length:N0}");
        sb.AppendLine($"UTF-8 sample: {(strictUtf8 ? "valid" : "contains invalid byte sequences")}");
        sb.AppendLine($"Binary markers: {nullBytes:N0} NUL bytes, {controlBytes:N0} control bytes, {highBitBytes:N0} bytes >= 0x80");
        if (decodeBytes < data.Length)
        {
            sb.AppendLine($"Preview scan: first {decodeBytes:N0} bytes of {data.Length:N0}");
        }
        sb.AppendLine();

        if (readableStrings.Count > 0)
        {
            sb.AppendLine($"Readable strings ({readableStrings.Count:N0} found in preview scan):");
            for (int i = 0; i < readableStrings.Count; i++)
            {
                sb.Append("  ");
                sb.Append((i + 1).ToString("D3", CultureInfo.InvariantCulture));
                sb.Append(": ");
                sb.AppendLine(readableStrings[i]);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Sanitized UTF-8 preview:");
        sb.AppendLine("------------------------------------------------------------");
        sb.AppendLine(SanitizeDecodedText(decoded, SanitizedPreviewChars));
        if (decodeBytes < data.Length)
        {
            sb.AppendLine($"[Decoded preview truncated at {decodeBytes:N0} bytes. Export the asset to inspect the full binary.]");
        }
        sb.AppendLine();

        sb.AppendLine($"Hex preview (first {Math.Min(data.Length, HexPreviewBytes):N0} bytes):");
        sb.AppendLine("------------------------------------------------------------");
        sb.Append(BuildHexPreview(data, HexPreviewBytes));

        return sb.ToString();
    }

    private static bool IsBinaryControlByte(byte value)
    {
        return value < 0x20 && value != 0x09 && value != 0x0A && value != 0x0D;
    }

    private static bool TryDecodeUtf8Strict(byte[] data, int index, int count, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(data, index, count);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static List<string> ExtractReadableStrings(string decoded, int maxStrings)
    {
        var strings = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var value = current.ToString().Trim();
            current.Clear();
            AddUniqueReadableString(strings, value, maxStrings);
        }

        foreach (var ch in decoded)
        {
            if (IsReadableTextChar(ch))
            {
                current.Append(ch);
            }
            else
            {
                Flush();
                if (strings.Count >= maxStrings)
                {
                    break;
                }
            }
        }

        Flush();
        return strings;
    }

    private static List<string> ExtractLengthPrefixedUtf8Strings(byte[] data, int scanBytes, int maxStrings)
    {
        var strings = new List<string>();
        for (int offset = 0; offset < scanBytes && strings.Count < maxStrings; offset++)
        {
            if (!TryGetStringPrefix(data, scanBytes, offset, out var headerBytes, out var length))
            {
                continue;
            }

            if (offset + headerBytes + length > scanBytes)
            {
                continue;
            }

            if (!TryDecodeUtf8Strict(data, offset + headerBytes, length, out var text))
            {
                continue;
            }

            text = text.Trim();
            if (IsCleanReadableString(text))
            {
                AddUniqueReadableString(strings, text, maxStrings);
            }
        }

        return strings;
    }

    private static bool TryGetStringPrefix(byte[] data, int scanBytes, int offset, out int headerBytes, out int length)
    {
        headerBytes = 0;
        length = 0;
        if (offset >= scanBytes)
        {
            return false;
        }

        var marker = data[offset];
        if ((marker & 0xE0) == 0xA0)
        {
            headerBytes = 1;
            length = marker & 0x1F;
            return length >= 2;
        }

        if (marker == 0xD9 && offset + 1 < scanBytes)
        {
            headerBytes = 2;
            length = data[offset + 1];
            return length >= 2;
        }

        if (marker == 0xDA && offset + 2 < scanBytes)
        {
            headerBytes = 3;
            length = (data[offset + 1] << 8) | data[offset + 2];
            return length >= 2 && length <= 4096;
        }

        if (marker == 0xDB && offset + 4 < scanBytes)
        {
            headerBytes = 5;
            length = (data[offset + 1] << 24) | (data[offset + 2] << 16) | (data[offset + 3] << 8) | data[offset + 4];
            return length >= 2 && length <= 4096;
        }

        if (marker >= 2 && marker <= 200)
        {
            headerBytes = 1;
            length = marker;
            return true;
        }

        return false;
    }

    private static bool IsReadableTextChar(char ch)
    {
        if (ch == '\uFFFD' || char.IsControl(ch))
        {
            return false;
        }

        var category = char.GetUnicodeCategory(ch);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber
            or UnicodeCategory.SpaceSeparator
            or UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
    }

    private static bool IsCleanReadableString(string text)
    {
        if (!IsUsefulReadableString(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!IsReadableTextChar(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsefulReadableString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        bool hasNonAscii = false;
        bool hasLetterOrDigit = false;
        foreach (var ch in trimmed)
        {
            if (ch > 0x7F)
            {
                hasNonAscii = true;
            }
            if (char.IsLetterOrDigit(ch))
            {
                hasLetterOrDigit = true;
            }
        }

        return hasLetterOrDigit && (trimmed.Length >= 3 || (hasNonAscii && trimmed.Length >= 2));
    }

    private static void AddUniqueReadableString(List<string> strings, string value, int maxStrings)
    {
        if (strings.Count >= maxStrings || !IsUsefulReadableString(value))
        {
            return;
        }

        value = value.Trim();
        const int maxStringLength = 320;
        if (value.Length > maxStringLength)
        {
            value = value.Substring(0, maxStringLength) + "...";
        }

        if (!strings.Contains(value, StringComparer.Ordinal))
        {
            strings.Add(value);
        }
    }

    private static string SanitizeDecodedText(string decoded, int maxChars)
    {
        var sb = new StringBuilder(Math.Min(decoded.Length, maxChars) + 256);
        int consumed = 0;
        foreach (var ch in decoded)
        {
            if (consumed >= maxChars)
            {
                break;
            }

            consumed++;
            if (ch == '\0')
            {
                sb.Append("\\0");
            }
            else if (ch == '\r' || ch == '\n' || ch == '\t')
            {
                sb.Append(ch);
            }
            else if (ch == '\uFFFD')
            {
                sb.Append("\\uFFFD");
            }
            else if (char.IsControl(ch))
            {
                AppendEscapedCodeUnit(sb, ch);
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (decoded.Length > maxChars)
        {
            sb.AppendLine();
            sb.AppendLine($"[Sanitized text truncated at {maxChars:N0} decoded characters.]");
        }

        return sb.ToString();
    }

    private static void AppendEscapedCodeUnit(StringBuilder sb, char ch)
    {
        if (ch <= 0xFF)
        {
            sb.Append("\\x");
            sb.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("\\u");
            sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    private static string BuildHexPreview(byte[] data, int maxBytes)
    {
        int count = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder((count / 16 + 1) * 80);
        for (int offset = 0; offset < count; offset += 16)
        {
            sb.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
            sb.Append("  ");

            for (int i = 0; i < 16; i++)
            {
                int index = offset + i;
                if (index < count)
                {
                    sb.Append(data[index].ToString("X2", CultureInfo.InvariantCulture));
                    sb.Append(' ');
                }
                else
                {
                    sb.Append("   ");
                }

                if (i == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");
            for (int i = 0; i < 16 && offset + i < count; i++)
            {
                var value = data[offset + i];
                sb.Append(value >= 0x20 && value <= 0x7E ? (char)value : '.');
            }
            sb.AppendLine("|");
        }

        if (count < data.Length)
        {
            sb.AppendLine($"[Hex preview truncated at {count:N0} bytes.]");
        }

        return sb.ToString();
    }
}
