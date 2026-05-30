using System;
using System.Collections.Generic;

namespace AssetStudio.Avalonia
{
    using Vector3 = OpenTK.Mathematics.Vector3;

internal sealed class AvatarReferenceMesh
{
    public AvatarReferenceMesh(Vector3[] vertices, int[] indices, int[] sourceVertexIndices)
    {
        Vertices = vertices;
        Indices = indices;
        SourceVertexIndices = sourceVertexIndices;
    }

    public Vector3[] Vertices { get; }
    public int[] Indices { get; }
    public int[] SourceVertexIndices { get; }
}

internal static class AvatarReferenceMeshSimplifier
{
    public const float DefaultDensityPercent = 70f;
    public const float MinDensityPercent = 1f;
    public const float MaxDensityPercent = 100f;

    private const double DensityCurvePower = 1.5;
    private const int PreferredMinimumPreviewVertices = 32;
    private const int MaximumGridAxisResolution = 1024;

    public static AvatarReferenceMesh Build(Vector3[] sourceVertices, List<uint> sourceIndices, Vector3 min, Vector3 max, float densityPercent)
    {
        if (sourceVertices.Length == 0 || sourceIndices.Count < 3)
        {
            return Empty();
        }

        var triangles = CollectTriangles(sourceVertices, sourceIndices);
        if (triangles.Count == 0)
        {
            return Empty();
        }

        densityPercent = Math.Clamp(densityPercent, MinDensityPercent, MaxDensityPercent);
        if (densityPercent >= MaxDensityPercent - 0.01f)
        {
            return CompactOriginalTriangles(sourceVertices, triangles);
        }

        var sourceVertexScores = BuildVertexScores(sourceVertices, triangles);
        var usedVertexIndices = CollectUsedVertices(sourceVertexScores);
        int targetVertexCount = ComputeTargetVertexCount(usedVertexIndices.Count, densityPercent);
        if (targetVertexCount >= usedVertexIndices.Count)
        {
            return CompactOriginalTriangles(sourceVertices, triangles);
        }

        var axisResolution = FindGridAxisResolution(sourceVertices, usedVertexIndices, min, max, targetVertexCount);
        var clustered = BuildClusteredMesh(sourceVertices, triangles, usedVertexIndices, sourceVertexScores, min, max, axisResolution);
        if (clustered.Indices.Length > 0)
        {
            return clustered;
        }

        return CompactOriginalTriangles(sourceVertices, triangles);
    }

    private static AvatarReferenceMesh Empty()
    {
        return new AvatarReferenceMesh(Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>());
    }

    private static List<Triangle> CollectTriangles(Vector3[] sourceVertices, List<uint> sourceIndices)
    {
        var triangles = new List<Triangle>(sourceIndices.Count / 3);
        for (int offset = 0; offset + 2 < sourceIndices.Count; offset += 3)
        {
            if (TryReadTriangle(sourceIndices, offset, sourceVertices.Length, out int i0, out int i1, out int i2))
            {
                var area = GetTriangleArea(sourceVertices[i0], sourceVertices[i1], sourceVertices[i2]);
                if (area > 1e-12f)
                {
                    triangles.Add(new Triangle(i0, i1, i2, area));
                }
            }
        }

        return triangles;
    }

    private static bool TryReadTriangle(List<uint> sourceIndices, int offset, int vertexCount, out int i0, out int i1, out int i2)
    {
        i0 = i1 = i2 = 0;
        if (offset < 0 || offset + 2 >= sourceIndices.Count)
        {
            return false;
        }

        uint u0 = sourceIndices[offset];
        uint u1 = sourceIndices[offset + 1];
        uint u2 = sourceIndices[offset + 2];
        uint vertexLimit = (uint)vertexCount;
        if (u0 >= vertexLimit || u1 >= vertexLimit || u2 >= vertexLimit)
        {
            return false;
        }

        i0 = (int)u0;
        i1 = (int)u1;
        i2 = (int)u2;
        return i0 != i1 && i1 != i2 && i2 != i0;
    }

    private static float GetTriangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        var cross = Vector3.Cross(b - a, c - a);
        return float.IsFinite(cross.X) && float.IsFinite(cross.Y) && float.IsFinite(cross.Z)
            ? cross.Length * 0.5f
            : 0f;
    }

    private static double[] BuildVertexScores(Vector3[] sourceVertices, List<Triangle> triangles)
    {
        var scores = new double[sourceVertices.Length];
        foreach (var triangle in triangles)
        {
            scores[triangle.A] += triangle.Area;
            scores[triangle.B] += triangle.Area;
            scores[triangle.C] += triangle.Area;
        }

        return scores;
    }

    private static List<int> CollectUsedVertices(double[] vertexScores)
    {
        var usedVertexIndices = new List<int>();
        for (int i = 0; i < vertexScores.Length; i++)
        {
            if (vertexScores[i] > 0)
            {
                usedVertexIndices.Add(i);
            }
        }

        return usedVertexIndices;
    }

    private static int ComputeTargetVertexCount(int usedVertexCount, float densityPercent)
    {
        if (usedVertexCount <= 0)
        {
            return 0;
        }

        double slider = Math.Clamp(densityPercent / 100.0, 0.0, 1.0);
        double curvedDensity = Math.Pow(slider, DensityCurvePower);
        int target = (int)Math.Ceiling(usedVertexCount * curvedDensity);
        int minimum = Math.Min(usedVertexCount, PreferredMinimumPreviewVertices);
        return Math.Clamp(Math.Max(target, minimum), Math.Min(3, usedVertexCount), usedVertexCount);
    }

    private static int FindGridAxisResolution(Vector3[] sourceVertices, List<int> usedVertexIndices, Vector3 min, Vector3 max, int targetVertexCount)
    {
        if (targetVertexCount <= 1)
        {
            return 1;
        }

        int low = 1;
        int high = 2;
        while (high < MaximumGridAxisResolution
            && CountOccupiedCells(sourceVertices, usedVertexIndices, min, max, high) < targetVertexCount)
        {
            low = high;
            high *= 2;
        }

        high = Math.Min(high, MaximumGridAxisResolution);
        while (low + 1 < high)
        {
            int mid = low + (high - low) / 2;
            int occupied = CountOccupiedCells(sourceVertices, usedVertexIndices, min, max, mid);
            if (occupied < targetVertexCount)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        int lowCount = CountOccupiedCells(sourceVertices, usedVertexIndices, min, max, low);
        int highCount = CountOccupiedCells(sourceVertices, usedVertexIndices, min, max, high);
        return Math.Abs(highCount - targetVertexCount) < Math.Abs(targetVertexCount - lowCount)
            ? high
            : low;
    }

    private static int CountOccupiedCells(Vector3[] sourceVertices, List<int> usedVertexIndices, Vector3 min, Vector3 max, int axisResolution)
    {
        var grid = CreateGrid(min, max, axisResolution);
        var occupied = new HashSet<long>();
        foreach (var sourceIndex in usedVertexIndices)
        {
            occupied.Add(grid.GetCellKey(sourceVertices[sourceIndex]));
        }

        return occupied.Count;
    }

    private static AvatarReferenceMesh BuildClusteredMesh(
        Vector3[] sourceVertices,
        List<Triangle> triangles,
        List<int> usedVertexIndices,
        double[] sourceVertexScores,
        Vector3 min,
        Vector3 max,
        int axisResolution)
    {
        var grid = CreateGrid(min, max, axisResolution);
        var cells = new Dictionary<long, VertexCell>();
        foreach (var sourceIndex in usedVertexIndices)
        {
            var vertex = sourceVertices[sourceIndex];
            long key = grid.GetCellKey(vertex);
            if (!cells.TryGetValue(key, out var cell))
            {
                cell = new VertexCell();
                cells.Add(key, cell);
            }

            double weight = Math.Max(sourceVertexScores[sourceIndex], 1e-12);
            cell.SourceIndices.Add(sourceIndex);
            cell.WeightedPosition += vertex * (float)weight;
            cell.Weight += weight;
        }

        var sourceToPreview = new int[sourceVertices.Length];
        Array.Fill(sourceToPreview, -1);

        var previewVertices = new List<Vector3>(cells.Count);
        var previewSourceIndices = new List<int>(cells.Count);
        foreach (var cell in cells.Values)
        {
            int representativeSourceIndex = ChooseRepresentativeVertex(sourceVertices, sourceVertexScores, cell, grid.CellSize);
            int previewIndex = previewVertices.Count;
            previewVertices.Add(sourceVertices[representativeSourceIndex]);
            previewSourceIndices.Add(representativeSourceIndex);

            foreach (int sourceIndex in cell.SourceIndices)
            {
                sourceToPreview[sourceIndex] = previewIndex;
            }
        }

        var previewIndices = new List<int>(triangles.Count * 3);
        var emittedTriangles = new HashSet<TriangleKey>();
        foreach (var triangle in triangles)
        {
            int i0 = sourceToPreview[triangle.A];
            int i1 = sourceToPreview[triangle.B];
            int i2 = sourceToPreview[triangle.C];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 == i1 || i1 == i2 || i2 == i0)
            {
                continue;
            }

            if (!emittedTriangles.Add(TriangleKey.Create(i0, i1, i2)))
            {
                continue;
            }

            previewIndices.Add(i0);
            previewIndices.Add(i1);
            previewIndices.Add(i2);
        }

        return CompactPreviewVertices(previewVertices, previewSourceIndices, previewIndices);
    }

    private static int ChooseRepresentativeVertex(Vector3[] sourceVertices, double[] sourceVertexScores, VertexCell cell, float cellSize)
    {
        var centroid = cell.Weight > 0
            ? cell.WeightedPosition / (float)cell.Weight
            : sourceVertices[cell.SourceIndices[0]];

        int bestIndex = cell.SourceIndices[0];
        double bestScore = double.NegativeInfinity;
        double safeCellSizeSquared = Math.Max(cellSize * cellSize, 1e-12f);
        foreach (int sourceIndex in cell.SourceIndices)
        {
            var vertex = sourceVertices[sourceIndex];
            double distancePenalty = (vertex - centroid).LengthSquared / safeCellSizeSquared;
            double score = sourceVertexScores[sourceIndex] / (1.0 + distancePenalty);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = sourceIndex;
            }
        }

        return bestIndex;
    }

    private static AvatarReferenceMesh CompactOriginalTriangles(Vector3[] sourceVertices, List<Triangle> triangles)
    {
        var vertexRemap = new Dictionary<int, int>();
        var previewVertices = new List<Vector3>();
        var previewSourceIndices = new List<int>();
        var previewIndices = new List<int>(triangles.Count * 3);

        int GetPreviewVertexIndex(int sourceIndex)
        {
            if (vertexRemap.TryGetValue(sourceIndex, out int previewIndex))
            {
                return previewIndex;
            }

            previewIndex = previewVertices.Count;
            vertexRemap[sourceIndex] = previewIndex;
            previewVertices.Add(sourceVertices[sourceIndex]);
            previewSourceIndices.Add(sourceIndex);
            return previewIndex;
        }

        foreach (var triangle in triangles)
        {
            previewIndices.Add(GetPreviewVertexIndex(triangle.A));
            previewIndices.Add(GetPreviewVertexIndex(triangle.B));
            previewIndices.Add(GetPreviewVertexIndex(triangle.C));
        }

        return new AvatarReferenceMesh(previewVertices.ToArray(), previewIndices.ToArray(), previewSourceIndices.ToArray());
    }

    private static AvatarReferenceMesh CompactPreviewVertices(List<Vector3> vertices, List<int> sourceIndices, List<int> indices)
    {
        if (indices.Count == 0)
        {
            return Empty();
        }

        var remap = new int[vertices.Count];
        Array.Fill(remap, -1);
        var compactVertices = new List<Vector3>();
        var compactSourceIndices = new List<int>();
        var compactIndices = new int[indices.Count];

        for (int i = 0; i < indices.Count; i++)
        {
            int oldIndex = indices[i];
            int newIndex = remap[oldIndex];
            if (newIndex < 0)
            {
                newIndex = compactVertices.Count;
                remap[oldIndex] = newIndex;
                compactVertices.Add(vertices[oldIndex]);
                compactSourceIndices.Add(sourceIndices[oldIndex]);
            }

            compactIndices[i] = newIndex;
        }

        return new AvatarReferenceMesh(compactVertices.ToArray(), compactIndices, compactSourceIndices.ToArray());
    }

    private static UniformGrid CreateGrid(Vector3 min, Vector3 max, int axisResolution)
    {
        var size = max - min;
        float maxExtent = Math.Max(Math.Max(Math.Abs(size.X), Math.Abs(size.Y)), Math.Abs(size.Z));
        if (!float.IsFinite(maxExtent) || maxExtent <= 1e-5f)
        {
            maxExtent = 1f;
        }

        float cellSize = maxExtent / Math.Max(1, axisResolution);
        return new UniformGrid(min, cellSize);
    }

    private readonly record struct Triangle(int A, int B, int C, float Area);

    private readonly record struct TriangleKey(int A, int B, int C)
    {
        public static TriangleKey Create(int a, int b, int c)
        {
            if (a > b)
            {
                (a, b) = (b, a);
            }
            if (b > c)
            {
                (b, c) = (c, b);
            }
            if (a > b)
            {
                (a, b) = (b, a);
            }

            return new TriangleKey(a, b, c);
        }
    }

    private sealed class VertexCell
    {
        public readonly List<int> SourceIndices = new();
        public Vector3 WeightedPosition;
        public double Weight;
    }

    private readonly struct UniformGrid
    {
        private readonly Vector3 origin;

        public UniformGrid(Vector3 origin, float cellSize)
        {
            this.origin = origin;
            CellSize = Math.Max(cellSize, 1e-5f);
        }

        public float CellSize { get; }

        public long GetCellKey(Vector3 point)
        {
            int x = Quantize(point.X, origin.X);
            int y = Quantize(point.Y, origin.Y);
            int z = Quantize(point.Z, origin.Z);
            return PackCellKey(x, y, z);
        }

        private int Quantize(float value, float min)
        {
            if (!float.IsFinite(value) || !float.IsFinite(min))
            {
                return 0;
            }

            return (int)Math.Floor((value - min) / CellSize);
        }

        private static long PackCellKey(int x, int y, int z)
        {
            unchecked
            {
                const long offset = 1_048_576;
                return ((x + offset) & 0x1fffffL)
                    | (((y + offset) & 0x1fffffL) << 21)
                    | (((z + offset) & 0x1fffffL) << 42);
            }
        }
    }
}
}
