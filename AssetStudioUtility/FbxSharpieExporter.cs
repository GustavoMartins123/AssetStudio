using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UkooLabs.FbxSharpie;
using UkooLabs.FbxSharpie.Tokens;
using UkooLabs.FbxSharpie.Tokens.Value;
using UkooLabs.FbxSharpie.Tokens.ValueArray;

namespace AssetStudio
{
    public class FbxSharpieExporter
    {
        private FbxDocument _document;
        private FbxNode _objects;
        private FbxNode _connections;
        private long _nextId = 1000;
        private float _scaleFactor;
        private int _versionIndex;
        private bool _isAscii;
        private bool _is60Fps;
        private bool _exportSkins;
        private bool _exportAnimations;
        private bool _exportBlendShape;
        private bool _castToBone;
        private bool _exportMeshes;
        private float _boneSize;
        private string? _exportDirectory;
        private ImportedFrame? _rootFrame;
        private FbxNode? _globalSettingsProps;
        private FbxNode? _definitionsCountNode;
        private Dictionary<string, FbxNode> _definitionTypeCountNodes = new Dictionary<string, FbxNode>(StringComparer.Ordinal);
        private Dictionary<long, System.Numerics.Matrix4x4> _bindPoseMatrixByModelId = new Dictionary<long, System.Numerics.Matrix4x4>();
        private List<long> _extraMeshModelIds = new List<long>();
        private Dictionary<string, long> _frameIdMap = new Dictionary<string, long>();
        private Dictionary<string, string?> _frameUniqueSuffixMap = new Dictionary<string, string?>();
        private Dictionary<string, ImportedFrame> _frameByPathMap = new Dictionary<string, ImportedFrame>();
        private Dictionary<string, ImportedFrame?> _frameUniqueLeafMap = new Dictionary<string, ImportedFrame?>();
        private Dictionary<string, System.Numerics.Matrix4x4> _frameGlobalMatrixMap = new Dictionary<string, System.Numerics.Matrix4x4>();
        private Dictionary<string, long> _materialIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _meshIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _blendShapeChannelMap = new Dictionary<string, long>();
        private Dictionary<string, long> _textureIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _videoIdMap = new Dictionary<string, long>();
        private Dictionary<string, ImportedTexture> _sourceTextureMap = new Dictionary<string, ImportedTexture>();
        private Dictionary<string, int> _fbxNameCounts = new Dictionary<string, int>();
        private HashSet<string> _bonePathSet = new HashSet<string>();
        private HashSet<string> _skinBonePathSet = new HashSet<string>();
        private HashSet<string> _meshPathSet = new HashSet<string>();
        private List<(string Name, long Start, long Stop)> _takes = new List<(string Name, long Start, long Stop)>();
        private static readonly int[] DefaultKeyAttrFlags = { 24840 };
        private static readonly float[] DefaultKeyAttrDataFloat = { 0f, 0f, 9.419963E-30f, 0f };
        private int _exportedFrameCount;
        private int _exportedBoneCount;
        private int _exportedMeshCount;
        private int _exportedSkinCount;
        private int _candidateClusterCount;
        private int _exportedClusterCount;
        private int _omittedClusterCount;
        private int _exportedMaterialCount;
        private int _exportedTextureCount;
        private int _exportedAnimationStackCount;
        private int _exportedAnimationTrackCount;
        private int _exportedAnimationCurveCount;
        private int _missingAnimationTrackCount;

        private class MeshDiagnosticInfo
        {
            public string Path { get; set; } = string.Empty;
            public int VertexCount { get; set; }
            public int PolygonCount { get; set; }
            public int MaxIndex { get; set; } = -1;
            public int MinIndex { get; set; } = int.MaxValue;
            public int SkippedFaces { get; set; }
            public List<SubmeshDiagnosticInfo> Submeshes { get; set; } = new List<SubmeshDiagnosticInfo>();
        }

        private class SubmeshDiagnosticInfo
        {
            public int Index { get; set; }
            public int FirstVertex { get; set; } = int.MaxValue;
            public int BaseVertex { get; set; }
            public int VertexCount { get; set; }
            public int IndexCount { get; set; }
            public string Material { get; set; } = string.Empty;
            public int MinIndex { get; set; } = int.MaxValue;
            public int MaxIndex { get; set; } = -1;
        }

        private List<MeshDiagnosticInfo> _meshDiagnostics = new List<MeshDiagnosticInfo>();

        public FbxSharpieExporter(string fileName, float scaleFactor, int versionIndex, bool isAscii, bool is60Fps,
            bool exportSkins = true, bool exportAnimations = true, bool exportBlendShape = true, bool castToBone = false, float boneSize = 10f, bool exportMeshes = true)
        {
            _scaleFactor = scaleFactor;
            _versionIndex = versionIndex;
            _isAscii = isAscii;
            _is60Fps = is60Fps;
            _exportSkins = exportSkins;
            _exportAnimations = exportAnimations;
            _exportBlendShape = exportBlendShape;
            _castToBone = castToBone;
            _boneSize = boneSize;
            _exportMeshes = exportMeshes;
            _document = new FbxDocument();
            _document.Version = versionIndex >= 5 ? (FbxVersion)7500 : FbxVersion.v7_4;

            BuildHeader();
            BuildGlobalSettings(scaleFactor);
            BuildDocuments();
            BuildDefinitions();

            _objects = N("Objects");
            _document.AddNode(_objects);

            _connections = N("Connections");
            _document.AddNode(_connections);
        }

        private string FormatObjectName(string? name, string className)
        {
            var cleanName = name ?? string.Empty;
            return _isAscii ? $"{className}::{cleanName}" : $"{cleanName}\0\u0001{className}";
        }

        public void Export(IImported convert, string exportPath)
        {
            var currentCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            var currentUICulture = System.Threading.Thread.CurrentThread.CurrentUICulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

                _rootFrame = convert.RootFrame;
                _exportDirectory = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(_exportDirectory))
                    Directory.CreateDirectory(_exportDirectory);

                BuildSourceTextureMap(convert.TextureList);
                BuildFrameLookupMaps(convert.RootFrame);
                BuildBonePathSet(convert);

                if (convert.RootFrame != null)
                {
                    ExportFrame(convert.RootFrame, 0, System.Numerics.Matrix4x4.Identity);
                }

                if (_exportMeshes && convert.MaterialList != null)
                {
                    for (int i = 0; i < convert.MaterialList.Count; i++)
                        ExportMaterial(convert.MaterialList[i], convert);
                }

                if (_exportMeshes && convert.MeshList != null)
                {
                    for (int i = 0; i < convert.MeshList.Count; i++)
                        ExportMesh(convert.MeshList[i], convert);
                }

                if (convert.RootFrame != null || _extraMeshModelIds.Count > 0)
                {
                    ExportBindPose();
                }

                if (_exportBlendShape && convert.MorphList != null)
                {
                    for (int i = 0; i < convert.MorphList.Count; i++)
                        ExportMorph(convert.MorphList[i], convert);
                }

                if (_exportAnimations && convert.AnimationList != null)
                {
                    for (int i = 0; i < convert.AnimationList.Count; i++)
                        ExportAnimation(convert.AnimationList[i]);
                }

                UpdateGlobalSettingsTiming(convert);
                UpdateDefinitionsCounts();
                BuildTakes();
                WriteExportReport(exportPath, convert);
                if (_isAscii) FbxIO.WriteAscii(_document, exportPath); else AutodeskBinaryWriter.Write(_document, exportPath);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = currentCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = currentUICulture;
            }
        }

        private void ExportFrame(ImportedFrame frame, long parentId, System.Numerics.Matrix4x4 parentGlobal)
        {
            var id = GenId();
            _exportedFrameCount++;
            var normalizedPath = NormalizeFramePath(frame.Path);
            _frameIdMap[normalizedPath] = id;
            var globalMatrix = BuildLocalMatrix(frame) * parentGlobal;
            _frameGlobalMatrixMap[normalizedPath] = globalMatrix;
            _bindPoseMatrixByModelId[id] = globalMatrix;
            var isBone = IsBonePath(normalizedPath);
            var zeroTransform = ShouldZeroBoneTransform(normalizedPath);
            var modelType = (_exportMeshes && _meshPathSet.Contains(normalizedPath)) ? "Mesh" : isBone ? "LimbNode" : "Null";
            var fbxName = GetUniqueFbxName(frame.Name);

            var model = N("Model");
            model.AddProperty(new LongToken(id));
            model.AddProperty(new StringToken(FormatObjectName(fbxName, "Model")));
            model.AddProperty(new StringToken(modelType));

            var props = N("Properties70");
            props.AddNode(MakeP("Lcl Translation", "Lcl Translation", "", "A+",
                zeroTransform ? 0 : (double)frame.LocalPosition.X,
                zeroTransform ? 0 : (double)frame.LocalPosition.Y,
                zeroTransform ? 0 : (double)frame.LocalPosition.Z));
            props.AddNode(MakeP("Lcl Rotation", "Lcl Rotation", "", "A+",
                zeroTransform ? 0 : (double)frame.LocalRotation.X,
                zeroTransform ? 0 : (double)frame.LocalRotation.Y,
                zeroTransform ? 0 : (double)frame.LocalRotation.Z));
            props.AddNode(MakeP("Lcl Scaling", "Lcl Scaling", "", "A+",
                (double)frame.LocalScale.X, (double)frame.LocalScale.Y, (double)frame.LocalScale.Z));
            model.AddNode(props);

            _objects.AddNode(model);
            Connect(id, parentId);

            if (isBone)
            {
                _exportedBoneCount++;
                ExportSkeletonAttribute(fbxName, id);
            }

            for (int i = 0; i < frame.Count; i++)
                ExportFrame(frame[i], id, globalMatrix);
        }

        private void ExportSkeletonAttribute(string name, long modelId)
        {
            var attrId = GenId();
            var attr = N("NodeAttribute");
            attr.AddProperty(new LongToken(attrId));
            attr.AddProperty(new StringToken(FormatObjectName(name, "NodeAttribute")));
            attr.AddProperty(new StringToken("LimbNode"));
            AddSimpleNode(attr, "TypeFlags", "Skeleton");

            var props = N("Properties70");
            var size = N("P");
            size.AddProperty(new StringToken("Size"));
            size.AddProperty(new StringToken("double"));
            size.AddProperty(new StringToken("Number"));
            size.AddProperty(new StringToken(""));
            size.AddProperty(new DoubleToken(_boneSize));
            props.AddNode(size);
            attr.AddNode(props);

            _objects.AddNode(attr);
            Connect(attrId, modelId);
        }

        private void ExportBindPose()
        {
            if (_frameIdMap.Count == 0 && _extraMeshModelIds.Count == 0)
                return;

            var poseNodes = new List<(long NodeId, System.Numerics.Matrix4x4 Matrix)>(_frameIdMap.Count + _extraMeshModelIds.Count);
            var includedIds = new HashSet<long>();

            foreach (var item in _frameIdMap)
            {
                if (_bindPoseMatrixByModelId.TryGetValue(item.Value, out var bindMat))
                {
                    if (includedIds.Add(item.Value))
                        poseNodes.Add((item.Value, bindMat));
                }
                else if (_frameGlobalMatrixMap.TryGetValue(item.Key, out var globalMat))
                {
                    if (includedIds.Add(item.Value))
                        poseNodes.Add((item.Value, globalMat));
                }
            }

            for (int i = 0; i < _extraMeshModelIds.Count; i++)
            {
                long extraId = _extraMeshModelIds[i];
                if (includedIds.Add(extraId))
                {
                    var mat = _bindPoseMatrixByModelId.TryGetValue(extraId, out var bindMat)
                        ? bindMat
                        : System.Numerics.Matrix4x4.Identity;
                    poseNodes.Add((extraId, mat));
                }
            }

            if (poseNodes.Count == 0)
                return;

            var pose = N("Pose");
            pose.AddProperty(new LongToken(GenId()));
            pose.AddProperty(new StringToken(FormatObjectName("BindPose", "Pose")));
            pose.AddProperty(new StringToken("BindPose"));
            AddSimpleNode(pose, "Type", "BindPose");
            AddSimpleNode(pose, "Version", 100);
            AddSimpleNode(pose, "NbPoseNodes", poseNodes.Count);

            for (int i = 0; i < poseNodes.Count; i++)
            {
                var (nodeId, matrix) = poseNodes[i];
                var poseNode = N("PoseNode");

                var node = N("Node");
                node.AddProperty(new LongToken(nodeId));
                poseNode.AddNode(node);

                var matrixNode = N("Matrix");
                matrixNode.AddProperty(new DoubleArrayToken(NumericsToFbxArray(matrix)));
                poseNode.AddNode(matrixNode);

                pose.AddNode(poseNode);
            }

            _objects.AddNode(pose);
        }

        private void ExportMesh(ImportedMesh mesh, IImported convert)
        {
            if (mesh.VertexList == null || mesh.VertexList.Count == 0)
                return;

            long modelId = 0;
            if (mesh.Path != null && TryGetFrameId(mesh.Path, out var meshModelId))
                modelId = meshModelId;

            bool isSkinned = _exportSkins && mesh.BoneList != null && mesh.BoneList.Count > 0;
            var normalizedMeshPath = NormalizeFramePath(mesh.Path);

            // If this is a skinned mesh whose path points to an ancestor of its own bones (e.g. RootFrame),
            // create a dedicated child Model node of type "Mesh" under that ancestor so the FBX hierarchy is strictly acyclic!
            if (modelId != 0 && isSkinned && IsAncestorOfAnyBone(normalizedMeshPath))
            {
                long parentModelId = modelId;
                modelId = GenId();
                _exportedFrameCount++;
                var meshName = string.IsNullOrEmpty(mesh.Path) ? "CombinedMesh" : $"{mesh.Path}_Mesh";
                var lastSlash = meshName.LastIndexOf('/');
                if (lastSlash >= 0) meshName = meshName.Substring(lastSlash + 1);
                var fbxName = GetUniqueFbxName(meshName);

                var model = N("Model");
                model.AddProperty(new LongToken(modelId));
                model.AddProperty(new StringToken(FormatObjectName(fbxName, "Model")));
                model.AddProperty(new StringToken("Mesh"));

                var props = N("Properties70");
                props.AddNode(MakeP("Lcl Translation", "Lcl Translation", "", "A+", 0, 0, 0));
                props.AddNode(MakeP("Lcl Rotation", "Lcl Rotation", "", "A+", 0, 0, 0));
                props.AddNode(MakeP("Lcl Scaling", "Lcl Scaling", "", "A+", 1, 1, 1));
                model.AddNode(props);

                _objects.AddNode(model);
                Connect(modelId, parentModelId);
                _extraMeshModelIds.Add(modelId);
                _bindPoseMatrixByModelId[modelId] = GetFrameMatrix(mesh.Path, System.Numerics.Matrix4x4.Identity);
            }
            else if (modelId == 0)
            {
                modelId = GenId();
                _exportedFrameCount++;
                var meshName = mesh.Path ?? "UnnamedMesh";
                var lastSlash = meshName.LastIndexOf('/');
                if (lastSlash >= 0) meshName = meshName.Substring(lastSlash + 1);
                var fbxName = GetUniqueFbxName(meshName);

                var model = N("Model");
                model.AddProperty(new LongToken(modelId));
                model.AddProperty(new StringToken(FormatObjectName(fbxName, "Model")));
                model.AddProperty(new StringToken("Mesh"));

                var props = N("Properties70");
                props.AddNode(MakeP("Lcl Translation", "Lcl Translation", "", "A+", 0, 0, 0));
                props.AddNode(MakeP("Lcl Rotation", "Lcl Rotation", "", "A+", 0, 0, 0));
                props.AddNode(MakeP("Lcl Scaling", "Lcl Scaling", "", "A+", 1, 1, 1));
                model.AddNode(props);

                _objects.AddNode(model);
                Connect(modelId, 0); // connect to root
                _extraMeshModelIds.Add(modelId);
                _bindPoseMatrixByModelId[modelId] = System.Numerics.Matrix4x4.Identity;
            }

            var geoId = GenId();
            _exportedMeshCount++;
            if (mesh.Path != null)
                _meshIdMap[mesh.Path] = geoId;

            var geo = N("Geometry");
            geo.AddProperty(new LongToken(geoId));
            geo.AddProperty(new StringToken(FormatObjectName(mesh.Path, "Geometry")));
            geo.AddProperty(new StringToken("Mesh"));

            BuildVertices(geo, mesh);
            var resolvedFaces = BuildPolygonVertexIndex(geo, mesh);

            if (mesh.hasNormal)
                BuildLayerElementNormal(geo, mesh, resolvedFaces.Indices);
            if (mesh.hasUV != null)
            {
                for (int uvIndex = 0; uvIndex < mesh.hasUV.Length; uvIndex++)
                {
                    if (mesh.hasUV[uvIndex])
                        BuildLayerElementUV(geo, mesh, uvIndex, resolvedFaces.Indices);
                }
            }
            if (mesh.hasTangent)
            {
                BuildLayerElementTangent(geo, mesh, resolvedFaces.Indices);
                BuildLayerElementBinormal(geo, mesh, resolvedFaces.Indices);
            }
            if (mesh.hasColor)
                BuildLayerElementColor(geo, mesh, resolvedFaces.Indices);

            List<string>? materialOrder = null;
            if (mesh.SubmeshList != null && mesh.SubmeshList.Count > 0)
            {
                materialOrder = GetMeshMaterialOrder(mesh);
                BuildLayerElementMaterial(geo, mesh, materialOrder, resolvedFaces.ValidFacesPerSubmesh, resolvedFaces.ValidFaceCount);
            }

            BuildLayer(geo, mesh);
            _objects.AddNode(geo);

            Connect(geoId, modelId);

            ConnectMaterialsToModel(materialOrder, modelId, convert);

            if (_exportSkins && mesh.BoneList != null && mesh.BoneList.Count > 0)
                ExportSkin(mesh, geoId);
        }

        private readonly struct ResolvedMeshFaces
        {
            public ResolvedMeshFaces(int[] indices, int[] validFacesPerSubmesh, int validFaceCount)
            {
                Indices = indices;
                ValidFacesPerSubmesh = validFacesPerSubmesh;
                ValidFaceCount = validFaceCount;
            }

            public int[] Indices { get; }
            public int[] ValidFacesPerSubmesh { get; }
            public int ValidFaceCount { get; }
        }

        private void BuildVertices(FbxNode geo, ImportedMesh mesh)
        {
            var vertexList = mesh.VertexList;
            int vertexCount = vertexList.Count;
            var verts = new double[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                var v = vertexList[i].Vertex;
                int offset = i * 3;
                verts[offset] = SanitizeFinite(v.X);
                verts[offset + 1] = SanitizeFinite(v.Y);
                verts[offset + 2] = SanitizeFinite(v.Z);
            }
            var n = N("Vertices");
            n.AddProperty(new DoubleArrayToken(verts));
            geo.AddNode(n);
        }

        private ResolvedMeshFaces BuildPolygonVertexIndex(FbxNode geo, ImportedMesh mesh)
        {
            var submeshList = mesh.SubmeshList;
            int submeshCount = submeshList?.Count ?? 0;
            int totalFaceCount = GetFaceCount(mesh);
            int vertexCount = mesh.VertexList.Count;

            var polygonIndices = new int[totalFaceCount * 3];
            var resolvedIndices = new int[totalFaceCount * 3];
            var validFacesPerSubmesh = new int[submeshCount];
            int writePos = 0;
            int skippedFaces = 0;

            var meshDiag = new MeshDiagnosticInfo
            {
                Path = mesh.Path ?? "<no path>",
                VertexCount = vertexCount,
                PolygonCount = 0,
                Submeshes = new List<SubmeshDiagnosticInfo>(submeshCount)
            };

            for (int subIdx = 0; submeshList != null && subIdx < submeshCount; subIdx++)
            {
                var sub = submeshList[subIdx];
                var faceList = sub.FaceList;
                int subFaceCount = faceList?.Count ?? 0;
                int baseVertex = sub.BaseVertex;
                int validSubFaces = 0;

                var subDiag = new SubmeshDiagnosticInfo
                {
                    Index = subIdx,
                    BaseVertex = baseVertex,
                    Material = sub.Material ?? "<none>",
                    IndexCount = subFaceCount * 3
                };

                for (int f = 0; faceList != null && f < subFaceCount; f++)
                {
                    if (!TryResolveFaceIndices(faceList[f], baseVertex, vertexCount, out var index0, out var index1, out var index2))
                    {
                        skippedFaces++;
                        continue;
                    }

                    resolvedIndices[writePos] = index0;
                    resolvedIndices[writePos + 1] = index1;
                    resolvedIndices[writePos + 2] = index2;

                    polygonIndices[writePos] = index0;
                    polygonIndices[writePos + 1] = index1;
                    polygonIndices[writePos + 2] = ~index2;
                    writePos += 3;
                    validSubFaces++;
                    meshDiag.PolygonCount++;

                    if (index0 < subDiag.MinIndex) subDiag.MinIndex = index0;
                    if (index1 < subDiag.MinIndex) subDiag.MinIndex = index1;
                    if (index2 < subDiag.MinIndex) subDiag.MinIndex = index2;

                    if (index0 > subDiag.MaxIndex) subDiag.MaxIndex = index0;
                    if (index1 > subDiag.MaxIndex) subDiag.MaxIndex = index1;
                    if (index2 > subDiag.MaxIndex) subDiag.MaxIndex = index2;

                    if (index0 < meshDiag.MinIndex) meshDiag.MinIndex = index0;
                    if (index1 < meshDiag.MinIndex) meshDiag.MinIndex = index1;
                    if (index2 < meshDiag.MinIndex) meshDiag.MinIndex = index2;

                    if (index0 > meshDiag.MaxIndex) meshDiag.MaxIndex = index0;
                    if (index1 > meshDiag.MaxIndex) meshDiag.MaxIndex = index1;
                    if (index2 > meshDiag.MaxIndex) meshDiag.MaxIndex = index2;
                }

                validFacesPerSubmesh[subIdx] = validSubFaces;

                if (subDiag.MaxIndex >= subDiag.MinIndex)
                {
                    subDiag.FirstVertex = subDiag.MinIndex;
                    subDiag.VertexCount = subDiag.MaxIndex - subDiag.MinIndex + 1;
                }
                else
                {
                    subDiag.FirstVertex = 0;
                    subDiag.VertexCount = 0;
                }

                meshDiag.Submeshes.Add(subDiag);
            }

            if (writePos != polygonIndices.Length)
            {
                Array.Resize(ref polygonIndices, writePos);
                Array.Resize(ref resolvedIndices, writePos);
            }

            meshDiag.SkippedFaces = skippedFaces;
            _meshDiagnostics.Add(meshDiag);

            if (skippedFaces > 0)
                Logger.Warning($"Mesh '{mesh.Path}' skipped {skippedFaces} FBX faces because their vertex indices were outside the exported vertex buffer.");

            var n = N("PolygonVertexIndex");
            n.AddProperty(new IntegerArrayToken(polygonIndices));
            geo.AddNode(n);

            return new ResolvedMeshFaces(resolvedIndices, validFacesPerSubmesh, writePos / 3);
        }

        private static bool TryResolveFaceIndices(ImportedFace face, int baseVertex, int vertexCount, out int index0, out int index1, out int index2)
        {
            index0 = index1 = index2 = 0;
            if (face?.VertexIndices == null || face.VertexIndices.Length < 3)
                return false;

            index0 = face.VertexIndices[0] + baseVertex;
            index1 = face.VertexIndices[1] + baseVertex;
            index2 = face.VertexIndices[2] + baseVertex;
            if (IsValidIndex(index0, vertexCount) && IsValidIndex(index1, vertexCount) && IsValidIndex(index2, vertexCount))
                return true;

            index0 = face.VertexIndices[0];
            index1 = face.VertexIndices[1];
            index2 = face.VertexIndices[2];
            return IsValidIndex(index0, vertexCount) && IsValidIndex(index1, vertexCount) && IsValidIndex(index2, vertexCount);
        }

        private static bool IsValidIndex(int index, int vertexCount)
        {
            return (uint)index < (uint)vertexCount;
        }

        private void BuildLayerElementNormal(FbxNode geo, ImportedMesh mesh, int[] resolvedIndices)
        {
            var layer = N("LayerElementNormal");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByPolygonVertex");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var vertexList = mesh.VertexList;
            int indexCount = resolvedIndices.Length;
            var normalData = new double[indexCount * 3];
            for (int i = 0; i < indexCount; i++)
            {
                var n = vertexList[resolvedIndices[i]].Normal;
                int offset = i * 3;
                normalData[offset] = SanitizeFinite(n.X);
                normalData[offset + 1] = SanitizeFinite(n.Y);
                normalData[offset + 2] = SanitizeFinite(n.Z);
            }

            var normNode = N("Normals");
            normNode.AddProperty(new DoubleArrayToken(normalData));
            layer.AddNode(normNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementUV(FbxNode geo, ImportedMesh mesh, int uvIndex, int[] resolvedIndices)
        {
            var layer = N("LayerElementUV");
            layer.AddProperty(new IntegerToken(uvIndex));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", uvIndex == 0 ? "UVMap" : $"UVMap_{uvIndex}");
            AddSimpleNode(layer, "MappingInformationType", "ByPolygonVertex");
            AddSimpleNode(layer, "ReferenceInformationType", "IndexToDirect");

            var vertexList = mesh.VertexList;
            int vertexCount = vertexList.Count;
            var uvData = new double[vertexCount * 2];
            for (int i = 0; i < vertexCount; i++)
            {
                var uvChannels = vertexList[i].UV;
                int offset = i * 2;
                if (uvChannels != null && uvIndex < uvChannels.Length && uvChannels[uvIndex] != null && uvChannels[uvIndex].Length >= 2)
                {
                    uvData[offset] = SanitizeFinite(uvChannels[uvIndex][0]);
                    uvData[offset + 1] = SanitizeFinite(uvChannels[uvIndex][1]);
                }
            }
            var uvNode = N("UV");
            uvNode.AddProperty(new DoubleArrayToken(uvData));
            layer.AddNode(uvNode);

            var uvIndexNode = N("UVIndex");
            uvIndexNode.AddProperty(new IntegerArrayToken(resolvedIndices));
            layer.AddNode(uvIndexNode);

            geo.AddNode(layer);
        }

        private void BuildLayerElementTangent(FbxNode geo, ImportedMesh mesh, int[] resolvedIndices)
        {
            var layer = N("LayerElementTangent");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByPolygonVertex");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var vertexList = mesh.VertexList;
            int indexCount = resolvedIndices.Length;
            var tangentData = new double[indexCount * 3];
            for (int i = 0; i < indexCount; i++)
            {
                var t = vertexList[resolvedIndices[i]].Tangent;
                int offset = i * 3;
                tangentData[offset] = SanitizeFinite(t.X);
                tangentData[offset + 1] = SanitizeFinite(t.Y);
                tangentData[offset + 2] = SanitizeFinite(t.Z);
            }

            var tangNode = N("Tangents");
            tangNode.AddProperty(new DoubleArrayToken(tangentData));
            layer.AddNode(tangNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementBinormal(FbxNode geo, ImportedMesh mesh, int[] resolvedIndices)
        {
            var layer = N("LayerElementBinormal");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByPolygonVertex");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var vertexList = mesh.VertexList;
            int indexCount = resolvedIndices.Length;
            var binormalData = new double[indexCount * 3];
            for (int i = 0; i < indexCount; i++)
            {
                var v = vertexList[resolvedIndices[i]];
                var n = v.Normal;
                var t = v.Tangent;
                double tw = SanitizeFinite(t.W);
                if (Math.Abs(tw) < 1e-6) tw = 1.0;
                double bx = ((double)n.Y * t.Z - (double)n.Z * t.Y) * tw;
                double by = ((double)n.Z * t.X - (double)n.X * t.Z) * tw;
                double bz = ((double)n.X * t.Y - (double)n.Y * t.X) * tw;
                int offset = i * 3;
                binormalData[offset] = SanitizeFinite(bx);
                binormalData[offset + 1] = SanitizeFinite(by);
                binormalData[offset + 2] = SanitizeFinite(bz);
            }

            var binormNode = N("Binormals");
            binormNode.AddProperty(new DoubleArrayToken(binormalData));
            layer.AddNode(binormNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementColor(FbxNode geo, ImportedMesh mesh, int[] resolvedIndices)
        {
            var layer = N("LayerElementColor");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByPolygonVertex");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var vertexList = mesh.VertexList;
            int indexCount = resolvedIndices.Length;
            var colorData = new double[indexCount * 4];
            for (int i = 0; i < indexCount; i++)
            {
                var c = vertexList[resolvedIndices[i]].Color;
                int offset = i * 4;
                colorData[offset] = SanitizeFinite(c.R);
                colorData[offset + 1] = SanitizeFinite(c.G);
                colorData[offset + 2] = SanitizeFinite(c.B);
                colorData[offset + 3] = SanitizeFinite(c.A);
            }

            var colorNode = N("Colors");
            colorNode.AddProperty(new DoubleArrayToken(colorData));
            layer.AddNode(colorNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementMaterial(FbxNode geo, ImportedMesh mesh, List<string> materialOrder, int[] validFacesPerSubmesh, int validFaceCount)
        {
            var layer = N("LayerElementMaterial");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByPolygon");
            AddSimpleNode(layer, "ReferenceInformationType", "IndexToDirect");

            var materialIndexByName = new Dictionary<string, int>(materialOrder.Count);
            for (var i = 0; i < materialOrder.Count; i++)
                materialIndexByName[materialOrder[i]] = i;

            var materialIndices = new int[validFaceCount];
            int writePos = 0;
            var submeshList = mesh.SubmeshList;
            for (int subIdx = 0; subIdx < submeshList.Count; subIdx++)
            {
                var sub = submeshList[subIdx];
                var materialIndex = 0;
                if (!string.IsNullOrEmpty(sub.Material) && materialIndexByName.TryGetValue(sub.Material, out var index))
                    materialIndex = index;

                int subValidFaces = validFacesPerSubmesh[subIdx];
                for (int f = 0; f < subValidFaces; f++)
                {
                    materialIndices[writePos++] = materialIndex;
                }
            }

            var matIdx = N("Materials");
            matIdx.AddProperty(new IntegerArrayToken(materialIndices));
            layer.AddNode(matIdx);
            geo.AddNode(layer);
        }

        private void BuildLayer(FbxNode geo, ImportedMesh mesh)
        {
            var layer = N("Layer");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 100);

            if (mesh.hasNormal)
                AddLayerRef(layer, "LayerElementNormal");
            if (mesh.hasUV != null && mesh.hasUV.Length > 0 && mesh.hasUV[0])
                AddLayerRef(layer, "LayerElementUV", 0);
            if (mesh.hasTangent)
            {
                AddLayerRef(layer, "LayerElementTangent");
                AddLayerRef(layer, "LayerElementBinormal");
            }
            if (mesh.hasColor)
                AddLayerRef(layer, "LayerElementColor");
            if (mesh.SubmeshList != null && mesh.SubmeshList.Count > 0)
                AddLayerRef(layer, "LayerElementMaterial");

            geo.AddNode(layer);

            if (mesh.hasUV != null)
            {
                for (int uvIndex = 1; uvIndex < mesh.hasUV.Length; uvIndex++)
                {
                    if (mesh.hasUV[uvIndex])
                    {
                        var uvLayer = N("Layer");
                        uvLayer.AddProperty(new IntegerToken(uvIndex));
                        AddSimpleNode(uvLayer, "Version", 100);
                        AddLayerRef(uvLayer, "LayerElementUV", uvIndex);
                        geo.AddNode(uvLayer);
                    }
                }
            }
        }

        private void AddLayerRef(FbxNode layer, string typeName, int index = 0)
        {
            var el = N("LayerElement");
            AddSimpleNode(el, "Type", typeName);
            var idx = N("TypedIndex"); idx.AddProperty(new IntegerToken(index));
            el.AddNode(idx);
            layer.AddNode(el);
        }

        private void ConnectMaterialsToModel(List<string>? materialOrder, long modelId, IImported convert)
        {
            if (modelId == 0 || convert.MaterialList == null || materialOrder == null)
                return;

            for (int i = 0; i < materialOrder.Count; i++)
            {
                if (_materialIdMap.TryGetValue(materialOrder[i], out var matId))
                    Connect(matId, modelId);
            }
        }

        private static List<string> GetMeshMaterialOrder(ImportedMesh mesh)
        {
            var submeshList = mesh.SubmeshList;
            var materials = new List<string>(submeshList.Count);
            var seen = new HashSet<string>();
            for (int i = 0; i < submeshList.Count; i++)
            {
                var matName = submeshList[i].Material;
                if (string.IsNullOrEmpty(matName) || !seen.Add(matName))
                    continue;

                materials.Add(matName);
            }

            return materials;
        }

        private void ExportSkin(ImportedMesh mesh, long geoId)
        {
            var skinId = GenId();
            _exportedSkinCount++;
            var skin = N("Deformer");
            skin.AddProperty(new LongToken(skinId));
            skin.AddProperty(new StringToken(FormatObjectName("Skin", "Deformer")));
            skin.AddProperty(new StringToken("Skin"));
            AddSimpleNode(skin, "Version", 101);
            AddSimpleNode(skin, "Link_DeformAcuracy", 50.0);
            _objects.AddNode(skin);
            Connect(skinId, geoId);

            var meshBindMatrix = GetFrameMatrix(mesh.Path, System.Numerics.Matrix4x4.Identity);
            if (mesh.Path != null && TryGetFrameId(mesh.Path, out var meshFrameId))
            {
                _bindPoseMatrixByModelId[meshFrameId] = meshBindMatrix;
            }

            var boneList = mesh.BoneList;
            if (boneList == null || boneList.Count == 0)
                return;
            var vertexList = mesh.VertexList;
            int boneCount = boneList.Count;
            int vertexCount = vertexList.Count;

            // Single-pass count per bone with per-vertex duplicate bone slot deduplication and no 4-weight cap
            var influenceCounts = new int[boneCount];
            for (int v = 0; v < vertexCount; v++)
            {
                var vert = vertexList[v];
                var boneIndices = vert.BoneIndices;
                var weights = vert.Weights;
                if (boneIndices == null || weights == null) continue;
                int weightSlots = boneIndices.Length < weights.Length ? boneIndices.Length : weights.Length;
                for (int w = 0; w < weightSlots; w++)
                {
                    float weight = weights[w];
                    if (weight > 0f && !float.IsNaN(weight) && !float.IsInfinity(weight))
                    {
                        int bIdx = boneIndices[w];
                        if ((uint)bIdx < (uint)boneCount && boneList[bIdx].Path != null)
                        {
                            bool duplicate = false;
                            for (int p = 0; p < w; p++)
                            {
                                if (boneIndices[p] == bIdx && weights[p] > 0f && !float.IsNaN(weights[p]) && !float.IsInfinity(weights[p]))
                                {
                                    duplicate = true;
                                    break;
                                }
                            }
                            if (!duplicate)
                            {
                                influenceCounts[bIdx]++;
                            }
                        }
                    }
                }
            }

            var boneVertexIndices = new int[boneCount][];
            var boneVertexWeights = new double[boneCount][];
            var writeCursor = new int[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                int count = influenceCounts[b];
                if (count > 0)
                {
                    boneVertexIndices[b] = new int[count];
                    boneVertexWeights[b] = new double[count];
                }
            }

            for (int v = 0; v < vertexCount; v++)
            {
                var vert = vertexList[v];
                var boneIndices = vert.BoneIndices;
                var weights = vert.Weights;
                if (boneIndices == null || weights == null) continue;
                int weightSlots = boneIndices.Length < weights.Length ? boneIndices.Length : weights.Length;
                for (int w = 0; w < weightSlots; w++)
                {
                    float weight = weights[w];
                    if (weight > 0f && !float.IsNaN(weight) && !float.IsInfinity(weight))
                    {
                        int bIdx = boneIndices[w];
                        if ((uint)bIdx < (uint)boneCount && boneVertexIndices[bIdx] != null)
                        {
                            bool duplicate = false;
                            for (int p = 0; p < w; p++)
                            {
                                if (boneIndices[p] == bIdx && weights[p] > 0f && !float.IsNaN(weights[p]) && !float.IsInfinity(weights[p]))
                                {
                                    duplicate = true;
                                    break;
                                }
                            }
                            if (duplicate)
                            {
                                continue;
                            }

                            double totalWeight = weight;
                            for (int p = w + 1; p < weightSlots; p++)
                            {
                                if (boneIndices[p] == bIdx && weights[p] > 0f && !float.IsNaN(weights[p]) && !float.IsInfinity(weights[p]))
                                {
                                    totalWeight += weights[p];
                                }
                            }

                            int pos = writeCursor[bIdx]++;
                            boneVertexIndices[bIdx][pos] = v;
                            boneVertexWeights[bIdx][pos] = totalWeight;
                        }
                    }
                }
            }

            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = boneList[boneIdx];
                if (bone.Path == null)
                    continue;

                var linkMatrix = GetBindPoseLinkMatrix(meshBindMatrix, bone);
                if (TryGetFrameId(bone.Path, out var boneModelId))
                {
                    _bindPoseMatrixByModelId[boneModelId] = linkMatrix;
                }

                _candidateClusterCount++;

                if (influenceCounts[boneIdx] == 0)
                {
                    _omittedClusterCount++;
                    Logger.Verbose($"Omitted zero-weight skin cluster for mesh '{mesh.Path}' and bone '{bone.Path}'.");
                    continue;
                }

                var clusterId = GenId();
                _exportedClusterCount++;
                var cluster = N("Deformer");
                cluster.AddProperty(new LongToken(clusterId));
                cluster.AddProperty(new StringToken(FormatObjectName(bone.Path, "SubDeformer")));
                cluster.AddProperty(new StringToken("Cluster"));
                AddSimpleNode(cluster, "Version", 100);
                AddSimpleNode(cluster, "Mode", "Total1");
                AddSimpleNode(cluster, "UserData", "");

                var idxNode = N("Indexes");
                idxNode.AddProperty(new IntegerArrayToken(boneVertexIndices[boneIdx]));
                cluster.AddNode(idxNode);

                var wNode = N("Weights");
                wNode.AddProperty(new DoubleArrayToken(boneVertexWeights[boneIdx]));
                cluster.AddNode(wNode);

                var clusterTransform = NumericsToFbxArray(UnityBindPoseToNumerics(bone.Matrix));
                var transformLink = NumericsToFbxArray(linkMatrix);

                var tNode = N("Transform");
                tNode.AddProperty(new DoubleArrayToken(clusterTransform));
                cluster.AddNode(tNode);

                var tlNode = N("TransformLink");
                tlNode.AddProperty(new DoubleArrayToken(transformLink));
                cluster.AddNode(tlNode);

                _objects.AddNode(cluster);
                Connect(clusterId, skinId);

                if (boneModelId != 0)
                    Connect(boneModelId, clusterId);
            }
        }

        private void ExportMaterial(ImportedMaterial mat, IImported convert)
        {
            var id = GenId();
            _exportedMaterialCount++;
            _materialIdMap[mat.Name] = id;

            var matNode = N("Material");
            matNode.AddProperty(new LongToken(id));
            matNode.AddProperty(new StringToken(FormatObjectName(mat.Name, "Material")));
            matNode.AddProperty(new StringToken(""));

            AddSimpleNode(matNode, "Version", 102);
            AddSimpleNode(matNode, "ShadingModel", "Phong");

            var props = N("Properties70");
            props.AddNode(MakeColorP("DiffuseColor", mat.Diffuse));
            props.AddNode(MakeColorP("AmbientColor", mat.Ambient));
            props.AddNode(MakeColorP("EmissiveColor", mat.Emissive));
            props.AddNode(MakeColorP("SpecularColor", mat.Specular));
            props.AddNode(MakeNumberP("Shininess", SanitizeFinite(mat.Shininess)));
            props.AddNode(MakeNumberP("TransparencyFactor", SanitizeFinite(mat.Transparency)));

            matNode.AddNode(props);
            _objects.AddNode(matNode);

            ExportMaterialTextures(mat, id);
        }

        private static readonly string[] FallbackMaterialTextureSlots =
        {
            "DiffuseFactor",
            "SpecularFactor",
            "EmissiveFactor",
            "AmbientFactor",
            "ReflectionColor",
            "ShininessExponent",
            "TransparencyFactor",
            "TransparentColor",
            "Bump"
        };

        private void ExportMaterialTextures(ImportedMaterial mat, long materialId)
        {
            if (mat.Textures == null)
                return;

            bool hasExplicitDiffuse = false;
            for (int i = 0; i < mat.Textures.Count; i++)
            {
                if (!string.IsNullOrEmpty(mat.Textures[i].Name) && mat.Textures[i].Dest == 0)
                {
                    hasExplicitDiffuse = true;
                    break;
                }
            }

            var usedSlots = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < mat.Textures.Count; i++)
            {
                var matTex = mat.Textures[i];
                if (string.IsNullOrEmpty(matTex.Name))
                    continue;

                _sourceTextureMap.TryGetValue(matTex.Name, out var texture);
                long textureId;
                if (texture != null)
                {
                    textureId = ExportTexture(texture, matTex.Offset, matTex.Scale);
                }
                else
                {
                    using (var ms = new MemoryStream())
                    {
                        var dummyTex = new ImportedTexture(ms, matTex.Name)
                        {
                            Data = null
                        };
                        textureId = ExportTexture(dummyTex, matTex.Offset, matTex.Scale);
                    }
                }

                string slotName = GetMaterialTextureProperty(matTex.Dest);
                if (matTex.Dest < 0)
                {
                    if (!hasExplicitDiffuse && !usedSlots.Contains("DiffuseColor"))
                    {
                        slotName = "DiffuseColor";
                    }
                    else
                    {
                        slotName = PickUnusedMaterialSlot(usedSlots);
                    }
                }
                else if (usedSlots.Contains(slotName))
                {
                    slotName = PickUnusedMaterialSlot(usedSlots);
                }

                usedSlots.Add(slotName);
                ConnectProperty(textureId, materialId, slotName);
            }
        }

        private static string PickUnusedMaterialSlot(HashSet<string> usedSlots)
        {
            for (int i = 0; i < FallbackMaterialTextureSlots.Length; i++)
            {
                if (!usedSlots.Contains(FallbackMaterialTextureSlots[i]))
                {
                    return FallbackMaterialTextureSlots[i];
                }
            }

            return "DiffuseFactor";
        }

        private void BuildSourceTextureMap(List<ImportedTexture> textureList)
        {
            _sourceTextureMap.Clear();
            if (textureList == null)
                return;

            for (int i = 0; i < textureList.Count; i++)
            {
                var tex = textureList[i];
                if (tex != null && !string.IsNullOrEmpty(tex.Name) && !_sourceTextureMap.ContainsKey(tex.Name))
                {
                    _sourceTextureMap[tex.Name] = tex;
                }
            }
        }

        private void BuildFrameLookupMaps(ImportedFrame rootFrame)
        {
            _frameByPathMap.Clear();
            _frameUniqueLeafMap.Clear();
            _frameUniqueSuffixMap.Clear();

            if (rootFrame == null)
                return;

            var visited = new HashSet<ImportedFrame>();
            var stack = new Stack<ImportedFrame>();
            stack.Push(rootFrame);

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame == null || !visited.Add(frame))
                    continue;

                var normalizedPath = NormalizeFramePath(frame.Path);
                if (!string.IsNullOrEmpty(normalizedPath))
                {
                    if (!_frameByPathMap.ContainsKey(normalizedPath))
                        _frameByPathMap[normalizedPath] = frame;

                    // Register all slash-separated suffixes for O(1) unique suffix lookup
                    int slashIdx = normalizedPath.IndexOf('/');
                    while (slashIdx >= 0 && slashIdx + 1 < normalizedPath.Length)
                    {
                        var suffix = normalizedPath.Substring(slashIdx + 1);
                        if (_frameUniqueSuffixMap.TryGetValue(suffix, out var existing))
                        {
                            if (existing != null && !string.Equals(existing, normalizedPath, StringComparison.Ordinal))
                                _frameUniqueSuffixMap[suffix] = null; // ambiguous
                        }
                        else
                        {
                            _frameUniqueSuffixMap[suffix] = normalizedPath;
                        }
                        slashIdx = normalizedPath.IndexOf('/', slashIdx + 1);
                    }
                }

                if (!string.IsNullOrEmpty(frame.Name))
                {
                    if (_frameUniqueLeafMap.TryGetValue(frame.Name, out var existingLeaf))
                    {
                        if (existingLeaf != null && !ReferenceEquals(existingLeaf, frame))
                            _frameUniqueLeafMap[frame.Name] = null; // ambiguous
                    }
                    else
                    {
                        _frameUniqueLeafMap[frame.Name] = frame;
                    }
                }

                for (int i = frame.Count - 1; i >= 0; i--)
                    stack.Push(frame[i]);
            }
        }

        private ImportedFrame? FindFrameFast(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
                return null;

            if (_frameByPathMap.TryGetValue(normalizedPath, out var exactFrame))
                return exactFrame;

            if (_frameUniqueSuffixMap.TryGetValue(normalizedPath, out var canonicalPath)
                && canonicalPath != null
                && _frameByPathMap.TryGetValue(canonicalPath, out var suffixFrame))
            {
                return suffixFrame;
            }

            if (normalizedPath.IndexOf('/') < 0
                && _frameUniqueLeafMap.TryGetValue(normalizedPath, out var leafFrame)
                && leafFrame != null)
            {
                return leafFrame;
            }

            return _rootFrame?.FindFrameByPath(normalizedPath);
        }

        private long ExportTexture(ImportedTexture texture, Vector2 offset = default, Vector2 scale = default)
        {
            if (_textureIdMap.TryGetValue(texture.Name, out var existingTextureId))
                return existingTextureId;

            var textureId = GenId();
            var videoId = GenId();
            _textureIdMap[texture.Name] = textureId;
            _videoIdMap[texture.Name] = videoId;

            var relativeFileName = FixTextureFileName(texture.Name);
            var fileName = string.IsNullOrEmpty(_exportDirectory)
                ? relativeFileName
                : Path.Combine(_exportDirectory, relativeFileName);

            if (texture.Data != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fileName) ?? ".");
                File.WriteAllBytes(fileName, texture.Data);
            }

            var texNode = N("Texture");
            texNode.AddProperty(new LongToken(textureId));
            texNode.AddProperty(new StringToken(FormatObjectName(texture.Name, "Texture")));
            texNode.AddProperty(new StringToken(""));
            AddSimpleNode(texNode, "Type", "TextureVideoClip");
            AddSimpleNode(texNode, "Version", 202);
            AddSimpleNode(texNode, "TextureName", FormatObjectName(texture.Name, "Texture"));
            AddSimpleNode(texNode, "Media", FormatObjectName(texture.Name, "Video"));
            AddSimpleNode(texNode, "FileName", fileName);
            AddSimpleNode(texNode, "RelativeFilename", relativeFileName);

            double scaleX = (scale.X == 0f && scale.Y == 0f) ? 1.0 : SanitizeFinite(scale.X);
            double scaleY = (scale.X == 0f && scale.Y == 0f) ? 1.0 : SanitizeFinite(scale.Y);
            var texProps = N("Properties70");
            texProps.AddNode(MakeP("Translation", "Vector", "", "A", SanitizeFinite(offset.X), SanitizeFinite(offset.Y), 0.0));
            texProps.AddNode(MakeP("Scaling", "Vector", "", "A", scaleX, scaleY, 1.0));
            texNode.AddNode(texProps);

            _objects.AddNode(texNode);

            var videoNode = N("Video");
            videoNode.AddProperty(new LongToken(videoId));
            videoNode.AddProperty(new StringToken(FormatObjectName(texture.Name, "Video")));
            videoNode.AddProperty(new StringToken("Clip"));
            AddSimpleNode(videoNode, "Type", "Clip");
            AddSimpleNode(videoNode, "UseMipMap", 0);
            AddSimpleNode(videoNode, "Filename", fileName);
            AddSimpleNode(videoNode, "RelativeFilename", relativeFileName);
            _objects.AddNode(videoNode);

            _exportedTextureCount++;
            Connect(videoId, textureId);
            return textureId;
        }

        private static string FixTextureFileName(string name)
        {
            return AssetExportHelper.FixFileName(name);
        }

        private static int GetFaceCount(ImportedMesh mesh)
        {
            var count = 0;
            if (mesh.SubmeshList == null)
                return count;

            for (int i = 0; i < mesh.SubmeshList.Count; i++)
                count += mesh.SubmeshList[i].FaceList?.Count ?? 0;

            return count;
        }

        private static string GetMaterialTextureProperty(int dest)
        {
            switch (dest)
            {
                case 0:
                    return "DiffuseColor";
                case 1:
                    return "NormalMap";
                case 2:
                    return "SpecularColor";
                case 3:
                    return "ReflectionFactor";
                case 4:
                    return "EmissiveColor";
                case 5:
                    return "AmbientColor";
                case 6:
                    return "Bump";
                case 7:
                    return "DiffuseFactor";
                case 8:
                    return "ShininessExponent";
                case 9:
                    return "TransparencyFactor";
                default:
                    return "DiffuseFactor";
            }
        }

        private string GetUniqueFbxName(string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "Node";

            if (!_fbxNameCounts.TryGetValue(name, out var count))
            {
                _fbxNameCounts[name] = 1;
                return name;
            }

            _fbxNameCounts[name] = count + 1;
            return $"{name}_{count}";
        }

        private void ExportAnimation(ImportedKeyframedAnimation anim)
        {
            if (anim.TrackList == null || anim.TrackList.Count == 0)
                return;

            var validTracks = new List<(ImportedAnimationKeyframedTrack Track, long ModelId, long BsChannelId)>(anim.TrackList.Count);
            var missingTrackCount = 0;
            float maxTime = 0;

            for (int tIdx = 0; tIdx < anim.TrackList.Count; tIdx++)
            {
                var track = anim.TrackList[tIdx];
                if (track.Path == null) continue;

                long modelId = 0;
                bool hasModel = TryGetFrameId(track.Path, out modelId);
                long bsChannelId = 0;
                if (track.BlendShape != null && track.BlendShape.Keyframes.Count > 0)
                {
                    var mapKey = track.Path + "::" + track.BlendShape.ChannelName;
                    if (!_blendShapeChannelMap.TryGetValue(mapKey, out bsChannelId))
                    {
                        var normTrackPath = NormalizeFramePath(track.Path);
                        if (!string.IsNullOrEmpty(normTrackPath))
                        {
                            _blendShapeChannelMap.TryGetValue(normTrackPath + "::" + track.BlendShape.ChannelName, out bsChannelId);
                        }
                    }
                }

                bool hasTrs = hasModel && (track.Translations.Count > 0 || track.Rotations.Count > 0 || track.Scalings.Count > 0);
                bool hasBs = bsChannelId != 0 && track.BlendShape != null && track.BlendShape.Keyframes.Count > 0;
                if (!hasTrs && !hasBs)
                {
                    if (!hasModel && (track.Translations.Count > 0 || track.Rotations.Count > 0 || track.Scalings.Count > 0 || (track.BlendShape != null && track.BlendShape.Keyframes.Count > 0)))
                    {
                        missingTrackCount++;
                        _missingAnimationTrackCount++;
                    }
                    continue;
                }

                for (int i = 0; i < track.Translations.Count; i++)
                {
                    float t = (float)SanitizeFinite(track.Translations[i].time);
                    if (t > maxTime) maxTime = t;
                }
                for (int i = 0; i < track.Rotations.Count; i++)
                {
                    float t = (float)SanitizeFinite(track.Rotations[i].time);
                    if (t > maxTime) maxTime = t;
                }
                for (int i = 0; i < track.Scalings.Count; i++)
                {
                    float t = (float)SanitizeFinite(track.Scalings[i].time);
                    if (t > maxTime) maxTime = t;
                }
                if (hasBs && track.BlendShape != null)
                {
                    var bsKeys = track.BlendShape.Keyframes;
                    for (int i = 0; i < bsKeys.Count; i++)
                    {
                        float t = (float)SanitizeFinite(bsKeys[i].time);
                        if (t > maxTime) maxTime = t;
                    }
                }

                validTracks.Add((track, modelId, bsChannelId));
            }

            if (validTracks.Count == 0)
            {
                Logger.Warning($"Animation '{anim.Name}' has {anim.TrackList.Count} tracks, but none matched the exported frame hierarchy.");
                return;
            }

            var stackId = GenId();
            _exportedAnimationStackCount++;
            var stack = N("AnimationStack");
            stack.AddProperty(new LongToken(stackId));
            stack.AddProperty(new StringToken(FormatObjectName(anim.Name, "AnimStack")));
            stack.AddProperty(new StringToken(""));

            var stackProps = N("Properties70");
            var fbxTime = (long)Math.Round(maxTime * 46186158000.0);
            _takes.Add((anim.Name, 0, fbxTime));
            stackProps.AddNode(MakeStringP("Description", ""));
            stackProps.AddNode(MakeTimeP("LocalStart", 0));
            stackProps.AddNode(MakeTimeP("LocalStop", fbxTime));
            stackProps.AddNode(MakeTimeP("ReferenceStart", 0));
            stackProps.AddNode(MakeTimeP("ReferenceStop", fbxTime));
            stack.AddNode(stackProps);
            _objects.AddNode(stack);

            var layerId = GenId();
            var layer = N("AnimationLayer");
            layer.AddProperty(new LongToken(layerId));
            layer.AddProperty(new StringToken(FormatObjectName("Base Layer", "AnimLayer")));
            layer.AddProperty(new StringToken(""));
            _objects.AddNode(layer);
            Connect(layerId, stackId);

            var exportedTrackCount = 0;
            for (int i = 0; i < validTracks.Count; i++)
            {
                var (track, modelId, bsChannelId) = validTracks[i];
                var exportedTrack = false;
                if (modelId != 0)
                {
                    if (track.Translations.Count > 0)
                    {
                        ExportCurveNode(layerId, modelId, "T", "Lcl Translation", track.Translations);
                        exportedTrack = true;
                    }
                    if (track.Rotations.Count > 0)
                    {
                        ExportCurveNode(layerId, modelId, "R", "Lcl Rotation", track.Rotations);
                        exportedTrack = true;
                    }
                    if (track.Scalings.Count > 0)
                    {
                        ExportCurveNode(layerId, modelId, "S", "Lcl Scaling", track.Scalings);
                        exportedTrack = true;
                    }
                }

                if (bsChannelId != 0 && track.BlendShape != null && track.BlendShape.Keyframes.Count > 0)
                {
                    ExportCurveNode1D(layerId, bsChannelId, track.BlendShape.ChannelName, "DeformPercent", track.BlendShape.Keyframes);
                    exportedTrack = true;
                }

                if (exportedTrack)
                {
                    _exportedAnimationTrackCount++;
                    exportedTrackCount++;
                }
            }

            if (missingTrackCount > 0)
                Logger.Warning($"Animation '{anim.Name}' exported {exportedTrackCount} tracks; {missingTrackCount} track paths did not match the exported frame hierarchy.");
        }

        private bool TryGetFrameId(string path, out long modelId)
        {
            modelId = 0;
            var normalizedPath = NormalizeFramePath(path);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                if (_rootFrame != null)
                {
                    var rootPath = NormalizeFramePath(_rootFrame.Path);
                    if (!string.IsNullOrEmpty(rootPath) && _frameIdMap.TryGetValue(rootPath, out modelId))
                    {
                        return true;
                    }
                }
                return false;
            }

            if (_frameIdMap.TryGetValue(normalizedPath, out modelId))
                return true;

            if (_frameUniqueSuffixMap.TryGetValue(normalizedPath, out var canonicalPath) && canonicalPath != null)
            {
                return _frameIdMap.TryGetValue(canonicalPath, out modelId);
            }

            var rootMatch = FindFrameFast(normalizedPath);
            if (rootMatch != null && _frameIdMap.TryGetValue(NormalizeFramePath(rootMatch.Path), out modelId))
                return true;

            return false;
        }

        private System.Numerics.Matrix4x4 GetBindPoseLinkMatrix(System.Numerics.Matrix4x4 meshBindMatrix, ImportedBone bone)
        {
            if (TryGetInverseBindPoseMatrix(bone.Matrix, out var inverseBindPose))
            {
                // In row-vector convention: P_row * B_row = M_row => B_row = inverse(P_row) * M_row
                return inverseBindPose * meshBindMatrix;
            }

            Logger.Warning($"Unable to invert bind pose for bone '{bone.Path}' while exporting FBX skin cluster; using frame transform.");
            return GetFrameMatrix(bone.Path, System.Numerics.Matrix4x4.Identity);
        }

        private static bool TryGetInverseBindPoseMatrix(Matrix4x4 matrix, out System.Numerics.Matrix4x4 inverseBindPose)
        {
            return System.Numerics.Matrix4x4.Invert(UnityBindPoseToNumerics(matrix), out inverseBindPose);
        }

        /// <summary>
        /// Converts a Unity deserialized bind pose Matrix4x4 into a System.Numerics.Matrix4x4.
        ///
        /// Mathematical Convention and Serialization Details:
        /// 1. Unity binary assets store serialized Matrix4x4f as 16 consecutive floats in row-major order:
        ///    e00, e01, e02, e03,
        ///    e10, e11, e12, e13,
        ///    e20, e21, e22, e23,
        ///    e30, e31, e32, e33
        ///    where the translation vector resides in elements e03, e13, e23 (or values[3], values[7], values[11]).
        /// 2. AssetStudio's Matrix4x4(float[] values) constructor populates internal storage column-by-column:
        ///    values[0]->M00, values[1]->M10, values[2]->M20, values[3]->M30 (row 3, col 0)
        ///    values[4]->M01, values[5]->M11, values[6]->M21, values[7]->M31 (row 3, col 1)
        ///    values[8]->M02, values[9]->M12, values[10]->M22, values[11]->M32 (row 3, col 2)
        ///    Consequently, translation is stored in AssetStudio.Matrix4x4 in row 3 (M30, M31, M32).
        /// 3. System.Numerics.Matrix4x4 follows row-vector convention (v' = v * M), where translation
        ///    is stored in the 4th row (M41, M42, M43).
        /// 4. Direct index mapping (matrix[r, c] -> M(r+1)(c+1)) maps row 3 (M30, M31, M32) directly into
        ///    M41, M42, M43, properly preserving the translation in System.Numerics row-vector representation.
        /// </summary>
        private static System.Numerics.Matrix4x4 UnityBindPoseToNumerics(Matrix4x4 matrix)
        {
            return new System.Numerics.Matrix4x4(
                matrix[0, 0], matrix[0, 1], matrix[0, 2], matrix[0, 3],
                matrix[1, 0], matrix[1, 1], matrix[1, 2], matrix[1, 3],
                matrix[2, 0], matrix[2, 1], matrix[2, 2], matrix[2, 3],
                matrix[3, 0], matrix[3, 1], matrix[3, 2], matrix[3, 3]);
        }

        private double[] GetFrameMatrixArray(string path, System.Numerics.Matrix4x4 fallback)
        {
            return NumericsToFbxArray(GetFrameMatrix(path, fallback));
        }

        private System.Numerics.Matrix4x4 GetFrameMatrix(string? path, System.Numerics.Matrix4x4 fallback)
        {
            var normalizedPath = NormalizeFramePath(path);
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                if (_frameGlobalMatrixMap.TryGetValue(normalizedPath, out var matrix))
                    return matrix;

                if (_frameUniqueSuffixMap.TryGetValue(normalizedPath, out var canonicalPath)
                    && canonicalPath != null
                    && _frameGlobalMatrixMap.TryGetValue(canonicalPath, out var suffixMatrix))
                {
                    return suffixMatrix;
                }
            }

            return fallback;
        }

        private static System.Numerics.Matrix4x4 BuildLocalMatrix(ImportedFrame frame)
        {
            var scale = System.Numerics.Matrix4x4.CreateScale(
                (float)SanitizeDouble(frame.LocalScale.X),
                (float)SanitizeDouble(frame.LocalScale.Y),
                (float)SanitizeDouble(frame.LocalScale.Z));
            var rotation =
                System.Numerics.Matrix4x4.CreateRotationX(ToRadians(frame.LocalRotation.X)) *
                System.Numerics.Matrix4x4.CreateRotationY(ToRadians(frame.LocalRotation.Y)) *
                System.Numerics.Matrix4x4.CreateRotationZ(ToRadians(frame.LocalRotation.Z));
            var translation = System.Numerics.Matrix4x4.CreateTranslation(
                (float)SanitizeDouble(frame.LocalPosition.X),
                (float)SanitizeDouble(frame.LocalPosition.Y),
                (float)SanitizeDouble(frame.LocalPosition.Z));

            return scale * rotation * translation;
        }

        private static float ToRadians(float degrees)
        {
            return (float)(SanitizeDouble(degrees) * Math.PI / 180.0);
        }

        /// <summary>
        /// Serializes a System.Numerics.Matrix4x4 row-vector matrix into a 16-element double array
        /// in FBX row-major layout.
        /// </summary>
        private static double[] NumericsToFbxArray(System.Numerics.Matrix4x4 matrix)
        {
            return new[]
            {
                SanitizeDouble(matrix.M11), SanitizeDouble(matrix.M12), SanitizeDouble(matrix.M13), SanitizeDouble(matrix.M14),
                SanitizeDouble(matrix.M21), SanitizeDouble(matrix.M22), SanitizeDouble(matrix.M23), SanitizeDouble(matrix.M24),
                SanitizeDouble(matrix.M31), SanitizeDouble(matrix.M32), SanitizeDouble(matrix.M33), SanitizeDouble(matrix.M34),
                SanitizeDouble(matrix.M41), SanitizeDouble(matrix.M42), SanitizeDouble(matrix.M43), SanitizeDouble(matrix.M44)
            };
        }

        private void BuildBonePathSet(IImported convert)
        {
            _bonePathSet.Clear();
            _skinBonePathSet.Clear();
            _meshPathSet.Clear();

            if (convert.MeshList == null)
                return;

            for (int m = 0; m < convert.MeshList.Count; m++)
            {
                var mesh = convert.MeshList[m];
                if (mesh.BoneList == null)
                    continue;

                for (int b = 0; b < mesh.BoneList.Count; b++)
                {
                    var bone = mesh.BoneList[b];
                    var normalizedPath = NormalizeFramePath(bone.Path);
                    if (string.IsNullOrEmpty(normalizedPath))
                        continue;

                    var frame = FindFrameFast(normalizedPath);
                    var resolvedPath = NormalizeFramePath(frame?.Path ?? normalizedPath);
                    if (!string.IsNullOrEmpty(resolvedPath))
                        _skinBonePathSet.Add(resolvedPath);
                    AddBonePathWithParent(frame, normalizedPath);
                }
            }

            for (int m = 0; m < convert.MeshList.Count; m++)
            {
                var mesh = convert.MeshList[m];
                var meshPath = NormalizeFramePath(mesh.Path);
                if (string.IsNullOrEmpty(meshPath))
                    continue;

                bool isSkinned = _exportSkins && mesh.BoneList != null && mesh.BoneList.Count > 0;
                if (isSkinned && IsAncestorOfAnyBone(meshPath))
                {
                    // Do not mark a bone ancestor (e.g. RootFrame) as a "Mesh" Model node; ExportMesh creates a dedicated child Mesh node.
                    continue;
                }

                _meshPathSet.Add(meshPath);
            }
        }

        private bool IsAncestorOfAnyBone(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath) || _bonePathSet.Count == 0)
                return false;

            if (normalizedPath == NormalizeFramePath(_rootFrame?.Path))
                return true;

            string prefix = normalizedPath + "/";
            foreach (var bonePath in _bonePathSet)
            {
                if (bonePath.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void AddBonePathWithParent(ImportedFrame? frame, string fallbackPath)
        {
            var normalizedPath = NormalizeFramePath(frame?.Path ?? fallbackPath);
            if (string.IsNullOrEmpty(normalizedPath))
                return;

            _bonePathSet.Add(normalizedPath);

            var parentPath = NormalizeFramePath(frame?.Parent?.Path);
            if (!string.IsNullOrEmpty(parentPath))
                _bonePathSet.Add(parentPath);
        }

        private bool IsBonePath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
                return false;

            if (normalizedPath == NormalizeFramePath(_rootFrame?.Path))
                return false;

            if (_castToBone)
                return true;

            return _bonePathSet.Contains(normalizedPath);
        }

        private bool ShouldZeroBoneTransform(string normalizedPath)
        {
            // Bone zeroing is permanently disabled. Preserving original rest poses ensures consistency
            // between the FBX skeletal hierarchy rest pose and skin cluster bind poses.
            return false;
        }

        private static string NormalizeFramePath(string? path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').Trim('/');
        }

        private void ExportCurveNode1D(long layerId, long targetId, string channelName, string propName, List<ImportedKeyframe<float>> keyframes)
        {
            var curveNodeId = GenId();
            var curveNode = N("AnimationCurveNode");
            curveNode.AddProperty(new LongToken(curveNodeId));
            curveNode.AddProperty(new StringToken(FormatObjectName(channelName, "AnimCurveNode")));
            curveNode.AddProperty(new StringToken(""));
            string channelProp = $"d|{propName}";
            var props = N("Properties70");
            props.AddNode(MakeNumberP(channelProp, keyframes.Count > 0 ? keyframes[0].value : 0));
            curveNode.AddNode(props);
            _objects.AddNode(curveNode);

            Connect(curveNodeId, layerId);
            ConnectProperty(curveNodeId, targetId, propName);

            int keyCount = keyframes.Count;
            var timesArr = new long[keyCount];
            var valuesArr = new float[keyCount];
            int writePos = 0;
            for (int i = 0; i < keyCount; i++)
            {
                var k = keyframes[i];
                long kTime = (long)Math.Round(SanitizeFinite(k.time) * 46186158000.0);
                float val = (float)SanitizeDouble(k.value);
                if (writePos > 0 && kTime == timesArr[writePos - 1])
                {
                    valuesArr[writePos - 1] = val;
                }
                else
                {
                    timesArr[writePos] = kTime;
                    valuesArr[writePos] = val;
                    writePos++;
                }
            }
            if (writePos != keyCount)
            {
                Array.Resize(ref timesArr, writePos);
                Array.Resize(ref valuesArr, writePos);
            }

            ExportCurve(curveNodeId, channelProp, timesArr, valuesArr);
        }

        private void ExportCurveNode(long layerId, long modelId, string channel, string propName, List<ImportedKeyframe<Vector3>> keyframes)
        {
            var curveNodeId = GenId();
            var curveNode = N("AnimationCurveNode");
            curveNode.AddProperty(new LongToken(curveNodeId));
            curveNode.AddProperty(new StringToken(FormatObjectName(channel, "AnimCurveNode")));
            curveNode.AddProperty(new StringToken(""));

            var props = N("Properties70");
            var defaultValue = keyframes.Count > 0 ? keyframes[0].value : Vector3.Zero;
            props.AddNode(MakeCompoundP("d"));
            props.AddNode(MakeNumberP("d|X", defaultValue.X));
            props.AddNode(MakeNumberP("d|Y", defaultValue.Y));
            props.AddNode(MakeNumberP("d|Z", defaultValue.Z));
            curveNode.AddNode(props);
            _objects.AddNode(curveNode);

            Connect(curveNodeId, layerId);
            ConnectProperty(curveNodeId, modelId, propName);

            int keyCount = keyframes.Count;
            var timesArr = new long[keyCount];
            var xArr = new float[keyCount];
            var yArr = new float[keyCount];
            var zArr = new float[keyCount];
            int writePos = 0;
            for (int i = 0; i < keyCount; i++)
            {
                var k = keyframes[i];
                long kTime = (long)Math.Round(SanitizeFinite(k.time) * 46186158000.0);
                float vx = (float)SanitizeDouble(k.value.X);
                float vy = (float)SanitizeDouble(k.value.Y);
                float vz = (float)SanitizeDouble(k.value.Z);
                if (writePos > 0 && kTime == timesArr[writePos - 1])
                {
                    xArr[writePos - 1] = vx;
                    yArr[writePos - 1] = vy;
                    zArr[writePos - 1] = vz;
                }
                else
                {
                    timesArr[writePos] = kTime;
                    xArr[writePos] = vx;
                    yArr[writePos] = vy;
                    zArr[writePos] = vz;
                    writePos++;
                }
            }
            if (writePos != keyCount)
            {
                Array.Resize(ref timesArr, writePos);
                Array.Resize(ref xArr, writePos);
                Array.Resize(ref yArr, writePos);
                Array.Resize(ref zArr, writePos);
            }

            ExportCurve(curveNodeId, "d|X", timesArr, xArr);
            ExportCurve(curveNodeId, "d|Y", timesArr, yArr);
            ExportCurve(curveNodeId, "d|Z", timesArr, zArr);
        }

        private void ExportCurve(long curveNodeId, string channel, long[] times, float[] floatValues)
        {
            var curveId = GenId();
            _exportedAnimationCurveCount++;
            var curve = N("AnimationCurve");
            curve.AddProperty(new LongToken(curveId));
            curve.AddProperty(new StringToken(FormatObjectName("", "AnimCurve")));
            curve.AddProperty(new StringToken(""));

            AddSimpleNode(curve, "Default", 0.0);
            AddSimpleNode(curve, "KeyVer", 4009);

            var keyTime = N("KeyTime");
            keyTime.AddProperty(new LongArrayToken(times));
            curve.AddNode(keyTime);

            var keyValueFloat = N("KeyValueFloat");
            keyValueFloat.AddProperty(new FloatArrayToken(floatValues));
            curve.AddNode(keyValueFloat);

            var keyAttrFlags = N("KeyAttrFlags");
            keyAttrFlags.AddProperty(new IntegerArrayToken(DefaultKeyAttrFlags));
            curve.AddNode(keyAttrFlags);

            var keyAttrDataFloat = N("KeyAttrDataFloat");
            keyAttrDataFloat.AddProperty(new FloatArrayToken(DefaultKeyAttrDataFloat));
            curve.AddNode(keyAttrDataFloat);

            var keyAttrRefCount = N("KeyAttrRefCount");
            keyAttrRefCount.AddProperty(new IntegerArrayToken(new[] { floatValues.Length }));
            curve.AddNode(keyAttrRefCount);

            _objects.AddNode(curve);
            ConnectProperty(curveId, curveNodeId, channel);
        }

        private void ConnectProperty(long childId, long parentId, string propertyName)
        {
            var c = N("C");
            c.AddProperty(new StringToken("OP"));
            c.AddProperty(new LongToken(childId));
            c.AddProperty(new LongToken(parentId));
            c.AddProperty(new StringToken(propertyName));
            _connections.AddNode(c);
        }

        private void ExportMorph(ImportedMorph morph, IImported convert)
        {
            if (morph.Channels == null || morph.Channels.Count == 0) return;
            if (morph.Path == null) return;
            if (!_meshIdMap.TryGetValue(morph.Path, out var geoId)) return;

            ImportedMesh? baseMesh = null;
            if (convert.MeshList != null)
            {
                baseMesh = ImportedHelpers.FindMesh(morph.Path, convert.MeshList);
                if (baseMesh == null)
                {
                    var normMorphPath = NormalizeFramePath(morph.Path);
                    for (int m = 0; m < convert.MeshList.Count; m++)
                    {
                        if (string.Equals(NormalizeFramePath(convert.MeshList[m].Path), normMorphPath, StringComparison.Ordinal))
                        {
                            baseMesh = convert.MeshList[m];
                            break;
                        }
                    }
                }
            }

            var blendShapeId = GenId();
            var bs = N("Deformer");
            bs.AddProperty(new LongToken(blendShapeId));
            bs.AddProperty(new StringToken(FormatObjectName("BlendShape", "Deformer")));
            bs.AddProperty(new StringToken("BlendShape"));
            AddSimpleNode(bs, "Version", 100);
            _objects.AddNode(bs);
            Connect(blendShapeId, geoId);

            for (int cIdx = 0; cIdx < morph.Channels.Count; cIdx++)
            {
                var channel = morph.Channels[cIdx];
                var channelId = GenId();
                var bc = N("Deformer");
                bc.AddProperty(new LongToken(channelId));
                bc.AddProperty(new StringToken(FormatObjectName(channel.Name, "SubDeformer")));
                bc.AddProperty(new StringToken("BlendShapeChannel"));
                AddSimpleNode(bc, "Version", 100);
                AddSimpleNode(bc, "DeformPercent", 0.0);

                var bcProps = N("Properties70");
                bcProps.AddNode(MakeNumberP("DeformPercent", 0.0));
                bc.AddNode(bcProps);

                var fullWeights = N("FullWeights");
                int kfCount = channel.KeyframeList.Count;
                var weights = new double[kfCount];
                for (int i = 0; i < kfCount; i++)
                    weights[i] = SanitizeFinite(channel.KeyframeList[i].Weight);
                fullWeights.AddProperty(new DoubleArrayToken(weights));
                bc.AddNode(fullWeights);

                _objects.AddNode(bc);
                Connect(channelId, blendShapeId);

                _blendShapeChannelMap[morph.Path + "::" + channel.Name] = channelId;
                var normMorphPath = NormalizeFramePath(morph.Path);
                if (!string.IsNullOrEmpty(normMorphPath))
                {
                    _blendShapeChannelMap[normMorphPath + "::" + channel.Name] = channelId;
                }

                for (int kIdx = 0; kIdx < kfCount; kIdx++)
                {
                    var keyframe = channel.KeyframeList[kIdx];
                    var shapeId = GenId();
                    var shapeName = kfCount > 1 ? $"{channel.Name}_Frame{kIdx}" : channel.Name;
                    var shape = N("Geometry");
                    shape.AddProperty(new LongToken(shapeId));
                    shape.AddProperty(new StringToken(FormatObjectName(shapeName, "Geometry")));
                    shape.AddProperty(new StringToken("Shape"));

                    AddSimpleNode(shape, "Version", 100);

                    var morphVerts = keyframe.VertexList;
                    int vertCount = morphVerts.Count;
                    var indices = new int[vertCount];
                    var verts = new double[vertCount * 3];
                    var normals = keyframe.hasNormals ? new double[vertCount * 3] : null;
                    var baseVertexList = baseMesh?.VertexList;
                    for (int i = 0; i < vertCount; i++)
                    {
                        var v = morphVerts[i];
                        int offset = i * 3;
                        indices[i] = (int)v.Index;
                        Vector3 basePos = default;
                        if (baseVertexList != null && v.Index < (uint)baseVertexList.Count)
                        {
                            basePos = baseVertexList[(int)v.Index].Vertex;
                        }
                        verts[offset] = SanitizeFinite((double)v.Vertex.Vertex.X - basePos.X);
                        verts[offset + 1] = SanitizeFinite((double)v.Vertex.Vertex.Y - basePos.Y);
                        verts[offset + 2] = SanitizeFinite((double)v.Vertex.Vertex.Z - basePos.Z);
                        if (normals != null)
                        {
                            normals[offset] = SanitizeFinite(v.Vertex.Normal.X);
                            normals[offset + 1] = SanitizeFinite(v.Vertex.Normal.Y);
                            normals[offset + 2] = SanitizeFinite(v.Vertex.Normal.Z);
                        }
                    }

                    var idxNode = N("Indexes");
                    idxNode.AddProperty(new IntegerArrayToken(indices));
                    shape.AddNode(idxNode);

                    var vertNode = N("Vertices");
                    vertNode.AddProperty(new DoubleArrayToken(verts));
                    shape.AddNode(vertNode);

                    if (normals != null)
                    {
                        var normNode = N("Normals");
                        normNode.AddProperty(new DoubleArrayToken(normals));
                        shape.AddNode(normNode);
                    }

                    _objects.AddNode(shape);
                    Connect(shapeId, channelId);
                }
            }
        }

        private void BuildHeader()
        {
            var now = DateTime.UtcNow;
            var header = N("FBXHeaderExtension");

            var ver = N("FBXHeaderVersion"); ver.AddProperty(new IntegerToken(1003));
            header.AddNode(ver);
            var fbxVer = N("FBXVersion"); fbxVer.AddProperty(new IntegerToken(_versionIndex >= 5 ? 7500 : 7400));
            header.AddNode(fbxVer);
            var encType = N("EncryptionType"); encType.AddProperty(new IntegerToken(0));
            header.AddNode(encType);

            var ts = N("CreationTimeStamp");
            AddSimpleNode(ts, "Version", 1000);
            AddSimpleNode(ts, "Year", now.Year);
            AddSimpleNode(ts, "Month", now.Month);
            AddSimpleNode(ts, "Day", now.Day);
            AddSimpleNode(ts, "Hour", now.Hour);
            AddSimpleNode(ts, "Minute", now.Minute);
            AddSimpleNode(ts, "Second", now.Second);
            AddSimpleNode(ts, "Millisecond", now.Millisecond);
            header.AddNode(ts);

            var creator = N("Creator"); creator.AddProperty(new StringToken("AssetStudio"));
            header.AddNode(creator);

            var sceneInfo = N("SceneInfo");
            sceneInfo.AddProperty(new StringToken(FormatObjectName("GlobalInfo", "SceneInfo")));
            sceneInfo.AddProperty(new StringToken("UserData"));
            AddSimpleNode(sceneInfo, "Type", "UserData");
            AddSimpleNode(sceneInfo, "Version", 100);
            var metaData = N("MetaData");
            AddSimpleNode(metaData, "Version", 100);
            AddSimpleNode(metaData, "Title", "");
            AddSimpleNode(metaData, "Subject", "");
            AddSimpleNode(metaData, "Author", "");
            AddSimpleNode(metaData, "Keywords", "");
            AddSimpleNode(metaData, "Revision", "");
            AddSimpleNode(metaData, "Comment", "");
            sceneInfo.AddNode(metaData);

            var props = N("Properties70");
            props.AddNode(MakeStringP("DocumentUrl", "/model.fbx"));
            props.AddNode(MakeStringP("SrcDocumentUrl", "/model.fbx"));
            sceneInfo.AddNode(props);
            header.AddNode(sceneInfo);

            _document.AddNode(header);

            // Top-level metadata required by Autodesk FBX SDK
            var fileId = N("FileId");
            fileId.AddProperty(new ByteArrayToken(new byte[] { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 }));
            _document.AddNode(fileId);

            var cTime = N("CreationTime");
            cTime.AddProperty(new StringToken(now.ToString("yyyy-MM-dd HH:mm:ss:fff", System.Globalization.CultureInfo.InvariantCulture)));
            _document.AddNode(cTime);

            var topCreator = N("Creator");
            topCreator.AddProperty(new StringToken("AssetStudio"));
            _document.AddNode(topCreator);
        }

        private void BuildDocuments()
        {
            var docs = N("Documents");
            AddSimpleNode(docs, "Count", 1);
            var doc = N("Document");
            doc.AddProperty(new LongToken(GenId()));
            doc.AddProperty(new StringToken(FormatObjectName("Scene", "Document")));
            doc.AddProperty(new StringToken("Scene"));
            doc.AddNode(N("Properties70"));
            AddSimpleNode(doc, "RootNode", 0L);
            docs.AddNode(doc);
            _document.AddNode(docs);

            var refs = N("References");
            _document.AddNode(refs);
        }

        private void BuildDefinitions()
        {
            var definitions = N("Definitions");
            AddSimpleNode(definitions, "Version", 100);
            _definitionsCountNode = N("Count");
            definitions.AddNode(_definitionsCountNode);

            AddObjectType(definitions, "GlobalSettings");
            AddObjectType(definitions, "Model");
            AddObjectType(definitions, "Geometry");
            AddObjectType(definitions, "Material");
            AddObjectType(definitions, "Texture");
            AddObjectType(definitions, "Video");
            AddObjectType(definitions, "NodeAttribute");
            AddObjectType(definitions, "Deformer");
            AddObjectType(definitions, "Pose");
            AddObjectType(definitions, "AnimationStack");
            AddObjectType(definitions, "AnimationLayer");
            AddObjectType(definitions, "AnimationCurveNode");
            AddObjectType(definitions, "AnimationCurve");

            _document.AddNode(definitions);
        }

        private void AddObjectType(FbxNode definitions, string typeName)
        {
            var objectType = N("ObjectType");
            objectType.AddProperty(new StringToken(typeName));
            var countNode = N("Count");
            objectType.AddNode(countNode);
            _definitionTypeCountNodes[typeName] = countNode;
            definitions.AddNode(objectType);
        }

        private void UpdateDefinitionsCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["GlobalSettings"] = 1
            };

            int totalCount = 1;
            if (_objects?.Nodes != null)
            {
                for (int i = 0; i < _objects.Nodes.Length; i++)
                {
                    var node = _objects.Nodes[i];
                    var typeName = node?.Identifier?.Value;
                    if (string.IsNullOrEmpty(typeName))
                        continue;

                    counts.TryGetValue(typeName, out var existing);
                    counts[typeName] = existing + 1;
                    totalCount++;
                }
            }

            _definitionsCountNode?.AddProperty(new IntegerToken(totalCount));

            foreach (var kv in _definitionTypeCountNodes)
            {
                counts.TryGetValue(kv.Key, out var typeCount);
                kv.Value.AddProperty(new IntegerToken(typeCount));
            }
        }

        private void BuildTakes()
        {
            var takes = N("Takes");
            if (_takes.Count == 0)
            {
                AddSimpleNode(takes, "Current", "");
            }
            else
            {
                AddSimpleNode(takes, "Current", _takes[0].Name);
                foreach (var takeInfo in _takes)
                {
                    var take = N("Take");
                    take.AddProperty(new StringToken(takeInfo.Name));

                    var fileName = N("FileName");
                    fileName.AddProperty(new StringToken($"{takeInfo.Name}.tak"));
                    take.AddNode(fileName);

                    var localTime = N("LocalTime");
                    localTime.AddProperty(new LongToken(takeInfo.Start));
                    localTime.AddProperty(new LongToken(takeInfo.Stop));
                    take.AddNode(localTime);

                    var referenceTime = N("ReferenceTime");
                    referenceTime.AddProperty(new LongToken(takeInfo.Start));
                    referenceTime.AddProperty(new LongToken(takeInfo.Stop));
                    take.AddNode(referenceTime);

                    takes.AddNode(take);
                }
            }
            _document.AddNode(takes);
        }

        private void WriteExportReport(string exportPath, IImported convert)
        {
            if (string.IsNullOrEmpty(_exportDirectory))
                return;

            var reportPath = Path.Combine(_exportDirectory, Path.GetFileNameWithoutExtension(exportPath) + ".fbx-export-report.txt");
            using (var writer = new StreamWriter(reportPath, false, Encoding.UTF8))
            {
                writer.WriteLine("AssetStudio FBX export report");
                writer.WriteLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine();
                writer.WriteLine($"Source frames: {CountFrames(convert.RootFrame)}");
                writer.WriteLine($"Source meshes: {convert.MeshList?.Count ?? 0}");
                writer.WriteLine($"Source materials: {convert.MaterialList?.Count ?? 0}");
                writer.WriteLine($"Source textures: {convert.TextureList?.Count ?? 0}");
                writer.WriteLine($"Source animations: {convert.AnimationList?.Count ?? 0}");
                writer.WriteLine();
                writer.WriteLine($"Exported frames: {_exportedFrameCount}");
                writer.WriteLine($"Exported bone models: {_exportedBoneCount}");
                writer.WriteLine($"Exported meshes: {_exportedMeshCount}");
                writer.WriteLine($"Exported skins: {_exportedSkinCount}");
                writer.WriteLine($"Candidate bone bindings: {_candidateClusterCount}");
                writer.WriteLine($"Active FBX skin clusters: {_exportedClusterCount}");
                writer.WriteLine($"Zero-weight bindings omitted: {_omittedClusterCount}");
                writer.WriteLine($"Exported materials: {_exportedMaterialCount}");
                writer.WriteLine($"Exported textures: {_exportedTextureCount}");
                writer.WriteLine($"Exported animation stacks: {_exportedAnimationStackCount}");
                writer.WriteLine($"Exported animation tracks: {_exportedAnimationTrackCount}");
                writer.WriteLine($"Exported animation curves: {_exportedAnimationCurveCount}");
                writer.WriteLine($"Missing animation track targets: {_missingAnimationTrackCount}");

                writer.WriteLine();
                writer.WriteLine("Materials:");
                if (convert.MaterialList?.Count > 0)
                {
                    foreach (var material in convert.MaterialList)
                    {
                        writer.WriteLine($"  {material.Name} ({material.Textures?.Count ?? 0} textures)");
                    }
                }
                else
                {
                    writer.WriteLine("  <none>");
                }

                writer.WriteLine();
                writer.WriteLine("Mesh material assignments:");
                if (convert.MeshList?.Count > 0)
                {
                    foreach (var mesh in convert.MeshList)
                    {
                        writer.WriteLine($"  Mesh: {mesh.Path ?? "<no path>"}");
                        for (var i = 0; i < mesh.SubmeshList.Count; i++)
                        {
                            var submesh = mesh.SubmeshList[i];
                            writer.WriteLine($"    Submesh {i}: {submesh.Material ?? "<none>"} ({submesh.FaceList?.Count ?? 0} faces)");
                        }
                    }
                }
                else
                {
                    writer.WriteLine("  <none>");
                }

                writer.WriteLine();
                writer.WriteLine("Mesh Diagnostics & Statistics:");
                if (_meshDiagnostics.Count > 0)
                {
                    foreach (var mDiag in _meshDiagnostics)
                    {
                        writer.WriteLine($"  Mesh: {mDiag.Path}");
                        writer.WriteLine($"    Vertex Count: {mDiag.VertexCount}");
                        writer.WriteLine($"    Polygon Count (written): {mDiag.PolygonCount}");
                        writer.WriteLine($"    Min Index Written: {(mDiag.MinIndex == int.MaxValue ? -1 : mDiag.MinIndex)}");
                        writer.WriteLine($"    Max Index Written: {mDiag.MaxIndex}");
                        writer.WriteLine($"    Skipped Faces Count (out of range): {mDiag.SkippedFaces}");
                        writer.WriteLine("    Submeshes:");
                        foreach (var sDiag in mDiag.Submeshes)
                        {
                            writer.WriteLine($"      Submesh {sDiag.Index}:");
                            writer.WriteLine($"        Material: {sDiag.Material}");
                            writer.WriteLine($"        Base Vertex: {sDiag.BaseVertex}");
                            writer.WriteLine($"        First Vertex (Min Index): {(sDiag.FirstVertex == int.MaxValue ? -1 : sDiag.FirstVertex)}");
                            writer.WriteLine($"        Vertex Count (Index range length): {sDiag.VertexCount}");
                            writer.WriteLine($"        Index Count: {sDiag.IndexCount}");
                            writer.WriteLine($"        Index Range: {(sDiag.MinIndex == int.MaxValue ? -1 : sDiag.MinIndex)} to {sDiag.MaxIndex}");
                        }
                    }
                }
                else
                {
                    writer.WriteLine("  <none>");
                }
            }
        }

        private int CountFrames(ImportedFrame frame)
        {
            if (frame == null)
                return 0;

            var count = 1;
            for (int i = 0; i < frame.Count; i++)
                count += CountFrames(frame[i]);
            return count;
        }

        private void BuildGlobalSettings(float scaleFactor)
        {
            var gs = N("GlobalSettings");
            AddSimpleNode(gs, "Version", 1000);

            var props = N("Properties70");
            _globalSettingsProps = props;
            props.AddNode(MakeIntP("UpAxis", 1));
            props.AddNode(MakeIntP("UpAxisSign", 1));
            props.AddNode(MakeIntP("FrontAxis", 2));
            props.AddNode(MakeIntP("FrontAxisSign", 1));
            props.AddNode(MakeIntP("CoordAxis", 0));
            props.AddNode(MakeIntP("CoordAxisSign", 1));
            props.AddNode(MakeIntP("OriginalUpAxis", -1));
            props.AddNode(MakeIntP("OriginalUpAxisSign", 1));

            props.AddNode(MakeStringP("source", "Unity3D"));
            props.AddNode(MakeStringP("platform", "Windows"));

            double effectiveUnitScale = 100.0 * SanitizeDouble(scaleFactor);
            if (effectiveUnitScale <= 0)
                effectiveUnitScale = 100.0;

            var unitScale = N("P");
            unitScale.AddProperty(new StringToken("UnitScaleFactor"));
            unitScale.AddProperty(new StringToken("double"));
            unitScale.AddProperty(new StringToken("Number"));
            unitScale.AddProperty(new StringToken(""));
            unitScale.AddProperty(new DoubleToken(effectiveUnitScale));
            props.AddNode(unitScale);

            var origUnitScale = N("P");
            origUnitScale.AddProperty(new StringToken("OriginalUnitScaleFactor"));
            origUnitScale.AddProperty(new StringToken("double"));
            origUnitScale.AddProperty(new StringToken("Number"));
            origUnitScale.AddProperty(new StringToken(""));
            origUnitScale.AddProperty(new DoubleToken(effectiveUnitScale));
            props.AddNode(origUnitScale);

            gs.AddNode(props);
            _document.AddNode(gs);
        }

        private void UpdateGlobalSettingsTiming(IImported convert)
        {
            if (_globalSettingsProps == null)
                return;

            double fps = _is60Fps ? 60.0 : 30.0;
            if (convert.AnimationList != null)
            {
                for (int i = 0; i < convert.AnimationList.Count; i++)
                {
                    var sr = convert.AnimationList[i].SampleRate;
                    if (sr > 0f && !float.IsNaN(sr) && !float.IsInfinity(sr))
                    {
                        fps = sr;
                        break;
                    }
                }
            }

            int timeMode;
            if (Math.Abs(fps - 30.0) < 1e-3)
            {
                timeMode = 6; // eFrames30
                fps = 30.0;
            }
            else if (Math.Abs(fps - 60.0) < 1e-3)
            {
                timeMode = 14; // eFrames60
                fps = 60.0;
            }
            else
            {
                timeMode = 11; // eCustom
            }

            long maxStop = 0;
            for (int i = 0; i < _takes.Count; i++)
            {
                if (_takes[i].Stop > maxStop)
                    maxStop = _takes[i].Stop;
            }
            if (maxStop <= 0)
            {
                maxStop = 46186158000L;
            }

            _globalSettingsProps.AddNode(MakeEnumP("TimeMode", timeMode));
            var customFpsNode = N("P");
            customFpsNode.AddProperty(new StringToken("CustomFrameRate"));
            customFpsNode.AddProperty(new StringToken("double"));
            customFpsNode.AddProperty(new StringToken("Number"));
            customFpsNode.AddProperty(new StringToken(""));
            customFpsNode.AddProperty(new DoubleToken(SanitizeDouble(fps)));
            _globalSettingsProps.AddNode(customFpsNode);
            _globalSettingsProps.AddNode(MakeTimeP("TimeSpanStart", 0));
            _globalSettingsProps.AddNode(MakeTimeP("TimeSpanStop", maxStop));
        }

        private FbxNode MakeEnumP(string name, int value)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("enum"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new IntegerToken(value));
            return p;
        }

        private void Connect(long childId, long parentId)
        {
            var c = N("C");
            c.AddProperty(new StringToken("OO"));
            c.AddProperty(new LongToken(childId));
            c.AddProperty(new LongToken(parentId));
            _connections.AddNode(c);
        }

        private FbxNode N(string name)
        {
            return new FbxNode(new IdentifierToken(name));
        }

        private long GenId()
        {
            return _nextId++;
        }

        private void AddSimpleNode(FbxNode parent, string name, int value)
        {
            var n = N(name); n.AddProperty(new IntegerToken(value)); parent.AddNode(n);
        }

        private void AddSimpleNode(FbxNode parent, string name, double value)
        {
            var n = N(name); n.AddProperty(new DoubleToken(value)); parent.AddNode(n);
        }

        private void AddSimpleNode(FbxNode parent, string name, string value)
        {
            var n = N(name); n.AddProperty(new StringToken(value)); parent.AddNode(n);
        }

        private FbxNode MakeP(string name, string type1, string type2, string flags, double x, double y, double z)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken(type1));
            p.AddProperty(new StringToken(type2));
            p.AddProperty(new StringToken(flags));
            p.AddProperty(new DoubleToken(SanitizeDouble(x)));
            p.AddProperty(new DoubleToken(SanitizeDouble(y)));
            p.AddProperty(new DoubleToken(SanitizeDouble(z)));
            return p;
        }

        private FbxNode MakeCompoundP(string name)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("Compound"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new StringToken(""));
            return p;
        }

        private FbxNode MakeNumberP(string name, double value)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("Number"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new StringToken("A"));
            p.AddProperty(new DoubleToken(SanitizeDouble(value)));
            return p;
        }

        private FbxNode MakeStringP(string name, string value)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("KString"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new StringToken(value));
            return p;
        }

        private FbxNode MakeTimeP(string name, long value)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("KTime"));
            p.AddProperty(new StringToken("Time"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new LongToken(value));
            return p;
        }

        private FbxNode MakeColorP(string name, Color color)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("Color"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new StringToken("A"));
            p.AddProperty(new DoubleToken(SanitizeFinite(color.R)));
            p.AddProperty(new DoubleToken(SanitizeFinite(color.G)));
            p.AddProperty(new DoubleToken(SanitizeFinite(color.B)));
            return p;
        }

        private static double SanitizeFinite(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0;

            return value;
        }

        private static double SanitizeDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0;

            if (Math.Abs(value) < 1e-12)
                return 0;

            return value;
        }

        private FbxNode MakeIntP(string name, int value)
        {
            var p = N("P");
            p.AddProperty(new StringToken(name));
            p.AddProperty(new StringToken("int"));
            p.AddProperty(new StringToken("Integer"));
            p.AddProperty(new StringToken(""));
            p.AddProperty(new IntegerToken(value));
            return p;
        }
    }
}
