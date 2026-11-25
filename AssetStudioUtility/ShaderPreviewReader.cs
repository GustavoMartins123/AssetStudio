using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio
{
    public abstract class ShaderPreviewReader
    {
        protected const int MaxBlobBytes = 64 * 1024 * 1024;
        protected static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public abstract string Name { get; }
        public abstract IReadOnlyDictionary<string, NumericsVector4> Values { get; }
        public abstract IReadOnlyDictionary<string, string> TextureDefaults { get; }
        public abstract IReadOnlyDictionary<string, string> Dependencies { get; }
        public abstract IReadOnlySet<string> PropertyKeywords { get; }
        public virtual IReadOnlyList<string> DefaultKeywords => [];
        public abstract IReadOnlyList<ShaderPreviewSubShader> SubShaders { get; }

        public static bool SupportsVersion(int[] version) => version is [5, 3, ..] || SupportsSerializedVersion(version);

        private static bool SupportsSerializedVersion(int[] version) =>
            version is [2017, 4, ..] or [2018, 2, ..] or [2019, 3, ..] or [2020, 3, ..];

        public static ShaderPreviewReader Read(Shader shader)
        {
            ArgumentNullException.ThrowIfNull(shader);
            return shader.version switch
            {
                [5, 3, ..] => LegacyShaderPreview.Read(shader),
                _ when SupportsSerializedVersion(shader.version) => new SerializedShaderPreview(shader),
                _ => throw new NotSupportedException("Shader preview supports Unity 5.3, 2017.4, 2018.2, 2019.3 and 2020.3 Built-in shaders compiled for Direct3D 11.")
            };
        }

        public static string ReadName(Shader shader) => shader.m_ParsedForm is { } form
            ? form.m_Name : LegacyShaderPreview.ReadName(shader);

        public IEnumerable<string> GetKeywords(ShaderPreviewPassDefinition pass) =>
            GetVariantKeywords(pass, ShaderPreviewProgramKind.Vertex)
                .Concat(GetVariantKeywords(pass, ShaderPreviewProgramKind.Hull))
                .Concat(GetVariantKeywords(pass, ShaderPreviewProgramKind.Domain))
                .Concat(GetVariantKeywords(pass, ShaderPreviewProgramKind.Pixel))
                .Concat(GetVariantKeywords(pass, ShaderPreviewProgramKind.Geometry)).SelectMany(keywords => keywords);

        public IEnumerable<IReadOnlyList<string>> GetVariantKeywords(ShaderPreviewPassDefinition pass, bool vertex) =>
            GetVariantKeywords(pass, vertex ? ShaderPreviewProgramKind.Vertex : ShaderPreviewProgramKind.Pixel);

        protected abstract IEnumerable<IReadOnlyList<string>> GetVariantKeywords(ShaderPreviewPassDefinition pass, ShaderPreviewProgramKind kind);

        public abstract LegacyShaderPreviewPass PreparePass(ShaderPreviewSubShader subShader, ShaderPreviewPassDefinition pass,
            IReadOnlySet<string> enabledKeywords, IReadOnlyDictionary<string, NumericsVector4> values);

        protected static bool IsDirect3D11Stage(ShaderGpuProgramType type, ShaderPreviewProgramKind stage) => stage switch
        {
            ShaderPreviewProgramKind.Vertex => type is ShaderGpuProgramType.DX11VertexSM40 or ShaderGpuProgramType.DX11VertexSM50,
            ShaderPreviewProgramKind.Pixel => type is ShaderGpuProgramType.DX11PixelSM40 or ShaderGpuProgramType.DX11PixelSM50,
            ShaderPreviewProgramKind.Geometry => type is ShaderGpuProgramType.DX11GeometrySM40 or ShaderGpuProgramType.DX11GeometrySM50,
            ShaderPreviewProgramKind.Hull => type == ShaderGpuProgramType.DX11HullSM50,
            ShaderPreviewProgramKind.Domain => type == ShaderGpuProgramType.DX11DomainSM50,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };

        protected static void ReadProgramChannels(BinaryReader reader)
        {
            reader.ReadUInt32(); // Source-channel mask.
            int channelCount = ReadCount(reader, 32);
            for (int i = 0; i < channelCount; i++)
            {
                reader.ReadUInt32(); // Source channel.
                reader.ReadUInt32(); // Destination channel.
            }
        }

        protected static ShaderPreviewParameter ReadProgramParameter(BinaryReader reader) =>
            new(ReadString(reader), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                reader.ReadInt32() != 0, reader.ReadInt32(), reader.ReadInt32());

        protected static int ReadCount(BinaryReader reader, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum)
            {
                throw new InvalidDataException($"Invalid Unity shader field length {count}.");
            }
            return count;
        }

        protected static string ReadString(BinaryReader reader) => StrictUtf8.GetString(ReadField(reader, 4096));

        protected static byte[] ReadField(BinaryReader reader, int maximum)
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

        internal static ShaderPreviewSamplerBinding ReadInlineSampler(uint encoded, int slot)
        {
            // Point and Linear with Clamp on all three axes.
            if (encoded is not (0x54 or 0x55) || slot is < 0 or >= 16)
            {
                throw new NotSupportedException($"Unsupported inline sampler 0x{encoded:X} at slot {slot}.");
            }
            return new ShaderPreviewSamplerBinding(slot, Filter: (int)(encoded & 1), Wrap: 1);
        }

        protected static T[] MatchVariants<T>(IReadOnlyList<T> programs, Func<T, string[]> keywords,
            IReadOnlySet<string> enabled, ShaderPreviewPassDefinition pass, ShaderPreviewProgramKind kind)
        {
            var expected = new HashSet<string>(programs.SelectMany(keywords), StringComparer.Ordinal);
            expected.IntersectWith(enabled);
            var matches = programs.Where(program => expected.SetEquals(keywords(program))).ToArray();
            return matches.Length != 0 ? matches : throw new NotSupportedException(
                $"Pass '{pass.DisplayName}' has no exact D3D11 {kind.ToString().ToLowerInvariant()} variant for [{FormatKeywords(expected)}].");
        }

        protected static NotSupportedException AmbiguousVariant(ShaderPreviewPassDefinition pass, ShaderPreviewProgramKind kind,
            IEnumerable<string> keywords) => new($"Pass '{pass.DisplayName}' has ambiguous D3D11 {kind.ToString().ToLowerInvariant()} variants for [{FormatKeywords(keywords)}].");

        private static string FormatKeywords(IEnumerable<string> keywords) => string.Join(", ", keywords.Order(StringComparer.Ordinal));

        internal static string? AddPropertyKeywords(HashSet<string> keywords, string name, string attribute, IReadOnlyList<string> arguments,
            float? defaultValue = null)
        {
            string property = name.ToUpperInvariant();
            if (attribute == "KeywordEnum")
            {
                int selected = defaultValue is { } value && float.IsFinite(value) ? (int)value : -1;
                string? enabled = null;
                for (int option = 0; option < arguments.Count; option++)
                {
                    string keyword = property + "_" + arguments[option].Replace(' ', '_').ToUpperInvariant();
                    keywords.Add(keyword);
                    if (option == selected)
                    {
                        enabled = keyword;
                    }
                }
                return enabled;
            }
            if (attribute is "Toggle" or "ToggleOff")
            {
                bool toggleOff = attribute == "ToggleOff";
                string keyword = arguments.Count == 1 ? arguments[0].Trim('"') : property + (toggleOff ? "_OFF" : "_ON");
                keywords.Add(keyword);
                return defaultValue is { } value && (MathF.Abs(value) > 0.001f) != toggleOff ? keyword : null;
            }
            return null;
        }
    }
}
