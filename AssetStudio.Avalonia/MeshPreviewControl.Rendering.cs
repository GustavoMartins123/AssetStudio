using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.OpenGL;
using AssetStudio.Avalonia.Services;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;

namespace AssetStudio.Avalonia
{
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;

    public partial class MeshPreviewControl
    {
        public static readonly StyledProperty<bool> IsMaterialShaderPreviewProperty =
            AvaloniaProperty.Register<MeshPreviewControl, bool>(nameof(IsMaterialShaderPreview));

        public static readonly StyledProperty<ShaderPreviewFrame?> MaterialShaderFrameProperty =
            AvaloniaProperty.Register<MeshPreviewControl, ShaderPreviewFrame?>(nameof(MaterialShaderFrame));

        private ShaderPreviewFrame? uploadedMaterialShaderFrame;
        private int materialShaderTexture, materialShaderFramebuffer;
        private bool[]? previewTextureHasAlpha;

        internal static bool HasTransparentPixels(byte[]? rgbaBytes)
        {
            if (rgbaBytes == null || rgbaBytes.Length < 4)
            {
                return false;
            }

            for (int p = 3; p < rgbaBytes.Length; p += 4)
            {
                if (rgbaBytes[p] < 250)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsSubMeshTransparent(
            int subMeshIndex,
            int[]? subMeshTextureMap,
            bool[]? textureHasAlpha,
            Vector4 tint)
        {
            if (tint.W < 0.99f)
            {
                return true;
            }

            if (textureHasAlpha == null || textureHasAlpha.Length == 0 || subMeshIndex < 0)
            {
                return false;
            }

            if (subMeshTextureMap != null)
            {
                if (subMeshIndex >= subMeshTextureMap.Length)
                {
                    return false;
                }

                int mappedIndex = subMeshTextureMap[subMeshIndex];
                return mappedIndex >= 0 && mappedIndex < textureHasAlpha.Length && textureHasAlpha[mappedIndex];
            }

            int directIndex = subMeshIndex < textureHasAlpha.Length ? subMeshIndex : 0;
            return textureHasAlpha[directIndex];
        }

        internal static void PartitionSubMeshDrawOrder(
            int subMeshCount,
            Func<int, bool> isTransparentSubMesh,
            out List<int> opaqueSubMeshes,
            out List<int> transparentSubMeshes)
        {
            opaqueSubMeshes = new List<int>(Math.Max(0, subMeshCount));
            transparentSubMeshes = new List<int>();
            for (int i = 0; i < subMeshCount; i++)
            {
                if (isTransparentSubMesh(i))
                {
                    transparentSubMeshes.Add(i);
                }
                else
                {
                    opaqueSubMeshes.Add(i);
                }
            }
        }

        public bool IsMaterialShaderPreview
        {
            get => GetValue(IsMaterialShaderPreviewProperty);
            set => SetValue(IsMaterialShaderPreviewProperty, value);
        }

        public ShaderPreviewFrame? MaterialShaderFrame
        {
            get => GetValue(MaterialShaderFrameProperty);
            set => SetValue(MaterialShaderFrameProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsMaterialShaderPreviewProperty)
            {
                if (IsMaterialShaderPreview)
                {
                    ++meshLoadCounter;
                    ClearAvatarPreviewState();
                    ClearMeshPreviewSource();
                    SetInitialPreviewFrame(Matrix4.Identity, Matrix4.Identity);
                }
                else
                {
                    StopAnimation();
                    uploadedMaterialShaderFrame = null;
                }
                InvalidatePreviewCamera();
            }
            else if (change.Property == MaterialShaderFrameProperty)
            {
                RequestNextFrameRendering();
            }
        }

        public void SetMaterialOverrides(IReadOnlyList<Vector4>? materialTints)
        {
            materialTintOverrides = materialTints == null || materialTints.Count == 0
                ? null
                : materialTints.ToArray();
            RequestNextFrameRendering();
        }

        public void ApplyMeshTextures(List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights)
        {
            ApplyMeshTextures(null, subMeshTextures, subMeshTexWidths, subMeshTexHeights, null);
        }

        public void ApplyMeshTextures(List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights, int[]? subMeshToTextureIndex)
        {
            ApplyMeshTextures(null, subMeshTextures, subMeshTexWidths, subMeshTexHeights, subMeshToTextureIndex);
        }

        public void ApplyMeshTextures(Mesh? expectedMesh, List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights)
        {
            ApplyMeshTextures(expectedMesh, subMeshTextures, subMeshTexWidths, subMeshTexHeights, null);
        }

        public void ApplyMeshTextures(Mesh? expectedMesh, List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights, int[]? subMeshToTextureIndex)
        {
            if (subMeshTextures == null || !subMeshTextures.Any(t => t != null))
            {
                return;
            }

            if (expectedMesh != null
                && !ReferenceEquals(meshPreviewSource, expectedMesh)
                && !(avatarCompositeParts?.Any(p => ReferenceEquals(p.Mesh, expectedMesh)) ?? false))
            {
                return;
            }

            if (meshGroupPreviewSources != null && meshGroupPreviewSources.Count > 0 && expectedMesh == null)
            {
                meshGroupPreviewSourceSubMeshTextures = subMeshTextures;
                meshGroupPreviewSourceSubMeshTexWidths = subMeshTexWidths;
                meshGroupPreviewSourceSubMeshTexHeights = subMeshTexHeights;
                meshGroupPreviewSourceSubMeshTextureMap = subMeshToTextureIndex;
            }
            else
            {
                meshPreviewSourceSubMeshTextures = subMeshTextures;
                meshPreviewSourceSubMeshTexWidths = subMeshTexWidths;
                meshPreviewSourceSubMeshTexHeights = subMeshTexHeights;
                meshPreviewSourceSubMeshTextureMap = subMeshToTextureIndex;
            }

            keepExistingTextures = true;
            previewMaterialMode = true;

            lock (textureLock)
            {
                pendingSubMeshTextures = subMeshTextures;
                pendingSubMeshTexWidths = subMeshTexWidths;
                pendingSubMeshTexHeights = subMeshTexHeights;
                pendingSubMeshTextureMap = subMeshToTextureIndex;
                hasPendingSubMeshTextures = true;
            }

            RequestNextFrameRendering();
        }

        public void SetMaterialTexture(SKBitmap image)
        {
            ++meshLoadCounter;
            ClearAvatarPreviewState();
            ClearMeshPreviewSource();
            materialTintOverrides = null;

            lock (textureLock)
            {
                pendingTextureWidth = image.Width;
                pendingTextureHeight = image.Height;
                pendingTextureData = new byte[image.Width * image.Height * 4];
                image.CopyBgraPixelsTo(pendingTextureData);
                for (int i = 0; i < pendingTextureData.Length; i += 4)
                {
                    byte temp = pendingTextureData[i];
                    pendingTextureData[i] = pendingTextureData[i + 2];
                    pendingTextureData[i + 2] = temp;
                }
                hasPendingTexture = true;
            }

            GenerateSphere(32, 32);
            previewMaterialMode = true;

            vao = 0;
            RequestNextFrameRendering();
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                if (IsMaterialShaderPreview)
                {
                    RenderMaterialShaderFrame(fb);
                    return;
                }
                var indices = indiceData;
                bool hasMeshGeometry = vertexData != null && vertexData.Length > 0 && indices != null && indices.Length > 0;
                bool hasSkeletonGeometry = isAvatarMode && (boneLinesVertexCount > 0 || jointPointsVertexCount > 0 || pendingSkeletonVertices != null);
                if (!hasMeshGeometry && !hasSkeletonGeometry)
                {
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    return;
                }

                bool updatedTextures = TryUploadPendingTextures();

                if (Bounds.Width != lastWidth || Bounds.Height != lastHeight)
                {
                    lastWidth = Bounds.Width;
                    lastHeight = Bounds.Height;
                    ChangeGLSize(lastWidth, lastHeight);
                }

                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

                if (vao == 0)
                {
                    if (!updatedTextures && !keepExistingTextures)
                    {
                        DeletePreviewTextures();
                    }
                    CreateVAO();
                }

                if (isAvatarMode)
                {
                    UploadPendingDynamicGeometry();
                }

                if (showMesh && hasMeshGeometry && indices != null)
                {
                    GL.BindVertexArray(vao);

                    if (wireFrameMode == 0 || wireFrameMode == 2)
                    {
                        RenderMeshSolidPass(indices);
                    }

                    if (wireFrameMode == 1 || wireFrameMode == 2)
                    {
                        RenderWireframeOverlay(indices, useGreenWireframe: isAvatarMode && wireFrameMode == 1);
                    }
                }

                if (isAvatarMode && showSkeleton && vaoSkeleton != 0 && (boneLinesVertexCount > 0 || jointPointsVertexCount > 0))
                {
                    RenderSkeletonOverlay();
                }

                GL.BindVertexArray(0);
                GL.Flush();
            }
            catch (Exception ex)
            {
                GpuErrorOccurred?.Invoke($"OpenGL rendering failed: {ex.Message}");
            }
        }

        private void RenderMaterialShaderFrame(int framebuffer)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            if (MaterialShaderFrame is not { } frame)
            {
                return;
            }

            if (materialShaderTexture == 0)
            {
                materialShaderTexture = GL.GenTexture();
                materialShaderFramebuffer = GL.GenFramebuffer();
                GL.BindTexture(TextureTarget.Texture2D, materialShaderTexture);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            }
            if (!ReferenceEquals(uploadedMaterialShaderFrame, frame))
            {
                GL.BindTexture(TextureTarget.Texture2D, materialShaderTexture);
                if (uploadedMaterialShaderFrame is { } previous && previous.Width == frame.Width && previous.Height == frame.Height)
                {
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, frame.Width, frame.Height, PixelFormat.Rgba, PixelType.UnsignedByte, frame.RgbaPixels);
                }
                else
                {
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, frame.Width, frame.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, frame.RgbaPixels);
                    GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, materialShaderFramebuffer);
                    GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, materialShaderTexture, 0);
                }
                uploadedMaterialShaderFrame = frame;
            }

            double scale = global::Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            int width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
            int size = Math.Min(width, height);
            int x = (width - size) / 2, y = (height - size) / 2;
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, materialShaderFramebuffer);
            GL.BlitFramebuffer(0, frame.Height, frame.Width, 0, x, y, x + size, y + size, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.Flush();
        }

        private void DeleteMaterialShaderFrame()
        {
            if (materialShaderFramebuffer != 0)
            {
                GL.DeleteFramebuffer(materialShaderFramebuffer);
                GL.DeleteTexture(materialShaderTexture);
                materialShaderFramebuffer = materialShaderTexture = 0;
                uploadedMaterialShaderFrame = null;
            }
        }

        private bool TryUploadPendingTextures()
        {
            bool updateTex = false;
            List<byte[]?>? subTexBytes = null;
            List<int>? subTexW = null;
            List<int>? subTexH = null;
            int[]? subTexMap = null;

            lock (textureLock)
            {
                if (hasPendingSubMeshTextures)
                {
                    updateTex = true;
                    subTexBytes = pendingSubMeshTextures;
                    subTexW = pendingSubMeshTexWidths;
                    subTexH = pendingSubMeshTexHeights;
                    subTexMap = pendingSubMeshTextureMap;
                    hasPendingSubMeshTextures = false;
                    pendingSubMeshTextures = null;
                    pendingSubMeshTexWidths = null;
                    pendingSubMeshTexHeights = null;
                    pendingSubMeshTextureMap = null;
                }
                else if (hasPendingTexture)
                {
                    updateTex = true;
                    hasPendingTexture = false;
                    subTexBytes = new List<byte[]?> { pendingTextureData };
                    subTexW = new List<int> { pendingTextureWidth };
                    subTexH = new List<int> { pendingTextureHeight };
                    subTexMap = null;
                    pendingTextureData = null;
                }
            }

            if (!updateTex || subTexBytes == null)
            {
                return false;
            }

            DeletePreviewTextures();

            previewTextureIds = new int[subTexBytes.Count];
            previewTextureHasAlpha = new bool[subTexBytes.Count];
            previewSubMeshTextureMap = subTexMap;
            for (int i = 0; i < subTexBytes.Count; i++)
            {
                var tBytes = subTexBytes[i];
                if (tBytes == null)
                {
                    continue;
                }

                previewTextureHasAlpha[i] = HasTransparentPixels(tBytes);

                int tw = subTexW![i];
                int th = subTexH![i];

                int id = GL.GenTexture();
                previewTextureIds[i] = id;
                GL.BindTexture(TextureTarget.Texture2D, id);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, tw, th, 0, PixelFormat.Rgba, PixelType.UnsignedByte, tBytes);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }

            vao = 0;
            return true;
        }

        private void UploadPendingDynamicGeometry()
        {
            Vector3[]? localSkeletonVerts = null;
            lock (skeletonLock)
            {
                if (pendingSkeletonVertices != null)
                {
                    localSkeletonVerts = pendingSkeletonVertices;
                    pendingSkeletonVertices = null;
                }
            }

            if (localSkeletonVerts != null)
            {
                if (vaoSkeleton != 0 && vboSkeleton != 0 && localSkeletonVerts.Length > 0)
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vboSkeleton);
                    GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(localSkeletonVerts.Length * 12), localSkeletonVerts, BufferUsageHint.DynamicDraw);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                }
                else
                {
                    CreateSkeletonVAO(localSkeletonVerts);
                }
            }

            Vector3[]? localMeshVerts = null;
            Vector3[]? localMeshNormals = null;
            lock (meshLock)
            {
                if (pendingMeshVertices != null)
                {
                    localMeshVerts = pendingMeshVertices;
                    pendingMeshVertices = null;
                }
                if (pendingMeshNormals != null)
                {
                    localMeshNormals = pendingMeshNormals;
                    pendingMeshNormals = null;
                }
            }

            if (localMeshVerts != null && vboPosition != 0)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, vboPosition);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(localMeshVerts.Length * 12), localMeshVerts, BufferUsageHint.DynamicDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }

            if (localMeshNormals != null && vboNormal != 0)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, vboNormal);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(localMeshNormals.Length * 12), localMeshNormals, BufferUsageHint.DynamicDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }
        }

        private void RenderSkeletonOverlay()
        {
            if (skeletonXRay)
            {
                GL.Disable(EnableCap.DepthTest);
            }

            GL.BindVertexArray(vaoSkeleton);

            if (boneLinesVertexCount > 0)
            {
                GL.UseProgram(pgmYellowID);
                GL.UniformMatrix4(uniformModelMatrixYellow, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixYellow, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixYellow, false, ref projMatrixData);
                GL.DrawArrays(PrimitiveType.Lines, 0, boneLinesVertexCount);
            }

            if (jointPointsVertexCount > 0)
            {
                GL.UseProgram(pgmRedID);
                GL.UniformMatrix4(uniformModelMatrixRed, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixRed, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixRed, false, ref projMatrixData);
                GL.DrawArrays(PrimitiveType.Lines, boneLinesVertexCount, jointPointsVertexCount);
            }

            if (skeletonXRay)
            {
                GL.Enable(EnableCap.DepthTest);
            }
        }

        private void RenderMeshSolidPass(int[] indices)
        {
            if (!isGles)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            var localTextureIds = previewTextureIds;
            var localTextureHasAlpha = previewTextureHasAlpha;
            var localMaterialTints = materialTintOverrides;
            var localSubMeshIndexCounts = previewSubMeshIndexCounts;
            bool effectiveTextures = useTextures && previewMaterialMode;
            bool drawBySubMesh = localSubMeshIndexCounts != null
                && localSubMeshIndexCounts.Length > 0
                && ((effectiveTextures && localTextureIds != null) || (localMaterialTints != null && localMaterialTints.Length > 0));

            if (drawBySubMesh && localSubMeshIndexCounts != null)
            {
                var drawRanges = new List<(int SubMeshIndex, int OffsetElements, int IndexCount, int TexId, Vector4 Tint, bool IsTransparent)>(localSubMeshIndexCounts.Length);
                int flatOffsetElements = 0;
                bool hasOpaque = false;
                bool hasTransparent = false;

                for (int i = 0; i < localSubMeshIndexCounts.Length; i++)
                {
                    int indexCount = localSubMeshIndexCounts[i];
                    if (indexCount <= 0)
                    {
                        continue;
                    }

                    if (flatOffsetElements >= indices.Length)
                    {
                        break;
                    }

                    indexCount = Math.Min(indexCount, indices.Length - flatOffsetElements);
                    int texId = ResolveSubMeshTextureId(localTextureIds, previewSubMeshTextureMap, i);
                    var tint = GetMaterialTint(i);
                    bool isTransparent = IsSubMeshTransparent(
                        i,
                        previewSubMeshTextureMap,
                        effectiveTextures ? localTextureHasAlpha : null,
                        tint);

                    if (isTransparent)
                    {
                        hasTransparent = true;
                    }
                    else
                    {
                        hasOpaque = true;
                    }

                    drawRanges.Add((i, flatOffsetElements, indexCount, texId, tint, isTransparent));
                    flatOffsetElements += indexCount;
                }

                // Pass 1: Opaque submeshes with depth writes enabled.
                GL.DepthMask(true);
                for (int i = 0; i < drawRanges.Count; i++)
                {
                    var range = drawRanges[i];
                    if (range.IsTransparent)
                    {
                        continue;
                    }

                    BindMeshShaderProgram(range.TexId, range.Tint);
                    GL.DrawElements(PrimitiveType.Triangles, range.IndexCount, DrawElementsType.UnsignedInt, (IntPtr)(range.OffsetElements * 4));
                }

                // Pass 2: Transparent & decal overlay submeshes drawn last with polygon offset and depth writes disabled over opaque geometry.
                if (hasTransparent)
                {
                    GL.DepthMask(!hasOpaque);
                    GL.Enable(EnableCap.PolygonOffsetFill);
                    GL.PolygonOffset(-1.0f, -1.0f);

                    for (int i = 0; i < drawRanges.Count; i++)
                    {
                        var range = drawRanges[i];
                        if (!range.IsTransparent)
                        {
                            continue;
                        }

                        BindMeshShaderProgram(range.TexId, range.Tint);
                        GL.DrawElements(PrimitiveType.Triangles, range.IndexCount, DrawElementsType.UnsignedInt, (IntPtr)(range.OffsetElements * 4));
                    }

                    GL.Disable(EnableCap.PolygonOffsetFill);
                    GL.DepthMask(true);
                }

                return;
            }

            GL.DepthMask(true);
            var defaultTint = GetMaterialTint(0);
            int defaultTexId = localTextureIds != null && localTextureIds.Length > 0 ? localTextureIds[0] : 0;
            BindMeshShaderProgram(defaultTexId, defaultTint);
            GL.DrawElements(PrimitiveType.Triangles, indices.Length, DrawElementsType.UnsignedInt, 0);
        }

        private void BindMeshShaderProgram(int textureId, Vector4 tint)
        {
            if (useTextures && previewMaterialMode && textureId != 0 && uvData != null)
            {
                GL.UseProgram(pgmTexID);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, textureId);
                GL.Uniform1(uniformTexture, 0);
                GL.UniformMatrix4(uniformModelMatrixTex, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixTex, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixTex, false, ref projMatrixData);
                SetMaterialTint(uniformMaterialTintTex, tint);
                return;
            }

            if (shadeMode == 0)
            {
                GL.UseProgram(pgmID);
                GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
                SetMaterialTint(uniformMaterialTint, tint);
            }
            else
            {
                GL.UseProgram(pgmColorID);
                GL.UniformMatrix4(uniformModelMatrixColor, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixColor, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixColor, false, ref projMatrixData);
                SetMaterialTint(uniformMaterialTintColor, tint);
            }
        }

        private void RenderWireframeOverlay(int[] indices, bool useGreenWireframe = false)
        {
            EnsureWireframeVAO();
            if (vaoWireframe == 0)
            {
                return;
            }

            if (!isGles)
            {
                GL.Enable(EnableCap.PolygonOffsetLine);
                GL.PolygonOffset(-1.0f, -1.0f);
            }

            if (useGreenWireframe)
            {
                GL.UseProgram(pgmGreenID);
                GL.UniformMatrix4(uniformModelMatrixGreen, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixGreen, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixGreen, false, ref projMatrixData);
            }
            else
            {
                GL.UseProgram(pgmBlackID);
                GL.UniformMatrix4(uniformModelMatrixBlack, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixBlack, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixBlack, false, ref projMatrixData);
            }

            GL.BindVertexArray(vaoWireframe);

            int lineCount = (indices.Length / 3) * 6;
            if (lineCount > 0)
            {
                GL.DrawElements(BeginMode.Lines, lineCount, DrawElementsType.UnsignedInt, 0);
            }

            if (!isGles)
            {
                GL.Disable(EnableCap.PolygonOffsetLine);
            }
        }

        internal static int ResolveSubMeshTextureId(int[]? textureIds, int[]? subMeshTextureMap, int subMeshIndex)
        {
            if (textureIds == null || textureIds.Length == 0 || subMeshIndex < 0)
            {
                return 0;
            }

            if (subMeshTextureMap != null)
            {
                if (subMeshIndex >= subMeshTextureMap.Length)
                {
                    return 0;
                }

                var mappedIndex = subMeshTextureMap[subMeshIndex];
                return mappedIndex >= 0 && mappedIndex < textureIds.Length ? textureIds[mappedIndex] : 0;
            }

            int directIndex = subMeshIndex < textureIds.Length ? subMeshIndex : 0;
            return textureIds[directIndex];
        }

        private void ClearPendingTextureUploads()
        {
            lock (textureLock)
            {
                pendingTextureData = null;
                pendingTextureWidth = 0;
                pendingTextureHeight = 0;
                hasPendingTexture = false;
                pendingSubMeshTextures = null;
                pendingSubMeshTexWidths = null;
                pendingSubMeshTexHeights = null;
                pendingSubMeshTextureMap = null;
                hasPendingSubMeshTextures = false;
            }
        }

        private bool HasPreviewTexture()
        {
            return previewTextureId != 0 || previewTextureIds?.Any(id => id != 0) == true;
        }

        private void DeletePreviewTextures()
        {
            if (previewTextureId != 0)
            {
                GL.DeleteTexture(previewTextureId);
                previewTextureId = 0;
            }

            if (previewTextureIds != null)
            {
                foreach (var id in previewTextureIds)
                {
                    if (id != 0)
                    {
                        GL.DeleteTexture(id);
                    }
                }
                previewTextureIds = null;
            }

            previewSubMeshTextureMap = null;
            previewTextureHasAlpha = null;
        }

        private Vector4 GetMaterialTint(int materialIndex)
        {
            var tints = materialTintOverrides;
            if (tints != null && materialIndex >= 0 && materialIndex < tints.Length)
            {
                return tints[materialIndex];
            }

            return Vector4.One;
        }

        private static void SetMaterialTint(int uniformLocation, Vector4 tint)
        {
            if (uniformLocation >= 0)
            {
                GL.Uniform4(uniformLocation, tint.X, tint.Y, tint.Z, tint.W);
            }
        }
    }
}
