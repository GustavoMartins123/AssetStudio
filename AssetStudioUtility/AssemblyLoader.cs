using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio
{
    public class AssemblyLoader
    {
        public bool Loaded;
        private Dictionary<string, ModuleDefinition> moduleDic = new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);

        public void Load(string path)
        {
            if (!Directory.Exists(path)) return;

            var files = Directory.GetFiles(path, "*.dll");
            var filesUpper = Directory.GetFiles(path, "*.DLL");
            var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var f in files) allFiles.Add(f);
            foreach (var f in filesUpper) allFiles.Add(f);

            var resolver = new MyAssemblyResolver();
            resolver.AddSearchDirectory(path);
            var readerParameters = new ReaderParameters();
            readerParameters.AssemblyResolver = resolver;

            foreach (var file in allFiles)
            {
                try
                {
                    var assembly = AssemblyDefinition.ReadAssembly(file, readerParameters);
                    resolver.Register(assembly);
                    
                    var moduleName = assembly.MainModule.Name;
                    if (!moduleDic.ContainsKey(moduleName))
                    {
                        moduleDic.Add(moduleName, assembly.MainModule);
                    }

                    var nameWithoutExt = Path.GetFileNameWithoutExtension(moduleName);
                    if (!moduleDic.ContainsKey(nameWithoutExt))
                    {
                        moduleDic.Add(nameWithoutExt, assembly.MainModule);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Failed to load assembly {file}: {ex.Message}");
                }
            }
            Loaded = true;
        }

        public TypeDefinition GetTypeDefinition(string assemblyName, string fullName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return null;

            if (moduleDic.TryGetValue(assemblyName, out var module))
            {
                var typeDef = module.GetType(fullName);
                if (typeDef == null && (assemblyName.Equals("UnityEngine.dll", StringComparison.OrdinalIgnoreCase) || assemblyName.Equals("UnityEngine", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pair in moduleDic)
                    {
                        typeDef = pair.Value.GetType(fullName);
                        if (typeDef != null)
                        {
                            break;
                        }
                    }
                }
                return typeDef;
            }

            // Fallback: try with/without .dll extension
            var altName = assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) 
                ? assemblyName.Substring(0, assemblyName.Length - 4) 
                : assemblyName + ".dll";

            if (moduleDic.TryGetValue(altName, out module))
            {
                var typeDef = module.GetType(fullName);
                if (typeDef == null && (altName.Equals("UnityEngine.dll", StringComparison.OrdinalIgnoreCase) || altName.Equals("UnityEngine", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pair in moduleDic)
                    {
                        typeDef = pair.Value.GetType(fullName);
                        if (typeDef != null)
                        {
                            break;
                        }
                    }
                }
                return typeDef;
            }

            return null;
        }

        public void Clear()
        {
            foreach (var pair in moduleDic)
            {
                pair.Value.Dispose();
            }
            moduleDic.Clear();
            Loaded = false;
        }
    }
}
