using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static AssetHandle? GetSemanticHandleFromPPtr(SerializedFile sourceFile, PPtr<AssetStudio.Object> pptr)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null || pptr == null || pptr.IsNull)
            {
                return null;
            }

            if (pptr.TryGetAssetsFile(out var targetFile))
            {
                var handle = sourceFile.assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID));
                if (handle != null)
                {
                    return handle;
                }
            }

            if (pptr.m_FileID == 0)
            {
                return sourceFile.assetsManager.ProjectIndex.GetHandle(AssetHandle.BuildUniqueID(sourceFile, pptr.m_PathID));
            }

            return FindSemanticHandleForPPtr(sourceFile, pptr.m_FileID, pptr.m_PathID, expectedType: null);
        }

        private static Transform? ResolveTransformForGameObjectBackground(SerializedFile sourceFile, GameObject gameObject)
        {
            if (gameObject.m_Transform != null)
            {
                return gameObject.m_Transform;
            }

            if (gameObject.m_Components == null)
            {
                return null;
            }

            foreach (var componentPtr in gameObject.m_Components)
            {
                Component? component = null;
                if (componentPtr.TryGet(out var resolvedComponent))
                {
                    component = resolvedComponent;
                }
                else if (componentPtr.m_FileID == 0)
                {
                    component = ResolveObjectBackground(sourceFile, componentPtr.m_PathID) as Component;
                }

                if (component is Transform transform)
                {
                    return transform;
                }
            }

            return null;
        }

        private static Transform? ResolveTransformBackground(SerializedFile sourceFile, PPtr<Transform> pptr)
        {
            if (pptr.TryGet(out var transform))
            {
                return transform;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Transform : null;
        }

        private static Avatar? ResolveAvatarBackground(SerializedFile sourceFile, PPtr<Avatar> pptr)
        {
            if (pptr.TryGet(out var avatar))
            {
                return avatar;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Avatar : null;
        }

        private static RuntimeAnimatorController? ResolveRuntimeAnimatorControllerBackground(
            SerializedFile sourceFile,
            PPtr<RuntimeAnimatorController> pptr)
        {
            if (pptr.TryGet(out var controller))
            {
                return controller;
            }

            return pptr.m_FileID == 0
                ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as RuntimeAnimatorController
                : null;
        }

        private static string GetSemanticGameObjectId(
            SerializedFile sourceFile,
            PPtr<GameObject> gameObjectPtr,
            GameObject? gameObject)
        {
            var gameObjectId = GetSemanticAssetId(gameObject);
            if (!string.IsNullOrEmpty(gameObjectId))
            {
                return gameObjectId;
            }

            return GetSemanticAssetIdFromPPtr(sourceFile, gameObjectPtr, ClassIDType.GameObject);
        }

        private static GameObject? ResolveGameObjectBackground(SerializedFile sourceFile, PPtr<GameObject> pptr)
        {
            if (pptr.TryGet(out var go))
            {
                return go;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as GameObject : null;
        }

        private static Mesh? ResolveMeshBackground(SerializedFile sourceFile, PPtr<Mesh> pptr)
        {
            if (pptr.TryGet(out var mesh))
            {
                return mesh;
            }

            return pptr.m_FileID == 0 ? ResolveObjectBackground(sourceFile, pptr.m_PathID) as Mesh : null;
        }

        private static Material? ResolveRendererMaterialBackground(PPtr<Material> pptr)
        {
            if (pptr.TryGet(out var material))
            {
                return material;
            }

            return null;
        }

        private static AssetStudio.Object? ResolveObjectBackground(SerializedFile sourceFile, long pathID)
        {
            lock (sourceFile)
            {
                if (sourceFile.ObjectsDic.TryGetValue(pathID, out var obj))
                {
                    return obj;
                }
            }

            return null;
        }

        private static string GetSemanticAssetId(AssetStudio.Object? asset)
        {
            return asset?.assetsFile != null
                ? AssetHandle.BuildUniqueID(asset.assetsFile, asset.m_PathID)
                : string.Empty;
        }

        private static string GetSemanticAssetIdFromPPtr<T>(SerializedFile sourceFile, PPtr<T> pptr, ClassIDType expectedType)
            where T : AssetStudio.Object
        {
            if (sourceFile == null || pptr == null || pptr.IsNull)
            {
                return string.Empty;
            }

            if (pptr.TryGetAssetsFile(out var targetFile))
            {
                return AssetHandle.BuildUniqueID(targetFile, pptr.m_PathID);
            }

            if (pptr.m_FileID == 0)
            {
                return AssetHandle.BuildUniqueID(sourceFile, pptr.m_PathID);
            }

            var handle = FindSemanticHandleForPPtr(sourceFile, pptr.m_FileID, pptr.m_PathID, expectedType);
            return handle?.UniqueID ?? string.Empty;
        }

        private static AssetHandle? FindSemanticHandleForPPtr(SerializedFile sourceFile, int fileId, long pathId, ClassIDType? expectedType)
        {
            if (sourceFile?.assetsManager?.ProjectIndex == null
                || sourceFile.m_Externals == null
                || fileId <= 0
                || fileId - 1 >= sourceFile.m_Externals.Count)
            {
                return null;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var external = sourceFile.m_Externals[fileId - 1];
            if (external == null)
            {
                return null;
            }

            AddSemanticFileNameCandidate(candidates, external.fileName);
            AddSemanticFileNameCandidate(candidates, external.pathName);

            return candidates
                .SelectMany(fileName => sourceFile.assetsManager.ProjectIndex.GetHandlesForFile(fileName))
                .Where(candidate => candidate.PathID == pathId && (expectedType == null || candidate.Type == expectedType.Value))
                .GroupBy(candidate => candidate.UniqueID, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(candidate => ScoreSemanticHandleMatch(candidate, external, sourceFile))
                .FirstOrDefault();
        }

        private static int ScoreSemanticHandleMatch(AssetHandle handle, FileIdentifier external, SerializedFile sourceFile)
        {
            var score = 0;
            if (IsSameSemanticSource(handle.OriginalPath, external.pathName)
                || IsSameSemanticSource(handle.SourceFile?.originalPath, external.pathName)
                || IsSameSemanticSource(handle.SourceFile?.fullName, external.pathName))
            {
                score += 100;
            }

            if (IsSameSemanticSource(handle.SerializedFileName, external.fileName)
                || IsSameSemanticSource(handle.SourceFile?.fileName, external.fileName))
            {
                score += 50;
            }

            var externalPathFileName = GetSemanticFileName(external.pathName);
            if (!string.IsNullOrEmpty(externalPathFileName)
                && (IsSameSemanticSource(GetSemanticFileName(handle.OriginalPath), externalPathFileName)
                    || IsSameSemanticSource(handle.SerializedFileName, externalPathFileName)
                    || IsSameSemanticSource(handle.SourceFile?.fileName, externalPathFileName)))
            {
                score += 25;
            }

            var sourceDirectory = Path.GetDirectoryName(sourceFile.originalPath ?? sourceFile.fullName ?? string.Empty);
            var handleDirectory = Path.GetDirectoryName(handle.OriginalPath ?? handle.SourceFile?.originalPath ?? string.Empty);
            if (!string.IsNullOrEmpty(sourceDirectory)
                && !string.IsNullOrEmpty(handleDirectory)
                && string.Equals(sourceDirectory, handleDirectory, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            return score;
        }

        private static string GetSemanticFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFileName(normalized);
        }

        private static bool IsSameSemanticSource(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void AddSemanticFileNameCandidate(HashSet<string> candidates, string? fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return;
            }

            candidates.Add(fileNameOrPath);
            var normalized = fileNameOrPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fileName = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(fileName);
            }
        }

        private static void AddRendererMeshRelation(
            SemanticAssetRelations relations,
            string meshId,
            Component renderer,
            string rendererType,
            GameObject? gameObject,
            string description)
        {
            var rendererId = GetSemanticAssetId(renderer);
            if (string.IsNullOrEmpty(meshId) || string.IsNullOrEmpty(rendererId))
            {
                return;
            }

            relations.MeshRenderers.Add(new SemanticMeshRendererRelation(
                meshId,
                rendererId,
                rendererType,
                GetSemanticAssetId(gameObject),
                gameObject?.m_Name ?? string.Empty,
                description ?? string.Empty));
        }

        private static void AddAssetEdge(
            SemanticAssetRelations relations,
            AssetStudio.Object source,
            string edgeKind,
            string slotName,
            int slotIndex,
            string targetId,
            int targetFileId,
            long targetPathId)
        {
            var sourceId = GetSemanticAssetId(source);
            if (string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                sourceId,
                edgeKind,
                slotName,
                slotIndex,
                targetId ?? string.Empty,
                0,
                source.m_PathID,
                targetFileId,
                targetPathId,
                !string.IsNullOrEmpty(targetId)));
        }

        private static void AddAssetEdgeById(
            SemanticAssetRelations relations,
            string sourceId,
            string edgeKind,
            string slotName,
            int slotIndex,
            string targetId,
            int sourceFileId,
            long sourcePathId,
            int targetFileId,
            long targetPathId,
            bool isResolved)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            relations.AssetEdges.Add(new SemanticAssetEdgeRelation(
                sourceId,
                edgeKind,
                slotName,
                slotIndex,
                targetId ?? string.Empty,
                sourceFileId,
                sourcePathId,
                targetFileId,
                targetPathId,
                isResolved && !string.IsNullOrEmpty(targetId)));
        }
    }
}
