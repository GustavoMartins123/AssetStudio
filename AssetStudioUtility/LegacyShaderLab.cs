using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio
{
    // ShaderLab here describes the passes and properties of an already compiled Unity 5.3 asset.
    internal sealed class LegacyShaderLab
    {
        private readonly string _text;
        private int _position;
        private string? _next;
        private bool _hasSubShader;
        private bool _hasPass;

        internal string Name { get; private set; } = "";
        internal string PassName { get; private set; } = "";
        internal bool Plane { get; private set; }
        internal ShaderPreviewState State { get; private set; } = new();
        internal Dictionary<string, NumericsVector4> Values { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> TextureDefaults { get; } = new(StringComparer.Ordinal);
        internal List<int> VertexIndices { get; } = new();
        internal List<int> FragmentIndices { get; } = new();

        private LegacyShaderLab(string text) => _text = text.Trim('\uFEFF', '\0');

        internal static LegacyShaderLab Parse(string text)
        {
            var parser = new LegacyShaderLab(text);
            parser.ReadShader();
            return parser;
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
                    case "SubShader" when !_hasSubShader:
                        _hasSubShader = true;
                        ReadSubShader();
                        break;
                    case "SubShader":
                        throw new NotSupportedException("Shader preview requires a single SubShader.");
                    case "Fallback":
                        if (!Take("Off"))
                        {
                            ReadString();
                        }
                        break;
                    case "CustomEditor":
                        ReadString();
                        break;
                    default:
                        throw Unsupported(field);
                }
            }
            if (!_hasPass || Peek().Length != 0)
            {
                throw new InvalidDataException("Expected one complete programmable ShaderLab pass.");
            }
        }

        private void ReadProperties()
        {
            Require("{");
            while (!Take("}"))
            {
                while (Take("["))
                {
                    SkipBlock("[", "]");
                }
                string name = Read();
                Require("(");
                ReadString();
                Require(",");
                string type = Read();
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
                else if (type is "2D" or "3D" or "Cube" or "2DArray" or "CubeArray" or "Rect")
                {
                    string textureDefault = ReadString();
                    Require("{");
                    SkipBlock("{", "}");
                    if (type == "2D")
                    {
                        TextureDefaults.Add(name, textureDefault);
                    }
                }
                else
                {
                    throw Unsupported(type);
                }
            }
        }

        private void ReadSubShader()
        {
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
                        ReadTags();
                        break;
                    case "Pass" when !_hasPass:
                        _hasPass = true;
                        ReadPass();
                        break;
                    case "Pass":
                    case "UsePass":
                    case "GrabPass":
                        throw new NotSupportedException("Shader preview requires a single pass without UsePass or GrabPass.");
                    default:
                        throw Unsupported(field);
                }
            }
        }

        private void ReadTags()
        {
            Require("{");
            while (!Take("}"))
            {
                string tag = ReadString();
                Require("=");
                string value = ReadString();
                if (tag == "PreviewType")
                {
                    Plane = value.ToLowerInvariant() switch
                    {
                        "plane" => true,
                        "sphere" => false,
                        _ => throw Unsupported($"PreviewType {value}")
                    };
                }
                if (tag == "RenderPipeline" && value.Length != 0)
                {
                    throw Unsupported($"RenderPipeline {value}");
                }
            }
        }

        private void ReadPass()
        {
            Require("{");
            while (!Take("}"))
            {
                string field = Read();
                switch (field)
                {
                    case "Tags": ReadTags(); break;
                    case "Name": PassName = ReadString(); break;
                    case "GpuProgramID": ReadInteger(); break;
                    case "Program": ReadProgram(); break;
                    case "Cull": State = State with { Cull = ReadEnum<ShaderPreviewCull>() }; break;
                    case "ZTest": State = State with { ZTest = ReadEnum<ShaderPreviewDepth>() }; break;
                    case "ZWrite": State = State with { ZWrite = ReadOnOff() }; break;
                    case "ColorMask": State = State with { ColorMask = ReadColorMask() }; break;
                    case "Blend": ReadBlend(); break;
                    case "BlendOp": ReadBlendOp(); break;
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

        private void ReadProgram()
        {
            string stage = ReadString();
            List<int> indices = stage switch
            {
                "vp" => VertexIndices,
                "fp" => FragmentIndices,
                _ => throw Unsupported($"Program {stage}")
            };
            if (indices.Count != 0)
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
                    indices.Add(ReadInteger());
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
            string channels = Read();
            if (channels == "0")
            {
                ReadRenderTargetIndex();
                return 0;
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
            string token = Read();
            if (!char.IsLetter(token[0]) || !Enum.TryParse<T>(token, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                throw Unsupported(token);
            }
            return value;
        }

        private bool ReadOnOff() => Read() switch
        {
            "On" => true,
            "Off" => false,
            var token => throw Unsupported(token)
        };

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
            string token = Read();
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n : throw Unsupported(token);
        }

        private float ReadNumber()
        {
            string token = Read();
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
