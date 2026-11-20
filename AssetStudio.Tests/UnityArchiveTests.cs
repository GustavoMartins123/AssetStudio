using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Xunit;

namespace AssetStudio.Tests
{
    public class UnityArchiveTests
    {
        [Fact]
        public void FileReader_DetectsUnityArchiveSignatureAsBundleFile()
        {
            var archiveBytes = BuildUnityArchiveHeader(version: 6u, unityVersion: "5.x.x", unityRevision: "5.3.0f1");
            using var ms = new MemoryStream(archiveBytes);
            using var reader = new FileReader("legacy.unity3d", ms);

            Assert.Equal(FileType.BundleFile, reader.FileType);
        }

        [Fact]
        public void BundleFile_UnityArchiveSignature_ThrowsNotSupportedException()
        {
            var archiveBytes = BuildUnityArchiveHeader(version: 6u, unityVersion: "5.x.x", unityRevision: "5.3.0f1");
            using var ms = new MemoryStream(archiveBytes);
            using var reader = new FileReader("legacy.unity3d", ms);

            var exception = Assert.Throws<NotSupportedException>(() => new BundleFile(reader));
            Assert.Contains("UnityArchive", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BundleFile_UnknownSignature_ThrowsNotSupportedException()
        {
            var unknownBytes = BuildHeader("UnityUnknown", version: 1u, unityVersion: "5.x.x", unityRevision: "5.3.0f1");
            using var ms = new MemoryStream(unknownBytes);
            using var reader = new FileReader("unknown.bundle", ms);

            var exception = Assert.Throws<NotSupportedException>(() => new BundleFile(reader));
            Assert.Contains("UnityUnknown", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AssetsManager_LoadFiles_WithUnityArchiveFile_HandlesCleanlyWithoutNullReferenceException()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AssetStudio_UnityArchiveTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "test_archive.bundle");
            try
            {
                File.WriteAllBytes(archivePath, BuildUnityArchiveHeader(version: 6u, unityVersion: "5.x.x", unityRevision: "5.3.0f1"));

                using var manager = new AssetsManager();
                var exception = Record.Exception(() => manager.LoadFiles(archivePath));

                Assert.Null(exception);
                Assert.Empty(manager.assetsFileList);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                    // Ignore temp directory cleanup errors
                }
            }
        }

        private static byte[] BuildUnityArchiveHeader(uint version, string unityVersion, string unityRevision)
        {
            return BuildHeader("UnityArchive", version, unityVersion, unityRevision);
        }

        private static byte[] BuildHeader(string signature, uint version, string unityVersion, string unityRevision)
        {
            using var ms = new MemoryStream();
            WriteNullTerminatedUtf8(ms, signature);
            Span<byte> versionBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(versionBytes, version);
            ms.Write(versionBytes);
            WriteNullTerminatedUtf8(ms, unityVersion);
            WriteNullTerminatedUtf8(ms, unityRevision);
            ms.Write(new byte[32]);
            return ms.ToArray();
        }

        private static void WriteNullTerminatedUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }
    }
}
