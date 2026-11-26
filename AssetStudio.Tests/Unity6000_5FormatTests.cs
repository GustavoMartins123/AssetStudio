using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace AssetStudio.Tests
{
    // Unity 6000.5 writes SerializedFile format 23: every type carries the hash of its TypeTree and the TypeTree is a
    // self-describing 'tthm' block that a build can move into a separate TypeTree data archive.
    public class Unity6000_5FormatTests
    {
        private static readonly byte[] TypeTreeHash = Enumerable.Range(0xA0, 16).Select(i => (byte)i).ToArray();
        private const uint TthmMagic = 0x7474686D;

        [Fact]
        public void Format23_InlineTypeTreeBlock_ReadsHashNodesAndObject()
        {
            var sf = Open(BuildFormat23File(embedTypeTree: true, typeTreeBlock: BuildTextAssetTypeTreeBlock()));

            Assert.Equal(SerializedFileFormatVersion.ExtractableTypeTree, sf.header.m_Version);
            Assert.Equal("6000.5.10f1", sf.unityVersion);
            var type = Assert.Single(sf.m_Types);
            Assert.Equal(49, type.classID);
            Assert.Equal(TypeTreeHash, type.m_TypeTreeHash);
            Assert.NotNull(type.m_Type);
            Assert.Equal(new[] { "Base", "m_Name", "Array", "size", "data", "m_Script", "Array", "size", "data" }, type.m_Type!.m_Nodes.Select(n => n.m_Name));
            Assert.Equal("TextAsset", type.m_Type.m_Nodes[0].m_Type);
            Assert.Equal(0, sf.ExternalTypeTreeCount);
            AssertTextAssetReads(sf);
        }

        [Fact]
        public void Format23_WithoutTypeTrees_HasNoHashAndStillReadsObject()
        {
            var sf = Open(BuildFormat23File(embedTypeTree: false, typeTreeBlock: null));

            var type = Assert.Single(sf.m_Types);
            Assert.Null(type.m_Type);
            Assert.Null(type.m_TypeTreeHash);
            var info = Assert.Single(sf.m_Objects);
            var obj = (TextAsset)AssetsManager.CreateObjectFromReader(new ObjectReader(sf.reader, sf, info));
            Assert.Equal("hello", obj.m_Name);
            Assert.Equal("abc"u8.ToArray(), obj.m_Script);
        }

        [Fact]
        public void Format23_ExtractedTypeTree_IsResolvedFromTypeTreeArchiveByHash()
        {
            var sf = Open(BuildFormat23File(embedTypeTree: true, typeTreeBlock: Array.Empty<byte>()));
            var type = Assert.Single(sf.m_Types);
            Assert.Null(type.m_Type);
            Assert.Equal(TypeTreeHash, type.m_TypeTreeHash);
            Assert.Equal(1, sf.ExternalTypeTreeCount);

            var archiveBytes = BuildTypeTreeArchive(Convert.ToHexString(TypeTreeHash).ToLowerInvariant(), BuildTextAssetTypeTreeBlock());
            var bundle = new BundleFile(new FileReader("typetrees.bin", new MemoryStream(archiveBytes)));
            Assert.True(TypeTreeStore.IsTypeTreeArchive(bundle));
            var store = new TypeTreeStore();
            Assert.Equal(1, store.AddArchive(bundle, "typetrees.bin"));

            Assert.Equal(0, sf.ResolveExternalTypeTrees(store));
            Assert.NotNull(type.m_Type);
            Assert.Equal(9, type.m_Type!.m_Nodes.Count);
            AssertTextAssetReads(sf);
        }

        [Fact]
        public void Format23_ExtractedTypeTree_AssetsManagerResolvesArchiveLoadedInAnyOrder()
        {
            var dir = Path.Combine(Path.GetTempPath(), "as-tthm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var assetsPath = Path.Combine(dir, "extracted.assets");
                var archivePath = Path.Combine(dir, "typetrees.bin");
                File.WriteAllBytes(assetsPath, BuildFormat23File(embedTypeTree: true, typeTreeBlock: Array.Empty<byte>()));
                File.WriteAllBytes(archivePath, BuildTypeTreeArchive(Convert.ToHexString(TypeTreeHash), BuildTextAssetTypeTreeBlock()));

                foreach (var order in new[] { new[] { assetsPath, archivePath }, new[] { archivePath, assetsPath } })
                {
                    using var am = new AssetsManager();
                    am.LoadFiles(order);
                    var sf = Assert.Single(am.assetsFileList);
                    Assert.Equal(0, sf.ExternalTypeTreeCount);
                    Assert.NotNull(sf.m_Types[0].m_Type);
                    Assert.Equal(1, am.TypeTreeStore.Count);
                    var text = Assert.Single(sf.Objects.OfType<TextAsset>());
                    Assert.Equal("hello", text.m_Name);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void TypeTreeArchive_RejectsBundlesThatAreNotTypeTreeArchives()
        {
            var archiveBytes = BuildTypeTreeArchive("CAB-0123456789abcdef0123456789abcdef", BuildTextAssetTypeTreeBlock());
            var bundle = new BundleFile(new FileReader("normal.bundle", new MemoryStream(archiveBytes)));
            Assert.False(TypeTreeStore.IsTypeTreeArchive(bundle));

            var notTthm = BuildTypeTreeArchive(Convert.ToHexString(TypeTreeHash), new byte[64]);
            Assert.False(TypeTreeStore.IsTypeTreeArchive(new BundleFile(new FileReader("other.bundle", new MemoryStream(notTthm)))));
        }

        [Theory]
        [InlineData("magic", "expected 'tthm' header")]
        [InlineData("version", "Unsupported TypeTree data version 24")]
        [InlineData("size", "do not fill")]
        public void Format23_InvalidTypeTreeBlock_FailsWithClearMessage(string corruption, string expectedMessage)
        {
            var block = BuildTextAssetTypeTreeBlock();
            switch (corruption)
            {
                case "magic":
                    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), 0x12345678);
                    break;
                case "version":
                    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 24);
                    break;
                case "size":
                    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8)) + 1);
                    break;
            }
            var bytes = BuildFormat23File(embedTypeTree: true, typeTreeBlock: block);
            var ex = Assert.Throws<InvalidDataException>(() => Open(bytes));
            Assert.Contains(expectedMessage, ex.Message);
        }

        // Real files generated by the Unity 6000.5.10f1 / 6000.3.18f1 editors in batch mode (scratch/unity-fixtures, not in git).
        [SkippableTheory]
        [InlineData("6000.5/fixture/StandaloneWindows64/lz4/fixture", null)]
        [InlineData("6000.5/fixture/Android/lz4/fixture", null)]
        [InlineData("6000.5/fixture/StandaloneWindows64/raw-notypetree/fixture", null)]
        [InlineData("6000.5/fixture/StandaloneWindows64/extracted/fixture", "6000.5/fixture/StandaloneWindows64/extracted/typetrees.bin")]
        [InlineData("6000.5/StandaloneWindows64/sprites", null)]
        [InlineData("6000.5/Android/sprites", null)]
        [InlineData("6000.3/fixture/StandaloneWindows64/lz4/fixture", null)]
        public void RealUnityFixture_EveryObjectReadsWithinItsSizeAndTypeTreesMatchExactly_WhenPresent(string bundle, string? typeTreeArchive)
        {
            var samples = Path.Combine(LocateRepositoryRoot(), "scratch", "unity-fixtures", "samples");
            var bundlePath = Path.Combine(samples, bundle);
            Skip.IfNot(File.Exists(bundlePath), $"Unity sample not generated on this machine: {bundle} (scratch/unity-fixtures/samples).");

            using var am = new AssetsManager();
            am.LoadFiles(typeTreeArchive == null ? new[] { bundlePath } : new[] { bundlePath, Path.Combine(samples, typeTreeArchive) });
            var sf = Assert.Single(am.assetsFileList);
            Assert.Equal(0, sf.ExternalTypeTreeCount);
            Assert.NotEmpty(sf.Objects);

            var exactTypes = new HashSet<ClassIDType> { ClassIDType.Sprite, ClassIDType.SpriteAtlas, ClassIDType.AnimationClip, ClassIDType.AnimatorController, ClassIDType.Mesh, ClassIDType.Texture2D, ClassIDType.Texture2DArray, ClassIDType.AudioClip, ClassIDType.TextAsset, ClassIDType.Material };
            foreach (var info in sf.m_Objects)
            {
                var reader = new ObjectReader(sf.reader, sf, info);
                reader.Reset();
                AssetsManager.CreateObjectFromReader(reader);
                long consumed = reader.Position - info.byteStart;
                Assert.True(consumed <= info.byteSize, $"{reader.type} {info.m_PathID} read {consumed} of {info.byteSize} bytes");
                if (exactTypes.Contains(reader.type) && reader.type != ClassIDType.SpriteAtlas)
                {
                    Assert.True(consumed == info.byteSize, $"{reader.type} {info.m_PathID} read {consumed} of {info.byteSize} bytes");
                }

                if (info.serializedType?.m_Type != null)
                {
                    reader.Reset();
                    TypeTreeHelper.ReadType(info.serializedType.m_Type, reader);
                    Assert.Equal(info.byteSize, reader.Position - info.byteStart);
                }
            }
        }

        // Player builds carry no TypeTrees: MonoBehaviour fields come from the game assemblies, and so must the classes
        // stored through [SerializeReference] (the managed reference registry at the end of the object).
        [SkippableTheory]
        [InlineData("6000.5/player-mono/FixtureGame_Data")]
        [InlineData("6000.3/player-mono/FixtureGame_Data")]
        public void PlayerBuild_SerializeReferenceClassesAreReadFromGameAssemblies_WhenPresent(string dataDir)
        {
            var dir = Path.Combine(LocateRepositoryRoot(), "scratch", "unity-fixtures", "samples", dataDir);
            Skip.IfNot(Directory.Exists(dir), $"Unity player build not generated on this machine: {dataDir} (scratch/unity-fixtures/samples).");

            using var am = new AssetsManager();
            am.LoadFiles(Path.Combine(dir, "globalgamemanagers"), Path.Combine(dir, "globalgamemanagers.assets"), Path.Combine(dir, "level0"), Path.Combine(dir, "sharedassets0.assets"));
            var loader = new AssemblyLoader();
            loader.Load(Path.Combine(dir, "Managed"));

            var behaviours = am.assetsFileList.SelectMany(f => f.Objects).OfType<MonoBehaviour>()
                .Where(m => m.m_Script.TryGet(out var script) && script.m_ClassName == "FixtureBehaviour")
                .Select(m => (Behaviour: m, Tree: m.ConvertToTypeTree(loader)))
                .ToList();
            var (mb, tree) = Assert.Single(behaviours, b => (string?)b.Behaviour.ToType(b.Tree)?["text"] == "hello fixture");
            Assert.NotEmpty(tree.m_Nodes);

            var info = mb.assetsFile.ObjectInfoDic[mb.m_PathID];
            var reader = new ObjectReader(mb.assetsFile.reader, mb.assetsFile, info);
            var dict = TypeTreeHelper.ReadType(tree, reader);
            Assert.Equal(info.byteSize, reader.Position - info.byteStart);

            var refIds = ((List<object>)((System.Collections.Specialized.OrderedDictionary)dict["references"]!)["RefIds"]!)
                .Cast<System.Collections.Specialized.OrderedDictionary>()
                .Where(r => (string)((System.Collections.Specialized.OrderedDictionary)r["type"]!)["class"]! != string.Empty)
                .Select(r => (Class: (string)((System.Collections.Specialized.OrderedDictionary)r["type"]!)["class"]!, Data: (System.Collections.Specialized.OrderedDictionary)r["data"]!))
                .ToList();
            Assert.Contains(refIds, r => r.Class == "FixtureCircle" && (float)r.Data["radius"]! == 4.5f && (string)r.Data["label"]! == "main");
            Assert.Contains(refIds, r => r.Class == "FixtureCircle" && (float)r.Data["radius"]! == 1f && (string)r.Data["label"]! == "small");
            Assert.Contains(refIds, r => r.Class == "FixtureSquare" && (int)r.Data["side"]! == 3 && ((List<object>)r.Data["values"]!).Cast<int>().SequenceEqual(new[] { 1, 2, 3 }));
        }

        private static void AssertTextAssetReads(SerializedFile sf)
        {
            var info = Assert.Single(sf.m_Objects);
            var reader = new ObjectReader(sf.reader, sf, info);
            reader.Reset();
            var dict = TypeTreeHelper.ReadType(sf.m_Types[0].m_Type!, reader);
            Assert.Equal(info.byteSize, reader.Position - info.byteStart);
            Assert.Equal("hello", dict["m_Name"]);
            Assert.Equal("abc", dict["m_Script"]);
        }

        private static SerializedFile Open(byte[] bytes)
        {
            var fileReader = new FileReader("format23.assets", new MemoryStream(bytes));
            return new SerializedFile(fileReader, new AssetsManager());
        }

        // TextAsset: Base / m_Name (string) / m_Script (string), with 32-byte format 23 nodes and a local string buffer.
        private static byte[] BuildTextAssetTypeTreeBlock()
        {
            var strings = new List<string>();
            var offsets = new Dictionary<string, int>();
            int Offset(string s)
            {
                if (!offsets.TryGetValue(s, out var o))
                {
                    o = strings.Sum(x => x.Length + 1);
                    offsets[s] = o;
                    strings.Add(s);
                }
                return o;
            }
            var nodes = new (int Level, string Type, string Name, int Size, byte Flags, int Meta)[]
            {
                (0, "TextAsset", "Base", -1, 0, 0x8000),
                (1, "string", "m_Name", -1, 0, 0x8000),
                (2, "Array", "Array", -1, 1, 0x4000),
                (3, "int", "size", 4, 0, 0),
                (3, "char", "data", 1, 0, 0),
                (1, "string", "m_Script", -1, 0, 0x8000),
                (2, "Array", "Array", -1, 1, 0x4000),
                (3, "int", "size", 4, 0, 0),
                (3, "char", "data", 1, 0, 0),
            };
            using var nodeMs = new MemoryStream();
            using (var bw = new BinaryWriter(nodeMs, Encoding.UTF8, leaveOpen: true))
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    var n = nodes[i];
                    bw.Write((ushort)1);
                    bw.Write((byte)n.Level);
                    bw.Write(n.Flags);
                    bw.Write((uint)Offset(n.Type));
                    bw.Write((uint)Offset(n.Name));
                    bw.Write(n.Size);
                    bw.Write(i);
                    bw.Write(n.Meta);
                    bw.Write(0UL);
                }
            }
            var stringBuffer = Encoding.ASCII.GetBytes(string.Concat(strings.Select(s => s + "\0")));

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(TthmMagic);
                bw.Write(23u);
                bw.Write(nodes.Length);
                bw.Write(stringBuffer.Length);
                bw.Write(nodeMs.ToArray());
                bw.Write(stringBuffer);
            }
            return ms.ToArray();
        }

        // typeTreeBlock: null = file without TypeTrees, empty = TypeTree extracted (only the hash stays), else inline block.
        private static byte[] BuildFormat23File(bool embedTypeTree, byte[]? typeTreeBlock)
        {
            const int headerSize = 48;
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(new byte[headerSize]);
            bw.Write(Encoding.ASCII.GetBytes("6000.5.10f1\0"));
            bw.Write((int)BuildTarget.StandaloneWindows64);
            bw.Write(embedTypeTree);
            bw.Write(1); // type count
            bw.Write(49); // TextAsset
            bw.Write(false); // stripped
            bw.Write((short)-1); // script type index
            bw.Write(new byte[16]); // old type hash
            if (embedTypeTree)
            {
                bw.Write(TypeTreeHash);
                bw.Write((uint)typeTreeBlock!.Length);
                bw.Write(typeTreeBlock);
                bw.Write(0); // type dependencies
            }

            byte[] objectData;
            using (var objMs = new MemoryStream())
            using (var objBw = new BinaryWriter(objMs))
            {
                objBw.Write(5);
                objBw.Write("hello"u8.ToArray());
                objBw.Write(new byte[3]);
                objBw.Write(3);
                objBw.Write("abc"u8.ToArray());
                objBw.Write((byte)0);
                objBw.Flush();
                objectData = objMs.ToArray();
            }

            bw.Write(1); // object count
            while (ms.Position % 4 != 0) bw.Write((byte)0);
            bw.Write(1L); // path id
            bw.Write(0L); // byte start (relative to data offset)
            bw.Write((uint)objectData.Length);
            bw.Write(0); // type index
            bw.Write(0); // script types
            bw.Write(0); // externals
            bw.Write(0); // ref types
            bw.Write((byte)0); // user information
            uint metadataSize = (uint)(ms.Position - headerSize);
            while (ms.Position % 16 != 0) bw.Write((byte)0);
            long dataOffset = ms.Position;
            bw.Write(objectData);
            bw.Flush();

            var bytes = ms.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 23);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), metadataSize);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), bytes.Length);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), dataOffset);
            return bytes;
        }

        // Uncompressed UnityFS archive with a single entry, the shape ContentBuildInterface.CombineExtractedTypeTreeDataFiles writes.
        private static byte[] BuildTypeTreeArchive(string entryName, byte[] entry)
        {
            using var info = new MemoryStream();
            info.Write(new byte[16]);
            WriteBE(info, 1);
            WriteBE(info, entry.Length);
            WriteBE(info, entry.Length);
            info.Write(new byte[2]); // no compression
            WriteBE(info, 1);
            WriteBE64(info, 0);
            WriteBE64(info, entry.Length);
            WriteBE(info, 0); // flags: not a serialized file
            info.Write(Encoding.UTF8.GetBytes(entryName + "\0"));
            var blocksInfo = info.ToArray();

            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
            WriteBE(ms, 6);
            ms.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
            ms.Write(Encoding.ASCII.GetBytes("6000.5.10f1\0"));
            long sizePos = ms.Position;
            WriteBE64(ms, 0);
            WriteBE(ms, blocksInfo.Length);
            WriteBE(ms, blocksInfo.Length);
            WriteBE(ms, (int)ArchiveFlags.BlocksAndDirectoryInfoCombined);
            ms.Write(blocksInfo);
            ms.Write(entry);
            var result = ms.ToArray();
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan((int)sizePos), result.Length);
            return result;

            static void WriteBE(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
            static void WriteBE64(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }
        }

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AssetStudio.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
