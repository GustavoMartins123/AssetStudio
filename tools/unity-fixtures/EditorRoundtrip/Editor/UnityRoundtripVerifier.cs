using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class UnityRoundtripVerifier
{
    [Serializable]
    private sealed class Report
    {
        public string scriptGuid;
        public string prefabGuid;
        public int missingScripts;
        public int missingFbxMeshes;
        public bool materialHasMainTex;
        public bool materialHasBumpMap;
        public float towerPosX, towerPosY, towerPosZ;
        public float towerScaleX, towerScaleY, towerScaleZ;
        public int towerSubMeshCount;
        public bool hiddenIsActive;
        public string probeLabel;
        public int probeMagicNumber;
        public int probeItemsLength;
        public int probeItemFirst;
        public int probeItemLast;
        public string probeLinkedMaterialName;
        public bool boxIsTrigger;
        public float boxSizeX;
        public float sphereRadius;
        public float capsuleHeight;
        public bool meshColliderHasMesh;
        public bool meshColliderConvex;
        public int auxiliaryColliderMeshesImported;
        public int lodCount;
        public float lod0Height;
        public bool lod0HasMesh;
        public bool lodSharedHasMesh;
        public bool lod1HasMesh;
        public bool terrainHasMesh;
        public float terrainBoundsSizeY;
        public bool sceneSmrHasMesh;
        public int sceneSmrBindposeCount;
        public int sceneSmrBoneCount;
        public string sceneSmrBone0Name;
        public string sceneSmrBone1Name;
        public string sceneSmrRootBoneName;
        public bool prefabLoaded;
        public int prefabSmrBindposeCount;
        public int prefabSmrBoneCount;
    }

    public static void Run()
    {
        var rep = new Report();
        rep.scriptGuid = AssetDatabase.AssetPathToGUID("Assets/Scripts/RoundtripProbeBehaviour.cs");
        rep.prefabGuid = AssetDatabase.AssetPathToGUID("Assets/Prefabs/KnightRigRoot.prefab");

        foreach (var guid in AssetDatabase.FindAssets("", new[] { "Assets/Meshes" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
            if (!AssetDatabase.LoadAllAssetsAtPath(p).OfType<Mesh>().Any()) rep.missingFbxMeshes++;
        }

        foreach (var guid in AssetDatabase.FindAssets("", new[] { "Assets/Colliders" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) continue;
            if (AssetDatabase.LoadAllAssetsAtPath(p).OfType<Mesh>().Any()) rep.auxiliaryColliderMeshesImported++;
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Mat_Roundtrip_Urp.mat");
        if (mat != null)
        {
            rep.materialHasMainTex = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null;
            rep.materialHasBumpMap = mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null;
        }

        var scene = EditorSceneManager.OpenScene("Assets/Scenes/RoundtripTestScene.unity", OpenSceneMode.Single);
        var allGos = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList();
        foreach (var go in allGos) rep.missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);

        var tower = allGos.First(g => g.name == "TowerNode");
        rep.towerPosX = tower.transform.localPosition.x;
        rep.towerPosY = tower.transform.localPosition.y;
        rep.towerPosZ = tower.transform.localPosition.z;
        rep.towerScaleX = tower.transform.localScale.x;
        rep.towerScaleY = tower.transform.localScale.y;
        rep.towerScaleZ = tower.transform.localScale.z;
        var towerMf = tower.GetComponent<MeshFilter>();
        rep.towerSubMeshCount = (towerMf != null && towerMf.sharedMesh != null) ? towerMf.sharedMesh.subMeshCount : 0;

        var probe = tower.GetComponent<RoundtripProbeBehaviour>();
        if (probe != null)
        {
            rep.probeLabel = probe.label ?? "";
            rep.probeMagicNumber = probe.magicNumber;
            rep.probeItemsLength = probe.items != null ? probe.items.Length : 0;
            rep.probeItemFirst = probe.items != null && probe.items.Length > 0 ? probe.items[0] : -1;
            rep.probeItemLast = probe.items != null && probe.items.Length > 0 ? probe.items[probe.items.Length - 1] : -1;
            rep.probeLinkedMaterialName = probe.linkedMaterial != null ? probe.linkedMaterial.name : "";
        }

        rep.hiddenIsActive = allGos.First(g => g.name == "HiddenProxy").activeSelf;

        var col = allGos.First(g => g.name == "CollidersNode");
        var box = col.GetComponent<BoxCollider>();
        if (box != null) { rep.boxIsTrigger = box.isTrigger; rep.boxSizeX = box.size.x; }
        var sph = col.GetComponent<SphereCollider>();
        if (sph != null) rep.sphereRadius = sph.radius;
        var cap = col.GetComponent<CapsuleCollider>();
        if (cap != null) rep.capsuleHeight = cap.height;
        var mc = col.GetComponent<MeshCollider>();
        if (mc != null) { rep.meshColliderHasMesh = mc.sharedMesh != null; rep.meshColliderConvex = mc.convex; }

        var lodNode = allGos.First(g => g.name == "LodTreeNode");
        var lg = lodNode.GetComponent<LODGroup>();
        if (lg != null)
        {
            var lods = lg.GetLODs();
            rep.lodCount = lods.Length;
            if (lods.Length > 0) rep.lod0Height = lods[0].screenRelativeTransitionHeight;
        }
        var mfLod0 = allGos.First(g => g.name == "LodTree_LOD0").GetComponent<MeshFilter>();
        rep.lod0HasMesh = mfLod0 != null && mfLod0.sharedMesh != null;
        var mfShared = allGos.First(g => g.name == "LodTree_Shared").GetComponent<MeshFilter>();
        rep.lodSharedHasMesh = mfShared != null && mfShared.sharedMesh != null;
        var mfLod1 = allGos.First(g => g.name == "LodTree_LOD1").GetComponent<MeshFilter>();
        rep.lod1HasMesh = mfLod1 != null && mfLod1.sharedMesh != null;

        var terrainNode = allGos.First(g => g.name == "HillTerrainNode");
        var tMf = terrainNode.GetComponent<MeshFilter>();
        rep.terrainHasMesh = tMf != null && tMf.sharedMesh != null;
        if (rep.terrainHasMesh) rep.terrainBoundsSizeY = tMf.sharedMesh.bounds.size.y;

        var smr = allGos.First(g => g.name == "KnightSkinnedMesh").GetComponent<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null)
        {
            rep.sceneSmrHasMesh = true;
            rep.sceneSmrBindposeCount = smr.sharedMesh.bindposes != null ? smr.sharedMesh.bindposes.Length : 0;
            rep.sceneSmrBoneCount = smr.bones != null ? smr.bones.Length : 0;
            if (smr.bones != null && smr.bones.Length >= 2)
            {
                rep.sceneSmrBone0Name = smr.bones[0] != null ? smr.bones[0].name : "";
                rep.sceneSmrBone1Name = smr.bones[1] != null ? smr.bones[1].name : "";
            }
            rep.sceneSmrRootBoneName = smr.rootBone != null ? smr.rootBone.name : "";
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/KnightRigRoot.prefab");
        rep.prefabLoaded = prefab != null;
        var prefabSmr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
        if (prefabSmr != null && prefabSmr.sharedMesh != null)
        {
            rep.prefabSmrBindposeCount = prefabSmr.sharedMesh.bindposes != null ? prefabSmr.sharedMesh.bindposes.Length : 0;
            rep.prefabSmrBoneCount = prefabSmr.bones != null ? prefabSmr.bones.Length : 0;
        }

        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        File.WriteAllText(Path.Combine(projectRoot, "roundtrip_report.json"), JsonUtility.ToJson(rep, true));
    }
}
