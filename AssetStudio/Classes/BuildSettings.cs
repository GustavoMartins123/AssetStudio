using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public sealed class BuildSettings : Object
    {
        private const int MaxReasonableSceneCount = 100_000;

        public string[] scenes { get; set; } = Array.Empty<string>();
        public string[] preloadedPlugins { get; set; } = Array.Empty<string>();
        public string m_Version { get; set; } = string.Empty;
        public bool IsTruncated { get; private set; }

        /// <summary>
        /// Unity 3.x through 5.2 stores scene build index 0 in <c>mainData</c> and index 1+ in <c>level0..</c>.
        /// Unity 5.3+ stores scene build index 0 in <c>level0</c>, index 1 in <c>level1</c>, etc.
        /// </summary>
        public bool UsesMainDataForFirstScene =>
            version != null
            && version.Length >= 2
            && version[0] > 0
            && (version[0] < 5 || (version[0] == 5 && version[1] < 3));

        public BuildSettings(ObjectReader reader) : base(reader)
        {
            if (TryReadFromTypeTree(reader))
            {
                return;
            }

            try
            {
                scenes = ReadBoundedStringArray(reader, "scenes");

                if (version != null && version.Length > 0 && version[0] >= 5 && RemainingObjectBytes(reader) >= 4)
                {
                    try
                    {
                        preloadedPlugins = ReadBoundedStringArray(reader, "preloadedPlugins");
                    }
                    catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
                    {
                        preloadedPlugins = Array.Empty<string>();
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                scenes ??= Array.Empty<string>();
                preloadedPlugins ??= Array.Empty<string>();
                Logger.Warning($"BuildSettings (PathID {m_PathID}): failed to read scene list ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        public IReadOnlyList<(string LevelFileName, int BuildIndex, string ScenePath)> GetLevelSceneMappings()
        {
            if (scenes == null || scenes.Length == 0)
            {
                return Array.Empty<(string, int, string)>();
            }

            var usesMainData = UsesMainDataForFirstScene;
            var mappings = new List<(string, int, string)>(scenes.Length);
            for (int i = 0; i < scenes.Length; i++)
            {
                var scenePath = scenes[i] ?? string.Empty;
                var levelName = usesMainData
                    ? (i == 0 ? "mainData" : $"level{i - 1}")
                    : $"level{i}";
                mappings.Add((levelName, i, scenePath));
            }

            return mappings;
        }

        public Dictionary<string, string> BuildLevelFileToScenePathMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (levelFileName, _, scenePath) in GetLevelSceneMappings())
            {
                if (!string.IsNullOrWhiteSpace(scenePath))
                {
                    map[levelFileName] = scenePath;
                }
            }

            return map;
        }

        public bool TryResolveSceneForLevelFile(string? levelFileName, out string scenePath, out int buildIndex)
        {
            scenePath = string.Empty;
            buildIndex = -1;

            if (string.IsNullOrWhiteSpace(levelFileName) || scenes == null || scenes.Length == 0)
            {
                return false;
            }

            var normalized = levelFileName.Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            var leaf = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            if (string.IsNullOrWhiteSpace(leaf))
            {
                return false;
            }

            var usesMainData = UsesMainDataForFirstScene;
            if (string.Equals(leaf, "mainData", StringComparison.OrdinalIgnoreCase))
            {
                if (usesMainData && scenes.Length > 0)
                {
                    buildIndex = 0;
                    scenePath = scenes[0] ?? string.Empty;
                    return !string.IsNullOrEmpty(scenePath);
                }
                return false;
            }

            if (leaf.StartsWith("level", StringComparison.OrdinalIgnoreCase) && leaf.Length > 5)
            {
                var suffix = leaf.Substring(5);
                var dot = suffix.IndexOf('.');
                if (dot >= 0)
                {
                    suffix = suffix.Substring(0, dot);
                }

                if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int levelIndex) && levelIndex >= 0)
                {
                    int candidateBuildIndex = usesMainData ? levelIndex + 1 : levelIndex;
                    if (candidateBuildIndex >= 0 && candidateBuildIndex < scenes.Length)
                    {
                        buildIndex = candidateBuildIndex;
                        scenePath = scenes[candidateBuildIndex] ?? string.Empty;
                        return !string.IsNullOrEmpty(scenePath);
                    }
                }
            }

            return false;
        }

        public bool TryGetScenePathByBuildIndex(int buildIndex, out string scenePath)
        {
            scenePath = string.Empty;
            if (scenes == null || buildIndex < 0 || buildIndex >= scenes.Length)
            {
                return false;
            }

            scenePath = scenes[buildIndex] ?? string.Empty;
            return !string.IsNullOrEmpty(scenePath);
        }

        public static string ExtractSceneDisplayName(string? scenePath)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                return string.Empty;
            }

            var normalized = scenePath.Replace('\\', '/').Trim().TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            var leaf = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            if (leaf.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                leaf = leaf.Substring(0, leaf.Length - ".unity".Length);
            }

            return leaf.Trim();
        }

        private bool TryReadFromTypeTree(ObjectReader reader)
        {
            var typeTree = serializedType?.m_Type;
            if (typeTree?.m_Nodes == null || typeTree.m_Nodes.Count == 0)
            {
                return false;
            }

            var savedPosition = reader.Position;
            try
            {
                var dict = TypeTreeHelper.ReadType(typeTree, reader);
                if (dict == null)
                {
                    reader.Position = savedPosition;
                    return false;
                }

                var parsedScenes = ExtractStringArray(dict, "scenes") ?? ExtractStringArray(dict, "levels");
                if (parsedScenes == null)
                {
                    reader.Position = savedPosition;
                    return false;
                }

                scenes = parsedScenes;
                preloadedPlugins = ExtractStringArray(dict, "preloadedPlugins") ?? Array.Empty<string>();
                if (dict.Contains("m_Version") && dict["m_Version"] is string verStr)
                {
                    m_Version = verStr;
                }

                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"BuildSettings (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
                return true;
            }
        }

        private static string[]? ExtractStringArray(OrderedDictionary dict, string key)
        {
            if (!dict.Contains(key) || dict[key] is not IEnumerable enumerable || dict[key] is string)
            {
                return null;
            }

            var list = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is string s)
                {
                    list.Add(s);
                }
                else if (item != null)
                {
                    list.Add(item.ToString() ?? string.Empty);
                }

                if (list.Count >= MaxReasonableSceneCount)
                {
                    break;
                }
            }

            return list.ToArray();
        }

        private static string[] ReadBoundedStringArray(ObjectReader reader, string fieldName)
        {
            if (RemainingObjectBytes(reader) < 4)
            {
                throw new EndOfStreamException($"Truncated BuildSettings.{fieldName} length.");
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > MaxReasonableSceneCount)
            {
                throw new OverflowException($"Invalid BuildSettings.{fieldName} length: {count}.");
            }

            reader.CheckArrayLength(count, 4);
            if (count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ReadBoundedAlignedString(reader);
            }

            return result;
        }

        private static string ReadBoundedAlignedString(ObjectReader reader)
        {
            if (RemainingObjectBytes(reader) < 4)
            {
                throw new EndOfStreamException("Truncated BuildSettings string length.");
            }

            int length = reader.ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException($"Negative BuildSettings string length: {length}.");
            }

            if (length == 0)
            {
                return string.Empty;
            }

            if (length > RemainingObjectBytes(reader))
            {
                throw new EndOfStreamException($"BuildSettings string length {length} exceeds remaining object bytes.");
            }

            var bytes = reader.ReadBytes(length);
            int mod = length & 3;
            if (mod != 0)
            {
                int pad = 4 - mod;
                if (pad > RemainingObjectBytes(reader))
                {
                    throw new EndOfStreamException("Truncated BuildSettings string alignment padding.");
                }
                reader.Position += pad;
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static long RemainingObjectBytes(ObjectReader reader)
        {
            var remainingInObject = (long)reader.byteSize - (reader.Position - reader.byteStart);
            var remainingInStream = reader.BaseStream.CanSeek
                ? reader.BaseStream.Length - reader.Position
                : remainingInObject;
            return Math.Min(remainingInObject, remainingInStream);
        }
    }
}
