using K4os.Compression.LZ4;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AssetStudio
{
    [Flags]
    public enum SerializedPropertyFlag : uint
    {
        None = 0,
        HideInInspector = 1 << 0,
        PerRendererData = 1 << 1,
        NoScaleOffset = 1 << 2,
        Normal = 1 << 3,
        HDR = 1 << 4,
        Gamma = 1 << 5,
        NonModifiableTextureData = 1 << 6,
        MainTexture = 1 << 7,
        MainColor = 1 << 8,
        Vector2 = 1 << 9,
        Vector3 = 1 << 10,
    }

    public static class ShaderConverter
    {
        private static readonly (SerializedPropertyFlag Flag, string Attribute)[] PropertyFlagAttributes =
        {
            (SerializedPropertyFlag.HideInInspector, "HideInInspector"),
            (SerializedPropertyFlag.PerRendererData, "PerRendererData"),
            (SerializedPropertyFlag.NoScaleOffset, "NoScaleOffset"),
            (SerializedPropertyFlag.Normal, "Normal"),
            (SerializedPropertyFlag.HDR, "HDR"),
            (SerializedPropertyFlag.Gamma, "Gamma"),
            (SerializedPropertyFlag.NonModifiableTextureData, "NonModifiableTextureData"),
            (SerializedPropertyFlag.MainTexture, "MainTexture"),
            (SerializedPropertyFlag.MainColor, "MainColor"),
            (SerializedPropertyFlag.Vector2, "Vector2"),
            (SerializedPropertyFlag.Vector3, "Vector3"),
        };
        public static string Convert(this Shader shader)
        {
            if (shader.m_SubProgramBlob != null) //5.3 - 5.4
            {
                var decompressedBytes = new byte[shader.decompressedSize];
                LZ4Codec.Decode(shader.m_SubProgramBlob, decompressedBytes);
                using (var blobReader = new BinaryReader(new MemoryStream(decompressedBytes)))
                {
                    var program = new ShaderProgram(blobReader, shader.version);
                    program.Read(blobReader, 0);
                    return header + program.Export(Encoding.UTF8.GetString(shader.m_Script ?? Array.Empty<byte>()));
                }
            }

            if (shader.compressedBlob != null) //5.5 and up
            {
                return header + ConvertSerializedShader(shader);
            }

            return header + Encoding.UTF8.GetString(shader.m_Script ?? Array.Empty<byte>());
        }

        private static string ConvertSerializedShader(Shader shader)
        {
            if (shader.m_ParsedForm == null || shader.compressedBlob == null)
            {
                return string.Empty;
            }

            var length = shader.platforms.Length;
            var shaderPrograms = new ShaderProgram?[length];
            CollectProgramBlobIndices(
                shader.m_ParsedForm,
                shader.platforms,
                out var executableIndicesByPlatform,
                out var parameterIndicesByPlatform,
                out var hasAnyProgramRefs);

            for (var i = 0; i < length; i++)
            {
                if (i >= shader.offsets.Length || i >= shader.compressedLengths.Length || i >= shader.decompressedLengths.Length)
                {
                    break;
                }

                var platformOffsets = shader.offsets[i];
                var platformCompressed = shader.compressedLengths[i];
                var platformDecompressed = shader.decompressedLengths[i];
                var segmentCount = Math.Min(platformOffsets.Length, Math.Min(platformCompressed.Length, platformDecompressed.Length));
                if (segmentCount == 0)
                {
                    continue;
                }

                // On Unity >= 2021.3.10f1, compressed blobs contain both executable subprograms and
                // parameter reflection blobs (m_ParameterBlobIndices). Only parse executable subprogram
                // entries when program references are present so parameter blobs never trigger read failures.
                HashSet<uint>? allowedIndices = null;
                HashSet<uint>? excludedIndices = parameterIndicesByPlatform[i].Count > 0 ? parameterIndicesByPlatform[i] : null;
                if (executableIndicesByPlatform[i].Count > 0)
                {
                    allowedIndices = executableIndicesByPlatform[i];
                }
                else if (hasAnyProgramRefs || excludedIndices != null)
                {
                    allowedIndices = executableIndicesByPlatform[i]; // empty: skip non-executable entries
                }

                for (var j = 0; j < segmentCount; j++)
                {
                    var offset = platformOffsets[j];
                    var compressedLength = platformCompressed[j];
                    var decompressedLength = platformDecompressed[j];
                    if (decompressedLength == 0 || (long)offset + compressedLength > shader.compressedBlob.Length)
                    {
                        continue;
                    }

                    var decompressedBytes = new byte[decompressedLength];
                    LZ4Codec.Decode(shader.compressedBlob, (int)offset, (int)compressedLength, decompressedBytes, 0, (int)decompressedLength);
                    using (var blobReader = new BinaryReader(new MemoryStream(decompressedBytes)))
                    {
                        if (j == 0)
                        {
                            shaderPrograms[i] = new ShaderProgram(blobReader, shader.version);
                        }
                        shaderPrograms[i]?.Read(blobReader, j, allowedIndices, excludedIndices);
                    }
                }
            }

            return ConvertSerializedShader(shader.m_ParsedForm, shader.platforms, shaderPrograms);
        }

        private static void CollectProgramBlobIndices(
            SerializedShader parsedForm,
            ShaderCompilerPlatform[] platforms,
            out HashSet<uint>[] executableIndicesByPlatform,
            out HashSet<uint>[] parameterIndicesByPlatform,
            out bool hasAnyProgramRefs)
        {
            var platformCount = platforms.Length;
            executableIndicesByPlatform = new HashSet<uint>[platformCount];
            parameterIndicesByPlatform = new HashSet<uint>[platformCount];
            for (var i = 0; i < platformCount; i++)
            {
                executableIndicesByPlatform[i] = new HashSet<uint>();
                parameterIndicesByPlatform[i] = new HashSet<uint>();
            }

            hasAnyProgramRefs = false;
            if (parsedForm.m_SubShaders == null || platformCount == 0)
            {
                return;
            }

            var unmatchedParameterIndices = new HashSet<uint>();

            foreach (var subShader in parsedForm.m_SubShaders)
            {
                if (subShader?.m_Passes == null)
                {
                    continue;
                }

                foreach (var pass in subShader.m_Passes)
                {
                    if (pass == null)
                    {
                        continue;
                    }

                    CollectStageBlobIndices(pass.progVertex, platforms, executableIndicesByPlatform, parameterIndicesByPlatform, unmatchedParameterIndices, ref hasAnyProgramRefs);
                    CollectStageBlobIndices(pass.progFragment, platforms, executableIndicesByPlatform, parameterIndicesByPlatform, unmatchedParameterIndices, ref hasAnyProgramRefs);
                    CollectStageBlobIndices(pass.progGeometry, platforms, executableIndicesByPlatform, parameterIndicesByPlatform, unmatchedParameterIndices, ref hasAnyProgramRefs);
                    CollectStageBlobIndices(pass.progHull, platforms, executableIndicesByPlatform, parameterIndicesByPlatform, unmatchedParameterIndices, ref hasAnyProgramRefs);
                    CollectStageBlobIndices(pass.progDomain, platforms, executableIndicesByPlatform, parameterIndicesByPlatform, unmatchedParameterIndices, ref hasAnyProgramRefs);
                    CollectStageBlobIndices(pass.progRayTracing, platforms, executableIndicesByPlatform, parameterIndicesByPlatform, unmatchedParameterIndices, ref hasAnyProgramRefs);
                }
            }

            if (unmatchedParameterIndices.Count > 0)
            {
                for (var i = 0; i < platformCount; i++)
                {
                    foreach (var paramIdx in unmatchedParameterIndices)
                    {
                        if (!executableIndicesByPlatform[i].Contains(paramIdx))
                        {
                            parameterIndicesByPlatform[i].Add(paramIdx);
                        }
                    }
                }
            }
        }

        private static void CollectStageBlobIndices(
            SerializedProgram? program,
            ShaderCompilerPlatform[] platforms,
            HashSet<uint>[] executableIndicesByPlatform,
            HashSet<uint>[] parameterIndicesByPlatform,
            HashSet<uint> unmatchedParameterIndices,
            ref bool hasAnyProgramRefs)
        {
            if (program == null)
            {
                return;
            }

            if (program.m_SubPrograms != null)
            {
                foreach (var subProgram in program.m_SubPrograms)
                {
                    if (subProgram == null)
                    {
                        continue;
                    }

                    hasAnyProgramRefs = true;
                    var platformIndex = FindPlatformIndex(platforms, subProgram.m_GpuProgramType);
                    if (platformIndex >= 0)
                    {
                        executableIndicesByPlatform[platformIndex].Add(subProgram.m_BlobIndex);
                    }
                    else if (platforms.Length == 1)
                    {
                        executableIndicesByPlatform[0].Add(subProgram.m_BlobIndex);
                    }
                }
            }

            var playerTiers = program.m_PlayerSubPrograms;
            var paramTiers = program.m_ParameterBlobIndices;
            if (playerTiers != null)
            {
                for (var tier = 0; tier < playerTiers.Length; tier++)
                {
                    var tierSubPrograms = playerTiers[tier];
                    var tierParamIndices = paramTiers != null && tier < paramTiers.Length ? paramTiers[tier] : null;
                    if (tierSubPrograms == null)
                    {
                        continue;
                    }

                    for (var k = 0; k < tierSubPrograms.Length; k++)
                    {
                        var playerSub = tierSubPrograms[k];
                        if (playerSub == null)
                        {
                            continue;
                        }

                        hasAnyProgramRefs = true;
                        var platformIndex = FindPlatformIndex(platforms, playerSub.m_GpuProgramType);
                        if (platformIndex < 0 && platforms.Length == 1)
                        {
                            platformIndex = 0;
                        }

                        if (platformIndex >= 0)
                        {
                            executableIndicesByPlatform[platformIndex].Add(playerSub.m_BlobIndex);
                            if (tierParamIndices != null && k < tierParamIndices.Length)
                            {
                                parameterIndicesByPlatform[platformIndex].Add(tierParamIndices[k]);
                            }
                        }
                        else if (tierParamIndices != null && k < tierParamIndices.Length)
                        {
                            unmatchedParameterIndices.Add(tierParamIndices[k]);
                        }
                    }
                }
            }

            if (paramTiers != null)
            {
                for (var tier = 0; tier < paramTiers.Length; tier++)
                {
                    var tierParams = paramTiers[tier];
                    if (tierParams == null || tierParams.Length == 0)
                    {
                        continue;
                    }

                    var matchedCount = playerTiers != null && tier < playerTiers.Length && playerTiers[tier] != null
                        ? playerTiers[tier].Length
                        : 0;
                    for (var k = matchedCount; k < tierParams.Length; k++)
                    {
                        unmatchedParameterIndices.Add(tierParams[k]);
                    }
                }
            }
        }

        private static int FindPlatformIndex(ShaderCompilerPlatform[] platforms, ShaderGpuProgramType programType)
        {
            for (var i = 0; i < platforms.Length; i++)
            {
                if (CheckGpuProgramUsable(platforms[i], programType))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ConvertSerializedShader(SerializedShader m_ParsedForm, ShaderCompilerPlatform[] platforms, ShaderProgram?[] shaderPrograms)
        {
            var sb = new StringBuilder();
            sb.Append($"Shader \"{m_ParsedForm.m_Name}\" {{\n");

            sb.Append(ConvertSerializedProperties(m_ParsedForm.m_PropInfo));

            var keywordNames = m_ParsedForm.m_KeywordNames ?? Array.Empty<string>();
            foreach (var m_SubShader in m_ParsedForm.m_SubShaders)
            {
                sb.Append(ConvertSerializedSubShader(m_SubShader, keywordNames, platforms, shaderPrograms));
            }

            if (!string.IsNullOrEmpty(m_ParsedForm.m_FallbackName))
            {
                sb.Append($"Fallback \"{m_ParsedForm.m_FallbackName}\"\n");
            }

            if (!string.IsNullOrEmpty(m_ParsedForm.m_CustomEditorName))
            {
                sb.Append($"CustomEditor \"{m_ParsedForm.m_CustomEditorName}\"\n");
            }

            if (m_ParsedForm.m_CustomEditorForRenderPipelines != null)
            {
                foreach (var entry in m_ParsedForm.m_CustomEditorForRenderPipelines)
                {
                    if (entry != null && (!string.IsNullOrEmpty(entry.customEditorName) || !string.IsNullOrEmpty(entry.renderPipelineType)))
                    {
                        sb.Append($"CustomEditorForRenderPipeline \"{entry.customEditorName}\" \"{entry.renderPipelineType}\"\n");
                    }
                }
            }

            if (m_ParsedForm.m_Dependencies != null)
            {
                foreach (var dep in m_ParsedForm.m_Dependencies)
                {
                    if (dep != null && (!string.IsNullOrEmpty(dep.from) || !string.IsNullOrEmpty(dep.to)))
                    {
                        sb.Append($"Dependency \"{dep.from}\" = \"{dep.to}\"\n");
                    }
                }
            }

            sb.Append("}");
            return sb.ToString();
        }

        private static string ConvertSerializedSubShader(SerializedSubShader m_SubShader, string[] keywordNames, ShaderCompilerPlatform[] platforms, ShaderProgram?[] shaderPrograms)
        {
            var sb = new StringBuilder();
            sb.Append("SubShader {\n");
            if (m_SubShader.m_LOD != 0)
            {
                sb.Append($" LOD {m_SubShader.m_LOD}\n");
            }

            sb.Append(ConvertSerializedTagMap(m_SubShader.m_Tags, 1));

            foreach (var m_Passe in m_SubShader.m_Passes)
            {
                sb.Append(ConvertSerializedPass(m_Passe, keywordNames, platforms, shaderPrograms));
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string ConvertSerializedPass(SerializedPass m_Passe, string[] keywordNames, ShaderCompilerPlatform[] platforms, ShaderProgram?[] shaderPrograms)
        {
            var sb = new StringBuilder();
            switch (m_Passe.m_Type)
            {
                case PassType.Normal:
                    sb.Append(" Pass ");
                    break;
                case PassType.Use:
                    sb.Append(" UsePass ");
                    break;
                case PassType.Grab:
                    sb.Append(" GrabPass ");
                    break;
            }
            if (m_Passe.m_Type == PassType.Use)
            {
                sb.Append($"\"{m_Passe.m_UseName}\"\n");
            }
            else
            {
                sb.Append("{\n");

                if (m_Passe.m_Type == PassType.Grab)
                {
                    if (!string.IsNullOrEmpty(m_Passe.m_TextureName))
                    {
                        sb.Append($"  \"{m_Passe.m_TextureName}\"\n");
                    }
                }
                else
                {
                    if (m_Passe.m_State.m_Tags.tags.Length == 0 && m_Passe.m_Tags?.tags.Length > 0)
                    {
                        sb.Append(ConvertSerializedTagMap(m_Passe.m_Tags, 2));
                    }

                    sb.Append(ConvertSerializedShaderState(m_Passe.m_State));

                    sb.Append(ConvertSerializedProgram(m_Passe.progVertex, "vp", keywordNames, platforms, shaderPrograms));
                    sb.Append(ConvertSerializedProgram(m_Passe.progFragment, "fp", keywordNames, platforms, shaderPrograms));
                    sb.Append(ConvertSerializedProgram(m_Passe.progGeometry, "gp", keywordNames, platforms, shaderPrograms));
                    sb.Append(ConvertSerializedProgram(m_Passe.progHull, "hp", keywordNames, platforms, shaderPrograms));
                    sb.Append(ConvertSerializedProgram(m_Passe.progDomain, "dp", keywordNames, platforms, shaderPrograms));
                    sb.Append(ConvertSerializedProgram(m_Passe.progRayTracing, "rtp", keywordNames, platforms, shaderPrograms));
                }
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        private static string ConvertSerializedProgram(
            SerializedProgram? program,
            string programStage,
            string[] keywordNames,
            ShaderCompilerPlatform[] platforms,
            ShaderProgram?[] shaderPrograms)
        {
            if (program == null)
            {
                return string.Empty;
            }

            var hasSubPrograms = program.m_SubPrograms != null && program.m_SubPrograms.Length > 0;
            var hasPlayerSubPrograms = program.m_PlayerSubPrograms != null
                && program.m_PlayerSubPrograms.Any(static tier => tier != null && tier.Length > 0);
            if (!hasSubPrograms && !hasPlayerSubPrograms)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.Append($"Program \"{programStage}\" {{\n");
            HashSet<(int PlatformIndex, uint BlobIndex)>? exported = null;
            if (hasSubPrograms)
            {
                if (hasPlayerSubPrograms)
                {
                    exported = new HashSet<(int, uint)>();
                }
                sb.Append(ConvertSerializedSubPrograms(program.m_SubPrograms!, platforms, shaderPrograms, exported));
            }
            if (hasPlayerSubPrograms)
            {
                sb.Append(ConvertSerializedPlayerSubPrograms(program.m_PlayerSubPrograms!, keywordNames, platforms, shaderPrograms, exported));
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string ConvertSerializedSubPrograms(
            SerializedSubProgram[] m_SubPrograms,
            ShaderCompilerPlatform[] platforms,
            ShaderProgram?[] shaderPrograms,
            HashSet<(int PlatformIndex, uint BlobIndex)>? exported = null)
        {
            var sb = new StringBuilder();
            var groups = m_SubPrograms.GroupBy(x => x.m_BlobIndex);
            foreach (var group in groups)
            {
                var programs = group.GroupBy(x => x.m_GpuProgramType);
                foreach (var program in programs)
                {
                    for (int i = 0; i < platforms.Length; i++)
                    {
                        var platform = platforms[i];
                        if (CheckGpuProgramUsable(platform, program.Key))
                        {
                            var subPrograms = program.ToList();
                            var isTier = subPrograms.Count > 1;
                            foreach (var subProgram in subPrograms)
                            {
                                exported?.Add((i, subProgram.m_BlobIndex));
                                sb.Append($"SubProgram \"{GetPlatformString(platform)} ");
                                if (isTier)
                                {
                                    sb.Append($"hw_tier{subProgram.m_ShaderHardwareTier:00} ");
                                }
                                sb.Append("\" {\n");
                                sb.Append(ExportSubProgramOrFallback(shaderPrograms, i, subProgram.m_BlobIndex, null, Array.Empty<string>()));
                                sb.Append("\n}\n");
                            }
                            break;
                        }
                    }
                }
            }
            return sb.ToString();
        }

        private static string ConvertSerializedPlayerSubPrograms(
            SerializedPlayerSubProgram[][] playerSubPrograms,
            string[] keywordNames,
            ShaderCompilerPlatform[] platforms,
            ShaderProgram?[] shaderPrograms,
            HashSet<(int PlatformIndex, uint BlobIndex)>? alreadyExported)
        {
            var sb = new StringBuilder();
            var nonEmptyTierCount = 0;
            for (int t = 0; t < playerSubPrograms.Length; t++)
            {
                if (playerSubPrograms[t] != null && playerSubPrograms[t].Length > 0)
                {
                    nonEmptyTierCount++;
                }
            }

            var isTier = nonEmptyTierCount > 1;
            for (int tier = 0; tier < playerSubPrograms.Length; tier++)
            {
                var tierSubPrograms = playerSubPrograms[tier];
                if (tierSubPrograms == null || tierSubPrograms.Length == 0)
                {
                    continue;
                }

                foreach (var playerSub in tierSubPrograms)
                {
                    if (playerSub == null)
                    {
                        continue;
                    }

                    var platformIndex = FindPlatformIndex(platforms, playerSub.m_GpuProgramType);
                    if (platformIndex < 0 && platforms.Length == 1)
                    {
                        platformIndex = 0;
                    }

                    if (platformIndex < 0)
                    {
                        continue;
                    }

                    if (alreadyExported != null && alreadyExported.Contains((platformIndex, playerSub.m_BlobIndex)))
                    {
                        continue;
                    }

                    sb.Append($"SubProgram \"{GetPlatformString(platforms[platformIndex])} ");
                    if (isTier)
                    {
                        sb.Append($"hw_tier{tier:00} ");
                    }
                    sb.Append("\" {\n");
                    sb.Append(ExportSubProgramOrFallback(shaderPrograms, platformIndex, playerSub.m_BlobIndex, playerSub.m_KeywordIndices, keywordNames));
                    sb.Append("\n}\n");
                }
            }

            return sb.ToString();
        }

        private static string ExportSubProgramOrFallback(
            ShaderProgram?[] shaderPrograms,
            int platformIndex,
            uint blobIndex,
            ushort[]? fallbackKeywordIndices,
            string[] keywordNames)
        {
            if (platformIndex < 0 || platformIndex >= shaderPrograms.Length)
            {
                return "\"// subprogram blob unavailable\"";
            }

            var shaderProgram = shaderPrograms[platformIndex];
            if (shaderProgram == null || blobIndex >= (uint)shaderProgram.m_SubPrograms.Length)
            {
                return "\"// subprogram blob unavailable\"";
            }

            var subProgram = shaderProgram.m_SubPrograms[blobIndex];
            if (subProgram == null)
            {
                return "\"// subprogram blob unavailable\"";
            }

            var exported = subProgram.Export();
            if (subProgram.m_Keywords.Length == 0
                && fallbackKeywordIndices != null
                && fallbackKeywordIndices.Length > 0
                && keywordNames.Length > 0)
            {
                var kwBuilder = new StringBuilder();
                var anyKeyword = false;
                foreach (var kwIndex in fallbackKeywordIndices)
                {
                    if (kwIndex < keywordNames.Length && !string.IsNullOrEmpty(keywordNames[kwIndex]))
                    {
                        if (!anyKeyword)
                        {
                            kwBuilder.Append("Keywords { ");
                            anyKeyword = true;
                        }
                        kwBuilder.Append($"\"{keywordNames[kwIndex]}\" ");
                    }
                }
                if (anyKeyword)
                {
                    kwBuilder.Append("}\n");
                    kwBuilder.Append(exported);
                    return kwBuilder.ToString();
                }
            }

            return exported;
        }

        private static string ConvertSerializedShaderState(SerializedShaderState m_State)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(m_State.m_Name))
            {
                sb.Append($"  Name \"{m_State.m_Name}\"\n");
            }
            if (m_State.m_LOD != 0)
            {
                sb.Append($"  LOD {m_State.m_LOD}\n");
            }

            sb.Append(ConvertSerializedTagMap(m_State.m_Tags, 2));

            sb.Append(ConvertSerializedShaderRTBlendState(m_State.rtBlend, m_State.rtSeparateBlend));

            if (HasValidPropertyName(m_State.alphaToMask?.name))
            {
                sb.Append($"  AlphaToMask [{m_State.alphaToMask!.name}]\n");
            }
            else if (m_State.alphaToMask?.val > 0f)
            {
                sb.Append("  AlphaToMask On\n");
            }

            if (HasValidPropertyName(m_State.zClip?.name))
            {
                sb.Append($"  ZClip [{m_State.zClip!.name}]\n");
            }
            else if (m_State.zClip != null && m_State.zClip.val != 1f) //ZClip On
            {
                sb.Append("  ZClip Off\n");
            }

            if (HasValidPropertyName(m_State.zTest?.name))
            {
                sb.Append($"  ZTest [{m_State.zTest!.name}]\n");
            }
            else if (m_State.zTest != null && m_State.zTest.val != 4f) //ZTest LEqual
            {
                sb.Append("  ZTest ");
                switch (m_State.zTest.val) //enum CompareFunction
                {
                    case 0f: //kFuncDisabled
                        sb.Append("Off");
                        break;
                    case 1f: //kFuncNever
                        sb.Append("Never");
                        break;
                    case 2f: //kFuncLess
                        sb.Append("Less");
                        break;
                    case 3f: //kFuncEqual
                        sb.Append("Equal");
                        break;
                    case 5f: //kFuncGreater
                        sb.Append("Greater");
                        break;
                    case 6f: //kFuncNotEqual
                        sb.Append("NotEqual");
                        break;
                    case 7f: //kFuncGEqual
                        sb.Append("GEqual");
                        break;
                    case 8f: //kFuncAlways
                        sb.Append("Always");
                        break;
                }

                sb.Append("\n");
            }

            if (HasValidPropertyName(m_State.zWrite?.name))
            {
                sb.Append($"  ZWrite [{m_State.zWrite!.name}]\n");
            }
            else if (m_State.zWrite != null && m_State.zWrite.val != 1f) //ZWrite On
            {
                sb.Append("  ZWrite Off\n");
            }

            if (HasValidPropertyName(m_State.culling?.name))
            {
                sb.Append($"  Cull [{m_State.culling!.name}]\n");
            }
            else if (m_State.culling != null && m_State.culling.val != 2f) //Cull Back
            {
                sb.Append("  Cull ");
                switch (m_State.culling.val) //enum CullMode
                {
                    case 0f: //kCullOff
                        sb.Append("Off");
                        break;
                    case 1f: //kCullFront
                        sb.Append("Front");
                        break;
                }
                sb.Append("\n");
            }

            if (HasValidPropertyName(m_State.conservative?.name))
            {
                sb.Append($"  Conservative [{m_State.conservative!.name}]\n");
            }
            else if (m_State.conservative != null && m_State.conservative.val > 0f)
            {
                sb.Append("  Conservative True\n");
            }

            if (HasShaderFloatValue(m_State.offsetFactor, 0f) || HasShaderFloatValue(m_State.offsetUnits, 0f))
            {
                sb.Append($"  Offset {FormatShaderFloatValue(m_State.offsetFactor)}, {FormatShaderFloatValue(m_State.offsetUnits)}\n");
            }

            if (HasShaderFloatValue(m_State.stencilRef, 0f) ||
                HasShaderFloatValue(m_State.stencilReadMask, 255f) ||
                HasShaderFloatValue(m_State.stencilWriteMask, 255f) ||
                HasStencilOpValues(m_State.stencilOp) ||
                HasStencilOpValues(m_State.stencilOpFront) ||
                HasStencilOpValues(m_State.stencilOpBack))
            {
                sb.Append("  Stencil {\n");
                if (HasShaderFloatValue(m_State.stencilRef, 0f))
                {
                    sb.Append($"   Ref {FormatShaderFloatValue(m_State.stencilRef)}\n");
                }
                if (HasShaderFloatValue(m_State.stencilReadMask, 255f))
                {
                    sb.Append($"   ReadMask {FormatShaderFloatValue(m_State.stencilReadMask)}\n");
                }
                if (HasShaderFloatValue(m_State.stencilWriteMask, 255f))
                {
                    sb.Append($"   WriteMask {FormatShaderFloatValue(m_State.stencilWriteMask)}\n");
                }
                if (HasStencilOpValues(m_State.stencilOp))
                {
                    sb.Append(ConvertSerializedStencilOp(m_State.stencilOp, ""));
                }
                if (HasStencilOpValues(m_State.stencilOpFront))
                {
                    sb.Append(ConvertSerializedStencilOp(m_State.stencilOpFront, "Front"));
                }
                if (HasStencilOpValues(m_State.stencilOpBack))
                {
                    sb.Append(ConvertSerializedStencilOp(m_State.stencilOpBack, "Back"));
                }
                sb.Append("  }\n");
            }

            if (m_State.fogMode != FogMode.Unknown ||
                m_State.fogColor.x.val != 0f ||
                m_State.fogColor.y.val != 0f ||
                m_State.fogColor.z.val != 0f ||
                m_State.fogColor.w.val != 0f ||
                m_State.fogDensity.val != 0f ||
                m_State.fogStart.val != 0f ||
                m_State.fogEnd.val != 0f)
            {
                sb.Append("  Fog {\n");
                if (m_State.fogMode != FogMode.Unknown)
                {
                    sb.Append("   Mode ");
                    switch (m_State.fogMode)
                    {
                        case FogMode.Disabled:
                            sb.Append("Off");
                            break;
                        case FogMode.Linear:
                            sb.Append("Linear");
                            break;
                        case FogMode.Exp:
                            sb.Append("Exp");
                            break;
                        case FogMode.Exp2:
                            sb.Append("Exp2");
                            break;
                    }
                    sb.Append("\n");
                }
                if (m_State.fogColor.x.val != 0f ||
                    m_State.fogColor.y.val != 0f ||
                    m_State.fogColor.z.val != 0f ||
                    m_State.fogColor.w.val != 0f)
                {
                    sb.AppendFormat("   Color ({0},{1},{2},{3})\n",
                        m_State.fogColor.x.val.ToString(CultureInfo.InvariantCulture),
                        m_State.fogColor.y.val.ToString(CultureInfo.InvariantCulture),
                        m_State.fogColor.z.val.ToString(CultureInfo.InvariantCulture),
                        m_State.fogColor.w.val.ToString(CultureInfo.InvariantCulture));
                }
                if (m_State.fogDensity.val != 0f)
                {
                    sb.Append($"   Density {m_State.fogDensity.val.ToString(CultureInfo.InvariantCulture)}\n");
                }
                if (m_State.fogStart.val != 0f ||
                    m_State.fogEnd.val != 0f)
                {
                    sb.Append($"   Range {m_State.fogStart.val.ToString(CultureInfo.InvariantCulture)}, {m_State.fogEnd.val.ToString(CultureInfo.InvariantCulture)}\n");
                }
                sb.Append("  }\n");
            }

            if (m_State.lighting)
            {
                sb.Append($"  Lighting {(m_State.lighting ? "On" : "Off")}\n");
            }

            sb.Append($"  GpuProgramID {m_State.gpuProgramID}\n");

            return sb.ToString();
        }

        private static bool HasValidPropertyName(string? name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, "<noninit>", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasShaderFloatValue(SerializedShaderFloatValue value, float defaultVal)
        {
            return value != null && (HasValidPropertyName(value.name) || value.val != defaultVal);
        }

        private static string FormatShaderFloatValue(SerializedShaderFloatValue value)
        {
            return HasValidPropertyName(value.name)
                ? $"[{value.name}]"
                : value.val.ToString(CultureInfo.InvariantCulture);
        }

        private static bool HasStencilOpValues(SerializedStencilOp stencilOp)
        {
            return HasShaderFloatValue(stencilOp.pass, 0f) ||
                   HasShaderFloatValue(stencilOp.fail, 0f) ||
                   HasShaderFloatValue(stencilOp.zFail, 0f) ||
                   HasShaderFloatValue(stencilOp.comp, 8f);
        }

        private static string ConvertSerializedStencilOp(SerializedStencilOp stencilOp, string suffix)
        {
            var sb = new StringBuilder();
            sb.Append($"   Comp{suffix} {ConvertStencilComp(stencilOp.comp)}\n");
            sb.Append($"   Pass{suffix} {ConvertStencilOp(stencilOp.pass)}\n");
            sb.Append($"   Fail{suffix} {ConvertStencilOp(stencilOp.fail)}\n");
            sb.Append($"   ZFail{suffix} {ConvertStencilOp(stencilOp.zFail)}\n");
            return sb.ToString();
        }

        private static string ConvertStencilOp(SerializedShaderFloatValue op)
        {
            if (HasValidPropertyName(op.name))
            {
                return $"[{op.name}]";
            }

            switch (op.val)
            {
                case 0f:
                default:
                    return "Keep";
                case 1f:
                    return "Zero";
                case 2f:
                    return "Replace";
                case 3f:
                    return "IncrSat";
                case 4f:
                    return "DecrSat";
                case 5f:
                    return "Invert";
                case 6f:
                    return "IncrWrap";
                case 7f:
                    return "DecrWrap";
            }
        }

        private static string ConvertStencilComp(SerializedShaderFloatValue comp)
        {
            if (HasValidPropertyName(comp.name))
            {
                return $"[{comp.name}]";
            }

            switch (comp.val)
            {
                case 0f:
                    return "Disabled";
                case 1f:
                    return "Never";
                case 2f:
                    return "Less";
                case 3f:
                    return "Equal";
                case 4f:
                    return "LEqual";
                case 5f:
                    return "Greater";
                case 6f:
                    return "NotEqual";
                case 7f:
                    return "GEqual";
                case 8f:
                default:
                    return "Always";
            }
        }

        private static string ConvertSerializedShaderRTBlendState(SerializedShaderRTBlendState[] rtBlend, bool rtSeparateBlend)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < rtBlend.Length; i++)
            {
                var blend = rtBlend[i];
                if (HasShaderFloatValue(blend.srcBlend, 1f) ||
                    HasShaderFloatValue(blend.destBlend, 0f) ||
                    HasShaderFloatValue(blend.srcBlendAlpha, 1f) ||
                    HasShaderFloatValue(blend.destBlendAlpha, 0f))
                {
                    sb.Append("  Blend ");
                    if (i != 0 || rtSeparateBlend)
                    {
                        sb.Append($"{i} ");
                    }
                    sb.Append($"{ConvertBlendFactor(blend.srcBlend)} {ConvertBlendFactor(blend.destBlend)}");
                    if (HasShaderFloatValue(blend.srcBlendAlpha, 1f) ||
                        HasShaderFloatValue(blend.destBlendAlpha, 0f))
                    {
                        sb.Append($", {ConvertBlendFactor(blend.srcBlendAlpha)} {ConvertBlendFactor(blend.destBlendAlpha)}");
                    }
                    sb.Append("\n");
                }

                if (HasShaderFloatValue(blend.blendOp, 0f) ||
                    HasShaderFloatValue(blend.blendOpAlpha, 0f))
                {
                    sb.Append("  BlendOp ");
                    if (i != 0 || rtSeparateBlend)
                    {
                        sb.Append($"{i} ");
                    }
                    sb.Append(ConvertBlendOp(blend.blendOp));
                    if (HasShaderFloatValue(blend.blendOpAlpha, 0f))
                    {
                        sb.Append($", {ConvertBlendOp(blend.blendOpAlpha)}");
                    }
                    sb.Append("\n");
                }

                if (HasValidPropertyName(blend.colMask?.name))
                {
                    sb.Append($"  ColorMask [{blend.colMask!.name}] {i}\n");
                }
                else if (blend.colMask != null)
                {
                    var val = (int)blend.colMask.val;
                    if (val != 0xf)
                    {
                        sb.Append("  ColorMask ");
                        if (val == 0)
                        {
                            sb.Append(0);
                        }
                        else
                        {
                            if ((val & 0x2) != 0)
                            {
                                sb.Append("R");
                            }
                            if ((val & 0x4) != 0)
                            {
                                sb.Append("G");
                            }
                            if ((val & 0x8) != 0)
                            {
                                sb.Append("B");
                            }
                            if ((val & 0x1) != 0)
                            {
                                sb.Append("A");
                            }
                        }
                        sb.Append($" {i}\n");
                    }
                }
            }
            return sb.ToString();
        }

        private static string ConvertBlendOp(SerializedShaderFloatValue op)
        {
            if (HasValidPropertyName(op.name))
            {
                return $"[{op.name}]";
            }

            switch (op.val)
            {
                case 0f:
                default:
                    return "Add";
                case 1f:
                    return "Sub";
                case 2f:
                    return "RevSub";
                case 3f:
                    return "Min";
                case 4f:
                    return "Max";
                case 5f:
                    return "LogicalClear";
                case 6f:
                    return "LogicalSet";
                case 7f:
                    return "LogicalCopy";
                case 8f:
                    return "LogicalCopyInverted";
                case 9f:
                    return "LogicalNoop";
                case 10f:
                    return "LogicalInvert";
                case 11f:
                    return "LogicalAnd";
                case 12f:
                    return "LogicalNand";
                case 13f:
                    return "LogicalOr";
                case 14f:
                    return "LogicalNor";
                case 15f:
                    return "LogicalXor";
                case 16f:
                    return "LogicalEquiv";
                case 17f:
                    return "LogicalAndReverse";
                case 18f:
                    return "LogicalAndInverted";
                case 19f:
                    return "LogicalOrReverse";
                case 20f:
                    return "LogicalOrInverted";
            }
        }

        private static string ConvertBlendFactor(SerializedShaderFloatValue factor)
        {
            if (HasValidPropertyName(factor.name))
            {
                return $"[{factor.name}]";
            }

            switch (factor.val)
            {
                case 0f:
                    return "Zero";
                case 1f:
                default:
                    return "One";
                case 2f:
                    return "DstColor";
                case 3f:
                    return "SrcColor";
                case 4f:
                    return "OneMinusDstColor";
                case 5f:
                    return "SrcAlpha";
                case 6f:
                    return "OneMinusSrcColor";
                case 7f:
                    return "DstAlpha";
                case 8f:
                    return "OneMinusDstAlpha";
                case 9f:
                    return "SrcAlphaSaturate";
                case 10f:
                    return "OneMinusSrcAlpha";
            }
        }

        private static string ConvertSerializedTagMap(SerializedTagMap m_Tags, int intent)
        {
            var sb = new StringBuilder();
            if (m_Tags.tags.Length > 0)
            {
                sb.Append(new string(' ', intent));
                sb.Append("Tags { ");
                foreach (var pair in m_Tags.tags)
                {
                    sb.Append($"\"{pair.Key}\" = \"{pair.Value}\" ");
                }
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        private static string ConvertSerializedProperties(SerializedProperties m_PropInfo)
        {
            var sb = new StringBuilder();
            sb.Append("Properties {\n");
            foreach (var m_Prop in m_PropInfo.m_Props)
            {
                sb.Append(ConvertSerializedProperty(m_Prop));
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        public static string FormatPropertyFlags(uint rawFlags, string[]? existingAttributes = null)
        {
            var flags = (SerializedPropertyFlag)rawFlags;
            if (flags == SerializedPropertyFlag.None)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var (flag, attributeName) in PropertyFlagAttributes)
            {
                if ((flags & flag) != 0 && (existingAttributes == null || Array.IndexOf(existingAttributes, attributeName) < 0))
                {
                    sb.Append($"[{attributeName}] ");
                }
            }
            return sb.ToString();
        }

        private static string ConvertSerializedProperty(SerializedProperty m_Prop)
        {
            var sb = new StringBuilder();
            var attributes = m_Prop.m_Attributes;
            if (attributes != null)
            {
                foreach (var m_Attribute in attributes)
                {
                    if (!string.IsNullOrEmpty(m_Attribute))
                    {
                        sb.Append($"[{m_Attribute}] ");
                    }
                }
            }
            sb.Append(FormatPropertyFlags(m_Prop.m_Flags, attributes));
            sb.Append($"{m_Prop.m_Name} (\"{m_Prop.m_Description}\", ");
            switch (m_Prop.m_Type)
            {
                case SerializedPropertyType.Color:
                    sb.Append("Color");
                    break;
                case SerializedPropertyType.Vector:
                    sb.Append("Vector");
                    break;
                case SerializedPropertyType.Float:
                    sb.Append("Float");
                    break;
                case SerializedPropertyType.Range:
                    sb.Append($"Range({m_Prop.m_DefValue[1].ToString(CultureInfo.InvariantCulture)}, {m_Prop.m_DefValue[2].ToString(CultureInfo.InvariantCulture)})");
                    break;
                case SerializedPropertyType.Texture:
                    switch (m_Prop.m_DefTexture.m_TexDim)
                    {
                        case TextureDimension.Any:
                            sb.Append("any");
                            break;
                        case TextureDimension.Tex2D:
                            sb.Append("2D");
                            break;
                        case TextureDimension.Tex3D:
                            sb.Append("3D");
                            break;
                        case TextureDimension.Cube:
                            sb.Append("Cube");
                            break;
                        case TextureDimension.Tex2DArray:
                            sb.Append("2DArray");
                            break;
                        case TextureDimension.CubeArray:
                            sb.Append("CubeArray");
                            break;
                    }
                    break;
                case SerializedPropertyType.Int:
                    sb.Append("Int");
                    break;
            }
            sb.Append(") = ");
            switch (m_Prop.m_Type)
            {
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Vector:
                    sb.Append($"({m_Prop.m_DefValue[0].ToString(CultureInfo.InvariantCulture)},{m_Prop.m_DefValue[1].ToString(CultureInfo.InvariantCulture)},{m_Prop.m_DefValue[2].ToString(CultureInfo.InvariantCulture)},{m_Prop.m_DefValue[3].ToString(CultureInfo.InvariantCulture)})");
                    break;
                case SerializedPropertyType.Float:
                case SerializedPropertyType.Range:
                    sb.Append(m_Prop.m_DefValue[0].ToString(CultureInfo.InvariantCulture));
                    break;
                case SerializedPropertyType.Texture:
                    sb.Append($"\"{m_Prop.m_DefTexture.m_DefaultName}\" {{ }}");
                    break;
                case SerializedPropertyType.Int:
                    sb.Append(((int)m_Prop.m_DefValue[0]).ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            sb.Append("\n");
            return sb.ToString();
        }

        private static bool CheckGpuProgramUsable(ShaderCompilerPlatform platform, ShaderGpuProgramType programType)
        {
            switch (platform)
            {
                case ShaderCompilerPlatform.GL:
                    return programType == ShaderGpuProgramType.GLLegacy;
                case ShaderCompilerPlatform.D3D9:
                    return programType == ShaderGpuProgramType.DX9VertexSM20
                        || programType == ShaderGpuProgramType.DX9VertexSM30
                        || programType == ShaderGpuProgramType.DX9PixelSM20
                        || programType == ShaderGpuProgramType.DX9PixelSM30;
                case ShaderCompilerPlatform.Xbox360:
                case ShaderCompilerPlatform.PS3:
                case ShaderCompilerPlatform.PSP2:
                case ShaderCompilerPlatform.PS4:
                case ShaderCompilerPlatform.XboxOne:
                case ShaderCompilerPlatform.N3DS:
                case ShaderCompilerPlatform.WiiU:
                case ShaderCompilerPlatform.Switch:
                case ShaderCompilerPlatform.XboxOneD3D12:
                case ShaderCompilerPlatform.GameCoreXboxOne:
                case ShaderCompilerPlatform.GameCoreScarlett:
                case ShaderCompilerPlatform.PS5:
                    return programType == ShaderGpuProgramType.ConsoleVS
                        || programType == ShaderGpuProgramType.ConsoleFS
                        || programType == ShaderGpuProgramType.ConsoleHS
                        || programType == ShaderGpuProgramType.ConsoleDS
                        || programType == ShaderGpuProgramType.ConsoleGS
                        || programType == ShaderGpuProgramType.RayTracing;
                case ShaderCompilerPlatform.PS5NGGC:
                    return programType == ShaderGpuProgramType.PS5NGGC;
                case ShaderCompilerPlatform.D3D11:
                    return programType == ShaderGpuProgramType.DX11VertexSM40
                        || programType == ShaderGpuProgramType.DX11VertexSM50
                        || programType == ShaderGpuProgramType.DX11PixelSM40
                        || programType == ShaderGpuProgramType.DX11PixelSM50
                        || programType == ShaderGpuProgramType.DX11GeometrySM40
                        || programType == ShaderGpuProgramType.DX11GeometrySM50
                        || programType == ShaderGpuProgramType.DX11HullSM50
                        || programType == ShaderGpuProgramType.DX11DomainSM50
                        || programType == ShaderGpuProgramType.RayTracing;
                case ShaderCompilerPlatform.GLES20:
                    return programType == ShaderGpuProgramType.GLES;
                case ShaderCompilerPlatform.NaCl: //Obsolete
                    return false;
                case ShaderCompilerPlatform.Flash: //Obsolete
                    return false;
                case ShaderCompilerPlatform.D3D11_9x:
                    return programType == ShaderGpuProgramType.DX10Level9Vertex
                        || programType == ShaderGpuProgramType.DX10Level9Pixel;
                case ShaderCompilerPlatform.GLES3Plus:
                    return programType == ShaderGpuProgramType.GLES31AEP
                        || programType == ShaderGpuProgramType.GLES31
                        || programType == ShaderGpuProgramType.GLES3;
                case ShaderCompilerPlatform.PSM: //Unknown
                    return false;
                case ShaderCompilerPlatform.Metal:
                    return programType == ShaderGpuProgramType.MetalVS
                        || programType == ShaderGpuProgramType.MetalFS;
                case ShaderCompilerPlatform.OpenGLCore:
                    return programType == ShaderGpuProgramType.GLCore32
                        || programType == ShaderGpuProgramType.GLCore41
                        || programType == ShaderGpuProgramType.GLCore43;
                case ShaderCompilerPlatform.Vulkan:
                    return programType == ShaderGpuProgramType.SPIRV
                        || programType == ShaderGpuProgramType.RayTracing;
                default:
                    return false;
            }
        }

        public static string GetPlatformString(ShaderCompilerPlatform platform)
        {
            switch (platform)
            {
                case ShaderCompilerPlatform.GL:
                    return "openGL";
                case ShaderCompilerPlatform.D3D9:
                    return "d3d9";
                case ShaderCompilerPlatform.Xbox360:
                    return "xbox360";
                case ShaderCompilerPlatform.PS3:
                    return "ps3";
                case ShaderCompilerPlatform.D3D11:
                    return "d3d11";
                case ShaderCompilerPlatform.GLES20:
                    return "gles";
                case ShaderCompilerPlatform.NaCl:
                    return "glesdesktop";
                case ShaderCompilerPlatform.Flash:
                    return "flash";
                case ShaderCompilerPlatform.D3D11_9x:
                    return "d3d11_9x";
                case ShaderCompilerPlatform.GLES3Plus:
                    return "gles3";
                case ShaderCompilerPlatform.PSP2:
                    return "psp2";
                case ShaderCompilerPlatform.PS4:
                    return "ps4";
                case ShaderCompilerPlatform.XboxOne:
                    return "xboxone";
                case ShaderCompilerPlatform.PSM:
                    return "psm";
                case ShaderCompilerPlatform.Metal:
                    return "metal";
                case ShaderCompilerPlatform.OpenGLCore:
                    return "glcore";
                case ShaderCompilerPlatform.N3DS:
                    return "n3ds";
                case ShaderCompilerPlatform.WiiU:
                    return "wiiu";
                case ShaderCompilerPlatform.Vulkan:
                    return "vulkan";
                case ShaderCompilerPlatform.Switch:
                    return "switch";
                case ShaderCompilerPlatform.XboxOneD3D12:
                    return "xboxone_d3d12";
                case ShaderCompilerPlatform.GameCoreXboxOne:
                    return "xboxone";
                case ShaderCompilerPlatform.GameCoreScarlett:
                    return "xbox_scarlett";
                case ShaderCompilerPlatform.PS5:
                    return "ps5";
                case ShaderCompilerPlatform.PS5NGGC:
                    return "ps5_nggc";
                default:
                    return "unknown";
            }
        }

        private static string header = "//////////////////////////////////////////\n" +
                                      "//\n" +
                                      "// NOTE: This is *not* a valid shader file\n" +
                                      "//\n" +
                                      "///////////////////////////////////////////\n";
    }

    public class ShaderSubProgramEntry
    {
        public int Offset;
        public int Length;
        public int Segment;

        private static bool HasSegmentIndex(int[] version) => version[0] > 2019 || (version[0] == 2019 && version[1] >= 3);

        internal static int SerializedSize(int[] version) => (HasSegmentIndex(version) ? 3 : 2) * sizeof(int);

        public ShaderSubProgramEntry(BinaryReader reader, int[] version)
        {
            Offset = reader.ReadInt32();
            Length = reader.ReadInt32();
            if (HasSegmentIndex(version)) //2019.3 and up
            {
                Segment = reader.ReadInt32();
            }
        }
    }

    public class ShaderProgram
    {
        public ShaderSubProgramEntry[] entries;
        public ShaderSubProgram?[] m_SubPrograms;

        public ShaderProgram(BinaryReader reader, int[] version)
        {
            var subProgramsCapacity = reader.ReadInt32();
            if (subProgramsCapacity < 0)
            {
                throw new InvalidDataException($"Invalid shader subprogram entry count: {subProgramsCapacity}");
            }
            entries = new ShaderSubProgramEntry[subProgramsCapacity];
            for (int i = 0; i < subProgramsCapacity; i++)
            {
                entries[i] = new ShaderSubProgramEntry(reader, version);
            }
            m_SubPrograms = new ShaderSubProgram?[subProgramsCapacity];
        }

        public void Read(BinaryReader reader, int segment)
        {
            Read(reader, segment, null, null);
        }

        public void Read(BinaryReader reader, int segment, HashSet<uint>? allowedIndices, HashSet<uint>? excludedIndices = null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (allowedIndices != null && !allowedIndices.Contains((uint)i))
                {
                    continue;
                }
                if (excludedIndices != null && excludedIndices.Contains((uint)i))
                {
                    continue;
                }

                var entry = entries[i];
                if (entry.Segment == segment)
                {
                    if (entry.Offset < 0 || entry.Length < 0 || (long)entry.Offset + entry.Length > reader.BaseStream.Length)
                    {
                        continue;
                    }

                    reader.BaseStream.Position = entry.Offset;
                    try
                    {
                        m_SubPrograms[i] = new ShaderSubProgram(reader, (long)entry.Offset + entry.Length);
                    }
                    catch (Exception ex) when (ex is EndOfStreamException || ex is OverflowException || ex is InvalidDataException)
                    {
                        // Skip non-subprogram / unsupported future blob entries rather than failing the whole shader.
                        m_SubPrograms[i] = null;
                    }
                }
            }
        }

        public string Export(string shader)
        {
            var evaluator = new MatchEvaluator(match =>
            {
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index < 0 || index >= m_SubPrograms.Length || m_SubPrograms[index] == null)
                {
                    return "\"// subprogram blob unavailable\"";
                }
                return m_SubPrograms[index]!.Export();
            });
            shader = Regex.Replace(shader, "GpuProgramIndex (.+)", evaluator);
            return shader;
        }
    }

    public class ShaderSubProgram
    {
        private int m_Version;
        public ShaderGpuProgramType m_ProgramType;
        public string[] m_Keywords;
        public string[]? m_LocalKeywords;
        public byte[] m_ProgramCode;

        public ShaderSubProgram(BinaryReader reader)
            : this(reader, reader.BaseStream.Length)
        {
        }

        internal ShaderSubProgram(BinaryReader reader, long entryEnd)
        {
            //LoadGpuProgramFromData
            //201509030 - Unity 5.3
            //201510240 - Unity 5.4
            //201608170 - Unity 5.5
            //201609010 - Unity 5.6, 2017.1 & 2017.2
            //201708220 - Unity 2017.3, Unity 2017.4 & Unity 2018.1
            //201802150 - Unity 2018.2 & Unity 2018.3
            //201806140 - Unity 2019.1~2021.1
            //202012090 - Unity 2021.2+
            var limit = Math.Min(entryEnd, reader.BaseStream.Length);
            if (limit - reader.BaseStream.Position < 24)
            {
                throw new EndOfStreamException("ShaderSubProgram header truncated.");
            }

            m_Version = reader.ReadInt32();
            m_ProgramType = (ShaderGpuProgramType)reader.ReadInt32();
            reader.BaseStream.Position += 12;
            if (m_Version >= 201608170)
            {
                if (limit - reader.BaseStream.Position < 8)
                {
                    throw new EndOfStreamException("ShaderSubProgram extended header truncated.");
                }
                reader.BaseStream.Position += 4;
            }
            var m_KeywordsSize = reader.ReadInt32();
            if (m_KeywordsSize < 0 || (long)m_KeywordsSize * 4 > limit - reader.BaseStream.Position)
            {
                throw new InvalidDataException($"Invalid ShaderSubProgram keyword count: {m_KeywordsSize}");
            }
            m_Keywords = new string[m_KeywordsSize];
            for (int i = 0; i < m_KeywordsSize; i++)
            {
                if (limit - reader.BaseStream.Position < 4)
                {
                    throw new EndOfStreamException("ShaderSubProgram keyword entry truncated.");
                }
                m_Keywords[i] = reader.ReadAlignedString();
                if (reader.BaseStream.Position > limit)
                {
                    throw new EndOfStreamException("ShaderSubProgram keyword exceeds entry boundary.");
                }
            }
            if (m_Version >= 201806140 && m_Version < 202012090)
            {
                if (limit - reader.BaseStream.Position < 4)
                {
                    throw new EndOfStreamException("ShaderSubProgram local keyword header truncated.");
                }
                var m_LocalKeywordsSize = reader.ReadInt32();
                if (m_LocalKeywordsSize < 0 || (long)m_LocalKeywordsSize * 4 > limit - reader.BaseStream.Position)
                {
                    throw new InvalidDataException($"Invalid ShaderSubProgram local keyword count: {m_LocalKeywordsSize}");
                }
                m_LocalKeywords = new string[m_LocalKeywordsSize];
                for (int i = 0; i < m_LocalKeywordsSize; i++)
                {
                    if (limit - reader.BaseStream.Position < 4)
                    {
                        throw new EndOfStreamException("ShaderSubProgram local keyword entry truncated.");
                    }
                    m_LocalKeywords[i] = reader.ReadAlignedString();
                    if (reader.BaseStream.Position > limit)
                    {
                        throw new EndOfStreamException("ShaderSubProgram local keyword exceeds entry boundary.");
                    }
                }
            }
            if (limit - reader.BaseStream.Position < 4)
            {
                throw new EndOfStreamException("ShaderSubProgram program code length truncated.");
            }
            var programCodeLength = reader.ReadInt32();
            if (programCodeLength < 0 || programCodeLength > limit - reader.BaseStream.Position)
            {
                throw new InvalidDataException($"Invalid ShaderSubProgram code length {programCodeLength} for entry boundary.");
            }
            m_ProgramCode = reader.ReadUInt8Array(programCodeLength);
            if (reader.BaseStream.Position < limit)
            {
                reader.AlignStream();
            }

            // Unity 5.5+ stores source map, bind channels, and parameters on SerializedSubProgram;
            // Unity 5.3-5.4 trailing channel/parameter blocks are skipped by ShaderProgram.Read via entry.Offset.
        }

        public string Export()
        {
            var sb = new StringBuilder();
            if (m_Keywords.Length > 0)
            {
                sb.Append("Keywords { ");
                foreach (string keyword in m_Keywords)
                {
                    sb.Append($"\"{keyword}\" ");
                }
                sb.Append("}\n");
            }
            if (m_LocalKeywords != null && m_LocalKeywords.Length > 0)
            {
                sb.Append("Local Keywords { ");
                foreach (string keyword in m_LocalKeywords)
                {
                    sb.Append($"\"{keyword}\" ");
                }
                sb.Append("}\n");
            }

            sb.Append("\"");
            if (m_ProgramCode.Length > 0)
            {
                switch (m_ProgramType)
                {
                    case ShaderGpuProgramType.GLLegacy:
                    case ShaderGpuProgramType.GLES31AEP:
                    case ShaderGpuProgramType.GLES31:
                    case ShaderGpuProgramType.GLES3:
                    case ShaderGpuProgramType.GLES:
                    case ShaderGpuProgramType.GLCore32:
                    case ShaderGpuProgramType.GLCore41:
                    case ShaderGpuProgramType.GLCore43:
                        sb.Append(Encoding.UTF8.GetString(m_ProgramCode));
                        break;
                    case ShaderGpuProgramType.DX9VertexSM20:
                    case ShaderGpuProgramType.DX9VertexSM30:
                    case ShaderGpuProgramType.DX9PixelSM20:
                    case ShaderGpuProgramType.DX9PixelSM30:
                        {
                            /*var shaderBytecode = new ShaderBytecode(m_ProgramCode);
                            sb.Append(shaderBytecode.Disassemble());*/
                            sb.Append("// shader disassembly not supported on DXBC");
                            break;
                        }
                    case ShaderGpuProgramType.DX10Level9Vertex:
                    case ShaderGpuProgramType.DX10Level9Pixel:
                    case ShaderGpuProgramType.DX11VertexSM40:
                    case ShaderGpuProgramType.DX11VertexSM50:
                    case ShaderGpuProgramType.DX11PixelSM40:
                    case ShaderGpuProgramType.DX11PixelSM50:
                    case ShaderGpuProgramType.DX11GeometrySM40:
                    case ShaderGpuProgramType.DX11GeometrySM50:
                    case ShaderGpuProgramType.DX11HullSM50:
                    case ShaderGpuProgramType.DX11DomainSM50:
                        {
                            /*int start = 6;
                            if (m_Version == 201509030) // 5.3
                            {
                                start = 5;
                            }
                            var buff = new byte[m_ProgramCode.Length - start];
                            Buffer.BlockCopy(m_ProgramCode, start, buff, 0, buff.Length);
                            var shaderBytecode = new ShaderBytecode(buff);
                            sb.Append(shaderBytecode.Disassemble());*/
                            sb.Append("// shader disassembly not supported on DXBC");
                            break;
                        }
                    case ShaderGpuProgramType.MetalVS:
                    case ShaderGpuProgramType.MetalFS:
                        using (var reader = new BinaryReader(new MemoryStream(m_ProgramCode)))
                        {
                            var fourCC = reader.ReadUInt32();
                            if (fourCC == 0xf00dcafe)
                            {
                                int offset = reader.ReadInt32();
                                reader.BaseStream.Position = offset;
                            }
                            var entryName = reader.ReadStringToNull();
                            var buff = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));
                            sb.Append(Encoding.UTF8.GetString(buff));
                        }
                        break;
                    case ShaderGpuProgramType.SPIRV:
                        try
                        {
                            sb.Append(SpirVShaderConverter.Convert(m_ProgramCode));
                        }
                        catch (Exception e)
                        {
                            sb.Append($"// disassembly error {e.Message}\n");
                        }
                        break;
                    case ShaderGpuProgramType.ConsoleVS:
                    case ShaderGpuProgramType.ConsoleFS:
                    case ShaderGpuProgramType.ConsoleHS:
                    case ShaderGpuProgramType.ConsoleDS:
                    case ShaderGpuProgramType.ConsoleGS:
                        sb.Append(Encoding.UTF8.GetString(m_ProgramCode));
                        break;
                    default:
                        sb.Append($"//shader disassembly not supported on {m_ProgramType}");
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
