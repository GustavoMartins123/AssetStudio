using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AssetStudio;
using AssetStudio.Avalonia;
using OpenTK.Mathematics;
using SkiaSharp;
using Xunit;
using Vector2 = AssetStudio.Vector2;

namespace AssetStudio.Tests
{
    public class MeshPreviewMaterialsAndAlphaTests
    {
        [Fact]
        public void Mesh_HasDeclaredVertexData_DistinguishesEmptyPlaceholderStubFromStreamedAndCompressedMeshes()
        {
            // 1. 0-vertex placeholder stub mesh (e.g. *_LODMeshSet container stub)
            var stubMesh = CreateUninitialized<Mesh>();
            stubMesh.m_VertexCount = 0;
            stubMesh.m_VertexData = new VertexData { m_VertexCount = 0 };
            stubMesh.m_StreamData = new StreamingInfo { offset = 0, size = 0, path = string.Empty };
            stubMesh.m_CompressedMesh = CreateCompressedMesh(numVerticesItems: 0);

            Assert.False(stubMesh.HasDeclaredVertexData);
            Assert.True(MainWindow.TryGetEmptyMeshPreviewDiagnostic(stubMesh, out var stubIsError, out var stubMsg));
            Assert.False(stubIsError);
            Assert.Equal(MainWindow.EmptyMeshPlaceholderMessage, stubMsg);

            // 2. Mesh with declared m_VertexCount > 0 (missing .resS stream decompression failure)
            var declaredMesh = CreateUninitialized<Mesh>();
            declaredMesh.m_VertexCount = 661;
            Assert.True(declaredMesh.HasDeclaredVertexData);
            Assert.True(MainWindow.TryGetEmptyMeshPreviewDiagnostic(declaredMesh, out var declaredIsError, out var declaredMsg));
            Assert.True(declaredIsError);
            Assert.Equal(MainWindow.MissingMeshStreamErrorMessage, declaredMsg);

            // 3. Mesh with external m_StreamData referenced
            var streamedMesh = CreateUninitialized<Mesh>();
            streamedMesh.m_VertexCount = 0;
            streamedMesh.m_StreamData = new StreamingInfo
            {
                offset = 4096,
                size = 32768,
                path = "archive:/CAB-0123456789abcdef0123456789abcdef/CAB-0123456789abcdef0123456789abcdef.resS"
            };
            Assert.True(streamedMesh.HasDeclaredVertexData);
            Assert.True(MainWindow.TryGetEmptyMeshPreviewDiagnostic(streamedMesh, out var streamedIsError, out _));
            Assert.True(streamedIsError);

            // 4. Mesh with compressed vertex data
            var compressedMesh = CreateUninitialized<Mesh>();
            compressedMesh.m_VertexCount = 0;
            compressedMesh.m_CompressedMesh = CreateCompressedMesh(numVerticesItems: 300);
            Assert.True(compressedMesh.HasDeclaredVertexData);

            // 5. Mesh with populated vertices does not trigger empty diagnostic
            var validMesh = CreateUninitialized<Mesh>();
            validMesh.m_VertexCount = 1;
            validMesh.m_Vertices = new float[] { 0f, 0f, 0f };
            Assert.True(validMesh.HasDeclaredVertexData);
            Assert.False(MainWindow.TryGetEmptyMeshPreviewDiagnostic(validMesh, out var validIsError, out _));
            Assert.False(validIsError);
        }

        [Fact]
        public void MaterialPreviewBuilder_SelectsBaseEyeIrisMap_OverBaseEyeHighlightMapAndMatcapMap()
        {
            var highlightTex = CreateNamedTexture("Hero_Eye_hi_alb", 101);
            var irisTex = CreateNamedTexture("Hero_Eye_ir_alb", 102);
            var pupilTex = CreateNamedTexture("Hero_Eye_pu_alb", 103);
            var whiteTex = CreateNamedTexture("Hero_Eye_wh_alb", 104);
            var matcapTex = CreateNamedTexture("matcap_eye_alb", 105);

            // Alphabetical order in m_TexEnvs places _BaseEyeHighlightMap at index 0 and _BaseMap = null
            var material = CreateMaterialWithTexEnvs(
                "Hero_Eye_L",
                ("_BaseEyeHighlightMap", 101),
                ("_BaseEyeHighlightMapBackup", 0),
                ("_BaseEyeIrisMap", 102),
                ("_BaseEyePupilMap", 103),
                ("_BaseEyePupilMapBackup", 0),
                ("_BaseEyeWhiteMap", 104),
                ("_BaseMap", 0),
                ("_MatcapMap", 105));

            var slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_BaseEyeHighlightMap"] = highlightTex,
                ["_BaseEyeHighlightMapBackup"] = null,
                ["_BaseEyeIrisMap"] = irisTex,
                ["_BaseEyePupilMap"] = pupilTex,
                ["_BaseEyePupilMapBackup"] = null,
                ["_BaseEyeWhiteMap"] = whiteTex,
                ["_BaseMap"] = null,
                ["_MatcapMap"] = matcapTex
            };

            Assert.Equal("_BaseEyeIrisMap", MaterialPreviewBuilder.SelectMainTextureSlotName(material));
            Assert.Same(irisTex, MaterialPreviewBuilder.SelectMainTexture(material, slots));

            var builder = new MaterialPreviewBuilder(
                new AssetsManager(),
                m => m,
                (_, key) => slots.TryGetValue(key, out var t) ? t : null,
                _ => null);
            var previewData = builder.Build(material);
            Assert.Same(irisTex, previewData.PreviewTexture);
        }

        [Fact]
        public void MaterialPreviewBuilder_SelectsMainTextureAndFaceTex_OverMaskAndNonDiffuseSlots()
        {
            var maskTex = CreateNamedTexture("Stage_Mask", 201);
            var scrollTex = CreateNamedTexture("Stage_Scroll", 202);
            var mainTexture = CreateNamedTexture("Stage_Albedo", 203);

            var stageMaterial = CreateMaterialWithTexEnvs(
                "Stage_Mat",
                ("_MaskTexture", 201),
                ("_MainTexture", 203),
                ("_UVScrollTexture1", 202));

            var stageSlots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_MaskTexture"] = maskTex,
                ["_MainTexture"] = mainTexture,
                ["_UVScrollTexture1"] = scrollTex
            };

            Assert.Equal("_MainTexture", MaterialPreviewBuilder.SelectMainTextureSlotName(stageMaterial));
            Assert.Same(mainTexture, MaterialPreviewBuilder.SelectMainTexture(stageMaterial, stageSlots));

            // Face material with _FaceTex and non-diffuse slots
            var bumpTex = CreateNamedTexture("Face_Normal", 301);
            var weatheringMask = CreateNamedTexture("Face_Weathering", 302);
            var faceTex = CreateNamedTexture("Face_Diffuse", 303);

            var faceMaterial = CreateMaterialWithTexEnvs(
                "Hero_Face",
                ("_BumpMap", 301),
                ("_WeatheringMask", 302),
                ("_FaceTex", 303));

            var faceSlots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_BumpMap"] = bumpTex,
                ["_WeatheringMask"] = weatheringMask,
                ["_FaceTex"] = faceTex
            };

            Assert.Equal("_FaceTex", MaterialPreviewBuilder.SelectMainTextureSlotName(faceMaterial));
            Assert.Same(faceTex, MaterialPreviewBuilder.SelectMainTexture(faceMaterial, faceSlots));

            // Fallback slot picking skips NonDiffuseTextureSlots when picking a custom diffuse slot
            var customDiffuse = CreateNamedTexture("Custom_Albedo", 402);
            var customMaterial = CreateMaterialWithTexEnvs(
                "Custom_Mat",
                ("_BaseEyeHighlightMap", 101),
                ("_MaskTexture", 201),
                ("_CustomBodySurface", 402));
            var customSlots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_BaseEyeHighlightMap"] = highlightTexOrDummy(201),
                ["_MaskTexture"] = maskTex,
                ["_CustomBodySurface"] = customDiffuse
            };

            Assert.Equal("_CustomBodySurface", MaterialPreviewBuilder.SelectMainTextureSlotName(customMaterial));
            Assert.Same(customDiffuse, MaterialPreviewBuilder.SelectMainTexture(customMaterial, customSlots));
        }

        [Fact]
        public void ConvertPreviewBitmapToRgbaBytes_ForcesOpaqueAlphaForOpaqueMaterials_AndPreservesAlphaForTransparentDecals()
        {
            using var bitmap = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            // Raw BGRA bytes as produced by Texture2D decoder:
            // Pixel 0: transparent red background in decal texture (B=0, G=0, R=255, A=0)
            // Pixel 1: semi-transparent dark red decal streak (B=20, G=10, R=200, A=96)
            var rawBgra = new byte[] { 0, 0, 255, 0, 20, 10, 200, 96 };
            Marshal.Copy(rawBgra, 0, bitmap.GetPixels(), rawBgra.Length);

            // 1. Opaque material (e.g. eye iris texture with mask in alpha) -> preserveAlpha = false -> forces alpha = 255
            var opaqueMat = CreateMaterialWithTexEnvs("Hero_Eye_L");
            opaqueMat.m_CustomRenderQueue = 2000;
            opaqueMat.stringTagMap = new[]
            {
                new KeyValuePair<string, string>("RenderType", "Opaque")
            };
            Assert.False(MainWindow.ShouldPreserveMaterialTextureAlpha(opaqueMat, "Custom/CharaEyeShader"));

            // 1b. Character AlphaTest / Cutout / *_BLEND material (diffuse alpha stores specular/rim mask, NOT glass translucency) -> must stay opaque
            var cutoutMat = CreateMaterialWithTexEnvs("Hero_Body_BK_BLEND");
            cutoutMat.m_CustomRenderQueue = 2450;
            cutoutMat.m_ValidKeywords = new[] { "_ALPHATEST_ON" };
            cutoutMat.stringTagMap = new[]
            {
                new KeyValuePair<string, string>("RenderType", "TransparentCutout")
            };
            cutoutMat.m_SavedProperties.m_Floats = new[]
            {
                new KeyValuePair<string, float>("_AlphaClip", 1f)
            };
            cutoutMat.m_SavedProperties.m_Colors = new[]
            {
                new KeyValuePair<string, Color>("_BaseColor", new Color(1f, 1f, 1f, 0.5f))
            };
            Assert.False(MainWindow.ShouldPreserveMaterialTextureAlpha(cutoutMat, "Custom/CharaShader"));

            var opaqueRgba = MainWindow.ConvertPreviewBitmapToRgbaBytes(bitmap, out var ow, out var oh, preserveAlpha: false);
            Assert.NotNull(opaqueRgba);
            Assert.Equal(2, ow);
            Assert.Equal(1, oh);
            Assert.Equal(new byte[] { 255, 0, 0, 255, 200, 10, 20, 255 }, opaqueRgba);
            Assert.False(MeshPreviewControl.HasTransparentPixels(opaqueRgba));

            // 2. Transparent decal material -> preserveAlpha = true -> keeps original alpha (0 and 96)
            var decalMat = CreateMaterialWithTexEnvs("DefaultCharaDecal");
            decalMat.m_CustomRenderQueue = 3000;
            decalMat.stringTagMap = new[]
            {
                new KeyValuePair<string, string>("RenderType", "Transparent")
            };
            decalMat.m_SavedProperties.m_Floats = new[]
            {
                new KeyValuePair<string, float>("_ZWrite", 0f)
            };
            Assert.True(MainWindow.ShouldPreserveMaterialTextureAlpha(decalMat, "Custom/CharaDecalShader"));

            var decalRgba = MainWindow.ConvertPreviewBitmapToRgbaBytes(bitmap, out var dw, out var dh, preserveAlpha: true);
            Assert.NotNull(decalRgba);
            Assert.Equal(2, dw);
            Assert.Equal(1, dh);
            Assert.Equal(new byte[] { 255, 0, 0, 0, 200, 10, 20, 96 }, decalRgba);
            Assert.True(MeshPreviewControl.HasTransparentPixels(decalRgba));

            // 3. All-zero alpha fallback when preserveAlpha = true (maxAlpha == 0 -> falls back to 255)
            using var zeroAlphaBitmap = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var rawZeroAlphaBgra = new byte[] { 30, 20, 10, 0, 60, 50, 40, 0 };
            Marshal.Copy(rawZeroAlphaBgra, 0, zeroAlphaBitmap.GetPixels(), rawZeroAlphaBgra.Length);

            var fallbackRgba = MainWindow.ConvertPreviewBitmapToRgbaBytes(zeroAlphaBitmap, out _, out _, preserveAlpha: true);
            Assert.NotNull(fallbackRgba);
            Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, fallbackRgba);

            // 4. Multi-layer eye compositing: blends pupil + highlight over opaque iris+sclera without breaking alpha=255
            var eyeBaseRgba = new byte[] { 200, 150, 100, 255, 236, 218, 214, 255 }; // pixel 0 = iris, pixel 1 = sclera
            var pupilOverlayBgra = new byte[] { 20, 40, 60, 255, 100, 100, 100, 0 }; // pixel 0 = opaque dark pupil, pixel 1 = transparent
            MainWindow.CompositeBgraOverlayOntoRgbaInPlace(eyeBaseRgba, 2, 1, pupilOverlayBgra, 2, 1);
            Assert.Equal(new byte[] { 60, 40, 20, 255, 236, 218, 214, 255 }, eyeBaseRgba);
        }

        [Fact]
        public void MultiLayerEyeCompositing_CompositesIrisOverSclera_EliminatingDarkAndPremultipliedBlackBorderPixels()
        {
            // Case 1: Unpremultiplied iris texture whose alpha=0 outer sclera region contains dark RGB (31, 31, 51, 0)
            // Pixel 0: outer sclera region with dark hidden RGB and alpha=0
            // Pixel 1: semi-transparent iris rim (100, 160, 220, 128)
            // Pixel 2: opaque iris center (80, 140, 240, 255)
            using var darkBgIrisBitmap = new SKBitmap(3, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var darkBgIrisBgra = new byte[]
            {
                51, 31, 31, 0,
                220, 160, 100, 128,
                240, 140, 80, 255
            };
            Marshal.Copy(darkBgIrisBgra, 0, darkBgIrisBitmap.GetPixels(), darkBgIrisBgra.Length);

            var irisRgba = MainWindow.ConvertPreviewBitmapToRgbaBytes(darkBgIrisBitmap, out var iw, out var ih, preserveAlpha: true);
            Assert.NotNull(irisRgba);
            Assert.Equal(3, iw);
            Assert.Equal(1, ih);

            // 1x1 off-white sclera texture (BGRA: B=198, G=207, R=231, A=255)
            var scleraBgra = new byte[] { 198, 207, 231, 255 };
            MainWindow.CompositeEyeIrisOverScleraInPlace(irisRgba!, iw, ih, scleraBgra, 1, 1);

            // Pixel 0 must take sclera color (231, 207, 198, 255) instead of dark (31, 31, 51, 255)
            Assert.Equal(231, irisRgba![0]);
            Assert.Equal(207, irisRgba[1]);
            Assert.Equal(198, irisRgba[2]);
            Assert.Equal(255, irisRgba[3]);

            // Pixel 1 must blend iris rim over sclera with alpha=255
            Assert.InRange(irisRgba[4], 160, 170);
            Assert.InRange(irisRgba[5], 180, 190);
            Assert.InRange(irisRgba[6], 205, 215);
            Assert.Equal(255, irisRgba[7]);

            // Pixel 2 must preserve opaque iris center (80, 140, 240, 255)
            Assert.Equal(80, irisRgba[8]);
            Assert.Equal(140, irisRgba[9]);
            Assert.Equal(240, irisRgba[10]);
            Assert.Equal(255, irisRgba[11]);
            Assert.False(MeshPreviewControl.HasTransparentPixels(irisRgba));

            // Case 2: PNG-cached SKAlphaType.Premul round-trip (alpha=0 pixels have RGB zeroed to (0,0,0,0),
            // and semi-transparent pixels have RGB premultiplied by alpha/255)
            using var pngData = SKImage.FromBitmap(darkBgIrisBitmap).Encode(SKEncodedImageFormat.Png, 90);
            using var decodedPremulBitmap = SKBitmap.Decode(pngData);
            Assert.NotNull(decodedPremulBitmap);
            Assert.Equal(SKAlphaType.Premul, decodedPremulBitmap!.AlphaType);

            var cachedIrisRgba = MainWindow.ConvertPreviewBitmapToRgbaBytes(decodedPremulBitmap, out var cw, out var ch, preserveAlpha: true);
            Assert.NotNull(cachedIrisRgba);
            // Unpremultiplying restores straight RGB on semi-transparent pixel 1 instead of darkening it
            Assert.InRange(cachedIrisRgba![4], 96, 104);
            Assert.InRange(cachedIrisRgba[5], 156, 164);
            Assert.InRange(cachedIrisRgba[6], 216, 224);
            Assert.Equal(128, cachedIrisRgba[7]);

            // Compositing over fallback sclera (null scleraBgra) removes black (0,0,0) outer pixels cleanly
            MainWindow.CompositeEyeIrisOverScleraInPlace(cachedIrisRgba, cw, ch);
            Assert.Equal(236, cachedIrisRgba[0]);
            Assert.Equal(218, cachedIrisRgba[1]);
            Assert.Equal(214, cachedIrisRgba[2]);
            Assert.Equal(255, cachedIrisRgba[3]);

            // Premultiplied highlight overlay must not double-multiply alpha
            var premulHighlightBgra = new byte[]
            {
                0, 0, 0, 0,
                128, 128, 128, 128, // premultiplied 50% white -> straight (255,255,255,128)
                255, 255, 255, 255
            };
            MainWindow.CompositeBgraOverlayOntoRgbaInPlace(cachedIrisRgba, cw, ch, premulHighlightBgra, 3, 1, overlayIsPremul: true);
            // Pixel 1 should brighten toward white (~210+), not darken toward ~140
            Assert.True(cachedIrisRgba[4] >= 200);
            Assert.True(cachedIrisRgba[5] >= 210);
            Assert.True(cachedIrisRgba[6] >= 225);
            // Pixel 2 becomes pure specular white
            Assert.Equal(255, cachedIrisRgba[8]);
            Assert.Equal(255, cachedIrisRgba[9]);
            Assert.Equal(255, cachedIrisRgba[10]);
        }

        [Fact]
        public void CharacterModelGroupStemAndSiblingEyeFallback_SupplementsMissingExternalEyeMeshesWithoutGraftingOutfitParts()
        {
            Assert.Equal("P_C01_ALPHA", MainWindow.ExtractCharacterModelGroupBaseStem("P_C01_ALPHA_06_fbx"));
            Assert.Equal("P_C01_ALPHA", MainWindow.ExtractCharacterModelGroupBaseStem("P_C01_ALPHA_fbx"));
            Assert.Equal("P_C01_ALPHA", MainWindow.ExtractCharacterModelGroupBaseStem("P_C01_ALPHA_06_Combined"));
            Assert.Equal("P_C02_HERO", MainWindow.ExtractCharacterModelGroupBaseStem("P_C02_HERO_00_B_fbx"));
            Assert.Equal("P_C02_HERO", MainWindow.ExtractCharacterModelGroupBaseStem("P_C02_HERO_00_C_fbx"));
            Assert.Equal("P_C02_HEROINE", MainWindow.ExtractCharacterModelGroupBaseStem("P_C02_HEROINE_03_fbx"));
            Assert.Equal("P_C03_NPC_A", MainWindow.ExtractCharacterModelGroupBaseStem("P_C03_NPC_A_01_fbx"));
            Assert.Equal("P_C03_NPC_B", MainWindow.ExtractCharacterModelGroupBaseStem("P_C03_NPC_B_05_fbx"));
            Assert.NotEqual(
                MainWindow.ExtractCharacterModelGroupBaseStem("P_C02_HERO_06_fbx"),
                MainWindow.ExtractCharacterModelGroupBaseStem("P_C02_HEROINE_06_fbx"));

            Assert.True(MainWindow.IsEyeOrEyelashPartName("M_L_Eye"));
            Assert.True(MainWindow.IsEyeOrEyelashPartName("M_R_Eye"));
            Assert.True(MainWindow.IsEyeOrEyelashPartName("P_Eyelashes_L"));
            Assert.False(MainWindow.IsEyeOrEyelashPartName("M_CoatBK"));
            Assert.False(MainWindow.IsEyeOrEyelashPartName("combined"));

            var variantGroupId = "modelgroup:animator:CAB-variant#1";
            var variantBodyPart = new ModelGroupMeshInfo(
                variantGroupId,
                "CAB-variant#100",
                "CAB-variant#200",
                "SkinnedMeshRenderer",
                "CAB-variant#300",
                "combined",
                0,
                100,
                "Animator descendant",
                null,
                "P_C01_ALPHA_06_Combined",
                100,
                1_200_000,
                (int)ClassIDType.Mesh);

            var siblingParts = new List<ModelGroupMeshInfo>
            {
                new ModelGroupMeshInfo(
                    "modelgroup:animator:CAB-base#1",
                    "CAB-base#501",
                    "CAB-base#601",
                    "SkinnedMeshRenderer",
                    "CAB-base#701",
                    "M_L_Eye",
                    1,
                    100,
                    "Animator descendant",
                    null,
                    "M_L_Eye",
                    501,
                    8_000,
                    (int)ClassIDType.Mesh),
                new ModelGroupMeshInfo(
                    "modelgroup:animator:CAB-base#1",
                    "CAB-base#502",
                    "CAB-base#602",
                    "SkinnedMeshRenderer",
                    "CAB-base#702",
                    "M_R_Eye",
                    2,
                    100,
                    "Animator descendant",
                    null,
                    "M_R_Eye",
                    502,
                    8_000,
                    (int)ClassIDType.Mesh),
                new ModelGroupMeshInfo(
                    "modelgroup:animator:CAB-base#1",
                    "CAB-base#503",
                    "CAB-base#603",
                    "SkinnedMeshRenderer",
                    "CAB-base#703",
                    "M_CoatBK",
                    3,
                    100,
                    "Animator descendant",
                    null,
                    "M_CoatBK",
                    503,
                    50_000,
                    (int)ClassIDType.Mesh)
            };

            var unresolvedLocalRenderers = new List<(string GameObjectName, string GameObjectAssetId, string RendererAssetId, string RendererType)>
            {
                ("M_L_Eye", "CAB-variant#801", "CAB-variant#901", "SkinnedMeshRenderer"),
                ("M_R_Eye", "CAB-variant#802", "CAB-variant#902", "SkinnedMeshRenderer"),
                ("M_CoatBK", "CAB-variant#803", "CAB-variant#903", "SkinnedMeshRenderer")
            };

            var supplemented = MainWindow.SupplementMissingEyePartsFromSiblingParts(
                variantGroupId,
                new[] { variantBodyPart },
                unresolvedLocalRenderers,
                siblingParts);

            // Must add M_L_Eye and M_R_Eye using sibling MeshAssetIds while keeping variant's RendererAssetIds,
            // and must NEVER graft non-eye outfit mesh M_CoatBK onto the variant.
            Assert.Equal(3, supplemented.Count);
            Assert.Equal("CAB-variant#100", supplemented[0].MeshAssetId);
            Assert.Equal("M_L_Eye", supplemented[1].GameObjectName);
            Assert.Equal("CAB-base#501", supplemented[1].MeshAssetId);
            Assert.Equal("CAB-variant#901", supplemented[1].RendererAssetId);
            Assert.Equal("M_R_Eye", supplemented[2].GameObjectName);
            Assert.Equal("CAB-base#502", supplemented[2].MeshAssetId);
            Assert.Equal("CAB-variant#902", supplemented[2].RendererAssetId);
            Assert.DoesNotContain(supplemented, p => string.Equals(p.GameObjectName, "M_CoatBK", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void SelectRepresentativeModelGroupMesh_PrefersMainBodyMeshOverLargerDecalMesh_AndPartitionsDrawOrder()
        {
            var decalPartFirst = new ModelGroupMeshInfo(
                groupId: "modelgroup:animator:monster",
                meshAssetId: "bundle#decal",
                rendererAssetId: "bundle#smr_decal",
                rendererType: "SkinnedMeshRenderer",
                gameObjectAssetId: "bundle#go_decal",
                gameObjectName: "combined_Decal",
                slotIndex: 0,
                confidence: 100,
                confidenceReason: "Animator descendant",
                transformMatrix: null,
                meshName: "Monster_Green_Combined_DecalMesh",
                meshPathId: 200,
                meshByteSize: 1_823_336,
                meshType: (int)ClassIDType.Mesh);

            var bodyPartSecond = new ModelGroupMeshInfo(
                groupId: "modelgroup:animator:monster",
                meshAssetId: "bundle#body",
                rendererAssetId: "bundle#smr_body",
                rendererType: "SkinnedMeshRenderer",
                gameObjectAssetId: "bundle#go_body",
                gameObjectName: "combined",
                slotIndex: 1,
                confidence: 100,
                confidenceReason: "Animator descendant",
                transformMatrix: null,
                meshName: "Monster_Green_Combined",
                meshPathId: 100,
                meshByteSize: 1_243_752,
                meshType: (int)ClassIDType.Mesh);

            // Even when *_DecalMesh is larger in byte size and at SlotIndex 0, representative mesh must be main body mesh
            var rep1 = MainWindow.SelectRepresentativeModelGroupMesh(new[] { decalPartFirst, bodyPartSecond });
            Assert.NotNull(rep1);
            Assert.Equal("Monster_Green_Combined", rep1!.MeshName);

            var rep2 = MainWindow.SelectRepresentativeModelGroupMesh(new[] { bodyPartSecond, decalPartFirst });
            Assert.NotNull(rep2);
            Assert.Equal("Monster_Green_Combined", rep2!.MeshName);

            // Two-pass submesh draw order puts opaque body first and transparent decal last regardless of slot order
            var subMeshTextureMap = new[] { 0, 1 };
            var textureHasAlpha = new[] { true, false }; // submesh 0 = decal (has alpha), submesh 1 = body (opaque)
            MeshPreviewControl.PartitionSubMeshDrawOrder(
                2,
                idx => MeshPreviewControl.IsSubMeshTransparent(idx, subMeshTextureMap, textureHasAlpha, global::OpenTK.Mathematics.Vector4.One),
                out var opaqueOrder,
                out var transparentOrder);

            Assert.Equal(new[] { 1 }, opaqueOrder);
            Assert.Equal(new[] { 0 }, transparentOrder);
        }

        private static Texture2D CreateNamedTexture(string name, long pathId)
        {
            var tex = CreateUninitialized<Texture2D>();
            tex.m_Name = name;
            tex.m_PathID = pathId;
            tex.m_Width = 512;
            tex.m_Height = 512;
            tex.m_TextureFormat = TextureFormat.BC7;
            return tex;
        }

        private static Texture2D highlightTexOrDummy(long pathId) => CreateNamedTexture("Dummy_Highlight", pathId);

        private static Material CreateMaterialWithTexEnvs(string name, params (string Slot, long PathId)[] slots)
        {
            var mat = CreateUninitialized<Material>();
            mat.m_Name = name;
            mat.m_CustomRenderQueue = -1;
            mat.m_ValidKeywords = Array.Empty<string>();
            mat.stringTagMap = Array.Empty<KeyValuePair<string, string>>();
            var texEnvs = new KeyValuePair<string, UnityTexEnv>[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                var ptr = new PPtr<Texture>(0, slots[i].PathId, null!);
                texEnvs[i] = new KeyValuePair<string, UnityTexEnv>(
                    slots[i].Slot,
                    new UnityTexEnv(ptr, new Vector2(1f, 1f), new Vector2(0f, 0f)));
            }

            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs,
                m_Ints = Array.Empty<KeyValuePair<string, int>>(),
                m_Floats = Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = Array.Empty<KeyValuePair<string, Color>>()
            };
            return mat;
        }

        private static CompressedMesh CreateCompressedMesh(int numVerticesItems)
        {
            var cm = CreateUninitialized<CompressedMesh>();
            var vertices = CreateUninitialized<PackedFloatVector>();
            vertices.m_NumItems = (uint)numVerticesItems;
            cm.m_Vertices = vertices;
            return cm;
        }

        private static T CreateUninitialized<T>() where T : class
        {
            return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        }
    }
}
