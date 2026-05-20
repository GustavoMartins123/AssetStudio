using System;
using System.Collections.Generic;
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
        // Shaders
        private const string vsSource = @"#version 140
in vec3 vertexPosition;
in vec3 normalDirection;
in vec4 vertexColor;
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

        private const string fsSource = @"#version 140
in vec3 normal;
out vec4 outputColor;
void main()
{
	vec3 unitNormal = normalize(normal);
	float nDotProduct = clamp(dot(unitNormal, vec3(0.707, 0, 0.707)), 0, 1);
	vec2 ContributionWeightsSqrt = vec2(0.5, 0.5) + vec2(0.5, -0.5) * unitNormal.y;
	vec2 ContributionWeights = ContributionWeightsSqrt * ContributionWeightsSqrt;
	vec3 color = nDotProduct * vec3(1, 0.957, 0.839) / 3.14159;
	color += vec3(0.779, 0.716, 0.453) * ContributionWeights.y;
	color += vec3(0.368, 0.477, 0.735) * ContributionWeights.x;
	outputColor = vec4(sqrt(color), 1);
}";

        private const string fsBlackSource = @"#version 140
out vec4 outputColor;
void main()
{
	outputColor = vec4(0, 0, 0, 1);
}";

        private const string fsColorSource = @"#version 140
out vec4 outputColor;
in vec4 color;
void main()
{
	outputColor = color;
}";

        private const string vsTexSource = @"#version 140
in vec3 vertexPosition;
in vec3 normalDirection;
in vec2 vertexTexCoord;
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

        private const string fsTexSource = @"#version 140
in vec3 normal;
in vec2 texCoord;
out vec4 outputColor;
uniform sampler2D mainTex;
void main()
{
	vec3 unitNormal = normalize(normal);
	float nDotProduct = clamp(dot(unitNormal, vec3(0.707, 0, 0.707)), 0, 1);
	vec2 ContributionWeightsSqrt = vec2(0.5, 0.5) + vec2(0.5, -0.5) * unitNormal.y;
	vec2 ContributionWeights = ContributionWeightsSqrt * ContributionWeightsSqrt;
	vec3 lightColor = nDotProduct * vec3(1, 0.957, 0.839) / 3.14159;
	lightColor += vec3(0.779, 0.716, 0.453) * ContributionWeights.y;
	lightColor += vec3(0.368, 0.477, 0.735) * ContributionWeights.x;
	vec4 texColor = texture(mainTex, texCoord);
	outputColor = vec4(texColor.rgb * lightColor, texColor.a);
}";

        // Programs
        private int pgmID;
        private int pgmColorID;
        private int pgmBlackID;
        private int pgmTexID;

        // Attribute / Uniform locations
        private int attributeVertexPosition;
        private int attributeNormalDirection;
        private int attributeVertexColor;
        
        private int uniformModelMatrix;
        private int uniformViewMatrix;
        private int uniformProjMatrix;

        private int uniformModelMatrixColor;
        private int uniformViewMatrixColor;
        private int uniformProjMatrixColor;

        private int uniformModelMatrixBlack;
        private int uniformViewMatrixBlack;
        private int uniformProjMatrixBlack;

        private int attributeVertexPositionTex;
        private int attributeNormalDirectionTex;
        private int attributeTexCoord;
        private int uniformTexture;
        private int uniformModelMatrixTex;
        private int uniformViewMatrixTex;
        private int uniformProjMatrixTex;

        // VBO / VAO state
        private int vao;
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

        public void SetMesh(Mesh m_Mesh)
        {
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
            GL.LoadBindings(new AvaloniaBindingsContext(gl));
            
            GL.ClearColor(0.3f, 0.4f, 0.5f, 1.0f);

            // Shaded
            pgmID = GL.CreateProgram();
            LoadShaderFromString(vsSource, ShaderType.VertexShader, pgmID, out _);
            LoadShaderFromString(fsSource, ShaderType.FragmentShader, pgmID, out _);
            GL.LinkProgram(pgmID);

            // Color
            pgmColorID = GL.CreateProgram();
            LoadShaderFromString(vsSource, ShaderType.VertexShader, pgmColorID, out _);
            LoadShaderFromString(fsColorSource, ShaderType.FragmentShader, pgmColorID, out _);
            GL.LinkProgram(pgmColorID);

            // Wireframe black
            pgmBlackID = GL.CreateProgram();
            LoadShaderFromString(vsSource, ShaderType.VertexShader, pgmBlackID, out _);
            LoadShaderFromString(fsBlackSource, ShaderType.FragmentShader, pgmBlackID, out _);
            GL.LinkProgram(pgmBlackID);

            // Textured
            pgmTexID = GL.CreateProgram();
            LoadShaderFromString(vsTexSource, ShaderType.VertexShader, pgmTexID, out _);
            LoadShaderFromString(fsTexSource, ShaderType.FragmentShader, pgmTexID, out _);
            GL.LinkProgram(pgmTexID);

            attributeVertexPosition = GL.GetAttribLocation(pgmID, "vertexPosition");
            attributeNormalDirection = GL.GetAttribLocation(pgmID, "normalDirection");
            attributeVertexColor = GL.GetAttribLocation(pgmColorID, "vertexColor");
            
            // pgmID uniforms
            uniformModelMatrix = GL.GetUniformLocation(pgmID, "modelMatrix");
            uniformViewMatrix = GL.GetUniformLocation(pgmID, "viewMatrix");
            uniformProjMatrix = GL.GetUniformLocation(pgmID, "projMatrix");

            // pgmColorID uniforms
            uniformModelMatrixColor = GL.GetUniformLocation(pgmColorID, "modelMatrix");
            uniformViewMatrixColor = GL.GetUniformLocation(pgmColorID, "viewMatrix");
            uniformProjMatrixColor = GL.GetUniformLocation(pgmColorID, "projMatrix");

            // pgmBlackID uniforms
            uniformModelMatrixBlack = GL.GetUniformLocation(pgmBlackID, "modelMatrix");
            uniformViewMatrixBlack = GL.GetUniformLocation(pgmBlackID, "viewMatrix");
            uniformProjMatrixBlack = GL.GetUniformLocation(pgmBlackID, "projMatrix");

            // pgmTexID uniforms
            attributeVertexPositionTex = GL.GetAttribLocation(pgmTexID, "vertexPosition");
            attributeNormalDirectionTex = GL.GetAttribLocation(pgmTexID, "normalDirection");
            attributeTexCoord = GL.GetAttribLocation(pgmTexID, "vertexTexCoord");
            uniformTexture = GL.GetUniformLocation(pgmTexID, "mainTex");
            uniformModelMatrixTex = GL.GetUniformLocation(pgmTexID, "modelMatrix");
            uniformViewMatrixTex = GL.GetUniformLocation(pgmTexID, "viewMatrix");
            uniformProjMatrixTex = GL.GetUniformLocation(pgmTexID, "projMatrix");
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            CleanupBuffers();
            if (pgmID != 0) GL.DeleteProgram(pgmID);
            if (pgmColorID != 0) GL.DeleteProgram(pgmColorID);
            if (pgmBlackID != 0) GL.DeleteProgram(pgmBlackID);
            if (pgmTexID != 0) GL.DeleteProgram(pgmTexID);
            if (previewTextureId != 0) GL.DeleteTexture(previewTextureId);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (vertexData == null || indiceData == null)
            {
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                return;
            }

            // Bind/Update texture on render thread
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
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texW, texH, 0, PixelFormat.Bgra, PixelType.UnsignedByte, texBytes);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            }

            ChangeGLSize(Bounds.Width, Bounds.Height);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);

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
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                GL.DrawElements(BeginMode.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
            }

            if (wireFrameMode == 1 || wireFrameMode == 2)
            {
                GL.Enable(EnableCap.PolygonOffsetLine);
                GL.PolygonOffset(-1, -1);
                GL.UseProgram(pgmBlackID);
                GL.UniformMatrix4(uniformModelMatrixBlack, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrixBlack, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrixBlack, false, ref projMatrixData);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                GL.DrawElements(BeginMode.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
                GL.Disable(EnableCap.PolygonOffsetLine);
            }

            GL.BindVertexArray(0);
            GL.Flush();
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

        private void CreateVAO()
        {
            CleanupBuffers();

            GL.GenVertexArrays(1, out vao);
            GL.BindVertexArray(vao);

            if (previewMaterialMode && uvData != null)
            {
                CreateVBO(out _, vertexData, attributeVertexPositionTex);
                if (normalMode == 0)
                {
                    CreateVBO(out _, normal2Data, attributeNormalDirectionTex);
                }
                else
                {
                    if (normalData != null)
                        CreateVBO(out _, normalData, attributeNormalDirectionTex);
                }
                CreateVBO(out _, uvData, attributeTexCoord);
            }
            else
            {
                CreateVBO(out _, vertexData, attributeVertexPosition);
                if (normalMode == 0)
                {
                    CreateVBO(out _, normal2Data, attributeNormalDirection);
                }
                else
                {
                    if (normalData != null)
                        CreateVBO(out _, normalData, attributeNormalDirection);
                }
                CreateVBO(out _, colorData, attributeVertexColor);
            }

            CreateEBO(out _, indiceData);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        private void CleanupBuffers()
        {
            if (vao != 0)
            {
                GL.DeleteVertexArrays(1, ref vao);
                vao = 0;
            }
            if (vbos.Count > 0)
            {
                int[] arr = vbos.ToArray();
                GL.DeleteBuffers(arr.Length, arr);
                vbos.Clear();
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
                    shadeMode = shadeMode == 0 ? 1 : 0;
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
                System.Diagnostics.Debug.WriteLine($"GL Shader compile error ({type}): {log}");
            }
            
            GL.AttachShader(program, shaderId);
            GL.DeleteShader(shaderId);
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
