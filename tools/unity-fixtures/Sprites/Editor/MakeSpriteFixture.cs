using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

public static class MakeSpriteFixture
{
    static readonly (string Name, int W, int H)[] Shapes =
    {
        ("wide_a", 96, 20), ("tall_b", 20, 96), ("wide_c", 72, 18), ("tall_d", 18, 72),
        ("box_e", 40, 28), ("box_f", 28, 40), ("wide_g", 120, 14), ("tall_h", 14, 120),
        ("ring_i", 48, 48), ("ell_j", 50, 34), ("ell_k", 34, 50), ("bar_l", 88, 24)
    };

    static Color32 PixelFor(int i, int x, int y, int w, int h)
    {
        byte r = (byte)(x * 255 / Mathf.Max(1, w - 1));
        byte g = (byte)(y * 255 / Mathf.Max(1, h - 1));
        byte b = (byte)(40 + i * 17);
        byte a = (byte)(80 + (x + y) * 175 / Mathf.Max(1, w + h - 2));
        bool notch = x > w * 2 / 3 && y < h / 3;
        if (notch) a = 0;
        if (x < 4 && y > h - 5) { r = 255; g = 255; b = 255; a = 255; }
        if (i % 3 == 0 && (x - w / 2) * (x - w / 2) + (y - h / 2) * (y - h / 2) < (Mathf.Min(w, h) / 4) * (Mathf.Min(w, h) / 4)) a = 0;
        return new Color32(r, g, b, a);
    }

    public static void Build()
    {
        EditorSettings.spritePackerMode = SpritePackerMode.AlwaysOnAtlas;
        Directory.CreateDirectory("Assets/Sprites");
        Directory.CreateDirectory("SourcePng");
        for (int i = 0; i < Shapes.Length; i++)
        {
            var (name, w, h) = Shapes[i];
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = PixelFor(i, x, y, w, h);
            tex.SetPixels32(px);
            tex.Apply();
            var png = tex.EncodeToPNG();
            File.WriteAllBytes($"Assets/Sprites/{name}.png", png);
            File.WriteAllBytes($"SourcePng/{name}.png", png);
        }
        AssetDatabase.Refresh();

        foreach (var (name, _, _) in Shapes)
        {
            var path = $"Assets/Sprites/{name}.png";
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = TextureImporterType.Sprite;
            imp.spriteImportMode = SpriteImportMode.Single;
            var settings = new TextureImporterSettings();
            imp.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.Tight;
            settings.spriteAlignment = (int)SpriteAlignment.Center;
            imp.SetTextureSettings(settings);
            imp.alphaIsTransparency = true;
            imp.mipmapEnabled = false;
            imp.filterMode = FilterMode.Point;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.isReadable = true;
            imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "DefaultTexturePlatform", format = TextureImporterFormat.RGBA32, textureCompression = TextureImporterCompression.Uncompressed, maxTextureSize = 1024 });
            imp.assetBundleName = "sprites";
            imp.SaveAndReimport();
        }

        var atlas = new SpriteAtlas();
        atlas.SetPackingSettings(new SpriteAtlasPackingSettings { blockOffset = 1, padding = 2, enableRotation = true, enableTightPacking = true, enableAlphaDilation = false });
        atlas.SetTextureSettings(new SpriteAtlasTextureSettings { readable = true, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Point });
        var def = atlas.GetPlatformSettings("DefaultTexturePlatform");
        def.format = TextureImporterFormat.RGBA32;
        def.textureCompression = TextureImporterCompression.Uncompressed;
        def.maxTextureSize = 1024;
        atlas.SetPlatformSettings(def);
        atlas.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, format = TextureImporterFormat.RGBA32, maxTextureSize = 1024 });
        atlas.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, format = TextureImporterFormat.ETC_RGB4, allowsAlphaSplitting = true, maxTextureSize = 1024 });
        atlas.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Object>("Assets/Sprites") });
        AssetDatabase.CreateAsset(atlas, "Assets/FixtureAtlas.spriteatlas");
        AssetImporter.GetAtPath("Assets/FixtureAtlas.spriteatlas").assetBundleName = "sprites";
        AssetDatabase.SaveAssets();

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("FIXTURE_PREPARED");
    }

    public static void BuildBundles()
    {
        var target = EditorUserBuildSettings.activeBuildTarget;
        var outDir = $"Out/{target}";
        Directory.CreateDirectory(outDir);
        BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, target);
        Debug.Log("FIXTURE_DONE " + outDir);
    }
}
