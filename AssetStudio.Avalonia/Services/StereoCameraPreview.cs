using System;
using System.Collections.Generic;
using System.Threading;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal enum ShaderPreviewStereoLayout { TextureArray, DoubleWide }

internal sealed class StereoCameraPreview
{
    internal const string ReadArraySlice = "Hidden/VR/BlitTexArraySlice";
    internal const string WriteArraySlice = "Hidden/VR/BlitFromTex2DToTexArraySlice";
    internal const string VideoComposite = "Hidden/VideoComposite";
    private const string SliceIndex = "_ArraySliceIndex";
    private const int EyeCount = 2;
    private const float EyeSeparation = 0.064f;
    private const string Source = ImageEffectPreview.MainTexture;
    private readonly BuiltInLightingPreview _camera;
    private readonly ShaderPreviewTexture _eyeTexture;
    private readonly ShaderPreviewTexture _sourceTexture;

    internal StereoCameraPreview(ShaderPreviewStereoLayout layout, ShaderPreviewLight light, int frameSize,
        bool reversedDepth, NumericsVector3 ambientLinear)
    {
        _camera = new BuiltInLightingPreview(new HashSet<string> { Source }, light, frameSize / EyeCount, effect: null,
            reversedDepth, ambientLinear);
        _eyeTexture = _camera.Textures[Source];
        bool array = layout == ShaderPreviewStereoLayout.TextureArray;
        _sourceTexture = _eyeTexture with
        {
            Width = array ? _eyeTexture.Width : _eyeTexture.Width * EyeCount,
            ArraySize = array ? EyeCount : 1,
            Dimension = array ? ShaderPreviewTextureDimension.Texture2DArray : ShaderPreviewTextureDimension.Texture2D,
            Pixels = new byte[_eyeTexture.Pixels.Length * EyeCount]
        };
        Textures = new Dictionary<string, ShaderPreviewTexture> { [Source] = _sourceTexture };
    }

    internal IReadOnlyDictionary<string, ShaderPreviewTexture> Textures { get; }

    internal void RenderInputs(NumericsMatrix4x4 view, NumericsMatrix4x4 projection, NumericsVector3 eye, CancellationToken token)
    {
        var right = new NumericsVector3(view.M11, view.M21, view.M31);
        int pitch = _eyeTexture.Width * _eyeTexture.BytesPerBlock;
        for (int eyeIndex = 0; eyeIndex < EyeCount; eyeIndex++)
        {
            float offset = (eyeIndex - 0.5f) * EyeSeparation;
            var eyeView = view;
            eyeView.M41 -= offset;
            _camera.RenderInputs(eyeView, projection, eye + right * offset, time: 0, motion: null, token);

            var pixels = _eyeTexture.Pixels.AsSpan();
            if (_sourceTexture.Dimension == ShaderPreviewTextureDimension.Texture2DArray)
            {
                pixels.CopyTo(_sourceTexture.Pixels.AsSpan(eyeIndex * pixels.Length));
                continue;
            }
            for (int y = 0; y < _eyeTexture.Height; y++)
            {
                pixels.Slice(y * pitch, pitch).CopyTo(_sourceTexture.Pixels.AsSpan((y * EyeCount + eyeIndex) * pitch, pitch));
            }
        }
    }

    internal static ShaderPreviewDraw[] PrepareCopy(ShaderPreviewDraw draw, ShaderPreviewStereoLayout layout, int frameSize)
    {
        int size = frameSize / EyeCount;
        bool array = layout == ShaderPreviewStereoLayout.TextureArray;
        var eyes = new ShaderPreviewDraw[EyeCount];
        bool usesUnitQuad = !array && draw.Pass.ShaderName != VideoComposite;
        for (int i = 0; i < eyes.Length; i++)
        {
            var values = new Dictionary<string, NumericsVector4>();
            switch (draw.Pass.ShaderName)
            {
                case ReadArraySlice:
                    values.Add(SliceIndex, new(i));
                    break;
                case VideoComposite:
                    values.Add("_MainTex_ST", new(1f / EyeCount, 1, 0, 0));
                    values.Add("_RightEyeUVOffset", new(1f / EyeCount, 0, 0, 0));
                    values.Add("unity_StereoEyeIndex", new(i));
                    values.Add("_AlphaParam", NumericsVector4.One);
                    break;
                default:
                    if (array)
                    {
                        values.Add("_DepthSlice", new(i));
                    }
                    else
                    {
                        values.Add("_UVScaleOffset", new(1f / EyeCount, 1, (float)i / EyeCount, 0));
                    }
                    break;
            }
            eyes[i] = draw with
            {
                Geometry = usesUnitQuad ? ShaderPreviewGeometry.UnitQuad : ShaderPreviewGeometry.ScreenQuad,
                Viewport = new ShaderPreviewViewport(i * size, (frameSize - size) / 2, size),
                Values = values
            };
        }
        return eyes;
    }

    internal static ShaderPreviewOperation[] PrepareArrayWrite(ShaderPreviewDraw draw, int size)
    {
        // Show the image that the original blit writes to the right-eye layer.
        const int slice = 1;
        var output = new ShaderPreviewRenderTarget("Stereo color", size) { ArraySize = EyeCount, Format = D3D11PreviewDevice.Rgba8 };
        return
        [
            draw with { Output = output, Values = new Dictionary<string, NumericsVector4> { [SliceIndex] = new(slice) } },
            new ShaderPreviewPresentSlice(output, slice)
        ];
    }
}
