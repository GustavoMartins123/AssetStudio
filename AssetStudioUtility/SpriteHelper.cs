using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public sealed class SpriteExtractionOptions
    {
        public bool PreserveOriginalRect { get; set; }
        public bool ApplyAlphaMask { get; set; } = true;
        public bool DrawBoundsAndNineSliceOverlay { get; set; }
    }

    public static class SpriteHelper
    {
        private const int MaxCachedAtlases = 4;
        private const long MaxCachedBytes = 256L * 1024L * 1024L; // 256 MiB
        private static readonly SKSamplingOptions ResizeSampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<AtlasCacheKey, AtlasCacheEntry> _cacheByKey = new Dictionary<AtlasCacheKey, AtlasCacheEntry>();
        private static readonly LinkedList<AtlasCacheEntry> _lruList = new LinkedList<AtlasCacheEntry>();
        private static readonly Dictionary<AtlasCacheKey, InFlightDecode> _inFlight = new Dictionary<AtlasCacheKey, InFlightDecode>();
        private static long _currentCachedBytes;
        private static long _cacheGeneration;

        public static void ClearAtlasCache()
        {
            List<AtlasCacheEntry>? toDispose = null;
            lock (_cacheLock)
            {
                _cacheGeneration++;
                while (_lruList.First != null)
                {
                    RemoveEntryLocked(_lruList.First.Value, ref toDispose);
                }
            }
            DisposeEntries(toDispose);
        }

        public static SKBitmap? GetImage(this Sprite m_Sprite)
        {
            return GetImage(m_Sprite, options: null);
        }

        public static SKBitmap? GetImage(this Sprite m_Sprite, SpriteExtractionOptions? options)
        {
            if (m_Sprite == null)
            {
                return null;
            }

            if (m_Sprite.m_SpriteAtlas != null && m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlas))
            {
                if (m_SpriteAtlas.m_RenderDataMap != null
                    && m_SpriteAtlas.m_RenderDataMap.TryGetValue(m_Sprite.m_RenderDataKey, out var spriteAtlasData)
                    && spriteAtlasData != null
                    && spriteAtlasData.texture != null
                    && spriteAtlasData.texture.TryGet(out var m_Texture2D))
                {
                    Texture2D? alphaTexture = ResolveAlphaTexture(
                        spriteAtlasData.alphaTexture,
                        spriteAtlasData.secondaryTextures,
                        m_Sprite.m_RD?.secondaryTextures);
                    return CutImageCore(
                        m_Sprite,
                        m_Texture2D,
                        alphaTexture,
                        spriteAtlasData.textureRect,
                        spriteAtlasData.textureRectOffset,
                        spriteAtlasData.downscaleMultiplier,
                        spriteAtlasData.settingsRaw,
                        options);
                }
            }

            if (m_Sprite.m_RD != null && m_Sprite.m_RD.texture != null && m_Sprite.m_RD.texture.TryGet(out var directTexture))
            {
                Texture2D? alphaTexture = ResolveAlphaTexture(
                    m_Sprite.m_RD.alphaTexture,
                    m_Sprite.m_RD.secondaryTextures,
                    fallbackSecondaryTextures: null);
                return CutImageCore(
                    m_Sprite,
                    directTexture,
                    alphaTexture,
                    m_Sprite.m_RD.textureRect,
                    m_Sprite.m_RD.textureRectOffset,
                    m_Sprite.m_RD.downscaleMultiplier,
                    m_Sprite.m_RD.settingsRaw,
                    options);
            }

            return null;
        }

        internal static bool IsSecondaryAlphaTextureName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var trimmed = name.Trim();
            return trimmed.Equals("_AlphaTex", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("AlphaTex", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("_MaskTex", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("Alpha", StringComparison.OrdinalIgnoreCase);
        }

        internal static Texture2D? ResolveAlphaTexture(
            PPtr<Texture2D>? directAlphaPtr,
            SecondarySpriteTexture[]? primarySecondaryTextures,
            SecondarySpriteTexture[]? fallbackSecondaryTextures)
        {
            if (directAlphaPtr != null && directAlphaPtr.TryGet(out var directAlpha) && directAlpha != null)
            {
                return directAlpha;
            }

            return ResolveSecondaryAlphaTexture(primarySecondaryTextures)
                ?? ResolveSecondaryAlphaTexture(fallbackSecondaryTextures);
        }

        private static Texture2D? ResolveSecondaryAlphaTexture(SecondarySpriteTexture[]? secondaryTextures)
        {
            if (secondaryTextures == null || secondaryTextures.Length == 0)
            {
                return null;
            }

            Texture2D? partialMatch = null;
            for (int i = 0; i < secondaryTextures.Length; i++)
            {
                var entry = secondaryTextures[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.name) || entry.texture == null)
                {
                    continue;
                }

                var name = entry.name.Trim();
                bool isExact = name.Equals("_AlphaTex", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("AlphaTex", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("_MaskTex", StringComparison.OrdinalIgnoreCase);

                if (isExact)
                {
                    if (entry.texture.TryGet(out var exactTex) && exactTex != null)
                    {
                        return exactTex;
                    }
                    continue;
                }

                if (partialMatch == null
                    && name.Contains("Alpha", StringComparison.OrdinalIgnoreCase)
                    && entry.texture.TryGet(out var alphaTex)
                    && alphaTex != null)
                {
                    partialMatch = alphaTex;
                }
            }

            return partialMatch;
        }

        private static SKBitmap? CutImage(Sprite m_Sprite, Texture2D m_Texture2D, Rectf textureRect, Vector2 textureRectOffset, float downscaleMultiplier, SpriteSettings settingsRaw)
        {
            return CutImageCore(m_Sprite, m_Texture2D, null, textureRect, textureRectOffset, downscaleMultiplier, settingsRaw, options: null);
        }

        private static SKBitmap? CutImageCore(
            Sprite m_Sprite,
            Texture2D m_Texture2D,
            Texture2D? alphaTexture,
            Rectf? textureRect,
            Vector2 textureRectOffset,
            float downscaleMultiplier,
            SpriteSettings? settingsRaw,
            SpriteExtractionOptions? options)
        {
            if (m_Texture2D == null || textureRect == null)
            {
                return null;
            }

            bool applyAlphaMask = options?.ApplyAlphaMask ?? true;
            bool preserveOriginalRect = options?.PreserveOriginalRect ?? false;
            bool drawOverlay = options?.DrawBoundsAndNineSliceOverlay ?? false;

            int targetWidth = 0;
            int targetHeight = 0;
            if (float.IsFinite(downscaleMultiplier) && downscaleMultiplier > 0f && downscaleMultiplier != 1f)
            {
                double scaledW = m_Texture2D.m_Width / (double)downscaleMultiplier;
                double scaledH = m_Texture2D.m_Height / (double)downscaleMultiplier;
                if (!double.IsFinite(scaledW) || !double.IsFinite(scaledH)
                    || scaledW < 1d || scaledH < 1d
                    || scaledW > int.MaxValue || scaledH > int.MaxValue)
                {
                    return null;
                }

                targetWidth = (int)scaledW;
                targetHeight = (int)scaledH;
                if (targetWidth <= 0 || targetHeight <= 0 || (long)targetWidth * targetHeight * 4L > int.MaxValue)
                {
                    return null;
                }
            }

            SKBitmap? spriteImage;
            SKRectI rect;
            int atlasWidth;
            int atlasHeight;
            using (var atlasLease = AcquireAtlasLease(m_Texture2D, targetWidth, targetHeight))
            {
                if (atlasLease.Entry == null)
                {
                    return null;
                }

                atlasWidth = atlasLease.Entry.Image.Width;
                atlasHeight = atlasLease.Entry.Image.Height;
                spriteImage = CropSpriteRegion(atlasLease.Entry, textureRect, out rect);
                if (spriteImage == null)
                {
                    return null;
                }
            }

            try
            {
                if (applyAlphaMask && alphaTexture != null && !ReferenceEquals(alphaTexture, m_Texture2D))
                {
                    ApplyAlphaTexture(spriteImage, alphaTexture, atlasWidth, atlasHeight, rect);
                }

                if (settingsRaw != null && settingsRaw.packed == 1)
                {
                    //RotateAndFlip
                    switch (settingsRaw.packingRotation)
                    {
                        case SpritePackingRotation.FlipHorizontal:
                            FlipHorizontalInPlace(spriteImage);
                            break;
                        case SpritePackingRotation.FlipVertical:
                            spriteImage.FlipVerticalInPlace();
                            break;
                        case SpritePackingRotation.Rotate180:
                            Rotate180InPlace(spriteImage);
                            break;
                        case SpritePackingRotation.Rotate90:
                        {
                            var rotated = Rotate270Clockwise(spriteImage);
                            spriteImage.Dispose();
                            spriteImage = rotated;
                            break;
                        }
                    }
                }

                //Tight
                if (settingsRaw != null && settingsRaw.packingMode == SpritePackingMode.Tight && m_Sprite.m_RD != null)
                {
                    try
                    {
                        ApplyTightPolygonMask(spriteImage, m_Sprite, textureRectOffset);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to apply Tight packing mode for Sprite {m_Sprite.m_Name}, falling back to Rectangle packing mode: {ex.Message}");
                    }
                }

                // Convert from Unity bottom-up coordinates to top-down Skia coordinates.
                spriteImage.FlipVerticalInPlace();

                if (preserveOriginalRect)
                {
                    var padded = PadToOriginalRect(spriteImage, m_Sprite, textureRectOffset);
                    if (!ReferenceEquals(padded, spriteImage))
                    {
                        spriteImage.Dispose();
                        spriteImage = padded;
                    }
                }

                if (drawOverlay)
                {
                    DrawBoundsAndNineSliceOverlay(spriteImage, m_Sprite, preserveOriginalRect);
                }

                return spriteImage;
            }
            catch
            {
                spriteImage.Dispose();
                throw;
            }
        }

        private static void ApplyTightPolygonMask(SKBitmap spriteImage, Sprite m_Sprite, Vector2 textureRectOffset)
        {
            if (spriteImage.Width <= 0 || spriteImage.Height <= 0 || m_Sprite.m_Rect == null)
            {
                return;
            }

            var triangles = GetTriangles(m_Sprite.m_RD, m_Sprite.version);
            if (triangles.Length == 0)
            {
                return;
            }

            float scale = m_Sprite.m_PixelsToUnits;
            if (!float.IsFinite(scale) || scale <= 0f)
            {
                return;
            }

            float rectW = float.IsFinite(m_Sprite.m_Rect.width) ? m_Sprite.m_Rect.width : spriteImage.Width;
            float rectH = float.IsFinite(m_Sprite.m_Rect.height) ? m_Sprite.m_Rect.height : spriteImage.Height;
            float pivotX = float.IsFinite(m_Sprite.m_Pivot.X) ? m_Sprite.m_Pivot.X : 0.5f;
            float pivotY = float.IsFinite(m_Sprite.m_Pivot.Y) ? m_Sprite.m_Pivot.Y : 0.5f;
            float texOffX = float.IsFinite(textureRectOffset.X) ? textureRectOffset.X : 0f;
            float texOffY = float.IsFinite(textureRectOffset.Y) ? textureRectOffset.Y : 0f;

            float offsetX = rectW * pivotX - texOffX;
            float offsetY = rectH * pivotY - texOffY;
            if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY))
            {
                return;
            }

            int maskWidth = spriteImage.Width;
            int maskHeight = spriteImage.Height;
            var maskInfo = new SKImageInfo(maskWidth, maskHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var mask = new SKBitmap(maskInfo);
            int validTriangles = 0;

            using (var canvas = new SKCanvas(mask))
            using (var paint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            })
            using (var path = new SKPath())
            {
                canvas.Clear(SKColors.Transparent);
                foreach (var triangle in triangles)
                {
                    if (triangle == null || triangle.Length < 3)
                    {
                        continue;
                    }

                    bool allFinite = true;
                    for (int i = 0; i < triangle.Length; i++)
                    {
                        float vx = triangle[i].X * scale + offsetX;
                        float vy = triangle[i].Y * scale + offsetY;
                        if (!float.IsFinite(vx) || !float.IsFinite(vy))
                        {
                            allFinite = false;
                            break;
                        }
                    }

                    if (!allFinite)
                    {
                        continue;
                    }

                    path.Rewind();
                    path.MoveTo(triangle[0].X * scale + offsetX, triangle[0].Y * scale + offsetY);
                    for (int i = 1; i < triangle.Length; i++)
                    {
                        path.LineTo(triangle[i].X * scale + offsetX, triangle[i].Y * scale + offsetY);
                    }
                    path.Close();
                    canvas.DrawPath(path, paint);
                    validTriangles++;
                }
                canvas.Flush();
            }

            if (validTriangles == 0)
            {
                return;
            }

            var maskPixels = mask.GetPixelSpan();
            var dstPixels = spriteImage.GetWritablePixelSpan();
            int maskRowBytes = mask.RowBytes;
            int dstRowBytes = spriteImage.RowBytes;
            int rowCopyBytes = maskWidth * 4;
            if (maskRowBytes < rowCopyBytes || dstRowBytes < rowCopyBytes)
            {
                return;
            }

            for (int y = 0; y < maskHeight; y++)
            {
                var maskRow = maskPixels.Slice(y * maskRowBytes, rowCopyBytes);
                var dstRow = dstPixels.Slice(y * dstRowBytes, rowCopyBytes);
                for (int x = 0; x < maskWidth; x++)
                {
                    int offset = x * 4;
                    if (maskRow[offset + 3] == 0)
                    {
                        dstRow[offset] = 0;
                        dstRow[offset + 1] = 0;
                        dstRow[offset + 2] = 0;
                        dstRow[offset + 3] = 0;
                    }
                }
            }
            GC.KeepAlive(mask);
            GC.KeepAlive(spriteImage);
        }

        private static SKBitmap PadToOriginalRect(SKBitmap spriteImage, Sprite sprite, Vector2 textureRectOffset)
        {
            if (sprite.m_Rect == null
                || !float.IsFinite(sprite.m_Rect.width)
                || !float.IsFinite(sprite.m_Rect.height)
                || sprite.m_Rect.width <= 0f
                || sprite.m_Rect.height <= 0f)
            {
                return spriteImage;
            }

            double ceilW = Math.Ceiling(sprite.m_Rect.width);
            double ceilH = Math.Ceiling(sprite.m_Rect.height);
            if (!double.IsFinite(ceilW) || !double.IsFinite(ceilH)
                || ceilW < 1d || ceilH < 1d
                || ceilW > int.MaxValue || ceilH > int.MaxValue)
            {
                return spriteImage;
            }

            int canvasWidth = (int)ceilW;
            int canvasHeight = (int)ceilH;
            if ((long)canvasWidth * canvasHeight * 4L > int.MaxValue)
            {
                return spriteImage;
            }

            int dstX = float.IsFinite(textureRectOffset.X) ? (int)Math.Round(textureRectOffset.X) : 0;
            int bottomOffsetY = float.IsFinite(textureRectOffset.Y) ? (int)Math.Round(textureRectOffset.Y) : 0;
            // Unity textureRectOffset.Y is measured upward from the bottom of m_Rect.
            // Convert to top-down Skia canvas Y coordinate.
            int dstY = canvasHeight - bottomOffsetY - spriteImage.Height;

            if (canvasWidth == spriteImage.Width && canvasHeight == spriteImage.Height && dstX == 0 && dstY == 0)
            {
                return spriteImage;
            }

            var canvasInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var padded = new SKBitmap(canvasInfo);
            try
            {
                var dstPixels = padded.GetWritablePixelSpan();
                dstPixels.Clear();

                int srcStartX = Math.Max(0, -dstX);
                int srcStartY = Math.Max(0, -dstY);
                int dstStartX = Math.Max(0, dstX);
                int dstStartY = Math.Max(0, dstY);

                int copyWidth = Math.Min(spriteImage.Width - srcStartX, canvasWidth - dstStartX);
                int copyHeight = Math.Min(spriteImage.Height - srcStartY, canvasHeight - dstStartY);

                if (copyWidth > 0 && copyHeight > 0)
                {
                    var srcPixels = spriteImage.GetPixelSpan();
                    int srcRowBytes = spriteImage.RowBytes;
                    int dstRowBytes = padded.RowBytes;
                    int copyBytes = copyWidth * 4;
                    int srcXBytes = srcStartX * 4;
                    int dstXBytes = dstStartX * 4;

                    for (int y = 0; y < copyHeight; y++)
                    {
                        var srcRow = srcPixels.Slice((srcStartY + y) * srcRowBytes + srcXBytes, copyBytes);
                        var dstRow = dstPixels.Slice((dstStartY + y) * dstRowBytes + dstXBytes, copyBytes);
                        srcRow.CopyTo(dstRow);
                    }
                }

                GC.KeepAlive(spriteImage);
                GC.KeepAlive(padded);
                return padded;
            }
            catch
            {
                padded.Dispose();
                throw;
            }
        }

        public static void DrawBoundsAndNineSliceOverlay(SKBitmap bitmap, Sprite sprite, bool preserveOriginalRect)
        {
            if (bitmap == null || sprite == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return;
            }

            int width = bitmap.Width;
            int height = bitmap.Height;

            float rectW = (sprite.m_Rect != null && float.IsFinite(sprite.m_Rect.width) && sprite.m_Rect.width > 0f)
                ? sprite.m_Rect.width
                : width;
            float rectH = (sprite.m_Rect != null && float.IsFinite(sprite.m_Rect.height) && sprite.m_Rect.height > 0f)
                ? sprite.m_Rect.height
                : height;

            Vector2 texOffset = Vector2.Zero;
            if (!preserveOriginalRect)
            {
                if (sprite.m_SpriteAtlas != null
                    && sprite.m_SpriteAtlas.TryGet(out var atlas)
                    && atlas.m_RenderDataMap != null
                    && atlas.m_RenderDataMap.TryGetValue(sprite.m_RenderDataKey, out var atlasData)
                    && atlasData != null)
                {
                    texOffset = atlasData.textureRectOffset;
                }
                else if (sprite.m_RD != null)
                {
                    texOffset = sprite.m_RD.textureRectOffset;
                }
            }

            float offX = float.IsFinite(texOffset.X) ? texOffset.X : 0f;
            float offY = float.IsFinite(texOffset.Y) ? texOffset.Y : 0f;

            float ToCanvasX(float rectSpaceX) => preserveOriginalRect ? rectSpaceX : rectSpaceX - offX;
            float ToCanvasY(float rectSpaceYFromBottom) => preserveOriginalRect
                ? height - rectSpaceYFromBottom
                : (offY + height) - rectSpaceYFromBottom;

            using var canvas = new SKCanvas(bitmap);

            // Subtle outer sprite bounds rectangle
            if (width >= 2 && height >= 2)
            {
                using var boundsPaint = new SKPaint
                {
                    Color = new SKColor(0x64, 0xB5, 0xF6, 0xB0),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1f,
                    IsAntialias = false
                };
                canvas.DrawRect(new SKRect(0.5f, 0.5f, width - 0.5f, height - 0.5f), boundsPaint);
            }

            // 9-slice border guide lines (Unity m_Border: X=left, Y=bottom, Z=right, W=top)
            float left = float.IsFinite(sprite.m_Border.X) ? sprite.m_Border.X : 0f;
            float bottom = float.IsFinite(sprite.m_Border.Y) ? sprite.m_Border.Y : 0f;
            float right = float.IsFinite(sprite.m_Border.Z) ? sprite.m_Border.Z : 0f;
            float top = float.IsFinite(sprite.m_Border.W) ? sprite.m_Border.W : 0f;

            if (left > 0f || bottom > 0f || right > 0f || top > 0f)
            {
                using var slicePaint = new SKPaint
                {
                    Color = new SKColor(0x00, 0xE6, 0x76, 0xD8),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1f,
                    IsAntialias = false
                };

                if (left > 0f)
                {
                    float lx = ToCanvasX(left);
                    if (lx >= 0f && lx <= width)
                    {
                        float px = Math.Clamp(lx, 0.5f, Math.Max(0.5f, width - 0.5f));
                        canvas.DrawLine(px, 0f, px, height, slicePaint);
                    }
                }

                if (right > 0f)
                {
                    float rx = ToCanvasX(rectW - right);
                    if (rx >= 0f && rx <= width)
                    {
                        float px = Math.Clamp(rx, 0.5f, Math.Max(0.5f, width - 0.5f));
                        canvas.DrawLine(px, 0f, px, height, slicePaint);
                    }
                }

                if (bottom > 0f)
                {
                    float by = ToCanvasY(bottom);
                    if (by >= 0f && by <= height)
                    {
                        float py = Math.Clamp(by, 0.5f, Math.Max(0.5f, height - 0.5f));
                        canvas.DrawLine(0f, py, width, py, slicePaint);
                    }
                }

                if (top > 0f)
                {
                    float ty = ToCanvasY(rectH - top);
                    if (ty >= 0f && ty <= height)
                    {
                        float py = Math.Clamp(ty, 0.5f, Math.Max(0.5f, height - 0.5f));
                        canvas.DrawLine(0f, py, width, py, slicePaint);
                    }
                }
            }

            // Pivot crosshair (Unity m_Pivot: normalized [0..1] bottom-left origin relative to m_Rect)
            if (float.IsFinite(sprite.m_Pivot.X) && float.IsFinite(sprite.m_Pivot.Y))
            {
                float pivotX = ToCanvasX(rectW * sprite.m_Pivot.X);
                float pivotY = ToCanvasY(rectH * sprite.m_Pivot.Y);
                if (pivotX >= -1f && pivotX <= width + 1f && pivotY >= -1f && pivotY <= height + 1f)
                {
                    float cx = Math.Clamp(pivotX, 0.5f, Math.Max(0.5f, width - 0.5f));
                    float cy = Math.Clamp(pivotY, 0.5f, Math.Max(0.5f, height - 0.5f));
                    float arm = Math.Clamp(Math.Min(width, height) * 0.12f, 2f, 8f);

                    using var shadowPaint = new SKPaint
                    {
                        Color = new SKColor(0x00, 0x00, 0x00, 0xC0),
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 3f,
                        IsAntialias = false
                    };
                    using var pivotPaint = new SKPaint
                    {
                        Color = new SKColor(0xFF, 0x52, 0x52, 0xF0),
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1f,
                        IsAntialias = false
                    };

                    if (width == 1 && height == 1)
                    {
                        using var dotPaint = new SKPaint { Color = new SKColor(0xFF, 0x52, 0x52, 0xF0), Style = SKPaintStyle.Fill };
                        canvas.DrawPoint(0, 0, dotPaint);
                    }
                    else
                    {
                        canvas.DrawLine(cx - arm, cy, cx + arm, cy, shadowPaint);
                        canvas.DrawLine(cx, cy - arm, cx, cy + arm, shadowPaint);
                        canvas.DrawLine(cx - arm, cy, cx + arm, cy, pivotPaint);
                        canvas.DrawLine(cx, cy - arm, cx, cy + arm, pivotPaint);
                        if (width >= 6 && height >= 6)
                        {
                            canvas.DrawCircle(cx, cy, Math.Min(3f, arm * 0.5f), pivotPaint);
                        }
                    }
                }
            }

            canvas.Flush();
        }

        private static void FlipHorizontalInPlace(SKBitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            if (width <= 1 || height <= 0)
            {
                return;
            }

            var pixels = bitmap.GetWritablePixelSpan();
            int rowBytes = bitmap.RowBytes;
            int halfWidth = width / 2;
            for (int y = 0; y < height; y++)
            {
                var row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * rowBytes, width * 4));
                for (int x = 0; x < halfWidth; x++)
                {
                    int mirrorX = width - 1 - x;
                    uint tmp = row[x];
                    row[x] = row[mirrorX];
                    row[mirrorX] = tmp;
                }
            }
            GC.KeepAlive(bitmap);
        }

        private static void Rotate180InPlace(SKBitmap bitmap)
        {
            bitmap.FlipVerticalInPlace();
            FlipHorizontalInPlace(bitmap);
        }

        private static SKBitmap Rotate270Clockwise(SKBitmap source)
        {
            int srcWidth = source.Width;
            int srcHeight = source.Height;
            var dstInfo = new SKImageInfo(srcHeight, srcWidth, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var rotated = new SKBitmap(dstInfo);
            try
            {
                var srcPixels = source.GetPixelSpan();
                var dstPixels = rotated.GetWritablePixelSpan();
                int srcRowBytes = source.RowBytes;
                int dstRowBytes = rotated.RowBytes;

                for (int y = 0; y < srcHeight; y++)
                {
                    var srcRow = MemoryMarshal.Cast<byte, uint>(srcPixels.Slice(y * srcRowBytes, srcWidth * 4));
                    int dstX = y;
                    for (int x = 0; x < srcWidth; x++)
                    {
                        int dstY = srcWidth - 1 - x;
                        var dstRow = MemoryMarshal.Cast<byte, uint>(dstPixels.Slice(dstY * dstRowBytes, srcHeight * 4));
                        dstRow[dstX] = srcRow[x];
                    }
                }
                GC.KeepAlive(source);
                GC.KeepAlive(rotated);
                return rotated;
            }
            catch
            {
                rotated.Dispose();
                throw;
            }
        }

        private static SKBitmap? CropSpriteRegion(AtlasCacheEntry entry, Rectf textureRect, out SKRectI rect)
        {
            if (textureRect == null
                || !float.IsFinite(textureRect.x)
                || !float.IsFinite(textureRect.y)
                || !float.IsFinite(textureRect.width)
                || !float.IsFinite(textureRect.height)
                || textureRect.width <= 0f
                || textureRect.height <= 0f)
            {
                rect = default;
                return null;
            }

            lock (entry.SyncRoot)
            {
                var originalImage = entry.Image;
                if (originalImage.Width <= 0 || originalImage.Height <= 0 || originalImage.BytesPerPixel != 4)
                {
                    rect = default;
                    return null;
                }

                double xFloor = Math.Floor(textureRect.x);
                double yFloor = Math.Floor(textureRect.y);
                double rightCeil = Math.Ceiling((double)textureRect.x + textureRect.width);
                double bottomCeil = Math.Ceiling((double)textureRect.y + textureRect.height);

                if (!double.IsFinite(xFloor) || !double.IsFinite(yFloor) || !double.IsFinite(rightCeil) || !double.IsFinite(bottomCeil))
                {
                    rect = default;
                    return null;
                }

                int rectX = (int)Math.Clamp(xFloor, 0d, originalImage.Width);
                int rectY = (int)Math.Clamp(yFloor, 0d, originalImage.Height);
                int rectRight = (int)Math.Clamp(rightCeil, 0d, originalImage.Width);
                int rectBottom = (int)Math.Clamp(bottomCeil, 0d, originalImage.Height);
                int rectWidth = rectRight - rectX;
                int rectHeight = rectBottom - rectY;
                if (rectWidth <= 0 || rectHeight <= 0 || (long)rectWidth * rectHeight * 4L > int.MaxValue)
                {
                    rect = default;
                    return null;
                }

                rect = SKRectI.Create(rectX, rectY, rectWidth, rectHeight);
                var info = new SKImageInfo(rectWidth, rectHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                var spriteImage = new SKBitmap(info);
                try
                {
                    var srcPixels = originalImage.GetPixelSpan();
                    var dstPixels = spriteImage.GetWritablePixelSpan();
                    int srcRowBytes = originalImage.RowBytes;
                    int dstRowBytes = spriteImage.RowBytes;
                    int copyBytes = rectWidth * 4;
                    int srcXBytes = rectX * 4;
                    for (int y = 0; y < rectHeight; y++)
                    {
                        var srcRow = srcPixels.Slice((rectY + y) * srcRowBytes + srcXBytes, copyBytes);
                        var dstRow = dstPixels.Slice(y * dstRowBytes, copyBytes);
                        srcRow.CopyTo(dstRow);
                    }
                    GC.KeepAlive(originalImage);
                    GC.KeepAlive(spriteImage);
                    return spriteImage;
                }
                catch
                {
                    spriteImage.Dispose();
                    throw;
                }
            }
        }

        private static AtlasLease AcquireAtlasLease(Texture2D m_Texture2D, int targetWidth, int targetHeight)
        {
            var key = new AtlasCacheKey(
                m_Texture2D.assetsFile?.fullName,
                m_Texture2D.m_PathID,
                RuntimeHelpers.GetHashCode(m_Texture2D),
                targetWidth,
                targetHeight);

            AtlasCacheEntry? cachedHit = null;
            InFlightDecode? flightToWait = null;
            InFlightDecode? flightToLead = null;
            long capturedGeneration = 0;
            bool bypassInFlight = false;
            List<AtlasCacheEntry>? toDispose = null;

            lock (_cacheLock)
            {
                PruneDeadEntriesLocked(ref toDispose);

                if (_cacheByKey.TryGetValue(key, out var cached))
                {
                    if (cached.Matches(m_Texture2D))
                    {
                        if (cached.LruNode != null && cached.LruNode != _lruList.First)
                        {
                            _lruList.Remove(cached.LruNode);
                            _lruList.AddFirst(cached.LruNode);
                        }
                        cached.AddRef();
                        cachedHit = cached;
                    }
                    else
                    {
                        RemoveEntryLocked(cached, ref toDispose);
                    }
                }

                if (cachedHit == null)
                {
                    if (_inFlight.TryGetValue(key, out var existingFlight))
                    {
                        if (existingFlight.Matches(m_Texture2D))
                        {
                            existingFlight.WaiterCount++;
                            flightToWait = existingFlight;
                        }
                        else
                        {
                            // Extremely rare identity-hash collision with another live Texture2D currently decoding.
                            bypassInFlight = true;
                        }
                    }
                    else
                    {
                        flightToLead = new InFlightDecode(m_Texture2D);
                        _inFlight[key] = flightToLead;
                        capturedGeneration = _cacheGeneration;
                    }
                }
            }

            DisposeEntries(toDispose);

            if (cachedHit != null)
            {
                return new AtlasLease(cachedHit);
            }

            if (flightToWait != null)
            {
                var awaited = flightToWait.Completion.Task.GetAwaiter().GetResult();
                return awaited != null ? new AtlasLease(awaited) : default;
            }

            if (bypassInFlight)
            {
                var directImage = DecodeAtlasImage(m_Texture2D, targetWidth, targetHeight);
                if (directImage == null)
                {
                    return default;
                }

                var directEntry = new AtlasCacheEntry(key, m_Texture2D, directImage, (long)directImage.Width * directImage.Height * 4L);
                directEntry.InitializeRefCount(1);
                return new AtlasLease(directEntry);
            }

            if (flightToLead == null)
            {
                return default;
            }

            AtlasCacheEntry? decodedEntry = null;
            try
            {
                var decoded = DecodeAtlasImage(m_Texture2D, targetWidth, targetHeight);
                if (decoded != null)
                {
                    long estimatedBytes = (long)decoded.Width * decoded.Height * 4L;
                    decodedEntry = new AtlasCacheEntry(key, m_Texture2D, decoded, estimatedBytes);
                }
            }
            finally
            {
                List<AtlasCacheEntry>? evictedToDispose = null;
                lock (_cacheLock)
                {
                    if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flightToLead))
                    {
                        _inFlight.Remove(key);
                    }

                    if (decodedEntry != null)
                    {
                        // Reserve 1 lease for the leader plus 1 lease for every waiter that joined while in-flight.
                        decodedEntry.InitializeRefCount(flightToLead.WaiterCount);

                        if (capturedGeneration == _cacheGeneration && decodedEntry.EstimatedBytes <= MaxCachedBytes)
                        {
                            if (_cacheByKey.TryGetValue(key, out var existing))
                            {
                                RemoveEntryLocked(existing, ref evictedToDispose);
                            }

                            // Cache membership holds +1 reference until evicted or cleared.
                            decodedEntry.AddRef();
                            decodedEntry.LruNode = _lruList.AddFirst(decodedEntry);
                            _cacheByKey[key] = decodedEntry;
                            _currentCachedBytes += decodedEntry.EstimatedBytes;

                            EvictOverflowLocked(ref evictedToDispose);
                        }
                    }
                }

                flightToLead.Completion.TrySetResult(decodedEntry);
                DisposeEntries(evictedToDispose);
            }

            return decodedEntry != null ? new AtlasLease(decodedEntry) : default;
        }

        private static SKBitmap? DecodeAtlasImage(Texture2D m_Texture2D, int targetWidth, int targetHeight)
        {
            var originalImage = m_Texture2D.ConvertToImage(false);
            if (originalImage == null)
            {
                return null;
            }

            if (targetWidth > 0 && targetHeight > 0 && (originalImage.Width != targetWidth || originalImage.Height != targetHeight))
            {
                try
                {
                    var resizedInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    var resized = originalImage.Resize(resizedInfo, ResizeSampling);
                    originalImage.Dispose();
                    return resized;
                }
                catch
                {
                    originalImage.Dispose();
                    throw;
                }
            }

            return originalImage;
        }

        private static void PruneDeadEntriesLocked(ref List<AtlasCacheEntry>? toDispose)
        {
            var node = _lruList.First;
            while (node != null)
            {
                var next = node.Next;
                if (!node.Value.IsTextureAlive)
                {
                    RemoveEntryLocked(node.Value, ref toDispose);
                }
                node = next;
            }
        }

        private static void EvictOverflowLocked(ref List<AtlasCacheEntry>? toDispose)
        {
            while (_lruList.Count > MaxCachedAtlases || _currentCachedBytes > MaxCachedBytes)
            {
                var lru = _lruList.Last;
                if (lru == null)
                {
                    break;
                }
                RemoveEntryLocked(lru.Value, ref toDispose);
            }
        }

        private static void RemoveEntryLocked(AtlasCacheEntry entry, ref List<AtlasCacheEntry>? toDispose)
        {
            if (entry.LruNode == null)
            {
                return;
            }

            _lruList.Remove(entry.LruNode);
            entry.LruNode = null;
            if (_cacheByKey.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _cacheByKey.Remove(entry.Key);
            }
            _currentCachedBytes = Math.Max(0L, _currentCachedBytes - entry.EstimatedBytes);

            if (entry.Release())
            {
                toDispose ??= new List<AtlasCacheEntry>();
                toDispose.Add(entry);
            }
        }

        private static void DisposeEntries(List<AtlasCacheEntry>? entries)
        {
            if (entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].DisposeImage();
            }
        }

        private readonly struct AtlasCacheKey : IEquatable<AtlasCacheKey>
        {
            public readonly string FileKey;
            public readonly long PathId;
            public readonly int IdentityHash;
            public readonly int TargetWidth;
            public readonly int TargetHeight;

            public AtlasCacheKey(string? fileKey, long pathId, int identityHash, int targetWidth, int targetHeight)
            {
                FileKey = fileKey ?? string.Empty;
                PathId = pathId;
                IdentityHash = identityHash;
                TargetWidth = targetWidth;
                TargetHeight = targetHeight;
            }

            public bool Equals(AtlasCacheKey other)
            {
                return PathId == other.PathId
                    && IdentityHash == other.IdentityHash
                    && TargetWidth == other.TargetWidth
                    && TargetHeight == other.TargetHeight
                    && string.Equals(FileKey, other.FileKey, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj) => obj is AtlasCacheKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(FileKey, PathId, IdentityHash, TargetWidth, TargetHeight);
        }

        private sealed class AtlasCacheEntry
        {
            public readonly AtlasCacheKey Key;
            public readonly WeakReference<Texture2D> TextureRef;
            public readonly SKBitmap Image;
            public readonly long EstimatedBytes;
            public readonly object SyncRoot = new object();
            public LinkedListNode<AtlasCacheEntry>? LruNode;

            private int _refCount;

            public AtlasCacheEntry(AtlasCacheKey key, Texture2D texture, SKBitmap image, long estimatedBytes)
            {
                Key = key;
                TextureRef = new WeakReference<Texture2D>(texture);
                Image = image;
                EstimatedBytes = estimatedBytes;
            }

            public bool IsTextureAlive => TextureRef.TryGetTarget(out _);

            public bool Matches(Texture2D texture)
            {
                return TextureRef.TryGetTarget(out var target) && ReferenceEquals(target, texture);
            }

            public void InitializeRefCount(int initialCount)
            {
                Volatile.Write(ref _refCount, initialCount);
            }

            public void AddRef()
            {
                Interlocked.Increment(ref _refCount);
            }

            public bool Release()
            {
                return Interlocked.Decrement(ref _refCount) == 0;
            }

            public void DisposeImage()
            {
                Image.Dispose();
            }
        }

        private sealed class InFlightDecode
        {
            public readonly WeakReference<Texture2D> TextureRef;
            public readonly TaskCompletionSource<AtlasCacheEntry?> Completion = new TaskCompletionSource<AtlasCacheEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int WaiterCount = 1;

            public InFlightDecode(Texture2D texture)
            {
                TextureRef = new WeakReference<Texture2D>(texture);
            }

            public bool Matches(Texture2D texture)
            {
                return TextureRef.TryGetTarget(out var target) && ReferenceEquals(target, texture);
            }
        }

        private readonly struct AtlasLease : IDisposable
        {
            public readonly AtlasCacheEntry? Entry;

            public AtlasLease(AtlasCacheEntry? entry)
            {
                Entry = entry;
            }

            public void Dispose()
            {
                if (Entry != null && Entry.Release())
                {
                    Entry.DisposeImage();
                }
            }
        }

        private static void ApplyAlphaTexture(SKBitmap spriteImage, Texture2D alphaTexture, int atlasWidth, int atlasHeight, SKRectI rect)
        {
            if (spriteImage.Width <= 0 || spriteImage.Height <= 0 || atlasWidth <= 0 || atlasHeight <= 0)
            {
                return;
            }

            using var alphaLease = AcquireAtlasLease(alphaTexture, atlasWidth, atlasHeight);
            if (alphaLease.Entry == null)
            {
                return;
            }

            // Alpha8 stores opacity in channel 3 (A in BGRA8888); non-Alpha8 split masks (ETC1/RGB24/R8/RGBA32) store it in channel 2 (R in BGRA8888).
            int alphaChannelIndex = alphaTexture.m_TextureFormat == TextureFormat.Alpha8 ? 3 : 2;
            lock (alphaLease.Entry.SyncRoot)
            {
                var alphaImage = alphaLease.Entry.Image;
                if (rect.Left < 0 || rect.Top < 0 || rect.Left >= alphaImage.Width || rect.Top >= alphaImage.Height)
                {
                    return;
                }

                int copyWidth = Math.Min(spriteImage.Width, Math.Min(rect.Width, alphaImage.Width - rect.Left));
                int copyHeight = Math.Min(spriteImage.Height, Math.Min(rect.Height, alphaImage.Height - rect.Top));
                if (copyWidth <= 0 || copyHeight <= 0)
                {
                    return;
                }

                var alphaPixels = alphaImage.GetPixelSpan();
                var dstPixels = spriteImage.GetWritablePixelSpan();
                int alphaRowBytes = alphaImage.RowBytes;
                int dstRowBytes = spriteImage.RowBytes;
                int alphaXBytes = rect.Left * 4;
                int rowCopyBytes = copyWidth * 4;

                for (int y = 0; y < copyHeight; y++)
                {
                    var alphaRow = alphaPixels.Slice((rect.Top + y) * alphaRowBytes + alphaXBytes, rowCopyBytes);
                    var dstRow = dstPixels.Slice(y * dstRowBytes, rowCopyBytes);
                    for (int x = 0; x < copyWidth; x++)
                    {
                        int offset = x * 4;
                        dstRow[offset + 3] = alphaRow[offset + alphaChannelIndex];
                    }
                }

                GC.KeepAlive(alphaImage);
                GC.KeepAlive(spriteImage);
            }
        }

        public static Vector2[][] GetTriangles(SpriteRenderData m_RD, int[]? version = null)
        {
            if (m_RD.vertices != null && m_RD.indices != null) //5.6 down
            {
                var vertices = m_RD.vertices.Select(x => (Vector2)x.pos).ToArray();
                var triangleCount = m_RD.indices.Length / 3;
                var triangles = new List<Vector2[]>(triangleCount);
                for (int i = 0; i < triangleCount; i++)
                {
                    var first = m_RD.indices[i * 3];
                    var second = m_RD.indices[i * 3 + 1];
                    var third = m_RD.indices[i * 3 + 2];
                    if (first < vertices.Length && second < vertices.Length && third < vertices.Length)
                    {
                        triangles.Add(new[] { vertices[first], vertices[second], vertices[third] });
                    }
                }
                return triangles.ToArray();
            }
            else if (m_RD.m_VertexData != null && m_RD.m_IndexBuffer != null && m_RD.m_SubMeshes != null) //5.6 and up
            {
                var m_VertexData = m_RD.m_VertexData;
                if (m_VertexData.m_Channels == null || m_VertexData.m_Channels.Length == 0
                    || m_VertexData.m_Streams == null || m_VertexData.m_DataSize == null)
                {
                    return Array.Empty<Vector2[]>();
                }

                var m_Channel = m_VertexData.m_Channels[0]; //kShaderChannelVertex
                if (m_Channel.dimension == 0 || m_Channel.stream >= m_VertexData.m_Streams.Length)
                {
                    return Array.Empty<Vector2[]>();
                }

                var m_Stream = m_VertexData.m_Streams[m_Channel.stream];
                var vertexFormat = ResolveVertexFormat(m_Channel.format, version);
                int componentSize = (int)MeshHelper.GetFormatSize(vertexFormat);
                uint stride = m_Stream.stride;
                if (stride == 0)
                {
                    stride = ComputeFallbackStreamStride(m_VertexData.m_Channels, m_Channel.stream, version);
                    if (stride == 0)
                    {
                        stride = (uint)(componentSize * Math.Max(2, (int)m_Channel.dimension));
                    }
                }

                long totalIndexCount = 0;
                foreach (var sm in m_RD.m_SubMeshes)
                {
                    totalIndexCount += sm.indexCount;
                }

                bool use32BitIndices = m_VertexData.m_VertexCount > ushort.MaxValue
                    || (totalIndexCount > 0
                        && m_RD.m_IndexBuffer.Length >= totalIndexCount * 4L
                        && m_RD.m_SubMeshes.All(sm => (long)sm.firstByte + (long)sm.indexCount * 4L <= m_RD.m_IndexBuffer.Length));
                int indexSize = use32BitIndices ? 4 : 2;

                var triangles = new List<Vector2[]>();
                ReadOnlySpan<byte> vertexBytes = m_VertexData.m_DataSize;
                ReadOnlySpan<byte> indexBytes = m_RD.m_IndexBuffer;

                foreach (var subMesh in m_RD.m_SubMeshes)
                {
                    if (subMesh.vertexCount == 0 || subMesh.indexCount < 3)
                    {
                        continue;
                    }

                    var vertices = new Vector2[subMesh.vertexCount];
                    for (uint v = 0; v < subMesh.vertexCount; v++)
                    {
                        long posOffset = m_Stream.offset + ((long)subMesh.firstVertex + v) * stride + m_Channel.offset;
                        if (posOffset < 0 || posOffset + componentSize * 2L > vertexBytes.Length)
                        {
                            break;
                        }

                        var elemSpan = vertexBytes.Slice((int)posOffset);
                        float x = ReadVertexComponent(elemSpan, 0, vertexFormat);
                        float y = ReadVertexComponent(elemSpan, componentSize, vertexFormat);
                        vertices[v] = new Vector2(x, y);
                    }

                    var triangleCount = subMesh.indexCount / 3u;
                    for (uint i = 0; i < triangleCount; i++)
                    {
                        long idxOffset = (long)subMesh.firstByte + (long)i * 3L * indexSize;
                        if (idxOffset < 0 || idxOffset + 3L * indexSize > indexBytes.Length)
                        {
                            break;
                        }

                        uint raw0, raw1, raw2;
                        if (use32BitIndices)
                        {
                            raw0 = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.Slice((int)idxOffset, 4));
                            raw1 = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.Slice((int)idxOffset + 4, 4));
                            raw2 = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.Slice((int)idxOffset + 8, 4));
                        }
                        else
                        {
                            raw0 = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.Slice((int)idxOffset, 2));
                            raw1 = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.Slice((int)idxOffset + 2, 2));
                            raw2 = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.Slice((int)idxOffset + 4, 2));
                        }

                        long first = (long)raw0 + subMesh.baseVertex - subMesh.firstVertex;
                        long second = (long)raw1 + subMesh.baseVertex - subMesh.firstVertex;
                        long third = (long)raw2 + subMesh.baseVertex - subMesh.firstVertex;

                        if ((ulong)first < (ulong)vertices.Length
                            && (ulong)second < (ulong)vertices.Length
                            && (ulong)third < (ulong)vertices.Length)
                        {
                            triangles.Add(new[] { vertices[first], vertices[second], vertices[third] });
                        }
                    }
                }
                return triangles.ToArray();
            }

            return Array.Empty<Vector2[]>();
        }

        private static MeshHelper.VertexFormat ResolveVertexFormat(byte format, int[]? version)
        {
            try
            {
                if (version != null && version.Length > 0)
                {
                    return MeshHelper.ToVertexFormat(format, version);
                }

                if (Enum.IsDefined(typeof(MeshHelper.VertexFormat), (int)format))
                {
                    return (MeshHelper.VertexFormat)format;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
            }

            return MeshHelper.VertexFormat.Float;
        }

        private static uint ComputeFallbackStreamStride(ChannelInfo[] channels, byte streamIndex, int[]? version)
        {
            uint maxEnd = 0;
            for (int i = 0; i < channels.Length; i++)
            {
                var ch = channels[i];
                if (ch.stream == streamIndex && ch.dimension > 0)
                {
                    var fmt = ResolveVertexFormat(ch.format, version);
                    uint end = ch.offset + MeshHelper.GetFormatSize(fmt) * ch.dimension;
                    if (end > maxEnd)
                    {
                        maxEnd = end;
                    }
                }
            }
            return maxEnd;
        }

        private static float ReadVertexComponent(ReadOnlySpan<byte> data, int byteOffset, MeshHelper.VertexFormat format)
        {
            var slice = data.Slice(byteOffset);
            return format switch
            {
                MeshHelper.VertexFormat.Float => BinaryPrimitives.ReadSingleLittleEndian(slice),
                MeshHelper.VertexFormat.Float16 => (float)BinaryPrimitives.ReadHalfLittleEndian(slice),
                MeshHelper.VertexFormat.UNorm8 => slice[0] / 255f,
                MeshHelper.VertexFormat.SNorm8 => Math.Max((sbyte)slice[0] / 127f, -1f),
                MeshHelper.VertexFormat.UNorm16 => BinaryPrimitives.ReadUInt16LittleEndian(slice) / 65535f,
                MeshHelper.VertexFormat.SNorm16 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(slice) / 32767f, -1f),
                MeshHelper.VertexFormat.UInt8 => slice[0],
                MeshHelper.VertexFormat.SInt8 => (sbyte)slice[0],
                MeshHelper.VertexFormat.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(slice),
                MeshHelper.VertexFormat.SInt16 => BinaryPrimitives.ReadInt16LittleEndian(slice),
                MeshHelper.VertexFormat.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(slice),
                MeshHelper.VertexFormat.SInt32 => BinaryPrimitives.ReadInt32LittleEndian(slice),
                _ => BinaryPrimitives.ReadSingleLittleEndian(slice)
            };
        }
    }
}
