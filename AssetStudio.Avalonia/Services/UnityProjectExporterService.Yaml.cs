using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services.Scripting;

namespace AssetStudio.Avalonia.Services;

public sealed partial class UnityProjectExporterService
{
    // =========================================================================
    // Streaming Unity YAML 1.1 Scene (.unity) & Standalone Prefab (.prefab) Writers
    // =========================================================================

    private static void WriteUnitySceneFile(string scenePath, ExportState state, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(scenePath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        sw.WriteLine("%YAML 1.1");
        sw.WriteLine("%TAG !u! tag:unity3d.com,2011:");

        WriteSceneSettingsHeader(sw);

        for (int i = 0; i < state.SceneNodes.Count; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = state.SceneNodes[i];
            WriteGameObjectYaml(sw, node);

            for (int c = 0; c < node.Components.Count; c++)
            {
                WriteComponentYaml(sw, node, node.Components[c], state);
            }
        }
    }

    private static void WriteUnityPrefabFile(string prefabPath, ExportState state, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(prefabPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        sw.WriteLine("%YAML 1.1");
        sw.WriteLine("%TAG !u! tag:unity3d.com,2011:");

        for (int i = 0; i < state.SceneNodes.Count; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = state.SceneNodes[i];
            WriteGameObjectYaml(sw, node);

            for (int c = 0; c < node.Components.Count; c++)
            {
                WriteComponentYaml(sw, node, node.Components[c], state);
            }
        }
    }

    private static void WriteSceneSettingsHeader(StreamWriter sw)
    {
        sw.WriteLine("--- !u!29 &1");
        sw.WriteLine("OcclusionCullingSettings:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  serializedVersion: 2");
        sw.WriteLine("  m_OcclusionBakeSettings:");
        sw.WriteLine("    smallestOccluder: 5");
        sw.WriteLine("    smallestHole: 0.25");
        sw.WriteLine("    backfaceThreshold: 100");
        sw.WriteLine("  m_SceneGUID: 00000000000000000000000000000000");
        sw.WriteLine("  m_OcclusionCullingData: {fileID: 0}");

        sw.WriteLine("--- !u!104 &2");
        sw.WriteLine("RenderSettings:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  serializedVersion: 9");
        sw.WriteLine("  m_Fog: 0");
        sw.WriteLine("  m_FogColor: {r: 0.5, g: 0.5, b: 0.5, a: 1}");
        sw.WriteLine("  m_FogMode: 3");
        sw.WriteLine("  m_FogDensity: 0.01");
        sw.WriteLine("  m_LinearFogStart: 0");
        sw.WriteLine("  m_LinearFogEnd: 300");
        sw.WriteLine("  m_AmbientSkyColor: {r: 0.212, g: 0.227, b: 0.259, a: 1}");
        sw.WriteLine("  m_AmbientEquatorColor: {r: 0.114, g: 0.125, b: 0.133, a: 1}");
        sw.WriteLine("  m_AmbientGroundColor: {r: 0.047, g: 0.043, b: 0.035, a: 1}");
        sw.WriteLine("  m_AmbientIntensity: 1");
        sw.WriteLine("  m_AmbientMode: 0");
        sw.WriteLine("  m_SubtractiveShadowColor: {r: 0.42, g: 0.478, b: 0.627, a: 1}");
        sw.WriteLine("  m_SkyboxMaterial: {fileID: 0}");
        sw.WriteLine("  m_HaloStrength: 0.5");
        sw.WriteLine("  m_FlareStrength: 1");
        sw.WriteLine("  m_FlareFadeSpeed: 3");
        sw.WriteLine("  m_HaloTexture: {fileID: 0}");
        sw.WriteLine("  m_SpotCookie: {fileID: 0}");
        sw.WriteLine("  m_DefaultReflectionMode: 0");
        sw.WriteLine("  m_DefaultReflectionResolution: 128");
        sw.WriteLine("  m_ReflectionBounces: 1");
        sw.WriteLine("  m_ReflectionIntensity: 1");
        sw.WriteLine("  m_CustomReflection: {fileID: 0}");
        sw.WriteLine("  m_Sun: {fileID: 0}");

        sw.WriteLine("--- !u!157 &3");
        sw.WriteLine("LightmapSettings:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  serializedVersion: 12");
        sw.WriteLine("  m_GIWorkflowMode: 1");
        sw.WriteLine("  m_GISettings:");
        sw.WriteLine("    serializedVersion: 2");
        sw.WriteLine("    m_BounceScale: 1");
        sw.WriteLine("    m_IndirectOutputScale: 1");
        sw.WriteLine("    m_AlbedoBoost: 1");
        sw.WriteLine("    m_EnvironmentLightingMode: 0");
        sw.WriteLine("    m_EnableBakedLightmaps: 1");
        sw.WriteLine("    m_EnableRealtimeLightmaps: 0");
        sw.WriteLine("  m_LightmapEditorSettings:");
        sw.WriteLine("    serializedVersion: 12");
        sw.WriteLine("    m_Resolution: 2");
        sw.WriteLine("    m_BakeResolution: 40");
        sw.WriteLine("  m_LightingDataAsset: {fileID: 0}");
        sw.WriteLine("  m_LightingSettings: {fileID: 0}");

        sw.WriteLine("--- !u!196 &4");
        sw.WriteLine("NavMeshSettings:");
        sw.WriteLine("  serializedVersion: 2");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_BuildSettings:");
        sw.WriteLine("    serializedVersion: 3");
        sw.WriteLine("    agentTypeID: 0");
        sw.WriteLine("    agentRadius: 0.5");
        sw.WriteLine("    agentHeight: 2");
        sw.WriteLine("    agentSlope: 45");
        sw.WriteLine("    agentClimb: 0.4");
        sw.WriteLine("  m_NavMeshData: {fileID: 0}");
    }

    private static void WriteGameObjectYaml(StreamWriter sw, SceneNodeRecord node)
    {
        var go = node.GameObject;
        sw.Write("--- !u!1 &");
        sw.WriteLine(node.GameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("GameObject:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.WriteLine("  serializedVersion: 6");
        if (node.Components.Count == 0)
        {
            sw.WriteLine("  m_Component: []");
        }
        else
        {
            sw.WriteLine("  m_Component:");
            for (int i = 0; i < node.Components.Count; i++)
            {
                sw.Write("  - component: {fileID: ");
                sw.Write(node.Components[i].FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("}");
            }
        }
        sw.Write("  m_Layer: ");
        sw.WriteLine(go.m_Layer.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_Name: ");
        sw.WriteLine(FormatYamlScalarString(string.IsNullOrEmpty(go.m_Name) ? $"GameObject_{go.m_PathID}" : go.m_Name));
        sw.Write("  m_TagString: ");
        sw.WriteLine(FormatYamlScalarString(ResolveUnityTagString(go.m_Tag)));
        sw.WriteLine("  m_Icon: {fileID: 0}");
        sw.WriteLine("  m_NavMeshLayer: 0");
        sw.WriteLine("  m_StaticEditorFlags: 0");
        sw.Write("  m_IsActive: ");
        sw.WriteLine(go.m_IsActive ? "1" : "0");
    }

    private static void WriteComponentYaml(
        StreamWriter sw,
        SceneNodeRecord node,
        ExportedComponentRecord comp,
        ExportState state)
    {
        switch (comp.Kind)
        {
            case ExportedComponentKind.Transform:
            case ExportedComponentKind.RectTransform:
                WriteTransformYaml(sw, node, comp, state);
                break;
            case ExportedComponentKind.MeshFilter:
                WriteMeshFilterYaml(sw, comp);
                break;
            case ExportedComponentKind.MeshRenderer:
                WriteMeshRendererYaml(sw, comp);
                break;
            case ExportedComponentKind.SkinnedMeshRenderer:
                WriteSkinnedMeshRendererYaml(sw, comp, state);
                break;
            case ExportedComponentKind.Camera:
                WriteCameraYaml(sw, comp);
                break;
            case ExportedComponentKind.Light:
                WriteLightYaml(sw, comp);
                break;
            case ExportedComponentKind.LODGroup:
                WriteLodGroupYaml(sw, comp, state);
                break;
            case ExportedComponentKind.BoxCollider:
            case ExportedComponentKind.SphereCollider:
            case ExportedComponentKind.CapsuleCollider:
            case ExportedComponentKind.MeshCollider:
                WriteColliderYaml(sw, comp, state);
                break;
            case ExportedComponentKind.MonoBehaviour:
                WriteMonoBehaviourYaml(sw, comp, state);
                break;
            case ExportedComponentKind.TerrainMeshFilter:
                WriteTerrainMeshFilterYaml(sw, comp);
                break;
            case ExportedComponentKind.TerrainMeshRenderer:
                WriteTerrainMeshRendererYaml(sw, comp);
                break;
        }
    }

    private static void WriteTransformYaml(StreamWriter sw, SceneNodeRecord node, ExportedComponentRecord comp, ExportState state)
    {
        var tr = (Transform)comp.SourceObject!;
        bool isRect = tr is RectTransform;

        sw.Write(isRect ? "--- !u!224 &" : "--- !u!4 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine(isRect ? "RectTransform:" : "Transform:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        var localRot = node.OverrideLocalRotation ?? tr.m_LocalRotation;
        var localPos = node.OverrideLocalPosition ?? tr.m_LocalPosition;
        var localScale = node.OverrideLocalScale ?? tr.m_LocalScale;
        sw.Write("  m_LocalRotation: {x: ");
        sw.Write(FormatFloat(localRot.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(localRot.Y));
        sw.Write(", z: ");
        sw.Write(FormatFloat(localRot.Z));
        sw.Write(", w: ");
        sw.Write(FormatFloat(localRot.W, 1f));
        sw.WriteLine("}");
        sw.Write("  m_LocalPosition: {x: ");
        sw.Write(FormatFloat(localPos.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(localPos.Y));
        sw.Write(", z: ");
        sw.Write(FormatFloat(localPos.Z));
        sw.WriteLine("}");
        sw.Write("  m_LocalScale: {x: ");
        sw.Write(FormatFloat(localScale.X, 1f));
        sw.Write(", y: ");
        sw.Write(FormatFloat(localScale.Y, 1f));
        sw.Write(", z: ");
        sw.Write(FormatFloat(localScale.Z, 1f));
        sw.WriteLine("}");

        var childIds = new List<long>();
        if (tr.m_Children != null)
        {
            for (int i = 0; i < tr.m_Children.Length; i++)
            {
                var childPtr = tr.m_Children[i];
                if (childPtr != null && !childPtr.IsNull && TryLookupLocalFileId(tr.assetsFile, childPtr.m_FileID, childPtr.m_PathID, state, out long childId))
                {
                    childIds.Add(childId);
                }
            }
        }
        if (node.ExtraChildTransformFileIds is { Count: > 0 })
        {
            childIds.AddRange(node.ExtraChildTransformFileIds);
        }

        if (childIds.Count == 0)
        {
            sw.WriteLine("  m_Children: []");
        }
        else
        {
            sw.WriteLine("  m_Children:");
            for (int i = 0; i < childIds.Count; i++)
            {
                sw.Write("  - {fileID: ");
                sw.Write(childIds[i].ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("}");
            }
        }

        bool isPrefabRoot = state.PrefabRootObject != null && ReferenceEquals(node.GameObject, state.PrefabRootObject);
        long fatherId = 0;
        if (node.OverrideFatherFileId.HasValue)
        {
            fatherId = node.OverrideFatherFileId.Value;
        }
        else if (!isPrefabRoot && tr.m_Father != null && !tr.m_Father.IsNull)
        {
            TryLookupLocalFileId(tr.assetsFile, tr.m_Father.m_FileID, tr.m_Father.m_PathID, state, out fatherId);
        }

        sw.Write("  m_Father: {fileID: ");
        sw.Write(fatherId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_RootOrder: ");
        sw.WriteLine((isPrefabRoot ? 0 : node.RootOrder).ToString(CultureInfo.InvariantCulture));

        if (tr is RectTransform rt)
        {
            sw.Write("  m_AnchorMin: {x: ");
            sw.Write(FormatFloat(rt.m_AnchorMin.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_AnchorMin.Y));
            sw.WriteLine("}");
            sw.Write("  m_AnchorMax: {x: ");
            sw.Write(FormatFloat(rt.m_AnchorMax.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_AnchorMax.Y));
            sw.WriteLine("}");
            sw.Write("  m_AnchoredPosition: {x: ");
            sw.Write(FormatFloat(rt.m_AnchoredPosition.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_AnchoredPosition.Y));
            sw.WriteLine("}");
            sw.Write("  m_SizeDelta: {x: ");
            sw.Write(FormatFloat(rt.m_SizeDelta.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_SizeDelta.Y));
            sw.WriteLine("}");
            sw.Write("  m_Pivot: {x: ");
            sw.Write(FormatFloat(rt.m_Pivot.X));
            sw.Write(", y: ");
            sw.Write(FormatFloat(rt.m_Pivot.Y));
            sw.WriteLine("}");
        }
    }

    private static void WriteMeshFilterYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        sw.Write("--- !u!33 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshFilter:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Mesh: ");
        if (comp.ResolvedMesh != null && comp.ResolvedMesh.Exported)
        {
            sw.Write("{fileID: 4300000, guid: ");
            sw.Write(comp.ResolvedMesh.Guid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }
    }

    private static void WriteMeshRendererYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var mr = (MeshRenderer)comp.SourceObject!;
        sw.Write("--- !u!23 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshRenderer:");
        WriteCommonRendererProperties(sw, comp.OwnerGameObjectFileId, mr, comp.ResolvedMaterials);
    }

    private static void WriteSkinnedMeshRendererYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var smr = (SkinnedMeshRenderer)comp.SourceObject!;
        sw.Write("--- !u!137 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("SkinnedMeshRenderer:");
        WriteCommonRendererProperties(sw, comp.OwnerGameObjectFileId, smr, comp.ResolvedMaterials);

        sw.Write("  m_Mesh: ");
        if (comp.ResolvedMesh != null && comp.ResolvedMesh.Exported)
        {
            sw.Write("{fileID: 4300000, guid: ");
            sw.Write(comp.ResolvedMesh.Guid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }

        if (smr.m_Bones == null || smr.m_Bones.Length == 0)
        {
            sw.WriteLine("  m_Bones: []");
        }
        else
        {
            // Align m_Bones 1:1 with FbxSharpieExporter's active skin clusters (omitting zero-weight bones
            // that FBXImporter strips from Mesh.bindposes) so SkinnedMeshRenderer binds cleanly in Unity.
            var activeMask = comp.ResolvedMesh != null && comp.ResolvedMesh.RequiresSkinning
                ? FbxSharpieExporter.ComputeActiveSkinBoneMask(comp.ResolvedMesh.Mesh, smr.m_Bones.Length)
                : null;
            bool useActiveMask = activeMask != null && Array.IndexOf(activeMask, true) >= 0;

            var emittedBones = new List<long>(smr.m_Bones.Length);
            for (int i = 0; i < smr.m_Bones.Length; i++)
            {
                if (useActiveMask && !activeMask![i])
                {
                    continue;
                }

                var bonePtr = smr.m_Bones[i];
                long boneId = 0;
                if (bonePtr != null && !bonePtr.IsNull)
                {
                    TryLookupLocalFileId(smr.assetsFile, bonePtr.m_FileID, bonePtr.m_PathID, state, out boneId);
                }
                emittedBones.Add(boneId);
            }

            if (emittedBones.Count == 0)
            {
                sw.WriteLine("  m_Bones: []");
            }
            else
            {
                sw.WriteLine("  m_Bones:");
                for (int i = 0; i < emittedBones.Count; i++)
                {
                    sw.Write("  - {fileID: ");
                    sw.Write(emittedBones[i].ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine("}");
                }
            }
        }

        long rootBoneId = 0;
        if (smr.m_RootBone != null && !smr.m_RootBone.IsNull)
        {
            TryLookupLocalFileId(smr.assetsFile, smr.m_RootBone.m_FileID, smr.m_RootBone.m_PathID, state, out rootBoneId);
        }
        sw.Write("  m_RootBone: {fileID: ");
        sw.Write(rootBoneId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
    }

    private static void WriteCommonRendererProperties(
        StreamWriter sw,
        long ownerGoId,
        Renderer renderer,
        List<ExportedMaterialAsset?>? materials)
    {
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(ownerGoId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(renderer.m_Enabled ? "1" : "0");
        sw.Write("  m_CastShadows: ");
        sw.WriteLine(renderer.m_CastShadows.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_ReceiveShadows: ");
        sw.WriteLine(renderer.m_ReceiveShadows.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("  m_DynamicOccludee: 1");
        sw.WriteLine("  m_MotionVectors: 1");
        sw.WriteLine("  m_LightProbeUsage: 1");
        sw.WriteLine("  m_ReflectionProbeUsage: 1");
        sw.Write("  m_RenderingLayerMask: ");
        sw.WriteLine((renderer.m_RenderingLayerMask == 0 ? 1u : renderer.m_RenderingLayerMask).ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_RendererPriority: ");
        sw.WriteLine(renderer.m_RendererPriority.ToString(CultureInfo.InvariantCulture));

        if (materials == null || materials.Count == 0)
        {
            sw.WriteLine("  m_Materials: []");
        }
        else
        {
            sw.WriteLine("  m_Materials:");
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                if (mat != null)
                {
                    sw.Write("  - {fileID: 2100000, guid: ");
                    sw.Write(mat.Guid);
                    sw.WriteLine(", type: 2}");
                }
                else
                {
                    sw.WriteLine("  - {fileID: 0}");
                }
            }
        }

        sw.Write("  m_SortingLayerID: 0\n");
        sw.Write("  m_SortingLayer: ");
        sw.WriteLine(renderer.m_SortingLayer.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_SortingOrder: ");
        sw.WriteLine(renderer.m_SortingOrder.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteTerrainMeshFilterYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var terrainAsset = comp.ResolvedTerrain!;
        var size = terrainAsset.TerrainData.m_Heightmap.TerrainSize;

        sw.Write("--- !u!33 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshFilter:");
        sw.Write("  # OriginalComponent: Terrain (TerrainData: ");
        sw.Write(SanitizeYamlComment(terrainAsset.TerrainData.m_Name ?? terrainAsset.SafeName));
        sw.Write(", resolution: ");
        sw.Write(terrainAsset.TerrainData.m_Heightmap.EffectiveWidth.ToString(CultureInfo.InvariantCulture));
        sw.Write('x');
        sw.Write(terrainAsset.TerrainData.m_Heightmap.EffectiveHeight.ToString(CultureInfo.InvariantCulture));
        sw.Write(", size: ");
        sw.Write(FormatFloat(size.X));
        sw.Write('x');
        sw.Write(FormatFloat(size.Y));
        sw.Write('x');
        sw.Write(FormatFloat(size.Z));
        sw.WriteLine(")");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Mesh: ");
        if (terrainAsset.MeshData != null)
        {
            sw.Write("{fileID: 4300000, guid: ");
            sw.Write(terrainAsset.MeshGuid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }
    }

    private static void WriteTerrainMeshRendererYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var terrainAsset = comp.ResolvedTerrain!;
        var terrain = terrainAsset.Terrain;
        sw.Write("--- !u!23 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MeshRenderer:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(terrain.Enabled && terrain.m_DrawHeightmap ? "1" : "0");
        sw.Write("  m_CastShadows: ");
        sw.WriteLine(Math.Clamp(terrain.m_ShadowCastingMode, 0, 3).ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("  m_ReceiveShadows: 1");
        sw.WriteLine("  m_Materials:");
        sw.Write("  - {fileID: 2100000, guid: ");
        sw.Write(terrainAsset.MaterialGuid);
        sw.WriteLine(", type: 2}");
    }

    private static void WriteCameraYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var cam = (Camera)comp.SourceObject!;
        sw.Write("--- !u!20 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("Camera:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(cam.Enabled ? "1" : "0");
        sw.WriteLine("  serializedVersion: 2");
        sw.Write("  m_ClearFlags: ");
        sw.WriteLine(cam.m_ClearFlags.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_BackGroundColor: {r: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.R));
        sw.Write(", g: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.G));
        sw.Write(", b: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.B));
        sw.Write(", a: ");
        sw.Write(FormatFloat(cam.m_BackGroundColor.A));
        sw.WriteLine("}");
        sw.Write("  m_projectionMatrixMode: ");
        sw.WriteLine(cam.m_ProjectionMatrixMode.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_GateFitMode: ");
        sw.WriteLine(cam.m_GateFitMode.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_SensorSize: {x: ");
        sw.Write(FormatFloat(cam.m_SensorSize.X, 36f));
        sw.Write(", y: ");
        sw.Write(FormatFloat(cam.m_SensorSize.Y, 24f));
        sw.WriteLine("}");
        sw.Write("  m_LensShift: {x: ");
        sw.Write(FormatFloat(cam.m_LensShift.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(cam.m_LensShift.Y));
        sw.WriteLine("}");
        sw.Write("  m_FocalLength: ");
        sw.WriteLine(FormatFloat(cam.m_FocalLength, 50f));
        sw.Write("  m_NormalizedViewPortRect:");
        sw.WriteLine();
        sw.WriteLine("    serializedVersion: 2");
        sw.Write("    x: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.X));
        sw.Write("    y: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.Y));
        sw.Write("    width: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.Z, 1f));
        sw.Write("    height: ");
        sw.WriteLine(FormatFloat(cam.m_NormalizedViewPortRect.W, 1f));
        sw.Write("  near clip plane: ");
        sw.WriteLine(FormatFloat(cam.m_NearClipPlane, 0.3f));
        sw.Write("  far clip plane: ");
        sw.WriteLine(FormatFloat(cam.m_FarClipPlane, 1000f));
        sw.Write("  field of view: ");
        sw.WriteLine(FormatFloat(cam.m_FieldOfView, 60f));
        sw.Write("  orthographic: ");
        sw.WriteLine(cam.m_Orthographic ? "1" : "0");
        sw.Write("  orthographic size: ");
        sw.WriteLine(FormatFloat(cam.m_OrthographicSize, 5f));
        sw.Write("  m_Depth: ");
        sw.WriteLine(FormatFloat(cam.m_Depth));
        sw.WriteLine("  m_CullingMask:");
        sw.WriteLine("    serializedVersion: 2");
        sw.Write("    m_Bits: ");
        sw.WriteLine(cam.m_CullingMask.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_RenderingPath: ");
        sw.WriteLine(cam.m_RenderingPath.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteLightYaml(StreamWriter sw, ExportedComponentRecord comp)
    {
        var light = (Light)comp.SourceObject!;
        var shadows = light.m_Shadows ?? new ShadowSettings();

        sw.Write("--- !u!108 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("Light:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(light.Enabled ? "1" : "0");
        sw.WriteLine("  serializedVersion: 10");
        sw.Write("  m_Type: ");
        sw.WriteLine(((int)light.m_Type).ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_Shape: ");
        sw.WriteLine(light.m_Shape.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_Color: {r: ");
        sw.Write(FormatFloat(light.m_Color.R, 1f));
        sw.Write(", g: ");
        sw.Write(FormatFloat(light.m_Color.G, 1f));
        sw.Write(", b: ");
        sw.Write(FormatFloat(light.m_Color.B, 1f));
        sw.Write(", a: ");
        sw.Write(FormatFloat(light.m_Color.A, 1f));
        sw.WriteLine("}");
        sw.Write("  m_Intensity: ");
        sw.WriteLine(FormatFloat(light.m_Intensity, 1f));
        sw.Write("  m_Range: ");
        sw.WriteLine(FormatFloat(light.m_Range, 10f));
        sw.Write("  m_SpotAngle: ");
        sw.WriteLine(FormatFloat(light.m_SpotAngle, 30f));
        sw.Write("  m_InnerSpotAngle: ");
        sw.WriteLine(FormatFloat(light.m_InnerSpotAngle));
        sw.Write("  m_CookieSize: ");
        sw.WriteLine(FormatFloat(light.m_CookieSize, 10f));
        sw.WriteLine("  m_Shadows:");
        sw.Write("    m_Type: ");
        sw.WriteLine(shadows.m_Type.ToString(CultureInfo.InvariantCulture));
        sw.Write("    m_Resolution: ");
        sw.WriteLine(shadows.m_Resolution.ToString(CultureInfo.InvariantCulture));
        sw.Write("    m_CustomResolution: ");
        sw.WriteLine(shadows.m_CustomResolution.ToString(CultureInfo.InvariantCulture));
        sw.Write("    m_Strength: ");
        sw.WriteLine(FormatFloat(shadows.m_Strength, 1f));
        sw.Write("    m_Bias: ");
        sw.WriteLine(FormatFloat(shadows.m_Bias, 0.05f));
        sw.Write("    m_NormalBias: ");
        sw.WriteLine(FormatFloat(shadows.m_NormalBias, 0.4f));
        sw.Write("    m_NearPlane: ");
        sw.WriteLine(FormatFloat(shadows.m_NearPlane, 0.2f));
    }

    private static void WriteLodGroupYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var lodGroup = (LODGroup)comp.SourceObject!;
        sw.Write("--- !u!205 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("LODGroup:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_LocalReferencePoint: {x: ");
        sw.Write(FormatFloat(lodGroup.m_LocalReferencePoint.X));
        sw.Write(", y: ");
        sw.Write(FormatFloat(lodGroup.m_LocalReferencePoint.Y));
        sw.Write(", z: ");
        sw.Write(FormatFloat(lodGroup.m_LocalReferencePoint.Z));
        sw.WriteLine("}");
        sw.Write("  m_Size: ");
        sw.WriteLine(FormatFloat(lodGroup.m_Size, 1f));
        sw.Write("  m_FadeMode: ");
        sw.WriteLine(lodGroup.m_FadeMode.ToString(CultureInfo.InvariantCulture));
        sw.Write("  m_AnimateCrossFading: ");
        sw.WriteLine(lodGroup.m_AnimateCrossFading ? "1" : "0");
        sw.Write("  m_LastLODIsBillboard: ");
        sw.WriteLine(lodGroup.m_LastLODIsBillboard ? "1" : "0");

        var lodLevels = ModelConverter.ExtractLodLevels(lodGroup);
        if (lodLevels.Count == 0)
        {
            sw.WriteLine("  m_LODs: []");
        }
        else
        {
            sw.WriteLine("  m_LODs:");
            for (int l = 0; l < lodLevels.Count; l++)
            {
                var lod = lodLevels[l];
                if (lod == null)
                {
                    continue;
                }

                sw.Write("  - screenRelativeHeight: ");
                sw.WriteLine(FormatFloat(lod.screenRelativeHeight));
                sw.Write("    fadeTransitionWidth: ");
                sw.WriteLine(FormatFloat(lod.fadeTransitionWidth));

                var rendIds = new List<long>();
                if (lod.renderers != null)
                {
                    for (int r = 0; r < lod.renderers.Length; r++)
                    {
                        var rPtr = lod.renderers[r];
                        if (rPtr != null && !rPtr.IsNull && TryLookupLocalFileId(lodGroup.assetsFile, rPtr.m_FileID, rPtr.m_PathID, state, out long rendId))
                        {
                            rendIds.Add(rendId);
                        }
                    }
                }

                if (rendIds.Count == 0)
                {
                    sw.WriteLine("    renderers: []");
                }
                else
                {
                    sw.WriteLine("    renderers:");
                    for (int r = 0; r < rendIds.Count; r++)
                    {
                        sw.Write("    - renderer: {fileID: ");
                        sw.Write(rendIds[r].ToString(CultureInfo.InvariantCulture));
                        sw.WriteLine("}");
                    }
                }
            }
        }

        sw.Write("  m_Enabled: ");
        sw.WriteLine(lodGroup.m_Enabled ? "1" : "0");
    }

    private static void WriteColliderYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var obj = comp.SourceObject!;
        var hasProps = ColliderMeshConverter.TryExtractColliderProperties(obj, o => ReadColliderFields(o, state), out var props);

        bool enabled = !hasProps || props.Enabled;
        bool isTrigger = hasProps && props.IsTrigger;

        switch (comp.Kind)
        {
            case ExportedComponentKind.BoxCollider:
            {
                var size = hasProps ? props.Size : new Vector3(1f, 1f, 1f);
                var center = hasProps ? props.Center : Vector3.Zero;
                sw.Write("--- !u!65 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("BoxCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Size: {x: ");
                sw.Write(FormatFloat(size.X, 1f));
                sw.Write(", y: ");
                sw.Write(FormatFloat(size.Y, 1f));
                sw.Write(", z: ");
                sw.Write(FormatFloat(size.Z, 1f));
                sw.WriteLine("}");
                sw.Write("  m_Center: {x: ");
                sw.Write(FormatFloat(center.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(center.Y));
                sw.Write(", z: ");
                sw.Write(FormatFloat(center.Z));
                sw.WriteLine("}");
                break;
            }

            case ExportedComponentKind.SphereCollider:
            {
                float radius = hasProps ? props.Radius : 0.5f;
                var center = hasProps ? props.Center : Vector3.Zero;
                sw.Write("--- !u!135 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("SphereCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Radius: ");
                sw.WriteLine(FormatFloat(radius, 0.5f));
                sw.Write("  m_Center: {x: ");
                sw.Write(FormatFloat(center.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(center.Y));
                sw.Write(", z: ");
                sw.Write(FormatFloat(center.Z));
                sw.WriteLine("}");
                break;
            }

            case ExportedComponentKind.CapsuleCollider:
            {
                float radius = hasProps ? props.Radius : 0.5f;
                float height = hasProps ? props.Height : 2f;
                int direction = hasProps ? props.Direction : 1;
                var center = hasProps ? props.Center : Vector3.Zero;
                sw.Write("--- !u!136 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("CapsuleCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Radius: ");
                sw.WriteLine(FormatFloat(radius, 0.5f));
                sw.Write("  m_Height: ");
                sw.WriteLine(FormatFloat(height, 2f));
                sw.Write("  m_Direction: ");
                sw.WriteLine(direction.ToString(CultureInfo.InvariantCulture));
                sw.Write("  m_Center: {x: ");
                sw.Write(FormatFloat(center.X));
                sw.Write(", y: ");
                sw.Write(FormatFloat(center.Y));
                sw.Write(", z: ");
                sw.Write(FormatFloat(center.Z));
                sw.WriteLine("}");
                break;
            }

            case ExportedComponentKind.MeshCollider:
            {
                bool convex = hasProps && props.Convex;
                int cookingOptions = hasProps ? props.CookingOptions : 30;
                sw.Write("--- !u!64 &");
                sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine("MeshCollider:");
                WriteCommonColliderHeader(sw, comp.OwnerGameObjectFileId, enabled, isTrigger);
                sw.Write("  m_Convex: ");
                sw.WriteLine(convex ? "1" : "0");
                sw.Write("  m_CookingOptions: ");
                sw.WriteLine(cookingOptions.ToString(CultureInfo.InvariantCulture));
                sw.Write("  m_Mesh: ");
                if (comp.ResolvedMesh != null && comp.ResolvedMesh.Exported)
                {
                    sw.Write("{fileID: 4300000, guid: ");
                    sw.Write(comp.ResolvedMesh.Guid);
                    sw.WriteLine(", type: 3}");
                }
                else
                {
                    sw.WriteLine("{fileID: 0}");
                }
                break;
            }
        }
    }

    private static void WriteCommonColliderHeader(StreamWriter sw, long ownerGoId, bool enabled, bool isTrigger)
    {
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(ownerGoId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.WriteLine("  m_Material: {fileID: 0}");
        sw.Write("  m_IsTrigger: ");
        sw.WriteLine(isTrigger ? "1" : "0");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(enabled ? "1" : "0");
    }

    private static void WriteMonoBehaviourYaml(StreamWriter sw, ExportedComponentRecord comp, ExportState state)
    {
        var mb = (MonoBehaviour)comp.SourceObject!;
        sw.Write("--- !u!114 &");
        sw.WriteLine(comp.FileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("MonoBehaviour:");
        sw.WriteLine("  m_ObjectHideFlags: 0");
        sw.WriteLine("  m_CorrespondingSourceObject: {fileID: 0}");
        sw.WriteLine("  m_PrefabInstance: {fileID: 0}");
        sw.WriteLine("  m_PrefabAsset: {fileID: 0}");
        sw.Write("  m_GameObject: {fileID: ");
        sw.Write(comp.OwnerGameObjectFileId.ToString(CultureInfo.InvariantCulture));
        sw.WriteLine("}");
        sw.Write("  m_Enabled: ");
        sw.WriteLine(mb.m_Enabled != 0 ? "1" : "0");
        sw.WriteLine("  m_EditorHideFlags: 0");
        sw.Write("  m_Script: ");
        if (!string.IsNullOrEmpty(comp.MonoScriptGuid))
        {
            sw.Write("{fileID: 11500000, guid: ");
            sw.Write(comp.MonoScriptGuid);
            sw.WriteLine(", type: 3}");
        }
        else
        {
            sw.WriteLine("{fileID: 0}");
        }
        sw.Write("  m_Name: ");
        sw.WriteLine(FormatYamlScalarString(mb.m_Name ?? string.Empty));
        sw.WriteLine("  m_EditorClassIdentifier: ");

        var fields = ReadMonoBehaviourFields(mb, state);
        if (fields != null && fields.Count > 0)
        {
            var assetName = !string.IsNullOrWhiteSpace(mb.m_Name)
                ? mb.m_Name
                : (mb.m_Script != null && mb.m_Script.TryGet(out var ms) && !string.IsNullOrWhiteSpace(ms?.m_ClassName)
                    ? ms.m_ClassName
                    : $"MonoBehaviour_{mb.m_PathID}");
            var ctx = new YamlSerializationContext(
                assetName,
                mb.assetsFile?.fileName ?? string.Empty,
                mb.m_PathID,
                mb.assetsFile,
                state.Options.MaxYamlSerializationDepth > 0 ? state.Options.MaxYamlSerializationDepth : 64,
                Math.Max(0, state.Options.MaxYamlCollectionElements),
                Math.Max(0, state.Options.MaxYamlByteArrayLength),
                state);

            foreach (DictionaryEntry entry in fields)
            {
                var key = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(key) || MonoBehaviourHeaderFields.Contains(key))
                {
                    continue;
                }

                WriteYamlProperty(sw, key, entry.Value, indentSpaces: 2, depth: 0, fieldPath: key, ctx);
            }
        }
    }

    // =========================================================================
    // Incremental Unity YAML Serializer for MonoBehaviour TypeTree Fields
    // =========================================================================

    private readonly struct YamlSerializationContext
    {
        public string AssetName { get; }
        public string SourceFile { get; }
        public long PathId { get; }
        public SerializedFile? OwnerFile { get; }
        public int MaxDepth { get; }
        public int MaxCollectionElements { get; }
        public int MaxByteArrayLength { get; }
        public ExportState State { get; }

        public YamlSerializationContext(
            string assetName,
            string sourceFile,
            long pathId,
            SerializedFile? ownerFile,
            int maxDepth,
            int maxCollectionElements,
            int maxByteArrayLength,
            ExportState state)
        {
            AssetName = assetName;
            SourceFile = sourceFile;
            PathId = pathId;
            OwnerFile = ownerFile;
            MaxDepth = maxDepth;
            MaxCollectionElements = maxCollectionElements;
            MaxByteArrayLength = maxByteArrayLength;
            State = state;
        }

        public void RecordTruncation(string fieldPath, string limitKind, int limitValue, int totalCount, int omittedCount)
        {
            State.TruncationDiagnostics.Add(new UnityProjectTruncationDiagnostic
            {
                AssetName = AssetName,
                AssetType = "MonoBehaviour",
                SourceFile = SourceFile,
                PathId = PathId,
                FieldPath = fieldPath,
                LimitKind = limitKind,
                LimitValue = limitValue,
                TotalCount = totalCount,
                OmittedCount = omittedCount
            });
        }
    }

    private static void WriteYamlByteArrayHex(StreamWriter sw, byte[] bytes, string fieldPath, in YamlSerializationContext ctx)
    {
        if (bytes.Length == 0)
        {
            sw.WriteLine(" []");
            return;
        }

        int count = ctx.MaxByteArrayLength > 0 && bytes.Length > ctx.MaxByteArrayLength
            ? ctx.MaxByteArrayLength
            : bytes.Length;

        sw.Write(' ');
        for (int offset = 0; offset < count; offset += YamlHexChunkBytes)
        {
            int chunk = Math.Min(YamlHexChunkBytes, count - offset);
            sw.Write(Convert.ToHexString(bytes.AsSpan(offset, chunk)).ToLowerInvariant());
        }

        if (count < bytes.Length)
        {
            int omitted = bytes.Length - count;
            ctx.RecordTruncation(fieldPath, "MaxByteArrayLength", ctx.MaxByteArrayLength, bytes.Length, omitted);
            sw.Write(" # [AssetStudio] Truncated byte[] (");
            sw.Write(omitted.ToString(CultureInfo.InvariantCulture));
            sw.Write(" of ");
            sw.Write(bytes.Length.ToString(CultureInfo.InvariantCulture));
            sw.WriteLine(" bytes omitted)");
        }
        else
        {
            sw.WriteLine();
        }
    }

    private static void WriteYamlProperty(
        StreamWriter sw,
        string key,
        object? value,
        int indentSpaces,
        int depth,
        string fieldPath,
        in YamlSerializationContext ctx)
    {
        if (depth > ctx.MaxDepth)
        {
            ctx.RecordTruncation(fieldPath, "MaxDepth", ctx.MaxDepth, totalCount: 1, omittedCount: 1);
            WriteIndent(sw, indentSpaces);
            sw.Write("# [AssetStudio] Truncated field '");
            sw.Write(fieldPath);
            sw.Write("' at MaxDepth=");
            sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
            return;
        }

        WriteIndent(sw, indentSpaces);
        sw.Write(FormatYamlKey(key));
        sw.Write(':');

        if (value == null)
        {
            sw.WriteLine(" ");
            return;
        }

        if (TryFormatInlineScalarOrStruct(value, ctx.OwnerFile, ctx.State, out var inlineText))
        {
            sw.Write(' ');
            sw.WriteLine(inlineText);
            return;
        }

        if (value is byte[] bytes)
        {
            WriteYamlByteArrayHex(sw, bytes, fieldPath, ctx);
            return;
        }

        if (value is IDictionary dict)
        {
            if (dict.Count == 0)
            {
                sw.WriteLine(" {}");
                return;
            }

            if (depth + 1 > ctx.MaxDepth)
            {
                ctx.RecordTruncation(fieldPath, "MaxDepth", ctx.MaxDepth, totalCount: dict.Count, omittedCount: dict.Count);
                sw.Write(" {} # [AssetStudio] Truncated dictionary at MaxDepth=");
                sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
                return;
            }

            sw.WriteLine();
            int written = 0;
            int totalNonEmpty = 0;
            foreach (DictionaryEntry child in dict)
            {
                var childKey = child.Key?.ToString();
                if (string.IsNullOrWhiteSpace(childKey))
                {
                    continue;
                }

                totalNonEmpty++;
                if (ctx.MaxCollectionElements > 0 && written >= ctx.MaxCollectionElements)
                {
                    continue;
                }

                WriteYamlProperty(sw, childKey, child.Value, indentSpaces + 2, depth + 1, $"{fieldPath}.{childKey}", ctx);
                written++;
            }

            if (totalNonEmpty > written)
            {
                int omitted = totalNonEmpty - written;
                ctx.RecordTruncation(fieldPath, "MaxCollectionElements", ctx.MaxCollectionElements, totalNonEmpty, omitted);
                WriteIndent(sw, indentSpaces + 2);
                sw.Write("# [AssetStudio] Truncated dictionary '");
                sw.Write(fieldPath);
                sw.Write("' (");
                sw.Write(omitted.ToString(CultureInfo.InvariantCulture));
                sw.Write(" of ");
                sw.Write(totalNonEmpty.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine(" entries omitted)");
            }
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var enumerator = enumerable.GetEnumerator();
            try
            {
                if (!enumerator.MoveNext())
                {
                    sw.WriteLine(" []");
                    return;
                }

                if (depth + 1 > ctx.MaxDepth)
                {
                    int total = 1;
                    while (enumerator.MoveNext()) total++;
                    ctx.RecordTruncation(fieldPath, "MaxDepth", ctx.MaxDepth, totalCount: total, omittedCount: total);
                    sw.Write(" [] # [AssetStudio] Truncated collection at MaxDepth=");
                    sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                sw.WriteLine();
                int written = 0;
                int totalCount = 0;
                do
                {
                    totalCount++;
                    if (ctx.MaxCollectionElements > 0 && written >= ctx.MaxCollectionElements)
                    {
                        continue;
                    }

                    WriteYamlListItem(sw, enumerator.Current, indentSpaces, depth + 1, $"{fieldPath}[{written}]", ctx);
                    written++;
                }
                while (enumerator.MoveNext());

                if (totalCount > written)
                {
                    int omitted = totalCount - written;
                    ctx.RecordTruncation(fieldPath, "MaxCollectionElements", ctx.MaxCollectionElements, totalCount, omitted);
                    WriteIndent(sw, indentSpaces);
                    sw.Write("# [AssetStudio] Truncated collection '");
                    sw.Write(fieldPath);
                    sw.Write("' (");
                    sw.Write(omitted.ToString(CultureInfo.InvariantCulture));
                    sw.Write(" of ");
                    sw.Write(totalCount.ToString(CultureInfo.InvariantCulture));
                    sw.WriteLine(" elements omitted)");
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
            return;
        }

        sw.Write(' ');
        sw.WriteLine(FormatYamlScalarString(value.ToString() ?? string.Empty));
    }

    private static void WriteYamlListItem(
        StreamWriter sw,
        object? item,
        int indentSpaces,
        int depth,
        string itemPath,
        in YamlSerializationContext ctx)
    {
        if (depth > ctx.MaxDepth)
        {
            ctx.RecordTruncation(itemPath, "MaxDepth", ctx.MaxDepth, totalCount: 1, omittedCount: 1);
            WriteIndent(sw, indentSpaces);
            sw.Write("- {} # [AssetStudio] Truncated list item at MaxDepth=");
            sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
            return;
        }

        WriteIndent(sw, indentSpaces);
        if (item == null)
        {
            sw.WriteLine("- ");
            return;
        }

        if (TryFormatInlineScalarOrStruct(item, ctx.OwnerFile, ctx.State, out var inlineText))
        {
            sw.Write("- ");
            sw.WriteLine(inlineText);
            return;
        }

        if (item is byte[] itemBytes)
        {
            sw.Write('-');
            WriteYamlByteArrayHex(sw, itemBytes, itemPath, ctx);
            return;
        }

        if (item is IDictionary dict && dict.Count > 0)
        {
            bool first = true;
            int written = 0;
            int totalNonEmpty = 0;
            foreach (DictionaryEntry child in dict)
            {
                var childKey = child.Key?.ToString();
                if (string.IsNullOrWhiteSpace(childKey))
                {
                    continue;
                }

                totalNonEmpty++;
                if (ctx.MaxCollectionElements > 0 && written >= ctx.MaxCollectionElements)
                {
                    continue;
                }

                var childPath = $"{itemPath}.{childKey}";
                if (first)
                {
                    sw.Write("- ");
                    sw.Write(FormatYamlKey(childKey));
                    sw.Write(':');
                    if (child.Value != null && TryFormatInlineScalarOrStruct(child.Value, ctx.OwnerFile, ctx.State, out var firstInline))
                    {
                        sw.Write(' ');
                        sw.WriteLine(firstInline);
                    }
                    else if (child.Value == null)
                    {
                        sw.WriteLine(" ");
                    }
                    else if (child.Value is byte[] nestedBytes)
                    {
                        WriteYamlByteArrayHex(sw, nestedBytes, childPath, ctx);
                    }
                    else if (depth + 1 > ctx.MaxDepth)
                    {
                        ctx.RecordTruncation(childPath, "MaxDepth", ctx.MaxDepth, totalCount: 1, omittedCount: 1);
                        sw.Write(" {} # [AssetStudio] Truncated nested field at MaxDepth=");
                        sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sw.WriteLine();
                        WriteYamlNestedValue(sw, child.Value, indentSpaces + 4, depth + 1, childPath, ctx);
                    }
                    first = false;
                }
                else
                {
                    WriteYamlProperty(sw, childKey, child.Value, indentSpaces + 2, depth + 1, childPath, ctx);
                }

                written++;
            }

            if (first)
            {
                sw.WriteLine("- {}");
            }
            else if (totalNonEmpty > written)
            {
                int omitted = totalNonEmpty - written;
                ctx.RecordTruncation(itemPath, "MaxCollectionElements", ctx.MaxCollectionElements, totalNonEmpty, omitted);
                WriteIndent(sw, indentSpaces + 2);
                sw.Write("# [AssetStudio] Truncated dictionary '");
                sw.Write(itemPath);
                sw.Write("' (");
                sw.Write(omitted.ToString(CultureInfo.InvariantCulture));
                sw.Write(" of ");
                sw.Write(totalNonEmpty.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine(" entries omitted)");
            }
            return;
        }

        if (item is IEnumerable nestedEnum && item is not string)
        {
            if (depth + 1 > ctx.MaxDepth)
            {
                ctx.RecordTruncation(itemPath, "MaxDepth", ctx.MaxDepth, totalCount: 1, omittedCount: 1);
                sw.Write("- [] # [AssetStudio] Truncated nested collection at MaxDepth=");
                sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
                return;
            }

            sw.WriteLine("-");
            WriteYamlNestedValue(sw, nestedEnum, indentSpaces + 2, depth + 1, itemPath, ctx);
            return;
        }

        sw.Write("- ");
        sw.WriteLine(FormatYamlScalarString(item.ToString() ?? string.Empty));
    }

    private static void WriteYamlNestedValue(
        StreamWriter sw,
        object value,
        int indentSpaces,
        int depth,
        string fieldPath,
        in YamlSerializationContext ctx)
    {
        if (depth > ctx.MaxDepth)
        {
            ctx.RecordTruncation(fieldPath, "MaxDepth", ctx.MaxDepth, totalCount: 1, omittedCount: 1);
            WriteIndent(sw, indentSpaces);
            sw.Write("# [AssetStudio] Truncated nested value '");
            sw.Write(fieldPath);
            sw.Write("' at MaxDepth=");
            sw.WriteLine(ctx.MaxDepth.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value is IDictionary dict)
        {
            int written = 0;
            int totalNonEmpty = 0;
            foreach (DictionaryEntry entry in dict)
            {
                var k = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(k)) continue;
                totalNonEmpty++;
                if (ctx.MaxCollectionElements > 0 && written >= ctx.MaxCollectionElements) continue;
                WriteYamlProperty(sw, k, entry.Value, indentSpaces, depth + 1, $"{fieldPath}.{k}", ctx);
                written++;
            }

            if (totalNonEmpty > written)
            {
                int omitted = totalNonEmpty - written;
                ctx.RecordTruncation(fieldPath, "MaxCollectionElements", ctx.MaxCollectionElements, totalNonEmpty, omitted);
                WriteIndent(sw, indentSpaces);
                sw.Write("# [AssetStudio] Truncated nested dictionary '");
                sw.Write(fieldPath);
                sw.Write("' (");
                sw.Write(omitted.ToString(CultureInfo.InvariantCulture));
                sw.Write(" of ");
                sw.Write(totalNonEmpty.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine(" entries omitted)");
            }
        }
        else if (value is IEnumerable enumerable && value is not string)
        {
            int written = 0;
            int totalCount = 0;
            foreach (var elem in enumerable)
            {
                totalCount++;
                if (ctx.MaxCollectionElements > 0 && written >= ctx.MaxCollectionElements) continue;
                WriteYamlListItem(sw, elem, indentSpaces, depth + 1, $"{fieldPath}[{written}]", ctx);
                written++;
            }

            if (totalCount > written)
            {
                int omitted = totalCount - written;
                ctx.RecordTruncation(fieldPath, "MaxCollectionElements", ctx.MaxCollectionElements, totalCount, omitted);
                WriteIndent(sw, indentSpaces);
                sw.Write("# [AssetStudio] Truncated nested collection '");
                sw.Write(fieldPath);
                sw.Write("' (");
                sw.Write(omitted.ToString(CultureInfo.InvariantCulture));
                sw.Write(" of ");
                sw.Write(totalCount.ToString(CultureInfo.InvariantCulture));
                sw.WriteLine(" elements omitted)");
            }
        }
    }

    private static bool TryFormatInlineScalarOrStruct(
        object value,
        SerializedFile? ownerFile,
        ExportState state,
        out string formatted)
    {
        switch (value)
        {
            case bool b:
                formatted = b ? "1" : "0";
                return true;
            case byte u8:
                formatted = u8.ToString(CultureInfo.InvariantCulture);
                return true;
            case sbyte i8:
                formatted = i8.ToString(CultureInfo.InvariantCulture);
                return true;
            case short i16:
                formatted = i16.ToString(CultureInfo.InvariantCulture);
                return true;
            case ushort u16:
                formatted = u16.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i32:
                formatted = i32.ToString(CultureInfo.InvariantCulture);
                return true;
            case uint u32:
                formatted = u32.ToString(CultureInfo.InvariantCulture);
                return true;
            case long i64:
                formatted = i64.ToString(CultureInfo.InvariantCulture);
                return true;
            case ulong u64:
                formatted = u64.ToString(CultureInfo.InvariantCulture);
                return true;
            case float f32:
                formatted = FormatFloat(f32);
                return true;
            case double f64:
                formatted = FormatDouble(f64);
                return true;
            case string str:
                formatted = FormatYamlScalarString(str);
                return true;
            case char ch:
                formatted = FormatYamlScalarString(ch.ToString());
                return true;
            case IDictionary dict:
                // 1) PPtr rewriting ({m_FileID, m_PathID}) -> local {fileID} or external {fileID, guid, type}
                if (dict.Count == 2 && dict.Contains("m_FileID") && dict.Contains("m_PathID")
                    && TryConvertInt(dict["m_FileID"], out int fileId)
                    && TryConvertLong(dict["m_PathID"], out long pathId))
                {
                    formatted = FormatRewrittenPPtr(ownerFile, fileId, pathId, state);
                    return true;
                }

                // 2) Compact Unity math structs ({x, y}, {x, y, z}, {x, y, z, w}, {r, g, b, a}, {x, y, width, height})
                if (dict.Count == 2 && dict.Contains("x") && dict.Contains("y")
                    && TryConvertFloat(dict["x"], out float vx2) && TryConvertFloat(dict["y"], out float vy2))
                {
                    formatted = $"{{x: {FormatFloat(vx2)}, y: {FormatFloat(vy2)}}}";
                    return true;
                }

                if (dict.Count == 3 && dict.Contains("x") && dict.Contains("y") && dict.Contains("z")
                    && TryConvertFloat(dict["x"], out float vx3) && TryConvertFloat(dict["y"], out float vy3) && TryConvertFloat(dict["z"], out float vz3))
                {
                    formatted = $"{{x: {FormatFloat(vx3)}, y: {FormatFloat(vy3)}, z: {FormatFloat(vz3)}}}";
                    return true;
                }

                if (dict.Count == 4 && dict.Contains("x") && dict.Contains("y") && dict.Contains("z") && dict.Contains("w")
                    && TryConvertFloat(dict["x"], out float vx4) && TryConvertFloat(dict["y"], out float vy4)
                    && TryConvertFloat(dict["z"], out float vz4) && TryConvertFloat(dict["w"], out float vw4))
                {
                    formatted = $"{{x: {FormatFloat(vx4)}, y: {FormatFloat(vy4)}, z: {FormatFloat(vz4)}, w: {FormatFloat(vw4)}}}";
                    return true;
                }

                if (dict.Count == 4 && dict.Contains("r") && dict.Contains("g") && dict.Contains("b") && dict.Contains("a")
                    && TryConvertFloat(dict["r"], out float cr) && TryConvertFloat(dict["g"], out float cg)
                    && TryConvertFloat(dict["b"], out float cb) && TryConvertFloat(dict["a"], out float ca))
                {
                    formatted = $"{{r: {FormatFloat(cr)}, g: {FormatFloat(cg)}, b: {FormatFloat(cb)}, a: {FormatFloat(ca)}}}";
                    return true;
                }

                if (dict.Count == 4 && dict.Contains("x") && dict.Contains("y") && dict.Contains("width") && dict.Contains("height")
                    && TryConvertFloat(dict["x"], out float rx) && TryConvertFloat(dict["y"], out float ry)
                    && TryConvertFloat(dict["width"], out float rw) && TryConvertFloat(dict["height"], out float rh))
                {
                    formatted = $"{{x: {FormatFloat(rx)}, y: {FormatFloat(ry)}, width: {FormatFloat(rw)}, height: {FormatFloat(rh)}}}";
                    return true;
                }

                break;
        }

        formatted = string.Empty;
        return false;
    }

    private static string FormatRewrittenPPtr(SerializedFile? ownerFile, int fileId, long pathId, ExportState state)
    {
        if (pathId == 0 || fileId < 0)
        {
            return "{fileID: 0}";
        }

        if (TryLookupLocalFileId(ownerFile, fileId, pathId, state, out long localId))
        {
            return $"{{fileID: {localId.ToString(CultureInfo.InvariantCulture)}}}";
        }

        var targetFileName = ResolvePPtrFileName(ownerFile, fileId);
        var key = (targetFileName, pathId);

        if (state.MeshesByKey.TryGetValue(key, out var meshAsset) && meshAsset.Exported)
        {
            return $"{{fileID: 4300000, guid: {meshAsset.Guid}, type: 3}}";
        }

        if (state.MaterialsByKey.TryGetValue(key, out var matAsset))
        {
            return $"{{fileID: 2100000, guid: {matAsset.Guid}, type: 2}}";
        }

        if (state.TexturesByKey.TryGetValue(key, out var texAsset) && texAsset.Exported)
        {
            return $"{{fileID: 2800000, guid: {texAsset.Guid}, type: 3}}";
        }

        return "{fileID: 0}";
    }

    // =========================================================================
    // Path Confinement & YAML Formatting Helpers
    // =========================================================================

    private static string EnsureConfinedDirectory(string rootDirWithSep, string targetDir)
    {
        var confined = EnsureConfinedPath(rootDirWithSep, targetDir);
        Directory.CreateDirectory(confined);
        return confined;
    }

    private static string EnsureConfinedPath(string rootDirWithSep, string candidatePath)
    {
        var full = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootDirWithSep, comparison))
        {
            throw new InvalidOperationException($"Refusing to write outside Unity project root: '{candidatePath}'.");
        }
        return full;
    }

    private static void WriteYamlStringArray(StreamWriter sw, string prefixedKey, string[]? values)
    {
        if (values == null || values.Length == 0)
        {
            sw.Write(prefixedKey);
            sw.WriteLine(": []");
            return;
        }

        sw.Write(prefixedKey);
        sw.WriteLine(":");
        for (int i = 0; i < values.Length; i++)
        {
            sw.Write("  - ");
            sw.WriteLine(FormatYamlScalarString(values[i] ?? string.Empty));
        }
    }

    private static void WriteIndent(StreamWriter sw, int spaces)
    {
        for (int i = 0; i < spaces; i++)
        {
            sw.Write(' ');
        }
    }

    private static string FormatFloat(float value, float fallbackIfNonFinite = 0f)
    {
        float safe = float.IsFinite(value) ? value : fallbackIfNonFinite;
        if (safe == 0f)
        {
            return "0";
        }
        return safe.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value)
    {
        double safe = double.IsFinite(value) ? value : 0.0;
        if (safe == 0.0)
        {
            return "0";
        }
        return safe.ToString(CultureInfo.InvariantCulture);
    }

    private static string SanitizeYamlComment(string text)
    {
        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string FormatYamlKey(string? rawKey)
    {
        var key = string.IsNullOrWhiteSpace(rawKey) ? "unnamed" : rawKey.Trim();
        return NeedsYamlQuoting(key) ? QuoteYamlDouble(key) : key;
    }

    private static string FormatYamlScalarString(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return string.Empty;
        }

        return NeedsYamlQuoting(rawValue) ? QuoteYamlDouble(rawValue) : rawValue;
    }

    private static bool NeedsYamlQuoting(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
        {
            return true;
        }

        if (YamlSpecialKeywords.Contains(s))
        {
            return true;
        }

        char first = s[0];
        if (first is '-' or '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`')
        {
            return true;
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x20 || c == '\r' || c == '\n' || c == ':' || c == '#' || c == '"' || c == '\\' || c == '{' || c == '}' || c == '[' || c == ']')
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteYamlDouble(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // =========================================================================
    // Dictionary Field Conversion Helpers
    // =========================================================================

    private static bool TryExtractPPtrValues(IDictionary dict, string key, out int fileId, out long pathId)
    {
        fileId = 0;
        pathId = 0;
        if (!dict.Contains(key) || dict[key] is not IDictionary pptr)
        {
            return false;
        }

        return pptr.Contains("m_FileID")
            && pptr.Contains("m_PathID")
            && TryConvertInt(pptr["m_FileID"], out fileId)
            && TryConvertLong(pptr["m_PathID"], out pathId);
    }

    private static bool TryReadVector3(IDictionary dict, string key, out Vector3 value)
    {
        value = Vector3.Zero;
        if (!dict.Contains(key) || dict[key] is not IDictionary v)
        {
            return false;
        }

        TryReadFloat(v, "x", out float x);
        TryReadFloat(v, "y", out float y);
        TryReadFloat(v, "z", out float z);
        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryReadFloat(IDictionary dict, string key, out float value)
    {
        value = 0f;
        return dict.Contains(key) && TryConvertFloat(dict[key], out value);
    }

    private static bool TryReadInt(IDictionary dict, string key, out int value)
    {
        value = 0;
        return dict.Contains(key) && TryConvertInt(dict[key], out value);
    }

    private static bool TryReadBool(IDictionary dict, string key, out bool value)
    {
        value = false;
        if (!dict.Contains(key) || dict[key] == null) return false;
        try
        {
            value = Convert.ToBoolean(dict[key], CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertFloat(object? raw, out float value)
    {
        value = 0f;
        if (raw == null) return false;
        try
        {
            value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertInt(object? raw, out int value)
    {
        value = 0;
        if (raw == null) return false;
        try
        {
            value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertLong(object? raw, out long value)
    {
        value = 0;
        if (raw == null) return false;
        try
        {
            value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
