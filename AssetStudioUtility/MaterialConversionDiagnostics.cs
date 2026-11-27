using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    /// <summary>
    /// Describes how closely a Unity material/shader was mapped into glTF 2.0 PBR.
    /// Shared across glTF/GLB exporters, scene export manifests, CLI reports, and preview diagnostics.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MaterialConversionMode
    {
        StandardPbr,
        Unlit,
        PbrApproximation,
        BasicFallback
    }

    /// <summary>
    /// Describes which rendering or inspection path is currently active when previewing a Shader or Material.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ShaderPreviewRenderMode
    {
        InterpretedD3D11Shader,
        MaterialPbrApproximation,
        BasicFallback,
        ShaderLabText
    }

    /// <summary>
    /// Explicit per-material diagnostic recording what was preserved versus approximated or dropped
    /// during Unity-to-glTF PBR material conversion.
    /// </summary>
    public sealed class MaterialConversionDiagnostic
    {
        public string MaterialName { get; init; } = string.Empty;
        public string ShaderName { get; init; } = string.Empty;
        public string ShaderClassification { get; init; } = string.Empty;
        public MaterialConversionMode Mode { get; init; } = MaterialConversionMode.StandardPbr;
        public string AlphaMode { get; init; } = "OPAQUE";
        public List<string> PreservedChannels { get; init; } = new();
        public List<string> ExtensionsUsed { get; init; } = new();
        public List<string> Approximations { get; init; } = new();
        public List<string> IgnoredTextureSlots { get; init; } = new();
        public List<string> MissingTextures { get; init; } = new();

        [JsonIgnore]
        public bool HasApproximations =>
            Mode is MaterialConversionMode.PbrApproximation or MaterialConversionMode.BasicFallback
            || Approximations.Count > 0
            || IgnoredTextureSlots.Count > 0
            || MissingTextures.Count > 0;

        public ExportDiagnostic ToExportDiagnostic(string sourceFile = "", long pathId = 0)
        {
            var severity = Mode == MaterialConversionMode.BasicFallback || MissingTextures.Count > 0
                ? ExportDiagnosticSeverity.Warning
                : ExportDiagnosticSeverity.Info;
            var code = Mode == MaterialConversionMode.BasicFallback
                ? "MaterialBasicFallback"
                : "MaterialPbrApproximation";

            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(ShaderName))
            {
                parts.Add($"shader '{ShaderName}' ({Mode})");
            }
            else
            {
                parts.Add($"mode {Mode}");
            }

            if (Approximations.Count > 0)
            {
                parts.Add(string.Join("; ", Approximations));
            }
            if (IgnoredTextureSlots.Count > 0)
            {
                parts.Add($"unmapped texture slots: {string.Join(", ", IgnoredTextureSlots)}");
            }
            if (MissingTextures.Count > 0)
            {
                parts.Add($"missing textures: {string.Join(", ", MissingTextures)}");
            }

            return new ExportDiagnostic
            {
                Severity = severity,
                Outcome = ExportItemOutcome.Exported,
                Code = code,
                AssetName = MaterialName,
                AssetType = "Material",
                SourceFile = sourceFile ?? string.Empty,
                PathId = pathId,
                FieldOrResource = ShaderName,
                Message = $"Material '{MaterialName}': {string.Join(" | ", parts)}."
            };
        }
    }

    /// <summary>
    /// Diagnostic metadata describing the active preview rendering mode for a Shader or Material,
    /// explicitly aligned with export PBR conversion behavior.
    /// </summary>
    public sealed class ShaderPreviewDiagnosticInfo
    {
        public ShaderPreviewRenderMode RenderMode { get; init; } = ShaderPreviewRenderMode.ShaderLabText;
        public string BadgeLabel { get; init; } = "ShaderLab Text";
        public string ShaderName { get; init; } = string.Empty;
        public string ShaderFamily { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public string ExportAlignmentNote { get; init; } = string.Empty;

        public string BuildOverlayHeader()
        {
            var lines = new List<string>(4)
            {
                $"Preview Mode: {BadgeLabel}"
            };
            if (!string.IsNullOrWhiteSpace(ShaderFamily))
            {
                lines.Add($"Shader Family: {ShaderFamily}");
            }
            if (!string.IsNullOrWhiteSpace(Reason))
            {
                lines.Add($"Reason: {Reason}");
            }
            if (!string.IsNullOrWhiteSpace(ExportAlignmentNote))
            {
                lines.Add($"Export Note: {ExportAlignmentNote}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        public string BuildTooltip()
        {
            return BuildOverlayHeader();
        }
    }

    /// <summary>
    /// Shared shader family classifier used by both glTF PBR conversion diagnostics and
    /// Shader/Material preview fallback reporting so preview and export stay aligned.
    /// </summary>
    public static class ShaderConversionClassifier
    {
        public static bool IsStandardBuiltInPbrShader(string? shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            return string.Equals(shaderName, "Standard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(shaderName, "Standard (Roughness setup)", StringComparison.OrdinalIgnoreCase)
                || string.Equals(shaderName, "Autodesk Interactive", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsStandardSpecularShader(string? shaderName)
        {
            return !string.IsNullOrWhiteSpace(shaderName)
                && string.Equals(shaderName, "Standard (Specular setup)", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBuiltInUnlitShader(string? shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            return shaderName.StartsWith("Unlit/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Sprites/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("UI/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUrpShader(string? shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            return shaderName.StartsWith("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Lightweight Render Pipeline/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("URP/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsHdrpShader(string? shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return false;
            }

            return shaderName.StartsWith("HDRP/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("High Definition Render Pipeline/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsShaderGraphShader(string? shaderName)
        {
            return !string.IsNullOrWhiteSpace(shaderName)
                && (shaderName.StartsWith("Shader Graphs/", StringComparison.OrdinalIgnoreCase)
                    || shaderName.Contains("ShaderGraph", StringComparison.OrdinalIgnoreCase));
        }

        public static string DescribeShaderFamily(string? shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return "Unknown / Unresolved";
            }

            if (IsStandardBuiltInPbrShader(shaderName))
            {
                return "Built-in Standard (Metallic-Roughness)";
            }

            if (IsStandardSpecularShader(shaderName))
            {
                return "Built-in Standard (Specular)";
            }

            if (IsBuiltInUnlitShader(shaderName))
            {
                return "Built-in Unlit / UI / Sprite";
            }

            if (IsUrpShader(shaderName))
            {
                return "URP / LWRP";
            }

            if (IsHdrpShader(shaderName))
            {
                return "HDRP";
            }

            if (IsShaderGraphShader(shaderName))
            {
                return "Shader Graph";
            }

            if (shaderName.StartsWith("Legacy Shaders/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Mobile/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Nature/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Particles/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Skybox/", StringComparison.OrdinalIgnoreCase)
                || shaderName.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase))
            {
                return "Built-in Specialized / Legacy";
            }

            return "Custom Shader";
        }

        public static string BuildExportAlignmentNote(string? shaderName)
        {
            if (IsStandardBuiltInPbrShader(shaderName))
            {
                return "Exports cleanly to glTF 2.0 metallic-roughness PBR (base color, normal, metallic/smoothness, occlusion, emission).";
            }

            if (IsStandardSpecularShader(shaderName))
            {
                return "glTF export approximates specular-glossiness as metallic-roughness (metallic=0, roughness=1-gloss).";
            }

            if (IsBuiltInUnlitShader(shaderName))
            {
                return "Exports with KHR_materials_unlit in glTF 2.0.";
            }

            if (IsUrpShader(shaderName) || IsHdrpShader(shaderName) || IsShaderGraphShader(shaderName))
            {
                return "Custom/SRP shader programs are not converted; glTF export writes a metallic-roughness PBR approximation and Unity export falls back to Standard.";
            }

            if (string.IsNullOrWhiteSpace(shaderName))
            {
                return "Unresolved shader falls back to basic PBR channels on export.";
            }

            return "Custom shader math is not converted; glTF export approximates available PBR channels and records dropped slots in export diagnostics.";
        }

        public static ShaderPreviewDiagnosticInfo ClassifyPreview(
            string? shaderName,
            int[]? unityVersion,
            bool isWindows,
            bool d3d11Rendered,
            bool hasPreviewTexture,
            bool isMaterialPreview,
            string? d3d11FailureReason = null)
        {
            var resolvedName = string.IsNullOrWhiteSpace(shaderName) ? "<unresolved>" : shaderName.Trim();
            var family = DescribeShaderFamily(shaderName);
            var exportNote = BuildExportAlignmentNote(shaderName);

            if (d3d11Rendered)
            {
                return new ShaderPreviewDiagnosticInfo
                {
                    RenderMode = ShaderPreviewRenderMode.InterpretedD3D11Shader,
                    BadgeLabel = "D3D11 Shader",
                    ShaderName = resolvedName,
                    ShaderFamily = family,
                    Reason = "Rendered with Direct3D 11 compiled Built-in shader passes.",
                    ExportAlignmentNote = exportNote
                };
            }

            var fallbackCause = !string.IsNullOrWhiteSpace(d3d11FailureReason)
                ? d3d11FailureReason.Trim()
                : (!isWindows
                    ? "Direct3D 11 shader preview is only available on Windows."
                    : (unityVersion == null || !ShaderPreviewReader.SupportsVersion(unityVersion)
                        ? $"Direct3D 11 shader interpreter supports Unity 5.3, 2017.4, 2018.2, 2019.3, and 2020.3 Built-in shaders (asset version: {FormatVersion(unityVersion)})."
                        : "No supported Direct3D 11 Built-in preview pass was available."));

            if (isMaterialPreview)
            {
                if (hasPreviewTexture)
                {
                    return new ShaderPreviewDiagnosticInfo
                    {
                        RenderMode = ShaderPreviewRenderMode.MaterialPbrApproximation,
                        BadgeLabel = "PBR Approximation",
                        ShaderName = resolvedName,
                        ShaderFamily = family,
                        Reason = $"Showing OpenGL textured material approximation. {fallbackCause}",
                        ExportAlignmentNote = exportNote
                    };
                }

                return new ShaderPreviewDiagnosticInfo
                {
                    RenderMode = ShaderPreviewRenderMode.BasicFallback,
                    BadgeLabel = "Basic Fallback",
                    ShaderName = resolvedName,
                    ShaderFamily = family,
                    Reason = $"No previewable diffuse texture resolved; displaying material property summary. {fallbackCause}",
                    ExportAlignmentNote = exportNote
                };
            }

            return new ShaderPreviewDiagnosticInfo
            {
                RenderMode = ShaderPreviewRenderMode.ShaderLabText,
                BadgeLabel = "ShaderLab Text",
                ShaderName = resolvedName,
                ShaderFamily = family,
                Reason = fallbackCause,
                ExportAlignmentNote = exportNote
            };
        }

        private static string FormatVersion(int[]? version)
        {
            if (version == null || version.Length == 0)
            {
                return "unknown";
            }

            return string.Join(".", version.Take(3));
        }
    }
}
