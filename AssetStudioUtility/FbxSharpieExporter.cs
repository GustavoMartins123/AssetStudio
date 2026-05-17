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

        public FbxSharpieExporter(string fileName, float scaleFactor, int versionIndex, bool isAscii, bool is60Fps)
        {
            _scaleFactor = scaleFactor;
            _isAscii = isAscii;
            _document = new FbxDocument();
            _document.Version = FbxVersion.v7_4;

            BuildGlobalSettings(scaleFactor);

            _objects = N("Objects");
            _document.AddNode(_objects);

            _connections = N("Connections");
            _document.AddNode(_connections);
        }

        public void Export(IImported convert, string exportPath)
        {
            if (convert.RootFrame != null)
            {
                ExportFrame(convert.RootFrame, 0);
            }

            if (convert.MeshList != null)
            {
                foreach (var mesh in convert.MeshList)
                {
                    ExportMesh(mesh);
                }
            }

            if (convert.MaterialList != null)
            {
                foreach (var mat in convert.MaterialList)
                {
                    ExportMaterial(mat);
                }
            }

            if (_isAscii)
                FbxIO.WriteAscii(_document, exportPath);
            else
                FbxIO.WriteBinary(_document, exportPath);
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
            {
                ExportFrame(frame[i], id);
            }
        }

        private void ExportMesh(ImportedMesh mesh)
        {
            if (mesh.VertexList == null || mesh.VertexList.Count == 0)
                return;

            long modelId = 0;
            if (mesh.Path != null && _frameIdMap.ContainsKey(mesh.Path))
            {
                modelId = _frameIdMap[mesh.Path];
            }

            var geoId = GenId();
            var geo = N("Geometry");
            geo.AddProperty(new LongToken(geoId));
            geo.AddProperty(new StringToken($"Geometry::{mesh.Path}"));
            geo.AddProperty(new StringToken("Mesh"));

            var verts = new double[mesh.VertexList.Count * 3];
            for (int i = 0; i < mesh.VertexList.Count; i++)
            {
                var v = mesh.VertexList[i].Vertex;
                verts[i * 3] = v.X;
                verts[i * 3 + 1] = v.Y;
                verts[i * 3 + 2] = v.Z;
            }
            var vertNode = N("Vertices");
            vertNode.AddProperty(new DoubleArrayToken(verts));
            geo.AddNode(vertNode);

            var allIndices = new List<int>();
            foreach (var sub in mesh.SubmeshList)
            {
                foreach (var face in sub.FaceList)
                {
                    allIndices.Add(face.VertexIndices[0]);
                    allIndices.Add(face.VertexIndices[1]);
                    allIndices.Add(~face.VertexIndices[2]);
                }
            }
            var polyNode = N("PolygonVertexIndex");
            polyNode.AddProperty(new IntegerArrayToken(allIndices.ToArray()));
            geo.AddNode(polyNode);

            if (mesh.hasNormal)
            {
                var normalLayer = N("LayerElementNormal");
                normalLayer.AddProperty(new IntegerToken(0));

                var ver = N("Version"); ver.AddProperty(new IntegerToken(101));
                normalLayer.AddNode(ver);

                var nameN = N("Name"); nameN.AddProperty(new StringToken(""));
                normalLayer.AddNode(nameN);

                var mapType = N("MappingInformationType"); mapType.AddProperty(new StringToken("ByControlPoint"));
                normalLayer.AddNode(mapType);

                var refType = N("ReferenceInformationType"); refType.AddProperty(new StringToken("Direct"));
                normalLayer.AddNode(refType);

                var normals = new double[mesh.VertexList.Count * 3];
                for (int i = 0; i < mesh.VertexList.Count; i++)
                {
                    var n = mesh.VertexList[i].Normal;
                    normals[i * 3] = n.X;
                    normals[i * 3 + 1] = n.Y;
                    normals[i * 3 + 2] = n.Z;
                }
                var normData = N("Normals"); normData.AddProperty(new DoubleArrayToken(normals));
                normalLayer.AddNode(normData);

                geo.AddNode(normalLayer);
            }

            if (mesh.hasUV != null && mesh.hasUV[0])
            {
                var uvLayer = N("LayerElementUV");
                uvLayer.AddProperty(new IntegerToken(0));

                var ver = N("Version"); ver.AddProperty(new IntegerToken(101));
                uvLayer.AddNode(ver);

                var nameN = N("Name"); nameN.AddProperty(new StringToken("UVMap"));
                uvLayer.AddNode(nameN);

                var mapType = N("MappingInformationType"); mapType.AddProperty(new StringToken("ByControlPoint"));
                uvLayer.AddNode(mapType);

                var refType = N("ReferenceInformationType"); refType.AddProperty(new StringToken("Direct"));
                uvLayer.AddNode(refType);

                var uvs = new double[mesh.VertexList.Count * 2];
                for (int i = 0; i < mesh.VertexList.Count; i++)
                {
                    if (mesh.VertexList[i].UV != null && mesh.VertexList[i].UV[0] != null)
                    {
                        uvs[i * 2] = mesh.VertexList[i].UV[0][0];
                        uvs[i * 2 + 1] = mesh.VertexList[i].UV[0][1];
                    }
                }
                var uvData = N("UV"); uvData.AddProperty(new DoubleArrayToken(uvs));
                uvLayer.AddNode(uvData);

                geo.AddNode(uvLayer);
            }

            var layer = N("Layer");
            layer.AddProperty(new IntegerToken(0));
            var layerVer = N("Version"); layerVer.AddProperty(new IntegerToken(100));
            layer.AddNode(layerVer);

            if (mesh.hasNormal)
            {
                var layerEl = N("LayerElement");
                var leType = N("Type"); leType.AddProperty(new StringToken("LayerElementNormal"));
                layerEl.AddNode(leType);
                var leIdx = N("TypedIndex"); leIdx.AddProperty(new IntegerToken(0));
                layerEl.AddNode(leIdx);
                layer.AddNode(layerEl);
            }

            if (mesh.hasUV != null && mesh.hasUV[0])
            {
                var layerEl = N("LayerElement");
                var leType = N("Type"); leType.AddProperty(new StringToken("LayerElementUV"));
                layerEl.AddNode(leType);
                var leIdx = N("TypedIndex"); leIdx.AddProperty(new IntegerToken(0));
                layerEl.AddNode(leIdx);
                layer.AddNode(layerEl);
            }

            geo.AddNode(layer);
            _objects.AddNode(geo);

            if (modelId != 0)
                Connect(geoId, modelId);
        }

        private void ExportMaterial(ImportedMaterial mat)
        {
            var id = GenId();
            var matNode = N("Material");
            matNode.AddProperty(new LongToken(id));
            matNode.AddProperty(new StringToken($"Material::{mat.Name}"));
            matNode.AddProperty(new StringToken(""));

            var ver = N("Version"); ver.AddProperty(new IntegerToken(102));
            matNode.AddNode(ver);

            var shadingModel = N("ShadingModel"); shadingModel.AddProperty(new StringToken("Phong"));
            matNode.AddNode(shadingModel);

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

        private void BuildGlobalSettings(float scaleFactor)
        {
            var gs = N("GlobalSettings");
            var ver = N("Version"); ver.AddProperty(new IntegerToken(1000));
            gs.AddNode(ver);

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
