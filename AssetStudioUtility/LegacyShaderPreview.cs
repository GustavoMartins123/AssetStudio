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
    public enum ShaderPreviewTextureDimension { Texture2D = 2, Cube = 4 }
    public sealed record ShaderPreviewTextureBinding(string Name, int Slot, int SamplerSlot, ShaderPreviewTextureDimension Dimension);
    public sealed record ShaderPreviewStructuredBufferBinding(string Name, int Slot);
    public enum ShaderPreviewProgramKind { Pixel, Vertex, Geometry }
    public sealed record ShaderPreviewStage(byte[] Bytecode, ShaderPreviewBuffer[] Buffers, ShaderPreviewTextureBinding[] Textures)
    {
        public ShaderPreviewStructuredBufferBinding[] StructuredBuffers { get; init; } = [];
    }

    public enum ShaderPreviewBlend { Zero, One, SrcColor, OneMinusSrcColor, DstColor, OneMinusDstColor, SrcAlpha, OneMinusSrcAlpha, DstAlpha, OneMinusDstAlpha, SrcAlphaSaturate }
    public enum ShaderPreviewBlendOp { Add, Sub, RevSub, Min, Max }
    public enum ShaderPreviewCull { Off = 1, Front, Back }
    public enum ShaderPreviewDepth { Never = 1, Less, Equal, LEqual, Greater, NotEqual, GEqual, Always, Off }
    public enum ShaderPreviewStencilOp { Keep = 1, Zero, Replace, IncrSat, DecrSat, Invert, IncrWrap, DecrWrap }

    public sealed record ShaderPreviewStencilFace(ShaderPreviewDepth Function, ShaderPreviewStencilOp Pass,
        ShaderPreviewStencilOp Fail, ShaderPreviewStencilOp DepthFail);
    public sealed record ShaderPreviewStencil(byte Reference, byte ReadMask, byte WriteMask,
        ShaderPreviewStencilFace Front, ShaderPreviewStencilFace Back);

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
        public ShaderPreviewStencil? Stencil { get; init; }
        public bool AlphaToMask { get; init; }
    }

    public sealed record ShaderPreviewPassDefinition(PassType Type, string Name, string LightMode,
        string RenderState, IReadOnlyList<int> VertexIndices, IReadOnlyList<int> FragmentIndices, bool CaptureOnce = false)
    {
        public IReadOnlyList<int> GeometryIndices { get; init; } = [];
        public bool HasDirect3D11 => VertexIndices.Count != 0 && FragmentIndices.Count != 0;
        public string DisplayName => Name.Length == 0 ? LightMode : $"{Name} ({LightMode})";
    }

    public sealed record ShaderPreviewSubShader(IReadOnlyDictionary<string, string> Tags, string RenderState,
        IReadOnlyList<ShaderPreviewPassDefinition> Passes);

    public sealed record LegacyShaderPreviewPass(
        string ShaderName, string PassName, string LightMode, IReadOnlyList<string> Keywords, ShaderPreviewState State,
        ShaderPreviewStage Vertex, ShaderPreviewStage Fragment)
    {
        public ShaderPreviewStage? Geometry { get; init; }
        public IEnumerable<ShaderPreviewStage> Stages
        {
            get
            {
                yield return Vertex;
                if (Geometry != null)
                {
                    yield return Geometry;
                }
                yield return Fragment;
            }
        }
    }

    public sealed class LegacyShaderPreview
    {
        private const int MaxBlobBytes = 64 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly LegacyShaderLab _lab;
        private readonly Shader _shader;
        private readonly Dictionary<(int, ShaderPreviewProgramKind), ProgramHeader> _headers = new();
        private byte[]? _bytes;
        private (int Offset, int Length)[]? _entries;

        private LegacyShaderPreview(Shader shader, LegacyShaderLab lab)
        {
            _shader = shader;
            _lab = lab;
        }

        public string Name => _lab.Name;
        public IReadOnlyDictionary<string, NumericsVector4> Values => _lab.Values;
        public IReadOnlyDictionary<string, string> TextureDefaults => _lab.TextureDefaults;
        public IReadOnlyDictionary<string, string> Dependencies => _lab.Dependencies;
        public IReadOnlySet<string> PropertyKeywords => _lab.PropertyKeywords;
        public IReadOnlyList<ShaderPreviewSubShader> SubShaders => _lab.SubShaders;
        public string? DeclaredFallback => _lab.DeclaredFallback;

        public static LegacyShaderPreview Read(Shader shader)
        {
            ArgumentNullException.ThrowIfNull(shader);
            if (shader.version.Length < 2 || shader.version[0] != 5 || shader.version[1] != 3)
            {
                throw new NotSupportedException("Shader rendering currently supports Unity 5.3 Built-in shaders compiled for Direct3D 11.");
            }
            if (shader.m_Script is not { Length: > 0 and <= MaxBlobBytes } script)
            {
                throw new InvalidDataException("The Unity 5.3 shader has no compiled ShaderLab header.");
            }
            var lab = LegacyShaderLab.Parse(StrictUtf8.GetString(script));
            if (lab.SubShaders.Count == 0 && lab.DeclaredFallback is { } declared)
            {
                throw new NotSupportedException($"'{lab.Name}' defines no passes and declares Fallback '{declared}'.");
            }
            if (shader.m_SubProgramBlob is not { Length: > 0 } ||
                shader.decompressedSize is 0 or > MaxBlobBytes)
            {
                throw new InvalidDataException("The Unity 5.3 shader does not contain a valid program blob.");
            }

            return new LegacyShaderPreview(shader, lab);
        }

        public static string ReadName(Shader shader)
        {
            if (shader.m_Script is not { Length: > 0 and <= MaxBlobBytes } script)
            {
                throw new InvalidDataException("The shader has no compiled ShaderLab header.");
            }
            return LegacyShaderLab.ReadShaderName(StrictUtf8.GetString(script));
        }

        public IEnumerable<string> GetKeywords(ShaderPreviewPassDefinition pass) =>
            pass.VertexIndices.SelectMany(index => GetHeader(index, true).Keywords)
                .Concat(pass.FragmentIndices.SelectMany(index => GetHeader(index, false).Keywords))
                .Concat(pass.GeometryIndices.SelectMany(index => GetProgramHeader(index, ShaderPreviewProgramKind.Geometry).Keywords));

        public IEnumerable<IReadOnlyList<string>> GetVariantKeywords(ShaderPreviewPassDefinition pass, bool vertex) =>
            (vertex ? pass.VertexIndices : pass.FragmentIndices).Select(index => (IReadOnlyList<string>)GetHeader(index, vertex).Keywords);

        public LegacyShaderPreviewPass PreparePass(ShaderPreviewSubShader subShader, ShaderPreviewPassDefinition pass,
            IReadOnlySet<string> enabledKeywords, IReadOnlyDictionary<string, NumericsVector4> values)
        {
            if (!pass.HasDirect3D11)
            {
                throw new NotSupportedException($"Pass '{pass.DisplayName}' has no Direct3D 11 vertex/pixel program pair.");
            }
            ProgramHeader vertex = SelectStage(pass.VertexIndices, ShaderPreviewProgramKind.Vertex);
            ProgramHeader fragment = SelectStage(pass.FragmentIndices, ShaderPreviewProgramKind.Pixel);
            ProgramHeader? geometry = pass.GeometryIndices.Count > 0 ? SelectStage(pass.GeometryIndices, ShaderPreviewProgramKind.Geometry) : null;
            var keywords = vertex.Keywords.Concat(fragment.Keywords).Concat(geometry?.Keywords ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new LegacyShaderPreviewPass(Name, pass.Name, pass.LightMode, keywords,
                LegacyShaderLab.EvaluateState(subShader.RenderState, pass.RenderState, values),
                ReadStage(_bytes!, vertex), ReadStage(_bytes!, fragment))
            {
                Geometry = geometry == null ? null : ReadStage(_bytes!, geometry)
            };

            ProgramHeader SelectStage(IReadOnlyList<int> indices, ShaderPreviewProgramKind kind)
            {
                var programs = indices.Select(i => GetProgramHeader(i, kind)).ToArray();
                var expected = new HashSet<string>(programs.SelectMany(p => p.Keywords), StringComparer.Ordinal);
                expected.IntersectWith(enabledKeywords);
                var matches = programs.Where(p => expected.SetEquals(p.Keywords)).Distinct().ToArray();
                string stage = kind.ToString().ToLowerInvariant();
                if (matches.Length == 0)
                {
                    throw new NotSupportedException($"Pass '{pass.DisplayName}' has no exact D3D11 {stage} variant for [{string.Join(", ", expected.Order(StringComparer.Ordinal))}].");
                }
                if (matches.Length > 1 && matches.Skip(1).Any(other =>
                    !_bytes!.AsSpan(matches[0].Offset, matches[0].Length).SequenceEqual(_bytes.AsSpan(other.Offset, other.Length))))
                {
                    throw new NotSupportedException($"Pass '{pass.DisplayName}' has ambiguous D3D11 {stage} variants for [{string.Join(", ", expected.Order(StringComparer.Ordinal))}].");
                }
                return matches[0];
            }
        }

        private ProgramHeader GetHeader(int index, bool vertex) =>
            GetProgramHeader(index, vertex ? ShaderPreviewProgramKind.Vertex : ShaderPreviewProgramKind.Pixel);

        private ProgramHeader GetProgramHeader(int index, ShaderPreviewProgramKind stage)
        {
            if (_entries == null)
            {
                ReadBlob();
            }
            if (!_headers.TryGetValue((index, stage), out var header))
            {
                header = ReadHeader(_bytes!, _entries!, index, stage);
                _headers.Add((index, stage), header);
            }
            return header;
        }

        private void ReadBlob()
        {
            var bytes = new byte[_shader.decompressedSize];
            if (LZ4Codec.Decode(_shader.m_SubProgramBlob!, bytes) != bytes.Length)
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

            _bytes = bytes;
            _entries = entries;
        }

        private sealed record ProgramHeader(int Offset, int Length, int CodePosition, string[] Keywords);

        private static ProgramHeader ReadHeader(byte[] bytes, (int Offset, int Length)[] entries, int index, ShaderPreviewProgramKind stage)
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
            bool expected = stage switch
            {
                ShaderPreviewProgramKind.Vertex => kind is ShaderGpuProgramType.DX11VertexSM40 or ShaderGpuProgramType.DX11VertexSM50,
                ShaderPreviewProgramKind.Pixel => kind is ShaderGpuProgramType.DX11PixelSM40 or ShaderGpuProgramType.DX11PixelSM50,
                ShaderPreviewProgramKind.Geometry => kind is ShaderGpuProgramType.DX11GeometrySM40 or ShaderGpuProgramType.DX11GeometrySM50,
                _ => false
            };
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
            var structured = new List<ShaderPreviewStructuredBufferBinding>();
            for (int i = 0; i < resourceCount; i++)
            {
                string name = ReadString(reader);
                int kind = reader.ReadInt32();
                int slot = reader.ReadInt32();
                int extra = reader.ReadInt32();
                var dimension = (ShaderPreviewTextureDimension)(extra & 0xff);
                if (kind == 0 && dimension is ShaderPreviewTextureDimension.Texture2D or ShaderPreviewTextureDimension.Cube
                    && slot is >= 0 and < 128 && (extra >> 8) is >= -1 and < 16)
                {
                    textures.Add(new ShaderPreviewTextureBinding(name, slot, extra >> 8, dimension));
                }
                else if (kind == 1 && slot is >= 0 and < 14 && groups.Remove(name, out var group) && group.Size > 0)
                {
                    buffers.Add(new ShaderPreviewBuffer(name, slot, group.Size, group.Parameters));
                }
                else if (kind == 2 && slot is >= 0 and < 128 && extra == 0)
                {
                    structured.Add(new ShaderPreviewStructuredBufferBinding(name, slot));
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
            return new ShaderPreviewStage(code[unityDxbcPrefixBytes..], buffers.ToArray(), textures.ToArray()) { StructuredBuffers = structured.ToArray() };
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
