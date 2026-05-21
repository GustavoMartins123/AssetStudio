// using UnityEditor;
// using UnityEngine;

// public class FbxRigPostprocessor : AssetPostprocessor
// {
//     void OnPreprocessModel()
//     {
//         if (assetPath.ToLower().EndsWith(".fbx"))
//         {
//             ModelImporter modelImporter = assetImporter as ModelImporter;
//             if (modelImporter != null)
//             {
//                 modelImporter.animationType = ModelImporterAnimationType.Generic;
//             }
//         }
//     }

//     void OnPreprocessAnimation()
//     {
//         ModelImporter modelImporter = assetImporter as ModelImporter;
//         if (modelImporter != null && assetPath.ToLower().EndsWith(".fbx"))
//         {
//             if (modelImporter.animationType == ModelImporterAnimationType.Legacy)
//             {
//                 modelImporter.generateAnimations = ModelImporterGenerateAnimations.GenerateAnimations;

//                 ModelImporterClipAnimation[] clips = modelImporter.defaultClipAnimations;
//                 for (int i = 0; i < clips.Length; i++)
//                 {
//                     clips[i].keepOriginalOrientation = true;
//                     clips[i].keepOriginalPositionY = true;
//                     clips[i].keepOriginalPositionXZ = true;
//                 }
                
//                 modelImporter.clipAnimations = clips;
//             }
//         }
//     }
// }
