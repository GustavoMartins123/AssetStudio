using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using Xunit;

namespace AssetStudio.Tests
{
    public class BuildDiffContentVerificationTests
    {
        [Fact]
        public void FullContent_SameMetadataDifferentBytes_IsModified_WhileIdenticalAssetsAreVerified()
        {
            using var dirs = new TempBuilds();
            var baseline = dirs.WriteBaseline("level0", (1, "Alpha", "abc"), (2, "Beta", "xyz"), (3, "Gamma", "123"));
            var target = dirs.WriteTarget("level0", (1, "Alpha", "abc"), (2, "Beta", "xyw"), (3, "Delta", "123"));

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            Assert.Equal(BuildDiffContentCheck.FullContent, report.ContentCheck);
            var alpha = Entry(report, 1);
            Assert.Equal(BuildDiffChangeKind.Unchanged, alpha.ChangeKind);
            Assert.True(alpha.ContentVerified);
            Assert.Equal("Content identical", alpha.Detail);

            var beta = Entry(report, 2);
            Assert.Equal(BuildDiffChangeKind.Modified, beta.ChangeKind);
            Assert.True(beta.ContentVerified);
            Assert.Contains("Content changed", beta.Detail, StringComparison.Ordinal);

            var renamed = Entry(report, 3);
            Assert.Equal(BuildDiffChangeKind.Modified, renamed.ChangeKind);
            Assert.Contains("Name: 'Gamma' -> 'Delta'", renamed.Detail, StringComparison.Ordinal);

            Assert.Equal(1, report.UnchangedAssets);
            Assert.Equal(2, report.ModifiedAssets);
            Assert.Equal(2, report.ContentVerifiedAssets);
            Assert.Equal(0, report.ContentUnverifiedAssets);
            Assert.Contains("Content verified", report.ContentScopeNote, StringComparison.Ordinal);
        }

        [Fact]
        public void MetadataOnly_SameMetadataDifferentBytes_IsReportedAsNotVerified()
        {
            using var dirs = new TempBuilds();
            var baseline = dirs.WriteBaseline("level0", (1, "Alpha", "abc"), (2, "Beta", "xyz"));
            var target = dirs.WriteTarget("level0", (1, "Alpha", "abc"), (2, "Beta", "xyw"));

            var report = BuildDiffService.CompareAssetSnapshots(dirs.BaselineDir, baseline, dirs.TargetDir, target);

            Assert.Equal(BuildDiffContentCheck.MetadataOnly, report.ContentCheck);
            var beta = Entry(report, 2);
            Assert.Equal(BuildDiffChangeKind.Unchanged, beta.ChangeKind);
            Assert.False(beta.ContentVerified);
            Assert.Equal("Metadata unchanged (content not verified)", beta.Detail);
            Assert.Equal(2, report.ContentUnverifiedAssets);
            Assert.Equal(0, report.ContentVerifiedAssets);
            Assert.Contains("Content not verified", report.ContentScopeNote, StringComparison.Ordinal);
            Assert.Contains("Content check**: MetadataOnly", BuildDiffService.FormatSummaryMarkdown(report), StringComparison.Ordinal);
        }

        [Fact]
        public void FullContent_IdenticalFiles_AreVerifiedIdentical()
        {
            using var dirs = new TempBuilds();
            var baseline = dirs.WriteBaseline("sharedassets0.assets", (1, "Alpha", "abc"), (2, "Beta", "xyz"));
            var target = dirs.WriteTarget("sharedassets0.assets", (1, "Alpha", "abc"), (2, "Beta", "xyz"));

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            Assert.All(report.AssetEntries, e =>
            {
                Assert.Equal(BuildDiffChangeKind.Unchanged, e.ChangeKind);
                Assert.True(e.ContentVerified);
            });
            Assert.Equal(2, report.ContentVerifiedAssets);
        }

        [Fact]
        public void FullContent_ChangedStreamedCompanion_StillComparesTheAssets()
        {
            using var dirs = new TempBuilds();
            var baseline = dirs.WriteBaseline("level0", (1, "Alpha", "abc"));
            var target = dirs.WriteTarget("level0", (1, "Alpha", "abc"));
            File.WriteAllBytes(Path.Combine(dirs.BaselineDir, "level0.resS"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(dirs.TargetDir, "level0.resS"), new byte[] { 1, 2, 9, 4 });

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            var alpha = Entry(report, 1);
            Assert.Equal(BuildDiffChangeKind.Unchanged, alpha.ChangeKind);
            Assert.True(alpha.ContentVerified);
        }

        [Fact]
        public void FullContent_MovedAsset_ReportsWhetherItsBytesChanged()
        {
            using var dirs = new TempBuilds();
            var baseline = dirs.WriteBaseline("level1", (7, "Shared", "data"), (8, "Other", "same"))
                .ToList();
            var target = dirs.WriteTarget("level2", (7, "Shared", "data"), (8, "Other", "diff"))
                .ToList();

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            var shared = Entry(report, 7);
            Assert.Equal(BuildDiffChangeKind.Moved, shared.ChangeKind);
            Assert.EndsWith("content identical", shared.Detail, StringComparison.Ordinal);
            Assert.True(shared.ContentVerified);

            var other = Entry(report, 8);
            Assert.Equal(BuildDiffChangeKind.Moved, other.ChangeKind);
            Assert.EndsWith("content changed", other.Detail, StringComparison.Ordinal);
            Assert.Equal(2, report.MovedAssets);
        }

        [Fact]
        public void FullContent_MissingSourceFile_IsReportedAsNotVerified()
        {
            using var dirs = new TempBuilds();
            var baseline = dirs.WriteBaseline("level0", (1, "Alpha", "abc"));
            var target = dirs.WriteTarget("level0", (1, "Alpha", "abc"));
            File.Delete(baseline[0].OriginalPath);

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            var alpha = Entry(report, 1);
            Assert.Equal(BuildDiffChangeKind.Unchanged, alpha.ChangeKind);
            Assert.False(alpha.ContentVerified);
            Assert.Contains("content not verified: source file is no longer on disk", alpha.Detail, StringComparison.Ordinal);
            Assert.Equal(1, report.ContentUnverifiedAssets);
        }

        [Fact]
        public void FileLevel_SameSizeTimestampAndSampleHash_OnlyFullContentDetectsTheChange()
        {
            using var dirs = new TempBuilds();
            var payload = new byte[64 * 1024];
            new Random(1234).NextBytes(payload);
            var baselinePath = Path.Combine(dirs.BaselineDir, "data.bundle");
            var targetPath = Path.Combine(dirs.TargetDir, "data.bundle");
            File.WriteAllBytes(baselinePath, payload);
            payload[20_000] ^= 0xFF;
            File.WriteAllBytes(targetPath, payload);
            var untouchedBaseline = Path.Combine(dirs.BaselineDir, "same.bundle");
            var untouchedTarget = Path.Combine(dirs.TargetDir, "same.bundle");
            File.WriteAllBytes(untouchedBaseline, payload);
            File.WriteAllBytes(untouchedTarget, payload);

            const long ticks = 638000000000000000L;
            const ulong sampleHash = 0x1234_5678_9ABC_DEF0UL;
            ScannedFileInfo[] Files(string changed, string same) => new[]
            {
                new ScannedFileInfo(changed, payload.Length, ProjectFileKind.UnityBundle, ticks, sampleHash),
                new ScannedFileInfo(same, payload.Length, ProjectFileKind.UnityBundle, ticks, sampleHash)
            };

            var metadataOnly = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, Array.Empty<BuildDiffAssetSnapshot>(), dirs.TargetDir, Array.Empty<BuildDiffAssetSnapshot>(),
                Files(baselinePath, untouchedBaseline), Files(targetPath, untouchedTarget), BuildDiffContentCheck.MetadataOnly, CancellationToken.None);
            Assert.Equal(0, metadataOnly.ModifiedFiles);
            Assert.All(metadataOnly.FileEntries, f => Assert.Contains("full content not verified", f.Detail, StringComparison.Ordinal));

            var fullContent = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, Array.Empty<BuildDiffAssetSnapshot>(), dirs.TargetDir, Array.Empty<BuildDiffAssetSnapshot>(),
                Files(baselinePath, untouchedBaseline), Files(targetPath, untouchedTarget), BuildDiffContentCheck.FullContent, CancellationToken.None);
            Assert.Equal(1, fullContent.ModifiedFiles);
            Assert.Equal(1, fullContent.UnchangedFiles);
            var changed = Assert.Single(fullContent.FileEntries, f => f.ChangeKind == BuildDiffChangeKind.Modified);
            Assert.Equal("data.bundle", changed.RelativePath);
            Assert.Contains("Content changed (same size and sampled hash)", changed.Detail, StringComparison.Ordinal);
            var same = Assert.Single(fullContent.FileEntries, f => f.ChangeKind == BuildDiffChangeKind.Unchanged);
            Assert.Equal("Identical bytes", same.Detail);
        }

        [Fact]
        public void FullContent_RealPlayerBuild_TextureChangedOnlyInResS_IsModified()
        {
            using var dirs = new TempBuilds();
            foreach (var name in new[] { "sharedassets0.assets", "sharedassets0.assets.resS", "sharedassets0.resource" })
            {
                var source = UnityFixtureCorpus.File("6000.5/player-mono/" + name);
                File.Copy(source, Path.Combine(dirs.BaselineDir, name));
                File.Copy(source, Path.Combine(dirs.TargetDir, name));
            }

            var baseline = LoadSnapshots(Path.Combine(dirs.BaselineDir, "sharedassets0.assets"), out var streamedTexture);
            var target = LoadSnapshots(Path.Combine(dirs.TargetDir, "sharedassets0.assets"), out _);
            Assert.NotNull(streamedTexture);

            var resS = Path.Combine(dirs.TargetDir, "sharedassets0.assets.resS");
            var bytes = File.ReadAllBytes(resS);
            bytes[streamedTexture!.Value.Offset + streamedTexture.Value.Size / 2] ^= 0xFF;
            File.WriteAllBytes(resS, bytes);

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            var modified = Assert.Single(report.AssetEntries, e => e.ChangeKind == BuildDiffChangeKind.Modified);
            Assert.Equal(streamedTexture.Value.PathId, modified.PathId);
            Assert.Contains("Content changed", modified.Detail, StringComparison.Ordinal);
            Assert.Equal(report.AssetEntries.Count - 1, report.UnchangedAssets);
            Assert.Equal(report.AssetEntries.Count, report.ContentVerifiedAssets);

            var metadataOnly = BuildDiffService.CompareAssetSnapshots(dirs.BaselineDir, baseline, dirs.TargetDir, target);
            Assert.Equal(0, metadataOnly.ModifiedAssets);
        }

        // The same Unity 6000.5 bundle built with LZ4 and with LZMA: the files differ byte for byte, the assets do not.
        [Fact]
        public void FullContent_RealBundles_SameContentDifferentCompression_AllAssetsIdentical()
        {
            var lz4 = UnityFixtureCorpus.File("6000.5/fixture/StandaloneWindows64/lz4/fixture");
            var lzma = UnityFixtureCorpus.File("6000.5/fixture/StandaloneWindows64/lzma/fixture");

            using var dirs = new TempBuilds();
            File.Copy(lz4, Path.Combine(dirs.BaselineDir, "fixture"));
            File.Copy(lzma, Path.Combine(dirs.TargetDir, "fixture"));
            var baseline = LoadSnapshots(Path.Combine(dirs.BaselineDir, "fixture"), out _);
            var target = LoadSnapshots(Path.Combine(dirs.TargetDir, "fixture"), out _);
            Assert.NotEmpty(baseline);

            var report = BuildDiffService.CompareAssetSnapshots(
                dirs.BaselineDir, baseline, dirs.TargetDir, target, null, null, BuildDiffContentCheck.FullContent, CancellationToken.None);

            Assert.Equal(baseline.Count, report.UnchangedAssets);
            Assert.Equal(baseline.Count, report.ContentVerifiedAssets);
            Assert.All(report.AssetEntries, e => Assert.Equal("Content identical", e.Detail));
        }

        private static List<BuildDiffAssetSnapshot> LoadSnapshots(string assetsPath, out (long PathId, int Offset, int Size)? streamedTexture)
        {
            streamedTexture = null;
            using var manager = new AssetsManager();
            manager.LoadFiles(assetsPath);
            var file = Assert.Single(manager.assetsFileList);
            var result = new List<BuildDiffAssetSnapshot>();
            foreach (var obj in file.Objects)
            {
                var info = file.ObjectInfoDic[obj.m_PathID];
                var name = obj is NamedObject named ? named.m_Name : string.Empty;
                result.Add(new BuildDiffAssetSnapshot(
                    $"{file.fileName}#{obj.m_PathID}", file.fileName, assetsPath, obj.m_PathID, name, obj.type, string.Empty, info.byteStart, info.byteSize));
                if (streamedTexture == null && obj is Texture2D texture && texture.m_StreamData is { size: > 16 } stream)
                {
                    streamedTexture = (obj.m_PathID, checked((int)stream.offset), checked((int)stream.size));
                }
            }

            return result;
        }

        private static BuildDiffAssetEntry Entry(BuildDiffReport report, long pathId)
        {
            return Assert.Single(report.AssetEntries, e => e.PathId == pathId);
        }

        private sealed class TempBuilds : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "AssetStudio_BuildDiffContent_" + Guid.NewGuid().ToString("N"));

            public TempBuilds()
            {
                Directory.CreateDirectory(BaselineDir);
                Directory.CreateDirectory(TargetDir);
            }

            public string BaselineDir => Path.Combine(_root, "baseline");
            public string TargetDir => Path.Combine(_root, "target");

            public IReadOnlyList<BuildDiffAssetSnapshot> WriteBaseline(string fileName, params (long PathId, string Name, string Script)[] assets)
                => Write(BaselineDir, fileName, assets);

            public IReadOnlyList<BuildDiffAssetSnapshot> WriteTarget(string fileName, params (long PathId, string Name, string Script)[] assets)
                => Write(TargetDir, fileName, assets);

            public void Dispose()
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            private static IReadOnlyList<BuildDiffAssetSnapshot> Write(string dir, string fileName, (long PathId, string Name, string Script)[] assets)
            {
                var path = Path.Combine(dir, fileName);
                var bytes = SyntheticSerializedFile.BuildTextAssets(assets, out var objects);
                File.WriteAllBytes(path, bytes);
                return assets.Select((a, i) => new BuildDiffAssetSnapshot(
                    $"{fileName}#{a.PathId}", fileName, path, a.PathId, a.Name, ClassIDType.TextAsset, string.Empty, objects[i].Start, objects[i].Size)).ToList();
            }
        }
    }
}
