using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public sealed class GltfExporterTests
    {
        private readonly ITestOutputHelper _output;

        public GltfExporterTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ------------------------------------------------------------------
        // Full-featured synthetic IImported -> GLB & glTF validation
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(true, 1.0f)]
        [InlineData(false, 1.0f)]
        [InlineData(true, 0.5f)]
        public void GltfExporter_FullFeaturedModel_ProducesSpecValidGltfAndGlb(bool binary, float scaleFactor)
        {
            var tempDir = CreateTempDirectory($"GltfFull_{(binary ? "glb" : "gltf")}_{scaleFactor}");
            try
            {
                var imported = BuildFullFeaturedSyntheticModel();
                string ext = binary ? ".glb" : ".gltf";
                string outPath = Path.Combine(tempDir, "hero_model" + ext);

                ModelExporter.ExportGltf(
                    outPath,
                    imported,
                    binary: binary,
                    skins: true,
                    animation: true,
                    blendShape: true,
                    scaleFactor: scaleFactor,
                    combineMeshes: false);

                Assert.True(File.Exists(outPath));
                var doc = ValidateGltfOrGlbFile(outPath, expectBinary: binary);

                // Verify core graph counts
                var root = doc.RootElement;
                Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
                Assert.Equal(0, root.GetProperty("scene").GetInt32());
                Assert.True(root.GetProperty("nodes").GetArrayLength() >= 4);
                Assert.Equal(2, root.GetProperty("meshes").GetArrayLength());
                Assert.Equal(2, root.GetProperty("materials").GetArrayLength());
                Assert.Equal(1, root.GetProperty("skins").GetArrayLength());
                Assert.Equal(1, root.GetProperty("animations").GetArrayLength());

                // Verify UV flip: Unity (u=0.25, v=0.2) -> glTF (u=0.25, v=0.8)
                var bodyMesh = root.GetProperty("meshes")[0];
                var prim0 = bodyMesh.GetProperty("primitives")[0];
                int uv0AccIdx = prim0.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32();
                int uv1AccIdx = prim0.GetProperty("attributes").GetProperty("TEXCOORD_1").GetInt32();
                var uv0 = ReadVec2Accessor(doc, uv0AccIdx);
                var uv1 = ReadVec2Accessor(doc, uv1AccIdx);
                Assert.Equal(0.25f, uv0[0].X, 4);
                Assert.Equal(0.8f, uv0[0].Y, 4);
                Assert.Equal(0.1f, uv1[0].X, 4);
                Assert.Equal(0.75f, uv1[0].Y, 4);

                // Verify COLOR_0 and TANGENT present
                Assert.True(prim0.GetProperty("attributes").TryGetProperty("COLOR_0", out _));
                Assert.True(prim0.GetProperty("attributes").TryGetProperty("TANGENT", out var tanProp));
                var tangents = ReadVec4Accessor(doc, tanProp.GetInt32());
                foreach (var t in tangents)
                {
                    float len = MathF.Sqrt(t.X * t.X + t.Y * t.Y + t.Z * t.Z);
                    Assert.InRange(len, 0.999f, 1.001f);
                    Assert.True(MathF.Abs(t.W - 1f) < 1e-5f || MathF.Abs(t.W + 1f) < 1e-5f);
                }

                // Verify Morph Targets & extras.targetNames
                Assert.True(prim0.TryGetProperty("targets", out var targets));
                Assert.Equal(2, targets.GetArrayLength());
                Assert.True(targets[0].TryGetProperty("POSITION", out var morphPosProp));
                Assert.True(targets[0].TryGetProperty("NORMAL", out _));
                Assert.True(targets[0].TryGetProperty("TANGENT", out var morphTanProp));
                Assert.Equal("VEC3", root.GetProperty("accessors")[morphTanProp.GetInt32()].GetProperty("type").GetString());
                var morphDeltas = ReadVec3Accessor(doc, morphPosProp.GetInt32());
                Assert.Equal(0.2f * scaleFactor, morphDeltas[1].Y, 4);

                var targetNames = bodyMesh.GetProperty("extras").GetProperty("targetNames");
                Assert.Equal(2, targetNames.GetArrayLength());
                Assert.Equal("Smile", targetNames[0].GetString());
                Assert.Equal("Blink", targetNames[1].GetString());
                Assert.Equal(2, bodyMesh.GetProperty("weights").GetArrayLength());

                // Verify PBR material: packed metallicRoughnessTexture (G=Roughness, B=Metallic) & BMP/TGA transcoded to PNG
                var pbrMat = root.GetProperty("materials")[0];
                var pbr = pbrMat.GetProperty("pbrMetallicRoughness");
                Assert.True(pbr.TryGetProperty("baseColorTexture", out _));
                Assert.True(pbr.TryGetProperty("metallicRoughnessTexture", out var mrTexProp));
                Assert.True(pbrMat.TryGetProperty("normalTexture", out _));
                Assert.True(pbrMat.TryGetProperty("occlusionTexture", out _));
                Assert.True(pbrMat.TryGetProperty("emissiveTexture", out _));
                Assert.True(pbrMat.GetProperty("doubleSided").GetBoolean());

                // Inspect packed MR PNG pixel channels: Metallic R=200 -> B=200, Smoothness A=180 -> Roughness G=75
                int mrTexIdx = mrTexProp.GetProperty("index").GetInt32();
                int mrImgIdx = root.GetProperty("textures")[mrTexIdx].GetProperty("source").GetInt32();
                var mrBytes = ReadImageBytes(doc, mrImgIdx, outPath);
                using var mrBmp = ImageExtensions.DecodeBgraUnpremul(mrBytes);
                Assert.NotNull(mrBmp);
                var px = mrBmp!.GetPixel(0, 0);
                Assert.InRange(px.Blue, 198, 202);   // B = Metallic (~200)
                Assert.InRange(px.Green, 73, 77);    // G = Roughness (255 - 180 = 75)

                // Verify transparent/mask material
                var maskMat = root.GetProperty("materials")[1];
                Assert.Equal("MASK", maskMat.GetProperty("alphaMode").GetString());
                Assert.Equal(0.4f, maskMat.GetProperty("alphaCutoff").GetSingle(), 3);

                // Verify Rest-Pose Skinning Identity: GlobalJoint * IBM == GlobalMesh (Identity for our root mesh)
                VerifySkinRestPoseIdentity(doc, skinIndex: 0);

                // Verify Animation has translation, rotation, scale, and morph weights channels
                var anim = root.GetProperty("animations")[0];
                var paths = new HashSet<string>(
                    anim.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("target").GetProperty("path").GetString()!));
                Assert.Contains("translation", paths);
                Assert.Contains("rotation", paths);
                Assert.Contains("scale", paths);
                Assert.Contains("weights", paths);

                // Verify morph weight normalization (Unity 0..100 -> glTF 0..1)
                var weightsChannel = anim.GetProperty("channels").EnumerateArray()
                    .First(c => c.GetProperty("target").GetProperty("path").GetString() == "weights");
                var weightsSampler = anim.GetProperty("samplers")[weightsChannel.GetProperty("sampler").GetInt32()];
                var weightOut = ReadScalarFloatAccessor(doc, weightsSampler.GetProperty("output").GetInt32());
                Assert.True(weightOut.Length >= 4); // >= 2 keyframes * 2 targets
                Assert.Equal(0.0f, weightOut[0], 4);
                Assert.Equal(0.5f, weightOut[2], 4); // 50 / 100 = 0.5 at keyframe 1, target 0
                Assert.Equal(1.0f, weightOut[3], 4); // 100 / 100 = 1.0 at keyframe 1, target 1
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_LargeIndexBuffer_UsesUnsignedIntAndNeverUses65535PrimitiveRestart()
        {
            var tempDir = CreateTempDirectory("GltfUInt32Indices");
            try
            {
                // 65536 vertices so max index is 65535 -> MUST use UNSIGNED_INT (5125), NEVER UNSIGNED_SHORT (5123)!
                var root = new ImportedFrame { Name = "BigRoot", LocalScale = new Vector3(1, 1, 1) };
                var mesh = new ImportedMesh
                {
                    Path = "BigRoot",
                    hasNormal = false,
                    hasUV = Array.Empty<bool>()
                };

                const int vertCount = 65536;
                mesh.VertexList = new List<ImportedVertex>(vertCount);
                for (int i = 0; i < vertCount; i++)
                {
                    mesh.VertexList.Add(new ImportedVertex
                    {
                        Vertex = new Vector3(i * 0.001f, (i & 1) * 0.1f, 0f)
                    });
                }

                mesh.SubmeshList.Add(new ImportedSubmesh
                {
                    Material = "Default",
                    FaceList = new List<ImportedFace>
                    {
                        new ImportedFace { VertexIndices = new[] { 0, 1, 65535 } }
                    }
                });

                var imported = new SimpleImported(root) { MeshList = { mesh } };
                string outPath = Path.Combine(tempDir, "big.glb");
                ModelExporter.ExportGltf(outPath, imported, binary: true);

                var doc = ValidateGltfOrGlbFile(outPath, expectBinary: true);
                int idxAcc = doc.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32();
                int compType = doc.RootElement.GetProperty("accessors")[idxAcc].GetProperty("componentType").GetInt32();
                Assert.Equal(5125, compType); // UNSIGNED_INT
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_CombineMeshes_MergesMultiMeshModelCleanlyIntoValidGlb()
        {
            var tempDir = CreateTempDirectory("GltfCombine");
            try
            {
                var imported = BuildFullFeaturedSyntheticModel();
                Assert.Equal(2, imported.MeshList.Count);

                string outPath = Path.Combine(tempDir, "combined.glb");
                ModelExporter.ExportGltf(outPath, imported, binary: true, combineMeshes: true);

                var doc = ValidateGltfOrGlbFile(outPath, expectBinary: true);
                Assert.Equal(1, doc.RootElement.GetProperty("meshes").GetArrayLength());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------------------------
        // ExportOptionsViewModel & AssetExportService Dispatch Tests
        // ------------------------------------------------------------------

        [Fact]
        public void ExportOptionsViewModel_ModelFormat_BindsSavesResetsAndTogglesFbxControls()
        {
            var state = new ExportOptionsState();
            Assert.Equal(ModelExportFormat.Fbx, state.ModelFormat);

            var vm = new ExportOptionsViewModel(state);
            Assert.Equal(3, vm.ModelFormatOptions.Count);
            Assert.Equal(0, vm.ModelFormatIndex);
            Assert.True(vm.IsFbxFormatSelected);

            var changedProps = new List<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName != null) changedProps.Add(e.PropertyName); };

            vm.ModelFormatIndex = 1; // GLB
            Assert.Equal(ModelExportFormat.Glb, vm.ModelFormat);
            Assert.False(vm.IsFbxFormatSelected);
            Assert.Contains(nameof(ExportOptionsViewModel.IsFbxFormatSelected), changedProps);

            vm.ModelFormat = ModelExportFormat.Gltf;
            Assert.Equal(2, vm.ModelFormatIndex);
            Assert.False(vm.IsFbxFormatSelected);

            // Clamping out-of-range index
            vm.ModelFormatIndex = 99;
            Assert.Equal(ModelExportFormat.Gltf, vm.ModelFormat);
            vm.ModelFormatIndex = -5;
            Assert.Equal(ModelExportFormat.Fbx, vm.ModelFormat);
            Assert.True(vm.IsFbxFormatSelected);

            // Save GLB to target state
            vm.ModelFormat = ModelExportFormat.Glb;
            vm.SaveCommand.Execute(null);
            Assert.Equal(ModelExportFormat.Glb, state.ModelFormat);

            // Clone / CopyFrom preserves ModelFormat
            var copy = new ExportOptionsState();
            copy.CopyFrom(state);
            Assert.Equal(ModelExportFormat.Glb, copy.ModelFormat);

            // ResetDefaults restores Fbx
            vm.ResetDefaultsCommand.Execute(null);
            Assert.Equal(ModelExportFormat.Fbx, vm.ModelFormat);
            Assert.True(vm.IsFbxFormatSelected);
        }

        [Theory]
        [InlineData(ModelExportFormat.Glb, ".glb", true)]
        [InlineData(ModelExportFormat.Gltf, ".gltf", false)]
        public void AssetExportService_ExportModel_DispatchesFormatAndSplitsAnimationsCorrectly(
            ModelExportFormat format, string expectedExt, bool expectBinary)
        {
            var tempDir = CreateTempDirectory($"ServiceDispatch_{format}");
            try
            {
                var imported = BuildFullFeaturedSyntheticModel();
                var options = new ExportOptionsState
                {
                    ModelFormat = format,
                    ExportSkins = true,
                    ExportAnimations = true,
                    ExportAnimationsSplit = true,
                    ExportBlendShape = true,
                    ExportUnityMeta = false,
                    ExportUnityAnimClips = false
                };

                Assert.Equal(expectedExt, AssetExportService.GetModelFileExtension(options));

                string mainPath = Path.Combine(tempDir, "Character" + expectedExt);
                AssetExportService.ExportModel(imported, mainPath, options);

                Assert.True(File.Exists(mainPath));
                var mainDoc = ValidateGltfOrGlbFile(mainPath, expectBinary);
                Assert.False(mainDoc.RootElement.TryGetProperty("animations", out _)); // base model has 0 animations when split

                string splitAnimPath = Path.Combine(tempDir, "Character_Wave" + expectedExt);
                Assert.True(File.Exists(splitAnimPath), $"Expected split animation file at {splitAnimPath}");
                var animDoc = ValidateGltfOrGlbFile(splitAnimPath, expectBinary);
                Assert.Equal(1, animDoc.RootElement.GetProperty("animations").GetArrayLength());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------------------------
        // Real Unity Corpus Read-Only Verification (Anima: Gate of Memories)
        // ------------------------------------------------------------------

        [Fact]
        public void GltfExporter_RealUnityModel_AnimaGateOfMemories_ValidatesGlbAndGltfCleanly()
        {
            const string resPath = @"F:\SteamLibrary\steamapps\common\Anima Gate of Memories\AnimaGateOfMemories_Data\resources.assets";
            if (!File.Exists(resPath))
            {
                _output.WriteLine("Skipping real-game glTF test (Anima Gate of Memories resources.assets not present on this host).");
                return;
            }

            var tempDir = CreateTempDirectory("GltfRealUnity");
            try
            {
                var am = new AssetsManager();
                am.LoadFiles(resPath);
                var sf = am.assetsFileList[0];

                // Locate Baal skinned mesh (PathID 1681), avatar (3367), and a real skeletal animation clip
                Mesh? baalMesh = null;
                Avatar? baalAvatar = null;
                AnimationClip? baalClip = null;

                foreach (var info in sf.m_Objects)
                {
                    if (info.classID == (int)ClassIDType.Mesh && info.m_PathID == 1681)
                    {
                        baalMesh = new Mesh(new ObjectReader(sf.reader, sf, info));
                    }
                    else if (info.classID == (int)ClassIDType.Avatar && info.m_PathID == 3367)
                    {
                        baalAvatar = new Avatar(new ObjectReader(sf.reader, sf, info));
                    }
                    else if (info.classID == (int)ClassIDType.AnimationClip && info.m_PathID == 2351)
                    {
                        baalClip = new AnimationClip(new ObjectReader(sf.reader, sf, info));
                    }
                }

                Assert.NotNull(baalMesh);
                Assert.NotNull(baalAvatar);

                var clips = baalClip != null ? new[] { baalClip } : null;
                var converted = ModelConverter.CreateFromModelGroup(
                    "Baal",
                    rootGameObject: null,
                    meshes: new[] { baalMesh! },
                    renderers: null,
                    avatar: baalAvatar,
                    animationClips: clips,
                    imageFormat: ImageFormat.Png,
                    combineMeshes: false);

                Assert.True(converted.MeshList.Count > 0, "Expected at least one converted mesh from real Unity asset.");

                string glbPath = Path.Combine(tempDir, "Baal.glb");
                string gltfPath = Path.Combine(tempDir, "Baal.gltf");

                ModelExporter.ExportGltf(glbPath, converted, binary: true, skins: true, animation: true, blendShape: true);
                ModelExporter.ExportGltf(gltfPath, converted, binary: false, skins: true, animation: true, blendShape: true);

                var glbDoc = ValidateGltfOrGlbFile(glbPath, expectBinary: true);
                var gltfDoc = ValidateGltfOrGlbFile(gltfPath, expectBinary: false);

                Assert.True(glbDoc.RootElement.GetProperty("meshes").GetArrayLength() >= 1);
                Assert.True(glbDoc.RootElement.TryGetProperty("skins", out var skins) && skins.GetArrayLength() >= 1);
                Assert.True(gltfDoc.RootElement.GetProperty("meshes").GetArrayLength() >= 1);

                int animCount = glbDoc.RootElement.TryGetProperty("animations", out var anims) ? anims.GetArrayLength() : 0;
                _output.WriteLine($"Real Unity model 'Baal' validated: GLB={new FileInfo(glbPath).Length:N0} bytes, glTF={new FileInfo(gltfPath).Length:N0} bytes, nodes={glbDoc.RootElement.GetProperty("nodes").GetArrayLength()}, skins={skins.GetArrayLength()}, animations={animCount}");

                // Also export a real GameObject with MeshRenderer + Material + Texture2D from resources.assets when native texture decoder is available
                var nativeDecoderDll = Path.Combine(AppContext.BaseDirectory, "x64", "Texture2DDecoderNative.dll");
                if (File.Exists(nativeDecoderDll))
                {
                    GameObject? texturedGo = null;
                    foreach (var info in sf.m_Objects)
                    {
                        if (info.classID != (int)ClassIDType.MeshRenderer)
                        {
                            continue;
                        }
                        var mr = new MeshRenderer(new ObjectReader(sf.reader, sf, info));
                        if (mr.m_Materials.Length > 0 && mr.m_GameObject.TryGet(out var go) && go != null)
                        {
                            texturedGo = go;
                            break;
                        }
                    }

                    if (texturedGo != null)
                    {
                        var goConverted = new ModelConverter(texturedGo, ImageFormat.Png);
                        if (goConverted.MeshList.Count > 0)
                        {
                            string goGlb = Path.Combine(tempDir, "RealRenderer.glb");
                            ModelExporter.ExportGltf(goGlb, goConverted, binary: true);
                            var goDoc = ValidateGltfOrGlbFile(goGlb, expectBinary: true);
                            Assert.True(goDoc.RootElement.GetProperty("meshes").GetArrayLength() >= 1);
                            int matCount = goDoc.RootElement.TryGetProperty("materials", out var mats) ? mats.GetArrayLength() : 0;
                            int imgCount = goDoc.RootElement.TryGetProperty("images", out var imgs) ? imgs.GetArrayLength() : 0;
                            _output.WriteLine($"Real Unity renderer '{texturedGo.m_Name}' validated: GLB={new FileInfo(goGlb).Length:N0} bytes, materials={matCount}, images={imgCount}");
                        }
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_NonFiniteValues_TextureTransform_AndEmptyScene_RemainStrictSpecValid()
        {
            var tempDir = CreateTempDirectory("GltfEdgeCases");
            try
            {
                // 1. Model with NaN/Infinity values + non-default UV tiling/offset (KHR_texture_transform) + separate roughness map
                var root = new ImportedFrame
                {
                    Name = "SanitizedRoot",
                    LocalPosition = new Vector3(float.NaN, float.PositiveInfinity, -0.0f),
                    LocalRotation = new Vector3(float.NaN, 0f, float.NegativeInfinity),
                    LocalScale = new Vector3(float.NaN, 1f, 1f)
                };

                var mesh = new ImportedMesh
                {
                    Path = "SanitizedRoot",
                    hasNormal = true,
                    hasTangent = true,
                    hasColor = true,
                    hasUV = new[] { true, false, false, false, false, false, false, false },
                    VertexList = new List<ImportedVertex>
                    {
                        new ImportedVertex
                        {
                            Vertex = new Vector3(float.NaN, 0f, 0f),
                            Normal = new Vector3(0f, 0f, 0f), // zero-length normal -> fallback unit Y
                            Tangent = new Vector4(0f, 0f, 0f, float.NaN),
                            Color = new Color(2.5f, -1f, float.NaN, 1f),
                            UV = new[] { new[] { float.PositiveInfinity, 0.5f } }
                        },
                        new ImportedVertex { Vertex = new Vector3(1f, 0f, 0f), Normal = new Vector3(0f, 0f, 1f), Tangent = new Vector4(1f, 0f, 0f, 1f), Color = new Color(1, 1, 1, 1), UV = new[] { new[] { 1f, 0f } } },
                        new ImportedVertex { Vertex = new Vector3(0f, 1f, 0f), Normal = new Vector3(0f, 0f, 1f), Tangent = new Vector4(1f, 0f, 0f, 1f), Color = new Color(1, 1, 1, 1), UV = new[] { new[] { 0f, 1f } } }
                    },
                    SubmeshList = new List<ImportedSubmesh>
                    {
                        new ImportedSubmesh
                        {
                            Material = "TiledMat",
                            FaceList = new List<ImportedFace>
                            {
                                new ImportedFace { VertexIndices = new[] { 0, 1, 2 } }
                            }
                        }
                    }
                };

                var tiledMat = new ImportedMaterial
                {
                    Name = "TiledMat",
                    Diffuse = new Color(1f, 1f, 1f, 1f),
                    Textures = new List<ImportedMaterialTexture>
                    {
                        new ImportedMaterialTexture { Name = "tile.png", Dest = 0, Scale = new Vector2(2f, 3f), Offset = new Vector2(0.1f, 0.2f) },
                        new ImportedMaterialTexture { Name = "roughness.png", Dest = 8, Scale = new Vector2(1f, 1f) }
                    }
                };

                var anim = new ImportedKeyframedAnimation { Name = "NoisyAnim" };
                var tr = anim.FindTrack("SanitizedRoot");
                tr.Translations.Add(new ImportedKeyframe<Vector3>(-1f, new Vector3(float.NaN, 1f, 0f)));
                tr.Translations.Add(new ImportedKeyframe<Vector3>(float.NaN, new Vector3(0f, 2f, 0f)));
                tr.Translations.Add(new ImportedKeyframe<Vector3>(0.5f, new Vector3(1f, 2f, 3f)));

                var imported = new SimpleImported(root);
                imported.MeshList.Add(mesh);
                imported.MaterialList.Add(tiledMat);
                imported.TextureList.Add(new ImportedTexture(new MemoryStream(CreateSolidImageBytes(2, 2, 100, 150, 200, 255, ImageFormat.Png)), "tile.png"));
                imported.TextureList.Add(new ImportedTexture(new MemoryStream(CreateSolidImageBytes(2, 2, 120, 120, 120, 255, ImageFormat.Png)), "roughness.png"));
                imported.AnimationList.Add(anim);

                string edgeGlb = Path.Combine(tempDir, "edge.glb");
                ModelExporter.ExportGltf(edgeGlb, imported, binary: true);
                var edgeDoc = ValidateGltfOrGlbFile(edgeGlb, expectBinary: true);

                // Verify KHR_texture_transform registered and offset converted: v_g_off = 1 - 0.2 - 3 = -2.2
                var exts = edgeDoc.RootElement.GetProperty("extensionsUsed");
                Assert.Contains("KHR_texture_transform", exts.EnumerateArray().Select(e => e.GetString()));
                var khr = edgeDoc.RootElement.GetProperty("materials")[0]
                    .GetProperty("pbrMetallicRoughness")
                    .GetProperty("baseColorTexture")
                    .GetProperty("extensions")
                    .GetProperty("KHR_texture_transform");
                Assert.Equal(0.1f, khr.GetProperty("offset")[0].GetSingle(), 4);
                Assert.Equal(-2.2f, khr.GetProperty("offset")[1].GetSingle(), 4);
                Assert.Equal(2f, khr.GetProperty("scale")[0].GetSingle(), 4);
                Assert.Equal(3f, khr.GetProperty("scale")[1].GetSingle(), 4);

                // 2. Empty scene produces minimal valid GLB with no empty top-level arrays
                var emptyImported = new SimpleImported(new ImportedFrame { Name = "Empty" });
                string emptyGlb = Path.Combine(tempDir, "empty.glb");
                ModelExporter.ExportGltf(emptyGlb, emptyImported, binary: true);
                var emptyDoc = ValidateGltfOrGlbFile(emptyGlb, expectBinary: true);
                Assert.Equal("2.0", emptyDoc.RootElement.GetProperty("asset").GetProperty("version").GetString());
                Assert.False(emptyDoc.RootElement.TryGetProperty("meshes", out _));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------------------------
        // Strict glTF 2.0 / GLB v2 Validator Helper
        // ------------------------------------------------------------------

        private sealed class ParsedGltf
        {
            public JsonDocument Json { get; }
            public JsonElement RootElement => Json.RootElement;
            public byte[] BinData { get; }

            public ParsedGltf(JsonDocument json, byte[] binData)
            {
                Json = json;
                BinData = binData;
            }
        }

        private static ParsedGltf ValidateGltfOrGlbFile(string filePath, bool expectBinary)
        {
            byte[] jsonBytes;
            byte[] binBytes = Array.Empty<byte>();
            string dir = Path.GetDirectoryName(filePath) ?? string.Empty;

            if (expectBinary)
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                Assert.True(fileBytes.Length >= 20, "GLB file too small.");

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(0, 4));
                uint version = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(4, 4));
                uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(8, 4));

                Assert.Equal(0x46546C67u, magic);
                Assert.Equal(2u, version);
                Assert.Equal((uint)fileBytes.Length, totalLength);

                // Chunk 0: JSON
                uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(12, 4));
                uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(16, 4));
                Assert.Equal(0x4E4F534Au, jsonType);
                Assert.Equal(0u, jsonLen % 4u);
                Assert.True(20L + jsonLen <= fileBytes.Length);

                jsonBytes = fileBytes.AsSpan(20, (int)jsonLen).ToArray();
                // Trailing JSON padding bytes (after final '}') must be 0x20 spaces
                int lastBrace = Array.LastIndexOf(jsonBytes, (byte)'}');
                Assert.True(lastBrace > 0);
                for (int i = lastBrace + 1; i < jsonBytes.Length; i++)
                {
                    Assert.Equal((byte)0x20, jsonBytes[i]);
                }

                int nextOffset = 20 + (int)jsonLen;
                if (nextOffset < fileBytes.Length)
                {
                    Assert.True(nextOffset + 8 <= fileBytes.Length);
                    uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(nextOffset, 4));
                    uint binType = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(nextOffset + 4, 4));
                    Assert.Equal(0x004E4942u, binType);
                    Assert.Equal(0u, binLen % 4u);
                    Assert.Equal(fileBytes.Length, nextOffset + 8 + (int)binLen);
                    binBytes = fileBytes.AsSpan(nextOffset + 8, (int)binLen).ToArray();
                }
            }
            else
            {
                jsonBytes = File.ReadAllBytes(filePath);
            }

            // Ensure no raw NaN/Infinity tokens appear in JSON
            string jsonText = Encoding.UTF8.GetString(jsonBytes);
            Assert.DoesNotContain("NaN", jsonText, StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", jsonText, StringComparison.Ordinal);

            var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            // Load external .bin for separate .gltf
            if (!expectBinary && root.TryGetProperty("buffers", out var buffersProp) && buffersProp.GetArrayLength() > 0)
            {
                var buf0 = buffersProp[0];
                string uri = buf0.GetProperty("uri").GetString()!;
                int declaredLen = buf0.GetProperty("byteLength").GetInt32();
                string binPath = Path.Combine(dir, Uri.UnescapeDataString(uri));
                Assert.True(File.Exists(binPath), $"External .bin buffer missing: {binPath}");
                binBytes = File.ReadAllBytes(binPath);
                Assert.Equal(declaredLen, binBytes.Length);
            }

            // Top-level arrays must never be empty per glTF 2.0 spec
            string[] nonEmptyArrays = { "nodes", "meshes", "materials", "skins", "animations", "textures", "images", "samplers", "buffers", "bufferViews", "accessors" };
            foreach (var arrName in nonEmptyArrays)
            {
                if (root.TryGetProperty(arrName, out var arr))
                {
                    Assert.True(arr.GetArrayLength() > 0, $"Top-level '{arrName}' array must not be empty.");
                }
            }

            var parsed = new ParsedGltf(doc, binBytes);

            // Validate bufferViews: 4-byte alignment and bounds
            if (root.TryGetProperty("bufferViews", out var bvs))
            {
                for (int i = 0; i < bvs.GetArrayLength(); i++)
                {
                    var bv = bvs[i];
                    int offset = bv.GetProperty("byteOffset").GetInt32();
                    int len = bv.GetProperty("byteLength").GetInt32();
                    Assert.Equal(0, offset % 4);
                    Assert.True(offset >= 0 && len > 0 && offset + len <= binBytes.Length,
                        $"bufferView[{i}] out of bounds: offset={offset}, len={len}, bin={binBytes.Length}");
                }
            }

            // Validate accessors: bounds, finite floats, min/max accuracy
            if (root.TryGetProperty("accessors", out var accs))
            {
                for (int i = 0; i < accs.GetArrayLength(); i++)
                {
                    var acc = accs[i];
                    int bvIdx = acc.GetProperty("bufferView").GetInt32();
                    int bvOffset = root.GetProperty("bufferViews")[bvIdx].GetProperty("byteOffset").GetInt32();
                    int bvLen = root.GetProperty("bufferViews")[bvIdx].GetProperty("byteLength").GetInt32();
                    int accOffset = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
                    int compType = acc.GetProperty("componentType").GetInt32();
                    int count = acc.GetProperty("count").GetInt32();
                    string type = acc.GetProperty("type").GetString()!;

                    Assert.True(count > 0);
                    int elemSize = GetComponentByteSize(compType) * GetTypeComponentCount(type);
                    Assert.True(accOffset + count * elemSize <= bvLen, $"accessor[{i}] exceeds bufferView[{bvIdx}]");

                    if (compType == 5126) // FLOAT
                    {
                        int compCount = GetTypeComponentCount(type);
                        var computedMin = Enumerable.Repeat(float.PositiveInfinity, compCount).ToArray();
                        var computedMax = Enumerable.Repeat(float.NegativeInfinity, compCount).ToArray();

                        int baseOff = bvOffset + accOffset;
                        for (int k = 0; k < count; k++)
                        {
                            for (int c = 0; c < compCount; c++)
                            {
                                float val = BinaryPrimitives.ReadSingleLittleEndian(binBytes.AsSpan(baseOff + (k * compCount + c) * 4, 4));
                                Assert.True(float.IsFinite(val), $"Non-finite float in accessor[{i}] at [{k},{c}].");
                                if (val < computedMin[c]) computedMin[c] = val;
                                if (val > computedMax[c]) computedMax[c] = val;
                            }
                        }

                        if (acc.TryGetProperty("min", out var minProp) && acc.TryGetProperty("max", out var maxProp))
                        {
                            Assert.Equal(compCount, minProp.GetArrayLength());
                            Assert.Equal(compCount, maxProp.GetArrayLength());
                            for (int c = 0; c < compCount; c++)
                            {
                                Assert.InRange(MathF.Abs(minProp[c].GetSingle() - computedMin[c]), 0f, 1e-4f);
                                Assert.InRange(MathF.Abs(maxProp[c].GetSingle() - computedMax[c]), 0f, 1e-4f);
                            }
                        }
                    }
                }
            }

            // Validate nodes: unit rotation quaternions
            if (root.TryGetProperty("nodes", out var nodes))
            {
                for (int i = 0; i < nodes.GetArrayLength(); i++)
                {
                    var node = nodes[i];
                    if (node.TryGetProperty("rotation", out var rot))
                    {
                        float x = rot[0].GetSingle(), y = rot[1].GetSingle(), z = rot[2].GetSingle(), w = rot[3].GetSingle();
                        float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
                        Assert.InRange(len, 0.999f, 1.001f);
                    }
                }
            }

            // Validate meshes: POSITION min/max, index bounds, no uint16 65535 restart, WEIGHTS_0 sum == 1.0, morph target POSITION min/max
            if (root.TryGetProperty("meshes", out var meshes))
            {
                for (int m = 0; m < meshes.GetArrayLength(); m++)
                {
                    var prims = meshes[m].GetProperty("primitives");
                    Assert.True(prims.GetArrayLength() > 0);
                    for (int p = 0; p < prims.GetArrayLength(); p++)
                    {
                        var prim = prims[p];
                        var attrs = prim.GetProperty("attributes");
                        int posAccIdx = attrs.GetProperty("POSITION").GetInt32();
                        var posAcc = root.GetProperty("accessors")[posAccIdx];
                        Assert.True(posAcc.TryGetProperty("min", out _), "POSITION accessor MUST have min.");
                        Assert.True(posAcc.TryGetProperty("max", out _), "POSITION accessor MUST have max.");
                        int vertCount = posAcc.GetProperty("count").GetInt32();

                        if (prim.TryGetProperty("indices", out var idxProp))
                        {
                            int idxAccIdx = idxProp.GetInt32();
                            var idxAcc = root.GetProperty("accessors")[idxAccIdx];
                            int compType = idxAcc.GetProperty("componentType").GetInt32();
                            var indices = ReadIndexAccessor(parsed, idxAccIdx);
                            foreach (var idx in indices)
                            {
                                Assert.True(idx < (uint)vertCount, $"Index {idx} out of vertex count {vertCount}.");
                                if (compType == 5123)
                                {
                                    Assert.NotEqual(65535u, idx);
                                }
                            }
                        }

                        if (attrs.TryGetProperty("WEIGHTS_0", out var wProp) && attrs.TryGetProperty("JOINTS_0", out var jProp))
                        {
                            var weights = ReadVec4Accessor(parsed, wProp.GetInt32());
                            var joints = ReadUShort4Accessor(parsed, jProp.GetInt32());
                            Assert.Equal(vertCount, weights.Length);
                            Assert.Equal(vertCount, joints.Length / 4);
                            for (int v = 0; v < vertCount; v++)
                            {
                                float sum = weights[v].X + weights[v].Y + weights[v].Z + weights[v].W;
                                Assert.InRange(sum, 0.999f, 1.001f);
                                if (weights[v].X == 0f) Assert.Equal(0, joints[v * 4 + 0]);
                                if (weights[v].Y == 0f) Assert.Equal(0, joints[v * 4 + 1]);
                                if (weights[v].Z == 0f) Assert.Equal(0, joints[v * 4 + 2]);
                                if (weights[v].W == 0f) Assert.Equal(0, joints[v * 4 + 3]);
                            }
                        }

                        if (prim.TryGetProperty("targets", out var targets))
                        {
                            for (int t = 0; t < targets.GetArrayLength(); t++)
                            {
                                int tPosAccIdx = targets[t].GetProperty("POSITION").GetInt32();
                                var tPosAcc = root.GetProperty("accessors")[tPosAccIdx];
                                Assert.Equal(vertCount, tPosAcc.GetProperty("count").GetInt32());
                                Assert.True(tPosAcc.TryGetProperty("min", out _), "Morph target POSITION MUST have min.");
                                Assert.True(tPosAcc.TryGetProperty("max", out _), "Morph target POSITION MUST have max.");
                            }
                        }
                    }
                }
            }

            // Validate images: valid PNG/JPEG magic bytes
            if (root.TryGetProperty("images", out var images))
            {
                for (int i = 0; i < images.GetArrayLength(); i++)
                {
                    var img = images[i];
                    string mime = img.GetProperty("mimeType").GetString()!;
                    Assert.True(mime == "image/png" || mime == "image/jpeg", $"Unsupported glTF mimeType: {mime}");
                    byte[] imgBytes = ReadImageBytes(parsed, i, filePath);
                    if (mime == "image/png")
                    {
                        Assert.True(imgBytes.Length >= 8 && imgBytes[0] == 0x89 && imgBytes[1] == 0x50 && imgBytes[2] == 0x4E && imgBytes[3] == 0x47);
                    }
                    else
                    {
                        Assert.True(imgBytes.Length >= 3 && imgBytes[0] == 0xFF && imgBytes[1] == 0xD8 && imgBytes[2] == 0xFF);
                    }
                }
            }

            // Validate animations: strictly increasing input timestamps with min/max, unit rotation outputs
            if (root.TryGetProperty("animations", out var anims))
            {
                for (int a = 0; a < anims.GetArrayLength(); a++)
                {
                    var anim = anims[a];
                    var channels = anim.GetProperty("channels");
                    var samplers = anim.GetProperty("samplers");
                    Assert.True(channels.GetArrayLength() > 0);
                    Assert.True(samplers.GetArrayLength() > 0);

                    for (int c = 0; c < channels.GetArrayLength(); c++)
                    {
                        var ch = channels[c];
                        int smpIdx = ch.GetProperty("sampler").GetInt32();
                        string path = ch.GetProperty("target").GetProperty("path").GetString()!;
                        var smp = samplers[smpIdx];
                        int inAccIdx = smp.GetProperty("input").GetInt32();
                        var inAcc = root.GetProperty("accessors")[inAccIdx];
                        Assert.True(inAcc.TryGetProperty("min", out _), "Animation input accessor MUST have min.");
                        Assert.True(inAcc.TryGetProperty("max", out _), "Animation input accessor MUST have max.");

                        var times = ReadScalarFloatAccessor(parsed, inAccIdx);
                        Assert.True(times.Length > 0);
                        Assert.True(times[0] >= 0f);
                        for (int k = 1; k < times.Length; k++)
                        {
                            Assert.True(times[k] > times[k - 1], $"Animation timestamps not strictly increasing: {times[k - 1]} -> {times[k]}");
                        }

                        if (path == "rotation")
                        {
                            var rots = ReadVec4Accessor(parsed, smp.GetProperty("output").GetInt32());
                            Assert.Equal(times.Length, rots.Length);
                            for (int k = 0; k < rots.Length; k++)
                            {
                                float len = MathF.Sqrt(rots[k].X * rots[k].X + rots[k].Y * rots[k].Y + rots[k].Z * rots[k].Z + rots[k].W * rots[k].W);
                                Assert.InRange(len, 0.999f, 1.001f);
                            }
                        }
                    }
                }
            }

            return parsed;
        }

        private static void VerifySkinRestPoseIdentity(ParsedGltf doc, int skinIndex)
        {
            var root = doc.RootElement;
            var nodes = root.GetProperty("nodes");
            int nodeCount = nodes.GetArrayLength();

            var localRowMats = new System.Numerics.Matrix4x4[nodeCount];
            var parentOf = Enumerable.Repeat(-1, nodeCount).ToArray();

            for (int i = 0; i < nodeCount; i++)
            {
                var n = nodes[i];
                var t = System.Numerics.Vector3.Zero;
                var r = System.Numerics.Quaternion.Identity;
                var s = System.Numerics.Vector3.One;

                if (n.TryGetProperty("translation", out var tp))
                    t = new System.Numerics.Vector3(tp[0].GetSingle(), tp[1].GetSingle(), tp[2].GetSingle());
                if (n.TryGetProperty("rotation", out var rp))
                    r = new System.Numerics.Quaternion(rp[0].GetSingle(), rp[1].GetSingle(), rp[2].GetSingle(), rp[3].GetSingle());
                if (n.TryGetProperty("scale", out var sp))
                    s = new System.Numerics.Vector3(sp[0].GetSingle(), sp[1].GetSingle(), sp[2].GetSingle());

                localRowMats[i] =
                    System.Numerics.Matrix4x4.CreateScale(s) *
                    System.Numerics.Matrix4x4.CreateFromQuaternion(r) *
                    System.Numerics.Matrix4x4.CreateTranslation(t);

                if (n.TryGetProperty("children", out var ch))
                {
                    for (int c = 0; c < ch.GetArrayLength(); c++)
                    {
                        parentOf[ch[c].GetInt32()] = i;
                    }
                }
            }

            System.Numerics.Matrix4x4 GetGlobalRow(int idx)
            {
                var mat = localRowMats[idx];
                int p = parentOf[idx];
                while (p >= 0)
                {
                    mat = mat * localRowMats[p];
                    p = parentOf[p];
                }
                return mat;
            }

            var skin = root.GetProperty("skins")[skinIndex];
            var joints = skin.GetProperty("joints");
            var ibms = ReadMat4AccessorAsRowVector(doc, skin.GetProperty("inverseBindMatrices").GetInt32());
            Assert.Equal(joints.GetArrayLength(), ibms.Length);

            // In row-vector convention: IBM_row * GlobalJoint_row == Identity (equivalent to GlobalJoint_col * IBM_col == Identity)
            for (int j = 0; j < joints.GetArrayLength(); j++)
            {
                var jointGlobal = GetGlobalRow(joints[j].GetInt32());
                var prod = ibms[j] * jointGlobal;
                AssertMatrixNear(System.Numerics.Matrix4x4.Identity, prod, 1e-3f);
            }
        }

        private static void AssertMatrixNear(System.Numerics.Matrix4x4 expected, System.Numerics.Matrix4x4 actual, float eps)
        {
            Assert.InRange(MathF.Abs(expected.M11 - actual.M11), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M12 - actual.M12), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M13 - actual.M13), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M14 - actual.M14), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M21 - actual.M21), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M22 - actual.M22), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M23 - actual.M23), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M24 - actual.M24), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M31 - actual.M31), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M32 - actual.M32), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M33 - actual.M33), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M34 - actual.M34), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M41 - actual.M41), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M42 - actual.M42), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M43 - actual.M43), 0f, eps);
            Assert.InRange(MathF.Abs(expected.M44 - actual.M44), 0f, eps);
        }

        // ------------------------------------------------------------------
        // Binary Accessor Readers for Validator
        // ------------------------------------------------------------------

        private static byte[] ReadImageBytes(ParsedGltf doc, int imageIndex, string gltfPath)
        {
            var img = doc.RootElement.GetProperty("images")[imageIndex];
            if (img.TryGetProperty("bufferView", out var bvProp))
            {
                var bv = doc.RootElement.GetProperty("bufferViews")[bvProp.GetInt32()];
                return doc.BinData.AsSpan(bv.GetProperty("byteOffset").GetInt32(), bv.GetProperty("byteLength").GetInt32()).ToArray();
            }

            string uri = img.GetProperty("uri").GetString()!;
            string fullPath = Path.Combine(Path.GetDirectoryName(gltfPath)!, Uri.UnescapeDataString(uri));
            Assert.True(File.Exists(fullPath), $"External image missing: {fullPath}");
            return File.ReadAllBytes(fullPath);
        }

        private static float[] ReadScalarFloatAccessor(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, _) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new float[count];
            for (int i = 0; i < count; i++)
                arr[i] = BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(baseOff + i * 4, 4));
            return arr;
        }

        private static System.Numerics.Vector2[] ReadVec2Accessor(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, _) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new System.Numerics.Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int o = baseOff + i * 8;
                arr[i] = new System.Numerics.Vector2(
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 4, 4)));
            }
            return arr;
        }

        private static System.Numerics.Vector3[] ReadVec3Accessor(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, _) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new System.Numerics.Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int o = baseOff + i * 12;
                arr[i] = new System.Numerics.Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 8, 4)));
            }
            return arr;
        }

        private static System.Numerics.Vector4[] ReadVec4Accessor(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, _) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new System.Numerics.Vector4[count];
            for (int i = 0; i < count; i++)
            {
                int o = baseOff + i * 16;
                arr[i] = new System.Numerics.Vector4(
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 8, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 12, 4)));
            }
            return arr;
        }

        private static ushort[] ReadUShort4Accessor(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, _) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new ushort[count * 4];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = BinaryPrimitives.ReadUInt16LittleEndian(doc.BinData.AsSpan(baseOff + i * 2, 2));
            return arr;
        }

        private static uint[] ReadIndexAccessor(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, compType) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new uint[count];
            for (int i = 0; i < count; i++)
            {
                arr[i] = compType == 5123
                    ? BinaryPrimitives.ReadUInt16LittleEndian(doc.BinData.AsSpan(baseOff + i * 2, 2))
                    : BinaryPrimitives.ReadUInt32LittleEndian(doc.BinData.AsSpan(baseOff + i * 4, 4));
            }
            return arr;
        }

        private static System.Numerics.Matrix4x4[] ReadMat4AccessorAsRowVector(ParsedGltf doc, int accIdx)
        {
            var (baseOff, count, _) = GetAccessorSpanInfo(doc, accIdx);
            var arr = new System.Numerics.Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                int o = baseOff + i * 64;
                arr[i] = new System.Numerics.Matrix4x4(
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 0, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 8, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 12, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 16, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 20, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 24, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 28, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 32, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 36, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 40, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 44, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 48, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 52, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 56, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(doc.BinData.AsSpan(o + 60, 4)));
            }
            return arr;
        }

        private static (int BaseOffset, int Count, int ComponentType) GetAccessorSpanInfo(ParsedGltf doc, int accIdx)
        {
            var acc = doc.RootElement.GetProperty("accessors")[accIdx];
            var bv = doc.RootElement.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
            int accOff = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            return (bv.GetProperty("byteOffset").GetInt32() + accOff, acc.GetProperty("count").GetInt32(), acc.GetProperty("componentType").GetInt32());
        }

        private static int GetComponentByteSize(int compType) => compType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new InvalidOperationException($"Unknown componentType {compType}")
        };

        private static int GetTypeComponentCount(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => 16,
            _ => throw new InvalidOperationException($"Unknown accessor type {type}")
        };

        // ------------------------------------------------------------------
        // Synthetic Model Builder
        // ------------------------------------------------------------------

        private static SimpleImported BuildFullFeaturedSyntheticModel()
        {
            // Hierarchy: Hero -> Hips (translated +1 Y) -> Spine (translated +1 Y, rotated 30° Y)
            //            Hero -> Prop (rigid secondary mesh)
            var root = new ImportedFrame { Name = "Hero", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = new Vector3(1, 1, 1) };
            var hips = new ImportedFrame { Name = "Hips", LocalPosition = new Vector3(0, 1, 0), LocalRotation = Vector3.Zero, LocalScale = new Vector3(1, 1, 1) };
            var spine = new ImportedFrame { Name = "Spine", LocalPosition = new Vector3(0, 1, 0), LocalRotation = new Vector3(0, 30, 0), LocalScale = new Vector3(1, 1, 1) };
            var prop = new ImportedFrame { Name = "Prop", LocalPosition = new Vector3(2, 0, 0), LocalRotation = Vector3.Zero, LocalScale = new Vector3(1, 1, 1) };

            root.AddChild(hips);
            hips.AddChild(spine);
            root.AddChild(prop);

            // Compute exact inverse bind poses matching the hierarchy
            var hipsGlobal = System.Numerics.Matrix4x4.CreateTranslation(0, 1, 0);
            var spineLocal =
                System.Numerics.Matrix4x4.CreateRotationY((float)(30.0 * Math.PI / 180.0)) *
                System.Numerics.Matrix4x4.CreateTranslation(0, 1, 0);
            var spineGlobal = spineLocal * hipsGlobal;

            System.Numerics.Matrix4x4.Invert(hipsGlobal, out var hipsIbm);
            System.Numerics.Matrix4x4.Invert(spineGlobal, out var spineIbm);

            // Skinned + Morphed Body Mesh attached to Hero (tests ancestor-of-own-bones child node isolation too!)
            var bodyMesh = new ImportedMesh
            {
                Path = "Hero",
                hasNormal = true,
                hasTangent = true,
                hasColor = true,
                hasUV = new[] { true, true, false, false, false, false, false, false },
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex
                    {
                        Vertex = new Vector3(0, 0, 0),
                        Normal = new Vector3(0, 0, 1),
                        Tangent = new Vector4(1, 0, 0, 1),
                        Color = new Color(1, 1, 1, 1),
                        UV = new[] { new[] { 0.25f, 0.2f }, new[] { 0.1f, 0.25f } },
                        BoneIndices = new[] { 0, 1, 0, 0 },
                        Weights = new[] { 0.8f, 0.2f, 0f, 0f }
                    },
                    new ImportedVertex
                    {
                        Vertex = new Vector3(1, 0, 0),
                        Normal = new Vector3(0, 0, 1),
                        Tangent = new Vector4(1, 0, 0, -1),
                        Color = new Color(0.5f, 0.7f, 0.9f, 1),
                        UV = new[] { new[] { 0.75f, 0.2f }, new[] { 0.9f, 0.25f } },
                        BoneIndices = new[] { 0, 1, 0, 0 },
                        Weights = new[] { 0.5f, 0.5f, 0f, 0f }
                    },
                    new ImportedVertex
                    {
                        Vertex = new Vector3(0, 1, 0),
                        Normal = new Vector3(0, 0, 1),
                        Tangent = new Vector4(1, 0, 0, 1),
                        Color = new Color(1, 0.2f, 0.2f, 1),
                        UV = new[] { new[] { 0.25f, 0.8f }, new[] { 0.1f, 0.9f } },
                        BoneIndices = new[] { 1, 0, 0, 0 },
                        Weights = new[] { 1.0f, 0f, 0f, 0f }
                    },
                    new ImportedVertex
                    {
                        Vertex = new Vector3(1, 1, 0),
                        Normal = new Vector3(0, 0, 1),
                        Tangent = new Vector4(1, 0, 0, 1),
                        Color = new Color(0.2f, 1, 0.2f, 1),
                        UV = new[] { new[] { 0.75f, 0.8f }, new[] { 0.9f, 0.9f } },
                        BoneIndices = new[] { 1, 0, 0, 0 },
                        Weights = new[] { 0f, 0f, 0f, 0f } // zero weights -> must normalize cleanly to joint 0, weight 1.0
                    }
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh
                    {
                        Material = "HeroPbr",
                        FaceList = new List<ImportedFace>
                        {
                            new ImportedFace { VertexIndices = new[] { 0, 1, 2 } },
                            new ImportedFace { VertexIndices = new[] { 1, 3, 2 } }
                        }
                    }
                },
                BoneList = new List<ImportedBone>
                {
                    new ImportedBone { Path = "Hero/Hips", Matrix = NumericsRowToAssetStudioMatrix(hipsIbm) },
                    new ImportedBone { Path = "Hero/Hips/Spine", Matrix = NumericsRowToAssetStudioMatrix(spineIbm) }
                }
            };

            // Rigid secondary mesh on Prop
            var propMesh = new ImportedMesh
            {
                Path = "Hero/Prop",
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false },
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex { Vertex = new Vector3(0, 0, 0), Normal = new Vector3(0, 1, 0), UV = new[] { new[] { 0f, 0f } } },
                    new ImportedVertex { Vertex = new Vector3(1, 0, 0), Normal = new Vector3(0, 1, 0), UV = new[] { new[] { 1f, 0f } } },
                    new ImportedVertex { Vertex = new Vector3(0, 0, 1), Normal = new Vector3(0, 1, 0), UV = new[] { new[] { 0f, 1f } } }
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh
                    {
                        Material = "PropMask",
                        FaceList = new List<ImportedFace>
                        {
                            new ImportedFace { VertexIndices = new[] { 0, 1, 2 } }
                        }
                    }
                }
            };

            // Textures: PNG albedo, BMP normal (must transcode to PNG), TGA metallic+smoothness (must transcode & pack), JPEG emissive, PNG occlusion
            var albedoPng = CreateSolidImageBytes(4, 4, 220, 100, 80, 255, ImageFormat.Png);
            var normalBmp = CreateSolidImageBytes(4, 4, 128, 128, 255, 255, ImageFormat.Bmp);
            var metalSmoothTga = CreateSolidImageBytes(4, 4, 200, 0, 0, 180, ImageFormat.Tga); // R=200 metallic, A=180 smoothness -> Roughness=75
            var emissiveJpg = CreateSolidImageBytes(4, 4, 50, 200, 255, 255, ImageFormat.Jpeg);
            var occlusionPng = CreateSolidImageBytes(4, 4, 230, 230, 230, 255, ImageFormat.Png);

            var pbrMat = new ImportedMaterial
            {
                Name = "HeroPbr",
                Diffuse = new Color(1, 1, 1, 1),
                Emissive = new Color(0.2f, 0.4f, 0.6f, 1),
                DoubleSided = true,
                BumpScale = 1.2f,
                OcclusionStrength = 0.85f,
                Textures = new List<ImportedMaterialTexture>
                {
                    new ImportedMaterialTexture { Name = "albedo.png", Dest = 0, Scale = new Vector2(1, 1) },
                    new ImportedMaterialTexture { Name = "normal.bmp", Dest = 1, Scale = new Vector2(1, 1) },
                    new ImportedMaterialTexture { Name = "metallic.tga", Dest = 3, Scale = new Vector2(1, 1) },
                    new ImportedMaterialTexture { Name = "emissive.jpg", Dest = 4, Scale = new Vector2(1, 1) },
                    new ImportedMaterialTexture { Name = "occlusion.png", Dest = 5, Scale = new Vector2(1, 1) }
                }
            };

            var maskMat = new ImportedMaterial
            {
                Name = "PropMask",
                Diffuse = new Color(0.5f, 0.8f, 0.3f, 1),
                SurfaceMode = 1f, // MASK
                AlphaCutoff = 0.4f,
                Metallic = 0.25f,
                Smoothness = 0.7f
            };

            // Morph targets (Smile + Blink)
            var morph = new ImportedMorph
            {
                Path = "Hero",
                Channels = new List<ImportedMorphChannel>
                {
                    new ImportedMorphChannel
                    {
                        Name = "Smile",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new ImportedMorphKeyframe
                            {
                                Weight = 100f,
                                hasNormals = true,
                                hasTangents = true,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new ImportedMorphVertex
                                    {
                                        Index = 1,
                                        Vertex = new ImportedVertex
                                        {
                                            // Absolute position = base (1,0,0) + delta (0, 0.2, 0)
                                            Vertex = new Vector3(1, 0.2f, 0),
                                            Normal = new Vector3(0, 0.1f, 0),
                                            Tangent = new Vector4(0, 0.05f, 0, 0)
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new ImportedMorphChannel
                    {
                        Name = "Blink",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new ImportedMorphKeyframe
                            {
                                Weight = 100f,
                                hasNormals = false,
                                hasTangents = false,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new ImportedMorphVertex
                                    {
                                        Index = 2,
                                        Vertex = new ImportedVertex { Vertex = new Vector3(0, 0.85f, 0) }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            // Animation with TRS + duplicate timestamp dedupe + shortest-arc rotation + morph weights
            var anim = new ImportedKeyframedAnimation { Name = "Wave", SampleRate = 30f };
            var spineTrack = anim.FindTrack("Hero/Hips/Spine");
            spineTrack.Translations.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(0, 1, 0)));
            spineTrack.Translations.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(0, 1.05f, 0))); // duplicate timestamp test
            spineTrack.Translations.Add(new ImportedKeyframe<Vector3>(1f, new Vector3(0, 1.5f, 0)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(0, 0, 0)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Vector3>(0.5f, new Vector3(0, 120, 0)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Vector3>(1f, new Vector3(0, 240, 0))); // crosses 180° hemisphere
            spineTrack.Scalings.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(1, 1, 1)));
            spineTrack.Scalings.Add(new ImportedKeyframe<Vector3>(1f, new Vector3(1.1f, 1.1f, 1.1f)));

            var morphTrack = anim.FindTrack("Hero");
            var smileCurve = morphTrack.FindOrCreateBlendShape("blendShape.Smile");
            smileCurve.Keyframes.Add(new ImportedKeyframe<float>(0f, 0f));
            smileCurve.Keyframes.Add(new ImportedKeyframe<float>(1f, 50f));
            var blinkCurve = morphTrack.FindOrCreateBlendShape("Blink");
            blinkCurve.Keyframes.Add(new ImportedKeyframe<float>(0f, 0f));
            blinkCurve.Keyframes.Add(new ImportedKeyframe<float>(1f, 100f));

            var result = new SimpleImported(root);
            result.MeshList.Add(bodyMesh);
            result.MeshList.Add(propMesh);
            result.MaterialList.Add(pbrMat);
            result.MaterialList.Add(maskMat);
            result.TextureList.Add(new ImportedTexture(new MemoryStream(albedoPng), "albedo.png"));
            result.TextureList.Add(new ImportedTexture(new MemoryStream(normalBmp), "normal.bmp"));
            result.TextureList.Add(new ImportedTexture(new MemoryStream(metalSmoothTga), "metallic.tga"));
            result.TextureList.Add(new ImportedTexture(new MemoryStream(emissiveJpg), "emissive.jpg"));
            result.TextureList.Add(new ImportedTexture(new MemoryStream(occlusionPng), "occlusion.png"));
            result.MorphList.Add(morph);
            result.AnimationList.Add(anim);
            return result;
        }

        private static byte[] CreateSolidImageBytes(int width, int height, byte r, byte g, byte b, byte a, ImageFormat format)
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
            using var ms = bmp.ConvertToStream(format);
            return ms.ToArray();
        }

        private static Matrix4x4 NumericsRowToAssetStudioMatrix(System.Numerics.Matrix4x4 m)
        {
            var mat = new Matrix4x4();
            mat[0, 0] = m.M11; mat[0, 1] = m.M12; mat[0, 2] = m.M13; mat[0, 3] = m.M14;
            mat[1, 0] = m.M21; mat[1, 1] = m.M22; mat[1, 2] = m.M23; mat[1, 3] = m.M24;
            mat[2, 0] = m.M31; mat[2, 1] = m.M32; mat[2, 2] = m.M33; mat[2, 3] = m.M34;
            mat[3, 0] = m.M41; mat[3, 1] = m.M42; mat[3, 2] = m.M43; mat[3, 3] = m.M44;
            return mat;
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
                // Ignore temp cleanup race
            }
        }

        private sealed class SimpleImported : IImported
        {
            public ImportedFrame RootFrame { get; }
            public List<ImportedMesh> MeshList { get; } = new List<ImportedMesh>();
            public List<ImportedMaterial> MaterialList { get; } = new List<ImportedMaterial>();
            public List<ImportedTexture> TextureList { get; } = new List<ImportedTexture>();
            public List<ImportedKeyframedAnimation> AnimationList { get; } = new List<ImportedKeyframedAnimation>();
            public List<ImportedMorph> MorphList { get; } = new List<ImportedMorph>();

            public SimpleImported(ImportedFrame rootFrame)
            {
                RootFrame = rootFrame;
            }
        }
    }
}
