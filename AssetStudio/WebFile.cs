using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public class WebFile
    {
        public StreamFile[] fileList;

        private class WebData
        {
            public int dataOffset;
            public int dataLength;
            public string path = string.Empty;
        }

        public WebFile(EndianBinaryReader reader)
        {
            reader.Endian = EndianType.LittleEndian;
            var streamLength = reader.BaseStream.Length;
            var signature = reader.ReadStringToNull();
            var headLength = reader.ReadInt32();
            if (headLength < 0 || headLength > streamLength)
            {
                throw new InvalidDataException($"Invalid WebFile header length {headLength} (file length {streamLength}).");
            }
            var dataList = new List<WebData>();
            while (reader.BaseStream.Position < headLength)
            {
                var data = new WebData();
                data.dataOffset = reader.ReadInt32();
                data.dataLength = reader.ReadInt32();
                var pathLength = reader.ReadInt32();
                if (pathLength < 0 || pathLength > streamLength - reader.BaseStream.Position)
                {
                    throw new InvalidDataException($"Invalid WebFile entry path length {pathLength}.");
                }
                if (data.dataOffset < 0 || data.dataLength < 0 || (long)data.dataOffset + data.dataLength > streamLength)
                {
                    throw new InvalidDataException($"Invalid WebFile entry range (offset {data.dataOffset}, length {data.dataLength}, file length {streamLength}).");
                }
                data.path = Encoding.UTF8.GetString(reader.ReadBytes(pathLength));
                dataList.Add(data);
            }
            fileList = new StreamFile[dataList.Count];
            for (int i = 0; i < dataList.Count; i++)
            {
                var data = dataList[i];
                var file = new StreamFile();
                file.path = data.path;
                file.fileName = Path.GetFileName(data.path);
                reader.BaseStream.Position = data.dataOffset;
                file.stream = new MemoryStream(reader.ReadBytes(data.dataLength));
                fileList[i] = file;
            }
        }
    }
}
