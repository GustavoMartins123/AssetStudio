using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.OpenGL;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace AssetStudio.Avalonia
{
    using Vector2 = OpenTK.Mathematics.Vector2;
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public partial class MeshPreviewControl
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
uniform vec4 materialTint;
void main()
{
	vec3 unitNormal = normalize(normal);
	float nDotProduct = clamp(dot(unitNormal, vec3(0.707, 0.0, 0.707)), 0.0, 1.0);
	vec2 ContributionWeightsSqrt = vec2(0.5, 0.5) + vec2(0.5, -0.5) * unitNormal.y;
	vec2 ContributionWeights = ContributionWeightsSqrt * ContributionWeightsSqrt;
	vec3 color = nDotProduct * vec3(1.0, 0.957, 0.839) / 3.14159;
	color += vec3(0.779, 0.716, 0.453) * ContributionWeights.y;
	color += vec3(0.368, 0.477, 0.735) * ContributionWeights.x;
	outputColor = vec4(sqrt(color) * materialTint.rgb, materialTint.a);
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
uniform vec4 materialTint;
void main()
{
	outputColor = vec4(color.rgb * materialTint.rgb, color.a * materialTint.a);
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
uniform vec4 materialTint;
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
	outputColor = vec4(texColor.rgb * materialTint.rgb * lightColor, texColor.a * materialTint.a);
}";

        private const int LocationPosition = 0;
        private const int LocationNormal = 1;
        private const int LocationColor = 2;
        private const int LocationTexCoord = 3;

        // Shader programs
        private int pgmID;
        private int pgmColorID;
        private int pgmBlackID;
        private int pgmTexID;
        private int pgmGreenID;
        private int pgmYellowID;
        private int pgmRedID;

        // Uniform locations
        private int uniformModelMatrix;
        private int uniformViewMatrix;
        private int uniformProjMatrix;
        private int uniformMaterialTint;

        private int uniformModelMatrixColor;
        private int uniformViewMatrixColor;
        private int uniformProjMatrixColor;
        private int uniformMaterialTintColor;

        private int uniformModelMatrixBlack;
        private int uniformViewMatrixBlack;
        private int uniformProjMatrixBlack;

        private int uniformTexture;
        private int uniformModelMatrixTex;
        private int uniformViewMatrixTex;
        private int uniformProjMatrixTex;
        private int uniformMaterialTintTex;

        private int uniformModelMatrixGreen;
        private int uniformViewMatrixGreen;
        private int uniformProjMatrixGreen;

        private int uniformModelMatrixYellow;
        private int uniformViewMatrixYellow;
        private int uniformProjMatrixYellow;

        private int uniformModelMatrixRed;
        private int uniformViewMatrixRed;
        private int uniformProjMatrixRed;

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

                pgmID = CreateShaderProgram(glslVersion, precision, vsSource, fsSource, nameof(pgmID));
                pgmColorID = CreateShaderProgram(glslVersion, precision, vsSource, fsColorSource, nameof(pgmColorID));
                pgmBlackID = CreateShaderProgram(glslVersion, precision, vsSource, fsBlackSource, nameof(pgmBlackID));
                pgmTexID = CreateShaderProgram(glslVersion, precision, vsTexSource, fsTexSource, nameof(pgmTexID));

                uniformModelMatrix = GL.GetUniformLocation(pgmID, "modelMatrix");
                uniformViewMatrix = GL.GetUniformLocation(pgmID, "viewMatrix");
                uniformProjMatrix = GL.GetUniformLocation(pgmID, "projMatrix");
                uniformMaterialTint = GL.GetUniformLocation(pgmID, "materialTint");

                uniformModelMatrixColor = GL.GetUniformLocation(pgmColorID, "modelMatrix");
                uniformViewMatrixColor = GL.GetUniformLocation(pgmColorID, "viewMatrix");
                uniformProjMatrixColor = GL.GetUniformLocation(pgmColorID, "projMatrix");
                uniformMaterialTintColor = GL.GetUniformLocation(pgmColorID, "materialTint");

                uniformModelMatrixBlack = GL.GetUniformLocation(pgmBlackID, "modelMatrix");
                uniformViewMatrixBlack = GL.GetUniformLocation(pgmBlackID, "viewMatrix");
                uniformProjMatrixBlack = GL.GetUniformLocation(pgmBlackID, "projMatrix");

                uniformTexture = GL.GetUniformLocation(pgmTexID, "mainTex");
                uniformModelMatrixTex = GL.GetUniformLocation(pgmTexID, "modelMatrix");
                uniformViewMatrixTex = GL.GetUniformLocation(pgmTexID, "viewMatrix");
                uniformProjMatrixTex = GL.GetUniformLocation(pgmTexID, "projMatrix");
                uniformMaterialTintTex = GL.GetUniformLocation(pgmTexID, "materialTint");

                pgmGreenID = CreateShaderProgram(glslVersion, precision, vsSkeletonSource, fsGreenSource, nameof(pgmGreenID));
                uniformModelMatrixGreen = GL.GetUniformLocation(pgmGreenID, "modelMatrix");
                uniformViewMatrixGreen = GL.GetUniformLocation(pgmGreenID, "viewMatrix");
                uniformProjMatrixGreen = GL.GetUniformLocation(pgmGreenID, "projMatrix");

                pgmYellowID = CreateShaderProgram(glslVersion, precision, vsSkeletonSource, fsYellowSource, nameof(pgmYellowID));
                uniformModelMatrixYellow = GL.GetUniformLocation(pgmYellowID, "modelMatrix");
                uniformViewMatrixYellow = GL.GetUniformLocation(pgmYellowID, "viewMatrix");
                uniformProjMatrixYellow = GL.GetUniformLocation(pgmYellowID, "projMatrix");

                pgmRedID = CreateShaderProgram(glslVersion, precision, vsSkeletonSource, fsRedSource, nameof(pgmRedID));
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
                DeleteProgramIfCreated(ref pgmID);
                DeleteProgramIfCreated(ref pgmColorID);
                DeleteProgramIfCreated(ref pgmBlackID);
                DeleteProgramIfCreated(ref pgmTexID);
                DeleteProgramIfCreated(ref pgmGreenID);
                DeleteProgramIfCreated(ref pgmYellowID);
                DeleteProgramIfCreated(ref pgmRedID);
                DeletePreviewTextures();
            }
            catch
            {
            }
        }

        private int CreateShaderProgram(string glslVersion, string precision, string vertexSource, string fragmentSource, string programName)
        {
            int programId = GL.CreateProgram();
            LoadShaderFromString($"{glslVersion}\n{vertexSource}", ShaderType.VertexShader, programId, out _);
            LoadShaderFromString($"{glslVersion}\n{precision}\n{fragmentSource}", ShaderType.FragmentShader, programId, out _);
            GL.LinkProgram(programId);

            GL.GetProgram(programId, GetProgramParameterName.LinkStatus, out int status);
            if (status == 0)
            {
                string log = GL.GetProgramInfoLog(programId);
                throw new Exception($"Shader link error ({programName}): {log}");
            }

            return programId;
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

        private static void DeleteProgramIfCreated(ref int programId)
        {
            if (programId != 0)
            {
                GL.DeleteProgram(programId);
                programId = 0;
            }
        }

        private void ChangeGLSize(double width, double height)
        {
            GL.Viewport(0, 0, (int)Math.Max(1, width), (int)Math.Max(1, height));

            if (width <= height)
            {
                float k = (float)(width / (height == 0 ? 1 : height));
                projMatrixData = Matrix4.CreateScale(1, k, PreviewDepthScale);
            }
            else
            {
                float k = (float)(height / (width == 0 ? 1 : width));
                projMatrixData = Matrix4.CreateScale(k, 1, PreviewDepthScale);
            }
        }

        private void CreateVBO(out int vboAddress, Vector2[]? data, int address)
        {
            if (address < 0 || data == null)
            {
                vboAddress = 0;
                return;
            }

            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 8), data, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 2, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private void CreateVBO(out int vboAddress, Vector3[]? data, int address, BufferUsageHint usage = BufferUsageHint.StaticDraw)
        {
            if (address < 0 || data == null)
            {
                vboAddress = 0;
                return;
            }

            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 12), data, usage);
            GL.VertexAttribPointer(address, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private void CreateVBO(out int vboAddress, Vector4[]? data, int address)
        {
            if (address < 0 || data == null)
            {
                vboAddress = 0;
                return;
            }

            GL.GenBuffers(1, out vboAddress);
            vbos.Add(vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(data.Length * 16), data, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 4, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private void CreateEBO(out int address, int[]? data)
        {
            if (data == null)
            {
                address = 0;
                return;
            }

            GL.GenBuffers(1, out address);
            vbos.Add(address);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, address);
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(data.Length * sizeof(int)), data, BufferUsageHint.StaticDraw);
        }

        internal static bool ShouldCreateWireframeVao(bool avatarMode, int currentWireframeMode)
        {
            return avatarMode || currentWireframeMode == 1 || currentWireframeMode == 2;
        }

        internal static int[] BuildWireframeLineIndices(ReadOnlySpan<int> triangleIndices)
        {
            int triangleCount = triangleIndices.Length / 3;
            if (triangleCount <= 0)
            {
                return Array.Empty<int>();
            }

            int[] lineIndices = new int[triangleCount * 6];
            int dst = 0;
            int limit = triangleCount * 3;
            for (int i = 0; i < limit; i += 3)
            {
                int i0 = triangleIndices[i];
                int i1 = triangleIndices[i + 1];
                int i2 = triangleIndices[i + 2];
                lineIndices[dst] = i0;
                lineIndices[dst + 1] = i1;
                lineIndices[dst + 2] = i1;
                lineIndices[dst + 3] = i2;
                lineIndices[dst + 4] = i2;
                lineIndices[dst + 5] = i0;
                dst += 6;
            }

            return lineIndices;
        }

        private void BindSolidAttribsAndEBO()
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
            else if (normalData != null)
            {
                CreateVBO(out _, normalData, LocationNormal);
            }

            if (previewMaterialMode && uvData != null)
            {
                CreateVBO(out _, uvData, LocationTexCoord);
            }
            else
            {
                CreateVBO(out _, colorData, LocationColor);
            }

            CreateEBO(out _, indiceData);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        private void EnsureWireframeVAO()
        {
            if (vaoWireframe != 0 || indiceData == null || vboPosition == 0)
            {
                return;
            }

            var lineIndices = BuildWireframeLineIndices(indiceData);
            if (lineIndices.Length == 0)
            {
                return;
            }

            GL.GenVertexArrays(1, out vaoWireframe);
            GL.BindVertexArray(vaoWireframe);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboPosition);
            GL.VertexAttribPointer(LocationPosition, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(LocationPosition);
            CreateEBO(out _, lineIndices);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        private void CreateVAO()
        {
            CleanupBuffers(isAvatarMode);

            GL.GenVertexArrays(1, out vao);
            GL.BindVertexArray(vao);
            BindSolidAttribsAndEBO();
            GL.BindVertexArray(0);

            // Wireframe VAO is built lazily only when wireframe/avatar mode actually renders it.
            if (ShouldCreateWireframeVao(isAvatarMode, wireFrameMode))
            {
                EnsureWireframeVAO();
            }

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

            if (vertices.Length == 0)
            {
                return;
            }

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

        private sealed class AvaloniaBindingsContext : IBindingsContext
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
