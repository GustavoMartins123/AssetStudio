using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class ExportParityTests
    {
        [Fact]
        public void GltfExporter_ToonVertexColorAndUntexturedMaskMaterial_OmitsColor0ByDefault_AndDowngradesUntexturedMaskToOpaque()
        {
            var tempDir = CreateTempDirectory("GltfToonColorMask");
            try
            {
                var root = new ImportedFrame { Name = "CharacterRoot", LocalScale = Vector3.One };
                var mesh = new ImportedMesh
                {
                    Path = "CharacterRoot",
                    hasNormal = true,
                    hasColor = true,
                    hasUV = new[] { true, false, false, false, false, false, false, false },
                    VertexList = new List<ImportedVertex>
                    {
                        // Anime/toon outline weights in COLOR_0: RGB=(1,0,0), alpha=0.35 (< 0.5 cutoff)
                        new ImportedVertex { Vertex = new Vector3(0, 0, 0), Normal = new Vector3(0, 0, 1), Color = new Color(1f, 0f, 0f, 0.35f), UV = new[] { new[] { 0f, 0f } } },
                        new ImportedVertex { Vertex = new Vector3(1, 0, 0), Normal = new Vector3(0, 0, 1), Color = new Color(1f, 0f, 0f, 0.20f), UV = new[] { new[] { 1f, 0f } } },
                        new ImportedVertex { Vertex = new Vector3(0, 1, 0), Normal = new Vector3(0, 0, 1), Color = new Color(1f, 0f, 0f, 0.45f), UV = new[] { new[] { 0f, 1f } } }
                    },
                    SubmeshList = new List<ImportedSubmesh>
                    {
                        new ImportedSubmesh
                        {
                            Material = "UntexturedMaskMat",
                            FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } }
                        },
                        new ImportedSubmesh
                        {
                            Material = "TexturedMaskMat",
                            FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } }
                        },
                        new ImportedSubmesh
                        {
                            Material = "BlendMat",
                            FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } }
                        }
                    }
                };

                var untexturedMaskMat = new ImportedMaterial
                {
                    Name = "UntexturedMaskMat",
                    Diffuse = new Color(1f, 1f, 1f, 1f),
                    SurfaceMode = 1f, // MASK without texture -> must downgrade to OPAQUE
                    AlphaCutoff = 0.5f,
                    Textures = new List<ImportedMaterialTexture>()
                };

                var texturedMaskMat = new ImportedMaterial
                {
                    Name = "TexturedMaskMat",
                    Diffuse = new Color(1f, 1f, 1f, 1f),
                    SurfaceMode = 1f, // MASK with valid baseColorTexture -> stays MASK
                    AlphaCutoff = 0.5f,
                    Textures = new List<ImportedMaterialTexture>
                    {
                        new ImportedMaterialTexture { Name = "dress.png", Dest = 0, Scale = new Vector2(1, 1) }
                    }
                };

                var blendMat = new ImportedMaterial
                {
                    Name = "BlendMat",
                    Diffuse = new Color(1f, 1f, 1f, 0.75f),
                    SurfaceMode = 3f, // BLEND
                    Textures = new List<ImportedMaterialTexture>()
                };

                var imported = new SimpleImported(root);
                imported.MeshList.Add(mesh);
                imported.MaterialList.Add(untexturedMaskMat);
                imported.MaterialList.Add(texturedMaskMat);
                imported.MaterialList.Add(blendMat);
                imported.TextureList.Add(new ImportedTexture(new MemoryStream(CreateSolidPngBytes(4, 4, 120, 200, 140, 255)), "dress.png"));

                // 1. Default export: NO COLOR_0 emitted, untextured MASK downgraded to OPAQUE
                string defaultGlb = Path.Combine(tempDir, "default.glb");
                new GltfExporter(binary: true).Export(imported, defaultGlb);

                using var defaultDoc = ParseGlbJson(defaultGlb, out _);
                var defaultRoot = defaultDoc.RootElement;
                var defaultPrims = defaultRoot.GetProperty("meshes")[0].GetProperty("primitives");
                for (int p = 0; p < defaultPrims.GetArrayLength(); p++)
                {
                    Assert.False(defaultPrims[p].GetProperty("attributes").TryGetProperty("COLOR_0", out _),
                        $"Primitive {p} should not emit COLOR_0 by default.");
                }

                var mats = defaultRoot.GetProperty("materials");
                // UntexturedMaskMat downgraded to OPAQUE (alphaMode omitted in glTF JSON)
                Assert.False(mats[0].TryGetProperty("alphaMode", out _));
                Assert.False(mats[0].TryGetProperty("alphaCutoff", out _));
                // TexturedMaskMat keeps MASK
                Assert.Equal("MASK", mats[1].GetProperty("alphaMode").GetString());
                Assert.Equal(0.5f, mats[1].GetProperty("alphaCutoff").GetSingle(), 3);
                // BlendMat keeps BLEND
                Assert.Equal("BLEND", mats[2].GetProperty("alphaMode").GetString());

                // 2. Opt-in exportVertexColors: true -> COLOR_0 emitted, with alpha forced to 1.0 on OPAQUE/MASK and preserved on BLEND
                string optInGlb = Path.Combine(tempDir, "optin_colors.glb");
                new GltfExporter(binary: true, exportVertexColors: true).Export(imported, optInGlb);

                using var optInDoc = ParseGlbJson(optInGlb, out var optInBin);
                var optInRoot = optInDoc.RootElement;
                var optInPrims = optInRoot.GetProperty("meshes")[0].GetProperty("primitives");

                // Primitive 0 (OPAQUE) and Primitive 1 (MASK) must have COLOR_0 alpha == 1.0f
                for (int p = 0; p < 2; p++)
                {
                    int colorAcc = optInPrims[p].GetProperty("attributes").GetProperty("COLOR_0").GetInt32();
                    var colors = ReadVec4Accessor(optInRoot, optInBin, colorAcc);
                    Assert.All(colors, c => Assert.Equal(1.0f, c.W, 4));
                }

                // Primitive 2 (BLEND) preserves vertex alpha (0.35, 0.20, 0.45)
                int blendColorAcc = optInPrims[2].GetProperty("attributes").GetProperty("COLOR_0").GetInt32();
                var blendColors = ReadVec4Accessor(optInRoot, optInBin, blendColorAcc);
                Assert.Equal(0.35f, blendColors[0].W, 4);
                Assert.Equal(0.20f, blendColors[1].W, 4);
                Assert.Equal(0.45f, blendColors[2].W, 4);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_SkinnedMeshWithNullOrEmptyBonePaths_NeverMapsToRootOrEmitsIdentityDupNodes()
        {
            var tempDir = CreateTempDirectory("GltfNullBonePaths");
            try
            {
                var root = new ImportedFrame { Name = "ModelRoot", LocalScale = Vector3.One };
                var mesh = new ImportedMesh
                {
                    Path = "ModelRoot",
                    hasNormal = true,
                    hasUV = new[] { true, false, false, false, false, false, false, false },
                    VertexList = new List<ImportedVertex>
                    {
                        new ImportedVertex { Vertex = new Vector3(0, 0, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 0f, 0f } }, BoneIndices = new[] { 0, 1, 2, 0 }, Weights = new[] { 0.5f, 0.3f, 0.2f, 0f } },
                        new ImportedVertex { Vertex = new Vector3(1, 0, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 1f, 0f } }, BoneIndices = new[] { 1, 2, 3, 0 }, Weights = new[] { 0.6f, 0.4f, 0f, 0f } },
                        new ImportedVertex { Vertex = new Vector3(0, 1, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 0f, 1f } }, BoneIndices = new[] { 2, 3, 4, 0 }, Weights = new[] { 1.0f, 0f, 0f, 0f } }
                    },
                    SubmeshList = new List<ImportedSubmesh>
                    {
                        new ImportedSubmesh
                        {
                            Material = "BodyMat",
                            FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } }
                        }
                    },
                    BoneList = new List<ImportedBone>()
                };

                // Add 12 bones with null/empty paths and real bind-pose translations (reproducing skeleton-less skinned mesh export)
                for (int b = 0; b < 12; b++)
                {
                    var ibm = Matrix4x4.Scale(Vector3.One);
                    ibm[3, 0] = 0.4f * (b + 1);
                    ibm[3, 1] = 0.85f * (b + 1);
                    ibm[3, 2] = -0.1f * (b + 1);
                    mesh.BoneList.Add(new ImportedBone
                    {
                        Path = b % 2 == 0 ? null : string.Empty,
                        Matrix = ibm
                    });
                }

                var imported = new SimpleImported(root);
                imported.MeshList.Add(mesh);

                string glbPath = Path.Combine(tempDir, "unresolved_skin.glb");
                ModelExporter.ExportGltf(glbPath, imported, binary: true, skins: true);

                using var doc = ParseGlbJson(glbPath, out _);
                var jsonRoot = doc.RootElement;

                // Must NOT emit skins or *_Dup_* identity nodes when all bones are unresolved
                Assert.False(jsonRoot.TryGetProperty("skins", out _));
                var nodes = jsonRoot.GetProperty("nodes").EnumerateArray().ToList();
                Assert.DoesNotContain(nodes, n =>
                    n.TryGetProperty("name", out var nameProp)
                    && (nameProp.GetString()?.Contains("_Dup_", StringComparison.Ordinal) ?? false));

                var prim0 = jsonRoot.GetProperty("meshes")[0].GetProperty("primitives")[0];
                Assert.False(prim0.GetProperty("attributes").TryGetProperty("JOINTS_0", out _));
                Assert.False(prim0.GetProperty("attributes").TryGetProperty("WEIGHTS_0", out _));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ModelConverter_WithInjectedExternalTextureResolver_ExportsBaseColorTexture_PrefersEyeIrisAndMainTexture_AndRestrictsPathIdScan()
        {
            var tempDir = CreateTempDirectory("ModelConverterParity");
            try
            {
                var manager = new AssetsManager();
                var meshFile = CreateSyntheticSerializedFile(manager, "CAB-mesh");
                var matFile = CreateSyntheticSerializedFile(manager, "CAB-mat");
                var extTexFile = CreateSyntheticSerializedFile(manager, "CAB-ext-textures");
                var unrelatedFile = CreateSyntheticSerializedFile(manager, "CAB-unrelated");

                manager.assetsFileList.Add(meshFile);
                manager.assetsFileList.Add(matFile);
                manager.assetsFileList.Add(unrelatedFile);
                // Note: extTexFile is intentionally NOT in manager.assetsFileList or matFile.m_Externals
                // to verify that the injected textureResolver callback resolves cross-bundle textures!

                // Create textures:
                // 1. Eye textures: Highlight (salmon red) vs Iris (blue) vs White (white)
                var highlightTex = CreateSyntheticRgbaTexture(extTexFile, pathId: 1001, name: "Eye_Highlight_Tex", r: 255, g: 80, b: 80);
                var irisTex = CreateSyntheticRgbaTexture(extTexFile, pathId: 1002, name: "Eye_Iris_Tex", r: 40, g: 120, b: 240);
                var whiteTex = CreateSyntheticRgbaTexture(extTexFile, pathId: 1003, name: "Eye_White_Tex", r: 240, g: 240, b: 240);

                // 2. Body textures: _MaskTexture (green mask) vs _MainTexture (gold albedo)
                var maskTex = CreateSyntheticRgbaTexture(extTexFile, pathId: 2001, name: "Body_Mask_Tex", r: 0, g: 255, b: 0);
                var mainTex = CreateSyntheticRgbaTexture(extTexFile, pathId: 2002, name: "Body_Main_Tex", r: 240, g: 200, b: 80);

                // 3. Unrelated texture with colliding PathID 9999 in CAB-unrelated (must NOT be picked by fallback PathID scan!)
                var collidingWrongTex = CreateSyntheticRgbaTexture(unrelatedFile, pathId: 9999, name: "Wrong_Colliding_Tex", r: 255, g: 0, b: 255);
                var semanticCacheFallbackTex = CreateSyntheticRgbaTexture(extTexFile, pathId: 3001, name: "Semantic_Cache_Face_Tex", r: 250, g: 210, b: 190);

                // Eye Material: alphabetical m_TexEnvs with _BaseEyeHighlightMap before _BaseEyeIrisMap and _BaseEyeWhiteMap
                var eyeMat = CreateSyntheticMaterial(matFile, pathId: 501, name: "Hero_Eye_L", new[]
                {
                    MakeTexEnv("_BaseEyeHighlightMap", fileId: 2, pathId: 1001, matFile),
                    MakeTexEnv("_BaseEyeIrisMap", fileId: 2, pathId: 1002, matFile),
                    MakeTexEnv("_BaseEyeWhiteMap", fileId: 2, pathId: 1003, matFile),
                    MakeTexEnv("_MatcapMap", fileId: 2, pathId: 1001, matFile)
                });

                // Body Material: alphabetical m_TexEnvs with _MaskTexture before _MainTexture
                var bodyMat = CreateSyntheticMaterial(matFile, pathId: 502, name: "Hero_Body", new[]
                {
                    MakeTexEnv("_MaskTexture", fileId: 2, pathId: 2001, matFile),
                    MakeTexEnv("_MainTexture", fileId: 2, pathId: 2002, matFile),
                    MakeTexEnv("_UVScrollTexture1", fileId: 2, pathId: 2001, matFile)
                });

                // Face Material: points to PathID 9999 (which exists in CAB-unrelated, NOT in matFile or its m_Externals),
                // and falls back to semanticCacheFallbackTex when resolver is queried with (mat, "", null).
                var faceMat = CreateSyntheticMaterial(matFile, pathId: 503, name: "Hero_Face", new[]
                {
                    MakeTexEnv("_BaseMap", fileId: 3, pathId: 9999, matFile)
                });

                var mesh = CreateSyntheticThreeSubmeshMesh(meshFile, pathId: 100, name: "Hero_Combined");

                var resolvedByPathId = new Dictionary<long, Texture2D>
                {
                    [1001] = highlightTex,
                    [1002] = irisTex,
                    [1003] = whiteTex,
                    [2001] = maskTex,
                    [2002] = mainTex
                };

                Texture2D? Resolver(Material mat, string slotName, PPtr<Texture>? texPtr)
                {
                    if (texPtr != null && !texPtr.IsNull && resolvedByPathId.TryGetValue(texPtr.m_PathID, out var found))
                    {
                        return found;
                    }
                    if (string.IsNullOrEmpty(slotName) && ReferenceEquals(mat, faceMat))
                    {
                        return semanticCacheFallbackTex;
                    }
                    return null;
                }

                var converter = ModelConverter.CreateFromModelGroup(
                    "Hero_Combined",
                    rootGameObject: null,
                    meshes: new[] { mesh },
                    renderers: null,
                    avatar: null,
                    animationClips: null,
                    imageFormat: ImageFormat.Png,
                    combineMeshes: false,
                    partTransforms: null,
                    partMaterials: new IReadOnlyList<Material>[] { new[] { eyeMat, bodyMat, faceMat } },
                    partNames: new[] { "Hero_Combined" },
                    textureResolver: Resolver);

                Assert.Single(converter.MeshList);
                // Mesh had unverified m_BoneNameHashes -> must NOT emit bogus root-mapped BoneList
                Assert.True(converter.MeshList[0].BoneList == null || converter.MeshList[0].BoneList!.Count == 0);

                Assert.Equal(3, converter.MaterialList.Count);

                // 1. Eye material primary diffuse (first Dest==0) MUST be Eye_Iris_Tex (NOT Eye_Highlight_Tex and NOT Eye_White_Tex)
                var convEyeMat = converter.MaterialList.First(m => m.Name == "Hero_Eye_L");
                var primaryEyeDiffuse = convEyeMat.Textures.FirstOrDefault(t => t.Dest == 0);
                Assert.NotNull(primaryEyeDiffuse);
                Assert.Equal("Eye_Iris_Tex.png", primaryEyeDiffuse!.Name);
                Assert.DoesNotContain(convEyeMat.Textures, t => t.Name == "Eye_Highlight_Tex.png" && t.Dest == 0);

                // 2. Body material primary diffuse (first Dest==0) MUST be Body_Main_Tex (NOT Body_Mask_Tex)
                var convBodyMat = converter.MaterialList.First(m => m.Name == "Hero_Body");
                var primaryBodyDiffuse = convBodyMat.Textures.FirstOrDefault(t => t.Dest == 0);
                Assert.NotNull(primaryBodyDiffuse);
                Assert.Equal("Body_Main_Tex.png", primaryBodyDiffuse!.Name);
                Assert.DoesNotContain(convBodyMat.Textures, t => t.Name == "Body_Mask_Tex.png" && t.Dest == 0);

                // 3. Face material MUST NOT bind Wrong_Colliding_Tex from CAB-unrelated, and MUST use Semantic_Cache_Face_Tex fallback
                var convFaceMat = converter.MaterialList.First(m => m.Name == "Hero_Face");
                var primaryFaceDiffuse = convFaceMat.Textures.FirstOrDefault(t => t.Dest == 0);
                Assert.NotNull(primaryFaceDiffuse);
                Assert.Equal("Semantic_Cache_Face_Tex.png", primaryFaceDiffuse!.Name);
                Assert.DoesNotContain(converter.TextureList, t => t.Name.Contains("Wrong_Colliding_Tex", StringComparison.Ordinal));

                // Export to GLB and verify all 3 materials have valid baseColorTexture & embedded PNG images
                string glbPath = Path.Combine(tempDir, "hero_parity.glb");
                ModelExporter.ExportGltf(glbPath, converter, binary: true);

                using var glbDoc = ParseGlbJson(glbPath, out _);
                var glbRoot = glbDoc.RootElement;
                Assert.True(glbRoot.TryGetProperty("textures", out var glbTextures) && glbTextures.GetArrayLength() == 3);
                Assert.True(glbRoot.TryGetProperty("images", out var glbImages) && glbImages.GetArrayLength() == 3);

                var glbMats = glbRoot.GetProperty("materials");
                Assert.Equal(3, glbMats.GetArrayLength());
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(glbMats[i].GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _),
                        $"Material {glbMats[i].GetProperty("name").GetString()} missing baseColorTexture in exported GLB.");
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ExportStandardConvertedAsset_ZeroVertexEmptyStubMesh_SkipsCleanlyWithoutFailure()
        {
            var tempDir = CreateTempDirectory("EmptyStubMeshExport");
            try
            {
                var manager = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(manager, "CAB-stub");
                var emptyStubMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
                emptyStubMesh.assetsFile = sf;
                emptyStubMesh.m_PathID = -9183687587296855342L;
                emptyStubMesh.m_Name = "Stage_Tree_Base_01_LODMeshSet";
                emptyStubMesh.m_VertexCount = 0;
                emptyStubMesh.m_Vertices = Array.Empty<float>();
                emptyStubMesh.m_Indices = new List<uint>();
                emptyStubMesh.m_SubMeshes = Array.Empty<SubMesh>();
                typeof(Mesh).GetField("m_DataProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(emptyStubMesh, true);
                typeof(Mesh).GetField("m_ProcessLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(emptyStubMesh, new object());
                sf.AddObject(emptyStubMesh);

                var item = new AssetItem(emptyStubMesh);
                var ctx = new ExportConversionContext(new List<SerializedFile> { sf });
                var options = new ExportOptionsState();

                bool exported = AssetExportService.ExportStandardConvertedAsset(item, tempDir, ctx, options);
                Assert.False(exported);
                Assert.Empty(Directory.GetFiles(tempDir));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static KeyValuePair<string, UnityTexEnv> MakeTexEnv(string key, int fileId, long pathId, SerializedFile ownerFile)
        {
            var env = (UnityTexEnv)RuntimeHelpers.GetUninitializedObject(typeof(UnityTexEnv));
            env.m_Texture = new PPtr<Texture>(fileId, pathId, ownerFile);
            env.m_Scale = new Vector2(1f, 1f);
            env.m_Offset = Vector2.Zero;
            return new KeyValuePair<string, UnityTexEnv>(key, env);
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager manager, string fileName)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            typeof(SerializedFile)
                .GetField("objectCollectionLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(sf, new object());
            sf.assetsManager = manager;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.m_Externals = new List<FileIdentifier>();
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.version = new[] { 2022, 3, 10, 1 };
            return sf;
        }

        private static Texture2D CreateSyntheticRgbaTexture(SerializedFile sf, long pathId, string name, byte r, byte g, byte b)
        {
            var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.assetsFile = sf;
            tex.m_PathID = pathId;
            tex.m_Name = name;
            tex.m_Width = 4;
            tex.m_Height = 4;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.version = sf.version;
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
            var rr = (ResourceReader)RuntimeHelpers.GetUninitializedObject(typeof(ResourceReader));
            typeof(ResourceReader).GetField("needSearch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(rr, false);
            typeof(ResourceReader).GetField("reader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(rr, new EndianBinaryReader(new MemoryStream(pixels)));
            typeof(ResourceReader).GetField("offset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(rr, 0L);
            typeof(ResourceReader).GetField("size", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(rr, (long)pixels.Length);
            tex.image_data = rr;
            sf.AddObject(tex);
            return tex;
        }

        private static Material CreateSyntheticMaterial(SerializedFile sf, long pathId, string name, KeyValuePair<string, UnityTexEnv>[] texEnvs)
        {
            var mat = (Material)RuntimeHelpers.GetUninitializedObject(typeof(Material));
            mat.assetsFile = sf;
            mat.m_PathID = pathId;
            mat.m_Name = name;
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs,
                m_Floats = Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = new[] { new KeyValuePair<string, Color>("_BaseColor", new Color(1f, 1f, 1f, 1f)) }
            };
            sf.AddObject(mat);
            return mat;
        }

        private static Mesh CreateSyntheticThreeSubmeshMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            mesh.assetsFile = sf;
            mesh.m_PathID = pathId;
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            mesh.m_Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_UV0 = new[] { 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_Indices = new List<uint> { 0, 1, 2, 0, 1, 2, 0, 1, 2 };

            SubMesh MakeSub(uint firstByte)
            {
                var sub = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
                sub.firstByte = firstByte;
                sub.indexCount = 3;
                sub.firstVertex = 0;
                sub.vertexCount = 3;
                sub.baseVertex = 0;
                return sub;
            }

            mesh.m_SubMeshes = new[] { MakeSub(0), MakeSub(12), MakeSub(24) };
            // Include unverified bone hashes & bind poses to verify ModelConverter does not emit bogus root-mapped bones
            mesh.m_BoneNameHashes = new uint[] { 0u, 12345678u };
            mesh.m_BindPose = new[] { Matrix4x4.Scale(Vector3.One), Matrix4x4.Scale(Vector3.One) };
            typeof(Mesh).GetField("m_DataProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(mesh, new object());
            sf.AddObject(mesh);
            return mesh;
        }

        private static byte[] CreateSolidPngBytes(int width, int height, byte r, byte g, byte b, byte a)
        {
            var bgra = new byte[width * height * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i + 0] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = a;
            }
            using var bmp = ImageExtensions.CreateBgraUnpremulBitmap(bgra, width, height, flipVertical: false);
            using var ms = bmp.ConvertToStream(ImageFormat.Png);
            return ms.ToArray();
        }

        private static JsonDocument ParseGlbJson(string glbPath, out byte[] binData)
        {
            var bytes = File.ReadAllBytes(glbPath);
            uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            var jsonBytes = bytes.AsSpan(20, (int)jsonLen).ToArray();
            int nextOff = 20 + (int)jsonLen;
            if (nextOff + 8 <= bytes.Length)
            {
                uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(nextOff, 4));
                binData = bytes.AsSpan(nextOff + 8, (int)binLen).ToArray();
            }
            else
            {
                binData = Array.Empty<byte>();
            }
            return JsonDocument.Parse(jsonBytes);
        }

        private static System.Numerics.Vector4[] ReadVec4Accessor(JsonElement root, byte[] binData, int accIdx)
        {
            var acc = root.GetProperty("accessors")[accIdx];
            var bv = root.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
            int accOff = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            int baseOff = bv.GetProperty("byteOffset").GetInt32() + accOff;
            int count = acc.GetProperty("count").GetInt32();
            var arr = new System.Numerics.Vector4[count];
            for (int i = 0; i < count; i++)
            {
                int o = baseOff + i * 16;
                arr[i] = new System.Numerics.Vector4(
                    BinaryPrimitives.ReadSingleLittleEndian(binData.AsSpan(o, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(binData.AsSpan(o + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(binData.AsSpan(o + 8, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(binData.AsSpan(o + 12, 4)));
            }
            return arr;
        }

        private static string CreateTempDirectory(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"AssetStudioTests_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }

        private sealed class SimpleImported : IImported
        {
            public ImportedFrame RootFrame { get; }
            public List<ImportedMesh> MeshList { get; } = new();
            public List<ImportedMaterial> MaterialList { get; } = new();
            public List<ImportedTexture> TextureList { get; } = new();
            public List<ImportedKeyframedAnimation> AnimationList { get; } = new();
            public List<ImportedMorph> MorphList { get; } = new();

            public SimpleImported(ImportedFrame rootFrame)
            {
                RootFrame = rootFrame;
            }
        }
    }
}
