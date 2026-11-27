#if UNITY_6000_5_OR_NEWER
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEngine;

public static class MakeExtractedFixture
{
    public static void Build()
    {
        var target = EditorUserBuildSettings.activeBuildTarget;
        var group = BuildPipeline.GetBuildTargetGroup(target);
        var outDir = $"Out/{target}/extracted";
        Directory.CreateDirectory(outDir);

        var content = new BundleBuildContent(ContentBuildInterface.GenerateAssetBundleBuilds());
        var parameters = new BundleBuildParameters(target, group, outDir)
        {
            UseCache = false,
            BundleCompression = UnityEngine.BuildCompression.Uncompressed,
        };
        parameters.ContentBuildFlags |= ContentBuildFlags.ExtractTypeTree;

        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
        var typeTreePath = Path.GetFullPath($"{outDir}/typetrees.bin");
        tasks.Add(new CombineExtractedTypeTreeData { OutputPath = typeTreePath });

        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
        Debug.Log($"FIXTURE_EXTRACTED code={code}");
        if (results != null)
        {
            foreach (var w in results.WriteResults)
                Debug.Log($"FIXTURE_EXTRACTED write {w.Key} typetree={w.Value.extractedTypeTreeDataPath}");
            foreach (var b in results.BundleInfos)
                Debug.Log($"FIXTURE_EXTRACTED bundle {b.Key} -> {b.Value.FileName}");
        }
        if (File.Exists(typeTreePath))
        {
            var hashes = ContentBuildInterface.LoadTypeTreeDataHashesFromFile(typeTreePath);
            Debug.Log($"FIXTURE_EXTRACTED combined {typeTreePath} {new FileInfo(typeTreePath).Length} bytes, {hashes.Length} hashes: " + string.Join(",", hashes.Take(4)));
        }
        Debug.Log("FIXTURE_DONE extracted");
    }
}
#endif
