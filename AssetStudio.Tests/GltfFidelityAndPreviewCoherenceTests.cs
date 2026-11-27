#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using SkiaSharp;
using Xunit;

namespace AssetStudio.Tests
{
    /// <summary>
    /// Portable, 100% synthetic tests verifying strict glTF 2.0 structural validity,
    /// explicit material/shader PBR approximation diagnostics, and 3D Preview vs. exported GLB coherence.
    /// </summary>
    public sealed class GltfFidelityAndPreviewCoherenceTests
    {
        [Fact]
        public void Gltf2Export_FullFeatureSyntheticRig_PassesStrictStructuralValidation()
        {
            var tempDir = CreateTempDir("gltf_full_validation");
            try
            {
                var albedoBytes = CreateSolidPngBytes(4, 4, new SKColor(220, 120, 80, 200));
                var normalBytes = CreateSolidPngBytes(4, 4, new SKColor(128, 128, 255, 255));
                var metalSmoothBytes = CreateSolidPngBytes(4, 4, new SKColor(200, 0, 0, 180));
                var occlusionBytes = CreateSolidPngBytes(4, 4, new SKColor(240, 240, 240, 255));

                var root = new ImportedFrame { Name = "RigRoot", LocalScale = new AssetStudio.Vector3(1f, 1f, 1f) };
                var hips = new ImportedFrame
                {
                    Name = "Hips",
                    LocalPosition = new AssetStudio.Vector3(0f, 1.0f, 0f),
                    LocalScale = new AssetStudio.Vector3(1f, 1f, 1f)
                };
                var spine = new ImportedFrame
                {
                    Name = "Spine",
                    LocalPosition = new AssetStudio.Vector3(0f, 0.5f, 0f),
                    LocalScale = new AssetStudio.Vector3(1f, 1f, 1f)
                };
                var meshNode = new ImportedFrame
                {
                    Name = "CharacterMesh",
                    LocalPosition = new AssetStudio.Vector3(0f, 0f, 0f),
                    LocalScale = new AssetStudio.Vector3(1f, 1f, 1f)
                };
                hips.AddChild(spine);
                root.AddChild(hips);
                root.AddChild(meshNode);

                var verts = new List<ImportedVertex>
                {
                    CreateSkinnedVertex(new AssetStudio.Vector3(-0.5f, 0f, 0f), new[] { 0.0f, 0.0f }, new[] { 0.0f, 0.25f }, 0, 1.0f),
                    CreateSkinnedVertex(new AssetStudio.Vector3(0.5f, 0f, 0f),  new[] { 1.0f, 0.0f }, new[] { 0.5f, 0.25f }, 0, 1.0f),
                    CreateSkinnedVertex(new AssetStudio.Vector3(0.5f, 1.5f, 0f), new[] { 1.0f, 1.0f }, new[] { 0.5f, 0.75f }, 1, 1.0f),
                    CreateSkinnedVertex(new AssetStudio.Vector3(-0.5f, 1.5f, 0f), new[] { 0.0f, 1.0f }, new[] { 0.0f, 0.75f }, 1, 1.0f)
                };

                var importedMesh = new ImportedMesh
                {
                    Path = meshNode.Path,
                    VertexList = verts,
                    hasNormal = true,
                    hasUV = new[] { true, true },
                    hasColor = true,
                    SubmeshList = new List<ImportedSubmesh>
                    {
                        new ImportedSubmesh
                        {
                            Material = "StandardOpaqueMat",
                            FaceList = new List<ImportedFace>
                            {
                                new ImportedFace { VertexIndices = new[] { 0, 1, 2 } }
                            }
                        },
                        new ImportedSubmesh
                        {
                            Material = "CutoutLeafMat",
                            FaceList = new List<ImportedFace>
                            {
                                new ImportedFace { VertexIndices = new[] { 0, 2, 3 } }
                            }
                        }
                    },
                    BoneList = new List<ImportedBone>
                    {
                        new ImportedBone { Path = hips.Path, Matrix = CreateIdentityMatrix() },
                        new ImportedBone { Path = spine.Path, Matrix = CreateIdentityMatrix() }
                    }
                };

                var morph = new ImportedMorph
                {
                    Path = meshNode.Path,
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
                                    VertexList = new List<ImportedMorphVertex>
                                    {
                                        new ImportedMorphVertex
                                        {
                                            Index = 2,
                                            Vertex = new ImportedVertex
                                            {
                                                Vertex = new AssetStudio.Vector3(0.5f, 1.65f, 0.05f),
                                                Normal = new AssetStudio.Vector3(0f, 0f, 1f)
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                var anim = new ImportedKeyframedAnimation { Name = "Wave", SampleRate = 30f };
                var spineTrack = anim.FindTrack(spine.Path);
                spineTrack.Translations.Add(new ImportedKeyframe<AssetStudio.Vector3>(0f, new AssetStudio.Vector3(0f, 0.5f, 0f)));
                spineTrack.Translations.Add(new ImportedKeyframe<AssetStudio.Vector3>(15f, new AssetStudio.Vector3(0f, 0.6f, 0.1f)));
                spineTrack.Rotations.Add(new ImportedKeyframe<AssetStudio.Vector3>(0f, new AssetStudio.Vector3(0f, 0f, 0f)));
                spineTrack.Rotations.Add(new ImportedKeyframe<AssetStudio.Vector3>(15f, new AssetStudio.Vector3(10f, 20f, 0f)));
                spineTrack.Scalings.Add(new ImportedKeyframe<AssetStudio.Vector3>(0f, new AssetStudio.Vector3(1f, 1f, 1f)));
                spineTrack.Scalings.Add(new ImportedKeyframe<AssetStudio.Vector3>(15f, new AssetStudio.Vector3(1.1f, 1.1f, 1.1f)));

                var meshTrack = anim.FindTrack(meshNode.Path);
                var blendCurve = meshTrack.FindOrCreateBlendShape("Smile");
                blendCurve.Keyframes.Add(new ImportedKeyframe<float>(0f, 0f));
                blendCurve.Keyframes.Add(new ImportedKeyframe<float>(15f, 100f));

                var imported = new SyntheticImportedModel
                {
                    RootFrame = root,
                    MeshList = new List<ImportedMesh> { importedMesh },
                    MorphList = new List<ImportedMorph> { morph },
                    AnimationList = new List<ImportedKeyframedAnimation> { anim },
                    TextureList = new List<ImportedTexture>
                    {
                        new ImportedTexture(new MemoryStream(albedoBytes), "albedo.png"),
                        new ImportedTexture(new MemoryStream(normalBytes), "normal.png"),
                        new ImportedTexture(new MemoryStream(metalSmoothBytes), "metal_smooth.png"),
                        new ImportedTexture(new MemoryStream(occlusionBytes), "ao.png")
                    },
                    MaterialList = new List<ImportedMaterial>
                    {
                        new ImportedMaterial
                        {
                            Name = "StandardOpaqueMat",
                            Diffuse = new AssetStudio.Color(0.9f, 0.8f, 0.7f, 1f),
                            Metallic = 0.25f,
                            Smoothness = 0.6f,
                            SurfaceMode = 0f,
                            UnityShaderMetadata = new ImportedUnityShaderMetadata
                            {
                                ShaderName = "Standard",
                                Classification = "Standard"
                            },
                            Textures = new List<ImportedMaterialTexture>
                            {
                                new ImportedMaterialTexture { Name = "albedo.png", Dest = 0, Scale = new AssetStudio.Vector2(2f, 2f), Offset = new AssetStudio.Vector2(0.1f, 0.2f) },
                                new ImportedMaterialTexture { Name = "normal.png", Dest = 1, Scale = new AssetStudio.Vector2(1f, 1f) },
                                new ImportedMaterialTexture { Name = "metal_smooth.png", Dest = 3, Scale = new AssetStudio.Vector2(1f, 1f) },
                                new ImportedMaterialTexture { Name = "ao.png", Dest = 5, Scale = new AssetStudio.Vector2(1f, 1f) }
                            }
                        },
                        new ImportedMaterial
                        {
                            Name = "CutoutLeafMat",
                            Diffuse = new AssetStudio.Color(0.4f, 0.85f, 0.3f, 1f),
                            SurfaceMode = 1f,
                            AlphaCutoff = 0.45f,
                            DoubleSided = true,
                            UnityShaderMetadata = new ImportedUnityShaderMetadata
                            {
                                ShaderName = "Universal Render Pipeline/Lit",
                                Classification = "Cutout"
                            },
                            Textures = new List<ImportedMaterialTexture>
                            {
                                new ImportedMaterialTexture { Name = "albedo.png", Dest = 0, Scale = new AssetStudio.Vector2(1f, 1f) }
                            }
                        }
                    }
                };

                var glbPath = Path.Combine(tempDir, "full_rig.glb");
                var stats = ModelExporter.ExportGltfWithStats(
                    glbPath,
                    imported,
                    binary: true,
                    skins: true,
                    animation: true,
                    blendShape: true,
                    scaleFactor: 0.01f);

                var validation = Gltf2StructuralValidator.ValidateGlbFile(glbPath);
                Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
                Assert.True(validation.NodeCount >= 4);
                Assert.Equal(1, validation.MeshCount);
                Assert.Equal(2, validation.MaterialCount);
                Assert.Equal(1, validation.SkinCount);
                Assert.Equal(1, validation.AnimationCount);

                // Verify scaleFactor (0.01) applied to node hierarchy translations and vertex bounds
                var glbBytes = File.ReadAllBytes(glbPath);
                Assert.True(Gltf2StructuralValidator.TryExtractGlbParts(glbBytes, out var json, out _));
                using var doc = JsonDocument.Parse(json);
                var rootEl = doc.RootElement;

                var hipsNode = rootEl.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("name").GetString() == "Hips");
                Assert.Equal(0.01f, hipsNode.GetProperty("translation")[1].GetSingle(), 5);

                // Verify UV0 & UV1 attributes, alpha MASK cutoff, and KHR_texture_transform declared in extensionsUsed
                var prim0 = rootEl.GetProperty("meshes")[0].GetProperty("primitives")[0];
                var attrs = prim0.GetProperty("attributes");
                Assert.True(attrs.TryGetProperty("TEXCOORD_0", out _));
                Assert.True(attrs.TryGetProperty("TEXCOORD_1", out _));
                Assert.True(attrs.TryGetProperty("JOINTS_0", out _));
                Assert.True(attrs.TryGetProperty("WEIGHTS_0", out _));
                Assert.Equal(1, prim0.GetProperty("targets").GetArrayLength());

                var mat1 = rootEl.GetProperty("materials")[1];
                Assert.Equal("MASK", mat1.GetProperty("alphaMode").GetString());
                Assert.Equal(0.45f, mat1.GetProperty("alphaCutoff").GetSingle(), 4);
                Assert.True(mat1.GetProperty("doubleSided").GetBoolean());

                var exts = rootEl.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                Assert.Contains("KHR_texture_transform", exts);

                // Verify diagnostics distinguish Standard PBR vs URP approximation
                Assert.Equal(2, stats.MaterialDiagnostics.Count);
                Assert.Equal(1, stats.ApproximatedMaterialsCount);
                var stdDiag = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "StandardOpaqueMat");
                Assert.Equal(MaterialConversionMode.StandardPbr, stdDiag.Mode);
                Assert.False(stdDiag.HasApproximations);
                Assert.Contains("baseColorTexture", stdDiag.PreservedChannels);
                Assert.Contains("normalTexture", stdDiag.PreservedChannels);
                Assert.Contains("metallicRoughnessTexture", stdDiag.PreservedChannels);
                Assert.Contains("occlusionTexture", stdDiag.PreservedChannels);
                Assert.Contains("uvTransform", stdDiag.PreservedChannels);

                var urpDiag = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "CutoutLeafMat");
                Assert.Equal(MaterialConversionMode.PbrApproximation, urpDiag.Mode);
                Assert.True(urpDiag.HasApproximations);
                Assert.Contains(urpDiag.Approximations, a => a.Contains("URP", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                TryDeleteDir(tempDir);
            }
        }

        [Fact]
        public void GltfMaterialDiagnostics_ExplicitlyClassifyPreservedApproximatedAndFallbackMaterials()
        {
            var tempDir = CreateTempDir("gltf_material_diagnostics");
            try
            {
                var texBytes = CreateSolidPngBytes(2, 2, new SKColor(180, 180, 180, 128));
                var imported = new SyntheticImportedModel
                {
                    RootFrame = new ImportedFrame { Name = "Root", LocalScale = new AssetStudio.Vector3(1f, 1f, 1f) },
                    TextureList = new List<ImportedTexture>
                    {
                        new ImportedTexture(new MemoryStream(texBytes), "tex_main.png"),
                        new ImportedTexture(new MemoryStream(texBytes), "spec_gloss.png")
                    },
                    MaterialList = new List<ImportedMaterial>
                    {
                        new ImportedMaterial
                        {
                            Name = "UnlitSign",
                            Diffuse = new AssetStudio.Color(1f, 1f, 1f, 1f),
                            IsUnlit = true,
                            UnityShaderMetadata = new ImportedUnityShaderMetadata { ShaderName = "Unlit/Texture", Classification = "Unlit" },
                            Textures = new List<ImportedMaterialTexture> { new ImportedMaterialTexture { Name = "tex_main.png", Dest = 0 } }
                        },
                        new ImportedMaterial
                        {
                            Name = "WaterSurface",
                            Diffuse = new AssetStudio.Color(0.2f, 0.5f, 0.7f, 1f),
                            TransmissionFactor = 0.8f,
                            Ior = 1.333f,
                            Roughness = 0.08f,
                            UnityShaderMetadata = new ImportedUnityShaderMetadata
                            {
                                ShaderName = "Custom/StylizedWater",
                                Classification = "Water",
                                TextureProperties = new Dictionary<string, string> { ["_FoamMask"] = "foam.png" }
                            }
                        },
                        new ImportedMaterial
                        {
                            Name = "UntexturedCutout",
                            Diffuse = new AssetStudio.Color(0.5f, 0.5f, 0.5f, 1f),
                            SurfaceMode = 1f,
                            AlphaCutoff = 0.5f,
                            UnityShaderMetadata = new ImportedUnityShaderMetadata { ShaderName = "Standard", Classification = "Cutout" }
                        },
                        new ImportedMaterial
                        {
                            Name = "SpecularMetal",
                            Diffuse = new AssetStudio.Color(0.7f, 0.7f, 0.7f, 1f),
                            UnityShaderMetadata = new ImportedUnityShaderMetadata { ShaderName = "Standard (Specular setup)", Classification = "Standard" },
                            Textures = new List<ImportedMaterialTexture> { new ImportedMaterialTexture { Name = "spec_gloss.png", Dest = 2 } }
                        },
                        new ImportedMaterial
                        {
                            Name = "BrokenTextureMat",
                            Diffuse = new AssetStudio.Color(1f, 0f, 1f, 1f),
                            UnityShaderMetadata = new ImportedUnityShaderMetadata { ShaderName = "HDRP/Lit", Classification = "Standard" },
                            Textures = new List<ImportedMaterialTexture> { new ImportedMaterialTexture { Name = "missing_albedo.png", Dest = 0 } }
                        }
                    }
                };

                var glbPath = Path.Combine(tempDir, "materials.glb");
                var stats = ModelExporter.ExportGltfWithStats(glbPath, imported, binary: true);

                var validation = Gltf2StructuralValidator.ValidateGlbFile(glbPath);
                Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

                Assert.Equal(5, stats.MaterialDiagnostics.Count);
                Assert.Equal(4, stats.ApproximatedMaterialsCount);
                Assert.Equal(1, stats.FallbackMaterialsCount);

                var unlit = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "UnlitSign");
                Assert.Equal(MaterialConversionMode.Unlit, unlit.Mode);
                Assert.Contains("KHR_materials_unlit", unlit.ExtensionsUsed);
                Assert.False(unlit.HasApproximations);

                var water = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "WaterSurface");
                Assert.Equal(MaterialConversionMode.PbrApproximation, water.Mode);
                Assert.Contains("KHR_materials_transmission", water.ExtensionsUsed);
                Assert.Contains("KHR_materials_ior", water.ExtensionsUsed);
                Assert.Contains("_FoamMask", water.IgnoredTextureSlots);

                var cutout = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "UntexturedCutout");
                Assert.Equal("OPAQUE", cutout.AlphaMode);
                Assert.Contains(cutout.Approximations, a => a.Contains("MASK downgraded to OPAQUE", StringComparison.Ordinal));

                var spec = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "SpecularMetal");
                Assert.Equal(MaterialConversionMode.PbrApproximation, spec.Mode);
                Assert.Contains(spec.Approximations, a => a.Contains("Specular-glossiness", StringComparison.OrdinalIgnoreCase));

                var broken = Assert.Single(stats.MaterialDiagnostics, d => d.MaterialName == "BrokenTextureMat");
                Assert.Equal(MaterialConversionMode.BasicFallback, broken.Mode);
                Assert.Contains("Diffuse:missing_albedo.png", broken.MissingTextures);

                var exportDiag = broken.ToExportDiagnostic("level0", 42);
                Assert.Equal("MaterialBasicFallback", exportDiag.Code);
                Assert.Equal(ExportDiagnosticSeverity.Warning, exportDiag.Severity);
                Assert.Contains("missing_albedo.png", exportDiag.Message, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Preview3DAndExportedGlb_AgreeOnGeometryBoundsUvConventionAndMaterialChannels()
        {
            var tempDir = CreateTempDir("preview_glb_coherence");
            try
            {
                // Build a synthetic Unity Mesh object in memory (2 submeshes, 6 vertices, 2 triangles, UV0)
                var unityMesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
                typeof(Mesh).GetField("m_DataProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(unityMesh, true);
                unityMesh.m_Name = "CoherenceProp";
                unityMesh.m_VertexCount = 6;
                // Unity left-handed coordinates
                unityMesh.m_Vertices = new[]
                {
                    1.0f, 0.0f, 2.0f,
                    3.0f, 0.0f, 2.0f,
                    2.0f, 2.5f, 4.0f,
                    -2.0f, 0.5f, -1.0f,
                    -1.0f, 0.5f, -1.0f,
                    -1.5f, 1.5f, 0.5f
                };
                unityMesh.m_Normals = new[]
                {
                    0f, 0f, 1f,
                    0f, 0f, 1f,
                    0f, 0f, 1f,
                    0f, 1f, 0f,
                    0f, 1f, 0f,
                    0f, 1f, 0f
                };
                unityMesh.m_UV0 = new[]
                {
                    0.10f, 0.20f,
                    0.90f, 0.20f,
                    0.50f, 0.85f,
                    0.25f, 0.10f,
                    0.75f, 0.10f,
                    0.50f, 0.60f
                };
                unityMesh.m_Indices = new List<uint> { 0, 1, 2, 3, 4, 5 };
                unityMesh.m_SubMeshes = new[]
                {
                    CreateSubMesh(0, 3),
                    CreateSubMesh(6, 3)
                };

                // 1) Extract the exact 3D Preview snapshot using MeshPreviewControl's geometry pipeline
                var previewGeo = MeshPreviewControl.BuildPreviewGeometrySnapshot(unityMesh);
                Assert.Equal(6, previewGeo.Vertices.Length);
                Assert.Equal(6, previewGeo.Indices.Length);
                Assert.NotNull(previewGeo.SubMeshIndexCounts);
                Assert.Equal(new[] { 3, 3 }, previewGeo.SubMeshIndexCounts);
                Assert.NotNull(previewGeo.Uvs);

                // 2) Build synthetic Material (transparent URP material) and check preview alpha + color extraction
                var unityMat = (Material)RuntimeHelpers.GetUninitializedObject(typeof(Material));
                unityMat.m_Name = "GlassPanelMat";
                unityMat.m_CustomRenderQueue = 3000;
                unityMat.stringTagMap = new[] { new KeyValuePair<string, string>("RenderType", "Transparent") };
                unityMat.m_SavedProperties = new UnityPropertySheet
                {
                    m_Colors = new[] { new KeyValuePair<string, AssetStudio.Color>("_BaseColor", new AssetStudio.Color(0.25f, 0.65f, 0.90f, 0.55f)) },
                    m_Floats = new[]
                    {
                        new KeyValuePair<string, float>("_Surface", 1f),
                        new KeyValuePair<string, float>("_ZWrite", 0f),
                        new KeyValuePair<string, float>("_Smoothness", 0.8f)
                    },
                    m_Ints = Array.Empty<KeyValuePair<string, int>>(),
                    m_TexEnvs = Array.Empty<KeyValuePair<string, UnityTexEnv>>()
                };

                bool previewPreservesAlpha = MainWindow.ShouldPreserveMaterialTextureAlpha(unityMat, "Universal Render Pipeline/Lit");
                var (pr, pg, pb, pa) = SemanticGraphService.ExtractMaterialBaseColor(unityMat);
                Assert.True(previewPreservesAlpha);
                Assert.Equal(0.25f, pr, 4);
                Assert.Equal(0.65f, pg, 4);
                Assert.Equal(0.90f, pb, 4);
                Assert.Equal(0.55f, pa, 4);

                // 3) Export matching ImportedMesh/ImportedMaterial (following ModelConverter's documented LH->RH -X and UV flip convention)
                var importedVerts = new List<ImportedVertex>(unityMesh.m_VertexCount);
                for (int i = 0; i < unityMesh.m_VertexCount; i++)
                {
                    importedVerts.Add(new ImportedVertex
                    {
                        Vertex = new AssetStudio.Vector3(-unityMesh.m_Vertices[i * 3], unityMesh.m_Vertices[i * 3 + 1], unityMesh.m_Vertices[i * 3 + 2]),
                        Normal = new AssetStudio.Vector3(-unityMesh.m_Normals[i * 3], unityMesh.m_Normals[i * 3 + 1], unityMesh.m_Normals[i * 3 + 2]),
                        UV = new[] { new[] { unityMesh.m_UV0[i * 2], unityMesh.m_UV0[i * 2 + 1] } }
                    });
                }

                var importedModel = new SyntheticImportedModel
                {
                    RootFrame = new ImportedFrame { Name = "CoherenceProp", LocalScale = new AssetStudio.Vector3(1f, 1f, 1f) },
                    MeshList = new List<ImportedMesh>
                    {
                        new ImportedMesh
                        {
                            Path = "CoherenceProp",
                            VertexList = importedVerts,
                            hasNormal = true,
                            hasUV = new[] { true },
                            SubmeshList = new List<ImportedSubmesh>
                            {
                                new ImportedSubmesh
                                {
                                    Material = "GlassPanelMat",
                                    FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 0, 1, 2 } } }
                                },
                                new ImportedSubmesh
                                {
                                    Material = "GlassPanelMat",
                                    FaceList = new List<ImportedFace> { new ImportedFace { VertexIndices = new[] { 3, 4, 5 } } }
                                }
                            }
                        }
                    },
                    MaterialList = new List<ImportedMaterial>
                    {
                        new ImportedMaterial
                        {
                            Name = "GlassPanelMat",
                            Diffuse = new AssetStudio.Color(pr, pg, pb, pa),
                            Smoothness = 0.8f,
                            SurfaceMode = previewPreservesAlpha ? 3f : 0f,
                            UnityShaderMetadata = new ImportedUnityShaderMetadata
                            {
                                ShaderName = "Universal Render Pipeline/Lit",
                                Classification = "Transparent"
                            }
                        }
                    }
                };

                var glbPath = Path.Combine(tempDir, "coherence.glb");
                var stats = ModelExporter.ExportGltfWithStats(glbPath, importedModel, binary: true);
                var glbBytes = File.ReadAllBytes(glbPath);

                var validation = Gltf2StructuralValidator.ValidateGlb(glbBytes);
                Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
                Assert.True(Gltf2StructuralValidator.TryExtractGlbParts(glbBytes, out var json, out var bin));

                using var doc = JsonDocument.Parse(json);
                var rootEl = doc.RootElement;
                var prims = rootEl.GetProperty("meshes")[0].GetProperty("primitives");
                Assert.Equal(previewGeo.SubMeshIndexCounts!.Length, prims.GetArrayLength());

                // Compare exported GLB POSITION bounds against 3D Preview bounds (accounting for documented LH->RH X reflection)
                int posAccIdx = prims[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var posAcc = rootEl.GetProperty("accessors")[posAccIdx];
                Assert.Equal(previewGeo.Vertices.Length, posAcc.GetProperty("count").GetInt32());

                float glbMinX = posAcc.GetProperty("min")[0].GetSingle();
                float glbMinY = posAcc.GetProperty("min")[1].GetSingle();
                float glbMinZ = posAcc.GetProperty("min")[2].GetSingle();
                float glbMaxX = posAcc.GetProperty("max")[0].GetSingle();
                float glbMaxY = posAcc.GetProperty("max")[1].GetSingle();
                float glbMaxZ = posAcc.GetProperty("max")[2].GetSingle();

                const float tolerance = 1e-5f;
                Assert.InRange(MathF.Abs(glbMinX - (-previewGeo.Max.X)), 0f, tolerance);
                Assert.InRange(MathF.Abs(glbMaxX - (-previewGeo.Min.X)), 0f, tolerance);
                Assert.InRange(MathF.Abs(glbMinY - previewGeo.Min.Y), 0f, tolerance);
                Assert.InRange(MathF.Abs(glbMaxY - previewGeo.Max.Y), 0f, tolerance);
                Assert.InRange(MathF.Abs(glbMinZ - previewGeo.Min.Z), 0f, tolerance);
                Assert.InRange(MathF.Abs(glbMaxZ - previewGeo.Max.Z), 0f, tolerance);

                // Compare UV0 binary buffer values (accounting for glTF top-left V = 1 - Unity bottom-left V)
                int uvAccIdx = prims[0].GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32();
                var uvAcc = rootEl.GetProperty("accessors")[uvAccIdx];
                var uvBv = rootEl.GetProperty("bufferViews")[uvAcc.GetProperty("bufferView").GetInt32()];
                int uvBaseOffset = uvBv.GetProperty("byteOffset").GetInt32();
                for (int i = 0; i < previewGeo.Uvs!.Length; i++)
                {
                    float glbU = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(uvBaseOffset + i * 8, 4));
                    float glbV = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(uvBaseOffset + i * 8 + 4, 4));
                    Assert.InRange(MathF.Abs(glbU - previewGeo.Uvs[i].X), 0f, tolerance);
                    Assert.InRange(MathF.Abs(glbV - (1f - previewGeo.Uvs[i].Y)), 0f, tolerance);
                }

                // Compare material base color & alpha mode coherence
                var glbMat = rootEl.GetProperty("materials")[0];
                var bcf = glbMat.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor");
                Assert.Equal(pr, bcf[0].GetSingle(), 4);
                Assert.Equal(pg, bcf[1].GetSingle(), 4);
                Assert.Equal(pb, bcf[2].GetSingle(), 4);
                Assert.Equal(pa, bcf[3].GetSingle(), 4);
                Assert.Equal("BLEND", glbMat.GetProperty("alphaMode").GetString());

                // Confirm approximation diagnostic aligns with preview fallback classification
                var matDiag = Assert.Single(stats.MaterialDiagnostics);
                Assert.Equal(MaterialConversionMode.PbrApproximation, matDiag.Mode);
                var previewDiag = ShaderConversionClassifier.ClassifyPreview(
                    "Universal Render Pipeline/Lit",
                    new[] { 6000, 3, 18 },
                    isWindows: true,
                    d3d11Rendered: false,
                    hasPreviewTexture: true,
                    isMaterialPreview: true);
                Assert.Equal(ShaderPreviewRenderMode.MaterialPbrApproximation, previewDiag.RenderMode);
                Assert.Contains("URP", previewDiag.ShaderFamily, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Gltf2StructuralValidator_RejectsCorruptGlbAndUndeclaredExtension()
        {
            var shortResult = Gltf2StructuralValidator.ValidateGlb(new byte[] { 1, 2, 3, 4 });
            Assert.False(shortResult.IsValid);

            // Build a minimal valid-framed GLB whose JSON uses an extension missing from extensionsUsed
            var json = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"Hidden\",\"extensions\":{\"KHR_node_visibility\":{\"visible\":false}}}]}";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
            int paddedLen = (jsonBytes.Length + 3) & ~3;
            var glb = new byte[12 + 8 + paddedLen];
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), 0x46546C67u);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2u);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)paddedLen);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), 0x4E4F534Au);
            Buffer.BlockCopy(jsonBytes, 0, glb, 20, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < paddedLen; i++) glb[20 + i] = (byte)' ';

            var validation = Gltf2StructuralValidator.ValidateGlb(glb);
            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, e => e.Contains("KHR_node_visibility", StringComparison.Ordinal));
        }

        private static SubMesh CreateSubMesh(uint firstByte, uint indexCount)
        {
            var sub = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub.firstByte = firstByte;
            sub.indexCount = indexCount;
            sub.topology = GfxPrimitiveType.Triangles;
            return sub;
        }

        private static AssetStudio.Matrix4x4 CreateIdentityMatrix() =>
            new AssetStudio.Matrix4x4(new float[]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f
            });

        private static ImportedVertex CreateSkinnedVertex(AssetStudio.Vector3 pos, float[] uv0, float[] uv1, int boneIdx, float weight)
        {
            return new ImportedVertex
            {
                Vertex = pos,
                Normal = new AssetStudio.Vector3(0f, 0f, 1f),
                UV = new[] { uv0, uv1 },
                Color = new AssetStudio.Color(1f, 1f, 1f, 1f),
                BoneIndices = new[] { boneIdx, 0, 0, 0 },
                Weights = new[] { weight, 0f, 0f, 0f }
            };
        }

        private static byte[] CreateSolidPngBytes(int width, int height, SKColor color)
        {
            using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            bmp.Erase(color);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static string CreateTempDir(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        private sealed class SyntheticImportedModel : IImported
        {
            public ImportedFrame RootFrame { get; set; } = new ImportedFrame();
            public List<ImportedMesh> MeshList { get; set; } = new();
            public List<ImportedMaterial> MaterialList { get; set; } = new();
            public List<ImportedTexture> TextureList { get; set; } = new();
            public List<ImportedKeyframedAnimation> AnimationList { get; set; } = new();
            public List<ImportedMorph> MorphList { get; set; } = new();
            public List<ImportedCamera> CameraList { get; set; } = new();
            public List<ImportedLight> LightList { get; set; } = new();
        }
    }
}
