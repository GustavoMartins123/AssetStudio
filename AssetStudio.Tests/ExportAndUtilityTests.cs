using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using UkooLabs.FbxSharpie;
using UkooLabs.FbxSharpie.Tokens;
using UkooLabs.FbxSharpie.Tokens.Value;
using UkooLabs.FbxSharpie.Tokens.ValueArray;
using Xunit;

namespace AssetStudio.Tests
{
    public class ExportAndUtilityTests
    {
        private static readonly MethodInfo YamlStringMethod =
            typeof(AssetExportHelper).GetMethod("YamlString", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AssetExportHelper.YamlString method not found.");

        private static readonly MethodInfo GetMinimumDataSizeMethod =
            typeof(Texture2DConverter).GetMethod("GetMinimumDataSize", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Texture2DConverter.GetMinimumDataSize method not found.");

        [Theory]
        [InlineData(null, "unnamed")]
        [InlineData("", "unnamed")]
        [InlineData("   ", "unnamed")]
        [InlineData(".", "unnamed")]
        public void FixFileName_ReturnsUnnamedForEmptyOrSingleDotInputs_RegressionSEC02(string? input, string expected)
        {
            Assert.Equal(expected, AssetExportHelper.FixFileName(input!));
        }

        [Theory]
        [InlineData("..")]
        [InlineData("...")]
        [InlineData("../../evil")]
        [InlineData("..\\..\\Windows\\System32\\cmd.exe")]
        [InlineData("a\\b/c")]
        [InlineData("bad:name*with?chars<|>\"")]
        public void FixFileName_NeutralizesPathTraversalAndSeparators_RegressionSEC02(string input)
        {
            var sanitized = AssetExportHelper.FixFileName(input);

            Assert.False(string.IsNullOrWhiteSpace(sanitized));
            Assert.DoesNotContain("..", sanitized);
            Assert.DoesNotContain("/", sanitized);
            Assert.DoesNotContain("\\", sanitized);
            Assert.DoesNotContain(":", sanitized);
            Assert.DoesNotContain("*", sanitized);
            Assert.DoesNotContain("?", sanitized);
            Assert.DoesNotContain("<", sanitized);
            Assert.DoesNotContain(">", sanitized);
            Assert.DoesNotContain("|", sanitized);
            Assert.DoesNotContain("\"", sanitized);
        }

        [Theory]
        [InlineData("CON", "_CON")]
        [InlineData("PRN", "_PRN")]
        [InlineData("AUX", "_AUX")]
        [InlineData("NUL", "_NUL")]
        [InlineData("COM1", "_COM1")]
        [InlineData("LPT9", "_LPT9")]
        [InlineData("CON.txt", "_CON.txt")]
        [InlineData("nul.png", "_nul.png")]
        public void FixFileName_PrefixesWindowsReservedDeviceNames_RegressionSEC02(string input, string expected)
        {
            Assert.Equal(expected, AssetExportHelper.FixFileName(input));
        }

        [Fact]
        public void FixFileName_TruncatesOverlyLongNamesPreservingExtension()
        {
            var longName = new string('x', 250) + ".png";
            var sanitized = AssetExportHelper.FixFileName(longName);

            Assert.True(sanitized.Length <= 180);
            Assert.EndsWith(".png", sanitized, StringComparison.Ordinal);
        }

        [Fact]
        public void YamlString_EscapesControlAndQuoteCharactersToPreventInjection_RegressionSEC11()
        {
            Assert.Equal(string.Empty, InvokeYamlString(null!));
            Assert.Equal(string.Empty, InvokeYamlString(string.Empty));
            Assert.Equal("StandardMaterial", InvokeYamlString("StandardMaterial"));

            // Attempted YAML key injection via CR/LF, tab, backslash, and quotes
            var payload = "Evil\r\n  m_Shader: {fileID: 999}\t\"quoted\"\\slash";
            var escaped = InvokeYamlString(payload);

            Assert.StartsWith("\"", escaped);
            Assert.EndsWith("\"", escaped);
            Assert.DoesNotContain("\r", escaped);
            Assert.DoesNotContain("\n", escaped);
            Assert.DoesNotContain("\t", escaped);
            Assert.Contains("\\r\\n", escaped);
            Assert.Contains("\\t", escaped);
            Assert.Contains("\\\"quoted\\\"", escaped);
            Assert.Contains("\\\\slash", escaped);
            Assert.Equal("\"Evil\\r\\n  m_Shader: {fileID: 999}\\t\\\"quoted\\\"\\\\slash\"", escaped);
        }

        [Fact]
        public void AutodeskBinaryWriter_TruncatesOver255ByteNodeNamesWithoutCorruptingFraming_RegressionREL09()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"as_test_fbx_longname_{Guid.NewGuid():N}.fbx");
            try
            {
                var doc = new FbxDocument { Version = FbxVersion.v7_4 };

                var longName = new string('A', 300);
                var longNode = new FbxNode(new IdentifierToken(longName));
                longNode.AddProperty(new IntegerToken(42));
                doc.AddNode(longNode);

                var siblingNode = new FbxNode(new IdentifierToken("SiblingNode"));
                siblingNode.AddProperty(new StringToken("ok"));
                doc.AddNode(siblingNode);

                AutodeskBinaryWriter.Write(doc, tempPath);

                using var fs = File.OpenRead(tempPath);
                using var br = new BinaryReader(fs, Encoding.UTF8);

                // Header: 23-byte magic + 4-byte version 7400
                var magic = Encoding.ASCII.GetString(br.ReadBytes(23));
                Assert.Equal("Kaydara FBX Binary  \0\u001a\0", magic);
                Assert.Equal(7400, br.ReadInt32());

                // First node framing at offset 27
                uint firstEndOffset = br.ReadUInt32();
                uint firstPropCount = br.ReadUInt32();
                uint firstPropLen = br.ReadUInt32();
                byte firstNameLen = br.ReadByte();
                string firstName = Encoding.UTF8.GetString(br.ReadBytes(firstNameLen));

                Assert.Equal(1u, firstPropCount);
                Assert.Equal(5u, firstPropLen); // 'I' (1) + int32 (4)
                Assert.Equal((byte)255, firstNameLen);
                Assert.Equal(new string('A', 255), firstName);
                Assert.Equal((byte)'I', br.ReadByte());
                Assert.Equal(42, br.ReadInt32());
                Assert.Equal(firstEndOffset, (uint)fs.Position);

                // Second sibling node immediately follows at firstEndOffset
                uint secondEndOffset = br.ReadUInt32();
                uint secondPropCount = br.ReadUInt32();
                uint secondPropLen = br.ReadUInt32();
                byte secondNameLen = br.ReadByte();
                string secondName = Encoding.UTF8.GetString(br.ReadBytes(secondNameLen));

                Assert.Equal("SiblingNode", secondName);
                Assert.Equal(1u, secondPropCount);
                Assert.Equal((byte)'S', br.ReadByte());
                int strLen = br.ReadInt32();
                Assert.Equal("ok", Encoding.UTF8.GetString(br.ReadBytes(strLen)));
                Assert.Equal(secondEndOffset, (uint)fs.Position);

                // Top-level 13-byte null sentinel follows sibling node
                var sentinel = br.ReadBytes(13);
                Assert.All(sentinel, b => Assert.Equal((byte)0, b));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        [Fact]
        public void AutodeskBinaryWriter_WritesValidZlibCompressedAndUncompressedArrays_RegressionPERF07()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"as_test_fbx_zlib_{Guid.NewGuid():N}.fbx");
            try
            {
                var doc = new FbxDocument { Version = FbxVersion.v7_4 };

                // 256 doubles = 2048 bytes (>= 1024 threshold -> zlib compressed, encoding = 1)
                var largeDoubles = new double[256];
                for (int i = 0; i < largeDoubles.Length; i++)
                {
                    largeDoubles[i] = i * 1.25 - 50.0;
                }

                // 4 ints = 16 bytes (< 1024 threshold -> uncompressed, encoding = 0)
                var smallInts = new[] { 10, -20, 300, -4000 };

                var arrayNode = new FbxNode(new IdentifierToken("Vertices"));
                arrayNode.AddProperty(new DoubleArrayToken(largeDoubles));
                arrayNode.AddProperty(new IntegerArrayToken(smallInts));
                doc.AddNode(arrayNode);

                AutodeskBinaryWriter.Write(doc, tempPath);

                using var fs = File.OpenRead(tempPath);
                using var br = new BinaryReader(fs, Encoding.UTF8);

                // Skip 27-byte FBX header + node header (12 bytes + 1 byte len + "Vertices")
                fs.Position = 27;
                uint endOffset = br.ReadUInt32();
                uint propCount = br.ReadUInt32();
                uint propLen = br.ReadUInt32();
                byte nameLen = br.ReadByte();
                Assert.Equal("Vertices", Encoding.UTF8.GetString(br.ReadBytes(nameLen)));
                Assert.Equal(2u, propCount);

                long propStart = fs.Position;

                // Property 1: zlib-compressed double array
                Assert.Equal((byte)'d', br.ReadByte());
                Assert.Equal(largeDoubles.Length, br.ReadInt32());
                Assert.Equal(1, br.ReadInt32()); // encoding = 1 (zlib compressed)
                int compressedLen = br.ReadInt32();
                Assert.True(compressedLen > 6);
                var zlibBytes = br.ReadBytes(compressedLen);

                // Verify RFC 1950 zlib header (CMF=0x78, FLG=0x01, header % 31 == 0)
                Assert.Equal(0x78, zlibBytes[0]);
                Assert.Equal(0x01, zlibBytes[1]);
                Assert.Equal(0, ((zlibBytes[0] << 8) | zlibBytes[1]) % 31);

                // ZLibStream validates both RFC 1950 header and trailing Big-Endian Adler-32 checksum
                byte[] decompressed;
                using (var compMs = new MemoryStream(zlibBytes))
                using (var zlib = new ZLibStream(compMs, CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    zlib.CopyTo(outMs);
                    decompressed = outMs.ToArray();
                }

                Assert.Equal(largeDoubles.Length * sizeof(double), decompressed.Length);
                for (int i = 0; i < largeDoubles.Length; i++)
                {
                    Assert.Equal(largeDoubles[i], BitConverter.ToDouble(decompressed, i * sizeof(double)));
                }

                // Explicitly verify trailing Big-Endian Adler-32 checksum matches raw payload
                uint expectedAdler = ComputeAdler32(decompressed);
                uint actualAdler = ((uint)zlibBytes[^4] << 24)
                    | ((uint)zlibBytes[^3] << 16)
                    | ((uint)zlibBytes[^2] << 8)
                    | zlibBytes[^1];
                Assert.Equal(expectedAdler, actualAdler);

                // Property 2: uncompressed int array
                Assert.Equal((byte)'i', br.ReadByte());
                Assert.Equal(smallInts.Length, br.ReadInt32());
                Assert.Equal(0, br.ReadInt32()); // encoding = 0 (uncompressed)
                Assert.Equal(smallInts.Length * sizeof(int), br.ReadInt32());
                for (int i = 0; i < smallInts.Length; i++)
                {
                    Assert.Equal(smallInts[i], br.ReadInt32());
                }

                Assert.Equal(propLen, (uint)(fs.Position - propStart));
                Assert.Equal(endOffset, (uint)fs.Position);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        [Theory]
        [InlineData(TextureFormat.PVRTC_RGB2, 4, 4, 8L)]
        [InlineData(TextureFormat.PVRTC_RGB2, 8, 4, 8L)]
        [InlineData(TextureFormat.PVRTC_RGB2, 16, 8, 32L)]
        [InlineData(TextureFormat.PVRTC_RGBA2, 4, 4, 8L)]
        [InlineData(TextureFormat.PVRTC_RGBA4, 4, 4, 8L)]
        [InlineData(TextureFormat.PVRTC_RGBA4, 8, 8, 32L)]
        [InlineData(TextureFormat.BGR24, 4, 4, 48L)]
        [InlineData(TextureFormat.RGBFloat, 4, 4, 192L)]
        [InlineData(TextureFormat.ARGBFloat, 4, 4, 256L)]
        [InlineData(TextureFormat.DXT1, 4, 4, 8L)]
        [InlineData(TextureFormat.DXT1, 8, 8, 32L)]
        [InlineData(TextureFormat.DXT5, 4, 4, 16L)]
        [InlineData(TextureFormat.DXT5, 8, 8, 64L)]
        [InlineData(TextureFormat.ASTC_RGB_4x4, 4, 4, 16L)]
        [InlineData(TextureFormat.ASTC_RGB_4x4, 5, 5, 64L)]
        [InlineData(TextureFormat.ASTC_RGB_12x12, 12, 12, 16L)]
        [InlineData(TextureFormat.DXT1, 0, 4, 0L)]
        [InlineData(TextureFormat.DXT1, 4, -1, 0L)]
        public void Texture2DConverter_GetMinimumDataSize_ReturnsExactByteCounts_RegressionSEC08(
            TextureFormat format,
            int width,
            int height,
            long expectedBytes)
        {
            long actual = (long)GetMinimumDataSizeMethod.Invoke(null, new object[] { format, width, height })!;
            Assert.Equal(expectedBytes, actual);
        }

        private static string InvokeYamlString(string value)
        {
            return (string)YamlStringMethod.Invoke(null, new object?[] { value })!;
        }

        private static uint ComputeAdler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
