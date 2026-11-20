using System;
using System.Buffers;
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
        public const int DefaultWriteBufferSize = 256 * 1024; // 256 KiB

        private static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\u001a\0");
        private static readonly byte[] FootId = new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
        private static readonly byte[] ExtMagic = new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };
        private static readonly byte[] NullSentinel = new byte[13];
        private static readonly byte[] FooterZeros120 = new byte[120];

        private sealed class ArrayScratchBuffers : IDisposable
        {
            public readonly MemoryStream RawStream = new MemoryStream(4096);
            public readonly BinaryWriter RawWriter;
            public readonly MemoryStream CompressedStream = new MemoryStream(4096);

            public ArrayScratchBuffers()
            {
                RawWriter = new BinaryWriter(RawStream, Encoding.UTF8, leaveOpen: true);
            }

            public void Dispose()
            {
                RawWriter.Dispose();
                RawStream.Dispose();
                CompressedStream.Dispose();
            }
        }

        public static void Write(FbxDocument document, string path)
        {
            ArgumentNullException.ThrowIfNull(document);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                DefaultWriteBufferSize,
                FileOptions.SequentialScan);
            Write(document, fs);
        }

        /// <summary>
        /// Writes an <see cref="FbxDocument"/> in FBX 7400 binary format using an in-memory seek buffer
        /// so per-node header backpatching (<c>EndOffset</c> and <c>PropLength</c>) completes entirely in RAM
        /// with zero intermediate OS seek/flush syscalls on <paramref name="outputStream"/>.
        /// </summary>
        public static void Write(FbxDocument document, Stream outputStream)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(outputStream);

            using var bufferStream = new MemoryStream(DefaultWriteBufferSize);
            WriteDirectToSeekableStream(document, bufferStream);

            if (bufferStream.TryGetBuffer(out var segment) && segment.Array != null)
            {
                outputStream.Write(segment.Array, segment.Offset, segment.Count);
            }
            else
            {
                bufferStream.Position = 0;
                bufferStream.CopyTo(outputStream, DefaultWriteBufferSize);
            }
        }

        /// <summary>
        /// Writes the FBX binary document directly to a seekable stream without an outer buffer.
        /// Used internally by <see cref="Write(FbxDocument, Stream)"/> with a <see cref="MemoryStream"/>
        /// and exposed for benchmark/parity verification against unbuffered streams.
        /// </summary>
        public static void WriteDirectToSeekableStream(FbxDocument document, Stream seekableStream)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(seekableStream);

            using var scratch = new ArrayScratchBuffers();
            using var bw = new BinaryWriter(seekableStream, Encoding.UTF8, leaveOpen: true);

            bw.Write(HeadMagic);
            bw.Write(7400);

            foreach (var node in document.Nodes)
            {
                if (node != null)
                {
                    WriteNode(bw, node, scratch);
                }
            }

            // Top-level null sentinel
            bw.Write(NullSentinel);

            // Autodesk standard footer
            bw.Write(FootId);
            bw.Write(0); // 4 zero bytes

            long pos = seekableStream.Position;
            int pad = (int)(((pos + 15) & ~15) - pos);
            if (pad == 0) pad = 16;
            Span<byte> padBytes = stackalloc byte[16];
            padBytes.Clear();
            bw.Write(padBytes.Slice(0, pad)); // padding ensuring version is at 16-byte boundary

            bw.Write(7400);
            bw.Write(FooterZeros120);
            bw.Write(ExtMagic);
            bw.Flush();
        }

        private static void WriteNode(BinaryWriter bw, FbxNode node, ArrayScratchBuffers scratch)
        {
            var stream = bw.BaseStream;
            long startPos = stream.Position;

            // Placeholders: EndOffset (4 bytes), PropCount (4 bytes), PropLength (4 bytes)
            bw.Write(0u);
            bw.Write((uint)node.Properties.Length);
            bw.Write(0u);

            var name = node.Identifier?.Value ?? string.Empty;
            if (name.Length == 0)
            {
                bw.Write((byte)0);
            }
            else
            {
                int maxByteCount = Encoding.UTF8.GetMaxByteCount(name.Length);
                if (maxByteCount <= 512)
                {
                    Span<byte> nameBuffer = stackalloc byte[512];
                    int encodedLen = Encoding.UTF8.GetBytes(name.AsSpan(), nameBuffer);
                    int nameLen = Math.Min(encodedLen, 255);
                    bw.Write((byte)nameLen);
                    bw.Write(nameBuffer.Slice(0, nameLen));
                }
                else
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
                    try
                    {
                        int encodedLen = Encoding.UTF8.GetBytes(name.AsSpan(), rented);
                        int nameLen = Math.Min(encodedLen, 255);
                        bw.Write((byte)nameLen);
                        bw.Write(rented, 0, nameLen);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            long propStart = stream.Position;
            foreach (var prop in node.Properties)
            {
                WriteProperty(bw, prop, scratch);
            }
            long propEnd = stream.Position;
            uint propLen = (uint)(propEnd - propStart);

            bool alwaysSentinel = name == "AnimationStack" || name == "AnimationLayer";
            if (node.Nodes.Length > 0 || alwaysSentinel)
            {
                foreach (var child in node.Nodes)
                {
                    if (child != null)
                    {
                        WriteNode(bw, child, scratch);
                    }
                }
                bw.Write(NullSentinel);
            }

            long endOffset = stream.Position;

            // Backpatch
            stream.Position = startPos;
            bw.Write((uint)endOffset);
            stream.Position = startPos + 8;
            bw.Write(propLen);

            stream.Position = endOffset;
        }

        private static void WriteProperty(BinaryWriter bw, Token token, ArrayScratchBuffers scratch)
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
                    var strValue = str.Value ?? string.Empty;
                    int strByteLen = Encoding.UTF8.GetByteCount(strValue);
                    bw.Write((byte)'S');
                    bw.Write(strByteLen);
                    if (strByteLen > 0)
                    {
                        if (strByteLen <= 512)
                        {
                            Span<byte> strBuf = stackalloc byte[strByteLen];
                            Encoding.UTF8.GetBytes(strValue.AsSpan(), strBuf);
                            bw.Write(strBuf);
                        }
                        else
                        {
                            byte[] rented = ArrayPool<byte>.Shared.Rent(strByteLen);
                            try
                            {
                                Encoding.UTF8.GetBytes(strValue.AsSpan(), rented);
                                bw.Write(rented, 0, strByteLen);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(rented);
                            }
                        }
                    }
                    break;
                case ByteArrayToken bat:
                    bw.Write((byte)'R');
                    bw.Write(bat.Values.Length);
                    bw.Write(bat.Values);
                    break;
                case DoubleArrayToken dat:
                    WriteArray(bw, 'd', 8, dat.Values.Length, scratch, w => { foreach (var v in dat.Values) w.Write(v); });
                    break;
                case FloatArrayToken fat:
                    WriteArray(bw, 'f', 4, fat.Values.Length, scratch, w => { foreach (var v in fat.Values) w.Write(v); });
                    break;
                case IntegerArrayToken iat:
                    WriteArray(bw, 'i', 4, iat.Values.Length, scratch, w => { foreach (var v in iat.Values) w.Write(v); });
                    break;
                case LongArrayToken lat:
                    WriteArray(bw, 'l', 8, lat.Values.Length, scratch, w => { foreach (var v in lat.Values) w.Write(v); });
                    break;
                case BooleanArrayToken blat:
                    WriteArray(bw, 'b', 1, blat.Values.Length, scratch, w => { foreach (var v in blat.Values) w.Write(v ? (byte)1 : (byte)0); });
                    break;
                default:
                    throw new NotSupportedException($"Unsupported token type: {token?.GetType().Name}");
            }
        }

        private static void WriteArray(BinaryWriter bw, char typeChar, int elemSize, int length, ArrayScratchBuffers scratch, Action<BinaryWriter> itemWriter)
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
                var rawMs = scratch.RawStream;
                rawMs.SetLength(0);
                rawMs.Position = 0;
                if (rawMs.Capacity < uncompressedSize)
                {
                    rawMs.Capacity = uncompressedSize;
                }

                itemWriter(scratch.RawWriter);
                scratch.RawWriter.Flush();

                ReadOnlySpan<byte> rawSpan = rawMs.TryGetBuffer(out var rawSeg)
                    ? rawSeg.AsSpan(0, (int)rawMs.Length)
                    : rawMs.ToArray();

                var compMs = scratch.CompressedStream;
                compMs.SetLength(0);
                compMs.Position = 0;
                CompressZlib(rawSpan, compMs);

                ReadOnlySpan<byte> compSpan = compMs.TryGetBuffer(out var compSeg)
                    ? compSeg.AsSpan(0, (int)compMs.Length)
                    : compMs.ToArray();

                bw.Write(1); // encoding = 1 (compressed)
                bw.Write(compSpan.Length);
                bw.Write(compSpan);
            }
        }

        private static void CompressZlib(ReadOnlySpan<byte> rawData, MemoryStream compMs)
        {
            uint checksum = CalcAdler32(rawData);

            // Standard RFC 1950 zlib header: CMF = 0x78 (32K window, deflate), FLG = 0x01 (fastest)
            compMs.WriteByte(0x78);
            compMs.WriteByte(0x01);

            using (var deflate = new DeflateStream(compMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(rawData);
            }

            // Adler-32 checksum (Big Endian)
            compMs.WriteByte((byte)((checksum >> 24) & 0xFF));
            compMs.WriteByte((byte)((checksum >> 16) & 0xFF));
            compMs.WriteByte((byte)((checksum >> 8) & 0xFF));
            compMs.WriteByte((byte)(checksum & 0xFF));
        }

        private static uint CalcAdler32(ReadOnlySpan<byte> data)
        {
            const uint modAdler = 65521;
            const int nmax = 5552;

            uint a = 1;
            uint b = 0;
            int index = 0;
            int len = data.Length;

            while (len > 0)
            {
                int chunk = len < nmax ? len : nmax;
                len -= chunk;
                int end = index + chunk;
                while (index < end)
                {
                    a += data[index++];
                    b += a;
                }
                a %= modAdler;
                b %= modAdler;
            }

            return (b << 16) | a;
        }
    }
}
