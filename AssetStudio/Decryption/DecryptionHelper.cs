#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AssetStudio
{
    public sealed class DecryptionProfileCollectionDocument
    {
        [JsonPropertyName("profiles")]
        public List<DecryptionProfile> Profiles { get; set; } = new List<DecryptionProfile>();
    }

    public static class DecryptionHelper
    {
        private static readonly JsonSerializerOptions ProfileJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Read lock-free from many loader threads; written only while catalogLock is held.
        private static readonly ConcurrentDictionary<string, (string BundleName, string Hash, string Crc)> metadataMap =
            new ConcurrentDictionary<string, (string BundleName, string Hash, string Crc)>(StringComparer.OrdinalIgnoreCase);

        // Guarded by catalogLock. A catalog is only marked as loaded AFTER it has been fully parsed,
        // and the lock is held for the whole parse, so concurrent callers either wait for the parse
        // to finish or observe a fully populated metadataMap.
        private static readonly HashSet<string> loadedCatalogs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object catalogLock = new object();

        private static readonly object providerLock = new object();
        private static readonly List<DecryptionProfile> registeredProfiles = new List<DecryptionProfile>();
        private static readonly List<IStreamTransformProvider> customProviders = new List<IStreamTransformProvider>();
        private static readonly HashSet<string> loadedPluginAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static IStreamTransformProvider[] activePipeline = BuildDefaultPipeline();

        private static readonly byte[] encryptionMagic = Encoding.ASCII.GetBytes("Encryption");

        public static ReadOnlySpan<byte> EncryptionMagicBytes => encryptionMagic;

        public static string GetUserDecryptionProfilesDirectory()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "DecryptionProfiles");

        public static string GetUserDecryptionPluginsDirectory()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "Plugins",
                "Decryption");

        public static string GetAppDecryptionPluginsDirectory()
            => Path.Combine(AppContext.BaseDirectory, "Plugins", "Decryption");

        private static IStreamTransformProvider[] BuildDefaultPipeline()
        {
            var list = new List<IStreamTransformProvider>(5 + customProviders.Count)
            {
                new Block512CatalogTransformProvider(),
                new DeclarativeProfileTransformProvider(GetProfilesSnapshot),
                new OffsetHeaderSkipTransformProvider(),
                new PassphraseCtrTransformProvider(),
                new KnownPlaintextXorTransformProvider()
            };
            list.AddRange(customProviders);
            list.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
            return list.ToArray();
        }

        public static IReadOnlyList<IStreamTransformProvider> GetRegisteredProviders()
            => activePipeline;

        public static IReadOnlyList<IStreamTransformProvider> GetCustomProviders()
        {
            lock (providerLock)
            {
                return customProviders.Count == 0
                    ? Array.Empty<IStreamTransformProvider>()
                    : customProviders.ToArray();
            }
        }

        public static IReadOnlyList<DecryptionProfile> GetRegisteredProfiles()
        {
            lock (providerLock)
            {
                if (registeredProfiles.Count == 0)
                {
                    return Array.Empty<DecryptionProfile>();
                }

                var result = new DecryptionProfile[registeredProfiles.Count];
                for (int i = 0; i < registeredProfiles.Count; i++)
                {
                    result[i] = registeredProfiles[i].Clone();
                }
                return result;
            }
        }

        public static void RegisterProvider(IStreamTransformProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            lock (providerLock)
            {
                customProviders.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
                customProviders.Add(provider);
                activePipeline = BuildDefaultPipeline();
            }
        }

        public static void RegisterProfile(DecryptionProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var copy = profile.Clone();
            if (string.IsNullOrWhiteSpace(copy.Id))
            {
                copy.Id = $"profile-{Guid.NewGuid():N}".Substring(0, 16);
            }

            lock (providerLock)
            {
                registeredProfiles.RemoveAll(p => string.Equals(p.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
                registeredProfiles.Add(copy);
            }
        }

        public static bool RemoveProfile(string id, bool deleteUserFile = false)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            bool removed;
            lock (providerLock)
            {
                removed = registeredProfiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            }

            if (deleteUserFile)
            {
                TryDeleteUserProfileFile(id);
            }

            return removed;
        }

        public static string SaveProfileToUserDirectory(DecryptionProfile profile, string? targetDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var copy = profile.Clone();
            if (string.IsNullOrWhiteSpace(copy.Id))
            {
                copy.Id = $"profile-{DateTime.UtcNow:yyyyMMddHHmmss}";
            }

            RegisterProfile(copy);

            string dir = string.IsNullOrWhiteSpace(targetDirectory)
                ? GetUserDecryptionProfilesDirectory()
                : targetDirectory;
            Directory.CreateDirectory(dir);

            string fileName = BuildSafeProfileFileName(copy.Id) + ".json";
            string fullPath = Path.Combine(dir, fileName);
            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            string json = JsonSerializer.Serialize(copy, ProfileJsonOptions);
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, fullPath, overwrite: true);
            return fullPath;
        }

        private static void TryDeleteUserProfileFile(string profileId, string? targetDirectory = null)
        {
            try
            {
                string dir = string.IsNullOrWhiteSpace(targetDirectory)
                    ? GetUserDecryptionProfilesDirectory()
                    : targetDirectory;
                if (!Directory.Exists(dir))
                {
                    return;
                }

                string expectedPath = Path.Combine(dir, BuildSafeProfileFileName(profileId) + ".json");
                if (File.Exists(expectedPath))
                {
                    File.Delete(expectedPath);
                    return;
                }

                // Also scan user directory in case the profile lived under a custom filename
                foreach (var jsonFile in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var items = TryDeserializeProfilesFile(jsonFile);
                    if (items.Count == 1 && string.Equals(items[0].Id, profileId, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(jsonFile);
                        break;
                    }
                }
            }
            catch
            {
            }
        }

        private static string BuildSafeProfileFileName(string id)
        {
            var sb = new StringBuilder(id.Length);
            foreach (char c in id.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            return sb.Length > 0 ? sb.ToString() : "profile";
        }

        public static int LoadProfilesFromFile(string jsonFilePath)
        {
            if (string.IsNullOrWhiteSpace(jsonFilePath) || !File.Exists(jsonFilePath))
            {
                return 0;
            }

            var items = TryDeserializeProfilesFile(jsonFilePath);
            for (int i = 0; i < items.Count; i++)
            {
                RegisterProfile(items[i]);
            }
            return items.Count;
        }

        public static int ImportProfilesFromFile(string jsonFilePath, bool saveToUserDirectory = true, string? userDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(jsonFilePath) || !File.Exists(jsonFilePath))
            {
                return 0;
            }

            var items = TryDeserializeProfilesFile(jsonFilePath);
            for (int i = 0; i < items.Count; i++)
            {
                if (saveToUserDirectory)
                {
                    SaveProfileToUserDirectory(items[i], userDirectory);
                }
                else
                {
                    RegisterProfile(items[i]);
                }
            }
            return items.Count;
        }

        private static List<DecryptionProfile> TryDeserializeProfilesFile(string jsonFilePath)
        {
            var result = new List<DecryptionProfile>();
            try
            {
                string text = File.ReadAllText(jsonFilePath);
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    var list = JsonSerializer.Deserialize<List<DecryptionProfile>>(text, ProfileJsonOptions);
                    if (list != null)
                    {
                        result.AddRange(list.Where(static p => p != null));
                    }
                }
                else if (trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                    if (doc.RootElement.TryGetProperty("profiles", out var profilesElem)
                        && profilesElem.ValueKind == JsonValueKind.Array)
                    {
                        var collection = JsonSerializer.Deserialize<DecryptionProfileCollectionDocument>(text, ProfileJsonOptions);
                        if (collection?.Profiles != null)
                        {
                            result.AddRange(collection.Profiles.Where(static p => p != null));
                        }
                    }
                    else
                    {
                        var single = JsonSerializer.Deserialize<DecryptionProfile>(text, ProfileJsonOptions);
                        if (single != null)
                        {
                            result.Add(single);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to parse decryption profile file '{Path.GetFileName(jsonFilePath)}': {ex.Message}");
            }

            return result;
        }

        public static int LoadProfilesFromDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return 0;
            }

            int loaded = 0;
            foreach (var jsonFile in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                loaded += LoadProfilesFromFile(jsonFile);
            }

            return loaded;
        }

        public static int LoadPluginsFromDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return 0;
            }

            int loadedProviders = 0;
            foreach (var dllPath in Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(dllPath);
                }
                catch
                {
                    continue;
                }

                try
                {
                    var loadContext = new DecryptionPluginLoadContext(fullPath);
                    Assembly assembly = loadContext.LoadFromAssemblyPath(fullPath);
                    Type[] exportedTypes;
                    try
                    {
                        exportedTypes = assembly.GetExportedTypes();
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        exportedTypes = rtle.Types.Where(static t => t != null).Cast<Type>().ToArray();
                    }

                    foreach (var type in exportedTypes)
                    {
                        if (type.IsAbstract || type.IsInterface || !typeof(IStreamTransformProvider).IsAssignableFrom(type))
                        {
                            continue;
                        }

                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor == null)
                        {
                            continue;
                        }

                        if (Activator.CreateInstance(type) is IStreamTransformProvider provider)
                        {
                            RegisterProvider(provider);
                            loadedProviders++;
                        }
                    }

                    lock (providerLock)
                    {
                        loadedPluginAssemblies.Add(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load decryption plugin '{Path.GetFileName(dllPath)}': {ex.Message}");
                }
            }

            return loadedProviders;
        }

        public static (int ProfilesLoaded, int PluginsLoaded) LoadLocalProfilesAndPlugins(string? projectRoot = null)
        {
            int profiles = 0;
            int plugins = 0;

            string appBase = AppContext.BaseDirectory;
            profiles += LoadProfilesFromFile(Path.Combine(appBase, "decryption_profiles.json"));
            profiles += LoadProfilesFromDirectory(Path.Combine(appBase, "DecryptionProfiles"));
            profiles += LoadProfilesFromDirectory(GetUserDecryptionProfilesDirectory());

            if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            {
                profiles += LoadProfilesFromFile(Path.Combine(projectRoot, "decryption_profiles.json"));
            }

            plugins += LoadPluginsFromDirectory(GetAppDecryptionPluginsDirectory());
            plugins += LoadPluginsFromDirectory(GetUserDecryptionPluginsDirectory());

            return (profiles, plugins);
        }

        public static (int ProfilesLoaded, int PluginsLoaded) ReloadLocalProfilesAndPlugins(string? projectRoot = null)
        {
            lock (providerLock)
            {
                registeredProfiles.Clear();
                customProviders.Clear();
                loadedPluginAssemblies.Clear();
                activePipeline = BuildDefaultPipeline();
            }

            return LoadLocalProfilesAndPlugins(projectRoot);
        }

        private sealed class DecryptionPluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver resolver;

            public DecryptionPluginLoadContext(string pluginPath)
                : base(isCollectible: false)
            {
                resolver = new AssemblyDependencyResolver(pluginPath);
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                if (string.Equals(assemblyName.Name, typeof(IStreamTransformProvider).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(IStreamTransformProvider).Assembly;
                }

                string? path = resolver.ResolveAssemblyToPath(assemblyName);
                return path != null ? LoadFromAssemblyPath(path) : null;
            }
        }

        private static IReadOnlyList<DecryptionProfile> GetProfilesSnapshot()
        {
            lock (providerLock)
            {
                return registeredProfiles.Count == 0
                    ? Array.Empty<DecryptionProfile>()
                    : registeredProfiles.ToArray();
            }
        }

        /// <summary>
        /// Clears per-project Addressables catalog decryption caches without removing user-configured
        /// decryption profiles or local transform plugins.
        /// </summary>
        public static void ClearSessionCaches()
        {
            lock (catalogLock)
            {
                loadedCatalogs.Clear();
                metadataMap.Clear();
            }

            PassphraseCtrTransformProvider.ClearCache();
            AddressablesCatalogDependencyResolver.ClearCache();
        }

        /// <summary>
        /// Forgets all parsed Addressables catalogs and custom profiles/providers so they are re-read.
        /// </summary>
        public static void Clear()
        {
            ClearSessionCaches();

            lock (providerLock)
            {
                registeredProfiles.Clear();
                customProviders.Clear();
                loadedPluginAssemblies.Clear();
                activePipeline = BuildDefaultPipeline();
            }
        }

        public static Stream? CheckAndDecryptStream(Stream? stream, string? filePath)
            => CheckAndDecryptStream(stream, filePath, out _, out _);

        public static Stream? CheckAndDecryptStream(
            Stream? stream,
            string? filePath,
            out string? appliedProviderId,
            out StreamEncryptionBarrier? detectedBarrier)
        {
            appliedProviderId = null;
            detectedBarrier = null;

            if (stream == null || !stream.CanRead || !stream.CanSeek || string.IsNullOrEmpty(filePath))
            {
                return stream;
            }

            long startPos = stream.Position;
            long remaining = stream.Length - startPos;
            if (remaining < 26)
            {
                return stream;
            }

            int probeSize = (int)Math.Min(UnityHeaderProbe.DefaultProbeBufferSize, remaining);
            byte[] rentedProbe = ArrayPool<byte>.Shared.Rent(probeSize);
            try
            {
                int read = stream.Read(rentedProbe, 0, probeSize);
                stream.Position = startPos;
                if (read < 26)
                {
                    return stream;
                }

                var headerSpan = rentedProbe.AsSpan(0, read);
                var providers = activePipeline;
                for (int i = 0; i < providers.Length; i++)
                {
                    var provider = providers[i];
                    var transformed = provider.TryTransform(stream, filePath, headerSpan);
                    if (transformed != null && !ReferenceEquals(transformed, stream))
                    {
                        appliedProviderId = provider.Id;
                        return transformed;
                    }
                    stream.Position = startPos;
                }

                if (UnityHeaderProbe.TryDetectEncryptionBarrier(headerSpan, remaining, filePath, out var barrier))
                {
                    detectedBarrier = barrier;
                }

                return stream;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedProbe);
            }
        }

        public static bool InspectFileBarrier(string filePath, out StreamEncryptionBarrier barrier)
        {
            barrier = default;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < 26)
                {
                    return false;
                }

                var resolved = CheckAndDecryptStream(fs, filePath, out var appliedProviderId, out var detectedBarrier);
                if (resolved != null && !ReferenceEquals(resolved, fs))
                {
                    resolved.Dispose();
                }

                if (!string.IsNullOrEmpty(appliedProviderId))
                {
                    return false;
                }

                if (detectedBarrier.HasValue)
                {
                    barrier = detectedBarrier.Value;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryTestProfileOnFile(DecryptionProfile profile, string filePath, out string? unlockedHeaderSummary)
        {
            unlockedHeaderSummary = null;
            if (profile == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < 20)
                {
                    return false;
                }

                return DeclarativeProfileTransformProvider.TryTestProfileOnStream(profile, fs, filePath, out unlockedHeaderSummary);
            }
            catch
            {
                return false;
            }
        }

        public static string? TryResolveBlock512Token(string? filePath, string? explicitCatalogPath = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            string fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(explicitCatalogPath))
            {
                string trimmedCatalog = explicitCatalogPath.Trim();
                if (File.Exists(trimmedCatalog))
                {
                    if (TryResolveTokenFromExplicitCatalog(trimmedCatalog, fileName, out var directToken))
                    {
                        return directToken;
                    }
                }
            }

            if (!metadataMap.TryGetValue(fileName, out var meta))
            {
                LocateAndParseCatalog(filePath);
                if (!metadataMap.TryGetValue(fileName, out meta))
                {
                    return null;
                }
            }

            return $"{meta.BundleName}_{meta.Hash}-{meta.Crc}";
        }

        public sealed class Block512CatalogTransformProvider : IStreamTransformProvider
        {
            public string Id => "Block512Catalog";
            public int Priority => 100;

            public Stream? TryTransform(Stream stream, string filePath, ReadOnlySpan<byte> initialHeader)
            {
                if (!UnityHeaderProbe.MatchesAt(initialHeader, 0, encryptionMagic))
                {
                    return null;
                }

                string? token = TryResolveBlock512Token(filePath);
                if (string.IsNullOrEmpty(token))
                {
                    return null;
                }

                return new DecryptedStream(stream, token);
            }
        }

        private static bool TryResolveTokenFromExplicitCatalog(string catalogPath, string fileName, out string? token)
        {
            token = null;
            try
            {
                string fullCatalogPath = Path.GetFullPath(catalogPath);
                if (AddressablesCatalogDependencyResolver.TryLoadCatalog(fullCatalogPath, out var parsed) && parsed != null)
                {
                    if (parsed.ByFileName.TryGetValue(fileName, out var nodes))
                    {
                        for (int i = 0; i < nodes.Count; i++)
                        {
                            var node = nodes[i];
                            if (!string.IsNullOrEmpty(node.BundleName) && !string.IsNullOrEmpty(node.Hash))
                            {
                                string crc = string.IsNullOrEmpty(node.Crc) ? "0" : node.Crc;
                                token = $"{node.BundleName}_{node.Hash}-{crc}";
                                return true;
                            }
                        }
                    }

                    for (int i = 0; i < parsed.Bundles.Count; i++)
                    {
                        var node = parsed.Bundles[i];
                        if (string.Equals(node.BundleFileName, fileName, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(node.BundleName)
                            && !string.IsNullOrEmpty(node.Hash))
                        {
                            string crc = string.IsNullOrEmpty(node.Crc) ? "0" : node.Crc;
                            token = $"{node.BundleName}_{node.Hash}-{crc}";
                            return true;
                        }
                    }
                }

                // Also parse into shared cache as JSON fallback
                ParseCatalog(fullCatalogPath);
                if (metadataMap.TryGetValue(fileName, out var meta))
                {
                    token = $"{meta.BundleName}_{meta.Hash}-{meta.Crc}";
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void LocateAndParseCatalog(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;
            try
            {
                var catalogPaths = AddressablesCatalogDependencyResolver.FindCatalogPathsForFiles(new[] { filePath });

                // Also check application base directory only (never walk parent directories of app base)
                string appBase = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(appBase) && Directory.Exists(appBase))
                {
                    AddTopLevelCatalogCandidates(appBase, catalogPaths);
                }

                for (int i = 0; i < catalogPaths.Count; i++)
                {
                    ParseCatalog(catalogPaths[i]);
                }
            }
            catch
            {
                // Ignore errors locating catalog
            }
        }

        private static void AddTopLevelCatalogCandidates(string directory, List<string> target)
        {
            void TryAdd(string path)
            {
                if (File.Exists(path) && !target.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(path);
                }
            }

            TryAdd(Path.Combine(directory, "catalog.json"));
            TryAdd(Path.Combine(directory, "catalog.bin"));

            try
            {
                foreach (var candidate in Directory.EnumerateFiles(directory, "catalog*.json", SearchOption.TopDirectoryOnly))
                {
                    TryAdd(candidate);
                }

                foreach (var candidate in Directory.EnumerateFiles(directory, "catalog*.bin", SearchOption.TopDirectoryOnly))
                {
                    TryAdd(candidate);
                }
            }
            catch
            {
            }
        }

        private static void ParseCatalog(string catalogPath)
        {
            string fullPath = Path.GetFullPath(catalogPath);
            lock (catalogLock)
            {
                if (loadedCatalogs.Contains(fullPath))
                    return;

                try
                {
                    ParseCatalogCore(fullPath);
                }
                finally
                {
                    // Mark as loaded only after parsing finished (successfully or not) so that
                    // no thread can skip parsing while metadataMap is still being populated.
                    loadedCatalogs.Add(fullPath);
                }
            }
        }

        private static void ParseCatalogCore(string fullPath)
        {
            try
            {
                int addedFromResolver = AddressablesCatalogDependencyResolver.TryExtractBlock512BundleMetadata(
                    fullPath,
                    static (fileName, bundleName, hash, crc) => metadataMap[fileName] = (bundleName, hash, crc));

                if (addedFromResolver > 0 || string.Equals(Path.GetExtension(fullPath), ".bin", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string content = File.ReadAllText(fullPath);

                string? entryDataString = null;
                var entryMatch = Regex.Match(content, @"""m_EntryDataString""\s*:\s*""([^""]+)""");
                if (entryMatch.Success)
                    entryDataString = entryMatch.Groups[1].Value;

                string? extraDataString = null;
                var extraMatch = Regex.Match(content, @"""m_ExtraDataString""\s*:\s*""([^""]+)""");
                if (extraMatch.Success)
                    extraDataString = extraMatch.Groups[1].Value;

                if (string.IsNullOrEmpty(entryDataString) || string.IsNullOrEmpty(extraDataString))
                    return;

                List<string> internalIds = new List<string>();
                var idsMatch = Regex.Match(content, @"""m_InternalIds""\s*:\s*\[([^\[\]]*)\]");
                if (idsMatch.Success)
                {
                    var idMatches = Regex.Matches(idsMatch.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""");
                    foreach (Match m in idMatches)
                    {
                        internalIds.Add(m.Groups[1].Value);
                    }
                }

                List<string> prefixes = new List<string>();
                var prefMatch = Regex.Match(content, @"""m_InternalIdPrefixes""\s*:\s*\[([^\[\]]*)\]");
                if (prefMatch.Success)
                {
                    var prefMatches = Regex.Matches(prefMatch.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""");
                    foreach (Match m in prefMatches)
                    {
                        prefixes.Add(m.Groups[1].Value);
                    }
                }

                byte[] entryBytes = Convert.FromBase64String(entryDataString);
                byte[] extraBytes = Convert.FromBase64String(extraDataString);

                if (entryBytes.Length < 4)
                    return;

                int entryCount = BitConverter.ToInt32(entryBytes, 0);
                for (int i = 0; i < entryCount; i++)
                {
                    int idx = 4 + i * 28;
                    if (idx + 28 > entryBytes.Length)
                        break;

                    int internalIdIdx = BitConverter.ToInt32(entryBytes, idx);
                    int dataIdx = BitConverter.ToInt32(entryBytes, idx + 16);

                    if (dataIdx >= 0 && internalIdIdx >= 0 && internalIdIdx < internalIds.Count)
                    {
                        string internalPath = ExpandId(internalIds[internalIdIdx], prefixes);
                        if (internalPath.Contains("Embed") || internalPath.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                        {
                            string? json = ReadObject(extraBytes, dataIdx);
                            if (json != null)
                            {
                                var bundleNameMatch = Regex.Match(json, @"""m_BundleName""\s*:\s*""([^""]+)""");
                                var hashMatch = Regex.Match(json, @"""m_Hash""\s*:\s*""([^""]+)""");
                                var crcMatch = Regex.Match(json, @"""m_Crc""\s*:\s*(\d+)");

                                if (bundleNameMatch.Success && hashMatch.Success)
                                {
                                    string bundleName = bundleNameMatch.Groups[1].Value;
                                    string hash = hashMatch.Groups[1].Value;
                                    string crc = crcMatch.Success ? crcMatch.Groups[1].Value : "0";

                                    string fileName = Path.GetFileName(internalPath);
                                    if (!string.IsNullOrEmpty(fileName))
                                    {
                                        metadataMap[fileName] = (bundleName, hash, crc);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore catalog parsing errors
            }
        }

        private static string ExpandId(string val, List<string> prefixes)
        {
            if (prefixes == null || prefixes.Count == 0)
                return val;

            int hashIdx = val.IndexOf('#');
            if (hashIdx == -1)
                return val;

            string numStr = val.Substring(0, hashIdx);
            string rest = val.Substring(hashIdx + 1);

            if (int.TryParse(numStr, out int idx) && idx >= 0 && idx < prefixes.Count)
            {
                return prefixes[idx] + rest;
            }

            return val;
        }

        private static string? ReadObject(byte[] data, int offset)
        {
            if (offset >= data.Length)
                return null;

            byte objType = data[offset];
            offset += 1;

            if (objType == 0) // AsciiString
            {
                if (offset + 4 > data.Length)
                    return null;
                int length = BitConverter.ToInt32(data, offset);
                offset += 4;
                if (offset + length > data.Length)
                    return null;
                return Encoding.ASCII.GetString(data, offset, length);
            }
            else if (objType == 1) // UnicodeString
            {
                if (offset + 4 > data.Length)
                    return null;
                int length = BitConverter.ToInt32(data, offset);
                offset += 4;
                if (offset + length > data.Length)
                    return null;
                return Encoding.Unicode.GetString(data, offset, length);
            }
            else if (objType == 7) // JsonObject
            {
                if (offset >= data.Length)
                    return null;
                byte assemblyLen = data[offset];
                offset += 1 + assemblyLen;

                if (offset >= data.Length)
                    return null;
                byte classLen = data[offset];
                offset += 1 + classLen;

                if (offset + 4 > data.Length)
                    return null;
                int jsonLen = BitConverter.ToInt32(data, offset);
                offset += 4;

                if (offset + jsonLen > data.Length)
                    return null;
                return Encoding.Unicode.GetString(data, offset, jsonLen);
            }

            return null;
        }
    }
}
