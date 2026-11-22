using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio
{
    // ShaderLab here describes the passes and properties of an already compiled Unity 5.3 asset.
    internal sealed class LegacyShaderLab
    {
        private readonly string _text;
        private int _position;
        private string? _next;
        private int _nextStart;
        private readonly IReadOnlyDictionary<string, NumericsVector4>? _properties;

        internal string Name { get; private set; } = "";
        internal string? DeclaredFallback { get; private set; }
        internal ShaderPreviewState State { get; private set; } = new();
        internal Dictionary<string, NumericsVector4> Values { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> TextureDefaults { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Dependencies { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> PropertyKeywords { get; } = new(StringComparer.Ordinal);
        internal List<ShaderPreviewSubShader> SubShaders { get; } = new();

        private LegacyShaderLab(string text, IReadOnlyDictionary<string, NumericsVector4>? properties = null)
        {
            _text = text.Trim('\uFEFF', '\0');
            _properties = properties;
        }

        internal static LegacyShaderLab Parse(string text)
        {
            var parser = new LegacyShaderLab(text);
            parser.ReadShader();
            return parser;
        }

        internal static string ReadShaderName(string text)
        {
            var parser = new LegacyShaderLab(text);
            parser.Require("Shader");
            return parser.ReadString();
        }

        internal static ShaderPreviewState EvaluateState(string subShader, string pass, IReadOnlyDictionary<string, NumericsVector4> properties)
        {
            var parser = new LegacyShaderLab(subShader + "\n" + pass, properties);
            parser.ReadRenderState();
            return parser.State;
        }

        private void ReadShader()
        {
            Require("Shader");
            Name = ReadString();
            Require("{");
            while (!Take("}"))
            {
                string field = Read();
                switch (field)
                {
                    case "Properties":
                        ReadProperties();
                        break;
                    case "SubShader":
                        SubShaders.Add(ReadSubShader());
                        break;
                    case "Fallback":
                        DeclaredFallback = Take("Off") ? null : ReadString();
                        break;
                    case "CustomEditor":
                        ReadString();
                        break;
                    case "Dependency":
                        string role = ReadString();
                        Require("=");
                        Dependencies[role] = ReadString();
                        break;
                    default:
                        throw Unsupported(field);
                }
            }
            if ((SubShaders.Count == 0 && DeclaredFallback == null) || Peek().Length != 0)
            {
                throw new InvalidDataException("Expected a complete ShaderLab object with a SubShader.");
            }
        }

        private void ReadProperties()
        {
            Require("{");
            while (!Take("}"))
            {
                var keywordAttributes = new List<(string Name, string[] Values)>();
                while (Take("["))
                {
                    string attribute = Read();
                    if (attribute is not ("Toggle" or "ToggleOff" or "KeywordEnum"))
                    {
                        SkipBlock("[", "]");
                        continue;
                    }
                    var arguments = new List<string>();
                    if (Take("("))
                    {
                        do { arguments.Add(Read()); } while (Take(","));
                        Require(")");
                    }
                    Require("]");
                    keywordAttributes.Add((attribute, arguments.ToArray()));
                }
                string name = Read();
                foreach (var (attribute, arguments) in keywordAttributes)
                {
                    ShaderPreviewReader.AddPropertyKeywords(PropertyKeywords, name, attribute, arguments);
                }
                Require("(");
                ReadString();
                Require(",");
                string type = Read();
                if (type == "CUBE")
                {
                    type = "Cube";
                }
                if (type == "Range")
                {
                    Require("(");
                    ReadNumber();
                    Require(",");
                    ReadNumber();
                    Require(")");
                }
                Require(")");
                Require("=");
                if (type is "Vector" or "Color")
                {
                    Require("(");
                    float x = ReadNumber();
                    Require(",");
                    float y = ReadNumber();
                    Require(",");
                    float z = ReadNumber();
                    Require(",");
                    float w = ReadNumber();
                    Require(")");
                    Values.Add(name, new NumericsVector4(x, y, z, w));
                }
                else if (type is "Float" or "Range" or "Int")
                {
                    Values.Add(name, new NumericsVector4(ReadNumber(), 0, 0, 0));
                }
                else if (type is "2D" or "3D" or "Cube" or "2DArray" or "CubeArray" or "Rect" or "any")
                {
                    string textureDefault = ReadString();
                    Require("{");
                    SkipBlock("{", "}");
                    if (type is "2D" or "Cube" or "any")
                    {
                        if (TextureDefaults.TryGetValue(name, out string? previous) && previous != textureDefault)
                        {
                            throw new InvalidDataException($"Conflicting default textures for '{name}'.");
                        }
                        TextureDefaults[name] = textureDefault;
                    }
                }
                else
                {
                    throw Unsupported(type);
                }
            }
        }

        private ShaderPreviewSubShader ReadSubShader()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var passes = new List<ShaderPreviewPassDefinition>();
            var state = new StringBuilder();
            Require("{");
            while (!Take("}"))
            {
                string field = Read();
                switch (field)
                {
                    case "LOD":
                        ReadInteger();
                        break;
                    case "Tags":
                        ReadTags(tags);
                        break;
                    case "Pass":
                        passes.Add(ReadPass());
                        break;
                    case "UsePass":
                        passes.Add(new ShaderPreviewPassDefinition(PassType.Use, ReadString(), "", "", [], []));
                        break;
                    case "GrabPass":
                        passes.Add(ReadGrabPass());
                        break;
                    default:
                        CollectState(field, state);
                        break;
                }
            }
            return new ShaderPreviewSubShader(tags, state.ToString(), passes);
        }

        private ShaderPreviewPassDefinition ReadGrabPass()
        {
            Require("{");
            string name = "_GrabTexture";
            bool named = false;
            if (Peek().StartsWith('"'))
            {
                name = ReadString();
                named = name.Length != 0;
            }
            while (!Take("}"))
            {
                switch (Read())
                {
                    case "Tags": ReadTags(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); break;
                    case "Name": ReadString(); break;
                    default: throw Unsupported("GrabPass declaration");
                }
            }
            return new ShaderPreviewPassDefinition(PassType.Grab, named ? name : "_GrabTexture", "", "", [], [], named);
        }

        private void ReadTags(Dictionary<string, string> tags)
        {
            Require("{");
            while (!Take("}"))
            {
                string tag = ReadString();
                Require("=");
                tags[tag] = ReadString();
            }
        }

        private ShaderPreviewPassDefinition ReadPass()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var vertex = new List<int>();
            var fragment = new List<int>();
            var geometry = new List<int>();
            var state = new StringBuilder();
            string name = "";
            Require("{");
            while (!Take("}"))
            {
                string field = Read();
                switch (field)
                {
                    case "Tags": ReadTags(tags); break;
                    case "Name": name = ReadString(); break;
                    case "GpuProgramID": ReadInteger(); break;
                    case "Program": ReadProgram(vertex, fragment, geometry, state); break;
                    default: CollectState(field, state); break;
                }
            }
            return new ShaderPreviewPassDefinition(PassType.Normal, name, tags.GetValueOrDefault("LightMode", "Always"),
                state.ToString(), vertex, fragment) { GeometryIndices = geometry };
        }

        private void CollectState(string field, StringBuilder state)
        {
            state.Append(field).Append(' ');
            int lineEnd = _text.IndexOf('\n', _position);
            if (lineEnd < 0)
            {
                lineEnd = _text.Length;
            }
            while (Peek().Length != 0 && Peek() != "}")
            {
                if (Take("{"))
                {
                    state.Append("{ ");
                    int depth = 1;
                    while (depth != 0)
                    {
                        string token = Read();
                        depth += token == "{" ? 1 : token == "}" ? -1 : 0;
                        state.Append(token).Append(' ');
                    }
                    break;
                }
                if (_nextStart >= lineEnd || IsStatement(Peek()))
                {
                    break;
                }
                state.Append(Read()).Append(' ');
            }
            state.AppendLine();
        }

        private static bool IsStatement(string token) => token is
            "Tags" or "LOD" or "Pass" or "UsePass" or "GrabPass" or "Name" or "GpuProgramID" or "Program"
            or "Cull" or "ZTest" or "ZWrite" or "ColorMask" or "Blend" or "BlendOp" or "Offset"
            or "Lighting" or "Fog" or "Stencil" or "AlphaToMask" or "AlphaTest" or "ColorMaterial"
            or "Color" or "SeparateSpecular" or "Conservative" or "BindChannels" or "SetTexture";

        private void ReadRenderState()
        {
            while (Peek().Length != 0)
            {
                string field = Read();
                switch (field)
                {
                    case "Cull": State = State with { Cull = ReadEnum<ShaderPreviewCull>() }; break;
                    case "ZTest": State = State with { ZTest = ReadEnum<ShaderPreviewDepth>() }; break;
                    case "ZWrite": State = State with { ZWrite = ReadOnOff() }; break;
                    case "ColorMask": State = State with { ColorMask = ReadColorMask() }; break;
                    case "Blend": ReadBlend(); break;
                    case "BlendOp": ReadBlendOp(); break;
                    case "Stencil": State = State with { Stencil = ReadStencil() }; break;
                    case "AlphaToMask": State = State with { AlphaToMask = ReadOnOff() }; break;
                    case "Lighting": ReadOnOff(); break;
                    case "Offset":
                        float factor = ReadNumber();
                        Require(",");
                        State = State with { OffsetFactor = factor, OffsetUnits = ReadInteger() };
                        break;
                    default:
                        throw Unsupported(field);
                }
            }
        }

        private void ReadProgram(List<int> vertex, List<int> fragment, List<int> geometry, StringBuilder state)
        {
            string stage = ReadString();
            List<int>? indices = stage switch
            {
                "vp" => vertex,
                "fp" => fragment,
                "gp" => geometry,
                _ => null
            };
            if (indices?.Count > 0)
            {
                throw new InvalidDataException($"Duplicate ShaderLab program stage '{stage}'.");
            }

            Require("{");
            while (!Take("}"))
            {
                Require("SubProgram");
                string platform = ReadString().TrimEnd();
                Require("{");
                if (platform == "d3d11")
                {
                    Require("GpuProgramIndex");
                    int index = ReadInteger();
                    if (indices == null)
                    {
                        state.Append("ProgramStage-").AppendLine(stage);
                    }
                    else
                    {
                        indices.Add(index);
                    }
                    Require("}");
                }
                else
                {
                    SkipBlock("{", "}");
                }
            }
        }

        private void ReadBlend()
        {
            ReadRenderTargetIndex();
            if (Take("Off"))
            {
                State = State with { Blend = false };
                return;
            }
            var source = ReadEnum<ShaderPreviewBlend>();
            var destination = ReadEnum<ShaderPreviewBlend>();
            var sourceAlpha = source;
            var destinationAlpha = destination;
            if (Take(","))
            {
                sourceAlpha = ReadEnum<ShaderPreviewBlend>();
                destinationAlpha = ReadEnum<ShaderPreviewBlend>();
            }
            State = State with
            {
                Blend = true, Source = source, Destination = destination,
                SourceAlpha = sourceAlpha, DestinationAlpha = destinationAlpha
            };
        }

        private void ReadBlendOp()
        {
            ReadRenderTargetIndex();
            var op = ReadEnum<ShaderPreviewBlendOp>();
            State = State with { Operation = op, AlphaOperation = Take(",") ? ReadEnum<ShaderPreviewBlendOp>() : op };
        }

        private byte ReadColorMask()
        {
            string channels = ReadStateValue();
            if (byte.TryParse(channels, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte unityMask) && unityMask <= 15)
            {
                ReadRenderTargetIndex();
                return (byte)(((unityMask & 1) << 3) | ((unityMask & 2) << 1) | ((unityMask & 4) >> 1) | ((unityMask & 8) >> 3));
            }
            byte mask = 0;
            foreach (char channel in channels)
            {
                mask |= channel switch
                {
                    'R' => (byte)1, 'G' => (byte)2, 'B' => (byte)4, 'A' => (byte)8,
                    _ => throw Unsupported($"ColorMask {channels}")
                };
            }
            ReadRenderTargetIndex();
            return mask;
        }

        private void ReadRenderTargetIndex()
        {
            if (int.TryParse(Peek(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                Read();
                if (index != 0)
                {
                    throw Unsupported($"render target {index}");
                }
            }
        }

        private T ReadEnum<T>() where T : struct, Enum
        {
            string token = ReadStateValue();
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                if (typeof(T) == typeof(ShaderPreviewBlend))
                {
                    string[] modes =
                    [
                        "Zero", "One", "DstColor", "SrcColor", "OneMinusDstColor", "SrcAlpha",
                        "OneMinusSrcColor", "DstAlpha", "OneMinusDstAlpha", "SrcAlphaSaturate", "OneMinusSrcAlpha"
                    ];
                    token = (uint)number < (uint)modes.Length ? modes[number] : throw Unsupported(token);
                }
                else
                {
                    if (typeof(T) == typeof(ShaderPreviewCull) || typeof(T) == typeof(ShaderPreviewStencilOp))
                    {
                        number++;
                    }
                    if (typeof(T) == typeof(ShaderPreviewDepth) && number == 0)
                    {
                        number = (int)ShaderPreviewDepth.Off;
                    }
                    return Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : throw Unsupported(token);
                }
            }
            if (!char.IsLetter(token[0]) || !Enum.TryParse<T>(token, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                throw Unsupported(token);
            }
            return value;
        }

        private bool ReadOnOff() => ReadStateValue() switch
        {
            "On" or "1" => true,
            "Off" or "0" => false,
            var token => throw Unsupported(token)
        };

        private string ReadStateValue()
        {
            if (!Take("["))
            {
                return Read();
            }
            string name = Read();
            Require("]");
            if (_properties == null || !_properties.TryGetValue(name, out var value) || !float.IsFinite(value.X))
            {
                throw new NotSupportedException($"ShaderLab render state requires the material property '{name}'.");
            }
            return value.X.ToString("G9", CultureInfo.InvariantCulture);
        }

        private ShaderPreviewStencil ReadStencil()
        {
            Require("{");
            byte reference = 0, readMask = 255, writeMask = 255;
            var compares = new Dictionary<string, ShaderPreviewDepth>(StringComparer.OrdinalIgnoreCase);
            var operations = new Dictionary<string, ShaderPreviewStencilOp>(StringComparer.OrdinalIgnoreCase);
            while (!Take("}"))
            {
                string command = Read();
                switch (command)
                {
                    case "Ref": reference = ReadStencilByte(); break;
                    case "ReadMask": readMask = ReadStencilByte(); break;
                    case "WriteMask": writeMask = ReadStencilByte(); break;
                    case "Comp": case "CompFront": case "CompBack": compares[command] = ReadEnum<ShaderPreviewDepth>(); break;
                    case "Pass": case "PassFront": case "PassBack":
                    case "Fail": case "FailFront": case "FailBack":
                    case "ZFail": case "ZFailFront": case "ZFailBack": operations[command] = ReadEnum<ShaderPreviewStencilOp>(); break;
                    default: throw Unsupported($"Stencil {command}");
                }
            }
            return new ShaderPreviewStencil(reference, readMask, writeMask, Face("Front"), Face("Back"));

            ShaderPreviewStencilFace Face(string side) => new(
                compares.GetValueOrDefault("Comp" + side, compares.GetValueOrDefault("Comp", ShaderPreviewDepth.Always)),
                Op("Pass", side), Op("Fail", side), Op("ZFail", side));
            ShaderPreviewStencilOp Op(string command, string side) =>
                operations.GetValueOrDefault(command + side, operations.GetValueOrDefault(command, ShaderPreviewStencilOp.Keep));
        }

        private byte ReadStencilByte()
        {
            string value = ReadStateValue();
            return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte result)
                ? result : throw Unsupported($"Stencil value {value}");
        }

        private void SkipBlock(string open, string close)
        {
            int depth = 1;
            while (depth != 0)
            {
                string token = Read();
                if (token == open)
                {
                    depth++;
                }
                else if (token == close)
                {
                    depth--;
                }
            }
        }

        private int ReadInteger()
        {
            string token = ReadStateValue();
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n : throw Unsupported(token);
        }

        private float ReadNumber()
        {
            string token = ReadStateValue();
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n)
                ? n : throw Unsupported(token);
        }

        private string ReadString()
        {
            string token = Read();
            return token[0] == '"' ? token[1..^1] : throw Unsupported(token);
        }

        private void Require(string token)
        {
            if (!Take(token))
            {
                throw new InvalidDataException($"Expected '{token}' in compiled ShaderLab, found '{Peek()}'.");
            }
        }

        private bool Take(string token)
        {
            if (Peek() != token)
            {
                return false;
            }
            _next = null;
            return true;
        }

        private string Read()
        {
            string token = Peek();
            _next = null;
            return token.Length != 0 ? token : throw new EndOfStreamException("Unexpected end of compiled ShaderLab.");
        }

        private string Peek() => _next ??= Scan();

        private string Scan()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position]))
                {
                    _position++;
                    continue;
                }
                if (_text.AsSpan(_position).StartsWith("//", StringComparison.Ordinal))
                {
                    int end = _text.IndexOf('\n', _position + 2);
                    _position = end < 0 ? _text.Length : end + 1;
                    continue;
                }
                if (_text.AsSpan(_position).StartsWith("/*", StringComparison.Ordinal))
                {
                    int end = _text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _position = end >= 0 ? end + 2 : throw new InvalidDataException("Unterminated ShaderLab comment.");
                    continue;
                }

                int start = _position++;
                _nextStart = start;
                char initial = _text[start];
                if (initial == '"')
                {
                    while (_position < _text.Length)
                    {
                        char current = _text[_position++];
                        if (current == '"')
                        {
                            return _text[start.._position];
                        }
                        if (current == '\\' && _position < _text.Length)
                        {
                            _position++;
                        }
                    }
                    throw new InvalidDataException("Unterminated ShaderLab string.");
                }
                if (!IsDelimiter(initial))
                {
                    while (_position < _text.Length && !char.IsWhiteSpace(_text[_position]) && !IsDelimiter(_text[_position]))
                    {
                        _position++;
                    }
                }
                return _text[start.._position];
            }
            return "";
        }

        private static bool IsDelimiter(char value) => value is '{' or '}' or '(' or ')' or '[' or ']' or ',' or '=';
        private static NotSupportedException Unsupported(string token) => new($"Unsupported ShaderLab construct '{token}'.");
    }
}
