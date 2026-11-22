using System;
using System.Collections.Generic;
using System.IO;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio
{
    internal static class SerializedShaderPreviewState
    {
        internal static ShaderPreviewState Read(SerializedShaderState state, IReadOnlyDictionary<string, NumericsVector4> values)
        {
            if (state.rtSeparateBlend || state.fogMode is not (FogMode.Unknown or FogMode.Disabled))
            {
                throw new NotSupportedException("The pass requires independent render-target blending or fixed-function fog.");
            }

            var blend = state.rtBlend[0];
            var source = Blend(blend.srcBlend, "Blend source");
            var destination = Blend(blend.destBlend, "Blend destination");
            var sourceAlpha = Blend(blend.srcBlendAlpha, "Blend source alpha");
            var destinationAlpha = Blend(blend.destBlendAlpha, "Blend destination alpha");
            var operation = (ShaderPreviewBlendOp)Integer(blend.blendOp, "BlendOp", 0, 4);
            var alphaOperation = (ShaderPreviewBlendOp)Integer(blend.blendOpAlpha, "BlendOp alpha", 0, 4);
            int colorMask = Integer(blend.colMask, "ColorMask", 0, 15);
            var front = ReadFace(state.stencilOpFront);
            var back = ReadFace(state.stencilOpBack);
            var reference = (byte)Integer(state.stencilRef, "Stencil Ref", 0, 255);
            var readMask = (byte)Integer(state.stencilReadMask, "Stencil ReadMask", 0, 255);
            var writeMask = (byte)Integer(state.stencilWriteMask, "Stencil WriteMask", 0, 255);
            var stencil = front == DefaultStencil && back == DefaultStencil ? null : new ShaderPreviewStencil(reference, readMask, writeMask, front, back);

            return new ShaderPreviewState
            {
                Cull = (ShaderPreviewCull)(1 + Integer(state.culling, "Cull", 0, 2)),
                ZTest = Depth(state.zTest, "ZTest"),
                ZWrite = Integer(state.zWrite, "ZWrite", 0, 1) == 1,
                ZClip = Integer(state.zClip ?? throw new InvalidDataException("The pass is missing its ZClip state."), "ZClip", 0, 1) == 1,
                Blend = source != ShaderPreviewBlend.One || destination != ShaderPreviewBlend.Zero ||
                        sourceAlpha != ShaderPreviewBlend.One || destinationAlpha != ShaderPreviewBlend.Zero ||
                        operation != ShaderPreviewBlendOp.Add || alphaOperation != ShaderPreviewBlendOp.Add,
                Source = source,
                Destination = destination,
                SourceAlpha = sourceAlpha,
                DestinationAlpha = destinationAlpha,
                Operation = operation,
                AlphaOperation = alphaOperation,
                // Unity's ColorWriteMask starts with alpha in bit 0. D3D11 starts with red.
                ColorMask = (byte)(((colorMask & 8) >> 3) | ((colorMask & 4) >> 1) | ((colorMask & 2) << 1) | ((colorMask & 1) << 3)),
                OffsetFactor = Value(state.offsetFactor, "Offset factor"),
                OffsetUnits = Integer(state.offsetUnits, "Offset units", int.MinValue, int.MaxValue),
                Stencil = stencil,
                AlphaToMask = Integer(state.alphaToMask, "AlphaToMask", 0, 1) == 1
            };

            ShaderPreviewStencilFace ReadFace(SerializedStencilOp face) => new(
                Depth(FaceValue(state.stencilOp.comp, face.comp, 8), "Stencil Comp"),
                (ShaderPreviewStencilOp)(1 + Integer(FaceValue(state.stencilOp.pass, face.pass, 0), "Stencil Pass", 0, 7)),
                (ShaderPreviewStencilOp)(1 + Integer(FaceValue(state.stencilOp.fail, face.fail, 0), "Stencil Fail", 0, 7)),
                (ShaderPreviewStencilOp)(1 + Integer(FaceValue(state.stencilOp.zFail, face.zFail, 0), "Stencil ZFail", 0, 7)));

            ShaderPreviewDepth Depth(SerializedShaderFloatValue field, string command)
            {
                int value = Integer(field, command, 0, 8);
                return value == 0 ? ShaderPreviewDepth.Off : (ShaderPreviewDepth)value;
            }

            ShaderPreviewBlend Blend(SerializedShaderFloatValue field, string command) => Integer(field, command, 0, 10) switch
            {
                0 => ShaderPreviewBlend.Zero,
                1 => ShaderPreviewBlend.One,
                2 => ShaderPreviewBlend.DstColor,
                3 => ShaderPreviewBlend.SrcColor,
                4 => ShaderPreviewBlend.OneMinusDstColor,
                5 => ShaderPreviewBlend.SrcAlpha,
                6 => ShaderPreviewBlend.OneMinusSrcColor,
                7 => ShaderPreviewBlend.DstAlpha,
                8 => ShaderPreviewBlend.OneMinusDstAlpha,
                9 => ShaderPreviewBlend.SrcAlphaSaturate,
                10 => ShaderPreviewBlend.OneMinusSrcAlpha,
                _ => throw new InvalidDataException($"Invalid ShaderLab blend factor for '{command}'.")
            };

            int Integer(SerializedShaderFloatValue field, string command, int min, int max)
            {
                double value = Value(field, command);
                if (value < min || value > max || Math.Truncate(value) != value)
                {
                    throw new InvalidDataException($"Invalid ShaderLab value {value} for '{command}'.");
                }
                return (int)value;
            }

            float Value(SerializedShaderFloatValue field, string command)
            {
                float value = field.val;
                if (HasProperty(field))
                {
                    if (!values.TryGetValue(field.name, out var property))
                    {
                        throw new InvalidDataException($"ShaderLab state '{command}' requires material property '{field.name}'.");
                    }
                    value = property.X;
                }
                return float.IsFinite(value) ? value : throw new InvalidDataException($"Non-finite ShaderLab value for '{command}'.");
            }
        }

        private static readonly ShaderPreviewStencilFace DefaultStencil = new(
            ShaderPreviewDepth.Always, ShaderPreviewStencilOp.Keep, ShaderPreviewStencilOp.Keep, ShaderPreviewStencilOp.Keep);

        private static bool HasProperty(SerializedShaderFloatValue field) => field.name is not ("" or "<noninit>");

        private static SerializedShaderFloatValue FaceValue(SerializedShaderFloatValue all, SerializedShaderFloatValue face, float implicitValue) =>
            HasProperty(face) || face.val != implicitValue ? face : all;
    }
}
