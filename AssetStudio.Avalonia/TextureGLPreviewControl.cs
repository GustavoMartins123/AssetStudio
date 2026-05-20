using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Media;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Texture2DDecoder;

namespace AssetStudio.Avalonia
{
    public class TextureGLPreviewControl : OpenGlControlBase
    {
        // Shaders
        private const string vsSource = @"#version 140
in vec2 vertexPosition;
in vec2 vertexTexCoord;
out vec2 texCoord;
uniform mat4 u_mvp;
void main()
{
    gl_Position = u_mvp * vec4(vertexPosition, 0.0, 1.0);
    texCoord = vertexTexCoord;
}";

        private const string fsSource = @"#version 140
in vec2 texCoord;
out vec4 outputColor;
uniform sampler2D mainTex;
uniform vec4 channelMask;
uniform int isAlphaOnly;
void main()
{
    vec4 texColor = texture(mainTex, texCoord);
    if (isAlphaOnly == 1)
    {
        outputColor = vec4(vec3(texColor.a), 1.0);
    }
    else
    {
        vec3 rgb = texColor.rgb;
        float r = channelMask.r > 0.5 ? rgb.r : 0.0;
        float g = channelMask.g > 0.5 ? rgb.g : 0.0;
        float b = channelMask.b > 0.5 ? rgb.b : 0.0;
        float a = channelMask.a > 0.5 ? texColor.a : 1.0;
        outputColor = vec4(r, g, b, a);
    }
}";

        // Programs and bindings
        private int pgmID;
        private int attributeVertexPosition;
        private int attributeTexCoord;
        private int uniformTexture;
        private int uniformMvp;
        private int uniformChannelMask;
        private int uniformIsAlphaOnly;

        // VBO / VAO state
        private int vao;
        private int vbo;
        private int ebo;

        // Texture state
        private int glTextureId;
        private bool hasTexture;
        private int textureWidth;
        private int textureHeight;
        private Texture2D? currentTexture;

        // Panning & Zooming
        private Vector2 panOffset = Vector2.Zero;
        private float zoomLevel = 1.0f;
        private global::Avalonia.Point lastMousePos;

        // Channel masking
        private readonly bool[] channelMask = new bool[4] { true, true, true, true };

        // Pending texture upload data
        private readonly object textureLock = new object();
        private byte[]? pendingTextureData;
        private int pendingWidth;
        private int pendingHeight;
        private TextureFormat pendingFormat;
        private int[]? pendingVersion;
        private BuildTarget pendingPlatform;
        private bool hasPendingTexture;

        public event Action<string>? GpuErrorOccurred;

        public TextureGLPreviewControl()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        }

        public void SetTexture(Texture2D texture)
        {
            ResetPanAndZoom();

            lock (textureLock)
            {
                currentTexture = texture;
                pendingWidth = texture.m_Width;
                pendingHeight = texture.m_Height;
                pendingFormat = texture.m_TextureFormat;
                pendingVersion = texture.version;
                pendingPlatform = texture.platform;
                hasPendingTexture = true;

                try
                {
                    pendingTextureData = texture.image_data.GetData();
                }
                catch (Exception ex)
                {
                    GpuErrorOccurred?.Invoke($"Failed to get raw texture data: {ex.Message}");
                    return;
                }
            }

            hasTexture = true;
            RequestNextFrameRendering();
        }

        public void SetChannels(bool[] channels)
        {
            for (int i = 0; i < 4; i++)
            {
                channelMask[i] = channels[i];
            }
            RequestNextFrameRendering();
        }

        public void ResetPanAndZoom()
        {
            panOffset = Vector2.Zero;
            zoomLevel = 1.0f;
            RequestNextFrameRendering();
        }

        protected override void OnOpenGlInit(GlInterface gl)
        {
            try
            {
                GL.LoadBindings(new AvaloniaBindingsContext(gl));
                GL.ClearColor(0.15f, 0.15f, 0.15f, 1.0f);

                pgmID = GL.CreateProgram();
                LoadShaderFromString(vsSource, ShaderType.VertexShader, pgmID, out _);
                LoadShaderFromString(fsSource, ShaderType.FragmentShader, pgmID, out _);
                GL.LinkProgram(pgmID);

                GL.GetProgram(pgmID, GetProgramParameterName.LinkStatus, out int status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmID);
                    throw new Exception($"GL Program link error: {log}");
                }

                attributeVertexPosition = GL.GetAttribLocation(pgmID, "vertexPosition");
                attributeTexCoord = GL.GetAttribLocation(pgmID, "vertexTexCoord");
                uniformTexture = GL.GetUniformLocation(pgmID, "mainTex");
                uniformMvp = GL.GetUniformLocation(pgmID, "u_mvp");
                uniformChannelMask = GL.GetUniformLocation(pgmID, "channelMask");
                uniformIsAlphaOnly = GL.GetUniformLocation(pgmID, "isAlphaOnly");
            }
            catch (Exception ex)
            {
                GpuErrorOccurred?.Invoke($"OpenGL initialization failed: {ex.Message}");
            }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            CleanupBuffers();
            if (pgmID != 0) GL.DeleteProgram(pgmID);
            if (glTextureId != 0) GL.DeleteTexture(glTextureId);
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                // Check if there is a pending texture to upload
                bool uploadNew = false;
                byte[]? texBytes = null;
                int texW = 0, texH = 0;
                TextureFormat texFormat = TextureFormat.RGBA32;
                int[]? texVersion = null;
                BuildTarget texPlatform = BuildTarget.NoTarget;

                lock (textureLock)
                {
                    if (hasPendingTexture)
                    {
                        uploadNew = true;
                        texBytes = pendingTextureData;
                        texW = pendingWidth;
                        texH = pendingHeight;
                        texFormat = pendingFormat;
                        texVersion = pendingVersion;
                        texPlatform = pendingPlatform;
                        hasPendingTexture = false;
                    }
                }

                if (uploadNew && texBytes != null)
                {
                    if (glTextureId != 0)
                    {
                        GL.DeleteTexture(glTextureId);
                        glTextureId = 0;
                    }

                    UploadTexture(texBytes, texW, texH, texFormat, texVersion, texPlatform);
                }

                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                if (!hasTexture || glTextureId == 0)
                {
                    return;
                }

                // Adjust Viewport
                GL.Viewport(0, 0, (int)Math.Max(1, Bounds.Width), (int)Math.Max(1, Bounds.Height));

                // Setup MVP matrix
                float controlAspect = (float)(Bounds.Width / (Bounds.Height == 0 ? 1 : Bounds.Height));
                Matrix4 proj;
                if (controlAspect >= 1.0f)
                {
                    proj = Matrix4.CreateOrthographicOffCenter(-controlAspect, controlAspect, -1f, 1f, -1f, 1f);
                }
                else
                {
                    proj = Matrix4.CreateOrthographicOffCenter(-1f, 1f, -1f / controlAspect, 1f / controlAspect, -1f, 1f);
                }

                float quadW = 1.0f;
                float quadH = 1.0f;
                if (textureWidth >= textureHeight)
                {
                    quadW = 1.0f;
                    quadH = (float)textureHeight / textureWidth;
                }
                else
                {
                    quadW = (float)textureWidth / textureHeight;
                    quadH = 1.0f;
                }

                Matrix4 model = Matrix4.CreateScale(quadW, quadH, 1.0f);
                Matrix4 view = Matrix4.CreateScale(zoomLevel) * Matrix4.CreateTranslation(panOffset.X, panOffset.Y, 0.0f);
                Matrix4 mvp = model * view * proj;

                // Setup VAO and VBO if not already created
                if (vao == 0)
                {
                    // Quad vertices: Position (X, Y) and UV (U, V)
                    // Flipped vertically by adjusting UVs
                    float[] vertices = new float[]
                    {
                        -1.0f, -1.0f,  0.0f, 1.0f,
                         1.0f, -1.0f,  1.0f, 1.0f,
                         1.0f,  1.0f,  1.0f, 0.0f,
                        -1.0f,  1.0f,  0.0f, 0.0f
                    };

                    int[] indices = new int[]
                    {
                        0, 1, 2,
                        2, 3, 0
                    };

                    GL.GenVertexArrays(1, out vao);
                    GL.BindVertexArray(vao);

                    GL.GenBuffers(1, out vbo);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                    GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(vertices.Length * sizeof(float)), vertices, BufferUsageHint.StaticDraw);

                    GL.GenBuffers(1, out ebo);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(indices.Length * sizeof(int)), indices, BufferUsageHint.StaticDraw);

                    int stride = 4 * sizeof(float);
                    GL.VertexAttribPointer(attributeVertexPosition, 2, VertexAttribPointerType.Float, false, stride, 0);
                    GL.EnableVertexAttribArray(attributeVertexPosition);

                    GL.VertexAttribPointer(attributeTexCoord, 2, VertexAttribPointerType.Float, false, stride, (IntPtr)(2 * sizeof(float)));
                    GL.EnableVertexAttribArray(attributeTexCoord);

                    GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                    GL.BindVertexArray(0);
                }

                GL.UseProgram(pgmID);

                // Bind texture
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, glTextureId);
                GL.Uniform1(uniformTexture, 0);

                // Set uniforms
                GL.UniformMatrix4(uniformMvp, false, ref mvp);

                // Channel masking
                GL.Uniform4(uniformChannelMask, channelMask[0] ? 1.0f : 0.0f, channelMask[1] ? 1.0f : 0.0f, channelMask[2] ? 1.0f : 0.0f, channelMask[3] ? 1.0f : 0.0f);
                
                int activeColorCount = 0;
                for (int i = 0; i < 3; i++) if (channelMask[i]) activeColorCount++;
                bool alphaOnly = !channelMask[0] && !channelMask[1] && !channelMask[2] && channelMask[3];
                GL.Uniform1(uniformIsAlphaOnly, alphaOnly ? 1 : 0);

                // Draw
                GL.BindVertexArray(vao);
                GL.DrawElements(BeginMode.Triangles, 6, DrawElementsType.UnsignedInt, 0);
                GL.BindVertexArray(0);

                GL.UseProgram(0);
            }
            catch (Exception ex)
            {
                GpuErrorOccurred?.Invoke($"OpenGL rendering failed: {ex.Message}");
            }
        }

        private static int GetMip0Size(TextureFormat format, int width, int height)
        {
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            int blocks = bw * bh;
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.BC4:
                    return blocks * 8;
                case TextureFormat.DXT5:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                    return blocks * 16;
                default:
                    int pixels = width * height;
                    switch (format)
                    {
                        case TextureFormat.Alpha8:
                            return pixels;
                        case TextureFormat.RGB24:
                            return pixels * 3;
                        case TextureFormat.RGBA32:
                        case TextureFormat.BGRA32:
                            return pixels * 4;
                        default:
                            return pixels * 4;
                    }
            }
        }

        private void UploadTexture(byte[] rawBytes, int width, int height, TextureFormat format, int[]? version, BuildTarget platform)
        {
            textureWidth = width;
            textureHeight = height;

            byte[] dataToUpload = rawBytes;
            TextureFormat uploadFormat = format;

            // Unpack crunch compression on CPU if needed (which is fast and yields DXT/ETC blocks)
            if (format == TextureFormat.DXT1Crunched || format == TextureFormat.DXT5Crunched ||
                format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched)
            {
                try
                {
                    byte[]? unpacked;
                    if (version != null && (version[0] > 2017 || (version[0] == 2017 && version[1] >= 3))
                        || format == TextureFormat.ETC_RGB4Crunched
                        || format == TextureFormat.ETC2_RGBA8Crunched)
                    {
                        unpacked = TextureDecoder.UnpackUnityCrunch(rawBytes);
                    }
                    else
                    {
                        unpacked = TextureDecoder.UnpackCrunch(rawBytes);
                    }

                    if (unpacked != null)
                    {
                        dataToUpload = unpacked;
                        if (format == TextureFormat.DXT1Crunched) uploadFormat = TextureFormat.DXT1;
                        else if (format == TextureFormat.DXT5Crunched) uploadFormat = TextureFormat.DXT5;
                        else if (format == TextureFormat.ETC_RGB4Crunched) uploadFormat = TextureFormat.ETC_RGB4;
                        else if (format == TextureFormat.ETC2_RGBA8Crunched) uploadFormat = TextureFormat.ETC2_RGBA8;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Crunch unpack failed: {ex.Message}");
                }
            }

            // Slice the texture raw bytes array to the size of the first mipmap (mip 0)
            int mip0Size = GetMip0Size(uploadFormat, width, height);
            if (dataToUpload.Length > mip0Size && mip0Size > 0)
            {
                byte[] slice = new byte[mip0Size];
                System.Buffer.BlockCopy(dataToUpload, 0, slice, 0, mip0Size);
                dataToUpload = slice;
            }

            // Determine if the format is natively supported by GPU (Compressed or Uncompressed)
            bool isCompressed = false;
            int glInternalFormat = 0;

            switch (uploadFormat)
            {
                case TextureFormat.DXT1:
                    isCompressed = true;
                    glInternalFormat = 0x83F0; // GL_COMPRESSED_RGB_S3TC_DXT1_EXT
                    break;
                case TextureFormat.DXT5:
                    isCompressed = true;
                    glInternalFormat = 0x83F3; // GL_COMPRESSED_RGBA_S3TC_DXT5_EXT
                    break;
                case TextureFormat.BC4:
                    isCompressed = true;
                    glInternalFormat = 0x8DBB; // GL_COMPRESSED_RED_RGTC1
                    break;
                case TextureFormat.BC5:
                    isCompressed = true;
                    glInternalFormat = 0x8DBD; // GL_COMPRESSED_RG_RGTC2
                    break;
                case TextureFormat.BC6H:
                    isCompressed = true;
                    glInternalFormat = 0x8E8F; // GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT
                    break;
                case TextureFormat.BC7:
                    isCompressed = true;
                    glInternalFormat = 0x8E8C; // GL_COMPRESSED_RGBA_BPTC_UNORM
                    break;
            }

            // Clear any previous GL errors
            while (GL.GetError() != ErrorCode.NoError) { }

            glTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, glTextureId);

            if (isCompressed)
            {
                try
                {
                    GL.CompressedTexImage2D(TextureTarget.Texture2D, 0, (InternalFormat)glInternalFormat, width, height, 0, dataToUpload.Length, dataToUpload);
                    
                    ErrorCode err = GL.GetError();
                    if (err != ErrorCode.NoError)
                    {
                        throw new Exception($"CompressedTexImage2D failed with OpenGL error: {err}");
                    }

                    SetupTextureParameters();
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GPU Compressed texture upload failed, falling back to CPU decode: {ex.Message}");
                    GL.DeleteTexture(glTextureId);
                    
                    // Clear errors
                    while (GL.GetError() != ErrorCode.NoError) { }

                    glTextureId = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, glTextureId);
                }
            }

            // Uncompressed direct upload or fallback CPU decode
            if (uploadFormat == TextureFormat.RGBA32)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, dataToUpload);
            }
            else if (uploadFormat == TextureFormat.BGRA32)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, dataToUpload);
            }
            else if (uploadFormat == TextureFormat.RGB24)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, width, height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, dataToUpload);
            }
            else if (uploadFormat == TextureFormat.Alpha8)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Alpha, width, height, 0, PixelFormat.Alpha, PixelType.UnsignedByte, dataToUpload);
            }
            else
            {
                // Fallback: Use CPU decoder to decode to BGRA32, then upload
                if (currentTexture != null)
                {
                    byte[] decodedBytes = new byte[width * height * 4];
                    var converter = new Texture2DConverter(currentTexture);
                    if (converter.DecodeTexture2D(decodedBytes))
                    {
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, decodedBytes);
                    }
                    else
                    {
                        throw new Exception($"Unable to decode texture format {format} on CPU.");
                    }
                }
                else
                {
                    throw new Exception($"Unable to decode texture format {format} (no source texture reference).");
                }
            }

            ErrorCode finalErr = GL.GetError();
            if (finalErr != ErrorCode.NoError)
            {
                throw new Exception($"TexImage2D failed with OpenGL error: {finalErr}");
            }

            SetupTextureParameters();
        }

        private void SetupTextureParameters()
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }

        private void CleanupBuffers()
        {
            if (vao != 0)
            {
                GL.DeleteVertexArrays(1, ref vao);
                vao = 0;
            }
            if (vbo != 0)
            {
                GL.DeleteBuffers(1, ref vbo);
                vbo = 0;
            }
            if (ebo != 0)
            {
                GL.DeleteBuffers(1, ref ebo);
                ebo = 0;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            lastMousePos = e.GetPosition(this);
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
            if (prop.IsLeftButtonPressed || prop.IsRightButtonPressed)
            {
                var curPos = e.GetPosition(this);
                float dx = (float)(curPos.X - lastMousePos.X);
                float dy = (float)(curPos.Y - lastMousePos.Y);
                lastMousePos = curPos;

                float glDx = dx / (float)Bounds.Width * 2f;
                float glDy = -dy / (float)Bounds.Height * 2f;

                panOffset.X += glDx;
                panOffset.Y += glDy;

                RequestNextFrameRendering();
            }
            else
            {
                lastMousePos = e.GetPosition(this);
            }
            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            float delta = (float)e.Delta.Y;
            if (delta > 0)
                zoomLevel *= 1.15f;
            else if (delta < 0)
                zoomLevel /= 1.15f;

            zoomLevel = Math.Clamp(zoomLevel, 0.05f, 100f);
            RequestNextFrameRendering();
            e.Handled = true;
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
