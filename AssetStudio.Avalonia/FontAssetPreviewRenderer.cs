using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace AssetStudio.Avalonia;

internal static class FontAssetPreviewRenderer
{
    private const uint CmapTableTag = 0x636D6170; // 'cmap'
    private const int MaxCmapProbedGlyphs = 96;

    private static SKTypeface? TryCreateSystemSandboxTypeface()
    {
        var fontManager = SKFontManager.Default;
        var installed = new HashSet<string>(fontManager.FontFamilies, StringComparer.OrdinalIgnoreCase);
        string[] preferredFonts =
        {
            "Noto Sans CJK JP",
            "Noto Sans",
            "Segoe UI",
            "DejaVu Sans",
            "Liberation Sans",
            "Ubuntu",
            "Arial",
            "sans-serif"
        };

        foreach (var fontName in preferredFonts)
        {
            if (installed.Contains(fontName))
            {
                var matched = fontManager.MatchFamily(fontName, SKFontStyle.Normal);
                if (matched != null)
                {
                    return matched;
                }
            }
        }

        foreach (var familyName in fontManager.FontFamilies)
        {
            var matched = fontManager.MatchFamily(familyName, SKFontStyle.Normal);
            if (matched != null)
            {
                return matched;
            }
        }

        return null;
    }

    public static (AvaloniaBitmap Bitmap, string InfoText) Render(string fontName, byte[] fontData, Func<bool> isCancelled)
    {
        var pngBytes = RenderToPngBytes(fontName, fontData, isCancelled, out var infoText);
        if (isCancelled()) throw new OperationCanceledException();

        using var ms = new MemoryStream(pngBytes, writable: false);
        return (new AvaloniaBitmap(ms), infoText);
    }

    internal static byte[] RenderToPngBytes(string fontName, byte[] fontData, Func<bool> isCancelled)
    {
        return RenderToPngBytes(fontName, fontData, isCancelled, out _);
    }

    internal static byte[] RenderToPngBytes(
        string fontName,
        byte[] fontData,
        Func<bool> isCancelled,
        out string infoText)
    {
        const int width = 1200;
        const int height = 720;
        const int margin = 42;
        const float maxWidth = width - margin * 2;

        if (isCancelled()) throw new OperationCanceledException();

        var format = DetectFontFormat(fontData);
        if (format is "WOFF" or "WOFF2")
        {
            throw new InvalidDataException($"Web font format ({format}) cannot be rendered directly by SkiaSharp without decompression.");
        }

        using var skFontData = SKData.CreateCopy(fontData);
        using var typeface = SKTypeface.FromData(skFontData) ?? SKTypeface.FromData(skFontData, index: 0);
        if (typeface == null)
        {
            throw new InvalidDataException($"The embedded {format} font data could not be decoded by SkiaSharp.");
        }

        if (isCancelled()) throw new OperationCanceledException();

        using var systemMetadataTypeface = TryCreateSystemSandboxTypeface();
        var metadataTypeface = systemMetadataTypeface ?? typeface;

        if (isCancelled()) throw new OperationCanceledException();

        var hasAsciiLower = HasGlyphCoverage(typeface, "abcdefghijklmnopqrstuvwxyz", minRatio: 0.6f);
        var hasAsciiUpper = HasGlyphCoverage(typeface, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", minRatio: 0.6f);
        var hasAsciiDigits = HasGlyphCoverage(typeface, "0123456789", minRatio: 0.7f);
        var hasLatinAscii = hasAsciiLower || hasAsciiUpper;
        var hasJapanese = HasGlyphCoverage(typeface, "\u65e5\u672c\u8a9e\u30b5\u30f3\u30d7\u30eb", minRatio: 0.5f);

        IReadOnlyList<int> cmapCodepoints = Array.Empty<int>();
        if (!hasLatinAscii)
        {
            cmapCodepoints = ProbeSupportedCodepoints(typeface, fontData, MaxCmapProbedGlyphs);
        }

        infoText = BuildInfoText(fontData, typeface, hasLatinAscii, cmapCodepoints.Count);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        using (var titleFont = CreateFont(metadataTypeface, 30f))
        using (var smallFont = CreateFont(metadataTypeface, 18f))
        using (var mediumFont = CreateFont(typeface, 32f))
        using (var largeFont = CreateFont(typeface, 56f))
        using (var hugeFont = CreateFont(typeface, 84f))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(new SKColor(30, 33, 39));

            var muted = new SKColor(178, 186, 200);
            var foreground = new SKColor(245, 247, 250);
            var accent = new SKColor(117, 207, 180);

            if (isCancelled()) throw new OperationCanceledException();
            DrawWrappedText(canvas, titleFont, paint, $"Font: {fontName}", foreground, margin, 34f, maxWidth);
            if (isCancelled()) throw new OperationCanceledException();

            var subtitle = $"Format: {format}   Glyphs: {typeface.GlyphCount:N0}   Size: {fontData.Length:N0} bytes";
            if (!hasLatinAscii)
            {
                subtitle += "   [Symbol / Non-ASCII cmap]";
            }
            DrawWrappedText(canvas, smallFont, paint, subtitle, muted, margin, 88f, maxWidth);

            if (hasLatinAscii)
            {
                if (isCancelled()) throw new OperationCanceledException();
                if (hasAsciiLower)
                {
                    DrawWrappedText(canvas, mediumFont, paint, "abcdefghijklmnopqrstuvwxyz", foreground, margin, 150f, maxWidth);
                }
                else if (hasAsciiUpper)
                {
                    DrawWrappedText(canvas, mediumFont, paint, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", foreground, margin, 150f, maxWidth);
                }

                if (isCancelled()) throw new OperationCanceledException();
                var upperAndDigits = hasAsciiUpper && hasAsciiDigits
                    ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789"
                    : hasAsciiUpper
                        ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
                        : hasAsciiDigits
                            ? "0123456789"
                            : "abcdefghijklmnopqrstuvwxyz";
                DrawWrappedText(canvas, mediumFont, paint, upperAndDigits, foreground, margin, 205f, maxWidth);

                if (isCancelled()) throw new OperationCanceledException();
                var pangram = hasAsciiLower
                    ? "Pack my box with five dozen liquor jugs."
                    : "PACK MY BOX WITH FIVE DOZEN LIQUOR JUGS.";
                DrawWrappedText(canvas, largeFont, paint, pangram, accent, margin, 290f, maxWidth);

                if (isCancelled()) throw new OperationCanceledException();
                var specimen = hasAsciiLower
                    ? (hasAsciiDigits ? "Hamburgefonstiv 123" : "Hamburgefonstiv")
                    : (hasAsciiDigits ? "HAMBURGEFONSTIV 123" : "HAMBURGEFONSTIV");
                DrawWrappedText(canvas, hugeFont, paint, specimen, foreground, margin, 405f, maxWidth);

                if (isCancelled()) throw new OperationCanceledException();
                if (hasJapanese)
                {
                    DrawWrappedText(canvas, mediumFont, paint, "\u65e5\u672c\u8a9e\u30b5\u30f3\u30d7\u30eb: \u30ea\u30bc \u30d5\u30a3\u30fc\u30ca \u30a6\u30a7\u30f3\u30c7\u30a3", foreground, margin, 600f, maxWidth);
                }
                else
                {
                    var extraCodepoints = ProbeSupportedCodepoints(typeface, fontData, maxCount: 24, skipBasicAscii: true);
                    if (extraCodepoints.Count > 0)
                    {
                        var extraSample = BuildCodepointSpecimenLine(extraCodepoints, 0, Math.Min(20, extraCodepoints.Count), " ");
                        DrawWrappedText(canvas, mediumFont, paint, extraSample, muted, margin, 585f, maxWidth);
                    }
                }
            }
            else if (cmapCodepoints.Count > 0)
            {
                // Symbol / Icon / Non-Latin font: render actual glyphs discovered from the font's cmap!
                var rangeSummary = FormatCodepointRangeSummary(cmapCodepoints);
                DrawWrappedText(canvas, smallFont, paint, $"Mapped glyph sample ({cmapCodepoints.Count} shown): {rangeSummary}", accent, margin, 130f, maxWidth);

                using var specimenLargeFont = CreateFont(typeface, 52f);
                using var specimenMediumFont = CreateFont(typeface, 38f);
                var rowY = 185f;
                const int perRow = 14;
                var idx = 0;
                var rowIndex = 0;
                while (idx < cmapCodepoints.Count && rowY < height - 56f)
                {
                    if (isCancelled()) throw new OperationCanceledException();
                    var take = Math.Min(perRow, cmapCodepoints.Count - idx);
                    var line = BuildCodepointSpecimenLine(cmapCodepoints, idx, take, "  ");
                    var rowFont = rowIndex < 2 ? specimenLargeFont : specimenMediumFont;
                    var rowColor = rowIndex == 0 ? accent : foreground;
                    DrawWrappedText(canvas, rowFont, paint, line, rowColor, margin, rowY, maxWidth);
                    rowY += rowIndex < 2 ? 78f : 62f;
                    idx += take;
                    rowIndex++;
                }
            }
            else
            {
                // Last resort: render glyph IDs directly when font has unmapped/custom-encoded glyphs.
                var glyphCount = Math.Min(typeface.GlyphCount, 64);
                if (glyphCount > 1)
                {
                    DrawWrappedText(canvas, smallFont, paint, $"Direct glyph preview (GID 1..{glyphCount - 1}, unmapped cmap)", accent, margin, 140f, maxWidth);
                    using var gidFont = CreateFont(typeface, 44f);
                    paint.Color = foreground;
                    const int cols = 12;
                    var cellW = maxWidth / cols;
                    var cellH = 72f;
                    var glyphsToDraw = new List<(ushort Gid, SKPoint Pos)>(glyphCount);
                    for (var g = 1; g < glyphCount; g++)
                    {
                        var col = (g - 1) % cols;
                        var row = (g - 1) / cols;
                        var y = 230f + row * cellH;
                        if (y > height - 30f) break;
                        glyphsToDraw.Add(((ushort)g, new SKPoint(margin + col * cellW, y)));
                    }

                    if (glyphsToDraw.Count > 0)
                    {
                        using var builder = new SKTextBlobBuilder();
                        var run = builder.AllocatePositionedRun(gidFont, glyphsToDraw.Count);
                        var glyphSpan = run.Glyphs;
                        var posSpan = run.Positions;
                        for (var i = 0; i < glyphsToDraw.Count; i++)
                        {
                            glyphSpan[i] = glyphsToDraw[i].Gid;
                            posSpan[i] = glyphsToDraw[i].Pos;
                        }
                        using var blob = builder.Build();
                        if (blob != null)
                        {
                            canvas.DrawText(blob, 0, 0, paint);
                        }
                    }
                }
                else
                {
                    DrawWrappedText(canvas, smallFont, paint, "No renderable glyphs were found in this font.", muted, margin, 160f, maxWidth);
                }
            }

            canvas.Flush();
        }

        if (isCancelled()) throw new OperationCanceledException();

        using var ms = new MemoryStream();
        bitmap.WriteToStream(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    internal static bool HasGlyphCoverage(SKTypeface typeface, string sample, float minRatio)
    {
        if (typeface == null || string.IsNullOrEmpty(sample))
        {
            return false;
        }

        var total = 0;
        var found = 0;
        foreach (var rune in sample.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            total++;
            if (typeface.GetGlyph(rune.Value) != 0)
            {
                found++;
            }
        }

        return total > 0 && (float)found / total >= minRatio;
    }

    internal static List<int> ProbeSupportedCodepoints(
        SKTypeface typeface,
        byte[]? fontData,
        int maxCount = MaxCmapProbedGlyphs,
        bool skipBasicAscii = false)
    {
        var result = new List<int>(Math.Max(16, maxCount));
        var seen = new HashSet<int>();

        void TryAddCodepoint(int cp)
        {
            if (result.Count >= maxCount)
            {
                return;
            }

            if (cp < 0x21 || (cp >= 0x7F && cp <= 0x9F) || (cp >= 0xD800 && cp <= 0xDFFF))
            {
                return;
            }

            if (skipBasicAscii && cp >= 0x20 && cp <= 0x7E)
            {
                return;
            }

            if (seen.Contains(cp))
            {
                return;
            }

            if (typeface.GetGlyph(cp) != 0)
            {
                seen.Add(cp);
                result.Add(cp);
            }
        }

        // 1) Parse OpenType 'cmap' table directly (fast & exact for symbol/icon/PUA/CJK/custom fonts).
        try
        {
            var cmapBytes = typeface.GetTableData(CmapTableTag);
            if (cmapBytes != null && cmapBytes.Length >= 12)
            {
                foreach (var cp in ExtractCodepointsFromCmapTable(cmapBytes, maxCount * 2))
                {
                    TryAddCodepoint(cp);
                    if (result.Count >= maxCount)
                    {
                        return result;
                    }
                }
            }
        }
        catch
        {
            // Fall through to targeted Unicode range probe.
        }

        // 2) Targeted Unicode block fallback probe if cmap table wasn't exposed.
        (int Start, int End)[] probeRanges =
        {
            (0x0021, 0x007E), // Basic Latin
            (0x00A1, 0x00FF), // Latin-1 Supplement
            (0x0391, 0x03C9), // Greek
            (0x0410, 0x044F), // Cyrillic
            (0x2190, 0x27BF), // Arrows, Math, Dingbats, Misc Symbols
            (0xE000, 0xF8FF), // Private Use Area (FontAwesome / Material / HUD icon fonts)
            (0x3041, 0x30FF), // Hiragana & Katakana
            (0x4E00, 0x4E60), // Common CJK sample
            (0xF000, 0xF0FF)  // Symbol encoding range
        };

        foreach (var (start, end) in probeRanges)
        {
            for (var cp = start; cp <= end && result.Count < maxCount; cp++)
            {
                TryAddCodepoint(cp);
            }

            if (result.Count >= maxCount)
            {
                break;
            }
        }

        return result;
    }

    internal static List<int> ExtractCodepointsFromCmapTable(byte[] cmap, int maxCount)
    {
        var result = new List<int>();
        if (cmap == null || cmap.Length < 4 || maxCount <= 0)
        {
            return result;
        }

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(2, 2));
        if (numTables == 0 || cmap.Length < 4 + numTables * 8)
        {
            return result;
        }

        // Prefer Unicode full (format 12), Unicode BMP (format 4), then Windows Symbol (platform 3, encoding 0).
        var subtableOffsets = new List<(int Priority, int Offset)>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var recOffset = 4 + i * 8;
            var platformId = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(recOffset, 2));
            var encodingId = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(recOffset + 2, 2));
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(recOffset + 4, 4));
            if (offset < 0 || offset + 4 > cmap.Length)
            {
                continue;
            }

            var priority = (platformId, encodingId) switch
            {
                (3, 10) or (0, 4) => 0, // Full Unicode
                (3, 1) or (0, 3) => 1,  // Unicode BMP
                (3, 0) => 2,            // Windows Symbol
                _ => 3
            };
            subtableOffsets.Add((priority, offset));
        }

        subtableOffsets.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        var seen = new HashSet<int>();

        foreach (var (_, offset) in subtableOffsets)
        {
            var format = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(offset, 2));
            if (format == 4)
            {
                ReadCmapFormat4(cmap, offset, maxCount, seen, result);
            }
            else if (format == 12)
            {
                ReadCmapFormat12(cmap, offset, maxCount, seen, result);
            }

            if (result.Count >= maxCount)
            {
                break;
            }
        }

        return result;
    }

    private static void ReadCmapFormat4(byte[] cmap, int offset, int maxCount, HashSet<int> seen, List<int> result)
    {
        if (offset + 14 > cmap.Length)
        {
            return;
        }

        var segCountX2 = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(offset + 6, 2));
        var segCount = segCountX2 / 2;
        if (segCount <= 0)
        {
            return;
        }

        var endCodesOffset = offset + 14;
        var startCodesOffset = endCodesOffset + segCountX2 + 2;
        if (startCodesOffset + segCountX2 > cmap.Length)
        {
            return;
        }

        for (var seg = 0; seg < segCount && result.Count < maxCount; seg++)
        {
            var endCode = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(endCodesOffset + seg * 2, 2));
            var startCode = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan(startCodesOffset + seg * 2, 2));
            if (startCode == 0xFFFF && endCode == 0xFFFF)
            {
                break;
            }

            if (startCode > endCode)
            {
                continue;
            }

            var step = endCode - startCode > 256 ? Math.Max(1, (endCode - startCode) / 32) : 1;
            for (int cp = startCode; cp <= endCode && result.Count < maxCount; cp += step)
            {
                if (cp >= 0x21 && seen.Add(cp))
                {
                    result.Add(cp);
                }
            }
        }
    }

    private static void ReadCmapFormat12(byte[] cmap, int offset, int maxCount, HashSet<int> seen, List<int> result)
    {
        if (offset + 16 > cmap.Length)
        {
            return;
        }

        var numGroups = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(offset + 12, 4));
        var maxGroups = (uint)Math.Max(0, (cmap.Length - (offset + 16)) / 12);
        var safeGroups = Math.Min(numGroups, maxGroups);

        for (uint g = 0; g < safeGroups && result.Count < maxCount; g++)
        {
            var groupOffset = offset + 16 + (int)(g * 12);
            var startChar = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(groupOffset, 4));
            var endChar = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(groupOffset + 4, 4));
            if (startChar > endChar || startChar > 0x10FFFF)
            {
                continue;
            }

            var clampedEnd = Math.Min(endChar, 0x10FFFFu);
            var span = clampedEnd - startChar;
            var step = span > 256 ? Math.Max(1u, span / 32u) : 1u;
            for (var cp = startChar; cp <= clampedEnd && result.Count < maxCount; cp += step)
            {
                if (cp >= 0x21 && seen.Add((int)cp))
                {
                    result.Add((int)cp);
                }
            }
        }
    }

    private static string BuildCodepointSpecimenLine(IReadOnlyList<int> codepoints, int start, int count, string separator)
    {
        var sb = new StringBuilder(count * 3);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(separator);
            }

            var cp = codepoints[start + i];
            if (Rune.TryCreate(cp, out var rune))
            {
                sb.Append(rune.ToString());
            }
        }

        return sb.ToString();
    }

    private static string FormatCodepointRangeSummary(IReadOnlyList<int> codepoints)
    {
        if (codepoints.Count == 0)
        {
            return "None";
        }

        var min = codepoints[0];
        var max = codepoints[0];
        for (var i = 1; i < codepoints.Count; i++)
        {
            if (codepoints[i] < min) min = codepoints[i];
            if (codepoints[i] > max) max = codepoints[i];
        }

        return string.Create(CultureInfo.InvariantCulture, $"U+{min:X4}..U+{max:X4}");
    }

    /// <summary>
    /// Enhances bitmap Font atlases and TextMeshPro SDF atlases so white-on-transparent,
    /// Alpha8 (black RGB + alpha), single-channel R8, or transparent colored glyphs render
    /// cleanly and legibly against a dark charcoal background in both Light and Dark themes.
    /// </summary>
    internal static bool EnhanceFontAtlasVisibility(SKBitmap image)
    {
        if (image == null || image.Width <= 0 || image.Height <= 0 || image.ColorType != SKColorType.Bgra8888)
        {
            return false;
        }

        int width = image.Width;
        int height = image.Height;
        int rowBytes = image.RowBytes;
        int stepX = Math.Max(1, width / 128);
        int stepY = Math.Max(1, height / 128);

        byte minR = byte.MaxValue, maxR = byte.MinValue;
        byte minG = byte.MaxValue, maxG = byte.MinValue;
        byte minB = byte.MaxValue, maxB = byte.MinValue;
        byte minA = byte.MaxValue, maxA = byte.MinValue;
        int samples = 0;

        var pixels = image.GetWritablePixelSpan();
        for (int y = 0; y < height; y += stepY)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (int x = 0; x < width; x += stepX)
            {
                int offset = x * 4;
                byte b = row[offset];
                byte g = row[offset + 1];
                byte r = row[offset + 2];
                byte a = row[offset + 3];

                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (g < minG) minG = g;
                if (g > maxG) maxG = g;
                if (b < minB) minB = b;
                if (b > maxB) maxB = b;
                if (a < minA) minA = a;
                if (a > maxA) maxA = a;
                samples++;
            }
        }

        if (samples == 0)
        {
            GC.KeepAlive(image);
            return false;
        }

        const int bgR = 30;
        const int bgG = 33;
        const int bgB = 39;

        bool hasAlphaVariance = (maxA - minA) > 16;
        bool isUniformRgb = (maxR - minR) <= 16 && (maxG - minG) <= 16 && (maxB - minB) <= 16;
        bool isSingleChannelRed = !hasAlphaVariance && minA >= 245 && (maxR - minR) > 16 && maxG <= 8 && maxB <= 8;

        if (hasAlphaVariance && isUniformRgb)
        {
            // Alpha8 (black RGB + alpha) OR Unity white-on-transparent font/SDF atlas (255,255,255,A).
            for (int y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, width * 4);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    int a = row[offset + 3];
                    row[offset] = (byte)(bgB + ((255 - bgB) * a) / 255);
                    row[offset + 1] = (byte)(bgG + ((255 - bgG) * a) / 255);
                    row[offset + 2] = (byte)(bgR + ((255 - bgR) * a) / 255);
                    row[offset + 3] = byte.MaxValue;
                }
            }

            GC.KeepAlive(image);
            return true;
        }

        if (isSingleChannelRed)
        {
            // R8 single-channel SDF / bitmap atlas.
            for (int y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, width * 4);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    int intensity = row[offset + 2];
                    row[offset] = (byte)(bgB + ((255 - bgB) * intensity) / 255);
                    row[offset + 1] = (byte)(bgG + ((255 - bgG) * intensity) / 255);
                    row[offset + 2] = (byte)(bgR + ((255 - bgR) * intensity) / 255);
                    row[offset + 3] = byte.MaxValue;
                }
            }

            GC.KeepAlive(image);
            return true;
        }

        if (hasAlphaVariance && minA < 250)
        {
            // Colored bitmap font with transparency: composite over dark slate for consistent contrast.
            for (int y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, width * 4);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    int b = row[offset];
                    int g = row[offset + 1];
                    int r = row[offset + 2];
                    int a = row[offset + 3];
                    int invA = 255 - a;
                    row[offset] = (byte)((b * a + bgB * invA) / 255);
                    row[offset + 1] = (byte)((g * a + bgG * invA) / 255);
                    row[offset + 2] = (byte)((r * a + bgR * invA) / 255);
                    row[offset + 3] = byte.MaxValue;
                }
            }

            GC.KeepAlive(image);
            return true;
        }

        GC.KeepAlive(image);
        return false;
    }

    public static string BuildLegacyFontInfoText(AssetStudio.Font font, Texture2D? atlasTexture)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Type: Legacy Bitmap / Custom Font");
        if (!string.IsNullOrWhiteSpace(font.m_Name))
        {
            sb.AppendLine($"Font: {font.m_Name}");
        }

        if (font.m_FontSize > 0f)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Font Size: {font.m_FontSize:0.##}"));
        }

        if (font.m_LineSpacing > 0f)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Line Spacing: {font.m_LineSpacing:0.##}"));
        }

        sb.AppendLine($"Characters: {font.m_CharacterRectCount:N0}");

        if (font.m_FontNames is { Length: > 0 })
        {
            sb.AppendLine($"System Fonts: {string.Join(", ", font.m_FontNames)}");
        }

        if (atlasTexture != null)
        {
            if (!string.IsNullOrWhiteSpace(atlasTexture.m_Name))
            {
                sb.AppendLine($"Atlas Texture: {atlasTexture.m_Name}");
            }

            sb.AppendLine($"Atlas Dimensions: {atlasTexture.m_Width}x{atlasTexture.m_Height}");
            sb.AppendLine($"Atlas Format: {atlasTexture.m_TextureFormat}");
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildLegacyFontFallbackText(AssetStudio.Font font, string? atlasError = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Font: {font.m_Name}");
        sb.AppendLine("Format: System or Custom Reference (No embedded TTF/OTF data)");
        sb.AppendLine("Data size: 0 bytes");
        if (font.m_FontSize > 0f)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Font Size: {font.m_FontSize:0.##}"));
        }

        if (font.m_LineSpacing > 0f)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Line Spacing: {font.m_LineSpacing:0.##}"));
        }

        sb.AppendLine($"Character Rects: {font.m_CharacterRectCount:N0}");
        if (font.m_FontNames is { Length: > 0 })
        {
            sb.AppendLine($"Referenced System Fonts: {string.Join(", ", font.m_FontNames)}");
        }

        if (font.m_Texture != null && !font.m_Texture.IsNull)
        {
            sb.AppendLine($"Atlas Texture Reference: FileID={font.m_Texture.m_FileID}, PathID={font.m_Texture.m_PathID}");
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(atlasError))
        {
            sb.AppendLine($"Atlas preview note: {atlasError}");
            sb.AppendLine();
        }

        sb.AppendLine("This font asset does not contain embedded TrueType/OpenType binary data.");
        sb.AppendLine("It references system-installed fonts or external character textures.");
        sb.AppendLine();
        sb.AppendLine("Raw metadata dump is available in the Dump tab.");
        return sb.ToString();
    }

    public static string BuildFallbackText(AssetStudio.Font font, string errorMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Font: {font.m_Name}");
        sb.AppendLine($"Format: {DetectFontFormat(font.m_FontData)}");
        sb.AppendLine($"Data size: {font.m_FontData?.Length ?? 0:N0} bytes");
        sb.AppendLine();
        sb.AppendLine("The font data could not be rendered safely in the preview panel.");
        sb.AppendLine($"Reason: {errorMessage}");
        sb.AppendLine();
        sb.AppendLine("Raw export is still available.");
        return sb.ToString();
    }

    private static SKFont CreateFont(SKTypeface typeface, float size)
    {
        return new SKFont(typeface, size)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true
        };
    }

    private static string BuildInfoText(byte[] fontData, SKTypeface typeface, bool hasLatinAscii, int probedCmapCount)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(typeface.FamilyName))
        {
            sb.AppendLine($"Family: {typeface.FamilyName}");
        }

        sb.AppendLine($"Format: {DetectFontFormat(fontData)}");
        sb.AppendLine($"Glyphs: {typeface.GlyphCount:N0}");
        if (!hasLatinAscii)
        {
            sb.AppendLine(probedCmapCount > 0
                ? $"Charset: Symbol / Non-ASCII ({probedCmapCount} sampled)"
                : "Charset: Custom / Unmapped Glyph IDs");
        }
        sb.Append($"Data size: {fontData.Length:N0} bytes");
        return sb.ToString();
    }

    private static void DrawWrappedText(
        SKCanvas canvas,
        SKFont font,
        SKPaint paint,
        string text,
        SKColor color,
        float x,
        float topY,
        float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        paint.Color = color;
        var metrics = font.Metrics;
        float ascent = -metrics.Ascent;
        if (ascent <= 0f)
        {
            ascent = font.Size * 0.8f;
        }

        float lineHeight = font.Spacing;
        if (lineHeight <= 0f)
        {
            lineHeight = ascent + metrics.Descent + metrics.Leading;
        }
        if (lineHeight <= 0f)
        {
            lineHeight = font.Size * 1.2f;
        }

        float baselineY = topY + ascent;
        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                baselineY += lineHeight;
                continue;
            }

            foreach (var line in WrapParagraph(font, paint, paragraph, maxWidth))
            {
                canvas.DrawText(line, x, baselineY, SKTextAlign.Left, font, paint);
                baselineY += lineHeight;
            }
        }
    }

    private static List<string> WrapParagraph(SKFont font, SKPaint paint, string paragraph, float maxWidth)
    {
        var lines = new List<string>();
        if (maxWidth <= 0f || font.MeasureText(paragraph, paint) <= maxWidth)
        {
            lines.Add(paragraph);
            return lines;
        }

        var current = new StringBuilder();
        var words = paragraph.Split(' ');
        for (int w = 0; w < words.Length; w++)
        {
            var word = words[w];
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate, paint) <= maxWidth)
            {
                if (current.Length > 0)
                {
                    current.Append(' ');
                }
                current.Append(word);
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (font.MeasureText(word, paint) <= maxWidth)
            {
                current.Append(word);
                continue;
            }

            // Fallback character-level wrapping for long tokens or CJK runs without spaces.
            foreach (var ch in word)
            {
                var charCandidate = current.ToString() + ch;
                if (current.Length > 0 && font.MeasureText(charCandidate, paint) > maxWidth)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    internal static string DetectFontFormat(byte[]? data)
    {
        if (data == null || data.Length < 4)
        {
            return "Unknown";
        }

        if (data[0] == 0x00 && data[1] == 0x01 && data[2] == 0x00 && data[3] == 0x00)
        {
            return "TrueType";
        }

        var tag = Encoding.ASCII.GetString(data, 0, 4);
        return tag switch
        {
            "OTTO" => "OpenType/CFF",
            "ttcf" => "TrueType Collection",
            "true" => "TrueType (Apple)",
            "wOFF" => "WOFF",
            "wOF2" => "WOFF2",
            _ => $"Unknown ({data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2})"
        };
    }
}
