using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AssetStudio;
using AssetStudio.Avalonia;
using Xunit;

namespace AssetStudio.Tests;

public class CrossTrackIntegrationTests
{
    [Fact]
    public void MaterializeArenaItemsForExport_SkipsFbxMaterialAndShaderInConvertMode_AndOrdersTexturesBeforeMaterials()
    {
        var arena = new AssetIndexArena();
        var meshIdx = arena.AddRecord("cab-mesh", @"C:\Game\data.bundle", 101, "HeroMesh", ClassIDType.Mesh, "assets/models/hero.fbx/HeroMesh", 0, 1024);
        var fbxMatIdx = arena.AddRecord("cab-mesh", @"C:\Game\data.bundle", 102, "HeroFbxMat", ClassIDType.Material, "assets/models/hero.fbx/HeroFbxMat", 0, 256);
        var fbxShaderIdx = arena.AddRecord("cab-mesh", @"C:\Game\data.bundle", 103, "HeroFbxShader", ClassIDType.Shader, "assets/models/hero.fbx/HeroFbxShader", 0, 512);
        var standaloneMatIdx = arena.AddRecord("cab-mat", @"C:\Game\mat.bundle", 201, "ArmorMat", ClassIDType.Material, "assets/materials/armor.mat", 0, 300);
        var texIdx = arena.AddRecord("cab-tex", @"C:\Game\tex.bundle", 301, "ArmorAlbedo", ClassIDType.Texture2D, "assets/textures/armor.png", 0, 4096);
        var monoIdx = arena.AddRecord("cab-mb", @"C:\Game\mb.bundle", 401, "ConfigData", ClassIDType.MonoBehaviour, "assets/data/config.asset", 0, 128);

        var indices = new[] { meshIdx, fbxMatIdx, fbxShaderIdx, standaloneMatIdx, texIdx, monoIdx };

        Assert.True(MainWindow.ArenaIndicesContainType(arena, indices, ClassIDType.MonoBehaviour));
        Assert.False(MainWindow.ArenaIndicesContainType(arena, new[] { meshIdx, texIdx }, ClassIDType.MonoBehaviour));

        var convertItems = MainWindow.MaterializeArenaItemsForExport(arena, indices, ExportMode.Convert);
        Assert.Equal(4, convertItems.Count);
        Assert.Equal(ClassIDType.Texture2D, convertItems[0].Type);
        Assert.Equal(ClassIDType.Material, convertItems[1].Type);
        Assert.Equal("ArmorMat", convertItems[1].Name);
        Assert.DoesNotContain(convertItems, x => x.Name == "HeroFbxMat");
        Assert.DoesNotContain(convertItems, x => x.Name == "HeroFbxShader");

        var rawItems = MainWindow.MaterializeArenaItemsForExport(arena, indices, ExportMode.Raw);
        Assert.Equal(6, rawItems.Count);

        // Lazy FBX Material/Shader skip check must work from Type alone without touching Asset resolution.
        var lazyFbxMatItem = new AssetItem(arena.MaterializeHandle(fbxMatIdx), fbxMatIdx);
        var lazyStandaloneMatItem = new AssetItem(arena.MaterializeHandle(standaloneMatIdx), standaloneMatIdx);
        Assert.True(MainWindow.ShouldSkipConvertedAsset(lazyFbxMatItem));
        Assert.False(MainWindow.ShouldSkipConvertedAsset(lazyStandaloneMatItem));
    }

    [Fact]
    public void WriteArenaAssetsXml_StreamsArenaMetadataDirectlyWithoutMaterializingItemList()
    {
        var arena = new AssetIndexArena();
        var idx1 = arena.AddRecord("cab-a", @"C:\Game\a.bundle", 10, "HeroMesh", ClassIDType.Mesh, "assets/models/hero.fbx/HeroMesh", 0, 2048);
        var idx2 = arena.AddRecord("cab-b", @"C:\Game\b.bundle", 20, "UiAtlas", ClassIDType.Texture2D, "assets/ui/atlas.png", 0, 8192);

        var tempDir = Path.Combine(Path.GetTempPath(), "AssetStudio_CrossTrackXml_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var xmlPath = Path.Combine(tempDir, "assets.xml");
            var count = MainWindow.WriteArenaAssetsXml(arena, new[] { idx1, idx2, 99999 }, xmlPath);

            Assert.Equal(2, count);
            Assert.True(File.Exists(xmlPath));

            var doc = XDocument.Load(xmlPath);
            var assetElements = doc.Root!.Elements("Asset").ToList();
            Assert.Equal(2, assetElements.Count);

            Assert.Equal("HeroMesh", assetElements[0].Element("Name")?.Value);
            Assert.Equal("assets/models/hero.fbx/HeroMesh", assetElements[0].Element("Container")?.Value);
            Assert.Equal(((int)ClassIDType.Mesh).ToString(), assetElements[0].Element("Type")?.Attribute("id")?.Value);
            Assert.Contains("FBX sub-asset", assetElements[0].Element("Type")?.Value);
            Assert.Equal("10", assetElements[0].Element("PathID")?.Value);
            Assert.Equal(@"C:\Game\a.bundle", assetElements[0].Element("Source")?.Value);
            Assert.Equal("2048", assetElements[0].Element("Size")?.Value);

            Assert.Equal("UiAtlas", assetElements[1].Element("Name")?.Value);
            Assert.Equal("Texture2D", assetElements[1].Element("Type")?.Value);
            Assert.Equal("8192", assetElements[1].Element("Size")?.Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CrossTrackCoordinators_InvalidateAndCancelCleanlyOnProjectReset()
    {
        using var previewCoordinator = new AsyncPreviewRequestCoordinator();
        using var dumpCoordinator = new AsyncPreviewRequestCoordinator();
        var searchSession = new SceneTreeSearchSession();

        var previewToken = previewCoordinator.BeginNewRequest(out var previewReqId);
        var dumpToken = dumpCoordinator.BeginNewRequest(out var dumpReqId);

        var root = new GameObjectNode { Name = "SceneRoot" };
        root.AddChild(new GameObjectNode { Name = "HeroCharacter" });
        var roots = new List<GameObjectNode> { root };

        var matches = await searchSession.SearchAsync(roots, "Hero");
        Assert.NotNull(matches);
        Assert.Single(matches!);
        Assert.True(searchSession.TryGetCached(roots, "Hero", out _));

        // Simulate ResetForm/OnClosing cross-track cleanup.
        previewCoordinator.CancelCurrent();
        dumpCoordinator.CancelCurrent();
        searchSession.Invalidate();
        AssetExportHelper.ClearShaderGuidCache();

        Assert.True(previewToken.IsCancellationRequested);
        Assert.True(dumpToken.IsCancellationRequested);
        Assert.False(previewCoordinator.IsCurrent(previewReqId));
        Assert.False(dumpCoordinator.IsCurrent(dumpReqId));
        Assert.False(searchSession.TryGetCached(roots, "Hero", out _));
        Assert.Equal(0, AssetExportHelper.ShaderGuidDirectoryScanCount);
    }

    [Fact]
    public void ArenaMaterializedItems_IntegrateCleanlyWithLazyExportBatchGrouping()
    {
        var arena = new AssetIndexArena();
        var indices = new List<int>();
        for (int s = 0; s < 5; s++)
        {
            var sourcePath = $@"C:\Game\bundle_{s}.ab";
            for (int i = 0; i < 4; i++)
            {
                var idx = arena.AddRecord(
                    $"cab-{s}",
                    sourcePath,
                    1000 + s * 10 + i,
                    $"Tex_{s}_{i}",
                    ClassIDType.Texture2D,
                    $"assets/tex_{s}_{i}.png",
                    0,
                    64 * 1024);
                indices.Add(idx);
            }
        }

        var materialized = MainWindow.MaterializeArenaItemsForExport(arena, indices, ExportMode.Convert);
        Assert.Equal(20, materialized.Count);

        var batches = MainWindow.BuildLazyExportBatchesForItems(
            materialized,
            item => item.Handle?.OriginalPath,
            maxSourceFilesPerBatch: 2,
            maxAssetBytesPerBatch: long.MaxValue);

        Assert.Equal(3, batches.Count);
        Assert.Equal(2, batches[0].SourcePaths.Count);
        Assert.Equal(8, batches[0].Items.Count);
        Assert.Equal(2, batches[1].SourcePaths.Count);
        Assert.Equal(8, batches[1].Items.Count);
        Assert.Single(batches[2].SourcePaths);
        Assert.Equal(4, batches[2].Items.Count);
    }
}
