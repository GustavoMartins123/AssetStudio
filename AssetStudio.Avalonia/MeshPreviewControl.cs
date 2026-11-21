using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL.Controls;

namespace AssetStudio.Avalonia
{
    using Vector2 = OpenTK.Mathematics.Vector2;
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public enum MeshPreviewViewPreset
    {
        Auto,
        Front,
        Back,
        Left,
        Right,
        Top
    }

    public partial class MeshPreviewControl : OpenGlControlBase
    {
        // Skeleton & avatar state
        private int vaoSkeleton;
        private int vboSkeleton;
        private int boneLinesVertexCount;
        private int jointPointsVertexCount;
        private bool isAvatarMode;
        private Vector3[]? pendingSkeletonVertices;
        private readonly object skeletonLock = new object();
        private bool isAnimPlaying;
        private int vboPosition;
        private Vector3[][]? animatedMeshVertices;
        private Vector3[]? pendingMeshVertices;
        private readonly object meshLock = new object();
        private float previewMeshDensityPercent = PreviewMeshSimplifier.DefaultDensityPercent;
        private Mesh? avatarReferenceMeshSource;
        private Vector3[]? avatarReferenceSourceVertices;
        private List<uint>? avatarReferenceSourceIndices;
        private Vector3 avatarReferenceMin;
        private Vector3 avatarReferenceMax;
        private Matrix4[][]? avatarReferenceBoneMatrices;
        private int[]? avatarReferenceParentIndices;

        // Animation playback state
        private Vector3[][]? animationFrames;
        private int[]? animParentIndices;
        private int animCurrentFrame;
        private int animBoneLinesPerFrame;
        private int animJointPointsPerFrame;
        private float animFps = 30f;
        private global::Avalonia.Threading.DispatcherTimer? animTimer;

        // VBO / VAO state
        private int vao;
        private int vaoWireframe;
        private readonly List<int> vbos = new List<int>();

        // Mesh geometry buffers
        private Vector3[]? vertexData;
        private Vector3[]? normalData;
        private Vector3[]? normal2Data;
        private Vector4[]? colorData;
        private Vector2[]? uvData;
        private int[]? indiceData;

        // Matrices & camera framing state
        private Matrix4 modelMatrixData = Matrix4.Identity;
        private Matrix4 viewMatrixData = CreateUnityAutoViewMatrix();
        private Matrix4 projMatrixData = Matrix4.Identity;
        private Matrix4 initialViewMatrix = CreateUnityAutoViewMatrix();
        private Matrix4 initialModelMatrix = Matrix4.Identity;
        private Matrix4 automaticViewMatrix = CreateUnityAutoViewMatrix();
        private MeshPreviewViewPreset viewPreset = MeshPreviewViewPreset.Auto;
        private float boneScale = 1.0f;
        private Vector3[]? staticBonePositions;
        private int[]? staticParentIndices;

        // Mode flags & material state
        private int wireFrameMode;
        private int shadeMode;
        private int normalMode;
        private bool previewMaterialMode;
        private int previewTextureId;
        private Vector4[]? materialTintOverrides;
        private bool keepExistingTextures;

        // Thread-safe texture loading state
        private readonly object textureLock = new object();
        private byte[]? pendingTextureData;
        private int pendingTextureWidth;
        private int pendingTextureHeight;
        private bool hasPendingTexture;
        private List<byte[]?>? pendingSubMeshTextures;
        private List<int>? pendingSubMeshTexWidths;
        private List<int>? pendingSubMeshTexHeights;
        private int[]? pendingSubMeshTextureMap;
        private bool hasPendingSubMeshTextures;
        private int[]? previewTextureIds;
        private int[]? previewSubMeshTextureMap;

        // Source mesh & mesh-group references for density rebuilds
        private Mesh? meshPreviewSource;
        private Vector2[]? meshPreviewSourceUvs;
        private List<byte[]?>? meshPreviewSourceSubMeshTextures;
        private List<int>? meshPreviewSourceSubMeshTexWidths;
        private List<int>? meshPreviewSourceSubMeshTexHeights;
        private int[]? meshPreviewSourceSubMeshTextureMap;
        private IReadOnlyList<Mesh>? meshGroupPreviewSources;
        private IReadOnlyList<Vector2[]?>? meshGroupPreviewSourceUvs;
        private IReadOnlyList<float[]?>? meshGroupPreviewSourceTransforms;
        private List<byte[]?>? meshGroupPreviewSourceSubMeshTextures;
        private List<int>? meshGroupPreviewSourceSubMeshTexWidths;
        private List<int>? meshGroupPreviewSourceSubMeshTexHeights;
        private int[]? meshGroupPreviewSourceSubMeshTextureMap;
        private int[]? previewSubMeshIndexCounts;
        private int meshLoadCounter;

        // Viewport / OpenGL profile state
        private double lastWidth;
        private double lastHeight;
        private bool isGles;

        public event Action<string>? GpuErrorOccurred;
        public event Action<int, int>? AnimationFrameChanged;
        internal event Action<bool>? MeshGeometryLoaded;

        internal Func<Mesh, float, MeshPreviewGeometryCache?>? LoadMeshPreviewGeometryCache { get; set; }
        internal Action<Mesh, float, MeshPreviewGeometryCache>? SaveMeshPreviewGeometryCache { get; set; }

        public MeshPreviewControl()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public float BoneScale
        {
            get => boneScale;
            set
            {
                boneScale = value;
                UpdateSkeletonVertices();
                RequestNextFrameRendering();
            }
        }

        public float PreviewMeshDensityPercent
        {
            get => previewMeshDensityPercent;
            set
            {
                var clamped = Math.Clamp(value, PreviewMeshSimplifier.MinDensityPercent, PreviewMeshSimplifier.MaxDensityPercent);
                if (Math.Abs(previewMeshDensityPercent - clamped) < 0.01f)
                {
                    return;
                }

                previewMeshDensityPercent = clamped;
                RebuildPreviewMeshGeometry();
            }
        }

        public float AvatarReferenceMeshDensityPercent
        {
            get => PreviewMeshDensityPercent;
            set => PreviewMeshDensityPercent = value;
        }

        public int WireframeMode
        {
            get => wireFrameMode;
            set
            {
                wireFrameMode = value % 3;
                RequestNextFrameRendering();
            }
        }

        public int ShadeMode
        {
            get => shadeMode;
            set
            {
                shadeMode = value;
                RequestNextFrameRendering();
            }
        }

        public int NormalMode
        {
            get => normalMode;
            set
            {
                normalMode = value;
                vao = 0;
                RequestNextFrameRendering();
            }
        }

        public bool IsPlaying => isAnimPlaying;
        public int AnimCurrentFrame => animCurrentFrame;
        public int AnimTotalFrames => animationFrames?.Length ?? 0;
        public float AnimFps => animFps;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        }

        private void RebuildPreviewMeshGeometry()
        {
            if (isAvatarMode)
            {
                RebuildAvatarReferenceMeshPreview();
                return;
            }

            if (meshPreviewSource != null)
            {
                SetMesh(
                    meshPreviewSource,
                    meshPreviewSourceUvs,
                    meshPreviewSourceSubMeshTextures,
                    meshPreviewSourceSubMeshTexWidths,
                    meshPreviewSourceSubMeshTexHeights,
                    resetMaterialOverrides: false,
                    subMeshTextureMap: meshPreviewSourceSubMeshTextureMap);
            }
            else if (meshGroupPreviewSources != null && meshGroupPreviewSources.Count > 0)
            {
                SetMeshGroup(
                    meshGroupPreviewSources,
                    meshGroupPreviewSourceUvs,
                    meshGroupPreviewSourceTransforms,
                    meshGroupPreviewSourceSubMeshTextures,
                    meshGroupPreviewSourceSubMeshTexWidths,
                    meshGroupPreviewSourceSubMeshTexHeights,
                    resetMaterialOverrides: false,
                    subMeshTextureMap: meshGroupPreviewSourceSubMeshTextureMap);
            }
        }

        private void ClearMeshPreviewSource()
        {
            meshPreviewSource = null;
            meshPreviewSourceUvs = null;
            meshPreviewSourceSubMeshTextures = null;
            meshPreviewSourceSubMeshTexWidths = null;
            meshPreviewSourceSubMeshTexHeights = null;
            meshPreviewSourceSubMeshTextureMap = null;
            meshGroupPreviewSources = null;
            meshGroupPreviewSourceUvs = null;
            meshGroupPreviewSourceTransforms = null;
            meshGroupPreviewSourceSubMeshTextures = null;
            meshGroupPreviewSourceSubMeshTexWidths = null;
            meshGroupPreviewSourceSubMeshTexHeights = null;
            meshGroupPreviewSourceSubMeshTextureMap = null;
            previewSubMeshIndexCounts = null;
            keepExistingTextures = false;
            ClearPendingTextureUploads();
        }
    }
}
