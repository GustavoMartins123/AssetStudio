using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio
{
    public sealed record ShaderPreviewParameter(string Name, int Type, int Rows, int Columns, bool IsMatrix, int ArraySize, int Offset);
    public sealed record ShaderPreviewBuffer(string Name, int Slot, int Size, ShaderPreviewParameter[] Parameters);
    public sealed record ShaderPreviewTextureBinding(string Name, int Slot, int SamplerSlot);
    public sealed record ShaderPreviewStage(byte[] Bytecode, ShaderPreviewBuffer[] Buffers, ShaderPreviewTextureBinding[] Textures);

    public enum ShaderPreviewBlend { Zero, One, SrcColor, OneMinusSrcColor, DstColor, OneMinusDstColor, SrcAlpha, OneMinusSrcAlpha, DstAlpha, OneMinusDstAlpha, SrcAlphaSaturate }
    public enum ShaderPreviewBlendOp { Add, Sub, RevSub, Min, Max }
    public enum ShaderPreviewCull { Off = 1, Front, Back }
    public enum ShaderPreviewDepth { Never = 1, Less, Equal, LEqual, Greater, NotEqual, GEqual, Always, Off }

    public sealed record ShaderPreviewState
    {
        public ShaderPreviewCull Cull { get; init; } = ShaderPreviewCull.Back;
        public ShaderPreviewDepth ZTest { get; init; } = ShaderPreviewDepth.LEqual;
        public bool ZWrite { get; init; } = true;
        public bool Blend { get; init; }
        public ShaderPreviewBlend Source { get; init; } = ShaderPreviewBlend.One;
        public ShaderPreviewBlend Destination { get; init; } = ShaderPreviewBlend.Zero;
        public ShaderPreviewBlend SourceAlpha { get; init; } = ShaderPreviewBlend.One;
        public ShaderPreviewBlend DestinationAlpha { get; init; } = ShaderPreviewBlend.Zero;
        public ShaderPreviewBlendOp Operation { get; init; }
        public ShaderPreviewBlendOp AlphaOperation { get; init; }
        public byte ColorMask { get; init; } = 15;
        public float OffsetFactor { get; init; }
        public int OffsetUnits { get; init; }
    }

    public sealed record LegacyShaderPreviewPass(
        string ShaderName, string PassName, bool Plane, ShaderPreviewState State,
        IReadOnlyDictionary<string, NumericsVector4> Values, IReadOnlyDictionary<string, string> TextureDefaults,
        ShaderPreviewStage Vertex, ShaderPreviewStage Fragment);

    public static class LegacyShaderPreview
    {
        private const int MaxBlobBytes = 64 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static LegacyShaderPreviewPass Read(Shader shader, IReadOnlyCollection<string> keywords)
        {
            ArgumentNullException.ThrowIfNull(shader);
            ArgumentNullException.ThrowIfNull(keywords);
            if (shader.version.Length < 2 || shader.version[0] != 5 || shader.version[1] != 3)
            {
                throw new NotSupportedException("Shader rendering currently supports Unity 5.3 Built-in shaders compiled for Direct3D 11.");
            }
            if (shader.m_Script is not { Length: > 0 and <= MaxBlobBytes } script ||
                shader.m_SubProgramBlob is not { Length: > 0 } compressed ||
                shader.decompressedSize is 0 or > MaxBlobBytes)
            {
                throw new InvalidDataException("The Unity 5.3 shader does not contain a valid program blob.");
            }

            var lab = LegacyShaderLab.Parse(StrictUtf8.GetString(script));
            if (lab.VertexIndices.Count == 0 || lab.FragmentIndices.Count == 0)
            {
                throw new NotSupportedException("The shader pass has no Direct3D 11 vertex/pixel program pair.");
            }

            var bytes = new byte[shader.decompressedSize];
            if (LZ4Codec.Decode(compressed, bytes) != bytes.Length)
            {
                throw new InvalidDataException("The Unity shader program blob could not be decompressed.");
            }
            using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
            var entries = new (int Offset, int Length)[ReadCount(reader, bytes.Length / 8)];
            int tableEnd = checked(sizeof(int) + entries.Length * 2 * sizeof(int));
            for (int i = 0; i < entries.Length; i++)
            {
                int offset = reader.ReadInt32();
                int length = reader.ReadInt32();
                if (offset < tableEnd || (offset & 3) != 0 || length <= 0 || offset > bytes.Length - length)
                {
                    throw new InvalidDataException($"Invalid Unity shader program bounds at index {i}.");
                }
                entries[i] = (offset, length);
            }

            var enabled = new HashSet<string>(keywords, StringComparer.Ordinal);
            ProgramHeader vertex = SelectStage(lab.VertexIndices, true);
            ProgramHeader fragment = SelectStage(lab.FragmentIndices, false);
            if (!enabled.SetEquals(vertex.Keywords.Concat(fragment.Keywords)))
            {
                throw MissingVariant(enabled);
            }
            return new LegacyShaderPreviewPass(lab.Name, lab.PassName, lab.Plane, lab.State, lab.Values,
                lab.TextureDefaults, ReadStage(bytes, vertex), ReadStage(bytes, fragment));

            ProgramHeader SelectStage(IReadOnlyList<int> indices, bool isVertex)
            {
                var programs = indices.Select(i => ReadHeader(bytes, entries, i, isVertex)).ToArray();
                var stageKeywords = new HashSet<string>(programs.SelectMany(p => p.Keywords), StringComparer.Ordinal);
                stageKeywords.IntersectWith(enabled);
                var matches = programs.Where(p => stageKeywords.SetEquals(p.Keywords)).ToArray();
                if (matches.Length == 0)
                {
                    throw MissingVariant(enabled);
                }
                if (matches.Length > 1)
                {
                    throw new NotSupportedException("Multiple Direct3D 11 programs match the same stage and keyword set.");
                }
                return matches[0];
            }
        }

        private static NotSupportedException MissingVariant(HashSet<string> enabled) =>
            new($"The exact Direct3D 11 variant is missing for keywords [{string.Join(", ", enabled.Order(StringComparer.Ordinal))}].");

        private sealed record ProgramHeader(int Offset, int Length, int CodePosition, string[] Keywords);

        private static ProgramHeader ReadHeader(byte[] bytes, (int Offset, int Length)[] entries, int index, bool vertex)
        {
            if ((uint)index >= (uint)entries.Length)
            {
                throw new InvalidDataException($"Invalid GpuProgramIndex {index}.");
            }
            var (offset, length) = entries[index];
            using var reader = new BinaryReader(new MemoryStream(bytes, offset, length, writable: false));
            if (reader.ReadInt32() != 201509030)
            {
                throw new NotSupportedException("This Unity 5.3 shader program layout is unsupported.");
            }
            var kind = (ShaderGpuProgramType)reader.ReadInt32();
            bool expected = vertex
                ? kind is ShaderGpuProgramType.DX11VertexSM40 or ShaderGpuProgramType.DX11VertexSM50
                : kind is ShaderGpuProgramType.DX11PixelSM40 or ShaderGpuProgramType.DX11PixelSM50;
            if (!expected)
            {
                throw new InvalidDataException($"GpuProgramIndex {index} contains {kind} in an unexpected stage.");
            }
            reader.BaseStream.Seek(3 * sizeof(int), SeekOrigin.Current);
            var keywords = new string[ReadCount(reader, 256)];
            for (int i = 0; i < keywords.Length; i++)
            {
                keywords[i] = ReadString(reader);
            }
            return new ProgramHeader(offset, length, checked((int)reader.BaseStream.Position), keywords);
        }

        private static ShaderPreviewStage ReadStage(byte[] bytes, ProgramHeader header)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, header.Offset, header.Length, writable: false));
            reader.BaseStream.Position = header.CodePosition;
            var code = ReadField(reader, MaxBlobBytes);
            const int unityDxbcPrefixBytes = 5;
            if (code.Length < unityDxbcPrefixBytes + 32 || code[0] != 1 ||
                !code.AsSpan(unityDxbcPrefixBytes, 4).SequenceEqual("DXBC"u8))
            {
                throw new InvalidDataException("The selected stage does not contain a Unity 5.3 Direct3D 11 DXBC program.");
            }

            // Unity stores binding metadata after the DXBC container, including when RDEF is stripped.
            reader.ReadUInt32(); // source-channel mask
            int channelCount = ReadCount(reader, 32);
            for (int i = 0; i < channelCount; i++)
            {
                reader.ReadUInt32(); // source channel
                reader.ReadUInt32(); // destination channel
            }
            int groupCount = ReadCount(reader, 64);
            var groups = new Dictionary<string, (int Size, ShaderPreviewParameter[] Parameters)>(StringComparer.Ordinal);
            for (int i = 0; i < groupCount; i++)
            {
                string name = ReadString(reader);
                int size = ReadCount(reader, 65536);
                var parameters = new ShaderPreviewParameter[ReadCount(reader, 4096)];
                for (int j = 0; j < parameters.Length; j++)
                {
                    parameters[j] = new ShaderPreviewParameter(ReadString(reader), reader.ReadInt32(),
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() != 0, reader.ReadInt32(), reader.ReadInt32());
                }
                if (!groups.TryAdd(name, (size, parameters)))
                {
                    throw new InvalidDataException($"Duplicate Unity shader parameter group '{name}'.");
                }
            }

            int resourceCount = ReadCount(reader, 160);
            var buffers = new List<ShaderPreviewBuffer>();
            var textures = new List<ShaderPreviewTextureBinding>();
            for (int i = 0; i < resourceCount; i++)
            {
                string name = ReadString(reader);
                int kind = reader.ReadInt32();
                int slot = reader.ReadInt32();
                int extra = reader.ReadInt32();
                if (kind == 0 && (extra & 0xff) == 2 && slot is >= 0 and < 128 && (extra >> 8) is >= 0 and < 16)
                {
                    textures.Add(new ShaderPreviewTextureBinding(name, slot, extra >> 8));
                }
                else if (kind == 1 && slot is >= 0 and < 14 && groups.Remove(name, out var group) && group.Size > 0)
                {
                    buffers.Add(new ShaderPreviewBuffer(name, slot, group.Size, group.Parameters));
                }
                else
                {
                    throw new NotSupportedException($"Unsupported Unity shader resource '{name}' (type {kind}, slot {slot}).");
                }
            }
            if (groups.Values.Any(group => group.Parameters.Length != 0) || reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException("The Unity shader stage contains unbound parameters or extra metadata.");
            }
            return new ShaderPreviewStage(code[unityDxbcPrefixBytes..], buffers.ToArray(), textures.ToArray());
        }

        private static int ReadCount(BinaryReader reader, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum)
            {
                throw new InvalidDataException($"Invalid Unity shader field length {count}.");
            }
            return count;
        }

        private static string ReadString(BinaryReader reader) => StrictUtf8.GetString(ReadField(reader, 4096));

        private static byte[] ReadField(BinaryReader reader, int maximum)
        {
            int count = ReadCount(reader, maximum);
            int padding = (4 - count % 4) % 4;
            if (count + padding > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new EndOfStreamException("The Unity shader program ended inside an aligned field.");
            }
            byte[] value = reader.ReadBytes(count);
            reader.BaseStream.Seek(padding, SeekOrigin.Current);
            return value;
        }
    }
}
