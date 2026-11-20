namespace AssetStudio
{
    public static class ModelExporter
    {
        public static void ExportFbx(string path, IImported imported,
            bool skins, bool animation, bool blendShape, bool castToBone, float boneSize, float scaleFactor, int versionIndex, bool isAscii,
            bool combineMeshes = false)
        {
            var exportSource = imported;
            if (combineMeshes && imported != null && imported.MeshList != null && imported.MeshList.Count > 1)
            {
                exportSource = ModelConverter.CreateCombinedView(imported);
            }

            var exporter = new FbxSharpieExporter(path, scaleFactor, versionIndex, isAscii, false,
                skins, animation, blendShape, castToBone, boneSize);
            exporter.Export(exportSource, path);
        }
    }
}
