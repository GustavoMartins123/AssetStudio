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
            this.path = path;
            this.assetsFile = assetsFile;
            this.offset = offset;
            this.size = size;
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

                var findFiles = Directory.GetFiles(root, resourceFileName, SearchOption.AllDirectories);
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
