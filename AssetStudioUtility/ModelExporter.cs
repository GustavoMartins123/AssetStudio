using System;

namespace AssetStudio
{
    public enum ModelExportFormat
    {
        Fbx = 0,
        Glb = 1,
        Gltf = 2
    }

    public static class ModelExporter
    {
        public static string GetModelFileExtension(ModelExportFormat format)
        {
            return format switch
            {
                ModelExportFormat.Glb => ".glb",
                ModelExportFormat.Gltf => ".gltf",
                _ => ".fbx"
            };
        }

        public static void ExportFbx(string path, IImported imported,
            bool skins, bool animation, bool blendShape, bool castToBone, float boneSize, float scaleFactor, int versionIndex, bool isAscii,
            bool combineMeshes = false, bool writeExportReport = true)
        {
            var exportSource = imported;
            if (combineMeshes && imported != null && imported.MeshList != null && imported.MeshList.Count > 1)
            {
                exportSource = ModelConverter.CreateCombinedView(imported);
            }

            var exporter = new FbxSharpieExporter(path, scaleFactor, versionIndex, isAscii, false,
                skins, animation, blendShape, castToBone, boneSize, exportMeshes: true, writeExportReport: writeExportReport);
            exporter.Export(exportSource!, path);
        }

        public static void ExportGltf(string path, IImported imported,
            bool binary = true, bool skins = true, bool animation = true, bool blendShape = true, float scaleFactor = 1f,
            bool combineMeshes = false, bool enableInstancing = true)
        {
            _ = ExportGltfWithStats(path, imported, binary, skins, animation, blendShape, scaleFactor, combineMeshes, enableInstancing);
        }

        public static GltfExportStatistics ExportGltfWithStats(string path, IImported imported,
            bool binary = true, bool skins = true, bool animation = true, bool blendShape = true, float scaleFactor = 1f,
            bool combineMeshes = false, bool enableInstancing = true)
        {
            if (imported == null)
            {
                throw new ArgumentNullException(nameof(imported));
            }

            var exportSource = imported;
            if (combineMeshes && imported.MeshList != null && imported.MeshList.Count > 1)
            {
                exportSource = ModelConverter.CreateCombinedView(imported) ?? imported;
            }

            var exporter = new GltfExporter(binary, scaleFactor, skins, animation, blendShape, enableInstancing: enableInstancing);
            exporter.Export(exportSource, path);
            return exporter.LastExportStats;
        }

        public static void ExportModel(string path, IImported imported,
            ModelExportFormat format,
            bool skins, bool animation, bool blendShape, bool castToBone, float boneSize, float scaleFactor, int versionIndex, bool isAscii,
            bool combineMeshes = false)
        {
            switch (format)
            {
                case ModelExportFormat.Glb:
                    ExportGltf(path, imported, binary: true, skins, animation, blendShape, scaleFactor, combineMeshes);
                    break;
                case ModelExportFormat.Gltf:
                    ExportGltf(path, imported, binary: false, skins, animation, blendShape, scaleFactor, combineMeshes);
                    break;
                default:
                    ExportFbx(path, imported, skins, animation, blendShape, castToBone, boneSize, scaleFactor, versionIndex, isAscii, combineMeshes);
                    break;
            }
        }
    }
}
