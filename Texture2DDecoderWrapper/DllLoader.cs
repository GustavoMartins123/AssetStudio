using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Texture2DDecoder
{
    internal static class DllLoader
    {
        private static readonly ConcurrentDictionary<Assembly, byte> RegisteredAssemblies = new();
        private static readonly ConcurrentDictionary<string, IntPtr> LoadedLibraries = new(StringComparer.Ordinal);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void PreloadDll(string dllName)
        {
            if (string.IsNullOrWhiteSpace(dllName))
            {
                throw new ArgumentException("Library name cannot be null or whitespace.", nameof(dllName));
            }

            var callingAssembly = Assembly.GetCallingAssembly();
            RegisterResolver(callingAssembly);

            if (LoadedLibraries.ContainsKey(dllName))
            {
                return;
            }

            var dllFileName = GetPlatformLibraryFileName(dllName);
            var directedDllPath = Path.Combine(GetDirectedDllDirectory(), dllFileName);
            var baseDllPath = Path.Combine(GetBaseDirectory(), dllFileName);

            if (TryLoadAndCache(dllName, directedDllPath, out _) ||
                (File.Exists(baseDllPath) && TryLoadAndCache(dllName, baseDllPath, out _)) ||
                TryLoadDefaultAndCache(dllName, callingAssembly, null, out _))
            {
                return;
            }

            try
            {
                var handle = NativeLibrary.Load(directedDllPath);
                LoadedLibraries.TryAdd(dllName, handle);
            }
            catch (Exception ex)
            {
                throw new DllNotFoundException(
                    $"Failed to load native library '{directedDllPath}'. Build the native projects and copy the library into the platform folder beside the executable. {ex.Message}",
                    ex);
            }
        }

        private static void RegisterResolver(Assembly assembly)
        {
            if (assembly == null || !RegisteredAssemblies.TryAdd(assembly, 0))
            {
                return;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(assembly, ResolveNativeLibrary);
            }
            catch (InvalidOperationException)
            {
                // A DllImportResolver was already registered for this assembly.
            }
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                return IntPtr.Zero;
            }

            if (LoadedLibraries.TryGetValue(libraryName, out var cachedHandle))
            {
                return cachedHandle;
            }

            var directedDir = GetDirectedDllDirectory();
            var baseDir = GetBaseDirectory();

            var platformFileName = GetPlatformLibraryFileName(libraryName);
            var candidates = new[]
            {
                Path.Combine(directedDir, platformFileName),
                Path.Combine(baseDir, platformFileName),
                Path.Combine(directedDir, libraryName),
                Path.Combine(baseDir, libraryName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate) && TryLoadAndCache(libraryName, candidate, out var handle))
                {
                    return handle;
                }
            }

            if (TryLoadDefaultAndCache(libraryName, assembly, searchPath, out var defaultHandle))
            {
                return defaultHandle;
            }

            return IntPtr.Zero;
        }

        private static bool TryLoadAndCache(string libraryName, string fullPath, out IntPtr handle)
        {
            if (NativeLibrary.TryLoad(fullPath, out handle))
            {
                LoadedLibraries.TryAdd(libraryName, handle);
                return true;
            }

            handle = IntPtr.Zero;
            return false;
        }

        private static bool TryLoadDefaultAndCache(string libraryName, Assembly assembly, DllImportSearchPath? searchPath, out IntPtr handle)
        {
            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out handle))
            {
                LoadedLibraries.TryAdd(libraryName, handle);
                return true;
            }

            handle = IntPtr.Zero;
            return false;
        }

        private static string GetPlatformLibraryFileName(string dllName)
        {
            if (OperatingSystem.IsWindows())
            {
                return dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? dllName
                    : $"{dllName}.dll";
            }

            if (OperatingSystem.IsLinux())
            {
                var fileName = dllName.StartsWith("lib", StringComparison.Ordinal) ? dllName : $"lib{dllName}";
                return fileName.EndsWith(".so", StringComparison.Ordinal) ? fileName : $"{fileName}.so";
            }

            if (OperatingSystem.IsMacOS())
            {
                var fileName = dllName.StartsWith("lib", StringComparison.Ordinal) ? dllName : $"lib{dllName}";
                return fileName.EndsWith(".dylib", StringComparison.Ordinal) ? fileName : $"{fileName}.dylib";
            }

            throw new PlatformNotSupportedException($"Unsupported operating system: {RuntimeInformation.OSDescription}");
        }

        private static string GetBaseDirectory()
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                return baseDir;
            }

            var assemblyLocation = typeof(DllLoader).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(assemblyLocation))
            {
                var assemblyDir = Path.GetDirectoryName(assemblyLocation);
                if (!string.IsNullOrWhiteSpace(assemblyDir))
                {
                    return assemblyDir;
                }
            }

            return Environment.CurrentDirectory;
        }

        private static string GetDirectedDllDirectory()
        {
            var baseDir = GetBaseDirectory();
            var subDir = Environment.Is64BitProcess ? "x64" : "x86";
            return Path.Combine(baseDir, subDir);
        }
    }
}
