using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static AssetStudio.Avalonia.Services.D3D11PreviewDevice;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal sealed unsafe class D3D11ShaderPreviewRenderer : IDisposable
{
    private const int FrameSize = ShaderPreviewService.RenderSize;
    private const int Rgba8 = 28;
    private const uint BindVertexBuffer = 1, BindIndexBuffer = 2, BindConstantBuffer = 4,
        BindShaderResource = 8, BindRenderTarget = 32, BindDepthStencil = 64;
    private static readonly NumericsMatrix4x4 Projection = NumericsMatrix4x4.CreatePerspectiveFieldOfView(35 * MathF.PI / 180, 1,
        ShaderPreviewCamera.Near, ShaderPreviewCamera.Far);

    private static readonly (Vertex[] Vertices, uint[] Indices) Plane = BuildPlane();
    private static readonly (Vertex[] Vertices, uint[] Indices) ScreenQuad = BuildScreenQuad();
    private static readonly (Vertex[] Vertices, uint[] Indices) Sphere = BuildSphere();
    private static readonly (Vertex[] Vertices, uint[] Indices) Skybox = BuildSkybox();
    private const int ParticleCount = 8;
    private static readonly uint[] ParticleIndices = BuildParticleIndices();

    private readonly ShaderPreviewScene _scene;
    private readonly D3D11PreviewDevice _device;
    private readonly BuiltInLightingPreview? _lighting;
    private ShaderPreviewCamera? _lightingCamera;
    private readonly nint _vertexBuffer, _indexBuffer, _target, _targetView, _depth, _resolved, _staging;
    private readonly int _indexCount;
    private readonly TextureDesc _colorCopy;
    private readonly bool _multisampled, _showBackgroundPattern;
    private readonly Dictionary<LegacyShaderPreviewPass, (nint Layout, nint Vertex, nint Pixel, nint Blend, nint Depth, nint Rasterizer)> _passes = new();
    private readonly Dictionary<string, GpuTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (nint Texture, nint View)> _grabs = new(StringComparer.Ordinal);
    private readonly Dictionary<ShaderPreviewBuffer, nint> _constantBuffers = new();
    private nint _grabSampler;

    internal D3D11ShaderPreviewRenderer(ShaderPreviewScene scene)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unity 5.3 shader preview requires Direct3D 11 on Windows.");
        }

        _scene = scene;
        _device = new D3D11PreviewDevice();
        try
        {
            var (vertices, indices) = scene.Geometry switch
            {
                ShaderPreviewGeometry.Particles => (new Vertex[ParticleCount * 4], ParticleIndices),
                ShaderPreviewGeometry.Plane => Plane,
                ShaderPreviewGeometry.ScreenQuad => ScreenQuad,
                ShaderPreviewGeometry.Skybox or ShaderPreviewGeometry.Volume => Skybox,
                _ => Sphere
            };
            _vertexBuffer = _device.CreateBuffer<Vertex>(vertices, BindVertexBuffer, mutable: scene.Geometry == ShaderPreviewGeometry.Particles);
            _indexBuffer = _device.CreateBuffer<uint>(indices, BindIndexBuffer);
            _indexCount = indices.Length;

            _multisampled = scene.Operations.OfType<ShaderPreviewDraw>().Any(draw => draw.Pass.State.AlphaToMask);
            _showBackgroundPattern = scene.Operations.Any(operation => operation is ShaderPreviewGrab);
            const int depthStencilFormat = 45; // D24_UNORM_S8_UINT
            const uint sampleCount = 4;
            if (_multisampled)
            {
                _device.RequireMultisampling(Rgba8, sampleCount);
                _device.RequireMultisampling(depthStencilFormat, sampleCount);
            }
            var frame = new TextureDesc
            {
                Width = FrameSize, Height = FrameSize, MipLevels = 1, ArraySize = 1,
                Format = Rgba8, SampleCount = _multisampled ? sampleCount : 1, BindFlags = BindRenderTarget
            };
            _target = _device.CreateTexture(frame);
            _targetView = _device.CreateRenderView(_target);
            frame.Format = depthStencilFormat;
            frame.BindFlags = BindDepthStencil;
            _depth = _device.CreateDepthView(_device.CreateTexture(frame));
            frame.Format = Rgba8;
            frame.SampleCount = 1;
            frame.BindFlags = BindShaderResource;
            _colorCopy = frame;
            frame.BindFlags = 0;
            _resolved = _multisampled ? _device.CreateTexture(frame) : _target;
            frame.Usage = 3; // staging
            frame.CpuAccess = 0x20000; // read
            _staging = _device.CreateTexture(frame);
            if (scene.LightingPass != BuiltInLightingPass.None)
            {
                _lighting = new BuiltInLightingPreview(scene.LightingPass, scene.Lights[0], FrameSize);
            }
        }
        catch
        {
            _device.Dispose();
            throw;
        }
    }

    internal ShaderPreviewFrame Render(ShaderPreviewCamera camera, CancellationToken token, float time = 0, float deltaTime = 0)
    {
        token.ThrowIfCancellationRequested();
        var eye = new NumericsVector3(MathF.Sin(camera.Yaw) * MathF.Cos(camera.Pitch), MathF.Sin(camera.Pitch),
            MathF.Cos(camera.Yaw) * MathF.Cos(camera.Pitch)) * camera.Distance;
        NumericsMatrix4x4 view = NumericsMatrix4x4.CreateLookAt(eye, NumericsVector3.Zero, NumericsVector3.UnitY);
        if (_scene.Geometry == ShaderPreviewGeometry.Skybox)
        {
            view.Translation = NumericsVector3.Zero;
        }
        if (_lighting is { } lighting && _lightingCamera != camera)
        {
            lighting.RenderInputs(view, Projection, eye, token);
            _device.ClearBindings();
            foreach (var (name, texture) in lighting.Textures)
            {
                if (!_textures.TryGetValue(name, out var gpu))
                {
                    _textures.Add(name, UploadTexture(_device, texture, name, mutable: !texture.Comparison));
                }
                else if (!texture.Comparison)
                {
                    _device.UpdateTexture2D(gpu.Image, texture.Pixels, (uint)(texture.Width * texture.BytesPerBlock));
                }
            }
            _lightingCamera = camera;
        }
        else
        {
            var pan = new NumericsVector3(camera.Pan, 0);
            eye -= NumericsVector3.TransformNormal(pan, NumericsMatrix4x4.Transpose(view));
            view.Translation += pan;
            if (_scene.Geometry == ShaderPreviewGeometry.Particles)
            {
                Span<Vertex> particles = stackalloc Vertex[ParticleCount * 4];
                UpdateParticles(particles, view, time);
                _device.UpdateBuffer<Vertex>(_vertexBuffer, particles);
            }
        }
        _device.ClearFrame(_targetView, _depth, FrameSize, _showBackgroundPattern);
        var capturedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in _scene.Operations)
        {
            token.ThrowIfCancellationRequested();
            if (operation is ShaderPreviewGrab grab)
            {
                if (grab.Once && capturedNames.Contains(grab.TextureName))
                {
                    continue;
                }
                _device.ClearBindings();
                if (!_grabs.TryGetValue(grab.TextureName, out var copy))
                {
                    nint texture = _device.CreateTexture(_colorCopy);
                    copy = (texture, _device.CreateShaderView(texture));
                    _grabs.Add(grab.TextureName, copy);
                }
                _device.CopyColor(_target, copy.Texture, Rgba8, _multisampled);
                capturedNames.Add(grab.TextureName);
                if (_grabSampler == 0)
                {
                    _grabSampler = CreateSampler(_device, filterMode: 1, wrapMode: 1, anisotropy: 1, bias: 0);
                }
                continue;
            }
            var draw = (ShaderPreviewDraw)operation;
            try
            {
                DrawPass(draw);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or InvalidDataException)
            {
                throw new InvalidOperationException($"{draw.Pass.ShaderName} · {draw.Pass.LightMode} {draw.Pass.PassName}. {error.Message}", error);
            }
        }
        _device.ClearBindings();
        if (_multisampled)
        {
            _device.CopyColor(_target, _resolved, Rgba8, multisampled: true);
        }
        token.ThrowIfCancellationRequested();
        var pixels = _device.ReadPixels(_resolved, _staging, Rgba8, FrameSize);
        // The preview is already composited over its scene background.
        for (int alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            pixels[alpha] = 255;
        }
        token.ThrowIfCancellationRequested();
        return new ShaderPreviewFrame(FrameSize, FrameSize, pixels);

        void DrawPass(ShaderPreviewDraw draw)
        {
            LegacyShaderPreviewPass pass = draw.Pass;
            if (!_passes.TryGetValue(pass, out var gpu))
            {
                ValidateStage(pass.Vertex, vertex: true);
                ValidateStage(pass.Fragment, vertex: false);
                gpu = (CreateInputLayout(_device, pass.Vertex.Bytecode), _device.CreateShader(pass.Vertex.Bytecode, vertex: true),
                    _device.CreateShader(pass.Fragment.Bytecode, vertex: false), CreateBlend(_device, pass.State),
                    CreateDepth(_device, pass.State), _device.CreateRasterizerState(new RasterizerDesc
                    {
                        FillMode = 3, CullMode = (int)pass.State.Cull, FrontCounterClockwise = 1,
                        DepthClipEnable = 1, DepthBias = pass.State.OffsetUnits, SlopeScaledDepthBias = pass.State.OffsetFactor,
                        MultisampleEnable = _multisampled ? 1 : 0
                    }));
                _passes.Add(pass, gpu);
            }
            _device.ClearBindings();
            _device.SetPass(_targetView, _depth, gpu.Blend, gpu.Depth, pass.State.Stencil?.Reference ?? 0, gpu.Rasterizer, FrameSize);
            _device.SetGeometry(gpu.Layout, _vertexBuffer, _indexBuffer, (uint)sizeof(Vertex));
            _device.SetShader(gpu.Vertex, vertex: true);
            _device.SetShader(gpu.Pixel, vertex: false);
            BindStage(pass.Vertex, draw.Light, vertex: true);
            BindStage(pass.Fragment, draw.Light, vertex: false);
            if (draw.SkyboxSide >= 0)
            {
                _device.Draw(6, draw.SkyboxSide * 6);
            }
            else
            {
                _device.Draw(_indexCount);
            }
        }

        void BindStage(ShaderPreviewStage stage, ShaderPreviewLight light, bool vertex)
        {
            var buffers = new HashSet<int>();
            foreach (var buffer in stage.Buffers)
            {
                token.ThrowIfCancellationRequested();
                if (!buffers.Add(buffer.Slot))
                {
                    throw new InvalidDataException($"Duplicate constant buffer binding b{buffer.Slot}.");
                }
                var data = ShaderPreviewService.BindConstants(_scene, buffer, light, view, Projection, eye, FrameSize, capturedNames, time, deltaTime, _lighting);
                if (_constantBuffers.TryGetValue(buffer, out nint gpuBuffer))
                {
                    _device.UpdateBuffer<byte>(gpuBuffer, data);
                }
                else
                {
                    gpuBuffer = _device.CreateBuffer<byte>(data, BindConstantBuffer, mutable: true);
                    _constantBuffers.Add(buffer, gpuBuffer);
                }
                _device.SetConstantBuffer(gpuBuffer, buffer.Slot, vertex);
            }
            var slots = new HashSet<int>();
            var samplers = new Dictionary<int, (int Filter, int Wrap, int Anisotropy, float Bias, bool Comparison)>();
            foreach (var binding in stage.Textures)
            {
                token.ThrowIfCancellationRequested();
                if (!slots.Add(binding.Slot))
                {
                    throw new InvalidDataException($"Duplicate texture register t{binding.Slot} for '{binding.Name}'.");
                }
                GpuTexture gpu;
                (int Filter, int Wrap, int Anisotropy, float Bias, bool Comparison) sampling;
                if (capturedNames.Contains(binding.Name) && _grabs.TryGetValue(binding.Name, out var grab))
                {
                    gpu = new GpuTexture(grab.Texture, grab.View, _grabSampler);
                    sampling = (1, 1, 1, 0, false);
                }
                else
                {
                    var resources = BuiltInLightingPreview.ProvidesTexture(_scene.LightingPass, binding.Name) ? _lighting!.Textures : _scene.Textures;
                    if (!resources.TryGetValue(binding.Name, out var texture))
                    {
                        throw new InvalidDataException($"Texture '{binding.Name}' is unavailable before this pass.");
                    }
                    sampling = (texture.Filter, texture.Wrap, texture.Anisotropy, texture.MipBias, texture.Comparison);
                    if (!_textures.TryGetValue(binding.Name, out gpu))
                    {
                        gpu = UploadTexture(_device, texture, binding.Name);
                        _textures.Add(binding.Name, gpu);
                    }
                }
                _device.SetTexture(gpu.View, binding.Slot, vertex);
                if (binding.SamplerSlot >= 0)
                {
                    if (samplers.TryGetValue(binding.SamplerSlot, out var shared) && shared != sampling)
                    {
                        throw new NotSupportedException($"Conflicting sampler bindings for '{binding.Name}'.");
                    }
                    samplers[binding.SamplerSlot] = sampling;
                    _device.SetSampler(gpu.Sampler, binding.SamplerSlot, vertex);
                }
            }
        }
    }

    public void Dispose() => _device.Dispose();

    private readonly record struct GpuTexture(nint Image, nint View, nint Sampler);

    private static GpuTexture UploadTexture(D3D11PreviewDevice device, ShaderPreviewTexture texture, string name, bool mutable = false)
    {
        int subresources = texture.MipCount * texture.Faces;
        var levels = stackalloc SubresourceData[subresources];
        fixed (byte* data = texture.Pixels)
        {
            int consumed = 0;
            for (int index = 0; index < subresources; index++)
            {
                int mip = index % texture.MipCount;
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
                levels[index] = new SubresourceData { Data = data + consumed, RowPitch = (uint)pitch, SlicePitch = (uint)length };
                consumed += length;
            }
            if (consumed != texture.Pixels.Length)
            {
                throw new InvalidDataException($"Unexpected data after the mips for texture '{name}'.");
            }
            nint image = device.CreateTexture(new TextureDesc
            {
                Width = (uint)texture.Width, Height = (uint)texture.Height, MipLevels = (uint)texture.MipCount,
                ArraySize = (uint)texture.Faces, Format = texture.Format, SampleCount = 1, Usage = mutable ? 0u : 1u, BindFlags = BindShaderResource,
                MiscFlags = texture.Faces == 6 ? 4u : 0u // D3D11_RESOURCE_MISC_TEXTURECUBE
            }, levels);
            nint sampler = CreateSampler(device, texture.Filter, texture.Wrap, texture.Anisotropy, texture.MipBias, texture.Comparison);
            var view = texture.Faces == 6 ? device.CreateCubeView(image, texture.Format, texture.MipCount) : device.CreateShaderView(image);
            return new GpuTexture(image, view, sampler);
        }
    }

    private static nint CreateSampler(D3D11PreviewDevice device, int filterMode, int wrapMode, int anisotropy, float bias, bool comparison = false)
    {
        uint wrap = wrapMode switch { 0 => 1, 1 => 3, 2 => 2, 3 => 5, _ => throw new InvalidDataException("Invalid wrap mode.") };
        uint filter = anisotropy > 1 ? 0x55u : filterMode switch
        {
            0 => 0, 1 => 0x14, 2 => 0x15, _ => throw new InvalidDataException("Invalid filter mode.")
        };
        return device.CreateSamplerState(new SamplerDesc
        {
            Filter = comparison ? filter | 0x80u : filter, AddressU = wrap, AddressV = wrap, AddressW = wrap,
            MaxAnisotropy = (uint)Math.Max(1, anisotropy), LodBias = bias,
            Comparison = (uint)(comparison ? ShaderPreviewDepth.LEqual : ShaderPreviewDepth.Always), MaxLod = float.MaxValue
        });
    }

    private static nint CreateDepth(D3D11PreviewDevice device, ShaderPreviewState state)
    {
        var stencil = state.Stencil;
        return device.CreateDepthState(new DepthDesc
        {
            Enable = state.ZWrite || state.ZTest != ShaderPreviewDepth.Off ? 1 : 0,
            WriteMask = state.ZWrite ? 1 : 0,
            Function = state.ZTest == ShaderPreviewDepth.Off ? 8 : (int)state.ZTest,
            StencilEnable = stencil == null ? 0 : 1,
            StencilReadMask = stencil?.ReadMask ?? 255, StencilWriteMask = stencil?.WriteMask ?? 255,
            Front = MapFace(stencil?.Front), Back = MapFace(stencil?.Back)
        });

        static StencilFace MapFace(ShaderPreviewStencilFace? face) => new()
        {
            Fail = (int)(face?.Fail ?? ShaderPreviewStencilOp.Keep),
            DepthFail = (int)(face?.DepthFail ?? ShaderPreviewStencilOp.Keep),
            Pass = (int)(face?.Pass ?? ShaderPreviewStencilOp.Keep),
            Function = face == null || face.Function == ShaderPreviewDepth.Off ? 8 : (int)face.Function
        };
    }

    private static nint CreateBlend(D3D11PreviewDevice device, ShaderPreviewState state) =>
        device.CreateBlendState(new BlendDesc
        {
            AlphaToCoverage = state.AlphaToMask ? 1 : 0,
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

    private void ValidateStage(ShaderPreviewStage stage, bool vertex)
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

    private void ValidateResources(ReadOnlySpan<byte> program, ShaderPreviewStage stage)
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
                    declareTexture => Array.Exists(stage.Textures, texture => texture.Slot == slot &&
                        flags == (texture.Dimension == ShaderPreviewTextureDimension.Cube ? 6u : 3u)),
                    declareSampler => Array.Exists(stage.Textures, texture => texture.SamplerSlot == slot && flags ==
                        (_lighting?.Textures.TryGetValue(texture.Name, out var lightingTexture) == true && lightingTexture.Comparison ? 1u : 0u)),
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
            bool uvChannel = semanticIndex <= 3 && name.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase);
            if (systemValue != 0 || (semanticIndex != 0 && !uvChannel) || componentType != 3 /* float */)
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
            elements[elementCount++] = new InputElement { Semantic = semantic, SemanticIndex = semanticIndex, Offset = offset, Format = format };
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
    private readonly record struct Vertex(NumericsVector4 Position, NumericsVector4 Normal, NumericsVector4 Tangent,
        NumericsVector4 Color, NumericsVector4 Uv);

    private static (Vertex[], uint[]) BuildPlane() =>
        ([PlaneVertex(-1, -1, 0, 0), PlaneVertex(1, -1, 1, 0), PlaneVertex(1, 1, 1, 1), PlaneVertex(-1, 1, 0, 1)],
         [0, 1, 2, 0, 2, 3]);

    private static (Vertex[], uint[]) BuildScreenQuad()
    {
        const float far = ShaderPreviewCamera.Far;
        return (Array.ConvertAll(Plane.Vertices, vertex => vertex with
        {
            // Unity stores the perspective ray in NORMAL on its internal full-screen quad.
            Normal = new NumericsVector4(vertex.Position.X * far / Projection.M11, vertex.Position.Y * far / Projection.M22, far, 0),
            Uv = vertex.Uv with { Y = 1 - vertex.Uv.Y }
        }), Plane.Indices);
    }

    private static (Vertex[], uint[]) BuildSkybox()
    {
        (NumericsVector3 Forward, NumericsVector3 Right, NumericsVector3 Up)[] faces =
        [
            (NumericsVector3.UnitZ, -NumericsVector3.UnitX, NumericsVector3.UnitY),
            (-NumericsVector3.UnitZ, NumericsVector3.UnitX, NumericsVector3.UnitY),
            (NumericsVector3.UnitX, NumericsVector3.UnitZ, NumericsVector3.UnitY),
            (-NumericsVector3.UnitX, -NumericsVector3.UnitZ, NumericsVector3.UnitY),
            (NumericsVector3.UnitY, -NumericsVector3.UnitX, -NumericsVector3.UnitZ),
            (-NumericsVector3.UnitY, -NumericsVector3.UnitX, NumericsVector3.UnitZ)
        ];
        var vertices = new Vertex[24];
        var indices = new uint[36];
        for (int face = 0; face < faces.Length; face++)
        {
            var (forward, right, up) = faces[face];
            for (int i = 0; i < Plane.Vertices.Length; i++)
            {
                var vertex = Plane.Vertices[i];
                var position = forward + vertex.Position.X * right + vertex.Position.Y * up;
                vertices[face * 4 + i] = vertex with { Position = new NumericsVector4(position, 1), Normal = new NumericsVector4(-forward, 0) };
            }
            for (int i = 0; i < Plane.Indices.Length; i++)
            {
                indices[face * 6 + i] = (uint)(face * 4) + Plane.Indices[i];
            }
        }
        return (vertices, indices);
    }

    private static uint[] BuildParticleIndices()
    {
        var indices = new uint[ParticleCount * 6];
        for (int particle = 0; particle < ParticleCount; particle++)
        {
            for (int index = 0; index < Plane.Indices.Length; index++)
            {
                indices[particle * 6 + index] = (uint)(particle * 4) + Plane.Indices[index];
            }
        }
        return indices;
    }

    private static void UpdateParticles(Span<Vertex> vertices, NumericsMatrix4x4 view, float time)
    {
        var right = new NumericsVector3(view.M11, view.M21, view.M31);
        var up = new NumericsVector3(view.M12, view.M22, view.M32);
        var normal = new NumericsVector4(NumericsVector3.Cross(right, up), 0);
        var tangent = new NumericsVector4(right, 1);
        for (int particle = 0; particle < ParticleCount; particle++)
        {
            float age = (time * 0.75f + (float)particle / ParticleCount) % 1;
            float angle = particle * 2.39996f;
            float radius = 0.22f + age * 0.28f;
            float spread = 0.13f + 0.44f * age;
            var center = new NumericsVector3(spread * MathF.Cos(angle), age * 1.4f - 0.65f, spread * MathF.Sin(angle));
            var color = new NumericsVector4(1, 1, 1, MathF.Sin(age * MathF.PI) * 0.8f);
            for (int corner = 0; corner < Plane.Vertices.Length; corner++)
            {
                var template = Plane.Vertices[corner];
                var position = center + radius * (template.Position.X * right + template.Position.Y * up);
                vertices[4 * particle + corner] = template with { Position = new NumericsVector4(position, 1), Normal = normal, Tangent = tangent, Color = color };
            }
        }
    }

    private static Vertex PlaneVertex(float x, float y, float u, float v)
    {
        var uv = new NumericsVector4(u, v, 0, 0);
        return new Vertex(new NumericsVector4(x, y, 0, 1), new NumericsVector4(0, 0, 1, 0),
            new NumericsVector4(1, 0, 0, 1), NumericsVector4.One, uv);
    }

    private static (Vertex[], uint[]) BuildSphere()
    {
        var (positions, uvs, triangles) = MaterialPreviewGeometry.CreateSphere(32, 32);
        var vertices = new Vertex[positions.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            float phi = uvs[i].X * MathF.Tau;
            vertices[i] = new Vertex(new NumericsVector4(positions[i], 1), new NumericsVector4(positions[i], 0),
                new NumericsVector4(-MathF.Sin(phi), 0, MathF.Cos(phi), 1), NumericsVector4.One, new NumericsVector4(uvs[i], 0, 0));
        }
        var indices = new uint[triangles.Length];
        for (int i = 0; i < indices.Length; i += 3)
        {
            indices[i] = (uint)triangles[i];
            indices[i + 1] = (uint)triangles[i + 2];
            indices[i + 2] = (uint)triangles[i + 1];
        }
        return (vertices, indices);
    }
}
