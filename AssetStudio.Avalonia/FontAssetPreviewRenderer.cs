using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace AssetStudio.Avalonia;

internal static class FontAssetPreviewRenderer
{
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
        var pngBytes = RenderToPngBytes(fontName, fontData, isCancelled);
        if (isCancelled()) throw new OperationCanceledException();

        using var ms = new MemoryStream(pngBytes, writable: false);
        return (new AvaloniaBitmap(ms), BuildInfoText(fontData));
    }

    internal static byte[] RenderToPngBytes(string fontName, byte[] fontData, Func<bool> isCancelled)
    {
        const int width = 1200;
        const int height = 720;
        const int margin = 42;
        const float maxWidth = width - margin * 2;

        if (isCancelled()) throw new OperationCanceledException();

        using var skFontData = SKData.CreateCopy(fontData);
        using var typeface = SKTypeface.FromData(skFontData);
        if (typeface == null)
        {
            throw new InvalidDataException("The embedded font data could not be decoded by SkiaSharp.");
        }

        if (isCancelled()) throw new OperationCanceledException();

        using var systemMetadataTypeface = TryCreateSystemSandboxTypeface();
        var metadataTypeface = systemMetadataTypeface ?? typeface;

        if (isCancelled()) throw new OperationCanceledException();

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
            DrawWrappedText(canvas, smallFont, paint, $"Format: {DetectFontFormat(fontData)}   Size: {fontData.Length:N0} bytes", muted, margin, 88f, maxWidth);
            if (isCancelled()) throw new OperationCanceledException();
            DrawWrappedText(canvas, mediumFont, paint, "abcdefghijklmnopqrstuvwxyz", foreground, margin, 150f, maxWidth);
            if (isCancelled()) throw new OperationCanceledException();
            DrawWrappedText(canvas, mediumFont, paint, "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789", foreground, margin, 205f, maxWidth);
            if (isCancelled()) throw new OperationCanceledException();
            DrawWrappedText(canvas, largeFont, paint, "Pack my box with five dozen liquor jugs.", accent, margin, 290f, maxWidth);
            if (isCancelled()) throw new OperationCanceledException();
            DrawWrappedText(canvas, hugeFont, paint, "Hamburgefonstiv 123", foreground, margin, 405f, maxWidth);
            if (isCancelled()) throw new OperationCanceledException();
            DrawWrappedText(canvas, mediumFont, paint, "\u65e5\u672c\u8a9e\u30b5\u30f3\u30d7\u30eb: \u30ea\u30bc \u30d5\u30a3\u30fc\u30ca \u30a6\u30a7\u30f3\u30c7\u30a3", foreground, margin, 600f, maxWidth);

            canvas.Flush();
        }

        if (isCancelled()) throw new OperationCanceledException();

        using var ms = new MemoryStream();
        bitmap.WriteToStream(ms, ImageFormat.Png);
        return ms.ToArray();
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

    private static string BuildInfoText(byte[] fontData)
    {
        return $"Format: {DetectFontFormat(fontData)}\nData size: {fontData.Length:N0} bytes";
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

    private static string DetectFontFormat(byte[]? data)
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
            "wOFF" => "WOFF",
            "wOF2" => "WOFF2",
            _ => $"Unknown ({data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2})"
        };
    }
}
