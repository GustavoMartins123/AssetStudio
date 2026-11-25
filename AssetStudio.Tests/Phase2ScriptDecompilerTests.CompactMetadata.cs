using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio.Avalonia.Services.Scripting;
using Xunit;

namespace AssetStudio.Tests
{
    public partial class Phase2ScriptDecompilerTests
    {
        [Theory]
        [InlineData(39, 2)]
        [InlineData(39, 4)]
        [InlineData(107, 2)]
        [InlineData(107, 4)]
        public void Il2CppMetadataReader_ReadsUnity6CompactMetadata(int version, int typeIndexWidth)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var classicPath = Path.Combine(tempRoot, "classic.dat");
                WriteSyntheticIl2CppMetadata(classicPath, version: 31);
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, RewriteAsCompactMetadata(File.ReadAllBytes(classicPath), version, typeIndexWidth));

                var reader = new Il2CppMetadataReader();
                var inspection = reader.InspectMetadata(metadataPath);
                Assert.True(inspection.Success, inspection.DiagnosticMessage);
                Assert.Equal(version, inspection.MetadataVersion);
                Assert.Equal(2, inspection.ImageCount);
                Assert.Equal(9, inspection.TypeCount);

                var classResult = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "InventoryBag`1"
                    });

                Assert.NotNull(classResult);
                Assert.True(classResult!.Success);
                Assert.Equal($"IL2CPP (metadata v{version})", classResult.BackendBadge);
                Assert.Equal("Assembly-CSharp.dll", classResult.AssemblyName);
                Assert.Contains($"// Source: IL2CPP Metadata v{version}", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public class InventoryBag<TItem> : BaseController", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Capacity;", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public int Count { get; set; }", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public virtual void AddItem(int slotIndex, string itemKey);", classResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public static int ResolveTag<TTag>(string tagName);", classResult.SourceCode, StringComparison.Ordinal);

                var enumResult = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Assembly-CSharp.dll",
                        Namespace = "Demo.Gameplay",
                        ClassName = "ActorState"
                    });

                Assert.NotNull(enumResult);
                Assert.Contains("public enum ActorState", enumResult!.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Idle = 0", enumResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Moving = 1", enumResult.SourceCode, StringComparison.Ordinal);
                Assert.Contains("Attacking = 250", enumResult.SourceCode, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Theory]
        [InlineData(39)]
        [InlineData(107)]
        public void Il2CppBinaryAndMetadata_Unity6CompactMetadataWithPe64Binary_ResolvesTypesOffsetsAttributesAndRvas(int version)
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                var binaryPath = Path.Combine(tempRoot, "GameAssembly.dll");
                WriteRichSyntheticIl2CppFixture(metadataPath, binaryPath, SyntheticBinaryKind.Pe64V31);
                File.WriteAllBytes(metadataPath, RewriteAsCompactMetadata(File.ReadAllBytes(metadataPath), version, typeIndexWidth: 1));

                var reader = new Il2CppMetadataReader();
                var result = reader.TryExtractType(
                    metadataPath,
                    new ScriptLookupRequest
                    {
                        AssemblyName = "Gameplay.Framework.dll",
                        ClassName = "RigScaleControl`1",
                        GameAssemblyPath = binaryPath
                    });

                Assert.NotNull(result);
                Assert.True(result!.Success);
                Assert.Equal($"IL2CPP (metadata v{version} + binary)", result.BackendBadge);
                Assert.DoesNotContain("/* field */", result.SourceCode, StringComparison.Ordinal);
                Assert.DoesNotContain("/* param */", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private const int MAX_BONES = 300; // Token: 0x04000001", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("[SerializeField]", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public List<string> m_boneHideList; // Offset: 0x20 | Token: 0x04000002", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private Dictionary<string, List<int>> m_nestedMap; // Offset: 0x40 | Token: 0x04000007", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("private TState m_state; // Offset: 0x58 | Token: 0x0400000A", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("public bool TryLookup<TResult>(string key, out int value, ref float scale, in string tag, TResult fallback);", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// RVA: 0x00001060 | Offset: 0x00000460 | VA: 0x180001060", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains("// Aliases (2): ECLJAGIGDDM, KGPKONNLLPK", result.SourceCode, StringComparison.Ordinal);
                Assert.Contains(result.MethodSymbols, m => m.MethodName == "set_BoneNameList" && m.Address == 0x1060);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void Il2CppMetadataReader_RejectsCompactMetadataWhoseRowsDoNotMatchUnityLayouts()
        {
            var tempRoot = CreateTempDirectory();
            try
            {
                var classicPath = Path.Combine(tempRoot, "classic.dat");
                WriteSyntheticIl2CppMetadata(classicPath, version: 31);
                var compact = RewriteAsCompactMetadata(File.ReadAllBytes(classicPath), 39, typeIndexWidth: 2);

                const int methodsSection = 5;
                var countOffset = 8 + methodsSection * 12 + 8;
                BinaryPrimitives.WriteInt32LittleEndian(
                    compact.AsSpan(countOffset, 4),
                    BinaryPrimitives.ReadInt32LittleEndian(compact.AsSpan(countOffset, 4)) + 1);

                var metadataPath = Path.Combine(tempRoot, "global-metadata.dat");
                File.WriteAllBytes(metadataPath, compact);

                var outcome = new Il2CppMetadataReader().InspectMetadata(metadataPath);
                Assert.False(outcome.Success);
                Assert.True(outcome.IsObfuscatedOrUnsupported);
                Assert.Contains("v39", outcome.DiagnosticMessage, StringComparison.Ordinal);
                Assert.Contains("'Methods'", outcome.DiagnosticMessage, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        /// <summary>
        /// Writes a classic v31 synthetic file in the compact layout Unity 6000.3 (v39) and 6000.5 (v107) emit,
        /// following libil2cpp's MetadataDeserialization.cpp: (offset, size, count) header triples and index
        /// columns sized 1/2/4 bytes from table counts, with all-ones meaning -1.
        /// </summary>
        private static byte[] RewriteAsCompactMetadata(byte[] classic, int version, int typeIndexWidth)
        {
            const int sectionCount = 31;
            var isV107 = version == 107;
            var tables = new byte[sectionCount][];
            for (var i = 0; i < sectionCount; i++)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(classic.AsSpan(8 + i * 8, 4));
                var size = BinaryPrimitives.ReadInt32LittleEndian(classic.AsSpan(12 + i * 8, 4));
                tables[i] = classic.AsSpan(offset, size).ToArray();
            }

            static int WidthFor(int count) => count <= byte.MaxValue ? 1 : count <= ushort.MaxValue ? 2 : 4;
            int Rows(int section, int classicRowSize) => tables[section].Length / classicRowSize;
            int PerTable(int count) => isV107 ? WidthFor(count) : 4;

            var t = typeIndexWidth;
            var td = WidthFor(Rows(19, 88));
            var gc = WidthFor(Rows(14, 16));
            var p = WidthFor(Rows(10, 12));
            var gp = PerTable(Rows(12, 16));
            var itf = PerTable(Rows(18, 8));
            var e = PerTable(Rows(3, 24));
            var pr = PerTable(Rows(4, 20));
            var n = PerTable(Rows(15, 4));
            var f = PerTable(Rows(11, 12));
            var dvd = PerTable(tables[8].Length);
            var m = PerTable(Rows(5, 36));

            var layouts = new Dictionary<int, (int Src, int Dst)[]>
            {
                [3] = new[] { (4, 4), (4, t), (4, m), (4, m), (4, m), (4, 4) },
                [4] = new[] { (4, 4), (4, m), (4, m), (4, 4), (4, 4) },
                [5] = new[] { (4, 4), (4, td), (4, t), (4, 4), (4, p), (4, gc), (4, 4), (2, 2), (2, 2), (2, 2), (2, 2) },
                [6] = new[] { (4, p), (4, t), (4, dvd) },
                [7] = new[] { (4, f), (4, t), (4, dvd) },
                [9] = new[] { (4, f), (4, t), (4, 4) },
                [10] = new[] { (4, 4), (4, 4), (4, t) },
                [11] = new[] { (4, 4), (4, t), (4, 4) },
                [12] = new[] { (4, gc), (4, 4), (2, 2), (2, 2), (2, 2), (2, 2) },
                [13] = new[] { (4, t) },
                [14] = isV107 ? new[] { (4, 4), (4, 2), (4, 1), (4, gp) } : new[] { (4, 4), (4, 4), (4, 4), (4, 4) },
                [15] = new[] { (4, 4) },
                [16] = new[] { (4, t) },
                [17] = new[] { (4, 4) },
                [18] = new[] { (4, t), (4, 4) },
                [19] = new[]
                {
                    (4, 4), (4, 4), (4, t), (4, t), (4, t), (4, 0), (4, gc), (4, 4),
                    (4, f), (4, m), (4, e), (4, pr), (4, n), (4, itf), (4, 4), (4, itf),
                    (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (2, 2), (4, 4), (4, 4)
                },
                [20] = new[] { (4, 4), (4, 4), (4, td), (4, 4), (4, td), (4, 4), (4, m), (4, 4), (4, 4), (4, 4) },
                [22] = new[] { (4, t), (4, f) },
                [25] = new[] { (4, 4), (4, 4) }
            };

            var sections = new List<(byte[] Data, int Count)>(sectionCount + 1);
            for (var i = 0; i < sectionCount; i++)
            {
                if (layouts.TryGetValue(i, out var layout))
                {
                    var classicRowSize = layout.Sum(static c => c.Src);
                    var rows = tables[i].Length / classicRowSize;
                    sections.Add((NarrowRows(tables[i], rows, layout), rows));
                }
                else
                {
                    sections.Add((tables[i], tables[i].Length));
                }
            }

            if (isV107)
            {
                EncodeAttributeConstructors(sections[24].Data, tables[25]);
                sections.Insert(20, (Array.Empty<byte>(), 0));
            }

            var headerSize = 8 + sections.Count * 12;
            var output = new byte[headerSize + sections.Sum(static s => s.Data.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), Il2CppMetadataReader.MetadataSanity);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4, 4), version);
            var cursor = headerSize;
            for (var i = 0; i < sections.Count; i++)
            {
                var (data, count) = sections[i];
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8 + i * 12, 4), cursor);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12 + i * 12, 4), data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16 + i * 12, 4), count);
                data.CopyTo(output.AsSpan(cursor));
                cursor += data.Length;
            }

            return output;
        }

        private static byte[] NarrowRows(byte[] classicRows, int rows, (int Src, int Dst)[] layout)
        {
            var srcRowSize = layout.Sum(static c => c.Src);
            var dstRowSize = layout.Sum(static c => c.Dst);
            var output = new byte[rows * dstRowSize];
            for (var r = 0; r < rows; r++)
            {
                var s = r * srcRowSize;
                var d = r * dstRowSize;
                foreach (var (src, dst) in layout)
                {
                    if (src == dst)
                    {
                        classicRows.AsSpan(s, src).CopyTo(output.AsSpan(d, dst));
                    }
                    else if (dst > 0)
                    {
                        var value = BinaryPrimitives.ReadInt32LittleEndian(classicRows.AsSpan(s, 4));
                        var allOnes = dst == 1 ? 0xFFu : dst == 2 ? 0xFFFFu : uint.MaxValue;
                        Assert.True(value == -1 || (value >= 0 && (uint)value < allOnes), $"value {value} does not fit a {dst}-byte index");
                        var encoded = value == -1 ? allOnes : (uint)value;
                        for (var b = 0; b < dst; b++)
                        {
                            output[d + b] = (byte)(encoded >> (8 * b));
                        }
                    }

                    s += src;
                    d += dst;
                }
            }

            return output;
        }

        private static void EncodeAttributeConstructors(byte[] attributeData, byte[] ranges)
        {
            const uint methodDefinitionUsage = 3;
            for (var i = 0; i + 8 <= ranges.Length; i += 8)
            {
                var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(ranges.AsSpan(i + 4, 4));
                if (start >= attributeData.Length)
                {
                    continue;
                }

                var count = attributeData[start];
                Assert.True(count < 0x80);
                for (var k = 0; k < count; k++)
                {
                    var slot = attributeData.AsSpan(start + 1 + k * 4, 4);
                    var methodIndex = BinaryPrimitives.ReadUInt32LittleEndian(slot);
                    BinaryPrimitives.WriteUInt32LittleEndian(slot, (methodDefinitionUsage << 29) | (methodIndex << 1));
                }
            }
        }
    }
}
