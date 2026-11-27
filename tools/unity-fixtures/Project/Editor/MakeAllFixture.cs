using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class MakeAllFixture
{
    const string Bundle = "fixture";
    const string Root = "Assets/Fixture";

    static Color32 Pixel(int x, int y, int w, int h, int salt)
    {
        byte r = (byte)(x * 255 / Mathf.Max(1, w - 1));
        byte g = (byte)(y * 255 / Mathf.Max(1, h - 1));
        byte b = (byte)((x * 7 + y * 13 + salt * 31) & 0xFF);
        byte a = (byte)(255 - ((x + y) & 0x3F));
        if (x < 6 && y > h - 7) { r = 255; g = 255; b = 255; a = 255; }
        return new Color32(r, g, b, a);
    }

    static Texture2D MakePattern(int w, int h, int salt)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = Pixel(x, y, w, h, salt);
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    static void Tag(string path)
    {
        AssetImporter.GetAtPath(path).assetBundleName = Bundle;
    }

    static void ImportTexture(string name, TextureImporterFormat standalone, TextureImporterFormat android, bool mips)
    {
        var path = $"{Root}/Textures/{name}.png";
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.textureType = TextureImporterType.Default;
        imp.mipmapEnabled = mips;
        imp.filterMode = FilterMode.Point;
        imp.npotScale = TextureImporterNPOTScale.None;
        imp.isReadable = false;
        imp.alphaIsTransparency = false;
        imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "DefaultTexturePlatform", format = TextureImporterFormat.RGBA32, textureCompression = TextureImporterCompression.Uncompressed, maxTextureSize = 2048 });
        imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, format = standalone, maxTextureSize = 2048 });
        imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, format = android, maxTextureSize = 2048 });
        imp.assetBundleName = Bundle;
        imp.SaveAndReimport();
    }

    static byte[] MakeWav(int sampleRate, int channels, float seconds, float freq)
    {
        int frames = (int)(sampleRate * seconds);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int dataSize = frames * channels * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataSize); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataSize);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                w.Write((short)(Mathf.Sin(2 * Mathf.PI * freq * (c + 1) * i / sampleRate) * 12000));
        w.Flush();
        return ms.ToArray();
    }

    static Mesh MakeGridMesh(string name, int n)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var colors = new List<Color32>();
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                verts.Add(new Vector3(x * 0.5f, Mathf.Sin(x + y) * 0.25f, y * 0.5f));
                normals.Add(Vector3.up);
                tangents.Add(new Vector4(1, 0, 0, 1));
                colors.Add(new Color32((byte)(x * 40), (byte)(y * 40), 128, 255));
                uv0.Add(new Vector2(x / (float)n, y / (float)n));
                uv1.Add(new Vector2(1 - x / (float)n, y / (float)n));
            }
        var t0 = new List<int>();
        var t1 = new List<int>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * (n + 1) + x;
                var list = x < n / 2 ? t0 : t1;
                list.AddRange(new[] { i, i + n + 1, i + 1, i + 1, i + n + 1, i + n + 2 });
            }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTangents(tangents);
        mesh.SetColors(colors);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(t0, 0);
        mesh.SetTriangles(t1, 1);
        mesh.RecalculateBounds();
        return mesh;
    }

    public static void Build()
    {
        foreach (var d in new[] { "Textures", "Audio", "Meshes", "Materials", "Text", "Fonts", "Anim", "Prefabs", "Data" })
            Directory.CreateDirectory($"{Root}/{d}");
        Directory.CreateDirectory("Truth");

        var pattern = MakePattern(64, 64, 1).EncodeToPNG();
        File.WriteAllBytes("Truth/pattern64.png", pattern);
        foreach (var n in new[] { "tex_rgba32", "tex_dxt1", "tex_dxt5", "tex_bc7", "tex_mips" })
            File.WriteAllBytes($"{Root}/Textures/{n}.png", pattern);
        var npot = MakePattern(50, 30, 2).EncodeToPNG();
        File.WriteAllBytes("Truth/pattern50x30.png", npot);
        File.WriteAllBytes($"{Root}/Textures/tex_npot.png", npot);

        var wav = MakeWav(44100, 1, 0.5f, 440f);
        File.WriteAllBytes("Truth/sine_mono.wav", wav);
        File.WriteAllBytes($"{Root}/Audio/sine_pcm.wav", wav);
        File.WriteAllBytes($"{Root}/Audio/sine_vorbis.wav", wav);
        File.WriteAllBytes($"{Root}/Audio/sine_adpcm.wav", wav);
        var wavStereo = MakeWav(22050, 2, 0.25f, 330f);
        File.WriteAllBytes("Truth/sine_stereo.wav", wavStereo);
        File.WriteAllBytes($"{Root}/Audio/stereo_pcm.wav", wavStereo);

        var text = "Fixture text asset\nLinha 2 com acentuação: ção é ü\n";
        File.WriteAllText("Truth/notes.txt", text, new UTF8Encoding(false));
        File.WriteAllText($"{Root}/Text/notes.txt", text, new UTF8Encoding(false));
        var bin = new byte[1000];
        for (int i = 0; i < bin.Length; i++) bin[i] = (byte)(i * 31 + 7);
        File.WriteAllBytes("Truth/blob.bytes", bin);
        File.WriteAllBytes($"{Root}/Text/blob.bytes", bin);

        File.Copy("FixtureFont.ttf", $"{Root}/Fonts/fixture_font.ttf", true);

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        ImportTexture("tex_rgba32", TextureImporterFormat.RGBA32, TextureImporterFormat.RGBA32, false);
        ImportTexture("tex_dxt1", TextureImporterFormat.DXT1, TextureImporterFormat.ETC2_RGB4, false);
        ImportTexture("tex_dxt5", TextureImporterFormat.DXT5, TextureImporterFormat.ETC2_RGBA8, false);
        ImportTexture("tex_bc7", TextureImporterFormat.BC7, TextureImporterFormat.ASTC_6x6, false);
        ImportTexture("tex_mips", TextureImporterFormat.RGBA32, TextureImporterFormat.RGBA32, true);
        ImportTexture("tex_npot", TextureImporterFormat.RGBA32, TextureImporterFormat.RGBA32, false);

        void Audio(string name, AudioCompressionFormat fmt)
        {
            var path = $"{Root}/Audio/{name}.wav";
            var imp = (AudioImporter)AssetImporter.GetAtPath(path);
            var s = imp.defaultSampleSettings;
            s.compressionFormat = fmt;
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.quality = 0.8f;
            imp.defaultSampleSettings = s;
            imp.assetBundleName = Bundle;
            imp.SaveAndReimport();
        }
        Audio("sine_pcm", AudioCompressionFormat.PCM);
        Audio("sine_vorbis", AudioCompressionFormat.Vorbis);
        Audio("sine_adpcm", AudioCompressionFormat.ADPCM);
        Audio("stereo_pcm", AudioCompressionFormat.PCM);

        Tag($"{Root}/Text/notes.txt");
        Tag($"{Root}/Text/blob.bytes");
        Tag($"{Root}/Fonts/fixture_font.ttf");

        var arr = new Texture2DArray(32, 32, 3, TextureFormat.RGBA32, false);
        for (int s = 0; s < 3; s++) arr.SetPixels32(MakePattern(32, 32, 10 + s).GetPixels32(), s);
        arr.Apply(false, false);
        AssetDatabase.CreateAsset(arr, $"{Root}/Textures/tex_array.asset");
        Tag($"{Root}/Textures/tex_array.asset");

        var cube = new Cubemap(32, TextureFormat.RGBA32, false);
        for (int f = 0; f < 6; f++) cube.SetPixels(MakePattern(32, 32, 20 + f).GetPixels(), (CubemapFace)f);
        cube.Apply(false, false);
        AssetDatabase.CreateAsset(cube, $"{Root}/Textures/tex_cube.cubemap");
        Tag($"{Root}/Textures/tex_cube.cubemap");

        var grid = MakeGridMesh("fixture_grid", 4);
        AssetDatabase.CreateAsset(grid, $"{Root}/Meshes/fixture_grid.asset");
        Tag($"{Root}/Meshes/fixture_grid.asset");
        var sb = new StringBuilder();
        foreach (var v in grid.vertices) sb.AppendLine($"v {v.x:R} {v.y:R} {v.z:R}");
        for (int s = 0; s < grid.subMeshCount; s++) sb.AppendLine($"submesh {s} " + string.Join(" ", grid.GetTriangles(s)));
        File.WriteAllText("Truth/fixture_grid.txt", sb.ToString());

        var skinned = MakeGridMesh("fixture_skinned", 2);
        var weights = new BoneWeight[skinned.vertexCount];
        for (int i = 0; i < weights.Length; i++)
            weights[i] = new BoneWeight { boneIndex0 = i % 2, weight0 = 0.75f, boneIndex1 = (i + 1) % 2, weight1 = 0.25f };
        skinned.boneWeights = weights;
        skinned.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(-0.5f, 0, 0)) };
        var deltas = new Vector3[skinned.vertexCount];
        for (int i = 0; i < deltas.Length; i++) deltas[i] = new Vector3(0, 0.1f * i, 0);
        skinned.AddBlendShapeFrame("raise", 100f, deltas, null, null);
        AssetDatabase.CreateAsset(skinned, $"{Root}/Meshes/fixture_skinned.asset");
        Tag($"{Root}/Meshes/fixture_skinned.asset");

        var shader = Shader.Find("Fixture/Unlit");
        var mat = new Material(shader) { name = "fixture_mat" };
        mat.SetColor("_Tint", new Color(0.1f, 0.2f, 0.3f, 0.4f));
        mat.SetFloat("_Strength", 1.75f);
        mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/tex_rgba32.png");
        AssetDatabase.CreateAsset(mat, $"{Root}/Materials/fixture_mat.mat");
        Tag($"{Root}/Materials/fixture_mat.mat");
        var std = new Material(Shader.Find("Standard")) { name = "fixture_standard" };
        std.color = Color.cyan;
        AssetDatabase.CreateAsset(std, $"{Root}/Materials/fixture_standard.mat");
        Tag($"{Root}/Materials/fixture_standard.mat");
        Tag("Assets/Shaders/FixtureUnlit.shader");

        var clip = new AnimationClip { name = "fixture_move" };
        clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 5));
        clip.SetCurve("", typeof(Transform), "localPosition.y", new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.5f, 2), new Keyframe(1, 0)));
        clip.SetCurve("child", typeof(Transform), "localEulerAnglesRaw.z", AnimationCurve.Linear(0, 0, 1, 90));
        AssetDatabase.CreateAsset(clip, $"{Root}/Anim/fixture_move.anim");
        Tag($"{Root}/Anim/fixture_move.anim");
        var ctrl = AnimatorController.CreateAnimatorControllerAtPathWithClip($"{Root}/Anim/fixture_ctrl.controller", clip);
        Tag($"{Root}/Anim/fixture_ctrl.controller");

        var data = ScriptableObject.CreateInstance<FixtureData>();
        data.single = new FixtureSquare { side = 11 };
        AssetDatabase.CreateAsset(data, $"{Root}/Data/fixture_data.asset");
        Tag($"{Root}/Data/fixture_data.asset");

        var go = new GameObject("FixtureRoot");
        go.AddComponent<MeshFilter>().sharedMesh = grid;
        go.AddComponent<MeshRenderer>().sharedMaterials = new[] { mat, std };
        var fb = go.AddComponent<FixtureBehaviour>();
        fb.tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/tex_dxt5.png");
        fb.mat = mat;
        fb.nestedArray = new[] { new FixtureNested { id = 1, pos = Vector2.one, tag = "one" }, new FixtureNested { id = 2, pos = Vector2.right, tag = "two" } };
        fb.shape = new FixtureCircle { radius = 4.5f, label = "main" };
        fb.shapes = new List<IFixtureShape> { new FixtureSquare { side = 3 }, new FixtureCircle { radius = 1, label = "small" }, null };
        go.AddComponent<Animator>().runtimeAnimatorController = ctrl;
        var child = new GameObject("child");
        child.transform.SetParent(go.transform, false);
        child.transform.localPosition = new Vector3(1, 2, 3);
        var bone0 = new GameObject("bone0").transform;
        var bone1 = new GameObject("bone1").transform;
        bone0.SetParent(child.transform, false);
        bone1.SetParent(child.transform, false);
        bone1.localPosition = new Vector3(0.5f, 0, 0);
        var smr = child.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = skinned;
        smr.bones = new[] { bone0, bone1 };
        smr.rootBone = bone0;
        smr.sharedMaterial = mat;
        PrefabUtility.SaveAsPrefabAsset(go, $"{Root}/Prefabs/FixtureRoot.prefab");
        UnityEngine.Object.DestroyImmediate(go);
        Tag($"{Root}/Prefabs/FixtureRoot.prefab");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("FIXTURE_PREPARED " + Application.unityVersion);
    }

    public static void BuildBundles()
    {
        var target = EditorUserBuildSettings.activeBuildTarget;
        void One(string sub, BuildAssetBundleOptions opts)
        {
            var outDir = $"Out/{target}/{sub}";
            Directory.CreateDirectory(outDir);
            var m = BuildPipeline.BuildAssetBundles(outDir, opts | BuildAssetBundleOptions.ForceRebuildAssetBundle, target);
            Debug.Log($"FIXTURE_BUNDLES {sub} {(m != null ? "ok" : "FAILED")}");
        }
        One("lz4", BuildAssetBundleOptions.ChunkBasedCompression);
        One("lzma", BuildAssetBundleOptions.None);
        One("raw-notypetree", BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.DisableWriteTypeTree);
        Debug.Log("FIXTURE_DONE " + Application.unityVersion);
    }
}
