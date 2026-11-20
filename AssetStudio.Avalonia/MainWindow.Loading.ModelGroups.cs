using System;
using System.Collections.Generic;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private static Dictionary<string, List<MeshFilterSemanticBinding>> BuildMeshFilterBindingsByGameObject(
            SerializedFile sourceFile,
            IEnumerable<AssetStudio.Object> objects)
        {
            var result = new Dictionary<string, List<MeshFilterSemanticBinding>>(StringComparer.Ordinal);
            foreach (var meshFilter in objects.OfType<MeshFilter>())
            {
                var gameObject = ResolveGameObjectBackground(sourceFile, meshFilter.m_GameObject);
                var gameObjectId = GetSemanticGameObjectId(sourceFile, meshFilter.m_GameObject, gameObject);
                if (string.IsNullOrEmpty(gameObjectId))
                {
                    continue;
                }

                var mesh = ResolveMeshBackground(sourceFile, meshFilter.m_Mesh);
                var meshId = GetSemanticAssetId(mesh);
                if (string.IsNullOrEmpty(meshId))
                {
                    meshId = GetSemanticAssetIdFromPPtr(sourceFile, meshFilter.m_Mesh, ClassIDType.Mesh);
                }

                if (string.IsNullOrEmpty(meshId))
                {
                    continue;
                }

                if (!result.TryGetValue(gameObjectId, out var bindings))
                {
                    bindings = new List<MeshFilterSemanticBinding>();
                    result[gameObjectId] = bindings;
                }

                bindings.Add(new MeshFilterSemanticBinding(meshFilter, mesh, meshId, gameObject));
            }

            return result;
        }

        private static List<SkinnedMeshSemanticBinding> BuildSkinnedMeshBindings(
            SerializedFile sourceFile,
            IEnumerable<AssetStudio.Object> objects)
        {
            var result = new List<SkinnedMeshSemanticBinding>();
            foreach (var renderer in objects.OfType<SkinnedMeshRenderer>())
            {
                var mesh = ResolveMeshBackground(sourceFile, renderer.m_Mesh);
                var meshId = GetSemanticAssetId(mesh);
                if (string.IsNullOrEmpty(meshId))
                {
                    meshId = GetSemanticAssetIdFromPPtr(sourceFile, renderer.m_Mesh, ClassIDType.Mesh);
                }

                if (string.IsNullOrEmpty(meshId))
                {
                    continue;
                }

                result.Add(new SkinnedMeshSemanticBinding(
                    renderer,
                    mesh,
                    meshId,
                    ResolveGameObjectBackground(sourceFile, renderer.m_GameObject),
                    renderer.m_Mesh.m_FileID,
                    renderer.m_Mesh.m_PathID));
            }

            return result;
        }

        private static List<RendererMeshSemanticBinding> BuildMeshRendererBindings(
            SerializedFile sourceFile,
            IEnumerable<AssetStudio.Object> objects,
            IReadOnlyDictionary<string, List<MeshFilterSemanticBinding>> meshFiltersByGameObjectId)
        {
            var result = new List<RendererMeshSemanticBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in objects.OfType<MeshRenderer>())
            {
                var gameObject = ResolveGameObjectBackground(sourceFile, renderer.m_GameObject);
                var gameObjectId = GetSemanticGameObjectId(sourceFile, renderer.m_GameObject, gameObject);
                if (string.IsNullOrEmpty(gameObjectId)
                    || !meshFiltersByGameObjectId.TryGetValue(gameObjectId, out var meshFilterBindings))
                {
                    continue;
                }

                var rendererId = GetSemanticAssetId(renderer);
                if (string.IsNullOrEmpty(rendererId))
                {
                    continue;
                }

                foreach (var binding in meshFilterBindings)
                {
                    if (string.IsNullOrEmpty(binding.MeshId))
                    {
                        continue;
                    }

                    var key = $"{rendererId}\u001f{binding.MeshId}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    result.Add(new RendererMeshSemanticBinding(
                        renderer,
                        rendererId,
                        "MeshRenderer",
                        binding.Mesh,
                        binding.MeshId,
                        binding.GameObject ?? gameObject,
                        binding.MeshFilter.m_Mesh.m_FileID,
                        binding.MeshFilter.m_Mesh.m_PathID,
                        100,
                        "MeshRenderer GameObject MeshFilter"));
                }
            }

            return result;
        }

        private static void AddSceneObjectModelGroupRelations(
            SerializedFile sourceFile,
            SemanticAssetRelations relations,
            IEnumerable<RendererMeshSemanticBinding> rendererBindings)
        {
            var bindings = rendererBindings
                .Where(binding => binding.GameObject != null
                    && !string.IsNullOrEmpty(binding.MeshId)
                    && !string.IsNullOrEmpty(binding.RendererId))
                .ToList();
            if (bindings.Count < 2)
            {
                return;
            }

            var ancestryByGameObjectId = new Dictionary<string, List<GameObjectHierarchyEntry>>(StringComparer.Ordinal);
            var rendererCountByAncestorId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var ancestry = GetGameObjectAncestry(sourceFile, binding.GameObject!);
                if (ancestry.Count == 0)
                {
                    continue;
                }

                var ownId = ancestry[0].GameObjectId;
                ancestryByGameObjectId[ownId] = ancestry;
                foreach (var ancestor in ancestry)
                {
                    rendererCountByAncestorId.TryGetValue(ancestor.GameObjectId, out var count);
                    rendererCountByAncestorId[ancestor.GameObjectId] = count + 1;
                }
            }

            var groups = new Dictionary<string, SceneObjectModelGroupBuilder>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                var ownId = binding.GameObject != null ? GetSemanticAssetId(binding.GameObject) : string.Empty;
                if (string.IsNullOrEmpty(ownId)
                    || !ancestryByGameObjectId.TryGetValue(ownId, out var ancestry))
                {
                    continue;
                }

                var groupRoot = ancestry.FirstOrDefault(entry =>
                    rendererCountByAncestorId.TryGetValue(entry.GameObjectId, out var count) && count > 1);
                if (groupRoot.GameObject == null)
                {
                    continue;
                }

                if (!groups.TryGetValue(groupRoot.GameObjectId, out var group))
                {
                    group = new SceneObjectModelGroupBuilder(groupRoot);
                    groups[groupRoot.GameObjectId] = group;
                }

                group.Add(binding);
            }

            foreach (var group in groups.Values)
            {
                if (group.Bindings.Count < 2)
                {
                    continue;
                }

                var groupId = $"modelgroup:sceneobject:{group.Root.GameObjectId}";
                relations.ModelGroups.Add(new SemanticModelGroupRelation(
                    groupId,
                    "SceneObject",
                    group.Root.Name,
                    group.Root.GameObjectId,
                    group.Root.Name,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    sourceFile.fileName ?? string.Empty,
                    group.Confidence,
                    group.ConfidenceReason));

                var slotIndex = 0;
                var seenParts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in group.Bindings)
                {
                    var partKey = $"{binding.MeshId}\u001f{binding.RendererId}";
                    if (!seenParts.Add(partKey))
                    {
                        continue;
                    }

                    var gameObjectId = binding.GameObject != null
                        ? GetSemanticAssetId(binding.GameObject)
                        : string.Empty;
                    relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                        groupId,
                        binding.MeshId,
                        binding.RendererId,
                        binding.RendererType,
                        gameObjectId,
                        binding.GameObject?.m_Name ?? string.Empty,
                        slotIndex,
                        BuildLocalToGroupRootMatrix(sourceFile, binding.GameObject, group.Root.GameObjectId),
                        binding.Confidence,
                        binding.ConfidenceReason));
                    slotIndex++;
                }
            }
        }

        private static List<GameObjectHierarchyEntry> GetGameObjectAncestry(SerializedFile sourceFile, GameObject gameObject)
        {
            var result = new List<GameObjectHierarchyEntry>();
            var visited = new HashSet<long>();
            GameObject? current = gameObject;
            for (var depth = 0; current != null && depth < 256; depth++)
            {
                if (!visited.Add(current.m_PathID))
                {
                    break;
                }

                var currentId = GetSemanticAssetId(current);
                if (string.IsNullOrEmpty(currentId))
                {
                    break;
                }

                result.Add(new GameObjectHierarchyEntry(current, currentId, current.m_Name ?? string.Empty, depth));

                var transform = ResolveTransformForGameObjectBackground(sourceFile, current);
                if (transform?.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                var parentTransform = ResolveTransformBackground(sourceFile, transform.m_Father);
                if (parentTransform == null)
                {
                    break;
                }

                current = ResolveGameObjectBackground(sourceFile, parentTransform.m_GameObject);
            }

            return result;
        }

        private static bool IsSameOrDescendantGameObject(SerializedFile sourceFile, GameObject child, GameObject ancestor)
        {
            var ancestorPathId = ancestor.m_PathID;
            GameObject? current = child;
            var visited = new HashSet<long>();

            for (var depth = 0; current != null && depth < 256; depth++)
            {
                if (!visited.Add(current.m_PathID))
                {
                    return false;
                }

                if (current.m_PathID == ancestorPathId)
                {
                    return true;
                }

                var transform = ResolveTransformForGameObjectBackground(sourceFile, current);
                if (transform?.m_Father == null || transform.m_Father.IsNull)
                {
                    return false;
                }

                var parentTransform = ResolveTransformBackground(sourceFile, transform.m_Father);
                if (parentTransform == null)
                {
                    return false;
                }

                current = ResolveGameObjectBackground(sourceFile, parentTransform.m_GameObject);
            }

            return false;
        }

        private static float[]? BuildLocalToWorldMatrix(SerializedFile sourceFile, GameObject? gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var transforms = new List<Transform>();
            var visited = new HashSet<long>();
            var transform = ResolveTransformForGameObjectBackground(sourceFile, gameObject);
            for (var depth = 0; transform != null && depth < 256; depth++)
            {
                if (!visited.Add(transform.m_PathID))
                {
                    break;
                }

                transforms.Add(transform);
                if (transform.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                transform = ResolveTransformBackground(sourceFile, transform.m_Father);
            }

            if (transforms.Count == 0)
            {
                return null;
            }

            var world = CreateIdentityMatrix();
            for (var i = transforms.Count - 1; i >= 0; i--)
            {
                world = world * BuildLocalMatrix(transforms[i]);
            }

            return MatrixToRowMajorArray(world, omitIdentity: true);
        }

        private static float[]? BuildLocalToGroupRootMatrix(
            SerializedFile sourceFile,
            GameObject? gameObject,
            string rootGameObjectId)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(rootGameObjectId))
            {
                return BuildLocalToWorldMatrix(sourceFile, gameObject);
            }

            var transforms = new List<Transform>();
            var visited = new HashSet<long>();
            var transform = ResolveTransformForGameObjectBackground(sourceFile, gameObject);
            var reachedGroupRoot = false;
            for (var depth = 0; transform != null && depth < 256; depth++)
            {
                if (!visited.Add(transform.m_PathID))
                {
                    break;
                }

                var transformGameObject = ResolveGameObjectBackground(sourceFile, transform.m_GameObject);
                var transformGameObjectId = transformGameObject != null
                    ? GetSemanticAssetId(transformGameObject)
                    : string.Empty;
                if (string.Equals(transformGameObjectId, rootGameObjectId, StringComparison.Ordinal))
                {
                    reachedGroupRoot = true;
                    break;
                }

                transforms.Add(transform);
                if (transform.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                transform = ResolveTransformBackground(sourceFile, transform.m_Father);
            }

            if (!reachedGroupRoot)
            {
                return BuildLocalToWorldMatrix(sourceFile, gameObject);
            }

            var localToRoot = CreateIdentityMatrix();
            for (var i = transforms.Count - 1; i >= 0; i--)
            {
                localToRoot = localToRoot * BuildLocalMatrix(transforms[i]);
            }

            return MatrixToRowMajorArray(localToRoot, omitIdentity: true);
        }

        private static Matrix4x4 BuildLocalMatrix(Transform transform)
        {
            return Matrix4x4.Translate(transform.m_LocalPosition)
                * Matrix4x4.Rotate(transform.m_LocalRotation)
                * Matrix4x4.Scale(transform.m_LocalScale);
        }

        private static Matrix4x4 CreateIdentityMatrix()
        {
            var matrix = new Matrix4x4();
            matrix.M00 = 1f;
            matrix.M11 = 1f;
            matrix.M22 = 1f;
            matrix.M33 = 1f;
            return matrix;
        }

        private static float[]? MatrixToRowMajorArray(Matrix4x4 matrix, bool omitIdentity)
        {
            var values = new[]
            {
                matrix.M00, matrix.M01, matrix.M02, matrix.M03,
                matrix.M10, matrix.M11, matrix.M12, matrix.M13,
                matrix.M20, matrix.M21, matrix.M22, matrix.M23,
                matrix.M30, matrix.M31, matrix.M32, matrix.M33
            };

            if (!omitIdentity)
            {
                return values;
            }

            var identity = new[]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f
            };
            for (var i = 0; i < values.Length; i++)
            {
                if (Math.Abs(values[i] - identity[i]) > 0.000001f)
                {
                    return values;
                }
            }

            return null;
        }

        private readonly struct MeshFilterSemanticBinding
        {
            public MeshFilterSemanticBinding(MeshFilter meshFilter, Mesh? mesh, string meshId, GameObject? gameObject)
            {
                MeshFilter = meshFilter;
                Mesh = mesh;
                MeshId = meshId;
                GameObject = gameObject;
            }

            public MeshFilter MeshFilter { get; }
            public Mesh? Mesh { get; }
            public string MeshId { get; }
            public GameObject? GameObject { get; }
        }

        private readonly struct SkinnedMeshSemanticBinding
        {
            public SkinnedMeshSemanticBinding(
                SkinnedMeshRenderer renderer,
                Mesh? mesh,
                string meshId,
                GameObject? gameObject,
                int meshFileId,
                long meshPathId)
            {
                Renderer = renderer;
                Mesh = mesh;
                MeshId = meshId;
                GameObject = gameObject;
                MeshFileId = meshFileId;
                MeshPathId = meshPathId;
            }

            public SkinnedMeshRenderer Renderer { get; }
            public Mesh? Mesh { get; }
            public string MeshId { get; }
            public GameObject? GameObject { get; }
            public int MeshFileId { get; }
            public long MeshPathId { get; }
        }

        private readonly struct RendererMeshSemanticBinding
        {
            public RendererMeshSemanticBinding(
                Renderer renderer,
                string rendererId,
                string rendererType,
                Mesh? mesh,
                string meshId,
                GameObject? gameObject,
                int meshFileId,
                long meshPathId,
                int confidence,
                string confidenceReason)
            {
                Renderer = renderer;
                RendererId = rendererId;
                RendererType = rendererType;
                Mesh = mesh;
                MeshId = meshId;
                GameObject = gameObject;
                MeshFileId = meshFileId;
                MeshPathId = meshPathId;
                Confidence = confidence;
                ConfidenceReason = confidenceReason;
            }

            public Renderer Renderer { get; }
            public string RendererId { get; }
            public string RendererType { get; }
            public Mesh? Mesh { get; }
            public string MeshId { get; }
            public GameObject? GameObject { get; }
            public int MeshFileId { get; }
            public long MeshPathId { get; }
            public int Confidence { get; }
            public string ConfidenceReason { get; }

            public static RendererMeshSemanticBinding FromSkinned(SkinnedMeshSemanticBinding binding)
            {
                return new RendererMeshSemanticBinding(
                    binding.Renderer,
                    GetSemanticAssetId(binding.Renderer),
                    "SkinnedMeshRenderer",
                    binding.Mesh,
                    binding.MeshId,
                    binding.GameObject,
                    binding.MeshFileId,
                    binding.MeshPathId,
                    100,
                    "SkinnedMeshRenderer hierarchy");
            }
        }

        private readonly struct GameObjectHierarchyEntry
        {
            public GameObjectHierarchyEntry(GameObject gameObject, string gameObjectId, string name, int depth)
            {
                GameObject = gameObject;
                GameObjectId = gameObjectId;
                Name = name;
                Depth = depth;
            }

            public GameObject GameObject { get; }
            public string GameObjectId { get; }
            public string Name { get; }
            public int Depth { get; }
        }

        private sealed class SceneObjectModelGroupBuilder
        {
            public SceneObjectModelGroupBuilder(GameObjectHierarchyEntry root)
            {
                Root = root;
            }

            public GameObjectHierarchyEntry Root { get; }
            public List<RendererMeshSemanticBinding> Bindings { get; } = new();
            public int Confidence => Bindings.Count == 0 ? 0 : Bindings.Min(binding => binding.Confidence);
            public string ConfidenceReason => Bindings.Count == 0
                ? string.Empty
                : Bindings.All(binding => binding.ConfidenceReason == Bindings[0].ConfidenceReason)
                    ? Bindings[0].ConfidenceReason
                    : "Mixed renderer hierarchy evidence";

            public void Add(RendererMeshSemanticBinding binding)
            {
                Bindings.Add(binding);
            }
        }
    }
}
