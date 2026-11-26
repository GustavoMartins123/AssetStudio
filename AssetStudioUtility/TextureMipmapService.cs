using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AssetStudio
{
    public sealed record TextureMipLevelInfo(
        int Level,
        int Width,
        int Height,
        long ByteOffset,
        long ByteSize,
        bool IsAvailable);

    public sealed class TextureMipChainExportResult
    {
        public string TextureName { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public string? ManifestPath { get; init; }
        public int TotalMipLevels { get; init; }
        public int ExportedMipLevels { get; init; }
        public int SkippedMipLevels { get; init; }
        public IReadOnlyList<string> ExportedFilePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<TextureMipLevelInfo> MipLevels { get; init; } = Array.Empty<TextureMipLevelInfo>();
    }

    public static class TextureMipmapService
    {
        private static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static bool IsCrunchedFormat(TextureFormat format)
        {
            return format is TextureFormat.DXT1Crunched
                or TextureFormat.DXT5Crunched
                or TextureFormat.ETC_RGB4Crunched
                or TextureFormat.ETC2_RGBA8Crunched;
        }

        public static IReadOnlyList<TextureMipLevelInfo> ComputeMipChain(Texture2D? texture)
        {
            if (texture == null || texture.m_Width <= 0 || texture.m_Height <= 0)
            {
                return Array.Empty<TextureMipLevelInfo>();
            }

            long availableBytes = texture.image_data?.Size ?? 0L;
            if (availableBytes < 0)
            {
                availableBytes = 0;
            }

            if (IsCrunchedFormat(texture.m_TextureFormat))
            {
                return new[]
                {
                    new TextureMipLevelInfo(
                        Level: 0,
                        Width: texture.m_Width,
                        Height: texture.m_Height,
                        ByteOffset: 0L,
                        ByteSize: availableBytes,
                        IsAvailable: availableBytes > 0)
                };
            }

            long mip0Size = Texture2DConverter.GetMinimumDataSize(texture.m_TextureFormat, texture.m_Width, texture.m_Height);
            if (mip0Size <= 0)
            {
                return new[]
                {
                    new TextureMipLevelInfo(
                        Level: 0,
                        Width: texture.m_Width,
                        Height: texture.m_Height,
                        ByteOffset: 0L,
                        ByteSize: availableBytes,
                        IsAvailable: availableBytes > 0 && Texture2DConverter.IsSupportedFormat(texture.m_TextureFormat))
                };
            }

            int maxPossibleMips = ComputeMaxMipLevels(texture.m_Width, texture.m_Height);
            int targetMipCount;
            if (texture.m_MipCount > 0)
            {
                targetMipCount = Math.Min(texture.m_MipCount, maxPossibleMips);
            }
            else if (texture.m_MipMap)
            {
                targetMipCount = maxPossibleMips;
            }
            else
            {
                targetMipCount = 1;
            }

            var levels = new List<TextureMipLevelInfo>(targetMipCount);
            long offset = 0;
            for (int level = 0; level < targetMipCount; level++)
            {
                int mipW = Math.Max(1, texture.m_Width >> level);
                int mipH = Math.Max(1, texture.m_Height >> level);
                long levelSize = Texture2DConverter.GetMinimumDataSize(texture.m_TextureFormat, mipW, mipH);

                bool isAvailable = levelSize > 0
                    && offset >= 0
                    && availableBytes > 0
                    && offset <= availableBytes
                    && levelSize <= availableBytes - offset;

                levels.Add(new TextureMipLevelInfo(
                    Level: level,
                    Width: mipW,
                    Height: mipH,
                    ByteOffset: offset,
                    ByteSize: levelSize,
                    IsAvailable: isAvailable));

                if (levelSize > 0 && offset <= long.MaxValue - levelSize)
                {
                    offset += levelSize;
                }
                else
                {
                    offset = long.MaxValue;
                }

                if (mipW == 1 && mipH == 1)
                {
                    break;
                }
            }

            return levels;
        }

        public static byte[]? ReadMipRawBytes(Texture2D? texture, int mipLevel, out TextureMipLevelInfo mipInfo)
        {
            mipInfo = new TextureMipLevelInfo(mipLevel, 0, 0, 0, 0, false);
            if (texture == null || texture.image_data == null || mipLevel < 0)
            {
                return null;
            }

            var chain = ComputeMipChain(texture);
            if (mipLevel >= chain.Count)
            {
                return null;
            }

            mipInfo = chain[mipLevel];
            if (!mipInfo.IsAvailable || mipInfo.ByteSize <= 0 || mipInfo.ByteSize > int.MaxValue)
            {
                return null;
            }

            try
            {
                var bytes = new byte[(int)mipInfo.ByteSize];
                using var session = texture.image_data.OpenReadSession();
                if (mipInfo.ByteOffset < 0 || mipInfo.ByteOffset > session.Size - bytes.Length)
                {
                    return null;
                }

                int read = session.ReadSegment(mipInfo.ByteOffset, bytes, 0, bytes.Length);
                return read == bytes.Length ? bytes : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Logger.Warning($"Failed to read mip {mipLevel} bytes for Texture2D '{texture.m_Name}': {ex.Message}");
                return null;
            }
        }

        public static SKBitmap? DecodeMipLevel(
            Texture2D? texture,
            int mipLevel,
            bool flip = true,
            bool unpackNormalMap = true,
            bool forceNormalMapUnpack = false)
        {
            if (texture == null || mipLevel < 0)
            {
                return null;
            }

            var mipBytes = ReadMipRawBytes(texture, mipLevel, out var mipInfo);
            if (mipBytes == null || mipInfo.Width <= 0 || mipInfo.Height <= 0)
            {
                return null;
            }

            long outputSize = (long)mipInfo.Width * mipInfo.Height * 4L;
            if (outputSize <= 0 || outputSize > int.MaxValue)
            {
                return null;
            }

            var mipReader = new ResourceReader(mipBytes);
            var converter = new Texture2DConverter(
                mipReader,
                mipInfo.Width,
                mipInfo.Height,
                texture.m_TextureFormat,
                texture.version ?? Array.Empty<int>(),
                texture.platform);

            if (!converter.CanDecode())
            {
                return null;
            }

            SKBitmap? bitmap = null;
            try
            {
                var info = new SKImageInfo(mipInfo.Width, mipInfo.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                bitmap = new SKBitmap(info);
                var dstSpan = bitmap.GetWritablePixelSpan();
                int expectedRowBytes = checked(mipInfo.Width * 4);
                if (bitmap.RowBytes == expectedRowBytes && dstSpan.Length >= (int)outputSize)
                {
                    var pixelSpan = dstSpan.Slice(0, (int)outputSize);
                    if (!converter.DecodeTexture2D(pixelSpan))
                    {
                        bitmap.Dispose();
                        bitmap = null;
                        return null;
                    }

                    if (unpackNormalMap)
                    {
                        Texture2DConverter.UnpackNormalMapBgra32(pixelSpan, texture.m_TextureFormat, forceNormalMapUnpack);
                    }

                    if (flip)
                    {
                        bitmap.FlipVerticalInPlace();
                    }

                    GC.KeepAlive(bitmap);
                    var result = bitmap;
                    bitmap = null;
                    return result;
                }

                bitmap.Dispose();
                bitmap = null;

                var pixels = GC.AllocateUninitializedArray<byte>((int)outputSize, pinned: true);
                if (!converter.DecodeTexture2D(pixels))
                {
                    return null;
                }

                var span = pixels.AsSpan(0, (int)outputSize);
                if (unpackNormalMap)
                {
                    Texture2DConverter.UnpackNormalMapBgra32(span, texture.m_TextureFormat, forceNormalMapUnpack);
                }

                if (flip)
                {
                    int rowBytes = checked(mipInfo.Width * 4);
                    ImageExtensions.FlipVerticalInPlace(span, rowBytes, rowBytes, mipInfo.Height);
                }

                return ImageExtensions.WrapBgraUnpremulBitmap(pixels, mipInfo.Width, mipInfo.Height);
            }
            catch (OutOfMemoryException)
            {
                bitmap?.Dispose();
                Logger.Warning($"Out of memory decoding mip {mipLevel} ({mipInfo.Width}x{mipInfo.Height}) for Texture2D '{texture.m_Name}'.");
                return null;
            }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
        }

        public static TextureMipChainExportResult ExportMipChain(
            Texture2D texture,
            string outputDirectory,
            ImageFormat imageFormat = ImageFormat.Png,
            bool flip = true,
            bool unpackNormalMap = true,
            bool forceNormalMapUnpack = false,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(texture);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Output directory cannot be empty.", nameof(outputDirectory));
            }

            string fullOutputDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutputDir);

            string rawName = string.IsNullOrWhiteSpace(texture.m_Name)
                ? $"Texture2D_{texture.m_PathID}"
                : texture.m_Name;
            string safeBaseName = SanitizeFileNameStrict(rawName, $"Texture2D_{texture.m_PathID}");
            string extension = imageFormat.GetFileExtension();

            var mipChain = ComputeMipChain(texture);
            var exportedFiles = new List<string>(mipChain.Count);
            var manifestEntries = new List<MipManifestEntry>(mipChain.Count);
            int exportedCount = 0;
            int skippedCount = 0;

            for (int i = 0; i < mipChain.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var mip = mipChain[i];
                string? exportedFileName = null;

                if (mip.IsAvailable)
                {
                    using var bitmap = DecodeMipLevel(texture, mip.Level, flip, unpackNormalMap, forceNormalMapUnpack);
                    if (bitmap != null)
                    {
                        string candidateName = SanitizeFileNameStrict(
                            $"{safeBaseName}_mip{mip.Level}_{mip.Width}x{mip.Height}{extension}",
                            $"mip{mip.Level}{extension}");
                        string targetPath = CombineConfinedPath(fullOutputDir, candidateName);

                        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            bitmap.WriteToStream(fs, imageFormat);
                        }

                        exportedFileName = Path.GetFileName(targetPath);
                        exportedFiles.Add(targetPath);
                        exportedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                else
                {
                    skippedCount++;
                }

                manifestEntries.Add(new MipManifestEntry
                {
                    Level = mip.Level,
                    Width = mip.Width,
                    Height = mip.Height,
                    ByteOffset = mip.ByteOffset,
                    ByteSize = mip.ByteSize,
                    IsAvailable = mip.IsAvailable,
                    ExportedFile = exportedFileName
                });
            }

            string manifestFileName = SanitizeFileNameStrict($"{safeBaseName}_mipchain.json", "texture_mipchain.json");
            string manifestPath = CombineConfinedPath(fullOutputDir, manifestFileName);
            var manifest = new MipChainManifestDocument
            {
                TextureName = rawName,
                PathId = texture.m_PathID,
                SourceFile = texture.assetsFile?.fileName ?? string.Empty,
                Format = texture.m_TextureFormat.ToString(),
                IsHdr = HdrTextureExporter.IsHdrFormat(texture.m_TextureFormat),
                Width = texture.m_Width,
                Height = texture.m_Height,
                DeclaredMipCount = texture.m_MipCount > 0 ? texture.m_MipCount : mipChain.Count,
                ExportedMipCount = exportedCount,
                ImageFormat = imageFormat.ToString(),
                Levels = manifestEntries
            };

            string manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            File.WriteAllText(manifestPath, manifestJson, Encoding.UTF8);

            return new TextureMipChainExportResult
            {
                TextureName = rawName,
                OutputDirectory = fullOutputDir,
                ManifestPath = manifestPath,
                TotalMipLevels = mipChain.Count,
                ExportedMipLevels = exportedCount,
                SkippedMipLevels = skippedCount,
                ExportedFilePaths = exportedFiles,
                MipLevels = mipChain
            };
        }

        internal static string SanitizeFileNameStrict(string? rawName, string fallback)
        {
            string fixedName = AssetExportHelper.FixFileName(rawName ?? string.Empty);
            fixedName = fixedName
                .Replace('/', '_')
                .Replace('\\', '_')
                .Replace("..", "_")
                .Trim()
                .TrimEnd('.');

            if (string.IsNullOrWhiteSpace(fixedName) || fixedName == "." || fixedName == "..")
            {
                return fallback;
            }

            return fixedName;
        }

        internal static string CombineConfinedPath(string fullRootDirectory, string safeFileName)
        {
            string candidate = Path.GetFullPath(Path.Combine(fullRootDirectory, safeFileName));
            string normalizedRoot = fullRootDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!candidate.StartsWith(normalizedRoot, comparison))
            {
                throw new InvalidOperationException($"Refusing to write outside output directory: '{safeFileName}'.");
            }

            return candidate;
        }

        private static int ComputeMaxMipLevels(int width, int height)
        {
            int maxDim = Math.Max(width, height);
            int levels = 1;
            while (maxDim > 1)
            {
                maxDim >>= 1;
                levels++;
            }
            return levels;
        }

        private sealed class MipChainManifestDocument
        {
            public string TextureName { get; init; } = string.Empty;
            public long PathId { get; init; }
            public string SourceFile { get; init; } = string.Empty;
            public string Format { get; init; } = string.Empty;
            public bool IsHdr { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public int DeclaredMipCount { get; init; }
            public int ExportedMipCount { get; init; }
            public string ImageFormat { get; init; } = string.Empty;
            public List<MipManifestEntry> Levels { get; init; } = new();
        }

        private sealed class MipManifestEntry
        {
            public int Level { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public long ByteOffset { get; init; }
            public long ByteSize { get; init; }
            public bool IsAvailable { get; init; }
            public string? ExportedFile { get; init; }
        }
    }
}
