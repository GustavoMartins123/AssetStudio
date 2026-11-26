using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio.Avalonia.Services.Scripting;

/// <summary>
/// Shared deterministic GUID generator for Unity .meta files and Visual Studio .sln/.csproj exports.
/// Keeps Script and Asset GUID contracts bit-for-bit deterministic across script export and scene export.
/// </summary>
public static class UnityProjectGuidUtility
{
    public static string NormalizeAssemblyShortName(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return string.Empty;
        }

        var raw = assemblyName.Trim().Replace('\\', '/');
        var trimmed = Path.GetFileName(raw);
        if (trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
        }

        return trimmed.Trim();
    }

    /// <summary>
    /// Deterministic 32-char lowercase hex GUID matching Unity .meta format:
    /// SHA256("unity-script|" + NormalizeAssemblyShortName(assemblyName).ToLowerInvariant() + "|" + (ns ?? "").Trim() + "|" + (className ?? "").Trim())[0..16]
    /// </summary>
    public static string ComputeScriptGuid(string? assemblyName, string? ns, string? className)
    {
        var normalizedAsm = NormalizeAssemblyShortName(assemblyName).ToLowerInvariant();
        var cleanNs = (ns ?? string.Empty).Trim();
        var cleanClass = (className ?? string.Empty).Trim();
        var payload = $"unity-script|{normalizedAsm}|{cleanNs}|{cleanClass}";
        return ComputeSha256First16HexLower(payload);
    }

    /// <summary>
    /// Deterministic 32-char lowercase hex GUID for generic Unity assets:
    /// SHA256("unity-asset|" + assetKind.Trim().ToLowerInvariant() + "|" + (serializedFileName ?? "").Trim().ToLowerInvariant() + "|" + pathId + "|" + (fallbackKey ?? "").Trim())[0..16]
    /// </summary>
    public static string ComputeAssetGuid(string assetKind, string? serializedFileName, long pathId, string? fallbackKey = null)
    {
        var cleanKind = (assetKind ?? string.Empty).Trim().ToLowerInvariant();
        var cleanFile = (serializedFileName ?? string.Empty).Trim().ToLowerInvariant();
        var cleanFallback = (fallbackKey ?? string.Empty).Trim();
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"unity-asset|{cleanKind}|{cleanFile}|{pathId}|{cleanFallback}");
        return ComputeSha256First16HexLower(payload);
    }

    /// <summary>
    /// Deterministic Visual Studio project GUID for .sln entries ({XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX} uppercase).
    /// </summary>
    public static Guid ComputeVisualStudioProjectGuid(string assemblyShortName)
    {
        var normalizedAsm = NormalizeAssemblyShortName(assemblyShortName).ToLowerInvariant();
        var payload = $"vs-project|{normalizedAsm}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash.Slice(0, 16).CopyTo(guidBytes);
        // Set deterministic RFC 4122 version 5 & variant bits
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    public static string FormatVisualStudioGuid(Guid guid)
    {
        return guid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    public static string BuildMonoScriptMetaContent(string guid)
    {
        return string.Concat(
            "fileFormatVersion: 2\n",
            "guid: ", guid, "\n",
            "MonoImporter:\n",
            "  externalObjects: {}\n",
            "  serializedVersion: 2\n",
            "  defaultReferences: []\n",
            "  executionOrder: 0\n",
            "  icon: {instanceID: 0}\n",
            "  userData: \n",
            "  assetBundleName: \n",
            "  assetBundleVariant: \n");
    }

    public static string BuildAsmdefMetaContent(string guid)
    {
        return string.Concat(
            "fileFormatVersion: 2\n",
            "guid: ", guid, "\n",
            "AssemblyDefinitionImporter:\n",
            "  externalObjects: {}\n",
            "  userData: \n",
            "  assetBundleName: \n",
            "  assetBundleVariant: \n");
    }

    public static string BuildFolderMetaContent(string guid)
    {
        return string.Concat(
            "fileFormatVersion: 2\n",
            "guid: ", guid, "\n",
            "folderAsset: yes\n",
            "DefaultImporter:\n",
            "  externalObjects: {}\n",
            "  userData: \n",
            "  assetBundleName: \n",
            "  assetBundleVariant: \n");
    }

    private static string ComputeSha256First16HexLower(string payload)
    {
        Span<byte> hash = stackalloc byte[32];
        var utf8 = Encoding.UTF8.GetBytes(payload);
        SHA256.HashData(utf8, hash);
        return Convert.ToHexString(hash.Slice(0, 16)).ToLowerInvariant();
    }
}
