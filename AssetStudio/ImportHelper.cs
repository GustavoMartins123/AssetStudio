using Org.Brotli.Dec;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AssetStudio
{
    public static class ImportHelper
    {
        public static void MergeSplitAssets(string path, bool allDirectories = false)
        {
            var splitFiles = Directory.GetFiles(path, "*.split0", allDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            foreach (var splitFile in splitFiles)
            {
                var destFile = Path.GetFileNameWithoutExtension(splitFile);
                var destPath = Path.GetDirectoryName(splitFile);
                var destFull = Path.Combine(destPath, destFile);
                if (!File.Exists(destFull))
                {
                    var splitParts = Directory.GetFiles(destPath, destFile + ".split*");
                    using (var destStream = File.Create(destFull))
                    {
                        for (int i = 0; i < splitParts.Length; i++)
                        {
                            var splitPart = destFull + ".split" + i;
                            using (var sourceStream = File.OpenRead(splitPart))
                            {
                                sourceStream.CopyTo(destStream);
                            }
                        }
                    }
                }
            }
        }

        public static string[] ProcessingSplitFiles(IEnumerable<string> selectFile)
        {
            var splitFiles = new List<string>();
            var selectFilesList = new List<string>();
            foreach (var x in selectFile)
            {
                if (x.Contains(".split"))
                {
                    splitFiles.Add(Path.Combine(Path.GetDirectoryName(x), Path.GetFileNameWithoutExtension(x)));
                }
                else
                {
                    selectFilesList.Add(x);
                }
            }

            var distinctSplitFiles = splitFiles.Distinct();
            foreach (var file in distinctSplitFiles)
            {
                if (File.Exists(file))
                {
                    selectFilesList.Add(file);
                }
            }
            return selectFilesList.Distinct().ToArray();
        }

        public static FileReader DecompressGZip(FileReader reader)
        {
            using (reader)
            {
                var stream = new MemoryStream((int)(reader.BaseStream.Length * 2));
                using (var gs = new GZipStream(reader.BaseStream, CompressionMode.Decompress))
                {
                    gs.CopyTo(stream);
                }
                stream.Position = 0;
                return new FileReader(reader.FullPath, stream);
            }
        }

        public static FileReader DecompressBrotli(FileReader reader)
        {
            using (reader)
            {
                var stream = new MemoryStream((int)(reader.BaseStream.Length * 2));
                using (var brotliStream = new BrotliInputStream(reader.BaseStream))
                {
                    brotliStream.CopyTo(stream);
                }
                stream.Position = 0;
                return new FileReader(reader.FullPath, stream);
            }
        }
    }
}
