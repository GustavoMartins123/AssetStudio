#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AssetStudio
{
    public static class DecryptionHelper
    {
        private static readonly Dictionary<string, (string BundleName, string Hash, string Crc)> metadataMap = 
            new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        
        private static readonly HashSet<string> loadedCatalogs = 
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly byte[] encryptionMagic = Encoding.ASCII.GetBytes("Encryption");

        public static Stream? CheckAndDecryptStream(Stream? stream, string? filePath)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek || string.IsNullOrEmpty(filePath))
                return stream;

            long startPos = stream.Position;
            if (stream.Length - startPos < 26) // Minimum header size
                return stream;

            byte[] magic = new byte[10];
            int read = stream.Read(magic, 0, 10);
            stream.Position = startPos; // Seek back

            if (read < 10)
                return stream;

            // Check signature
            bool isEncrypted = true;
            for (int i = 0; i < 10; i++)
            {
                if (magic[i] != encryptionMagic[i])
                {
                    isEncrypted = false;
                    break;
                }
            }

            if (!isEncrypted)
                return stream;

            // It's encrypted! Find the file name.
            string fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
                return stream;

            // Try to find metadata for this file
            if (!metadataMap.ContainsKey(fileName))
            {
                // Try to locate and parse catalog.json
                LocateAndParseCatalog(filePath);
            }

            if (metadataMap.TryGetValue(fileName, out var meta))
            {
                string token = $"{meta.BundleName}_{meta.Hash}-{meta.Crc}";
                return new DecryptedStream(stream, token);
            }
            else
            {
                return stream;
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
            lock (loadedCatalogs)
            {
                if (loadedCatalogs.Contains(fullPath))
                    return;
                loadedCatalogs.Add(fullPath);
            }

            try
            {
                // Read catalog
                string content = File.ReadAllText(fullPath);

                // Extract m_EntryDataString
                string? entryDataString = null;
                var entryMatch = Regex.Match(content, @"""m_EntryDataString""\s*:\s*""([^""]+)""");
                if (entryMatch.Success)
                    entryDataString = entryMatch.Groups[1].Value;

                // Extract m_ExtraDataString
                string? extraDataString = null;
                var extraMatch = Regex.Match(content, @"""m_ExtraDataString""\s*:\s*""([^""]+)""");
                if (extraMatch.Success)
                    extraDataString = extraMatch.Groups[1].Value;

                if (string.IsNullOrEmpty(entryDataString) || string.IsNullOrEmpty(extraDataString))
                    return;

                // Extract m_InternalIds
                List<string> internalIds = new List<string>();
                var idsMatch = Regex.Match(content, @"""m_InternalIds""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                if (idsMatch.Success)
                {
                    var idMatches = Regex.Matches(idsMatch.Groups[1].Value, @"""([^""]+)""");
                    foreach (Match m in idMatches)
                    {
                        internalIds.Add(m.Groups[1].Value);
                    }
                }

                // Extract m_InternalIdPrefixes
                List<string> prefixes = new List<string>();
                var prefMatch = Regex.Match(content, @"""m_InternalIdPrefixes""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                if (prefMatch.Success)
                {
                    var prefMatches = Regex.Matches(prefMatch.Groups[1].Value, @"""([^""]+)""");
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
                        // We are interested in files in Embed or ending with .bundle
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
                                        lock (metadataMap)
                                        {
                                            metadataMap[fileName] = (bundleName, hash, crc);
                                        }
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
