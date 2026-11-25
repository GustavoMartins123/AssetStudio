using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using K4os.Compression.LZ4;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio
{
    internal sealed class SerializedShaderPreview : ShaderPreviewReader
    {
        private const int PreviewHardwareTier = 3;
        private readonly Shader _shader;
        private readonly SerializedShader _form;
        private readonly Dictionary<string, NumericsVector4> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _textureDefaults = new(StringComparer.Ordinal);
        private readonly HashSet<string> _propertyKeywords = new(StringComparer.Ordinal);
        private readonly List<string> _defaultKeywords = [];
        private readonly Lazy<ProgramTable> _programs;

        private sealed record ProgramTable(Lazy<byte[]>[] Segments, int EntryCount, int EntrySize, int TableEnd);

        internal SerializedShaderPreview(Shader shader)
        {
            _shader = shader;
            _form = shader.m_ParsedForm ?? throw new InvalidDataException("The shader has no serialized ShaderLab data.");
            if (!_shader.platforms.Contains(ShaderCompilerPlatform.D3D11))
            {
                throw new NotSupportedException($"'{Name}' has no compiled Direct3D 11 programs.");
            }
            if (_form.m_SubShaders.Length == 0 && _form.m_FallbackName.Length > 0)
            {
                throw new NotSupportedException($"'{Name}' defines no passes and declares Fallback '{_form.m_FallbackName}'.");
            }

            foreach (var property in _form.m_PropInfo.m_Props)
            {
                if (property.m_Type == SerializedPropertyType.Texture)
                {
                    _textureDefaults.Add(property.m_Name, property.m_DefTexture.m_DefaultName);
                }
                else
                {
                    float[] value = property.m_DefValue;
                    _values.Add(property.m_Name, property.m_Type switch
                    {
                        SerializedPropertyType.Color or SerializedPropertyType.Vector => new NumericsVector4(value[0], value[1], value[2], value[3]),
                        SerializedPropertyType.Float or SerializedPropertyType.Range or SerializedPropertyType.Int => new NumericsVector4(value[0], 0, 0, 0),
                        _ => throw new NotSupportedException($"Unsupported type '{property.m_Type}' for shader property '{property.m_Name}'.")
                    });
                }
                foreach (string raw in property.m_Attributes)
                {
                    int open = raw.IndexOf('(');
                    string attribute = open >= 0 ? raw[..open].TrimEnd() : raw;
                    string[] arguments = open >= 0 && raw.EndsWith(')')
                        ? raw[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries) : [];
                    float? defaultValue = property.m_Type is SerializedPropertyType.Float or SerializedPropertyType.Range or SerializedPropertyType.Int
                        ? property.m_DefValue[0] : null;
                    if (AddPropertyKeywords(_propertyKeywords, property.m_Name, attribute, arguments, defaultValue) is { } keyword)
                    {
                        _defaultKeywords.Add(keyword);
                    }
                }
            }

            Dependencies = _form.m_Dependencies.ToDictionary(dependency => dependency.from, dependency => dependency.to, StringComparer.Ordinal);
            SubShaders = _form.m_SubShaders.Select(subShader => new ShaderPreviewSubShader(
                ReadTags(subShader.m_Tags), "", subShader.m_Passes.Select(ReadPass).ToArray())).ToArray();
            _programs = new Lazy<ProgramTable>(ReadProgramTable);
        }

        public override string Name => _form.m_Name;
        public override IReadOnlyDictionary<string, NumericsVector4> Values => _values;
        public override IReadOnlyDictionary<string, string> TextureDefaults => _textureDefaults;
        public override IReadOnlyDictionary<string, string> Dependencies { get; }
        public override IReadOnlySet<string> PropertyKeywords => _propertyKeywords;
        public override IReadOnlyList<string> DefaultKeywords => _defaultKeywords;
        public override IReadOnlyList<ShaderPreviewSubShader> SubShaders { get; }

        private sealed record Variant(SerializedSubProgram Program, SerializedProgramParameters? CommonParameters, string[] Keywords);
        private sealed record ConstantBufferLayout(string Name, int Size, bool IsPartial, ShaderPreviewParameter[] Parameters);
        private sealed record EmbeddedBindings(IReadOnlyList<ConstantBufferLayout> Layouts, IReadOnlyList<(string Name, int Slot)> Buffers,
            IReadOnlyList<ShaderPreviewTextureBinding> Textures, IReadOnlyList<ShaderPreviewStructuredBufferBinding> StructuredBuffers)
        {
            internal static readonly EmbeddedBindings Empty = new([], [], [], []);
        }

        private sealed record Pass(string PassName, string Mode, SerializedPass Source, IReadOnlyDictionary<int, string> Names,
            Variant[] Vertex, Variant[] Pixel, Variant[] Geometry, Variant[] Hull, Variant[] Domain) :
            ShaderPreviewPassDefinition(PassType.Normal, PassName, Mode, "", [], [])
        {
            public override bool HasDirect3D11 => Vertex.Length != 0 && Pixel.Length != 0;

            public Variant[] GetVariants(ShaderPreviewProgramKind kind) => kind switch
            {
                ShaderPreviewProgramKind.Vertex => Vertex,
                ShaderPreviewProgramKind.Pixel => Pixel,
                ShaderPreviewProgramKind.Geometry => Geometry,
                ShaderPreviewProgramKind.Hull => Hull,
                ShaderPreviewProgramKind.Domain => Domain,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        private static ShaderPreviewPassDefinition ReadPass(SerializedPass source)
        {
            switch (source.m_Type)
            {
                case PassType.Use:
                    return new ShaderPreviewPassDefinition(PassType.Use, source.m_UseName, "", "", [], []);
                case PassType.Grab:
                    bool named = source.m_TextureName.Length != 0;
                    return new ShaderPreviewPassDefinition(PassType.Grab, named ? source.m_TextureName : "_GrabTexture", "", "", [], [], named);
                case PassType.Normal:
                    break;
                default:
                    throw new InvalidDataException($"Unknown serialized ShaderLab pass type '{source.m_Type}'.");
            }

            var tags = ReadTags(source.m_State.m_Tags.tags.Length != 0 ? source.m_State.m_Tags : source.m_Tags);
            var names = source.m_NameIndices.ToDictionary(pair => pair.Value, pair => pair.Key);
            return new Pass(source.m_State.m_Name, tags.GetValueOrDefault("LightMode", "Always"), source, names,
                Variants(source.progVertex, ShaderPreviewProgramKind.Vertex), Variants(source.progFragment, ShaderPreviewProgramKind.Pixel),
                Variants(source.progGeometry, ShaderPreviewProgramKind.Geometry), Variants(source.progHull, ShaderPreviewProgramKind.Hull),
                Variants(source.progDomain, ShaderPreviewProgramKind.Domain));

            Variant[] Variants(SerializedProgram program, ShaderPreviewProgramKind kind) => program.m_SubPrograms
                .Where(sub => IsDirect3D11Stage(sub.m_GpuProgramType, kind))
                .Select(sub => new Variant(sub, program.m_CommonParameters,
                    sub.m_KeywordIndices.Select(index => ReadNameIndex(names, index)).ToArray())).ToArray();
        }

        private static Dictionary<string, string> ReadTags(SerializedTagMap tags) =>
            tags.tags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        private static string ReadNameIndex(IReadOnlyDictionary<int, string> names, int index) => names.TryGetValue(index, out var name)
            ? name : throw new InvalidDataException($"The shader pass has no name for index {index}.");

        protected override IEnumerable<IReadOnlyList<string>> GetVariantKeywords(ShaderPreviewPassDefinition pass, ShaderPreviewProgramKind kind) =>
            pass is Pass definition ? definition.GetVariants(kind).Select(variant => variant.Keywords) : [];

        public override LegacyShaderPreviewPass PreparePass(ShaderPreviewSubShader subShader, ShaderPreviewPassDefinition pass,
            IReadOnlySet<string> enabledKeywords, IReadOnlyDictionary<string, NumericsVector4> values)
        {
            if (pass is not Pass { HasDirect3D11: true } definition)
            {
                throw new NotSupportedException($"Pass '{pass.DisplayName}' has no Direct3D 11 vertex/pixel program pair.");
            }
            if ((definition.Hull.Length == 0) != (definition.Domain.Length == 0))
            {
                throw new InvalidDataException($"Pass '{pass.DisplayName}' has an incomplete Direct3D 11 hull/domain program pair.");
            }

            var vertex = SelectStage(ShaderPreviewProgramKind.Vertex);
            var pixel = SelectStage(ShaderPreviewProgramKind.Pixel);
            var hull = definition.Hull.Length == 0 ? null : SelectStage(ShaderPreviewProgramKind.Hull);
            var domain = definition.Domain.Length == 0 ? null : SelectStage(ShaderPreviewProgramKind.Domain);
            var geometry = definition.Geometry.Length != 0 ? SelectStage(ShaderPreviewProgramKind.Geometry) : null;
            Variant?[] stages = [vertex, hull, domain, geometry, pixel];
            var keywords = stages.OfType<Variant>().SelectMany(stage => stage.Keywords)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new LegacyShaderPreviewPass(Name, pass.Name, pass.LightMode, keywords,
                SerializedShaderPreviewState.Read(definition.Source.m_State, values),
                ReadStage(vertex, definition.Names), ReadStage(pixel, definition.Names))
            {
                Geometry = geometry == null ? null : ReadStage(geometry, definition.Names),
                Hull = hull == null ? null : ReadStage(hull, definition.Names),
                Domain = domain == null ? null : ReadStage(domain, definition.Names)
            };

            Variant SelectStage(ShaderPreviewProgramKind kind)
            {
                var matches = MatchVariants(definition.GetVariants(kind), variant => variant.Keywords, enabledKeywords, pass, kind);
                var eligible = matches.Where(variant => variant.Program.m_ShaderHardwareTier is 0 or PreviewHardwareTier).ToArray();
                if (eligible.Length == 0)
                {
                    throw new NotSupportedException($"Pass '{pass.DisplayName}' has no matching D3D11 {kind} program for hardware tier {PreviewHardwareTier}.");
                }
                if (eligible.Length > 1)
                {
                    throw AmbiguousVariant(pass, kind, matches[0].Keywords);
                }
                return eligible[0];
            }
        }

        private ProgramTable ReadProgramTable()
        {
            int api = Array.IndexOf(_shader.platforms, ShaderCompilerPlatform.D3D11);
            if (api < 0 || _shader.compressedBlob is not { } compressed || api >= _shader.offsets.Length ||
                api >= _shader.compressedLengths.Length || api >= _shader.decompressedLengths.Length)
            {
                throw new InvalidDataException("Missing serialized Direct3D 11 program data.");
            }
            var offsets = _shader.offsets[api];
            var lengths = _shader.compressedLengths[api];
            var sizes = _shader.decompressedLengths[api];
            int entrySize = ShaderSubProgramEntry.SerializedSize(_shader.version);
            if (offsets.Length == 0 || lengths.Length != offsets.Length || sizes.Length != offsets.Length ||
                (entrySize == 2 * sizeof(int) && offsets.Length != 1))
            {
                throw new InvalidDataException("Invalid serialized Direct3D 11 program segment table.");
            }
            var segments = new Lazy<byte[]>[offsets.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                int segment = i;
                segments[i] = new Lazy<byte[]>(() => ReadSegment(compressed, offsets[segment], lengths[segment], sizes[segment], segment));
            }
            var table = segments[0].Value;
            if (table.Length < sizeof(int))
            {
                throw new InvalidDataException("Truncated serialized Direct3D 11 program table.");
            }
            int count = BinaryPrimitives.ReadInt32LittleEndian(table);
            if (count < 0 || count > (table.Length - sizeof(int)) / entrySize)
            {
                throw new InvalidDataException("Invalid serialized shader program table size.");
            }
            return new ProgramTable(segments, count, entrySize, sizeof(int) + count * entrySize);
        }

        private static byte[] ReadSegment(byte[] compressed, uint offset, uint length, uint size, int segment)
        {
            if (size is 0 or > MaxBlobBytes || length == 0 || (ulong)offset + length > (ulong)compressed.Length)
            {
                throw new InvalidDataException($"Invalid bounds for serialized Direct3D 11 program segment {segment}.");
            }
            var bytes = new byte[size];
            if (LZ4Codec.Decode(compressed.AsSpan((int)offset, (int)length), bytes) != bytes.Length)
            {
                throw new InvalidDataException($"Serialized Direct3D 11 program segment {segment} could not be decompressed.");
            }
            return bytes;
        }

        private ShaderPreviewStage ReadStage(Variant variant, IReadOnlyDictionary<int, string> names)
        {
            var sub = variant.Program;
            var programs = _programs.Value;
            uint index = sub.m_BlobIndex;
            if (index >= programs.EntryCount)
            {
                throw new InvalidDataException($"Invalid serialized GPU program index {index}.");
            }
            int entryOffset = sizeof(int) + (int)index * programs.EntrySize;
            using var entryReader = new BinaryReader(new MemoryStream(programs.Segments[0].Value, entryOffset, programs.EntrySize, writable: false));
            var entry = new ShaderSubProgramEntry(entryReader, _shader.version);
            if ((uint)entry.Segment >= (uint)programs.Segments.Length)
            {
                throw new InvalidDataException($"Invalid segment {entry.Segment} for serialized GPU program {index}.");
            }
            var bytes = programs.Segments[entry.Segment].Value;
            int minOffset = entry.Segment == 0 ? programs.TableEnd : 0;
            if (entry.Offset < minOffset || (entry.Offset & 3) != 0 ||
                entry.Length <= 0 || entry.Offset > bytes.Length - entry.Length)
            {
                throw new InvalidDataException($"Invalid bounds for serialized GPU program {index}.");
            }
            using var reader = new BinaryReader(new MemoryStream(bytes, entry.Offset, entry.Length, writable: false));
            var code = new ShaderSubProgram(reader);
            const int extendedHeaderBytes = 38;
            // Unity 2020.3 can store a larger Direct3D 11 header before the DXBC container.
            int prefixBytes = code.m_ProgramCode switch
            {
                [1, ..] => 6,
                [2, ..] => extendedHeaderBytes,
                _ => throw new InvalidDataException($"GPU program {index} has an unsupported Direct3D 11 program header.")
            };
            if (code.m_ProgramType != sub.m_GpuProgramType || code.m_ProgramCode.Length < prefixBytes + 32 ||
                !code.m_ProgramCode.AsSpan(prefixBytes, 4).SequenceEqual("DXBC"u8))
            {
                throw new InvalidDataException($"GPU program {index} does not contain the declared Direct3D 11 stage.");
            }
            var specific = sub.m_Parameters;
            var common = variant.CommonParameters;
            if ((specific == null) != (common == null))
            {
                throw new InvalidDataException("The stage has an incomplete common and per-variant parameter layout.");
            }
            if (Parameters(sub.m_VectorParams, p => p.m_VectorParams).Any() ||
                Parameters(sub.m_MatrixParams, p => p.m_MatrixParams).Any() ||
                Parameters(sub.m_UAVParams, p => p.m_UAVParams).Any())
            {
                throw new NotSupportedException("The stage requires ungrouped constants or UAVs.");
            }

            string NameOf(int id) => ReadNameIndex(names, id);
            var embedded = prefixBytes == extendedHeaderBytes ? ReadEmbeddedBindings(reader) : EmbeddedBindings.Empty;
            var constants = Parameters(sub.m_ConstantBuffers, p => p.m_ConstantBuffers)
                .Select(ReadLayout).Concat(embedded.Layouts)
                .GroupBy(layout => layout.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var buffers = new List<ShaderPreviewBuffer>();
            var slots = new HashSet<int>();
            var bindings = Parameters(sub.m_ConstantBufferBindings, p => p.m_ConstantBufferBindings)
                .Select(binding => (Name: NameOf(binding.m_NameIndex), Slot: binding.m_Index))
                .Concat(embedded.Buffers).Distinct();
            foreach (var (name, slot) in bindings)
            {
                if (slot is < 0 or >= 14 || !slots.Add(slot) || !constants.Remove(name, out var layouts) ||
                    layouts.Count(layout => !layout.IsPartial) > 1)
                {
                    throw new InvalidDataException($"Invalid constant buffer binding '{name}'.");
                }
                int size = layouts.Max(layout => layout.Size);
                if (size is <= 0 or > 65536)
                {
                    throw new InvalidDataException($"Invalid constant buffer size for '{name}'.");
                }
                var parameters = layouts.SelectMany(layout => layout.Parameters).Distinct().ToArray();
                if (parameters.Select(parameter => parameter.Offset).Distinct().Count() != parameters.Length)
                {
                    throw new InvalidDataException($"Constant buffer '{name}' has conflicting declarations for the same offset.");
                }
                buffers.Add(new ShaderPreviewBuffer(name, slot, size, parameters));
            }
            if (constants.Values.SelectMany(layouts => layouts).Any(layout => layout.Parameters.Length != 0))
            {
                throw new InvalidDataException("The stage contains constants with no buffer binding.");
            }

            var textures = Parameters(sub.m_TextureParams, p => p.m_TextureParams)
                .Select(binding => ReadTextureBinding(NameOf(binding.m_NameIndex), binding.m_Index, binding.m_SamplerIndex, binding.m_Dim))
                .Concat(embedded.Textures).Distinct().ToArray();
            var samplers = Parameters(sub.m_Samplers, p => p.m_Samplers)
                .Select(binding => ReadInlineSampler(binding.sampler, binding.bindPoint)).Distinct().ToArray();
            return new ShaderPreviewStage(code.m_ProgramCode[prefixBytes..], buffers.ToArray(), textures)
            {
                StructuredBuffers = Parameters(sub.m_BufferParams, p => p.m_BufferParams)
                    .Select(binding => new ShaderPreviewStructuredBufferBinding(NameOf(binding.m_NameIndex), binding.m_Index))
                    .Concat(embedded.StructuredBuffers).Distinct().ToArray(),
                InlineSamplers = samplers
            };

            // Unity 2020.3.2 and later share bindings across the variants of a stage.
            IEnumerable<T> Parameters<T>(T[] legacy, Func<SerializedProgramParameters, T[]> select) =>
                specific == null ? legacy : select(common!).Concat(select(specific));

            ConstantBufferLayout ReadLayout(ConstantBuffer buffer)
            {
                string name = NameOf(buffer.m_NameIndex);
                if (buffer.m_StructParams.Length != 0)
                {
                    throw new NotSupportedException($"Constant buffer '{name}' contains structured parameters.");
                }
                var parameters = buffer.m_MatrixParams.Select(m => new ShaderPreviewParameter(NameOf(m.m_NameIndex), m.m_Type,
                    m.m_RowCount, 4, true, ReadArraySize(m.m_ArraySize), m.m_Index))
                    .Concat(buffer.m_VectorParams.Select(v => new ShaderPreviewParameter(NameOf(v.m_NameIndex), v.m_Type,
                        1, v.m_Dim, false, ReadArraySize(v.m_ArraySize), v.m_Index))).ToArray();
                return new ConstantBufferLayout(name, buffer.m_Size, buffer.m_IsPartialCB, parameters);
            }
        }

        private static EmbeddedBindings ReadEmbeddedBindings(BinaryReader reader)
        {
            // The extended program stores the rest of each layout and the per-program bindings after DXBC.
            ReadProgramChannels(reader);
            int groupCount = ReadCount(reader, 64);
            var layouts = new List<ConstantBufferLayout>(groupCount);
            for (int i = 0; i < groupCount; i++)
            {
                string name = ReadString(reader);
                int size = ReadCount(reader, 65536);
                var parameters = new ShaderPreviewParameter[ReadCount(reader, 4096)];
                for (int j = 0; j < parameters.Length; j++)
                {
                    var parameter = ReadProgramParameter(reader);
                    parameters[j] = parameter with { ArraySize = ReadArraySize(parameter.ArraySize) };
                }
                if (ReadCount(reader, 4096) != 0)
                {
                    throw new NotSupportedException($"Constant buffer '{name}' contains structured parameters.");
                }
                if (name.Length != 0 || size != 0 || parameters.Length != 0)
                {
                    layouts.Add(new ConstantBufferLayout(name, size, true, parameters));
                }
            }

            int resourceCount = ReadCount(reader, 160);
            var buffers = new List<(string Name, int Slot)>();
            var textures = new List<ShaderPreviewTextureBinding>();
            var structuredBuffers = new List<ShaderPreviewStructuredBufferBinding>();
            for (int i = 0; i < resourceCount; i++)
            {
                string name = ReadString(reader);
                int kind = reader.ReadInt32();
                int slot = reader.ReadInt32();
                int extra = reader.ReadInt32();
                switch (kind)
                {
                    case 0:
                        // The texture dimension is stored above the multisample flag.
                        textures.Add(ReadTextureBinding(name, slot, extra, reader.ReadInt32() >> 1));
                        break;
                    case 1 when extra == 0:
                        buffers.Add((name, slot));
                        break;
                    case 2 when extra == 0 && slot is >= 0 and < 128:
                        structuredBuffers.Add(new ShaderPreviewStructuredBufferBinding(name, slot));
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported embedded shader resource '{name}' (type {kind}, slot {slot}).");
                }
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException("The shader program contains extra binding data.");
            }
            return new EmbeddedBindings(layouts, buffers, textures, structuredBuffers);
        }

        private static ShaderPreviewTextureBinding ReadTextureBinding(string name, int slot, int sampler, int dimension)
        {
            var textureDimension = (ShaderPreviewTextureDimension)dimension;
            if (slot is < 0 or >= 128 || sampler is < -1 or >= 16 || !Enum.IsDefined(textureDimension))
            {
                throw new NotSupportedException($"Unsupported shader texture '{name}' (dimension {dimension}, slot {slot}, sampler {sampler}).");
            }
            return new ShaderPreviewTextureBinding(name, slot, sampler, textureDimension);
        }

        // The serialized layout uses a zero array count for single parameters.
        private static int ReadArraySize(int size) => size == 0 ? 1 : size;
    }
}
