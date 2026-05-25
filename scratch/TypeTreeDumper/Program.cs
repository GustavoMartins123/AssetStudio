using System;
using System.IO;
using System.Linq;
using AssetStudio;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Please provide the path to globalgamemanagers.assets.");
            return;
        }

        string filePath = args[0];
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        try
        {
            var reader = new FileReader(filePath);
            var file = new SerializedFile(reader, null);
            
            Console.WriteLine("==================================================");
            Console.WriteLine("DUMPING TYPETREES");
            Console.WriteLine("==================================================");

            foreach (var type in file.m_Types)
            {
                if (type.classID == 21 || (ClassIDType)type.classID == ClassIDType.Material)
                {
                    Console.WriteLine("--------------------------------------------------");
                    Console.WriteLine("Material TypeTree:");
                    PrintTypeTree(type);
                }
                else if (type.classID == 48 || (ClassIDType)type.classID == ClassIDType.Shader)
                {
                    Console.WriteLine("--------------------------------------------------");
                    Console.WriteLine("Shader TypeTree:");
                    PrintTypeTree(type);
                }
            }

            bool dumpedMaterial = false;
            bool dumpedShader = false;
            
            foreach (var objectInfo in file.m_Objects)
            {
                if (dumpedMaterial && dumpedShader)
                    break;

                var objectReader = new ObjectReader(file.reader, file, objectInfo);
                try
                {
                    if (objectReader.type == ClassIDType.Material && !dumpedMaterial)
                    {
                        var mat = new Material(objectReader);
                    }
                    else if (objectReader.type == ClassIDType.Shader && !dumpedShader)
                    {
                        var shader = new Shader(objectReader);
                    }
                }
                catch (Exception ex)
                {
                    if (objectReader.type == ClassIDType.Material && !dumpedMaterial)
                    {
                        dumpedMaterial = true;
                        Console.WriteLine($"==================================================");
                        Console.WriteLine($"Material PathID: {objectInfo.m_PathID} failed: {ex.Message}");
                        objectReader.Reset();
                        var bytes = objectReader.ReadBytes((int)objectInfo.byteSize);
                        Console.WriteLine("HEX: " + BitConverter.ToString(bytes).Replace("-", " "));
                    }
                    else if (objectReader.type == ClassIDType.Shader && !dumpedShader)
                    {
                        dumpedShader = true;
                        Console.WriteLine($"==================================================");
                        Console.WriteLine($"Shader PathID: {objectInfo.m_PathID} failed: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                        objectReader.Reset();
                        var bytes = objectReader.ReadBytes((int)objectInfo.byteSize);
                        Console.WriteLine("HEX: " + BitConverter.ToString(bytes).Replace("-", " "));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error occurred: {ex}");
        }
    }

    static void PrintTypeTree(SerializedType type)
    {
        if (type?.m_Type?.m_Nodes == null)
        {
            Console.WriteLine("  (No TypeTree nodes)");
            return;
        }
        foreach (var node in type.m_Type.m_Nodes)
        {
            Console.WriteLine($"{new string(' ', node.m_Level * 2)}{node.m_Type} {node.m_Name} (size: {node.m_ByteSize}, flags: {node.m_TypeFlags:X}, meta: {node.m_MetaFlag:X})");
        }
    }
}
