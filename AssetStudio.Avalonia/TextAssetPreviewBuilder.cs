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
    private const int MaxAmmoDecodedCommands = 160;

    public static string Build(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        return BuildPreview(assetItem, data, fbxHeader).DetailsText;
    }

    public static TextAssetPreviewResult BuildPreview(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        if ((TryExtractAmmoEpisodeStringTable(data, out var ammoTable) ||
             TryExtractAmmoEpisodeStringTableLenient(data, out ammoTable)) &&
            IsAmmoEpisodeStringTable(ammoTable.Strings))
        {
            return BuildAmmoEpisodePreview(assetItem, data, fbxHeader, ammoTable);
        }

        return BuildGenericPreview(assetItem, data, fbxHeader);
    }

    private static TextAssetPreviewResult BuildGenericPreview(AssetItem assetItem, byte[] data, string fbxHeader)
    {
        CountBinaryMarkers(data, out var nullBytes, out var controlBytes, out var highBitBytes);

        int decodeBytes = Math.Min(data.Length, DecodeByteLimit);
        string decoded = decodeBytes > 0 ? Encoding.UTF8.GetString(data, 0, decodeBytes) : string.Empty;
        bool strictUtf8 = TryDecodeUtf8Strict(data, 0, decodeBytes, out _);
        bool likelyBinary = nullBytes > 0 || controlBytes > Math.Max(8, data.Length / 100);
        if (strictUtf8 && !likelyBinary)
        {
            return BuildPlainTextPreview(data, fbxHeader, decoded, decodeBytes);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fbxHeader))
        {
            sb.Append(fbxHeader);
        }

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

    private static TextAssetPreviewResult BuildPlainTextPreview(byte[] data, string fbxHeader, string decoded, int decodeBytes)
    {
        var sb = new StringBuilder(decoded.Length + fbxHeader.Length + 128);
        if (!string.IsNullOrEmpty(fbxHeader))
        {
            sb.Append(fbxHeader);
        }

        sb.Append(decoded);
        if (decodeBytes < data.Length)
        {
            sb.AppendLine();
            sb.AppendLine($"[Text preview truncated at {decodeBytes:N0} bytes of {data.Length:N0}.]");
        }

        return new TextAssetPreviewResult(
            sb.ToString(),
            "UTF-8 text",
            0,
            Array.Empty<TextAssetDialogueCard>());
    }

    private static TextAssetPreviewResult BuildAmmoEpisodePreview(
        AssetItem assetItem,
        byte[] data,
        string fbxHeader,
        AmmoStringTable stringTable)
    {
        var strings = stringTable.Strings;
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fbxHeader))
        {
            sb.Append(fbxHeader);
        }

        CountBinaryMarkers(data, out var nullBytes, out var controlBytes, out var highBitBytes);

        var commandEntries = strings
            .Where(entry => !entry.IsEmpty && IsAmmoCommandClass(entry.Text))
            .GroupBy(entry => entry.Text, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var dialogueIndexes = new List<int>();
        for (int i = 0; i < strings.Count; i++)
        {
            if (!strings[i].IsEmpty && IsLocalizedOrDialogueString(strings[i].Text))
            {
                dialogueIndexes.Add(i);
            }
        }

        var technicalEntries = strings
            .Where(entry => !entry.IsEmpty && !IsAmmoCommandClass(entry.Text) && !IsLocalizedOrDialogueString(entry.Text))
            .ToList();

        var commandStream = DecodeAmmoCommandStream(data, stringTable);

        sb.AppendLine($"TextAsset: {assetItem.Name}");
        sb.AppendLine($"Bytes: {data.Length:N0}");
        sb.AppendLine("Detected format: Ammo episode binary strings (0x0C + UInt16LE length + UTF-8)");
        sb.AppendLine($"String table: {stringTable.DeclaredCount:N0} declared, {strings.Count(entry => !entry.IsEmpty):N0} non-empty");
        sb.AppendLine($"Command classes: {commandEntries.Count:N0}");
        sb.AppendLine($"Localized/dialogue candidates: {dialogueIndexes.Count:N0}");
        if (commandStream.Commands.Count > 0)
        {
            sb.AppendLine($"Decoded command stream: {commandStream.Commands.Count:N0} commands parsed from offset 0x{commandStream.StreamOffset.ToString("X6", CultureInfo.InvariantCulture)}");
            sb.AppendLine("Card preview: extracted from decoded command fields; unsupported value tags are shown in the text report.");
        }
        else
        {
            sb.AppendLine("Card preview: extracted from the string table; speaker/order are best-effort hints, not a decoded command stream.");
        }
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

        if (commandStream.Commands.Count > 0)
        {
            AppendDecodedCommandStream(sb, commandStream);
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

        var commandCards = commandStream.Commands.Count > 0
            ? BuildAmmoDialogueCardsFromCommands(commandStream.Commands)
            : new List<TextAssetDialogueCard>();
        var cards = commandCards.Count > 0
            ? commandCards
            : BuildAmmoDialogueCards(strings, dialogueIndexes);
        return new TextAssetPreviewResult(
            sb.ToString(),
            commandStream.Commands.Count > 0 ? "Ammo episode command stream" : "Ammo episode binary strings",
            strings.Count(entry => !entry.IsEmpty),
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

            if (entry.Text.Contains("\"Messages\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsLikelySpeakerString(entry.Text, label))
            {
                continue;
            }

            var displayText = NormalizeDialogueText(entry.Text);
            if (string.IsNullOrWhiteSpace(displayText) || !LooksLikeDialogueCardText(entry.Text, label))
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

    private static bool TryExtractAmmoEpisodeStringTable(byte[] data, out AmmoStringTable stringTable)
    {
        stringTable = new AmmoStringTable(Array.Empty<BinaryStringEntry>(), 0, 0);

        if (data.Length < 5 || data[2] != 0x0A)
        {
            return false;
        }

        int declaredCount = ReadUInt16LittleEndian(data, 3);
        if (declaredCount <= 0 || declaredCount > 8192)
        {
            return false;
        }

        var strings = new List<BinaryStringEntry>(declaredCount);
        int offset = 5;
        for (int index = 0; index < declaredCount; index++)
        {
            if (offset + 3 > data.Length || data[offset] != 0x0C)
            {
                return false;
            }

            int length = ReadUInt16LittleEndian(data, offset + 1);
            if (length > AmmoStringMaxLength || offset + 3 + length > data.Length)
            {
                return false;
            }

            string text = string.Empty;
            if (length > 0)
            {
                if (!TryDecodeUtf8Strict(data, offset + 3, length, out text))
                {
                    return false;
                }

                text = text.Trim();
                if (!IsCleanAmmoString(text))
                {
                    return false;
                }
            }

            strings.Add(new BinaryStringEntry(index, offset, length, text));
            offset += 3 + length;
        }

        stringTable = new AmmoStringTable(strings, offset, declaredCount);
        return true;
    }

    private static bool TryExtractAmmoEpisodeStringTableLenient(byte[] data, out AmmoStringTable stringTable)
    {
        stringTable = new AmmoStringTable(Array.Empty<BinaryStringEntry>(), 0, 0);
        if (data.Length < 8)
        {
            return false;
        }

        int offset = data[2] == 0x0A ? 5 : 0;
        while (offset + 3 <= data.Length && data[offset] != 0x0C)
        {
            offset++;
        }

        var strings = new List<BinaryStringEntry>();
        int index = 0;
        while (offset + 3 <= data.Length && data[offset] == 0x0C)
        {
            int length = ReadUInt16LittleEndian(data, offset + 1);
            if (length > AmmoStringMaxLength || offset + 3 + length > data.Length)
            {
                break;
            }

            string text = string.Empty;
            if (length > 0)
            {
                if (!TryDecodeUtf8Strict(data, offset + 3, length, out text))
                {
                    break;
                }

                text = text.Trim();
                if (!IsCleanAmmoString(text))
                {
                    break;
                }
            }

            strings.Add(new BinaryStringEntry(index, offset, length, text));
            offset += 3 + length;
            index++;
        }

        if (strings.Count < 16)
        {
            return false;
        }

        int declaredCount = data[2] == 0x0A ? ReadUInt16LittleEndian(data, 3) : strings.Count;
        stringTable = new AmmoStringTable(strings, offset, declaredCount);
        return true;
    }

    private static AmmoCommandStream DecodeAmmoCommandStream(byte[] data, AmmoStringTable stringTable)
    {
        var commands = new List<AmmoCommandNode>();
        int streamOffset = stringTable.EndOffset;
        if (streamOffset + 2 > data.Length)
        {
            return new AmmoCommandStream(streamOffset, 0, commands, "No command count after string table.");
        }

        int declaredCount = ReadUInt16LittleEndian(data, streamOffset);
        if (declaredCount <= 0 || declaredCount > 4096)
        {
            return new AmmoCommandStream(streamOffset, declaredCount, commands, "Command count is outside expected range.");
        }

        int offset = streamOffset + 2;
        string status = string.Empty;
        for (int i = 0; i < declaredCount && offset < data.Length; i++)
        {
            SkipPaddingToMarker(data, ref offset, 0x0B, 8);
            if (offset >= data.Length || data[offset] != 0x0B)
            {
                status = $"Stopped before command {i + 1:N0}: expected object marker 0x0B at 0x{offset.ToString("X6", CultureInfo.InvariantCulture)}.";
                break;
            }

            if (!TryReadAmmoObject(data, stringTable, ref offset, commands.Count + 1, 0, out var command, out var error))
            {
                status = $"Stopped before command {i + 1:N0}: {error}";
                break;
            }

            commands.Add(command);
        }

        if (commands.Count == 0 && string.IsNullOrEmpty(status))
        {
            status = "No commands parsed.";
        }
        else if (commands.Count < declaredCount && string.IsNullOrEmpty(status))
        {
            status = $"Parsed {commands.Count:N0} of {declaredCount:N0} declared commands.";
        }

        return new AmmoCommandStream(streamOffset, declaredCount, commands, status);
    }

    private static bool TryReadAmmoObject(
        byte[] data,
        AmmoStringTable stringTable,
        ref int offset,
        int commandIndex,
        int depth,
        out AmmoCommandNode command,
        out string error)
    {
        command = new AmmoCommandNode(commandIndex, offset, string.Empty, Array.Empty<AmmoCommandField>());
        error = string.Empty;
        if (depth > 12)
        {
            error = "nested object depth limit reached.";
            return false;
        }

        int startOffset = offset;
        if (offset >= data.Length || data[offset] != 0x0B)
        {
            error = $"expected object marker 0x0B at 0x{offset.ToString("X6", CultureInfo.InvariantCulture)}.";
            return false;
        }
        offset++;

        if (!TryReadAmmoStringRef(data, stringTable, ref offset, out _, out var typeName))
        {
            error = $"expected object type string reference at 0x{offset.ToString("X6", CultureInfo.InvariantCulture)}.";
            return false;
        }

        if (offset + 2 > data.Length)
        {
            error = "unexpected end while reading field count.";
            return false;
        }

        int fieldCount = ReadUInt16LittleEndian(data, offset);
        offset += 2;
        if (fieldCount < 0 || fieldCount > 512)
        {
            error = $"field count {fieldCount:N0} is outside expected range.";
            return false;
        }

        var fields = new List<AmmoCommandField>(fieldCount);
        for (int i = 0; i < fieldCount; i++)
        {
            SkipPaddingToMarker(data, ref offset, 0x0D, 2);
            if (!TryReadAmmoStringRef(data, stringTable, ref offset, out _, out var fieldName))
            {
                error = $"expected field name string reference at 0x{offset.ToString("X6", CultureInfo.InvariantCulture)}.";
                return false;
            }

            if (!TryReadAmmoValue(data, stringTable, ref offset, depth + 1, out var value, out error))
            {
                error = $"{fieldName}: {error}";
                return false;
            }

            fields.Add(new AmmoCommandField(fieldName, value));
        }

        command = new AmmoCommandNode(commandIndex, startOffset, typeName, fields);
        return true;
    }

    private static bool TryReadAmmoValue(
        byte[] data,
        AmmoStringTable stringTable,
        ref int offset,
        int depth,
        out AmmoValue value,
        out string error)
    {
        value = AmmoValue.Unknown(0, offset);
        error = string.Empty;
        if (offset >= data.Length)
        {
            error = "unexpected end while reading value.";
            return false;
        }

        int valueOffset = offset;
        byte tag = data[offset++];
        switch (tag)
        {
            case 0x02:
                if (offset + 4 > data.Length)
                {
                    error = "unexpected end while reading Int32.";
                    return false;
                }
                value = AmmoValue.Integer(BitConverter.ToInt32(data, offset), valueOffset);
                offset += 4;
                return true;

            case 0x03:
                if (offset + 8 > data.Length)
                {
                    error = "unexpected end while reading Float64.";
                    return false;
                }
                value = AmmoValue.Number(BitConverter.ToDouble(data, offset), valueOffset);
                offset += 8;
                return true;

            case 0x04:
                value = AmmoValue.Boolean(true, valueOffset);
                return true;

            case 0x05:
                value = AmmoValue.Boolean(false, valueOffset);
                return true;

            case 0x0A:
                value = AmmoValue.Null(valueOffset);
                return true;

            case 0x0B:
                offset = valueOffset;
                if (!TryReadAmmoObject(data, stringTable, ref offset, 0, depth, out var nestedObject, out error))
                {
                    return false;
                }
                value = AmmoValue.Object(nestedObject, valueOffset);
                return true;

            case 0x0D:
                offset = valueOffset;
                if (!TryReadAmmoStringRef(data, stringTable, ref offset, out var stringIndex, out var text))
                {
                    error = "invalid string reference.";
                    return false;
                }
                value = AmmoValue.String(stringIndex, text, GetStringLength(stringTable, stringIndex), valueOffset);
                return true;

            default:
                value = AmmoValue.Unknown(tag, valueOffset);
                return true;
        }
    }

    private static bool TryReadAmmoStringRef(
        byte[] data,
        AmmoStringTable stringTable,
        ref int offset,
        out int stringIndex,
        out string text)
    {
        stringIndex = -1;
        text = string.Empty;
        if (offset + 3 > data.Length || data[offset] != 0x0D)
        {
            return false;
        }

        stringIndex = ReadUInt16LittleEndian(data, offset + 1);
        offset += 3;
        text = LookupString(stringTable, stringIndex);
        return true;
    }

    private static void AppendDecodedCommandStream(StringBuilder sb, AmmoCommandStream commandStream)
    {
        sb.AppendLine($"Decoded command stream (experimental, declared {commandStream.DeclaredCount:N0}, parsed {commandStream.Commands.Count:N0}):");
        if (!string.IsNullOrEmpty(commandStream.Status))
        {
            sb.Append("  ");
            sb.AppendLine(commandStream.Status);
        }

        int count = Math.Min(commandStream.Commands.Count, MaxAmmoDecodedCommands);
        for (int i = 0; i < count; i++)
        {
            var command = commandStream.Commands[i];
            sb.Append("  ");
            sb.Append(command.Index.ToString("D3", CultureInfo.InvariantCulture));
            sb.Append(" @0x");
            sb.Append(command.Offset.ToString("X6", CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(ShortCommandType(command.TypeName));
            sb.Append(" (");
            sb.Append(command.Fields.Count.ToString("N0", CultureInfo.InvariantCulture));
            sb.AppendLine(" fields)");

            foreach (var field in command.Fields)
            {
                sb.Append("      ");
                sb.Append(field.Name);
                sb.Append(" = ");
                sb.AppendLine(FormatAmmoValue(field.Value));
            }
        }

        if (commandStream.Commands.Count > MaxAmmoDecodedCommands)
        {
            AppendTruncatedCount(sb, commandStream.Commands.Count - MaxAmmoDecodedCommands);
        }
    }

    private static List<TextAssetDialogueCard> BuildAmmoDialogueCardsFromCommands(IReadOnlyList<AmmoCommandNode> commands)
    {
        var cards = new List<TextAssetDialogueCard>();
        foreach (var command in commands)
        {
            var speaker = FindCommandSpeaker(command);
            foreach (var field in command.Fields)
            {
                if (field.Value.Kind != AmmoValueKind.String ||
                    string.IsNullOrWhiteSpace(field.Value.Text) ||
                    field.Value.Text.Contains("\"Messages\"", StringComparison.Ordinal) ||
                    !LooksLikeDialogueCardText(field.Value.Text, field.Name))
                {
                    continue;
                }

                cards.Add(new TextAssetDialogueCard(
                    command.Offset,
                    field.Value.StringLength,
                    $"{command.Index.ToString("D3", CultureInfo.InvariantCulture)} {ShortCommandType(command.TypeName)}.{field.Name}",
                    speaker,
                    NormalizeDialogueText(field.Value.Text),
                    "Decoded command"));
            }
        }

        return cards;
    }

    private static string FindCommandSpeaker(AmmoCommandNode command)
    {
        foreach (var field in command.Fields)
        {
            if (field.Name.Equals("Name", StringComparison.Ordinal) &&
                field.Value.Kind == AmmoValueKind.String &&
                IsLikelySpeakerString(field.Value.Text, "Name"))
            {
                return NormalizeDialogueText(field.Value.Text);
            }
        }

        return string.Empty;
    }

    private static string FormatAmmoValue(AmmoValue value)
    {
        return value.Kind switch
        {
            AmmoValueKind.Null => "null",
            AmmoValueKind.Boolean => value.BooleanValue ? "true" : "false",
            AmmoValueKind.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
            AmmoValueKind.Number => value.NumberValue.ToString("G9", CultureInfo.InvariantCulture),
            AmmoValueKind.String => $"\"{ToInlinePreview(value.Text, 180)}\" [#{value.StringIndex.ToString(CultureInfo.InvariantCulture)}]",
            AmmoValueKind.Object => FormatAmmoObjectInline(value.ObjectValue),
            _ => $"<unknown tag 0x{value.UnknownTag.ToString("X2", CultureInfo.InvariantCulture)} @0x{value.Offset.ToString("X6", CultureInfo.InvariantCulture)}>"
        };
    }

    private static string FormatAmmoObjectInline(AmmoCommandNode? command)
    {
        if (command == null)
        {
            return "{}";
        }

        var fields = command.Fields
            .Take(8)
            .Select(field => $"{field.Name}={FormatAmmoValue(field.Value)}");
        var suffix = command.Fields.Count > 8 ? ", ..." : string.Empty;
        return $"{ShortCommandType(command.TypeName)} {{ {string.Join(", ", fields)}{suffix} }}";
    }

    private static string ShortCommandType(string typeName)
    {
        var value = typeName;
        if (value.StartsWith("Ammo.", StringComparison.Ordinal))
        {
            value = value.Substring("Ammo.".Length);
        }

        const string suffix = "EpisodeCommand";
        if (value.EndsWith(suffix, StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - suffix.Length);
        }

        return string.IsNullOrEmpty(value) ? typeName : value;
    }

    private static string LookupString(AmmoStringTable stringTable, int stringIndex)
    {
        return stringIndex >= 0 && stringIndex < stringTable.Strings.Count
            ? stringTable.Strings[stringIndex].Text
            : $"<string #{stringIndex.ToString(CultureInfo.InvariantCulture)}>";
    }

    private static int GetStringLength(AmmoStringTable stringTable, int stringIndex)
    {
        return stringIndex >= 0 && stringIndex < stringTable.Strings.Count
            ? stringTable.Strings[stringIndex].Length
            : 0;
    }

    private static int ReadUInt16LittleEndian(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8);
    }

    private static void SkipPaddingToMarker(byte[] data, ref int offset, byte marker, int maxPadding)
    {
        int skipped = 0;
        while (offset < data.Length && data[offset] != marker && data[offset] == 0 && skipped < maxPadding)
        {
            offset++;
            skipped++;
        }
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

    private static bool LooksLikeDialogueCardText(string text, string label)
    {
        if (IsLikelySpeakerString(text, label) || IsTechnicalString(text))
        {
            return false;
        }

        if (label is "Memo" or "Message" or "Oneshot")
        {
            return true;
        }

        if (text.Contains("<color=", StringComparison.OrdinalIgnoreCase) ||
            text.Contains('\n', StringComparison.Ordinal) ||
            text.Contains("\\n", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var ch in text)
        {
            if (ch is '\u3002' or '\u3001' or '\uFF01' or '\uFF1F' or '\u2026' or '\u2661')
            {
                return true;
            }
        }

        return text.Length >= 18 && ContainsNonAsciiLetter(text);
    }

    private static bool ContainsNonAsciiLetter(string text)
    {
        foreach (var ch in text)
        {
            if (ch <= 0x7F)
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

    private static readonly HashSet<string> TechnicalStringBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Message", "Name", "Memo", "Oneshot", "StartPosition", "Duration", "Fov", "Time", "Scale", "Rotate", "Offset", "Distance", "Type", "Color", "Target", "Human", "Effect", "Common", "Smile", "Walk", "None", "Solo", "Horizon", "Center", "Forward", "Big", "Normal", "DollyIn", "Placement", "Prefabname",
        "Top", "Bottom", "Left", "Right", "BottomLeft", "BottomRight", "TopLeft", "TopRight", "UpperLeft", "UpperRight", "UpperLeft1", "UpperLeft2", "UpperRight1", "UpperRight2",
        "SmallOpenMouth", "BlinkEye", "CloseEyes", "FadeIn", "FadeOut", "AutoLocation", "EpisodeType", "Episode3D", "ExitCameraDuration", "IsDisableSkip", "IsExitKeepFade", "ShadowSetting", "Title", "LayoutType", "TwoOnTwo", "LocationName", "MapId", "UniqueId", "BgmId", "FadeInTime", "AvaterPositionType", "CharaId", "Gender", "Female", "HBodyShape", "Middle", "HModelId", "HMountType", "HPersonal", "HSequenceId", "HWeaponId", "IsInvalidBoundsExpand", "IsInvalidPhysical", "IsMount", "LayoutId", "MModelId", "MMotionId", "MSequenceId", "MonsterId", "Position", "x", "y", "z", "PreloadExtraFaceAtlas", "Rotation", "UniqueCharacterId", "AvaterBodySizeType", "HumanM", "CameraPresetAngleType", "CameraPresetDirectionType", "CameraPresetLayoutType", "CameraPresetPositionType", "CameraPresetShotType", "WaistShot", "IsClossFade", "PresetMove", "TargetId", "TargetIsMonster", "TargetUniqueId", "BodyType", "IsImmediateExecute", "IsRide", "Personality", "UnavailableMotion", "BodyShape", "ClipNumber", "CompleteMotionClipNumber", "IsInvalidCompleteTransition", "MotionType", "MountType", "MovedPosition", "PlayMotionOnComplete", "UnavailableFacialChange", "IsSystem", "CharaSubId", "CharacterProfileId", "ClipCategory", "DefaultClipNumber", "DefaultEpisodeUniqueClipId", "DefaultMotionType", "EpisodeUniqueClipId", "HumanMotionType", "IsInvalidTransition", "IsSetDefaultMotion", "SetDefaultMotion", "FaceMotion", "IsTrack", "BalloonPointCharaId", "BalloonPointType", "BalloonPosition", "BalloonPositionPresetType", "BalloonType", "IsAutoBalloonPointType", "IsAutoExecute", "IsAutoScreenFit", "IsLastReuseMessage", "IsMonster", "IsRefresh", "IsReuseMessageBox", "ReuseMessageListJson", "VoiceCharaId", "VoiceNo", "CharacterId", "IsAllCharacter", "IsRideTarget", "IsTargetCamera", "IsTargetNull", "MonsterYOffset", "TargetCharacterId", "TargetMonsterId", "TargetMountMonsterId", "UniqueTargetCharacterId"
    };

    private static bool IsTechnicalString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.StartsWith("Ammo.", StringComparison.Ordinal) ||
            text.StartsWith("UnityEngine.", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length >= 3 && text.StartsWith("Is", StringComparison.Ordinal) && char.IsUpper(text[2]))
        {
            return true;
        }

        return TechnicalStringBlacklist.Contains(text);
    }

    private static bool IsLikelySpeakerString(string text, string label)
    {
        if (IsTechnicalString(text))
        {
            return false;
        }

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
            if (char.IsWhiteSpace(ch) || ch == '\u30FC' || ch == '\u30FB')
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
        if (trimmed.Length <= 2)
        {
            return trimmed.Any(char.IsLetterOrDigit);
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
        sb.Append(" #");
        sb.Append(entry.Index.ToString("D3", CultureInfo.InvariantCulture));
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
        sb.AppendLine(entry.IsEmpty ? "<empty>" : ToInlinePreview(entry.Text, 260));
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
        public BinaryStringEntry(int index, int offset, int length, string text)
        {
            Index = index;
            Offset = offset;
            Length = length;
            Text = text;
        }

        public int Index { get; }

        public int Offset { get; }

        public int Length { get; }

        public string Text { get; }

        public bool IsEmpty => Text.Length == 0;
    }

    private sealed class AmmoStringTable
    {
        public AmmoStringTable(IReadOnlyList<BinaryStringEntry> strings, int endOffset, int declaredCount)
        {
            Strings = strings;
            EndOffset = endOffset;
            DeclaredCount = declaredCount;
        }

        public IReadOnlyList<BinaryStringEntry> Strings { get; }

        public int EndOffset { get; }

        public int DeclaredCount { get; }
    }

    private sealed class AmmoCommandStream
    {
        public AmmoCommandStream(int streamOffset, int declaredCount, IReadOnlyList<AmmoCommandNode> commands, string status)
        {
            StreamOffset = streamOffset;
            DeclaredCount = declaredCount;
            Commands = commands;
            Status = status;
        }

        public int StreamOffset { get; }

        public int DeclaredCount { get; }

        public IReadOnlyList<AmmoCommandNode> Commands { get; }

        public string Status { get; }
    }

    private sealed class AmmoCommandNode
    {
        public AmmoCommandNode(int index, int offset, string typeName, IReadOnlyList<AmmoCommandField> fields)
        {
            Index = index;
            Offset = offset;
            TypeName = typeName;
            Fields = fields;
        }

        public int Index { get; }

        public int Offset { get; }

        public string TypeName { get; }

        public IReadOnlyList<AmmoCommandField> Fields { get; }
    }

    private sealed class AmmoCommandField
    {
        public AmmoCommandField(string name, AmmoValue value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public AmmoValue Value { get; }
    }

    private enum AmmoValueKind
    {
        Null,
        Boolean,
        Integer,
        Number,
        String,
        Object,
        Unknown
    }

    private sealed class AmmoValue
    {
        private AmmoValue(AmmoValueKind kind, int offset)
        {
            Kind = kind;
            Offset = offset;
            Text = string.Empty;
        }

        public AmmoValueKind Kind { get; private init; }

        public int Offset { get; private init; }

        public bool BooleanValue { get; private init; }

        public int IntegerValue { get; private init; }

        public double NumberValue { get; private init; }

        public int StringIndex { get; private init; }

        public int StringLength { get; private init; }

        public string Text { get; private init; }

        public AmmoCommandNode? ObjectValue { get; private init; }

        public byte UnknownTag { get; private init; }

        public static AmmoValue Null(int offset) => new(AmmoValueKind.Null, offset);

        public static AmmoValue Boolean(bool value, int offset) => new(AmmoValueKind.Boolean, offset)
        {
            BooleanValue = value
        };

        public static AmmoValue Integer(int value, int offset) => new(AmmoValueKind.Integer, offset)
        {
            IntegerValue = value
        };

        public static AmmoValue Number(double value, int offset) => new(AmmoValueKind.Number, offset)
        {
            NumberValue = value
        };

        public static AmmoValue String(int stringIndex, string text, int stringLength, int offset) => new(AmmoValueKind.String, offset)
        {
            StringIndex = stringIndex,
            StringLength = stringLength,
            Text = text
        };

        public static AmmoValue Object(AmmoCommandNode command, int offset) => new(AmmoValueKind.Object, offset)
        {
            ObjectValue = command
        };

        public static AmmoValue Unknown(byte tag, int offset) => new(AmmoValueKind.Unknown, offset)
        {
            UnknownTag = tag
        };
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
