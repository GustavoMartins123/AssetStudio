using System;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class ResourceReader
    {
        private bool needSearch;
        private string path;
        private SerializedFile assetsFile;
        private long offset;
        private long size;
        private BinaryReader reader;
        private bool isEmbedded;

        public int Size { get => (int)size; }

        public ResourceReader(string path, SerializedFile assetsFile, long offset, long size)
        {
            needSearch = true;
            this.path = NormalizeResourcePath(path);
            this.assetsFile = assetsFile;
            this.offset = offset;
            this.size = size;
        }

        public ResourceReader(byte[] data)
        {
            reader = new BinaryReader(new MemoryStream(data));
            offset = 0;
            size = data.Length;
        }

        public ResourceReader(BinaryReader reader, long offset, long size)
        {
            if (reader is ObjectReader objReader)
            {
                this.assetsFile = objReader.assetsFile;
                this.isEmbedded = true;
            }
            else
            {
                this.reader = reader;
            }
            this.offset = offset;
            this.size = size;
        }

        private BinaryReader GetReader(out bool shouldDispose)
        {
            shouldDispose = false;
            if (needSearch)
            {
                var resourceFileName = Path.GetFileName(path);
                var normalizedPath = NormalizePath(path);

                if (assetsFile != null && (string.Equals(resourceFileName, assetsFile.fileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedPath, assetsFile.fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    needSearch = false;
                    reader = assetsFile.reader;
                    return CloneIfFileReader(reader, out shouldDispose);
                }

                if (assetsFile.assetsManager.resourceFileReaders.TryGetValue(normalizedPath, out reader) && CanReadRange(reader))
                {
                    needSearch = false;
                    return CloneIfFileReader(reader, out shouldDispose);
                }
                if (TryGetResourceReaderBySuffix(normalizedPath, out reader))
                {
                    needSearch = false;
                    return CloneIfFileReader(reader, out shouldDispose);
                }
                if (!HasDirectory(path)
                    && assetsFile.assetsManager.resourceFileReaders.TryGetValue(resourceFileName, out reader)
                    && CanReadRange(reader))
                {
                    needSearch = false;
                    return CloneIfFileReader(reader, out shouldDispose);
                }

                var resourceFilePath = ResolveResourceFilePath(resourceFileName, normalizedPath);
                if (resourceFilePath != null)
                {
                    var resolvedKey = NormalizePath(Path.GetFullPath(resourceFilePath));
                    needSearch = false;
                    var fileReader = new FileReader(resourceFilePath);
                    assetsFile.assetsManager.resourceFileReaders[normalizedPath] = fileReader;
                    assetsFile.assetsManager.resourceFileReaders[resolvedKey] = fileReader;
                    if (!HasDirectory(path) && !assetsFile.assetsManager.resourceFileReaders.ContainsKey(resourceFileName))
                    {
                        assetsFile.assetsManager.resourceFileReaders[resourceFileName] = fileReader;
                    }
                    reader = fileReader;
                    return CloneIfFileReader(reader, out shouldDispose);
                }
                throw new FileNotFoundException($"Can't find the resource file {path}");
            }
            else
            {
                if (isEmbedded && assetsFile != null)
                {
                    shouldDispose = true;
                    return assetsFile.reader.Clone();
                }
                return CloneIfFileReader(reader, out shouldDispose);
            }
        }

        private bool TryGetResourceReaderBySuffix(string normalizedPath, out BinaryReader reader)
        {
            reader = null;
            if (string.IsNullOrEmpty(normalizedPath) || assetsFile?.assetsManager == null)
            {
                return false;
            }

            BinaryReader matchedReader = null;
            foreach (var pair in assetsFile.assetsManager.resourceFileReaders)
            {
                var key = NormalizePath(pair.Key);
                if (!key.EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) || !CanReadRange(pair.Value))
                {
                    continue;
                }

                if (matchedReader == null || ReferenceEquals(matchedReader, pair.Value))
                {
                    matchedReader = pair.Value;
                    continue;
                }

                return false;
            }

            reader = matchedReader;
            return reader != null;
        }

        private static BinaryReader CloneIfFileReader(BinaryReader binaryReader, out bool shouldDispose)
        {
            if (binaryReader is FileReader fileReader)
            {
                shouldDispose = true;
                return fileReader.Clone();
            }

            shouldDispose = false;
            return binaryReader;
        }

        private string ResolveResourceFilePath(string resourceFileName, string normalizedPath)
        {
            if (Path.IsPathRooted(path) && File.Exists(path))
            {
                return path;
            }

            if (Path.IsPathRooted(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                {
                    var file = Path.GetFileName(path);
                    var matchedFile = Directory.EnumerateFiles(dir)
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
                    if (matchedFile != null)
                    {
                        return matchedFile;
                    }
                }
            }

            foreach (var root in GetSearchRoots())
            {
                var directPath = Path.Combine(root, path);
                if (File.Exists(directPath))
                {
                    return directPath;
                }

                var resourceFilePath = Path.Combine(root, resourceFileName);
                if (File.Exists(resourceFilePath))
                {
                    return resourceFilePath;
                }

                var directDir = Path.GetDirectoryName(directPath);
                if (directDir != null && Directory.Exists(directDir))
                {
                    var fileToFind = Path.GetFileName(directPath);
                    var matchedFile = Directory.EnumerateFiles(directDir)
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileToFind, StringComparison.OrdinalIgnoreCase));
                    if (matchedFile != null)
                    {
                        return matchedFile;
                    }
                }

                if (Directory.Exists(root))
                {
                    var matchedFile = Directory.EnumerateFiles(root)
                        .FirstOrDefault(f => string.Equals(Path.GetFileName(f), resourceFileName, StringComparison.OrdinalIgnoreCase));
                    if (matchedFile != null)
                    {
                        return matchedFile;
                    }
                }

                if (Directory.Exists(root))
                {
                    var findFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(f => string.Equals(Path.GetFileName(f), resourceFileName, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var suffixMatches = findFiles
                        .Where(x => NormalizePath(x).EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (suffixMatches.Length == 1)
                    {
                        return suffixMatches[0];
                    }
                    if (suffixMatches.Length > 1)
                    {
                        throw new IOException($"Found multiple resource files matching {path}");
                    }
                    if (findFiles.Length == 1)
                    {
                        return findFiles[0];
                    }
                }
            }

            // Fallback for renamed companion files (like level77 + level77.resS / level77.assets.resS)
            var assetsFileDir = Path.GetDirectoryName(assetsFile.fullName);
            if (!string.IsNullOrEmpty(assetsFileDir))
            {
                var assetsFileNameWithoutExt = Path.GetFileNameWithoutExtension(assetsFile.fullName);
                var resourceExtension = Path.GetExtension(resourceFileName); // e.g. ".resS" or ".resource"
                if (!string.IsNullOrEmpty(resourceExtension))
                {
                    string[] possibleNames = {
                        assetsFileNameWithoutExt + resourceExtension,
                        assetsFileNameWithoutExt + ".assets" + resourceExtension,
                        assetsFileNameWithoutExt + (resourceExtension.Equals(".resS", StringComparison.OrdinalIgnoreCase) ? ".resource" : ".resS"),
                        assetsFileNameWithoutExt + ".assets" + (resourceExtension.Equals(".resS", StringComparison.OrdinalIgnoreCase) ? ".resource" : ".resS")
                    };

                    foreach (var name in possibleNames)
                    {
                        var companionPath = Path.Combine(assetsFileDir, name);
                        if (File.Exists(companionPath))
                        {
                            return companionPath;
                        }
                    }
                }
            }

            return null;
        }

        private string[] GetSearchRoots()
        {
            var assetsFileDirectory = Path.GetDirectoryName(assetsFile.fullName);
            if (!string.IsNullOrEmpty(assetsFile.assetsManager.ProjectRoot))
            {
                return new[] { assetsFileDirectory, assetsFile.assetsManager.ProjectRoot }
                    .Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            return new[] { assetsFileDirectory }
                .Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x))
                .ToArray();
        }

        private static string NormalizePath(string value)
        {
            return value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private static string NormalizeResourcePath(string value)
        {
            if (value == null)
            {
                return null;
            }

            const string archivePrefix = "archive:/";
            if (value.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(archivePrefix.Length);
                var slash = value.IndexOf('/');
                if (slash >= 0)
                {
                    value = value.Substring(slash + 1);
                }
            }

            return value;
        }

        private static bool HasDirectory(string value)
        {
            return !string.IsNullOrEmpty(Path.GetDirectoryName(value));
        }

        private bool CanReadRange(BinaryReader binaryReader)
        {
            try
            {
                return offset >= 0 && size >= 0 && binaryReader.BaseStream.Length >= offset + size;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public byte[] GetData()
        {
            var binaryReader = GetReader(out var shouldDispose);
            try
            {
                binaryReader.BaseStream.Position = offset;
                var data = binaryReader.ReadBytes((int)size);
                if (data.Length != size)
                {
                    throw new EndOfStreamException($"Unable to read {size} bytes from resource {path} at offset {offset}. Read {data.Length} bytes.");
                }

                // Testa se a maioria dos bytes é 0x01 (indicativo de XOR)
                int sampleSize = Math.Min(data.Length, 1024);
                int count01 = 0;
                for (int i = 0; i < sampleSize; i++)
                {
                    if (data[i] == 0x01)
                        count01++;
                }

                // Se for criptografado com XOR
                if (sampleSize > 0 && count01 > sampleSize * 0.5)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] ^= 0xFF;
                    }
                }

                return data;
            }
            finally
            {
                if (shouldDispose)
                {
                    binaryReader.Dispose();
                }
            }
        }

        public void GetData(byte[] buff)
        {
            var binaryReader = GetReader(out var shouldDispose);
            try
            {
                binaryReader.BaseStream.Position = offset;
                var read = binaryReader.Read(buff, 0, (int)size);
                if (read != size)
                {
                    throw new EndOfStreamException($"Unable to read {size} bytes from resource {path} at offset {offset}. Read {read} bytes.");
                }

                int sampleSize = Math.Min(buff.Length, 1024);
                int count01 = 0;
                for (int i = 0; i < sampleSize; i++)
                {
                    if (buff[i] == 0x01)
                        count01++;
                }

                if (sampleSize > 0 && count01 > sampleSize * 0.5)
                {
                    for (int i = 0; i < buff.Length; i++)
                    {
                        buff[i] ^= 0xFF;
                    }
                }
            }
            finally
            {
                if (shouldDispose)
                {
                    binaryReader.Dispose();
                }
            }
        }

        public void WriteData(string path)
        {
            var binaryReader = GetReader(out var shouldDispose);
            try
            {
                binaryReader.BaseStream.Position = offset;
                using (var writer = File.OpenWrite(path))
                {
                    binaryReader.BaseStream.CopyTo(writer, size);
                }
            }
            finally
            {
                if (shouldDispose)
                {
                    binaryReader.Dispose();
                }
            }
        }
    }
}
