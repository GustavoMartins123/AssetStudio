#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Pure-C# reconstructor that converts FMOD FSB5 Vorbis (codec 15) sample streams
    /// into standard RFC 3533 Ogg Vorbis (.ogg) byte streams without external native libraries.
    /// </summary>
    public static class Fsb5VorbisReconstructor
    {
        public const uint ChunkTypeLoop = 3u;
        public const uint ChunkTypeVorbisData = 11u;

        private const int ShortBlockSize = 256; // 2^8
        private const int LongBlockSize = 2048; // 2^11
        private const byte BlockSizeByte = 0xB8; // blocksize_0 = 8 (256), blocksize_1 = 11 (2048)
        private const string VendorName = "AssetStudio";

        private static readonly byte[] VorbisCodecId = Encoding.ASCII.GetBytes("vorbis");
        private static readonly uint[] OggCrcTable = BuildOggCrcTable();

        /// <summary>
        /// Reconstructs a standard Ogg Vorbis (.ogg) file from parsed FSB5 Vorbis sample metadata and payload bytes.
        /// Returns <c>null</c> when required Vorbis metadata/setup headers are missing or the payload is invalid.
        /// </summary>
        public static byte[]? ReconstructOgg(UnityAudioSampleMetadata sample, ReadOnlySpan<byte> sampleBytes, uint serialNumber = 1u)
        {
            if (sample == null
                || sample.NumChannels <= 0
                || sample.NumChannels > 255
                || sample.Frequency <= 0
                || sampleBytes.Length < 4)
            {
                return null;
            }

            if (!TryGetVorbisSetupCrc32(sample, out uint setupCrc32))
            {
                return null;
            }

            if (!Fsb5VorbisCodebookTable.TryGetSetupHeader(setupCrc32, out var setup))
            {
                return null;
            }

            // Collect (offset, length) spans of length-prefixed FSB5 Vorbis packets.
            var packets = new List<(int Offset, int Length)>(Math.Max(16, sampleBytes.Length / 128));
            int pos = 0;
            while (pos + 2 <= sampleBytes.Length)
            {
                ushort packetSize = BinaryPrimitives.ReadUInt16LittleEndian(sampleBytes.Slice(pos, 2));
                pos += 2;

                // FSB5 pads sample data to 32-byte alignment with zeros; 0xFFFF is also an EOS sentinel.
                if (packetSize == 0 || packetSize == 0xFFFF)
                {
                    break;
                }

                if (packetSize > sampleBytes.Length - pos)
                {
                    break;
                }

                packets.Add((pos, packetSize));
                pos += packetSize;
            }

            if (packets.Count == 0)
            {
                return null;
            }

            bool hasLoop = TryGetLoopPoints(sample, out uint loopStart, out uint loopEnd);

            var identPacket = BuildIdentificationPacket((byte)sample.NumChannels, sample.Frequency);
            var commentPacket = BuildCommentPacket(VendorName, hasLoop, loopStart, loopEnd);

            int estimatedSize = sampleBytes.Length + setup.SetupPacket.Length + 4096;
            var writer = new OggStreamWriter(serialNumber, estimatedSize);

            // Vorbis I Appendix A.2:
            //   Page 0: BOS containing ONLY the Identification header (granulePos = 0).
            //   Page 1+: Comment + Setup headers, flushed before the first audio packet (granulePos = 0).
            writer.WritePacket(identPacket, granulePos: 0, isBos: true, isEos: false, forceFlush: true);
            writer.WritePacket(commentPacket, granulePos: 0, isBos: false, isEos: false, forceFlush: false);
            writer.WritePacket(setup.SetupPacket, granulePos: 0, isBos: false, isEos: false, forceFlush: true);

            long granulePos = 0;
            int previousBlockSize = 0;
            long targetSampleCount = sample.SampleCount;

            for (int i = 0; i < packets.Count; i++)
            {
                var (pktOffset, pktLength) = packets[i];
                var packetSpan = sampleBytes.Slice(pktOffset, pktLength);
                int blockSize = GetPacketBlockSize(packetSpan, setup.ModeCount, setup.BlockFlagsMask);

                if (previousBlockSize == 0)
                {
                    granulePos = 0;
                }
                else if (blockSize > 0)
                {
                    granulePos += (previousBlockSize + blockSize) / 4;
                }

                if (blockSize > 0)
                {
                    previousBlockSize = blockSize;
                }

                bool reachedTarget = targetSampleCount > 0 && granulePos >= targetSampleCount;
                bool isLast = (i == packets.Count - 1) || reachedTarget;
                if (reachedTarget)
                {
                    granulePos = targetSampleCount;
                }

                writer.WritePacket(packetSpan, granulePos, isBos: false, isEos: isLast, forceFlush: isLast);

                if (isLast)
                {
                    break;
                }
            }

            return writer.ToArray();
        }

        public static bool TryGetVorbisSetupCrc32(UnityAudioSampleMetadata sample, out uint setupCrc32)
        {
            setupCrc32 = 0;
            if (sample?.Chunks == null)
            {
                return false;
            }

            for (int i = 0; i < sample.Chunks.Count; i++)
            {
                var chunk = sample.Chunks[i];
                if (chunk.ChunkType == ChunkTypeVorbisData && chunk.Data != null && chunk.Data.Length >= 4)
                {
                    setupCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Data.AsSpan(0, 4));
                    return true;
                }
            }

            return false;
        }

        public static bool TryGetLoopPoints(UnityAudioSampleMetadata sample, out uint loopStart, out uint loopEnd)
        {
            loopStart = 0;
            loopEnd = 0;
            if (sample?.Chunks == null)
            {
                return false;
            }

            for (int i = 0; i < sample.Chunks.Count; i++)
            {
                var chunk = sample.Chunks[i];
                if (chunk.ChunkType == ChunkTypeLoop && chunk.Data != null && chunk.Data.Length >= 8)
                {
                    uint start = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Data.AsSpan(0, 4));
                    uint end = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Data.AsSpan(4, 4));
                    if (end > start)
                    {
                        loopStart = start;
                        loopEnd = end;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Computes the RFC 3533 Ogg page CRC32 (non-reflected MSB-first polynomial 0x04C11DB7, init 0, final XOR 0).
        /// Bytes 22..25 of the page must be zeroed before calling this method.
        /// </summary>
        public static uint ComputeOggCrc32(ReadOnlySpan<byte> pageData)
        {
            uint crc = 0;
            var table = OggCrcTable;
            for (int i = 0; i < pageData.Length; i++)
            {
                crc = (crc << 8) ^ table[((crc >> 24) ^ pageData[i]) & 0xFF];
            }
            return crc;
        }

        public static int GetPacketBlockSize(ReadOnlySpan<byte> packet, byte modeCount, byte blockFlagsMask)
        {
            if (packet.IsEmpty || modeCount == 0)
            {
                return 0;
            }

            byte firstByte = packet[0];
            // Vorbis I spec section 4.3.1: audio packets have packet_type bit 0 == 0.
            if ((firstByte & 1) != 0)
            {
                return 0;
            }

            int modeBits = ILog(modeCount - 1);
            int modeNumber = modeBits == 0 ? 0 : (firstByte >> 1) & ((1 << modeBits) - 1);
            if (modeNumber >= modeCount)
            {
                return ShortBlockSize;
            }

            bool isLongBlock = ((blockFlagsMask >> modeNumber) & 1) != 0;
            return isLongBlock ? LongBlockSize : ShortBlockSize;
        }

        private static int ILog(int value)
        {
            int bits = 0;
            while (value > 0)
            {
                bits++;
                value >>= 1;
            }
            return bits;
        }

        private static byte[] BuildIdentificationPacket(byte channels, int sampleRate)
        {
            var packet = new byte[30];
            packet[0] = 1; // Packet type 1: Identification
            Buffer.BlockCopy(VorbisCodecId, 0, packet, 1, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7, 4), 0u); // vorbis_version = 0
            packet[11] = channels;
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), sampleRate);
            // bitrate_maximum, bitrate_nominal, bitrate_minimum = 0 (unspecified)
            packet[28] = BlockSizeByte;
            packet[29] = 1; // framing_flag = 1
            return packet;
        }

        private static byte[] BuildCommentPacket(string vendor, bool hasLoop, uint loopStart, uint loopEnd)
        {
            var vendorBytes = Encoding.UTF8.GetBytes(vendor);
            byte[]? loopStartComment = null;
            byte[]? loopEndComment = null;
            int userCommentCount = 0;

            if (hasLoop)
            {
                loopStartComment = Encoding.UTF8.GetBytes("LOOPSTART=" + loopStart.ToString(CultureInfo.InvariantCulture));
                loopEndComment = Encoding.UTF8.GetBytes("LOOPEND=" + loopEnd.ToString(CultureInfo.InvariantCulture));
                userCommentCount = 2;
            }

            int totalLength = 1 + 6 + 4 + vendorBytes.Length + 4 + 1;
            if (hasLoop)
            {
                totalLength += 4 + loopStartComment!.Length + 4 + loopEndComment!.Length;
            }

            var packet = new byte[totalLength];
            int pos = 0;
            packet[pos++] = 3; // Packet type 3: Comment
            Buffer.BlockCopy(VorbisCodecId, 0, packet, pos, 6);
            pos += 6;

            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(pos, 4), vendorBytes.Length);
            pos += 4;
            Buffer.BlockCopy(vendorBytes, 0, packet, pos, vendorBytes.Length);
            pos += vendorBytes.Length;

            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(pos, 4), userCommentCount);
            pos += 4;

            if (hasLoop)
            {
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(pos, 4), loopStartComment!.Length);
                pos += 4;
                Buffer.BlockCopy(loopStartComment, 0, packet, pos, loopStartComment.Length);
                pos += loopStartComment.Length;

                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(pos, 4), loopEndComment!.Length);
                pos += 4;
                Buffer.BlockCopy(loopEndComment, 0, packet, pos, loopEndComment.Length);
                pos += loopEndComment.Length;
            }

            packet[pos] = 1; // framing_bit = 1
            return packet;
        }

        private static uint[] BuildOggCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int bit = 0; bit < 8; bit++)
                {
                    r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : (r << 1);
                }
                table[i] = r;
            }
            return table;
        }

        private sealed class OggStreamWriter
        {
            private const byte FlagContinued = 0x01;
            private const byte FlagBos = 0x02;
            private const byte FlagEos = 0x04;
            private const int MaxSegmentsPerPage = 255;
            private const int TargetPageBodyBytes = 4096;

            private readonly MemoryStream _output;
            private readonly MemoryStream _body = new MemoryStream(8192);
            private readonly byte[] _segments = new byte[MaxSegmentsPerPage];
            private readonly uint _serialNumber;

            private int _segmentCount;
            private uint _pageSequence;
            private bool _pageIsBos;
            private bool _pageIsContinued;
            private bool _hasFinishedPacketOnPage;
            private long _pageGranulePos = -1L;

            public OggStreamWriter(uint serialNumber, int initialCapacity)
            {
                _serialNumber = serialNumber;
                _output = new MemoryStream(Math.Max(4096, initialCapacity));
            }

            public void WritePacket(ReadOnlySpan<byte> packet, long granulePos, bool isBos, bool isEos, bool forceFlush)
            {
                if (isBos && _pageSequence == 0 && _segmentCount == 0)
                {
                    _pageIsBos = true;
                }

                int remaining = packet.Length;
                int offset = 0;
                bool wroteSegmentForCurrentPacket = false;

                while (true)
                {
                    int chunk = remaining >= 255 ? 255 : remaining;

                    if (_segmentCount == MaxSegmentsPerPage)
                    {
                        FlushPage(isEos: false);
                        _pageIsContinued = wroteSegmentForCurrentPacket;
                    }

                    _segments[_segmentCount++] = (byte)chunk;
                    wroteSegmentForCurrentPacket = true;
                    if (chunk > 0)
                    {
                        _body.Write(packet.Slice(offset, chunk));
                        offset += chunk;
                        remaining -= chunk;
                    }

                    if (chunk < 255)
                    {
                        break;
                    }
                }

                _hasFinishedPacketOnPage = true;
                _pageGranulePos = granulePos;

                if (forceFlush || isEos || _body.Length >= TargetPageBodyBytes || _segmentCount == MaxSegmentsPerPage)
                {
                    FlushPage(isEos: isEos);
                    _pageIsContinued = false;
                }
            }

            public byte[] ToArray()
            {
                if (_segmentCount > 0)
                {
                    FlushPage(isEos: false);
                }

                return _output.ToArray();
            }

            private void FlushPage(bool isEos)
            {
                if (_segmentCount == 0)
                {
                    return;
                }

                int bodyLength = (int)_body.Length;
                int headerLength = 27 + _segmentCount;
                int pageLength = headerLength + bodyLength;
                byte[] rented = ArrayPool<byte>.Shared.Rent(pageLength);
                try
                {
                    var page = rented.AsSpan(0, pageLength);
                    page[0] = (byte)'O';
                    page[1] = (byte)'g';
                    page[2] = (byte)'g';
                    page[3] = (byte)'S';
                    page[4] = 0; // Ogg stream structure version

                    byte headerType = 0;
                    if (_pageIsContinued) headerType |= FlagContinued;
                    if (_pageIsBos) headerType |= FlagBos;
                    if (isEos) headerType |= FlagEos;
                    page[5] = headerType;

                    long pageGranule = _hasFinishedPacketOnPage ? _pageGranulePos : -1L;
                    BinaryPrimitives.WriteInt64LittleEndian(page.Slice(6, 8), pageGranule);
                    BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(14, 4), _serialNumber);
                    BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(18, 4), _pageSequence++);
                    BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(22, 4), 0u); // Zero CRC field before hashing
                    page[26] = (byte)_segmentCount;

                    _segments.AsSpan(0, _segmentCount).CopyTo(page.Slice(27, _segmentCount));
                    if (bodyLength > 0)
                    {
                        _body.GetBuffer().AsSpan(0, bodyLength).CopyTo(page.Slice(headerLength, bodyLength));
                    }

                    uint crc = ComputeOggCrc32(page);
                    BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(22, 4), crc);

                    _output.Write(rented, 0, pageLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                _segmentCount = 0;
                _body.SetLength(0);
                _pageIsBos = false;
                _hasFinishedPacketOnPage = false;
                _pageGranulePos = -1L;
            }
        }
    }
}
