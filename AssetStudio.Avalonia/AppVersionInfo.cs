using System;
using System.Reflection;

namespace AssetStudio.Avalonia;

/// <summary>
/// Resolves and formats the application's stamped display version from assembly metadata.
/// </summary>
internal static class AppVersionInfo
{
    public const string FallbackVersion = "0.19.0";

    public static string Version => GetVersion(typeof(AppVersionInfo).Assembly);

    public static string DisplayVersion => Version;

    public static string PrefixedDisplayVersion => $"v{Version}";

    public static string GetVersion(Assembly? assembly)
    {
        if (assembly == null)
        {
            return FallbackVersion;
        }

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return ResolveVersion(informationalVersion, assembly.GetName().Version);
    }

    public static string ResolveVersion(string? informationalVersion, Version? assemblyVersion)
    {
        var formattedInfo = FormatInformationalVersion(informationalVersion);
        if (!string.IsNullOrEmpty(formattedInfo))
        {
            return formattedInfo;
        }

        var formattedAssembly = FormatAssemblyVersion(assemblyVersion);
        if (!string.IsNullOrEmpty(formattedAssembly))
        {
            return formattedAssembly;
        }

        return FallbackVersion;
    }

    public static string? FormatInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var value = informationalVersion.Trim();
        var plusIndex = value.IndexOf('+');
        if (plusIndex >= 0)
        {
            value = value[..plusIndex].Trim();
        }

        value = StripLeadingVersionPrefix(value);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? FormatAssemblyVersion(Version? assemblyVersion)
    {
        if (assemblyVersion == null)
        {
            return null;
        }

        if (assemblyVersion.Revision > 0)
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}";
        }

        if (assemblyVersion.Build >= 0)
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        }

        return $"{assemblyVersion.Major}.{assemblyVersion.Minor}";
    }

    public static string FormatPrefixedVersion(string? informationalVersion, Version? assemblyVersion = null)
    {
        return $"v{ResolveVersion(informationalVersion, assemblyVersion)}";
    }

    private static string StripLeadingVersionPrefix(string value)
    {
        var index = 0;
        while (index < value.Length && (value[index] == 'v' || value[index] == 'V'))
        {
            index++;
        }

        // Only strip leading 'v'/'V' when it prefixes a numeric version (e.g., "v0.18.0-rc2").
        if (index > 0 && index < value.Length && char.IsDigit(value[index]))
        {
            return value[index..].TrimStart();
        }

        return index == value.Length ? string.Empty : value;
    }
}
