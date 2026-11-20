using System;
using Avalonia.Input;

namespace AssetStudio.Avalonia
{
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public partial class MeshPreviewControl
    {
        private const float PreviewFitScale = 1.6f;
        private const float PreviewDepthScale = 0.02f;

        // Pointer drag anchor
        private global::Avalonia.Point mpos;

        public MeshPreviewViewPreset ViewPreset
        {
            get => viewPreset;
            set
            {
                if (!Enum.IsDefined(value))
                {
                    value = MeshPreviewViewPreset.Auto;
                }

                viewPreset = value;
                initialViewMatrix = ResolveViewPresetMatrix();
                viewMatrixData = initialViewMatrix;
                modelMatrixData = initialModelMatrix;
                RequestNextFrameRendering();
            }
        }

        public void FocusSubMesh(int subMeshIndex)
        {
            if (subMeshIndex < 0 || isAvatarMode)
            {
                return;
            }

            if (!TryGetSubMeshBounds(subMeshIndex, out var min, out var max))
            {
                return;
            }

            modelMatrixData = CreatePreviewModelMatrix(min, max);
            RequestNextFrameRendering();
        }

        public void ResetView()
        {
            viewMatrixData = initialViewMatrix;
            modelMatrixData = initialModelMatrix;
            RequestNextFrameRendering();
        }

        public void RotateLeft90()
        {
            viewMatrixData *= Matrix4.CreateRotationY((float)(Math.PI / 2));
            RequestNextFrameRendering();
        }

        public void RotateRight90()
        {
            viewMatrixData *= Matrix4.CreateRotationY((float)(-Math.PI / 2));
            RequestNextFrameRendering();
        }

        public void RotateUp90()
        {
            viewMatrixData *= Matrix4.CreateRotationX((float)(Math.PI / 2));
            RequestNextFrameRendering();
        }

        public void RotateDown90()
        {
            viewMatrixData *= Matrix4.CreateRotationX((float)(-Math.PI / 2));
            RequestNextFrameRendering();
        }

        private bool TryGetSubMeshBounds(int subMeshIndex, out Vector3 min, out Vector3 max)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;

            var subMeshIndexCounts = previewSubMeshIndexCounts;
            var indices = indiceData;
            var vertices = vertexData;
            if (subMeshIndexCounts == null
                || indices == null
                || vertices == null
                || subMeshIndex < 0
                || subMeshIndex >= subMeshIndexCounts.Length)
            {
                return false;
            }

            var flatOffsetElements = 0;
            for (var i = 0; i < subMeshIndex; i++)
            {
                flatOffsetElements += Math.Max(0, subMeshIndexCounts[i]);
            }

            if (flatOffsetElements >= indices.Length)
            {
                return false;
            }

            var indexCount = Math.Min(Math.Max(0, subMeshIndexCounts[subMeshIndex]), indices.Length - flatOffsetElements);
            if (indexCount <= 0)
            {
                return false;
            }

            var hasPoint = false;
            var end = flatOffsetElements + indexCount;
            for (var i = flatOffsetElements; i < end; i++)
            {
                var vertexIndex = indices[i];
                if ((uint)vertexIndex >= (uint)vertices.Length)
                {
                    continue;
                }

                var point = vertices[vertexIndex];
                if (!IsFinitePoint(point))
                {
                    continue;
                }

                if (!hasPoint)
                {
                    min = point;
                    max = point;
                    hasPoint = true;
                }
                else
                {
                    ExpandBounds(point, ref min, ref max);
                }
            }

            return hasPoint;
        }

        private static Matrix4 CreatePreviewModelMatrix(Vector3 min, Vector3 max)
        {
            var size = max - min;
            var center = (max + min) * 0.5f;
            var largestExtent = Math.Max(1e-5f, Math.Max(size.X, Math.Max(size.Y, size.Z)));
            return Matrix4.CreateTranslation(-center) * Matrix4.CreateScale(PreviewFitScale / largestExtent);
        }

        private static Matrix4 CreateUnityAutoViewMatrix()
        {
            // Unity assets use +Y as up and +Z as forward. A three-quarter view
            // keeps those axes stable while still showing depth.
            return Matrix4.CreateRotationY(-(float)Math.PI / 4)
                * Matrix4.CreateRotationX(-(float)Math.PI / 6);
        }

        private static Matrix4 CreateAxisViewMatrix(Vector3 cameraDirection, Vector3 up)
        {
            var view = Matrix4.LookAt(cameraDirection, Vector3.Zero, up);
            view.Row3 = new Vector4(0, 0, 0, 1);
            return view;
        }

        private Matrix4 ResolveViewPresetMatrix()
        {
            return viewPreset switch
            {
                MeshPreviewViewPreset.Front => CreateAxisViewMatrix(Vector3.UnitZ, Vector3.UnitY),
                MeshPreviewViewPreset.Back => CreateAxisViewMatrix(-Vector3.UnitZ, Vector3.UnitY),
                MeshPreviewViewPreset.Left => CreateAxisViewMatrix(-Vector3.UnitX, Vector3.UnitY),
                MeshPreviewViewPreset.Right => CreateAxisViewMatrix(Vector3.UnitX, Vector3.UnitY),
                MeshPreviewViewPreset.Top => CreateAxisViewMatrix(Vector3.UnitY, -Vector3.UnitZ),
                _ => automaticViewMatrix
            };
        }

        private void SetInitialPreviewFrame(Matrix4 autoViewMatrix, Matrix4 modelMatrix)
        {
            automaticViewMatrix = autoViewMatrix;
            initialModelMatrix = modelMatrix;
            initialViewMatrix = ResolveViewPresetMatrix();
            modelMatrixData = initialModelMatrix;
            viewMatrixData = initialViewMatrix;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            mpos = e.GetPosition(this);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var prop = e.GetCurrentPoint(this).Properties;
            bool isLeft = prop.IsLeftButtonPressed;
            bool isRight = prop.IsRightButtonPressed;

            if (isLeft || isRight)
            {
                var curPos = e.GetPosition(this);
                float dx = (float)(mpos.X - curPos.X);
                float dy = (float)(mpos.Y - curPos.Y);
                mpos = curPos;

                if (isLeft)
                {
                    dx *= 0.01f;
                    dy *= 0.01f;
                    viewMatrixData *= Matrix4.CreateRotationX(dy);
                    viewMatrixData *= Matrix4.CreateRotationY(dx);
                }
                else if (isRight)
                {
                    dx *= 0.003f;
                    dy *= 0.003f;
                    viewMatrixData *= Matrix4.CreateTranslation(-dx, dy, 0);
                }

                RequestNextFrameRendering();
            }
            else
            {
                mpos = e.GetPosition(this);
            }

            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            float delta = (float)e.Delta.Y;
            viewMatrixData *= Matrix4.CreateScale(1 + delta * 0.1f);
            RequestNextFrameRendering();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var isCtrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (!isCtrl)
            {
                if (e.Key == Key.Left || e.Key == Key.A)
                {
                    RotateLeft90();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Right || e.Key == Key.D)
                {
                    RotateRight90();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Up || e.Key == Key.W)
                {
                    RotateUp90();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Down || e.Key == Key.S)
                {
                    RotateDown90();
                    e.Handled = true;
                    return;
                }
            }

            base.OnKeyDown(e);

            if (isCtrl)
            {
                if (e.Key == Key.W)
                {
                    wireFrameMode = (wireFrameMode + 1) % 3;
                    RequestNextFrameRendering();
                    e.Handled = true;
                }
                else if (e.Key == Key.S)
                {
                    if (uvData != null && HasPreviewTexture())
                    {
                        previewMaterialMode = !previewMaterialMode;
                        vao = 0;
                    }
                    else
                    {
                        shadeMode = shadeMode == 0 ? 1 : 0;
                    }
                    RequestNextFrameRendering();
                    e.Handled = true;
                }
                else if (e.Key == Key.N)
                {
                    normalMode = normalMode == 0 ? 1 : 0;
                    vao = 0;
                    RequestNextFrameRendering();
                    e.Handled = true;
                }
            }
        }
    }
}
