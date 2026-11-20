using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia
{
    using Vector2 = OpenTK.Mathematics.Vector2;
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public partial class MeshPreviewControl
    {
        public void SetMesh(Mesh m_Mesh, Vector2[]? uvs = null, List<byte[]?>? subMeshTextures = null, List<int>? subMeshTexWidths = null, List<int>? subMeshTexHeights = null)
        {
            SetMesh(m_Mesh, uvs, subMeshTextures, subMeshTexWidths, subMeshTexHeights, resetMaterialOverrides: true);
        }

        public void SetMeshGroup(IReadOnlyList<Mesh> meshes, IReadOnlyList<Vector2[]?>? uvs = null, List<byte[]?>? subMeshTextures = null, List<int>? subMeshTexWidths = null, List<int>? subMeshTexHeights = null)
        {
            SetMeshGroup(meshes, uvs, null, subMeshTextures, subMeshTexWidths, subMeshTexHeights, resetMaterialOverrides: true);
        }

        public void SetMeshGroup(IReadOnlyList<Mesh> meshes, IReadOnlyList<Vector2[]?>? uvs, IReadOnlyList<float[]?>? transforms, List<byte[]?>? subMeshTextures = null, List<int>? subMeshTexWidths = null, List<int>? subMeshTexHeights = null)
        {
            SetMeshGroup(meshes, uvs, transforms, subMeshTextures, subMeshTexWidths, subMeshTexHeights, resetMaterialOverrides: true);
        }

        private void SetMesh(Mesh m_Mesh, Vector2[]? uvs, List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights, bool resetMaterialOverrides)
        {
            ClearAvatarPreviewState();
            meshGroupPreviewSources = null;
            meshGroupPreviewSourceUvs = null;
            meshGroupPreviewSourceTransforms = null;
            meshGroupPreviewSourceSubMeshTextures = null;
            meshGroupPreviewSourceSubMeshTexWidths = null;
            meshGroupPreviewSourceSubMeshTexHeights = null;
            ApplyMaterialResetState(resetMaterialOverrides);

            meshPreviewSource = m_Mesh;
            meshPreviewSourceUvs = uvs;
            meshPreviewSourceSubMeshTextures = subMeshTextures;
            meshPreviewSourceSubMeshTexWidths = subMeshTexWidths;
            meshPreviewSourceSubMeshTexHeights = subMeshTexHeights;
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var m_Vertices = m_Mesh.m_Vertices;
            var m_VertexCount = m_Mesh.m_VertexCount;
            var m_Indices = m_Mesh.m_Indices;
            var m_Normals = m_Mesh.m_Normals;
            var m_Colors = m_Mesh.m_Colors;
            var densityPercent = previewMeshDensityPercent;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (m_Vertices == null || m_Vertices.Length == 0)
                {
                    return;
                }

                Vector3[] localVertexData;
                int[] localIndiceData;
                Vector3[]? localNormalData;
                Vector3[] localNormal2Data;
                Vector4[] localColorData;
                Vector2[]? localUvData;
                int[]? localPreviewSubMeshIndexCounts;
                Matrix4 localModelMatrixData;

                var cachedGeometry = LoadMeshPreviewGeometryCache?.Invoke(m_Mesh, densityPercent);
                if (cachedGeometry != null)
                {
                    localVertexData = cachedGeometry.Vertices;
                    localIndiceData = cachedGeometry.Indices;
                    localNormalData = cachedGeometry.Normals;
                    localNormal2Data = cachedGeometry.CalculatedNormals ?? BuildCalculatedNormals(cachedGeometry.Vertices, cachedGeometry.Indices);
                    localColorData = cachedGeometry.Colors;
                    localUvData = cachedGeometry.Uvs;
                    localPreviewSubMeshIndexCounts = cachedGeometry.SubMeshIndexCounts;
                    localModelMatrixData = cachedGeometry.ModelMatrix;
                }
                else
                {
                    localVertexData = ExtractMeshVertices(m_Vertices, m_VertexCount, out var min, out var max);
                    localIndiceData = ExtractMeshIndices(m_Indices);
                    localNormalData = ExtractMeshNormals(m_Normals, m_VertexCount);
                    localColorData = ExtractMeshColors(m_Colors, m_VertexCount);
                    localUvData = uvs ?? BuildPreviewUvData(m_Mesh.m_UV0, m_VertexCount);
                    localPreviewSubMeshIndexCounts = BuildSubMeshIndexCounts(m_Mesh, localIndiceData.Length);

                    if (densityPercent < PreviewMeshSimplifier.MaxDensityPercent - 0.01f)
                    {
                        var simplified = PreviewMeshSimplifier.Build(localVertexData, m_Indices, min, max, densityPercent, localPreviewSubMeshIndexCounts);
                        if (simplified.Indices.Length > 0)
                        {
                            localVertexData = simplified.Vertices;
                            localIndiceData = simplified.Indices;
                            localPreviewSubMeshIndexCounts = simplified.SubMeshIndexCounts;
                            localNormalData = RemapVector3Data(localNormalData, simplified.SourceVertexIndices);
                            localColorData = RemapVector4Data(localColorData, simplified.SourceVertexIndices) ?? localColorData;
                            localUvData = RemapVector2Data(localUvData, simplified.SourceVertexIndices);
                        }
                    }

                    localNormal2Data = BuildCalculatedNormals(localVertexData, localIndiceData);
                    GetRobustMeshPreviewGeometryBounds(localVertexData, out var previewMin, out var previewMax);
                    localModelMatrixData = CreatePreviewModelMatrix(previewMin, previewMax);

                    SaveMeshPreviewGeometryCache?.Invoke(m_Mesh, densityPercent, new MeshPreviewGeometryCache
                    {
                        ModelMatrix = localModelMatrixData,
                        Vertices = localVertexData,
                        Indices = localIndiceData,
                        Normals = localNormalData,
                        CalculatedNormals = localNormal2Data,
                        Colors = localColorData,
                        Uvs = localUvData,
                        SubMeshIndexCounts = localPreviewSubMeshIndexCounts
                    });
                }

                bool loadedFromCache = cachedGeometry != null;
                bool hasSubMeshTextures = QueueSubMeshTexturesIfNeeded(resetMaterialOverrides, subMeshTextures, subMeshTexWidths, subMeshTexHeights);

                PostLoadedMeshGeometry(
                    currentLoadId,
                    resetMaterialOverrides,
                    localModelMatrixData,
                    localVertexData,
                    localIndiceData,
                    localNormalData,
                    localNormal2Data,
                    localColorData,
                    localUvData,
                    localPreviewSubMeshIndexCounts,
                    hasSubMeshTextures,
                    loadedFromCache);
            });
        }

        private void SetMeshGroup(IReadOnlyList<Mesh> meshes, IReadOnlyList<Vector2[]?>? uvs, IReadOnlyList<float[]?>? transforms, List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights, bool resetMaterialOverrides)
        {
            ClearAvatarPreviewState();
            meshPreviewSource = null;
            meshPreviewSourceUvs = null;
            meshPreviewSourceSubMeshTextures = null;
            meshPreviewSourceSubMeshTexWidths = null;
            meshPreviewSourceSubMeshTexHeights = null;

            var meshSources = meshes
                .Where(mesh => mesh != null)
                .ToArray();
            meshGroupPreviewSources = meshSources;
            meshGroupPreviewSourceUvs = uvs;
            meshGroupPreviewSourceTransforms = transforms;
            meshGroupPreviewSourceSubMeshTextures = subMeshTextures;
            meshGroupPreviewSourceSubMeshTexWidths = subMeshTexWidths;
            meshGroupPreviewSourceSubMeshTexHeights = subMeshTexHeights;
            ApplyMaterialResetState(resetMaterialOverrides);

            if (meshSources.Length == 0)
            {
                return;
            }

            var uvSources = uvs?.ToArray();
            var transformSources = transforms?.ToArray();
            var densityPercent = previewMeshDensityPercent;
            int currentLoadId = ++meshLoadCounter;

            System.Threading.Tasks.Task.Run(() =>
            {
                var parts = new List<MeshPreviewPartGeometry>();
                var combinedSubMeshCounts = new List<int>();
                var totalVertexCount = 0;
                var totalIndexCount = 0;
                var hasBounds = false;
                var min = Vector3.Zero;
                var max = Vector3.Zero;

                for (var meshIndex = 0; meshIndex < meshSources.Length; meshIndex++)
                {
                    var mesh = meshSources[meshIndex];
                    mesh.EnsureProcessed();
                    if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0 || mesh.m_Indices == null || mesh.m_Indices.Count == 0)
                    {
                        continue;
                    }

                    var meshUvs = uvSources != null && meshIndex < uvSources.Length ? uvSources[meshIndex] : null;
                    var part = BuildMeshPreviewPartGeometry(mesh, meshUvs, densityPercent);
                    var partTransform = transformSources != null && meshIndex < transformSources.Length ? transformSources[meshIndex] : null;
                    if (partTransform != null && partTransform.Length == 16)
                    {
                        part = TransformMeshPreviewPartGeometry(part, partTransform);
                    }

                    if (part.Vertices.Length == 0 || part.Indices.Length == 0)
                    {
                        continue;
                    }

                    parts.Add(part);
                    totalVertexCount += part.Vertices.Length;
                    totalIndexCount += part.Indices.Length;
                    if (part.SubMeshIndexCounts != null && part.SubMeshIndexCounts.Length > 0)
                    {
                        combinedSubMeshCounts.AddRange(part.SubMeshIndexCounts);
                    }
                    else
                    {
                        combinedSubMeshCounts.Add(part.Indices.Length);
                    }

                    if (!hasBounds)
                    {
                        min = part.Min;
                        max = part.Max;
                        hasBounds = true;
                    }
                    else
                    {
                        ExpandBounds(part.Min, ref min, ref max);
                        ExpandBounds(part.Max, ref min, ref max);
                    }
                }

                if (parts.Count == 0 || !hasBounds || totalVertexCount <= 0 || totalIndexCount <= 0)
                {
                    return;
                }

                var localVertexData = new Vector3[totalVertexCount];
                var localIndiceData = new int[totalIndexCount];
                Vector3[]? localNormalData = parts.All(part => part.Normals != null && part.Normals.Length == part.Vertices.Length)
                    ? new Vector3[totalVertexCount]
                    : null;
                var localColorData = new Vector4[totalVertexCount];
                Vector2[]? localUvData = parts.Any(part => part.Uvs != null && part.Uvs.Length == part.Vertices.Length)
                    ? new Vector2[totalVertexCount]
                    : null;

                var vertexOffset = 0;
                var indexOffset = 0;
                foreach (var part in parts)
                {
                    Array.Copy(part.Vertices, 0, localVertexData, vertexOffset, part.Vertices.Length);
                    Array.Copy(part.Colors, 0, localColorData, vertexOffset, part.Colors.Length);

                    if (localNormalData != null && part.Normals != null)
                    {
                        Array.Copy(part.Normals, 0, localNormalData, vertexOffset, part.Normals.Length);
                    }

                    if (localUvData != null && part.Uvs != null)
                    {
                        Array.Copy(part.Uvs, 0, localUvData, vertexOffset, part.Uvs.Length);
                    }

                    for (var i = 0; i < part.Indices.Length; i++)
                    {
                        localIndiceData[indexOffset + i] = part.Indices[i] + vertexOffset;
                    }

                    vertexOffset += part.Vertices.Length;
                    indexOffset += part.Indices.Length;
                }

                GetRobustMeshPreviewGeometryBounds(localVertexData, out var previewMin, out var previewMax);
                var localNormal2Data = BuildCalculatedNormals(localVertexData, localIndiceData);
                var localPreviewSubMeshIndexCounts = combinedSubMeshCounts.Count > 0
                    ? combinedSubMeshCounts.ToArray()
                    : null;
                var localModelMatrixData = CreatePreviewModelMatrix(previewMin, previewMax);
                bool loadedFromCache = parts.Count > 0 && parts.All(part => part.FromCache);
                bool hasSubMeshTextures = QueueSubMeshTexturesIfNeeded(resetMaterialOverrides, subMeshTextures, subMeshTexWidths, subMeshTexHeights);

                PostLoadedMeshGeometry(
                    currentLoadId,
                    resetMaterialOverrides,
                    localModelMatrixData,
                    localVertexData,
                    localIndiceData,
                    localNormalData,
                    localNormal2Data,
                    localColorData,
                    localUvData,
                    localPreviewSubMeshIndexCounts,
                    hasSubMeshTextures,
                    loadedFromCache);
            });
        }

        private void ApplyMaterialResetState(bool resetMaterialOverrides)
        {
            if (resetMaterialOverrides)
            {
                previewMaterialMode = false;
                materialTintOverrides = null;
                keepExistingTextures = false;
                ClearPendingTextureUploads();
            }
            else
            {
                keepExistingTextures = true;
            }
        }

        private bool QueueSubMeshTexturesIfNeeded(bool resetMaterialOverrides, List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights)
        {
            bool hasSubMeshTextures = resetMaterialOverrides && subMeshTextures != null && subMeshTextures.Any(t => t != null);
            if (hasSubMeshTextures)
            {
                lock (textureLock)
                {
                    pendingSubMeshTextures = subMeshTextures;
                    pendingSubMeshTexWidths = subMeshTexWidths;
                    pendingSubMeshTexHeights = subMeshTexHeights;
                    hasPendingSubMeshTextures = true;
                }
            }

            return hasSubMeshTextures;
        }

        private void PostLoadedMeshGeometry(
            int currentLoadId,
            bool resetMaterialOverrides,
            Matrix4 localModelMatrixData,
            Vector3[] localVertexData,
            int[] localIndiceData,
            Vector3[]? localNormalData,
            Vector3[] localNormal2Data,
            Vector4[] localColorData,
            Vector2[]? localUvData,
            int[]? localPreviewSubMeshIndexCounts,
            bool hasSubMeshTextures,
            bool loadedFromCache)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (currentLoadId != meshLoadCounter)
                {
                    return;
                }

                if (resetMaterialOverrides)
                {
                    SetInitialPreviewFrame(CreateUnityAutoViewMatrix(), localModelMatrixData);
                }

                vertexData = localVertexData;
                indiceData = localIndiceData;
                normalData = localNormalData;
                normal2Data = localNormal2Data;
                colorData = localColorData;
                uvData = localUvData;
                previewSubMeshIndexCounts = localPreviewSubMeshIndexCounts;
                if (hasSubMeshTextures)
                {
                    previewMaterialMode = true;
                }
                vao = 0;
                RequestNextFrameRendering();
                MeshGeometryLoaded?.Invoke(loadedFromCache);
            });
        }

        private MeshPreviewPartGeometry BuildMeshPreviewPartGeometry(Mesh mesh, Vector2[]? uvs, float densityPercent)
        {
            var cachedGeometry = LoadMeshPreviewGeometryCache?.Invoke(mesh, densityPercent);
            if (cachedGeometry != null)
            {
                GetRobustMeshPreviewGeometryBounds(cachedGeometry.Vertices, out var cachedMin, out var cachedMax);
                return new MeshPreviewPartGeometry(
                    cachedGeometry.Vertices,
                    cachedGeometry.Indices,
                    cachedGeometry.Normals,
                    cachedGeometry.Colors,
                    cachedGeometry.Uvs,
                    cachedGeometry.SubMeshIndexCounts,
                    cachedMin,
                    cachedMax,
                    fromCache: true);
            }

            var localVertexData = ExtractMeshVertices(mesh.m_Vertices!, mesh.m_VertexCount, out var min, out var max);
            var localIndiceData = ExtractMeshIndices(mesh.m_Indices);
            var localNormalData = ExtractMeshNormals(mesh.m_Normals, mesh.m_VertexCount);
            var localColorData = ExtractMeshColors(mesh.m_Colors, mesh.m_VertexCount);
            var localUvData = uvs ?? BuildPreviewUvData(mesh.m_UV0, mesh.m_VertexCount);
            var localPreviewSubMeshIndexCounts = BuildSubMeshIndexCounts(mesh, localIndiceData.Length);

            if (densityPercent < PreviewMeshSimplifier.MaxDensityPercent - 0.01f)
            {
                var simplified = PreviewMeshSimplifier.Build(localVertexData, mesh.m_Indices, min, max, densityPercent, localPreviewSubMeshIndexCounts);
                if (simplified.Indices.Length > 0)
                {
                    localVertexData = simplified.Vertices;
                    localIndiceData = simplified.Indices;
                    localPreviewSubMeshIndexCounts = simplified.SubMeshIndexCounts;
                    localNormalData = RemapVector3Data(localNormalData, simplified.SourceVertexIndices);
                    localColorData = RemapVector4Data(localColorData, simplified.SourceVertexIndices) ?? localColorData;
                    localUvData = RemapVector2Data(localUvData, simplified.SourceVertexIndices);
                }
            }

            GetRobustMeshPreviewGeometryBounds(localVertexData, out min, out max);

            var localNormal2Data = BuildCalculatedNormals(localVertexData, localIndiceData);
            SaveMeshPreviewGeometryCache?.Invoke(mesh, densityPercent, new MeshPreviewGeometryCache
            {
                ModelMatrix = CreatePreviewModelMatrix(min, max),
                Vertices = localVertexData,
                Indices = localIndiceData,
                Normals = localNormalData,
                CalculatedNormals = localNormal2Data,
                Colors = localColorData,
                Uvs = localUvData,
                SubMeshIndexCounts = localPreviewSubMeshIndexCounts
            });

            return new MeshPreviewPartGeometry(
                localVertexData,
                localIndiceData,
                localNormalData,
                localColorData,
                localUvData,
                localPreviewSubMeshIndexCounts,
                min,
                max);
        }

        private void GenerateSphere(int latitudes, int longitudes)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();

            for (int lat = 0; lat <= latitudes; lat++)
            {
                float theta = lat * (float)Math.PI / latitudes;
                float sinTheta = (float)Math.Sin(theta);
                float cosTheta = (float)Math.Cos(theta);

                for (int lon = 0; lon <= longitudes; lon++)
                {
                    float phi = lon * 2 * (float)Math.PI / longitudes;
                    float sinPhi = (float)Math.Sin(phi);
                    float cosPhi = (float)Math.Cos(phi);

                    Vector3 normal = new Vector3(cosPhi * sinTheta, cosTheta, sinPhi * sinTheta);
                    vertices.Add(normal);
                    normals.Add(normal);
                    uvs.Add(new Vector2((float)lon / longitudes, 1f - (float)lat / latitudes));
                }
            }

            for (int lat = 0; lat < latitudes; lat++)
            {
                for (int lon = 0; lon < longitudes; lon++)
                {
                    int first = (lat * (longitudes + 1)) + lon;
                    int second = first + longitudes + 1;

                    indices.Add(first);
                    indices.Add(second);
                    indices.Add(first + 1);

                    indices.Add(second);
                    indices.Add(second + 1);
                    indices.Add(first + 1);
                }
            }

            vertexData = vertices.ToArray();
            normal2Data = normals.ToArray();
            normalData = normals.ToArray();
            uvData = uvs.ToArray();
            indiceData = indices.ToArray();

            colorData = new Vector4[vertexData.Length];
            Array.Fill(colorData, Vector4.One);

            SetInitialPreviewFrame(CreateUnityAutoViewMatrix(), Matrix4.CreateScale(0.8f));
        }

        private sealed class MeshPreviewPartGeometry
        {
            public MeshPreviewPartGeometry(
                Vector3[] vertices,
                int[] indices,
                Vector3[]? normals,
                Vector4[] colors,
                Vector2[]? uvs,
                int[]? subMeshIndexCounts,
                Vector3 min,
                Vector3 max,
                bool fromCache = false)
            {
                Vertices = vertices;
                Indices = indices;
                Normals = normals;
                Colors = colors;
                Uvs = uvs;
                SubMeshIndexCounts = subMeshIndexCounts;
                Min = min;
                Max = max;
                FromCache = fromCache;
            }

            public Vector3[] Vertices { get; }
            public int[] Indices { get; }
            public Vector3[]? Normals { get; }
            public Vector4[] Colors { get; }
            public Vector2[]? Uvs { get; }
            public int[]? SubMeshIndexCounts { get; }
            public Vector3 Min { get; }
            public Vector3 Max { get; }
            public bool FromCache { get; }
        }
    }
}
