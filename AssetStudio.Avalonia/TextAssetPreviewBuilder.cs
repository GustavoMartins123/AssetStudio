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
    private const int AmmoStringMaxLength = 4096;
    private const int MaxAmmoDialogueStrings = 180;
    private const int MaxAmmoCommandStrings = 120;
    private const int MaxAmmoTechnicalStrings = 260;
    private const int MaxAmmoStringTableEntries = 360;

    public static string Build(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        return BuildPreview(assetItem, data, fbxHeader).DetailsText;
    }

    public static TextAssetPreviewResult BuildPreview(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        var ammoStrings = ExtractAmmoEpisodeBinaryStrings(data);
        if (IsAmmoEpisodeStringTable(ammoStrings))
        {
            return BuildAmmoEpisodePreview(assetItem, data, fbxHeader, ammoStrings);
        }

        return BuildGenericPreview(assetItem, data, fbxHeader);
    }

    private static TextAssetPreviewResult BuildGenericPreview(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fbxHeader))
        {
            sb.Append(fbxHeader);
        }

        CountBinaryMarkers(data, out var nullBytes, out var controlBytes, out var highBitBytes);

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

        return new TextAssetPreviewResult(
            sb.ToString(),
            "Generic TextAsset",
            0,
            Array.Empty<TextAssetDialogueCard>());
    }

    private static TextAssetPreviewResult BuildAmmoEpisodePreview(
        AssetItem assetItem,
        byte[] data,
        string fbxHeader,
        IReadOnlyList<BinaryStringEntry> strings)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fbxHeader))
        {
            sb.Append(fbxHeader);
        }

        CountBinaryMarkers(data, out var nullBytes, out var controlBytes, out var highBitBytes);

        var commandEntries = strings
            .Where(entry => IsAmmoCommandClass(entry.Text))
            .GroupBy(entry => entry.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var dialogueIndexes = new List<int>();
        for (int i = 0; i < strings.Count; i++)
        {
            if (IsLocalizedOrDialogueString(strings[i].Text))
            {
                dialogueIndexes.Add(i);
            }
        }

        var technicalEntries = strings
            .Where(entry => !IsAmmoCommandClass(entry.Text) && !IsLocalizedOrDialogueString(entry.Text))
            .ToList();

        sb.AppendLine($"TextAsset: {assetItem.Name}");
        sb.AppendLine($"Bytes: {data.Length:N0}");
        sb.AppendLine("Detected format: Ammo episode binary strings (0x0C + UInt16LE length + UTF-8)");
        sb.AppendLine($"Parsed strings: {strings.Count:N0}");
        sb.AppendLine($"Command classes: {commandEntries.Count:N0}");
        sb.AppendLine($"Localized/dialogue candidates: {dialogueIndexes.Count:N0}");
        sb.AppendLine($"Binary markers: {nullBytes:N0} NUL bytes, {controlBytes:N0} control bytes, {highBitBytes:N0} bytes >= 0x80");
        sb.AppendLine();

        if (dialogueIndexes.Count > 0)
        {
            sb.AppendLine($"Localized / dialogue strings ({dialogueIndexes.Count:N0}):");
            int written = 0;
            foreach (var index in dialogueIndexes)
            {
                if (written >= MaxAmmoDialogueStrings)
                {
                    AppendTruncatedCount(sb, dialogueIndexes.Count - written);
                    break;
                }

                var label = FindNearbyLabel(strings, index);
                AppendEntryLine(sb, written + 1, strings[index], label);
                written++;
            }
            sb.AppendLine();
        }

        if (commandEntries.Count > 0)
        {
            sb.AppendLine($"Command classes ({commandEntries.Count:N0}):");
            for (int i = 0; i < commandEntries.Count && i < MaxAmmoCommandStrings; i++)
            {
                AppendEntryLine(sb, i + 1, commandEntries[i], string.Empty);
            }
            if (commandEntries.Count > MaxAmmoCommandStrings)
            {
                AppendTruncatedCount(sb, commandEntries.Count - MaxAmmoCommandStrings);
            }
            sb.AppendLine();
        }

        if (technicalEntries.Count > 0)
        {
            sb.AppendLine($"Technical strings ({technicalEntries.Count:N0}):");
            for (int i = 0; i < technicalEntries.Count && i < MaxAmmoTechnicalStrings; i++)
            {
                AppendEntryLine(sb, i + 1, technicalEntries[i], string.Empty);
            }
            if (technicalEntries.Count > MaxAmmoTechnicalStrings)
            {
                AppendTruncatedCount(sb, technicalEntries.Count - MaxAmmoTechnicalStrings);
            }
            sb.AppendLine();
        }

        sb.AppendLine($"String table order (first {Math.Min(strings.Count, MaxAmmoStringTableEntries):N0} of {strings.Count:N0}):");
        for (int i = 0; i < strings.Count && i < MaxAmmoStringTableEntries; i++)
        {
            AppendEntryLine(sb, i + 1, strings[i], string.Empty);
        }
        if (strings.Count > MaxAmmoStringTableEntries)
        {
            AppendTruncatedCount(sb, strings.Count - MaxAmmoStringTableEntries);
        }

        var cards = BuildAmmoDialogueCards(strings, dialogueIndexes);
        return new TextAssetPreviewResult(
            sb.ToString(),
            "Ammo episode binary strings",
            strings.Count,
            cards);
    }

    private static List<TextAssetDialogueCard> BuildAmmoDialogueCards(
        IReadOnlyList<BinaryStringEntry> strings,
        IReadOnlyList<int> dialogueIndexes)
    {
        var cards = new List<TextAssetDialogueCard>();
        foreach (var index in dialogueIndexes)
        {
            var entry = strings[index];
            var label = FindNearbyLabel(strings, index);
            if (entry.Text.Contains("\"Messages\"", StringComparison.Ordinal) ||
                IsLikelySpeakerString(entry.Text, label))
            {
                continue;
            }

            var displayText = NormalizeDialogueText(entry.Text);
            if (string.IsNullOrWhiteSpace(displayText))
            {
                continue;
            }

            cards.Add(new TextAssetDialogueCard(
                entry.Offset,
                entry.Length,
                label,
                FindNearbySpeaker(strings, index),
                displayText,
                GetDialogueKind(label)));
        }

        return cards;
    }

    private static List<BinaryStringEntry> ExtractAmmoEpisodeBinaryStrings(byte[] data)
    {
        var strings = new List<BinaryStringEntry>();
        for (int offset = 0; offset + 3 <= data.Length; offset++)
        {
            if (data[offset] != 0x0C)
            {
                continue;
            }

            int length = data[offset + 1] | (data[offset + 2] << 8);
            if (length <= 0 || length > AmmoStringMaxLength || offset + 3 + length > data.Length)
            {
                continue;
            }

            if (!TryDecodeUtf8Strict(data, offset + 3, length, out var text))
            {
                continue;
            }

            text = text.Trim();
            if (!IsCleanAmmoString(text))
            {
                continue;
            }

            strings.Add(new BinaryStringEntry(offset, length, text));
            offset += 2 + length;
        }

        return strings;
    }

    private static bool IsAmmoEpisodeStringTable(IReadOnlyList<BinaryStringEntry> strings)
    {
        if (strings.Count < 16)
        {
            return false;
        }

        int ammoClassCount = 0;
        bool hasEpisodeCommand = false;
        foreach (var entry in strings)
        {
            if (!IsAmmoCommandClass(entry.Text))
            {
                continue;
            }

            ammoClassCount++;
            if (entry.Text.Contains("EpisodeCommand", StringComparison.Ordinal))
            {
                hasEpisodeCommand = true;
            }
        }

        return ammoClassCount >= 2 && hasEpisodeCommand;
    }

    private static bool IsAmmoCommandClass(string text)
    {
        return text.StartsWith("Ammo.", StringComparison.Ordinal);
    }

    private static bool IsLocalizedOrDialogueString(string text)
    {
        if (IsAmmoCommandClass(text) || text.StartsWith("UnityEngine.", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("<color=", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"Messages\"", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var ch in text)
        {
            if (ch > 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    private static string FindNearbySpeaker(IReadOnlyList<BinaryStringEntry> strings, int index)
    {
        if (index + 1 < strings.Count && IsLikelySpeakerString(strings[index + 1].Text, FindNearbyLabel(strings, index + 1)))
        {
            return NormalizeDialogueText(strings[index + 1].Text);
        }

        if (index + 2 < strings.Count &&
            strings[index + 1].Text.Equals("Name", StringComparison.Ordinal) &&
            IsLikelySpeakerString(strings[index + 2].Text, "Name"))
        {
            return NormalizeDialogueText(strings[index + 2].Text);
        }

        int start = Math.Max(0, index - 3);
        for (int i = index - 1; i >= start; i--)
        {
            if (IsLikelySpeakerString(strings[i].Text, FindNearbyLabel(strings, i)))
            {
                return NormalizeDialogueText(strings[i].Text);
            }
        }

        return string.Empty;
    }

    private static bool IsLikelySpeakerString(string text, string label)
    {
        if (label.Equals("Name", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length > 12 ||
            text.Contains('\n', StringComparison.Ordinal) ||
            text.Contains("\\n", StringComparison.Ordinal) ||
            text.Contains("<color=", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"Messages\"", StringComparison.Ordinal))
        {
            return false;
        }

        bool hasLetter = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == 'ー' || ch == '・')
            {
                continue;
            }

            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter)
            {
                hasLetter = true;
                continue;
            }

            return false;
        }

        return hasLetter;
    }

    private static string GetDialogueKind(string label)
    {
        return label is "Memo" or "Oneshot" or "StartPosition"
            ? "Note"
            : "Dialogue";
    }

    private static string NormalizeDialogueText(string text)
    {
        return RemoveUnityRichTextTags(text)
            .Replace("\\n", Environment.NewLine, StringComparison.Ordinal)
            .Trim();
    }

    private static string RemoveUnityRichTextTags(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end > i)
                {
                    var tag = text.Substring(i + 1, end - i - 1);
                    if (tag.StartsWith("color=", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("/color", StringComparison.OrdinalIgnoreCase) ||
                        tag.StartsWith("size=", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("/size", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("b", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("/b", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("i", StringComparison.OrdinalIgnoreCase) ||
                        tag.Equals("/i", StringComparison.OrdinalIgnoreCase))
                    {
                        i = end;
                        continue;
                    }
                }
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private static string FindNearbyLabel(IReadOnlyList<BinaryStringEntry> strings, int index)
    {
        int start = Math.Max(0, index - 5);
        for (int i = index - 1; i >= start; i--)
        {
            var candidate = strings[i].Text;
            if (IsLikelyFieldName(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsLikelyFieldName(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            IsAmmoCommandClass(text) ||
            IsLocalizedOrDialogueString(text) ||
            text.Length > 48 ||
            text.Contains('.', StringComparison.Ordinal) ||
            text.All(char.IsDigit))
        {
            return false;
        }

        if (!char.IsLetter(text[0]))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCleanAmmoString(string text)
    {
        if (!IsUsefulAmmoString(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch != '\r' && ch != '\n' && ch != '\t' && !IsReadableTextChar(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsefulAmmoString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 1)
        {
            return char.IsLetterOrDigit(trimmed[0]);
        }

        return IsUsefulReadableString(trimmed);
    }

    private static void CountBinaryMarkers(byte[] data, out int nullBytes, out int controlBytes, out int highBitBytes)
    {
        nullBytes = 0;
        controlBytes = 0;
        highBitBytes = 0;

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
    }

    private static void AppendEntryLine(StringBuilder sb, int number, BinaryStringEntry entry, string label)
    {
        sb.Append("  ");
        sb.Append(number.ToString("D3", CultureInfo.InvariantCulture));
        sb.Append(" @0x");
        sb.Append(entry.Offset.ToString("X6", CultureInfo.InvariantCulture));
        sb.Append(" len=");
        sb.Append(entry.Length.ToString(CultureInfo.InvariantCulture));
        sb.Append(": ");
        if (!string.IsNullOrEmpty(label))
        {
            sb.Append(label);
            sb.Append(" = ");
        }
        sb.AppendLine(ToInlinePreview(entry.Text, 260));
    }

    private static void AppendTruncatedCount(StringBuilder sb, int remaining)
    {
        sb.Append("  ... ");
        sb.Append(remaining.ToString("N0", CultureInfo.InvariantCulture));
        sb.AppendLine(" more omitted from preview.");
    }

    private static string ToInlinePreview(string text, int maxChars)
    {
        var value = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        if (value.Length > maxChars)
        {
            value = value.Substring(0, maxChars) + "...";
        }

        return value;
    }

    private sealed class BinaryStringEntry
    {
        public BinaryStringEntry(int offset, int length, string text)
        {
            Offset = offset;
            Length = length;
            Text = text;
        }

        public int Offset { get; }

        public int Length { get; }

        public string Text { get; }
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

internal sealed class TextAssetPreviewResult
{
    public TextAssetPreviewResult(
        string detailsText,
        string formatName,
        int parsedStringCount,
        IReadOnlyList<TextAssetDialogueCard> dialogueCards)
    {
        DetailsText = detailsText;
        FormatName = formatName;
        ParsedStringCount = parsedStringCount;
        DialogueCards = dialogueCards;
    }

    public string DetailsText { get; }

    public string FormatName { get; }

    public int ParsedStringCount { get; }

    public IReadOnlyList<TextAssetDialogueCard> DialogueCards { get; }

    public bool HasDialogueCards => DialogueCards.Count > 0;
}

internal sealed class TextAssetDialogueCard
{
    public TextAssetDialogueCard(int offset, int length, string label, string speaker, string text, string kind)
    {
        Offset = offset;
        Length = length;
        Label = label;
        Speaker = speaker;
        Text = text;
        Kind = kind;
    }

    public int Offset { get; }

    public int Length { get; }

    public string Label { get; }

    public string Speaker { get; }

    public string Text { get; }

    public string Kind { get; }
}
