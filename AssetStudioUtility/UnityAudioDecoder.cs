#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public enum UnityAudioCodec : uint
    {
        None = 0,
        Pcm8 = 1,
        Pcm16 = 2,
        Pcm24 = 3,
        Pcm32 = 4,
        PcmFloat = 5,
        Gcadpcm = 6,
        ImaAdpcm = 7,
        Vag = 8,
        Hevag = 9,
        Xma = 10,
        Mpeg = 11,
        Celt = 12,
        At9 = 13,
        Xwma = 14,
        Vorbis = 15,
        Fadpcm = 16,
        Opus = 17
    }

    public class UnityAudioSampleMetadata
    {
        public bool HasAnyChunks;
        public uint FrequencyId;
        public int NumChannels;
        public bool IsStereo;
        public uint SampleCount;
        public uint DataOffset;
        public List<UnityAudioChunk> Chunks = new List<UnityAudioChunk>();

        private static readonly Dictionary<uint, int> Frequencies = new Dictionary<uint, int>
        {
            { 0u, 4000 },
            { 1u, 8000 },
            { 2u, 11000 },
            { 3u, 12000 },
            { 4u, 16000 },
            { 5u, 22050 },
            { 6u, 24000 },
            { 7u, 32000 },
            { 8u, 44100 },
            { 9u, 48000 },
            { 10u, 96000 }
        };

        public int Frequency
        {
            get
            {
                if (Frequencies.TryGetValue(FrequencyId, out var val))
                {
                    return val;
                }
                return (int)FrequencyId;
            }
        }

        public void Read(BinaryReader reader)
        {
            ulong raw = reader.ReadUInt64();
            HasAnyChunks = (raw & 1) == 1;
            FrequencyId = (uint)Bits(raw, 1, 4);
            int chBits = (int)Bits(raw, 5, 2);
            NumChannels = chBits switch
            {
                0 => 1,
                1 => 2,
                2 => 6,
                3 => 8,
                _ => 1
            };
            IsStereo = NumChannels == 2;
            DataOffset = (uint)((int)Bits(raw, 7, 27) * 32);
            SampleCount = (uint)Bits(raw, 34, 30);
        }

        private static ulong Bits(ulong raw, int lowestBit, int numBits)
        {
            ulong mask = (1UL << numBits) - 1;
            return (raw >> lowestBit) & mask;
        }
    }

    public class UnityAudioChunk
    {
        public uint ChunkType;
        public byte[] Data;

        public UnityAudioChunk(uint chunkType, byte[] data)
        {
            ChunkType = chunkType;
            Data = data;
        }
    }

    public static class UnityAudioDecoder
    {
        private static readonly int[] ADPCMTable = new int[89]
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
            19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
            337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
            2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
            5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        private static readonly int[] IMA_IndexTable = new int[16]
        {
            -1, -1, -1, -1, 2, 4, 6, 8,
            -1, -1, -1, -1, 2, 4, 6, 8
        };

        private static readonly short[,] FadpcmCoefs = new short[8, 2]
        {
            { 0, 0 },
            { 60, 0 },
            { 122, 60 },
            { 115, 52 },
            { 98, 55 },
            { 0, 0 },
            { 0, 0 },
            { 0, 0 }
        };

        public static byte[]? ConvertToWav(byte[] containerData)
        {
            if (containerData == null || containerData.Length < 32)
                return null;

            using (var stream = new MemoryStream(containerData))
            using (var reader = new BinaryReader(stream))
            {
                // Parse container header
                var magicBytes = reader.ReadBytes(4);
                string magic = Encoding.ASCII.GetString(magicBytes);
                if (magic != "FSB5")
                    return null;

                uint version = reader.ReadUInt32();
                uint numSamples = reader.ReadUInt32();
                uint sizeOfSampleHeaders = reader.ReadUInt32();
                uint sizeOfNameTable = reader.ReadUInt32();
                uint sizeOfData = reader.ReadUInt32();
                uint codecValue = reader.ReadUInt32();
                var codec = (UnityAudioCodec)codecValue;

                uint headerSize = (version == 0) ? 64u : 60u;
                
                // Read sample headers
                stream.Position = headerSize;
                var sampleMetadatas = new List<UnityAudioSampleMetadata>((int)numSamples);
                for (int i = 0; i < numSamples; i++)
                {
                    var meta = new UnityAudioSampleMetadata();
                    meta.Read(reader);
                    sampleMetadatas.Add(meta);
                }

                // Process chunks if any
                for (int i = 0; i < numSamples; i++)
                {
                    var meta = sampleMetadatas[i];
                    if (meta.HasAnyChunks)
                    {
                        bool moreChunks = true;
                        while (moreChunks)
                        {
                            uint rawChunk = reader.ReadUInt32();
                            moreChunks = (rawChunk & 1) == 1;
                            uint chunkSize = (rawChunk >> 1) & 0xFFFFFF;
                            uint chunkType = (rawChunk >> 25) & 0x7F;
                            byte[] chunkData = reader.ReadBytes((int)chunkSize);
                            meta.Chunks.Add(new UnityAudioChunk(chunkType, chunkData));

                            // Override frequency if FREQUENCY chunk (type 2)
                            if (chunkType == 2 && chunkData.Length >= 4)
                            {
                                meta.FrequencyId = BitConverter.ToUInt32(chunkData, 0);
                            }
                            // Override channel count if CHANNELS chunk (type 1)
                            else if (chunkType == 1 && chunkData.Length >= 1)
                            {
                                meta.NumChannels = chunkData[0];
                                meta.IsStereo = meta.NumChannels == 2;
                            }
                        }
                    }
                }

                if (numSamples == 0)
                    return null;

                // For simplicity, we decode the first sample.
                var activeSample = sampleMetadatas[0];
                long audioDataStart = headerSize + sizeOfSampleHeaders + sizeOfNameTable;
                long sampleOffset = audioDataStart + activeSample.DataOffset;
                long sampleSize = sizeOfData - activeSample.DataOffset;
                if (numSamples > 1)
                {
                    sampleSize = sampleMetadatas[1].DataOffset - activeSample.DataOffset;
                }

                if (sampleOffset + sampleSize > containerData.Length)
                {
                    return null;
                }

                byte[] sampleBytes = new byte[sampleSize];
                Array.Copy(containerData, sampleOffset, sampleBytes, 0, sampleSize);

                short[]? decodedPcm = null;

                if (codec == UnityAudioCodec.ImaAdpcm)
                {
                    if (activeSample.NumChannels == 2)
                    {
                        decodedPcm = DecodeImaAdpcmStereo(sampleBytes, (int)activeSample.SampleCount, activeSample.NumChannels);
                    }
                    else
                    {
                        decodedPcm = DecodeImaAdpcmMono(sampleBytes, (int)activeSample.SampleCount);
                    }
                }
                else if (codec == UnityAudioCodec.Fadpcm)
                {
                    decodedPcm = DecodeFadpcm(sampleBytes, activeSample.NumChannels);
                }
                else if (codec == UnityAudioCodec.Gcadpcm)
                {
                    // Find DSPCOEFF chunk (type 7)
                    UnityAudioChunk? dspCoeffChunk = activeSample.Chunks.Find(c => c.ChunkType == 7);
                    if (dspCoeffChunk != null)
                    {
                        var dspCoeffs = ParseDspCoefficients(dspCoeffChunk.Data, activeSample.NumChannels);
                        decodedPcm = DecodeGcadpcm(sampleBytes, (int)activeSample.SampleCount, activeSample.NumChannels, dspCoeffs);
                    }
                }
                else if (codec == UnityAudioCodec.Pcm8 || codec == UnityAudioCodec.Pcm16 || codec == UnityAudioCodec.Pcm32)
                {
                    // Fallback to direct raw PCM copy if requested
                    int bytesPerSample = codec switch
                    {
                        UnityAudioCodec.Pcm8 => 1,
                        UnityAudioCodec.Pcm16 => 2,
                        UnityAudioCodec.Pcm32 => 4,
                        _ => 2
                    };
                    int sampleLength = (int)activeSample.SampleCount * activeSample.NumChannels;
                    decodedPcm = new short[sampleLength];
                    for (int i = 0; i < sampleLength; i++)
                    {
                        int srcOffset = i * bytesPerSample;
                        if (srcOffset + bytesPerSample <= sampleBytes.Length)
                        {
                            if (bytesPerSample == 1)
                            {
                                // Convert signed/unsigned 8-bit to 16-bit
                                decodedPcm[i] = (short)((sampleBytes[srcOffset] - 128) * 256);
                            }
                            else if (bytesPerSample == 2)
                            {
                                decodedPcm[i] = ReadInt16LE(sampleBytes, srcOffset);
                            }
                            else if (bytesPerSample == 4)
                            {
                                int val32 = (int)ReadUInt32LE(sampleBytes, srcOffset);
                                decodedPcm[i] = (short)(val32 >> 16);
                            }
                        }
                    }
                }

                if (decodedPcm == null)
                    return null;

                return CreateWavFile(decodedPcm, activeSample.Frequency, activeSample.NumChannels);
            }
        }

        private static short[] DecodeImaAdpcmStereo(byte[] sampleBytes, int sampleCount, int channels)
        {
            short[] array = new short[sampleCount * channels];
            int numFrames = sampleCount / 64;

            int[] hist = new int[channels];
            int[] stepIndex = new int[channels];

            for (int j = 0; j < numFrames; j++)
            {
                int blockStart = 36 * channels * j;

                // Read headers for all channels
                for (int i = 0; i < channels; i++)
                {
                    int headerOffset = blockStart + 4 * i;
                    if (headerOffset + 2 < sampleBytes.Length)
                    {
                        hist[i] = (short)(sampleBytes[headerOffset] | (sampleBytes[headerOffset + 1] << 8));
                        stepIndex[i] = sampleBytes[headerOffset + 2];
                        if (stepIndex[i] < 0) stepIndex[i] = 0;
                        if (stepIndex[i] > 88) stepIndex[i] = 88;
                    }

                    int outIdx = j * 64 * channels + i;
                    if (outIdx < array.Length)
                    {
                        array[outIdx] = (short)hist[i];
                    }
                }

                // Decode remaining 63 samples per channel in frame
                for (int k = 1; k < 64; k++)
                {
                    for (int i = 0; i < channels; i++)
                    {
                        int byteOffset = blockStart + 8 + 4 * (i % 2) + 8 * ((k - 1) / 8) + (k - 1) % 8 / 2;
                        if (byteOffset < sampleBytes.Length)
                        {
                            int b = sampleBytes[byteOffset];
                            int nibble = ((k - 1) % 2 == 1) ? ((b >> 4) & 0xF) : (b & 0xF);

                            int step = ADPCMTable[stepIndex[i]];
                            int delta = step >> 3;
                            if ((nibble & 1) != 0) delta += step >> 2;
                            if ((nibble & 2) != 0) delta += step >> 1;
                            if ((nibble & 4) != 0) delta += step;
                            if ((nibble & 8) != 0) delta = -delta;

                            int newHist = hist[i] + delta;
                            if (newHist < -32768) newHist = -32768;
                            if (newHist > 32767) newHist = 32767;

                            hist[i] = newHist;
                            stepIndex[i] += IMA_IndexTable[nibble];
                            if (stepIndex[i] < 0) stepIndex[i] = 0;
                            if (stepIndex[i] > 88) stepIndex[i] = 88;

                            int outIdx = (j * 64 + k) * channels + i;
                            if (outIdx < array.Length)
                            {
                                array[outIdx] = (short)hist[i];
                            }
                        }
                    }
                }
            }
            return array;
        }

        private static short[] DecodeImaAdpcmMono(byte[] sampleBytes, int sampleCount)
        {
            short[] array = new short[sampleCount];
            int numFrames = sampleCount / 64;
            int outIdx = 0;

            for (int i = 0; i < numFrames; i++)
            {
                int blockStart = 36 * i;
                if (blockStart + 2 >= sampleBytes.Length) break;

                int hist = (short)(sampleBytes[blockStart] | (sampleBytes[blockStart + 1] << 8));
                int stepIndex = sampleBytes[blockStart + 2];
                if (stepIndex < 0) stepIndex = 0;
                if (stepIndex > 88) stepIndex = 88;

                if (outIdx < array.Length)
                {
                    array[outIdx++] = (short)hist;
                }

                for (int j = 1; j < 64; j++)
                {
                    int byteOffset = blockStart + 4 + (j - 1) / 2;
                    if (byteOffset < sampleBytes.Length)
                    {
                        int b = sampleBytes[byteOffset];
                        int nibble = ((j - 1) % 2 == 1) ? ((b >> 4) & 0xF) : (b & 0xF);

                        int step = ADPCMTable[stepIndex];
                        int delta = step >> 3;
                        if ((nibble & 1) != 0) delta += step >> 2;
                        if ((nibble & 2) != 0) delta += step >> 1;
                        if ((nibble & 4) != 0) delta += step;
                        if ((nibble & 8) != 0) delta = -delta;

                        hist += delta;
                        if (hist < -32768) hist = -32768;
                        if (hist > 32767) hist = 32767;

                        stepIndex += IMA_IndexTable[nibble];
                        if (stepIndex < 0) stepIndex = 0;
                        if (stepIndex > 88) stepIndex = 88;

                        if (outIdx < array.Length)
                        {
                            array[outIdx++] = (short)hist;
                        }
                    }
                }
            }
            return array;
        }

        private static short[] DecodeFadpcm(byte[] sampleBytes, int channels)
        {
            int numBlocks = sampleBytes.Length / 140;
            short[] array = new short[numBlocks * 256];
            int[] prev1 = new int[channels];
            int[] prev2 = new int[channels];

            for (int i = 0; i < numBlocks; i++)
            {
                int ch = i % channels;
                int blockStart = i * 140;
                if (blockStart + 12 > sampleBytes.Length) break;

                uint num3 = ReadUInt32LE(sampleBytes, blockStart + 0);
                uint num4 = ReadUInt32LE(sampleBytes, blockStart + 4);
                prev1[ch] = ReadInt16LE(sampleBytes, blockStart + 8);
                prev2[ch] = ReadInt16LE(sampleBytes, blockStart + 10);

                int outBase = (i / channels) * 256 * channels + ch;

                for (int j = 0; j < 8; j++)
                {
                    int coefIndex = (int)((num3 >> (j * 4)) & 0xF) % 7;
                    int scale = (int)((num4 >> (j * 4)) & 0xF);
                    int coef1 = FadpcmCoefs[coefIndex, 0];
                    int coef2 = FadpcmCoefs[coefIndex, 1];
                    int shift = 22 - scale;

                    for (int k = 0; k < 4; k++)
                    {
                        int byteOffset = blockStart + 12 + 16 * j + 4 * k;
                        if (byteOffset + 4 > sampleBytes.Length) continue;

                        uint packedNibbles = ReadUInt32LE(sampleBytes, byteOffset);

                        for (int l = 0; l < 8; l++)
                        {
                            int nibble = (int)((packedNibbles >> (l * 4)) & 0xF);
                            int sampleVal = (nibble << 28) >> shift;

                            int predicted = (sampleVal - prev2[ch] * coef2 + prev1[ch] * coef1) >> 6;

                            if (predicted < -32768) predicted = -32768;
                            else if (predicted > 32767) predicted = 32767;

                            short finalSample = (short)predicted;

                            int outIdx = outBase + (j * 32 + k * 8 + l) * channels;
                            if (outIdx < array.Length)
                            {
                                array[outIdx] = finalSample;
                            }

                            prev2[ch] = prev1[ch];
                            prev1[ch] = finalSample;
                        }
                    }
                }
            }
            return array;
        }

        private static List<short>[] ParseDspCoefficients(byte[] chunkData, int channels)
        {
            var list = new List<short>[channels];
            for (int i = 0; i < channels; i++)
            {
                list[i] = new List<short>();
            }

            int offset = 0;
            for (int i = 0; i < channels; i++)
            {
                for (int j = 0; j < 16; j++)
                {
                    if (offset + 2 <= chunkData.Length)
                    {
                        // Big-endian coefficients
                        short val = (short)((chunkData[offset] << 8) | chunkData[offset + 1]);
                        list[i].Add(val);
                        offset += 2;
                    }
                }
                offset += 14; // Skip history and padding
            }
            return list;
        }

        private static short[] DecodeGcadpcm(byte[] sampleBytes, int sampleCount, int channels, List<short>[] dspCoeffs)
        {
            short[] array = new short[sampleCount * channels];
            int framesPerChannel = (int)Math.Ceiling((double)sampleCount / 14.0);

            short[] yn1 = new short[channels];
            short[] yn2 = new short[channels];

            for (int f = 0; f < framesPerChannel; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int blockStart = 8 * (f * channels + c);
                    if (blockStart >= sampleBytes.Length) break;

                    byte header = sampleBytes[blockStart];
                    int scale = 1 << (header & 0xF);
                    int coefIndex = header >> 4;

                    if (c >= dspCoeffs.Length || coefIndex * 2 + 1 >= dspCoeffs[c].Count) continue;

                    short coef1 = dspCoeffs[c][coefIndex * 2];
                    short coef2 = dspCoeffs[c][coefIndex * 2 + 1];

                    int sampleIdx = f * 14;
                    int limit = Math.Min(14, sampleCount - sampleIdx);

                    int dataOffset = blockStart + 1;

                    for (int s = 0; s < limit; s++)
                    {
                        int byteOffset = dataOffset + s / 2;
                        if (byteOffset >= sampleBytes.Length) break;

                        byte b = sampleBytes[byteOffset];
                        int nibble = (s % 2 == 0) ? GetHighNibbleSigned(b) : GetLowNibbleSigned(b);

                        int sampleVal = (nibble * scale) << 11;
                        int predicted = (sampleVal + 1024 + coef1 * yn1[c] + coef2 * yn2[c]) >> 11;

                        if (predicted < -32768) predicted = -32768;
                        else if (predicted > 32767) predicted = 32767;

                        short finalSample = (short)predicted;
                        yn2[c] = yn1[c];
                        yn1[c] = finalSample;

                        int outIdx = (f * 14 + s) * channels + c;
                        if (outIdx < array.Length)
                        {
                            array[outIdx] = finalSample;
                        }
                    }
                }
            }
            return array;
        }

        private static int GetHighNibbleSigned(byte value)
        {
            int nibble = (value >> 4) & 0xF;
            return (nibble >= 8) ? (nibble - 16) : nibble;
        }

        private static int GetLowNibbleSigned(byte value)
        {
            int nibble = value & 0xF;
            return (nibble >= 8) ? (nibble - 16) : nibble;
        }

        private static ushort ReadUInt16LE(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static short ReadInt16LE(byte[] data, int offset)
        {
            return (short)(data[offset] | (data[offset + 1] << 8));
        }

        private static uint ReadUInt32LE(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static byte[] CreateWavFile(short[] pcmSamples, int sampleRate, int channels)
        {
            int dataLength = pcmSamples.Length * 2;
            byte[] wavBytes = new byte[44 + dataLength];

            wavBytes[0] = (byte)'R'; wavBytes[1] = (byte)'I'; wavBytes[2] = (byte)'F'; wavBytes[3] = (byte)'F';

            uint fileSize = (uint)(dataLength + 36);
            wavBytes[4] = (byte)(fileSize & 0xFF);
            wavBytes[5] = (byte)((fileSize >> 8) & 0xFF);
            wavBytes[6] = (byte)((fileSize >> 16) & 0xFF);
            wavBytes[7] = (byte)((fileSize >> 24) & 0xFF);

            wavBytes[8] = (byte)'W'; wavBytes[9] = (byte)'A'; wavBytes[10] = (byte)'V'; wavBytes[11] = (byte)'E';

            wavBytes[12] = (byte)'f'; wavBytes[13] = (byte)'m'; wavBytes[14] = (byte)'t'; wavBytes[15] = (byte)' ';

            wavBytes[16] = 16; wavBytes[17] = 0; wavBytes[18] = 0; wavBytes[19] = 0;

            wavBytes[20] = 1; wavBytes[21] = 0;

            wavBytes[22] = (byte)(channels & 0xFF);
            wavBytes[23] = (byte)((channels >> 8) & 0xFF);

            wavBytes[24] = (byte)(sampleRate & 0xFF);
            wavBytes[25] = (byte)((sampleRate >> 8) & 0xFF);
            wavBytes[26] = (byte)((sampleRate >> 16) & 0xFF);
            wavBytes[27] = (byte)((sampleRate >> 24) & 0xFF);

            int byteRate = sampleRate * channels * 2;
            wavBytes[28] = (byte)(byteRate & 0xFF);
            wavBytes[29] = (byte)((byteRate >> 8) & 0xFF);
            wavBytes[30] = (byte)((byteRate >> 16) & 0xFF);
            wavBytes[31] = (byte)((byteRate >> 24) & 0xFF);

            int blockAlign = channels * 2;
            wavBytes[32] = (byte)(blockAlign & 0xFF);
            wavBytes[33] = (byte)((blockAlign >> 8) & 0xFF);

            wavBytes[34] = 16; wavBytes[35] = 0;

            wavBytes[36] = (byte)'d'; wavBytes[37] = (byte)'a'; wavBytes[38] = (byte)'t'; wavBytes[39] = (byte)'a';

            wavBytes[40] = (byte)(dataLength & 0xFF);
            wavBytes[41] = (byte)((dataLength >> 8) & 0xFF);
            wavBytes[42] = (byte)((dataLength >> 16) & 0xFF);
            wavBytes[43] = (byte)((dataLength >> 24) & 0xFF);

            int offset = 44;
            for (int i = 0; i < pcmSamples.Length; i++)
            {
                short sample = pcmSamples[i];
                wavBytes[offset++] = (byte)(sample & 0xFF);
                wavBytes[offset++] = (byte)((sample >> 8) & 0xFF);
            }

            return wavBytes;
        }
    }
}
