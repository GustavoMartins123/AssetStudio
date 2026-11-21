using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AssetStudio.Avalonia.Services;

internal sealed unsafe class D3D11PreviewDevice : IDisposable
{
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

    internal nint CreateBuffer<T>(ReadOnlySpan<T> data, uint flags) where T : unmanaged
    {
        var desc = new BufferDesc { ByteWidth = checked((uint)(data.Length * sizeof(T))), Usage = 1, BindFlags = flags };
        nint buffer = 0;
        fixed (T* pixels = data)
        {
            var initialData = new SubresourceData { Data = pixels };
            var create = (delegate* unmanaged[Stdcall]<nint, BufferDesc*, SubresourceData*, nint*, int>)Slot(_device, 3);
            return Keep(create(_device, &desc, &initialData, &buffer), buffer, "create a buffer");
        }
    }

    internal nint CreateTexture(TextureDesc desc, SubresourceData* initialData = null)
    {
        nint texture = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, TextureDesc*, SubresourceData*, nint*, int>)Slot(_device, 5);
        return Keep(create(_device, &desc, initialData, &texture), texture, "create a texture");
    }

    internal nint CreateShaderView(nint texture) => CreateView(7, texture, "create a shader resource view");
    internal nint CreateRenderView(nint texture) => CreateView(9, texture, "create the render target view");
    internal nint CreateDepthView(nint texture) => CreateView(10, texture, "create the depth view");

    private nint CreateView(int slot, nint texture, string operation)
    {
        nint view = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)Slot(_device, slot);
        return Keep(create(_device, texture, null, &view), view, operation);
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

    internal nint CreateShader(byte[] bytecode, bool vertex)
    {
        nint shader = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, void*, nuint, nint, nint*, int>)Slot(_device, vertex ? 12 : 15);
        fixed (byte* code = bytecode)
        {
            return Keep(create(_device, code, (nuint)bytecode.Length, 0, &shader), shader,
                vertex ? "create the vertex shader" : "create the pixel shader");
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

    internal void SetFrame(nint surface, nint depth, nint blendState, nint depthState, nint rasterizerState, int size)
    {
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(_context, 33))(_context, 1, &surface, depth);
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Slot(_context, 35))(_context, blendState, null, uint.MaxValue);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 36))(_context, depthState, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(_context, 43))(_context, rasterizerState);
        var viewport = new Viewport { Width = size, Height = size, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)Slot(_context, 44))(_context, 1, &viewport);
        float* clear = stackalloc float[] { 0.18f, 0.18f, 0.18f, 1f };
        ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Slot(_context, 50))(_context, surface, clear);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, float, byte, void>)Slot(_context, 53))(_context, depth, 1, 1f, 0);
    }

    internal void SetGeometry(nint layout, nint vertices, nint indices, uint stride)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(_context, 17))(_context, layout);
        uint offset = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, uint*, void>)Slot(_context, 18))(
            _context, 0, 1, &vertices, &stride, &offset);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void>)Slot(_context, 19))(_context, indices, 42 /* R32_UINT */, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(_context, 24))(_context, 4 /* triangle list */);
    }

    internal void SetShader(nint shader, bool vertex) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(_context, vertex ? 11 : 9))(_context, shader, null, 0);

    internal void SetConstantBuffer(nint buffer, int slot, bool vertex) => Bind(buffer, slot, vertex ? 7 : 16);
    internal void SetTexture(nint view, int slot, bool vertex) => Bind(view, slot, vertex ? 25 : 8);
    internal void SetSampler(nint sampler, int slot, bool vertex) => Bind(sampler, slot, vertex ? 26 : 10);

    private void Bind(nint resource, int slot, int method) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, method))(_context, (uint)slot, 1, &resource);

    internal void Draw(int indexCount) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int, void>)Slot(_context, 12))(_context, (uint)indexCount, 0, 0);

    internal byte[] ReadPixels(nint source, nint staging, int size)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47))(_context, staging, source);
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
    internal struct SubresourceData
    {
        public void* Data;
        public uint RowPitch, SlicePitch;
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
