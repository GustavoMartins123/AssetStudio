using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class CombineMeshAndFbxTests
    {
        private sealed class TestModelConverter : ModelConverter
        {
            public TestModelConverter(ImportedFrame rootFrame) : base(ImageFormat.Png)
            {
                RootFrame = rootFrame;
            }
        }

        private sealed class SyntheticImported : IImported
        {
            public ImportedFrame RootFrame { get; set; } = new();
            public List<ImportedMesh> MeshList { get; set; } = new();
            public List<ImportedMaterial> MaterialList { get; set; } = new();
            public List<ImportedTexture> TextureList { get; set; } = new();
            public List<ImportedKeyframedAnimation> AnimationList { get; set; } = new();
            public List<ImportedMorph> MorphList { get; set; } = new();
        }

        [Fact]
        public void CombineMeshes_TwoStaticMeshesWithDistinctTransformsNormalsUvsAndMaterials_TransformsGeometryAndPreservesMaterials()
        {
            var root = CreateFrame("Root", Vector3.Zero, Vector3.Zero, Vector3.One);

            // PartA: translated (10, 0, 0), rotated 90 deg around Y, scaled (2, 2, 2)
            // Under System.Numerics row-vector Rx*Ry*Rz:
            // (1, 0, 0) * Scale(2,2,2) = (2, 0, 0) -> * RotY(90) = (0, 0, -2) -> + (10, 0, 0) = (10, 0, -2)
            var partAFrame = CreateFrame("PartA", new Vector3(10f, 0f, 0f), new Vector3(0f, 90f, 0f), new Vector3(2f, 2f, 2f));
            root.AddChild(partAFrame);

            // PartB: translated (0, 5, -3), non-uniform scale (2, 1, 1), no UV0, no Color
            var partBFrame = CreateFrame("PartB", new Vector3(0f, 5f, -3f), Vector3.Zero, new Vector3(2f, 1f, 1f));
            root.AddChild(partBFrame);

            var invSqrt2 = 1f / (float)Math.Sqrt(2.0);
            var meshA = new ImportedMesh
            {
                Path = partAFrame.Path,
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false },
                hasTangent = true,
                hasColor = true,
                VertexList = new List<ImportedVertex>
                {
                    MakeVertex(new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), new[] { 0.25f, 0.75f }, new Vector4(0f, 0f, 1f, -1f), new Color(1f, 0.5f, 0.25f, 1f)),
                    MakeVertex(new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), new[] { 0.5f, 0.5f }, new Vector4(1f, 0f, 0f, 1f), new Color(0.2f, 0.8f, 0.4f, 1f)),
                    MakeVertex(new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 1f), new[] { 1f, 0f }, new Vector4(0f, 1f, 0f, -1f), new Color(0.1f, 0.2f, 0.9f, 1f))
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new()
                    {
                        Material = "MatA",
                        BaseVertex = 0,
                        FaceList = new List<ImportedFace>
                        {
                            new() { VertexIndices = new[] { 0, 1, 2 } }
                        }
                    }
                }
            };

            var meshB = new ImportedMesh
            {
                Path = partBFrame.Path,
                hasNormal = true,
                hasUV = new[] { false, false, false, false, false, false, false, false },
                hasTangent = false,
                hasColor = false,
                VertexList = new List<ImportedVertex>
                {
                    // Under non-uniform scale (2, 1, 1):
                    // normal (1, 1, 0)/sqrt(2) -> inverse-transpose scale (0.5, 1, 1) -> (0.5, 1, 0) normalized = (1/sqrt(5), 2/sqrt(5), 0)
                    MakeVertex(new Vector3(1f, 2f, 3f), new Vector3(invSqrt2, invSqrt2, 0f), null, Vector4.Zero, default),
                    MakeVertex(new Vector3(2f, 0f, 0f), new Vector3(0f, 1f, 0f), null, Vector4.Zero, default),
                    MakeVertex(new Vector3(0f, 0f, 4f), new Vector3(0f, 0f, 1f), null, Vector4.Zero, default)
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new()
                    {
                        Material = "MatB",
                        BaseVertex = 0,
                        FaceList = new List<ImportedFace>
                        {
                            new() { VertexIndices = new[] { 0, 1, 2 } }
                        }
                    }
                }
            };

            var converter = new TestModelConverter(root);
            converter.MeshList.Add(meshA);
            converter.MeshList.Add(meshB);
            converter.MaterialList.Add(new ImportedMaterial { Name = "MatA", Diffuse = new Color(1, 0, 0, 1) });
            converter.MaterialList.Add(new ImportedMaterial { Name = "MatB", Diffuse = new Color(0, 0, 1, 1) });

            converter.CombineMeshes();

            var combined = Assert.Single(converter.MeshList);
            Assert.Equal("Root", combined.Path);
            Assert.Equal(6, combined.VertexList.Count);
            Assert.Equal(2, combined.SubmeshList.Count);
            Assert.Equal("MatA", combined.SubmeshList[0].Material);
            Assert.Equal("MatB", combined.SubmeshList[1].Material);
            Assert.True(combined.hasNormal);
            Assert.True(combined.hasUV[0]);
            Assert.True(combined.hasTangent);
            Assert.True(combined.hasColor);

            // PartA vertex 0: (1,0,0) * S(2) * Ry(90) + (10,0,0) = (10, 0, -2)
            AssertVector3Near(new Vector3(10f, 0f, -2f), combined.VertexList[0].Vertex, 1e-4f);
            // PartA normal 0: (1,0,0) rotated 90 deg around Y -> (0, 0, -1)
            AssertVector3Near(new Vector3(0f, 0f, -1f), combined.VertexList[0].Normal, 1e-4f);
            // PartA tangent 0: (0,0,1) rotated 90 deg around Y -> (1, 0, 0), handedness W = -1 preserved!
            AssertVector3Near(new Vector3(1f, 0f, 0f), new Vector3(combined.VertexList[0].Tangent.X, combined.VertexList[0].Tangent.Y, combined.VertexList[0].Tangent.Z), 1e-4f);
            Assert.Equal(-1f, combined.VertexList[0].Tangent.W, 4);
            // PartA UV0 & Color preserved
            Assert.Equal(0.25f, combined.VertexList[0].UV[0][0], 4);
            Assert.Equal(0.75f, combined.VertexList[0].UV[0][1], 4);
            Assert.Equal(1f, combined.VertexList[0].Color.R, 4);

            // PartB vertex 0: (1,2,3) * S(2,1,1) + (0,5,-3) = (2, 7, 0)
            AssertVector3Near(new Vector3(2f, 7f, 0f), combined.VertexList[3].Vertex, 1e-4f);
            // PartB normal 0 under non-uniform scale (2,1,1): inverse-transpose gives (1/sqrt(5), 2/sqrt(5), 0)
            float invSqrt5 = 1f / (float)Math.Sqrt(5.0);
            AssertVector3Near(new Vector3(invSqrt5, 2f * invSqrt5, 0f), combined.VertexList[3].Normal, 1e-4f);
            // PartB padded UV0 = (0,0) and padded Color = white (1,1,1,1)
            Assert.NotNull(combined.VertexList[3].UV[0]);
            Assert.Equal(0f, combined.VertexList[3].UV[0][0], 4);
            Assert.Equal(0f, combined.VertexList[3].UV[0][1], 4);
            Assert.Equal(1f, combined.VertexList[3].Color.R, 4);
            Assert.Equal(1f, combined.VertexList[3].Color.A, 4);

            // Triangle indices strictly within [0, VertexList.Count) and properly offset for PartB
            Assert.Equal(new[] { 0, 1, 2 }, combined.SubmeshList[0].FaceList[0].VertexIndices);
            Assert.Equal(new[] { 3, 4, 5 }, combined.SubmeshList[1].FaceList[0].VertexIndices);
            foreach (var sub in combined.SubmeshList)
            {
                foreach (var face in sub.FaceList)
                {
                    foreach (var idx in face.VertexIndices)
                    {
                        var resolved = idx + sub.BaseVertex;
                        Assert.InRange(resolved, 0, combined.VertexList.Count - 1);
                    }
                }
            }
        }

        [Fact]
        public void CombineMeshes_TwoSkinnedMeshesWithOverlappingBonesAndMorphs_DeduplicatesBonesRemapsIndicesAndOffsetsMorphs()
        {
            var root = CreateFrame("Hero", Vector3.Zero, Vector3.Zero, Vector3.One);
            var hips = CreateFrame("Hips", new Vector3(0f, 1f, 0f), Vector3.Zero, Vector3.One);
            var spine = CreateFrame("Spine", new Vector3(0f, 1f, 0f), Vector3.Zero, Vector3.One);
            var leftArm = CreateFrame("LeftArm", new Vector3(-1f, 0f, 0f), Vector3.Zero, Vector3.One);
            var rightArm = CreateFrame("RightArm", new Vector3(1f, 0f, 0f), Vector3.Zero, Vector3.One);
            root.AddChild(hips);
            hips.AddChild(spine);
            spine.AddChild(leftArm);
            spine.AddChild(rightArm);

            var bodyFrame = CreateFrame("BodyMesh", Vector3.Zero, Vector3.Zero, Vector3.One);
            var armorFrame = CreateFrame("ArmorMesh", new Vector3(0f, 2f, 0f), Vector3.Zero, Vector3.One);
            root.AddChild(bodyFrame);
            root.AddChild(armorFrame);

            var identityBind = MakeIdentityBindPose();
            var spineBind = MakeTranslationBindPose(0f, -2f, 0f);
            var rightArmBindInArmorSpace = MakeTranslationBindPose(-1f, 0f, 0f); // Armor at Y=+2, RightArm at (1,2,0)

            var bodyMesh = new ImportedMesh
            {
                Path = bodyFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    MakeSkinnedVertex(new Vector3(0f, 0f, 0f), new[] { 0, 1, 0, 0 }, new[] { 0.7f, 0.3f, 0f, 0f }),
                    MakeSkinnedVertex(new Vector3(1f, 0f, 0f), new[] { 2, 0, 0, 0 }, new[] { 1.0f, 0f, 0f, 0f }),
                    MakeSkinnedVertex(new Vector3(0f, 1f, 0f), new[] { 1, 2, 0, 0 }, new[] { 0.5f, 0.5f, 0f, 0f })
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "BodyMat", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                },
                BoneList = new List<ImportedBone>
                {
                    new() { Path = hips.Path, Matrix = identityBind },
                    new() { Path = spine.Path, Matrix = spineBind },
                    new() { Path = leftArm.Path, Matrix = identityBind }
                }
            };

            var armorMesh = new ImportedMesh
            {
                Path = armorFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    // Local bone 0 = Spine (overlaps Body bone 1!), Local bone 1 = RightArm (new unified bone 3!)
                    MakeSkinnedVertex(new Vector3(0f, 0f, 0f), new[] { 0, 1, 0, 0 }, new[] { 0.4f, 0.6f, 0f, 0f }),
                    MakeSkinnedVertex(new Vector3(1f, 0f, 0f), new[] { 1, 0, 0, 0 }, new[] { 1.0f, 0f, 0f, 0f }),
                    MakeSkinnedVertex(new Vector3(0f, 1f, 0f), new[] { 0, 0, 0, 0 }, new[] { 1.0f, 0f, 0f, 0f })
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "ArmorMat", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                },
                BoneList = new List<ImportedBone>
                {
                    new() { Path = spine.Path, Matrix = identityBind },
                    new() { Path = rightArm.Path, Matrix = rightArmBindInArmorSpace }
                }
            };

            var converter = new TestModelConverter(root);
            converter.MeshList.Add(bodyMesh);
            converter.MeshList.Add(armorMesh);
            converter.MorphList.Add(new ImportedMorph
            {
                Path = bodyFrame.Path,
                Channels = new List<ImportedMorphChannel>
                {
                    new()
                    {
                        Name = "Smile",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new()
                            {
                                Weight = 100f,
                                hasNormals = true,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new()
                                    {
                                        Index = 1,
                                        Vertex = new ImportedVertex { Vertex = new Vector3(1f, 0.25f, 0f), Normal = new Vector3(0f, 0.1f, 0f) }
                                    }
                                }
                            }
                        }
                    },
                    new()
                    {
                        Name = "Blink",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new()
                            {
                                Weight = 100f,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new()
                                    {
                                        Index = 2,
                                        Vertex = new ImportedVertex { Vertex = new Vector3(0f, 0.9f, 0f) }
                                    }
                                }
                            }
                        }
                    }
                }
            });
            converter.MorphList.Add(new ImportedMorph
            {
                Path = armorFrame.Path,
                Channels = new List<ImportedMorphChannel>
                {
                    new()
                    {
                        Name = "Smile",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new()
                            {
                                Weight = 100f,
                                hasNormals = false,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new()
                                    {
                                        Index = 0,
                                        Vertex = new ImportedVertex { Vertex = new Vector3(0f, 0.5f, 0f) }
                                    }
                                }
                            }
                        }
                    },
                    new()
                    {
                        Name = "Recoil",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new()
                            {
                                Weight = 100f,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new()
                                    {
                                        Index = 2,
                                        Vertex = new ImportedVertex { Vertex = new Vector3(0f, 1.5f, 0f) }
                                    }
                                }
                            }
                        }
                    }
                }
            });

            var anim = new ImportedKeyframedAnimation
            {
                Name = "Emote",
                SampleRate = 30f,
                TrackList = new List<ImportedAnimationKeyframedTrack>
                {
                    new()
                    {
                        Path = armorFrame.Path,
                        BlendShape = new ImportedBlendShape
                        {
                            ChannelName = "Recoil",
                            Keyframes = new List<ImportedKeyframe<float>>
                            {
                                new(0f, 0f),
                                new(0.5f, 100f)
                            }
                        }
                    }
                }
            };
            converter.AnimationList.Add(anim);

            converter.CombineMeshes();

            var combined = Assert.Single(converter.MeshList);
            Assert.Equal("Hero", combined.Path);
            Assert.Equal(6, combined.VertexList.Count);

            // Unified bone table: Hips(0), Spine(1), LeftArm(2), RightArm(3)
            Assert.NotNull(combined.BoneList);
            Assert.Equal(4, combined.BoneList.Count);
            Assert.Equal(hips.Path, combined.BoneList[0].Path);
            Assert.Equal(spine.Path, combined.BoneList[1].Path);
            Assert.Equal(leftArm.Path, combined.BoneList[2].Path);
            Assert.Equal(rightArm.Path, combined.BoneList[3].Path);

            // Verify Armor's RightArm bind pose was adjusted by Inverse(ArmorRelMatrix = Translate(0,2,0)):
            // Inv(Translate(0,2,0)) * Translate(-1,0,0) = Translate(-1,-2,0) (row 3 in AssetStudio Matrix4x4!)
            Assert.Equal(-1f, combined.BoneList[3].Matrix[3, 0], 4);
            Assert.Equal(-2f, combined.BoneList[3].Matrix[3, 1], 4);
            Assert.Equal(0f, combined.BoneList[3].Matrix[3, 2], 4);

            // Verify Armor vertex 0 (combined index 3): local bones (0=Spine, 1=RightArm) -> unified (1, 3)
            Assert.Equal(1, combined.VertexList[3].BoneIndices[0]);
            Assert.Equal(3, combined.VertexList[3].BoneIndices[1]);
            Assert.Equal(0.4f, combined.VertexList[3].Weights[0], 4);
            Assert.Equal(0.6f, combined.VertexList[3].Weights[1], 4);

            // Verify Armor vertex 1 (combined index 4): local bone 1=RightArm -> unified 3
            Assert.Equal(3, combined.VertexList[4].BoneIndices[0]);
            Assert.Equal(1.0f, combined.VertexList[4].Weights[0], 4);

            // Verify merged morphs
            var mergedMorph = Assert.Single(converter.MorphList);
            Assert.Equal("Hero", mergedMorph.Path);
            Assert.Equal(3, mergedMorph.Channels.Count);

            var smileChannel = Assert.Single(mergedMorph.Channels, c => c.Name == "Smile");
            var smileKf = Assert.Single(smileChannel.KeyframeList);
            Assert.True(smileKf.hasNormals);
            Assert.Equal(2, smileKf.VertexList.Count);
            Assert.Equal(1u, smileKf.VertexList[0].Index);
            Assert.Equal(3u, smileKf.VertexList[1].Index); // 0 + bodyMesh.VertexList.Count(3)
            // Armor morph vertex (0, 0.5, 0) transformed by Armor translation (0, 2, 0) -> (0, 2.5, 0)
            AssertVector3Near(new Vector3(0f, 2.5f, 0f), smileKf.VertexList[1].Vertex.Vertex, 1e-4f);

            var recoilChannel = Assert.Single(mergedMorph.Channels, c => c.Name == "Recoil");
            var recoilVertex = Assert.Single(Assert.Single(recoilChannel.KeyframeList).VertexList);
            Assert.Equal(5u, recoilVertex.Index); // 2 + 3
            AssertVector3Near(new Vector3(0f, 3.5f, 0f), recoilVertex.Vertex.Vertex, 1e-4f);

            // Verify blendshape animation track was remapped to the combined mesh path
            var combinedTrack = Assert.Single(converter.AnimationList[0].TrackList, t => t.Path == "Hero");
            Assert.NotNull(combinedTrack.BlendShape);
            Assert.Equal("Recoil", combinedTrack.BlendShape.ChannelName);
        }

        [Fact]
        public void CombineMeshes_SingleMeshUntouched_AndStaticPlusSkinnedPreservesZeroWeights_AndNegativeScaleFlipsWinding()
        {
            // 1. Single-mesh model untouched
            var singleRoot = CreateFrame("SingleRoot", Vector3.Zero, Vector3.Zero, Vector3.One);
            var singlePart = CreateFrame("OnlyPart", new Vector3(3f, 0f, 0f), Vector3.Zero, Vector3.One);
            singleRoot.AddChild(singlePart);
            var singleMesh = new ImportedMesh
            {
                Path = singlePart.Path,
                VertexList = new List<ImportedVertex>
                {
                    MakeVertex(new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), null, Vector4.Zero, default)
                },
                SubmeshList = new List<ImportedSubmesh>()
            };
            var singleConverter = new TestModelConverter(singleRoot);
            singleConverter.MeshList.Add(singleMesh);
            singleConverter.CombineMeshes();
            Assert.Same(singleMesh, Assert.Single(singleConverter.MeshList));
            Assert.Equal(1f, singleConverter.MeshList[0].VertexList[0].Vertex.X, 4);

            // 2. Static + Skinned + Negative Scale mirrored mesh
            var mixRoot = CreateFrame("MixRoot", Vector3.Zero, Vector3.Zero, Vector3.One);
            var boneFrame = CreateFrame("Bone0", Vector3.Zero, Vector3.Zero, Vector3.One);
            var skinnedFrame = CreateFrame("SkinnedPart", Vector3.Zero, Vector3.Zero, Vector3.One);
            var mirroredStaticFrame = CreateFrame("MirroredStaticPart", Vector3.Zero, Vector3.Zero, new Vector3(-1f, 1f, 1f));
            mixRoot.AddChild(boneFrame);
            mixRoot.AddChild(skinnedFrame);
            mixRoot.AddChild(mirroredStaticFrame);

            var skinned = new ImportedMesh
            {
                Path = skinnedFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    MakeSkinnedVertex(new Vector3(0f, 0f, 0f), new[] { 0, 0, 0, 0 }, new[] { 1f, 0f, 0f, 0f }),
                    MakeSkinnedVertex(new Vector3(1f, 0f, 0f), new[] { 0, 0, 0, 0 }, new[] { 1f, 0f, 0f, 0f }),
                    MakeSkinnedVertex(new Vector3(0f, 1f, 0f), new[] { 0, 0, 0, 0 }, new[] { 1f, 0f, 0f, 0f })
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "MatSkin", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                },
                BoneList = new List<ImportedBone>
                {
                    new() { Path = boneFrame.Path, Matrix = MakeIdentityBindPose() }
                }
            };

            var mirroredStatic = new ImportedMesh
            {
                Path = mirroredStaticFrame.Path,
                hasNormal = true,
                hasUV = new bool[8],
                VertexList = new List<ImportedVertex>
                {
                    MakeVertex(new Vector3(2f, 0f, 0f), new Vector3(1f, 0f, 0f), null, Vector4.Zero, default),
                    MakeVertex(new Vector3(3f, 0f, 0f), new Vector3(1f, 0f, 0f), null, Vector4.Zero, default),
                    MakeVertex(new Vector3(2f, 1f, 0f), new Vector3(1f, 0f, 0f), null, Vector4.Zero, default)
                },
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = "MatStatic", FaceList = new List<ImportedFace> { new() { VertexIndices = new[] { 0, 1, 2 } } } }
                }
            };

            var combined = ModelConverter.CombineMeshList(new[] { skinned, mirroredStatic }, mixRoot, "Combined");
            Assert.Equal(6, combined.VertexList.Count);
            Assert.NotNull(combined.BoneList);
            Assert.Single(combined.BoneList);

            // Static vertices got zero weights safely
            for (int v = 3; v < 6; v++)
            {
                Assert.NotNull(combined.VertexList[v].BoneIndices);
                Assert.NotNull(combined.VertexList[v].Weights);
                Assert.All(combined.VertexList[v].Weights, w => Assert.Equal(0f, w));
            }

            // Mirrored static mesh flipped X position, flipped normal X, and flipped triangle winding (3, 5, 4)
            AssertVector3Near(new Vector3(-2f, 0f, 0f), combined.VertexList[3].Vertex, 1e-4f);
            AssertVector3Near(new Vector3(-1f, 0f, 0f), combined.VertexList[3].Normal, 1e-4f);
            Assert.Equal(new[] { 3, 5, 4 }, combined.SubmeshList[1].FaceList[0].VertexIndices);
        }

        [Fact]
        public void FbxSharpieExporter_BinaryFbx7400_MultiPartAndCombinedWithAnimationsAndBlendShapes_WritesValidFbxAndReport()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"as_test_fbx_combine_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            try
            {
                var imported = BuildRichMultiPartImportedModel();

                var multiPartPath = Path.Combine(tempRoot, "character_multipart.fbx");
                var combinedPath = Path.Combine(tempRoot, "character_combined.fbx");

                ModelExporter.ExportFbx(
                    multiPartPath,
                    imported,
                    skins: true,
                    animation: true,
                    blendShape: true,
                    castToBone: false,
                    boneSize: 10f,
                    scaleFactor: 1f,
                    versionIndex: 4,
                    isAscii: false,
                    combineMeshes: false);

                ModelExporter.ExportFbx(
                    combinedPath,
                    imported,
                    skins: true,
                    animation: true,
                    blendShape: true,
                    castToBone: false,
                    boneSize: 10f,
                    scaleFactor: 1f,
                    versionIndex: 4,
                    isAscii: false,
                    combineMeshes: true);

                // Source imported model must NOT have been mutated by combineMeshes: true
                Assert.Equal(2, imported.MeshList.Count);

                // Validate both binary FBX 7400 files thoroughly
                var multiAudit = AuditBinaryFbx7400(multiPartPath);
                var combinedAudit = AuditBinaryFbx7400(combinedPath);

                Assert.Equal(7400, multiAudit.Version);
                Assert.Equal(7400, combinedAudit.Version);
                Assert.True(multiAudit.CompressedArrayCount > 0, "Expected zlib-compressed arrays in multi-part FBX.");
                Assert.True(combinedAudit.CompressedArrayCount > 0, "Expected zlib-compressed arrays in combined FBX.");
                Assert.Equal(0, multiAudit.NonFiniteFloatOrDoubleCount);
                Assert.Equal(0, combinedAudit.NonFiniteFloatOrDoubleCount);

                // Multi-part FBX exports 2 Geometry Mesh nodes; combined FBX exports 1 Geometry Mesh node
                Assert.Equal(2, multiAudit.MeshGeometryCount);
                Assert.Equal(1, combinedAudit.MeshGeometryCount);
                Assert.Equal(multiAudit.TotalMeshVertexDoubleCount, combinedAudit.TotalMeshVertexDoubleCount);
                Assert.True(combinedAudit.BlendShapeDeformerCount >= 1);
                Assert.True(combinedAudit.SkinClusterCount >= 2);
                Assert.True(combinedAudit.AnimationStackCount >= 1);
                Assert.True(combinedAudit.MaterialCount >= 2);

                // Verify exported texture file + export reports
                Assert.True(File.Exists(Path.Combine(tempRoot, "hero_diffuse.png")));

                var multiReportPath = Path.Combine(tempRoot, "character_multipart.fbx-export-report.txt");
                var combinedReportPath = Path.Combine(tempRoot, "character_combined.fbx-export-report.txt");
                Assert.True(File.Exists(multiReportPath));
                Assert.True(File.Exists(combinedReportPath));

                var multiReport = File.ReadAllText(multiReportPath);
                var combinedReport = File.ReadAllText(combinedReportPath);
                Assert.Contains("Exported meshes: 2", multiReport);
                Assert.Contains("Exported meshes: 1", combinedReport);
                Assert.Contains("Exported materials: 2", combinedReport);
                Assert.Contains("Exported textures: 1", combinedReport);
                Assert.Contains("Exported animation stacks: 1", combinedReport);
                Assert.Contains("Zero-weight bindings omitted:", combinedReport);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Fact]
        public void ModelConverter_CreateFromModelGroup_WithNullRootAndPartTransforms_BuildsUnifiedAndCombinedModel()
        {
            var meshA = CreateSyntheticUnityMesh("PartMeshA", vertexCount: 3, offsetX: 0f);
            var meshB = CreateSyntheticUnityMesh("PartMeshB", vertexCount: 3, offsetX: 0f);

            // Unity column-vector row-major matrix: translate X by +4 on PartB
            // Note: ModelConverter flips X into FBX right-handed space (-x, y, z)
            var partBMatrix = new float[]
            {
                1f, 0f, 0f, 4f,
                0f, 1f, 0f, 2f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f
            };

            var multiConverter = ModelConverter.CreateFromModelGroup(
                "ModularProp",
                rootGameObject: null,
                meshes: new[] { meshA, meshB },
                renderers: null,
                avatar: null,
                animationClips: null,
                imageFormat: ImageFormat.Png,
                combineMeshes: false,
                partTransforms: new float[]?[] { null, partBMatrix }!,
                partMaterials: null,
                partNames: new[] { "Base", "TopCap" });

            Assert.Equal("ModularProp", multiConverter.RootFrame.Name);
            Assert.Equal(2, multiConverter.MeshList.Count);
            Assert.Equal("ModularProp/Base", multiConverter.MeshList[0].Path);
            Assert.Equal("ModularProp/TopCap", multiConverter.MeshList[1].Path);

            var combinedConverter = ModelConverter.CreateFromModelGroup(
                "ModularProp",
                rootGameObject: null,
                meshes: new[] { meshA, meshB },
                renderers: null,
                avatar: null,
                animationClips: null,
                imageFormat: ImageFormat.Png,
                combineMeshes: true,
                partTransforms: new float[]?[] { null, partBMatrix }!,
                partMaterials: null,
                partNames: new[] { "Base", "TopCap" });

            var combinedMesh = Assert.Single(combinedConverter.MeshList);
            Assert.Equal("ModularProp", combinedMesh.Path);
            Assert.Equal(6, combinedMesh.VertexList.Count);
            // PartB vertex 0 was Unity (0,0,0) -> FBX local (0,0,0) + PartB frame FBX position (-4, 2, 0) = (-4, 2, 0)
            AssertVector3Near(new Vector3(-4f, 2f, 0f), combinedMesh.VertexList[3].Vertex, 1e-4f);

            // Also verify named CombineMeshes attaches an identity child frame under RootFrame and exports valid FBX 7400
            multiConverter.CombineMeshes("ModularProp_Merged");
            var namedCombined = Assert.Single(multiConverter.MeshList);
            Assert.Equal("ModularProp/ModularProp_Merged", namedCombined.Path);
            Assert.NotNull(multiConverter.RootFrame.FindChild("ModularProp_Merged", recursive: false));

            var tempFbx = Path.Combine(Path.GetTempPath(), $"as_test_modelgroup_{Guid.NewGuid():N}.fbx");
            try
            {
                ModelExporter.ExportFbx(tempFbx, multiConverter, skins: true, animation: true, blendShape: true, castToBone: false, boneSize: 10f, scaleFactor: 1f, versionIndex: 4, isAscii: false);
                var audit = AuditBinaryFbx7400(tempFbx);
                Assert.Equal(7400, audit.Version);
                Assert.Equal(1, audit.MeshGeometryCount);
                Assert.Equal(18, audit.TotalMeshVertexDoubleCount); // 6 vertices * 3 doubles
                Assert.Equal(0, audit.NonFiniteFloatOrDoubleCount);
            }
            finally
            {
                if (File.Exists(tempFbx)) File.Delete(tempFbx);
                var report = Path.Combine(Path.GetDirectoryName(tempFbx)!, Path.GetFileNameWithoutExtension(tempFbx) + ".fbx-export-report.txt");
                if (File.Exists(report)) File.Delete(report);
            }
        }

        [Fact]
        public void ExportOptionsState_AndAvaloniaAppSettings_CombineMeshes_RoundTripPersistence()
        {
            var options = new ExportOptionsState();
            Assert.False(options.CombineMeshes);

            options.CombineMeshes = true;
            var cloned = options.Clone();
            Assert.True(cloned.CombineMeshes);

            var copied = new ExportOptionsState();
            copied.CopyFrom(cloned);
            Assert.True(copied.CombineMeshes);

            // JSON round-trip on AvaloniaAppSettings
            var settings = new AvaloniaAppSettings { CombineMeshes = true };
            Assert.True(settings.ExportOptions.CombineMeshes);
            var json = JsonSerializer.Serialize(settings);
            var deserialized = JsonSerializer.Deserialize<AvaloniaAppSettings>(json);
            Assert.NotNull(deserialized);
            Assert.True(deserialized!.CombineMeshes);
            Assert.True(deserialized.ExportOptions.CombineMeshes);

            // SQLite ProjectManagerStore round-trip (global + per-project)
            var tempRoot = Path.Combine(Path.GetTempPath(), $"as_test_combine_settings_{Guid.NewGuid():N}");
            try
            {
                var store = new ProjectManagerStore(Path.Combine(tempRoot, "Store"));
                store.SaveGlobalSettings(settings);
                var reloadedGlobal = store.LoadGlobalSettings();
                Assert.True(reloadedGlobal.CombineMeshes);
                Assert.True(reloadedGlobal.ExportOptions.CombineMeshes);

                var project = new ManagedProject { Name = "CombineTestProject", ProjectRoot = tempRoot };
                store.SaveProject(project);
                var projectSettings = new AvaloniaAppSettings();
                projectSettings.ExportOptions.CombineMeshes = true;
                store.SaveProjectSettings(project.Id, projectSettings);

                var reloadedProject = store.LoadProjectSettings(project);
                Assert.True(reloadedProject.CombineMeshes);
                Assert.True(reloadedProject.ExportOptions.CombineMeshes);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        private static ImportedFrame CreateFrame(string name, Vector3 pos, Vector3 rotDeg, Vector3 scale)
        {
            return new ImportedFrame
            {
                Name = name,
                LocalPosition = pos,
                LocalRotation = rotDeg,
                LocalScale = scale
            };
        }

        private static ImportedVertex MakeVertex(Vector3 pos, Vector3 normal, float[]? uv0, Vector4 tangent, Color color)
        {
            var uv = new float[8][];
            if (uv0 != null)
            {
                uv[0] = uv0;
            }
            return new ImportedVertex
            {
                Vertex = pos,
                Normal = normal,
                UV = uv,
                Tangent = tangent,
                Color = color
            };
        }

        private static ImportedVertex MakeSkinnedVertex(Vector3 pos, int[] boneIndices, float[] weights)
        {
            return new ImportedVertex
            {
                Vertex = pos,
                Normal = new Vector3(0f, 1f, 0f),
                UV = new float[8][],
                BoneIndices = boneIndices,
                Weights = weights
            };
        }

        private static Matrix4x4 MakeIdentityBindPose()
        {
            return MakeTranslationBindPose(0f, 0f, 0f);
        }

        private static Matrix4x4 MakeTranslationBindPose(float tx, float ty, float tz)
        {
            var m = new Matrix4x4();
            m[0, 0] = 1f;
            m[1, 1] = 1f;
            m[2, 2] = 1f;
            m[3, 3] = 1f;
            m[3, 0] = tx;
            m[3, 1] = ty;
            m[3, 2] = tz;
            return m;
        }

        private static Mesh CreateSyntheticUnityMesh(string name, int vertexCount, float offsetX)
        {
            var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            mesh.m_Name = name;
            mesh.m_VertexCount = vertexCount;
            mesh.m_Vertices = new float[vertexCount * 3];
            mesh.m_Normals = new float[vertexCount * 3];
            mesh.m_UV0 = new float[vertexCount * 2];
            for (int i = 0; i < vertexCount; i++)
            {
                mesh.m_Vertices[i * 3] = offsetX + (i == 1 ? 1f : 0f);
                mesh.m_Vertices[i * 3 + 1] = i == 2 ? 1f : 0f;
                mesh.m_Vertices[i * 3 + 2] = 0f;
                mesh.m_Normals[i * 3 + 1] = 1f;
                mesh.m_UV0[i * 2] = i * 0.25f;
                mesh.m_UV0[i * 2 + 1] = 0.5f;
            }
            mesh.m_Indices = new List<uint> { 0, 1, 2 };
            var subMesh = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            subMesh.firstByte = 0;
            subMesh.indexCount = 3;
            subMesh.firstVertex = 0;
            subMesh.vertexCount = (uint)vertexCount;
            subMesh.baseVertex = 0;
            mesh.m_SubMeshes = new[] { subMesh };
            return mesh;
        }

        private static SyntheticImported BuildRichMultiPartImportedModel()
        {
            var root = CreateFrame("HeroRoot", Vector3.Zero, Vector3.Zero, Vector3.One);
            var pelvis = CreateFrame("Pelvis", new Vector3(0f, 1f, 0f), Vector3.Zero, Vector3.One);
            var chest = CreateFrame("Chest", new Vector3(0f, 1f, 0f), Vector3.Zero, Vector3.One);
            var unusedBone = CreateFrame("UnusedBone", new Vector3(0f, 2f, 0f), Vector3.Zero, Vector3.One);
            root.AddChild(pelvis);
            pelvis.AddChild(chest);
            chest.AddChild(unusedBone);

            var part1Frame = CreateFrame("Torso", Vector3.Zero, Vector3.Zero, Vector3.One);
            var part2Frame = CreateFrame("Head", new Vector3(0f, 1.5f, 0f), Vector3.Zero, Vector3.One);
            root.AddChild(part1Frame);
            root.AddChild(part2Frame);

            // Build 90 triangles (270 vertices per part -> > 1024 bytes for vertices/normals/uvs so zlib compression activates!)
            var mesh1 = BuildGridSkinnedMesh(part1Frame.Path, "MatTorso", 90, pelvis.Path, chest.Path, unusedBone.Path, injectNonFiniteOnFirstUv: true);
            var mesh2 = BuildGridSkinnedMesh(part2Frame.Path, "MatHead", 90, chest.Path, pelvis.Path, unusedBone.Path, injectNonFiniteOnFirstUv: false);

            var texBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            using var texMs = new MemoryStream(texBytes);
            var importedTex = new ImportedTexture(texMs, "hero_diffuse.png");

            var morph1 = new ImportedMorph
            {
                Path = part1Frame.Path,
                Channels = new List<ImportedMorphChannel>
                {
                    new()
                    {
                        Name = "Breathe",
                        KeyframeList = new List<ImportedMorphKeyframe>
                        {
                            new()
                            {
                                Weight = 100f,
                                hasNormals = true,
                                VertexList = new List<ImportedMorphVertex>
                                {
                                    new()
                                    {
                                        Index = 0,
                                        Vertex = new ImportedVertex
                                        {
                                            Vertex = new Vector3(0f, 0.1f, 0.2f),
                                            Normal = new Vector3(0f, 1f, 0f)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var anim = new ImportedKeyframedAnimation
            {
                Name = "Idle",
                SampleRate = 30f,
                TrackList = new List<ImportedAnimationKeyframedTrack>
                {
                    new()
                    {
                        Path = pelvis.Path,
                        Translations = new List<ImportedKeyframe<Vector3>>
                        {
                            new(0f, new Vector3(0f, 1f, 0f)),
                            new(0.5f, new Vector3(0f, 1.05f, 0f)),
                            new(1.0f, new Vector3(0f, 1f, 0f))
                        },
                        Rotations = new List<ImportedKeyframe<Vector3>>
                        {
                            new(0f, Vector3.Zero),
                            new(1.0f, new Vector3(0f, 15f, 0f))
                        },
                        Scalings = new List<ImportedKeyframe<Vector3>>
                        {
                            new(0f, Vector3.One),
                            new(1.0f, Vector3.One)
                        }
                    },
                    new()
                    {
                        Path = part1Frame.Path,
                        BlendShape = new ImportedBlendShape
                        {
                            ChannelName = "Breathe",
                            Keyframes = new List<ImportedKeyframe<float>>
                            {
                                new(0f, 0f),
                                new(0.5f, 100f),
                                new(1.0f, 0f)
                            }
                        }
                    }
                }
            };

            return new SyntheticImported
            {
                RootFrame = root,
                MeshList = new List<ImportedMesh> { mesh1, mesh2 },
                MaterialList = new List<ImportedMaterial>
                {
                    new()
                    {
                        Name = "MatTorso",
                        Diffuse = new Color(0.8f, 0.2f, 0.2f, 1f),
                        Ambient = new Color(0.1f, 0.1f, 0.1f, 1f),
                        Specular = new Color(0.2f, 0.2f, 0.2f, 1f),
                        Emissive = new Color(0f, 0f, 0f, 1f),
                        Shininess = 32f,
                        Textures = new List<ImportedMaterialTexture>
                        {
                            new() { Name = "hero_diffuse.png", Dest = 0 }
                        }
                    },
                    new()
                    {
                        Name = "MatHead",
                        Diffuse = new Color(0.2f, 0.8f, 0.2f, 1f),
                        Ambient = new Color(0.1f, 0.1f, 0.1f, 1f),
                        Specular = new Color(0.2f, 0.2f, 0.2f, 1f),
                        Emissive = new Color(0f, 0f, 0f, 1f),
                        Shininess = 16f,
                        Textures = new List<ImportedMaterialTexture>()
                    }
                },
                TextureList = new List<ImportedTexture> { importedTex },
                MorphList = new List<ImportedMorph> { morph1 },
                AnimationList = new List<ImportedKeyframedAnimation> { anim }
            };
        }

        private static ImportedMesh BuildGridSkinnedMesh(
            string path,
            string materialName,
            int triangleCount,
            string bone0Path,
            string bone1Path,
            string unusedBonePath,
            bool injectNonFiniteOnFirstUv)
        {
            var vertices = new List<ImportedVertex>(triangleCount * 3);
            var faces = new List<ImportedFace>(triangleCount);

            for (int t = 0; t < triangleCount; t++)
            {
                int baseIdx = t * 3;
                for (int k = 0; k < 3; k++)
                {
                    var uv = new float[8][];
                    uv[0] = (injectNonFiniteOnFirstUv && t == 0 && k == 0)
                        ? new[] { float.NaN, float.PositiveInfinity }
                        : new[] { (t % 10) * 0.1f, k * 0.33f };
                    vertices.Add(new ImportedVertex
                    {
                        Vertex = new Vector3(t * 0.1f + (k == 1 ? 0.05f : 0f), k == 2 ? 0.05f : 0f, 0f),
                        Normal = new Vector3(0f, 0f, 1f),
                        Tangent = new Vector4(1f, 0f, 0f, 1f),
                        Color = new Color(1f, 1f, 1f, 1f),
                        UV = uv,
                        BoneIndices = new[] { 0, 1, 0, 0 },
                        Weights = new[] { 0.75f, 0.25f, 0f, 0f }
                    });
                }
                faces.Add(new ImportedFace { VertexIndices = new[] { baseIdx, baseIdx + 1, baseIdx + 2 } });
            }

            return new ImportedMesh
            {
                Path = path,
                hasNormal = true,
                hasUV = new[] { true, false, false, false, false, false, false, false },
                hasTangent = true,
                hasColor = true,
                VertexList = vertices,
                SubmeshList = new List<ImportedSubmesh>
                {
                    new() { Material = materialName, BaseVertex = 0, FaceList = faces }
                },
                BoneList = new List<ImportedBone>
                {
                    new() { Path = bone0Path, Matrix = MakeIdentityBindPose() },
                    new() { Path = bone1Path, Matrix = MakeTranslationBindPose(0f, -1f, 0f) },
                    new() { Path = unusedBonePath, Matrix = MakeIdentityBindPose() }
                }
            };
        }

        private sealed class BinaryFbxAuditResult
        {
            public int Version { get; set; }
            public int CompressedArrayCount { get; set; }
            public int NonFiniteFloatOrDoubleCount { get; set; }
            public int MeshGeometryCount { get; set; }
            public int TotalMeshVertexDoubleCount { get; set; }
            public int BlendShapeDeformerCount { get; set; }
            public int SkinClusterCount { get; set; }
            public int AnimationStackCount { get; set; }
            public int MaterialCount { get; set; }
        }

        private static BinaryFbxAuditResult AuditBinaryFbx7400(string path)
        {
            var result = new BinaryFbxAuditResult();
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8);

            var magic = Encoding.ASCII.GetString(br.ReadBytes(23));
            Assert.Equal("Kaydara FBX Binary  \0\u001a\0", magic);
            result.Version = br.ReadInt32();

            while (fs.Position < fs.Length)
            {
                if (!ReadFbxNode(br, result, parentIsMeshGeometry: false))
                {
                    break;
                }
            }

            return result;
        }

        private static bool ReadFbxNode(BinaryReader br, BinaryFbxAuditResult audit, bool parentIsMeshGeometry)
        {
            uint endOffset = br.ReadUInt32();
            uint numProperties = br.ReadUInt32();
            uint propertyListLen = br.ReadUInt32();
            byte nameLen = br.ReadByte();

            if (endOffset == 0 && numProperties == 0 && propertyListLen == 0 && nameLen == 0)
            {
                return false;
            }

            string nodeName = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var stringProps = new List<string>();
            int? arrayLength = null;

            long propsStart = br.BaseStream.Position;
            for (uint p = 0; p < numProperties; p++)
            {
                byte typeCode = br.ReadByte();
                switch ((char)typeCode)
                {
                    case 'C':
                        br.ReadByte();
                        break;
                    case 'Y':
                        br.ReadInt16();
                        break;
                    case 'I':
                        br.ReadInt32();
                        break;
                    case 'L':
                        br.ReadInt64();
                        break;
                    case 'F':
                    {
                        float fv = br.ReadSingle();
                        if (float.IsNaN(fv) || float.IsInfinity(fv)) audit.NonFiniteFloatOrDoubleCount++;
                        break;
                    }
                    case 'D':
                    {
                        double dv = br.ReadDouble();
                        if (double.IsNaN(dv) || double.IsInfinity(dv)) audit.NonFiniteFloatOrDoubleCount++;
                        break;
                    }
                    case 'S':
                    {
                        int len = br.ReadInt32();
                        stringProps.Add(Encoding.UTF8.GetString(br.ReadBytes(len)));
                        break;
                    }
                    case 'R':
                    {
                        int len = br.ReadInt32();
                        br.ReadBytes(len);
                        break;
                    }
                    case 'd':
                    case 'f':
                    case 'i':
                    case 'l':
                    case 'b':
                    {
                        int elemCount = br.ReadInt32();
                        int encoding = br.ReadInt32();
                        int compLen = br.ReadInt32();
                        byte[] payload = br.ReadBytes(compLen);
                        arrayLength = elemCount;

                        if (encoding == 1)
                        {
                            audit.CompressedArrayCount++;
                            using var compMs = new MemoryStream(payload);
                            using var zlib = new ZLibStream(compMs, CompressionMode.Decompress);
                            using var outMs = new MemoryStream();
                            zlib.CopyTo(outMs);
                            payload = outMs.ToArray();
                        }

                        if ((char)typeCode == 'd')
                        {
                            Assert.Equal(elemCount * sizeof(double), payload.Length);
                            for (int i = 0; i < elemCount; i++)
                            {
                                double v = BitConverter.ToDouble(payload, i * sizeof(double));
                                if (double.IsNaN(v) || double.IsInfinity(v)) audit.NonFiniteFloatOrDoubleCount++;
                            }
                        }
                        else if ((char)typeCode == 'f')
                        {
                            Assert.Equal(elemCount * sizeof(float), payload.Length);
                            for (int i = 0; i < elemCount; i++)
                            {
                                float v = BitConverter.ToSingle(payload, i * sizeof(float));
                                if (float.IsNaN(v) || float.IsInfinity(v)) audit.NonFiniteFloatOrDoubleCount++;
                            }
                        }
                        break;
                    }
                    default:
                        throw new InvalidDataException($"Unexpected FBX property type code '{(char)typeCode}' in node '{nodeName}'.");
                }
            }

            Assert.Equal(propertyListLen, (uint)(br.BaseStream.Position - propsStart));

            bool isMeshGeometry = nodeName == "Geometry" && stringProps.Contains("Mesh");
            if (isMeshGeometry) audit.MeshGeometryCount++;
            if (parentIsMeshGeometry && nodeName == "Vertices" && arrayLength.HasValue) audit.TotalMeshVertexDoubleCount += arrayLength.Value;
            if (nodeName == "Deformer" && stringProps.Contains("BlendShape")) audit.BlendShapeDeformerCount++;
            if (nodeName == "Deformer" && stringProps.Contains("Cluster")) audit.SkinClusterCount++;
            if (nodeName == "AnimationStack") audit.AnimationStackCount++;
            if (nodeName == "Material") audit.MaterialCount++;

            if (br.BaseStream.Position < endOffset)
            {
                while (br.BaseStream.Position < endOffset)
                {
                    if (!ReadFbxNode(br, audit, isMeshGeometry))
                    {
                        break;
                    }
                }
            }

            Assert.Equal(endOffset, (uint)br.BaseStream.Position);
            return true;
        }

        private static void AssertVector3Near(Vector3 expected, Vector3 actual, float tolerance)
        {
            Assert.True(Math.Abs(expected.X - actual.X) <= tolerance, $"X mismatch: expected {expected.X}, got {actual.X}");
            Assert.True(Math.Abs(expected.Y - actual.Y) <= tolerance, $"Y mismatch: expected {expected.Y}, got {actual.Y}");
            Assert.True(Math.Abs(expected.Z - actual.Z) <= tolerance, $"Z mismatch: expected {expected.Z}, got {actual.Z}");
        }
    }
}
