namespace AssetStudio
{
    public static class ModelExporter
    {
        public static void ExportFbx(string path, IImported imported, bool eulerFilter, float filterPrecision,
            bool allNodes, bool skins, bool animation, bool blendShape, bool castToBone, float boneSize, bool exportAllUvsAsDiffuseMaps, float scaleFactor, int versionIndex, bool isAscii)
        {
            var exporter = new FbxSharpieExporter(path, scaleFactor, versionIndex, isAscii, false,
                skins, animation, blendShape, castToBone, boneSize);
            exporter.Export(imported, path);
        }
    }
}
