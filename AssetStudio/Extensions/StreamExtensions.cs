using System.IO;

namespace AssetStudio
{
    public static class StreamExtensions
    {
        private const int BufferSize = 81920;

        public static void CopyTo(this Stream source, Stream destination, long size)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                for (var left = size; left > 0; left -= BufferSize)
                {
                    int toRead = BufferSize < left ? BufferSize : (int)left;
                    int read = source.Read(buffer, 0, toRead);
                    destination.Write(buffer, 0, read);
                    if (read != toRead)
                    {
                        return;
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
