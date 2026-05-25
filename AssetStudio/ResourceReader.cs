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
            this.reader = reader;
            this.offset = offset;
            this.size = size;
        }

        private BinaryReader GetReader()
        {
            if (needSearch)
            {
                var resourceFileName = Path.GetFileName(path);
                var normalizedPath = NormalizePath(path);
                if (assetsFile.assetsManager.resourceFileReaders.TryGetValue(normalizedPath, out reader) && CanReadRange(reader))
                {
                    needSearch = false;
                    return reader;
                }
                if (!HasDirectory(path)
                    && assetsFile.assetsManager.resourceFileReaders.TryGetValue(resourceFileName, out reader)
                    && CanReadRange(reader))
                {
                    needSearch = false;
                    return reader;
                }

                var resourceFilePath = ResolveResourceFilePath(resourceFileName, normalizedPath);
                if (resourceFilePath != null)
                {
                    var resolvedKey = NormalizePath(Path.GetFullPath(resourceFilePath));
                    needSearch = false;
                    reader = new BinaryReader(File.OpenRead(resourceFilePath));
                    assetsFile.assetsManager.resourceFileReaders[normalizedPath] = reader;
                    assetsFile.assetsManager.resourceFileReaders[resolvedKey] = reader;
                    if (!HasDirectory(path) && !assetsFile.assetsManager.resourceFileReaders.ContainsKey(resourceFileName))
                    {
                        assetsFile.assetsManager.resourceFileReaders[resourceFileName] = reader;
                    }
                    return reader;
                }
                throw new FileNotFoundException($"Can't find the resource file {path}");
            }
            else
            {
                return reader;
            }
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
            return offset >= 0 && size >= 0 && binaryReader.BaseStream.Length >= offset + size;
        }

        public byte[] GetData()
        {
            var binaryReader = GetReader();
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

        public void GetData(byte[] buff)
        {
            var binaryReader = GetReader();
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

        public void WriteData(string path)
        {
            var binaryReader = GetReader();
            binaryReader.BaseStream.Position = offset;
            using (var writer = File.OpenWrite(path))
            {
                binaryReader.BaseStream.CopyTo(writer, size);
            }
        }
    }
}
