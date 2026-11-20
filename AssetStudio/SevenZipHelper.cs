using System;
using System.IO;
using SevenZip.Compression.LZMA;


namespace AssetStudio
{
    public static class SevenZipHelper
    {
        /// <summary>
        /// Upper bound for the LZMA dictionary (sliding window) requested by a stream header.
        /// The decoder allocates the whole window up front, so an unchecked 5-byte header could
        /// otherwise demand up to 4 GB. Unity never uses dictionaries this large.
        /// </summary>
        public const uint MaxDictionarySize = 128u * 1024u * 1024u;

        private static void ValidateDecoderProperties(byte[] properties)
        {
            var dictionarySize = BitConverter.ToUInt32(properties, 1);
            if (dictionarySize > MaxDictionarySize)
            {
                throw new InvalidDataException($"LZMA dictionary size {dictionarySize} exceeds the safety limit of {MaxDictionarySize} bytes.");
            }
        }

        public static MemoryStream StreamDecompress(MemoryStream inStream)
        {
            var decoder = new Decoder();

            inStream.Seek(0, SeekOrigin.Begin);
            
            var properties = new byte[5];
            if (inStream.Read(properties, 0, 5) != 5)
                throw new Exception("input .lzma is too short");
            long outSize = 0;
            for (var i = 0; i < 8; i++)
            {
                var v = inStream.ReadByte();
                if (v < 0)
                    throw new Exception("Can't Read 1");
                outSize |= ((long)(byte)v) << (8 * i);
            }
            if (outSize < 0 || outSize > int.MaxValue)
            {
                throw new InvalidDataException($"Invalid LZMA uncompressed size {outSize}.");
            }
            ValidateDecoderProperties(properties);
            decoder.SetDecoderProperties(properties);

            var compressedSize = inStream.Length - inStream.Position;
            // Do not trust the header blindly for the up-front allocation; the stream grows if needed.
            var initialCapacity = (int)Math.Min(outSize, Math.Max(compressedSize * 8, 1L << 20));
            var newOutStream = new MemoryStream(initialCapacity);
            decoder.Code(inStream, newOutStream, compressedSize, outSize, null);

            newOutStream.Position = 0;
            return newOutStream;
        }

        public static void StreamDecompress(Stream compressedStream, Stream decompressedStream, long compressedSize, long decompressedSize)
        {
            if (compressedSize < 5 || decompressedSize < 0)
            {
                throw new InvalidDataException($"Invalid LZMA block sizes (compressed {compressedSize}, uncompressed {decompressedSize}).");
            }
            var basePosition = compressedStream.Position;
            var decoder = new Decoder();
            var properties = new byte[5];
            if (compressedStream.Read(properties, 0, 5) != 5)
                throw new Exception("input .lzma is too short");
            ValidateDecoderProperties(properties);
            decoder.SetDecoderProperties(properties);
            decoder.Code(compressedStream, decompressedStream, compressedSize - 5, decompressedSize, null);
            compressedStream.Position = basePosition + compressedSize;
        }
    }
}
