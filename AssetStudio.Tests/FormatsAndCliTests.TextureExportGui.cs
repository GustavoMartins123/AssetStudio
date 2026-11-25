using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed partial class FormatsAndCliTests
    {
        [Fact]
        public void ExportOptionsViewModel_WebpQualityAndLossless_RoundTripClampAndVisibility()
        {
            var state = new ExportOptionsState
            {
                ConvertTextureFormat = ImageFormat.Png,
                WebpQuality = 40,
                WebpLossless = true
            };

            var vm = new ExportOptionsViewModel(state);
            Assert.Equal(40, vm.WebpQuality);
            Assert.True(vm.WebpLossless);
            Assert.False(vm.IsWebpFormatSelected);

            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.ConvertTextureFormat = ImageFormat.Webp;
            Assert.True(vm.IsWebpFormatSelected);
            Assert.Contains(nameof(ExportOptionsViewModel.IsWebpFormatSelected), changed);

            vm.WebpQuality = 150;
            Assert.Equal(100, vm.WebpQuality);
            vm.WebpQuality = -5;
            Assert.Equal(0, vm.WebpQuality);
            vm.WebpQuality = 75;
            vm.WebpLossless = false;

            var target = new ExportOptionsState();
            vm.ApplyTo(target);
            Assert.Equal(ImageFormat.Webp, target.ConvertTextureFormat);
            Assert.Equal(75, target.WebpQuality);
            Assert.False(target.WebpLossless);

            var copy = new ExportOptionsState();
            copy.CopyFrom(new ExportOptionsState { WebpQuality = 12, WebpLossless = true });
            Assert.Equal(12, copy.WebpQuality);
            Assert.True(copy.WebpLossless);

            vm.ResetDefaults();
            Assert.Equal(90, vm.WebpQuality);
            Assert.False(vm.WebpLossless);
        }

        [Fact]
        public void ImageExtensions_WebpLosslessKeepsExactPixels_AndQualityChangesLossySize()
        {
            const int size = 64;
            using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            var random = new Random(1234);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var alpha = (byte)(x * 4 + 1);
                    bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), alpha));
                }
            }

            using (var lossless = new MemoryStream())
            {
                bitmap.WriteToStream(lossless, ImageFormat.Webp, webpQuality: 100, webpLossless: true);
                lossless.Position = 0;
                using var decoded = SKBitmap.Decode(lossless, new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                Assert.NotNull(decoded);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        Assert.Equal(bitmap.GetPixel(x, y), decoded.GetPixel(x, y));
                    }
                }
            }

            long EncodedLength(int quality)
            {
                using var ms = new MemoryStream();
                bitmap.WriteToStream(ms, ImageFormat.Webp, quality, webpLossless: false);
                return ms.Length;
            }

            Assert.True(EncodedLength(5) < EncodedLength(100));

            using var png = new MemoryStream();
            bitmap.WriteToStream(png, ImageFormat.Png, webpQuality: 5, webpLossless: false);
            png.Position = 0;
            Assert.Equal(0x89, png.ReadByte());
        }

        [Theory]
        [InlineData(ImageFormat.Webp, ".webp")]
        [InlineData(ImageFormat.Png, ".png")]
        public void AssetExportService_Texture2DArray_ExportsEverySliceAsItsOwnImage(ImageFormat format, string extension)
        {
            const int width = 4;
            const int height = 4;
            var payload = CreateSolidRgbaPixels(width, height, 255, 20, 30, 255)
                .Concat(CreateSolidRgbaPixels(width, height, 40, 230, 50, 128))
                .Concat(CreateSolidRgbaPixels(width, height, 60, 70, 240, 255))
                .ToArray();
            var objectBytes = BuildTexture2DArrayBinary(
                name: "GuiArray",
                unityVersion: "2021.3.16f1",
                formatValue: 8,
                width: width,
                height: height,
                depth: 3,
                mipCount: 1,
                dataSize: (uint)payload.Length,
                imageData: payload);

            var sf = CreateSerializedFileWithSingleObject("gui_array.assets", "2021.3.16f1", pathId: 301, ClassIDType.Texture2DArray, objectBytes);
            var exportDir = CreateTempDirectory("Texture2DArrayGuiExport");
            try
            {
                var textureArray = Assert.IsType<Texture2DArray>(Assert.Single(sf.Objects));
                var item = new AssetItem(textureArray) { Name = textureArray.m_Name };
                var context = new ExportConversionContext(new List<SerializedFile> { sf });
                var options = new ExportOptionsState { ConvertTextureFormat = format, WebpLossless = true, WebpQuality = 100 };

                Assert.True(AssetExportService.ExportStandardConvertedAsset(item, exportDir, context, options));

                var expected = new (byte R, byte G, byte B, byte A)[] { (255, 20, 30, 255), (40, 230, 50, 128), (60, 70, 240, 255) };
                for (var slice = 0; slice < expected.Length; slice++)
                {
                    var path = Path.Combine(exportDir, $"GuiArray_slice{slice:D3}{extension}");
                    Assert.True(File.Exists(path), $"missing {path}");
                    using var decoded = SKBitmap.Decode(path);
                    Assert.NotNull(decoded);
                    Assert.Equal(width, decoded.Width);
                    Assert.Equal(height, decoded.Height);
                    var pixel = decoded.GetPixel(1, 1);
                    Assert.Equal(expected[slice].A, pixel.Alpha);
                    if (expected[slice].A == 255)
                    {
                        Assert.Equal(expected[slice].R, pixel.Red);
                        Assert.Equal(expected[slice].G, pixel.Green);
                        Assert.Equal(expected[slice].B, pixel.Blue);
                    }
                }

                Assert.Equal(expected.Length, Directory.GetFiles(exportDir).Length);

                var rawDir = Path.Combine(exportDir, "raw");
                var rawItem = new AssetItem(textureArray) { Name = textureArray.m_Name };
                var rawContext = new ExportConversionContext(new List<SerializedFile> { sf });
                Assert.True(AssetExportService.ExportStandardConvertedAsset(rawItem, rawDir, rawContext, new ExportOptionsState { ConvertTexture = false }));
                Assert.Equal(payload, File.ReadAllBytes(Path.Combine(rawDir, "GuiArray.tex")));
            }
            finally
            {
                try
                {
                    Directory.Delete(exportDir, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void TextureArraySliceViewModel_ClampsSlices_DrivesCommands_AndReportsOnlyUserChanges()
        {
            var vm = new TextureArraySliceViewModel();
            var reported = new List<int>();
            vm.SliceChanged += reported.Add;
            Assert.False(vm.IsVisible);

            vm.Show(3);
            Assert.True(vm.IsVisible);
            Assert.Equal(2, vm.MaxSliceIndex);
            Assert.True(vm.HasMultipleSlices);
            Assert.Equal("Slice 1 / 3", vm.SliceText);
            Assert.False(vm.PreviousSliceCommand.CanExecute());
            Assert.True(vm.NextSliceCommand.CanExecute());
            Assert.Empty(reported);

            vm.NextSliceCommand.Execute();
            vm.NextSliceCommand.Execute();
            Assert.Equal(2, vm.SliceIndex);
            Assert.False(vm.NextSliceCommand.CanExecute());
            vm.SliceIndex = 99;
            Assert.Equal(2, vm.SliceIndex);
            vm.PreviousSliceCommand.Execute();
            Assert.Equal("Slice 2 / 3", vm.SliceText);
            Assert.Equal(new[] { 1, 2, 1 }, reported);

            vm.Show(1);
            Assert.Equal(0, vm.SliceIndex);
            Assert.False(vm.HasMultipleSlices);
            Assert.False(vm.NextSliceCommand.CanExecute());
            vm.Hide();
            Assert.False(vm.IsVisible);
            Assert.Equal(new[] { 1, 2, 1 }, reported);
        }

        [Fact]
        public void ProjectLoadingService_ListsTexture2DArrayAsExportable()
        {
            Assert.True(ProjectLoadingService.IsLazyExportableType(ClassIDType.Texture2DArray));
            Assert.True(ProjectLoadingService.IsLazyExportableType(ClassIDType.Texture2D));
        }
    }
}
