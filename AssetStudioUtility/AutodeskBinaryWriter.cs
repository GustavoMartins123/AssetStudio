using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UkooLabs.FbxSharpie;
using UkooLabs.FbxSharpie.Tokens;
using UkooLabs.FbxSharpie.Tokens.Value;
using UkooLabs.FbxSharpie.Tokens.ValueArray;

namespace AssetStudio
{
    public static class AutodeskBinaryWriter
    {
        private static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\u001a\0");
        private static readonly byte[] FootId = new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
        private static readonly byte[] ExtMagic = new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };
        private static readonly byte[] NullSentinel = new byte[13];

        public static void Write(FbxDocument document, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs, Encoding.UTF8);

            bw.Write(HeadMagic);
            bw.Write((int)7400);

            foreach (var node in document.Nodes)
            {
                if (node != null)
                {
                    WriteNode(bw, node);
                }
            }

            // Top-level null sentinel
            bw.Write(NullSentinel);

            // Autodesk standard footer
            bw.Write(FootId);
            bw.Write(new byte[4]); // 4 zero bytes

            long pos = fs.Position;
            int pad = (int)(((pos + 15) & ~15) - pos);
            if (pad == 0) pad = 16;
            bw.Write(new byte[pad]); // padding ensuring version is at 16-byte boundary

            bw.Write((int)7400);
            bw.Write(new byte[120]);
            bw.Write(ExtMagic);
        }

        private static void WriteNode(BinaryWriter bw, FbxNode node)
        {
            long startPos = bw.BaseStream.Position;

            // Placeholders: EndOffset (4 bytes), PropCount (4 bytes), PropLength (4 bytes)
            bw.Write((uint)0);
            bw.Write((uint)node.Properties.Length);
            bw.Write((uint)0);

            var nameBytes = Encoding.UTF8.GetBytes(node.Identifier?.Value ?? "");
            bw.Write((byte)nameBytes.Length);
            bw.Write(nameBytes);

            long propStart = bw.BaseStream.Position;
            foreach (var prop in node.Properties)
            {
                WriteProperty(bw, prop);
            }
            long propEnd = bw.BaseStream.Position;
            uint propLen = (uint)(propEnd - propStart);

            bool alwaysSentinel = node.Identifier?.Value == "AnimationStack" || node.Identifier?.Value == "AnimationLayer";
            if (node.Nodes.Length > 0 || alwaysSentinel)
            {
                foreach (var child in node.Nodes)
                {
                    if (child != null)
                    {
                        WriteNode(bw, child);
                    }
                }
                bw.Write(NullSentinel);
            }

            long endOffset = bw.BaseStream.Position;

            // Backpatch
            bw.BaseStream.Position = startPos;
            bw.Write((uint)endOffset);
            bw.BaseStream.Position = startPos + 8;
            bw.Write(propLen);

            bw.BaseStream.Position = endOffset;
        }

        private static void WriteProperty(BinaryWriter bw, Token token)
        {
            switch (token)
            {
                case BooleanToken bt:
                    bw.Write((byte)'C');
                    bw.Write(bt.Value ? (byte)1 : (byte)0);
                    break;
                case ShortToken st:
                    bw.Write((byte)'Y');
                    bw.Write(st.Value);
                    break;
                case IntegerToken it:
                    bw.Write((byte)'I');
                    bw.Write(it.Value);
                    break;
                case LongToken lt:
                    bw.Write((byte)'L');
                    bw.Write(lt.Value);
                    break;
                case FloatToken ft:
                    bw.Write((byte)'F');
                    bw.Write(ft.Value);
                    break;
                case DoubleToken dt:
                    bw.Write((byte)'D');
                    bw.Write(dt.Value);
                    break;
                case StringToken str:
                    var strBytes = Encoding.UTF8.GetBytes(str.Value ?? "");
                    bw.Write((byte)'S');
                    bw.Write(strBytes.Length);
                    bw.Write(strBytes);
                    break;
                case ByteArrayToken bat:
                    bw.Write((byte)'R');
                    bw.Write(bat.Values.Length);
                    bw.Write(bat.Values);
                    break;
                case DoubleArrayToken dat:
                    WriteArray(bw, 'd', 8, dat.Values.Length, w => { foreach (var v in dat.Values) w.Write(v); });
                    break;
                case FloatArrayToken fat:
                    WriteArray(bw, 'f', 4, fat.Values.Length, w => { foreach (var v in fat.Values) w.Write(v); });
                    break;
                case IntegerArrayToken iat:
                    WriteArray(bw, 'i', 4, iat.Values.Length, w => { foreach (var v in iat.Values) w.Write(v); });
                    break;
                case LongArrayToken lat:
                    WriteArray(bw, 'l', 8, lat.Values.Length, w => { foreach (var v in lat.Values) w.Write(v); });
                    break;
                case BooleanArrayToken blat:
                    WriteArray(bw, 'b', 1, blat.Values.Length, w => { foreach (var v in blat.Values) w.Write(v ? (byte)1 : (byte)0); });
                    break;
                default:
                    throw new NotSupportedException($"Unsupported token type: {token?.GetType().Name}");
            }
        }

        private static void WriteArray(BinaryWriter bw, char typeChar, int elemSize, int length, Action<BinaryWriter> itemWriter)
        {
            bw.Write((byte)typeChar);
            bw.Write(length);

            int uncompressedSize = length * elemSize;
            if (uncompressedSize < 1024)
            {
                bw.Write(0); // encoding = 0 (uncompressed)
                bw.Write(uncompressedSize);
                itemWriter(bw);
            }
            else
            {
                using var rawMs = new MemoryStream(uncompressedSize);
                using (var rawBw = new BinaryWriter(rawMs))
                {
                    itemWriter(rawBw);
                }
                var rawBytes = rawMs.ToArray();
                uint checksum = CalcAdler32(rawBytes);

                using var compMs = new MemoryStream();
                // Standard RFC 1950 zlib header: CMF = 0x78 (32K window, deflate), FLG = 0x01 (fastest)
                compMs.WriteByte(0x78);
                compMs.WriteByte(0x01);

                using (var deflate = new DeflateStream(compMs, CompressionLevel.Fastest, leaveOpen: true))
                {
                    deflate.Write(rawBytes, 0, rawBytes.Length);
                }

                // Adler-32 checksum (Big Endian)
                compMs.WriteByte((byte)((checksum >> 24) & 0xFF));
                compMs.WriteByte((byte)((checksum >> 16) & 0xFF));
                compMs.WriteByte((byte)((checksum >> 8) & 0xFF));
                compMs.WriteByte((byte)(checksum & 0xFF));

                var compBytes = compMs.ToArray();
                bw.Write(1); // encoding = 1 (compressed)
                bw.Write(compBytes.Length);
                bw.Write(compBytes);
            }
        }

        private static uint CalcAdler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
