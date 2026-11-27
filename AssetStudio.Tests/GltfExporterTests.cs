using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

                // Verify COLOR_0 is omitted by default (matching 3D Preview) and TANGENT is present
                Assert.False(prim0.GetProperty("attributes").TryGetProperty("COLOR_0", out _));
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
            Assert.Equal(ModelExportFormat.Glb, state.ModelFormat);

            var vm = new ExportOptionsViewModel(state);
            Assert.Equal(3, vm.ModelFormatOptions.Count);
            Assert.Equal(1, vm.ModelFormatIndex);
            Assert.False(vm.IsFbxFormatSelected);

            var changedProps = new List<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName != null) changedProps.Add(e.PropertyName); };

            vm.ModelFormatIndex = 0; // FBX
            Assert.Equal(ModelExportFormat.Fbx, vm.ModelFormat);
            Assert.True(vm.IsFbxFormatSelected);
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

            // Save Fbx to target state
            vm.ModelFormat = ModelExportFormat.Fbx;
            vm.SaveCommand.Execute(null);
            Assert.Equal(ModelExportFormat.Fbx, state.ModelFormat);

            // Clone / CopyFrom preserves ModelFormat
            var copy = new ExportOptionsState();
            copy.CopyFrom(state);
            Assert.Equal(ModelExportFormat.Fbx, copy.ModelFormat);

            // ResetDefaults restores Glb
            vm.ResetDefaultsCommand.Execute(null);
            Assert.Equal(ModelExportFormat.Glb, vm.ModelFormat);
            Assert.False(vm.IsFbxFormatSelected);
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
        // Pure C# Regression Tests (Multi-Skin Canonicalization & PBR Packing)
        // ------------------------------------------------------------------

        [Fact]
        public void GltfExporter_MultiSkinAndTransformedSkinnedMorph_UnifiesSkeletonAndCanonicalizesArmatureSpace()
        {
            var tempDir = CreateTempDirectory("GltfMultiSkinCanon");
            try
            {
                var imported = BuildTransformedMultiSkinMorphModel(
                    out var expectedHeadMorphDeltaArmatureSpace,
                    out var origHeadVerts,
                    out var origHeadIbm);

                string glbPath = Path.Combine(tempDir, "multiskin_transformed.glb");
                ModelExporter.ExportGltf(glbPath, imported, binary: true, skins: true, animation: true, blendShape: true, scaleFactor: 1f);

                var doc = ValidateGltfOrGlbFile(glbPath, expectBinary: true);
                var root = doc.RootElement;
                var nodes = root.GetProperty("nodes");
                var skins = root.GetProperty("skins");
                var meshes = root.GetProperty("meshes");

                Assert.Equal(3, skins.GetArrayLength());

                // All 3 skins (BodyPart, HeadPart, MirroredPart) must share the exact same unified skin.skeleton (Armature)
                int sharedSkeleton = skins[0].GetProperty("skeleton").GetInt32();
                Assert.Equal("Armature", nodes[sharedSkeleton].GetProperty("name").GetString());
                Assert.Equal(sharedSkeleton, skins[1].GetProperty("skeleton").GetInt32());
                Assert.Equal(sharedSkeleton, skins[2].GetProperty("skeleton").GetInt32());

                // Compute global row matrices and parent map
                var (globalMats, parentOf) = ComputeNodeGlobalRowMatrices(root);
                Assert.True(System.Numerics.Matrix4x4.Invert(globalMats[sharedSkeleton], out var invArmaGlobal));

                // Verify every skin's IBMs satisfy IBM * LocalJointInArmature == Identity, and all joints are unique
                for (int s = 0; s < skins.GetArrayLength(); s++)
                {
                    var skin = skins[s];
                    var joints = skin.GetProperty("joints");
                    var ibms = ReadMat4AccessorAsRowVector(doc, skin.GetProperty("inverseBindMatrices").GetInt32());
                    var seenJoints = new HashSet<int>();
                    for (int j = 0; j < joints.GetArrayLength(); j++)
                    {
                        int jointNode = joints[j].GetInt32();
                        Assert.True(seenJoints.Add(jointNode), $"Duplicate joint index {jointNode} in skin[{s}].");
                        var localJointInArma = globalMats[jointNode] * invArmaGlobal;
                        AssertMatrixNear(System.Numerics.Matrix4x4.Identity, ibms[j] * localJointInArma, 1e-3f);
                    }
                }

                // Verify HeadPart's duplicate bone slot created an identity child directly under Head (NOT an orphan scene root!)
                var headSkin = skins[1];
                var headJoints = headSkin.GetProperty("joints");
                Assert.Equal(3, headJoints.GetArrayLength()); // Neck, Head, Head_Dup_2
                int headBoneIdx = headJoints[1].GetInt32();
                int dupBoneIdx = headJoints[2].GetInt32();
                Assert.Equal("Head", nodes[headBoneIdx].GetProperty("name").GetString());
                Assert.Equal(headBoneIdx, parentOf[dupBoneIdx]);

                // Verify exact runtime skinning equivalence under an arbitrary deformed joint pose:
                // (v_exported * IBM_exported * G_joint_deformed) == (v_orig * IBM_orig * G_joint_deformed)
                var headMesh = meshes[1];
                var headPrim = headMesh.GetProperty("primitives")[0];
                var exportedHeadVerts = ReadVec3Accessor(doc, headPrim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                var exportedHeadIbms = ReadMat4AccessorAsRowVector(doc, headSkin.GetProperty("inverseBindMatrices").GetInt32());
                var arbitraryPose =
                    System.Numerics.Matrix4x4.CreateRotationX(0.4f) *
                    System.Numerics.Matrix4x4.CreateRotationZ(-0.7f) *
                    System.Numerics.Matrix4x4.CreateTranslation(3.5f, -1.2f, 2.1f);

                for (int i = 0; i < origHeadVerts.Length; i++)
                {
                    var origDeformed = System.Numerics.Vector3.Transform(origHeadVerts[i], origHeadIbm * arbitraryPose);
                    var expDeformed = System.Numerics.Vector3.Transform(exportedHeadVerts[i], exportedHeadIbms[1] * arbitraryPose);
                    Assert.InRange(System.Numerics.Vector3.Distance(origDeformed, expDeformed), 0f, 1e-3f);
                }

                // Verify morph target delta on transformed HeadPart was canonicalized into Armature space
                var headTargets = headPrim.GetProperty("targets");
                Assert.Equal(1, headTargets.GetArrayLength());
                var morphDeltas = ReadVec3Accessor(doc, headTargets[0].GetProperty("POSITION").GetInt32());
                Assert.InRange(System.Numerics.Vector3.Distance(expectedHeadMorphDeltaArmatureSpace, morphDeltas[2]), 0f, 1e-3f);

                // Verify mirrored mesh (det < 0) flipped triangle winding, preserved outward normals, and flipped tangent.W
                var mirrorPrim = meshes[2].GetProperty("primitives")[0];
                var mirrorPos = ReadVec3Accessor(doc, mirrorPrim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                var mirrorNorm = ReadVec3Accessor(doc, mirrorPrim.GetProperty("attributes").GetProperty("NORMAL").GetInt32());
                var mirrorTan = ReadVec4Accessor(doc, mirrorPrim.GetProperty("attributes").GetProperty("TANGENT").GetInt32());
                var mirrorIdx = ReadIndexAccessor(doc, mirrorPrim.GetProperty("indices").GetInt32());
                var e1 = mirrorPos[mirrorIdx[1]] - mirrorPos[mirrorIdx[0]];
                var e2 = mirrorPos[mirrorIdx[2]] - mirrorPos[mirrorIdx[0]];
                var geoNorm = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(e1, e2));
                Assert.True(System.Numerics.Vector3.Dot(geoNorm, mirrorNorm[mirrorIdx[0]]) > 0.99f,
                    $"Mirrored skinned mesh winding disagreed with normal: dot={System.Numerics.Vector3.Dot(geoNorm, mirrorNorm[mirrorIdx[0]])}");
                Assert.Equal(-1f, mirrorTan[0].W, 4); // Original W was +1, det < 0 must flip to -1
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void GltfExporter_PartialMetallicRoughnessTextures_DoNotDoubleMultiplyScalarFactors()
        {
            var tempDir = CreateTempDirectory("GltfPartialMr");
            try
            {
                var root = new ImportedFrame { Name = "MatTest", LocalScale = Vector3.One };
                var mesh = new ImportedMesh
                {
                    Path = "MatTest",
                    hasNormal = true,
                    hasUV = new[] { true, false, false, false, false, false, false, false },
                    VertexList = new List<ImportedVertex>
                    {
                        new ImportedVertex { Vertex = new Vector3(0, 0, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 0f, 0f } } },
                        new ImportedVertex { Vertex = new Vector3(1, 0, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 1f, 0f } } },
                        new ImportedVertex { Vertex = new Vector3(0, 1, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 0f, 1f } } }
                    },
                    SubmeshList = new List<ImportedSubmesh>
                    {
                        new ImportedSubmesh { Material = "RoughOnlyMat", FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } } },
                        new ImportedSubmesh { Material = "MetalOnlyMat", FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } } }
                    }
                };

                var roughOnlyMat = new ImportedMaterial
                {
                    Name = "RoughOnlyMat",
                    Diffuse = new Color(1, 1, 1, 1),
                    Metallic = 0.6f,
                    Roughness = 0.4f,
                    Textures = new List<ImportedMaterialTexture>
                    {
                        new ImportedMaterialTexture { Name = "rough_100.png", Dest = 8, Scale = new Vector2(1, 1) }
                    }
                };

                var metalOnlyMat = new ImportedMaterial
                {
                    Name = "MetalOnlyMat",
                    Diffuse = new Color(1, 1, 1, 1),
                    Metallic = 0.8f,
                    Roughness = 0.35f,
                    Textures = new List<ImportedMaterialTexture>
                    {
                        new ImportedMaterialTexture { Name = "metal_180_opaque.png", Dest = 3, Scale = new Vector2(1, 1) }
                    }
                };

                var imported = new SimpleImported(root);
                imported.MeshList.Add(mesh);
                imported.MaterialList.Add(roughOnlyMat);
                imported.MaterialList.Add(metalOnlyMat);
                imported.TextureList.Add(new ImportedTexture(new MemoryStream(CreateSolidImageBytes(4, 4, 100, 100, 100, 255, ImageFormat.Png)), "rough_100.png"));
                imported.TextureList.Add(new ImportedTexture(new MemoryStream(CreateSolidImageBytes(4, 4, 180, 0, 0, 255, ImageFormat.Png)), "metal_180_opaque.png"));

                string glbPath = Path.Combine(tempDir, "partial_mr.glb");
                ModelExporter.ExportGltf(glbPath, imported, binary: true);
                var doc = ValidateGltfOrGlbFile(glbPath, expectBinary: true);

                var mats = doc.RootElement.GetProperty("materials");
                var textures = doc.RootElement.GetProperty("textures");

                // 1. RoughOnlyMat: G = 100, B = 255 (1.0), metallicFactor = 0.6, roughnessFactor = 1.0
                var pbrRough = mats[0].GetProperty("pbrMetallicRoughness");
                Assert.Equal(0.6f, pbrRough.GetProperty("metallicFactor").GetSingle(), 4);
                Assert.Equal(1.0f, pbrRough.GetProperty("roughnessFactor").GetSingle(), 4);
                int roughImgIdx = textures[pbrRough.GetProperty("metallicRoughnessTexture").GetProperty("index").GetInt32()].GetProperty("source").GetInt32();
                using (var bmp = ImageExtensions.DecodeBgraUnpremul(ReadImageBytes(doc, roughImgIdx, glbPath)))
                {
                    Assert.NotNull(bmp);
                    var px = bmp!.GetPixel(0, 0);
                    Assert.InRange(px.Green, 99, 101);
                    Assert.Equal(255, px.Blue); // MUST be 255 so metallicFactor=0.6 is not squared to 0.36
                }

                // 2. MetalOnlyMat: B = 180, G = 255 (1.0), metallicFactor = 1.0, roughnessFactor = 0.35
                var pbrMetal = mats[1].GetProperty("pbrMetallicRoughness");
                Assert.Equal(1.0f, pbrMetal.GetProperty("metallicFactor").GetSingle(), 4);
                Assert.Equal(0.35f, pbrMetal.GetProperty("roughnessFactor").GetSingle(), 4);
                int metalImgIdx = textures[pbrMetal.GetProperty("metallicRoughnessTexture").GetProperty("index").GetInt32()].GetProperty("source").GetInt32();
                using (var bmp = ImageExtensions.DecodeBgraUnpremul(ReadImageBytes(doc, metalImgIdx, glbPath)))
                {
                    Assert.NotNull(bmp);
                    var px = bmp!.GetPixel(0, 0);
                    Assert.InRange(px.Blue, 179, 181);
                    Assert.Equal(255, px.Green); // MUST be 255 so roughnessFactor=0.35 is not squared
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // ------------------------------------------------------------------
        // Headless Blender 5.x Roundtrip Validation Tests
        // ------------------------------------------------------------------

        [Fact]
        public void GltfExporter_BlenderHeadlessRoundtrip_SyntheticFullFidelity_ValidatesGeometryMaterialsSkinningMorphsAndAnimations()
        {
            var blenderExe = TryLocateBlenderExecutable();
            if (blenderExe == null)
            {
                Assert.Fail("Blender was not found (set ASSETSTUDIO_BLENDER_EXE or put blender on PATH).");
            }

            var tempDir = CreateTempDirectory("GltfBlenderSynth");
            try
            {
                var rich = BuildRichBlenderSyntheticModel();
                string glbS1Path = Path.Combine(tempDir, "rich_s1.glb");
                string gltfDir = Path.Combine(tempDir, "rich_gltf");
                Directory.CreateDirectory(gltfDir);
                string gltfS1Path = Path.Combine(gltfDir, "rich_s1.gltf");
                string glbS05Path = Path.Combine(tempDir, "rich_s05.glb");

                ModelExporter.ExportGltf(glbS1Path, rich, binary: true, skins: true, animation: true, blendShape: true, scaleFactor: 1.0f);
                ModelExporter.ExportGltf(gltfS1Path, rich, binary: false, skins: true, animation: true, blendShape: true, scaleFactor: 1.0f);
                ModelExporter.ExportGltf(glbS05Path, rich, binary: true, skins: true, animation: true, blendShape: true, scaleFactor: 0.5f);

                // Multi-skin + transformed SkinnedMeshRenderer + BlendShape + mirrored mesh
                var multiSkin = BuildTransformedMultiSkinMorphModel(out var expectedHeadMorphDelta, out _, out _);
                string multiSkinGlbPath = Path.Combine(tempDir, "multiskin_transformed.glb");
                ModelExporter.ExportGltf(multiSkinGlbPath, multiSkin, binary: true, skins: true, animation: true, blendShape: true, scaleFactor: 1.0f);

                // Split animation files (.glb and .gltf) via AssetExportService
                string splitGlbDir = Path.Combine(tempDir, "split_glb");
                string splitGltfDir = Path.Combine(tempDir, "split_gltf");
                Directory.CreateDirectory(splitGlbDir);
                Directory.CreateDirectory(splitGltfDir);

                string splitMainGlb = Path.Combine(splitGlbDir, "SplitHero.glb");
                AssetExportService.ExportModel(rich, splitMainGlb, new ExportOptionsState
                {
                    ModelFormat = ModelExportFormat.Glb,
                    ExportSkins = true,
                    ExportAnimations = true,
                    ExportAnimationsSplit = true,
                    ExportBlendShape = true,
                    ExportUnityMeta = false,
                    ExportUnityAnimClips = false
                });
                string splitClipGlb = Path.Combine(splitGlbDir, "SplitHero_Wave.glb");
                Assert.True(File.Exists(splitClipGlb));

                string splitMainGltf = Path.Combine(splitGltfDir, "SplitHero.gltf");
                AssetExportService.ExportModel(rich, splitMainGltf, new ExportOptionsState
                {
                    ModelFormat = ModelExportFormat.Gltf,
                    ExportSkins = true,
                    ExportAnimations = true,
                    ExportAnimationsSplit = true,
                    ExportBlendShape = true,
                    ExportUnityMeta = false,
                    ExportUnityAnimClips = false
                });
                string splitClipGltf = Path.Combine(splitGltfDir, "SplitHero_Wave.gltf");
                Assert.True(File.Exists(splitClipGltf));

                var inspectPaths = new[]
                {
                    glbS1Path,
                    gltfS1Path,
                    glbS05Path,
                    multiSkinGlbPath,
                    splitMainGlb,
                    splitClipGlb,
                    splitMainGltf,
                    splitClipGltf
                };

                using var reportDoc = RunBlenderGltfInspector(blenderExe, tempDir, inspectPaths, out _);
                var reports = reportDoc.RootElement;
                Assert.Equal(inspectPaths.Length, reports.GetArrayLength());

                // 1 & 2. Verify BOTH rich_s1.glb (embedded) AND rich_s1.gltf (external .bin + PNGs)
                for (int rIdx = 0; rIdx < 2; rIdx++)
                {
                    var r = reports[rIdx];
                    bool isGlb = rIdx == 0;
                    Assert.True(r.GetProperty("import_ok").GetBoolean(), $"Blender import failed for {inspectPaths[rIdx]}: {r.GetProperty("import_error")}");
                    Assert.Equal(JsonValueKind.Null, r.GetProperty("import_error").ValueKind);

                    var meshes = r.GetProperty("meshes");
                    Assert.Equal(3, meshes.GetArrayLength());

                    // UnitCube: exact 1x1x1 bounds, +1.0 volume, outward normals
                    var cube = FindByProp(meshes, "object_name", "UnitCube");
                    Assert.Equal(24, cube.GetProperty("vertex_count").GetInt32());
                    Assert.Equal(12, cube.GetProperty("polygon_count").GetInt32());
                    Assert.True(cube.GetProperty("all_finite").GetBoolean());
                    AssertJsonFloatNear(cube.GetProperty("signed_volume"), 1.0f, 1e-3f);
                    Assert.True(cube.GetProperty("avg_normal_winding_dot").GetSingle() > 0.99f);
                    AssertVec3Near(cube.GetProperty("static_bounds_min"), 3f, -1f, 0f, 1e-3f);
                    AssertVec3Near(cube.GetProperty("static_bounds_max"), 4f, 0f, 1f, 1e-3f);

                    // BlendQuad
                    var blendQuad = FindByProp(meshes, "object_name", "BlendQuad");
                    Assert.Equal(3, blendQuad.GetProperty("vertex_count").GetInt32());
                    Assert.Equal(1, blendQuad.GetProperty("polygon_count").GetInt32());
                    Assert.True(blendQuad.GetProperty("avg_normal_winding_dot").GetSingle() > 0.99f);

                    // Skinned + Morphed RichHero: 2 UV layers, no COLOR_0 by default, normalized weights, REST alignment, Shape Keys!
                    var hero = meshes.EnumerateArray().First(m => m.GetProperty("object_name").GetString()!.StartsWith("RichHero", StringComparison.Ordinal));
                    Assert.Equal(4, hero.GetProperty("vertex_count").GetInt32());
                    Assert.Equal(2, hero.GetProperty("polygon_count").GetInt32());
                    Assert.Equal(2, hero.GetProperty("uv_layers").GetArrayLength());
                    Assert.Equal(0, hero.GetProperty("color_attributes").GetArrayLength());
                    Assert.Equal(2, hero.GetProperty("vertex_group_count").GetInt32());
                    Assert.InRange(hero.GetProperty("weight_sum_min").GetSingle(), 0.999f, 1.001f);
                    Assert.InRange(hero.GetProperty("weight_sum_max").GetSingle(), 0.999f, 1.001f);
                    Assert.Equal(0, hero.GetProperty("zero_weight_verts").GetInt32());
                    Assert.InRange(hero.GetProperty("max_rest_vertex_diff").GetSingle(), 0f, 1e-3f);

                    // Verify UV orientation in Blender: Unity (u=0.25, v=0.2) -> glTF (0.25, 0.8) -> Blender UV (0.25, 0.2)!
                    var uv0Samples = hero.GetProperty("uv_layers")[0].GetProperty("samples");
                    bool hasExpectedUvCorner = uv0Samples.EnumerateArray().Any(s =>
                        MathF.Abs(s[0].GetSingle() - 0.25f) < 1e-3f && MathF.Abs(s[1].GetSingle() - 0.2f) < 1e-3f);
                    Assert.True(hasExpectedUvCorner, "Expected Blender UV0 to match Unity bottom-left UV (0.25, 0.2) without vertical flip.");

                    // Verify asymmetric 4-quadrant PNG pixel orientation in Blender (not vertically flipped!)
                    var images = r.GetProperty("images");
                    Assert.Equal(6, images.GetArrayLength());
                    var quadImg = FindByProp(images, "name", "quadrant");
                    Assert.Equal(4, quadImg.GetProperty("width").GetInt32());
                    Assert.Equal(4, quadImg.GetProperty("height").GetInt32());
                    if (isGlb)
                    {
                        Assert.True(quadImg.GetProperty("packed").GetBoolean());
                    }
                    var quadPx = quadImg.GetProperty("pixels");
                    // Top-left=Red, Top-right=Green, Bottom-left=Blue, Bottom-right=Yellow
                    AssertVec3Near(quadPx.GetProperty("top_left"), 1f, 0f, 0f, 0.02f);
                    AssertVec3Near(quadPx.GetProperty("top_right"), 0f, 1f, 0f, 0.02f);
                    AssertVec3Near(quadPx.GetProperty("bottom_left"), 0f, 0f, 1f, 0.02f);
                    AssertVec3Near(quadPx.GetProperty("bottom_right"), 1f, 1f, 0f, 0.02f);

                    // Verify BlendTiled_metallicRoughness has Blue == 1.0 (no metallic double-multiplication) and Green == 100/255
                    var blendMrImg = FindByProp(images, "name", "BlendTiled_metallicRoughness");
                    Assert.Equal("Non-Color", blendMrImg.GetProperty("colorspace").GetString());
                    var blendMrPx = blendMrImg.GetProperty("pixels").GetProperty("bottom_left");
                    AssertJsonFloatNear(blendMrPx[1], 100f / 255f, 0.01f); // G = Roughness
                    AssertJsonFloatNear(blendMrPx[2], 1.0f, 0.01f);        // B = 1.0 (metallicFactor applies once)

                    // Verify PBR Principled BSDF materials
                    var mats = r.GetProperty("materials");
                    Assert.Equal(3, mats.GetArrayLength());

                    var heroMat = FindByProp(mats, "name", "HeroPbr");
                    Assert.False(heroMat.GetProperty("use_backface_culling").GetBoolean()); // doubleSided = true
                    var heroBsdf = heroMat.GetProperty("bsdf");
                    Assert.True(heroBsdf.GetProperty("Base Color").GetProperty("linked").GetBoolean());
                    Assert.True(heroBsdf.GetProperty("Metallic").GetProperty("linked").GetBoolean());
                    Assert.Equal("Blue", heroBsdf.GetProperty("Metallic").GetProperty("chain").GetProperty("from_socket").GetString());
                    Assert.True(heroBsdf.GetProperty("Roughness").GetProperty("linked").GetBoolean());
                    Assert.Equal("Green", heroBsdf.GetProperty("Roughness").GetProperty("chain").GetProperty("from_socket").GetString());
                    Assert.True(heroBsdf.GetProperty("Normal").GetProperty("linked").GetBoolean());
                    Assert.Equal("NORMAL_MAP", heroBsdf.GetProperty("Normal").GetProperty("chain").GetProperty("node_type").GetString());
                    AssertJsonFloatNear(heroBsdf.GetProperty("Normal").GetProperty("chain").GetProperty("strength"), 1.2f, 1e-3f);
                    Assert.True(heroBsdf.GetProperty("Emission Color").GetProperty("linked").GetBoolean());
                    AssertJsonFloatNear(heroBsdf.GetProperty("Emission Strength").GetProperty("default"), 1.0f, 1e-3f);
                    Assert.True(heroMat.GetProperty("gltf_material_output").GetProperty("occlusion_linked").GetBoolean());

                    var propMat = FindByProp(mats, "name", "PropMask");
                    Assert.True(propMat.GetProperty("use_backface_culling").GetBoolean());
                    Assert.Equal("DITHERED", propMat.GetProperty("surface_render_method").GetString());
                    var propBsdf = propMat.GetProperty("bsdf");
                    AssertJsonFloatNear(propBsdf.GetProperty("Metallic").GetProperty("default"), 0.25f, 1e-3f);
                    AssertJsonFloatNear(propBsdf.GetProperty("Roughness").GetProperty("default"), 0.30f, 1e-3f);
                    Assert.True(propBsdf.GetProperty("Alpha").GetProperty("linked").GetBoolean()); // MASK alpha cutoff node chain

                    var blendMat = FindByProp(mats, "name", "BlendTiled");
                    Assert.True(blendMat.GetProperty("use_backface_culling").GetBoolean());
                    Assert.Equal("BLENDED", blendMat.GetProperty("surface_render_method").GetString());
                    var blendBsdf = blendMat.GetProperty("bsdf");
                    Assert.True(blendBsdf.GetProperty("Base Color").GetProperty("linked").GetBoolean());
                    // Verify KHR_texture_transform Mapping node Scale=[2,3,1] and Location=[0.1,0.2,0]
                    var mappingNode = blendBsdf.GetProperty("Base Color").GetProperty("chain")
                        .GetProperty("sources").GetProperty("6:A").GetProperty("vector_source");
                    Assert.Equal("MAPPING", mappingNode.GetProperty("node_type").GetString());
                    AssertVec3Near(mappingNode.GetProperty("location"), 0.1f, 0.2f, 0f, 1e-3f);
                    AssertVec3Near(mappingNode.GetProperty("scale"), 2.0f, 3.0f, 1.0f, 1e-3f);
                    // Verify scalar Metallic=0.6 multiplies Blue channel once
                    var blendMetalChain = blendBsdf.GetProperty("Metallic").GetProperty("chain");
                    Assert.Equal("MATH", blendMetalChain.GetProperty("node_type").GetString());
                    Assert.Equal("MULTIPLY", blendMetalChain.GetProperty("operation").GetString());
                    AssertJsonFloatNear(blendMetalChain.GetProperty("values")[1], 0.6f, 1e-3f);

                    // Verify Armature hierarchy
                    var arms = r.GetProperty("armatures");
                    Assert.Equal(1, arms.GetArrayLength());
                    Assert.Equal(2, arms[0].GetProperty("bone_count").GetInt32());

                    // Verify Morph Targets / Shape Keys & live evaluated displacement at value = 1.0!
                    var sk = hero.GetProperty("shape_keys");
                    Assert.Equal(2, sk.GetProperty("count").GetInt32());
                    var keys = sk.GetProperty("keys");
                    Assert.Equal("Smile", keys[0].GetProperty("name").GetString());
                    Assert.Equal("Blink", keys[1].GetProperty("name").GetString());
                    AssertJsonFloatNear(keys[0].GetProperty("eval_max_disp_at_1"), 0.2f, 1e-3f);
                    AssertVec3Near(keys[0].GetProperty("eval_max_vec_at_1"), 0f, 0f, 0.2f, 1e-3f); // Unity +Y -> Blender +Z
                    AssertJsonFloatNear(keys[1].GetProperty("eval_max_disp_at_1"), 0.15f, 1e-3f);

                    // Verify Blender 5.x Slotted Action F-curves (L=3, R=4, S=3, K=2) and 0 animation defects
                    var actions = r.GetProperty("actions");
                    Assert.Equal(1, actions.GetArrayLength());
                    var wave = actions[0];
                    Assert.Equal("Wave", wave.GetProperty("name").GetString());
                    Assert.Equal(12, wave.GetProperty("fcurve_count").GetInt32());
                    Assert.Equal(3, wave.GetProperty("loc_fcurves").GetInt32());
                    Assert.Equal(4, wave.GetProperty("rot_fcurves").GetInt32());
                    Assert.Equal(3, wave.GetProperty("scale_fcurves").GetInt32());
                    Assert.Equal(2, wave.GetProperty("shapekey_fcurves").GetInt32());
                    Assert.Equal(0, wave.GetProperty("non_monotonic_keys").GetInt32());
                    Assert.Equal(0, wave.GetProperty("non_finite_keys").GetInt32());
                    Assert.Equal(0, wave.GetProperty("quat_hemisphere_flips").GetInt32());

                    // Verify multi-frame depsgraph bone & deformed mesh motion
                    var frameEval = r.GetProperty("frame_eval");
                    Assert.True(frameEval.GetArrayLength() >= 3);
                    float maxBoneDisp = frameEval.EnumerateArray().Max(fe => fe.GetProperty("armatures")[0].GetProperty("max_bone_disp_from_start").GetSingle());
                    float maxHeroDisp = frameEval.EnumerateArray().Max(fe =>
                        fe.GetProperty("meshes").EnumerateArray()
                          .First(m => m.GetProperty("name").GetString()!.StartsWith("RichHero", StringComparison.Ordinal))
                          .GetProperty("max_vert_disp_from_start").GetSingle());
                    Assert.True(maxBoneDisp > 0.4f, $"Expected bone motion across animation frames, got {maxBoneDisp}");
                    Assert.True(maxHeroDisp > 0.4f, $"Expected skinned+morphed mesh motion across animation frames, got {maxHeroDisp}");
                }

                // 3. Verify scaleFactor = 0.5 halves bounds and scales volume by 1/8
                {
                    var r05 = reports[2];
                    Assert.True(r05.GetProperty("import_ok").GetBoolean());
                    var cube05 = FindByProp(r05.GetProperty("meshes"), "object_name", "UnitCube");
                    AssertJsonFloatNear(cube05.GetProperty("signed_volume"), 0.125f, 1e-4f);
                    AssertVec3Near(cube05.GetProperty("static_bounds_min"), 1.5f, -0.5f, 0f, 1e-3f);
                    AssertVec3Near(cube05.GetProperty("static_bounds_max"), 2.0f, 0f, 0.5f, 1e-3f);
                }

                // 4. Verify multi-skin + transformed SkinnedMeshRenderer + BlendShape + MirroredPart in Blender 5.x
                {
                    var rMulti = reports[3];
                    Assert.True(rMulti.GetProperty("import_ok").GetBoolean());
                    var arms = rMulti.GetProperty("armatures");
                    Assert.Equal(1, arms.GetArrayLength()); // MUST be 1 unified Armature, NOT split!
                    Assert.Equal("Armature", arms[0].GetProperty("name").GetString());

                    var multiMeshes = rMulti.GetProperty("meshes");
                    Assert.Equal(3, multiMeshes.GetArrayLength());
                    foreach (var m in multiMeshes.EnumerateArray())
                    {
                        Assert.True(m.GetProperty("all_finite").GetBoolean());
                        Assert.InRange(m.GetProperty("max_rest_vertex_diff").GetSingle(), 0f, 1e-3f);
                        Assert.InRange(m.GetProperty("weight_sum_min").GetSingle(), 0.999f, 1.001f);
                        Assert.InRange(m.GetProperty("weight_sum_max").GetSingle(), 0.999f, 1.001f);
                        Assert.Equal(0, m.GetProperty("zero_weight_verts").GetInt32());
                        Assert.True(m.GetProperty("avg_normal_winding_dot").GetSingle() > 0.99f);
                    }

                    var headMesh = FindByProp(multiMeshes, "object_name", "HeadPart");
                    var headSk = headMesh.GetProperty("shape_keys");
                    Assert.Equal(1, headSk.GetProperty("count").GetInt32());
                    var browKey = headSk.GetProperty("keys")[0];
                    Assert.Equal("RaiseBrow", browKey.GetProperty("name").GetString());
                    AssertJsonFloatNear(browKey.GetProperty("eval_max_disp_at_1"), expectedHeadMorphDelta.Length(), 2e-3f);
                }

                // 5. Verify Split Animation files (both GLB and glTF) import cleanly in Blender 5.x
                for (int pair = 0; pair < 2; pair++)
                {
                    var rMain = reports[4 + pair * 2];
                    var rClip = reports[5 + pair * 2];
                    Assert.True(rMain.GetProperty("import_ok").GetBoolean());
                    Assert.True(rClip.GetProperty("import_ok").GetBoolean());
                    Assert.Equal(0, rMain.GetProperty("actions").GetArrayLength());
                    Assert.Equal(1, rClip.GetProperty("actions").GetArrayLength());
                    var clipAct = rClip.GetProperty("actions")[0];
                    Assert.Equal("Wave", clipAct.GetProperty("name").GetString());
                    Assert.Equal(12, clipAct.GetProperty("fcurve_count").GetInt32());
                    Assert.Equal(0, clipAct.GetProperty("quat_hemisphere_flips").GetInt32());
                }

                _output.WriteLine($"Blender headless synthetic full-fidelity roundtrip PASSED across all {inspectPaths.Length} exports via '{blenderExe}'.");
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
                Smoothness = 0.7f,
                Textures = new List<ImportedMaterialTexture>
                {
                    new ImportedMaterialTexture { Name = "albedo.png", Dest = 0, Scale = new Vector2(1, 1) }
                }
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

        private static (System.Numerics.Matrix4x4[] GlobalRowMats, int[] ParentOf) ComputeNodeGlobalRowMatrices(JsonElement root)
        {
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

            var globalMats = new System.Numerics.Matrix4x4[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                var mat = localRowMats[i];
                int p = parentOf[i];
                while (p >= 0)
                {
                    mat = mat * localRowMats[p];
                    p = parentOf[p];
                }
                globalMats[i] = mat;
            }

            return (globalMats, parentOf);
        }

        private static string? TryLocateBlenderExecutable()
        {
            var envPath = Environment.GetEnvironmentVariable("ASSETSTUDIO_BLENDER_EXE");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                return envPath;
            }

            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVar))
            {
                string exeName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
                foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir, exeName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                        // Ignore malformed PATH entries
                    }
                }
            }

            return null;
        }

        private static JsonDocument RunBlenderGltfInspector(
            string blenderExe,
            string tempDir,
            IReadOnlyList<string> gltfFilePaths,
            out string combinedOutput)
        {
            string scriptPath = Path.Combine(tempDir, "blender_gltf_inspector.py");
            File.WriteAllText(scriptPath, BlenderInspectorScript, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = blenderExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--background");
            psi.ArgumentList.Add("--factory-startup");
            psi.ArgumentList.Add("--python");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--");
            foreach (var fp in gltfFilePaths)
            {
                psi.ArgumentList.Add(fp);
            }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch Blender at '{blenderExe}'.");
            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(180_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Headless Blender glTF inspection timed out after 180 seconds.");
            }

            string stdOut = stdOutTask.GetAwaiter().GetResult();
            string stdErr = stdErrTask.GetAwaiter().GetResult();
            combinedOutput = stdOut + Environment.NewLine + stdErr;

            Assert.True(proc.ExitCode == 0, $"Blender exited with code {proc.ExitCode}.{Environment.NewLine}{combinedOutput}");
            Assert.DoesNotContain("Traceback (most recent call last):", combinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("| ERROR:", combinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("| WARNING:", combinedOutput, StringComparison.Ordinal);

            const string startMarker = "===BLENDER_JSON_START===";
            const string endMarker = "===BLENDER_JSON_END===";
            int startIdx = stdOut.IndexOf(startMarker, StringComparison.Ordinal);
            int endIdx = stdOut.IndexOf(endMarker, StringComparison.Ordinal);
            Assert.True(startIdx >= 0 && endIdx > startIdx, $"Blender inspector JSON markers not found.{Environment.NewLine}{combinedOutput}");

            string jsonPayload = stdOut.Substring(startIdx + startMarker.Length, endIdx - startIdx - startMarker.Length).Trim();
            return JsonDocument.Parse(jsonPayload);
        }

        private const string BlenderInspectorScript = """
import bpy
import sys
import json
import math
from mathutils import Vector, Quaternion

def get_action_fcurves(action):
    fcurves = []
    if hasattr(action, "layers"):
        for layer in action.layers:
            for strip in layer.strips:
                if hasattr(strip, "channelbags"):
                    for bag in strip.channelbags:
                        slot_name = bag.slot.name_display if bag.slot else ""
                        slot_type = bag.slot.target_id_type if bag.slot else ""
                        for fc in bag.fcurves:
                            fcurves.append((fc, slot_name, slot_type))
    elif hasattr(action, "fcurves"):
        for fc in action.fcurves:
            fcurves.append((fc, "", ""))
    return fcurves

def trace_input_chain(inp, depth=0):
    if depth > 8 or not inp or not inp.is_linked:
        return None
    link = inp.links[0]
    node = link.from_node
    info = {
        "node_type": node.type,
        "node_name": node.name,
        "from_socket": link.from_socket.name,
    }
    if node.type == 'TEX_IMAGE':
        img = node.image
        info["image_name"] = img.name if img else None
        info["image_size"] = list(img.size) if img else [0, 0]
        info["image_packed"] = (img.packed_file is not None) if img else False
        info["colorspace"] = img.colorspace_settings.name if img else ""
        vec_in = node.inputs.get("Vector")
        if vec_in and vec_in.is_linked:
            info["vector_source"] = trace_input_chain(vec_in, depth + 1)
    elif node.type == 'MAPPING':
        info["location"] = [float(x) for x in node.inputs["Location"].default_value]
        info["rotation"] = [float(x) for x in node.inputs["Rotation"].default_value]
        info["scale"] = [float(x) for x in node.inputs["Scale"].default_value]
        vec_in = node.inputs.get("Vector")
        if vec_in and vec_in.is_linked:
            info["vector_source"] = trace_input_chain(vec_in, depth + 1)
    elif node.type == 'UVMAP':
        info["uv_map"] = node.uv_map
    elif node.type == 'NORMAL_MAP':
        info["strength"] = float(node.inputs["Strength"].default_value)
        info["color_source"] = trace_input_chain(node.inputs["Color"], depth + 1)
    elif node.type == 'SEPARATE_COLOR':
        info["color_source"] = trace_input_chain(node.inputs["Color"], depth + 1)
    elif node.type == 'MATH':
        info["operation"] = node.operation
        info["values"] = [float(i.default_value) for i in node.inputs if hasattr(i, "default_value")]
        info["sources"] = [trace_input_chain(i, depth + 1) for i in node.inputs if i.is_linked]
    elif node.type == 'MIX':
        info["data_type"] = node.data_type
        info["blend_type"] = node.blend_type
        info["sources"] = {}
        for idx, i in enumerate(node.inputs):
            if i.is_linked:
                info["sources"][f"{idx}:{i.name}"] = trace_input_chain(i, depth + 1)
            elif hasattr(i, "default_value"):
                dv = i.default_value
                info["sources"][f"{idx}:{i.name}"] = [float(x) for x in dv] if hasattr(dv, "__len__") else float(dv)
    else:
        linked = {}
        for i in node.inputs:
            if i.is_linked:
                linked[i.name] = trace_input_chain(i, depth + 1)
        if linked:
            info["sources"] = linked
    return info

def inspect_file(filepath):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    result = {
        "filepath": filepath,
        "blender_version": list(bpy.app.version),
        "import_ok": False,
        "import_error": None,
        "meshes": [],
        "materials": [],
        "images": [],
        "armatures": [],
        "actions": [],
        "frame_eval": [],
    }
    try:
        res = bpy.ops.import_scene.gltf(filepath=filepath, disable_bone_shape=True)
        result["import_ok"] = ('FINISHED' in res)
    except Exception as ex:
        result["import_error"] = repr(ex)
        return result

    scene_objs = list(bpy.context.scene.objects)
    arm_objs = [o for o in scene_objs if o.type == 'ARMATURE']
    mesh_objs = [o for o in scene_objs if o.type == 'MESH']

    # Inspect images
    for img in bpy.data.images:
        w, h = img.size[0], img.size[1]
        pixels_sample = None
        if w > 0 and h > 0:
            px = img.pixels[:]
            def get_px(x, y):
                idx = (y * w + x) * 4
                return [round(px[idx], 4), round(px[idx + 1], 4), round(px[idx + 2], 4), round(px[idx + 3], 4)]
            pixels_sample = {
                "bottom_left": get_px(0, 0),
                "bottom_right": get_px(w - 1, 0),
                "top_left": get_px(0, h - 1),
                "top_right": get_px(w - 1, h - 1),
            }
        result["images"].append({
            "name": img.name,
            "width": w,
            "height": h,
            "packed": img.packed_file is not None,
            "colorspace": img.colorspace_settings.name,
            "pixels": pixels_sample,
        })

    # Inspect materials
    for mat in bpy.data.materials:
        mat_info = {
            "name": mat.name,
            "use_backface_culling": mat.use_backface_culling,
            "surface_render_method": getattr(mat, "surface_render_method", None),
        }
        if mat.node_tree:
            bsdf = None
            gltf_out = None
            for n in mat.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED':
                    bsdf = n
                elif n.type == 'GROUP' and n.node_tree and 'glTF' in n.node_tree.name:
                    gltf_out = n
            if bsdf:
                bsdf_info = {}
                for sock_name in ["Base Color", "Metallic", "Roughness", "Alpha", "Normal", "Emission Color", "Emission Strength"]:
                    inp = bsdf.inputs.get(sock_name)
                    if inp is None:
                        continue
                    dv = None
                    if hasattr(inp, "default_value"):
                        raw = inp.default_value
                        dv = [float(x) for x in raw] if hasattr(raw, "__len__") else float(raw)
                    bsdf_info[sock_name] = {
                        "default": dv,
                        "linked": inp.is_linked,
                        "chain": trace_input_chain(inp) if inp.is_linked else None,
                    }
                mat_info["bsdf"] = bsdf_info
            if gltf_out:
                occ_in = gltf_out.inputs.get("Occlusion")
                mat_info["gltf_material_output"] = {
                    "occlusion_linked": occ_in.is_linked if occ_in else False,
                    "occlusion_chain": trace_input_chain(occ_in) if (occ_in and occ_in.is_linked) else None,
                }
        result["materials"].append(mat_info)

    # Inspect armatures
    for obj in arm_objs:
        arm = obj.data
        bmin = Vector((float('inf'), float('inf'), float('inf')))
        bmax = Vector((float('-inf'), float('-inf'), float('-inf')))
        for b in arm.bones:
            wh = obj.matrix_world @ b.head_local
            wt = obj.matrix_world @ b.tail_local
            for pt in (wh, wt):
                for k in range(3):
                    if pt[k] < bmin[k]: bmin[k] = pt[k]
                    if pt[k] > bmax[k]: bmax[k] = pt[k]
        result["armatures"].append({
            "name": obj.name,
            "bone_count": len(arm.bones),
            "bounds_min": [round(x, 5) for x in bmin] if len(arm.bones) else None,
            "bounds_max": [round(x, 5) for x in bmax] if len(arm.bones) else None,
        })

    # Inspect meshes in clean REST pose (also evaluates live shape key displacements at value=1.0)
    saved_pose_pos = {a: a.data.pose_position for a in arm_objs}
    for a in arm_objs:
        a.data.pose_position = 'REST'
    bpy.context.view_layer.update()

    try:
        for obj in mesh_objs:
            mesh = obj.data
            sk = mesh.shape_keys
            saved_sk_action = None
            saved_sk_use_nla = True
            saved_sk_vals = []
            if sk:
                if sk.animation_data:
                    saved_sk_action = sk.animation_data.action
                    saved_sk_use_nla = sk.animation_data.use_nla
                    sk.animation_data.action = None
                    sk.animation_data.use_nla = False
                for kb in sk.key_blocks:
                    saved_sk_vals.append(kb.value)
                    kb.value = 0.0
                bpy.context.view_layer.update()

            try:
                dg = bpy.context.evaluated_depsgraph_get()
                eval_obj = obj.evaluated_get(dg)
                eval_mesh = eval_obj.to_mesh()
                try:
                    smin = Vector((float('inf'), float('inf'), float('inf')))
                    smax = Vector((float('-inf'), float('-inf'), float('-inf')))
                    all_finite = True
                    base_world_pts = []
                    max_rest_diff = 0.0
                    for i, v in enumerate(eval_mesh.vertices):
                        ewp = eval_obj.matrix_world @ v.co
                        swp = obj.matrix_world @ mesh.vertices[i].co
                        base_world_pts.append(ewp.copy())
                        diff = (ewp - swp).length
                        if diff > max_rest_diff:
                            max_rest_diff = diff
                        for k in range(3):
                            if not math.isfinite(ewp[k]):
                                all_finite = False
                            if swp[k] < smin[k]: smin[k] = swp[k]
                            if swp[k] > smax[k]: smax[k] = swp[k]

                    signed_vol = 0.0
                    dot_sum = 0.0
                    dot_count = 0
                    for poly in eval_mesh.polygons:
                        if len(poly.vertices) >= 3:
                            v0 = eval_mesh.vertices[poly.vertices[0]].co
                            v1 = eval_mesh.vertices[poly.vertices[1]].co
                            v2 = eval_mesh.vertices[poly.vertices[2]].co
                            signed_vol += v0.dot(v1.cross(v2)) / 6.0
                            gn = (v1 - v0).cross(v2 - v0)
                            if gn.length > 1e-8:
                                gn.normalize()
                                for vi in poly.vertices:
                                    vn = eval_mesh.vertices[vi].normal
                                    if vn.length > 1e-8:
                                        dot_sum += gn.dot(vn)
                                        dot_count += 1
                finally:
                    eval_obj.to_mesh_clear()

                uv_info = []
                for uv_layer in mesh.uv_layers:
                    samples = [[round(uv_layer.data[i].uv[0], 5), round(uv_layer.data[i].uv[1], 5)] for i in range(min(8, len(uv_layer.data)))]
                    uv_info.append({"name": uv_layer.name, "loop_count": len(uv_layer.data), "samples": samples})

                color_attrs = [{"name": ca.name, "domain": ca.domain, "data_type": ca.data_type} for ca in mesh.color_attributes]

                vg_names = [vg.name for vg in obj.vertex_groups]
                w_min = float('inf')
                w_max = float('-inf')
                zero_weight_verts = 0
                if vg_names:
                    for v in mesh.vertices:
                        ws = sum(g.weight for g in v.groups)
                        if ws < 1e-6:
                            zero_weight_verts += 1
                        if ws < w_min: w_min = ws
                        if ws > w_max: w_max = ws
                else:
                    w_min = w_max = 0.0

                sk_info = None
                if sk and sk.key_blocks:
                    kbs = sk.key_blocks
                    keys_out = []
                    for kb in kbs[1:]:
                        kb.value = 1.0
                        bpy.context.view_layer.update()
                        dg2 = bpy.context.evaluated_depsgraph_get()
                        e2 = obj.evaluated_get(dg2)
                        m2 = e2.to_mesh()
                        try:
                            max_d = 0.0
                            max_vec = [0.0, 0.0, 0.0]
                            for i, v in enumerate(m2.vertices):
                                wp = e2.matrix_world @ v.co
                                d = wp - base_world_pts[i]
                                if d.length > max_d:
                                    max_d = d.length
                                    max_vec = [round(d.x, 5), round(d.y, 5), round(d.z, 5)]
                        finally:
                            e2.to_mesh_clear()
                        kb.value = 0.0
                        keys_out.append({
                            "name": kb.name,
                            "eval_max_disp_at_1": round(max_d, 5),
                            "eval_max_vec_at_1": max_vec,
                        })
                    sk_info = {"count": len(kbs) - 1, "basis": kbs[0].name, "keys": keys_out}

                result["meshes"].append({
                    "object_name": obj.name,
                    "vertex_count": len(mesh.vertices),
                    "polygon_count": len(mesh.polygons),
                    "all_finite": all_finite,
                    "static_bounds_min": [round(x, 5) for x in smin] if len(mesh.vertices) else None,
                    "static_bounds_max": [round(x, 5) for x in smax] if len(mesh.vertices) else None,
                    "max_rest_vertex_diff": round(max_rest_diff, 5),
                    "signed_volume": round(signed_vol, 6),
                    "avg_normal_winding_dot": round(dot_sum / dot_count, 5) if dot_count > 0 else None,
                    "uv_layers": uv_info,
                    "color_attributes": color_attrs,
                    "vertex_group_count": len(vg_names),
                    "weight_sum_min": round(w_min, 5),
                    "weight_sum_max": round(w_max, 5),
                    "zero_weight_verts": zero_weight_verts,
                    "shape_keys": sk_info,
                })
            finally:
                if sk:
                    for kb, val in zip(sk.key_blocks, saved_sk_vals):
                        kb.value = val
                    if sk.animation_data:
                        sk.animation_data.action = saved_sk_action
                        sk.animation_data.use_nla = saved_sk_use_nla
    finally:
        for a, pos in saved_pose_pos.items():
            a.data.pose_position = pos
        bpy.context.view_layer.update()

    # Inspect actions (Blender 5.x slotted Actions API)
    max_frame = 1.0
    min_frame = 0.0
    for act in bpy.data.actions:
        fcs = get_action_fcurves(act)
        loc_cnt = rot_cnt = scale_cnt = key_cnt = 0
        non_monotonic = non_finite = quat_flips = 0
        fr = act.frame_range
        if fr[1] > max_frame: max_frame = fr[1]
        if fr[0] < min_frame: min_frame = fr[0]

        quat_by_path = {}
        for fc, slot_name, _ in fcs:
            dp = fc.data_path
            if dp.endswith("location"): loc_cnt += 1
            elif dp.endswith("rotation_quaternion"):
                rot_cnt += 1
                quat_by_path.setdefault((slot_name, dp), {})[fc.array_index] = fc
            elif dp.endswith("scale"): scale_cnt += 1
            elif "key_blocks" in dp: key_cnt += 1

            prev_x = None
            for kp in fc.keyframe_points:
                x, y = kp.co[0], kp.co[1]
                if not (math.isfinite(x) and math.isfinite(y)):
                    non_finite += 1
                if prev_x is not None and x < prev_x - 1e-6:
                    non_monotonic += 1
                prev_x = x

        for (_, _), comp_map in quat_by_path.items():
            if len(comp_map) == 4:
                xs = sorted({kp.co[0] for fc in comp_map.values() for kp in fc.keyframe_points})
                prev_q = None
                for x in xs:
                    q = Quaternion((comp_map[0].evaluate(x), comp_map[1].evaluate(x), comp_map[2].evaluate(x), comp_map[3].evaluate(x)))
                    if prev_q is not None and prev_q.dot(q) < -0.1:
                        quat_flips += 1
                    prev_q = q

        result["actions"].append({
            "name": act.name,
            "frame_range": [round(float(fr[0]), 4), round(float(fr[1]), 4)],
            "fcurve_count": len(fcs),
            "loc_fcurves": loc_cnt,
            "rot_fcurves": rot_cnt,
            "scale_fcurves": scale_cnt,
            "shapekey_fcurves": key_cnt,
            "non_monotonic_keys": non_monotonic,
            "non_finite_keys": non_finite,
            "quat_hemisphere_flips": quat_flips,
        })

    # Scrub animation frames and verify bone + deformed mesh evaluation
    if bpy.data.actions and max_frame > min_frame + 0.01:
        raw_frames = [
            int(round(min_frame)),
            int(round(min_frame + (max_frame - min_frame) * 0.25)),
            int(round(min_frame + (max_frame - min_frame) * 0.5)),
            int(round(min_frame + (max_frame - min_frame) * 0.75)),
            int(round(max_frame)),
        ]
        seen = set()
        sample_frames = [f for f in raw_frames if not (f in seen or seen.add(f))]
        ref_verts = {}
        ref_bones = {}

        for f in sample_frames:
            bpy.context.scene.frame_set(f)
            dg = bpy.context.evaluated_depsgraph_get()
            f_entry = {"frame": f, "meshes": [], "armatures": []}
            for aobj in arm_objs:
                ea = aobj.evaluated_get(dg)
                max_bone_disp = 0.0
                cur_b = {pb.name: (ea.matrix_world @ pb.head).copy() for pb in ea.pose.bones}
                if aobj.name not in ref_bones:
                    ref_bones[aobj.name] = cur_b
                else:
                    for bn, wp in cur_b.items():
                        d = (wp - ref_bones[aobj.name][bn]).length
                        if d > max_bone_disp: max_bone_disp = d
                f_entry["armatures"].append({"name": aobj.name, "max_bone_disp_from_start": round(max_bone_disp, 5)})

            for mobj in mesh_objs:
                em = mobj.evaluated_get(dg)
                tm = em.to_mesh()
                try:
                    all_fin = True
                    pts = []
                    for v in tm.vertices:
                        wp = em.matrix_world @ v.co
                        pts.append(wp.copy())
                        if not (math.isfinite(wp.x) and math.isfinite(wp.y) and math.isfinite(wp.z)):
                            all_fin = False
                    max_v_disp = 0.0
                    if mobj.name not in ref_verts:
                        ref_verts[mobj.name] = pts
                    else:
                        prev_pts = ref_verts[mobj.name]
                        for i in range(min(len(pts), len(prev_pts))):
                            d = (pts[i] - prev_pts[i]).length
                            if d > max_v_disp: max_v_disp = d
                    f_entry["meshes"].append({
                        "name": mobj.name,
                        "all_finite": all_fin,
                        "max_vert_disp_from_start": round(max_v_disp, 5),
                    })
                finally:
                    em.to_mesh_clear()
            result["frame_eval"].append(f_entry)

    return result

if __name__ == "__main__":
    argv = sys.argv
    args = argv[argv.index("--") + 1:] if "--" in argv else []
    out_reports = [inspect_file(path) for path in args]
    print("===BLENDER_JSON_START===")
    print(json.dumps(out_reports))
    print("===BLENDER_JSON_END===")
""";

        private static SimpleImported BuildRichBlenderSyntheticModel()
        {
            var root = new ImportedFrame { Name = "RichHero", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var hips = new ImportedFrame { Name = "Hips", LocalPosition = new Vector3(0, 1, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var spine = new ImportedFrame { Name = "Spine", LocalPosition = new Vector3(0, 1, 0), LocalRotation = new Vector3(0, 30, 0), LocalScale = Vector3.One };
            var cubeFrame = new ImportedFrame { Name = "UnitCube", LocalPosition = new Vector3(3, 0, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var blendFrame = new ImportedFrame { Name = "BlendQuad", LocalPosition = new Vector3(-3, 0, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };

            root.AddChild(hips);
            hips.AddChild(spine);
            root.AddChild(cubeFrame);
            root.AddChild(blendFrame);

            var hipsGlobal = System.Numerics.Matrix4x4.CreateTranslation(0, 1, 0);
            var spineLocal = System.Numerics.Matrix4x4.CreateRotationY((float)(30.0 * Math.PI / 180.0)) * System.Numerics.Matrix4x4.CreateTranslation(0, 1, 0);
            var spineGlobal = spineLocal * hipsGlobal;
            System.Numerics.Matrix4x4.Invert(hipsGlobal, out var hipsIbm);
            System.Numerics.Matrix4x4.Invert(spineGlobal, out var spineIbm);

            var bodyMesh = new ImportedMesh
            {
                Path = "RichHero",
                hasNormal = true,
                hasTangent = true,
                hasColor = true,
                hasUV = new[] { true, true, false, false, false, false, false, false },
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex { Vertex = new Vector3(0, 0, 0), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), Color = new Color(1, 1, 1, 1), UV = new[] { new[] { 0.25f, 0.2f }, new[] { 0.1f, 0.25f } }, BoneIndices = new[] { 0, 1, 0, 0 }, Weights = new[] { 0.8f, 0.2f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(1, 0, 0), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, -1), Color = new Color(0.5f, 0.7f, 0.9f, 1), UV = new[] { new[] { 0.75f, 0.2f }, new[] { 0.9f, 0.25f } }, BoneIndices = new[] { 0, 1, 0, 0 }, Weights = new[] { 0.5f, 0.5f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(0, 1, 0), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), Color = new Color(1, 0.2f, 0.2f, 1), UV = new[] { new[] { 0.25f, 0.8f }, new[] { 0.1f, 0.9f } }, BoneIndices = new[] { 1, 0, 0, 0 }, Weights = new[] { 1.0f, 0f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(1, 1, 0), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), Color = new Color(0.2f, 1, 0.2f, 1), UV = new[] { new[] { 0.75f, 0.8f }, new[] { 0.9f, 0.9f } }, BoneIndices = new[] { 1, 0, 0, 0 }, Weights = new[] { 0f, 0f, 0f, 0f } }
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
                    new ImportedBone { Path = "RichHero/Hips", Matrix = NumericsRowToAssetStudioMatrix(hipsIbm) },
                    new ImportedBone { Path = "RichHero/Hips/Spine", Matrix = NumericsRowToAssetStudioMatrix(spineIbm) }
                }
            };

            var cubeMesh = CreateUnitCubeMesh("RichHero/UnitCube", "PropMask");

            var blendMesh = new ImportedMesh
            {
                Path = "RichHero/BlendQuad",
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false },
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex { Vertex = new Vector3(0, 0, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 0f, 0f } } },
                    new ImportedVertex { Vertex = new Vector3(1, 0, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 1f, 0f } } },
                    new ImportedVertex { Vertex = new Vector3(0, 1, 0), Normal = new Vector3(0, 0, 1), UV = new[] { new[] { 0f, 1f } } }
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new ImportedSubmesh { Material = "BlendTiled", FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } } }
                }
            };

            var quadrantPng = CreateQuadrantPngBytes(4);
            var normalBmp = CreateSolidImageBytes(4, 4, 128, 128, 255, 255, ImageFormat.Bmp);
            var metalSmoothTga = CreateSolidImageBytes(4, 4, 200, 0, 0, 180, ImageFormat.Tga);
            var emissiveJpg = CreateSolidImageBytes(4, 4, 50, 200, 255, 255, ImageFormat.Jpeg);
            var occlusionPng = CreateSolidImageBytes(4, 4, 230, 230, 230, 255, ImageFormat.Png);
            var roughOnlyPng = CreateSolidImageBytes(4, 4, 100, 100, 100, 255, ImageFormat.Png);

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
                    new ImportedMaterialTexture { Name = "quadrant.png", Dest = 0, Scale = new Vector2(1, 1) },
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
                SurfaceMode = 1f,
                AlphaCutoff = 0.4f,
                Metallic = 0.25f,
                Smoothness = 0.7f,
                DoubleSided = false,
                Textures = new List<ImportedMaterialTexture>
                {
                    new ImportedMaterialTexture { Name = "quadrant.png", Dest = 0, Scale = new Vector2(1, 1) }
                }
            };

            var blendMat = new ImportedMaterial
            {
                Name = "BlendTiled",
                Diffuse = new Color(0.9f, 0.7f, 0.5f, 0.6f),
                SurfaceMode = 3f,
                Metallic = 0.6f,
                Roughness = 0.4f,
                DoubleSided = false,
                Textures = new List<ImportedMaterialTexture>
                {
                    new ImportedMaterialTexture { Name = "quadrant.png", Dest = 0, Scale = new Vector2(2f, 3f), Offset = new Vector2(0.1f, 0.2f) },
                    new ImportedMaterialTexture { Name = "roughonly.png", Dest = 8, Scale = new Vector2(1, 1) }
                }
            };

            var morph = new ImportedMorph
            {
                Path = "RichHero",
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
                                        Vertex = new ImportedVertex { Vertex = new Vector3(1, 0.2f, 0), Normal = new Vector3(0, 0.1f, 0), Tangent = new Vector4(0, 0.05f, 0, 0) }
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

            var anim = new ImportedKeyframedAnimation { Name = "Wave", SampleRate = 30f };
            var spineTrack = anim.FindTrack("RichHero/Hips/Spine");
            spineTrack.Translations.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(0, 1, 0)));
            spineTrack.Translations.Add(new ImportedKeyframe<Vector3>(1f, new Vector3(0, 1.5f, 0)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(0, 0, 0)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Vector3>(0.5f, new Vector3(0, 120, 0)));
            spineTrack.Rotations.Add(new ImportedKeyframe<Vector3>(1f, new Vector3(0, 240, 0)));
            spineTrack.Scalings.Add(new ImportedKeyframe<Vector3>(0f, new Vector3(1, 1, 1)));
            spineTrack.Scalings.Add(new ImportedKeyframe<Vector3>(1f, new Vector3(1.1f, 1.1f, 1.1f)));

            var morphTrack = anim.FindTrack("RichHero");
            var smileCurve = morphTrack.FindOrCreateBlendShape("blendShape.Smile");
            smileCurve.Keyframes.Add(new ImportedKeyframe<float>(0f, 0f));
            smileCurve.Keyframes.Add(new ImportedKeyframe<float>(1f, 50f));
            var blinkCurve = morphTrack.FindOrCreateBlendShape("Blink");
            blinkCurve.Keyframes.Add(new ImportedKeyframe<float>(0f, 0f));
            blinkCurve.Keyframes.Add(new ImportedKeyframe<float>(1f, 100f));

            var res = new SimpleImported(root);
            res.MeshList.Add(bodyMesh);
            res.MeshList.Add(cubeMesh);
            res.MeshList.Add(blendMesh);
            res.MaterialList.Add(pbrMat);
            res.MaterialList.Add(maskMat);
            res.MaterialList.Add(blendMat);
            res.TextureList.Add(new ImportedTexture(new MemoryStream(quadrantPng), "quadrant.png"));
            res.TextureList.Add(new ImportedTexture(new MemoryStream(normalBmp), "normal.bmp"));
            res.TextureList.Add(new ImportedTexture(new MemoryStream(metalSmoothTga), "metallic.tga"));
            res.TextureList.Add(new ImportedTexture(new MemoryStream(emissiveJpg), "emissive.jpg"));
            res.TextureList.Add(new ImportedTexture(new MemoryStream(occlusionPng), "occlusion.png"));
            res.TextureList.Add(new ImportedTexture(new MemoryStream(roughOnlyPng), "roughonly.png"));
            res.MorphList.Add(morph);
            res.AnimationList.Add(anim);
            return res;
        }

        private static SimpleImported BuildTransformedMultiSkinMorphModel(
            out System.Numerics.Vector3 expectedHeadMorphDeltaArmatureSpace,
            out System.Numerics.Vector3[] origHeadVerts,
            out System.Numerics.Matrix4x4 origHeadIbm)
        {
            static System.Numerics.Matrix4x4 MakeLocalRow(Vector3 pos, Vector3 eulerDeg, Vector3 scale)
            {
                float rx = (float)(eulerDeg.X * Math.PI / 180.0);
                float ry = (float)(eulerDeg.Y * Math.PI / 180.0);
                float rz = (float)(eulerDeg.Z * Math.PI / 180.0);
                var rotMat =
                    System.Numerics.Matrix4x4.CreateRotationX(rx) *
                    System.Numerics.Matrix4x4.CreateRotationY(ry) *
                    System.Numerics.Matrix4x4.CreateRotationZ(rz);
                return System.Numerics.Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z)
                     * rotMat
                     * System.Numerics.Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z);
            }

            var root = new ImportedFrame { Name = "CharRoot", LocalPosition = Vector3.Zero, LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var arma = new ImportedFrame { Name = "Armature", LocalPosition = new Vector3(10, 2, -5), LocalRotation = new Vector3(0, 0, 90), LocalScale = Vector3.One };
            var hips = new ImportedFrame { Name = "Hips", LocalPosition = new Vector3(0, 1, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var spine = new ImportedFrame { Name = "Spine", LocalPosition = new Vector3(0, 1, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var neck = new ImportedFrame { Name = "Neck", LocalPosition = new Vector3(0, 0.5f, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };
            var head = new ImportedFrame { Name = "Head", LocalPosition = new Vector3(0, 0.5f, 0), LocalRotation = Vector3.Zero, LocalScale = Vector3.One };

            var bodyNode = new ImportedFrame { Name = "BodyPart", LocalPosition = new Vector3(1, 0, 0), LocalRotation = new Vector3(-90, 0, 0), LocalScale = new Vector3(1.2f, 1.2f, 1.2f) };
            var headNode = new ImportedFrame { Name = "HeadPart", LocalPosition = new Vector3(0.5f, 1.0f, -0.3f), LocalRotation = new Vector3(0, 45, 0), LocalScale = new Vector3(1.5f, 1.5f, 1.5f) };
            var mirrorNode = new ImportedFrame { Name = "MirroredPart", LocalPosition = new Vector3(-1f, 0.5f, 0f), LocalRotation = Vector3.Zero, LocalScale = new Vector3(-1f, 1f, 1f) };

            root.AddChild(arma);
            arma.AddChild(hips);
            hips.AddChild(spine);
            spine.AddChild(neck);
            neck.AddChild(head);
            root.AddChild(bodyNode);
            root.AddChild(headNode);
            root.AddChild(mirrorNode);

            var gArma = MakeLocalRow(arma.LocalPosition, arma.LocalRotation, arma.LocalScale);
            var gHips = MakeLocalRow(hips.LocalPosition, hips.LocalRotation, hips.LocalScale) * gArma;
            var gSpine = MakeLocalRow(spine.LocalPosition, spine.LocalRotation, spine.LocalScale) * gHips;
            var gNeck = MakeLocalRow(neck.LocalPosition, neck.LocalRotation, neck.LocalScale) * gSpine;
            var gHead = MakeLocalRow(head.LocalPosition, head.LocalRotation, head.LocalScale) * gNeck;

            var gBody = MakeLocalRow(bodyNode.LocalPosition, bodyNode.LocalRotation, bodyNode.LocalScale);
            var gHeadMesh = MakeLocalRow(headNode.LocalPosition, headNode.LocalRotation, headNode.LocalScale);
            var gMirror = MakeLocalRow(mirrorNode.LocalPosition, mirrorNode.LocalRotation, mirrorNode.LocalScale);

            System.Numerics.Matrix4x4.Invert(gArma, out var invGArma);
            System.Numerics.Matrix4x4.Invert(gHips, out var invGHips);
            System.Numerics.Matrix4x4.Invert(gSpine, out var invGSpine);
            System.Numerics.Matrix4x4.Invert(gNeck, out var invGNeck);
            System.Numerics.Matrix4x4.Invert(gHead, out var invGHead);

            // Unity bindPose = G_mesh * inv(G_joint)
            var bodyMesh = new ImportedMesh
            {
                Path = "CharRoot/BodyPart",
                hasNormal = true,
                hasUV = Array.Empty<bool>(),
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex { Vertex = new Vector3(-0.3f, 0.0f, 0f), Normal = new Vector3(0, 0, 1), BoneIndices = new[] { 0, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(0.3f, 0.0f, 0f), Normal = new Vector3(0, 0, 1), BoneIndices = new[] { 0, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(0.0f, 0.8f, 0f), Normal = new Vector3(0, 0, 1), BoneIndices = new[] { 1, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } }
                },
                SubmeshList = new List<ImportedSubmesh> { new ImportedSubmesh { Material = "Default", FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } } } },
                BoneList = new List<ImportedBone>
                {
                    new ImportedBone { Path = "CharRoot/Armature/Hips", Matrix = NumericsRowToAssetStudioMatrix(gBody * invGHips) },
                    new ImportedBone { Path = "CharRoot/Armature/Hips/Spine", Matrix = NumericsRowToAssetStudioMatrix(gBody * invGSpine) }
                }
            };

            origHeadVerts = new[]
            {
                new System.Numerics.Vector3(-0.2f, 0.0f, 0f),
                new System.Numerics.Vector3(0.2f, 0.0f, 0f),
                new System.Numerics.Vector3(0.0f, 0.4f, 0f)
            };
            origHeadIbm = gHeadMesh * invGHead;

            var headMesh = new ImportedMesh
            {
                Path = "CharRoot/HeadPart",
                hasNormal = true,
                hasTangent = true,
                hasUV = Array.Empty<bool>(),
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex { Vertex = new Vector3(origHeadVerts[0].X, origHeadVerts[0].Y, origHeadVerts[0].Z), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), BoneIndices = new[] { 0, 1, 0, 0 }, Weights = new[] { 0.4f, 0.6f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(origHeadVerts[1].X, origHeadVerts[1].Y, origHeadVerts[1].Z), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), BoneIndices = new[] { 1, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(origHeadVerts[2].X, origHeadVerts[2].Y, origHeadVerts[2].Z), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), BoneIndices = new[] { 2, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } }
                },
                SubmeshList = new List<ImportedSubmesh> { new ImportedSubmesh { Material = "Default", FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } } } },
                BoneList = new List<ImportedBone>
                {
                    new ImportedBone { Path = "CharRoot/Armature/Hips/Spine/Neck", Matrix = NumericsRowToAssetStudioMatrix(gHeadMesh * invGNeck) },
                    new ImportedBone { Path = "CharRoot/Armature/Hips/Spine/Neck/Head", Matrix = NumericsRowToAssetStudioMatrix(origHeadIbm) },
                    new ImportedBone { Path = "CharRoot/Armature/Hips/Spine/Neck/Head", Matrix = NumericsRowToAssetStudioMatrix(origHeadIbm) } // duplicate bone slot test
                }
            };

            var rawHeadMorphDelta = new System.Numerics.Vector3(0f, 0.2f, 0.1f);
            expectedHeadMorphDeltaArmatureSpace = System.Numerics.Vector3.TransformNormal(rawHeadMorphDelta, gHeadMesh * invGArma);

            var headMorph = new ImportedMorph
            {
                Path = "CharRoot/HeadPart",
                Channels = new List<ImportedMorphChannel>
                {
                    new ImportedMorphChannel
                    {
                        Name = "RaiseBrow",
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
                                        Index = 2,
                                        Vertex = new ImportedVertex
                                        {
                                            Vertex = new Vector3(
                                                origHeadVerts[2].X + rawHeadMorphDelta.X,
                                                origHeadVerts[2].Y + rawHeadMorphDelta.Y,
                                                origHeadVerts[2].Z + rawHeadMorphDelta.Z),
                                            Normal = new Vector3(0f, 0.1f, 0f),
                                            Tangent = new Vector4(0f, 0.05f, 0f, 0f)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var mirrorMesh = new ImportedMesh
            {
                Path = "CharRoot/MirroredPart",
                hasNormal = true,
                hasTangent = true,
                hasUV = Array.Empty<bool>(),
                VertexList = new List<ImportedVertex>
                {
                    new ImportedVertex { Vertex = new Vector3(0f, 0f, 0f), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), BoneIndices = new[] { 0, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(1f, 0f, 0f), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), BoneIndices = new[] { 0, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } },
                    new ImportedVertex { Vertex = new Vector3(0f, 1f, 0f), Normal = new Vector3(0, 0, 1), Tangent = new Vector4(1, 0, 0, 1), BoneIndices = new[] { 0, 0, 0, 0 }, Weights = new[] { 1f, 0f, 0f, 0f } }
                },
                SubmeshList = new List<ImportedSubmesh> { new ImportedSubmesh { Material = "Default", FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } } } },
                BoneList = new List<ImportedBone>
                {
                    new ImportedBone { Path = "CharRoot/Armature/Hips", Matrix = NumericsRowToAssetStudioMatrix(gMirror * invGHips) }
                }
            };

            var res = new SimpleImported(root);
            res.MeshList.Add(bodyMesh);
            res.MeshList.Add(headMesh);
            res.MeshList.Add(mirrorMesh);
            res.MorphList.Add(headMorph);
            return res;
        }

        private static ImportedMesh CreateUnitCubeMesh(string path, string materialName)
        {
            var mesh = new ImportedMesh
            {
                Path = path,
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false }
            };
            var faces = new List<ImportedFace>();

            void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
            {
                int b = mesh.VertexList.Count;
                mesh.VertexList.Add(new ImportedVertex { Vertex = p0, Normal = n, UV = new[] { new[] { 0f, 0f } } });
                mesh.VertexList.Add(new ImportedVertex { Vertex = p1, Normal = n, UV = new[] { new[] { 1f, 0f } } });
                mesh.VertexList.Add(new ImportedVertex { Vertex = p2, Normal = n, UV = new[] { new[] { 1f, 1f } } });
                mesh.VertexList.Add(new ImportedVertex { Vertex = p3, Normal = n, UV = new[] { new[] { 0f, 1f } } });
                faces.Add(new ImportedFace { VertexIndices = new[] { b, b + 1, b + 2 } });
                faces.Add(new ImportedFace { VertexIndices = new[] { b, b + 2, b + 3 } });
            }

            AddQuad(new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1));
            AddQuad(new Vector3(1, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 0, -1));
            AddQuad(new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 0));
            AddQuad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(-1, 0, 0));
            AddQuad(new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 0));
            AddQuad(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), new Vector3(0, -1, 0));

            mesh.SubmeshList.Add(new ImportedSubmesh { Material = materialName, FaceList = faces });
            return mesh;
        }

        private static byte[] CreateQuadrantPngBytes(int size)
        {
            // Asymmetric 4-quadrant PNG (top-down layout):
            // Top-left = Red, Top-right = Green, Bottom-left = Blue, Bottom-right = Yellow
            var bgra = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    byte r, g, b;
                    if (y < size / 2 && x < size / 2) { r = 255; g = 0; b = 0; }
                    else if (y < size / 2) { r = 0; g = 255; b = 0; }
                    else if (x < size / 2) { r = 0; g = 0; b = 255; }
                    else { r = 255; g = 255; b = 0; }

                    int off = (y * size + x) * 4;
                    bgra[off + 0] = b;
                    bgra[off + 1] = g;
                    bgra[off + 2] = r;
                    bgra[off + 3] = 255;
                }
            }
            using var bmp = ImageExtensions.CreateBgraUnpremulBitmap(bgra, size, size, flipVertical: false);
            using var ms = bmp.ConvertToStream(ImageFormat.Png);
            return ms.ToArray();
        }

        private static JsonElement FindByProp(JsonElement array, string propName, string expectedValue)
        {
            foreach (var elem in array.EnumerateArray())
            {
                if (elem.TryGetProperty(propName, out var p) && string.Equals(p.GetString(), expectedValue, StringComparison.Ordinal))
                {
                    return elem;
                }
            }
            throw new Xunit.Sdk.XunitException($"Element with {propName} == '{expectedValue}' not found.");
        }

        private static void AssertJsonFloatNear(JsonElement elem, float expected, float tolerance)
        {
            float actual = elem.GetSingle();
            Assert.True(MathF.Abs(actual - expected) <= tolerance,
                string.Format(CultureInfo.InvariantCulture, "Expected {0} ± {1}, got {2}.", expected, tolerance, actual));
        }

        private static void AssertVec3Near(JsonElement arrayElem, float ex, float ey, float ez, float tolerance)
        {
            AssertJsonFloatNear(arrayElem[0], ex, tolerance);
            AssertJsonFloatNear(arrayElem[1], ey, tolerance);
            AssertJsonFloatNear(arrayElem[2], ez, tolerance);
        }

        [Fact]
        public void CreateFromModelGroup_WithRendererAndAvatarAndClip_AvoidsDuplicateSkeletonAndPreservesAnimationAndParentMaterialTexture()
        {
            var manager = new AssetsManager();
            var mainFile = CreateSyntheticSerializedFile(manager, "character.assets");
            var texFile = CreateSyntheticSerializedFile(manager, "textures.assets");
            mainFile.m_Externals.Add(new FileIdentifier
            {
                fileName = "textures.assets",
                pathName = "archive:/textures/textures.assets"
            });

            // 1. Verify PPtr lazy external recovery: probe BEFORE texFile is added to assetsFileList,
            //    then add texFile and confirm TryGet resolves the Texture2D cleanly (no stale -1 cache).
            var tex = CreateSyntheticRgbaTexture(texFile, pathId: 500, name: "HeroAlbedo", width: 4, height: 4);
            var texPtr = new PPtr<Texture>(fileID: 1, pathID: 500, mainFile);
            Assert.False(texPtr.TryGet<Texture2D>(out _));

            manager.assetsFileList.Add(mainFile);
            manager.assetsFileList.Add(texFile);
            Assert.True(texPtr.TryGet<Texture2D>(out var resolvedTex));
            Assert.Same(tex, resolvedTex);

            // 2. Build Material Variant (child m_Parent -> parentMat holding _BaseMap)
            var texEnv = CreateUninitialized<UnityTexEnv>();
            texEnv.m_Texture = texPtr;
            texEnv.m_Scale = new Vector2(1f, 1f);
            texEnv.m_Offset = new Vector2(0f, 0f);
            var parentMat = CreateSyntheticMaterial(mainFile, pathId: 400, name: "HeroBaseMat", new[]
            {
                new KeyValuePair<string, UnityTexEnv>("_BaseMap", texEnv)
            });
            var variantMat = CreateSyntheticMaterial(mainFile, pathId: 401, name: "HeroVariantMat", Array.Empty<KeyValuePair<string, UnityTexEnv>>());
            variantMat.m_Parent = new PPtr<Material>(0, 400, mainFile);

            // 3. Build scene Transform hierarchy: CharRoot -> Hips -> Spine + BodyMeshGO
            var charRootGo = CreateSyntheticGameObject(mainFile, pathId: 10, name: "CharRoot", out var charRootTr);
            var hipsGo = CreateSyntheticGameObject(mainFile, pathId: 11, name: "Hips", out var hipsTr, parent: charRootTr, localPos: new Vector3(0f, 1f, 0f));
            var spineGo = CreateSyntheticGameObject(mainFile, pathId: 12, name: "Spine", out var spineTr, parent: hipsTr, localPos: new Vector3(0f, 0.5f, 0f));
            var bodyGo = CreateSyntheticGameObject(mainFile, pathId: 13, name: "BodyMesh", out var bodyTr, parent: charRootTr);

            // 4. Build skinned Mesh bound to [Hips, Spine]
            var mesh = CreateSyntheticSkinnedMesh(mainFile, pathId: 200, name: "HeroBody", bonePaths: new[] { "Hips", "Hips/Spine" });

            var smr = CreateUninitialized<SkinnedMeshRenderer>();
            smr.assetsFile = mainFile;
            smr.m_PathID = 300;
            smr.m_GameObject = new PPtr<GameObject>(0, 13, mainFile);
            smr.m_Mesh = new PPtr<Mesh>(0, 200, mainFile);
            smr.m_Materials = new[] { new PPtr<Material>(0, 401, mainFile) };
            smr.m_Bones = new[]
            {
                new PPtr<Transform>(0, hipsTr.m_PathID, mainFile),
                new PPtr<Transform>(0, spineTr.m_PathID, mainFile)
            };
            mainFile.AddObject(smr);
            bodyGo.m_SkinnedMeshRenderer = smr;

            // 5. Build Avatar with m_AvatarSkeletonPose (and null m_DefaultPose!)
            uint hipsCrc = ComputeUnityCrc32("Hips");
            uint spineCrc = ComputeUnityCrc32("Hips/Spine");
            var avatar = CreateUninitialized<Avatar>();
            avatar.assetsFile = mainFile;
            avatar.m_PathID = 600;
            avatar.m_Name = "HeroAvatar";
            avatar.m_TOS = new[]
            {
                new KeyValuePair<uint, string>(0u, string.Empty),
                new KeyValuePair<uint, string>(hipsCrc, "Hips"),
                new KeyValuePair<uint, string>(spineCrc, "Hips/Spine")
            };
            var avSkel = CreateUninitialized<Skeleton>();
            avSkel.m_Node = new[]
            {
                CreateNode(-1),
                CreateNode(0),
                CreateNode(1)
            };
            avSkel.m_ID = new[] { 0u, hipsCrc, spineCrc };
            var avPose = CreateUninitialized<SkeletonPose>();
            avPose.m_X = new[]
            {
                CreateXForm(Vector3.Zero),
                CreateXForm(new Vector3(0, 1, 0)),
                CreateXForm(new Vector3(0, 0.5f, 0))
            };
            var avConst = CreateUninitialized<AvatarConstant>();
            avConst.m_AvatarSkeleton = avSkel;
            avConst.m_DefaultPose = null;
            avConst.m_AvatarSkeletonPose = avPose;
            avatar.m_Avatar = avConst;
            mainFile.AddObject(avatar);

            // 6. Build AnimationClip animating Hips/Spine via DenseClip
            var clip = CreateUninitialized<AnimationClip>();
            clip.assetsFile = mainFile;
            clip.m_PathID = 700;
            clip.m_Name = "SpineWave";
            clip.m_SampleRate = 30f;
            clip.m_Legacy = false;
            clip.m_ClipBindingConstant = new AnimationClipBindingConstant
            {
                genericBindings = new[]
                {
                    new GenericBinding { path = spineCrc, attribute = 2, typeID = ClassIDType.Transform }
                },
                pptrCurveMapping = Array.Empty<PPtr<AssetStudio.Object>>()
            };
            var streamed = CreateUninitialized<StreamedClip>();
            streamed.curveCount = 0;
            streamed.data = Array.Empty<uint>();
            var dense = CreateUninitialized<DenseClip>();
            dense.m_FrameCount = 2;
            dense.m_CurveCount = 4;
            dense.m_SampleRate = 1f;
            dense.m_BeginTime = 0f;
            dense.m_SampleArray = new[]
            {
                0f, 0f, 0f, 1f,
                0f, 0.3826834f, 0f, 0.9238795f // 45 deg around Y
            };
            var constant = CreateUninitialized<ConstantClip>();
            constant.data = Array.Empty<float>();
            var innerClip = CreateUninitialized<Clip>();
            innerClip.m_StreamedClip = streamed;
            innerClip.m_DenseClip = dense;
            innerClip.m_ConstantClip = constant;
            var muscleClip = CreateUninitialized<ClipMuscleConstant>();
            muscleClip.m_StartTime = 0f;
            muscleClip.m_StopTime = 1f;
            muscleClip.m_Clip = innerClip;
            clip.m_MuscleClip = muscleClip;
            mainFile.AddObject(clip);

            // 7. Convert via CreateFromModelGroup(rootGameObject: null, renderers: [smr], avatar, [clip])
            var converter = ModelConverter.CreateFromModelGroup(
                "HeroGroup",
                rootGameObject: null,
                new[] { mesh },
                new Renderer[] { smr },
                avatar,
                new[] { clip },
                ImageFormat.Png,
                combineMeshes: false,
                partTransforms: null,
                partMaterials: new IReadOnlyList<Material>[] { new[] { variantMat } },
                partNames: new[] { "BodyMesh" });

            // Assert NO duplicate "Spine" frame under RootFrame AND renderer stayed parented under CharRoot (not flattened)
            var allSpines = converter.RootFrame.FindChilds("Spine").ToList();
            Assert.Single(allSpines);
            var bodyFrames = converter.RootFrame.FindChilds("BodyMesh").ToList();
            Assert.Single(bodyFrames);
            Assert.Equal("CharRoot", bodyFrames[0].Parent?.Name);

            // Assert skinned bone path AND animation track path point to the EXACT same Spine frame!
            Assert.Single(converter.MeshList);
            var spineBone = converter.MeshList[0].BoneList!.FirstOrDefault(b => b.Path != null && b.Path.EndsWith("/Spine", StringComparison.Ordinal));
            Assert.NotNull(spineBone);
            Assert.Equal(allSpines[0].Path, spineBone!.Path);

            Assert.Single(converter.AnimationList);
            var spineTrack = converter.AnimationList[0].TrackList.FirstOrDefault(t => t.Path == allSpines[0].Path);
            Assert.NotNull(spineTrack);
            Assert.Equal(2, spineTrack!.Rotations.Count);

            // Assert Material Variant inherited parent texture cleanly
            Assert.Single(converter.MaterialList);
            Assert.Single(converter.MaterialList[0].Textures);
            Assert.Single(converter.TextureList);

            // Assert GLB export binds animation channel directly to the actual skinned Spine joint node AND exports texture!
            var tempDir = CreateTempDirectory("AnimClipModelGroupFix");
            try
            {
                var glbPath = Path.Combine(tempDir, "hero_anim.glb");
                ModelExporter.ExportGltf(glbPath, converter, binary: true, skins: true, animation: true, blendShape: true, scaleFactor: 1f, combineMeshes: false);

                var doc = ValidateGltfOrGlbFile(glbPath, expectBinary: true);
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("textures", out var textures) && textures.GetArrayLength() == 1);
                Assert.True(root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() == 1);
                var skinJoints = skins[0].GetProperty("joints").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();

                Assert.True(root.TryGetProperty("animations", out var anims) && anims.GetArrayLength() == 1);
                var channels = anims[0].GetProperty("channels").EnumerateArray().ToList();
                Assert.Single(channels);
                int animatedNodeIdx = channels[0].GetProperty("target").GetProperty("node").GetInt32();
                Assert.Contains(animatedNodeIdx, skinJoints);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
            // Also verify custom/ShaderGraph/packed-alpha diffuse slot names (_Color_Texture, _AlbedoRGBAlphaA, Texture2D_XXXX) bind as Dest=0
            var customTexEnv = CreateUninitialized<UnityTexEnv>();
            customTexEnv.m_Texture = new PPtr<Texture>(1, 100, mainFile);
            customTexEnv.m_Scale = new Vector2(1f, 1f);
            customTexEnv.m_Offset = Vector2.Zero;
            var customMat = CreateSyntheticMaterial(mainFile, pathId: 402, name: "CustomShaderMat", new[]
            {
                new KeyValuePair<string, UnityTexEnv>("_RampTex", customTexEnv),
                new KeyValuePair<string, UnityTexEnv>("_Color_Texture", customTexEnv)
            });
            var packedAlphaMat = CreateSyntheticMaterial(mainFile, pathId: 403, name: "PackedAlphaMat", new[]
            {
                new KeyValuePair<string, UnityTexEnv>("_AlbedoRGBAlphaA", customTexEnv)
            });
            var shaderGraphMat = CreateSyntheticMaterial(mainFile, pathId: 404, name: "ShaderGraphMat", new[]
            {
                new KeyValuePair<string, UnityTexEnv>("_OutlineMask", customTexEnv),
                new KeyValuePair<string, UnityTexEnv>("Texture2D_9F3A10B2", customTexEnv)
            });
            foreach (var testMat in new[] { customMat, packedAlphaMat, shaderGraphMat })
            {
                var matConv = ModelConverter.CreateFromModelGroup(
                    groupName: testMat.m_Name,
                    rootGameObject: null,
                    meshes: new[] { mesh },
                    renderers: new Renderer[] { smr },
                    avatar: null,
                    animationClips: null,
                    imageFormat: ImageFormat.Png,
                    combineMeshes: false,
                    partTransforms: null,
                    partMaterials: new IReadOnlyList<Material>[] { new Material[] { testMat } },
                    partNames: null);
                Assert.Single(matConv.MaterialList);
                Assert.Contains(matConv.MaterialList[0].Textures, t => t.Dest == 0);
            }
        }

        private static T CreateUninitialized<T>() where T : class
            => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

        private static Node CreateNode(int parentId)
        {
            var n = CreateUninitialized<Node>();
            n.m_ParentId = parentId;
            n.m_AxesId = -1;
            return n;
        }

        private static XForm CreateXForm(Vector3 t)
        {
            var x = CreateUninitialized<XForm>();
            x.t = t;
            x.q = new Quaternion(0, 0, 0, 1);
            x.s = Vector3.One;
            return x;
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager manager, string fileName)
        {
            var sf = (SerializedFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
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

        private static GameObject CreateSyntheticGameObject(
            SerializedFile sf,
            long pathId,
            string name,
            out Transform tr,
            Transform? parent = null,
            Vector3? localPos = null)
        {
            var go = (GameObject)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            go.assetsFile = sf;
            go.m_PathID = pathId;
            go.m_Name = name;

            tr = (Transform)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Transform));
            tr.assetsFile = sf;
            tr.m_PathID = pathId + 1000;
            tr.m_GameObject = new PPtr<GameObject>(0, pathId, sf);
            tr.m_LocalPosition = localPos ?? Vector3.Zero;
            tr.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            tr.m_LocalScale = Vector3.One;
            tr.m_Father = parent != null ? new PPtr<Transform>(0, parent.m_PathID, sf) : new PPtr<Transform>(0, 0, sf);
            tr.m_Children = Array.Empty<PPtr<Transform>>();

            go.m_Transform = tr;
            go.m_Components = new[] { new PPtr<Component>(0, tr.m_PathID, sf) };

            sf.AddObject(go);
            sf.AddObject(tr);

            if (parent != null)
            {
                var children = parent.m_Children.ToList();
                children.Add(new PPtr<Transform>(0, tr.m_PathID, sf));
                parent.m_Children = children.ToArray();
            }

            return go;
        }

        private static Texture2D CreateSyntheticRgbaTexture(SerializedFile sf, long pathId, string name, int width, int height)
        {
            var tex = (Texture2D)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            tex.assetsFile = sf;
            tex.m_PathID = pathId;
            tex.m_Name = name;
            tex.m_Width = width;
            tex.m_Height = height;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.version = sf.version;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = 220;
                pixels[i + 1] = 120;
                pixels[i + 2] = 60;
                pixels[i + 3] = 255;
            }
            var rr = (ResourceReader)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ResourceReader));
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
            var mat = (Material)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Material));
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

        private static Mesh CreateSyntheticSkinnedMesh(SerializedFile sf, long pathId, string name, string[] bonePaths)
        {
            var mesh = (Mesh)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            mesh.assetsFile = sf;
            mesh.m_PathID = pathId;
            mesh.m_Name = name;
            mesh.m_VertexCount = 3;
            mesh.m_Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            mesh.m_Normals = new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_UV0 = new[] { 0f, 0f, 1f, 0f, 0f, 1f };
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            var subMesh = CreateUninitialized<SubMesh>();
            subMesh.firstByte = 0;
            subMesh.indexCount = 3;
            subMesh.firstVertex = 0;
            subMesh.vertexCount = 3;
            subMesh.baseVertex = 0;
            mesh.m_SubMeshes = new[] { subMesh };
            mesh.m_BoneNameHashes = bonePaths.Select(ComputeUnityCrc32).ToArray();
            mesh.m_BindPose = new[]
            {
                Matrix4x4.Scale(Vector3.One),
                Matrix4x4.Scale(Vector3.One)
            };
            mesh.m_Skin = new[]
            {
                new BoneWeights4 { boneIndex = new[] { 0, 1, 0, 0 }, weight = new[] { 0.5f, 0.5f, 0f, 0f } },
                new BoneWeights4 { boneIndex = new[] { 1, 0, 0, 0 }, weight = new[] { 1f, 0f, 0f, 0f } },
                new BoneWeights4 { boneIndex = new[] { 1, 0, 0, 0 }, weight = new[] { 1f, 0f, 0f, 0f } }
            };
            typeof(Mesh).GetField("m_DataProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(mesh, new object());
            sf.AddObject(mesh);
            return mesh;
        }

        private static uint ComputeUnityCrc32(string path)
        {
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(path);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
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
