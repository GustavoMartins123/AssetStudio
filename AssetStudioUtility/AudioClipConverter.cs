#nullable enable
using Fmod5Sharp;
using System;

namespace AssetStudio
{
    public class AudioClipConverter
    {
        private AudioClip m_AudioClip;

        public AudioClipConverter(AudioClip audioClip)
        {
            m_AudioClip = audioClip;
        }

        public byte[]? ConvertToWav()
        {
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return null;

            try
            {
                var bank = FsbLoader.LoadFsbFromByteArray(m_AudioData);
                if (bank.Samples == null || bank.Samples.Count == 0)
                    return null;

                var sample = bank.Samples[0];
                if (bank.Header.AudioType == Fmod5Sharp.FmodTypes.FmodAudioType.MPEG)
                {
                    return sample.SampleBytes;
                }
                if (sample.RebuildAsStandardFileFormat(out var dataBytes, out _))
                {
                    return dataBytes;
                }
            }
            catch (Exception)
            {
                // Fallback or ignore
            }
            return null;
        }


        public string GetExtensionName()
        {
            if (m_AudioClip.version[0] < 5)
            {
                switch (m_AudioClip.m_Type)
                {
                    case FMODSoundType.ACC:
                        return ".m4a";
                    case FMODSoundType.AIFF:
                        return ".aif";
                    case FMODSoundType.IT:
                        return ".it";
                    case FMODSoundType.MOD:
                        return ".mod";
                    case FMODSoundType.MPEG:
                        return ".mp3";
                    case FMODSoundType.OGGVORBIS:
                        return ".ogg";
                    case FMODSoundType.S3M:
                        return ".s3m";
                    case FMODSoundType.WAV:
                        return ".wav";
                    case FMODSoundType.XM:
                        return ".xm";
                    case FMODSoundType.XMA:
                        return ".wav";
                    case FMODSoundType.VAG:
                        return ".vag";
                    case FMODSoundType.AUDIOQUEUE:
                        return ".fsb";
                }

            }
            else
            {
                switch (m_AudioClip.m_CompressionFormat)
                {
                    case AudioCompressionFormat.PCM:
                        return ".fsb";
                    case AudioCompressionFormat.Vorbis:
                        return ".fsb";
                    case AudioCompressionFormat.ADPCM:
                        return ".fsb";
                    case AudioCompressionFormat.MP3:
                        return ".mp3";
                    case AudioCompressionFormat.PSMVAG:
                        return ".fsb";
                    case AudioCompressionFormat.HEVAG:
                        return ".fsb";
                    case AudioCompressionFormat.XMA:
                        return ".fsb";
                    case AudioCompressionFormat.AAC:
                        return ".m4a";
                    case AudioCompressionFormat.GCADPCM:
                        return ".fsb";
                    case AudioCompressionFormat.ATRAC9:
                        return ".fsb";
                }
            }

            return ".AudioClip";
        }

        public bool IsSupport
        {
            get
            {
                if (m_AudioClip.version[0] < 5)
                {
                    return false;
                }
                try
                {
                    var m_AudioData = m_AudioClip.m_AudioData.GetData();
                    if (m_AudioData == null || m_AudioData.Length == 0)
                        return false;
                    
                    var bank = FsbLoader.LoadFsbFromByteArray(m_AudioData);
                    if (bank.Samples == null || bank.Samples.Count == 0)
                        return false;

                    if (bank.Header.AudioType == Fmod5Sharp.FmodTypes.FmodAudioType.MPEG)
                    {
                        return true;
                    }
                    return bank.Samples[0].RebuildAsStandardFileFormat(out _, out _);
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
