using UnityEditor;
using UnityEngine;

public class GenericAnimationPostprocessor : AssetPostprocessor
{
    void OnPreprocessModel()
    {
        if (assetPath.ToLower().EndsWith(".fbx"))
        {
            ModelImporter importer = assetImporter as ModelImporter;
            if (importer != null)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                
                importer.animationCompression = ModelImporterAnimationCompression.Off;
                
                importer.importAnimations = true;
            }
        }
    }
}