using System;
using System.Collections.Generic;
using System.Threading;
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
using SkiaSharp;

namespace AssetStudio.Avalonia
{
    public enum PreparedGpuTextureUploadKind
    {
        Compressed,
        Rgba32,
        Bgra32,
        Rgb24
    }

    public sealed class PreparedGpuTexturePayload
    {
        public required byte[] Data { get; init; }
        public required int DataLength { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required TextureFormat SourceFormat { get; init; }
        public required TextureFormat UploadFormat { get; init; }
        public required PreparedGpuTextureUploadKind UploadKind { get; init; }
        public required int GlInternalFormat { get; init; }
        public required bool NeedsYFlip { get; init; }
        public required bool WasCrunchUnpacked { get; init; }
        public required bool WasCpuDecoded { get; init; }
        public required TextureGLPreviewControl.GlCapabilities TargetCapabilities { get; init; }
        internal Texture2D? SourceTextureForFallback { get; init; }
    }

    public class TextureGLPreviewControl : OpenGlControlBase
    {
        private const string vsSource = @"
layout(location = 0) in vec2 vertexPosition;
layout(location = 1) in vec2 vertexTexCoord;
out vec2 texCoord;
uniform mat4 u_mvp;
uniform int yFlip;
void main()
{
    gl_Position = u_mvp * vec4(vertexPosition, 0.0, 1.0);
    texCoord = vec2(vertexTexCoord.x, yFlip == 1 ? (1.0 - vertexTexCoord.y) : vertexTexCoord.y);
}";

        private const string fsSource = @"
in vec2 texCoord;
layout(location = 0) out vec4 outputColor;
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

        private const int LocationPosition = 0;
        private const int LocationTexCoord = 1;

        // Programs and bindings
        private int pgmID;
        private int uniformTexture;
        private int uniformMvp;
        private int uniformChannelMask;
        private int uniformIsAlphaOnly;
        private int uniformYFlip;

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

        public readonly record struct GlCapabilities(
            bool IsOpenGles,
            int MajorVersion,
            int MinorVersion,
            bool SupportsBgraUpload,
            bool SupportsS3tc,
            bool SupportsRgtc,
            bool SupportsBptc,
            bool SupportsEtc1,
            bool SupportsEtc2,
            bool SupportsAstcLdr,
            bool SupportsAstcHdr)
        {
            public static GlCapabilities FromVersionAndExtensions(
                bool isOpenGles,
                int majorVersion,
                int minorVersion,
                IReadOnlySet<string>? extensions)
            {
                bool HasExt(string name) => extensions != null && extensions.Contains(name);

                bool supportsBgra = !isOpenGles;
                bool supportsS3tc = HasExt("GL_EXT_texture_compression_s3tc")
                    || HasExt("GL_NV_texture_compression_s3tc")
                    || HasExt("GL_S3_s3tc");
                bool supportsRgtc = (!isOpenGles && majorVersion >= 3)
                    || HasExt("GL_ARB_texture_compression_rgtc")
                    || HasExt("GL_EXT_texture_compression_rgtc");
                bool supportsBptc = (!isOpenGles && (majorVersion > 4 || (majorVersion == 4 && minorVersion >= 2)))
                    || HasExt("GL_ARB_texture_compression_bptc")
                    || HasExt("GL_EXT_texture_compression_bptc");
                bool supportsEtc2 = (isOpenGles && majorVersion >= 3)
                    || (!isOpenGles && (majorVersion > 4 || (majorVersion == 4 && minorVersion >= 3)))
                    || HasExt("GL_ARB_ES3_compatibility")
                    || (HasExt("GL_OES_compressed_ETC2_RGB8_texture") && HasExt("GL_OES_compressed_ETC2_RGBA8_texture"));
                bool supportsEtc1 = supportsEtc2
                    || (isOpenGles && HasExt("GL_OES_compressed_ETC1_RGB8_texture"));
                bool supportsAstcHdr = HasExt("GL_KHR_texture_compression_astc_hdr");
                bool supportsAstcLdr = (isOpenGles && (majorVersion > 3 || (majorVersion == 3 && minorVersion >= 2)))
                    || supportsAstcHdr
                    || HasExt("GL_KHR_texture_compression_astc_ldr")
                    || HasExt("GL_OES_texture_compression_astc");

                return new GlCapabilities(
                    isOpenGles,
                    majorVersion,
                    minorVersion,
                    supportsBgra,
                    supportsS3tc,
                    supportsRgtc,
                    supportsBptc,
                    supportsEtc1,
                    supportsEtc2,
                    supportsAstcLdr,
                    supportsAstcHdr);
            }
        }

        // Conservative capability set used only before OnOpenGlInit has queried the real GPU context.
        public static GlCapabilities ConservativeUninitializedCapabilities { get; } = new(
            IsOpenGles: false,
            MajorVersion: 3,
            MinorVersion: 3,
            SupportsBgraUpload: true,
            SupportsS3tc: false,
            SupportsRgtc: false,
            SupportsBptc: false,
            SupportsEtc1: false,
            SupportsEtc2: false,
            SupportsAstcLdr: false,
            SupportsAstcHdr: false);

        // Pending texture upload data
        private readonly object textureLock = new object();
        private PreparedGpuTexturePayload? pendingPreparedPayload;
        private PreparedGpuTexturePayload? lastUploadedPreparedPayload;
        private byte[]? pendingTextureData;
        private int pendingWidth;
        private int pendingHeight;
        private TextureFormat pendingFormat;
        private int[]? pendingVersion;
        private BuildTarget pendingPlatform;
        private bool pendingNeedsYFlip;
        private bool currentNeedsYFlip;
        private bool hasPendingTexture;
        private GlCapabilities glCapabilities;
        private bool hasGlCapabilities;

        public event Action<string>? GpuErrorOccurred;

        public bool HasGlCapabilities
        {
            get
            {
                lock (textureLock)
                {
                    return hasGlCapabilities;
                }
            }
        }

        public GlCapabilities CurrentGlCapabilities
        {
            get
            {
                lock (textureLock)
                {
                    return hasGlCapabilities ? glCapabilities : ConservativeUninitializedCapabilities;
                }
            }
        }

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

        public static PreparedGpuTexturePayload PrepareGpuTexturePayload(
            Texture2D texture,
            GlCapabilities capabilities,
            CancellationToken cancellationToken = default)
        {
            return PrepareGpuTexturePayload(texture, capabilities, crunchUnpacker: null, cancellationToken);
        }

        internal static PreparedGpuTexturePayload PrepareGpuTexturePayload(
            Texture2D texture,
            GlCapabilities capabilities,
            Func<byte[], TextureFormat, int[]?, byte[]?>? crunchUnpacker,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(texture);
            cancellationToken.ThrowIfCancellationRequested();

            int width = texture.m_Width;
            int height = texture.m_Height;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"Invalid texture dimensions: {width}x{height}.");
            }

            TextureFormat sourceFormat = texture.m_TextureFormat;
            byte[] rawBytes = texture.image_data.GetData();
            cancellationToken.ThrowIfCancellationRequested();

            if (rawBytes == null || rawBytes.Length == 0)
            {
                throw new InvalidOperationException($"Texture {texture.m_Name} has no image data.");
            }

            byte[] dataToUpload = rawBytes;
            TextureFormat uploadFormat = sourceFormat;
            bool wasCrunchUnpacked = false;

            if (sourceFormat == TextureFormat.DXT1Crunched || sourceFormat == TextureFormat.DXT5Crunched ||
                sourceFormat == TextureFormat.ETC_RGB4Crunched || sourceFormat == TextureFormat.ETC2_RGBA8Crunched)
            {
                byte[]? unpacked = crunchUnpacker != null
                    ? crunchUnpacker(rawBytes, sourceFormat, texture.version)
                    : UnpackCrunchedTextureBytes(rawBytes, sourceFormat, texture.version);
                cancellationToken.ThrowIfCancellationRequested();
                if (unpacked != null && unpacked.Length > 0)
                {
                    dataToUpload = unpacked;
                    wasCrunchUnpacked = true;
                    uploadFormat = sourceFormat switch
                    {
                        TextureFormat.DXT1Crunched => TextureFormat.DXT1,
                        TextureFormat.DXT5Crunched => TextureFormat.DXT5,
                        TextureFormat.ETC_RGB4Crunched => TextureFormat.ETC_RGB4,
                        TextureFormat.ETC2_RGBA8Crunched => TextureFormat.ETC2_RGBA8,
                        _ => sourceFormat
                    };
                }
            }

            int mip0Size = GetMip0Size(uploadFormat, width, height);
            bool hasFullMip0 = mip0Size > 0 && dataToUpload.Length >= mip0Size;

            if (hasFullMip0 && TryResolveCompressedInternalFormat(uploadFormat, capabilities, out int glInternalFormat))
            {
                return new PreparedGpuTexturePayload
                {
                    Data = dataToUpload,
                    DataLength = mip0Size,
                    Width = width,
                    Height = height,
                    SourceFormat = sourceFormat,
                    UploadFormat = uploadFormat,
                    UploadKind = PreparedGpuTextureUploadKind.Compressed,
                    GlInternalFormat = glInternalFormat,
                    NeedsYFlip = false,
                    WasCrunchUnpacked = wasCrunchUnpacked,
                    WasCpuDecoded = false,
                    TargetCapabilities = capabilities,
                    SourceTextureForFallback = texture
                };
            }

            if (hasFullMip0 && uploadFormat == TextureFormat.RGBA32)
            {
                return new PreparedGpuTexturePayload
                {
                    Data = dataToUpload,
                    DataLength = mip0Size,
                    Width = width,
                    Height = height,
                    SourceFormat = sourceFormat,
                    UploadFormat = TextureFormat.RGBA32,
                    UploadKind = PreparedGpuTextureUploadKind.Rgba32,
                    GlInternalFormat = 0,
                    NeedsYFlip = false,
                    WasCrunchUnpacked = wasCrunchUnpacked,
                    WasCpuDecoded = false,
                    TargetCapabilities = capabilities
                };
            }

            if (hasFullMip0 && uploadFormat == TextureFormat.BGRA32)
            {
                if (capabilities.SupportsBgraUpload)
                {
                    return new PreparedGpuTexturePayload
                    {
                        Data = dataToUpload,
                        DataLength = mip0Size,
                        Width = width,
                        Height = height,
                        SourceFormat = sourceFormat,
                        UploadFormat = TextureFormat.BGRA32,
                        UploadKind = PreparedGpuTextureUploadKind.Bgra32,
                        GlInternalFormat = 0,
                        NeedsYFlip = false,
                        WasCrunchUnpacked = wasCrunchUnpacked,
                        WasCpuDecoded = false,
                        TargetCapabilities = capabilities
                    };
                }

                byte[] rgbaBytes = new byte[mip0Size];
                Texture2DConverter.SwizzleRgbaBgra(dataToUpload.AsSpan(0, mip0Size), rgbaBytes.AsSpan(0, mip0Size));
                cancellationToken.ThrowIfCancellationRequested();
                return new PreparedGpuTexturePayload
                {
                    Data = rgbaBytes,
                    DataLength = mip0Size,
                    Width = width,
                    Height = height,
                    SourceFormat = sourceFormat,
                    UploadFormat = TextureFormat.RGBA32,
                    UploadKind = PreparedGpuTextureUploadKind.Rgba32,
                    GlInternalFormat = 0,
                    NeedsYFlip = false,
                    WasCrunchUnpacked = wasCrunchUnpacked,
                    WasCpuDecoded = false,
                    TargetCapabilities = capabilities
                };
            }

            if (hasFullMip0 && uploadFormat == TextureFormat.RGB24)
            {
                return new PreparedGpuTexturePayload
                {
                    Data = dataToUpload,
                    DataLength = mip0Size,
                    Width = width,
                    Height = height,
                    SourceFormat = sourceFormat,
                    UploadFormat = TextureFormat.RGB24,
                    UploadKind = PreparedGpuTextureUploadKind.Rgb24,
                    GlInternalFormat = 0,
                    NeedsYFlip = false,
                    WasCrunchUnpacked = wasCrunchUnpacked,
                    WasCpuDecoded = false,
                    TargetCapabilities = capabilities
                };
            }

            // Off-UI CPU fallback decode for formats the active GL context does not natively support (e.g. ASTC on Desktop GL).
            cancellationToken.ThrowIfCancellationRequested();
            long requiredBytes = checked((long)width * height * 4L);
            if (requiredBytes > Array.MaxLength)
            {
                throw new OverflowException($"Decoded texture buffer size exceeds supported limits ({width}x{height}).");
            }

            int byteCount = checked((int)requiredBytes);
            byte[] decodedBytes = new byte[byteCount];
            var converter = new Texture2DConverter(texture);
            if (!converter.DecodeTexture2D(decodedBytes.AsSpan(0, byteCount)))
            {
                throw new InvalidOperationException($"Unable to decode texture format {sourceFormat} on CPU.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (capabilities.SupportsBgraUpload)
            {
                return new PreparedGpuTexturePayload
                {
                    Data = decodedBytes,
                    DataLength = byteCount,
                    Width = width,
                    Height = height,
                    SourceFormat = sourceFormat,
                    UploadFormat = TextureFormat.BGRA32,
                    UploadKind = PreparedGpuTextureUploadKind.Bgra32,
                    GlInternalFormat = 0,
                    NeedsYFlip = false,
                    WasCrunchUnpacked = wasCrunchUnpacked,
                    WasCpuDecoded = true,
                    TargetCapabilities = capabilities
                };
            }

            Texture2DConverter.SwizzleRgbaBgra(decodedBytes.AsSpan(0, byteCount), decodedBytes.AsSpan(0, byteCount));
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedGpuTexturePayload
            {
                Data = decodedBytes,
                DataLength = byteCount,
                Width = width,
                Height = height,
                SourceFormat = sourceFormat,
                UploadFormat = TextureFormat.RGBA32,
                UploadKind = PreparedGpuTextureUploadKind.Rgba32,
                GlInternalFormat = 0,
                NeedsYFlip = false,
                WasCrunchUnpacked = wasCrunchUnpacked,
                WasCpuDecoded = true,
                TargetCapabilities = capabilities
            };
        }

        public static PreparedGpuTexturePayload PrepareGpuImagePayload(
            SKBitmap image,
            GlCapabilities capabilities,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(image);
            cancellationToken.ThrowIfCancellationRequested();

            int width = image.Width;
            int height = image.Height;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"Invalid image dimensions: {width}x{height}.");
            }

            int byteCount = checked(width * height * 4);
            byte[] pixels = new byte[byteCount];
            image.CopyBgraPixelsTo(pixels);
            cancellationToken.ThrowIfCancellationRequested();

            if (capabilities.SupportsBgraUpload)
            {
                return new PreparedGpuTexturePayload
                {
                    Data = pixels,
                    DataLength = byteCount,
                    Width = width,
                    Height = height,
                    SourceFormat = TextureFormat.BGRA32,
                    UploadFormat = TextureFormat.BGRA32,
                    UploadKind = PreparedGpuTextureUploadKind.Bgra32,
                    GlInternalFormat = 0,
                    NeedsYFlip = true,
                    WasCrunchUnpacked = false,
                    WasCpuDecoded = false,
                    TargetCapabilities = capabilities
                };
            }

            Texture2DConverter.SwizzleRgbaBgra(pixels.AsSpan(0, byteCount), pixels.AsSpan(0, byteCount));
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedGpuTexturePayload
            {
                Data = pixels,
                DataLength = byteCount,
                Width = width,
                Height = height,
                SourceFormat = TextureFormat.BGRA32,
                UploadFormat = TextureFormat.RGBA32,
                UploadKind = PreparedGpuTextureUploadKind.Rgba32,
                GlInternalFormat = 0,
                NeedsYFlip = true,
                WasCrunchUnpacked = false,
                WasCpuDecoded = false,
                TargetCapabilities = capabilities
            };
        }

        private static byte[]? UnpackCrunchedTextureBytes(byte[] rawBytes, TextureFormat format, int[]? version)
        {
            try
            {
                if (version != null && version.Length >= 2 && (version[0] > 2017 || (version[0] == 2017 && version[1] >= 3))
                    || format == TextureFormat.ETC_RGB4Crunched
                    || format == TextureFormat.ETC2_RGBA8Crunched)
                {
                    return TextureDecoder.UnpackUnityCrunch(rawBytes);
                }

                return TextureDecoder.UnpackCrunch(rawBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Crunch unpack failed: {ex.Message}");
                return null;
            }
        }

        public void SetPreparedTexture(PreparedGpuTexturePayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ResetPanAndZoom();

            lock (textureLock)
            {
                pendingPreparedPayload = payload;
                lastUploadedPreparedPayload = payload;
                currentTexture = payload.SourceTextureForFallback;
                pendingTextureData = null;
                pendingWidth = payload.Width;
                pendingHeight = payload.Height;
                pendingFormat = payload.UploadFormat;
                pendingVersion = null;
                pendingPlatform = BuildTarget.NoTarget;
                pendingNeedsYFlip = payload.NeedsYFlip;
                hasPendingTexture = true;
            }

            hasTexture = true;
            RequestNextFrameRendering();
        }

        public void SetTexture(Texture2D texture)
        {
            ResetPanAndZoom();

            lock (textureLock)
            {
                pendingPreparedPayload = null;
                lastUploadedPreparedPayload = null;
                currentTexture = texture;
                pendingWidth = texture.m_Width;
                pendingHeight = texture.m_Height;
                pendingFormat = texture.m_TextureFormat;
                pendingVersion = texture.version;
                pendingPlatform = texture.platform;
                pendingNeedsYFlip = false;
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

        public void SetImage(SKBitmap image)
        {
            var payload = PrepareGpuImagePayload(image, CurrentGlCapabilities);
            SetPreparedTexture(payload);
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
                var queriedCapabilities = QueryGlCapabilities(gl);
                lock (textureLock)
                {
                    glCapabilities = queriedCapabilities;
                    hasGlCapabilities = true;
                }

                bool isGles = queriedCapabilities.IsOpenGles;
                string glslVersion = isGles ? "#version 300 es" : "#version 330 core";
                string precision = isGles ? "precision mediump float;" : "";

                GL.ClearColor(0.15f, 0.15f, 0.15f, 1.0f);

                pgmID = GL.CreateProgram();
                LoadShaderFromString($"{glslVersion}\n{vsSource}", ShaderType.VertexShader, pgmID, out _);
                LoadShaderFromString($"{glslVersion}\n{precision}\n{fsSource}", ShaderType.FragmentShader, pgmID, out _);
                GL.LinkProgram(pgmID);

                GL.GetProgram(pgmID, GetProgramParameterName.LinkStatus, out int status);
                if (status == 0)
                {
                    string log = GL.GetProgramInfoLog(pgmID);
                    throw new Exception($"GL Program link error: {log}");
                }

                uniformTexture = GL.GetUniformLocation(pgmID, "mainTex");
                uniformMvp = GL.GetUniformLocation(pgmID, "u_mvp");
                uniformChannelMask = GL.GetUniformLocation(pgmID, "channelMask");
                uniformIsAlphaOnly = GL.GetUniformLocation(pgmID, "isAlphaOnly");
                uniformYFlip = GL.GetUniformLocation(pgmID, "yFlip");

                bool shouldRequestFrame = false;
                lock (textureLock)
                {
                    if (!hasPendingTexture && lastUploadedPreparedPayload != null)
                    {
                        pendingPreparedPayload = lastUploadedPreparedPayload;
                        pendingWidth = lastUploadedPreparedPayload.Width;
                        pendingHeight = lastUploadedPreparedPayload.Height;
                        pendingFormat = lastUploadedPreparedPayload.UploadFormat;
                        pendingNeedsYFlip = lastUploadedPreparedPayload.NeedsYFlip;
                        hasPendingTexture = true;
                        hasTexture = true;
                        shouldRequestFrame = true;
                    }
                    else if (hasPendingTexture)
                    {
                        shouldRequestFrame = true;
                    }
                }

                if (shouldRequestFrame)
                {
                    RequestNextFrameRendering();
                }
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
                if (pgmID != 0)
                {
                    GL.DeleteProgram(pgmID);
                    pgmID = 0;
                }
                if (glTextureId != 0)
                {
                    GL.DeleteTexture(glTextureId);
                    glTextureId = 0;
                }
                hasTexture = false;
            }
            catch
            {
            }
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                // Check if there is a pending texture to upload
                bool uploadNew = false;
                PreparedGpuTexturePayload? prepared = null;
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
                        prepared = pendingPreparedPayload;
                        texBytes = pendingTextureData;
                        texW = pendingWidth;
                        texH = pendingHeight;
                        texFormat = pendingFormat;
                        texVersion = pendingVersion;
                        texPlatform = pendingPlatform;
                        currentNeedsYFlip = pendingNeedsYFlip;
                        hasPendingTexture = false;
                        pendingPreparedPayload = null;
                        pendingTextureData = null;
                    }
                }

                if (uploadNew)
                {
                    if (glTextureId != 0)
                    {
                        GL.DeleteTexture(glTextureId);
                        glTextureId = 0;
                    }

                    if (prepared != null)
                    {
                        UploadPreparedTexture(prepared);
                    }
                    else if (texBytes != null)
                    {
                        UploadTexture(texBytes, texW, texH, texFormat, texVersion, texPlatform);
                    }
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
                    float[] vertices = new float[]
                    {
                        -1.0f, -1.0f,  0.0f, 0.0f,
                         1.0f, -1.0f,  1.0f, 0.0f,
                         1.0f,  1.0f,  1.0f, 1.0f,
                        -1.0f,  1.0f,  0.0f, 1.0f
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
                    GL.VertexAttribPointer(LocationPosition, 2, VertexAttribPointerType.Float, false, stride, 0);
                    GL.EnableVertexAttribArray(LocationPosition);

                    GL.VertexAttribPointer(LocationTexCoord, 2, VertexAttribPointerType.Float, false, stride, (IntPtr)(2 * sizeof(float)));
                    GL.EnableVertexAttribArray(LocationTexCoord);

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
                GL.Uniform1(uniformYFlip, currentNeedsYFlip ? 1 : 0);

                // Channel masking
                GL.Uniform4(uniformChannelMask, channelMask[0] ? 1.0f : 0.0f, channelMask[1] ? 1.0f : 0.0f, channelMask[2] ? 1.0f : 0.0f, channelMask[3] ? 1.0f : 0.0f);

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

        private void UploadPreparedTexture(PreparedGpuTexturePayload payload)
        {
            textureWidth = payload.Width;
            textureHeight = payload.Height;

            var capabilities = CurrentGlCapabilities;
            while (GL.GetError() != ErrorCode.NoError) { }

            glTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, glTextureId);

            if (payload.UploadKind == PreparedGpuTextureUploadKind.Compressed)
            {
                GL.CompressedTexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    (InternalFormat)payload.GlInternalFormat,
                    payload.Width,
                    payload.Height,
                    0,
                    payload.DataLength,
                    payload.Data);

                ErrorCode err = GL.GetError();
                if (err == ErrorCode.NoError)
                {
                    SetupTextureParameters();
                    return;
                }

                // Driver rejected compressed upload unexpectedly; delete texture and report error to trigger async CPU fallback.
                GL.DeleteTexture(glTextureId);
                glTextureId = 0;
                while (GL.GetError() != ErrorCode.NoError) { }
                throw new Exception($"CompressedTexImage2D failed with OpenGL error: {err}");
            }

            switch (payload.UploadKind)
            {
                case PreparedGpuTextureUploadKind.Rgba32:
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, payload.Width, payload.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, payload.Data);
                    break;

                case PreparedGpuTextureUploadKind.Bgra32:
                    if (capabilities.SupportsBgraUpload)
                    {
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, payload.Width, payload.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, payload.Data);
                    }
                    else
                    {
                        byte[] rgbaBytes = BigArrayPool<byte>.Shared.Rent(payload.DataLength);
                        try
                        {
                            Texture2DConverter.SwizzleRgbaBgra(payload.Data.AsSpan(0, payload.DataLength), rgbaBytes.AsSpan(0, payload.DataLength));
                            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, payload.Width, payload.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgbaBytes);
                        }
                        finally
                        {
                            BigArrayPool<byte>.Shared.Return(rgbaBytes);
                        }
                    }
                    break;

                case PreparedGpuTextureUploadKind.Rgb24:
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                    try
                    {
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, payload.Width, payload.Height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, payload.Data);
                    }
                    finally
                    {
                        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                    }
                    break;
            }

            ErrorCode finalErr = GL.GetError();
            if (finalErr != ErrorCode.NoError)
            {
                GL.DeleteTexture(glTextureId);
                glTextureId = 0;
                throw new Exception($"TexImage2D failed with OpenGL error: {finalErr}");
            }

            SetupTextureParameters();
        }

        private static GlCapabilities QueryGlCapabilities(GlInterface gl)
        {
            var version = gl.ContextInfo?.Version;
            bool isGles = version.HasValue && version.Value.Type == GlProfileType.OpenGLES;
            int major = version?.Major ?? 0;
            int minor = version?.Minor ?? 0;

            if (major == 0)
            {
                while (GL.GetError() != ErrorCode.NoError) { }
                GL.GetInteger(GetPName.MajorVersion, out major);
                GL.GetInteger(GetPName.MinorVersion, out minor);
                if (GL.GetError() != ErrorCode.NoError || major <= 0)
                {
                    major = 2;
                    minor = 1;
                    while (GL.GetError() != ErrorCode.NoError) { }
                }
            }

            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gl.ContextInfo?.Extensions != null)
            {
                foreach (var ext in gl.ContextInfo.Extensions)
                {
                    if (!string.IsNullOrWhiteSpace(ext))
                    {
                        extensions.Add(ext);
                    }
                }
            }

            if (extensions.Count == 0)
            {
                while (GL.GetError() != ErrorCode.NoError) { }
                if (major >= 3)
                {
                    GL.GetInteger(GetPName.NumExtensions, out int numExt);
                    if (GL.GetError() == ErrorCode.NoError && numExt > 0)
                    {
                        for (int i = 0; i < numExt; i++)
                        {
                            string? ext = GL.GetString(StringNameIndexed.Extensions, i);
                            if (!string.IsNullOrWhiteSpace(ext))
                            {
                                extensions.Add(ext);
                            }
                        }
                    }
                }
                else
                {
                    string? extStr = GL.GetString(StringName.Extensions);
                    if (GL.GetError() == ErrorCode.NoError && !string.IsNullOrEmpty(extStr))
                    {
                        foreach (var ext in extStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            extensions.Add(ext);
                        }
                    }
                }
                while (GL.GetError() != ErrorCode.NoError) { }
            }

            return GlCapabilities.FromVersionAndExtensions(isGles, major, minor, extensions);
        }

        public static int GetMip0Size(TextureFormat format, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            long BlockBytes(int bw, int bh, int bytesPerBlock)
            {
                long blocksX = ((long)width + bw - 1) / bw;
                long blocksY = ((long)height + bh - 1) / bh;
                return blocksX * blocksY * bytesPerBlock;
            }

            long size = format switch
            {
                TextureFormat.DXT1 or
                TextureFormat.BC4 or
                TextureFormat.ETC_RGB4 or
                TextureFormat.ETC_RGB4_3DS or
                TextureFormat.ETC2_RGB or
                TextureFormat.ETC2_RGBA1 or
                TextureFormat.EAC_R or
                TextureFormat.EAC_R_SIGNED or
                TextureFormat.ATC_RGB4 or
                TextureFormat.PVRTC_RGB4 or
                TextureFormat.PVRTC_RGBA4 => BlockBytes(4, 4, 8),

                TextureFormat.PVRTC_RGB2 or
                TextureFormat.PVRTC_RGBA2 => BlockBytes(8, 4, 8),

                TextureFormat.DXT3 or
                TextureFormat.DXT5 or
                TextureFormat.BC5 or
                TextureFormat.BC6H or
                TextureFormat.BC7 or
                TextureFormat.ETC2_RGBA8 or
                TextureFormat.ETC_RGBA8_3DS or
                TextureFormat.EAC_RG or
                TextureFormat.EAC_RG_SIGNED or
                TextureFormat.ATC_RGBA8 or
                TextureFormat.ASTC_RGB_4x4 or
                TextureFormat.ASTC_RGBA_4x4 or
                TextureFormat.ASTC_HDR_4x4 => BlockBytes(4, 4, 16),

                TextureFormat.ASTC_RGB_5x5 or
                TextureFormat.ASTC_RGBA_5x5 or
                TextureFormat.ASTC_HDR_5x5 => BlockBytes(5, 5, 16),

                TextureFormat.ASTC_RGB_6x6 or
                TextureFormat.ASTC_RGBA_6x6 or
                TextureFormat.ASTC_HDR_6x6 => BlockBytes(6, 6, 16),

                TextureFormat.ASTC_RGB_8x8 or
                TextureFormat.ASTC_RGBA_8x8 or
                TextureFormat.ASTC_HDR_8x8 => BlockBytes(8, 8, 16),

                TextureFormat.ASTC_RGB_10x10 or
                TextureFormat.ASTC_RGBA_10x10 or
                TextureFormat.ASTC_HDR_10x10 => BlockBytes(10, 10, 16),

                TextureFormat.ASTC_RGB_12x12 or
                TextureFormat.ASTC_RGBA_12x12 or
                TextureFormat.ASTC_HDR_12x12 => BlockBytes(12, 12, 16),

                TextureFormat.Alpha8 or
                TextureFormat.R8 => (long)width * height,

                TextureFormat.ARGB4444 or
                TextureFormat.RGBA4444 or
                TextureFormat.RGB565 or
                TextureFormat.R16 or
                TextureFormat.RG16 or
                TextureFormat.RHalf or
                TextureFormat.YUY2 => (long)width * height * 2L,

                TextureFormat.RGB24 or
                TextureFormat.BGR24 => (long)width * height * 3L,

                TextureFormat.RGBA32 or
                TextureFormat.ARGB32 or
                TextureFormat.BGRA32 or
                TextureFormat.RGB9e5Float or
                TextureFormat.RG32 or
                TextureFormat.RGHalf or
                TextureFormat.RFloat => (long)width * height * 4L,

                TextureFormat.RGB48 => (long)width * height * 6L,

                TextureFormat.RGBAHalf or
                TextureFormat.RGFloat or
                TextureFormat.RGBA64 => (long)width * height * 8L,

                TextureFormat.RGBFloat => (long)width * height * 12L,

                TextureFormat.RGBAFloat or
                TextureFormat.ARGBFloat => (long)width * height * 16L,

                _ => (long)width * height * 4L
            };

            return size <= int.MaxValue ? (int)size : 0;
        }

        public static bool TryResolveCompressedInternalFormat(
            TextureFormat format,
            GlCapabilities capabilities,
            out int glInternalFormat,
            bool srgb = false)
        {
            glInternalFormat = 0;

            switch (format)
            {
                case TextureFormat.DXT1:
                    if (!capabilities.SupportsS3tc) return false;
                    glInternalFormat = srgb ? 0x8C4C : 0x83F0; // GL_COMPRESSED_SRGB_S3TC_DXT1_EXT / GL_COMPRESSED_RGB_S3TC_DXT1_EXT
                    return true;
                case TextureFormat.DXT3:
                    if (!capabilities.SupportsS3tc) return false;
                    glInternalFormat = srgb ? 0x8C4E : 0x83F2; // GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT3_EXT / GL_COMPRESSED_RGBA_S3TC_DXT3_EXT
                    return true;
                case TextureFormat.DXT5:
                    if (!capabilities.SupportsS3tc) return false;
                    glInternalFormat = srgb ? 0x8C4F : 0x83F3; // GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT / GL_COMPRESSED_RGBA_S3TC_DXT5_EXT
                    return true;
                case TextureFormat.BC4:
                    if (!capabilities.SupportsRgtc) return false;
                    glInternalFormat = 0x8DBB; // GL_COMPRESSED_RED_RGTC1
                    return true;
                case TextureFormat.BC5:
                    if (!capabilities.SupportsRgtc) return false;
                    glInternalFormat = 0x8DBD; // GL_COMPRESSED_RG_RGTC2
                    return true;
                case TextureFormat.BC6H:
                    if (!capabilities.SupportsBptc) return false;
                    glInternalFormat = 0x8E8F; // GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT
                    return true;
                case TextureFormat.BC7:
                    if (!capabilities.SupportsBptc) return false;
                    glInternalFormat = srgb ? 0x8E8D : 0x8E8C; // GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM / GL_COMPRESSED_RGBA_BPTC_UNORM
                    return true;

                case TextureFormat.ETC_RGB4:
                    if (capabilities.SupportsEtc2)
                    {
                        glInternalFormat = srgb ? 0x9275 : 0x9274; // GL_COMPRESSED_SRGB8_ETC2 / GL_COMPRESSED_RGB8_ETC2
                        return true;
                    }
                    if (!srgb && capabilities.SupportsEtc1)
                    {
                        glInternalFormat = 0x8D64; // GL_ETC1_RGB8_OES
                        return true;
                    }
                    return false;
                case TextureFormat.ETC2_RGB:
                    if (!capabilities.SupportsEtc2) return false;
                    glInternalFormat = srgb ? 0x9275 : 0x9274; // GL_COMPRESSED_SRGB8_ETC2 / GL_COMPRESSED_RGB8_ETC2
                    return true;
                case TextureFormat.ETC2_RGBA1:
                    if (!capabilities.SupportsEtc2) return false;
                    glInternalFormat = srgb ? 0x9277 : 0x9276; // GL_COMPRESSED_SRGB8_PUNCHTHROUGH_ALPHA1_ETC2 / GL_COMPRESSED_RGB8_PUNCHTHROUGH_ALPHA1_ETC2
                    return true;
                case TextureFormat.ETC2_RGBA8:
                    if (!capabilities.SupportsEtc2) return false;
                    glInternalFormat = srgb ? 0x9279 : 0x9278; // GL_COMPRESSED_SRGB8_ALPHA8_ETC2_EAC / GL_COMPRESSED_RGBA8_ETC2_EAC
                    return true;

                case TextureFormat.ASTC_RGB_4x4:
                case TextureFormat.ASTC_RGBA_4x4:
                    if (!capabilities.SupportsAstcLdr) return false;
                    glInternalFormat = srgb ? 0x93D0 : 0x93B0; // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_4x4_KHR / GL_COMPRESSED_RGBA_ASTC_4x4_KHR
                    return true;
                case TextureFormat.ASTC_RGB_5x5:
                case TextureFormat.ASTC_RGBA_5x5:
                    if (!capabilities.SupportsAstcLdr) return false;
                    glInternalFormat = srgb ? 0x93D2 : 0x93B2; // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_5x5_KHR / GL_COMPRESSED_RGBA_ASTC_5x5_KHR
                    return true;
                case TextureFormat.ASTC_RGB_6x6:
                case TextureFormat.ASTC_RGBA_6x6:
                    if (!capabilities.SupportsAstcLdr) return false;
                    glInternalFormat = srgb ? 0x93D4 : 0x93B4; // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_6x6_KHR / GL_COMPRESSED_RGBA_ASTC_6x6_KHR
                    return true;
                case TextureFormat.ASTC_RGB_8x8:
                case TextureFormat.ASTC_RGBA_8x8:
                    if (!capabilities.SupportsAstcLdr) return false;
                    glInternalFormat = srgb ? 0x93D7 : 0x93B7; // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_8x8_KHR / GL_COMPRESSED_RGBA_ASTC_8x8_KHR
                    return true;
                case TextureFormat.ASTC_RGB_10x10:
                case TextureFormat.ASTC_RGBA_10x10:
                    if (!capabilities.SupportsAstcLdr) return false;
                    glInternalFormat = srgb ? 0x93DB : 0x93BB; // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_10x10_KHR / GL_COMPRESSED_RGBA_ASTC_10x10_KHR
                    return true;
                case TextureFormat.ASTC_RGB_12x12:
                case TextureFormat.ASTC_RGBA_12x12:
                    if (!capabilities.SupportsAstcLdr) return false;
                    glInternalFormat = srgb ? 0x93DD : 0x93BD; // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_12x12_KHR / GL_COMPRESSED_RGBA_ASTC_12x12_KHR
                    return true;

                case TextureFormat.ASTC_HDR_4x4:
                    if (srgb || !capabilities.SupportsAstcHdr) return false;
                    glInternalFormat = 0x93B0;
                    return true;
                case TextureFormat.ASTC_HDR_5x5:
                    if (srgb || !capabilities.SupportsAstcHdr) return false;
                    glInternalFormat = 0x93B2;
                    return true;
                case TextureFormat.ASTC_HDR_6x6:
                    if (srgb || !capabilities.SupportsAstcHdr) return false;
                    glInternalFormat = 0x93B4;
                    return true;
                case TextureFormat.ASTC_HDR_8x8:
                    if (srgb || !capabilities.SupportsAstcHdr) return false;
                    glInternalFormat = 0x93B7;
                    return true;
                case TextureFormat.ASTC_HDR_10x10:
                    if (srgb || !capabilities.SupportsAstcHdr) return false;
                    glInternalFormat = 0x93BB;
                    return true;
                case TextureFormat.ASTC_HDR_12x12:
                    if (srgb || !capabilities.SupportsAstcHdr) return false;
                    glInternalFormat = 0x93BD;
                    return true;

                default:
                    return false;
            }
        }

        private void UploadTexture(byte[] rawBytes, int width, int height, TextureFormat format, int[]? version, BuildTarget platform)
        {
            textureWidth = width;
            textureHeight = height;

            var capabilities = CurrentGlCapabilities;
            byte[] dataToUpload = rawBytes;
            TextureFormat uploadFormat = format;

            if (format == TextureFormat.DXT1Crunched || format == TextureFormat.DXT5Crunched ||
                format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched)
            {
                byte[]? unpacked = UnpackCrunchedTextureBytes(rawBytes, format, version);
                if (unpacked != null)
                {
                    dataToUpload = unpacked;
                    if (format == TextureFormat.DXT1Crunched) uploadFormat = TextureFormat.DXT1;
                    else if (format == TextureFormat.DXT5Crunched) uploadFormat = TextureFormat.DXT5;
                    else if (format == TextureFormat.ETC_RGB4Crunched) uploadFormat = TextureFormat.ETC_RGB4;
                    else if (format == TextureFormat.ETC2_RGBA8Crunched) uploadFormat = TextureFormat.ETC2_RGBA8;
                }
            }

            int mip0Size = GetMip0Size(uploadFormat, width, height);
            bool hasFullMip0 = mip0Size > 0 && dataToUpload.Length >= mip0Size;
            int glInternalFormat = 0;
            bool isCompressed = hasFullMip0 && TryResolveCompressedInternalFormat(uploadFormat, capabilities, out glInternalFormat);

            while (GL.GetError() != ErrorCode.NoError) { }

            glTextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, glTextureId);

            if (isCompressed)
            {
                try
                {
                    GL.CompressedTexImage2D(TextureTarget.Texture2D, 0, (InternalFormat)glInternalFormat, width, height, 0, mip0Size, dataToUpload);

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
                    while (GL.GetError() != ErrorCode.NoError) { }
                    glTextureId = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, glTextureId);
                }
            }

            if (hasFullMip0 && uploadFormat == TextureFormat.RGBA32)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, dataToUpload);
            }
            else if (hasFullMip0 && uploadFormat == TextureFormat.BGRA32)
            {
                if (capabilities.SupportsBgraUpload)
                {
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, dataToUpload);
                }
                else
                {
                    byte[] rgbaBytes = BigArrayPool<byte>.Shared.Rent(mip0Size);
                    try
                    {
                        Texture2DConverter.SwizzleRgbaBgra(dataToUpload.AsSpan(0, mip0Size), rgbaBytes.AsSpan(0, mip0Size));
                        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgbaBytes);
                    }
                    finally
                    {
                        BigArrayPool<byte>.Shared.Return(rgbaBytes);
                    }
                }
            }
            else if (hasFullMip0 && uploadFormat == TextureFormat.RGB24)
            {
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                try
                {
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, width, height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, dataToUpload);
                }
                finally
                {
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                }
            }
            else
            {
                if (currentTexture != null)
                {
                    if (width <= 0 || height <= 0)
                    {
                        throw new Exception($"Invalid texture dimensions: {width}x{height}.");
                    }

                    long requiredBytes = checked((long)width * height * 4L);
                    if (requiredBytes > Array.MaxLength)
                    {
                        throw new OverflowException($"Decoded texture buffer size exceeds supported limits ({width}x{height}).");
                    }

                    int byteCount = checked((int)requiredBytes);
                    byte[] decodedBytes = BigArrayPool<byte>.Shared.Rent(byteCount);
                    try
                    {
                        var converter = new Texture2DConverter(currentTexture);
                        if (converter.DecodeTexture2D(decodedBytes.AsSpan(0, byteCount)))
                        {
                            if (capabilities.SupportsBgraUpload)
                            {
                                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, decodedBytes);
                            }
                            else
                            {
                                var decodedSpan = decodedBytes.AsSpan(0, byteCount);
                                Texture2DConverter.SwizzleRgbaBgra(decodedSpan, decodedSpan);
                                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, decodedBytes);
                            }
                        }
                        else
                        {
                            throw new Exception($"Unable to decode texture format {format} on CPU.");
                        }
                    }
                    finally
                    {
                        BigArrayPool<byte>.Shared.Return(decodedBytes);
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
