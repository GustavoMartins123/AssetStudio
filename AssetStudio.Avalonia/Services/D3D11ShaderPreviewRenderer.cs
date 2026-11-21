using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static AssetStudio.Avalonia.Services.D3D11PreviewDevice;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static unsafe class D3D11ShaderPreviewRenderer
{
    private const int FrameSize = 384;
    private const int Rgba8 = 28;
    private const uint BindVertexBuffer = 1, BindIndexBuffer = 2, BindConstantBuffer = 4,
        BindShaderResource = 8, BindRenderTarget = 32, BindDepthStencil = 64;

    private static readonly (Vertex[] Vertices, uint[] Indices) Plane = BuildPlane();
    private static readonly (Vertex[] Vertices, uint[] Indices) Sphere = BuildSphere();

    internal static ShaderPreviewFrame Render(ShaderPreviewScene scene, ShaderPreviewCamera camera, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unity 5.3 shader preview requires Direct3D 11 on Windows.");
        }
        token.ThrowIfCancellationRequested();

        LegacyShaderPreviewPass pass = scene.Pass;
        ValidateStage(pass.Vertex, vertex: true);
        ValidateStage(pass.Fragment, vertex: false);
        var eye = new NumericsVector3(MathF.Sin(camera.Yaw) * MathF.Cos(camera.Pitch), MathF.Sin(camera.Pitch),
            MathF.Cos(camera.Yaw) * MathF.Cos(camera.Pitch)) * camera.Distance;
        NumericsMatrix4x4 view = NumericsMatrix4x4.CreateLookAt(eye, NumericsVector3.Zero, NumericsVector3.UnitY);
        NumericsMatrix4x4 projection = NumericsMatrix4x4.CreatePerspectiveFieldOfView(35 * MathF.PI / 180, 1, 0.1f, 100);
        var (vertices, indices) = pass.Plane ? Plane : Sphere;

        using var device = new D3D11PreviewDevice();
        nint layout = CreateInputLayout(device, pass.Vertex.Bytecode);
        nint vertexShader = device.CreateShader(pass.Vertex.Bytecode, vertex: true);
        nint fragmentShader = device.CreateShader(pass.Fragment.Bytecode, vertex: false);
        nint vertexBuffer = device.CreateBuffer<Vertex>(vertices, BindVertexBuffer);
        nint indexBuffer = device.CreateBuffer<uint>(indices, BindIndexBuffer);

        var frame = new TextureDesc
        {
            Width = FrameSize, Height = FrameSize, MipLevels = 1, ArraySize = 1,
            Format = Rgba8, SampleCount = 1, BindFlags = BindRenderTarget
        };
        nint target = device.CreateTexture(frame);
        nint targetView = device.CreateRenderView(target);
        frame.Format = 45; // D24_UNORM_S8_UINT
        frame.BindFlags = BindDepthStencil;
        nint depth = device.CreateDepthView(device.CreateTexture(frame));
        frame.Format = Rgba8;
        frame.BindFlags = 0;
        frame.Usage = 3; // staging
        frame.CpuAccess = 0x20000; // read
        nint staging = device.CreateTexture(frame);

        device.SetFrame(targetView, depth, CreateBlend(device, pass.State), CreateDepth(device, pass.State),
            device.CreateRasterizerState(new RasterizerDesc
            {
                FillMode = 3, CullMode = (int)pass.State.Cull, FrontCounterClockwise = 1,
                DepthClipEnable = 1, DepthBias = pass.State.OffsetUnits, SlopeScaledDepthBias = pass.State.OffsetFactor
            }), FrameSize);
        device.SetGeometry(layout, vertexBuffer, indexBuffer, (uint)sizeof(Vertex));
        device.SetShader(vertexShader, vertex: true);
        device.SetShader(fragmentShader, vertex: false);

        var textures = new Dictionary<string, (nint View, nint Sampler)>(StringComparer.Ordinal);
        BindStage(pass.Vertex, vertex: true);
        BindStage(pass.Fragment, vertex: false);
        token.ThrowIfCancellationRequested();
        device.Draw(indices.Length);
        var pixels = device.ReadPixels(target, staging, FrameSize);
        token.ThrowIfCancellationRequested();
        return new ShaderPreviewFrame(FrameSize, FrameSize, pixels);

        void BindStage(ShaderPreviewStage stage, bool vertex)
        {
            var buffers = new HashSet<int>();
            foreach (var buffer in stage.Buffers)
            {
                token.ThrowIfCancellationRequested();
                if (!buffers.Add(buffer.Slot))
                {
                    throw new InvalidDataException($"Duplicate constant buffer binding b{buffer.Slot}.");
                }
                var data = ShaderPreviewService.BindConstants(scene, buffer, view, projection, eye, FrameSize);
                device.SetConstantBuffer(device.CreateBuffer<byte>(data, BindConstantBuffer), buffer.Slot, vertex);
            }
            var slots = new HashSet<int>();
            var samplers = new Dictionary<int, ShaderPreviewTexture>();
            foreach (var binding in stage.Textures)
            {
                token.ThrowIfCancellationRequested();
                ShaderPreviewTexture texture = scene.Textures[binding.Name];
                if (!slots.Add(binding.Slot) || samplers.TryGetValue(binding.SamplerSlot, out var shared) &&
                    (shared.Filter, shared.Wrap, shared.Anisotropy, shared.MipBias) != (texture.Filter, texture.Wrap, texture.Anisotropy, texture.MipBias))
                {
                    throw new NotSupportedException($"Conflicting texture or sampler bindings for '{binding.Name}'.");
                }
                samplers[binding.SamplerSlot] = texture;
                if (!textures.TryGetValue(binding.Name, out var gpu))
                {
                    gpu = UploadTexture(device, texture, binding.Name);
                    textures.Add(binding.Name, gpu);
                }
                device.SetTexture(gpu.View, binding.Slot, vertex);
                device.SetSampler(gpu.Sampler, binding.SamplerSlot, vertex);
            }
        }
    }

    private static (nint View, nint Sampler) UploadTexture(D3D11PreviewDevice device, ShaderPreviewTexture texture, string name)
    {
        var levels = stackalloc SubresourceData[texture.MipCount];
        fixed (byte* data = texture.Pixels)
        {
            int consumed = 0;
            for (int mip = 0; mip < texture.MipCount; mip++)
            {
                int width = Math.Max(1, texture.Width >> mip);
                int height = Math.Max(1, texture.Height >> mip);
                int columns = texture.BlockCompressed ? (width + 3) / 4 : width;
                int rows = texture.BlockCompressed ? (height + 3) / 4 : height;
                int pitch = checked(texture.BytesPerBlock * columns);
                int length = checked(rows * pitch);
                if (consumed > texture.Pixels.Length - length)
                {
                    throw new InvalidDataException($"Incomplete mip data for texture '{name}'.");
                }
                levels[mip] = new SubresourceData { Data = data + consumed, RowPitch = (uint)pitch, SlicePitch = (uint)length };
                consumed += length;
            }
            if (consumed != texture.Pixels.Length)
            {
                throw new InvalidDataException($"Unexpected data after the mips for texture '{name}'.");
            }
            nint image = device.CreateTexture(new TextureDesc
            {
                Width = (uint)texture.Width, Height = (uint)texture.Height, MipLevels = (uint)texture.MipCount,
                ArraySize = 1, Format = texture.Format, SampleCount = 1, Usage = 1, BindFlags = BindShaderResource
            }, levels);
            uint wrap = texture.Wrap switch { 0 => 1, 1 => 3, 2 => 2, 3 => 5, _ => throw new InvalidDataException("Invalid wrap mode.") };
            uint filter = texture.Anisotropy > 1 ? 0x55u : texture.Filter switch
            {
                0 => 0, 1 => 0x14, 2 => 0x15, _ => throw new InvalidDataException("Invalid filter mode.")
            };
            nint sampler = device.CreateSamplerState(new SamplerDesc
            {
                Filter = filter, AddressU = wrap, AddressV = wrap, AddressW = wrap,
                MaxAnisotropy = (uint)Math.Max(1, texture.Anisotropy), LodBias = texture.MipBias, Comparison = 8, MaxLod = float.MaxValue
            });
            return (device.CreateShaderView(image), sampler);
        }
    }

    private static nint CreateDepth(D3D11PreviewDevice device, ShaderPreviewState state)
    {
        var stencil = new StencilFace { Fail = 1, DepthFail = 1, Pass = 1, Function = 8 };
        return device.CreateDepthState(new DepthDesc
        {
            Enable = state.ZWrite || state.ZTest != ShaderPreviewDepth.Off ? 1 : 0,
            WriteMask = state.ZWrite ? 1 : 0,
            Function = state.ZTest == ShaderPreviewDepth.Off ? 8 : (int)state.ZTest,
            StencilReadMask = 255, StencilWriteMask = 255, Front = stencil, Back = stencil
        });
    }

    private static nint CreateBlend(D3D11PreviewDevice device, ShaderPreviewState state) =>
        device.CreateBlendState(new BlendDesc
        {
            Target = new BlendTarget
            {
                Enabled = state.Blend ? 1 : 0, Source = BlendFactor(state.Source, alpha: false),
                Destination = BlendFactor(state.Destination, alpha: false), Operation = (int)state.Operation + 1,
                SourceAlpha = BlendFactor(state.SourceAlpha, alpha: true), DestinationAlpha = BlendFactor(state.DestinationAlpha, alpha: true),
                AlphaOperation = (int)state.AlphaOperation + 1, ColorMask = state.ColorMask
            }
        });

    private static int BlendFactor(ShaderPreviewBlend factor, bool alpha) => factor switch
    {
        ShaderPreviewBlend.Zero => 1,
        ShaderPreviewBlend.One => 2,
        ShaderPreviewBlend.SrcColor => alpha ? 5 : 3,
        ShaderPreviewBlend.OneMinusSrcColor => alpha ? 6 : 4,
        ShaderPreviewBlend.SrcAlpha => 5,
        ShaderPreviewBlend.OneMinusSrcAlpha => 6,
        ShaderPreviewBlend.DstAlpha => 7,
        ShaderPreviewBlend.OneMinusDstAlpha => 8,
        ShaderPreviewBlend.DstColor => alpha ? 7 : 9,
        ShaderPreviewBlend.OneMinusDstColor => alpha ? 8 : 10,
        ShaderPreviewBlend.SrcAlphaSaturate => alpha ? 2 : 11,
        _ => throw new NotSupportedException($"Unsupported blend factor {factor}.")
    };

    private static void ValidateStage(ShaderPreviewStage stage, bool vertex)
    {
        byte[] bytecode = stage.Bytecode;
        ReadOnlySpan<byte> program = FindChunk(bytecode, "SHDR"u8, "SHEX"u8);
        uint version = ReadUInt(program, 0);
        if (version >> 16 != (vertex ? 1u : 0u) || ((version >> 4) & 15) is not (4 or 5) ||
            program.Length % 4 != 0 || ReadUInt(program, 4) != program.Length / 4)
        {
            throw new InvalidDataException("DXBC contains an unexpected Direct3D 11 stage or program length.");
        }
        ValidateResources(program, stage);
        if (!vertex)
        {
            var signature = FindChunk(bytecode, "OSGN"u8);
            int outputs = ReadSignatureCount(signature);
            for (int i = 0; i < outputs; i++)
            {
                int start = 8 + i * 24;
                string semantic = ReadSemantic(signature, start);
                if ((semantic.Equals("SV_Target", StringComparison.OrdinalIgnoreCase) && ReadUInt(signature, start + 4) == 0)
                    || semantic.Equals("SV_Depth", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                throw new NotSupportedException($"Unsupported DXBC pixel output '{semantic}'.");
            }
        }
    }

    private static void ValidateResources(ReadOnlySpan<byte> program, ShaderPreviewStage stage)
    {
        // The Unity 5.3 DXBC can omit RDEF. Compare the actual SM4/5 resource declarations with Unity's metadata.
        const uint customData = 53, declareTexture = 88, declareBuffer = 89, declareSampler = 90;
        int offset = 8;
        while (offset < program.Length)
        {
            uint token = ReadUInt(program, offset);
            uint opcode = token & 0x7ff;
            uint words = opcode == customData ? ReadUInt(program, offset + 4) : (token >> 24) & 0x7f;
            if (words == 0 || words > (program.Length - offset) / 4 || (opcode == customData && words < 2))
            {
                throw new InvalidDataException("Invalid DXBC instruction length.");
            }
            if (opcode is declareTexture or declareBuffer or declareSampler)
            {
                uint expectedWords = opcode == declareSampler ? 3u : 4u;
                if (words != expectedWords || (token & 0x80000000) != 0)
                {
                    throw new NotSupportedException("Unsupported DXBC resource declaration.");
                }
                uint operand = ReadUInt(program, offset + 4);
                uint dimensions = opcode == declareBuffer ? 2u : 1u;
                if (((operand >> 20) & 3) != dimensions || (operand & 0x9fc00000) != 0)
                {
                    throw new NotSupportedException("DXBC resource declarations must use immediate register indices.");
                }
                uint slot = ReadUInt(program, offset + 8);
                uint flags = (token >> 11) & 0x1f;
                bool bound = opcode switch
                {
                    declareTexture => flags == 3 && Array.Exists(stage.Textures, texture => texture.Slot == slot),
                    declareSampler => flags == 0 && Array.Exists(stage.Textures, texture => texture.SamplerSlot == slot),
                    _ => ReadUInt(program, offset + 12) is var count && count is > 0 and <= 4096 &&
                         Array.Exists(stage.Buffers, buffer => buffer.Slot == slot && ((buffer.Size + 15) & ~15) >= count * 16)
                };
                if (!bound)
                {
                    throw new NotSupportedException($"The DXBC resource at register {slot} is incompatible or has no Unity binding.");
                }
            }
            else if (opcode is 156 or 157 or 158 or 161 or 162)
            {
                throw new NotSupportedException("Shader preview does not support DXBC UAV, raw, or structured resources.");
            }
            offset += (int)words * 4;
        }
    }

    private static nint CreateInputLayout(D3D11PreviewDevice device, byte[] bytecode)
    {
        var signature = FindChunk(bytecode, "ISGN"u8);
        int count = ReadSignatureCount(signature);
        var elements = stackalloc InputElement[count];
        byte* names = stackalloc byte[count * 16];
        int elementCount = 0;
        for (int i = 0; i < count; i++)
        {
            int start = 8 + i * 24;
            string name = ReadSemantic(signature, start);
            uint semanticIndex = ReadUInt(signature, start + 4);
            uint systemValue = ReadUInt(signature, start + 8);
            uint componentType = ReadUInt(signature, start + 12);
            if (systemValue == 6 && name.Equals("SV_VertexID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (systemValue != 0 || semanticIndex != 0 || componentType != 3 /* float */)
            {
                throw new NotSupportedException($"Unsupported DXBC vertex input '{name}{semanticIndex}'.");
            }
            uint offset = name.ToUpperInvariant() switch
            {
                "POSITION" => 0, "NORMAL" => 16, "TANGENT" => 32, "COLOR" => 48, "TEXCOORD" => 64,
                _ => throw new NotSupportedException($"Unsupported DXBC vertex attribute '{name}'.")
            };
            uint format = signature[start + 20] switch
            {
                1 => 41, 3 => 16, 7 => 6, 15 => 2,
                _ => throw new NotSupportedException($"Unsupported DXBC component mask for '{name}'.")
            };
            byte* semantic = names + elementCount * 16;
            int length = Encoding.ASCII.GetBytes(name, new Span<byte>(semantic, 15));
            semantic[length] = 0;
            elements[elementCount++] = new InputElement { Semantic = semantic, Offset = offset, Format = format };
        }
        return elementCount == 0 ? 0 : device.CreateLayout(elements, elementCount, bytecode);
    }

    private static ReadOnlySpan<byte> FindChunk(ReadOnlySpan<byte> bytecode, ReadOnlySpan<byte> name, ReadOnlySpan<byte> alternative = default)
    {
        if (bytecode.Length < 32 || !bytecode[..4].SequenceEqual("DXBC"u8) || ReadUInt(bytecode, 24) != bytecode.Length)
        {
            throw new InvalidDataException("Invalid DXBC container size or signature.");
        }
        uint count = ReadUInt(bytecode, 28);
        if (count > 64 || count * 4 > bytecode.Length - 32)
        {
            throw new InvalidDataException("Invalid DXBC chunk table.");
        }
        ReadOnlySpan<byte> match = default;
        for (int i = 0; i < count; i++)
        {
            uint offset = ReadUInt(bytecode, 32 + 4 * i);
            if (offset < 32 + count * 4 || offset > bytecode.Length - 8)
            {
                throw new InvalidDataException("Invalid DXBC chunk offset.");
            }
            var chunk = bytecode[(int)offset..];
            uint length = ReadUInt(chunk, 4);
            if (length > chunk.Length - 8)
            {
                throw new InvalidDataException("Invalid DXBC chunk length.");
            }
            if (chunk[..4].SequenceEqual(name) || chunk[..4].SequenceEqual(alternative))
            {
                if (!match.IsEmpty || length == 0)
                {
                    throw new InvalidDataException("The DXBC program contains an empty or duplicate required chunk.");
                }
                match = chunk.Slice(8, (int)length);
            }
        }
        return !match.IsEmpty ? match : throw new NotSupportedException($"Missing DXBC chunk '{Encoding.ASCII.GetString(name)}'.");
    }

    private static int ReadSignatureCount(ReadOnlySpan<byte> signature)
    {
        uint count = ReadUInt(signature, 0);
        if (signature.Length < 8 || count > 32 || count * 24 > signature.Length - 8)
        {
            throw new InvalidDataException("Invalid DXBC signature.");
        }
        return (int)count;
    }

    private static string ReadSemantic(ReadOnlySpan<byte> signature, int start)
    {
        uint offset = ReadUInt(signature, start);
        if (offset >= signature.Length)
        {
            throw new InvalidDataException("Invalid DXBC semantic offset.");
        }
        var name = signature[(int)offset..];
        int length = name.IndexOf((byte)0);
        if (length is <= 0 or > 64)
        {
            throw new InvalidDataException("Invalid DXBC semantic name.");
        }
        return Encoding.ASCII.GetString(name[..length]);
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset) =>
        offset <= data.Length - 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : throw new EndOfStreamException("Truncated DXBC data.");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Vertex(NumericsVector4 Position, NumericsVector4 Normal, NumericsVector4 Tangent, NumericsVector4 Color, NumericsVector4 Uv);

    private static (Vertex[], uint[]) BuildPlane() =>
        ([PlaneVertex(-1, -1, 0, 0), PlaneVertex(1, -1, 1, 0), PlaneVertex(1, 1, 1, 1), PlaneVertex(-1, 1, 0, 1)],
         [0, 1, 2, 0, 2, 3]);

    private static Vertex PlaneVertex(float x, float y, float u, float v) =>
        new(new NumericsVector4(x, y, 0, 1), new NumericsVector4(0, 0, 1, 0), new NumericsVector4(1, 0, 0, 1), NumericsVector4.One, new NumericsVector4(u, v, 0, 0));

    private static (Vertex[], uint[]) BuildSphere()
    {
        const int longitude = 32;
        const int latitude = 24;
        var vertices = new Vertex[(longitude + 1) * (latitude + 1)];
        var indices = new uint[longitude * latitude * 6];
        int written = 0;
        for (int row = 0; row <= latitude; row++)
        {
            float v = (float)row / latitude;
            float y = MathF.Cos(v * MathF.PI);
            float radius = MathF.Sin(v * MathF.PI);
            for (int column = 0; column <= longitude; column++)
            {
                float u = (float)column / longitude;
                float x = MathF.Cos(u * MathF.Tau);
                float z = MathF.Sin(u * MathF.Tau);
                var position = new NumericsVector4(radius * x, y, radius * z, 1);
                int index = row * (longitude + 1) + column;
                vertices[index] = new Vertex(position, position with { W = 0 },
                    new NumericsVector4(-z, 0, x, 1), NumericsVector4.One, new NumericsVector4(u, 1 - v, 0, 0));
                if (row < latitude && column < longitude)
                {
                    uint top = (uint)index;
                    uint bottom = top + longitude + 1;
                    indices[written++] = top;
                    indices[written++] = top + 1;
                    indices[written++] = bottom;
                    indices[written++] = top + 1;
                    indices[written++] = bottom + 1;
                    indices[written++] = bottom;
                }
            }
        }
        return (vertices, indices);
    }
}
