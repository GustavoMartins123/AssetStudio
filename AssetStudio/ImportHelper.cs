using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AssetStudio
{
    public static class ImportHelper
    {
        public static long MaxDecompressedSize { get; set; } = 4L * 1024 * 1024 * 1024; // 4 GiB safety cap

        public static void MergeSplitAssets(string path, bool allDirectories = false)
        {
            var splitFiles = GetFilesSafe(path, "*.split0", allDirectories);
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
                using (var gs = new GZipStream(reader.BaseStream, CompressionMode.Decompress))
                {
                    var stream = CopyDecompressedWithLimits(gs, reader.FullPath, "gzip", reader.BaseStream.Length);
                    return new FileReader(reader.FullPath, stream);
                }
            }
        }

        public static FileReader DecompressBrotli(FileReader reader)
        {
            using (reader)
            {
                using (var brotliStream = new BrotliStream(reader.BaseStream, CompressionMode.Decompress))
                {
                    var stream = CopyDecompressedWithLimits(brotliStream, reader.FullPath, "brotli", reader.BaseStream.Length);
                    return new FileReader(reader.FullPath, stream);
                }
            }
        }

        internal static Stream CopyDecompressedWithLimits(Stream source, string sourcePath, string kind, long compressedHintBytes)
        {
            var initialCapacity = (int)Math.Min(Math.Max(compressedHintBytes, 4096L), 16L * 1024 * 1024);
            Stream output = new MemoryStream(initialCapacity);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalWritten = 0;
                int bytesRead;
                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalWritten += bytesRead;
                    if (totalWritten > MaxDecompressedSize)
                    {
                        throw new InvalidDataException($"Decompressed {kind} stream '{Path.GetFileName(sourcePath)}' exceeds safety limit of {MaxDecompressedSize} bytes.");
                    }

                    if (output is MemoryStream && BundleFile.ShouldUseTemporaryStream(totalWritten))
                    {
                        var tempStream = BundleFile.CreateTemporaryStream(sourcePath, kind);
                        try
                        {
                            output.Position = 0;
                            output.CopyTo(tempStream);
                        }
                        catch
                        {
                            tempStream.Dispose();
                            throw;
                        }
                        output.Dispose();
                        output = tempStream;
                    }

                    output.Write(buffer, 0, bytesRead);
                }

                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static string[] GetFilesSafe(string path, string searchPattern, bool recursive)
        {
            var files = new List<string>();
            GetFilesSafeInternal(path, searchPattern, recursive, files);
            return files.ToArray();
        }

        private static void GetFilesSafeInternal(string path, string searchPattern, bool recursive, List<string> result)
        {
            try
            {
                var files = Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
                result.AddRange(files);
            }
            catch
            {
                // ignore
            }

            if (recursive)
            {
                try
                {
                    var subDirs = Directory.GetDirectories(path);
                    foreach (var dir in subDirs)
                    {
                        GetFilesSafeInternal(dir, searchPattern, true, result);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
