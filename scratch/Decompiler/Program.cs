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
            var assemblyPath = @"f:\AssetStudio\AssetStudio.Avalonia\bin\Debug\net10.0\FFmpegVideoPlayer.Core.dll";
            var outputPath = @"f:\AssetStudio\scratch\FFmpegVideoPlayerCore_decompiled.cs";

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
