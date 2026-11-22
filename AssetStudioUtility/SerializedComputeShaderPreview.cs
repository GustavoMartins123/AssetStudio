using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public sealed record ShaderPreviewComputeOutput(string Name, int Slot, ShaderPreviewTextureDimension? Dimension);

    public sealed record ShaderPreviewComputeKernel(string ShaderName, string Name, ShaderPreviewStage Stage,
        uint ThreadsX, uint ThreadsY, uint ThreadsZ, ShaderPreviewComputeOutput[] Outputs);

    public sealed class SerializedComputeShaderPreview : NamedObject
    {
        private const int Direct3D11Device = 2;
        private readonly Dictionary<string, Kernel> _kernels = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, ShaderPreviewBuffer> _layouts;

        private SerializedComputeShaderPreview(ObjectReader reader) : base(reader)
        {
            Dictionary<string, ShaderPreviewBuffer>? buffers = null;
            for (int i = 0, count = ReadCount(reader, 17); i < count; i++)
            {
                int renderer = reader.ReadInt32();
                reader.ReadInt32(); // Target level.
                var kernels = ReadArray(reader, 32, ReadKernel);
                var layouts = ReadArray(reader, 12, ReadBufferLayout);
                bool resolved = reader.ReadBoolean();
                reader.AlignStream();
                if (renderer != Direct3D11Device)
                {
                    continue;
                }
                if (buffers != null || !resolved || kernels.Length == 0)
                {
                    throw new InvalidDataException($"ComputeShader '{m_Name}' has an invalid Direct3D 11 variant.");
                }
                buffers = layouts.ToDictionary(buffer => buffer.Name, StringComparer.Ordinal);
                foreach (var kernel in kernels)
                {
                    if (!_kernels.TryAdd(kernel.Name, kernel))
                    {
                        throw new InvalidDataException($"ComputeShader '{m_Name}' contains duplicate kernel '{kernel.Name}'.");
                    }
                }
            }
            if (reader.Position != reader.byteStart + reader.byteSize)
            {
                throw new InvalidDataException($"ComputeShader '{m_Name}' has an unexpected serialized layout.");
            }
            _layouts = buffers ?? throw new NotSupportedException($"ComputeShader '{m_Name}' has no compiled Direct3D 11 kernels.");
        }

        public static SerializedComputeShaderPreview Read(Object asset)
        {
            if (asset.type != ClassIDType.ComputeShader)
            {
                throw new InvalidDataException("The compute resource does not reference a ComputeShader asset.");
            }
            if (asset.version is not [2019, 3, ..])
            {
                throw new NotSupportedException("The compute preview reader supports the Unity 2019.3 layout.");
            }
            var file = asset.assetsFile;
            using var stream = file.reader.Clone();
            return new SerializedComputeShaderPreview(new ObjectReader(stream, file, file.ObjectInfoDic[asset.m_PathID]));
        }

        public ShaderPreviewComputeKernel GetKernel(string name) => _kernels.TryGetValue(name, out var kernel)
            ? Prepare(kernel) : throw new NotSupportedException($"ComputeShader '{m_Name}' has no Direct3D 11 kernel '{name}'.");

        private sealed record Resource(string Name, int Slot, int SamplerSlot, int Dimension);

        private sealed record Kernel(string Name, Resource[] Buffers, Resource[] Textures, SamplerParameter[] Samplers,
            Resource[] Inputs, Resource[] Outputs, byte[] Code, uint[] Threads);

        private static Kernel ReadKernel(ObjectReader reader)
        {
            string name = reader.ReadAlignedString();
            var buffers = ReadArray(reader, 20, ReadResource);
            var textures = ReadArray(reader, 20, ReadResource);
            var samplers = ReadArray(reader, 8, input => new SamplerParameter(input));
            var inputs = ReadArray(reader, 20, ReadResource);
            var outputs = ReadArray(reader, 20, ReadResource);
            var code = reader.ReadBytes(ReadCount(reader, 1));
            reader.AlignStream();
            var threads = ReadArray(reader, sizeof(uint), input => input.ReadUInt32());
            return new Kernel(name, buffers, textures, samplers, inputs, outputs, code, threads);
        }

        private static Resource ReadResource(ObjectReader reader)
        {
            string name = reader.ReadAlignedString();
            reader.ReadAlignedString(); // Generated name for other graphics APIs.
            return new Resource(name, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        }

        private static ShaderPreviewBuffer ReadBufferLayout(ObjectReader reader)
        {
            string name = reader.ReadAlignedString();
            int size = reader.ReadInt32();
            var parameters = ReadArray(reader, 24, input =>
            {
                string parameter = input.ReadAlignedString();
                int type = input.ReadInt32(), offset = input.ReadInt32(), count = input.ReadInt32();
                int rows = input.ReadInt32(), columns = input.ReadInt32();
                return new ShaderPreviewParameter(parameter, type, rows, columns, rows > 1, count == 0 ? 1 : count, offset);
            });
            return new ShaderPreviewBuffer(name, -1, size, parameters);
        }

        private ShaderPreviewComputeKernel Prepare(Kernel kernel)
        {
            if (kernel.Name.Length == 0 || kernel.Code.Length < 32 || !kernel.Code.AsSpan(0, 4).SequenceEqual("DXBC"u8) ||
                kernel.Threads is not [> 0 and <= 1024, > 0 and <= 1024, > 0 and <= 64] ||
                (ulong)kernel.Threads[0] * kernel.Threads[1] * kernel.Threads[2] > 1024)
            {
                throw new InvalidDataException($"ComputeShader '{m_Name}' has an invalid kernel '{kernel.Name}'.");
            }
            var buffers = kernel.Buffers.Select(binding =>
            {
                if (binding.Slot is < 0 or >= 14 || binding.Dimension != -1 || binding.SamplerSlot != -1 ||
                    !_layouts.TryGetValue(binding.Name, out var buffer) || buffer.Size is <= 0 or > 65536)
                {
                    throw BindError(binding);
                }
                return buffer with { Slot = binding.Slot };
            }).ToArray();
            var textures = kernel.Textures.Select(binding =>
            {
                var dimension = (ShaderPreviewTextureDimension)binding.Dimension;
                if (binding.Slot is < 0 or >= 128 || binding.SamplerSlot is < -1 or >= 16 || !Enum.IsDefined(dimension))
                {
                    throw BindError(binding);
                }
                return new ShaderPreviewTextureBinding(binding.Name, binding.Slot, binding.SamplerSlot, dimension);
            }).ToArray();
            var inputs = kernel.Inputs.Select(binding =>
            {
                if (binding.Slot is < 0 or >= 128 || binding.SamplerSlot != -1 || binding.Dimension != -1)
                {
                    throw BindError(binding);
                }
                return new ShaderPreviewStructuredBufferBinding(binding.Name, binding.Slot);
            }).ToArray();
            var outputs = kernel.Outputs.Select(binding =>
            {
                var dimension = (ShaderPreviewTextureDimension)binding.Dimension;
                if (binding.Slot is < 0 or >= 8 || binding.SamplerSlot != -1 ||
                    (binding.Dimension != -1 && !Enum.IsDefined(dimension)))
                {
                    throw BindError(binding);
                }
                return new ShaderPreviewComputeOutput(binding.Name, binding.Slot, binding.Dimension == -1 ? null : dimension);
            }).ToArray();
            var stage = new ShaderPreviewStage(kernel.Code, buffers, textures)
            {
                StructuredBuffers = inputs,
                InlineSamplers = kernel.Samplers.Select(binding => ShaderPreviewReader.ReadInlineSampler(binding.sampler, binding.bindPoint)).ToArray()
            };
            return new ShaderPreviewComputeKernel(m_Name, kernel.Name, stage, kernel.Threads[0], kernel.Threads[1], kernel.Threads[2], outputs);

            InvalidDataException BindError(Resource binding) =>
                new($"Invalid resource '{binding.Name}' in compute kernel '{m_Name}/{kernel.Name}'.");
        }

        private static T[] ReadArray<T>(ObjectReader reader, int minimumItemBytes, Func<ObjectReader, T> read)
        {
            var items = new T[ReadCount(reader, minimumItemBytes)];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = read(reader);
            }
            return items;
        }

        private static int ReadCount(ObjectReader reader, int minimumItemBytes)
        {
            reader.CheckArrayLength(sizeof(int));
            int count = reader.ReadInt32();
            if (count < 0 || count > (reader.byteStart + reader.byteSize - reader.Position) / minimumItemBytes)
            {
                throw new InvalidDataException("Invalid ComputeShader array length.");
            }
            return count;
        }
    }
}
