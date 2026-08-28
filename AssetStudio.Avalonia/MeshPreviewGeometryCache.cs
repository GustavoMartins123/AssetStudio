using System;
using System.IO;

namespace AssetStudio.Avalonia
{
using Matrix4 = OpenTK.Mathematics.Matrix4;
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

internal sealed class MeshPreviewGeometryCache
{
    public const int AlgorithmVersion = 3;

    public Matrix4 ModelMatrix { get; init; }
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    public int[] Indices { get; init; } = Array.Empty<int>();
    public Vector3[]? Normals { get; init; }
    public Vector3[]? CalculatedNormals { get; init; }
    public Vector4[] Colors { get; init; } = Array.Empty<Vector4>();
    public Vector2[]? Uvs { get; init; }
    public int[]? SubMeshIndexCounts { get; init; }
}

internal static class MeshPreviewGeometryCacheSerializer
{
    private const int Magic = 0x41534d50; // ASMP

    public static byte[] Serialize(MeshPreviewGeometryCache cache)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(MeshPreviewGeometryCache.AlgorithmVersion);
        WriteMatrix(writer, cache.ModelMatrix);
        WriteVector3Array(writer, cache.Vertices);
        WriteIntArray(writer, cache.Indices);
        WriteNullableVector3Array(writer, cache.Normals);
        WriteNullableVector3Array(writer, cache.CalculatedNormals);
        WriteVector4Array(writer, cache.Colors);
        WriteNullableVector2Array(writer, cache.Uvs);
        WriteNullableIntArray(writer, cache.SubMeshIndexCounts);

        writer.Flush();
        return stream.ToArray();
    }

    public static MeshPreviewGeometryCache Deserialize(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException("Invalid mesh preview cache payload.");
        }

        var version = reader.ReadInt32();
        if (version != MeshPreviewGeometryCache.AlgorithmVersion)
        {
            throw new InvalidDataException("Unsupported mesh preview cache payload version.");
        }

        return new MeshPreviewGeometryCache
        {
            ModelMatrix = ReadMatrix(reader),
            Vertices = ReadVector3Array(reader),
            Indices = ReadIntArray(reader),
            Normals = ReadNullableVector3Array(reader),
            CalculatedNormals = ReadNullableVector3Array(reader),
            Colors = ReadVector4Array(reader),
            Uvs = ReadNullableVector2Array(reader),
            SubMeshIndexCounts = ReadNullableIntArray(reader)
        };
    }

    private static void WriteMatrix(BinaryWriter writer, Matrix4 matrix)
    {
        writer.Write(matrix.M11);
        writer.Write(matrix.M12);
        writer.Write(matrix.M13);
        writer.Write(matrix.M14);
        writer.Write(matrix.M21);
        writer.Write(matrix.M22);
        writer.Write(matrix.M23);
        writer.Write(matrix.M24);
        writer.Write(matrix.M31);
        writer.Write(matrix.M32);
        writer.Write(matrix.M33);
        writer.Write(matrix.M34);
        writer.Write(matrix.M41);
        writer.Write(matrix.M42);
        writer.Write(matrix.M43);
        writer.Write(matrix.M44);
    }

    private static Matrix4 ReadMatrix(BinaryReader reader)
    {
        return new Matrix4(
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteVector3Array(BinaryWriter writer, Vector3[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }
    }

    private static Vector3[] ReadVector3Array(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var values = new Vector3[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        return values;
    }

    private static void WriteNullableVector3Array(BinaryWriter writer, Vector3[]? values)
    {
        if (values == null)
        {
            writer.Write(-1);
            return;
        }

        WriteVector3Array(writer, values);
    }

    private static Vector3[]? ReadNullableVector3Array(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            return null;
        }

        var values = new Vector3[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        return values;
    }

    private static void WriteVector4Array(BinaryWriter writer, Vector4[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
            writer.Write(value.W);
        }
    }

    private static Vector4[] ReadVector4Array(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var values = new Vector4[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        return values;
    }

    private static void WriteNullableVector2Array(BinaryWriter writer, Vector2[]? values)
    {
        if (values == null)
        {
            writer.Write(-1);
            return;
        }

        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
        }
    }

    private static Vector2[]? ReadNullableVector2Array(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            return null;
        }

        var values = new Vector2[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        return values;
    }

    private static void WriteIntArray(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static int[] ReadIntArray(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var values = new int[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return values;
    }

    private static void WriteNullableIntArray(BinaryWriter writer, int[]? values)
    {
        if (values == null)
        {
            writer.Write(-1);
            return;
        }

        WriteIntArray(writer, values);
    }

    private static int[]? ReadNullableIntArray(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            return null;
        }

        var values = new int[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return values;
    }
}
}
