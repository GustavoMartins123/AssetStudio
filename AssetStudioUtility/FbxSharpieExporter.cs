using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private Dictionary<string, long> _frameIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _materialIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _meshIdMap = new Dictionary<string, long>();
        private Dictionary<string, long> _blendShapeChannelMap = new Dictionary<string, long>();

        public FbxSharpieExporter(string fileName, float scaleFactor, int versionIndex, bool isAscii, bool is60Fps)
        {
            _scaleFactor = scaleFactor;
            _isAscii = isAscii;
            _document = new FbxDocument();
            _document.Version = FbxVersion.v7_4;

            BuildHeader();
            BuildGlobalSettings(scaleFactor);

            _objects = N("Objects");
            _document.AddNode(_objects);

            _connections = N("Connections");
            _document.AddNode(_connections);
        }

        public void Export(IImported convert, string exportPath)
        {
            if (convert.RootFrame != null)
                ExportFrame(convert.RootFrame, 0);

            if (convert.MaterialList != null)
            {
                foreach (var mat in convert.MaterialList)
                    ExportMaterial(mat);
            }

            if (convert.MeshList != null)
            {
                foreach (var mesh in convert.MeshList)
                    ExportMesh(mesh, convert);
            }

            if (convert.MorphList != null)
            {
                foreach (var morph in convert.MorphList)
                    ExportMorph(morph);
            }

            if (convert.AnimationList != null)
            {
                foreach (var anim in convert.AnimationList)
                    ExportAnimation(anim);
            }

            var dir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            FbxIO.WriteAscii(_document, exportPath);
        }

        private void ExportFrame(ImportedFrame frame, long parentId)
        {
            var id = GenId();
            _frameIdMap[frame.Path] = id;

            var model = N("Model");
            model.AddProperty(new LongToken(id));
            model.AddProperty(new StringToken($"Model::{frame.Name}"));
            model.AddProperty(new StringToken("Null"));

            var props = N("Properties70");
            props.AddNode(MakeP("Lcl Translation", "Lcl Translation", "", "A+",
                (double)frame.LocalPosition.X, (double)frame.LocalPosition.Y, (double)frame.LocalPosition.Z));
            props.AddNode(MakeP("Lcl Rotation", "Lcl Rotation", "", "A+",
                (double)frame.LocalRotation.X, (double)frame.LocalRotation.Y, (double)frame.LocalRotation.Z));
            props.AddNode(MakeP("Lcl Scaling", "Lcl Scaling", "", "A+",
                (double)frame.LocalScale.X, (double)frame.LocalScale.Y, (double)frame.LocalScale.Z));
            model.AddNode(props);

            _objects.AddNode(model);
            Connect(id, parentId);

            for (int i = 0; i < frame.Count; i++)
                ExportFrame(frame[i], id);
        }

        private void ExportMesh(ImportedMesh mesh, IImported convert)
        {
            if (mesh.VertexList == null || mesh.VertexList.Count == 0)
                return;

            long modelId = 0;
            if (mesh.Path != null && _frameIdMap.ContainsKey(mesh.Path))
                modelId = _frameIdMap[mesh.Path];

            var geoId = GenId();
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

            if (mesh.BoneList != null && mesh.BoneList.Count > 0)
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
            var skin = N("Deformer");
            skin.AddProperty(new LongToken(skinId));
            skin.AddProperty(new StringToken("Deformer::Skin"));
            skin.AddProperty(new StringToken("Skin"));
            AddSimpleNode(skin, "Version", 101);
            AddSimpleNode(skin, "Link_DeformAcuracy", 50.0);
            _objects.AddNode(skin);
            Connect(skinId, geoId);

            foreach (var bone in mesh.BoneList)
            {
                if (bone.Path == null)
                    continue;

                var clusterId = GenId();
                var cluster = N("Deformer");
                cluster.AddProperty(new LongToken(clusterId));
                cluster.AddProperty(new StringToken($"SubDeformer::{bone.Path}"));
                cluster.AddProperty(new StringToken("Cluster"));
                AddSimpleNode(cluster, "Version", 100);
                AddSimpleNode(cluster, "UserData", "");

                var boneIdx = mesh.BoneList.IndexOf(bone);
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

                var transform = new double[16];
                var transformLink = new double[16];
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        transform[r * 4 + c] = (r == c) ? 1.0 : 0.0;
                        transformLink[r * 4 + c] = bone.Matrix[r, c];
                    }
                }

                var tNode = N("Transform");
                tNode.AddProperty(new DoubleArrayToken(transform));
                cluster.AddNode(tNode);

                var tlNode = N("TransformLink");
                tlNode.AddProperty(new DoubleArrayToken(transformLink));
                cluster.AddNode(tlNode);

                _objects.AddNode(cluster);
                Connect(clusterId, skinId);

                if (_frameIdMap.TryGetValue(bone.Path, out var boneModelId))
                    Connect(boneModelId, clusterId);
            }
        }

        private void ExportMaterial(ImportedMaterial mat)
        {
            var id = GenId();
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
        }

        private void ExportAnimation(ImportedKeyframedAnimation anim)
        {
            if (anim.TrackList == null || anim.TrackList.Count == 0)
                return;

            var stackId = GenId();
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
            }
            var fbxTime = (long)(maxTime * 46186158000L);
            var localStop = N("P");
            localStop.AddProperty(new StringToken("LocalStop"));
            localStop.AddProperty(new StringToken("KTime"));
            localStop.AddProperty(new StringToken("Time"));
            localStop.AddProperty(new StringToken(""));
            localStop.AddProperty(new LongToken(fbxTime));
            stackProps.AddNode(localStop);
            stack.AddNode(stackProps);
            _objects.AddNode(stack);
            Connect(stackId, 0);

            var layerId = GenId();
            var layer = N("AnimationLayer");
            layer.AddProperty(new LongToken(layerId));
            layer.AddProperty(new StringToken($"AnimLayer::{anim.Name}"));
            layer.AddProperty(new StringToken(""));
            _objects.AddNode(layer);
            Connect(layerId, stackId);

            foreach (var track in anim.TrackList)
            {
                if (track.Path == null) continue;
                if (!_frameIdMap.TryGetValue(track.Path, out var modelId)) continue;

                if (track.Translations.Count > 0)
                    ExportCurveNode(layerId, modelId, "T", "Lcl Translation", track.Translations);
                if (track.Rotations.Count > 0)
                    ExportCurveNode(layerId, modelId, "R", "Lcl Rotation", track.Rotations);
                if (track.Scalings.Count > 0)
                    ExportCurveNode(layerId, modelId, "S", "Lcl Scaling", track.Scalings);

                if (track.BlendShape != null && track.BlendShape.Keyframes.Count > 0)
                {
                    var mapKey = track.Path + "::" + track.BlendShape.ChannelName;
                    if (_blendShapeChannelMap.TryGetValue(mapKey, out var bsChannelId))
                        ExportCurveNode1D(layerId, bsChannelId, track.BlendShape.ChannelName, "DeformPercent", track.BlendShape.Keyframes);
                }
            }
        }

        private void ExportCurveNode1D(long layerId, long targetId, string channelName, string propName, List<ImportedKeyframe<float>> keyframes)
        {
            var curveNodeId = GenId();
            var curveNode = N("AnimationCurveNode");
            curveNode.AddProperty(new LongToken(curveNodeId));
            curveNode.AddProperty(new StringToken($"AnimCurveNode::{channelName}"));
            curveNode.AddProperty(new StringToken(""));
            var props = N("Properties70");
            props.AddNode(MakeP("d", "Number", "", "A", 0, 0, 0));
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
            props.AddNode(MakeP("d|X", "Number", "", "A", 0, 0, 0));
            props.AddNode(MakeP("d|Y", "Number", "", "A", 0, 0, 0));
            props.AddNode(MakeP("d|Z", "Number", "", "A", 0, 0, 0));
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
                floatValues[i] = (float)values[i];
            keyValueFloat.AddProperty(new FloatArrayToken(floatValues));
            curve.AddNode(keyValueFloat);

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
            p.AddProperty(new DoubleToken(x));
            p.AddProperty(new DoubleToken(y));
            p.AddProperty(new DoubleToken(z));
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
