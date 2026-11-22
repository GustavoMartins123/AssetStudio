using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis;

public sealed class AddressablesCatalogAnalyzer
{
    private const long MaxJsonCatalogBytes = 256L * 1024 * 1024;
    private const int MaxBinaryCatalogScanBytes = 4 * 1024 * 1024;
    private const uint BinaryCatalogMagic = 0x0DE38942u;
    private const int MaxIndexedBundles = 50_000;
    private const int MaxBundleDecryptionHintsPerCatalog = 50;
    private const int MaxLocalBundleMagicProbes = 8;

    private static readonly byte[] EncryptionMagicBytes = Encoding.ASCII.GetBytes("Encryption");

    private static readonly HashSet<string> StandardUnityProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.SceneProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.AtlasSpriteProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.JsonAssetProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.TextDataProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.LegacyResourcesProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.InstanceProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.ContentCatalogProvider",
        "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleResource"
    };

    private static readonly string[] EncryptionKeywords =
    {
        "encrypt",
        "decrypt",
        "crypt",
        "cipher",
        "aes",
        "xor",
        "obfusc",
        "secure",
        "offsetbundle",
        "customassetbundle"
    };

    public (AddressablesIndex Index, DecryptionHintsReport DecryptionHints) AnalyzeCatalogs(
        string projectRoot,
        IReadOnlyList<string> catalogPaths,
        List<string> warnings,
        CancellationToken cancellationToken = default)
    {
        var index = new AddressablesIndex
        {
            ProjectRoot = projectRoot,
            GeneratedAtUtc = DateTime.UtcNow
        };
        var hints = new DecryptionHintsReport
        {
            ProjectRoot = projectRoot,
            GeneratedAtUtc = DateTime.UtcNow
        };

        if (catalogPaths == null || catalogPaths.Count == 0)
        {
            return (index, hints);
        }

        var providerSet = new HashSet<string>(StringComparer.Ordinal);
        var seenHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var catalogPath in catalogPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
            {
                continue;
            }

            if (string.Equals(Path.GetExtension(catalogPath), ".bin", StringComparison.OrdinalIgnoreCase))
            {
                AnalyzeBinaryCatalog(catalogPath, index, hints, providerSet, seenHints, warnings);
            }
            else
            {
                AnalyzeJsonCatalog(catalogPath, index, hints, providerSet, seenHints, warnings, cancellationToken);
            }
        }

        index.Providers = providerSet.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase).ToList();
        index.TotalBundleCount = index.Bundles.Count;
        index.LocalBundleCount = index.Bundles.Count(static b => !b.IsRemote);
        index.RemoteBundleCount = index.Bundles.Count(static b => b.IsRemote);

        return (index, hints);
    }

    private static void AnalyzeJsonCatalog(
        string catalogPath,
        AddressablesIndex index,
        DecryptionHintsReport hints,
        HashSet<string> allProviders,
        HashSet<string> seenHints,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var summary = new AddressablesCatalogSummary
        {
            CatalogPath = catalogPath,
            Format = "json"
        };

        try
        {
            var fileInfo = new FileInfo(catalogPath);
            if (fileInfo.Length > MaxJsonCatalogBytes)
            {
                var msg = $"Addressables catalog exceeds {MaxJsonCatalogBytes:N0} byte limit and was skipped: {Path.GetFileName(catalogPath)}";
                summary.Warnings.Add(msg);
                warnings.Add(msg);
                index.Catalogs.Add(summary);
                return;
            }

            using var stream = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                var msg = $"Addressables catalog root is not a JSON object: {Path.GetFileName(catalogPath)}";
                summary.Warnings.Add(msg);
                warnings.Add(msg);
                index.Catalogs.Add(summary);
                return;
            }

            var root = doc.RootElement;
            summary.LocatorId = ReadStringProperty(root, "m_LocatorId") ?? string.Empty;

            var providerIds = ReadStringArrayProperty(root, "m_ProviderIds");
            var internalIds = ReadStringArrayProperty(root, "m_InternalIds");
            var prefixes = ReadStringArrayProperty(root, "m_InternalIdPrefixes");

            var keyDataString = ReadStringProperty(root, "m_KeyDataString");
            var bucketDataString = ReadStringProperty(root, "m_BucketDataString");
            var entryDataString = ReadStringProperty(root, "m_EntryDataString");
            var extraDataString = ReadStringProperty(root, "m_ExtraDataString");

            summary.HasKeyData = !string.IsNullOrWhiteSpace(keyDataString);
            summary.HasBucketData = !string.IsNullOrWhiteSpace(bucketDataString);
            summary.HasEntryData = !string.IsNullOrWhiteSpace(entryDataString);
            summary.HasExtraData = !string.IsNullOrWhiteSpace(extraDataString);
            summary.InternalIdCount = internalIds.Count;

            // Collect providers from m_ProviderIds and m_ResourceProviderData
            var catalogProviders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in providerIds)
            {
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    catalogProviders.Add(provider);
                    allProviders.Add(provider);
                }
            }

            ExtractResourceProviderData(root, catalogProviders, allProviders, catalogPath, hints, seenHints);
            summary.ProviderCount = catalogProviders.Count;

            // Emit provider-level decryption hints for any non-standard or encrypted providers
            foreach (var provider in catalogProviders)
            {
                EvaluateProviderHint(provider, catalogPath, hints, seenHints);
            }

            // Handle obfuscated / custom JSON catalogs missing standard m_InternalIds
            if (internalIds.Count == 0 && providerIds.Count == 0 && !summary.HasEntryData)
            {
                summary.Warnings.Add("Catalog JSON does not contain standard Addressables fields (m_InternalIds/m_ProviderIds/m_EntryDataString); schema may be custom or obfuscated.");
                AddHint(hints, seenHints, new DecryptionHintEntry
                {
                    Provider = "CustomCatalogSchema",
                    CatalogPath = catalogPath,
                    Bundle = string.Empty,
                    Reason = "Catalog JSON uses non-standard/obfuscated field names instead of Unity Addressables m_InternalIds/m_ProviderIds.",
                    Confidence = 0.65
                });
            }

            // Parse m_EntryDataString + m_ExtraDataString for bundle metadata & provider mapping
            var metadataByInternalIdIndex = ParseEntryAndExtraData(
                entryDataString,
                extraDataString,
                providerIds,
                internalIds,
                prefixes,
                catalogPath,
                summary,
                hints,
                seenHints,
                cancellationToken);

            // Build bundle entries
            var seenBundlesInCatalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var catalogBundleHintsEmitted = 0;
            var localMagicProbes = 0;
            var catalogDir = Path.GetDirectoryName(catalogPath) ?? string.Empty;

            for (var i = 0; i < internalIds.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expandedId = ExpandInternalId(internalIds[i], prefixes);
                metadataByInternalIdIndex.TryGetValue(i, out var meta);

                var isBundleCandidate = IsBundlePath(expandedId)
                    || (meta != null && (IsBundleProvider(meta.ProviderId) || !string.IsNullOrEmpty(meta.BundleName)));

                if (!isBundleCandidate || !seenBundlesInCatalog.Add(expandedId))
                {
                    continue;
                }

                var providerId = meta?.ProviderId ?? string.Empty;
                var nonStandardProvider = !string.IsNullOrWhiteSpace(providerId) && IsNonStandardProvider(providerId);
                var isRemote = IsRemoteUrl(expandedId);
                var bundleFileName = ExtractBundleFileName(expandedId);

                var entry = new AddressablesBundleEntry
                {
                    CatalogPath = catalogPath,
                    InternalId = expandedId,
                    BundleFileName = bundleFileName,
                    BundleName = meta?.BundleName ?? string.Empty,
                    Hash = meta?.Hash ?? string.Empty,
                    Crc = meta?.Crc ?? string.Empty,
                    ProviderId = providerId,
                    IsRemote = isRemote,
                    UsesNonStandardProvider = nonStandardProvider
                };

                summary.BundleCount++;
                if (index.Bundles.Count < MaxIndexedBundles)
                {
                    index.Bundles.Add(entry);
                }

                // Emit bundle-level decryption hint if using non-standard provider or custom options
                if ((nonStandardProvider || (meta?.HasCustomOptions == true))
                    && catalogBundleHintsEmitted < MaxBundleDecryptionHintsPerCatalog)
                {
                    catalogBundleHintsEmitted++;
                    var reason = nonStandardProvider
                        ? $"Bundle loaded via non-standard provider '{providerId}'."
                        : $"Bundle uses custom extra data options '{meta?.ExtraClassName}'.";
                    var confidence = HasEncryptionKeyword(providerId) || HasEncryptionKeyword(meta?.ExtraClassName)
                        ? 0.92
                        : 0.72;
                    AddHint(hints, seenHints, new DecryptionHintEntry
                    {
                        Provider = !string.IsNullOrEmpty(providerId) ? providerId : (meta?.ExtraClassName ?? "CustomProvider"),
                        CatalogPath = catalogPath,
                        Bundle = !string.IsNullOrEmpty(bundleFileName) ? bundleFileName : expandedId,
                        Reason = reason,
                        Confidence = confidence,
                        MetadataSummary = BuildBundleMetadataSummary(entry)
                    });
                }

                // Lightweight 10-byte magic check on a few local bundles for AssetStudio's "Encryption" header
                if (!isRemote && localMagicProbes < MaxLocalBundleMagicProbes && !string.IsNullOrEmpty(bundleFileName))
                {
                    var localBundlePath = TryResolveLocalBundlePath(catalogDir, expandedId, bundleFileName);
                    if (localBundlePath != null)
                    {
                        localMagicProbes++;
                        if (StartsWithEncryptionMagic(localBundlePath))
                        {
                            AddHint(hints, seenHints, new DecryptionHintEntry
                            {
                                Provider = !string.IsNullOrEmpty(providerId) ? providerId : "DecryptionHelper.EncryptionMagic",
                                CatalogPath = catalogPath,
                                Bundle = bundleFileName,
                                Reason = "Local bundle starts with 'Encryption' magic header compatible with DecryptionHelper token derivation.",
                                Confidence = 0.98,
                                MetadataSummary = BuildBundleMetadataSummary(entry)
                            });
                        }
                    }
                }
            }

            index.Catalogs.Add(summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"Failed to parse Addressables catalog '{Path.GetFileName(catalogPath)}': {ex.Message}";
            summary.Warnings.Add(msg);
            warnings.Add(msg);
            index.Catalogs.Add(summary);
        }
    }

    private static void AnalyzeBinaryCatalog(
        string catalogPath,
        AddressablesIndex index,
        DecryptionHintsReport hints,
        HashSet<string> allProviders,
        HashSet<string> seenHints,
        List<string> warnings)
    {
        var summary = new AddressablesCatalogSummary
        {
            CatalogPath = catalogPath,
            Format = "bin"
        };

        try
        {
            using var stream = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var readLength = (int)Math.Min(stream.Length, MaxBinaryCatalogScanBytes);
            var buffer = new byte[readLength];
            var totalRead = 0;
            while (totalRead < readLength)
            {
                var read = stream.Read(buffer, totalRead, readLength - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            var asciiTokens = ExtractBinaryCatalogStrings(buffer.AsSpan(0, totalRead), minLength: 6);
            var catalogProviders = new HashSet<string>(StringComparer.Ordinal);
            var seenBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in asciiTokens)
            {
                if (LooksLikeProviderClassName(token))
                {
                    catalogProviders.Add(token);
                    allProviders.Add(token);
                    EvaluateProviderHint(token, catalogPath, hints, seenHints);
                }
                else if (IsBundlePath(token) && !token.Contains(' ') && seenBundles.Add(token))
                {
                    summary.BundleCount++;
                    if (index.Bundles.Count < MaxIndexedBundles)
                    {
                        index.Bundles.Add(new AddressablesBundleEntry
                        {
                            CatalogPath = catalogPath,
                            InternalId = token,
                            BundleFileName = ExtractBundleFileName(token),
                            IsRemote = IsRemoteUrl(token)
                        });
                    }
                }
            }

            summary.ProviderCount = catalogProviders.Count;
            summary.InternalIdCount = seenBundles.Count;
            index.Catalogs.Add(summary);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to inspect binary Addressables catalog '{Path.GetFileName(catalogPath)}': {ex.Message}";
            summary.Warnings.Add(msg);
            warnings.Add(msg);
            index.Catalogs.Add(summary);
        }
    }

    private static Dictionary<int, EntryExtraMetadata> ParseEntryAndExtraData(
        string? entryDataString,
        string? extraDataString,
        List<string> providerIds,
        List<string> internalIds,
        List<string> prefixes,
        string catalogPath,
        AddressablesCatalogSummary summary,
        DecryptionHintsReport hints,
        HashSet<string> seenHints,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, EntryExtraMetadata>();
        if (string.IsNullOrWhiteSpace(entryDataString))
        {
            return result;
        }

        byte[] entryBytes;
        try
        {
            entryBytes = Convert.FromBase64String(entryDataString);
        }
        catch
        {
            summary.Warnings.Add("m_EntryDataString is not valid Base64.");
            return result;
        }

        byte[]? extraBytes = null;
        if (!string.IsNullOrWhiteSpace(extraDataString))
        {
            try
            {
                extraBytes = Convert.FromBase64String(extraDataString);
            }
            catch
            {
                summary.Warnings.Add("m_ExtraDataString is not valid Base64.");
            }
        }

        if (entryBytes.Length < 4)
        {
            return result;
        }

        var entryCount = BitConverter.ToInt32(entryBytes, 0);
        if (entryCount < 0)
        {
            return result;
        }

        var maxPossibleEntries = (entryBytes.Length - 4) / 28;
        var count = Math.Min(entryCount, maxPossibleEntries);
        summary.EntryCount = count;

        var customExtraClassesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var offset = 4 + i * 28;
            var internalIdIdx = BitConverter.ToInt32(entryBytes, offset);
            var providerIdx = BitConverter.ToInt32(entryBytes, offset + 4);
            var dataIdx = BitConverter.ToInt32(entryBytes, offset + 16);

            if (internalIdIdx < 0 || internalIdIdx >= internalIds.Count)
            {
                continue;
            }

            var providerId = providerIdx >= 0 && providerIdx < providerIds.Count
                ? providerIds[providerIdx]
                : string.Empty;

            var meta = new EntryExtraMetadata
            {
                ProviderId = providerId
            };

            if (extraBytes != null && dataIdx >= 0)
            {
                var extraObj = ReadSerializedExtraObject(extraBytes, dataIdx);
                if (extraObj != null)
                {
                    meta.ExtraClassName = extraObj.ClassName;
                    if (!string.IsNullOrWhiteSpace(extraObj.ClassName)
                        && !extraObj.ClassName.StartsWith("UnityEngine.ResourceManagement.", StringComparison.OrdinalIgnoreCase))
                    {
                        meta.HasCustomOptions = true;
                        if (customExtraClassesSeen.Add(extraObj.ClassName))
                        {
                            AddHint(hints, seenHints, new DecryptionHintEntry
                            {
                                Provider = !string.IsNullOrEmpty(providerId) ? providerId : extraObj.ClassName,
                                CatalogPath = catalogPath,
                                Bundle = ExtractBundleFileName(ExpandInternalId(internalIds[internalIdIdx], prefixes)),
                                Reason = $"Non-standard Addressables extra data type '{extraObj.ClassName}' detected in m_ExtraDataString.",
                                Confidence = HasEncryptionKeyword(extraObj.ClassName) ? 0.90 : 0.70
                            });
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(extraObj.JsonText))
                    {
                        ParseBundleRequestOptionsJson(extraObj.JsonText, meta);
                    }
                }
            }

            result[internalIdIdx] = meta;
        }

        return result;
    }

    private static void ExtractResourceProviderData(
        JsonElement root,
        HashSet<string> catalogProviders,
        HashSet<string> allProviders,
        string catalogPath,
        DecryptionHintsReport hints,
        HashSet<string> seenHints)
    {
        if (!TryGetPropertyCaseInsensitive(root, "m_ResourceProviderData", out var providerData)
            || providerData.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in providerData.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringProperty(item, "m_Id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                catalogProviders.Add(id);
                allProviders.Add(id);
            }

            if (TryGetPropertyCaseInsensitive(item, "m_ObjectType", out var objectType)
                && objectType.ValueKind == JsonValueKind.Object)
            {
                var className = ReadStringProperty(objectType, "m_ClassName");
                if (!string.IsNullOrWhiteSpace(className))
                {
                    catalogProviders.Add(className);
                    allProviders.Add(className);
                    EvaluateProviderHint(className, catalogPath, hints, seenHints);
                }
            }
        }
    }

    private static void EvaluateProviderHint(
        string providerId,
        string catalogPath,
        DecryptionHintsReport hints,
        HashSet<string> seenHints)
    {
        if (string.IsNullOrWhiteSpace(providerId) || !IsNonStandardProvider(providerId))
        {
            return;
        }

        double confidence;
        string reason;
        if (HasEncryptionKeyword(providerId))
        {
            confidence = 0.95;
            reason = $"Encrypted or custom-cipher Addressables provider detected: '{providerId}'.";
        }
        else if (IsBundleProvider(providerId))
        {
            confidence = 0.78;
            reason = $"Custom AssetBundle provider detected outside Unity standard namespace: '{providerId}'.";
        }
        else
        {
            confidence = 0.60;
            reason = $"Non-standard IResourceProvider detected in Addressables catalog: '{providerId}'.";
        }

        AddHint(hints, seenHints, new DecryptionHintEntry
        {
            Provider = providerId,
            CatalogPath = catalogPath,
            Bundle = string.Empty,
            Reason = reason,
            Confidence = confidence
        });
    }

    private static bool IsNonStandardProvider(string providerId)
    {
        if (StandardUnityProviderIds.Contains(providerId))
        {
            return false;
        }

        if (HasEncryptionKeyword(providerId))
        {
            return true;
        }

        return !providerId.StartsWith("UnityEngine.ResourceManagement.ResourceProviders.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBundleProvider(string? providerId)
    {
        return !string.IsNullOrWhiteSpace(providerId)
            && providerId.Contains("Bundle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeProviderClassName(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > 200
            || !token.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || !token.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '+' || c == '`'))
            {
                return false;
            }
        }

        return char.IsLetter(token[0]) && char.IsLetterOrDigit(token[^1]);
    }

    private static bool HasEncryptionKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var keyword in EncryptionKeywords)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddHint(
        DecryptionHintsReport hints,
        HashSet<string> seenHints,
        DecryptionHintEntry entry)
    {
        var dedupeKey = $"{entry.CatalogPath}|{entry.Provider}|{entry.Bundle}|{entry.Reason}";
        if (seenHints.Add(dedupeKey))
        {
            hints.Hints.Add(entry);
        }
    }

    private static string BuildBundleMetadataSummary(AddressablesBundleEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.BundleName))
        {
            parts.Add($"bundle={entry.BundleName}");
        }

        if (!string.IsNullOrWhiteSpace(entry.Hash))
        {
            parts.Add($"hash={entry.Hash}");
        }

        if (!string.IsNullOrWhiteSpace(entry.Crc))
        {
            parts.Add($"crc={entry.Crc}");
        }

        return string.Join(", ", parts);
    }

    private static void ParseBundleRequestOptionsJson(string json, EntryExtraMetadata meta)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var root = doc.RootElement;
            meta.BundleName = ReadStringProperty(root, "m_BundleName") ?? string.Empty;
            meta.Hash = ReadStringProperty(root, "m_Hash") ?? string.Empty;

            if (TryGetPropertyCaseInsensitive(root, "m_Crc", out var crcProp))
            {
                meta.Crc = crcProp.ValueKind switch
                {
                    JsonValueKind.Number => crcProp.GetRawText(),
                    JsonValueKind.String => crcProp.GetString() ?? string.Empty,
                    _ => string.Empty
                };
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (HasEncryptionKeyword(prop.Name))
                {
                    meta.HasCustomOptions = true;
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static SerializedExtraObject? ReadSerializedExtraObject(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return null;
        }

        var objType = data[offset++];
        if (objType == 0) // AsciiString
        {
            if (offset + 4 > data.Length)
            {
                return null;
            }

            var len = BitConverter.ToInt32(data, offset);
            offset += 4;
            if (len < 0 || offset + len > data.Length)
            {
                return null;
            }

            return new SerializedExtraObject { JsonText = Encoding.ASCII.GetString(data, offset, len) };
        }

        if (objType == 1) // UnicodeString
        {
            if (offset + 4 > data.Length)
            {
                return null;
            }

            var len = BitConverter.ToInt32(data, offset);
            offset += 4;
            if (len < 0 || offset + len > data.Length)
            {
                return null;
            }

            return new SerializedExtraObject { JsonText = Encoding.Unicode.GetString(data, offset, len) };
        }

        if (objType == 7) // JsonObject
        {
            if (offset >= data.Length)
            {
                return null;
            }

            var assemblyLen = data[offset++];
            if (offset + assemblyLen > data.Length)
            {
                return null;
            }

            var assemblyName = Encoding.ASCII.GetString(data, offset, assemblyLen);
            offset += assemblyLen;

            if (offset >= data.Length)
            {
                return null;
            }

            var classLen = data[offset++];
            if (offset + classLen > data.Length)
            {
                return null;
            }

            var className = Encoding.ASCII.GetString(data, offset, classLen);
            offset += classLen;

            if (offset + 4 > data.Length)
            {
                return null;
            }

            var jsonLen = BitConverter.ToInt32(data, offset);
            offset += 4;
            if (jsonLen < 0 || offset + jsonLen > data.Length)
            {
                return null;
            }

            var jsonText = Encoding.Unicode.GetString(data, offset, jsonLen);
            return new SerializedExtraObject
            {
                AssemblyName = assemblyName,
                ClassName = className,
                JsonText = jsonText
            };
        }

        return null;
    }

    private static string ExpandInternalId(string value, List<string> prefixes)
    {
        if (prefixes.Count == 0 || string.IsNullOrEmpty(value))
        {
            return value;
        }

        var hashIdx = value.IndexOf('#');
        if (hashIdx <= 0)
        {
            return value;
        }

        if (int.TryParse(value.AsSpan(0, hashIdx), out var idx) && idx >= 0 && idx < prefixes.Count)
        {
            return prefixes[idx] + value.Substring(hashIdx + 1);
        }

        return value;
    }

    private static bool IsBundlePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/Embed/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("\\Embed\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("RemoteLoadPath", StringComparison.OrdinalIgnoreCase)
            || value.Contains("RemoteUrl", StringComparison.OrdinalIgnoreCase)
            || value.Contains("CdnUrl", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBundleFileName(string internalId)
    {
        if (string.IsNullOrWhiteSpace(internalId))
        {
            return string.Empty;
        }

        var queryIdx = internalId.IndexOf('?');
        var clean = queryIdx >= 0 ? internalId.Substring(0, queryIdx) : internalId;
        var lastSlash = Math.Max(clean.LastIndexOf('/'), clean.LastIndexOf('\\'));
        return lastSlash >= 0 ? clean.Substring(lastSlash + 1) : clean;
    }

    private static string? TryResolveLocalBundlePath(string catalogDir, string expandedId, string bundleFileName)
    {
        if (string.IsNullOrWhiteSpace(catalogDir) || !Directory.Exists(catalogDir))
        {
            return null;
        }

        var direct = Path.Combine(catalogDir, bundleFileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var embed = Path.Combine(catalogDir, "Embed", bundleFileName);
        if (File.Exists(embed))
        {
            return embed;
        }

        // Check immediate platform subdirectories (e.g., StandaloneWindows64)
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(catalogDir))
            {
                var candidate = Path.Combine(subDir, bundleFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
        }

        if (!expandedId.Contains('{') && File.Exists(expandedId))
        {
            return expandedId;
        }

        return null;
    }

    private static bool StartsWithEncryptionMagic(string bundlePath)
    {
        try
        {
            using var stream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[10];
            var read = stream.Read(header);
            if (read < EncryptionMagicBytes.Length)
            {
                return false;
            }

            return header.Slice(0, EncryptionMagicBytes.Length).SequenceEqual(EncryptionMagicBytes);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ExtractBinaryCatalogStrings(ReadOnlySpan<byte> data, int minLength)
    {
        if (data.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4)) == BinaryCatalogMagic)
        {
            var lengthPrefixed = ExtractLengthPrefixedAsciiStrings(data.Slice(8), minLength, maxLength: 2048);
            if (lengthPrefixed.Count > 0)
            {
                return lengthPrefixed;
            }
        }

        return ExtractPrintableAsciiStrings(data, minLength);
    }

    private static List<string> ExtractLengthPrefixedAsciiStrings(ReadOnlySpan<byte> data, int minLength, int maxLength)
    {
        var results = new List<string>();
        var i = 0;

        while (i <= data.Length - 4)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i, 4));
            if (length >= minLength && length <= maxLength && i + 4 + length <= data.Length)
            {
                var slice = data.Slice(i + 4, length);
                if (IsAllPrintableAscii(slice))
                {
                    results.Add(Encoding.ASCII.GetString(slice));
                    i += 4 + length;
                    continue;
                }
            }

            i++;
        }

        return results;
    }

    private static bool IsAllPrintableAscii(ReadOnlySpan<byte> slice)
    {
        for (var i = 0; i < slice.Length; i++)
        {
            var b = slice[i];
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ExtractPrintableAsciiStrings(ReadOnlySpan<byte> data, int minLength)
    {
        var results = new List<string>();
        var start = -1;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            var printable = b >= 0x20 && b <= 0x7E;
            if (printable)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                TryEmitBoundedAsciiRun(data, start, i - start, minLength, results);
                start = -1;
            }
        }

        if (start >= 0)
        {
            TryEmitBoundedAsciiRun(data, start, data.Length - start, minLength, results);
        }

        return results;
    }

    private static void TryEmitBoundedAsciiRun(
        ReadOnlySpan<byte> data,
        int start,
        int runLength,
        int minLength,
        List<string> results)
    {
        var effectiveLength = runLength;

        // Respect 4-byte little-endian length-prefixed strings even in binary catalogs without
        // the standard 0x0DE38942 header, avoiding trailing length/offset bytes merging into tokens.
        if (start >= 4)
        {
            var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start - 4, 4));
            var trailingBytes = runLength - declaredLength;
            if (declaredLength >= minLength
                && declaredLength <= 512
                && trailingBytes > 0
                && trailingBytes <= 4
                && start + declaredLength + 4 <= data.Length)
            {
                effectiveLength = declaredLength;
            }
        }

        if (effectiveLength >= minLength && effectiveLength <= 512)
        {
            results.Add(Encoding.ASCII.GetString(data.Slice(start, effectiveLength)));
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadStringProperty(JsonElement obj, string name)
    {
        if (TryGetPropertyCaseInsensitive(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static List<string> ReadStringArrayProperty(JsonElement obj, string name)
    {
        var items = new List<string>();
        if (!TryGetPropertyCaseInsensitive(obj, name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var element in prop.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                if (str != null)
                {
                    items.Add(str);
                }
            }
        }

        return items;
    }

    private sealed class EntryExtraMetadata
    {
        public string ProviderId { get; set; } = string.Empty;
        public string BundleName { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Crc { get; set; } = string.Empty;
        public string ExtraClassName { get; set; } = string.Empty;
        public bool HasCustomOptions { get; set; }
    }

    private sealed class SerializedExtraObject
    {
        public string AssemblyName { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string JsonText { get; set; } = string.Empty;
    }
}
