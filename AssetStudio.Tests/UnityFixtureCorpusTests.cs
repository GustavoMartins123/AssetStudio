using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AssetStudio;
using Xunit;

namespace AssetStudio.Tests
{
    public class UnityFixtureCorpusTests
    {
        [Fact]
        public void Manifest_ListsEveryCorpusFile_WithMatchingSizeAndSha256()
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(UnityFixtureCorpus.File("manifest.json")));
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var relativePath = entry.GetProperty("path").GetString()!;
                var bytes = File.ReadAllBytes(UnityFixtureCorpus.File(relativePath));
                Assert.Equal(entry.GetProperty("size").GetInt64(), bytes.LongLength);
                Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("unity").GetString()), relativePath);
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("build").GetString()), relativePath);
                Assert.True(listed.Add(relativePath), $"Listed twice: {relativePath}");
            }

            var unlisted = Directory.EnumerateFiles(UnityFixtureCorpus.Root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(UnityFixtureCorpus.Root, p).Replace('\\', '/'))
                .Where(p => p is not ("manifest.json" or "FONT-OFL.txt") && !listed.Contains(p))
                .ToList();
            Assert.Empty(unlisted);
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", File.ReadAllText(UnityFixtureCorpus.File("FONT-OFL.txt")), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("6000.5/fixture/StandaloneWindows64/lz4/fixture", null)]
        [InlineData("6000.5/fixture/StandaloneWindows64/lzma/fixture", null)]
        [InlineData("6000.5/fixture/StandaloneWindows64/raw-notypetree/fixture", null)]
        [InlineData("6000.5/fixture/StandaloneWindows64/extracted/fixture", "6000.5/fixture/StandaloneWindows64/extracted/typetrees.bin")]
        [InlineData("6000.5/fixture/Android/lz4/fixture", null)]
        [InlineData("6000.3/fixture/StandaloneWindows64/lz4/fixture", null)]
        public void EmbeddedFont_IsTheFreelyLicensedFiraCode(string bundle, string? typeTreeArchive)
        {
            var files = typeTreeArchive == null
                ? new[] { UnityFixtureCorpus.File(bundle) }
                : new[] { UnityFixtureCorpus.File(bundle), UnityFixtureCorpus.File(typeTreeArchive) };
            using var manager = new AssetsManager();
            manager.LoadFiles(files);

            var font = Assert.Single(manager.assetsFileList.SelectMany(f => f.Objects).OfType<Font>());
            Assert.Equal(File.ReadAllBytes(UnityFixtureCorpus.FiraCodeFont), font.m_FontData);
        }
    }
}
