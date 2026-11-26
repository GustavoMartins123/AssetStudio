using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;

namespace AssetStudio.Tests
{
    internal static class SyntheticSerializedFile
    {
        // Format 22 (Unity 2022.3) serialized file without TypeTrees holding TextAssets: m_Name and m_Script as aligned strings.
        public static byte[] BuildTextAssets((long PathId, string Name, string Script)[] assets, out (long Start, int Size)[] objects)
        {
            const int headerSize = 48;
            var blobs = assets.Select(a => EncodeTextAsset(a.Name, a.Script)).ToArray();
            objects = new (long, int)[blobs.Length];

            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write(new byte[headerSize]);
            bw.Write(Encoding.ASCII.GetBytes("2022.3.20f1\0"));
            bw.Write((int)BuildTarget.StandaloneWindows64);
            bw.Write(false); // no TypeTrees
            bw.Write(1); // type count
            bw.Write(49); // TextAsset
            bw.Write(false); // stripped
            bw.Write((short)-1); // script type index
            bw.Write(new byte[16]); // old type hash

            bw.Write(blobs.Length);
            long start = 0;
            for (int i = 0; i < blobs.Length; i++)
            {
                while (ms.Position % 4 != 0) bw.Write((byte)0);
                bw.Write(assets[i].PathId);
                bw.Write(start);
                bw.Write((uint)blobs[i].Length);
                bw.Write(0); // type index
                objects[i] = (start, blobs[i].Length);
                start += (blobs[i].Length + 7) & ~7;
            }

            bw.Write(0); // script types
            bw.Write(0); // externals
            bw.Write(0); // ref types
            bw.Write((byte)0); // user information
            uint metadataSize = (uint)(ms.Position - headerSize);
            while (ms.Position % 16 != 0) bw.Write((byte)0);
            long dataOffset = ms.Position;
            foreach (var blob in blobs)
            {
                bw.Write(blob);
                while ((ms.Position - dataOffset) % 8 != 0) bw.Write((byte)0);
            }

            bw.Flush();
            var bytes = ms.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 22);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), metadataSize);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), bytes.Length);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), dataOffset);
            return bytes;
        }

        public static string LocateRepositoryRoot()
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

        private static byte[] EncodeTextAsset(string name, string script)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            WriteAligned(bw, ms, Encoding.UTF8.GetBytes(name));
            WriteAligned(bw, ms, Encoding.UTF8.GetBytes(script));
            bw.Flush();
            return ms.ToArray();

            static void WriteAligned(BinaryWriter writer, MemoryStream stream, byte[] value)
            {
                writer.Write(value.Length);
                writer.Write(value);
                while (stream.Position % 4 != 0) writer.Write((byte)0);
            }
        }
    }
}
