using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private bool _isAscii;
        private bool _exportSkins;
        private bool _exportAnimations;
        private bool _exportBlendShape;
        private bool _castToBone;
        private float _boneSize;
        private string _exportDirectory;
        private ImportedFrame _rootFrame;
        private Dictionary<string, long> _frameIdMap = new Dictionary<string, long>();
        private Dictionary<string, System.Numerics.Matrix4x4> _frameGlobalMatrixMap = new Dictionary<string, System.Numerics.Matrix4x4>();
        private Dictionary<string, long> _materialIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _meshIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _blendShapeChannelMap = new Dictionary<string, long>();
        private Dictionary<string, long> _textureIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _videoIdMap = new Dictionary<string, long>();
        private Dictionary<string, int> _fbxNameCounts = new Dictionary<string, int>();
        private HashSet<string> _bonePathSet = new HashSet<string>();
        private HashSet<string> _skinBonePathSet = new HashSet<string>();
        private HashSet<string> _meshPathSet = new HashSet<string>();
        private List<(string Name, long Start, long Stop)> _takes = new List<(string Name, long Start, long Stop)>();
        private int _exportedFrameCount;
        private int _exportedBoneCount;
        private int _exportedMeshCount;
        private int _exportedSkinCount;
        private int _exportedClusterCount;
        private int _exportedMaterialCount;
        private int _exportedTextureCount;
        private int _exportedAnimationStackCount;
        private int _exportedAnimationTrackCount;
        private int _exportedAnimationCurveCount;
        private int _missingAnimationTrackCount;

        public FbxSharpieExporter(string fileName, float scaleFactor, int versionIndex, bool isAscii, bool is60Fps,
            bool exportSkins = true, bool exportAnimations = true, bool exportBlendShape = true, bool castToBone = false, float boneSize = 10f)
        {
            _scaleFactor = scaleFactor;
            _isAscii = isAscii;
            _exportSkins = exportSkins;
            _exportAnimations = exportAnimations;
            _exportBlendShape = exportBlendShape;
            _castToBone = castToBone;
            _boneSize = boneSize;
            _document = new FbxDocument();
            _document.Version = FbxVersion.v7_4;

            BuildHeader();
            BuildGlobalSettings(scaleFactor);
            BuildDefinitions();

            _objects = N("Objects");
            _document.AddNode(_objects);

            _connections = N("Connections");
            _document.AddNode(_connections);
        }

        public void Export(IImported convert, string exportPath)
        {
            _rootFrame = convert.RootFrame;
            _exportDirectory = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(_exportDirectory))
                Directory.CreateDirectory(_exportDirectory);

            BuildBonePathSet(convert);

            if (convert.RootFrame != null)
            {
                ExportFrame(convert.RootFrame, 0, System.Numerics.Matrix4x4.Identity);
                ExportBindPose();
            }

            if (convert.MaterialList != null)
            {
                foreach (var mat in convert.MaterialList)
                    ExportMaterial(mat, convert);
            }

            if (convert.MeshList != null)
            {
                foreach (var mesh in convert.MeshList)
                    ExportMesh(mesh, convert);
            }

            if (_exportBlendShape && convert.MorphList != null)
            {
                foreach (var morph in convert.MorphList)
                    ExportMorph(morph);
            }

            if (_exportAnimations && convert.AnimationList != null)
            {
                foreach (var anim in convert.AnimationList)
                    ExportAnimation(anim);
            }

            BuildTakes();
            WriteExportReport(exportPath, convert);
            FbxIO.WriteAscii(_document, exportPath);
        }

        private void ExportFrame(ImportedFrame frame, long parentId, System.Numerics.Matrix4x4 parentGlobal)
        {
            var id = GenId();
            _exportedFrameCount++;
            var normalizedPath = NormalizeFramePath(frame.Path);
            _frameIdMap[normalizedPath] = id;
            var globalMatrix = BuildLocalMatrix(frame) * parentGlobal;
            _frameGlobalMatrixMap[normalizedPath] = globalMatrix;
            var isBone = IsBonePath(normalizedPath);
            var zeroTransform = ShouldZeroBoneTransform(normalizedPath);
            var modelType = isBone ? "LimbNode" : "Null";
            var fbxName = GetUniqueFbxName(frame.Name);

            var model = N("Model");
            model.AddProperty(new LongToken(id));
            model.AddProperty(new StringToken($"Model::{fbxName}"));
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
            attr.AddProperty(new StringToken($"NodeAttribute::{name}"));
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
            if (_frameIdMap.Count == 0)
                return;

            var pose = N("Pose");
            pose.AddProperty(new LongToken(GenId()));
            pose.AddProperty(new StringToken("Pose::BindPose"));
            pose.AddProperty(new StringToken("BindPose"));
            AddSimpleNode(pose, "Type", "BindPose");
            AddSimpleNode(pose, "Version", 100);
            AddSimpleNode(pose, "NbPoseNodes", _frameIdMap.Count);

            foreach (var item in _frameIdMap)
            {
                if (!_frameGlobalMatrixMap.TryGetValue(item.Key, out var matrix))
                    continue;

                var poseNode = N("PoseNode");

                var node = N("Node");
                node.AddProperty(new LongToken(item.Value));
                poseNode.AddNode(node);

                var matrixNode = N("Matrix");
                matrixNode.AddProperty(new DoubleArrayToken(ToFbxMatrixArray(matrix)));
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

            var geoId = GenId();
            _exportedMeshCount++;
            if (mesh.Path != null)
                _meshIdMap[mesh.Path] = geoId;

            var geo = N("Geometry");
            geo.AddProperty(new LongToken(geoId));
            geo.AddProperty(new StringToken($"Geometry::{mesh.Path}"));
            geo.AddProperty(new StringToken("Mesh"));

            BuildVertices(geo, mesh);
            BuildPolygonVertexIndex(geo, mesh);

            if (mesh.hasNormal)
                BuildLayerElementNormal(geo, mesh);
            if (mesh.hasUV != null)
            {
                for (int uvIndex = 0; uvIndex < mesh.hasUV.Length; uvIndex++)
                {
                    if (mesh.hasUV[uvIndex])
                        BuildLayerElementUV(geo, mesh, uvIndex);
                }
            }
            if (mesh.hasTangent)
                BuildLayerElementTangent(geo, mesh);
            if (mesh.hasColor)
                BuildLayerElementColor(geo, mesh);

            if (mesh.SubmeshList.Count > 0)
                BuildLayerElementMaterial(geo, mesh);

            BuildLayer(geo, mesh);
            _objects.AddNode(geo);

            if (modelId != 0)
                Connect(geoId, modelId);

            ConnectMaterialsToModel(mesh, modelId, convert);

            if (_exportSkins && mesh.BoneList != null && mesh.BoneList.Count > 0)
                ExportSkin(mesh, geoId);
        }

        private void BuildVertices(FbxNode geo, ImportedMesh mesh)
        {
            var verts = new double[mesh.VertexList.Count * 3];
            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                var v = mesh.VertexList[i].Vertex;
                verts[i * 3] = v.X;
                verts[i * 3 + 1] = v.Y;
                verts[i * 3 + 2] = v.Z;
            }
            var n = N("Vertices");
            n.AddProperty(new DoubleArrayToken(verts));
            geo.AddNode(n);
        }

        private void BuildPolygonVertexIndex(FbxNode geo, ImportedMesh mesh)
        {
            var indices = new List<int>();
            foreach (var sub in mesh.SubmeshList)
            {
                foreach (var face in sub.FaceList)
                {
                    indices.Add(face.VertexIndices[0]);
                    indices.Add(face.VertexIndices[1]);
                    indices.Add(~face.VertexIndices[2]);
                }
            }
            var n = N("PolygonVertexIndex");
            n.AddProperty(new IntegerArrayToken(indices.ToArray()));
            geo.AddNode(n);
        }

        private void BuildLayerElementNormal(FbxNode geo, ImportedMesh mesh)
        {
            var layer = N("LayerElementNormal");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByControlPoint");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var data = new double[mesh.VertexList.Count * 3];
            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                var n = mesh.VertexList[i].Normal;
                data[i * 3] = n.X;
                data[i * 3 + 1] = n.Y;
                data[i * 3 + 2] = n.Z;
            }
            var normNode = N("Normals");
            normNode.AddProperty(new DoubleArrayToken(data));
            layer.AddNode(normNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementUV(FbxNode geo, ImportedMesh mesh, int uvIndex)
        {
            var layer = N("LayerElementUV");
            layer.AddProperty(new IntegerToken(uvIndex));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", uvIndex == 0 ? "UVMap" : $"UVMap_{uvIndex}");
            AddSimpleNode(layer, "MappingInformationType", "ByControlPoint");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var data = new double[mesh.VertexList.Count * 2];
            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                if (mesh.VertexList[i].UV?[uvIndex] != null)
                {
                    data[i * 2] = mesh.VertexList[i].UV[uvIndex][0];
                    data[i * 2 + 1] = mesh.VertexList[i].UV[uvIndex][1];
                }
            }
            var uvNode = N("UV");
            uvNode.AddProperty(new DoubleArrayToken(data));
            layer.AddNode(uvNode);
            var uvIdxNode = N("UVIndex");
            var indices = new int[mesh.VertexList.Count];
            for(int i = 0; i < indices.Length; i++) indices[i] = i;
            uvIdxNode.AddProperty(new IntegerArrayToken(indices));
            layer.AddNode(uvIdxNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementTangent(FbxNode geo, ImportedMesh mesh)
        {
            var layer = N("LayerElementTangent");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByControlPoint");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var data = new double[mesh.VertexList.Count * 3];
            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                var t = mesh.VertexList[i].Tangent;
                data[i * 3] = t.X;
                data[i * 3 + 1] = t.Y;
                data[i * 3 + 2] = t.Z;
            }
            var tangNode = N("Tangents");
            tangNode.AddProperty(new DoubleArrayToken(data));
            layer.AddNode(tangNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementColor(FbxNode geo, ImportedMesh mesh)
        {
            var layer = N("LayerElementColor");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "ByControlPoint");
            AddSimpleNode(layer, "ReferenceInformationType", "Direct");

            var data = new double[mesh.VertexList.Count * 4];
            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                var c = mesh.VertexList[i].Color;
                data[i * 4] = c.R;
                data[i * 4 + 1] = c.G;
                data[i * 4 + 2] = c.B;
                data[i * 4 + 3] = c.A;
            }
            var colorNode = N("Colors");
            colorNode.AddProperty(new DoubleArrayToken(data));
            layer.AddNode(colorNode);
            geo.AddNode(layer);
        }

        private void BuildLayerElementMaterial(FbxNode geo, ImportedMesh mesh)
        {
            var layer = N("LayerElementMaterial");
            layer.AddProperty(new IntegerToken(0));
            AddSimpleNode(layer, "Version", 101);
            AddSimpleNode(layer, "Name", "");
            AddSimpleNode(layer, "MappingInformationType", "AllSame");
            AddSimpleNode(layer, "ReferenceInformationType", "IndexToDirect");

            var matIdx = N("Materials");
            matIdx.AddProperty(new IntegerArrayToken(new[] { 0 }));
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
            if (mesh.hasUV != null)
            {
                for (int uvIndex = 0; uvIndex < mesh.hasUV.Length; uvIndex++)
                {
                    if (mesh.hasUV[uvIndex])
                        AddLayerRef(layer, "LayerElementUV", uvIndex);
                }
            }
            if (mesh.hasTangent)
                AddLayerRef(layer, "LayerElementTangent");
            if (mesh.hasColor)
                AddLayerRef(layer, "LayerElementColor");
            if (mesh.SubmeshList.Count > 0)
                AddLayerRef(layer, "LayerElementMaterial");

            geo.AddNode(layer);
        }

        private void AddLayerRef(FbxNode layer, string typeName, int index = 0)
        {
            var el = N("LayerElement");
            AddSimpleNode(el, "Type", typeName);
            var idx = N("TypedIndex"); idx.AddProperty(new IntegerToken(index));
            el.AddNode(idx);
            layer.AddNode(el);
        }

        private void ConnectMaterialsToModel(ImportedMesh mesh, long modelId, IImported convert)
        {
            if (modelId == 0 || convert.MaterialList == null)
                return;

            var connectedMats = new HashSet<string>();
            foreach (var sub in mesh.SubmeshList)
            {
                if (sub.Material != null && !connectedMats.Contains(sub.Material))
                {
                    connectedMats.Add(sub.Material);
                    if (_materialIdMap.TryGetValue(sub.Material, out var matId))
                        Connect(matId, modelId);
                }
            }
        }

        private void ExportSkin(ImportedMesh mesh, long geoId)
        {
            var skinId = GenId();
            _exportedSkinCount++;
            var skin = N("Deformer");
            skin.AddProperty(new LongToken(skinId));
            skin.AddProperty(new StringToken("Deformer::Skin"));
            skin.AddProperty(new StringToken("Skin"));
            AddSimpleNode(skin, "Version", 101);
            AddSimpleNode(skin, "Link_DeformAcuracy", 50.0);
            _objects.AddNode(skin);
            Connect(skinId, geoId);

            var meshBindMatrix = GetFrameMatrix(mesh.Path, System.Numerics.Matrix4x4.Identity);
            var transform = ToFbxMatrixArray(meshBindMatrix);

            for (int boneIdx = 0; boneIdx < mesh.BoneList.Count; boneIdx++)
            {
                var bone = mesh.BoneList[boneIdx];
                if (bone.Path == null)
                    continue;

                var clusterId = GenId();
                _exportedClusterCount++;
                var cluster = N("Deformer");
                cluster.AddProperty(new LongToken(clusterId));
                cluster.AddProperty(new StringToken($"SubDeformer::{bone.Path}"));
                cluster.AddProperty(new StringToken("Cluster"));
                AddSimpleNode(cluster, "Version", 100);
                AddSimpleNode(cluster, "Mode", "TotalOne");
                AddSimpleNode(cluster, "UserData", "");

                var indices = new List<int>();
                var weights = new List<double>();
                for (int v = 0; v < mesh.VertexList.Count; v++)
                {
                    var vert = mesh.VertexList[v];
                    if (vert.BoneIndices == null) continue;
                    for (int w = 0; w < 4; w++)
                    {
                        if (vert.BoneIndices[w] == boneIdx && vert.Weights[w] > 0)
                        {
                            indices.Add(v);
                            weights.Add(vert.Weights[w]);
                        }
                    }
                }

                if (indices.Count > 0)
                {
                    var idxNode = N("Indexes");
                    idxNode.AddProperty(new IntegerArrayToken(indices.ToArray()));
                    cluster.AddNode(idxNode);

                    var wNode = N("Weights");
                    wNode.AddProperty(new DoubleArrayToken(weights.ToArray()));
                    cluster.AddNode(wNode);
                }

                var transformLink = ToFbxMatrixArray(GetBindPoseLinkMatrix(meshBindMatrix, bone));

                var tNode = N("Transform");
                tNode.AddProperty(new DoubleArrayToken(transform));
                cluster.AddNode(tNode);

                var tlNode = N("TransformLink");
                tlNode.AddProperty(new DoubleArrayToken(transformLink));
                cluster.AddNode(tlNode);

                _objects.AddNode(cluster);
                Connect(clusterId, skinId);

                if (TryGetFrameId(bone.Path, out var boneModelId))
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
            matNode.AddProperty(new StringToken($"Material::{mat.Name}"));
            matNode.AddProperty(new StringToken(""));

            AddSimpleNode(matNode, "Version", 102);
            AddSimpleNode(matNode, "ShadingModel", "Phong");

            var props = N("Properties70");
            props.AddNode(MakeColorP("DiffuseColor", mat.Diffuse));
            props.AddNode(MakeColorP("AmbientColor", mat.Ambient));
            props.AddNode(MakeColorP("EmissiveColor", mat.Emissive));
            props.AddNode(MakeColorP("SpecularColor", mat.Specular));

            var shininess = N("P");
            shininess.AddProperty(new StringToken("Shininess"));
            shininess.AddProperty(new StringToken("Number"));
            shininess.AddProperty(new StringToken(""));
            shininess.AddProperty(new StringToken("A"));
            shininess.AddProperty(new DoubleToken(mat.Shininess));
            props.AddNode(shininess);

            matNode.AddNode(props);
            _objects.AddNode(matNode);

            ExportMaterialTextures(mat, id, convert);
        }

        private void ExportMaterialTextures(ImportedMaterial mat, long materialId, IImported convert)
        {
            if (mat.Textures == null || convert.TextureList == null)
                return;

            foreach (var matTex in mat.Textures)
            {
                if (string.IsNullOrEmpty(matTex.Name))
                    continue;

                var texture = ImportedHelpers.FindTexture(matTex.Name, convert.TextureList);
                if (texture == null)
                {
                    Logger.Warning($"Material '{mat.Name}' references texture '{matTex.Name}', but it was not converted.");
                    continue;
                }

                var textureId = ExportTexture(texture);
                ConnectProperty(textureId, materialId, GetMaterialTextureProperty(matTex.Dest));
            }
        }

        private long ExportTexture(ImportedTexture texture)
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
            texNode.AddProperty(new StringToken($"Texture::{texture.Name}"));
            texNode.AddProperty(new StringToken(""));
            AddSimpleNode(texNode, "Type", "TextureVideoClip");
            AddSimpleNode(texNode, "Version", 202);
            AddSimpleNode(texNode, "TextureName", $"Texture::{texture.Name}");
            AddSimpleNode(texNode, "Media", $"Video::{texture.Name}");
            AddSimpleNode(texNode, "FileName", fileName);
            AddSimpleNode(texNode, "RelativeFilename", relativeFileName);
            _objects.AddNode(texNode);

            var videoNode = N("Video");
            videoNode.AddProperty(new LongToken(videoId));
            videoNode.AddProperty(new StringToken($"Video::{texture.Name}"));
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
            return Path.GetInvalidFileNameChars().Aggregate(name, (current, c) => current.Replace(c, '_'));
        }

        private static string GetMaterialTextureProperty(int dest)
        {
            switch (dest)
            {
                case 1:
                    return "NormalMap";
                case 2:
                    return "SpecularColor";
                case 3:
                    return "Bump";
                default:
                    return "DiffuseColor";
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

            var stackId = GenId();
            _exportedAnimationStackCount++;
            var stack = N("AnimationStack");
            stack.AddProperty(new LongToken(stackId));
            stack.AddProperty(new StringToken($"AnimStack::{anim.Name}"));
            stack.AddProperty(new StringToken(""));

            var stackProps = N("Properties70");
            float maxTime = 0;
            foreach (var track in anim.TrackList)
            {
                foreach (var k in track.Translations) if (k.time > maxTime) maxTime = k.time;
                foreach (var k in track.Rotations) if (k.time > maxTime) maxTime = k.time;
                foreach (var k in track.Scalings) if (k.time > maxTime) maxTime = k.time;
                if (track.BlendShape != null)
                {
                    foreach (var k in track.BlendShape.Keyframes) if (k.time > maxTime) maxTime = k.time;
                }
            }
            var fbxTime = (long)(maxTime * 46186158000L);
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
            layer.AddProperty(new StringToken("AnimLayer::Base Layer"));
            layer.AddProperty(new StringToken(""));
            _objects.AddNode(layer);
            Connect(layerId, stackId);

            var exportedTrackCount = 0;
            var missingTrackCount = 0;
            foreach (var track in anim.TrackList)
            {
                if (track.Path == null) continue;
                if (!TryGetFrameId(track.Path, out var modelId))
                {
                    missingTrackCount++;
                    _missingAnimationTrackCount++;
                    continue;
                }

                var exportedTrack = false;
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

                if (track.BlendShape != null && track.BlendShape.Keyframes.Count > 0)
                {
                    var mapKey = track.Path + "::" + track.BlendShape.ChannelName;
                    if (_blendShapeChannelMap.TryGetValue(mapKey, out var bsChannelId))
                    {
                        ExportCurveNode1D(layerId, bsChannelId, track.BlendShape.ChannelName, "DeformPercent", track.BlendShape.Keyframes);
                        exportedTrack = true;
                    }
                }

                if (exportedTrack)
                {
                    _exportedAnimationTrackCount++;
                    exportedTrackCount++;
                }
            }

            if (exportedTrackCount == 0)
                Logger.Warning($"Animation '{anim.Name}' has {anim.TrackList.Count} tracks, but none matched the exported frame hierarchy.");
            else if (missingTrackCount > 0)
                Logger.Warning($"Animation '{anim.Name}' exported {exportedTrackCount} tracks; {missingTrackCount} track paths did not match the exported frame hierarchy.");
        }

        private bool TryGetFrameId(string path, out long modelId)
        {
            modelId = 0;
            var normalizedPath = NormalizeFramePath(path);
            if (string.IsNullOrEmpty(normalizedPath))
                return false;

            if (_frameIdMap.TryGetValue(normalizedPath, out modelId))
                return true;

            var rootMatch = _rootFrame?.FindFrameByPath(normalizedPath);
            if (rootMatch != null && _frameIdMap.TryGetValue(NormalizeFramePath(rootMatch.Path), out modelId))
                return true;

            var suffix = "/" + normalizedPath;
            var matchCount = 0;
            foreach (var item in _frameIdMap)
            {
                if (!item.Key.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                modelId = item.Value;
                matchCount++;
                if (matchCount > 1)
                {
                    modelId = 0;
                    return false;
                }
            }

            return matchCount == 1;
        }

        private System.Numerics.Matrix4x4 GetBindPoseLinkMatrix(System.Numerics.Matrix4x4 meshBindMatrix, ImportedBone bone)
        {
            if (TryGetInverseBindPoseMatrix(bone.Matrix, out var inverseBindPose))
            {
                // Unity stores bind poses as bone^-1 * mesh; FBX clusters need the bone global matrix at bind time.
                return meshBindMatrix * inverseBindPose;
            }

            Logger.Warning($"Unable to invert bind pose for bone '{bone.Path}' while exporting FBX skin cluster; using frame transform.");
            return GetFrameMatrix(bone.Path, System.Numerics.Matrix4x4.Identity);
        }

        private static bool TryGetInverseBindPoseMatrix(Matrix4x4 matrix, out System.Numerics.Matrix4x4 inverseBindPose)
        {
            return System.Numerics.Matrix4x4.Invert(ToFbxMatrix(matrix), out inverseBindPose);
        }

        private static System.Numerics.Matrix4x4 ToFbxMatrix(Matrix4x4 matrix)
        {
            return new System.Numerics.Matrix4x4(
                matrix[0, 0], matrix[1, 0], matrix[2, 0], matrix[3, 0],
                matrix[0, 1], matrix[1, 1], matrix[2, 1], matrix[3, 1],
                matrix[0, 2], matrix[1, 2], matrix[2, 2], matrix[3, 2],
                matrix[0, 3], matrix[1, 3], matrix[2, 3], matrix[3, 3]);
        }

        private double[] GetFrameMatrixArray(string path, System.Numerics.Matrix4x4 fallback)
        {
            return ToFbxMatrixArray(GetFrameMatrix(path, fallback));
        }

        private System.Numerics.Matrix4x4 GetFrameMatrix(string path, System.Numerics.Matrix4x4 fallback)
        {
            var normalizedPath = NormalizeFramePath(path);
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                if (_frameGlobalMatrixMap.TryGetValue(normalizedPath, out var matrix))
                    return matrix;

                var suffix = "/" + normalizedPath;
                var matchCount = 0;
                var match = fallback;
                foreach (var item in _frameGlobalMatrixMap)
                {
                    if (!item.Key.EndsWith(suffix, StringComparison.Ordinal))
                        continue;

                    match = item.Value;
                    matchCount++;
                    if (matchCount > 1)
                        return fallback;
                }

                if (matchCount == 1)
                    return match;
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

        private static double[] ToFbxMatrixArray(System.Numerics.Matrix4x4 matrix)
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

            if (convert.MeshList != null)
            {
                foreach (var mesh in convert.MeshList)
                {
                    var meshPath = NormalizeFramePath(mesh.Path);
                    if (!string.IsNullOrEmpty(meshPath))
                        _meshPathSet.Add(meshPath);
                }
            }

            if (convert.MeshList == null)
                return;

            foreach (var mesh in convert.MeshList)
            {
                if (mesh.BoneList == null)
                    continue;

                foreach (var bone in mesh.BoneList)
                {
                    var normalizedPath = NormalizeFramePath(bone.Path);
                    if (string.IsNullOrEmpty(normalizedPath))
                        continue;

                    var frame = _rootFrame?.FindFrameByPath(normalizedPath);
                    var resolvedPath = NormalizeFramePath(frame?.Path ?? normalizedPath);
                    if (!string.IsNullOrEmpty(resolvedPath))
                        _skinBonePathSet.Add(resolvedPath);
                    AddBonePathWithParent(frame, normalizedPath);
                }
            }
        }

        private void AddBonePathWithParent(ImportedFrame frame, string fallbackPath)
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
            if (_castToBone)
                return true;

            if (string.IsNullOrEmpty(normalizedPath))
                return false;

            return _bonePathSet.Contains(normalizedPath);
        }

        private bool ShouldZeroBoneTransform(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
                return false;

            if (_meshPathSet.Contains(normalizedPath))
                return false;

            return _bonePathSet.Contains(normalizedPath) || _skinBonePathSet.Contains(normalizedPath);
        }

        private static string NormalizeFramePath(string path)
        {
            return path?.Replace('\\', '/').Trim('/');
        }

        private void ExportCurveNode1D(long layerId, long targetId, string channelName, string propName, List<ImportedKeyframe<float>> keyframes)
        {
            var curveNodeId = GenId();
            var curveNode = N("AnimationCurveNode");
            curveNode.AddProperty(new LongToken(curveNodeId));
            curveNode.AddProperty(new StringToken($"AnimCurveNode::{channelName}"));
            curveNode.AddProperty(new StringToken(""));
            var props = N("Properties70");
            props.AddNode(MakeNumberP("d", keyframes.Count > 0 ? keyframes[0].value : 0));
            curveNode.AddNode(props);
            _objects.AddNode(curveNode);

            Connect(curveNodeId, layerId);
            ConnectProperty(curveNodeId, targetId, propName);

            var timesArr = new long[keyframes.Count];
            var valuesArr = new double[keyframes.Count];
            for (int i = 0; i < keyframes.Count; i++)
            {
                timesArr[i] = (long)(keyframes[i].time * 46186158000L);
                valuesArr[i] = keyframes[i].value;
            }

            ExportCurve(curveNodeId, "d", timesArr, valuesArr);
        }

        private void ExportCurveNode(long layerId, long modelId, string channel, string propName, List<ImportedKeyframe<Vector3>> keyframes)
        {
            var curveNodeId = GenId();
            var curveNode = N("AnimationCurveNode");
            curveNode.AddProperty(new LongToken(curveNodeId));
            curveNode.AddProperty(new StringToken($"AnimCurveNode::{channel}"));
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

            var timesArr = new long[keyframes.Count];
            var xArr = new double[keyframes.Count];
            var yArr = new double[keyframes.Count];
            var zArr = new double[keyframes.Count];
            for (int i = 0; i < keyframes.Count; i++)
            {
                timesArr[i] = (long)(keyframes[i].time * 46186158000L);
                xArr[i] = keyframes[i].value.X;
                yArr[i] = keyframes[i].value.Y;
                zArr[i] = keyframes[i].value.Z;
            }

            ExportCurve(curveNodeId, "d|X", timesArr, xArr);
            ExportCurve(curveNodeId, "d|Y", timesArr, yArr);
            ExportCurve(curveNodeId, "d|Z", timesArr, zArr);
        }

        private void ExportCurve(long curveNodeId, string channel, long[] times, double[] values)
        {
            var curveId = GenId();
            _exportedAnimationCurveCount++;
            var curve = N("AnimationCurve");
            curve.AddProperty(new LongToken(curveId));
            curve.AddProperty(new StringToken($"AnimCurve::"));
            curve.AddProperty(new StringToken(""));

            AddSimpleNode(curve, "Default", 0.0);
            AddSimpleNode(curve, "KeyVer", 4009);

            var keyTime = N("KeyTime");
            keyTime.AddProperty(new LongArrayToken(times));
            curve.AddNode(keyTime);

            var keyValueFloat = N("KeyValueFloat");
            var floatValues = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
                floatValues[i] = (float)SanitizeDouble(values[i]);
            keyValueFloat.AddProperty(new FloatArrayToken(floatValues));
            curve.AddNode(keyValueFloat);

            var keyAttrFlags = N("KeyAttrFlags");
            keyAttrFlags.AddProperty(new IntegerArrayToken(new[] { 24840 }));
            curve.AddNode(keyAttrFlags);

            var keyAttrDataFloat = N("KeyAttrDataFloat");
            keyAttrDataFloat.AddProperty(new FloatArrayToken(new[] { 0f, 0f, 9.419963E-30f, 0f }));
            curve.AddNode(keyAttrDataFloat);

            var keyAttrRefCount = N("KeyAttrRefCount");
            keyAttrRefCount.AddProperty(new IntegerArrayToken(new[] { values.Length }));
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

        private void ExportMorph(ImportedMorph morph)
        {
            if (morph.Channels == null || morph.Channels.Count == 0) return;
            if (morph.Path == null) return;
            if (!_meshIdMap.TryGetValue(morph.Path, out var geoId)) return;

            var blendShapeId = GenId();
            var bs = N("Deformer");
            bs.AddProperty(new LongToken(blendShapeId));
            bs.AddProperty(new StringToken($"Deformer::BlendShape"));
            bs.AddProperty(new StringToken("BlendShape"));
            AddSimpleNode(bs, "Version", 100);
            _objects.AddNode(bs);
            Connect(blendShapeId, geoId);

            foreach (var channel in morph.Channels)
            {
                var channelId = GenId();
                var bc = N("Deformer");
                bc.AddProperty(new LongToken(channelId));
                bc.AddProperty(new StringToken($"SubDeformer::{channel.Name}"));
                bc.AddProperty(new StringToken("BlendShapeChannel"));
                AddSimpleNode(bc, "Version", 100);
                AddSimpleNode(bc, "DeformPercent", 0.0);

                var fullWeights = N("FullWeights");
                var weights = new double[channel.KeyframeList.Count];
                for (int i = 0; i < weights.Length; i++) weights[i] = channel.KeyframeList[i].Weight;
                fullWeights.AddProperty(new DoubleArrayToken(weights));
                bc.AddNode(fullWeights);

                _objects.AddNode(bc);
                Connect(channelId, blendShapeId);

                _blendShapeChannelMap[morph.Path + "::" + channel.Name] = channelId;

                foreach (var keyframe in channel.KeyframeList)
                {
                    var shapeId = GenId();
                    var shape = N("Geometry");
                    shape.AddProperty(new LongToken(shapeId));
                    shape.AddProperty(new StringToken($"Geometry::{channel.Name}"));
                    shape.AddProperty(new StringToken("Shape"));

                    AddSimpleNode(shape, "Version", 100);

                    var indices = new int[keyframe.VertexList.Count];
                    var verts = new double[keyframe.VertexList.Count * 3];
                    var normals = new double[keyframe.VertexList.Count * 3];
                    for (int i = 0; i < keyframe.VertexList.Count; i++)
                    {
                        var v = keyframe.VertexList[i];
                        indices[i] = (int)v.Index;
                        verts[i * 3] = v.Vertex.Vertex.X;
                        verts[i * 3 + 1] = v.Vertex.Vertex.Y;
                        verts[i * 3 + 2] = v.Vertex.Vertex.Z;
                        normals[i * 3] = v.Vertex.Normal.X;
                        normals[i * 3 + 1] = v.Vertex.Normal.Y;
                        normals[i * 3 + 2] = v.Vertex.Normal.Z;
                    }

                    var idxNode = N("Indexes");
                    idxNode.AddProperty(new IntegerArrayToken(indices));
                    shape.AddNode(idxNode);

                    var vertNode = N("Vertices");
                    vertNode.AddProperty(new DoubleArrayToken(verts));
                    shape.AddNode(vertNode);

                    if (keyframe.hasNormals)
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
            var header = N("FBXHeaderExtension");

            var ver = N("FBXHeaderVersion"); ver.AddProperty(new IntegerToken(1003));
            header.AddNode(ver);
            var fbxVer = N("FBXVersion"); fbxVer.AddProperty(new IntegerToken(7400));
            header.AddNode(fbxVer);

            var ts = N("CreationTimeStamp");
            var now = DateTime.Now;
            AddSimpleNode(ts, "Year", now.Year);
            AddSimpleNode(ts, "Month", now.Month);
            AddSimpleNode(ts, "Day", now.Day);
            AddSimpleNode(ts, "Hour", now.Hour);
            AddSimpleNode(ts, "Minute", now.Minute);
            AddSimpleNode(ts, "Second", now.Second);
            AddSimpleNode(ts, "Millisecond", now.Millisecond);
            header.AddNode(ts);

            _document.AddNode(header);
        }

        private void BuildDefinitions()
        {
            var definitions = N("Definitions");
            AddSimpleNode(definitions, "Version", 100);
            AddSimpleNode(definitions, "Count", 13);

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
            AddSimpleNode(objectType, "Count", 0);
            definitions.AddNode(objectType);
        }

        private void BuildTakes()
        {
            if (_takes.Count == 0)
                return;

            var takes = N("Takes");
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
                writer.WriteLine($"Exported skin clusters: {_exportedClusterCount}");
                writer.WriteLine($"Exported materials: {_exportedMaterialCount}");
                writer.WriteLine($"Exported textures: {_exportedTextureCount}");
                writer.WriteLine($"Exported animation stacks: {_exportedAnimationStackCount}");
                writer.WriteLine($"Exported animation tracks: {_exportedAnimationTrackCount}");
                writer.WriteLine($"Exported animation curves: {_exportedAnimationCurveCount}");
                writer.WriteLine($"Missing animation track targets: {_missingAnimationTrackCount}");
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
            props.AddNode(MakeIntP("UpAxis", 1));
            props.AddNode(MakeIntP("UpAxisSign", 1));
            props.AddNode(MakeIntP("FrontAxis", 2));
            props.AddNode(MakeIntP("FrontAxisSign", 1));
            props.AddNode(MakeIntP("CoordAxis", 0));
            props.AddNode(MakeIntP("CoordAxisSign", 1));
            props.AddNode(MakeIntP("OriginalUpAxis", -1));
            props.AddNode(MakeIntP("OriginalUpAxisSign", 1));

            var unitScale = N("P");
            unitScale.AddProperty(new StringToken("UnitScaleFactor"));
            unitScale.AddProperty(new StringToken("double"));
            unitScale.AddProperty(new StringToken("Number"));
            unitScale.AddProperty(new StringToken(""));
            unitScale.AddProperty(new DoubleToken(scaleFactor));
            props.AddNode(unitScale);

            gs.AddNode(props);
            _document.AddNode(gs);
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
            p.AddProperty(new DoubleToken(color.R));
            p.AddProperty(new DoubleToken(color.G));
            p.AddProperty(new DoubleToken(color.B));
            return p;
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
