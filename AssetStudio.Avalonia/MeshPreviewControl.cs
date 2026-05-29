using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Media;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
namespace AssetStudio.Avalonia
{
    using Vector2 = OpenTK.Mathematics.Vector2;
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public class MeshPreviewControl : OpenGlControlBase
    {
        private const string vsSource = @"
layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec3 normalDirection;
layout(location = 2) in vec4 vertexColor;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projMatrix;
out vec3 normal;
out vec4 color;
void main()
{
	gl_Position = projMatrix * viewMatrix * modelMatrix * vec4(vertexPosition, 1.0);
	normal = normalDirection;
	color = vertexColor; 
}";

        private const string fsSource = @"
in vec3 normal;
layout(location = 0) out vec4 outputColor;
void main()
{
	vec3 unitNormal = normalize(normal);
	float nDotProduct = clamp(dot(unitNormal, vec3(0.707, 0.0, 0.707)), 0.0, 1.0);
	vec2 ContributionWeightsSqrt = vec2(0.5, 0.5) + vec2(0.5, -0.5) * unitNormal.y;
	vec2 ContributionWeights = ContributionWeightsSqrt * ContributionWeightsSqrt;
	vec3 color = nDotProduct * vec3(1.0, 0.957, 0.839) / 3.14159;
	color += vec3(0.779, 0.716, 0.453) * ContributionWeights.y;
	color += vec3(0.368, 0.477, 0.735) * ContributionWeights.x;
	outputColor = vec4(sqrt(color), 1.0);
}";

        private const string fsBlackSource = @"
layout(location = 0) out vec4 outputColor;
void main()
{
	outputColor = vec4(0.0, 0.0, 0.0, 1.0);
}";

        private const string vsSkeletonSource = @"
layout(location = 0) in vec3 vertexPosition;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projMatrix;
void main()
{
	gl_Position = projMatrix * viewMatrix * modelMatrix * vec4(vertexPosition, 1.0);
}";

        private const string fsGreenSource = @"
layout(location = 0) out vec4 outputColor;
void main()
{
	outputColor = vec4(0.0, 0.9, 0.1, 1.0);
}";

        private const string fsYellowSource = @"
layout(location = 0) out vec4 outputColor;
void main()
{
	outputColor = vec4(1.0, 0.9, 0.0, 1.0);
}";

        private const string fsRedSource = @"
layout(location = 0) out vec4 outputColor;
void main()
{
	outputColor = vec4(1.0, 0.3, 0.0, 1.0);
}";

        private const string fsColorSource = @"
layout(location = 0) out vec4 outputColor;
in vec4 color;
void main()
{
	outputColor = color;
}";

        private const string vsTexSource = @"
layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec3 normalDirection;
layout(location = 3) in vec2 vertexTexCoord;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projMatrix;
out vec3 normal;
out vec2 texCoord;
void main()
{
	gl_Position = projMatrix * viewMatrix * modelMatrix * vec4(vertexPosition, 1.0);
	normal = normalDirection;
	texCoord = vec2(vertexTexCoord.x, 1.0 - vertexTexCoord.y); 
}";

        private const string fsTexSource = @"
in vec3 normal;
in vec2 texCoord;
layout(location = 0) out vec4 outputColor;
uniform sampler2D mainTex;
void main()
{
	vec3 unitNormal = normalize(normal);
	float nDotProduct = clamp(dot(unitNormal, vec3(0.707, 0.0, 0.707)), 0.0, 1.0);
	vec2 ContributionWeightsSqrt = vec2(0.5, 0.5) + vec2(0.5, -0.5) * unitNormal.y;
	vec2 ContributionWeights = ContributionWeightsSqrt * ContributionWeightsSqrt;
	vec3 lightColor = nDotProduct * vec3(1.0, 0.957, 0.839) / 3.14159;
	lightColor += vec3(0.779, 0.716, 0.453) * ContributionWeights.y;
	lightColor += vec3(0.368, 0.477, 0.735) * ContributionWeights.x;
	vec4 texColor = texture(mainTex, texCoord);
	if (texColor.a < 0.25)
		discard;
	outputColor = vec4(texColor.rgb * lightColor, texColor.a);
}";

        private const int LocationPosition = 0;
        private const int LocationNormal = 1;
        private const int LocationColor = 2;
        private const int LocationTexCoord = 3;
        private const float PreviewFitScale = 1.35f;
        private const float DefaultAvatarReferenceMeshDensityPercent = 70f;
        private const float MinAvatarReferenceMeshDensityPercent = 1f;
        private const float MaxAvatarReferenceMeshDensityPercent = 100f;

        // Programs
        private int pgmID;
        private int pgmColorID;
        private int pgmBlackID;
        private int pgmTexID;

        // Attribute / Uniform locations
        private int uniformModelMatrix;
        private int uniformViewMatrix;
        private int uniformProjMatrix;

        private int uniformModelMatrixColor;
        private int uniformViewMatrixColor;
        private int uniformProjMatrixColor;

        private int uniformModelMatrixBlack;
        private int uniformViewMatrixBlack;
        private int uniformProjMatrixBlack;

        private int uniformTexture;
        private int uniformModelMatrixTex;
        private int uniformViewMatrixTex;
        private int uniformProjMatrixTex;

        private int pgmGreenID;
        private int uniformModelMatrixGreen;
        private int uniformViewMatrixGreen;
        private int uniformProjMatrixGreen;

        private int pgmYellowID;
        private int uniformModelMatrixYellow;
        private int uniformViewMatrixYellow;
        private int uniformProjMatrixYellow;

        private int pgmRedID;
        private int uniformModelMatrixRed;
        private int uniformViewMatrixRed;
        private int uniformProjMatrixRed;

        private int vaoSkeleton;
        private int vboSkeleton;
        private int boneLinesVertexCount;
        private int jointPointsVertexCount;
        private bool isAvatarMode;
        private Vector3[]? pendingSkeletonVertices;
        private readonly object skeletonLock = new object();
        private bool isAnimPlaying = false;
        private int vboPosition;
        private Vector3[][]? animatedMeshVertices;
        private Vector3[]? pendingMeshVertices;
        private readonly object meshLock = new object();
        private float avatarReferenceMeshDensityPercent = DefaultAvatarReferenceMeshDensityPercent;
        private Mesh? avatarReferenceMeshSource;
        private Vector3[]? avatarReferenceSourceVertices;
        private List<uint>? avatarReferenceSourceIndices;
        private Vector3 avatarReferenceMin;
        private Vector3 avatarReferenceMax;
        private Matrix4[][]? avatarReferenceBoneMatrices;
        private int[]? avatarReferenceParentIndices;

        // Animation playback
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
        private List<int> vbos = new List<int>();

        // Mesh geometry
        private Vector3[]? vertexData;
        private Vector3[]? normalData;
        private Vector3[]? normal2Data;
        private Vector4[]? colorData;
        private Vector2[]? uvData;
        private int[]? indiceData;

        // Matrices
        private Matrix4 modelMatrixData = Matrix4.Identity;
        private Matrix4 viewMatrixData = Matrix4.Identity;
        private Matrix4 projMatrixData = Matrix4.Identity;
        private Matrix4 initialViewMatrix = Matrix4.Identity;
        private Matrix4 initialModelMatrix = Matrix4.Identity;
        private float boneScale = 1.0f;
        private Vector3[]? staticBonePositions;
        private int[]? staticParentIndices;

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

        public float AvatarReferenceMeshDensityPercent
        {
            get => avatarReferenceMeshDensityPercent;
            set
            {
                var clamped = Math.Clamp(value, MinAvatarReferenceMeshDensityPercent, MaxAvatarReferenceMeshDensityPercent);
                if (Math.Abs(avatarReferenceMeshDensityPercent - clamped) < 0.01f)
                {
                    return;
                }

                avatarReferenceMeshDensityPercent = clamped;
                RebuildAvatarReferenceMeshPreview();
            }
        }

        // Mode flags
        private int wireFrameMode = 0;
        private int shadeMode = 0;
        private int normalMode = 0;
        private bool previewMaterialMode = false;
        private int previewTextureId = 0;

        // Interaction state
        private global::Avalonia.Point mpos;

        // Thread-safe texture loading helper
        private readonly object textureLock = new object();
        private byte[]? pendingTextureData;
        private int pendingTextureWidth;
        private int pendingTextureHeight;
        private bool hasPendingTexture;
        private List<byte[]?>? pendingSubMeshTextures;
        private List<int>? pendingSubMeshTexWidths;
        private List<int>? pendingSubMeshTexHeights;
        private bool hasPendingSubMeshTextures;
        private int[]? previewTextureIds;
        private Mesh? currentMesh;
        private int meshLoadCounter = 0;

        private double lastWidth;
        private double lastHeight;
        private bool isGles;

        public event Action<string>? GpuErrorOccurred;

        public MeshPreviewControl()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
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

        private static void ExpandBounds(Vector3 point, ref Vector3 min, ref Vector3 max)
        {
            if (!IsFinitePoint(point))
            {
                return;
            }

            min = Vector3.ComponentMin(min, point);
            max = Vector3.ComponentMax(max, point);
        }

        private static bool IsFinitePoint(Vector3 point)
        {
            return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
        }

        private static Matrix4 CreatePreviewModelMatrix(Vector3 min, Vector3 max)
        {
            var size = max - min;
            var center = (max + min) * 0.5f;
            float diagonal = Math.Max(1e-5f, size.Length);
            return Matrix4.CreateTranslation(-center) * Matrix4.CreateScale(PreviewFitScale / diagonal);
        }

        private sealed class AvatarReferenceMesh
        {
            public AvatarReferenceMesh(Vector3[] vertices, int[] indices, int[] sourceVertexIndices)
            {
                Vertices = vertices;
                Indices = indices;
                SourceVertexIndices = sourceVertexIndices;
            }

            public Vector3[] Vertices { get; }
            public int[] Indices { get; }
            public int[] SourceVertexIndices { get; }
        }

        private static AvatarReferenceMesh BuildAvatarReferenceMesh(Vector3[] sourceVertices, List<uint> sourceIndices, Vector3 min, Vector3 max, float densityPercent)
        {
            if (sourceVertices.Length == 0 || sourceIndices.Count < 3)
            {
                return new AvatarReferenceMesh(Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>());
            }

            int sourceTriangleCount = sourceIndices.Count / 3;
            densityPercent = Math.Clamp(densityPercent, MinAvatarReferenceMeshDensityPercent, MaxAvatarReferenceMeshDensityPercent);
            var triangleOffsets = densityPercent >= MaxAvatarReferenceMeshDensityPercent - 0.01f
                ? CollectAvatarReferenceTriangles(sourceVertices, sourceIndices)
                : SelectAvatarReferenceTriangles(sourceVertices, sourceIndices, min, max, sourceTriangleCount, densityPercent);

            if (triangleOffsets.Count == 0)
            {
                return new AvatarReferenceMesh(Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>());
            }

            return CompactAvatarReferenceMesh(sourceVertices, sourceIndices, triangleOffsets);
        }

        private static List<int> CollectAvatarReferenceTriangles(Vector3[] sourceVertices, List<uint> sourceIndices)
        {
            var triangleOffsets = new List<int>(sourceIndices.Count / 3);
            for (int offset = 0; offset + 2 < sourceIndices.Count; offset += 3)
            {
                if (TryReadAvatarTriangle(sourceIndices, offset, sourceVertices.Length, out _, out _, out _))
                {
                    triangleOffsets.Add(offset);
                }
            }

            return triangleOffsets;
        }

        private static List<int> SelectAvatarReferenceTriangles(Vector3[] sourceVertices, List<uint> sourceIndices, Vector3 min, Vector3 max, int sourceTriangleCount, float densityPercent)
        {
            int targetTriangleCount = Math.Clamp((int)Math.Ceiling(sourceTriangleCount * densityPercent / 100f), 1, sourceTriangleCount);
            int targetVertexCount = Math.Clamp((int)Math.Ceiling(sourceVertices.Length * densityPercent / 100f), 3, sourceVertices.Length);
            var selectedOffsets = new List<int>(targetTriangleCount * 2);
            var selectedSet = new HashSet<int>();
            var selectedSourceVertices = new HashSet<int>();
            var occupiedCells = new HashSet<long>();
            int gridResolution = Math.Clamp((int)Math.Ceiling(Math.Pow(targetTriangleCount, 1.0 / 3.0) * 2.25), 6, 28);
            var size = max - min;

            for (int offset = 0; selectedOffsets.Count < targetTriangleCount && offset + 2 < sourceIndices.Count; offset += 3)
            {
                if (!TryReadAvatarTriangle(sourceIndices, offset, sourceVertices.Length, out int i0, out int i1, out int i2))
                {
                    continue;
                }

                var centroid = (sourceVertices[i0] + sourceVertices[i1] + sourceVertices[i2]) / 3f;
                if (!IsFinitePoint(centroid))
                {
                    continue;
                }

                int cellX = QuantizeAvatarReferenceCell(centroid.X, min.X, size.X, gridResolution);
                int cellY = QuantizeAvatarReferenceCell(centroid.Y, min.Y, size.Y, gridResolution);
                int cellZ = QuantizeAvatarReferenceCell(centroid.Z, min.Z, size.Z, gridResolution);
                long cellKey = PackAvatarReferenceCellKey(cellX, cellY, cellZ);

                if (occupiedCells.Add(cellKey))
                {
                    TryAddAvatarReferenceTriangle(sourceVertices, sourceIndices, offset, selectedOffsets, selectedSet, selectedSourceVertices, targetVertexCount, selectedOffsets.Count == 0);
                }
            }

            if (selectedOffsets.Count < targetTriangleCount)
            {
                FillAvatarReferenceTriangleSample(sourceVertices, sourceIndices, targetTriangleCount, targetVertexCount, selectedOffsets, selectedSet, selectedSourceVertices);
            }

            return selectedOffsets;
        }

        private static bool TryReadAvatarTriangle(List<uint> sourceIndices, int offset, int vertexCount, out int i0, out int i1, out int i2)
        {
            i0 = i1 = i2 = 0;
            if (offset < 0 || offset + 2 >= sourceIndices.Count)
            {
                return false;
            }

            uint u0 = sourceIndices[offset];
            uint u1 = sourceIndices[offset + 1];
            uint u2 = sourceIndices[offset + 2];
            uint vertexLimit = (uint)vertexCount;
            if (u0 >= vertexLimit || u1 >= vertexLimit || u2 >= vertexLimit)
            {
                return false;
            }

            i0 = (int)u0;
            i1 = (int)u1;
            i2 = (int)u2;
            return i0 != i1 && i1 != i2 && i2 != i0;
        }

        private static int QuantizeAvatarReferenceCell(float value, float min, float extent, int resolution)
        {
            if (!float.IsFinite(value) || !float.IsFinite(min) || !float.IsFinite(extent) || extent <= 1e-5f)
            {
                return 0;
            }

            float normalized = (value - min) / extent;
            if (!float.IsFinite(normalized))
            {
                return 0;
            }

            return Math.Clamp((int)(normalized * resolution), 0, resolution - 1);
        }

        private static long PackAvatarReferenceCellKey(int x, int y, int z)
        {
            return ((long)x << 32) | ((long)y << 16) | (uint)z;
        }

        private static void FillAvatarReferenceTriangleSample(Vector3[] sourceVertices, List<uint> sourceIndices, int targetTriangleCount, int targetVertexCount, List<int> selectedOffsets, HashSet<int> selectedSet, HashSet<int> selectedSourceVertices)
        {
            int sourceTriangleCount = sourceIndices.Count / 3;
            double stride = Math.Max(1.0, (double)sourceTriangleCount / targetTriangleCount);

            for (int sample = 0; selectedOffsets.Count < targetTriangleCount && sample < targetTriangleCount; sample++)
            {
                int offset = Math.Min(sourceTriangleCount - 1, (int)Math.Floor(sample * stride)) * 3;
                TryAddAvatarReferenceTriangle(sourceVertices, sourceIndices, offset, selectedOffsets, selectedSet, selectedSourceVertices, targetVertexCount, selectedOffsets.Count == 0);
            }

            for (int offset = 0; selectedOffsets.Count < targetTriangleCount && offset + 2 < sourceIndices.Count; offset += 3)
            {
                TryAddAvatarReferenceTriangle(sourceVertices, sourceIndices, offset, selectedOffsets, selectedSet, selectedSourceVertices, targetVertexCount, selectedOffsets.Count == 0);
            }
        }

        private static void TryAddAvatarReferenceTriangle(Vector3[] sourceVertices, List<uint> sourceIndices, int offset, List<int> selectedOffsets, HashSet<int> selectedSet, HashSet<int> selectedSourceVertices, int targetVertexCount, bool allowOverflow)
        {
            if (selectedSet.Contains(offset)
                || !TryReadAvatarTriangle(sourceIndices, offset, sourceVertices.Length, out int i0, out int i1, out int i2))
            {
                return;
            }

            int newVertices = 0;
            if (!selectedSourceVertices.Contains(i0)) newVertices++;
            if (!selectedSourceVertices.Contains(i1)) newVertices++;
            if (!selectedSourceVertices.Contains(i2)) newVertices++;

            if (!allowOverflow && selectedSourceVertices.Count + newVertices > targetVertexCount)
            {
                return;
            }

            selectedOffsets.Add(offset);
            selectedSet.Add(offset);
            selectedSourceVertices.Add(i0);
            selectedSourceVertices.Add(i1);
            selectedSourceVertices.Add(i2);
        }

        private static AvatarReferenceMesh CompactAvatarReferenceMesh(Vector3[] sourceVertices, List<uint> sourceIndices, List<int> triangleOffsets)
        {
            var vertexRemap = new Dictionary<int, int>();
            var previewVertices = new List<Vector3>();
            var previewSourceIndices = new List<int>();
            var previewIndices = new List<int>(triangleOffsets.Count * 3);

            int GetPreviewVertexIndex(int sourceIndex)
            {
                if (vertexRemap.TryGetValue(sourceIndex, out int previewIndex))
                {
                    return previewIndex;
                }

                previewIndex = previewVertices.Count;
                vertexRemap[sourceIndex] = previewIndex;
                previewVertices.Add(sourceVertices[sourceIndex]);
                previewSourceIndices.Add(sourceIndex);
                return previewIndex;
            }

            foreach (var offset in triangleOffsets)
            {
                if (!TryReadAvatarTriangle(sourceIndices, offset, sourceVertices.Length, out int i0, out int i1, out int i2))
                {
                    continue;
                }

                previewIndices.Add(GetPreviewVertexIndex(i0));
                previewIndices.Add(GetPreviewVertexIndex(i1));
                previewIndices.Add(GetPreviewVertexIndex(i2));
            }

            return new AvatarReferenceMesh(previewVertices.ToArray(), previewIndices.ToArray(), previewSourceIndices.ToArray());
        }

        private static Vector3[][]? BuildAnimatedAvatarReferenceVertices(Mesh mesh, Vector3[] sourceVertexData, AvatarReferenceMesh referenceMesh, Matrix4[][] boneMatrices, int[] parentIndices, ref Vector3 min, ref Vector3 max, bool expandBounds)
        {
            if (referenceMesh.SourceVertexIndices.Length == 0
                || mesh.m_Skin == null
                || mesh.m_Skin.Length < sourceVertexData.Length
                || mesh.m_BindPose == null
                || mesh.m_BindPose.Length < parentIndices.Length
                || boneMatrices.Length == 0)
            {
                return null;
            }

            var localAnimatedMeshVerts = new Vector3[boneMatrices.Length][];
            for (int f = 0; f < boneMatrices.Length; f++)
            {
                var frameVerts = new Vector3[referenceMesh.SourceVertexIndices.Length];
                var frameMatrices = boneMatrices[f];
                var skinningMatrices = new Matrix4[frameMatrices.Length];

                for (int b = 0; b < frameMatrices.Length; b++)
                {
                    if (b < mesh.m_BindPose.Length)
                    {
                        var bp = mesh.m_BindPose[b];
                        var otkMat = new Matrix4(
                            bp.M00, bp.M01, bp.M02, bp.M03,
                            bp.M10, bp.M11, bp.M12, bp.M13,
                            bp.M20, bp.M21, bp.M22, bp.M23,
                            bp.M30, bp.M31, bp.M32, bp.M33
                        );
                        skinningMatrices[b] = otkMat * frameMatrices[b];
                    }
                    else
                    {
                        skinningMatrices[b] = Matrix4.Identity;
                    }
                }

                for (int v = 0; v < referenceMesh.SourceVertexIndices.Length; v++)
                {
                    int sourceVertexIndex = referenceMesh.SourceVertexIndices[v];
                    var sourceVertex = sourceVertexData[sourceVertexIndex];
                    var skin = mesh.m_Skin[sourceVertexIndex];
                    Vector3 skinnedPos = Vector3.Zero;
                    float totalWeight = 0f;

                    for (int j = 0; j < 4; j++)
                    {
                        float w = skin.weight[j];
                        if (w <= 0)
                        {
                            continue;
                        }

                        int bIdx = skin.boneIndex[j];
                        if (bIdx >= 0 && bIdx < skinningMatrices.Length)
                        {
                            var posB = Vector3.TransformPosition(sourceVertex, skinningMatrices[bIdx]);
                            skinnedPos += posB * w;
                            totalWeight += w;
                        }
                    }

                    frameVerts[v] = totalWeight > 0f ? skinnedPos / totalWeight : sourceVertex;
                    if (expandBounds)
                    {
                        ExpandBounds(frameVerts[v], ref min, ref max);
                    }
                }

                localAnimatedMeshVerts[f] = frameVerts;
            }

            return localAnimatedMeshVerts;
        }

        private void RebuildAvatarReferenceMeshPreview()
        {
            if (!isAvatarMode || avatarReferenceSourceVertices == null || avatarReferenceSourceIndices == null)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var sourceVertices = avatarReferenceSourceVertices;
            var sourceIndices = avatarReferenceSourceIndices;
            var sourceMesh = avatarReferenceMeshSource;
            var boneMatrices = avatarReferenceBoneMatrices;
            var parentIndices = avatarReferenceParentIndices;
            var min = avatarReferenceMin;
            var max = avatarReferenceMax;
            var densityPercent = avatarReferenceMeshDensityPercent;

            System.Threading.Tasks.Task.Run(() =>
            {
                var referenceMesh = BuildAvatarReferenceMesh(sourceVertices, sourceIndices, min, max, densityPercent);
                Vector3[][]? rebuiltAnimatedVertices = null;

                if (sourceMesh != null && boneMatrices != null && parentIndices != null)
                {
                    var ignoredMin = min;
                    var ignoredMax = max;
                    rebuiltAnimatedVertices = BuildAnimatedAvatarReferenceVertices(sourceMesh, sourceVertices, referenceMesh, boneMatrices, parentIndices, ref ignoredMin, ref ignoredMax, false);
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter || !isAvatarMode)
                    {
                        return;
                    }

                    vertexData = referenceMesh.Vertices;
                    indiceData = referenceMesh.Indices;
                    animatedMeshVertices = rebuiltAnimatedVertices;

                    lock (meshLock)
                    {
                        pendingMeshVertices = rebuiltAnimatedVertices != null && animCurrentFrame < rebuiltAnimatedVertices.Length
                            ? rebuiltAnimatedVertices[animCurrentFrame]
                            : null;
                    }

                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        private static string NormalizeBoneName(string name)
        {
            return name.Replace("\\", "/", StringComparison.Ordinal)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()
                ?.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant() ?? string.Empty;
        }

        private static int FindBoneIndex(string[]? boneNames, params string[] candidates)
        {
            if (boneNames == null)
            {
                return -1;
            }

            for (int c = 0; c < candidates.Length; c++)
            {
                string candidate = candidates[c].ToLowerInvariant();
                for (int i = 0; i < boneNames.Length; i++)
                {
                    if (NormalizeBoneName(boneNames[i]).Contains(candidate, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static bool TryGetBonePosition(Vector3[] bonePositions, string[]? boneNames, out Vector3 position, params string[] candidates)
        {
            int index = FindBoneIndex(boneNames, candidates);
            if (index >= 0 && index < bonePositions.Length)
            {
                position = bonePositions[index];
                return true;
            }

            position = Vector3.Zero;
            return false;
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            if (value.LengthSquared > 1e-8f)
            {
                normalized = Vector3.Normalize(value);
                return true;
            }

            normalized = Vector3.Zero;
            return false;
        }

        private static Matrix4 CreateAvatarInitialViewMatrix(Vector3[] bonePositions, string[]? boneNames)
        {
            if (TryGetBonePosition(bonePositions, boneNames, out var hips, "hips", "pelvis", "bip001pelvis")
                && TryGetBonePosition(bonePositions, boneNames, out var head, "head", "neck")
                && TryNormalize(head - hips, out var up))
            {
                Vector3 left = Vector3.Zero;
                Vector3 right = Vector3.Zero;
                bool hasLeft = TryGetBonePosition(bonePositions, boneNames, out left, "leftshoulder", "leftupperarm", "leftarm", "lefthand", "lshoulder", "lupperarm");
                bool hasRight = TryGetBonePosition(bonePositions, boneNames, out right, "rightshoulder", "rightupperarm", "rightarm", "righthand", "rshoulder", "rupperarm");

                if (hasLeft && hasRight && TryNormalize(right - left, out var side))
                {
                    var front = Vector3.Cross(side, up);
                    if (TryNormalize(front, out front))
                    {
                        var view = Matrix4.LookAt(front, Vector3.Zero, up);
                        view.Row3 = new Vector4(0, 0, 0, 1);
                        return view;
                    }
                }

                var fallbackFront = Math.Abs(Vector3.Dot(up, Vector3.UnitZ)) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
                var fallbackView = Matrix4.LookAt(fallbackFront, Vector3.Zero, up);
                fallbackView.Row3 = new Vector4(0, 0, 0, 1);
                return fallbackView;
            }

            return Matrix4.Identity;
        }

        private void ClearAvatarPreviewState()
        {
            isAvatarMode = false;
            StopAnimation();
            animatedMeshVertices = null;
            avatarReferenceMeshSource = null;
            avatarReferenceSourceVertices = null;
            avatarReferenceSourceIndices = null;
            avatarReferenceBoneMatrices = null;
            avatarReferenceParentIndices = null;
            boneLinesVertexCount = 0;
            jointPointsVertexCount = 0;
            animBoneLinesPerFrame = 0;
            animJointPointsPerFrame = 0;

            lock (skeletonLock)
            {
                pendingSkeletonVertices = null;
            }

            lock (meshLock)
            {
                pendingMeshVertices = null;
            }
        }

        public void SetMesh(Mesh m_Mesh, Vector2[]? uvs = null, List<byte[]?>? subMeshTextures = null, List<int>? subMeshTexWidths = null, List<int>? subMeshTexHeights = null)
        {
            ClearAvatarPreviewState();
            previewMaterialMode = false;
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0) return;

            int currentLoadId = ++meshLoadCounter;
            var m_Vertices = m_Mesh.m_Vertices;
            var m_VertexCount = m_Mesh.m_VertexCount;
            var m_Indices = m_Mesh.m_Indices;
            var m_Normals = m_Mesh.m_Normals;
            var m_Colors = m_Mesh.m_Colors;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (m_Vertices == null || m_Vertices.Length == 0) return;

                int count = 3;
                if (m_Vertices.Length == m_VertexCount * 4)
                {
                    count = 4;
                }
                var localVertexData = new Vector3[m_VertexCount];

                // Calculate Bounding
                Vector3 min = new Vector3(m_Vertices[0], m_Vertices[1], m_Vertices[2]);
                Vector3 max = min;
                for (int v = 0; v < m_VertexCount; v++)
                {
                    var vertex = new Vector3(
                        m_Vertices[v * count],
                        m_Vertices[v * count + 1],
                        m_Vertices[v * count + 2]);
                    localVertexData[v] = vertex;
                    ExpandBounds(vertex, ref min, ref max);
                }

                var localModelMatrixData = CreatePreviewModelMatrix(min, max);

                // Indices
                var localIndiceData = new int[m_Indices.Count];
                for (int i = 0; i < m_Indices.Count; i = i + 3)
                {
                    localIndiceData[i] = (int)m_Indices[i];
                    localIndiceData[i + 1] = (int)m_Indices[i + 1];
                    localIndiceData[i + 2] = (int)m_Indices[i + 2];
                }

                // Normals
                Vector3[]? localNormalData = null;
                if (m_Normals != null && m_Normals.Length > 0)
                {
                    int nCount = 3;
                    if (m_Normals.Length == m_VertexCount * 3)
                        nCount = 3;
                    else if (m_Normals.Length == m_VertexCount * 4)
                        nCount = 4;
                    localNormalData = new Vector3[m_VertexCount];
                    for (int n = 0; n < m_VertexCount; n++)
                    {
                        localNormalData[n] = new Vector3(
                            m_Normals[n * nCount],
                            m_Normals[n * nCount + 1],
                            m_Normals[n * nCount + 2]);
                    }
                }

                // calculate normal by ourselves
                var localNormal2Data = new Vector3[m_VertexCount];
                int[] normalCalculatedCount = new int[m_VertexCount];
                for (int i = 0; i < m_VertexCount; i++)
                {
                    localNormal2Data[i] = Vector3.Zero;
                    normalCalculatedCount[i] = 0;
                }
                for (int i = 0; i < m_Indices.Count; i = i + 3)
                {
                    if (localIndiceData[i + 2] >= m_VertexCount) continue;
                    Vector3 dir1 = localVertexData[localIndiceData[i + 1]] - localVertexData[localIndiceData[i]];
                    Vector3 dir2 = localVertexData[localIndiceData[i + 2]] - localVertexData[localIndiceData[i]];
                    Vector3 normal = Vector3.Cross(dir1, dir2);
                    if (normal.LengthSquared > 0)
                        normal = Vector3.Normalize(normal);
                    for (int j = 0; j < 3; j++)
                    {
                        localNormal2Data[localIndiceData[i + j]] += normal;
                        normalCalculatedCount[localIndiceData[i + j]]++;
                    }
                }
                for (int i = 0; i < m_VertexCount; i++)
                {
                    if (normalCalculatedCount[i] == 0)
                        localNormal2Data[i] = new Vector3(0, 1, 0);
                    else
                        localNormal2Data[i] = Vector3.Normalize(localNormal2Data[i]);
                }

                // Colors
                Vector4[] localColorData;
                if (m_Colors != null && m_Colors.Length == m_VertexCount * 3)
                {
                    localColorData = new Vector4[m_VertexCount];
                    for (int c = 0; c < m_VertexCount; c++)
                    {
                        localColorData[c] = new Vector4(
                            m_Colors[c * 3],
                            m_Colors[c * 3 + 1],
                            m_Colors[c * 3 + 2],
                            1.0f);
                    }
                }
                else if (m_Colors != null && m_Colors.Length == m_VertexCount * 4)
                {
                    localColorData = new Vector4[m_VertexCount];
                    for (int c = 0; c < m_VertexCount; c++)
                    {
                        localColorData[c] = new Vector4(
                            m_Colors[c * 4],
                            m_Colors[c * 4 + 1],
                            m_Colors[c * 4 + 2],
                            m_Colors[c * 4 + 3]);
                    }
                }
                else
                {
                    localColorData = new Vector4[m_VertexCount];
                    for (int c = 0; c < m_VertexCount; c++)
                    {
                        localColorData[c] = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
                    }
                }

                Vector2[]? localUvData = uvs;
                if (localUvData == null && m_Mesh.m_UV0 != null && m_Mesh.m_UV0.Length >= m_VertexCount * 2)
                {
                    localUvData = new Vector2[m_VertexCount];
                    for (int i = 0; i < m_VertexCount; i++)
                    {
                        localUvData[i] = new Vector2(m_Mesh.m_UV0[i * 2], m_Mesh.m_UV0[i * 2 + 1]);
                    }
                }

                if (subMeshTextures != null && subMeshTextures.Any(t => t != null))
                {
                    lock (textureLock)
                    {
                        pendingSubMeshTextures = subMeshTextures;
                        pendingSubMeshTexWidths = subMeshTexWidths;
                        pendingSubMeshTexHeights = subMeshTexHeights;
                        hasPendingSubMeshTextures = true;
                    }
                }

                // Post back to UI thread
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter) return;

                    viewMatrixData = Matrix4.CreateRotationY(-(float)Math.PI / 4) * Matrix4.CreateRotationX(-(float)Math.PI / 6);
                    vertexData = localVertexData;
                    modelMatrixData = localModelMatrixData;
                    initialViewMatrix = viewMatrixData;
                    initialModelMatrix = modelMatrixData;
                    indiceData = localIndiceData;
                    normalData = localNormalData;
                    normal2Data = localNormal2Data;
                    colorData = localColorData;
                    uvData = localUvData;
                    currentMesh = m_Mesh; // Keep reference for submesh drawing
                    if (subMeshTextures != null && subMeshTextures.Any(t => t != null))
                    {
                        previewMaterialMode = true;
                    }
                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        public void SetMaterialTexture(Image<Bgra32> image)
        {
            ++meshLoadCounter;
            ClearAvatarPreviewState();

            lock (textureLock)
            {
                pendingTextureWidth = image.Width;
                pendingTextureHeight = image.Height;
                pendingTextureData = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pendingTextureData);
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

        private void GenerateSphere(int latitudes, int longitudes)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();

            for (int lat = 0; lat <= latitudes; lat++)
            {
                float theta = lat * (float)Math.PI / latitudes;
                float sinTheta = (float)Math.Sin(theta);
                float cosTheta = (float)Math.Cos(theta);

                for (int lon = 0; lon <= longitudes; lon++)
                {
                    float phi = lon * 2 * (float)Math.PI / longitudes;
                    float sinPhi = (float)Math.Sin(phi);
                    float cosPhi = (float)Math.Cos(phi);

                    Vector3 normal = new Vector3(cosPhi * sinTheta, cosTheta, sinPhi * sinTheta);
                    vertices.Add(normal);
                    normals.Add(normal);
                    uvs.Add(new Vector2((float)lon / longitudes, 1f - (float)lat / latitudes));
                }
            }

            for (int lat = 0; lat < latitudes; lat++)
            {
                for (int lon = 0; lon < longitudes; lon++)
                {
                    int first = (lat * (longitudes + 1)) + lon;
                    int second = first + longitudes + 1;

                    indices.Add(first);
                    indices.Add(second);
                    indices.Add(first + 1);

                    indices.Add(second);
                    indices.Add(second + 1);
                    indices.Add(first + 1);
                }
            }

            vertexData = vertices.ToArray();
            normal2Data = normals.ToArray();
            normalData = normals.ToArray();
            uvData = uvs.ToArray();
            indiceData = indices.ToArray();

            colorData = new Vector4[vertexData.Length];
            for (int i = 0; i < vertexData.Length; i++) colorData[i] = new Vector4(1, 1, 1, 1);

            viewMatrixData = Matrix4.CreateRotationY(-(float)Math.PI / 4) * Matrix4.CreateRotationX(-(float)Math.PI / 6);
            modelMatrixData = Matrix4.CreateScale(0.8f);
            initialViewMatrix = viewMatrixData;
            initialModelMatrix = modelMatrixData;
        }

        protected override void OnOpenGlInit(GlInterface gl)
        {
            try
            {
                GL.LoadBindings(new AvaloniaBindingsContext(gl));
                
                var version = gl.ContextInfo?.Version;
                isGles = version.HasValue && version.Value.Type == GlProfileType.OpenGLES;
                string glslVersion = isGles ? "#version 300 es" : "#version 330 core";
                string precision = isGles ? "precision mediump float;" : "";

                GL.ClearColor(0.3f, 0.4f, 0.5f, 1.0f);

                pgmID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSource}", ShaderType.VertexShader, pgmID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsSource}", ShaderType.FragmentShader, pgmID, out _);
                GL.LinkProgram(pgmID);

                GL.GetProgram(pgmID, GetProgramParameterName.LinkStatus, out int status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmID);
                    throw new Exception($"Shader link error (pgmID): {log}");
                }

                pgmColorID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSource}", ShaderType.VertexShader, pgmColorID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsColorSource}", ShaderType.FragmentShader, pgmColorID, out _);
                GL.LinkProgram(pgmColorID);

                GL.GetProgram(pgmColorID, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmColorID);
                    throw new Exception($"Shader link error (pgmColorID): {log}");
                }

                pgmBlackID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSource}", ShaderType.VertexShader, pgmBlackID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsBlackSource}", ShaderType.FragmentShader, pgmBlackID, out _);
                GL.LinkProgram(pgmBlackID);

                GL.GetProgram(pgmBlackID, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmBlackID);
                    throw new Exception($"Shader link error (pgmBlackID): {log}");
                }

                pgmTexID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsTexSource}", ShaderType.VertexShader, pgmTexID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsTexSource}", ShaderType.FragmentShader, pgmTexID, out _);
                GL.LinkProgram(pgmTexID);

                GL.GetProgram(pgmTexID, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmTexID);
                    throw new Exception($"Shader link error (pgmTexID): {log}");
                }

                uniformModelMatrix = GL.GetUniformLocation(pgmID, "modelMatrix");
                uniformViewMatrix = GL.GetUniformLocation(pgmID, "viewMatrix");
                uniformProjMatrix = GL.GetUniformLocation(pgmID, "projMatrix");

                uniformModelMatrixColor = GL.GetUniformLocation(pgmColorID, "modelMatrix");
                uniformViewMatrixColor = GL.GetUniformLocation(pgmColorID, "viewMatrix");
                uniformProjMatrixColor = GL.GetUniformLocation(pgmColorID, "projMatrix");

                uniformModelMatrixBlack = GL.GetUniformLocation(pgmBlackID, "modelMatrix");
                uniformViewMatrixBlack = GL.GetUniformLocation(pgmBlackID, "viewMatrix");
                uniformProjMatrixBlack = GL.GetUniformLocation(pgmBlackID, "projMatrix");

                uniformTexture = GL.GetUniformLocation(pgmTexID, "mainTex");
                uniformModelMatrixTex = GL.GetUniformLocation(pgmTexID, "modelMatrix");
                uniformViewMatrixTex = GL.GetUniformLocation(pgmTexID, "viewMatrix");
                uniformProjMatrixTex = GL.GetUniformLocation(pgmTexID, "projMatrix");

                pgmGreenID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSkeletonSource}", ShaderType.VertexShader, pgmGreenID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsGreenSource}", ShaderType.FragmentShader, pgmGreenID, out _);
                GL.LinkProgram(pgmGreenID);
                GL.GetProgram(pgmGreenID, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmGreenID);
                    throw new Exception($"Shader link error (pgmGreenID): {log}");
                }

                uniformModelMatrixGreen = GL.GetUniformLocation(pgmGreenID, "modelMatrix");
                uniformViewMatrixGreen = GL.GetUniformLocation(pgmGreenID, "viewMatrix");
                uniformProjMatrixGreen = GL.GetUniformLocation(pgmGreenID, "projMatrix");

                pgmYellowID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSkeletonSource}", ShaderType.VertexShader, pgmYellowID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsYellowSource}", ShaderType.FragmentShader, pgmYellowID, out _);
                GL.LinkProgram(pgmYellowID);
                GL.GetProgram(pgmYellowID, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmYellowID);
                    throw new Exception($"Shader link error (pgmYellowID): {log}");
                }

                uniformModelMatrixYellow = GL.GetUniformLocation(pgmYellowID, "modelMatrix");
                uniformViewMatrixYellow = GL.GetUniformLocation(pgmYellowID, "viewMatrix");
                uniformProjMatrixYellow = GL.GetUniformLocation(pgmYellowID, "projMatrix");

                pgmRedID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSkeletonSource}", ShaderType.VertexShader, pgmRedID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsRedSource}", ShaderType.FragmentShader, pgmRedID, out _);
                GL.LinkProgram(pgmRedID);
                GL.GetProgram(pgmRedID, GetProgramParameterName.LinkStatus, out status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmRedID);
                    throw new Exception($"Shader link error (pgmRedID): {log}");
                }

                uniformModelMatrixRed = GL.GetUniformLocation(pgmRedID, "modelMatrix");
                uniformViewMatrixRed = GL.GetUniformLocation(pgmRedID, "viewMatrix");
                uniformProjMatrixRed = GL.GetUniformLocation(pgmRedID, "projMatrix");
            }
            catch (Exception ex)
            {
                GpuErrorOccurred?.Invoke($"OpenGL initialization failed: {ex.Message}");
            }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            try
            {
                CleanupBuffers();
                if (pgmID != 0) GL.DeleteProgram(pgmID);
                if (pgmColorID != 0) GL.DeleteProgram(pgmColorID);
                if (pgmBlackID != 0) GL.DeleteProgram(pgmBlackID);
                if (pgmTexID != 0) GL.DeleteProgram(pgmTexID);
                if (pgmGreenID != 0) GL.DeleteProgram(pgmGreenID);
                if (pgmYellowID != 0) GL.DeleteProgram(pgmYellowID);
                if (pgmRedID != 0) GL.DeleteProgram(pgmRedID);
                if (previewTextureId != 0) GL.DeleteTexture(previewTextureId);
                if (previewTextureIds != null)
                {
                    foreach (var id in previewTextureIds)
                    {
                        if (id != 0) GL.DeleteTexture(id);
                    }
                    previewTextureIds = null;
                }
            }
            catch
            {
            }
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                if (vertexData == null || indiceData == null)
                {
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    return;
                }

                bool updateTex = false;
                byte[]? texBytes = null;
                int texW = 0, texH = 0;
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
                        texBytes = pendingTextureData;
                        texW = pendingTextureWidth;
                        texH = pendingTextureHeight;
                        hasPendingTexture = false;
                        subTexBytes = new List<byte[]?> { pendingTextureData };
                        subTexW = new List<int> { pendingTextureWidth };
                        subTexH = new List<int> { pendingTextureHeight };
                        pendingTextureData = null;
                    }
                }

                if (updateTex && subTexBytes != null)
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
                            if (id != 0) GL.DeleteTexture(id);
                        }
                    }
                    
                    previewTextureIds = new int[subTexBytes.Count];
                    for (int i = 0; i < subTexBytes.Count; i++)
                    {
                        var tBytes = subTexBytes[i];
                        if (tBytes == null) continue;

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
                }

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
                    if (!updateTex)
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
                                if (id != 0) GL.DeleteTexture(id);
                            }
                            previewTextureIds = null;
                        }
                    }
                    CreateVAO();
                }

                if (isAvatarMode)
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

                    if (vaoWireframe != 0)
                    {
                        GL.UseProgram(pgmGreenID);
                        GL.UniformMatrix4(uniformModelMatrixGreen, false, ref modelMatrixData);
                        GL.UniformMatrix4(uniformViewMatrixGreen, false, ref viewMatrixData);
                        GL.UniformMatrix4(uniformProjMatrixGreen, false, ref projMatrixData);
                        GL.BindVertexArray(vaoWireframe);
                        int lineCount = indiceData != null ? (indiceData.Length / 3) * 6 : 0;
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
                    return;
                }

                GL.BindVertexArray(vao);

                if (wireFrameMode == 0 || wireFrameMode == 2)
                {
                    if (!isGles)
                    {
                        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                    }

                    var localTextureIds = previewTextureIds;
                    if (previewMaterialMode && localTextureIds != null && currentMesh != null && currentMesh.m_SubMeshes != null && currentMesh.m_SubMeshes.Length > 0 && !isAvatarMode)
                    {
                        int flatOffsetElements = 0;
                        for (int i = 0; i < currentMesh.m_SubMeshes.Length; i++)
                        {
                            var subMesh = currentMesh.m_SubMeshes[i];
                            int texIndex = i < localTextureIds.Length ? i : 0;
                            int texId = texIndex < localTextureIds.Length ? localTextureIds[texIndex] : 0;

                            if (texId != 0)
                            {
                                GL.UseProgram(pgmTexID);
                                GL.ActiveTexture(TextureUnit.Texture0);
                                GL.BindTexture(TextureTarget.Texture2D, texId);
                                GL.Uniform1(uniformTexture, 0);
                                GL.UniformMatrix4(uniformModelMatrixTex, false, ref modelMatrixData);
                                GL.UniformMatrix4(uniformViewMatrixTex, false, ref viewMatrixData);
                                GL.UniformMatrix4(uniformProjMatrixTex, false, ref projMatrixData);
                            }
                            else
                            {
                                if (shadeMode == 0)
                                {
                                    GL.UseProgram(pgmID);
                                    GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                                    GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                                    GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
                                }
                                else
                                {
                                    GL.UseProgram(pgmColorID);
                                    GL.UniformMatrix4(uniformModelMatrixColor, false, ref modelMatrixData);
                                    GL.UniformMatrix4(uniformViewMatrixColor, false, ref viewMatrixData);
                                    GL.UniformMatrix4(uniformProjMatrixColor, false, ref projMatrixData);
                                }
                            }

                            GL.DrawElements(PrimitiveType.Triangles, (int)subMesh.indexCount, DrawElementsType.UnsignedInt, (IntPtr)(flatOffsetElements * 4));
                            flatOffsetElements += (int)subMesh.indexCount;
                        }
                    }
                    else
                    {
                        if (previewMaterialMode && localTextureIds != null && localTextureIds.Length > 0 && localTextureIds[0] != 0)
                        {
                            GL.UseProgram(pgmTexID);
                            GL.ActiveTexture(TextureUnit.Texture0);
                            GL.BindTexture(TextureTarget.Texture2D, localTextureIds[0]);
                            GL.Uniform1(uniformTexture, 0);
                            GL.UniformMatrix4(uniformModelMatrixTex, false, ref modelMatrixData);
                            GL.UniformMatrix4(uniformViewMatrixTex, false, ref viewMatrixData);
                            GL.UniformMatrix4(uniformProjMatrixTex, false, ref projMatrixData);
                        }
                        else
                        {
                            if (shadeMode == 0)
                            {
                                GL.UseProgram(pgmID);
                                GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                                GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                                GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
                            }
                            else
                            {
                                GL.UseProgram(pgmColorID);
                                GL.UniformMatrix4(uniformModelMatrixColor, false, ref modelMatrixData);
                                GL.UniformMatrix4(uniformViewMatrixColor, false, ref viewMatrixData);
                                GL.UniformMatrix4(uniformProjMatrixColor, false, ref projMatrixData);
                            }
                        }
                        GL.DrawElements(PrimitiveType.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
                    }
                }

                if (wireFrameMode == 1 || wireFrameMode == 2)
                {
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
                    int lineCount = indiceData != null ? (indiceData.Length / 3) * 6 : 0;
                    if (lineCount > 0)
                    {
                        GL.DrawElements(BeginMode.Lines, lineCount, DrawElementsType.UnsignedInt, 0);
                    }
                    if (!isGles)
                    {
                        GL.Disable(EnableCap.PolygonOffsetLine);
                    }
                }

                GL.BindVertexArray(0);
                GL.Flush();
            }
            catch (Exception ex)
            {
                GpuErrorOccurred?.Invoke($"OpenGL rendering failed: {ex.Message}");
            }
        }

        private void ChangeGLSize(double width, double height)
        {
            GL.Viewport(0, 0, (int)Math.Max(1, width), (int)Math.Max(1, height));

            if (width <= height)
            {
                float k = (float)(width / (height == 0 ? 1 : height));
                projMatrixData = Matrix4.CreateScale(1, k, 1);
            }
            else
            {
                float k = (float)(height / (width == 0 ? 1 : width));
                projMatrixData = Matrix4.CreateScale(k, 1, 1);
            }
        }

        private void CreateVBO(out int vboAddress, Vector2[]? data, int address)
        {
            if (address < 0 || data == null) { vboAddress = 0; return; }
            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 8), data, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 2, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private void CreateVBO(out int vboAddress, Vector3[]? data, int address, BufferUsageHint usage = BufferUsageHint.StaticDraw)
        {
            if (address < 0 || data == null) { vboAddress = 0; return; }
            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 12), data, usage);
            GL.VertexAttribPointer(address, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private void CreateVBO(out int vboAddress, Vector4[]? data, int address)
        {
            if (address < 0 || data == null) { vboAddress = 0; return; }
            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 16), data, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 4, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private void CreateEBO(out int address, int[]? data)
        {
            if (data == null) { address = 0; return; }
            GL.GenBuffers(1, out address);
            vbos.Add(address);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, address);
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(data.Length * sizeof(int)), data, BufferUsageHint.StaticDraw);
        }

        private void BindAttribsAndEBO(bool isWireframe)
        {
            if (previewMaterialMode && uvData != null)
            {
                if (vboPosition == 0)
                {
                    CreateVBO(out vboPosition, vertexData, LocationPosition, isAvatarMode ? BufferUsageHint.DynamicDraw : BufferUsageHint.StaticDraw);
                }
                else
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vboPosition);
                    GL.VertexAttribPointer(LocationPosition, 3, VertexAttribPointerType.Float, false, 0, 0);
                    GL.EnableVertexAttribArray(LocationPosition);
                }

                if (normalMode == 0)
                {
                    CreateVBO(out _, normal2Data, LocationNormal);
                }
                else
                {
                    if (normalData != null)
                        CreateVBO(out _, normalData, LocationNormal);
                }
                CreateVBO(out _, uvData, LocationTexCoord);
            }
            else
            {
                if (vboPosition == 0)
                {
                    CreateVBO(out vboPosition, vertexData, LocationPosition, isAvatarMode ? BufferUsageHint.DynamicDraw : BufferUsageHint.StaticDraw);
                }
                else
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vboPosition);
                    GL.VertexAttribPointer(LocationPosition, 3, VertexAttribPointerType.Float, false, 0, 0);
                    GL.EnableVertexAttribArray(LocationPosition);
                }

                if (normalMode == 0)
                {
                    CreateVBO(out _, normal2Data, LocationNormal);
                }
                else
                {
                    if (normalData != null)
                        CreateVBO(out _, normalData, LocationNormal);
                }
                CreateVBO(out _, colorData, LocationColor);
            }

            if (isWireframe)
            {
                if (indiceData != null)
                {
                    var lineIndices = new List<int>();
                    for (int i = 0; i < indiceData.Length; i += 3)
                    {
                        if (i + 2 < indiceData.Length)
                        {
                            int i0 = indiceData[i];
                            int i1 = indiceData[i + 1];
                            int i2 = indiceData[i + 2];
                            lineIndices.Add(i0); lineIndices.Add(i1);
                            lineIndices.Add(i1); lineIndices.Add(i2);
                            lineIndices.Add(i2); lineIndices.Add(i0);
                        }
                    }
                    CreateEBO(out _, lineIndices.ToArray());
                }
            }
            else
            {
                CreateEBO(out _, indiceData);
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        private void CreateVAO()
        {
            CleanupBuffers(isAvatarMode);

            GL.GenVertexArrays(1, out vao);
            GL.BindVertexArray(vao);
            BindAttribsAndEBO(false);
            GL.BindVertexArray(0);

            GL.GenVertexArrays(1, out vaoWireframe);
            GL.BindVertexArray(vaoWireframe);
            BindAttribsAndEBO(true);
            GL.BindVertexArray(0);

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
                CreateSkeletonVAO(localSkeletonVerts);
            }
        }

        private void CleanupBuffers(bool keepSkeleton = false)
        {
            if (vao != 0)
            {
                GL.DeleteVertexArrays(1, ref vao);
                vao = 0;
            }
            if (vaoWireframe != 0)
            {
                GL.DeleteVertexArrays(1, ref vaoWireframe);
                vaoWireframe = 0;
            }
            if (!keepSkeleton)
            {
                if (vaoSkeleton != 0)
                {
                    GL.DeleteVertexArrays(1, ref vaoSkeleton);
                    vaoSkeleton = 0;
                }
                if (vboSkeleton != 0)
                {
                    GL.DeleteBuffer(vboSkeleton);
                    vbos.Remove(vboSkeleton);
                    vboSkeleton = 0;
                }
            }
            if (vbos.Count > 0)
            {
                if (keepSkeleton && vboSkeleton != 0)
                {
                    var toDelete = vbos.Where(x => x != vboSkeleton).ToArray();
                    if (toDelete.Length > 0)
                    {
                        GL.DeleteBuffers(toDelete.Length, toDelete);
                        foreach (var id in toDelete)
                        {
                            vbos.Remove(id);
                        }
                    }
                }
                else
                {
                    int[] arr = vbos.ToArray();
                    GL.DeleteBuffers(arr.Length, arr);
                    vbos.Clear();
                }
            }
            vboPosition = 0;
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
                    if (uvData != null && previewTextureId != 0)
                    {
                        previewMaterialMode = !previewMaterialMode;
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

        private void LoadShaderFromString(string source, ShaderType type, int program, out int shaderId)
        {
            shaderId = GL.CreateShader(type);
            GL.ShaderSource(shaderId, source);
            GL.CompileShader(shaderId);
            
            GL.GetShader(shaderId, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
            {
                string log = GL.GetShaderInfoLog(shaderId);
                throw new Exception($"GL Shader compile error ({type}): {log}");
            }
            
            GL.AttachShader(program, shaderId);
            GL.DeleteShader(shaderId);
        }

        private void CreateSkeletonVAO(Vector3[] vertices)
        {
            if (vaoSkeleton != 0)
            {
                GL.DeleteVertexArrays(1, ref vaoSkeleton);
                vaoSkeleton = 0;
            }
            if (vboSkeleton != 0)
            {
                GL.DeleteBuffer(vboSkeleton);
                vbos.Remove(vboSkeleton);
                vboSkeleton = 0;
            }

            if (vertices.Length == 0) return;

            GL.GenVertexArrays(1, out vaoSkeleton);
            GL.BindVertexArray(vaoSkeleton);

            GL.GenBuffers(1, out vboSkeleton);
            vbos.Add(vboSkeleton);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboSkeleton);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(vertices.Length * 12), vertices, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(LocationPosition, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(LocationPosition);

            GL.BindVertexArray(0);
        }

        public void SetAvatar(Mesh m_Mesh, Vector3[] bonePositions, int[] parentIndices, string[]? boneNames = null)
        {
            isAvatarMode = true;
            StopAnimation();
            previewMaterialMode = false;
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0) return;

            staticBonePositions = bonePositions;
            staticParentIndices = parentIndices;

            int currentLoadId = ++meshLoadCounter;
            var avatarBonePositions = bonePositions;
            var m_Vertices = m_Mesh.m_Vertices;
            var m_VertexCount = m_Mesh.m_VertexCount;
            var m_Indices = m_Mesh.m_Indices;
            var m_Normals = m_Mesh.m_Normals;
            var m_Colors = m_Mesh.m_Colors;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (m_Vertices == null || m_Vertices.Length == 0) return;

                int count = 3;
                if (m_Vertices.Length == m_VertexCount * 4)
                {
                    count = 4;
                }
                var localVertexData = new Vector3[m_VertexCount];

                Vector3 min = new Vector3(m_Vertices[0], m_Vertices[1], m_Vertices[2]);
                Vector3 max = min;
                for (int v = 0; v < m_VertexCount; v++)
                {
                    var vertex = new Vector3(
                        m_Vertices[v * count],
                        m_Vertices[v * count + 1],
                        m_Vertices[v * count + 2]);
                    localVertexData[v] = vertex;
                    ExpandBounds(vertex, ref min, ref max);
                }

                var sourceMeshMin = min;
                var sourceMeshMax = max;
                var referenceMesh = BuildAvatarReferenceMesh(localVertexData, m_Indices, sourceMeshMin, sourceMeshMax, avatarReferenceMeshDensityPercent);
                var localIndiceData = referenceMesh.Indices;

                var skeletonVerts = new List<Vector3>();
                int boneLinesCount = 0;
                int jointPointsCount = 0;

                if (bonePositions != null && parentIndices != null)
                {
                    var normalizedBones = bonePositions;

                    for (int i = 0; i < normalizedBones.Length; i++)
                    {
                        ExpandBounds(normalizedBones[i], ref min, ref max);
                    }

                    var verts = BuildSkeletonVertices(normalizedBones, parentIndices, out boneLinesCount, out jointPointsCount);
                    skeletonVerts.AddRange(verts);
                }

                var localModelMatrixData = CreatePreviewModelMatrix(min, max);

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter) return;

                    viewMatrixData = CreateAvatarInitialViewMatrix(avatarBonePositions, boneNames);
                    avatarReferenceMeshSource = m_Mesh;
                    avatarReferenceSourceVertices = localVertexData;
                    avatarReferenceSourceIndices = m_Indices;
                    avatarReferenceMin = sourceMeshMin;
                    avatarReferenceMax = sourceMeshMax;
                    avatarReferenceBoneMatrices = null;
                    avatarReferenceParentIndices = null;
                    vertexData = referenceMesh.Vertices;
                    modelMatrixData = localModelMatrixData;
                    initialViewMatrix = viewMatrixData;
                    initialModelMatrix = modelMatrixData;
                    indiceData = localIndiceData;
                    normalData = null;
                    normal2Data = null;
                    colorData = null;
                    uvData = null;

                    boneLinesVertexCount = boneLinesCount;
                    jointPointsVertexCount = jointPointsCount;

                    lock (skeletonLock)
                    {
                        pendingSkeletonVertices = skeletonVerts.ToArray();
                    }
                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        public void SetAnimatedAvatar(Mesh m_Mesh, Vector3[][] frames, Matrix4[][] boneMatrices, int[] parentIndices, float fps, string[]? boneNames = null)
        {
            isAvatarMode = true;
            StopAnimation();
            previewMaterialMode = false;
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0 || frames.Length == 0) return;

            animationFrames = frames;
            animParentIndices = parentIndices;
            animCurrentFrame = 0;
            animFps = fps > 0 ? fps : 30f;

            int currentLoadId = ++meshLoadCounter;
            var m_Vertices = m_Mesh.m_Vertices;
            var m_VertexCount = m_Mesh.m_VertexCount;
            var m_Indices = m_Mesh.m_Indices;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (m_Vertices == null || m_Vertices.Length == 0) return;

                int count = 3;
                if (m_Vertices.Length == m_VertexCount * 4) count = 4;
                var sourceVertexData = new Vector3[m_VertexCount];

                Vector3 min = new Vector3(m_Vertices[0], m_Vertices[1], m_Vertices[2]);
                Vector3 max = min;
                for (int v = 0; v < m_VertexCount; v++)
                {
                    var vertex = new Vector3(
                        m_Vertices[v * count],
                        m_Vertices[v * count + 1],
                        m_Vertices[v * count + 2]);
                    sourceVertexData[v] = vertex;
                    ExpandBounds(vertex, ref min, ref max);
                }

                var sourceMeshMin = min;
                var sourceMeshMax = max;
                var referenceMesh = BuildAvatarReferenceMesh(sourceVertexData, m_Indices, sourceMeshMin, sourceMeshMax, avatarReferenceMeshDensityPercent);
                var localVertexData = referenceMesh.Vertices;
                var localIndiceData = referenceMesh.Indices;

                // Compute skinned mesh vertices for all frames of the animation
                Vector3[][]? localAnimatedMeshVerts = BuildAnimatedAvatarReferenceVertices(m_Mesh, sourceVertexData, referenceMesh, boneMatrices, parentIndices, ref min, ref max, true);

                var firstFrameBones = frames[0];
                foreach (var frame in frames)
                {
                    foreach (var bonePosition in frame)
                    {
                        ExpandBounds(bonePosition, ref min, ref max);
                    }
                }
                var skeletonVerts = BuildSkeletonVertices(firstFrameBones, parentIndices, out int boneLines, out int jointPoints);
                var localModelMatrixData = CreatePreviewModelMatrix(min, max);

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter) return;

                    viewMatrixData = CreateAvatarInitialViewMatrix(frames[0], boneNames);
                    avatarReferenceMeshSource = m_Mesh;
                    avatarReferenceSourceVertices = sourceVertexData;
                    avatarReferenceSourceIndices = m_Indices;
                    avatarReferenceMin = sourceMeshMin;
                    avatarReferenceMax = sourceMeshMax;
                    avatarReferenceBoneMatrices = boneMatrices;
                    avatarReferenceParentIndices = parentIndices;
                    vertexData = localVertexData;
                    modelMatrixData = localModelMatrixData;
                    initialViewMatrix = viewMatrixData;
                    initialModelMatrix = modelMatrixData;
                    indiceData = localIndiceData;
                    normalData = null;
                    normal2Data = null;
                    colorData = null;
                    uvData = null;

                    animBoneLinesPerFrame = boneLines;
                    animJointPointsPerFrame = jointPoints;
                    boneLinesVertexCount = boneLines;
                    jointPointsVertexCount = jointPoints;

                    animatedMeshVertices = localAnimatedMeshVerts;

                    lock (skeletonLock)
                    {
                        pendingSkeletonVertices = skeletonVerts;
                    }
                    vao = 0;
                    RequestNextFrameRendering();

                    // Start playback timer
                    animTimer = new global::Avalonia.Threading.DispatcherTimer();
                    animTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / animFps);
                    animTimer.Tick += OnAnimationTick;
                    animTimer.Start();
                    isAnimPlaying = true;
                });
            });
        }

        public bool IsPlaying => isAnimPlaying;

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

        public void PlayAnimation()
        {
            if (animationFrames == null || animTimer == null) return;
            if (!isAnimPlaying)
            {
                animTimer.Start();
                isAnimPlaying = true;
            }
        }

        public void PauseAnimation()
        {
            if (animTimer != null && isAnimPlaying)
            {
                animTimer.Stop();
                isAnimPlaying = false;
            }
        }

        public void RestartAnimation()
        {
            if (animationFrames == null) return;
            animCurrentFrame = 0;
            if (animTimer != null)
            {
                if (!isAnimPlaying)
                {
                    animTimer.Start();
                    isAnimPlaying = true;
                }
            }
            OnAnimationTick(null, EventArgs.Empty);
        }

        public void StopAnimation()
        {
            if (animTimer != null)
            {
                animTimer.Stop();
                animTimer.Tick -= OnAnimationTick;
                animTimer = null;
            }
            animationFrames = null;
            animParentIndices = null;
            animCurrentFrame = 0;
            isAnimPlaying = false;
            animatedMeshVertices = null;
            staticBonePositions = null;
            staticParentIndices = null;
            avatarReferenceBoneMatrices = null;
            avatarReferenceParentIndices = null;

            lock (meshLock)
            {
                pendingMeshVertices = null;
            }

            lock (skeletonLock)
            {
                pendingSkeletonVertices = null;
            }
        }

        public int AnimCurrentFrame => animCurrentFrame;
        public int AnimTotalFrames => animationFrames?.Length ?? 0;
        public float AnimFps => animFps;

        public event Action<int, int>? AnimationFrameChanged;

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (animationFrames == null || animParentIndices == null || animationFrames.Length == 0) return;

            animCurrentFrame = (animCurrentFrame + 1) % animationFrames.Length;
            var bonePositions = animationFrames[animCurrentFrame];
            var skeletonVerts = BuildSkeletonVertices(bonePositions, animParentIndices, out int boneLines, out int jointPoints);

            boneLinesVertexCount = boneLines;
            jointPointsVertexCount = jointPoints;

            lock (skeletonLock)
            {
                pendingSkeletonVertices = skeletonVerts;
            }

            if (animatedMeshVertices != null && animCurrentFrame < animatedMeshVertices.Length)
            {
                lock (meshLock)
                {
                    pendingMeshVertices = animatedMeshVertices[animCurrentFrame];
                }
            }

            RequestNextFrameRendering();

            AnimationFrameChanged?.Invoke(animCurrentFrame, animationFrames.Length);
        }

        private void UpdateSkeletonVertices()
        {
            Vector3[]? bones = null;
            int[]? parents = null;

            if (animationFrames != null && animParentIndices != null && animCurrentFrame < animationFrames.Length)
            {
                bones = animationFrames[animCurrentFrame];
                parents = animParentIndices;
            }
            else if (staticBonePositions != null && staticParentIndices != null)
            {
                bones = staticBonePositions;
                parents = staticParentIndices;
            }

            if (bones != null && parents != null)
            {
                var skeletonVerts = BuildSkeletonVertices(bones, parents, out int boneLines, out int jointPoints);
                boneLinesVertexCount = boneLines;
                jointPointsVertexCount = jointPoints;
                lock (skeletonLock)
                {
                    pendingSkeletonVertices = skeletonVerts;
                }
            }
        }

        private void AddOctahedronBone(List<Vector3> verts, Vector3 b, Vector3 a, float scale)
        {
            Vector3 v = a - b;
            float len = v.Length;
            if (len < 1e-5f) return;

            Vector3 temp = Math.Abs(v.X) < Math.Abs(v.Y) ? Vector3.UnitX : Vector3.UnitY;
            Vector3 u1 = Vector3.Normalize(Vector3.Cross(v, temp));
            Vector3 u2 = Vector3.Normalize(Vector3.Cross(v, u1));

            float thickness = Math.Min(len * 0.15f, 0.02f * scale);
            if (thickness < 1e-5f) thickness = 1e-5f;

            Vector3 c = b + v * 0.2f;

            Vector3 p1 = c + u1 * thickness;
            Vector3 p2 = c + u2 * thickness;
            Vector3 p3 = c - u1 * thickness;
            Vector3 p4 = c - u2 * thickness;

            verts.Add(b); verts.Add(p1);
            verts.Add(b); verts.Add(p2);
            verts.Add(b); verts.Add(p3);
            verts.Add(b); verts.Add(p4);

            verts.Add(p1); verts.Add(p2);
            verts.Add(p2); verts.Add(p3);
            verts.Add(p3); verts.Add(p4);
            verts.Add(p4); verts.Add(p1);

            verts.Add(p1); verts.Add(a);
            verts.Add(p2); verts.Add(a);
            verts.Add(p3); verts.Add(a);
            verts.Add(p4); verts.Add(a);
        }

        private void AddOctahedronJoint(List<Vector3> verts, Vector3 p, float scale)
        {
            float r = 0.005f * scale;
            if (r < 1e-5f) r = 1e-5f;

            Vector3 px1 = p + Vector3.UnitX * r;
            Vector3 px2 = p - Vector3.UnitX * r;
            Vector3 py1 = p + Vector3.UnitY * r;
            Vector3 py2 = p - Vector3.UnitY * r;
            Vector3 pz1 = p + Vector3.UnitZ * r;
            Vector3 pz2 = p - Vector3.UnitZ * r;

            verts.Add(px1); verts.Add(py1);
            verts.Add(py1); verts.Add(px2);
            verts.Add(px2); verts.Add(py2);
            verts.Add(py2); verts.Add(px1);

            verts.Add(px1); verts.Add(pz1);
            verts.Add(py1); verts.Add(pz1);
            verts.Add(px2); verts.Add(pz1);
            verts.Add(py2); verts.Add(pz1);

            verts.Add(px1); verts.Add(pz2);
            verts.Add(py1); verts.Add(pz2);
            verts.Add(px2); verts.Add(pz2);
            verts.Add(py2); verts.Add(pz2);
        }

        private Vector3[] BuildSkeletonVertices(Vector3[] bonePositions, int[] parentIndices, out int boneLinesCount, out int jointPointsCount)
        {
            var skeletonVerts = new List<Vector3>();
            boneLinesCount = 0;
            jointPointsCount = 0;

            for (int i = 0; i < bonePositions.Length; i++)
            {
                int pIdx = parentIndices[i];
                if (pIdx >= 0 && pIdx < bonePositions.Length)
                {
                    var a = bonePositions[i];
                    var b = bonePositions[pIdx];
                    AddOctahedronBone(skeletonVerts, b, a, boneScale);
                    boneLinesCount += 24;
                }
            }

            for (int i = 0; i < bonePositions.Length; i++)
            {
                AddOctahedronJoint(skeletonVerts, bonePositions[i], boneScale);
                jointPointsCount += 24;
            }

            return skeletonVerts.ToArray();
        }

        private class AvaloniaBindingsContext : IBindingsContext
        {
            private readonly GlInterface _gl;
            public AvaloniaBindingsContext(GlInterface gl)
            {
                _gl = gl;
            }
            public IntPtr GetProcAddress(string procName)
            {
                return _gl.GetProcAddress(procName);
            }
        }
    }
}
