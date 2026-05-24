using System.IO;
using System.Text;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using ImageSharpColor = SixLabors.ImageSharp.Color;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AssetStudio.Avalonia;

internal static class FontAssetPreviewRenderer
{
    public static (AvaloniaBitmap Bitmap, string InfoText) Render(string fontName, byte[] fontData)
    {
        const int width = 1200;
        const int height = 720;
        const int margin = 42;

        var collection = new FontCollection();
        using var fontStream = new MemoryStream(fontData, writable: false);
        var family = collection.Add(fontStream);

        using var image = new Image<Rgba32>(width, height, new Rgba32(30, 33, 39));
        image.Mutate(ctx =>
        {
            var titleFont = family.CreateFont(30, SixLabors.Fonts.FontStyle.Regular);
            var smallFont = family.CreateFont(18, SixLabors.Fonts.FontStyle.Regular);
            var mediumFont = family.CreateFont(32, SixLabors.Fonts.FontStyle.Regular);
            var largeFont = family.CreateFont(56, SixLabors.Fonts.FontStyle.Regular);
            var hugeFont = family.CreateFont(84, SixLabors.Fonts.FontStyle.Regular);

            var muted = ImageSharpColor.FromRgb(178, 186, 200);
            var foreground = ImageSharpColor.FromRgb(245, 247, 250);
            var accent = ImageSharpColor.FromRgb(117, 207, 180);

            DrawText(ctx, titleFont, $"Font: {fontName}", foreground, margin, 34, width - margin * 2);
            DrawText(ctx, smallFont, $"Format: {DetectFontFormat(fontData)}   Size: {fontData.Length:N0} bytes", muted, margin, 88, width - margin * 2);
            DrawText(ctx, mediumFont, "abcdefghijklmnopqrstuvwxyz", foreground, margin, 150, width - margin * 2);
            DrawText(ctx, mediumFont, "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789", foreground, margin, 205, width - margin * 2);
            DrawText(ctx, largeFont, "Pack my box with five dozen liquor jugs.", accent, margin, 290, width - margin * 2);
            DrawText(ctx, hugeFont, "Hamburgefonstiv 123", foreground, margin, 405, width - margin * 2);
            DrawText(ctx, mediumFont, "\u65e5\u672c\u8a9e\u30b5\u30f3\u30d7\u30eb: \u30ea\u30bc \u30d5\u30a3\u30fc\u30ca \u30a6\u30a7\u30f3\u30c7\u30a3", foreground, margin, 600, width - margin * 2);
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;
        return (new AvaloniaBitmap(ms), BuildInfoText(fontData));
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

    private static string BuildInfoText(byte[] fontData)
    {
        return $"Format: {DetectFontFormat(fontData)}\nData size: {fontData.Length:N0} bytes";
    }

    private static void DrawText(
        IImageProcessingContext ctx,
        SixLabors.Fonts.Font font,
        string text,
        ImageSharpColor color,
        float x,
        float y,
        float wrappingLength)
    {
        var options = new RichTextOptions(font)
        {
            Origin = new PointF(x, y),
            WrappingLength = wrappingLength,
            Dpi = 96
        };
        ctx.DrawText(options, text, color);
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
