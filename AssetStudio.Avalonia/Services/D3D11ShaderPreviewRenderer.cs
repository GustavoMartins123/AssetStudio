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
    private const uint BokehPointStride = 28;
    private const uint BindVertexBuffer = 1, BindIndexBuffer = 2, BindConstantBuffer = 4,
        BindShaderResource = 8, BindRenderTarget = 32, BindDepthStencil = 64;
    private static readonly NumericsMatrix4x4 Projection = NumericsMatrix4x4.CreatePerspectiveFieldOfView(35 * MathF.PI / 180, 1,
        ShaderPreviewCamera.Near, ShaderPreviewCamera.Far);
    private static readonly NumericsMatrix4x4 ReversedProjection = BuiltInLightingPreview.DepthProjection(Projection, reversedDepth: true);

    private static readonly (Vertex[] Vertices, uint[] Indices) Plane = BuildPlane();
    private static readonly (Vertex[] Vertices, uint[] Indices) TextPlane = BuildTextPlane();
    private static readonly (Vertex[] Vertices, uint[] Indices) ScreenQuad = BuildScreenQuad();
    private static readonly (Vertex[] Vertices, uint[] Indices) FogQuad = BuildScreenQuad(includeCornerIndices: true);
    private static readonly (Vertex[] Vertices, uint[] Indices) Sphere = BuildSphere();
    private static readonly (Vertex[] Vertices, uint[] Indices) Skybox = BuildSkybox();
    private const int ParticleCount = 8;
    private static readonly uint[] ParticleIndices = BuildParticleIndices();

    private readonly ShaderPreviewScene _scene;
    private readonly D3D11PreviewDevice _device;
    private readonly BuiltInLightingPreview? _lighting;
    private readonly ImageEffectPreview? _animatedEffect;
    private readonly IReadOnlyDictionary<string, ShaderPreviewTexture>? _cameraTextures;
    private ShaderPreviewCamera? _lightingCamera;
    private float _lightingTime;
    private MotionPreviewFrame? _lightingMotion;
    private readonly nint _vertexBuffer, _indexBuffer, _target, _targetView, _depth, _resolved, _staging;
    private readonly nint _depthTexture, _depthInputView, _depthInputState;
    private readonly GpuTexture _depthInput;
    private readonly int _indexCount;
    private readonly TextureDesc _colorCopy;
    private readonly bool _multisampled, _showBackgroundPattern;
    private readonly Dictionary<LegacyShaderPreviewPass, (nint Layout, nint Vertex, nint Pixel, nint Geometry,
        nint Blend, nint Depth, nint Rasterizer, ShaderPreviewParameter? UnusedVertexParameter)> _passes = new();
    private readonly (nint ShaderView, nint AppendView, nint DrawArguments) _bokehPoints;
    private readonly Vertex[]? _motionVertices;
    private ShaderPreviewCamera? _lastMotionCamera;
    private float _lastMotionTime;
    private readonly Dictionary<string, GpuTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (nint Texture, nint View)> _grabs = new(StringComparer.Ordinal);
    private readonly Dictionary<ShaderPreviewRenderTarget, (GpuTexture Texture, nint View)> _renderTargets = new();
    private readonly Dictionary<ShaderPreviewBuffer, nint> _constantBuffers = new();
    private readonly Dictionary<(int Filter, int Wrap), nint> _inlineSamplers = new();
    private readonly Dictionary<(ShaderPreviewGeometry Geometry, NumericsVector4? Color), (nint Vertices, nint Indices, int Count)> _drawMeshes = new();
    private nint _billboardVertices;
    private nint _grabSampler;
    private nint _exposureHistogram, _exposureHistogramView;
    private static readonly ShaderPreviewRenderTarget[] TemporalHistory =
        [new("Temporal antialiasing history A", FrameSize), new("Temporal antialiasing history B", FrameSize)];
    private int _historyReadIndex;
    private bool _historyReady;
    private System.Numerics.Vector2 _lightingJitter;

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
            var (vertices, indices) = GetMesh(scene.Geometry);
            if (scene.ImageEffect == ImageEffectPass.ObjectMotion && scene.Operations.OfType<ShaderPreviewDraw>()
                .Any(draw => UsesVertexNormal(draw.Pass.Vertex.Bytecode)))
            {
                _motionVertices = new Vertex[vertices.Length];
            }
            _vertexBuffer = _device.CreateBuffer<Vertex>(vertices, BindVertexBuffer,
                mutable: scene.Geometry == ShaderPreviewGeometry.Particles || _motionVertices != null);
            _indexBuffer = _device.CreateBuffer<uint>(indices, BindIndexBuffer);
            _indexCount = indices.Length;

            _multisampled = scene.Operations.OfType<ShaderPreviewDraw>().Any(draw => draw.Pass.State.AlphaToMask);
            _showBackgroundPattern = scene.Operations.Any(operation => operation is ShaderPreviewGrab);
            bool depthBlit = scene.DepthBlitSamples != 0;
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
            if (!depthBlit)
            {
                _target = _device.CreateTexture(frame);
                _targetView = _device.CreateRenderView(_target);
            }
            frame.Format = depthBlit ? R32Typeless : depthStencilFormat;
            frame.BindFlags = BindDepthStencil;
            _depthTexture = _device.CreateTexture(frame);
            _depth = depthBlit ? _device.CreateDepth32View(_depthTexture, 1) : _device.CreateDepthView(_depthTexture);
            if (depthBlit)
            {
                var input = frame;
                input.SampleCount = scene.DepthBlitSamples;
                input.BindFlags |= BindShaderResource;
                if (input.SampleCount > 1)
                {
                    _device.RequireMultisampling(D32Float, input.SampleCount);
                }
                nint texture = _device.CreateTexture(input);
                _depthInputView = _device.CreateDepth32View(texture, input.SampleCount);
                _depthInput = new GpuTexture(texture, _device.CreateDepth32ShaderView(texture, input.SampleCount),
                    input.SampleCount == 1 ? CreateSampler(_device, filterMode: 0, wrapMode: 1, anisotropy: 1, bias: 0) : 0);
                _depthInputState = CreateDepth(_device, new ShaderPreviewState { ZTest = ShaderPreviewDepth.GEqual });
            }
            frame.Format = Rgba8;
            frame.SampleCount = 1;
            frame.BindFlags = BindShaderResource;
            _colorCopy = frame;
            frame.BindFlags = 0;
            _resolved = _multisampled ? _device.CreateTexture(frame) : _target;
            frame.Format = depthBlit ? R32Float : Rgba8;
            frame.Usage = 3; // staging
            frame.CpuAccess = 0x20000; // read
            _staging = _device.CreateTexture(frame);
            if (scene.ImageEffect == ImageEffectPass.Bokeh)
            {
                // The collection pass can append at most one point per pixel.
                _bokehPoints = _device.CreateAppendBuffer(FrameSize * FrameSize, BokehPointStride);
            }
            if (scene.CameraTextures.Count != 0)
            {
                _lighting = new BuiltInLightingPreview(scene.CameraTextures, scene.Lights[0], FrameSize, scene.ImageEffect,
                    scene.SerializedShader, scene.AmbientLinear);
                _cameraTextures = _lighting.Textures;
            }
            else if (scene.ImageEffect is { } effect && ImageEffectPreview.HasAnimatedInput(effect))
            {
                _animatedEffect = new ImageEffectPreview(effect, FrameSize);
                _cameraTextures = _animatedEffect.Textures;
            }
        }
        catch
        {
            _device.Dispose();
            throw;
        }
    }

    internal ShaderPreviewFrame Render(ShaderPreviewCamera camera, CancellationToken token, float time = 0,
        float deltaTime = 0, bool resetMotionHistory = false)
    {
        token.ThrowIfCancellationRequested();
        const float motionStart = 0.42f;
        float motionTime = time + motionStart;
        bool motionBlur = MotionPreview.IsMotionBlur(_scene.ImageEffect);
        bool reproject = MotionPreview.UsesCameraHistory(_scene.ImageEffect);
        bool temporal = _scene.ImageEffect == ImageEffectPass.TemporalAntialiasing;
        var jitter = temporal ? TemporalAntialiasingPreview.Jitter(time, FrameSize) : default;
        var renderCamera = reproject ? MotionPreview.CameraAtTime(camera, motionTime) : camera;
        var (view, eye) = CameraView(renderCamera, _scene.Geometry == ShaderPreviewGeometry.Skybox);
        MotionPreviewFrame? motion = null;
        if (MotionPreview.UsesHistory(_scene.ImageEffect))
        {
            bool hasHistory = !resetMotionHistory && _lastMotionCamera.HasValue && time >= _lastMotionTime;
            var previousCamera = hasHistory ? _lastMotionCamera.GetValueOrDefault() : camera;
            float previousTime = hasHistory ? _lastMotionTime : time;
            float elapsed = time - previousTime;
            // Camera input can change while the timeline is paused. Measure that movement over one preview frame.
            float scale = elapsed > 0 ? MotionPreview.MotionScale / elapsed :
                previousCamera != camera ? MotionPreview.MotionScale / MotionPreview.FrameInterval : 0;
            if (reproject)
            {
                previousCamera = MotionPreview.CameraAtTime(previousCamera, previousTime + motionStart);
            }
            var (previousView, _) = CameraView(previousCamera, skybox: false);
            var frame = MotionPreview.CreateFrame(view, previousView, Projection, motionTime, previousTime + motionStart, scale,
                motionBlur || _scene.ImageEffect == ImageEffectPass.ObjectMotion, motionBlur || _motionVertices != null);
            motion = frame;
            if (_motionVertices is { } deformed)
            {
                for (int i = 0; i < deformed.Length; i++)
                {
                    var vertex = Sphere.Vertices[i];
                    var position = new NumericsVector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                    // The Amplify CPU vertex programs consume the previous local position in NORMAL.
                    deformed[i] = vertex with
                    {
                        Position = new NumericsVector4(position * frame.Deformation, vertex.Position.W),
                        Normal = new NumericsVector4(position * frame.PreviousDeformation, 0)
                    };
                }
                _device.UpdateBuffer<Vertex>(_vertexBuffer, deformed);
            }
        }
        if (_scene.Geometry == ShaderPreviewGeometry.Particles)
        {
            Span<Vertex> particles = stackalloc Vertex[ParticleCount * 4];
            UpdateParticles(particles, view, time);
            _device.UpdateBuffer<Vertex>(_vertexBuffer, particles);
        }
        if (_lighting is { } lighting && (_lightingCamera != renderCamera ||
            (_scene.ImageEffect is ImageEffectPass.SunShafts or ImageEffectPass.SkinDeformation && _lightingTime != time) ||
            (MotionPreview.UsesUnityMotionTexture(_scene.ImageEffect) || motionBlur) && _lightingMotion != motion ||
            _lightingJitter != jitter))
        {
            lighting.RenderInputs(view, Projection, eye, time, motion, token, jitter);
            UploadCameraTextures(lighting.Textures);
            if (_scene.ImageEffect == ImageEffectPass.ExposureHistogram)
            {
                UpdateExposureHistogram(lighting.Textures[ImageEffectPreview.MainTexture], token);
            }
            _lightingCamera = renderCamera;
            _lightingTime = time;
            _lightingMotion = motion;
            _lightingJitter = jitter;
        }
        if (temporal && (!_historyReady || resetMotionHistory || time < _lastMotionTime))
        {
            _device.ClearBindings();
            var history = GetRenderTarget(TemporalHistory[_historyReadIndex]).Texture;
            _device.CopyColor(_textures[ImageEffectPreview.MainTexture].Image, history.Image, 10, multisampled: false);
        }
        if (_animatedEffect is { } effect)
        {
            effect.RenderInputs(camera, time, token);
            UploadCameraTextures(effect.Textures);
        }
        if (_scene.DepthBlitSamples != 0)
        {
            _device.ClearDepth(_depth, 0f);
        }
        else
        {
            _device.ClearFrame(_targetView, _depth, FrameSize, _showBackgroundPattern, background: _animatedEffect == null ? 0.18f : 0f);
        }
        var capturedNames = new HashSet<string>(StringComparer.Ordinal);
        var renderedTargets = new HashSet<ShaderPreviewRenderTarget>();
        bool pointsCollected = false;
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
                if (draw.Output is { } output)
                {
                    renderedTargets.Add(output);
                }
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
        var pixels = _scene.DepthBlitSamples != 0 ? ReadDepthImage(renderCamera.Distance) :
            _device.ReadPixels(_resolved, _staging, Rgba8, FrameSize);
        // The preview is already composited over its scene background.
        for (int alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            pixels[alpha] = 255;
        }
        token.ThrowIfCancellationRequested();
        if (motion.HasValue)
        {
            _lastMotionCamera = camera;
            _lastMotionTime = time;
        }
        if (temporal)
        {
            _historyReadIndex ^= 1;
            _historyReady = true;
        }
        return new ShaderPreviewFrame(FrameSize, FrameSize, pixels);

        void DrawPass(ShaderPreviewDraw draw)
        {
            if (temporal)
            {
                draw = draw with { Values = new Dictionary<string, NumericsVector4> { ["_Jitter"] = new(jitter, 0, 0) } };
            }
            LegacyShaderPreviewPass pass = draw.Pass;
            NumericsMatrix4x4? billboardTransform = null;
            NumericsVector4 billboardColor = default;
            if (draw.Billboard is { } billboard)
            {
                if (!billboard.Project(view * Projection, time, out var transform, out billboardColor))
                {
                    return;
                }
                billboardTransform = transform;
            }
            if (draw.PointPass == ShaderPreviewPointPass.Draw && !pointsCollected)
            {
                throw new InvalidDataException("Bokeh points must be collected before drawing them.");
            }
            if (!_passes.TryGetValue(pass, out var gpu))
            {
                ValidateStage(pass.Vertex, ShaderPreviewProgramKind.Vertex, draw);
                ValidateStage(pass.Fragment, ShaderPreviewProgramKind.Pixel, draw);
                nint geometry = 0;
                if (pass.Geometry is { } stage)
                {
                    if (draw.PointPass != ShaderPreviewPointPass.Draw)
                    {
                        throw new NotSupportedException("The geometry stage requires a supported input topology.");
                    }
                    ValidateStage(stage, ShaderPreviewProgramKind.Geometry, draw);
                    geometry = _device.CreateShader(stage.Bytecode, ShaderPreviewProgramKind.Geometry);
                }
                bool usesSecondaryUv = (draw.Geometry ?? _scene.Geometry) == ShaderPreviewGeometry.Text ||
                    _scene.SerializedShader && _scene.LightingPass == BuiltInLightingPass.CollectShadows;
                gpu = (CreateInputLayout(_device, pass.Vertex.Bytecode, usesSecondaryUv),
                    _device.CreateShader(pass.Vertex.Bytecode, ShaderPreviewProgramKind.Vertex),
                    _device.CreateShader(pass.Fragment.Bytecode, ShaderPreviewProgramKind.Pixel), geometry, CreateBlend(_device, pass.State),
                    CreateDepth(_device, pass.State), _device.CreateRasterizerState(new RasterizerDesc
                    {
                        FillMode = 3, CullMode = (int)pass.State.Cull, FrontCounterClockwise = 1,
                        DepthClipEnable = pass.State.ZClip ? 1 : 0, DepthBias = pass.State.OffsetUnits, SlopeScaledDepthBias = pass.State.OffsetFactor,
                        MultisampleEnable = _multisampled || _scene.DepthBlitSamples > 1 ? 1 : 0
                    }), FindUnusedVertexTransform(pass));
                _passes.Add(pass, gpu);
            }
            if (_scene.DepthBlitSamples != 0)
            {
                // This blit's vertex stage transforms positions with the supplied view-projection matrix.
                // Use it to rasterize the preview mesh into the camera's reversed-Z depth texture first.
                _device.ClearBindings();
                _device.ClearDepth(_depthInputView, 0f);
                _device.SetPass(0, _depthInputView, 0, _depthInputState, 0, gpu.Rasterizer, FrameSize);
                var capture = draw with { Geometry = ShaderPreviewGeometry.Sphere };
                int inputCount = BindDrawGeometry(capture, gpu.Layout, default);
                _device.SetShader(gpu.Vertex, ShaderPreviewProgramKind.Vertex);
                BindStage(pass.Vertex, capture, FrameSize, ShaderPreviewProgramKind.Vertex, null, gpu.UnusedVertexParameter,
                    ReversedProjection);
                _device.Draw(inputCount);
            }
            _device.ClearBindings();
            nint targetView = _targetView;
            int size = FrameSize;
            if (draw.Output is { } output)
            {
                targetView = GetRenderTarget(output).View;
                size = output.Size;
                _device.ClearColor(targetView);
            }
            nint depthView = draw.Output == null ? _depth : 0;
            nint secondaryView = temporal ? GetRenderTarget(TemporalHistory[_historyReadIndex ^ 1]).View : 0;
            _device.SetPass(targetView, depthView, gpu.Blend, gpu.Depth,
                pass.State.Stencil?.Reference ?? 0, gpu.Rasterizer, size, secondaryView);
            if (draw.PointPass == ShaderPreviewPointPass.Collect)
            {
                _device.SetAppendTarget(targetView, depthView, _bokehPoints.AppendView);
            }
            int indexCount = BindDrawGeometry(draw, gpu.Layout, billboardColor);
            _device.SetShader(gpu.Vertex, ShaderPreviewProgramKind.Vertex);
            _device.SetShader(gpu.Pixel, ShaderPreviewProgramKind.Pixel);
            BindStage(pass.Vertex, draw, size, ShaderPreviewProgramKind.Vertex, billboardTransform, gpu.UnusedVertexParameter);
            BindStage(pass.Fragment, draw, size, ShaderPreviewProgramKind.Pixel, billboardTransform);
            if (draw.PointPass == ShaderPreviewPointPass.Draw)
            {
                var geometry = pass.Geometry ?? throw new InvalidDataException("The bokeh draw has no geometry shader.");
                _device.SetShader(gpu.Geometry, ShaderPreviewProgramKind.Geometry);
                BindStage(geometry, draw, size, ShaderPreviewProgramKind.Geometry, billboardTransform);
                _device.DrawPoints(_bokehPoints.DrawArguments, _bokehPoints.AppendView);
            }
            else if (draw.SkyboxSide >= 0)
            {
                _device.Draw(6, draw.SkyboxSide * 6);
            }
            else
            {
                _device.Draw(indexCount);
            }
            if (draw.PointPass == ShaderPreviewPointPass.Collect)
            {
                pointsCollected = true;
            }
        }

        void BindStage(ShaderPreviewStage stage, ShaderPreviewDraw draw, int size, ShaderPreviewProgramKind kind,
            NumericsMatrix4x4? billboardTransform, ShaderPreviewParameter? unusedVertexParameter = null,
            NumericsMatrix4x4? projection = null)
        {
            var buffers = new HashSet<int>();
            foreach (var buffer in stage.Buffers)
            {
                token.ThrowIfCancellationRequested();
                if (!buffers.Add(buffer.Slot))
                {
                    throw new InvalidDataException($"Duplicate constant buffer binding b{buffer.Slot}.");
                }
                var data = ShaderPreviewService.BindConstants(_scene, draw, buffer, view, projection ?? Projection, eye, size, capturedNames,
                    time, deltaTime, _lighting, _cameraTextures, motion, billboardTransform, unusedVertexParameter);
                if (_constantBuffers.TryGetValue(buffer, out nint gpuBuffer))
                {
                    _device.UpdateBuffer<byte>(gpuBuffer, data);
                }
                else
                {
                    gpuBuffer = _device.CreateBuffer<byte>(data, BindConstantBuffer, mutable: true);
                    _constantBuffers.Add(buffer, gpuBuffer);
                }
                _device.SetConstantBuffer(gpuBuffer, buffer.Slot, kind);
            }
            var slots = new HashSet<int>();
            foreach (var binding in stage.StructuredBuffers)
            {
                nint view = binding.Name switch
                {
                    "pointBuffer" when draw.PointPass == ShaderPreviewPointPass.Draw && kind == ShaderPreviewProgramKind.Vertex => _bokehPoints.ShaderView,
                    ExposureHistogramPreview.BufferName when _scene.ImageEffect == ImageEffectPass.ExposureHistogram => _exposureHistogramView,
                    _ => 0
                };
                if (view == 0 || binding.Slot != 0 || !slots.Add(binding.Slot))
                {
                    throw new NotSupportedException($"Unsupported structured buffer '{binding.Name}'.");
                }
                _device.SetTexture(view, binding.Slot, kind);
            }
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
                if (draw.Inputs?.TryGetValue(binding.Name, out var input) == true)
                {
                    if (input == draw.Output || !renderedTargets.Contains(input))
                    {
                        throw new InvalidDataException($"Render target '{input.Name}' is unavailable before this pass.");
                    }
                    gpu = GetRenderTarget(input).Texture;
                    sampling = (input.Filter, 1, 1, 0, false);
                }
                else if (capturedNames.Contains(binding.Name) && _grabs.TryGetValue(binding.Name, out var grab))
                {
                    gpu = new GpuTexture(grab.Texture, grab.View, _grabSampler);
                    sampling = (1, 1, 1, 0, false);
                }
                else if (temporal && binding.Name == TemporalAntialiasingPreview.HistoryTexture)
                {
                    gpu = GetRenderTarget(TemporalHistory[_historyReadIndex]).Texture;
                    sampling = (1, 1, 1, 0, false);
                }
                else if (IsDepthBlitInput(binding.Name))
                {
                    gpu = _depthInput;
                    sampling = (0, 1, 1, 0, false);
                }
                else
                {
                    string name = draw.TextureSource(binding.Name);
                    var resources = _scene.CameraTextures.Contains(name) || ImageEffectPreview.ProvidesTexture(_scene.ImageEffect, name)
                        ? _cameraTextures! : _scene.Textures;
                    if (!resources.TryGetValue(name, out var texture))
                    {
                        throw new InvalidDataException($"Texture '{binding.Name}' is unavailable before this pass.");
                    }
                    sampling = (texture.Filter, texture.Wrap, texture.Anisotropy, texture.MipBias, texture.Comparison);
                    if (!_textures.TryGetValue(name, out gpu))
                    {
                        gpu = UploadTexture(_device, texture, name);
                        _textures.Add(name, gpu);
                    }
                }
                _device.SetTexture(gpu.View, binding.Slot, kind);
                if (binding.SamplerSlot >= 0)
                {
                    if (samplers.TryGetValue(binding.SamplerSlot, out var shared) && shared != sampling)
                    {
                        throw new NotSupportedException($"Conflicting sampler bindings for '{binding.Name}'.");
                    }
                    samplers[binding.SamplerSlot] = sampling;
                    _device.SetSampler(gpu.Sampler, binding.SamplerSlot, kind);
                }
            }
            foreach (var binding in stage.InlineSamplers)
            {
                if (!samplers.TryAdd(binding.Slot, (binding.Filter, binding.Wrap, 1, 0, false)))
                {
                    throw new InvalidDataException($"Conflicting inline sampler at s{binding.Slot}.");
                }
                var settings = (binding.Filter, binding.Wrap);
                if (!_inlineSamplers.TryGetValue(settings, out nint sampler))
                {
                    sampler = CreateSampler(_device, binding.Filter, binding.Wrap, anisotropy: 1, bias: 0);
                    _inlineSamplers.Add(settings, sampler);
                }
                _device.SetSampler(sampler, binding.Slot, kind);
            }
        }
    }

    public void Dispose() => _device.Dispose();

    private bool IsDepthBlitInput(string name) => _scene.DepthBlitSamples != 0 && name == ImageEffectPreview.MainTexture;

    private byte[] ReadDepthImage(float cameraDistance)
    {
        var pixels = _device.ReadPixels(_depthTexture, _staging, R32Float, FrameSize);
        var depths = MemoryMarshal.Cast<byte, float>(pixels);
        var parameters = BuiltInLightingPreview.DepthParameters(reversedDepth: true);
        const float near = ShaderPreviewCamera.Near;
        float range = Math.Clamp(cameraDistance * 2, near + 1, ShaderPreviewCamera.Far) - near;
        for (int i = 0; i < depths.Length; i++)
        {
            float eyeDepth = 1 / (parameters.Z * depths[i] + parameters.W);
            float linearDepth = Math.Clamp((eyeDepth - near) / range, 0, 1);
            byte shade = (byte)MathF.Round(255 * (1 - linearDepth));
            int offset = i * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
        }
        return pixels;
    }

    private (Vertex[] Vertices, uint[] Indices) GetMesh(ShaderPreviewGeometry geometry) => geometry switch
    {
        ShaderPreviewGeometry.Particles => (new Vertex[ParticleCount * 4], ParticleIndices),
        ShaderPreviewGeometry.Plane => Plane,
        ShaderPreviewGeometry.Text => TextPlane,
        ShaderPreviewGeometry.ScreenQuad => _scene.ImageEffect == ImageEffectPass.GlobalFog ? FogQuad : ScreenQuad,
        ShaderPreviewGeometry.Skybox or ShaderPreviewGeometry.Volume => Skybox,
        ShaderPreviewGeometry.Sphere => Sphere,
        _ => throw new ArgumentOutOfRangeException(nameof(geometry))
    };

    private int BindDrawGeometry(ShaderPreviewDraw draw, nint layout, NumericsVector4 billboardColor)
    {
        var mesh = (Vertices: _vertexBuffer, Indices: _indexBuffer, Count: _indexCount);
        if (draw.Billboard.HasValue)
        {
            Span<Vertex> vertices = stackalloc Vertex[4];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = ScreenQuad.Vertices[i] with { Color = billboardColor };
            }
            if (_billboardVertices == 0)
            {
                _billboardVertices = _device.CreateBuffer<Vertex>(vertices, BindVertexBuffer, mutable: true);
            }
            else
            {
                _device.UpdateBuffer<Vertex>(_billboardVertices, vertices);
            }
            mesh.Vertices = _billboardVertices;
        }
        else
        {
            var key = (Geometry: draw.Geometry ?? _scene.Geometry, Color: draw.VertexColor);
            if (key.Geometry != _scene.Geometry || key.Color.HasValue)
            {
                if (!_drawMeshes.TryGetValue(key, out mesh))
                {
                    var (vertices, indices) = GetMesh(key.Geometry);
                    if (key.Color is { } color)
                    {
                        vertices = Array.ConvertAll(vertices, vertex => vertex with { Color = color });
                    }
                    mesh = (_device.CreateBuffer<Vertex>(vertices, BindVertexBuffer),
                        _device.CreateBuffer<uint>(indices, BindIndexBuffer), indices.Length);
                    _drawMeshes.Add(key, mesh);
                }
            }
        }
        _device.SetGeometry(layout, mesh.Vertices, mesh.Indices, (uint)sizeof(Vertex));
        return mesh.Count;
    }

    private static (NumericsMatrix4x4 View, NumericsVector3 Eye) CameraView(ShaderPreviewCamera camera, bool skybox)
    {
        var eye = new NumericsVector3(MathF.Sin(camera.Yaw) * MathF.Cos(camera.Pitch), MathF.Sin(camera.Pitch),
            MathF.Cos(camera.Yaw) * MathF.Cos(camera.Pitch)) * camera.Distance;
        var view = NumericsMatrix4x4.CreateLookAt(eye, NumericsVector3.Zero, NumericsVector3.UnitY);
        if (skybox)
        {
            view.Translation = NumericsVector3.Zero;
        }
        else
        {
            var pan = new NumericsVector3(camera.Pan, 0);
            eye -= NumericsVector3.TransformNormal(pan, NumericsMatrix4x4.Transpose(view));
            view.Translation += pan;
        }
        return (view, eye);
    }

    private static bool UsesVertexNormal(byte[] bytecode)
    {
        var signature = FindChunk(bytecode, "ISGN"u8);
        int count = ReadSignatureCount(signature);
        for (int i = 0; i < count; i++)
        {
            if (ReadSemantic(signature, 8 + i * 24).Equals("NORMAL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static ShaderPreviewParameter? FindUnusedVertexTransform(LegacyShaderPreviewPass pass)
    {
        if (pass.Geometry != null || pass.Fragment.Textures.Length != 0 || pass.Fragment.Buffers.Length != 0 ||
            pass.State is not { Blend: true, Source: ShaderPreviewBlend.Zero, Destination: ShaderPreviewBlend.One })
        {
            return null;
        }

        foreach (var buffer in pass.Vertex.Buffers)
        {
            if (buffer.Parameters.Length != 1 ||
                buffer.Parameters[0] is not { Type: 0, IsMatrix: false, Rows: 1, Columns: 4, ArraySize: 1 } parameter ||
                parameter.Offset < 0 || parameter.Offset > buffer.Size - 16 || parameter.Offset % 16 != 0 ||
                !parameter.Name.EndsWith("_ST", StringComparison.Ordinal))
            {
                continue;
            }

            var inputs = FindChunk(pass.Fragment.Bytecode, "ISGN"u8);
            int inputCount = ReadSignatureCount(inputs);
            for (int i = 0; i < inputCount; i++)
            {
                if (inputs[8 + i * 24 + 21] != 0)
                {
                    return null;
                }
            }

            var outputs = FindChunk(pass.Vertex.Bytecode, "OSGN"u8);
            int outputCount = ReadSignatureCount(outputs);
            var program = FindChunk(pass.Vertex.Bytecode, "SHDR"u8, "SHEX"u8);
            for (int i = 0; i < outputCount; i++)
            {
                int start = 8 + i * 24;
                if (ReadSemantic(outputs, start).Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) &&
                    ReadUInt(outputs, start + 8) == 0 &&
                    ReadsOnlyForUv(program, (uint)buffer.Slot, (uint)(parameter.Offset / 16), ReadUInt(outputs, start + 16)))
                {
                    return parameter;
                }
            }
        }
        return null;
    }

    // A vertex-only UV transform is unnecessary when the pixel shader consumes no varyings.
    // Prove that its constant flows directly to TEXCOORD, using only straight-line SM4 arithmetic.
    private static bool ReadsOnlyForUv(ReadOnlySpan<byte> program, uint slot, uint register, uint uvOutput)
    {
        const uint output = 2, immediateValue = 4, constantBuffer = 8;
        const uint add = 0, mad = 50, move = 54, multiply = 56, ret = 62;
        bool found = false;
        int offset = 8;
        while (offset < program.Length)
        {
            uint token = ReadUInt(program, offset);
            uint opcode = token & 0x7ff;
            int bytes = (int)((token >> 24) & 0x7f) * 4;
            if (bytes < 4 || bytes > program.Length - offset || (token & 0x80000000u) != 0)
            {
                return false;
            }
            var instruction = program.Slice(offset + 4, bytes - 4);
            offset += bytes;
            if (opcode is 89 or 95 or 101 or 103 or 104) // Constant buffer, input, output, system output, temps.
            {
                continue;
            }
            if (opcode == ret)
            {
                return found && instruction.IsEmpty && offset == program.Length;
            }
            int operands = opcode switch { add or multiply => 3, mad => 4, move => 2, _ => 0 };
            if (operands == 0)
            {
                return false;
            }
            bool readsTransform = false, writesUv = false;
            for (int operandIndex = 0; operandIndex < operands; operandIndex++)
            {
                if (instruction.Length < sizeof(uint))
                {
                    return false;
                }
                uint operand = ReadUInt(instruction, 0);
                uint type = (operand >> 12) & 0xff;
                int dimensions = (int)((operand >> 20) & 3);
                if ((operand & 0xffc00000u) != 0 || (operandIndex > 0 && type == output))
                {
                    return false;
                }
                int words = type switch
                {
                    0 or 1 or output when dimensions == 1 => 2,
                    immediateValue when dimensions == 0 && (operand & 3) == 1 => 2,
                    immediateValue when dimensions == 0 && (operand & 3) == 2 => 5,
                    constantBuffer when dimensions == 2 => 3,
                    _ => 0
                };
                if (words == 0 || instruction.Length < words * sizeof(uint))
                {
                    return false;
                }
                writesUv |= operandIndex == 0 && type == output && ReadUInt(instruction, 4) == uvOutput;
                readsTransform |= type == constantBuffer && ReadUInt(instruction, 4) == slot && ReadUInt(instruction, 8) == register;
                instruction = instruction[(words * sizeof(uint))..];
            }
            if (!instruction.IsEmpty || (readsTransform && (opcode != mad || !writesUv)))
            {
                return false;
            }
            found |= readsTransform;
        }
        return false;
    }

    private (GpuTexture Texture, nint View) GetRenderTarget(ShaderPreviewRenderTarget target)
    {
        if (!_renderTargets.TryGetValue(target, out var resource))
        {
            nint image = _device.CreateTexture(new TextureDesc
            {
                Width = (uint)target.Size, Height = (uint)target.Size, MipLevels = 1, ArraySize = 1,
                Format = 10, // R16G16B16A16_FLOAT, to retain signed focus values and HDR color.
                SampleCount = 1, BindFlags = BindShaderResource | BindRenderTarget
            });
            var texture = new GpuTexture(image, _device.CreateShaderView(image), CreateSampler(_device, target.Filter, 1, 1, 0));
            resource = (texture, _device.CreateRenderView(image));
            _renderTargets.Add(target, resource);
        }
        return resource;
    }

    private void UploadCameraTextures(IReadOnlyDictionary<string, ShaderPreviewTexture> textures)
    {
        _device.ClearBindings();
        foreach (var (name, texture) in textures)
        {
            if (!_textures.TryGetValue(name, out var gpu))
            {
                _textures.Add(name, UploadTexture(_device, texture, name, mutable: !texture.Comparison));
            }
            else if (!texture.Comparison)
            {
                _device.UpdateTexture2D(gpu.Image, texture.Pixels, checked((uint)(texture.Width * texture.BytesPerBlock)));
            }
        }
    }

    private void UpdateExposureHistogram(ShaderPreviewTexture source, CancellationToken token)
    {
        Span<uint> bins = stackalloc uint[ExposureHistogramPreview.BinCount];
        ExposureHistogramPreview.Count(source, bins, token);
        if (_exposureHistogram == 0)
        {
            _exposureHistogram = _device.CreateBuffer<uint>(bins, BindShaderResource, mutable: true, structured: true);
            _exposureHistogramView = _device.CreateStructuredView(_exposureHistogram, ExposureHistogramPreview.BinCount);
        }
        else
        {
            _device.UpdateBuffer<uint>(_exposureHistogram, bins);
        }
    }

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
                int slicePitch = checked(rows * pitch);
                int length = checked(slicePitch * Math.Max(1, texture.VolumeDepth >> mip));
                if (consumed > texture.Pixels.Length - length)
                {
                    throw new InvalidDataException($"Incomplete mip data for texture '{name}'.");
                }
                levels[index] = new SubresourceData { Data = data + consumed, RowPitch = (uint)pitch, SlicePitch = (uint)slicePitch };
                consumed += length;
            }
            if (consumed != texture.Pixels.Length)
            {
                throw new InvalidDataException($"Unexpected data after the mips for texture '{name}'.");
            }
            uint usage = mutable ? 0u : 1u;
            nint image = texture.VolumeDepth > 0
                ? device.CreateTexture3D(new Texture3DDesc
                {
                    Width = (uint)texture.Width, Height = (uint)texture.Height, Depth = (uint)texture.VolumeDepth,
                    MipLevels = (uint)texture.MipCount, Format = texture.Format, Usage = usage, BindFlags = BindShaderResource
                }, levels)
                : device.CreateTexture(new TextureDesc
                {
                    Width = (uint)texture.Width, Height = (uint)texture.Height, MipLevels = (uint)texture.MipCount,
                    ArraySize = (uint)texture.Faces, Format = texture.Format, SampleCount = 1, Usage = usage, BindFlags = BindShaderResource,
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

    private void ValidateStage(ShaderPreviewStage stage, ShaderPreviewProgramKind kind, ShaderPreviewDraw draw)
    {
        byte[] bytecode = stage.Bytecode;
        ReadOnlySpan<byte> program = FindChunk(bytecode, "SHDR"u8, "SHEX"u8);
        uint version = ReadUInt(program, 0);
        if (version >> 16 != (uint)kind || ((version >> 4) & 15) is not (4 or 5) ||
            program.Length % 4 != 0 || ReadUInt(program, 4) != program.Length / 4)
        {
            throw new InvalidDataException("DXBC contains an unexpected Direct3D 11 stage or program length.");
        }
        ValidateResources(program, stage, kind, draw);
        if (kind == ShaderPreviewProgramKind.Pixel)
        {
            var signature = FindChunk(bytecode, "OSGN"u8);
            int outputs = ReadSignatureCount(signature);
            bool historyOutput = false, depthOutput = false;
            for (int i = 0; i < outputs; i++)
            {
                int start = 8 + i * 24;
                string semantic = ReadSemantic(signature, start);
                uint index = ReadUInt(signature, start + 4);
                bool color = semantic.Equals("SV_Target", StringComparison.OrdinalIgnoreCase);
                if (color && index == 1 && _scene.ImageEffect == ImageEffectPass.TemporalAntialiasing)
                {
                    historyOutput = true;
                }
                else if (semantic.Equals("SV_Depth", StringComparison.OrdinalIgnoreCase))
                {
                    depthOutput = true;
                }
                else if (!(color && index == 0))
                {
                    throw new NotSupportedException($"Unsupported DXBC pixel output '{semantic}{index}'.");
                }
            }
            if (_scene.ImageEffect == ImageEffectPass.TemporalAntialiasing && !historyOutput)
            {
                throw new NotSupportedException("The temporal pass has no history render target output.");
            }
            if (_scene.DepthBlitSamples != 0 && !depthOutput)
            {
                throw new NotSupportedException("The depth blit pass has no SV_Depth output.");
            }
        }
    }

    private void ValidateResources(ReadOnlySpan<byte> program, ShaderPreviewStage stage, ShaderPreviewProgramKind kind, ShaderPreviewDraw draw)
    {
        // Unity can strip RDEF. Compare the actual SM4/5 resource declarations with Unity's metadata.
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
                uint samples = (token >> 16) & 0x7f;
                bool bound = opcode switch
                {
                    declareTexture => Array.Exists(stage.Textures, texture => texture.Slot == slot && flags == (texture.Dimension switch
                    {
                        ShaderPreviewTextureDimension.Texture2D when IsDepthBlitInput(texture.Name) && _scene.DepthBlitSamples > 1 =>
                            kind == ShaderPreviewProgramKind.Pixel && texture.SamplerSlot < 0 &&
                            (samples == 0 || samples == _scene.DepthBlitSamples) ? 4u : 0u,
                        ShaderPreviewTextureDimension.Texture2D => 3u,
                        ShaderPreviewTextureDimension.Texture3D => 5u,
                        ShaderPreviewTextureDimension.Cube => 6u,
                        _ => throw new InvalidDataException($"Unsupported DXBC texture dimension '{texture.Dimension}'.")
                    })),
                    declareSampler => Array.Exists(stage.Textures, texture => texture.SamplerSlot == slot && flags ==
                        (_lighting?.Textures.TryGetValue(texture.Name, out var lightingTexture) == true && lightingTexture.Comparison ? 1u : 0u))
                        || flags == 0 && Array.Exists(stage.InlineSamplers, sampler => sampler.Slot == slot),
                    _ => ReadUInt(program, offset + 12) is var count && count is > 0 and <= 4096 &&
                         Array.Exists(stage.Buffers, buffer => buffer.Slot == slot && ((buffer.Size + 15) & ~15) >= count * 16)
                };
                if (!bound)
                {
                    if (opcode == declareTexture && flags is 4 or 9)
                    {
                        throw new NotSupportedException($"Direct3D 11 texture t{slot} requires a compatible multisampled scene input.");
                    }
                    throw new NotSupportedException($"The DXBC resource at register {slot} is incompatible or has no Unity binding.");
                }
            }
            else if (opcode is 158 or 162)
            {
                bool supported = false;
                if (words == 4 && (token & 0x80000000) == 0)
                {
                    uint stride = ReadUInt(program, offset + 12), slot = ReadUInt(program, offset + 8);
                    supported = opcode switch
                    {
                        158 => stride == BokehPointStride && slot == 1 && draw.PointPass == ShaderPreviewPointPass.Collect &&
                            kind == ShaderPreviewProgramKind.Pixel,
                        162 when _scene.ImageEffect == ImageEffectPass.ExposureHistogram => stride == sizeof(uint) && slot == 0 &&
                            Array.Exists(stage.StructuredBuffers, item => item is { Name: ExposureHistogramPreview.BufferName, Slot: 0 }),
                        _ => stride == BokehPointStride && slot == 0 && draw.PointPass == ShaderPreviewPointPass.Draw &&
                            kind == ShaderPreviewProgramKind.Vertex && Array.Exists(stage.StructuredBuffers, item => item is { Name: "pointBuffer", Slot: 0 })
                    };
                }
                if (!supported)
                {
                    throw new NotSupportedException("The structured buffer has no matching resource in the preview pass.");
                }
            }
            else if (opcode is 156 or 157 or 161)
            {
                throw new NotSupportedException("Shader preview does not support DXBC UAV, raw, or structured resources.");
            }
            offset += (int)words * 4;
        }
    }

    private static nint CreateInputLayout(D3D11PreviewDevice device, byte[] bytecode, bool usesSecondaryUv)
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
                "POSITION" => 0, "NORMAL" => 16, "TANGENT" => 32, "COLOR" => 48,
                "TEXCOORD" => usesSecondaryUv && semanticIndex == 1 ? 80u : 64u,
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
        NumericsVector4 Color, NumericsVector4 Uv, NumericsVector4 Uv1 = default);

    private static (Vertex[], uint[]) BuildPlane() =>
        ([PlaneVertex(-1, -1, 0, 0), PlaneVertex(1, -1, 1, 0), PlaneVertex(1, 1, 1, 1), PlaneVertex(-1, 1, 0, 1)],
         [0, 1, 2, 0, 2, 3]);

    private static (Vertex[], uint[]) BuildTextPlane() => (Array.ConvertAll(Plane.Vertices, vertex => vertex with
    {
        // TextMeshPro's second UV includes packed face UVs and the signed glyph scale.
        Uv1 = new NumericsVector4(512 * (4096 * vertex.Uv.X + vertex.Uv.Y), 1, 0, 0)
    }), Plane.Indices);

    private static (Vertex[], uint[]) BuildScreenQuad(bool includeCornerIndices = false)
    {
        const float far = ShaderPreviewCamera.Far;
        var vertices = Array.ConvertAll(Plane.Vertices, vertex =>
        {
            // Unity stores the perspective ray in NORMAL on its internal full-screen quad.
            var ray = new NumericsVector4(vertex.Position.X * far / Projection.M11, vertex.Position.Y * far / Projection.M22, far, 0);
            return vertex with { Normal = ray, Uv1 = ray, Uv = vertex.Uv with { Y = 1 - vertex.Uv.Y } };
        });
        if (includeCornerIndices)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = vertices[i] with { Position = vertices[i].Position with { Z = 3 - i } };
            }
        }
        return (vertices, Plane.Indices);
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
