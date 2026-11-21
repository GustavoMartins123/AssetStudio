using System;
using System.Collections.Generic;

namespace AssetStudio.Avalonia
{
    using Vector2 = OpenTK.Mathematics.Vector2;
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;

    public partial class MeshPreviewControl
    {
        private const int PreviewBoundsSampleLimit = 32_768;
        private const int PreviewBoundsMinimumTrimCount = 200;
        private const float PreviewBoundsTrimFraction = 0.0025f;

        private static Vector3[] ExtractMeshVertices(float[] vertices, int vertexCount, out Vector3 min, out Vector3 max)
        {
            var componentCount = vertices.Length == vertexCount * 4 ? 4 : 3;
            var result = new Vector3[vertexCount];
            min = new Vector3(vertices[0], vertices[1], vertices[2]);
            max = min;
            for (var v = 0; v < vertexCount; v++)
            {
                var vertex = new Vector3(
                    vertices[v * componentCount],
                    vertices[v * componentCount + 1],
                    vertices[v * componentCount + 2]);
                result[v] = vertex;
                ExpandBounds(vertex, ref min, ref max);
            }

            return result;
        }

        private static int[] ExtractMeshIndices(IReadOnlyList<uint> indices)
        {
            var result = new int[indices.Count];
            for (var i = 0; i < indices.Count; i++)
            {
                result[i] = (int)indices[i];
            }

            return result;
        }

        private static Vector3[]? ExtractMeshNormals(float[]? normals, int vertexCount)
        {
            if (normals == null || normals.Length == 0)
            {
                return null;
            }

            var componentCount = normals.Length == vertexCount * 4 ? 4 : 3;
            var result = new Vector3[vertexCount];
            for (var n = 0; n < vertexCount; n++)
            {
                result[n] = new Vector3(
                    normals[n * componentCount],
                    normals[n * componentCount + 1],
                    normals[n * componentCount + 2]);
            }

            return result;
        }

        private static Vector4[] ExtractMeshColors(float[]? colors, int vertexCount)
        {
            var result = new Vector4[vertexCount];
            if (colors != null && colors.Length == vertexCount * 3)
            {
                for (var c = 0; c < vertexCount; c++)
                {
                    result[c] = new Vector4(colors[c * 3], colors[c * 3 + 1], colors[c * 3 + 2], 1.0f);
                }
            }
            else if (colors != null && colors.Length == vertexCount * 4)
            {
                for (var c = 0; c < vertexCount; c++)
                {
                    result[c] = new Vector4(colors[c * 4], colors[c * 4 + 1], colors[c * 4 + 2], colors[c * 4 + 3]);
                }
            }
            else
            {
                Array.Fill(result, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
            }

            return result;
        }

        private static MeshPreviewPartGeometry TransformMeshPreviewPartGeometry(MeshPreviewPartGeometry part, float[] matrix)
        {
            var vertices = new Vector3[part.Vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i] = TransformPoint(matrix, part.Vertices[i]);
            }

            Vector3[]? normals = null;
            if (part.Normals != null)
            {
                normals = new Vector3[part.Normals.Length];
                for (var i = 0; i < normals.Length; i++)
                {
                    var normal = TransformDirection(matrix, part.Normals[i]);
                    normals[i] = normal.LengthSquared > 0
                        ? Vector3.Normalize(normal)
                        : part.Normals[i];
                }
            }

            GetRobustMeshPreviewGeometryBounds(vertices, out var min, out var max);
            return new MeshPreviewPartGeometry(
                vertices,
                part.Indices,
                normals,
                part.Colors,
                part.Uvs,
                part.SubMeshIndexCounts,
                min,
                max,
                part.FromCache);
        }

        private static Vector3 TransformPoint(float[] matrix, Vector3 point)
        {
            return new Vector3(
                matrix[0] * point.X + matrix[1] * point.Y + matrix[2] * point.Z + matrix[3],
                matrix[4] * point.X + matrix[5] * point.Y + matrix[6] * point.Z + matrix[7],
                matrix[8] * point.X + matrix[9] * point.Y + matrix[10] * point.Z + matrix[11]);
        }

        private static Vector3 TransformDirection(float[] matrix, Vector3 direction)
        {
            return new Vector3(
                matrix[0] * direction.X + matrix[1] * direction.Y + matrix[2] * direction.Z,
                matrix[4] * direction.X + matrix[5] * direction.Y + matrix[6] * direction.Z,
                matrix[8] * direction.X + matrix[9] * direction.Y + matrix[10] * direction.Z);
        }

        private static Vector3[] BuildCalculatedNormals(Vector3[] vertices, int[] indices)
        {
            var normals = new Vector3[vertices.Length];
            var counts = new int[vertices.Length];

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int i0 = indices[i];
                int i1 = indices[i + 1];
                int i2 = indices[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                {
                    continue;
                }

                Vector3 dir1 = vertices[i1] - vertices[i0];
                Vector3 dir2 = vertices[i2] - vertices[i0];
                Vector3 normal = Vector3.Cross(dir1, dir2);
                if (normal.LengthSquared > 0)
                {
                    normal = Vector3.Normalize(normal);
                }

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
                counts[i0]++;
                counts[i1]++;
                counts[i2]++;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = counts[i] == 0 || normals[i].LengthSquared <= 0
                    ? new Vector3(0, 1, 0)
                    : Vector3.Normalize(normals[i]);
            }

            return normals;
        }

        private static Vector2[]? BuildPreviewUvData(float[]? uv, int vertexCount)
        {
            if (uv == null || vertexCount <= 0 || uv.Length < vertexCount * 2 || uv.Length % vertexCount != 0)
            {
                return null;
            }

            var componentCount = uv.Length / vertexCount;
            if (componentCount < 2 || componentCount > 4)
            {
                return null;
            }

            var result = new Vector2[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                var offset = i * componentCount;
                result[i] = new Vector2(uv[offset], uv[offset + 1]);
            }

            return result;
        }

        private static Vector3[]? RemapVector3Data(Vector3[]? source, int[] sourceVertexIndices)
        {
            if (source == null)
            {
                return null;
            }

            var remapped = new Vector3[sourceVertexIndices.Length];
            for (int i = 0; i < sourceVertexIndices.Length; i++)
            {
                int sourceIndex = sourceVertexIndices[i];
                if (sourceIndex >= 0 && sourceIndex < source.Length)
                {
                    remapped[i] = source[sourceIndex];
                }
            }

            return remapped;
        }

        private static Vector4[]? RemapVector4Data(Vector4[]? source, int[] sourceVertexIndices)
        {
            if (source == null)
            {
                return null;
            }

            var remapped = new Vector4[sourceVertexIndices.Length];
            for (int i = 0; i < sourceVertexIndices.Length; i++)
            {
                int sourceIndex = sourceVertexIndices[i];
                remapped[i] = sourceIndex >= 0 && sourceIndex < source.Length
                    ? source[sourceIndex]
                    : Vector4.One;
            }

            return remapped;
        }

        private static Vector2[]? RemapVector2Data(Vector2[]? source, int[] sourceVertexIndices)
        {
            if (source == null)
            {
                return null;
            }

            var remapped = new Vector2[sourceVertexIndices.Length];
            for (int i = 0; i < sourceVertexIndices.Length; i++)
            {
                int sourceIndex = sourceVertexIndices[i];
                if (sourceIndex >= 0 && sourceIndex < source.Length)
                {
                    remapped[i] = source[sourceIndex];
                }
            }

            return remapped;
        }

        internal static int[]? BuildSubMeshIndexCounts(Mesh mesh, int indexCount)
        {
            var subMeshes = mesh.m_SubMeshes;
            if (subMeshes == null || subMeshes.Length == 0 || indexCount <= 0)
            {
                return null;
            }

            var counts = new int[subMeshes.Length];
            long total = 0;
            for (int i = 0; i < subMeshes.Length; i++)
            {
                long count = Math.Max(0, (long)subMeshes[i].indexCount);
                if (count > int.MaxValue)
                {
                    return null;
                }

                counts[i] = (int)count;
                total += count;
            }

            return total == indexCount ? counts : null;
        }

        private static void ExpandBounds(Vector3 point, ref Vector3 min, ref Vector3 max)
        {
            if (!IsFinitePoint(point))
            {
                return;
            }

            min = Vector3.ComponentMin(min, point);
            max = Vector3.ComponentMax(max, point);
        }

        private static bool IsFinitePoint(Vector3 point)
        {
            return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
        }

        private static void GetMeshPreviewGeometryBounds(Vector3[] vertices, out Vector3 min, out Vector3 max)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            var hasPoint = false;
            foreach (var vertex in vertices)
            {
                if (!IsFinitePoint(vertex))
                {
                    continue;
                }

                if (!hasPoint)
                {
                    min = vertex;
                    max = vertex;
                    hasPoint = true;
                }
                else
                {
                    ExpandBounds(vertex, ref min, ref max);
                }
            }
        }

        private static void GetRobustMeshPreviewGeometryBounds(Vector3[] vertices, out Vector3 min, out Vector3 max)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            if (vertices.Length == 0)
            {
                return;
            }

            var stride = Math.Max(1, (int)Math.Ceiling(vertices.Length / (double)PreviewBoundsSampleLimit));
            var capacity = Math.Min(PreviewBoundsSampleLimit, (vertices.Length + stride - 1) / stride);
            var x = new float[capacity];
            var y = new float[capacity];
            var z = new float[capacity];
            var count = 0;
            for (var i = 0; i < vertices.Length && count < capacity; i += stride)
            {
                var point = vertices[i];
                if (!IsFinitePoint(point))
                {
                    continue;
                }

                x[count] = point.X;
                y[count] = point.Y;
                z[count] = point.Z;
                count++;
            }

            if (count == 0)
            {
                return;
            }

            Array.Sort(x, 0, count);
            Array.Sort(y, 0, count);
            Array.Sort(z, 0, count);
            var lowerIndex = count < PreviewBoundsMinimumTrimCount
                ? 0
                : Math.Clamp((int)Math.Floor((count - 1) * PreviewBoundsTrimFraction), 0, count - 1);
            var upperIndex = count < PreviewBoundsMinimumTrimCount
                ? count - 1
                : Math.Clamp((int)Math.Ceiling((count - 1) * (1f - PreviewBoundsTrimFraction)), lowerIndex, count - 1);

            min = new Vector3(x[lowerIndex], y[lowerIndex], z[lowerIndex]);
            max = new Vector3(x[upperIndex], y[upperIndex], z[upperIndex]);

            var robustSize = max - min;
            if (Math.Max(robustSize.X, Math.Max(robustSize.Y, robustSize.Z)) <= 1e-5f)
            {
                GetMeshPreviewGeometryBounds(vertices, out min, out max);
            }
        }
    }
}
