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
	outputColor = vec4(texColor.rgb * lightColor, texColor.a);
}";

        private const int LocationPosition = 0;
        private const int LocationNormal = 1;
        private const int LocationColor = 2;
        private const int LocationTexCoord = 3;

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

        public void SetMesh(Mesh m_Mesh, Vector2[]? uvs = null, byte[]? textureData = null, int textureWidth = 0, int textureHeight = 0)
        {
            isAvatarMode = false;
            previewMaterialMode = false;
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
                float[] min = new float[3];
                float[] max = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    min[i] = m_Vertices[i];
                    max[i] = m_Vertices[i];
                }
                for (int v = 0; v < m_VertexCount; v++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        min[i] = Math.Min(min[i], m_Vertices[v * count + i]);
                        max[i] = Math.Max(max[i], m_Vertices[v * count + i]);
                    }
                    localVertexData[v] = new Vector3(
                        m_Vertices[v * count],
                        m_Vertices[v * count + 1],
                        m_Vertices[v * count + 2]);
                }

                // Calculate modelMatrix
                Vector3 dist = Vector3.One, offset = Vector3.Zero;
                for (int i = 0; i < 3; i++)
                {
                    dist[i] = max[i] - min[i];
                    offset[i] = (max[i] + min[i]) / 2;
                }
                float d = Math.Max(1e-5f, dist.Length);
                var localModelMatrixData = Matrix4.CreateTranslation(-offset) * Matrix4.CreateScale(2f / d);

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

                if (textureData != null && textureWidth > 0 && textureHeight > 0)
                {
                    lock (textureLock)
                    {
                        pendingTextureWidth = textureWidth;
                        pendingTextureHeight = textureHeight;
                        pendingTextureData = textureData;
                        hasPendingTexture = true;
                    }
                }

                // Post back to UI thread
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter) return;

                    viewMatrixData = Matrix4.CreateRotationY(-(float)Math.PI / 4) * Matrix4.CreateRotationX(-(float)Math.PI / 6);
                    vertexData = localVertexData;
                    modelMatrixData = localModelMatrixData;
                    indiceData = localIndiceData;
                    normalData = localNormalData;
                    normal2Data = localNormal2Data;
                    colorData = localColorData;
                    uvData = localUvData;
                    if (textureData != null)
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
                lock (textureLock)
                {
                    if (hasPendingTexture)
                    {
                        updateTex = true;
                        texBytes = pendingTextureData;
                        texW = pendingTextureWidth;
                        texH = pendingTextureHeight;
                        hasPendingTexture = false;
                    }
                }

                if (updateTex && texBytes != null)
                {
                    if (previewTextureId != 0)
                    {
                        GL.DeleteTexture(previewTextureId);
                    }
                    previewTextureId = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, previewTextureId);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texW, texH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, texBytes);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
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

                if (isAvatarMode)
                {
                    if (vao == 0)
                    {
                        CreateVAO();
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
                            GL.LineWidth(3.0f);
                            GL.UseProgram(pgmYellowID);
                            GL.UniformMatrix4(uniformModelMatrixYellow, false, ref modelMatrixData);
                            GL.UniformMatrix4(uniformViewMatrixYellow, false, ref viewMatrixData);
                            GL.UniformMatrix4(uniformProjMatrixYellow, false, ref projMatrixData);
                            GL.DrawArrays(PrimitiveType.Lines, 0, boneLinesVertexCount);
                        }

                        if (jointPointsVertexCount > 0)
                        {
                            GL.PointSize(12.0f);
                            GL.UseProgram(pgmRedID);
                            GL.UniformMatrix4(uniformModelMatrixRed, false, ref modelMatrixData);
                            GL.UniformMatrix4(uniformViewMatrixRed, false, ref viewMatrixData);
                            GL.UniformMatrix4(uniformProjMatrixRed, false, ref projMatrixData);
                            GL.DrawArrays(PrimitiveType.Points, boneLinesVertexCount, jointPointsVertexCount);
                        }

                        GL.LineWidth(1.0f);
                        GL.PointSize(1.0f);
                        GL.Enable(EnableCap.DepthTest);
                    }

                    GL.BindVertexArray(0);
                    GL.Flush();
                    return;
                }

                if (vao == 0)
                {
                    CreateVAO();
                }

                GL.BindVertexArray(vao);

                if (wireFrameMode == 0 || wireFrameMode == 2)
                {
                    if (previewMaterialMode && previewTextureId != 0)
                    {
                        GL.UseProgram(pgmTexID);
                        GL.ActiveTexture(TextureUnit.Texture0);
                        GL.BindTexture(TextureTarget.Texture2D, previewTextureId);
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
                    if (!isGles)
                    {
                        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                    }
                    GL.DrawElements(BeginMode.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
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

        private void CreateVBO(out int vboAddress, Vector3[]? data, int address)
        {
            if (address < 0 || data == null) { vboAddress = 0; return; }
            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 12), data, BufferUsageHint.StaticDraw);
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
                CreateVBO(out _, vertexData, LocationPosition);
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
                CreateVBO(out _, vertexData, LocationPosition);
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

            if (pendingSkeletonVertices != null)
            {
                CreateSkeletonVAO(pendingSkeletonVertices);
                pendingSkeletonVertices = null;
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
            if (isAvatarMode)
            {
                if (e.Key == Key.Left || e.Key == Key.A)
                {
                    viewMatrixData *= Matrix4.CreateRotationY((float)(Math.PI / 2));
                    RequestNextFrameRendering();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Right || e.Key == Key.D)
                {
                    viewMatrixData *= Matrix4.CreateRotationY((float)(-Math.PI / 2));
                    RequestNextFrameRendering();
                    e.Handled = true;
                    return;
                }
            }
            base.OnKeyDown(e);
            var isCtrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
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

        public void SetAvatar(Mesh m_Mesh, Vector3[] bonePositions, int[] parentIndices)
        {
            isAvatarMode = true;
            previewMaterialMode = false;
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

                float[] min = new float[3];
                float[] max = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    min[i] = m_Vertices[i];
                    max[i] = m_Vertices[i];
                }
                for (int v = 0; v < m_VertexCount; v++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        min[i] = Math.Min(min[i], m_Vertices[v * count + i]);
                        max[i] = Math.Max(max[i], m_Vertices[v * count + i]);
                    }
                    localVertexData[v] = new Vector3(
                        m_Vertices[v * count],
                        m_Vertices[v * count + 1],
                        m_Vertices[v * count + 2]);
                }

                Vector3 dist = Vector3.One, offset = Vector3.Zero;
                for (int i = 0; i < 3; i++)
                {
                    dist[i] = max[i] - min[i];
                    offset[i] = (max[i] + min[i]) / 2;
                }
                float d = Math.Max(1e-5f, dist.Length);
                var localModelMatrixData = Matrix4.CreateTranslation(-offset) * Matrix4.CreateScale(2f / d);

                var localIndiceData = new int[m_Indices.Count];
                for (int i = 0; i < m_Indices.Count; i = i + 3)
                {
                    localIndiceData[i] = (int)m_Indices[i];
                    localIndiceData[i + 1] = (int)m_Indices[i + 1];
                    localIndiceData[i + 2] = (int)m_Indices[i + 2];
                }

                var skeletonVerts = new List<Vector3>();
                int boneLinesCount = 0;
                int jointPointsCount = 0;

                if (bonePositions != null && parentIndices != null)
                {
                    var normalizedBones = bonePositions;

                    for (int i = 0; i < normalizedBones.Length; i++)
                    {
                        int pIdx = parentIndices[i];
                        if (pIdx >= 0 && pIdx < normalizedBones.Length)
                        {
                            var a = normalizedBones[i];
                            var b = normalizedBones[pIdx];
                            int segments = 8;
                            for (int s = 0; s < segments; s++)
                            {
                                if (s % 2 == 0)
                                {
                                    float t0 = (float)s / segments;
                                    float t1 = (float)(s + 1) / segments;
                                    skeletonVerts.Add(a + (b - a) * t0);
                                    skeletonVerts.Add(a + (b - a) * t1);
                                    boneLinesCount += 2;
                                }
                            }
                        }
                    }

                    for (int i = 0; i < normalizedBones.Length; i++)
                    {
                        skeletonVerts.Add(normalizedBones[i]);
                        jointPointsCount++;
                    }
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter) return;

                    viewMatrixData = Matrix4.Identity;
                    vertexData = localVertexData;
                    modelMatrixData = localModelMatrixData;
                    indiceData = localIndiceData;
                    normalData = null;
                    normal2Data = null;
                    colorData = null;
                    uvData = null;

                    boneLinesVertexCount = boneLinesCount;
                    jointPointsVertexCount = jointPointsCount;

                    pendingSkeletonVertices = skeletonVerts.ToArray();
                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
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
