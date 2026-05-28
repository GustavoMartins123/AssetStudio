using System;
using System.Buffers.Binary;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BitStreams;
using Fmod5Sharp.ChunkData;
using Fmod5Sharp.CodecRebuilders;
using Fmod5Sharp.FmodTypes;
using Fmod5Sharp.Util;
using NAudio.Wave;
using OggVorbisEncoder;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyMetadata("IsTrimmable", "True")]
[assembly: AssemblyCompany("Sam Byass (Samboy063)")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyDescription("Decoder for FMOD 5 sound banks (FSB files)")]
[assembly: AssemblyFileVersion("3.1.0.0")]
[assembly: AssemblyInformationalVersion("3.1.0+d9df2cbcc1aa478d7de9fcb64f83f7ba49f23258")]
[assembly: AssemblyProduct("Fmod5Sharp")]
[assembly: AssemblyTitle("Fmod5Sharp")]
[assembly: AssemblyMetadata("RepositoryUrl", "https://github.com/SamboyCoding/Fmod5Sharp.git")]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: AssemblyVersion("3.1.0.0")]
[module: UnverifiableCode]
[module: RefSafetyRules(11)]
namespace Fmod5Sharp
{
	public static class FsbLoader
	{
		internal static readonly Dictionary<uint, int> Frequencies = new Dictionary<uint, int>
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

		private static FmodSoundBank? LoadInternal(Stream stream, bool throwIfError)
		{
			using BinaryReader binaryReader = new BinaryReader(stream);
			FmodAudioHeader fmodAudioHeader = new FmodAudioHeader(binaryReader);
			if (!fmodAudioHeader.IsValid)
			{
				if (throwIfError)
				{
					throw new Exception("File is probably not an FSB file (magic number mismatch)");
				}
				return null;
			}
			long num = fmodAudioHeader.SizeOfThisHeader + fmodAudioHeader.SizeOfNameTable + fmodAudioHeader.SizeOfSampleHeaders;
			List<FmodSample> list = new List<FmodSample>(fmodAudioHeader.Samples.Count);
			for (int i = 0; i < fmodAudioHeader.Samples.Count; i++)
			{
				FmodSampleMetadata fmodSampleMetadata = fmodAudioHeader.Samples[i];
				long num2 = fmodSampleMetadata.DataOffset;
				long num3 = fmodAudioHeader.SizeOfData;
				if (i < fmodAudioHeader.Samples.Count - 1)
				{
					num3 = fmodAudioHeader.Samples[i + 1].DataOffset;
				}
				byte[] array = new byte[num3 - num2];
				stream.Position = num + num2;
				stream.ReadExactly(array, 0, array.Length);
				FmodSample fmodSample = new FmodSample(fmodSampleMetadata, array);
				if (fmodAudioHeader.SizeOfNameTable != 0)
				{
					long position = fmodAudioHeader.SizeOfThisHeader + fmodAudioHeader.SizeOfSampleHeaders + 4 * i;
					binaryReader.BaseStream.Position = position;
					uint num4 = binaryReader.ReadUInt32();
					num4 += fmodAudioHeader.SizeOfThisHeader + fmodAudioHeader.SizeOfSampleHeaders;
					stream.Position = num4;
					fmodSample.Name = stream.ReadNullTerminatedString();
				}
				list.Add(fmodSample);
			}
			return new FmodSoundBank(fmodAudioHeader, list);
		}

		public static bool TryLoadFsbFromStream(Stream stream, out FmodSoundBank? bank)
		{
			bank = LoadInternal(stream, throwIfError: false);
			return bank != null;
		}

		public static FmodSoundBank LoadFsbFromStream(Stream stream)
		{
			return LoadInternal(stream, throwIfError: true);
		}

		public static bool TryLoadFsbFromByteArray(byte[] bankBytes, out FmodSoundBank? bank)
		{
			using MemoryStream stream = new MemoryStream(bankBytes);
			bank = LoadInternal(stream, throwIfError: false);
			return bank != null;
		}

		public static FmodSoundBank LoadFsbFromByteArray(byte[] bankBytes)
		{
			using MemoryStream stream = new MemoryStream(bankBytes);
			return LoadInternal(stream, throwIfError: true);
		}
	}
}
namespace Fmod5Sharp.Util
{
	internal static class Extensions
	{
		internal static T ReadEndian<T>(this BinaryReader reader) where T : IBinaryReadable, new()
		{
			T result = new T();
			result.Read(reader);
			return result;
		}

		internal static long Position(this BinaryReader reader)
		{
			return reader.BaseStream.Position;
		}

		internal static string ReadString(this BinaryReader reader, int length, Encoding? encoding = null)
		{
			if (encoding == null)
			{
				encoding = Encoding.UTF8;
			}
			byte[] bytes = reader.ReadBytes(length);
			return encoding.GetString(bytes);
		}

		internal static ulong Bits(this uint raw, int lowestBit, int numBits)
		{
			return ((ulong)raw).Bits(lowestBit, numBits);
		}

		internal static ulong Bits(this ulong raw, int lowestBit, int numBits)
		{
			ulong num = 1uL;
			for (int i = 1; i < numBits; i++)
			{
				num = (num << 1) | 1;
			}
			num <<= lowestBit;
			return (raw & num) >> lowestBit;
		}

		internal static string ReadNullTerminatedString(this Stream stream)
		{
			List<byte> list = new List<byte>(16);
			int num;
			while ((num = stream.ReadByte()) > 0)
			{
				list.Add((byte)num);
			}
			return Encoding.UTF8.GetString(list.ToArray());
		}
	}
	[JsonSerializable(typeof(Dictionary<uint, FmodVorbisData>))]
	[GeneratedCode("System.Text.Json.SourceGeneration", "10.0.14.7603")]
	internal class Fmod5SharpJsonContext : JsonSerializerContext, IJsonTypeInfoResolver
	{
		private JsonTypeInfo<byte[]>? _ByteArray;

		private JsonTypeInfo<FmodVorbisData>? _FmodVorbisData;

		private JsonTypeInfo<Dictionary<uint, FmodVorbisData>>? _DictionaryUInt32FmodVorbisData;

		private JsonTypeInfo<int>? _Int32;

		private JsonTypeInfo<uint>? _UInt32;

		private static readonly JsonSerializerOptions s_defaultOptions = new JsonSerializerOptions();

		private const BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		private static readonly JsonEncodedText PropName_headerBytes = JsonEncodedText.Encode("headerBytes");

		private static readonly JsonEncodedText PropName_seekBit = JsonEncodedText.Encode("seekBit");

		public JsonTypeInfo<byte[]> ByteArray => _ByteArray ?? (_ByteArray = (JsonTypeInfo<byte[]>)base.Options.GetTypeInfo(typeof(byte[])));

		public JsonTypeInfo<FmodVorbisData> FmodVorbisData => _FmodVorbisData ?? (_FmodVorbisData = (JsonTypeInfo<FmodVorbisData>)base.Options.GetTypeInfo(typeof(FmodVorbisData)));

		public JsonTypeInfo<Dictionary<uint, FmodVorbisData>> DictionaryUInt32FmodVorbisData => _DictionaryUInt32FmodVorbisData ?? (_DictionaryUInt32FmodVorbisData = (JsonTypeInfo<Dictionary<uint, FmodVorbisData>>)base.Options.GetTypeInfo(typeof(Dictionary<uint, FmodVorbisData>)));

		public JsonTypeInfo<int> Int32 => _Int32 ?? (_Int32 = (JsonTypeInfo<int>)base.Options.GetTypeInfo(typeof(int)));

		public JsonTypeInfo<uint> UInt32 => _UInt32 ?? (_UInt32 = (JsonTypeInfo<uint>)base.Options.GetTypeInfo(typeof(uint)));

		public static Fmod5SharpJsonContext Default { get; } = new Fmod5SharpJsonContext(new JsonSerializerOptions(s_defaultOptions));

		protected override JsonSerializerOptions? GeneratedSerializerOptions { get; } = s_defaultOptions;

		private JsonTypeInfo<byte[]> Create_ByteArray(JsonSerializerOptions options)
		{
			if (!TryGetTypeInfoForRuntimeCustomConverter(options, out JsonTypeInfo<byte[]> jsonTypeInfo))
			{
				jsonTypeInfo = JsonMetadataServices.CreateValueInfo<byte[]>(options, JsonMetadataServices.ByteArrayConverter);
			}
			jsonTypeInfo.OriginatingResolver = this;
			return jsonTypeInfo;
		}

		private JsonTypeInfo<FmodVorbisData> Create_FmodVorbisData(JsonSerializerOptions options)
		{
			if (!TryGetTypeInfoForRuntimeCustomConverter(options, out JsonTypeInfo<FmodVorbisData> jsonTypeInfo))
			{
				JsonObjectInfoValues<FmodVorbisData> obj = new JsonObjectInfoValues<FmodVorbisData>
				{
					ObjectCreator = null,
					ObjectWithParameterizedConstructorCreator = (object[] args) => new FmodVorbisData((byte[])args[0], (int)args[1]),
					PropertyMetadataInitializer = (JsonSerializerContext _) => FmodVorbisDataPropInit(options),
					ConstructorParameterMetadataInitializer = FmodVorbisDataCtorParamInit
				};
				obj.set_ConstructorAttributeProviderFactory((Func<ICustomAttributeProvider>)(() => typeof(FmodVorbisData).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[2]
				{
					typeof(byte[]),
					typeof(int)
				}, null)));
				obj.SerializeHandler = FmodVorbisDataSerializeHandler;
				JsonObjectInfoValues<FmodVorbisData> objectInfo = obj;
				jsonTypeInfo = JsonMetadataServices.CreateObjectInfo(options, objectInfo);
				jsonTypeInfo.NumberHandling = null;
			}
			jsonTypeInfo.OriginatingResolver = this;
			return jsonTypeInfo;
		}

		private static JsonPropertyInfo[] FmodVorbisDataPropInit(JsonSerializerOptions options)
		{
			JsonPropertyInfo[] array = new JsonPropertyInfo[2];
			JsonPropertyInfoValues<byte[]> obj = new JsonPropertyInfoValues<byte[]>
			{
				IsProperty = true,
				IsPublic = true,
				IsVirtual = false,
				DeclaringType = typeof(FmodVorbisData),
				Converter = null,
				Getter = (object obj3) => ((FmodVorbisData)obj3).HeaderBytes,
				Setter = delegate(object obj3, byte[]? value)
				{
					((FmodVorbisData)obj3).HeaderBytes = value;
				},
				IgnoreCondition = null,
				HasJsonInclude = false,
				IsExtensionData = false,
				NumberHandling = null,
				PropertyName = "HeaderBytes",
				JsonPropertyName = "headerBytes"
			};
			obj.set_AttributeProviderFactory((Func<ICustomAttributeProvider>)(() => typeof(FmodVorbisData).GetProperty("HeaderBytes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, typeof(byte[]), Array.Empty<Type>(), null)));
			JsonPropertyInfoValues<byte[]> propertyInfo = obj;
			array[0] = JsonMetadataServices.CreatePropertyInfo(options, propertyInfo);
			array[0].IsGetNullable = false;
			array[0].IsSetNullable = false;
			JsonPropertyInfoValues<int> obj2 = new JsonPropertyInfoValues<int>
			{
				IsProperty = true,
				IsPublic = true,
				IsVirtual = false,
				DeclaringType = typeof(FmodVorbisData),
				Converter = null,
				Getter = (object obj3) => ((FmodVorbisData)obj3).SeekBit,
				Setter = delegate(object obj3, int value)
				{
					((FmodVorbisData)obj3).SeekBit = value;
				},
				IgnoreCondition = null,
				HasJsonInclude = false,
				IsExtensionData = false,
				NumberHandling = null,
				PropertyName = "SeekBit",
				JsonPropertyName = "seekBit"
			};
			obj2.set_AttributeProviderFactory((Func<ICustomAttributeProvider>)(() => typeof(FmodVorbisData).GetProperty("SeekBit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, typeof(int), Array.Empty<Type>(), null)));
			JsonPropertyInfoValues<int> propertyInfo2 = obj2;
			array[1] = JsonMetadataServices.CreatePropertyInfo(options, propertyInfo2);
			return array;
		}

		private void FmodVorbisDataSerializeHandler(Utf8JsonWriter writer, FmodVorbisData? value)
		{
			if (value == null)
			{
				writer.WriteNullValue();
				return;
			}
			writer.WriteStartObject();
			writer.WriteBase64String(PropName_headerBytes, value.HeaderBytes);
			writer.WriteNumber(PropName_seekBit, value.SeekBit);
			writer.WriteEndObject();
		}

		private static JsonParameterInfoValues[] FmodVorbisDataCtorParamInit()
		{
			JsonParameterInfoValues[] array = new JsonParameterInfoValues[2];
			JsonParameterInfoValues obj = new JsonParameterInfoValues
			{
				Name = "headerBytes",
				ParameterType = typeof(byte[]),
				Position = 0,
				HasDefaultValue = false,
				DefaultValue = null
			};
			obj.set_IsNullable(false);
			array[0] = obj;
			JsonParameterInfoValues obj2 = new JsonParameterInfoValues
			{
				Name = "seekBit",
				ParameterType = typeof(int),
				Position = 1,
				HasDefaultValue = false,
				DefaultValue = null
			};
			obj2.set_IsNullable(false);
			array[1] = obj2;
			return array;
		}

		private JsonTypeInfo<Dictionary<uint, FmodVorbisData>> Create_DictionaryUInt32FmodVorbisData(JsonSerializerOptions options)
		{
			if (!TryGetTypeInfoForRuntimeCustomConverter(options, out JsonTypeInfo<Dictionary<uint, FmodVorbisData>> jsonTypeInfo))
			{
				JsonCollectionInfoValues<Dictionary<uint, FmodVorbisData>> collectionInfo = new JsonCollectionInfoValues<Dictionary<uint, FmodVorbisData>>
				{
					ObjectCreator = () => new Dictionary<uint, FmodVorbisData>(),
					SerializeHandler = null
				};
				jsonTypeInfo = JsonMetadataServices.CreateDictionaryInfo<Dictionary<uint, FmodVorbisData>, uint, FmodVorbisData>(options, collectionInfo);
				jsonTypeInfo.NumberHandling = null;
			}
			jsonTypeInfo.OriginatingResolver = this;
			return jsonTypeInfo;
		}

		private JsonTypeInfo<int> Create_Int32(JsonSerializerOptions options)
		{
			if (!TryGetTypeInfoForRuntimeCustomConverter(options, out JsonTypeInfo<int> jsonTypeInfo))
			{
				jsonTypeInfo = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);
			}
			jsonTypeInfo.OriginatingResolver = this;
			return jsonTypeInfo;
		}

		private JsonTypeInfo<uint> Create_UInt32(JsonSerializerOptions options)
		{
			if (!TryGetTypeInfoForRuntimeCustomConverter(options, out JsonTypeInfo<uint> jsonTypeInfo))
			{
				jsonTypeInfo = JsonMetadataServices.CreateValueInfo<uint>(options, JsonMetadataServices.UInt32Converter);
			}
			jsonTypeInfo.OriginatingResolver = this;
			return jsonTypeInfo;
		}

		public Fmod5SharpJsonContext()
			: base(null)
		{
		}

		public Fmod5SharpJsonContext(JsonSerializerOptions options)
			: base(options)
		{
		}

		private static bool TryGetTypeInfoForRuntimeCustomConverter<TJsonMetadataType>(JsonSerializerOptions options, out JsonTypeInfo<TJsonMetadataType> jsonTypeInfo)
		{
			JsonConverter runtimeConverterForType = GetRuntimeConverterForType(typeof(TJsonMetadataType), options);
			if (runtimeConverterForType != null)
			{
				jsonTypeInfo = JsonMetadataServices.CreateValueInfo<TJsonMetadataType>(options, runtimeConverterForType);
				return true;
			}
			jsonTypeInfo = null;
			return false;
		}

		private static JsonConverter? GetRuntimeConverterForType(Type type, JsonSerializerOptions options)
		{
			for (int i = 0; i < options.Converters.Count; i++)
			{
				JsonConverter jsonConverter = options.Converters[i];
				if (jsonConverter != null && jsonConverter.CanConvert(type))
				{
					return ExpandConverter(type, jsonConverter, options, validateCanConvert: false);
				}
			}
			return null;
		}

		private static JsonConverter ExpandConverter(Type type, JsonConverter converter, JsonSerializerOptions options, bool validateCanConvert = true)
		{
			if (validateCanConvert && !converter.CanConvert(type))
			{
				throw new InvalidOperationException($"The converter '{converter.GetType()}' is not compatible with the type '{type}'.");
			}
			if (converter is JsonConverterFactory jsonConverterFactory)
			{
				converter = jsonConverterFactory.CreateConverter(type, options);
				if (converter == null || converter is JsonConverterFactory)
				{
					throw new InvalidOperationException($"The converter '{jsonConverterFactory.GetType()}' cannot return null or a JsonConverterFactory instance.");
				}
			}
			return converter;
		}

		public override JsonTypeInfo? GetTypeInfo(Type type)
		{
			base.Options.TryGetTypeInfo(type, out JsonTypeInfo typeInfo);
			return typeInfo;
		}

		JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options)
		{
			if (type == typeof(byte[]))
			{
				return Create_ByteArray(options);
			}
			if (type == typeof(FmodVorbisData))
			{
				return Create_FmodVorbisData(options);
			}
			if (type == typeof(Dictionary<uint, FmodVorbisData>))
			{
				return Create_DictionaryUInt32FmodVorbisData(options);
			}
			if (type == typeof(int))
			{
				return Create_Int32(options);
			}
			if (type == typeof(uint))
			{
				return Create_UInt32(options);
			}
			return null;
		}
	}
	public static class FmodAudioTypeExtensions
	{
		public static bool IsSupported(this FmodAudioType @this)
		{
			return @this switch
			{
				FmodAudioType.VORBIS => true, 
				FmodAudioType.PCM8 => true, 
				FmodAudioType.PCM16 => true, 
				FmodAudioType.PCM32 => true, 
				FmodAudioType.GCADPCM => true, 
				FmodAudioType.IMAADPCM => true, 
				FmodAudioType.FADPCM => true, 
				_ => false, 
			};
		}

		public static string? FileExtension(this FmodAudioType @this)
		{
			return @this switch
			{
				FmodAudioType.VORBIS => "ogg", 
				FmodAudioType.PCM8 => "wav", 
				FmodAudioType.PCM16 => "wav", 
				FmodAudioType.PCM32 => "wav", 
				FmodAudioType.GCADPCM => "wav", 
				FmodAudioType.IMAADPCM => "wav", 
				FmodAudioType.FADPCM => "wav", 
				_ => null, 
			};
		}
	}
	internal class FmodVorbisData
	{
		private bool _initialized;

		[JsonPropertyName("headerBytes")]
		public byte[] HeaderBytes { get; set; }

		[JsonPropertyName("seekBit")]
		public int SeekBit { get; set; }

		[JsonIgnore]
		private byte[] BlockFlags { get; set; } = Array.Empty<byte>();

		[JsonConstructor]
		public FmodVorbisData(byte[] headerBytes, int seekBit)
		{
			HeaderBytes = headerBytes;
			SeekBit = seekBit;
		}

		internal void InitBlockFlags()
		{
			if (_initialized)
			{
				return;
			}
			_initialized = true;
			BitStream bitStream = new BitStream(HeaderBytes);
			if (bitStream.ReadByte() == 5 && !(bitStream.ReadString(6) != "vorbis"))
			{
				bitStream.Seek(SeekBit / 8, SeekBit % 8);
				int count = bitStream.ReadByte(6) + 1;
				BlockFlags = Enumerable.Range(0, count).Select((Func<int, byte>)delegate
				{
					byte result = bitStream.ReadBit();
					bitStream.ReadBits(16);
					bitStream.ReadBits(16);
					bitStream.ReadBits(8);
					return result;
				}).ToArray();
			}
		}

		public uint GetPacketBlockSize(byte[] packetBytes)
		{
			BitStream bitStream = new BitStream(packetBytes);
			if ((bool)bitStream.ReadBit())
			{
				return 0u;
			}
			int num = 0;
			if (BlockFlags.Length > 1)
			{
				num = bitStream.ReadByte(BlockFlags.Length - 1);
			}
			if (BlockFlags[num] == 1)
			{
				return 2048u;
			}
			return 256u;
		}
	}
	internal interface IBinaryReadable
	{
		internal void Read(BinaryReader reader);
	}
	internal static class Utils
	{
		private static readonly sbyte[] SignedNibbles = new sbyte[16]
		{
			0, 1, 2, 3, 4, 5, 6, 7, -8, -7,
			-6, -5, -4, -3, -2, -1
		};

		internal static sbyte GetHighNibbleSigned(byte value)
		{
			return SignedNibbles[(value >> 4) & 0xF];
		}

		internal static sbyte GetLowNibbleSigned(byte value)
		{
			return SignedNibbles[value & 0xF];
		}

		internal static short Clamp(short val, short min, short max)
		{
			return Math.Max(Math.Min(val, max), min);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static short ClampToShort(int value)
		{
			if (value < -32768)
			{
				return short.MinValue;
			}
			if (value > 32767)
			{
				return short.MaxValue;
			}
			return (short)value;
		}
	}
}
namespace Fmod5Sharp.FmodTypes
{
	public class FmodAudioHeader
	{
		private static readonly object ChunkReadingLock = new object();

		internal readonly bool IsValid;

		public readonly FmodAudioType AudioType;

		public readonly uint Version;

		public readonly uint NumSamples;

		internal readonly uint SizeOfThisHeader;

		internal readonly uint SizeOfSampleHeaders;

		internal readonly uint SizeOfNameTable;

		internal readonly uint SizeOfData;

		internal readonly List<FmodSampleMetadata> Samples = new List<FmodSampleMetadata>();

		public FmodAudioHeader(BinaryReader reader)
		{
			if (reader.ReadString(4) != "FSB5")
			{
				IsValid = false;
				return;
			}
			Version = reader.ReadUInt32();
			NumSamples = reader.ReadUInt32();
			SizeOfSampleHeaders = reader.ReadUInt32();
			SizeOfNameTable = reader.ReadUInt32();
			SizeOfData = reader.ReadUInt32();
			AudioType = (FmodAudioType)reader.ReadUInt32();
			reader.ReadUInt32();
			if (Version == 0)
			{
				SizeOfThisHeader = 64u;
				reader.ReadUInt32();
			}
			else
			{
				SizeOfThisHeader = 60u;
			}
			reader.ReadUInt32();
			reader.ReadUInt64();
			reader.ReadUInt64();
			reader.ReadUInt64();
			reader.Position();
			for (int i = 0; i < NumSamples; i++)
			{
				FmodSampleMetadata fmodSampleMetadata = reader.ReadEndian<FmodSampleMetadata>();
				if (!fmodSampleMetadata.HasAnyChunks)
				{
					Samples.Add(fmodSampleMetadata);
					continue;
				}
				lock (ChunkReadingLock)
				{
					List<FmodSampleChunk> list = new List<FmodSampleChunk>();
					FmodSampleChunk.CurrentSample = fmodSampleMetadata;
					FmodSampleChunk fmodSampleChunk;
					do
					{
						fmodSampleChunk = reader.ReadEndian<FmodSampleChunk>();
						list.Add(fmodSampleChunk);
					}
					while (fmodSampleChunk.MoreChunks);
					FmodSampleChunk.CurrentSample = null;
					FmodSampleChunk fmodSampleChunk2 = list.FirstOrDefault((FmodSampleChunk c) => c.ChunkType == FmodSampleChunkType.FREQUENCY);
					if (fmodSampleChunk2 != null && fmodSampleChunk2.ChunkData is FrequencyChunkData frequencyChunkData)
					{
						fmodSampleMetadata.FrequencyId = frequencyChunkData.ActualFrequencyId;
					}
					fmodSampleMetadata.Chunks = list;
					Samples.Add(fmodSampleMetadata);
				}
			}
			IsValid = true;
		}
	}
	public enum FmodAudioType : uint
	{
		NONE,
		PCM8,
		PCM16,
		PCM24,
		PCM32,
		PCMFLOAT,
		GCADPCM,
		IMAADPCM,
		VAG,
		HEVAG,
		XMA,
		MPEG,
		CELT,
		AT9,
		XWMA,
		VORBIS,
		FADPCM,
		OPUS
	}
	public class FmodSample
	{
		public FmodSampleMetadata Metadata;

		public byte[] SampleBytes;

		public string? Name;

		internal FmodSoundBank? MyBank;

		public FmodSample(FmodSampleMetadata metadata, byte[] sampleBytes)
		{
			Metadata = metadata;
			SampleBytes = sampleBytes;
		}

		public bool RebuildAsStandardFileFormat([NotNullWhen(true)] out byte[]? data, [NotNullWhen(true)] out string? fileExtension)
		{
			switch (MyBank.Header.AudioType)
			{
			case FmodAudioType.VORBIS:
				data = FmodVorbisRebuilder.RebuildOggFile(this);
				fileExtension = "ogg";
				return data.Length != 0;
			case FmodAudioType.PCM8:
			case FmodAudioType.PCM16:
			case FmodAudioType.PCM32:
				data = FmodPcmRebuilder.Rebuild(this, MyBank.Header.AudioType);
				fileExtension = "wav";
				return data.Length != 0;
			case FmodAudioType.GCADPCM:
				data = FmodGcadPcmRebuilder.Rebuild(this);
				fileExtension = "wav";
				return data.Length != 0;
			case FmodAudioType.IMAADPCM:
				data = FmodImaAdPcmRebuilder.Rebuild(this);
				fileExtension = "wav";
				return data.Length != 0;
			case FmodAudioType.FADPCM:
				data = FmodFadPcmRebuilder.Rebuild(this);
				fileExtension = "wav";
				return data.Length != 0;
			default:
				data = null;
				fileExtension = null;
				return false;
			}
		}
	}
	internal class FmodSampleChunk : IBinaryReadable
	{
		internal static FmodSampleMetadata? CurrentSample;

		public FmodSampleChunkType ChunkType;

		public uint ChunkSize;

		public bool MoreChunks;

		internal IChunkData ChunkData;

		void IBinaryReadable.Read(BinaryReader reader)
		{
			uint raw = reader.ReadUInt32();
			MoreChunks = raw.Bits(0, 1) == 1;
			ChunkSize = (uint)raw.Bits(1, 24);
			ChunkType = (FmodSampleChunkType)raw.Bits(25, 7);
			ChunkData = ChunkType switch
			{
				FmodSampleChunkType.VORBISDATA => new VorbisChunkData(), 
				FmodSampleChunkType.FREQUENCY => new FrequencyChunkData(), 
				FmodSampleChunkType.CHANNELS => new ChannelChunkData(), 
				FmodSampleChunkType.LOOP => new LoopChunkData(), 
				FmodSampleChunkType.DSPCOEFF => new DspCoefficientsBlockData(CurrentSample), 
				_ => new UnknownChunkData(), 
			};
			long num = reader.Position();
			ChunkData.Read(reader, ChunkSize);
			long num2 = reader.Position() - num;
			if (num2 != ChunkSize)
			{
				throw new Exception($"Expected fmod sample chunk to read {ChunkSize} bytes, but it only read {num2}");
			}
		}
	}
	internal enum FmodSampleChunkType : uint
	{
		CHANNELS = 1u,
		FREQUENCY = 2u,
		LOOP = 3u,
		COMMENT = 4u,
		XMASEEK = 6u,
		DSPCOEFF = 7u,
		ATRAC9CFG = 9u,
		XWMADATA = 10u,
		VORBISDATA = 11u,
		PEAKVOLUME = 13u,
		VORBISINTRALAYERS = 14u,
		OPUSDATALEN = 15u
	}
	public class FmodSampleMetadata : IBinaryReadable
	{
		internal bool HasAnyChunks;

		internal uint FrequencyId;

		internal uint DataOffset;

		internal List<FmodSampleChunk> Chunks = new List<FmodSampleChunk>();

		internal int NumChannels;

		public bool IsStereo;

		public uint SampleCount;

		public int Frequency
		{
			get
			{
				if (!FsbLoader.Frequencies.TryGetValue(FrequencyId, out var value))
				{
					return (int)FrequencyId;
				}
				return value;
			}
		}

		public uint Channels => (uint)NumChannels;

		void IBinaryReadable.Read(BinaryReader reader)
		{
			ulong num = reader.ReadUInt64();
			HasAnyChunks = (num & 1) == 1;
			FrequencyId = (uint)num.Bits(1, 4);
			NumChannels = (int)num.Bits(5, 2) switch
			{
				0 => 1, 
				1 => 2, 
				2 => 6, 
				3 => 8, 
				_ => 0, 
			};
			IsStereo = NumChannels == 2;
			DataOffset = (uint)((int)num.Bits(7, 27) * 32);
			SampleCount = (uint)num.Bits(34, 30);
		}
	}
	public class FmodSoundBank
	{
		public FmodAudioHeader Header;

		public List<FmodSample> Samples;

		internal FmodSoundBank(FmodAudioHeader header, List<FmodSample> samples)
		{
			Header = header;
			Samples = samples;
			Samples.ForEach(delegate(FmodSample s)
			{
				s.MyBank = this;
			});
		}
	}
}
namespace Fmod5Sharp.CodecRebuilders
{
	public class FmodFadPcmRebuilder
	{
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

		public static short[] DecodeFadpcm(FmodSample sample)
		{
			ReadOnlySpan<byte> readOnlySpan = sample.SampleBytes;
			int numChannels = sample.Metadata.NumChannels;
			int num = readOnlySpan.Length / 140;
			short[] array = new short[num * 256];
			Span<short> span = array;
			int[] array2 = new int[numChannels];
			int[] array3 = new int[numChannels];
			for (int i = 0; i < num; i++)
			{
				int num2 = i % numChannels;
				int start = i * 140;
				ReadOnlySpan<byte> readOnlySpan2 = readOnlySpan.Slice(start, 140);
				uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan2.Slice(0, 4));
				uint num4 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan2.Slice(4, readOnlySpan2.Length - 4));
				array2[num2] = BinaryPrimitives.ReadInt16LittleEndian(readOnlySpan2.Slice(8, readOnlySpan2.Length - 8));
				array3[num2] = BinaryPrimitives.ReadInt16LittleEndian(readOnlySpan2.Slice(10, readOnlySpan2.Length - 10));
				int num5 = i / numChannels * 256 * numChannels + num2;
				for (int j = 0; j < 8; j++)
				{
					int num6 = (int)((num3 >> j * 4) & 0xF) % 7;
					int num7 = (int)((num4 >> j * 4) & 0xF);
					int num8 = FadpcmCoefs[num6, 0];
					int num9 = FadpcmCoefs[num6, 1];
					int num10 = 22 - num7;
					for (int k = 0; k < 4; k++)
					{
						int num11 = 12 + 16 * j + 4 * k;
						uint num12 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan2.Slice(num11, readOnlySpan2.Length - num11));
						for (int l = 0; l < 8; l++)
						{
							short num13 = Utils.ClampToShort(((int)(((num12 >> l * 4) & 0xF) << 28) >> num10) - array3[num2] * num9 + array2[num2] * num8 >> 6);
							int num14 = num5 + (j * 32 + k * 8 + l) * numChannels;
							if (num14 < span.Length)
							{
								span[num14] = num13;
							}
							array3[num2] = array2[num2];
							array2[num2] = num13;
						}
					}
				}
			}
			return array;
		}

		public static byte[] Rebuild(FmodSample sample)
		{
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0026: Unknown result type (might be due to invalid IL or missing references)
			//IL_002c: Expected O, but got Unknown
			WaveFormat val = new WaveFormat(sample.Metadata.Frequency, 16, sample.Metadata.NumChannels);
			using MemoryStream memoryStream = new MemoryStream();
			WaveFileWriter val2 = new WaveFileWriter((Stream)memoryStream, val);
			try
			{
				short[] array = DecodeFadpcm(sample);
				val2.WriteSamples(array, 0, array.Length);
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
			return memoryStream.ToArray();
		}
	}
	public static class FmodGcadPcmRebuilder
	{
		private const int BytesPerFrame = 8;

		private const int SamplesPerFrame = 14;

		private const int NibblesPerFrame = 16;

		private static short[] GetPcmData(FmodSample sample)
		{
			int num = ByteCountToSampleCount(sample.SampleBytes.Length);
			double num2 = Math.Ceiling((double)num / 14.0);
			short[] array = new short[num];
			byte[] sampleBytes = sample.SampleBytes;
			List<short> list = ((DspCoefficientsBlockData)sample.Metadata.Chunks.First((FmodSampleChunk c) => c.ChunkType == FmodSampleChunkType.DSPCOEFF).ChunkData).ChannelData[0];
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			short num6 = 0;
			short num7 = 0;
			for (int num8 = 0; (double)num8 < num2; num8++)
			{
				byte b = sampleBytes[num5++];
				int num9 = 1 << (b & 0xF);
				int num10 = b >> 4;
				short num11 = list[num10 * 2];
				short num12 = list[num10 * 2 + 1];
				int num13 = Math.Min(14, num - num3);
				for (int num14 = 0; num14 < num13; num14++)
				{
					short num15 = Clamp16((((num14 % 2 == 0) ? Utils.GetHighNibbleSigned(sampleBytes[num5]) : Utils.GetLowNibbleSigned(sampleBytes[num5++])) * num9 << 11) + 1024 + num11 * num6 + num12 * num7 >> 11);
					num7 = num6;
					num6 = num15;
					array[num4++] = num15;
					num3++;
				}
			}
			return array;
		}

		public static byte[] Rebuild(FmodSample sample)
		{
			//IL_0041: Unknown result type (might be due to invalid IL or missing references)
			//IL_0047: Expected O, but got Unknown
			int num = ((!sample.Metadata.IsStereo) ? 1 : 2);
			WaveFormat val = WaveFormat.CreateCustomFormat((WaveFormatEncoding)1, sample.Metadata.Frequency, num, sample.Metadata.Frequency * num * 2, num * 2, 16);
			using MemoryStream memoryStream = new MemoryStream();
			WaveFileWriter val2 = new WaveFileWriter((Stream)memoryStream, val);
			try
			{
				short[] pcmData = GetPcmData(sample);
				val2.WriteSamples(pcmData, 0, pcmData.Length);
				return memoryStream.ToArray();
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
		}

		private static int NibbleCountToSampleCount(int nibbleCount)
		{
			int num = nibbleCount / 16;
			int num2 = nibbleCount % 16;
			int num3 = ((num2 >= 2) ? (num2 - 2) : 0);
			return 14 * num + num3;
		}

		private static int ByteCountToSampleCount(int byteCount)
		{
			return NibbleCountToSampleCount(byteCount * 2);
		}

		private static short Clamp16(int value)
		{
			if (value > 32767)
			{
				return short.MaxValue;
			}
			if (value < -32768)
			{
				return short.MinValue;
			}
			return (short)value;
		}
	}
	public static class FmodImaAdPcmRebuilder
	{
		public const int SamplesPerFramePerChannel = 64;

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
			-1, -1, -1, -1, 2, 4, 6, 8, -1, -1,
			-1, -1, 2, 4, 6, 8
		};

		private static void ExpandNibble(MemoryStream stream, long byteOffset, int nibbleShift, ref int hist, ref int stepIndex)
		{
			stream.Seek(byteOffset, SeekOrigin.Begin);
			int num = (stream.ReadByte() >> nibbleShift) & 0xF;
			int num2 = hist;
			int num3 = ADPCMTable[stepIndex];
			int num4 = num3 >> 3;
			if ((num & 1) != 0)
			{
				num4 += num3 >> 2;
			}
			if ((num & 2) != 0)
			{
				num4 += num3 >> 1;
			}
			if ((num & 4) != 0)
			{
				num4 += num3;
			}
			if ((num & 8) != 0)
			{
				num4 = -num4;
			}
			num2 += num4;
			hist = Utils.Clamp((short)num2, short.MinValue, short.MaxValue);
			stepIndex += IMA_IndexTable[num];
			stepIndex = Utils.Clamp((short)stepIndex, 0, 88);
		}

		private static short[] DecodeSamplesFsbIma(FmodSample sample)
		{
			int channels = (int)sample.Metadata.Channels;
			using MemoryStream memoryStream = new MemoryStream(sample.SampleBytes);
			using BinaryReader binaryReader = new BinaryReader(memoryStream);
			short[] array = new short[sample.Metadata.SampleCount * 2];
			int num = (int)sample.Metadata.SampleCount / 64;
			for (int i = 0; i < channels; i++)
			{
				int num2 = i;
				for (int j = 0; j < num; j++)
				{
					int num3 = 36 * channels * j;
					int num4 = num3 + 4 * i;
					memoryStream.Seek(num4, SeekOrigin.Begin);
					int hist = binaryReader.ReadInt16();
					memoryStream.Seek(num4 + 2, SeekOrigin.Begin);
					int num5 = binaryReader.ReadByte();
					num5 = Utils.Clamp((short)num5, 0, 88);
					array[num2] = (short)hist;
					num2 += channels;
					for (int k = 1; k <= 64; k++)
					{
						int num6 = num3 + 8 + 4 * (i % 2) + 8 * ((k - 1) / 8) + (k - 1) % 8 / 2;
						if (channels == 0)
						{
							num6 = num3 + 4 + (k - 1) / 2;
						}
						int nibbleShift = ((((k - 1) & 1) != 0) ? 4 : 0);
						if (k < 64)
						{
							ExpandNibble(memoryStream, num6, nibbleShift, ref hist, ref num5);
							array[num2] = (short)hist;
							num2 += channels;
						}
					}
				}
			}
			return array;
		}

		private static short[] DecodeSamplesXboxIma(FmodSample sample)
		{
			using MemoryStream memoryStream = new MemoryStream(sample.SampleBytes);
			using BinaryReader binaryReader = new BinaryReader(memoryStream);
			int num = (int)sample.Metadata.SampleCount / 64;
			short[] array = new short[sample.Metadata.SampleCount];
			int num2 = 0;
			for (int i = 0; i < num; i++)
			{
				int num3 = 36 * i;
				memoryStream.Seek(num3, SeekOrigin.Begin);
				int hist = binaryReader.ReadInt16();
				memoryStream.Seek(num3 + 2, SeekOrigin.Begin);
				int num4 = binaryReader.ReadByte();
				num4 = Utils.Clamp((short)num4, 0, 88);
				array[num2] = (short)hist;
				num2++;
				for (int j = 1; j <= 64; j++)
				{
					int num5 = num3 + 4 + (j - 1) / 2;
					int nibbleShift = ((((j - 1) & 1) != 0) ? 4 : 0);
					if (j < 64)
					{
						ExpandNibble(memoryStream, num5, nibbleShift, ref hist, ref num4);
						array[num2] = (short)hist;
						num2++;
					}
				}
			}
			return array;
		}

		public static byte[] Rebuild(FmodSample sample)
		{
			//IL_0041: Unknown result type (might be due to invalid IL or missing references)
			//IL_0047: Expected O, but got Unknown
			int num = ((!sample.Metadata.IsStereo) ? 1 : 2);
			WaveFormat val = WaveFormat.CreateCustomFormat((WaveFormatEncoding)1, sample.Metadata.Frequency, num, sample.Metadata.Frequency * num * 2, num * 2, 16);
			using MemoryStream memoryStream = new MemoryStream();
			WaveFileWriter val2 = new WaveFileWriter((Stream)memoryStream, val);
			try
			{
				short[] array = ((num == 1) ? DecodeSamplesXboxIma(sample) : DecodeSamplesFsbIma(sample));
				val2.WriteSamples(array, 0, array.Length);
				return memoryStream.ToArray();
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
		}
	}
	public static class FmodPcmRebuilder
	{
		public static byte[] Rebuild(FmodSample sample, FmodAudioType type)
		{
			//IL_0099: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a0: Expected O, but got Unknown
			int num = type switch
			{
				FmodAudioType.PCM8 => 1, 
				FmodAudioType.PCM16 => 2, 
				FmodAudioType.PCM32 => 4, 
				_ => throw new Exception($"FmodPcmRebuilder does not support encoding of type {type}"), 
			};
			int num2 = ((!sample.Metadata.IsStereo) ? 1 : 2);
			WaveFormat val = WaveFormat.CreateCustomFormat((WaveFormatEncoding)1, sample.Metadata.Frequency, num2, sample.Metadata.Frequency * num2 * num, num2 * num, num * 8);
			using MemoryStream memoryStream = new MemoryStream();
			WaveFileWriter val2 = new WaveFileWriter((Stream)memoryStream, val);
			try
			{
				((Stream)(object)val2).Write(sample.SampleBytes, 0, num2 * num * (int)sample.Metadata.SampleCount);
				return memoryStream.GetBuffer();
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
		}
	}
	public static class FmodVorbisRebuilder
	{
		private static Dictionary<uint, FmodVorbisData>? headers;

		private static void LoadVorbisHeaders()
		{
			using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Fmod5Sharp.Util.vorbis_headers_converted.json") ?? throw new Exception("Embedded resources for vorbis header data not found, has the assembly been tampered with?");
			using StreamReader streamReader = new StreamReader(stream);
			headers = JsonSerializer.Deserialize(streamReader.ReadToEnd(), Fmod5SharpJsonContext.Default.DictionaryUInt32FmodVorbisData);
		}

		public static byte[] RebuildOggFile(FmodSample sample)
		{
			//IL_009c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a3: Expected O, but got Unknown
			//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ab: Expected O, but got Unknown
			uint crc = ((VorbisChunkData)(sample.Metadata.Chunks.FirstOrDefault((FmodSampleChunk f) => f.ChunkType == FmodSampleChunkType.VORBISDATA) ?? throw new Exception("Rebuilding Vorbis data requires a VORBISDATA chunk, which wasn't found")).ChunkData).Crc32;
			if (headers == null)
			{
				LoadVorbisHeaders();
			}
			FmodVorbisData fmodVorbisData = headers[crc];
			fmodVorbisData.InitBlockFlags();
			OggPacket val = BuildInfoPacket((byte)sample.Metadata.Channels, sample.Metadata.Frequency);
			OggPacket val2 = BuildCommentPacket("Fmod5Sharp (Samboy063)");
			OggPacket val3 = new OggPacket(fmodVorbisData.HeaderBytes, false, 0, 2);
			OggStream val4 = new OggStream(1);
			using MemoryStream memoryStream = new MemoryStream();
			val4.PacketIn(val);
			val4.PacketIn(val2);
			val4.PacketIn(val3);
			val4.FlushAndCopyTo(memoryStream, force: true);
			CopySampleData(fmodVorbisData, sample.Metadata.SampleCount, sample.SampleBytes, val4, memoryStream);
			return memoryStream.ToArray();
		}

		private static void FlushAndCopyTo(this OggStream stream, Stream other, bool force = false)
		{
			OggPage val = default(OggPage);
			while (stream.PageOut(ref val, force))
			{
				other.Write(val.Header, 0, val.Header.Length);
				other.Write(val.Body, 0, val.Body.Length);
			}
		}

		private static OggPacket BuildInfoPacket(byte channels, int frequency)
		{
			//IL_0070: Unknown result type (might be due to invalid IL or missing references)
			//IL_0076: Expected O, but got Unknown
			using MemoryStream memoryStream = new MemoryStream(30);
			using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
			binaryWriter.Write((byte)1);
			binaryWriter.Write(Encoding.UTF8.GetBytes("vorbis"));
			binaryWriter.Write(0);
			binaryWriter.Write(channels);
			binaryWriter.Write(frequency);
			binaryWriter.Write(0);
			binaryWriter.Write(0);
			binaryWriter.Write(0);
			binaryWriter.Write((byte)184);
			binaryWriter.Write((byte)1);
			return new OggPacket(memoryStream.ToArray(), false, 0, 0);
		}

		private static OggPacket BuildCommentPacket(string vendor)
		{
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_007a: Expected O, but got Unknown
			using MemoryStream input = new MemoryStream();
			using (new BinaryReader(input))
			{
				using MemoryStream memoryStream = new MemoryStream();
				using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
				binaryWriter.Seek(0, SeekOrigin.Begin);
				binaryWriter.Write((byte)3);
				binaryWriter.Write(Encoding.UTF8.GetBytes("vorbis"));
				binaryWriter.Write(vendor.Length);
				binaryWriter.Write(Encoding.UTF8.GetBytes(vendor));
				binaryWriter.Write(0);
				binaryWriter.Write((byte)1);
				return new OggPacket(memoryStream.ToArray(), false, 0, 1);
			}
		}

		private static void CopySampleData(FmodVorbisData vorbisData, uint sampleCount, byte[] sampleBytes, OggStream oggStream, Stream outputStream)
		{
			//IL_0089: Unknown result type (might be due to invalid IL or missing references)
			//IL_0093: Expected O, but got Unknown
			using MemoryStream input = new MemoryStream(sampleBytes);
			using BinaryReader inputReader = new BinaryReader(input);
			ReadSamplePackets(inputReader, out List<ushort> packetLengths, out List<byte[]> packets);
			int num = 1;
			uint num2 = 0u;
			uint num3 = 0u;
			int num4 = packetLengths.Count - 1;
			for (int i = 0; i < packets.Count; i++)
			{
				bool flag = i == num4;
				byte[] array = packets[i];
				num++;
				uint num5 = ((packetLengths[i] != 0) ? vorbisData.GetPacketBlockSize(array) : 0u);
				num2 = ((num3 != 0) ? (num2 + (num5 + num3) / 4) : 0u);
				if (num2 > sampleCount)
				{
					num2 = sampleCount;
				}
				num3 = num5;
				oggStream.PacketIn(new OggPacket(array, flag, (int)num2, num));
				oggStream.FlushAndCopyTo(outputStream, flag);
				if (num2 == sampleCount)
				{
					break;
				}
			}
		}

		private static void ReadSamplePackets(BinaryReader inputReader, out List<ushort> packetLengths, out List<byte[]> packets)
		{
			packetLengths = new List<ushort>();
			packets = new List<byte[]>();
			while (inputReader.BaseStream.Position + 2 < inputReader.BaseStream.Length)
			{
				ushort num = inputReader.ReadUInt16();
				if (num != 0 && num != ushort.MaxValue)
				{
					packetLengths.Add(num);
					packets.Add(inputReader.ReadBytes(num));
					continue;
				}
				break;
			}
		}
	}
}
namespace Fmod5Sharp.ChunkData
{
	internal class ChannelChunkData : IChunkData
	{
		public byte NumChannels;

		public void Read(BinaryReader reader, uint expectedSize)
		{
			NumChannels = reader.ReadByte();
		}
	}
	public class DspCoefficientsBlockData : IChunkData
	{
		public List<short>[] ChannelData;

		private readonly FmodSampleMetadata _sampleMetadata;

		public DspCoefficientsBlockData(FmodSampleMetadata sampleMetadata)
		{
			_sampleMetadata = sampleMetadata;
			ChannelData = new List<short>[_sampleMetadata.Channels];
			for (int i = 0; i < _sampleMetadata.Channels; i++)
			{
				ChannelData[i] = new List<short>();
			}
		}

		public void Read(BinaryReader reader, uint expectedSize)
		{
			for (int i = 0; i < _sampleMetadata.Channels; i++)
			{
				for (int j = 0; j < 16; j++)
				{
					ChannelData[i].Add(BitConverter.ToInt16(Enumerable.Reverse(reader.ReadBytes(2)).ToArray(), 0));
				}
				reader.ReadInt64();
				reader.ReadInt32();
				reader.ReadInt16();
			}
		}
	}
	internal class FrequencyChunkData : IChunkData
	{
		public uint ActualFrequencyId;

		public void Read(BinaryReader reader, uint expectedSize)
		{
			ActualFrequencyId = reader.ReadUInt32();
		}
	}
	internal interface IChunkData
	{
		void Read(BinaryReader reader, uint expectedSize);
	}
	internal class LoopChunkData : IChunkData
	{
		public uint LoopStart;

		public uint LoopEnd;

		public void Read(BinaryReader reader, uint expectedSize)
		{
			LoopStart = reader.ReadUInt32();
			LoopEnd = reader.ReadUInt32();
		}
	}
	internal class UnknownChunkData : IChunkData
	{
		public byte[] UnknownData = new byte[0];

		public void Read(BinaryReader reader, uint expectedSize)
		{
			UnknownData = reader.ReadBytes((int)expectedSize);
		}
	}
	internal class VorbisChunkData : IChunkData
	{
		public uint Crc32;

		public void Read(BinaryReader reader, uint expectedSize)
		{
			Crc32 = reader.ReadUInt32();
			reader.ReadBytes((int)(expectedSize - 4));
		}
	}
}
namespace BitStreams
{
	[Serializable]
	internal struct Bit
	{
		private readonly byte value;

		private Bit(int value)
		{
			this.value = (byte)(value & 1);
		}

		public static implicit operator Bit(int value)
		{
			return new Bit(value);
		}

		public static implicit operator Bit(bool value)
		{
			return new Bit(value ? 1 : 0);
		}

		public static implicit operator int(Bit bit)
		{
			return bit.value;
		}

		public static implicit operator byte(Bit bit)
		{
			return bit.value;
		}

		public static implicit operator bool(Bit bit)
		{
			return bit.value == 1;
		}

		public static Bit operator &(Bit x, Bit y)
		{
			return x.value & y.value;
		}

		public static Bit operator |(Bit x, Bit y)
		{
			return x.value | y.value;
		}

		public static Bit operator ^(Bit x, Bit y)
		{
			return x.value ^ y.value;
		}

		public static Bit operator ~(Bit bit)
		{
			return ~bit.value & 1;
		}

		public static implicit operator string(Bit bit)
		{
			return bit.value.ToString();
		}

		public int AsInt()
		{
			return value;
		}

		public bool AsBool()
		{
			return value == 1;
		}
	}
	internal static class BitExtensions
	{
		public static Bit GetBit(this byte n, int index)
		{
			return n >> index;
		}

		public static Bit GetBit(this sbyte n, int index)
		{
			return n >> index;
		}

		public static Bit GetBit(this short n, int index)
		{
			return n >> index;
		}

		public static Bit GetBit(this ushort n, int index)
		{
			return n >> index;
		}

		public static Bit GetBit(this int n, int index)
		{
			return n >> index;
		}

		public static Bit GetBit(this uint n, int index)
		{
			return (byte)(n >> index);
		}

		public static Bit GetBit(this long n, int index)
		{
			return (byte)(n >> index);
		}

		public static Bit GetBit(this ulong n, int index)
		{
			return (byte)(n >> index);
		}

		public static byte CircularShift(this byte n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((byte)((n >> bits) | (n << 8 - bits))) : ((byte)((n << bits) | (n >> 8 - bits))));
			return n;
		}

		public static sbyte CircularShift(this sbyte n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((sbyte)((n >> bits) | (n << 8 - bits))) : ((sbyte)((n << bits) | (n >> 8 - bits))));
			return n;
		}

		public static short CircularShift(this short n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((short)((n >> bits) | (n << 16 - bits))) : ((short)((n << bits) | (n >> 16 - bits))));
			return n;
		}

		public static ushort CircularShift(this ushort n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((ushort)((n >> bits) | (n << 16 - bits))) : ((ushort)((n << bits) | (n >> 16 - bits))));
			return n;
		}

		public static int CircularShift(this int n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((n >> bits) | (n << 32 - bits)) : ((n << bits) | (n >> 32 - bits)));
			return n;
		}

		public static uint CircularShift(this uint n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((n >> bits) | (n << 32 - bits)) : ((n << bits) | (n >> 32 - bits)));
			return n;
		}

		public static long CircularShift(this long n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((n >> bits) | (n << 64 - bits)) : ((n << bits) | (n >> 64 - bits)));
			return n;
		}

		public static ulong CircularShift(this ulong n, int bits, bool leftShift)
		{
			n = ((!leftShift) ? ((n >> bits) | (n << 64 - bits)) : ((n << bits) | (n >> 64 - bits)));
			return n;
		}

		public static byte ReverseBits(this byte b)
		{
			return (byte)(((b & 1) << 7) + (((b >> 1) & 1) << 6) + (((b >> 2) & 1) << 5) + (((b >> 3) & 1) << 4) + (((b >> 4) & 1) << 3) + (((b >> 5) & 1) << 2) + (((b >> 6) & 1) << 1) + ((b >> 7) & 1));
		}
	}
	internal class BitStream
	{
		private Stream stream;

		private Encoding encoding;

		private long offset { get; set; }

		private int bit { get; set; }

		private bool MSB { get; set; }

		public bool AutoIncreaseStream { get; set; }

		public long Length => stream.Length;

		public long BitPosition => bit;

		private bool ValidPosition => offset < Length;

		public bool this[long offset, int bit]
		{
			get
			{
				Seek(offset, bit);
				return ValidPosition;
			}
			private set
			{
			}
		}

		public BitStream(Stream stream, bool MSB = false)
		{
			this.stream = new MemoryStream();
			stream.CopyTo(this.stream);
			this.MSB = MSB;
			offset = 0L;
			bit = 0;
			encoding = Encoding.UTF8;
			AutoIncreaseStream = false;
		}

		public BitStream(Stream stream, Encoding encoding, bool MSB = false)
		{
			this.stream = new MemoryStream();
			stream.CopyTo(this.stream);
			this.MSB = MSB;
			offset = 0L;
			bit = 0;
			this.encoding = encoding;
			AutoIncreaseStream = false;
		}

		public BitStream(byte[] buffer, bool MSB = false)
		{
			stream = new MemoryStream();
			new MemoryStream(buffer).CopyTo(stream);
			this.MSB = MSB;
			offset = 0L;
			bit = 0;
			encoding = Encoding.UTF8;
			AutoIncreaseStream = false;
		}

		public BitStream(byte[] buffer, Encoding encoding, bool MSB = false)
		{
			stream = new MemoryStream();
			new MemoryStream(buffer).CopyTo(stream);
			this.MSB = MSB;
			offset = 0L;
			bit = 0;
			this.encoding = encoding;
			AutoIncreaseStream = false;
		}

		public static BitStream Create(byte[] buffer, bool MSB = false)
		{
			return new BitStream(buffer, MSB);
		}

		public static BitStream Create(byte[] buffer, Encoding encoding, bool MSB = false)
		{
			return new BitStream(buffer, encoding, MSB);
		}

		public static BitStream CreateFromFile(string path, Encoding? encoding = null)
		{
			if (!File.Exists(path))
			{
				throw new IOException("File doesn't exists!");
			}
			if (File.GetAttributes(path) == FileAttributes.Directory)
			{
				throw new IOException("Path is a directory!");
			}
			if (encoding == null)
			{
				encoding = Encoding.UTF8;
			}
			return new BitStream(File.ReadAllBytes(path), encoding);
		}

		public void Seek(long offset, int bit)
		{
			if (offset > Length)
			{
				this.offset = Length;
			}
			else if (offset >= 0)
			{
				this.offset = offset;
			}
			else
			{
				offset = 0L;
			}
			if (bit >= 8)
			{
				int num = bit / 8;
				this.offset += num;
				this.bit = bit % 8;
			}
			else
			{
				this.bit = bit;
			}
			stream.Seek(offset, SeekOrigin.Begin);
		}

		public void AdvanceBit()
		{
			bit = (bit + 1) % 8;
			if (bit == 0)
			{
				offset++;
			}
		}

		public void ReturnBit()
		{
			bit = ((bit - 1 == -1) ? 7 : (bit - 1));
			if (bit == 7)
			{
				offset--;
			}
			if (offset < 0)
			{
				offset = 0L;
			}
		}

		public Stream GetStream()
		{
			return stream;
		}

		public byte[] GetStreamData()
		{
			stream.Seek(0L, SeekOrigin.Begin);
			MemoryStream memoryStream = new MemoryStream();
			stream.CopyTo(memoryStream);
			Seek(offset, bit);
			return memoryStream.ToArray();
		}

		public Encoding GetEncoding()
		{
			return encoding;
		}

		public void SetEncoding(Encoding encoding)
		{
			this.encoding = encoding;
		}

		public bool ChangeLength(long length)
		{
			if (stream.CanSeek && stream.CanWrite)
			{
				stream.SetLength(length);
				return true;
			}
			return false;
		}

		public void CutStream(long offset, long length)
		{
			byte[] streamData = GetStreamData();
			byte[] array = new byte[length];
			Array.Copy(streamData, offset, array, 0L, length);
			stream = new MemoryStream();
			MemoryStream memoryStream = new MemoryStream(array);
			stream = new MemoryStream();
			memoryStream.CopyTo(stream);
			this.offset = 0L;
			bit = 0;
		}

		public void CopyStreamTo(Stream stream)
		{
			Seek(0L, 0);
			stream.SetLength(this.stream.Length);
			this.stream.CopyTo(stream);
		}

		public void CopyStreamTo(BitStream stream)
		{
			Seek(0L, 0);
			stream.ChangeLength(this.stream.Length);
			this.stream.CopyTo(stream.stream);
			stream.Seek(0L, 0);
		}

		public void SaveStreamAsFile(string filename)
		{
			File.WriteAllBytes(filename, GetStreamData());
		}

		public MemoryStream CloneAsMemoryStream()
		{
			return new MemoryStream(GetStreamData());
		}

		public BufferedStream CloneAsBufferedStream()
		{
			BufferedStream bufferedStream = new BufferedStream(stream);
			new StreamWriter(bufferedStream).Write(GetStreamData());
			bufferedStream.Seek(0L, SeekOrigin.Begin);
			return bufferedStream;
		}

		private bool ValidPositionWhen(int bits)
		{
			long num = offset;
			if ((bit + 1) % 8 == 0)
			{
				num++;
			}
			return num < Length;
		}

		public Bit ReadBit()
		{
			if (!ValidPosition)
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			stream.Seek(offset, SeekOrigin.Begin);
			byte b = (MSB ? ((byte)((stream.ReadByte() >> 7 - bit) & 1)) : ((byte)((stream.ReadByte() >> bit) & 1)));
			AdvanceBit();
			stream.Seek(offset, SeekOrigin.Begin);
			return b;
		}

		public Bit[] ReadBits(int length)
		{
			Bit[] array = new Bit[length];
			for (int i = 0; i < length; i++)
			{
				array[i] = ReadBit();
			}
			return array;
		}

		public void WriteBit(Bit data)
		{
			stream.Seek(offset, SeekOrigin.Begin);
			byte b = (byte)stream.ReadByte();
			stream.Seek(offset, SeekOrigin.Begin);
			if (!MSB)
			{
				b &= (byte)(~(1 << bit));
				b |= (byte)((int)data << bit);
			}
			else
			{
				b &= (byte)(~(1 << 7 - bit));
				b |= (byte)((int)data << 7 - bit);
			}
			if (ValidPosition)
			{
				stream.WriteByte(b);
			}
			else
			{
				if (!AutoIncreaseStream)
				{
					throw new IOException("Cannot write in an offset bigger than the length of the stream");
				}
				if (!ChangeLength(Length + (offset - Length) + 1))
				{
					throw new IOException("Cannot write in an offset bigger than the length of the stream");
				}
				stream.WriteByte(b);
			}
			AdvanceBit();
			stream.Seek(offset, SeekOrigin.Begin);
		}

		public void WriteBits(ICollection<Bit> bits)
		{
			foreach (Bit bit in bits)
			{
				WriteBit(bit);
			}
		}

		public void WriteBits(ICollection<Bit> bits, int length)
		{
			Bit[] array = new Bit[bits.Count];
			bits.CopyTo(array, 0);
			for (int i = 0; i < length; i++)
			{
				WriteBit(array[i]);
			}
		}

		public void WriteBits(Bit[] bits, int offset, int length)
		{
			for (int i = offset; i < length; i++)
			{
				WriteBit(bits[i]);
			}
		}

		public byte[] ReadBytes(long length, bool isBytes = false)
		{
			if (isBytes)
			{
				length *= 8;
			}
			List<byte> list = new List<byte>();
			long num = 0L;
			while (num < length)
			{
				byte b = 0;
				for (int i = 0; i < 8; i++)
				{
					if (num >= length)
					{
						break;
					}
					b = (MSB ? ((byte)(b | (byte)((int)ReadBit() << 7 - i))) : ((byte)(b | (byte)((int)ReadBit() << i))));
					num++;
				}
				list.Add(b);
			}
			return list.ToArray();
		}

		public byte ReadByte()
		{
			return ReadBytes(8L)[0];
		}

		public byte ReadByte(int bits)
		{
			if (bits < 0)
			{
				bits = 0;
			}
			if (bits > 8)
			{
				bits = 8;
			}
			return ReadBytes(bits)[0];
		}

		public sbyte ReadSByte()
		{
			return (sbyte)ReadBytes(8L)[0];
		}

		public sbyte ReadSByte(int bits)
		{
			if (bits < 0)
			{
				bits = 0;
			}
			if (bits > 8)
			{
				bits = 8;
			}
			return (sbyte)ReadBytes(bits)[0];
		}

		public bool ReadBool()
		{
			return ReadBytes(8L)[0] != 0;
		}

		public char ReadChar()
		{
			return encoding.GetChars(ReadBytes(encoding.GetMaxByteCount(1) * 8))[0];
		}

		public string ReadString(int length)
		{
			int num = encoding.GetByteCount(" ") * 8;
			return encoding.GetString(ReadBytes(num * length));
		}

		public short ReadInt16()
		{
			return BitConverter.ToInt16(ReadBytes(16L), 0);
		}

		public Int24 ReadInt24()
		{
			byte[] array = ReadBytes(24L);
			Array.Resize(ref array, 4);
			return BitConverter.ToInt32(array, 0);
		}

		public int ReadInt32()
		{
			return BitConverter.ToInt32(ReadBytes(32L), 0);
		}

		public Int48 ReadInt48()
		{
			byte[] array = ReadBytes(48L);
			Array.Resize(ref array, 8);
			return BitConverter.ToInt64(array, 0);
		}

		public long ReadInt64()
		{
			return BitConverter.ToInt64(ReadBytes(64L), 0);
		}

		public ushort ReadUInt16()
		{
			return BitConverter.ToUInt16(ReadBytes(16L), 0);
		}

		public UInt24 ReadUInt24()
		{
			byte[] array = ReadBytes(24L);
			Array.Resize(ref array, 4);
			return BitConverter.ToUInt32(array, 0);
		}

		public uint ReadUInt32()
		{
			return BitConverter.ToUInt32(ReadBytes(32L), 0);
		}

		public UInt48 ReadUInt48()
		{
			byte[] array = ReadBytes(48L);
			Array.Resize(ref array, 8);
			return BitConverter.ToUInt64(array, 0);
		}

		public ulong ReadUInt64()
		{
			return BitConverter.ToUInt64(ReadBytes(64L), 0);
		}

		public void WriteBytes(byte[] data, long length, bool isBytes = false)
		{
			if (isBytes)
			{
				length *= 8;
			}
			int num = 0;
			long num2 = 0L;
			while (num2 < length)
			{
				byte b = 0;
				for (int i = 0; i < 8; i++)
				{
					if (num2 >= length)
					{
						break;
					}
					b = (MSB ? ((byte)((data[num] >> 7 - i) & 1)) : ((byte)((data[num] >> i) & 1)));
					WriteBit(b);
					num2++;
				}
				num++;
			}
		}

		public void WriteByte(byte value)
		{
			WriteBytes(new byte[1] { value }, 8L);
		}

		public void WriteByte(byte value, int bits)
		{
			if (bits < 0)
			{
				bits = 0;
			}
			if (bits > 8)
			{
				bits = 8;
			}
			WriteBytes(new byte[1] { value }, bits);
		}

		public void WriteSByte(sbyte value)
		{
			WriteBytes(new byte[1] { (byte)value }, 8L);
		}

		public void WriteSByte(sbyte value, int bits)
		{
			if (bits < 0)
			{
				bits = 0;
			}
			if (bits > 8)
			{
				bits = 8;
			}
			WriteBytes(new byte[1] { (byte)value }, bits);
		}

		public void WriteBool(bool value)
		{
			WriteBytes(new byte[1] { value ? ((byte)1) : ((byte)0) }, 8L);
		}

		public void WriteChar(char value)
		{
			byte[] bytes = encoding.GetBytes(new char[1] { value }, 0, 1);
			WriteBytes(bytes, bytes.Length * 8);
		}

		public void WriteString(string value)
		{
			byte[] bytes = encoding.GetBytes(value);
			WriteBytes(bytes, bytes.Length * 8);
		}

		public void WriteInt16(short value)
		{
			WriteBytes(BitConverter.GetBytes(value), 16L);
		}

		public void WriteInt24(Int24 value)
		{
			WriteBytes(BitConverter.GetBytes(value), 24L);
		}

		public void WriteInt32(int value)
		{
			WriteBytes(BitConverter.GetBytes(value), 32L);
		}

		public void WriteInt48(Int48 value)
		{
			WriteBytes(BitConverter.GetBytes(value), 48L);
		}

		public void WriteInt64(long value)
		{
			WriteBytes(BitConverter.GetBytes(value), 64L);
		}

		public void WriteUInt16(ushort value)
		{
			WriteBytes(BitConverter.GetBytes(value), 16L);
		}

		public void WriteUInt24(UInt24 value)
		{
			WriteBytes(BitConverter.GetBytes(value), 24L);
		}

		public void WriteUInt32(uint value)
		{
			WriteBytes(BitConverter.GetBytes(value), 32L);
		}

		public void WriteUInt48(UInt48 value)
		{
			WriteBytes(BitConverter.GetBytes(value), 48L);
		}

		public void WriteUInt64(ulong value)
		{
			WriteBytes(BitConverter.GetBytes(value), 64L);
		}

		public void bitwiseShift(int bits, bool leftShift)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, 0);
			if (bits != 0 && bits <= 7)
			{
				byte b = (byte)stream.ReadByte();
				b = ((!leftShift) ? ((byte)(b >> bits)) : ((byte)(b << bits)));
				Seek(offset, 0);
				stream.WriteByte(b);
			}
			bit = 0;
			offset++;
		}

		public void bitwiseShiftOnBit(int bits, bool leftShift)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, bit);
			if (bits != 0 && bits <= 7)
			{
				byte b = ReadByte();
				b = ((!leftShift) ? ((byte)(b >> bits)) : ((byte)(b << bits)));
				offset--;
				Seek(offset, bit);
				WriteByte(b);
			}
			offset++;
		}

		public void circularShift(int bits, bool leftShift)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, 0);
			if (bits != 0 && bits <= 7)
			{
				byte b = (byte)stream.ReadByte();
				b = ((!leftShift) ? ((byte)((b >> bits) | (b << 8 - bits))) : ((byte)((b << bits) | (b >> 8 - bits))));
				Seek(offset, 0);
				stream.WriteByte(b);
			}
			bit = 0;
			offset++;
		}

		public void circularShiftOnBit(int bits, bool leftShift)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, bit);
			if (bits != 0 && bits <= 7)
			{
				byte b = ReadByte();
				b = ((!leftShift) ? ((byte)((b >> bits) | (b << 8 - bits))) : ((byte)((b << bits) | (b >> 8 - bits))));
				offset--;
				Seek(offset, bit);
				WriteByte(b);
			}
			offset++;
		}

		public void And(byte x)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, bit);
			byte b = ReadByte();
			offset--;
			Seek(offset, bit);
			WriteByte((byte)(b & x));
		}

		public void Or(byte x)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, bit);
			byte b = ReadByte();
			offset--;
			Seek(offset, bit);
			WriteByte((byte)(b | x));
		}

		public void Xor(byte x)
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, bit);
			byte b = ReadByte();
			offset--;
			Seek(offset, bit);
			WriteByte((byte)(b ^ x));
		}

		public void Not()
		{
			if (!ValidPositionWhen(8))
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, bit);
			byte b = ReadByte();
			offset--;
			Seek(offset, bit);
			WriteByte((byte)(~b));
		}

		public void BitAnd(Bit x)
		{
			if (!ValidPosition)
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, this.bit);
			Bit bit = ReadBit();
			ReturnBit();
			WriteBit(x & bit);
		}

		public void BitOr(Bit x)
		{
			if (!ValidPosition)
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, this.bit);
			Bit bit = ReadBit();
			ReturnBit();
			WriteBit(x | bit);
		}

		public void BitXor(Bit x)
		{
			if (!ValidPosition)
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, this.bit);
			Bit bit = ReadBit();
			ReturnBit();
			WriteBit(x ^ bit);
		}

		public void BitNot()
		{
			if (!ValidPosition)
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, this.bit);
			Bit bit = ReadBit();
			ReturnBit();
			WriteBit(~bit);
		}

		public void ReverseBits()
		{
			if (!ValidPosition)
			{
				throw new IOException("Cannot read in an offset bigger than the length of the stream");
			}
			Seek(offset, 0);
			byte b = ReadByte();
			offset--;
			Seek(offset, 0);
			WriteByte(b.ReverseBits());
		}
	}
	[Serializable]
	internal struct Int24
	{
		private byte b0;

		private byte b1;

		private byte b2;

		private Bit sign;

		private Int24(int value)
		{
			b0 = (byte)(value & 0xFF);
			b1 = (byte)((value >> 8) & 0xFF);
			b2 = (byte)((value >> 16) & 0x7F);
			sign = (byte)((value >> 23) & 1);
		}

		public static implicit operator Int24(int value)
		{
			return new Int24(value);
		}

		public static implicit operator int(Int24 i)
		{
			int num = i.b0 | (i.b1 << 8) | (i.b2 << 16);
			return -((int)i.sign << 23) + num;
		}

		public Bit GetBit(int index)
		{
			return (int)this >> index;
		}
	}
	[Serializable]
	internal struct Int48
	{
		private byte b0;

		private byte b1;

		private byte b2;

		private byte b3;

		private byte b4;

		private byte b5;

		private Bit sign;

		private Int48(long value)
		{
			b0 = (byte)(value & 0xFF);
			b1 = (byte)((value >> 8) & 0xFF);
			b2 = (byte)((value >> 16) & 0xFF);
			b3 = (byte)((value >> 24) & 0xFF);
			b4 = (byte)((value >> 32) & 0xFF);
			b5 = (byte)((value >> 40) & 0x7F);
			sign = (byte)((value >> 47) & 1);
		}

		public static implicit operator Int48(long value)
		{
			return new Int48(value);
		}

		public static implicit operator long(Int48 i)
		{
			long num = (long)(i.b0 + (i.b1 << 8) + (i.b2 << 16)) + (long)((ulong)i.b3 << 24) + (long)((ulong)i.b4 << 32) + (long)((ulong)i.b5 << 40);
			return -((long)(int)i.sign << 47) + num;
		}

		public Bit GetBit(int index)
		{
			return (byte)((long)this >> index);
		}
	}
	[Serializable]
	internal struct UInt24
	{
		private byte b0;

		private byte b1;

		private byte b2;

		private UInt24(uint value)
		{
			b0 = (byte)(value & 0xFF);
			b1 = (byte)((value >> 8) & 0xFF);
			b2 = (byte)((value >> 16) & 0xFF);
		}

		public static implicit operator UInt24(uint value)
		{
			return new UInt24(value);
		}

		public static implicit operator uint(UInt24 i)
		{
			return (uint)(i.b0 | (i.b1 << 8) | (i.b2 << 16));
		}

		public Bit GetBit(int index)
		{
			return (byte)((uint)this >> index);
		}
	}
	[Serializable]
	internal struct UInt48
	{
		private byte b0;

		private byte b1;

		private byte b2;

		private byte b3;

		private byte b4;

		private byte b5;

		private UInt48(ulong value)
		{
			b0 = (byte)(value & 0xFF);
			b1 = (byte)((value >> 8) & 0xFF);
			b2 = (byte)((value >> 16) & 0xFF);
			b3 = (byte)((value >> 24) & 0xFF);
			b4 = (byte)((value >> 32) & 0xFF);
			b5 = (byte)((value >> 40) & 0xFF);
		}

		public static implicit operator UInt48(ulong value)
		{
			return new UInt48(value);
		}

		public static implicit operator ulong(UInt48 i)
		{
			return i.b0 + ((ulong)i.b1 << 8) + ((ulong)i.b2 << 16) + ((ulong)i.b3 << 24) + ((ulong)i.b4 << 32) + ((ulong)i.b5 << 40);
		}

		public Bit GetBit(int index)
		{
			return (byte)((ulong)this >> index);
		}
	}
}
