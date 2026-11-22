#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetStudio
{
    public static class DecryptionHelper
    {
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
        private static IStreamTransformProvider[] activePipeline = BuildDefaultPipeline();

        private static readonly byte[] encryptionMagic = Encoding.ASCII.GetBytes("Encryption");

        public static ReadOnlySpan<byte> EncryptionMagicBytes => encryptionMagic;

        private static IStreamTransformProvider[] BuildDefaultPipeline()
        {
            var list = new List<IStreamTransformProvider>(4 + customProviders.Count)
            {
                new Block512CatalogTransformProvider(),
                new DeclarativeProfileTransformProvider(GetProfilesSnapshot),
                new OffsetHeaderSkipTransformProvider(),
                new KnownPlaintextXorTransformProvider()
            };
            list.AddRange(customProviders);
            list.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
            return list.ToArray();
        }

        public static IReadOnlyList<IStreamTransformProvider> GetRegisteredProviders()
            => activePipeline;

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
            lock (providerLock)
            {
                if (!string.IsNullOrWhiteSpace(profile.Id))
                {
                    registeredProfiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
                }
                registeredProfiles.Add(profile);
            }
        }

        public static int LoadProfilesFromDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return 0;
            }

            int loaded = 0;
            foreach (var jsonFile in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var text = File.ReadAllText(jsonFile);
                    if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                    {
                        var list = JsonSerializer.Deserialize<List<DecryptionProfile>>(text);
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                RegisterProfile(item);
                                loaded++;
                            }
                        }
                    }
                    else
                    {
                        var single = JsonSerializer.Deserialize<DecryptionProfile>(text);
                        if (single != null)
                        {
                            RegisterProfile(single);
                            loaded++;
                        }
                    }
                }
                catch
                {
                }
            }

            return loaded;
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
        /// Forgets all parsed Addressables catalogs and custom profiles/providers so they are re-read.
        /// </summary>
        public static void Clear()
        {
            lock (catalogLock)
            {
                loadedCatalogs.Clear();
                metadataMap.Clear();
            }

            lock (providerLock)
            {
                registeredProfiles.Clear();
                customProviders.Clear();
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
                if (fs.Length < 32)
                {
                    return false;
                }

                int probeSize = (int)Math.Min(UnityHeaderProbe.DefaultProbeBufferSize, fs.Length);
                byte[] rented = ArrayPool<byte>.Shared.Rent(probeSize);
                try
                {
                    int read = fs.Read(rented, 0, probeSize);
                    return UnityHeaderProbe.TryDetectEncryptionBarrier(rented.AsSpan(0, read), fs.Length, filePath, out barrier);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            catch
            {
                return false;
            }
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

                string fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return null;
                }

                if (!metadataMap.TryGetValue(fileName, out var meta))
                {
                    LocateAndParseCatalog(filePath);
                    if (!metadataMap.TryGetValue(fileName, out meta))
                    {
                        return null;
                    }
                }

                string token = $"{meta.BundleName}_{meta.Hash}-{meta.Crc}";
                return new DecryptedStream(stream, token);
            }
        }

        private static void LocateAndParseCatalog(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(dir))
                    return;

                // Check parent directories for catalog.json (standard Addressables layout: Embed is in aa/Embed/, catalog is in aa/)
                string? currentDir = dir;
                for (int i = 0; i < 3; i++)
                {
                    if (string.IsNullOrEmpty(currentDir))
                        break;

                    string catalogPath = Path.Combine(currentDir, "catalog.json");
                    if (File.Exists(catalogPath))
                    {
                        ParseCatalog(catalogPath);
                        return; // Found and parsed
                    }

                    currentDir = Path.GetDirectoryName(currentDir);
                }

                // Also try application directory
                string appCatalog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog.json");
                if (File.Exists(appCatalog))
                {
                    ParseCatalog(appCatalog);
                }
            }
            catch
            {
                // Ignore errors locating catalog
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
