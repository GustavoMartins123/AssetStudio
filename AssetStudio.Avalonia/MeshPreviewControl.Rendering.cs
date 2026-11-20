using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.OpenGL;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;

namespace AssetStudio.Avalonia
{
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;

    public partial class MeshPreviewControl
    {
        public void SetMaterialOverrides(IReadOnlyList<Vector4>? materialTints)
        {
            materialTintOverrides = materialTints == null || materialTints.Count == 0
                ? null
                : materialTints.ToArray();
            RequestNextFrameRendering();
        }

        public void ApplyMeshTextures(List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights)
        {
            ApplyMeshTextures(null, subMeshTextures, subMeshTexWidths, subMeshTexHeights);
        }

        public void ApplyMeshTextures(Mesh? expectedMesh, List<byte[]?>? subMeshTextures, List<int>? subMeshTexWidths, List<int>? subMeshTexHeights)
        {
            if (subMeshTextures == null || !subMeshTextures.Any(t => t != null))
            {
                return;
            }

            if (expectedMesh != null && !ReferenceEquals(meshPreviewSource, expectedMesh))
            {
                return;
            }

            if (meshGroupPreviewSources != null && meshGroupPreviewSources.Count > 0 && expectedMesh == null)
            {
                meshGroupPreviewSourceSubMeshTextures = subMeshTextures;
                meshGroupPreviewSourceSubMeshTexWidths = subMeshTexWidths;
                meshGroupPreviewSourceSubMeshTexHeights = subMeshTexHeights;
            }
            else
            {
                meshPreviewSourceSubMeshTextures = subMeshTextures;
                meshPreviewSourceSubMeshTexWidths = subMeshTexWidths;
                meshPreviewSourceSubMeshTexHeights = subMeshTexHeights;
            }

            keepExistingTextures = true;
            previewMaterialMode = true;
            vao = 0;

            lock (textureLock)
            {
                pendingSubMeshTextures = subMeshTextures;
                pendingSubMeshTexWidths = subMeshTexWidths;
                pendingSubMeshTexHeights = subMeshTexHeights;
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
                var indices = indiceData;
                if (vertexData == null || indices == null)
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
                    RenderAvatarFrame(indices);
                    return;
                }

                GL.BindVertexArray(vao);

                if (wireFrameMode == 0 || wireFrameMode == 2)
                {
                    RenderMeshSolidPass(indices);
                }

                if (wireFrameMode == 1 || wireFrameMode == 2)
                {
                    RenderWireframeOverlay(indices);
                }

                GL.BindVertexArray(0);
                GL.Flush();
            }
            catch (Exception ex)
            {
                GpuErrorOccurred?.Invoke($"OpenGL rendering failed: {ex.Message}");
            }
        }

        private bool TryUploadPendingTextures()
        {
            bool updateTex = false;
            List<byte[]?>? subTexBytes = null;
            List<int>? subTexW = null;
            List<int>? subTexH = null;

            lock (textureLock)
            {
                if (hasPendingSubMeshTextures)
                {
                    updateTex = true;
                    subTexBytes = pendingSubMeshTextures;
                    subTexW = pendingSubMeshTexWidths;
                    subTexH = pendingSubMeshTexHeights;
                    hasPendingSubMeshTextures = false;
                    pendingSubMeshTextures = null;
                    pendingSubMeshTexWidths = null;
                    pendingSubMeshTexHeights = null;
                }
                else if (hasPendingTexture)
                {
                    updateTex = true;
                    hasPendingTexture = false;
                    subTexBytes = new List<byte[]?> { pendingTextureData };
                    subTexW = new List<int> { pendingTextureWidth };
                    subTexH = new List<int> { pendingTextureHeight };
                    pendingTextureData = null;
                }
            }

            if (!updateTex || subTexBytes == null)
            {
                return false;
            }

            DeletePreviewTextures();

            previewTextureIds = new int[subTexBytes.Count];
            for (int i = 0; i < subTexBytes.Count; i++)
            {
                var tBytes = subTexBytes[i];
                if (tBytes == null)
                {
                    continue;
                }

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

        private void RenderAvatarFrame(int[] indices)
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

            if (localSkeletonVerts != null && vaoSkeleton != 0 && vboSkeleton != 0)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, vboSkeleton);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(localSkeletonVerts.Length * 12), localSkeletonVerts, BufferUsageHint.DynamicDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }

            Vector3[]? localMeshVerts = null;
            lock (meshLock)
            {
                if (pendingMeshVertices != null)
                {
                    localMeshVerts = pendingMeshVertices;
                    pendingMeshVertices = null;
                }
            }

            if (localMeshVerts != null && vboPosition != 0)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, vboPosition);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(localMeshVerts.Length * 12), localMeshVerts, BufferUsageHint.DynamicDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }

            EnsureWireframeVAO();
            if (vaoWireframe != 0)
            {
                GL.UseProgram(pgmGreenID);
                GL.UniformMatrix4(uniformModelMatrixGreen, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixGreen, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixGreen, false, ref projMatrixData);
                GL.BindVertexArray(vaoWireframe);
                int lineCount = (indices.Length / 3) * 6;
                if (lineCount > 0)
                {
                    GL.DrawElements(BeginMode.Lines, lineCount, DrawElementsType.UnsignedInt, 0);
                }
            }

            if (vaoSkeleton != 0 && (boneLinesVertexCount > 0 || jointPointsVertexCount > 0))
            {
                GL.Disable(EnableCap.DepthTest);
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

                GL.Enable(EnableCap.DepthTest);
            }

            GL.BindVertexArray(0);
            GL.Flush();
        }

        private void RenderMeshSolidPass(int[] indices)
        {
            if (!isGles)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            var localTextureIds = previewTextureIds;
            var localMaterialTints = materialTintOverrides;
            var localSubMeshIndexCounts = previewSubMeshIndexCounts;
            bool drawBySubMesh = localSubMeshIndexCounts != null
                && localSubMeshIndexCounts.Length > 0
                && !isAvatarMode
                && ((previewMaterialMode && localTextureIds != null) || (localMaterialTints != null && localMaterialTints.Length > 0));

            if (drawBySubMesh && localSubMeshIndexCounts != null)
            {
                int flatOffsetElements = 0;
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
                    int texIndex = localTextureIds != null && i < localTextureIds.Length ? i : 0;
                    int texId = localTextureIds != null && texIndex < localTextureIds.Length ? localTextureIds[texIndex] : 0;
                    var tint = GetMaterialTint(i);

                    BindMeshShaderProgram(texId, tint);
                    GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, (IntPtr)(flatOffsetElements * 4));
                    flatOffsetElements += indexCount;
                }

                return;
            }

            var defaultTint = GetMaterialTint(0);
            int defaultTexId = localTextureIds != null && localTextureIds.Length > 0 ? localTextureIds[0] : 0;
            BindMeshShaderProgram(defaultTexId, defaultTint);
            GL.DrawElements(PrimitiveType.Triangles, indices.Length, DrawElementsType.UnsignedInt, 0);
        }

        private void BindMeshShaderProgram(int textureId, Vector4 tint)
        {
            if (previewMaterialMode && textureId != 0)
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

        private void RenderWireframeOverlay(int[] indices)
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

            GL.UseProgram(pgmBlackID);
            GL.UniformMatrix4(uniformModelMatrixBlack, false, ref modelMatrixData);
            GL.UniformMatrix4(uniformViewMatrixBlack, false, ref viewMatrixData);
            GL.UniformMatrix4(uniformProjMatrixBlack, false, ref projMatrixData);
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
