using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text;

namespace AssetStudio.Avalonia.Services;

public enum ColliderKind
{
    Box,
    Sphere,
    Capsule,
    Mesh
}

public sealed class ColliderMeshGeometry
{
    public string Name { get; init; } = string.Empty;
    public ColliderKind ColliderKind { get; init; }
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public int[] TriangleIndices { get; init; } = Array.Empty<int>();
    public bool IsTrigger { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => TriangleIndices.Length / 3;
}

public sealed class ColliderExtractedProperties
{
    public ColliderKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsTrigger { get; init; }
    public Vector3 Center { get; init; } = Vector3.Zero;
    public Vector3 Size { get; init; } = new Vector3(1f, 1f, 1f);
    public float Radius { get; init; } = 0.5f;
    public float Height { get; init; } = 2f;
    public int Direction { get; init; } = 1;
    public bool Convex { get; init; }
    public int CookingOptions { get; init; } = 30;
    public int MeshFileId { get; init; }
    public long MeshPathId { get; init; }
}

/// <summary>
/// Pure managed converter that transforms Unity physics collider components (BoxCollider,
/// SphereCollider, CapsuleCollider, MeshCollider) into clean triangulated local-space geometry
/// and streams Wavefront .obj files compatible with Unity ModelImporter round-tripping.
/// </summary>
public static class ColliderMeshConverter
{
    public const int DefaultSphereLongitudeSegments = 16;
    public const int DefaultSphereLatitudeSegments = 12;
    public const int DefaultCapsuleRadialSegments = 16;
    public const int DefaultCapsuleHemisphereRings = 6;

    private const float MinDimensionEpsilon = 1e-5f;
    private const int StreamWriterBufferSize = 32_768;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static ColliderMeshGeometry? TryBuildColliderMesh(
        AssetStudio.Object? colliderComponent,
        string gameObjectName,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider = null,
        Func<PPtr<Mesh>, Mesh?>? meshResolver = null)
    {
        if (colliderComponent == null)
        {
            return null;
        }

        try
        {
            var baseName = string.IsNullOrWhiteSpace(gameObjectName)
                ? $"Collider_{colliderComponent.m_PathID}"
                : gameObjectName.Trim();

            return colliderComponent.type switch
            {
                ClassIDType.BoxCollider => TryBuildBoxCollider(colliderComponent, baseName, fieldProvider),
                ClassIDType.SphereCollider => TryBuildSphereCollider(colliderComponent, baseName, fieldProvider),
                ClassIDType.CapsuleCollider => TryBuildCapsuleCollider(colliderComponent, baseName, fieldProvider),
                ClassIDType.MeshCollider => TryBuildMeshCollider(colliderComponent, baseName, fieldProvider, meshResolver),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static ColliderMeshGeometry? BuildBoxMesh(
        string name,
        Vector3 center,
        Vector3 size,
        bool isTrigger = false)
    {
        if (!IsFiniteVector3(center) || !IsFiniteVector3(size))
        {
            return null;
        }

        float sx = MathF.Abs(size.X);
        float sy = MathF.Abs(size.Y);
        float sz = MathF.Abs(size.Z);
        if (sx < MinDimensionEpsilon || sy < MinDimensionEpsilon || sz < MinDimensionEpsilon)
        {
            return null;
        }

        float hx = sx * 0.5f;
        float hy = sy * 0.5f;
        float hz = sz * 0.5f;

        float x0 = center.X - hx, x1 = center.X + hx;
        float y0 = center.Y - hy, y1 = center.Y + hy;
        float z0 = center.Z - hz, z1 = center.Z + hz;

        var positions = new Vector3[24];
        var normals = new Vector3[24];
        var indices = new int[36];

        // 6 faces x 4 vertices = 24 vertices with exact flat outward normals and Unity CW outward winding
        // (satisfies Dot(Cross(v1 - v0, v2 - v0), normal) > 0 for every triangle).
        int v = 0;
        int idx = 0;

        AddBoxFace(positions, normals, indices, ref v, ref idx,
            new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1),
            new Vector3(1f, 0f, 0f)); // +X

        AddBoxFace(positions, normals, indices, ref v, ref idx,
            new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0),
            new Vector3(-1f, 0f, 0f)); // -X

        AddBoxFace(positions, normals, indices, ref v, ref idx,
            new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0),
            new Vector3(0f, 1f, 0f)); // +Y

        AddBoxFace(positions, normals, indices, ref v, ref idx,
            new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1),
            new Vector3(0f, -1f, 0f)); // -Y

        AddBoxFace(positions, normals, indices, ref v, ref idx,
            new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1),
            new Vector3(0f, 0f, 1f)); // +Z

        AddBoxFace(positions, normals, indices, ref v, ref idx,
            new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0),
            new Vector3(0f, 0f, -1f)); // -Z

        return new ColliderMeshGeometry
        {
            Name = name,
            ColliderKind = ColliderKind.Box,
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
            IsTrigger = isTrigger
        };
    }

    public static ColliderMeshGeometry? BuildSphereMesh(
        string name,
        Vector3 center,
        float radius,
        bool isTrigger = false,
        int longitudeSegments = DefaultSphereLongitudeSegments,
        int latitudeSegments = DefaultSphereLatitudeSegments)
    {
        if (!IsFiniteVector3(center) || !float.IsFinite(radius) || radius < MinDimensionEpsilon)
        {
            return null;
        }

        int lonSegs = Math.Clamp(longitudeSegments, 6, 128);
        int latSegs = Math.Clamp(latitudeSegments, 4, 64);

        var positions = new List<Vector3>((latSegs - 1) * lonSegs + 2);
        var normals = new List<Vector3>((latSegs - 1) * lonSegs + 2);
        var indices = new List<int>(lonSegs * (latSegs - 1) * 6);

        // Top pole (+Y)
        int topPoleIndex = 0;
        positions.Add(new Vector3(center.X, center.Y + radius, center.Z));
        normals.Add(new Vector3(0f, 1f, 0f));

        // Intermediate latitude rings (1 .. latSegs - 1)
        for (int lat = 1; lat < latSegs; lat++)
        {
            float phi = MathF.PI * lat / latSegs; // 0 (top) .. PI (bottom)
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int lon = 0; lon < lonSegs; lon++)
            {
                float theta = 2f * MathF.PI * lon / lonSegs;
                float nx = sinPhi * MathF.Cos(theta);
                float ny = cosPhi;
                float nz = sinPhi * MathF.Sin(theta);

                normals.Add(new Vector3(nx, ny, nz));
                positions.Add(new Vector3(center.X + nx * radius, center.Y + ny * radius, center.Z + nz * radius));
            }
        }

        // Bottom pole (-Y)
        int bottomPoleIndex = positions.Count;
        positions.Add(new Vector3(center.X, center.Y - radius, center.Z));
        normals.Add(new Vector3(0f, -1f, 0f));

        // Top cap triangles
        int firstRingStart = 1;
        for (int lon = 0; lon < lonSegs; lon++)
        {
            int nextLon = (lon + 1) % lonSegs;
            AddOutwardTriangle(positions, normals, indices, topPoleIndex, firstRingStart + nextLon, firstRingStart + lon);
        }

        // Middle bands
        for (int lat = 0; lat < latSegs - 2; lat++)
        {
            int rowA = 1 + lat * lonSegs;
            int rowB = rowA + lonSegs;

            for (int lon = 0; lon < lonSegs; lon++)
            {
                int nextLon = (lon + 1) % lonSegs;
                int a0 = rowA + lon;
                int a1 = rowA + nextLon;
                int b0 = rowB + lon;
                int b1 = rowB + nextLon;

                AddOutwardTriangle(positions, normals, indices, a0, a1, b1);
                AddOutwardTriangle(positions, normals, indices, a0, b1, b0);
            }
        }

        // Bottom cap triangles
        int lastRingStart = 1 + (latSegs - 2) * lonSegs;
        for (int lon = 0; lon < lonSegs; lon++)
        {
            int nextLon = (lon + 1) % lonSegs;
            AddOutwardTriangle(positions, normals, indices, bottomPoleIndex, lastRingStart + lon, lastRingStart + nextLon);
        }

        return new ColliderMeshGeometry
        {
            Name = name,
            ColliderKind = ColliderKind.Sphere,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TriangleIndices = indices.ToArray(),
            IsTrigger = isTrigger
        };
    }

    public static ColliderMeshGeometry? BuildCapsuleMesh(
        string name,
        Vector3 center,
        float radius,
        float height,
        int direction = 1,
        bool isTrigger = false,
        int radialSegments = DefaultCapsuleRadialSegments,
        int hemisphereRings = DefaultCapsuleHemisphereRings)
    {
        if (!IsFiniteVector3(center) || !float.IsFinite(radius) || !float.IsFinite(height))
        {
            return null;
        }

        if (radius < MinDimensionEpsilon || height < 0f)
        {
            return null;
        }

        int radSegs = Math.Clamp(radialSegments, 6, 128);
        int hemiRings = Math.Clamp(hemisphereRings, 2, 32);
        int axisDir = direction is >= 0 and <= 2 ? direction : 1;

        // Unity clamps capsule cylinder height to max(0, height - 2 * radius).
        float halfCylinder = MathF.Max(0f, (height * 0.5f) - radius);

        // Build along canonical +Y axis first, then permute coordinates for X (0), Y (1), or Z (2).
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();

        Vector3 MapDirection(float x, float y, float z) => axisDir switch
        {
            0 => new Vector3(y, z, x), // +Y -> +X (even permutation, preserves handedness!)
            2 => new Vector3(z, x, y), // +Y -> +Z (even permutation, preserves handedness!)
            _ => new Vector3(x, y, z)  // +Y
        };

        void AddLocalVertex(float x, float y, float z, float nx, float ny, float nz)
        {
            var lp = MapDirection(x, y, z);
            var ln = MapDirection(nx, ny, nz);
            ln.Normalize();
            positions.Add(new Vector3(center.X + lp.X, center.Y + lp.Y, center.Z + lp.Z));
            normals.Add(ln);
        }

        // Top pole
        int topPoleIndex = positions.Count;
        AddLocalVertex(0f, halfCylinder + radius, 0f, 0f, 1f, 0f);

        var ringStarts = new List<int>();

        // Upper hemisphere rings: ring 1 .. hemiRings (where ring == hemiRings is upper equator at y = +halfCylinder)
        for (int r = 1; r <= hemiRings; r++)
        {
            float phi = (MathF.PI * 0.5f) * r / hemiRings; // (0 .. PI/2]
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);
            float ringY = halfCylinder + cosPhi * radius;

            ringStarts.Add(positions.Count);
            for (int seg = 0; seg < radSegs; seg++)
            {
                float theta = 2f * MathF.PI * seg / radSegs;
                float nx = sinPhi * MathF.Cos(theta);
                float ny = cosPhi;
                float nz = sinPhi * MathF.Sin(theta);
                AddLocalVertex(nx * radius, ringY, nz * radius, nx, ny, nz);
            }
        }

        // Lower hemisphere rings: start at lower equator (y = -halfCylinder) when halfCylinder > 0
        int lowerStartRing = halfCylinder > MinDimensionEpsilon ? 0 : 1;
        for (int r = lowerStartRing; r < hemiRings; r++)
        {
            float phi = (MathF.PI * 0.5f) + (MathF.PI * 0.5f) * r / hemiRings; // [PI/2 .. PI)
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);
            float ringY = -halfCylinder + cosPhi * radius;

            ringStarts.Add(positions.Count);
            for (int seg = 0; seg < radSegs; seg++)
            {
                float theta = 2f * MathF.PI * seg / radSegs;
                float nx = sinPhi * MathF.Cos(theta);
                float ny = cosPhi;
                float nz = sinPhi * MathF.Sin(theta);
                AddLocalVertex(nx * radius, ringY, nz * radius, nx, ny, nz);
            }
        }

        // Bottom pole
        int bottomPoleIndex = positions.Count;
        AddLocalVertex(0f, -(halfCylinder + radius), 0f, 0f, -1f, 0f);

        // Top cap
        int firstRing = ringStarts[0];
        for (int seg = 0; seg < radSegs; seg++)
        {
            int next = (seg + 1) % radSegs;
            AddOutwardTriangle(positions, normals, indices, topPoleIndex, firstRing + next, firstRing + seg);
        }

        // Ring-to-ring quads (covers upper hemisphere, cylinder wall, and lower hemisphere smoothly!)
        for (int r = 0; r < ringStarts.Count - 1; r++)
        {
            int rowA = ringStarts[r];
            int rowB = ringStarts[r + 1];
            for (int seg = 0; seg < radSegs; seg++)
            {
                int next = (seg + 1) % radSegs;
                int a0 = rowA + seg;
                int a1 = rowA + next;
                int b0 = rowB + seg;
                int b1 = rowB + next;

                AddOutwardTriangle(positions, normals, indices, a0, a1, b1);
                AddOutwardTriangle(positions, normals, indices, a0, b1, b0);
            }
        }

        // Bottom cap
        int lastRing = ringStarts[^1];
        for (int seg = 0; seg < radSegs; seg++)
        {
            int next = (seg + 1) % radSegs;
            AddOutwardTriangle(positions, normals, indices, bottomPoleIndex, lastRing + seg, lastRing + next);
        }

        return new ColliderMeshGeometry
        {
            Name = name,
            ColliderKind = ColliderKind.Capsule,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TriangleIndices = indices.ToArray(),
            IsTrigger = isTrigger
        };
    }

    public static ColliderMeshGeometry? BuildMeshColliderMesh(
        string name,
        Mesh? mesh,
        bool isTrigger = false)
    {
        if (mesh == null)
        {
            return null;
        }

        try
        {
            mesh.EnsureProcessed();
        }
        catch
        {
            return null;
        }

        int vertexCount = mesh.m_VertexCount;
        var rawVerts = mesh.m_Vertices;
        var rawIndices = mesh.m_Indices;
        if (vertexCount <= 0 || rawVerts == null || rawVerts.Length < vertexCount * 3 || rawIndices == null || rawIndices.Count < 3)
        {
            return null;
        }

        int posStride = rawVerts.Length >= vertexCount * 4 ? 4 : 3;
        var positions = new Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int o = v * posStride;
            float x = rawVerts[o];
            float y = rawVerts[o + 1];
            float z = rawVerts[o + 2];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                return null;
            }
            positions[v] = new Vector3(x, y, z);
        }

        var validIndices = new List<int>(rawIndices.Count);
        int triCount = rawIndices.Count / 3;
        for (int t = 0; t < triCount; t++)
        {
            int baseIdx = t * 3;
            uint ua = rawIndices[baseIdx];
            uint ub = rawIndices[baseIdx + 1];
            uint uc = rawIndices[baseIdx + 2];
            if (ua >= (uint)vertexCount || ub >= (uint)vertexCount || uc >= (uint)vertexCount)
            {
                continue;
            }
            if (ua == ub || ub == uc || uc == ua)
            {
                continue;
            }

            validIndices.Add((int)ua);
            validIndices.Add((int)ub);
            validIndices.Add((int)uc);
        }

        if (validIndices.Count < 3)
        {
            return null;
        }

        var normals = new Vector3[vertexCount];
        bool hasValidSourceNormals = mesh.m_Normals != null && mesh.m_Normals.Length >= vertexCount * 3;
        if (hasValidSourceNormals)
        {
            var rawNrms = mesh.m_Normals!;
            int nrmStride = rawNrms.Length >= vertexCount * 4 ? 4 : 3;
            for (int v = 0; v < vertexCount; v++)
            {
                int o = v * nrmStride;
                float nx = rawNrms[o];
                float ny = rawNrms[o + 1];
                float nz = rawNrms[o + 2];
                if (!float.IsFinite(nx) || !float.IsFinite(ny) || !float.IsFinite(nz))
                {
                    hasValidSourceNormals = false;
                    break;
                }

                var n = new Vector3(nx, ny, nz);
                if (n.LengthSquared() > 1e-10f)
                {
                    n.Normalize();
                    normals[v] = n;
                }
                else
                {
                    normals[v] = new Vector3(0f, 1f, 0f);
                }
            }
        }

        if (!hasValidSourceNormals)
        {
            Array.Clear(normals);
            for (int i = 0; i < validIndices.Count; i += 3)
            {
                int ia = validIndices[i];
                int ib = validIndices[i + 1];
                int ic = validIndices[i + 2];
                var faceNormal = Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
                normals[ia] = normals[ia] + faceNormal;
                normals[ib] = normals[ib] + faceNormal;
                normals[ic] = normals[ic] + faceNormal;
            }

            for (int v = 0; v < vertexCount; v++)
            {
                if (normals[v].LengthSquared() > 1e-10f)
                {
                    normals[v].Normalize();
                }
                else
                {
                    normals[v] = new Vector3(0f, 1f, 0f);
                }
            }
        }

        return new ColliderMeshGeometry
        {
            Name = name,
            ColliderKind = ColliderKind.Mesh,
            Positions = positions,
            Normals = normals,
            TriangleIndices = validIndices.ToArray(),
            IsTrigger = isTrigger
        };
    }

    public static void WriteObj(ColliderMeshGeometry geometry, string outputPath, string? objGroupName = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output OBJ path must not be empty.", nameof(outputPath));
        }

        var parentDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var rawGroupName = !string.IsNullOrWhiteSpace(objGroupName)
            ? objGroupName
            : (string.IsNullOrWhiteSpace(geometry.Name) ? $"Collider_{geometry.ColliderKind}" : geometry.Name);
        var sbName = new StringBuilder(rawGroupName.Length);
        for (int i = 0; i < rawGroupName.Length; i++)
        {
            char c = rawGroupName[i];
            sbName.Append(char.IsWhiteSpace(c) || c < 0x20 ? '_' : c);
        }
        var safeObjectName = sbName.ToString().Trim('_');
        if (string.IsNullOrEmpty(safeObjectName))
        {
            safeObjectName = $"Collider_{geometry.ColliderKind}";
        }

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamWriterBufferSize);
        using var sw = new StreamWriter(fs, Utf8NoBom, StreamWriterBufferSize) { NewLine = "\n" };

        sw.Write("# AssetStudio Auxiliary Collider Mesh (");
        sw.Write(geometry.ColliderKind.ToString());
        sw.Write(geometry.IsTrigger ? ", Trigger)\n" : ", Solid)\n");
        sw.Write("o ");
        sw.WriteLine(safeObjectName);

        // Negate X and reverse triangle winding so Unity ModelImporter's OBJ import
        // restores exact original Unity collider coordinates and outward normals.
        for (int i = 0; i < geometry.Positions.Length; i++)
        {
            var p = geometry.Positions[i];
            sw.Write("v ");
            sw.Write(FormatFloat(-p.X));
            sw.Write(' ');
            sw.Write(FormatFloat(p.Y));
            sw.Write(' ');
            sw.WriteLine(FormatFloat(p.Z));
        }

        bool hasNormals = geometry.Normals.Length == geometry.Positions.Length && geometry.Normals.Length > 0;
        if (hasNormals)
        {
            for (int i = 0; i < geometry.Normals.Length; i++)
            {
                var n = geometry.Normals[i];
                sw.Write("vn ");
                sw.Write(FormatFloat(-n.X));
                sw.Write(' ');
                sw.Write(FormatFloat(n.Y));
                sw.Write(' ');
                sw.WriteLine(FormatFloat(n.Z));
            }
        }

        sw.Write("g ");
        sw.WriteLine(safeObjectName);
        sw.WriteLine("usemtl collider_mat");

        int vertexCount = geometry.Positions.Length;
        int triCount = geometry.TriangleIndices.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            int idx = t * 3;
            int a = geometry.TriangleIndices[idx + 2] + 1;
            int b = geometry.TriangleIndices[idx + 1] + 1;
            int c = geometry.TriangleIndices[idx + 0] + 1;

            if (a < 1 || a > vertexCount || b < 1 || b > vertexCount || c < 1 || c > vertexCount)
            {
                continue;
            }

            if (hasNormals)
            {
                sw.Write("f ");
                sw.Write(a.ToString(CultureInfo.InvariantCulture));
                sw.Write("//");
                sw.Write(a.ToString(CultureInfo.InvariantCulture));
                sw.Write(' ');
                sw.Write(b.ToString(CultureInfo.InvariantCulture));
                sw.Write("//");
                sw.Write(b.ToString(CultureInfo.InvariantCulture));
                sw.Write(' ');
                sw.Write(c.ToString(CultureInfo.InvariantCulture));
                sw.Write("//");
                sw.WriteLine(c.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sw.Write("f ");
                sw.Write(a.ToString(CultureInfo.InvariantCulture));
                sw.Write(' ');
                sw.Write(b.ToString(CultureInfo.InvariantCulture));
                sw.Write(' ');
                sw.WriteLine(c.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    // =========================================================================
    // Component Field Extraction (TypeTree/Provider first, Binary Fallback)
    // =========================================================================

    public static bool TryExtractColliderProperties(
        AssetStudio.Object? colliderObj,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider,
        out ColliderExtractedProperties properties)
    {
        properties = null!;
        if (colliderObj == null)
        {
            return false;
        }

        try
        {
            var dict = TryReadColliderFields(colliderObj, fieldProvider);
            switch (colliderObj.type)
            {
                case ClassIDType.BoxCollider:
                    if (dict != null && TryReadVector3(dict, "m_Size", out var dictSize))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Box,
                            Enabled = !TryReadBool(dict, "m_Enabled", out var en) || en,
                            IsTrigger = TryReadBool(dict, "m_IsTrigger", out var tr) && tr,
                            Center = TryReadVector3(dict, "m_Center", out var c) ? c : Vector3.Zero,
                            Size = dictSize
                        };
                        return true;
                    }
                    if (TryReadBoxColliderBinary(colliderObj, out var binCenter, out var binSize, out var binTrigger, out var binEnabled))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Box,
                            Enabled = binEnabled,
                            IsTrigger = binTrigger,
                            Center = binCenter,
                            Size = binSize
                        };
                        return true;
                    }
                    return false;

                case ClassIDType.SphereCollider:
                    if (dict != null && TryReadFloat(dict, "m_Radius", out var dictRadius))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Sphere,
                            Enabled = !TryReadBool(dict, "m_Enabled", out var en) || en,
                            IsTrigger = TryReadBool(dict, "m_IsTrigger", out var tr) && tr,
                            Center = TryReadVector3(dict, "m_Center", out var c) ? c : Vector3.Zero,
                            Radius = dictRadius
                        };
                        return true;
                    }
                    if (TryReadSphereColliderBinary(colliderObj, out var binSCenter, out var binSRadius, out var binSTrigger, out var binSEnabled))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Sphere,
                            Enabled = binSEnabled,
                            IsTrigger = binSTrigger,
                            Center = binSCenter,
                            Radius = binSRadius
                        };
                        return true;
                    }
                    return false;

                case ClassIDType.CapsuleCollider:
                    if (dict != null && TryReadFloat(dict, "m_Radius", out var dictCapRadius))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Capsule,
                            Enabled = !TryReadBool(dict, "m_Enabled", out var en) || en,
                            IsTrigger = TryReadBool(dict, "m_IsTrigger", out var tr) && tr,
                            Center = TryReadVector3(dict, "m_Center", out var c) ? c : Vector3.Zero,
                            Radius = dictCapRadius,
                            Height = TryReadFloat(dict, "m_Height", out var h) ? h : dictCapRadius * 2f,
                            Direction = TryReadInt(dict, "m_Direction", out var d) ? d : 1
                        };
                        return true;
                    }
                    if (TryReadCapsuleColliderBinary(colliderObj, out var binCCenter, out var binCRadius, out var binCHeight, out var binCDir, out var binCTrigger, out var binCEnabled))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Capsule,
                            Enabled = binCEnabled,
                            IsTrigger = binCTrigger,
                            Center = binCCenter,
                            Radius = binCRadius,
                            Height = binCHeight,
                            Direction = binCDir
                        };
                        return true;
                    }
                    return false;

                case ClassIDType.MeshCollider:
                    if (dict != null)
                    {
                        TryExtractPPtr(dict, "m_Mesh", out var mFileId, out var mPathId);
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Mesh,
                            Enabled = !TryReadBool(dict, "m_Enabled", out var en) || en,
                            IsTrigger = TryReadBool(dict, "m_IsTrigger", out var tr) && tr,
                            Convex = TryReadBool(dict, "m_Convex", out var cv) && cv,
                            CookingOptions = TryReadInt(dict, "m_CookingOptions", out var co) ? co : 30,
                            MeshFileId = mFileId,
                            MeshPathId = mPathId
                        };
                        return true;
                    }
                    if (TryReadMeshColliderBinary(colliderObj, out var binMFileId, out var binMPathId, out var binMTrigger, out var binMEnabled, out var binMConvex, out var binMCooking))
                    {
                        properties = new ColliderExtractedProperties
                        {
                            Kind = ColliderKind.Mesh,
                            Enabled = binMEnabled,
                            IsTrigger = binMTrigger,
                            Convex = binMConvex,
                            CookingOptions = binMCooking,
                            MeshFileId = binMFileId,
                            MeshPathId = binMPathId
                        };
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static ColliderMeshGeometry? TryBuildBoxCollider(
        AssetStudio.Object colliderObj,
        string baseName,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider)
    {
        if (TryExtractColliderProperties(colliderObj, fieldProvider, out var props) && props.Kind == ColliderKind.Box)
        {
            return BuildBoxMesh($"{baseName}_Collider_Box", props.Center, props.Size, props.IsTrigger);
        }

        return null;
    }

    private static ColliderMeshGeometry? TryBuildSphereCollider(
        AssetStudio.Object colliderObj,
        string baseName,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider)
    {
        if (TryExtractColliderProperties(colliderObj, fieldProvider, out var props) && props.Kind == ColliderKind.Sphere)
        {
            return BuildSphereMesh($"{baseName}_Collider_Sphere", props.Center, props.Radius, props.IsTrigger);
        }

        return null;
    }

    private static ColliderMeshGeometry? TryBuildCapsuleCollider(
        AssetStudio.Object colliderObj,
        string baseName,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider)
    {
        if (TryExtractColliderProperties(colliderObj, fieldProvider, out var props) && props.Kind == ColliderKind.Capsule)
        {
            return BuildCapsuleMesh($"{baseName}_Collider_Capsule", props.Center, props.Radius, props.Height, props.Direction, props.IsTrigger);
        }

        return null;
    }

    private static ColliderMeshGeometry? TryBuildMeshCollider(
        AssetStudio.Object colliderObj,
        string baseName,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider,
        Func<PPtr<Mesh>, Mesh?>? meshResolver)
    {
        if (!TryExtractColliderProperties(colliderObj, fieldProvider, out var props)
            || props.Kind != ColliderKind.Mesh
            || props.MeshPathId == 0)
        {
            return null;
        }

        var meshPtr = new PPtr<Mesh>(props.MeshFileId, props.MeshPathId, colliderObj.assetsFile);
        Mesh? resolvedMesh = null;
        if (meshResolver != null)
        {
            try
            {
                resolvedMesh = meshResolver(meshPtr);
            }
            catch
            {
                resolvedMesh = null;
            }
        }

        if (resolvedMesh == null && !meshPtr.IsNull)
        {
            meshPtr.TryGet(out resolvedMesh);
        }

        return BuildMeshColliderMesh($"{baseName}_Collider_Mesh", resolvedMesh, props.IsTrigger);
    }

    private static OrderedDictionary? TryReadColliderFields(
        AssetStudio.Object colliderObj,
        Func<AssetStudio.Object, OrderedDictionary?>? fieldProvider)
    {
        if (fieldProvider != null)
        {
            try
            {
                var provided = fieldProvider(colliderObj);
                if (provided != null)
                {
                    return provided;
                }
            }
            catch
            {
            }
        }

        if (colliderObj.serializedType?.m_Type?.m_Nodes is { Count: > 0 })
        {
            try
            {
                return colliderObj.ToType(colliderObj.serializedType.m_Type);
            }
            catch
            {
            }
        }

        return null;
    }

    // =========================================================================
    // Safe Binary Fallback Readers (when TypeTree is stripped or reader closed)
    // =========================================================================

    private static byte[]? TryReadColliderRawBytes(
        AssetStudio.Object colliderObj,
        out EndianType endian,
        out SerializedFileFormatVersion formatVersion)
    {
        endian = colliderObj.reader != null
            ? colliderObj.reader.Endian
            : (colliderObj.assetsFile?.header?.m_Endianess == 0 ? EndianType.LittleEndian : EndianType.BigEndian);
        formatVersion = colliderObj.assetsFile?.header?.m_Version
            ?? colliderObj.reader?.m_Version
            ?? SerializedFileFormatVersion.LargeFilesSupport;

        // 1. Prefer Object.GetRawData() when SerializedFile.reader is live — safely clones file reader
        //    even after MaterializeSceneObjectsForFile disposes the per-object localReader!
        if (colliderObj.assetsFile?.reader != null && colliderObj.byteSize > 0 && colliderObj.byteSize <= 4096)
        {
            try
            {
                var raw = colliderObj.GetRawData();
                if (raw is { Length: > 0 })
                {
                    return raw;
                }
            }
            catch
            {
            }
        }

        // 2. Fallback to live ObjectReader (covers synthetic in-memory unit-test objects).
        var objReader = colliderObj.reader;
        if (objReader != null && objReader.byteSize > 0 && objReader.byteSize <= 4096)
        {
            try
            {
                if (objReader.BaseStream != null && objReader.BaseStream.CanRead)
                {
                    long savedPos = objReader.Position;
                    try
                    {
                        long start = objReader.byteStart;
                        if (start >= 0 && start + objReader.byteSize <= objReader.BaseStream.Length)
                        {
                            objReader.Position = start;
                            return objReader.ReadBytes((int)objReader.byteSize);
                        }
                    }
                    finally
                    {
                        objReader.Position = savedPos;
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool TryOpenColliderBinaryReader(
        AssetStudio.Object colliderObj,
        int expectedTailBytes,
        out EndianBinaryReader? reader,
        out long endOffset,
        out bool isTrigger,
        out bool enabled,
        out int pptrSize)
    {
        reader = null;
        endOffset = 0;
        isTrigger = false;
        enabled = true;
        pptrSize = 12;

        var rawBytes = TryReadColliderRawBytes(colliderObj, out var endian, out var formatVersion);
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return false;
        }

        pptrSize = formatVersion < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;
        var localReader = new EndianBinaryReader(new MemoryStream(rawBytes, writable: false), endian);
        long localEndOffset = rawBytes.Length;
        endOffset = localEndOffset;

        try
        {
            if (colliderObj.platform == BuildTarget.NoTarget)
            {
                if (!HasRemaining(localReader, localEndOffset, 4))
                {
                    localReader.Dispose();
                    return false;
                }
                localReader.Position += 4; // m_ObjectHideFlags
            }

            // m_GameObject (PPtr) + m_Material (PPtr)
            if (!HasRemaining(localReader, localEndOffset, pptrSize * 2))
            {
                localReader.Dispose();
                return false;
            }
            localReader.Position += pptrSize * 2;

            // Unity 2022.2+: m_IncludeLayers (4), m_ExcludeLayers (4), m_LayerOverridePriority (4)
            bool is2022_2OrUp = IsVersionAtLeast(colliderObj.version, 2022, 2);
            if (is2022_2OrUp)
            {
                if (!HasRemaining(localReader, localEndOffset, 12))
                {
                    localReader.Dispose();
                    return false;
                }
                localReader.Position += 12;
            }

            long flagsStart = localReader.Position;
            if (!HasRemaining(localReader, localEndOffset, 2))
            {
                localReader.Dispose();
                return false;
            }

            // Candidate A (Unity 2022.2+ / 6000.x): m_IsTrigger + m_ProvidesContacts + Align4 (4B), then m_Enabled + Align4 (4B).
            // Candidate B (Unity 5.x-2022.1): m_IsTrigger + m_Enabled + Align4 (4B).
            bool TrySeparateAlignedLayout(out bool trig, out bool en)
            {
                trig = false;
                en = true;
                localReader.Position = flagsStart;
                if (!HasRemaining(localReader, localEndOffset, 1)) return false;
                trig = localReader.ReadBoolean();
                if (is2022_2OrUp)
                {
                    if (!HasRemaining(localReader, localEndOffset, 1)) return false;
                    _ = localReader.ReadBoolean(); // m_ProvidesContacts
                }
                localReader.AlignStream();
                if (!HasRemaining(localReader, localEndOffset, 1)) return false;
                en = localReader.ReadBoolean();
                localReader.AlignStream();
                return localReader.Position <= localEndOffset;
            }

            bool TryPackedLayout(out bool trig, out bool en)
            {
                trig = false;
                en = true;
                localReader.Position = flagsStart;
                if (!HasRemaining(localReader, localEndOffset, 1)) return false;
                trig = localReader.ReadBoolean();
                if (is2022_2OrUp && HasRemaining(localReader, localEndOffset, 1))
                {
                    _ = localReader.ReadBoolean(); // m_ProvidesContacts
                }
                if (HasRemaining(localReader, localEndOffset, 1))
                {
                    en = localReader.ReadBoolean();
                }
                localReader.AlignStream();
                return localReader.Position <= localEndOffset;
            }

            if (is2022_2OrUp)
            {
                if (TrySeparateAlignedLayout(out var tSep, out var eSep)
                    && (expectedTailBytes <= 0 || localEndOffset - localReader.Position == expectedTailBytes))
                {
                    isTrigger = tSep;
                    enabled = eSep;
                    reader = localReader;
                    return true;
                }

                if (TryPackedLayout(out var tPack, out var ePack)
                    && (expectedTailBytes <= 0 || localEndOffset - localReader.Position == expectedTailBytes))
                {
                    isTrigger = tPack;
                    enabled = ePack;
                    reader = localReader;
                    return true;
                }

                if (TrySeparateAlignedLayout(out tSep, out eSep) && localEndOffset - localReader.Position >= expectedTailBytes)
                {
                    isTrigger = tSep;
                    enabled = eSep;
                    reader = localReader;
                    return true;
                }

                if (TryPackedLayout(out tPack, out ePack) && localEndOffset - localReader.Position >= expectedTailBytes)
                {
                    isTrigger = tPack;
                    enabled = ePack;
                    reader = localReader;
                    return true;
                }
            }
            else
            {
                if (TryPackedLayout(out var tPack, out var ePack)
                    && (expectedTailBytes <= 0 || localEndOffset - localReader.Position == expectedTailBytes))
                {
                    isTrigger = tPack;
                    enabled = ePack;
                    reader = localReader;
                    return true;
                }

                if (TrySeparateAlignedLayout(out var tSep, out var eSep)
                    && (expectedTailBytes <= 0 || localEndOffset - localReader.Position == expectedTailBytes))
                {
                    isTrigger = tSep;
                    enabled = eSep;
                    reader = localReader;
                    return true;
                }

                if (TryPackedLayout(out tPack, out ePack) && localEndOffset - localReader.Position >= expectedTailBytes)
                {
                    isTrigger = tPack;
                    enabled = ePack;
                    reader = localReader;
                    return true;
                }
            }

            localReader.Dispose();
            return false;
        }
        catch
        {
            localReader.Dispose();
            return false;
        }
    }

    private static bool TryReadBoxColliderBinary(
        AssetStudio.Object colliderObj,
        out Vector3 center,
        out Vector3 size,
        out bool isTrigger,
        out bool enabled)
    {
        center = Vector3.Zero;
        size = Vector3.Zero;
        isTrigger = false;
        enabled = true;

        if (!TryOpenColliderBinaryReader(colliderObj, expectedTailBytes: 24, out var reader, out var endOffset, out isTrigger, out enabled, out _) || reader == null)
        {
            return false;
        }

        using (reader)
        {
            if (!HasRemaining(reader, endOffset, 24))
            {
                return false;
            }

            size = reader.ReadVector3();
            center = reader.ReadVector3();
            return IsFiniteVector3(size) && IsFiniteVector3(center);
        }
    }

    private static bool TryReadSphereColliderBinary(
        AssetStudio.Object colliderObj,
        out Vector3 center,
        out float radius,
        out bool isTrigger,
        out bool enabled)
    {
        center = Vector3.Zero;
        radius = 0f;
        isTrigger = false;
        enabled = true;

        if (!TryOpenColliderBinaryReader(colliderObj, expectedTailBytes: 16, out var reader, out var endOffset, out isTrigger, out enabled, out _) || reader == null)
        {
            return false;
        }

        using (reader)
        {
            if (!HasRemaining(reader, endOffset, 16))
            {
                return false;
            }

            radius = reader.ReadSingle();
            center = reader.ReadVector3();
            return float.IsFinite(radius) && IsFiniteVector3(center);
        }
    }

    private static bool TryReadCapsuleColliderBinary(
        AssetStudio.Object colliderObj,
        out Vector3 center,
        out float radius,
        out float height,
        out int direction,
        out bool isTrigger,
        out bool enabled)
    {
        center = Vector3.Zero;
        radius = 0f;
        height = 0f;
        direction = 1;
        isTrigger = false;
        enabled = true;

        if (!TryOpenColliderBinaryReader(colliderObj, expectedTailBytes: 24, out var reader, out var endOffset, out isTrigger, out enabled, out _) || reader == null)
        {
            return false;
        }

        using (reader)
        {
            if (!HasRemaining(reader, endOffset, 24))
            {
                return false;
            }

            radius = reader.ReadSingle();
            height = reader.ReadSingle();
            direction = reader.ReadInt32();
            center = reader.ReadVector3();
            return float.IsFinite(radius) && float.IsFinite(height) && IsFiniteVector3(center);
        }
    }

    private static bool TryReadMeshColliderBinary(
        AssetStudio.Object colliderObj,
        out int meshFileId,
        out long meshPathId,
        out bool isTrigger,
        out bool enabled,
        out bool convex,
        out int cookingOptions)
    {
        meshFileId = 0;
        meshPathId = 0;
        isTrigger = false;
        enabled = true;
        convex = false;
        cookingOptions = 30;

        var formatVersion = colliderObj.assetsFile?.header?.m_Version
            ?? colliderObj.reader?.m_Version
            ?? SerializedFileFormatVersion.LargeFilesSupport;
        int pptrSizeEstimate = formatVersion < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;
        int expectedTail = 4 + (IsVersionAtLeast(colliderObj.version, 2017, 3) ? 4 : 0) + pptrSizeEstimate;

        if (!TryOpenColliderBinaryReader(colliderObj, expectedTail, out var reader, out var endOffset, out isTrigger, out enabled, out var pptrSize) || reader == null)
        {
            return false;
        }

        using (reader)
        {
            // m_Convex (bool) + align4
            if (!HasRemaining(reader, endOffset, 1))
            {
                return false;
            }
            convex = reader.ReadBoolean();
            reader.AlignStream();

            // Unity 2017.3+: m_CookingOptions (int32)
            if (IsVersionAtLeast(colliderObj.version, 2017, 3))
            {
                if (!HasRemaining(reader, endOffset, 4))
                {
                    return false;
                }
                cookingOptions = reader.ReadInt32();
            }

            if (!HasRemaining(reader, endOffset, pptrSize))
            {
                return false;
            }

            meshFileId = reader.ReadInt32();
            meshPathId = pptrSize >= 12 ? reader.ReadInt64() : reader.ReadInt32();
            return true;
        }
    }

    // =========================================================================
    // Geometry & Math Helpers
    // =========================================================================

    private static void AddBoxFace(
        Vector3[] positions,
        Vector3[] normals,
        int[] indices,
        ref int vCursor,
        ref int iCursor,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 normal)
    {
        int b = vCursor;
        positions[b] = p0;
        positions[b + 1] = p1;
        positions[b + 2] = p2;
        positions[b + 3] = p3;

        normals[b] = normal;
        normals[b + 1] = normal;
        normals[b + 2] = normal;
        normals[b + 3] = normal;

        indices[iCursor++] = b;
        indices[iCursor++] = b + 1;
        indices[iCursor++] = b + 2;

        indices[iCursor++] = b;
        indices[iCursor++] = b + 2;
        indices[iCursor++] = b + 3;

        vCursor += 4;
    }

    private static void AddOutwardTriangle(
        List<Vector3> positions,
        List<Vector3> normals,
        List<int> indices,
        int ia,
        int ib,
        int ic)
    {
        var faceCross = Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
        var avgNormal = normals[ia] + normals[ib] + normals[ic];

        if (Dot(faceCross, avgNormal) >= 0f)
        {
            indices.Add(ia);
            indices.Add(ib);
            indices.Add(ic);
        }
        else
        {
            indices.Add(ia);
            indices.Add(ic);
            indices.Add(ib);
        }
    }

    private static Vector3 Cross(Vector3 a, Vector3 b)
    {
        return new Vector3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    private static float Dot(Vector3 a, Vector3 b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    private static bool IsFiniteVector3(Vector3 v)
    {
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    private static bool HasRemaining(EndianBinaryReader reader, long endOffset, int requiredBytes)
    {
        return endOffset - reader.Position >= requiredBytes;
    }

    private static bool IsVersionAtLeast(int[]? version, int major, int minor)
    {
        if (version == null || version.Length == 0)
        {
            return false;
        }

        if (version[0] != major)
        {
            return version[0] > major;
        }

        int vMinor = version.Length > 1 ? version[1] : 0;
        return vMinor >= minor;
    }

    private static string FormatFloat(float value)
    {
        float safe = float.IsFinite(value) ? value : 0f;
        if (safe == 0f)
        {
            return "0";
        }
        return safe.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryExtractPPtr(IDictionary dict, string key, out int fileId, out long pathId)
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
        if (!dict.Contains(key) || dict[key] is not IDictionary vec)
        {
            return false;
        }

        bool hasX = TryReadFloat(vec, "x", out float x);
        bool hasY = TryReadFloat(vec, "y", out float y);
        bool hasZ = TryReadFloat(vec, "z", out float z);
        if (!hasX && !hasY && !hasZ)
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryReadFloat(IDictionary dict, string key, out float value)
    {
        value = 0f;
        if (!dict.Contains(key) || dict[key] == null)
        {
            return false;
        }

        try
        {
            value = Convert.ToSingle(dict[key], CultureInfo.InvariantCulture);
            return float.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadInt(IDictionary dict, string key, out int value)
    {
        value = 0;
        return dict.Contains(key) && TryConvertInt(dict[key], out value);
    }

    private static bool TryReadBool(IDictionary dict, string key, out bool value)
    {
        value = false;
        if (!dict.Contains(key) || dict[key] == null)
        {
            return false;
        }

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
