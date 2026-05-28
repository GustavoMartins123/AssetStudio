using System;
using System.IO;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace Decompiler
{
    class Program
    {
        static void Main(string[] args)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var assemblyPath = Path.Combine(userProfile, @".nuget\packages\fmod5sharp\3.1.0\lib\net8.0\Fmod5Sharp.dll");
            var outputPath = @"f:\AssetStudio\scratch\Fmod5Sharp_decompiled.cs";

            Console.WriteLine($"Decompiling {assemblyPath}...");
            
            var decompiler = new CSharpDecompiler(assemblyPath, new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false
            });

            var code = decompiler.DecompileWholeModuleAsString();

            File.WriteAllText(outputPath, code);
            Console.WriteLine($"Decompiled to {outputPath}");
        }
    }
}
