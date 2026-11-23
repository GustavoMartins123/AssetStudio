using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AssetStudio.Avalonia.Services;

internal sealed unsafe class D3D11PreviewDevice : IDisposable
{
    internal const int Rgba16Float = 10, Rgba8 = 28, R32Typeless = 39, D32Float = 40, R32Float = 41, R16Float = 54, R8Unorm = 61;
    internal const uint TriangleList = 4, TrianglePatchList = 35;

    // Slots are from the Windows SDK definitions of ID3D11Device and ID3D11DeviceContext.
    private readonly nint _device;
    private readonly nint _context;
    private readonly List<nint> _resources = new();

    internal D3D11PreviewDevice()
    {
        nint device = 0;
        nint context = 0;
        int result = D3D11CreateDevice(0, 1 /* Hardware */, 0, 0x20 /* BGRA support */,
            null, 0, 7 /* SDK version */, &device, null, &context);
        if (result < 0 || device == 0 || context == 0)
        {
            Release(context);
            Release(device);
            Check(result, "create the hardware device");
            throw new InvalidOperationException("Direct3D 11 did not return a hardware device and context.");
        }
        _device = device;
        _context = context;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        uint* featureLevels, uint featureLevelCount, uint sdkVersion, nint* device, uint* featureLevel, nint* context);

    internal nint CreateBuffer<T>(ReadOnlySpan<T> data, uint flags, bool mutable = false, bool structured = false) where T : unmanaged
    {
        var desc = new BufferDesc
        {
            ByteWidth = checked((uint)(data.Length * sizeof(T))), Usage = mutable ? 0u : 1u, BindFlags = flags,
            StructureStride = structured ? (uint)sizeof(T) : 0, MiscFlags = structured ? 0x40u : 0
        };
        fixed (T* pixels = data)
        {
            var initialData = new SubresourceData { Data = pixels };
            return CreateBuffer(desc, &initialData);
        }
    }

    internal nint CreateStructuredView(nint buffer, uint count)
    {
        var desc = new ShaderViewDesc { Dimension = 1, MipLevels = count }; // BUFFER union: first element, number of elements.
        return CreateView(7, buffer, "create a structured buffer view", &desc);
    }

    private nint CreateBuffer(BufferDesc desc, SubresourceData* initialData = null)
    {
        nint buffer = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, BufferDesc*, SubresourceData*, nint*, int>)Slot(_device, 3);
        return Keep(create(_device, &desc, initialData, &buffer), buffer, "create a buffer");
    }

    internal (nint ShaderView, nint AppendView, nint DrawArguments) CreateAppendBuffer(uint capacity, uint stride)
    {
        nint points = CreateWritableBuffer(capacity, stride);
        uint* args = stackalloc uint[] { 0, 1, 0, 0 };
        var initial = new SubresourceData { Data = args };
        nint indirect = CreateBuffer(new BufferDesc { ByteWidth = 16, MiscFlags = 0x10 }, &initial);
        return (CreateStructuredView(points, capacity),
            CreateUnorderedBufferView(points, capacity, flags: 2 /* D3D11_BUFFER_UAV_FLAG_APPEND */), indirect);
    }

    internal (nint ShaderView, nint UnorderedView) CreateComputeBuffer(uint count, uint stride)
    {
        nint buffer = CreateWritableBuffer(count, stride);
        return (CreateStructuredView(buffer, count), CreateUnorderedBufferView(buffer, count, flags: 0));
    }

    private nint CreateWritableBuffer(uint count, uint stride) => CreateBuffer(new BufferDesc
    {
        ByteWidth = checked(count * stride), StructureStride = stride,
        BindFlags = 0x88, MiscFlags = 0x40 // Shader resource, unordered access and structured buffer.
    });

    private nint CreateUnorderedBufferView(nint buffer, uint count, uint flags)
    {
        var desc = new BufferUavDesc { Dimension = 1, Count = count, Flags = flags };
        return CreateView(8, buffer, "create the unordered buffer view", &desc);
    }

    internal void UpdateBuffer<T>(nint buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        fixed (T* bytes = data)
        {
            UpdateResource(buffer, bytes, 0);
        }
    }

    internal void UpdateTexture2D(nint texture, ReadOnlySpan<byte> pixels, uint rowPitch, uint subresource)
    {
        fixed (byte* bytes = pixels)
        {
            UpdateResource(texture, bytes, rowPitch, subresource);
        }
    }

    private void UpdateResource(nint resource, void* data, uint rowPitch, uint subresource = 0) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)Slot(_context, 48))(
            _context, resource, subresource, null, data, rowPitch, 0);

    internal nint CreateTexture(TextureDesc desc, SubresourceData* initialData = null)
    {
        nint texture = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, TextureDesc*, SubresourceData*, nint*, int>)Slot(_device, 5);
        return Keep(create(_device, &desc, initialData, &texture), texture, "create a texture");
    }

    internal nint CreateTexture3D(Texture3DDesc desc, SubresourceData* initialData)
    {
        nint texture = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, Texture3DDesc*, SubresourceData*, nint*, int>)Slot(_device, 6);
        return Keep(create(_device, &desc, initialData, &texture), texture, "create a 3D texture");
    }

    internal nint CreateShaderView(nint texture) => CreateView(7, texture, "create a shader resource view");
    internal nint CreateCubeView(nint texture, int format, int mips)
    {
        var desc = new ShaderViewDesc { Format = format, Dimension = 9, MipLevels = (uint)mips }; // D3D11_SRV_DIMENSION_TEXTURECUBE
        return CreateView(7, texture, "create a cubemap shader resource view", &desc);
    }
    internal nint CreateRenderView(nint texture) => CreateView(9, texture, "create the render target view");
    internal nint CreateDepthView(nint texture) => CreateView(10, texture, "create the depth view");
    internal nint CreateUnorderedTextureView(nint texture) => CreateView(8, texture, "create the unordered texture view");

    internal nint CreateDepth32View(nint texture, uint samples)
    {
        var desc = new DepthViewDesc { Format = D32Float, Dimension = samples == 1 ? 3 : 5 }; // TEXTURE2D or TEXTURE2DMS
        return CreateView(10, texture, "create the 32-bit depth view", &desc);
    }

    internal nint CreateDepth32ShaderView(nint texture, uint samples)
    {
        var desc = new ShaderViewDesc
        {
            Format = R32Float, Dimension = samples == 1 ? 4 : 6, MipLevels = samples == 1 ? 1u : 0u
        }; // TEXTURE2D or TEXTURE2DMS
        return CreateView(7, texture, "create the depth shader resource view", &desc);
    }

    private nint CreateView(int slot, nint texture, string operation, void* desc = null)
    {
        nint view = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)Slot(_device, slot);
        return Keep(create(_device, texture, desc, &view), view, operation);
    }

    internal nint CreateLayout(InputElement* elements, int count, byte[] vertexBytecode)
    {
        nint layout = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, InputElement*, uint, void*, nuint, nint*, int>)Slot(_device, 11);
        fixed (byte* code = vertexBytecode)
        {
            return Keep(create(_device, elements, (uint)count, code, (nuint)vertexBytecode.Length, &layout),
                layout, "create the vertex input layout");
        }
    }

    internal nint CreateShader(byte[] bytecode, ShaderPreviewProgramKind kind)
    {
        nint shader = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, void*, nuint, nint, nint*, int>)Slot(_device, kind switch
        {
            ShaderPreviewProgramKind.Vertex => 12,
            ShaderPreviewProgramKind.Geometry => 13,
            ShaderPreviewProgramKind.Pixel => 15,
            ShaderPreviewProgramKind.Hull => 16,
            ShaderPreviewProgramKind.Domain => 17,
            ShaderPreviewProgramKind.Compute => 18,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
        fixed (byte* code = bytecode)
        {
            return Keep(create(_device, code, (nuint)bytecode.Length, 0, &shader), shader,
                $"create the {kind.ToString().ToLowerInvariant()} shader");
        }
    }

    internal nint CreateBlendState(BlendDesc desc) => CreateState(20, &desc, "create the blend state");
    internal nint CreateDepthState(DepthDesc desc) => CreateState(21, &desc, "create the depth state");
    internal nint CreateRasterizerState(RasterizerDesc desc) => CreateState(22, &desc, "create the rasterizer state");
    internal nint CreateSamplerState(SamplerDesc desc) => CreateState(23, &desc, "create the sampler state");

    private nint CreateState(int slot, void* desc, string operation)
    {
        nint state = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, void*, nint*, int>)Slot(_device, slot);
        return Keep(create(_device, desc, &state), state, operation);
    }

    internal void ClearBindings() =>
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_context, 110))(_context);

    internal void ClearFrame(nint surface, nint depth, int size, bool showBackgroundPattern, float background = 0.18f, float depthValue = 1f)
    {
        ClearColor(surface, background, 1f);
        ClearDepth(depth, depthValue, clearStencil: true);
        if (showBackgroundPattern)
        {
            ClearBackgroundTiles(surface, size);
        }
    }

    internal void ClearDepth(nint depth, float value, bool clearStencil = false) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)Slot(_context, 53))(
            _context, depth, clearStencil ? 3u : 1u, value, 0);

    internal void ClearColor(nint surface, float color = 0, float alpha = 0)
    {
        float* clear = stackalloc float[] { color, color, color, alpha };
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Slot(_context, 50))(_context, surface, clear);
    }

    private void ClearBackgroundTiles(nint surface, int size)
    {
        Guid interfaceId = new("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1"); // ID3D11DeviceContext1
        nint context1 = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(_context, 0))(_context, &interfaceId, &context1);
        Check(result, "get the Direct3D 11.1 context for the GrabPass preview background");
        if (context1 == 0)
        {
            throw new InvalidOperationException("Direct3D 11.1 returned no context for the GrabPass preview background.");
        }
        try
        {
            const int tiles = 8;
            var rects = stackalloc ClearRect[tiles * tiles / 2];
            int count = 0;
            for (int row = 0; row < tiles; row++)
            {
                for (int column = row % 2; column < tiles; column += 2)
                {
                    rects[count++] = new ClearRect
                    {
                        Left = column * size / tiles, Top = row * size / tiles,
                        Right = (column + 1) * size / tiles, Bottom = (row + 1) * size / tiles
                    };
                }
            }
            float* color = stackalloc float[] { 0.32f, 0.35f, 0.40f, 1f };
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, ClearRect*, uint, void>)Slot(context1, 132))(
                context1, surface, color, rects, (uint)count);
        }
        finally
        {
            Release(context1);
        }
    }

    internal void SetPass(nint surface, nint depth, nint blendState, nint depthState, byte stencilReference, nint rasterizerState,
        int size, nint secondarySurface = 0, int x = 0, int y = 0)
    {
        nint* outputs = stackalloc nint[] { surface, secondarySurface };
        uint outputCount = secondarySurface != 0 ? 2u : surface != 0 ? 1u : 0u;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(_context, 33))(
            _context, outputCount, outputCount == 0 ? null : outputs, depth);
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Slot(_context, 35))(_context, blendState, null, uint.MaxValue);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 36))(_context, depthState, stencilReference);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(_context, 43))(_context, rasterizerState);
        var viewport = new Viewport { X = x, Y = y, Width = size, Height = size, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)Slot(_context, 44))(_context, 1, &viewport);
    }

    internal void SetGeometry(nint layout, nint vertices, nint indices, uint stride, uint topology)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(_context, 17))(_context, layout);
        uint offset = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, uint*, void>)Slot(_context, 18))(
            _context, 0, 1, &vertices, &stride, &offset);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void>)Slot(_context, 19))(_context, indices, 42 /* R32_UINT */, 0);
        SetTopology(topology);
    }

    internal void SetTopology(uint topology) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(_context, 24))(_context, topology);

    internal void SetShader(nint shader, ShaderPreviewProgramKind kind) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(_context, kind switch
        {
            ShaderPreviewProgramKind.Vertex => 11,
            ShaderPreviewProgramKind.Geometry => 23,
            ShaderPreviewProgramKind.Pixel => 9,
            ShaderPreviewProgramKind.Hull => 60,
            ShaderPreviewProgramKind.Domain => 64,
            ShaderPreviewProgramKind.Compute => 69,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }))(_context, shader, null, 0);

    internal void SetConstantBuffer(nint buffer, int slot, ShaderPreviewProgramKind kind) =>
        Bind(buffer, slot, kind switch
        {
            ShaderPreviewProgramKind.Vertex => 7, ShaderPreviewProgramKind.Geometry => 22,
            ShaderPreviewProgramKind.Pixel => 16, ShaderPreviewProgramKind.Compute => 71,
            ShaderPreviewProgramKind.Hull => 62, ShaderPreviewProgramKind.Domain => 66,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
    internal void SetTexture(nint view, int slot, ShaderPreviewProgramKind kind) =>
        Bind(view, slot, kind switch
        {
            ShaderPreviewProgramKind.Vertex => 25, ShaderPreviewProgramKind.Geometry => 31,
            ShaderPreviewProgramKind.Pixel => 8, ShaderPreviewProgramKind.Compute => 67,
            ShaderPreviewProgramKind.Hull => 59, ShaderPreviewProgramKind.Domain => 63,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
    internal void SetSampler(nint sampler, int slot, ShaderPreviewProgramKind kind) =>
        Bind(sampler, slot, kind switch
        {
            ShaderPreviewProgramKind.Vertex => 26, ShaderPreviewProgramKind.Geometry => 32,
            ShaderPreviewProgramKind.Pixel => 10, ShaderPreviewProgramKind.Compute => 70,
            ShaderPreviewProgramKind.Hull => 61, ShaderPreviewProgramKind.Domain => 65,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

    internal void SetComputeOutput(nint view, int slot) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(_context, 68))(
            _context, (uint)slot, 1, &view, null);

    internal void Dispatch(uint x, uint y, uint z) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(_context, 41))(_context, x, y, z);

    internal void SetAppendTarget(nint surface, nint depth, nint appendView)
    {
        uint resetCount = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, uint, uint, nint*, uint*, void>)Slot(_context, 34))(
            _context, 1, &surface, depth, 1, 1, &appendView, &resetCount);
    }

    internal void DrawPoints(nint arguments, nint appendView)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void>)Slot(_context, 49))(_context, arguments, 0, appendView);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(_context, 24))(_context, 1 /* point list */);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 40))(_context, arguments, 0);
    }

    private void Bind(nint resource, int slot, int method) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, method))(_context, (uint)slot, 1, &resource);

    internal void Draw(int indexCount, int startIndex = 0) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, void>)Slot(_context, 12))(_context, (uint)indexCount, (uint)startIndex, 0);

    internal void DrawVertices(int count) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(_context, 13))(_context, (uint)count, 0);

    internal void RequireMultisampling(int format, uint samples)
    {
        uint levels = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, int, uint, uint*, int>)Slot(_device, 30))(_device, format, samples, &levels);
        Check(result, "check multisampled support");
        if (levels == 0)
        {
            throw new NotSupportedException($"D3D11 format {format} does not support {samples} samples.");
        }
    }

    internal void CopyColor(nint source, nint destination, int format, bool multisampled)
    {
        if (multisampled)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, uint, int, void>)Slot(_context, 57))(
                _context, destination, 0, source, 0, format);
        }
        else
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47))(_context, destination, source);
        }
    }

    internal void CopySubresource(nint source, uint sourceIndex, nint destination, uint destinationIndex) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)Slot(_context, 46))(
            _context, destination, destinationIndex, 0, 0, 0, source, sourceIndex, null);

    internal void RequireVertexRenderTargetArrayIndex()
    {
        const uint options3 = 15; // D3D11_FEATURE_D3D11_OPTIONS3
        uint supported = 0;
        var check = (delegate* unmanaged[Stdcall]<nint, uint, void*, uint, int>)Slot(_device, 33);
        if (check(_device, options3, &supported, sizeof(uint)) < 0 || supported == 0)
        {
            throw new NotSupportedException("This Direct3D 11 device cannot select a render target array slice from a vertex shader.");
        }
    }

    internal byte[] ReadPixels(nint source, nint staging, int format, int size)
    {
        CopyColor(source, staging, format, multisampled: false);
        MappedResource mapped = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)Slot(_context, 14))(
            _context, staging, 0, 1 /* read */, 0, &mapped);
        Check(hr, "read the rendered pixels");
        try
        {
            int rowBytes = size * 4;
            if (mapped.Data == null || mapped.RowPitch < rowBytes || mapped.RowPitch > int.MaxValue / size)
            {
                throw new InvalidDataException("Direct3D 11 returned an invalid staging row pitch.");
            }
            var result = new byte[rowBytes * size];
            for (int row = 0; row < size; row++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.Data + row * (int)mapped.RowPitch, rowBytes)
                    .CopyTo(result.AsSpan(row * rowBytes, rowBytes));
            }
            return result;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15))(_context, staging, 0);
        }
    }

    private nint Keep(int hr, nint resource, string operation)
    {
        if (hr < 0)
        {
            Release(resource);
            Check(hr, operation);
        }
        if (resource == 0)
        {
            throw new InvalidOperationException($"Direct3D 11 returned no resource while attempting to {operation}.");
        }
        _resources.Add(resource);
        return resource;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"Direct3D 11 could not {operation} (0x{result:X8}).");
        }
    }

    private static void* Slot(nint owner, int index) => (*(void***)owner)[index];

    private static void Release(nint resource)
    {
        if (resource != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(resource, 2))(resource);
        }
    }

    public void Dispose()
    {
        Release(_context);
        for (int i = _resources.Count - 1; i >= 0; i--)
        {
            Release(_resources[i]);
        }
        Release(_device);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CpuAccess, MiscFlags, StructureStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Texture3DDesc
    {
        public uint Width, Height, Depth, MipLevels;
        public int Format;
        public uint Usage, BindFlags, CpuAccess, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SubresourceData
    {
        public void* Data;
        public uint RowPitch, SlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderViewDesc
    {
        public int Format, Dimension;
        public uint MostDetailedMip, MipLevels, Reserved0, Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DepthViewDesc
    {
        public int Format, Dimension;
        public uint Flags, MipSlice, FirstArraySlice, ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferUavDesc
    {
        public int Format, Dimension;
        public uint First, Count, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedResource
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Viewport
    {
        public float X, Y, Width, Height, MinDepth, MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClearRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InputElement
    {
        public byte* Semantic;
        public uint SemanticIndex, Format, InputSlot, Offset, Classification, InstanceStepRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RasterizerDesc
    {
        public int FillMode, CullMode, FrontCounterClockwise, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StencilFace
    {
        public int Fail, DepthFail, Pass, Function;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DepthDesc
    {
        public int Enable, WriteMask, Function, StencilEnable;
        public byte StencilReadMask, StencilWriteMask;
        public StencilFace Front, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendTarget
    {
        public int Enabled, Source, Destination, Operation, SourceAlpha, DestinationAlpha, AlphaOperation;
        public byte ColorMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendDesc
    {
        public int AlphaToCoverage, IndependentBlend;
        public BlendTarget Target;
        private fixed uint _otherTargets[7 * 8];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SamplerDesc
    {
        public uint Filter, AddressU, AddressV, AddressW;
        public float LodBias;
        public uint MaxAnisotropy, Comparison;
        public fixed float BorderColor[4];
        public float MinLod, MaxLod;
    }
}
