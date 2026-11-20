using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public class ModelConverter : IImported
    {
        public ImportedFrame RootFrame { get; protected set; } = new ImportedFrame();
        public List<ImportedMesh> MeshList { get; protected set; } = new List<ImportedMesh>();
        public List<ImportedMaterial> MaterialList { get; protected set; } = new List<ImportedMaterial>();
        public List<ImportedTexture> TextureList { get; protected set; } = new List<ImportedTexture>();
        public List<ImportedKeyframedAnimation> AnimationList { get; protected set; } = new List<ImportedKeyframedAnimation>();
        public List<ImportedMorph> MorphList { get; protected set; } = new List<ImportedMorph>();

        private ImageFormat imageFormat;
        private Avatar? avatar;
        private bool hasRootFrame;
        private HashSet<AnimationClip> animationClipHashSet = new HashSet<AnimationClip>();
        private Dictionary<AnimationClip, string> boundAnimationPathDic = new Dictionary<AnimationClip, string>();
        private Dictionary<uint, string> bonePathHash = new Dictionary<uint, string>();
        private Dictionary<Texture2D, string> textureNameDictionary = new Dictionary<Texture2D, string>();
        private Dictionary<Transform, ImportedFrame> transformDictionary = new Dictionary<Transform, ImportedFrame>();
        Dictionary<uint, string> morphChannelNames = new Dictionary<uint, string>();

        private void SetRootFrame(ImportedFrame frame)
        {
            RootFrame = frame;
            hasRootFrame = true;
        }

        protected ModelConverter(ImageFormat imageFormat)
        {
            this.imageFormat = imageFormat;
        }

        public ModelConverter(GameObject m_GameObject, ImageFormat imageFormat, AnimationClip[]? animationList = null)
        {
            this.imageFormat = imageFormat;
            if (m_GameObject.m_Animator != null)
            {
                InitWithAnimator(m_GameObject.m_Animator);
                if (animationList == null)
                {
                    CollectAnimationClip(m_GameObject.m_Animator);
                }
            }
            else
            {
                InitWithGameObject(m_GameObject);
            }
            if (avatar == null)
            {
                avatar = FindAvatar(m_GameObject);
            }
            if (animationList != null)
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
        }

        public ModelConverter(string rootName, List<GameObject> m_GameObjects, ImageFormat imageFormat, AnimationClip[]? animationList = null)
        {
            this.imageFormat = imageFormat;
            SetRootFrame(CreateFrame(rootName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One));
            foreach (var m_GameObject in m_GameObjects)
            {
                if (m_GameObject.m_Animator != null && animationList == null)
                {
                    CollectAnimationClip(m_GameObject.m_Animator);
                }

                var m_Transform = m_GameObject.m_Transform;
                ConvertTransforms(m_Transform, RootFrame);
                CreateBonePathHash(m_Transform);
            }
            foreach (var m_GameObject in m_GameObjects)
            {
                var m_Transform = m_GameObject.m_Transform;
                ConvertMeshRenderer(m_Transform);
            }
            if (avatar == null)
            {
                foreach (var go in m_GameObjects)
                {
                    avatar = FindAvatar(go);
                    if (avatar != null) break;
                }
            }
            if (animationList != null)
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
        }

        public ModelConverter(Animator m_Animator, ImageFormat imageFormat, AnimationClip[]? animationList = null)
        {
            this.imageFormat = imageFormat;
            InitWithAnimator(m_Animator);
            if (avatar == null && m_Animator.m_GameObject.TryGet(out var go))
            {
                avatar = FindAvatar(go);
            }
            if (animationList == null)
            {
                CollectAnimationClip(m_Animator);
            }
            else
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
        }

        private void InitWithAnimator(Animator m_Animator)
        {
            if (!m_Animator.m_Avatar.TryGet(out var m_Avatar))
            {
                if (m_Animator.m_Avatar != null && !m_Animator.m_Avatar.IsNull)
                {
                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(m_Animator.m_Avatar.m_PathID, out var obj) && obj is Avatar foundAvatar)
                        {
                            m_Avatar = foundAvatar;
                            Logger.Info($"Found Avatar '{foundAvatar.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                            break;
                        }
                    }
                }
            }
            if (m_Avatar != null)
                avatar = m_Avatar;

            if (!m_Animator.m_GameObject.TryGet(out var m_GameObject))
            {
                if (m_Animator.m_GameObject != null && !m_Animator.m_GameObject.IsNull)
                {
                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(m_Animator.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                        {
                            m_GameObject = foundGo;
                            Logger.Info($"Found GameObject '{foundGo.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                            break;
                        }
                    }
                }
            }
            if (m_GameObject != null)
            {
                InitWithGameObject(m_GameObject, m_Animator.m_HasTransformHierarchy);
            }
            else
            {
                Logger.Error($"GameObject for Animator (PathID: {m_Animator.m_PathID}) could not be resolved.");
            }
        }

        private void InitWithGameObject(GameObject m_GameObject, bool hasTransformHierarchy = true)
        {
            var m_Transform = m_GameObject.m_Transform;
            if (!hasTransformHierarchy)
            {
                ConvertTransforms(m_Transform, null);
                DeoptimizeTransformHierarchy();
            }
            else
            {
                var frameList = new List<ImportedFrame>();
                var visitedFathers = new HashSet<Transform>();
                var tempTransform = m_Transform;
                if (tempTransform != null)
                {
                    visitedFathers.Add(tempTransform);
                }
                while (tempTransform != null)
                {
                    var fatherPtr = tempTransform.m_Father;
                    if (fatherPtr == null || fatherPtr.IsNull)
                        break;
                    Transform? m_Father = null;
                    if (!fatherPtr.TryGet(out m_Father))
                    {
                        foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                        {
                            if (sf.ObjectsDic.TryGetValue(fatherPtr.m_PathID, out var obj) && obj is Transform foundFather)
                            {
                                m_Father = foundFather;
                                break;
                            }
                        }
                    }
                    if (m_Father == null || !visitedFathers.Add(m_Father))
                        break;
                    frameList.Add(ConvertTransform(m_Father));
                    tempTransform = m_Father;
                }
                if (frameList.Count > 0)
                {
                    SetRootFrame(frameList[frameList.Count - 1]);
                    for (var i = frameList.Count - 2; i >= 0; i--)
                    {
                        var frame = frameList[i];
                        var parent = frameList[i + 1];
                        parent.AddChild(frame);
                    }
                    ConvertTransforms(m_Transform, frameList[0]);
                }
                else
                {
                    ConvertTransforms(m_Transform, null);
                }

                CreateBonePathHash(m_Transform);
            }

            ConvertMeshRenderer(m_Transform);
        }

        private void ConvertMeshRenderer(Transform m_Transform)
        {
            ConvertMeshRenderer(m_Transform, new HashSet<Transform>());
        }

        private void ConvertMeshRenderer(Transform m_Transform, HashSet<Transform> visited)
        {
            if (m_Transform == null || !visited.Add(m_Transform))
                return;

            if (!m_Transform.m_GameObject.TryGet(out var m_GameObject))
            {
                foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                {
                    if (sf.ObjectsDic.TryGetValue(m_Transform.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        m_GameObject = foundGo;
                        break;
                    }
                }
            }

            if (m_GameObject != null)
            {
                if (m_GameObject.m_MeshRenderer != null)
                {
                    ConvertMeshRenderer(m_GameObject.m_MeshRenderer);
                }

                if (m_GameObject.m_SkinnedMeshRenderer != null)
                {
                    ConvertMeshRenderer(m_GameObject.m_SkinnedMeshRenderer);
                }

                if (m_GameObject.m_Animation != null)
                {
                    foreach (var animation in m_GameObject.m_Animation.m_Animations)
                    {
                        var animationPtr = animation;
                        if (!animationPtr.TryGet(out var animationClip))
                        {
                            foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                            {
                                if (sf.ObjectsDic.TryGetValue(animationPtr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                {
                                    animationClip = foundClip;
                                    break;
                                }
                            }
                        }
                        if (animationClip != null)
                        {
                            var transformPath = GetTransformPath(m_Transform);
                            if (transformPath != null && !boundAnimationPathDic.ContainsKey(animationClip))
                            {
                                boundAnimationPathDic.Add(animationClip, transformPath);
                            }
                            animationClipHashSet.Add(animationClip);
                        }
                    }
                }
            }

            foreach (var pptr in m_Transform.m_Children)
            {
                var childPtr = pptr;
                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in m_Transform.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }
                if (child != null)
                    ConvertMeshRenderer(child, visited);
            }
        }

        private void CollectAnimationClip(Animator m_Animator)
        {
            RuntimeAnimatorController? m_Controller = null;
            if (!m_Animator.m_Controller.TryGet(out m_Controller))
            {
                if (m_Animator.m_Controller != null && !m_Animator.m_Controller.IsNull)
                {
                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(m_Animator.m_Controller.m_PathID, out var obj) && obj is RuntimeAnimatorController foundController)
                        {
                            m_Controller = foundController;
                            Logger.Info($"Found Controller '{foundController.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                            break;
                        }
                    }
                }
            }

            if (m_Controller != null)
            {
                switch (m_Controller)
                {
                    case AnimatorOverrideController m_AnimatorOverrideController:
                        {
                            foreach (var clipOverride in m_AnimatorOverrideController.m_Clips)
                            {
                                AnimationClip? m_OverrideClip = null;
                                if (clipOverride.m_OverrideClip != null && !clipOverride.m_OverrideClip.TryGet(out m_OverrideClip) && !clipOverride.m_OverrideClip.IsNull)
                                {
                                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                    {
                                        if (sf.ObjectsDic.TryGetValue(clipOverride.m_OverrideClip.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                        {
                                            m_OverrideClip = foundClip;
                                            break;
                                        }
                                    }
                                }

                                if (m_OverrideClip != null)
                                {
                                    animationClipHashSet.Add(m_OverrideClip);
                                }
                                else
                                {
                                    AnimationClip? m_OriginalClip = null;
                                    if (clipOverride.m_OriginalClip != null && !clipOverride.m_OriginalClip.TryGet(out m_OriginalClip) && !clipOverride.m_OriginalClip.IsNull)
                                    {
                                        foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                        {
                                            if (sf.ObjectsDic.TryGetValue(clipOverride.m_OriginalClip.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                            {
                                                m_OriginalClip = foundClip;
                                                break;
                                            }
                                        }
                                    }
                                    if (m_OriginalClip != null)
                                    {
                                        animationClipHashSet.Add(m_OriginalClip);
                                    }
                                }
                            }

                            AnimatorController? m_AnimatorController = null;
                            if (m_AnimatorOverrideController.m_Controller != null && !m_AnimatorOverrideController.m_Controller.TryGet<AnimatorController>(out m_AnimatorController) && !m_AnimatorOverrideController.m_Controller.IsNull)
                            {
                                foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                {
                                    if (sf.ObjectsDic.TryGetValue(m_AnimatorOverrideController.m_Controller.m_PathID, out var obj) && obj is AnimatorController foundController)
                                    {
                                        m_AnimatorController = foundController;
                                        break;
                                    }
                                }
                            }

                            if (m_AnimatorController != null)
                            {
                                foreach (var pptr in m_AnimatorController.m_AnimationClips)
                                {
                                    AnimationClip? m_AnimationClip = null;
                                    if (pptr != null && !pptr.TryGet(out m_AnimationClip) && !pptr.IsNull)
                                    {
                                        foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                        {
                                            if (sf.ObjectsDic.TryGetValue(pptr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                            {
                                                m_AnimationClip = foundClip;
                                                break;
                                            }
                                        }
                                    }
                                    if (m_AnimationClip != null)
                                    {
                                        animationClipHashSet.Add(m_AnimationClip);
                                    }
                                }
                            }
                            break;
                        }

                    case AnimatorController m_AnimatorController:
                        {
                            foreach (var pptr in m_AnimatorController.m_AnimationClips)
                            {
                                AnimationClip? m_AnimationClip = null;
                                if (pptr != null && !pptr.TryGet(out m_AnimationClip) && !pptr.IsNull)
                                {
                                    foreach (var sf in m_Animator.assetsFile.assetsManager.assetsFileList)
                                    {
                                        if (sf.ObjectsDic.TryGetValue(pptr.m_PathID, out var obj) && obj is AnimationClip foundClip)
                                        {
                                            m_AnimationClip = foundClip;
                                            break;
                                        }
                                    }
                                }
                                if (m_AnimationClip != null)
                                {
                                    animationClipHashSet.Add(m_AnimationClip);
                                }
                            }
                            break;
                        }
                }
            }
        }

        private ImportedFrame ConvertTransform(Transform trans)
        {
            if (transformDictionary.TryGetValue(trans, out var existingFrame))
            {
                return existingFrame;
            }

            var frame = new ImportedFrame(trans.m_Children.Length);
            transformDictionary[trans] = frame;
            if (!trans.m_GameObject.TryGet(out var m_GameObject))
            {
                foreach (var sf in trans.assetsFile.assetsManager.assetsFileList)
                {
                    if (sf.ObjectsDic.TryGetValue(trans.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        m_GameObject = foundGo;
                        break;
                    }
                }
            }
            frame.Name = m_GameObject != null ? m_GameObject.m_Name : $"GameObject_{trans.m_GameObject.m_PathID}";
            SetFrame(frame, trans.m_LocalPosition, trans.m_LocalRotation, trans.m_LocalScale);
            return frame;
        }

        private static ImportedFrame CreateFrame(string name, Vector3 t, Quaternion q, Vector3 s)
        {
            var frame = new ImportedFrame();
            frame.Name = name;
            SetFrame(frame, t, q, s);
            return frame;
        }

        private static void SetFrame(ImportedFrame frame, Vector3 t, Quaternion q, Vector3 s)
        {
            frame.LocalPosition = new Vector3(-t.X, t.Y, t.Z);
            frame.LocalRotation = QuaternionToEuler(new Quaternion(q.X, -q.Y, -q.Z, q.W));
            frame.LocalScale = s;
        }

        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            float sqw = q.W * q.W;
            float sqx = q.X * q.X;
            float sqy = q.Y * q.Y;
            float sqz = q.Z * q.Z;
            float unit = sqx + sqy + sqz + sqw;
            if (unit <= float.Epsilon || float.IsNaN(unit) || float.IsInfinity(unit))
            {
                return Vector3.Zero;
            }
            float invNorm = 1f / (float)Math.Sqrt(unit);
            float qx = q.X * invNorm;
            float qy = q.Y * invNorm;
            float qz = q.Z * invNorm;
            float qw = q.W * invNorm;

            // Compute matrix elements for Euler XYZ rotation (Rx * Ry * Rz)
            // matching FbxSharpieExporter.BuildLocalMatrix and FBX default eEulerXYZ
            float m11 = 1f - 2f * (qy * qy + qz * qz);
            float m12 = 2f * (qx * qy + qz * qw);
            float m13 = 2f * (qx * qz - qy * qw);
            float m23 = 2f * (qy * qz + qx * qw);
            float m33 = 1f - 2f * (qx * qx + qy * qy);

            float sinY = -m13;
            sinY = Math.Max(-1f, Math.Min(1f, sinY));
            float rotY = (float)Math.Asin(sinY);

            float rotX, rotZ;
            if (Math.Abs(sinY) < 0.99999f)
            {
                rotX = (float)Math.Atan2(m23, m33);
                rotZ = (float)Math.Atan2(m12, m11);
            }
            else
            {
                float m21 = 2f * (qx * qy - qz * qw);
                float m22 = 1f - 2f * (qx * qx + qz * qz);
                rotX = (float)Math.Atan2(-m21, m22);
                rotZ = 0f;
            }

            const float rad2deg = 180f / (float)Math.PI;
            return new Vector3(rotX * rad2deg, rotY * rad2deg, rotZ * rad2deg);
        }

        private void ConvertTransforms(Transform trans, ImportedFrame? parent)
        {
            if (trans == null || transformDictionary.ContainsKey(trans))
            {
                return;
            }

            var frame = ConvertTransform(trans);
            if (parent == null)
            {
                SetRootFrame(frame);
            }
            else
            {
                parent.AddChild(frame);
            }
            foreach (var pptr in trans.m_Children)
            {
                var childPtr = pptr;
                if (!childPtr.TryGet(out var child))
                {
                    foreach (var sf in trans.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(childPtr.m_PathID, out var obj) && obj is Transform foundChild)
                        {
                            child = foundChild;
                            break;
                        }
                    }
                }
                if (child != null)
                    ConvertTransforms(child, frame);
            }
        }

        private void ConvertMeshRenderer(Renderer meshR)
        {
            var mesh = GetMesh(meshR);
            if (mesh == null)
                return;
            mesh.EnsureProcessed();
            var iMesh = new ImportedMesh();
            if (!meshR.m_GameObject.TryGet(out var m_GameObject2))
            {
                foreach (var sf in meshR.assetsFile.assetsManager.assetsFileList)
                {
                    if (sf.ObjectsDic.TryGetValue(meshR.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        m_GameObject2 = foundGo;
                        break;
                    }
                }
            }
            iMesh.Path = m_GameObject2 != null ? GetTransformPath(m_GameObject2.m_Transform) : "";
            iMesh.SubmeshList = new List<ImportedSubmesh>();
            var subHashSet = new HashSet<int>();
            var combine = false;
            int firstSubMesh = 0;
            if (meshR.m_StaticBatchInfo?.subMeshCount > 0)
            {
                firstSubMesh = meshR.m_StaticBatchInfo.firstSubMesh;
                var finalSubMesh = meshR.m_StaticBatchInfo.firstSubMesh + meshR.m_StaticBatchInfo.subMeshCount;
                for (int i = meshR.m_StaticBatchInfo.firstSubMesh; i < finalSubMesh; i++)
                {
                    subHashSet.Add(i);
                }
                combine = true;
            }
            else if (meshR.m_SubsetIndices?.Length > 0)
            {
                firstSubMesh = (int)meshR.m_SubsetIndices.Min(x => x);
                foreach (var index in meshR.m_SubsetIndices)
                {
                    subHashSet.Add((int)index);
                }
                combine = true;
            }

            iMesh.hasNormal = mesh.m_Normals?.Length > 0;
            iMesh.hasUV = new bool[8];
            for (int uv = 0; uv < 8; uv++)
            {
                iMesh.hasUV[uv] = mesh.GetUV(uv)?.Length > 0;
            }
            iMesh.hasTangent = mesh.m_Tangents != null && mesh.m_Tangents.Length == mesh.m_VertexCount * 4;
            iMesh.hasColor = mesh.m_Colors?.Length > 0;

            int firstFace = 0;
            for (int i = 0; i < mesh.m_SubMeshes.Length; i++)
            {
                int numFaces = (int)mesh.m_SubMeshes[i].indexCount / 3;
                if (subHashSet.Count > 0 && !subHashSet.Contains(i))
                {
                    firstFace += numFaces;
                    continue;
                }
                var submesh = mesh.m_SubMeshes[i];
                var iSubmesh = new ImportedSubmesh();
                Material? mat = null;
                if (i - firstSubMesh >= 0 && i - firstSubMesh < meshR.m_Materials.Length)
                {
                    var matPPtr = meshR.m_Materials[i - firstSubMesh];
                    if (matPPtr != null && !matPPtr.IsNull)
                    {
                        if (!matPPtr.TryGet(out var m_Material))
                        {
                            Logger.Warning($"Material TryGet failed for submesh {i}: FileID={matPPtr.m_FileID}, PathID={matPPtr.m_PathID}. Searching all loaded files...");
                            // Fallback: search all loaded files for this material by PathID
                            foreach (var sf in meshR.assetsFile.assetsManager.assetsFileList)
                            {
                                if (sf.ObjectsDic.TryGetValue(matPPtr.m_PathID, out var obj) && obj is Material foundMat)
                                {
                                    m_Material = foundMat;
                                    Logger.Info($"Found material '{foundMat.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                    break;
                                }
                            }
                        }
                        mat = m_Material;
                    }
                    else
                    {
                        Logger.Warning($"Material PPtr is null for submesh {i} (m_Materials.Length={meshR.m_Materials.Length}).");
                    }
                }
                else
                {
                    Logger.Warning($"No material slot for submesh {i}: index {i - firstSubMesh} >= m_Materials.Length {meshR.m_Materials.Length}.");
                }
                ImportedMaterial iMat = ConvertMaterial(mat);
                iSubmesh.Material = iMat.Name;
                iSubmesh.BaseVertex = checked((int)(mesh.m_SubMeshes[i].firstVertex + mesh.m_SubMeshes[i].baseVertex));

                //Face
                iSubmesh.FaceList = new List<ImportedFace>(numFaces);
                var end = firstFace + numFaces;
                for (int f = firstFace; f < end; f++)
                {
                    if (f * 3 + 2 >= mesh.m_Indices.Count)
                    {
                        break;
                    }
                    var face = new ImportedFace();
                    face.VertexIndices = new int[3];
                    face.VertexIndices[0] = (int)(mesh.m_Indices[f * 3 + 2] - submesh.firstVertex);
                    face.VertexIndices[1] = (int)(mesh.m_Indices[f * 3 + 1] - submesh.firstVertex);
                    face.VertexIndices[2] = (int)(mesh.m_Indices[f * 3] - submesh.firstVertex);
                    iSubmesh.FaceList.Add(face);
                }
                firstFace = end;

                iMesh.SubmeshList.Add(iSubmesh);
            }

            // Shared vertex list
            iMesh.VertexList = new List<ImportedVertex>((int)mesh.m_VertexCount);
            for (var j = 0; j < mesh.m_VertexCount; j++)
            {
                var iVertex = new ImportedVertex();
                //Vertices
                int c = 3;
                if (mesh.m_Vertices.Length == mesh.m_VertexCount * 4)
                {
                    c = 4;
                }
                iVertex.Vertex = new Vector3(-mesh.m_Vertices[j * c], mesh.m_Vertices[j * c + 1], mesh.m_Vertices[j * c + 2]);
                //Normals
                if (iMesh.hasNormal && mesh.m_Normals != null)
                {
                    if (mesh.m_Normals.Length == mesh.m_VertexCount * 3)
                    {
                        c = 3;
                    }
                    else if (mesh.m_Normals.Length == mesh.m_VertexCount * 4)
                    {
                        c = 4;
                    }
                    iVertex.Normal = new Vector3(-mesh.m_Normals[j * c], mesh.m_Normals[j * c + 1], mesh.m_Normals[j * c + 2]);
                }
                //UV
                iVertex.UV = new float[8][];
                for (int uv = 0; uv < 8; uv++)
                {
                    if (iMesh.hasUV[uv])
                    {
                        var m_UV = mesh.GetUV(uv);
                        if (m_UV.Length == mesh.m_VertexCount * 2)
                        {
                            c = 2;
                        }
                        else if (m_UV.Length == mesh.m_VertexCount * 3)
                        {
                            c = 3;
                        }
                        iVertex.UV[uv] = new[] { m_UV[j * c], m_UV[j * c + 1] };
                    }
                }
                //Tangent
                if (iMesh.hasTangent && mesh.m_Tangents != null)
                {
                    iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], mesh.m_Tangents[j * 4 + 3]);
                }
                //Colors
                if (iMesh.hasColor && mesh.m_Colors != null)
                {
                    if (mesh.m_Colors.Length == mesh.m_VertexCount * 3)
                    {
                        iVertex.Color = new Color(mesh.m_Colors[j * 3], mesh.m_Colors[j * 3 + 1], mesh.m_Colors[j * 3 + 2], 1.0f);
                    }
                    else
                    {
                        iVertex.Color = new Color(mesh.m_Colors[j * 4], mesh.m_Colors[j * 4 + 1], mesh.m_Colors[j * 4 + 2], mesh.m_Colors[j * 4 + 3]);
                    }
                }
                //BoneInfluence
                if (mesh.m_Skin?.Length > 0)
                {
                    var inf = mesh.m_Skin[j];
                    iVertex.BoneIndices = new int[4];
                    iVertex.Weights = new float[4];
                    for (var k = 0; k < 4; k++)
                    {
                        iVertex.BoneIndices[k] = inf.boneIndex[k];
                        iVertex.Weights[k] = inf.weight[k];
                    }
                }
                iMesh.VertexList.Add(iVertex);
            }

            if (meshR is SkinnedMeshRenderer sMesh)
            {
                //Bone
                /*
                 * 0 - None
                 * 1 - m_Bones
                 * 2 - m_BoneNameHashes
                 */
                var boneType = 0;
                if (sMesh.m_Bones.Length > 0)
                {
                    if (sMesh.m_Bones.Length == mesh.m_BindPose.Length)
                    {
                        var verifiedBoneCount = sMesh.m_Bones.Count(x => TryGetBoneTransform(x, meshR, out _));
                        if (verifiedBoneCount > 0)
                        {
                            boneType = 1;
                        }
                        if (verifiedBoneCount != sMesh.m_Bones.Length)
                        {
                            //尝试使用m_BoneNameHashes 4.3 and up
                            if (mesh.m_BindPose.Length > 0 && (mesh.m_BindPose.Length == mesh.m_BoneNameHashes?.Length))
                            {
                                //有效bone数量是否大于SkinnedMeshRenderer
                                var verifiedBoneCount2 = mesh.m_BoneNameHashes.Count(x => FixBonePath(GetPathFromHash(x)) != null);
                                if (verifiedBoneCount2 > verifiedBoneCount)
                                {
                                    boneType = 2;
                                }
                            }
                        }
                    }
                }
                if (boneType == 0)
                {
                    //尝试使用m_BoneNameHashes 4.3 and up
                    if (mesh.m_BindPose.Length > 0 && (mesh.m_BindPose.Length == mesh.m_BoneNameHashes?.Length))
                    {
                        var verifiedBoneCount = mesh.m_BoneNameHashes.Count(x => FixBonePath(GetPathFromHash(x)) != null);
                        if (verifiedBoneCount > 0)
                        {
                            boneType = 2;
                        }
                    }
                }

                if (boneType == 1)
                {
                    var boneCount = sMesh.m_Bones.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        var bone = new ImportedBone();
                        if (TryGetBoneTransform(sMesh.m_Bones[i], meshR, out var m_Transform))
                        {
                            bone.Path = GetTransformPath(m_Transform);
                        }
                        var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                        bone.Matrix = convert * mesh.m_BindPose[i] * convert;
                        iMesh.BoneList.Add(bone);
                    }
                }
                else if (boneType == 2 && mesh.m_BoneNameHashes != null)
                {
                    var boneCount = mesh.m_BindPose.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        var bone = new ImportedBone();
                        var boneHash = mesh.m_BoneNameHashes[i];
                        var path = GetPathFromHash(boneHash);
                        bone.Path = FixBonePath(path);
                        var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                        bone.Matrix = convert * mesh.m_BindPose[i] * convert;
                        iMesh.BoneList.Add(bone);
                    }
                }

                //Morphs
                if (mesh.m_Shapes?.channels?.Length > 0)
                {
                    var morph = new ImportedMorph();
                    MorphList.Add(morph);
                    morph.Path = iMesh.Path;
                    morph.Channels = new List<ImportedMorphChannel>(mesh.m_Shapes.channels.Length);
                    for (int i = 0; i < mesh.m_Shapes.channels.Length; i++)
                    {
                        var channel = new ImportedMorphChannel();
                        morph.Channels.Add(channel);
                        var shapeChannel = mesh.m_Shapes.channels[i];

                        var blendShapeName = "blendShape." + shapeChannel.name;
                        var crc = new SevenZip.CRC();
                        var bytes = Encoding.UTF8.GetBytes(blendShapeName);
                        crc.Update(bytes, 0, (uint)bytes.Length);
                        morphChannelNames[crc.GetDigest()] = blendShapeName;

                        channel.Name = shapeChannel.name.Split('.').Last();
                        channel.KeyframeList = new List<ImportedMorphKeyframe>(shapeChannel.frameCount);
                        var frameEnd = shapeChannel.frameIndex + shapeChannel.frameCount;
                        for (int frameIdx = shapeChannel.frameIndex; frameIdx < frameEnd; frameIdx++)
                        {
                            if (frameIdx < 0 || frameIdx >= mesh.m_Shapes.fullWeights.Length || frameIdx >= mesh.m_Shapes.shapes.Length)
                                break;
                            var keyframe = new ImportedMorphKeyframe();
                            channel.KeyframeList.Add(keyframe);
                            keyframe.Weight = mesh.m_Shapes.fullWeights[frameIdx];
                            var shape = mesh.m_Shapes.shapes[frameIdx];
                            keyframe.hasNormals = shape.hasNormals;
                            keyframe.hasTangents = shape.hasTangents;
                            keyframe.VertexList = new List<ImportedMorphVertex>((int)shape.vertexCount);
                            var vertexEnd = shape.firstVertex + shape.vertexCount;
                            for (uint j = shape.firstVertex; j < vertexEnd; j++)
                            {
                                if (j >= (uint)mesh.m_Shapes.vertices.Length)
                                    break;
                                var morphVertex = mesh.m_Shapes.vertices[j];
                                if (morphVertex.index >= (uint)iMesh.VertexList.Count)
                                    continue;
                                var destVertex = new ImportedMorphVertex();
                                destVertex.Index = morphVertex.index;
                                var sourceVertex = iMesh.VertexList[(int)morphVertex.index];
                                destVertex.Vertex = new ImportedVertex();
                                var morphPos = morphVertex.vertex;
                                destVertex.Vertex.Vertex = sourceVertex.Vertex + new Vector3(-morphPos.X, morphPos.Y, morphPos.Z);
                                if (shape.hasNormals)
                                {
                                    var morphNormal = morphVertex.normal;
                                    destVertex.Vertex.Normal = new Vector3(-morphNormal.X, morphNormal.Y, morphNormal.Z);
                                }
                                if (shape.hasTangents)
                                {
                                    var morphTangent = morphVertex.tangent;
                                    destVertex.Vertex.Tangent = new Vector4(-morphTangent.X, morphTangent.Y, morphTangent.Z, 0);
                                }
                                keyframe.VertexList.Add(destVertex);
                            }
                        }
                    }
                }
            }

            // Reset frame transforms for Unity static-batched meshes (vertices are already baked in world space)
            if (combine)
            {
                var batchGameObject = m_GameObject2;
                if (batchGameObject == null && meshR.m_GameObject != null)
                {
                    meshR.m_GameObject.TryGet(out batchGameObject);
                }
                var frame = batchGameObject != null ? RootFrame.FindChild(batchGameObject.m_Name) : null;
                if (frame != null)
                {
                    frame.LocalPosition = RootFrame.LocalPosition;
                    frame.LocalRotation = RootFrame.LocalRotation;
                    var visitedBatchFrames = new HashSet<ImportedFrame> { frame };
                    while (frame.Parent != null && visitedBatchFrames.Add(frame.Parent))
                    {
                        frame = frame.Parent;
                        frame.LocalPosition = RootFrame.LocalPosition;
                        frame.LocalRotation = RootFrame.LocalRotation;
                    }
                }
            }

            MeshList.Add(iMesh);
        }

        private static bool TryGetBoneTransform(PPtr<Transform> bonePtr, Renderer meshR, out Transform transform)
        {
            if (bonePtr.TryGet(out transform))
                return true;
            if (bonePtr.IsNull)
                return false;
            foreach (var sf in meshR.assetsFile.assetsManager.assetsFileList)
            {
                if (sf.ObjectsDic.TryGetValue(bonePtr.m_PathID, out var obj) && obj is Transform foundTransform)
                {
                    transform = foundTransform;
                    return true;
                }
            }
            return false;
        }

        private static Mesh? GetMesh(Renderer meshR)
        {
            if (meshR is SkinnedMeshRenderer sMesh)
            {
                if (!sMesh.m_Mesh.TryGet(out var m_Mesh))
                {
                    if (sMesh.m_Mesh != null && !sMesh.m_Mesh.IsNull)
                    {
                        foreach (var sf in meshR.assetsFile.assetsManager.assetsFileList)
                        {
                            if (sf.ObjectsDic.TryGetValue(sMesh.m_Mesh.m_PathID, out var obj) && obj is Mesh foundMesh)
                            {
                                m_Mesh = foundMesh;
                                Logger.Info($"Found mesh '{foundMesh.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                break;
                            }
                        }
                    }
                }
                return m_Mesh;
            }
            else
            {
                if (!meshR.m_GameObject.TryGet(out var m_GameObject))
                {
                    foreach (var sf in meshR.assetsFile.assetsManager.assetsFileList)
                    {
                        if (sf.ObjectsDic.TryGetValue(meshR.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                        {
                            m_GameObject = foundGo;
                            break;
                        }
                    }
                }
                if (m_GameObject?.m_MeshFilter != null)
                {
                    if (!m_GameObject.m_MeshFilter.m_Mesh.TryGet(out var m_Mesh))
                    {
                        if (m_GameObject.m_MeshFilter.m_Mesh != null && !m_GameObject.m_MeshFilter.m_Mesh.IsNull)
                        {
                            foreach (var sf in meshR.assetsFile.assetsManager.assetsFileList)
                            {
                                if (sf.ObjectsDic.TryGetValue(m_GameObject.m_MeshFilter.m_Mesh.m_PathID, out var obj) && obj is Mesh foundMesh)
                                {
                                    m_Mesh = foundMesh;
                                    Logger.Info($"Found mesh '{foundMesh.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                    break;
                                }
                            }
                        }
                    }
                    return m_Mesh;
                }
            }

            return null;
        }

        private string? GetTransformPath(Transform transform)
        {
            if (transformDictionary.TryGetValue(transform, out var frame))
            {
                return frame.Path;
            }
            return null;
        }

        private string? FixBonePath(AnimationClip m_AnimationClip, string? path)
        {
            if (boundAnimationPathDic.TryGetValue(m_AnimationClip, out var basePath))
            {
                path = string.IsNullOrEmpty(path) ? basePath : basePath + "/" + path;
            }
            return FixBonePath(path);
        }

        private string? FixBonePath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return RootFrame.Path;

            var frame = RootFrame.FindFrameByPath(path);
            return frame?.Path ?? path;
        }

        private static string GetTransformPathByFather(Transform transform)
        {
            return GetTransformPathByFather(transform, new HashSet<Transform>());
        }

        private static string GetTransformPathByFather(Transform transform, HashSet<Transform> visited)
        {
            if (transform == null || !visited.Add(transform))
            {
                return "";
            }
            transform.m_GameObject.TryGet(out var m_GameObject);
            var goName = m_GameObject?.m_Name ?? "";
            if (transform.m_Father.TryGet(out var father) && !visited.Contains(father))
            {
                var fatherPath = GetTransformPathByFather(father, visited);
                return string.IsNullOrEmpty(fatherPath) ? goName : fatherPath + "/" + goName;
            }

            return goName;
        }

        private ImportedMaterial ConvertMaterial(Material? mat)
        {
            ImportedMaterial iMat;
            if (mat != null && !string.IsNullOrEmpty(mat.m_Name))
            {
                iMat = ImportedHelpers.FindMaterial(mat.m_Name, MaterialList);
                if (iMat != null)
                {
                    return iMat;
                }
                iMat = new ImportedMaterial();
                iMat.Name = mat.m_Name;
                //default values
                iMat.Diffuse = new Color(0.8f, 0.8f, 0.8f, 1);
                iMat.Ambient = new Color(0.2f, 0.2f, 0.2f, 1);
                iMat.Emissive = new Color(0, 0, 0, 1);
                iMat.Specular = new Color(0.2f, 0.2f, 0.2f, 1);
                iMat.Reflection = new Color(0, 0, 0, 1);
                iMat.Shininess = 20f;
                iMat.Transparency = 0f;
                foreach (var col in mat.m_SavedProperties?.m_Colors ?? Array.Empty<KeyValuePair<string, Color>>())
                {
                    switch (col.Key)
                    {
                        case "_Color":
                        case "_BaseColor":
                            iMat.Diffuse = col.Value;
                            break;
                        case "_SColor":
                            iMat.Ambient = col.Value;
                            break;
                        case "_EmissionColor":
                            iMat.Emissive = col.Value;
                            break;
                        case "_SpecularColor":
                        case "_SpecColor":
                            iMat.Specular = col.Value;
                            break;
                        case "_ReflectColor":
                            iMat.Reflection = col.Value;
                            break;
                    }
                }

                foreach (var flt in mat.m_SavedProperties?.m_Floats ?? Array.Empty<KeyValuePair<string, float>>())
                {
                    switch (flt.Key)
                    {
                        case "_Shininess":
                            iMat.Shininess = flt.Value;
                            break;
                        case "_Transparency":
                            iMat.Transparency = flt.Value;
                            break;
                    }
                }

                //textures
                iMat.Textures = new List<ImportedMaterialTexture>();
                foreach (var texEnv in mat.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                {
                    if (texEnv.Value == null)
                    {
                        continue;
                    }
                    var textureRef = texEnv.Value.m_Texture;
                    Texture2D? m_Texture2D = null;
                    if (textureRef != null && !textureRef.IsNull && !textureRef.TryGet<Texture2D>(out m_Texture2D)) //TODO other Texture
                    {
                        foreach (var sf in mat.assetsFile.assetsManager.assetsFileList)
                        {
                            if (sf.ObjectsDic.TryGetValue(textureRef.m_PathID, out var obj) && obj is Texture2D foundTex)
                            {
                                m_Texture2D = foundTex;
                                Logger.Info($"Found texture '{foundTex.m_Name}' by PathID fallback in file '{sf.fileName}'.");
                                break;
                            }
                        }
                    }

                    if (m_Texture2D == null)
                    {
                        if (textureRef != null && !textureRef.IsNull)
                        {
                            Logger.Warning($"Unable to resolve texture reference for material {mat.m_Name}, property {texEnv.Key}, PathID {textureRef.m_PathID}.");
                        }
                        continue;
                    }

                    var texture = new ImportedMaterialTexture();
                    iMat.Textures.Add(texture);

                    texture.Dest = GetTextureDestination(texEnv.Key);

                    var ext = $".{imageFormat.ToString().ToLower()}";
                    if (textureNameDictionary.TryGetValue(m_Texture2D, out var textureName))
                    {
                        texture.Name = textureName;
                    }
                    else if (ImportedHelpers.FindTexture(m_Texture2D.m_Name + ext, TextureList) != null) //已有相同名字的图片
                    {
                        for (int i = 1; ; i++)
                        {
                            var name = m_Texture2D.m_Name + $" ({i}){ext}";
                            if (ImportedHelpers.FindTexture(name, TextureList) == null)
                            {
                                texture.Name = name;
                                textureNameDictionary.Add(m_Texture2D, name);
                                break;
                            }
                        }
                    }
                    else
                    {
                        texture.Name = m_Texture2D.m_Name + ext;
                        textureNameDictionary.Add(m_Texture2D, texture.Name);
                    }

                    texture.Offset = texEnv.Value.m_Offset;
                    texture.Scale = texEnv.Value.m_Scale;
                    ConvertTexture2D(m_Texture2D, texture.Name, mat.m_Name, texEnv.Key);
                }

                MaterialList.Add(iMat);
            }
            else
            {
                iMat = ImportedHelpers.FindMaterial("Material", MaterialList);
                if (iMat == null)
                {
                    iMat = new ImportedMaterial();
                    iMat.Name = "Material";
                    iMat.Diffuse = new Color(0.8f, 0.8f, 0.8f, 1);
                    iMat.Ambient = new Color(0.2f, 0.2f, 0.2f, 1);
                    iMat.Emissive = new Color(0, 0, 0, 1);
                    iMat.Specular = new Color(0.2f, 0.2f, 0.2f, 1);
                    iMat.Reflection = new Color(0, 0, 0, 1);
                    iMat.Shininess = 20f;
                    iMat.Transparency = 0f;
                    iMat.Textures = new List<ImportedMaterialTexture>();
                    MaterialList.Add(iMat);
                }
            }
            return iMat;
        }

        private void ConvertTexture2D(Texture2D m_Texture2D, string name, string materialName, string propertyName)
        {
            var iTex = ImportedHelpers.FindTexture(name, TextureList);
            if (iTex != null)
            {
                return;
            }

            var stream = m_Texture2D.ConvertToStream(imageFormat, true);
            if (stream == null)
            {
                Logger.Warning($"Unable to convert texture {m_Texture2D.m_Name} for material {materialName}, property {propertyName}.");
                return;
            }

            using (stream)
            {
                iTex = new ImportedTexture(stream, name);
                TextureList.Add(iTex);
            }
        }

        private static int GetTextureDestination(string propertyName)
        {
            switch (propertyName)
            {
                case "_BaseMap":
                case "_MainTex":
                case "_BaseColorMap":
                case "_BaseColorTexture":
                    return 0;
                case "_BumpMap":
                case "_NormalMap":
                case "_DetailNormalMap":
                    return 1;
                case "_SpecGlossMap":
                case "_SpecularMap":
                    return 2;
                case "_MetallicGlossMap":
                case "_MetallicMap":
                    return 3;
                case "_EmissionMap":
                    return 4;
                case "_OcclusionMap":
                    return 5;
                case "_ParallaxMap":
                case "_HeightMap":
                    return 6;
                case "_DetailAlbedoMap":
                    return 7;
            }

            if (propertyName.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            if (propertyName.IndexOf("Spec", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }
            if (propertyName.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 3;
            }
            if (propertyName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 4;
            }
            if (propertyName.IndexOf("Occlusion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 5;
            }
            if (propertyName.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 6;
            }

            return 0;
        }

        private void ConvertAnimations()
        {
            foreach (var animationClip in animationClipHashSet)
            {
                var iAnim = new ImportedKeyframedAnimation();
                var name = animationClip.m_Name;
                if (AnimationList.Exists(x => x.Name == name))
                {
                    for (int i = 1; ; i++)
                    {
                        var fixName = name + $"_{i}";
                        if (!AnimationList.Exists(x => x.Name == fixName))
                        {
                            name = fixName;
                            break;
                        }
                    }
                }
                iAnim.Name = name;
                iAnim.SampleRate = animationClip.m_SampleRate;
                iAnim.TrackList = new List<ImportedAnimationKeyframedTrack>();
                AnimationList.Add(iAnim);
                if (animationClip.m_Legacy)
                {
                    foreach (var m_CompressedRotationCurve in animationClip.m_CompressedRotationCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_CompressedRotationCurve.m_Path));

                        var numKeys = m_CompressedRotationCurve.m_Times.m_NumItems;
                        var data = m_CompressedRotationCurve.m_Times.UnpackInts();
                        var times = new float[numKeys];
                        int t = 0;
                        for (int i = 0; i < numKeys; i++)
                        {
                            t += data[i];
                            times[i] = t * 0.01f;
                        }
                        var quats = m_CompressedRotationCurve.m_Values.UnpackQuats();

                        for (int i = 0; i < numKeys; i++)
                        {
                            var quat = quats[i];
                            var value = QuaternionToEuler(new Quaternion(quat.X, -quat.Y, -quat.Z, quat.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(times[i], value));
                        }
                    }
                    foreach (var m_RotationCurve in animationClip.m_RotationCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_RotationCurve.path));
                        foreach (var m_Curve in m_RotationCurve.curve.m_Curve)
                        {
                            var value = QuaternionToEuler(new Quaternion(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z, m_Curve.value.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, value));
                        }
                    }
                    foreach (var m_PositionCurve in animationClip.m_PositionCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_PositionCurve.path));
                        foreach (var m_Curve in m_PositionCurve.curve.m_Curve)
                        {
                            track.Translations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(-m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z)));
                        }
                    }
                    foreach (var m_ScaleCurve in animationClip.m_ScaleCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_ScaleCurve.path));
                        foreach (var m_Curve in m_ScaleCurve.curve.m_Curve)
                        {
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z)));
                        }
                    }
                    if (animationClip.m_EulerCurves != null)
                    {
                        foreach (var m_EulerCurve in animationClip.m_EulerCurves)
                        {
                            var track = iAnim.FindTrack(FixBonePath(animationClip, m_EulerCurve.path));
                            foreach (var m_Curve in m_EulerCurve.curve.m_Curve)
                            {
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z)));
                            }
                        }
                    }
                    foreach (var m_FloatCurve in animationClip.m_FloatCurves)
                    {
                        if (m_FloatCurve.classID == ClassIDType.SkinnedMeshRenderer) //BlendShape
                        {
                            var channelName = m_FloatCurve.attribute;
                            int dotPos = channelName.IndexOf('.');
                            if (dotPos >= 0)
                            {
                                channelName = channelName.Substring(dotPos + 1);
                            }

                            var path = FixBonePath(animationClip, m_FloatCurve.path);
                            if (string.IsNullOrEmpty(path))
                            {
                                path = GetPathByChannelName(channelName);
                            }
                            var track = iAnim.FindTrack(path);
                            track.BlendShape = new ImportedBlendShape();
                            track.BlendShape.ChannelName = channelName;
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                track.BlendShape.Keyframes.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value));
                            }
                        }
                    }
                }
                else if (animationClip.m_MuscleClip?.m_Clip != null)
                {
                    var m_Clip = animationClip.m_MuscleClip.m_Clip;
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    var m_ClipBindingConstant = animationClip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();
                    float[]? streamedValues = null;
                    for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        if (streamedValues == null || streamedValues.Length < frame.keyList.Length)
                            streamedValues = new float[frame.keyList.Length];

                        for (int i = 0; i < frame.keyList.Length; i++)
                            streamedValues[i] = frame.keyList[i].value;

                        for (int curveIndex = 0; curveIndex < frame.keyList.Length;)
                        {
                            ReadCurveData(iAnim, m_ClipBindingConstant, frame.keyList[curveIndex].index, frame.time, streamedValues, 0, ref curveIndex);
                        }
                    }
                    var m_DenseClip = m_Clip.m_DenseClip;
                    var streamCount = m_Clip.m_StreamedClip.curveCount;
                    for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                    {
                        var time = m_DenseClip.m_BeginTime + frameIndex / m_DenseClip.m_SampleRate;
                        var frameOffset = frameIndex * m_DenseClip.m_CurveCount;
                        for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                        {
                            var index = streamCount + curveIndex;
                            ReadCurveData(iAnim, m_ClipBindingConstant, (int)index, time, m_DenseClip.m_SampleArray, (int)frameOffset, ref curveIndex);
                        }
                    }
                    if (m_Clip.m_ConstantClip != null)
                    {
                        var m_ConstantClip = m_Clip.m_ConstantClip;
                        var denseCount = m_Clip.m_DenseClip.m_CurveCount;
                        var time2 = 0.0f;
                        for (int i = 0; i < 2; i++)
                        {
                            for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                            {
                                var index = streamCount + denseCount + curveIndex;
                                ReadCurveData(iAnim, m_ClipBindingConstant, (int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                            }
                            time2 = animationClip.m_MuscleClip.m_StopTime;
                        }
                    }
                    if (animationClip.m_MuscleClip?.m_Clip != null && avatar != null)
                    {
                        ReadHumanoidMuscleCurves(iAnim, animationClip);
                    }
                }
            }
        }

        private void ReadCurveData(ImportedKeyframedAnimation iAnim, AnimationClipBindingConstant m_ClipBindingConstant, int index, float time, float[] data, int offset, ref int curveIndex)
        {
            var binding = m_ClipBindingConstant.FindBinding(index);
            if (binding == null)
            {
                curveIndex++;
                return;
            }
            if (binding.typeID == ClassIDType.SkinnedMeshRenderer) //BlendShape
            {
                var channelName = GetChannelNameFromHash(binding.attribute);
                if (string.IsNullOrEmpty(channelName))
                {
                    curveIndex++;
                    return;
                }
                int dotPos = channelName.IndexOf('.');
                if (dotPos >= 0)
                {
                    channelName = channelName.Substring(dotPos + 1);
                }

                var bPath = FixBonePath(GetPathFromHash(binding.path));
                if (string.IsNullOrEmpty(bPath))
                {
                    bPath = GetPathByChannelName(channelName);
                }
                var bTrack = iAnim.FindTrack(bPath);
                bTrack.BlendShape = new ImportedBlendShape();
                bTrack.BlendShape.ChannelName = channelName;
                bTrack.BlendShape.Keyframes.Add(new ImportedKeyframe<float>(time, data[curveIndex++ + offset]));
            }
            else if (binding.typeID == ClassIDType.Transform)
            {
                var path = FixBonePath(GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);

                switch (binding.attribute)
                {
                    case 1:
                        track.Translations.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                        (
                            -data[curveIndex++ + offset],
                            data[curveIndex++ + offset],
                            data[curveIndex++ + offset]
                        )));
                        break;
                    case 2:
                        var value = QuaternionToEuler(new Quaternion
                        (
                            data[curveIndex++ + offset],
                            -data[curveIndex++ + offset],
                            -data[curveIndex++ + offset],
                            data[curveIndex++ + offset]
                        ));
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(time, value));
                        break;
                    case 3:
                        track.Scalings.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                        (
                            data[curveIndex++ + offset],
                            data[curveIndex++ + offset],
                            data[curveIndex++ + offset]
                        )));
                        break;
                    case 4:
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                        (
                            data[curveIndex++ + offset],
                            -data[curveIndex++ + offset],
                            -data[curveIndex++ + offset]
                        )));
                        break;
                    default:
                        curveIndex++;
                        break;
                }
            }
            else
            {
                curveIndex++;
            }
        }

        private string GetPathFromHash(uint hash)
        {
            if (bonePathHash.TryGetValue(hash, out var boneName))
            {
                return boneName;
            }
            if (avatar != null)
            {
                boneName = avatar.FindBonePath(hash);
                if (boneName != null)
                {
                    return boneName;
                }
            }
            return $"Bone_{hash}";
        }

        private void CreateBonePathHash(Transform m_Transform)
        {
            CreateBonePathHash(m_Transform, new HashSet<Transform>());
        }

        private void CreateBonePathHash(Transform m_Transform, HashSet<Transform> visited)
        {
            if (m_Transform == null || !visited.Add(m_Transform))
            {
                return;
            }
            var name = GetTransformPathByFather(m_Transform);
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(name);
            crc.Update(bytes, 0, (uint)bytes.Length);
            bonePathHash[crc.GetDigest()] = name;
            int index;
            while ((index = name.IndexOf("/", StringComparison.Ordinal)) >= 0)
            {
                name = name.Substring(index + 1);
                crc = new SevenZip.CRC();
                bytes = Encoding.UTF8.GetBytes(name);
                crc.Update(bytes, 0, (uint)bytes.Length);
                bonePathHash[crc.GetDigest()] = name;
            }
            foreach (var pptr in m_Transform.m_Children)
            {
                if (pptr.TryGet(out var child))
                    CreateBonePathHash(child, visited);
            }
        }

        private void DeoptimizeTransformHierarchy()
        {
            if (avatar == null)
                throw new Exception("Transform hierarchy has been optimized, but can't find Avatar to deoptimize.");

            var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
            var skeletonPose = avatar.m_Avatar.m_DefaultPose;
            var nodes = skeleton.m_Node;

            if (nodes == null || skeletonPose == null || skeletonPose.m_X == null)
                return;

            Mesh? avatarMesh = null;
            int bestScore = 0;
            var avatarName = avatar.m_Name.Replace("Avatar", "").Trim();
            var allMeshes = new List<Mesh>();
            if (avatar.assetsFile?.assetsManager != null)
            {
                foreach (var f in avatar.assetsFile.assetsManager.assetsFileList)
                {
                    foreach (var obj in f.Objects)
                    {
                        if (obj is Mesh m && m.m_VertexCount > 0)
                        {
                            allMeshes.Add(m);
                        }
                    }
                }
            }

            foreach (var mesh in allMeshes)
            {
                mesh.EnsureProcessed();
                int score = 0;
                if (mesh.assetsFile == avatar.assetsFile) score += 20;

                if (mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length > 0
                    && mesh.m_BindPose != null && mesh.m_BindPose.Length > 0)
                {
                    score += mesh.m_BoneNameHashes.Length;
                }

                if (!string.IsNullOrEmpty(avatarName) && mesh.m_Name.IndexOf(avatarName, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 15;

                if (score > bestScore)
                {
                    bestScore = score;
                    avatarMesh = mesh;
                }
            }

            var skelCount = nodes.Length;
            var skelIds = skeleton.m_ID;
            
            var skelNodeToMeshBone = new int[skelCount];
            for (int i = 0; i < skelCount; i++) skelNodeToMeshBone[i] = -1;
            
            var meshBoneToSkelNode = new int[0];
            var meshParentIndices = new int[0];
            System.Numerics.Matrix4x4[]? bindPoseInverses = null;
            System.Numerics.Matrix4x4[]? bindPoses = null;
            bool hasBindPose = false;

            System.Numerics.Matrix4x4 ToSystem(Matrix4x4 m)
            {
                return new System.Numerics.Matrix4x4(
                    m.M00, m.M01, m.M02, m.M03,
                    m.M10, m.M11, m.M12, m.M13,
                    m.M20, m.M21, m.M22, m.M23,
                    m.M30, m.M31, m.M32, m.M33
                );
            }

            Vector3 ToAssetStudioVec(System.Numerics.Vector3 v)
            {
                return new Vector3(v.X, v.Y, v.Z);
            }

            Quaternion ToAssetStudioQuat(System.Numerics.Quaternion q)
            {
                return new Quaternion(q.X, q.Y, q.Z, q.W);
            }

            avatarMesh?.EnsureProcessed();
            if (avatarMesh != null && avatarMesh.m_BoneNameHashes != null && avatarMesh.m_BindPose != null)
            {
                var meshBoneCount = avatarMesh.m_BoneNameHashes.Length;
                var meshBoneHashToIdx = new Dictionary<uint, int>();
                for (int j = 0; j < meshBoneCount; j++)
                {
                    meshBoneHashToIdx[avatarMesh.m_BoneNameHashes[j]] = j;
                }

                for (int i = 0; i < skelCount; i++)
                {
                    if (skelIds != null && i < skelIds.Length)
                    {
                        if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                        {
                            skelNodeToMeshBone[i] = mbIdx;
                        }
                    }
                }

                meshBoneToSkelNode = new int[meshBoneCount];
                for (int j = 0; j < meshBoneCount; j++) meshBoneToSkelNode[j] = -1;
                for (int i = 0; i < skelCount; i++)
                {
                    if (skelNodeToMeshBone[i] >= 0)
                    {
                        meshBoneToSkelNode[skelNodeToMeshBone[i]] = i;
                    }
                }

                meshParentIndices = new int[meshBoneCount];
                for (int mb = 0; mb < meshBoneCount; mb++)
                {
                    meshParentIndices[mb] = -1;
                    int skelIdx = meshBoneToSkelNode[mb];
                    if (skelIdx < 0) continue;

                    int current = nodes[skelIdx].m_ParentId;
                    while (current >= 0 && current < skelCount)
                    {
                        if (skelNodeToMeshBone[current] >= 0)
                        {
                            meshParentIndices[mb] = skelNodeToMeshBone[current];
                            break;
                        }
                        current = nodes[current].m_ParentId;
                    }
                }

                bindPoseInverses = new System.Numerics.Matrix4x4[meshBoneCount];
                bindPoses = new System.Numerics.Matrix4x4[meshBoneCount];
                hasBindPose = true;
                for (int j = 0; j < meshBoneCount; j++)
                {
                    var bp = ToSystem(avatarMesh.m_BindPose[j]);
                    bindPoseInverses[j] = bp;
                    if (System.Numerics.Matrix4x4.Invert(bp, out var inv))
                    {
                        bindPoses[j] = inv;
                    }
                    else
                    {
                        bindPoses[j] = System.Numerics.Matrix4x4.Identity;
                    }
                }
            }

            var frames = new ImportedFrame[nodes.Length];

            // 1. Create all frames
            for (int i = 0; i < nodes.Length; i++)
            {
                var xform = skeletonPose.m_X[i];
                var name = avatar.FindBonePath(skeleton.m_ID[i]);
                if (string.IsNullOrEmpty(name))
                {
                    name = $"Bone_{skeleton.m_ID[i]}";
                }
                else
                {
                    var lastSlash = name.LastIndexOf('/');
                    if (lastSlash >= 0)
                        name = name.Substring(lastSlash + 1);
                }

                Vector3 tVec;
                Quaternion qQuat;
                Vector3 sVec;

                int mb = skelNodeToMeshBone[i];
                if (hasBindPose && mb >= 0 && meshParentIndices != null && bindPoses != null && bindPoseInverses != null)
                {
                    int pMb = meshParentIndices[mb];
                    System.Numerics.Matrix4x4 localMat = pMb >= 0 ? bindPoses[mb] * bindPoseInverses[pMb] : bindPoses[mb];
                    if (System.Numerics.Matrix4x4.Decompose(localMat, out var s, out var q, out var t))
                    {
                        tVec = ToAssetStudioVec(t);
                        qQuat = ToAssetStudioQuat(q);
                        sVec = ToAssetStudioVec(s);
                    }
                    else
                    {
                        tVec = xform.t;
                        qQuat = xform.q;
                        sVec = xform.s;
                    }
                }
                else
                {
                    tVec = xform.t;
                    qQuat = xform.q;
                    sVec = xform.s;
                }

                var frame = RootFrame.FindChild(name);
                if (frame != null)
                {
                    SetFrame(frame, tVec, qQuat, sVec);
                }
                else
                {
                    frame = CreateFrame(name, tVec, qQuat, sVec);
                }
                frames[i] = frame;
            }

            // 2. Build hierarchy using m_ParentId
            for (int i = 0; i < nodes.Length; i++)
            {
                var parentId = nodes[i].m_ParentId;
                ImportedFrame parentFrame = (parentId >= 0 && parentId < frames.Length)
                    ? frames[parentId]
                    : RootFrame;

                if (frames[i].Parent == null)
                {
                    parentFrame.AddChild(frames[i]);
                }
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                bonePathHash[skeleton.m_ID[i]] = frames[i].Path;
            }
        }

        private string? GetPathByChannelName(string channelName)
        {
            foreach (var morph in MorphList)
            {
                foreach (var channel in morph.Channels)
                {
                    if (channel.Name == channelName)
                    {
                        return morph.Path;
                    }
                }
            }
            return null;
        }

        private string? GetChannelNameFromHash(uint attribute)
        {
            if (morphChannelNames.TryGetValue(attribute, out var name))
            {
                return name;
            }
            else
            {
                return null;
            }
        }

        private Avatar? FindAvatar(GameObject? go)
        {
            if (go == null) return null;
            if (go.m_Animator != null && go.m_Animator.m_Avatar != null && go.m_Animator.m_Avatar.TryGet(out var av1))
                return av1;

            var trans = go.m_Transform;
            if (trans != null)
            {
                var queue = new Queue<Transform>();
                queue.Enqueue(trans);
                while (queue.Count > 0)
                {
                    var curr = queue.Dequeue();
                    if (curr.m_GameObject.TryGet(out var childGo) && childGo.m_Animator != null)
                    {
                        if (childGo.m_Animator.m_Avatar != null && childGo.m_Animator.m_Avatar.TryGet(out var av2))
                            return av2;
                    }
                    foreach (var childPtr in curr.m_Children)
                    {
                        if (childPtr.TryGet(out var childTrans))
                            queue.Enqueue(childTrans);
                    }
                }
            }

            if (go.assetsFile?.assetsManager != null)
            {
                var cleanName = go.m_Name.Replace("_Model", "").Replace("Model", "").Trim();
                Avatar? fallback = null;
                foreach (var sf in go.assetsFile.assetsManager.assetsFileList)
                {
                    foreach (var obj in sf.Objects)
                    {
                        if (obj is Avatar a)
                        {
                            if (!string.IsNullOrEmpty(cleanName) && a.m_Name.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                                return a;
                            if (fallback == null)
                                fallback = a;
                        }
                    }
                }
                return fallback;
            }

            return null;
        }

        private static Quaternion MultiplyQuat(Quaternion p, Quaternion q)
        {
            return new Quaternion(
                p.W * q.X + p.X * q.W + p.Y * q.Z - p.Z * q.Y,
                p.W * q.Y - p.X * q.Z + p.Y * q.W + p.Z * q.X,
                p.W * q.Z + p.X * q.Y - p.Y * q.X + p.Z * q.W,
                p.W * q.W - p.X * q.X - p.Y * q.Y - p.Z * q.Z
            );
        }

        private static Quaternion ConjugateQuat(Quaternion q)
        {
            return new Quaternion(-q.X, -q.Y, -q.Z, q.W);
        }

        private static Vector3 ToVec3(object? obj)
        {
            if (obj is Vector3 v3) return v3;
            if (obj is Vector4 v4) return new Vector3(v4.X, v4.Y, v4.Z);
            return Vector3.Zero;
        }

        private class HumanBoneMuscleBinding
        {
            public int HumanBoneId;
            public int MuscleStartIndex;
            public int MuscleCount;
            public int[] DofAxes = Array.Empty<int>();
        }

        private static readonly HumanBoneMuscleBinding[] HumanBoneBindings = new[]
        {
            // Spine, Chest, UpperChest
            new HumanBoneMuscleBinding { HumanBoneId = 7, MuscleStartIndex = 0, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 8, MuscleStartIndex = 3, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 9, MuscleStartIndex = 6, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 54, MuscleStartIndex = 6, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },

            // Neck, Head
            new HumanBoneMuscleBinding { HumanBoneId = 10, MuscleStartIndex = 9, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 11, MuscleStartIndex = 12, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },

            // Left Leg
            new HumanBoneMuscleBinding { HumanBoneId = 1, MuscleStartIndex = 21, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 3, MuscleStartIndex = 24, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 5, MuscleStartIndex = 26, MuscleCount = 2, DofAxes = new[] { 1, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 19, MuscleStartIndex = 28, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 20, MuscleStartIndex = 28, MuscleCount = 1, DofAxes = new[] { 0 } },

            // Right Leg
            new HumanBoneMuscleBinding { HumanBoneId = 2, MuscleStartIndex = 29, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 4, MuscleStartIndex = 32, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 6, MuscleStartIndex = 34, MuscleCount = 2, DofAxes = new[] { 1, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 21, MuscleStartIndex = 36, MuscleCount = 1, DofAxes = new[] { 0 } },

            // Left Arm
            new HumanBoneMuscleBinding { HumanBoneId = 12, MuscleStartIndex = 37, MuscleCount = 2, DofAxes = new[] { 1, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 14, MuscleStartIndex = 39, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 16, MuscleStartIndex = 42, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 18, MuscleStartIndex = 44, MuscleCount = 2, DofAxes = new[] { 1, 2 } },

            // Right Arm
            new HumanBoneMuscleBinding { HumanBoneId = 13, MuscleStartIndex = 46, MuscleCount = 2, DofAxes = new[] { 1, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 15, MuscleStartIndex = 48, MuscleCount = 3, DofAxes = new[] { 0, 2, 1 } },
            new HumanBoneMuscleBinding { HumanBoneId = 17, MuscleStartIndex = 51, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 19, MuscleStartIndex = 53, MuscleCount = 2, DofAxes = new[] { 1, 2 } },

            // Left Fingers
            new HumanBoneMuscleBinding { HumanBoneId = 24, MuscleStartIndex = 55, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 25, MuscleStartIndex = 57, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 26, MuscleStartIndex = 58, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 27, MuscleStartIndex = 59, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 28, MuscleStartIndex = 61, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 29, MuscleStartIndex = 62, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 30, MuscleStartIndex = 63, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 31, MuscleStartIndex = 65, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 32, MuscleStartIndex = 66, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 33, MuscleStartIndex = 67, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 34, MuscleStartIndex = 69, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 35, MuscleStartIndex = 70, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 36, MuscleStartIndex = 71, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 37, MuscleStartIndex = 73, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 38, MuscleStartIndex = 74, MuscleCount = 1, DofAxes = new[] { 0 } },

            // Right Fingers
            new HumanBoneMuscleBinding { HumanBoneId = 39, MuscleStartIndex = 75, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 40, MuscleStartIndex = 77, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 41, MuscleStartIndex = 78, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 42, MuscleStartIndex = 79, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 43, MuscleStartIndex = 81, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 44, MuscleStartIndex = 82, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 45, MuscleStartIndex = 83, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 46, MuscleStartIndex = 85, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 47, MuscleStartIndex = 86, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 48, MuscleStartIndex = 87, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 49, MuscleStartIndex = 89, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 50, MuscleStartIndex = 90, MuscleCount = 1, DofAxes = new[] { 0 } },

            new HumanBoneMuscleBinding { HumanBoneId = 51, MuscleStartIndex = 91, MuscleCount = 2, DofAxes = new[] { 0, 2 } },
            new HumanBoneMuscleBinding { HumanBoneId = 52, MuscleStartIndex = 93, MuscleCount = 1, DofAxes = new[] { 0 } },
            new HumanBoneMuscleBinding { HumanBoneId = 53, MuscleStartIndex = 94, MuscleCount = 1, DofAxes = new[] { 0 } }
        };

        private void ReadHumanoidMuscleCurves(ImportedKeyframedAnimation iAnim, AnimationClip animationClip)
        {
            if (avatar?.m_Avatar?.m_AvatarSkeleton == null || avatar.m_Avatar.m_HumanSkeletonIndexArray == null)
                return;

            var muscleClip = animationClip.m_MuscleClip;
            var clip = muscleClip?.m_Clip;
            if (muscleClip == null || clip == null)
                return;

            var streamedClip = clip.m_StreamedClip;
            var denseClip = clip.m_DenseClip;
            var constantClip = clip.m_ConstantClip;
            var indexArray = muscleClip.m_IndexArray;
            if (indexArray == null || indexArray.Length == 0)
                return;

            uint streamCount = streamedClip?.curveCount ?? 0;
            uint denseCount = denseClip?.m_CurveCount ?? 0;

            List<StreamedClip.StreamedFrame>? streamedFrames = null;
            if (streamedClip != null && streamCount > 0)
            {
                try
                {
                    streamedFrames = streamedClip.ReadData();
                }
                catch
                {
                    streamedFrames = null;
                }
            }

            int frameCount = 0;
            float sampleRate = animationClip.m_SampleRate > 0 ? animationClip.m_SampleRate : 30f;
            float beginTime = 0f;

            if (denseClip != null && denseClip.m_FrameCount > 0)
            {
                frameCount = denseClip.m_FrameCount;
                if (denseClip.m_SampleRate > 0)
                    sampleRate = denseClip.m_SampleRate;
                beginTime = denseClip.m_BeginTime;
            }
            else if (streamedFrames != null && streamedFrames.Count > 1)
            {
                frameCount = streamedFrames.Count - 1;
            }
            else
            {
                frameCount = 2;
            }

            if (frameCount <= 0)
                return;

            float GetTime(int f)
            {
                if (denseClip != null && denseClip.m_FrameCount > 0)
                {
                    return beginTime + (float)f / sampleRate;
                }
                if (streamedFrames != null && streamedFrames.Count > 1)
                {
                    int idx = f + 1;
                    if (idx < streamedFrames.Count)
                        return Math.Max(0f, streamedFrames[idx].time);
                }
                return f == 0 ? 0f : muscleClip.m_StopTime;
            }

            float GetChannelValue(int chIdx, int frameIdx, float tVal)
            {
                if (chIdx < 0 || chIdx >= indexArray.Length)
                    return 0f;
                int curveIdx = indexArray[chIdx];
                if (curveIdx < 0)
                    return 0f;

                if (curveIdx < streamCount)
                {
                    if (streamedFrames != null && streamedFrames.Count > 0)
                    {
                        float bestVal = 0f;
                        float bestDist = float.MaxValue;
                        for (int i = 0; i < streamedFrames.Count; i++)
                        {
                            var sf = streamedFrames[i];
                            if (sf.keyList != null)
                            {
                                for (int k = 0; k < sf.keyList.Length; k++)
                                {
                                    if (sf.keyList[k].index == curveIdx)
                                    {
                                        float d = Math.Abs(sf.time - tVal);
                                        if (d < bestDist)
                                        {
                                            bestDist = d;
                                            bestVal = sf.keyList[k].value;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        if (bestDist < float.MaxValue)
                            return bestVal;
                    }
                    return 0f;
                }
                else if (curveIdx < streamCount + denseCount)
                {
                    int denseIdx = (int)(curveIdx - streamCount);
                    if (denseClip?.m_SampleArray != null)
                    {
                        int offset = frameIdx * (int)denseCount + denseIdx;
                        if (offset >= 0 && offset < denseClip.m_SampleArray.Length)
                            return denseClip.m_SampleArray[offset];
                    }
                    return 0f;
                }
                else
                {
                    int constIdx = (int)(curveIdx - (streamCount + denseCount));
                    if (constantClip?.data != null && constIdx >= 0 && constIdx < constantClip.data.Length)
                        return constantClip.data[constIdx];
                    return 0f;
                }
            }

            bool IsChannelAnimated(int chIdx)
            {
                if (chIdx < 0 || chIdx >= indexArray.Length)
                    return false;
                return indexArray[chIdx] >= 0;
            }

            // 1. Process Pelvis (Bone 0) for RootT and RootQ
            if (avatar.m_Avatar.m_HumanSkeletonIndexArray.Length > 0)
            {
                int pelvisNode = avatar.m_Avatar.m_HumanSkeletonIndexArray[0];
                if (pelvisNode >= 0 && pelvisNode < avatar.m_Avatar.m_AvatarSkeleton.m_Node.Length)
                {
                    uint pelvisId = avatar.m_Avatar.m_AvatarSkeleton.m_ID[pelvisNode];
                    string pelvisPath = avatar.FindBonePath(pelvisId);
                    if (!string.IsNullOrEmpty(pelvisPath))
                    {
                        string fixedPelvis = FixBonePath(pelvisPath) ?? pelvisPath;
                        var pTrack = iAnim.FindTrack(fixedPelvis);

                        var defX = avatar.m_Avatar.m_DefaultPose?.m_X != null && pelvisNode < avatar.m_Avatar.m_DefaultPose.m_X.Length
                            ? avatar.m_Avatar.m_DefaultPose.m_X[pelvisNode]
                            : null;
                        var defT = defX?.t ?? Vector3.Zero;
                        var defQ = defX?.q ?? new Quaternion(0, 0, 0, 1);

                        bool hasRootT = IsChannelAnimated(0) || IsChannelAnimated(1) || IsChannelAnimated(2);
                        bool hasRootQ = IsChannelAnimated(3) || IsChannelAnimated(4) || IsChannelAnimated(5) || IsChannelAnimated(6);

                        if (pTrack.Translations.Count == 0)
                        {
                            for (int f = 0; f < frameCount; f++)
                            {
                                float time = GetTime(f);
                                float rx = hasRootT ? GetChannelValue(0, f, time) : 0f;
                                float ry = hasRootT ? GetChannelValue(1, f, time) : 0f;
                                float rz = hasRootT ? GetChannelValue(2, f, time) : 0f;
                                var tLocal = new Vector3(defT.X + rx, defT.Y + ry, defT.Z + rz);
                                var tFbx = new Vector3(-tLocal.X, tLocal.Y, tLocal.Z);
                                pTrack.Translations.Add(new ImportedKeyframe<Vector3>(time, tFbx));
                            }
                        }

                        if (pTrack.Rotations.Count == 0)
                        {
                            for (int f = 0; f < frameCount; f++)
                            {
                                float time = GetTime(f);
                                Quaternion qLocal;
                                if (hasRootQ)
                                {
                                    float qx = GetChannelValue(3, f, time);
                                    float qy = GetChannelValue(4, f, time);
                                    float qz = GetChannelValue(5, f, time);
                                    float qw = GetChannelValue(6, f, time);
                                    var qRoot = new Quaternion(qx, qy, qz, qw);
                                    qLocal = MultiplyQuat(qRoot, defQ);
                                }
                                else
                                {
                                    qLocal = defQ;
                                }
                                var qFbx = new Quaternion(qLocal.X, -qLocal.Y, -qLocal.Z, qLocal.W);
                                pTrack.Rotations.Add(new ImportedKeyframe<Vector3>(time, QuaternionToEuler(qFbx)));
                            }
                        }
                    }
                }
            }

            // 2. Process all mapped humanoid bones from HumanBoneBindings
            var humanSkeleton = avatar.m_Avatar.m_Human?.m_Skeleton;
            if (humanSkeleton == null || humanSkeleton.m_Node == null || humanSkeleton.m_AxesArray == null)
                return;

            var processedNodes = new HashSet<int>();
            foreach (var binding in HumanBoneBindings)
            {
                if (binding.HumanBoneId >= avatar.m_Avatar.m_HumanSkeletonIndexArray.Length)
                    continue;

                int nodeIdx = avatar.m_Avatar.m_HumanSkeletonIndexArray[binding.HumanBoneId];
                if (nodeIdx < 0 || nodeIdx >= avatar.m_Avatar.m_AvatarSkeleton.m_Node.Length)
                    continue;

                if (!processedNodes.Add(nodeIdx))
                    continue;

                if (binding.HumanBoneId >= humanSkeleton.m_Node.Length)
                    continue;

                var hNode = humanSkeleton.m_Node[binding.HumanBoneId];
                if (hNode.m_AxesId < 0 || hNode.m_AxesId >= humanSkeleton.m_AxesArray.Length)
                    continue;

                var axes = humanSkeleton.m_AxesArray[hNode.m_AxesId];
                if (axes == null)
                    continue;

                uint boneId = avatar.m_Avatar.m_AvatarSkeleton.m_ID[nodeIdx];
                string bonePath = avatar.FindBonePath(boneId);
                if (string.IsNullOrEmpty(bonePath))
                    continue;

                string fixedPath = FixBonePath(bonePath) ?? bonePath;
                var track = iAnim.FindTrack(fixedPath);
                if (track.Rotations.Count > 0)
                    continue;

                bool anyAnimated = false;
                for (int i = 0; i < binding.MuscleCount; i++)
                {
                    if (IsChannelAnimated(binding.MuscleStartIndex + i + 7))
                    {
                        anyAnimated = true;
                        break;
                    }
                }
                if (!anyAnimated)
                    continue;

                var preQ = new Quaternion(axes.m_PreQ.X, axes.m_PreQ.Y, axes.m_PreQ.Z, axes.m_PreQ.W);
                var postQ = new Quaternion(axes.m_PostQ.X, axes.m_PostQ.Y, axes.m_PostQ.Z, axes.m_PostQ.W);
                var postQInv = ConjugateQuat(postQ);
                var min = ToVec3(axes.m_Limit?.m_Min);
                var max = ToVec3(axes.m_Limit?.m_Max);
                var sgn = ToVec3(axes.m_Sgn);

                for (int f = 0; f < frameCount; f++)
                {
                    float time = GetTime(f);
                    float thetaX = 0f, thetaY = 0f, thetaZ = 0f;

                    for (int i = 0; i < binding.MuscleCount; i++)
                    {
                        int mIdx = binding.MuscleStartIndex + i;
                        float val = GetChannelValue(mIdx + 7, f, time);
                        int axis = binding.DofAxes[i];

                        float minVal = axis == 0 ? min.X : (axis == 1 ? min.Y : min.Z);
                        float maxVal = axis == 0 ? max.X : (axis == 1 ? max.Y : max.Z);
                        float sgnVal = axis == 0 ? sgn.X : (axis == 1 ? sgn.Y : sgn.Z);

                        float angle = (val > 0f ? val * maxVal : -val * minVal) * sgnVal;

                        if (axis == 0) thetaX = angle;
                        else if (axis == 1) thetaY = angle;
                        else if (axis == 2) thetaZ = angle;
                    }

                    float hx = thetaX * 0.5f;
                    float hy = thetaY * 0.5f;
                    float hz = thetaZ * 0.5f;
                    var qx = new Quaternion((float)Math.Sin(hx), 0f, 0f, (float)Math.Cos(hx));
                    var qy = new Quaternion(0f, (float)Math.Sin(hy), 0f, (float)Math.Cos(hy));
                    var qz = new Quaternion(0f, 0f, (float)Math.Sin(hz), (float)Math.Cos(hz));
                    var qMuscle = MultiplyQuat(MultiplyQuat(qy, qx), qz);

                    var qLocal = MultiplyQuat(MultiplyQuat(preQ, qMuscle), postQInv);
                    var qFbx = new Quaternion(qLocal.X, -qLocal.Y, -qLocal.Z, qLocal.W);
                    track.Rotations.Add(new ImportedKeyframe<Vector3>(time, QuaternionToEuler(qFbx)));
                }
            }
        }

        public static ModelConverter CreateFromModelGroup(
            string? groupName,
            GameObject? rootGameObject,
            IReadOnlyList<Mesh>? meshes,
            IReadOnlyList<Renderer>? renderers,
            Avatar? avatar,
            AnimationClip[]? animationClips,
            ImageFormat imageFormat,
            bool combineMeshes = false)
        {
            return CreateFromModelGroup(
                groupName,
                rootGameObject,
                meshes,
                renderers,
                avatar,
                animationClips,
                imageFormat,
                combineMeshes,
                partTransforms: null,
                partMaterials: null,
                partNames: null);
        }

        public static ModelConverter CreateFromModelGroup(
            string? groupName,
            GameObject? rootGameObject,
            IReadOnlyList<Mesh>? meshes,
            IReadOnlyList<Renderer>? renderers,
            Avatar? avatar,
            AnimationClip[]? animationClips,
            ImageFormat imageFormat,
            bool combineMeshes,
            IReadOnlyList<float[]>? partTransforms,
            IReadOnlyList<IReadOnlyList<Material>>? partMaterials = null,
            IReadOnlyList<string>? partNames = null)
        {
            var converter = new ModelConverter(imageFormat);
            converter.InitFromModelGroup(
                groupName,
                rootGameObject,
                meshes,
                renderers,
                avatar,
                animationClips,
                combineMeshes,
                partTransforms,
                partMaterials,
                partNames);
            return converter;
        }

        private void InitFromModelGroup(
            string? groupName,
            GameObject? rootGameObject,
            IReadOnlyList<Mesh>? meshes,
            IReadOnlyList<Renderer>? renderers,
            Avatar? providedAvatar,
            AnimationClip[]? animationClips,
            bool combineMeshes,
            IReadOnlyList<float[]>? partTransforms,
            IReadOnlyList<IReadOnlyList<Material>>? partMaterials,
            IReadOnlyList<string>? partNames)
        {
            var effectiveGroupName = !string.IsNullOrWhiteSpace(groupName)
                ? groupName
                : !string.IsNullOrWhiteSpace(rootGameObject?.m_Name)
                    ? rootGameObject.m_Name
                    : "ModelGroup";

            if (providedAvatar != null)
            {
                avatar = providedAvatar;
            }

            var convertedRenderers = new HashSet<Renderer>();
            var convertedMeshes = new HashSet<Mesh>();

            if (rootGameObject != null)
            {
                HydrateGameObjectHierarchy(rootGameObject);
                if (rootGameObject.m_Animator != null)
                {
                    InitWithAnimator(rootGameObject.m_Animator);
                    if (animationClips == null)
                    {
                        CollectAnimationClip(rootGameObject.m_Animator);
                    }
                }
                else
                {
                    InitWithGameObject(rootGameObject);
                }

                if (avatar == null)
                {
                    avatar = FindAvatar(rootGameObject);
                }

                CollectConvertedRenderersAndMeshesFromHierarchy(rootGameObject.m_Transform, convertedRenderers, convertedMeshes);
            }

            if (!hasRootFrame)
            {
                SetRootFrame(CreateFrame(effectiveGroupName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One));
                if (avatar != null)
                {
                    TryBuildAvatarSkeletonFrames(meshes);
                }
            }

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    var renderer = renderers[i];
                    if (renderer == null || !convertedRenderers.Add(renderer))
                    {
                        continue;
                    }

                    var rendererMesh = GetMesh(renderer);
                    if (rendererMesh == null && meshes != null && i < meshes.Count)
                    {
                        rendererMesh = meshes[i];
                    }

                    if (rendererMesh == null)
                    {
                        continue;
                    }

                    var rendererGo = ResolveRendererGameObject(renderer);
                    if (rendererGo != null)
                    {
                        HydrateGameObjectHierarchy(rendererGo);
                        if (rendererGo.m_Transform != null && !transformDictionary.ContainsKey(rendererGo.m_Transform))
                        {
                            ConvertTransforms(rendererGo.m_Transform, RootFrame);
                            CreateBonePathHash(rendererGo.m_Transform);
                        }
                    }

                    if (renderer is SkinnedMeshRenderer smr && smr.m_Bones != null)
                    {
                        EnsureBoneTransformsInHierarchy(smr);
                    }

                    var fallbackMats = partMaterials != null && i < partMaterials.Count ? partMaterials[i] : null;
                    var partTransform = partTransforms != null && i < partTransforms.Count ? partTransforms[i] : null;
                    var explicitName = partNames != null && i < partNames.Count ? partNames[i] : null;

                    string framePath;
                    if (rendererGo?.m_Transform != null && transformDictionary.TryGetValue(rendererGo.m_Transform, out var existingFrame))
                    {
                        framePath = existingFrame.Path;
                    }
                    else
                    {
                        var partFrame = CreatePartFrameUnderRoot(
                            !string.IsNullOrWhiteSpace(explicitName) ? explicitName : rendererGo?.m_Name ?? rendererMesh.m_Name ?? $"Part_{i}",
                            partTransform);
                        framePath = partFrame.Path;
                    }

                    ConvertModelGroupMeshPart(rendererMesh, framePath, fallbackMats, renderer);
                    convertedMeshes.Add(rendererMesh);
                }
            }

            if (meshes != null)
            {
                for (int i = 0; i < meshes.Count; i++)
                {
                    var mesh = meshes[i];
                    if (mesh == null)
                    {
                        continue;
                    }

                    var partTransform = partTransforms != null && i < partTransforms.Count ? partTransforms[i] : null;
                    var partMats = partMaterials != null && i < partMaterials.Count ? partMaterials[i] : null;
                    var explicitName = partNames != null && i < partNames.Count ? partNames[i] : null;

                    // If the same Mesh asset appears multiple times in a ModelGroup with distinct transforms/names,
                    // still emit each part instance when rootGameObject was not used to convert them.
                    if (rootGameObject != null && convertedMeshes.Contains(mesh))
                    {
                        continue;
                    }
                    if (rootGameObject == null && renderers != null && i < renderers.Count && renderers[i] != null && convertedMeshes.Contains(mesh))
                    {
                        continue;
                    }

                    var partName = !string.IsNullOrWhiteSpace(explicitName)
                        ? explicitName
                        : !string.IsNullOrWhiteSpace(mesh.m_Name)
                            ? mesh.m_Name
                            : $"Part_{i}";
                    var partFrame = CreatePartFrameUnderRoot(partName, partTransform);
                    ConvertModelGroupMeshPart(mesh, partFrame.Path, partMats, null);
                    convertedMeshes.Add(mesh);
                }
            }

            if (animationClips != null)
            {
                for (int i = 0; i < animationClips.Length; i++)
                {
                    if (animationClips[i] != null)
                    {
                        animationClipHashSet.Add(animationClips[i]);
                    }
                }
            }

            ConvertAnimations();

            if (combineMeshes)
            {
                CombineMeshes();
            }
        }

        private static void HydrateGameObjectHierarchy(GameObject rootGameObject)
        {
            if (rootGameObject == null)
            {
                return;
            }

            var visited = new HashSet<GameObject>();
            var queue = new Queue<GameObject>();
            queue.Enqueue(rootGameObject);

            while (queue.Count > 0)
            {
                var go = queue.Dequeue();
                if (go == null || !visited.Add(go))
                {
                    continue;
                }

                if (go.m_Components != null)
                {
                    for (int i = 0; i < go.m_Components.Length; i++)
                    {
                        var compPtr = go.m_Components[i];
                        if (compPtr == null || compPtr.IsNull)
                        {
                            continue;
                        }

                        Component? comp = null;
                        if (!compPtr.TryGet(out comp) && go.assetsFile?.assetsManager != null)
                        {
                            var files = go.assetsFile.assetsManager.assetsFileList;
                            for (int f = 0; f < files.Count; f++)
                            {
                                if (files[f].ObjectsDic.TryGetValue(compPtr.m_PathID, out var obj) && obj is Component foundComp)
                                {
                                    comp = foundComp;
                                    break;
                                }
                            }
                        }

                        if (comp == null)
                        {
                            continue;
                        }

                        switch (comp)
                        {
                            case Transform tr when go.m_Transform == null:
                                go.m_Transform = tr;
                                break;
                            case MeshRenderer mr when go.m_MeshRenderer == null:
                                go.m_MeshRenderer = mr;
                                break;
                            case MeshFilter mf when go.m_MeshFilter == null:
                                go.m_MeshFilter = mf;
                                break;
                            case SkinnedMeshRenderer smr when go.m_SkinnedMeshRenderer == null:
                                go.m_SkinnedMeshRenderer = smr;
                                break;
                            case Animator anim when go.m_Animator == null:
                                go.m_Animator = anim;
                                break;
                            case Animation legacyAnim when go.m_Animation == null:
                                go.m_Animation = legacyAnim;
                                break;
                        }
                    }
                }

                if (go.m_Transform?.m_Children != null)
                {
                    for (int c = 0; c < go.m_Transform.m_Children.Length; c++)
                    {
                        var childPtr = go.m_Transform.m_Children[c];
                        if (childPtr == null || childPtr.IsNull)
                        {
                            continue;
                        }

                        if (!childPtr.TryGet(out var childTrans) && go.assetsFile?.assetsManager != null)
                        {
                            var files = go.assetsFile.assetsManager.assetsFileList;
                            for (int f = 0; f < files.Count; f++)
                            {
                                if (files[f].ObjectsDic.TryGetValue(childPtr.m_PathID, out var obj) && obj is Transform foundTrans)
                                {
                                    childTrans = foundTrans;
                                    break;
                                }
                            }
                        }

                        if (childTrans?.m_GameObject == null || childTrans.m_GameObject.IsNull)
                        {
                            continue;
                        }

                        if (!childTrans.m_GameObject.TryGet(out var childGo) && childTrans.assetsFile?.assetsManager != null)
                        {
                            var files = childTrans.assetsFile.assetsManager.assetsFileList;
                            for (int f = 0; f < files.Count; f++)
                            {
                                if (files[f].ObjectsDic.TryGetValue(childTrans.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                                {
                                    childGo = foundGo;
                                    break;
                                }
                            }
                        }

                        if (childGo != null && !visited.Contains(childGo))
                        {
                            queue.Enqueue(childGo);
                        }
                    }
                }
            }
        }

        private static void CollectConvertedRenderersAndMeshesFromHierarchy(
            Transform rootTransform,
            HashSet<Renderer> convertedRenderers,
            HashSet<Mesh> convertedMeshes)
        {
            if (rootTransform == null)
            {
                return;
            }

            var visited = new HashSet<Transform>();
            var stack = new Stack<Transform>();
            stack.Push(rootTransform);

            while (stack.Count > 0)
            {
                var trans = stack.Pop();
                if (trans == null || !visited.Add(trans))
                {
                    continue;
                }

                if (trans.m_GameObject != null && trans.m_GameObject.TryGet(out var go) && go != null)
                {
                    if (go.m_MeshRenderer != null)
                    {
                        convertedRenderers.Add(go.m_MeshRenderer);
                        var mesh = GetMesh(go.m_MeshRenderer);
                        if (mesh != null) convertedMeshes.Add(mesh);
                    }
                    if (go.m_SkinnedMeshRenderer != null)
                    {
                        convertedRenderers.Add(go.m_SkinnedMeshRenderer);
                        var mesh = GetMesh(go.m_SkinnedMeshRenderer);
                        if (mesh != null) convertedMeshes.Add(mesh);
                    }
                }

                if (trans.m_Children != null)
                {
                    for (int i = 0; i < trans.m_Children.Length; i++)
                    {
                        if (trans.m_Children[i] != null && trans.m_Children[i].TryGet(out var child) && child != null)
                        {
                            stack.Push(child);
                        }
                    }
                }
            }
        }

        private static GameObject? ResolveRendererGameObject(Renderer? renderer)
        {
            if (renderer?.m_GameObject == null || renderer.m_GameObject.IsNull)
            {
                return null;
            }

            if (renderer.m_GameObject.TryGet(out var go) && go != null)
            {
                return go;
            }

            if (renderer.assetsFile?.assetsManager != null)
            {
                var files = renderer.assetsFile.assetsManager.assetsFileList;
                for (int i = 0; i < files.Count; i++)
                {
                    if (files[i].ObjectsDic.TryGetValue(renderer.m_GameObject.m_PathID, out var obj) && obj is GameObject foundGo)
                    {
                        return foundGo;
                    }
                }
            }

            return null;
        }

        private void EnsureBoneTransformsInHierarchy(SkinnedMeshRenderer smr)
        {
            if (smr.m_Bones == null || RootFrame == null)
            {
                return;
            }

            var rootBoneTransforms = new HashSet<Transform>();
            for (int i = 0; i < smr.m_Bones.Length; i++)
            {
                if (!TryGetBoneTransform(smr.m_Bones[i], smr, out var boneTrans) || boneTrans == null)
                {
                    continue;
                }

                if (transformDictionary.ContainsKey(boneTrans))
                {
                    continue;
                }

                // Walk up to the highest ancestor transform not yet in transformDictionary
                var top = boneTrans;
                var visitedAncestors = new HashSet<Transform> { top };
                while (top.m_Father != null && !top.m_Father.IsNull)
                {
                    Transform? father = null;
                    if (!top.m_Father.TryGet(out father) && smr.assetsFile?.assetsManager != null)
                    {
                        var files = smr.assetsFile.assetsManager.assetsFileList;
                        for (int f = 0; f < files.Count; f++)
                        {
                            if (files[f].ObjectsDic.TryGetValue(top.m_Father.m_PathID, out var obj) && obj is Transform foundFather)
                            {
                                father = foundFather;
                                break;
                            }
                        }
                    }

                    if (father == null || !visitedAncestors.Add(father))
                    {
                        break;
                    }

                    if (transformDictionary.ContainsKey(father))
                    {
                        ConvertTransforms(top, transformDictionary[father]);
                        CreateBonePathHash(top);
                        top = null;
                        break;
                    }

                    top = father;
                }

                if (top != null && rootBoneTransforms.Add(top))
                {
                    ConvertTransforms(top, RootFrame);
                    CreateBonePathHash(top);
                }
            }
        }

        private void TryBuildAvatarSkeletonFrames(IReadOnlyList<Mesh>? meshes)
        {
            var skeleton = avatar?.m_Avatar?.m_AvatarSkeleton;
            var skeletonPose = avatar?.m_Avatar?.m_DefaultPose;
            var nodes = skeleton?.m_Node;
            if (avatar == null || skeleton == null || nodes == null || nodes.Length == 0 || skeletonPose?.m_X == null || skeleton.m_ID == null)
            {
                return;
            }

            Mesh? bestMesh = null;
            int bestBoneCount = 0;
            if (meshes != null)
            {
                for (int i = 0; i < meshes.Count; i++)
                {
                    var m = meshes[i];
                    if (m == null) continue;
                    m.EnsureProcessed();
                    if (m.m_BoneNameHashes != null && m.m_BindPose != null
                        && m.m_BoneNameHashes.Length == m.m_BindPose.Length
                        && m.m_BoneNameHashes.Length > bestBoneCount)
                    {
                        bestBoneCount = m.m_BoneNameHashes.Length;
                        bestMesh = m;
                    }
                }
            }

            var skelCount = nodes.Length;
            var skelNodeToMeshBone = new int[skelCount];
            for (int i = 0; i < skelCount; i++) skelNodeToMeshBone[i] = -1;

            int[]? meshParentIndices = null;
            System.Numerics.Matrix4x4[]? bindPoseInverses = null;
            System.Numerics.Matrix4x4[]? bindPoses = null;
            bool hasBindPose = false;

            if (bestMesh != null && bestBoneCount > 0)
            {
                var meshBoneHashToIdx = new Dictionary<uint, int>(bestBoneCount);
                for (int j = 0; j < bestBoneCount; j++)
                {
                    meshBoneHashToIdx[bestMesh.m_BoneNameHashes[j]] = j;
                }

                var meshBoneToSkelNode = new int[bestBoneCount];
                for (int j = 0; j < bestBoneCount; j++) meshBoneToSkelNode[j] = -1;

                for (int i = 0; i < skelCount && i < skeleton.m_ID.Length; i++)
                {
                    if (meshBoneHashToIdx.TryGetValue(skeleton.m_ID[i], out int mbIdx))
                    {
                        skelNodeToMeshBone[i] = mbIdx;
                        meshBoneToSkelNode[mbIdx] = i;
                    }
                }

                meshParentIndices = new int[bestBoneCount];
                for (int mb = 0; mb < bestBoneCount; mb++)
                {
                    meshParentIndices[mb] = -1;
                    int skelIdx = meshBoneToSkelNode[mb];
                    if (skelIdx < 0) continue;

                    int current = nodes[skelIdx].m_ParentId;
                    int guard = 0;
                    while (current >= 0 && current < skelCount && guard++ < skelCount)
                    {
                        if (skelNodeToMeshBone[current] >= 0)
                        {
                            meshParentIndices[mb] = skelNodeToMeshBone[current];
                            break;
                        }
                        current = nodes[current].m_ParentId;
                    }
                }

                bindPoseInverses = new System.Numerics.Matrix4x4[bestBoneCount];
                bindPoses = new System.Numerics.Matrix4x4[bestBoneCount];
                hasBindPose = true;
                for (int j = 0; j < bestBoneCount; j++)
                {
                    var bp = ToNumericsMatrix(bestMesh.m_BindPose[j]);
                    bindPoseInverses[j] = bp;
                    bindPoses[j] = System.Numerics.Matrix4x4.Invert(bp, out var inv)
                        ? inv
                        : System.Numerics.Matrix4x4.Identity;
                }
            }

            var frames = new ImportedFrame[skelCount];
            for (int i = 0; i < skelCount; i++)
            {
                var xform = i < skeletonPose.m_X.Length ? skeletonPose.m_X[i] : null;
                uint nodeId = i < skeleton.m_ID.Length ? skeleton.m_ID[i] : (uint)i;
                var name = avatar.FindBonePath(nodeId);
                if (string.IsNullOrEmpty(name))
                {
                    name = $"Bone_{nodeId}";
                }
                else
                {
                    var lastSlash = name.LastIndexOf('/');
                    if (lastSlash >= 0) name = name.Substring(lastSlash + 1);
                }

                var tVec = xform?.t ?? Vector3.Zero;
                var qQuat = xform?.q ?? new Quaternion(0, 0, 0, 1);
                var sVec = xform?.s ?? Vector3.One;

                int mb = skelNodeToMeshBone[i];
                if (hasBindPose && mb >= 0 && meshParentIndices != null && bindPoses != null && bindPoseInverses != null)
                {
                    int pMb = meshParentIndices[mb];
                    var localMat = pMb >= 0 ? bindPoses[mb] * bindPoseInverses[pMb] : bindPoses[mb];
                    if (System.Numerics.Matrix4x4.Decompose(localMat, out var s, out var q, out var t))
                    {
                        tVec = new Vector3(t.X, t.Y, t.Z);
                        qQuat = new Quaternion(q.X, q.Y, q.Z, q.W);
                        sVec = new Vector3(s.X, s.Y, s.Z);
                    }
                }

                var frame = RootFrame.FindChild(name);
                if (frame != null)
                {
                    SetFrame(frame, tVec, qQuat, sVec);
                }
                else
                {
                    frame = CreateFrame(name, tVec, qQuat, sVec);
                }
                frames[i] = frame;
            }

            for (int i = 0; i < skelCount; i++)
            {
                var parentId = nodes[i].m_ParentId;
                var parentFrame = (parentId >= 0 && parentId < frames.Length && parentId != i)
                    ? frames[parentId]
                    : RootFrame;
                if (frames[i].Parent == null)
                {
                    parentFrame.AddChild(frames[i]);
                }
            }

            for (int i = 0; i < skelCount && i < skeleton.m_ID.Length; i++)
            {
                bonePathHash[skeleton.m_ID[i]] = frames[i].Path;
            }
        }

        private ImportedFrame CreatePartFrameUnderRoot(string? preferredName, float[]? unityRowMajorMatrix)
        {
            var baseName = string.IsNullOrWhiteSpace(preferredName) ? "Part" : preferredName;
            var uniqueName = baseName;
            if (RootFrame != null && RootFrame.FindChild(uniqueName, recursive: false) != null)
            {
                for (int suffix = 1; ; suffix++)
                {
                    var candidate = $"{baseName}_{suffix}";
                    if (RootFrame.FindChild(candidate, recursive: false) == null)
                    {
                        uniqueName = candidate;
                        break;
                    }
                }
            }

            var tVec = Vector3.Zero;
            var qQuat = new Quaternion(0, 0, 0, 1);
            var sVec = Vector3.One;

            if (unityRowMajorMatrix != null && unityRowMajorMatrix.Length == 16)
            {
                // ModelGroupMeshInfo.TransformMatrix stores a Unity column-vector 4x4 matrix in row-major order
                // (translation in [3], [7], [11]). Transpose to System.Numerics row-vector layout before decomposing.
                var unityNumerics = new System.Numerics.Matrix4x4(
                    unityRowMajorMatrix[0], unityRowMajorMatrix[4], unityRowMajorMatrix[8], unityRowMajorMatrix[12],
                    unityRowMajorMatrix[1], unityRowMajorMatrix[5], unityRowMajorMatrix[9], unityRowMajorMatrix[13],
                    unityRowMajorMatrix[2], unityRowMajorMatrix[6], unityRowMajorMatrix[10], unityRowMajorMatrix[14],
                    unityRowMajorMatrix[3], unityRowMajorMatrix[7], unityRowMajorMatrix[11], unityRowMajorMatrix[15]);

                if (System.Numerics.Matrix4x4.Decompose(unityNumerics, out var s, out var q, out var t))
                {
                    tVec = new Vector3(t.X, t.Y, t.Z);
                    qQuat = new Quaternion(q.X, q.Y, q.Z, q.W);
                    sVec = new Vector3(s.X, s.Y, s.Z);
                }
                else
                {
                    tVec = new Vector3(unityRowMajorMatrix[3], unityRowMajorMatrix[7], unityRowMajorMatrix[11]);
                }
            }

            var frame = CreateFrame(uniqueName, tVec, qQuat, sVec);
            RootFrame?.AddChild(frame);
            return frame;
        }

        private void ConvertModelGroupMeshPart(Mesh mesh, string? framePath, IReadOnlyList<Material>? fallbackMaterials, Renderer? renderer)
        {
            if (mesh == null)
            {
                return;
            }

            mesh.EnsureProcessed();
            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
            {
                return;
            }

            var iMesh = new ImportedMesh
            {
                Path = framePath ?? string.Empty,
                SubmeshList = new List<ImportedSubmesh>()
            };

            HashSet<int>? subHashSet = null;
            int firstSubMesh = 0;
            if (renderer != null)
            {
                if (renderer.m_StaticBatchInfo?.subMeshCount > 0)
                {
                    firstSubMesh = renderer.m_StaticBatchInfo.firstSubMesh;
                    var finalSubMesh = firstSubMesh + renderer.m_StaticBatchInfo.subMeshCount;
                    subHashSet = new HashSet<int>();
                    for (int i = firstSubMesh; i < finalSubMesh; i++)
                    {
                        subHashSet.Add(i);
                    }
                }
                else if (renderer.m_SubsetIndices?.Length > 0)
                {
                    subHashSet = new HashSet<int>();
                    uint minIdx = uint.MaxValue;
                    for (int i = 0; i < renderer.m_SubsetIndices.Length; i++)
                    {
                        var idx = renderer.m_SubsetIndices[i];
                        if (idx < minIdx) minIdx = idx;
                        subHashSet.Add((int)idx);
                    }
                    firstSubMesh = (int)minIdx;
                }
            }

            iMesh.hasNormal = mesh.m_Normals?.Length > 0;
            iMesh.hasUV = new bool[8];
            for (int uv = 0; uv < 8; uv++)
            {
                iMesh.hasUV[uv] = mesh.GetUV(uv)?.Length > 0;
            }
            iMesh.hasTangent = mesh.m_Tangents != null && mesh.m_Tangents.Length == mesh.m_VertexCount * 4;
            iMesh.hasColor = mesh.m_Colors?.Length > 0;

            var subMeshes = mesh.m_SubMeshes;
            if (subMeshes != null && subMeshes.Length > 0)
            {
                int firstFace = 0;
                for (int i = 0; i < subMeshes.Length; i++)
                {
                    int numFaces = (int)subMeshes[i].indexCount / 3;
                    if (subHashSet != null && subHashSet.Count > 0 && !subHashSet.Contains(i))
                    {
                        firstFace += numFaces;
                        continue;
                    }

                    var submesh = subMeshes[i];
                    var matSlotIndex = i - firstSubMesh;
                    Material? mat = ResolvePartMaterial(renderer, matSlotIndex, fallbackMaterials);
                    var iMat = ConvertMaterial(mat);

                    var iSubmesh = new ImportedSubmesh
                    {
                        Material = iMat.Name,
                        BaseVertex = checked((int)(submesh.firstVertex + submesh.baseVertex)),
                        FaceList = new List<ImportedFace>(numFaces)
                    };

                    var end = firstFace + numFaces;
                    for (int f = firstFace; f < end; f++)
                    {
                        if (f * 3 + 2 >= mesh.m_Indices.Count)
                        {
                            break;
                        }

                        var face = new ImportedFace
                        {
                            VertexIndices = new[]
                            {
                                (int)(mesh.m_Indices[f * 3 + 2] - submesh.firstVertex),
                                (int)(mesh.m_Indices[f * 3 + 1] - submesh.firstVertex),
                                (int)(mesh.m_Indices[f * 3] - submesh.firstVertex)
                            }
                        };
                        iSubmesh.FaceList.Add(face);
                    }

                    firstFace = end;
                    iMesh.SubmeshList.Add(iSubmesh);
                }
            }
            else if (mesh.m_Indices != null && mesh.m_Indices.Count >= 3)
            {
                var mat = ResolvePartMaterial(renderer, 0, fallbackMaterials);
                var iMat = ConvertMaterial(mat);
                int numFaces = mesh.m_Indices.Count / 3;
                var iSubmesh = new ImportedSubmesh
                {
                    Material = iMat.Name,
                    BaseVertex = 0,
                    FaceList = new List<ImportedFace>(numFaces)
                };
                for (int f = 0; f < numFaces; f++)
                {
                    iSubmesh.FaceList.Add(new ImportedFace
                    {
                        VertexIndices = new[]
                        {
                            (int)mesh.m_Indices[f * 3 + 2],
                            (int)mesh.m_Indices[f * 3 + 1],
                            (int)mesh.m_Indices[f * 3]
                        }
                    });
                }
                iMesh.SubmeshList.Add(iSubmesh);
            }

            int vertexCount = mesh.m_VertexCount;
            int posStride = mesh.m_Vertices.Length == vertexCount * 4 ? 4 : 3;
            int normalStride = iMesh.hasNormal && mesh.m_Normals != null && mesh.m_Normals.Length == vertexCount * 4 ? 4 : 3;
            int colorStride = iMesh.hasColor && mesh.m_Colors != null && mesh.m_Colors.Length == vertexCount * 3 ? 3 : 4;
            var uvArrays = new float[8][];
            var uvStrides = new int[8];
            for (int uv = 0; uv < 8; uv++)
            {
                if (iMesh.hasUV[uv])
                {
                    uvArrays[uv] = mesh.GetUV(uv);
                    uvStrides[uv] = uvArrays[uv].Length == vertexCount * 3 ? 3 : (uvArrays[uv].Length == vertexCount * 4 ? 4 : 2);
                }
            }

            iMesh.VertexList = new List<ImportedVertex>(vertexCount);
            for (int j = 0; j < vertexCount; j++)
            {
                var iVertex = new ImportedVertex
                {
                    Vertex = new Vector3(-mesh.m_Vertices[j * posStride], mesh.m_Vertices[j * posStride + 1], mesh.m_Vertices[j * posStride + 2]),
                    UV = new float[8][]
                };

                if (iMesh.hasNormal && mesh.m_Normals != null && j * normalStride + 2 < mesh.m_Normals.Length)
                {
                    iVertex.Normal = new Vector3(-mesh.m_Normals[j * normalStride], mesh.m_Normals[j * normalStride + 1], mesh.m_Normals[j * normalStride + 2]);
                }

                for (int uv = 0; uv < 8; uv++)
                {
                    if (iMesh.hasUV[uv] && j * uvStrides[uv] + 1 < uvArrays[uv].Length)
                    {
                        iVertex.UV[uv] = new[] { uvArrays[uv][j * uvStrides[uv]], uvArrays[uv][j * uvStrides[uv] + 1] };
                    }
                }

                if (iMesh.hasTangent && mesh.m_Tangents != null)
                {
                    iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], mesh.m_Tangents[j * 4 + 3]);
                }

                if (iMesh.hasColor && mesh.m_Colors != null && j * colorStride + 2 < mesh.m_Colors.Length)
                {
                    iVertex.Color = colorStride == 3
                        ? new Color(mesh.m_Colors[j * 3], mesh.m_Colors[j * 3 + 1], mesh.m_Colors[j * 3 + 2], 1.0f)
                        : new Color(mesh.m_Colors[j * 4], mesh.m_Colors[j * 4 + 1], mesh.m_Colors[j * 4 + 2], mesh.m_Colors[j * 4 + 3]);
                }

                if (mesh.m_Skin != null && j < mesh.m_Skin.Length && mesh.m_Skin[j]?.boneIndex != null && mesh.m_Skin[j]?.weight != null)
                {
                    var inf = mesh.m_Skin[j];
                    iVertex.BoneIndices = new int[4];
                    iVertex.Weights = new float[4];
                    int count = Math.Min(4, Math.Min(inf.boneIndex.Length, inf.weight.Length));
                    for (int k = 0; k < count; k++)
                    {
                        iVertex.BoneIndices[k] = inf.boneIndex[k];
                        iVertex.Weights[k] = inf.weight[k];
                    }
                }

                iMesh.VertexList.Add(iVertex);
            }

            // Bones (from SkinnedMeshRenderer.m_Bones or Mesh.m_BoneNameHashes)
            if (mesh.m_BindPose != null && mesh.m_BindPose.Length > 0)
            {
                var boneType = 0;
                var sMesh = renderer as SkinnedMeshRenderer;
                if (sMesh != null && sMesh.m_Bones != null && sMesh.m_Bones.Length == mesh.m_BindPose.Length)
                {
                    int verified = 0;
                    for (int b = 0; b < sMesh.m_Bones.Length; b++)
                    {
                        if (TryGetBoneTransform(sMesh.m_Bones[b], sMesh, out _)) verified++;
                    }

                    if (verified > 0)
                    {
                        boneType = 1;
                    }
                    if (verified != sMesh.m_Bones.Length && mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length == mesh.m_BindPose.Length)
                    {
                        int verifiedByHash = 0;
                        for (int b = 0; b < mesh.m_BoneNameHashes.Length; b++)
                        {
                            if (FixBonePath(GetPathFromHash(mesh.m_BoneNameHashes[b])) != null) verifiedByHash++;
                        }
                        if (verifiedByHash > verified)
                        {
                            boneType = 2;
                        }
                    }
                }

                if (boneType == 0 && mesh.m_BoneNameHashes != null && mesh.m_BoneNameHashes.Length == mesh.m_BindPose.Length)
                {
                    int verifiedByHash = 0;
                    for (int b = 0; b < mesh.m_BoneNameHashes.Length; b++)
                    {
                        if (FixBonePath(GetPathFromHash(mesh.m_BoneNameHashes[b])) != null) verifiedByHash++;
                    }
                    if (verifiedByHash > 0)
                    {
                        boneType = 2;
                    }
                }

                var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                if (boneType == 1 && sMesh?.m_Bones != null)
                {
                    var boneCount = sMesh.m_Bones.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int b = 0; b < boneCount; b++)
                    {
                        var bone = new ImportedBone();
                        if (TryGetBoneTransform(sMesh.m_Bones[b], sMesh, out var boneTransform))
                        {
                            bone.Path = GetTransformPath(boneTransform);
                        }
                        bone.Matrix = convert * mesh.m_BindPose[b] * convert;
                        iMesh.BoneList.Add(bone);
                    }
                }
                else if (boneType == 2 && mesh.m_BoneNameHashes != null)
                {
                    var boneCount = mesh.m_BindPose.Length;
                    iMesh.BoneList = new List<ImportedBone>(boneCount);
                    for (int b = 0; b < boneCount; b++)
                    {
                        var bone = new ImportedBone
                        {
                            Path = FixBonePath(GetPathFromHash(mesh.m_BoneNameHashes[b])),
                            Matrix = convert * mesh.m_BindPose[b] * convert
                        };
                        iMesh.BoneList.Add(bone);
                    }
                }
            }

            // BlendShapes / Morphs
            if (mesh.m_Shapes?.channels?.Length > 0)
            {
                var morph = new ImportedMorph
                {
                    Path = iMesh.Path,
                    Channels = new List<ImportedMorphChannel>(mesh.m_Shapes.channels.Length)
                };
                MorphList.Add(morph);
                for (int i = 0; i < mesh.m_Shapes.channels.Length; i++)
                {
                    var shapeChannel = mesh.m_Shapes.channels[i];
                    var blendShapeName = "blendShape." + shapeChannel.name;
                    var crc = new SevenZip.CRC();
                    var bytes = Encoding.UTF8.GetBytes(blendShapeName);
                    crc.Update(bytes, 0, (uint)bytes.Length);
                    morphChannelNames[crc.GetDigest()] = blendShapeName;

                    var lastDot = shapeChannel.name.LastIndexOf('.');
                    var channel = new ImportedMorphChannel
                    {
                        Name = lastDot >= 0 ? shapeChannel.name.Substring(lastDot + 1) : shapeChannel.name,
                        KeyframeList = new List<ImportedMorphKeyframe>(shapeChannel.frameCount)
                    };
                    morph.Channels.Add(channel);

                    var frameEnd = shapeChannel.frameIndex + shapeChannel.frameCount;
                    for (int frameIdx = shapeChannel.frameIndex; frameIdx < frameEnd; frameIdx++)
                    {
                        if (frameIdx < 0 || frameIdx >= mesh.m_Shapes.fullWeights.Length || frameIdx >= mesh.m_Shapes.shapes.Length)
                            break;
                        var shape = mesh.m_Shapes.shapes[frameIdx];
                        var keyframe = new ImportedMorphKeyframe
                        {
                            Weight = mesh.m_Shapes.fullWeights[frameIdx],
                            hasNormals = shape.hasNormals,
                            hasTangents = shape.hasTangents,
                            VertexList = new List<ImportedMorphVertex>((int)shape.vertexCount)
                        };
                        channel.KeyframeList.Add(keyframe);

                        var vertexEnd = shape.firstVertex + shape.vertexCount;
                        for (uint j = shape.firstVertex; j < vertexEnd; j++)
                        {
                            if (j >= (uint)mesh.m_Shapes.vertices.Length)
                                break;
                            var morphVertex = mesh.m_Shapes.vertices[j];
                            if (morphVertex.index >= (uint)iMesh.VertexList.Count)
                                continue;
                            var sourceVertex = iMesh.VertexList[(int)morphVertex.index];
                            var destVertex = new ImportedMorphVertex
                            {
                                Index = morphVertex.index,
                                Vertex = new ImportedVertex
                                {
                                    Vertex = sourceVertex.Vertex + new Vector3(-morphVertex.vertex.X, morphVertex.vertex.Y, morphVertex.vertex.Z)
                                }
                            };
                            if (shape.hasNormals)
                            {
                                destVertex.Vertex.Normal = new Vector3(-morphVertex.normal.X, morphVertex.normal.Y, morphVertex.normal.Z);
                            }
                            if (shape.hasTangents)
                            {
                                destVertex.Vertex.Tangent = new Vector4(-morphVertex.tangent.X, morphVertex.tangent.Y, morphVertex.tangent.Z, 0);
                            }
                            keyframe.VertexList.Add(destVertex);
                        }
                    }
                }
            }

            MeshList.Add(iMesh);
        }

        private static Material? ResolvePartMaterial(Renderer? renderer, int slotIndex, IReadOnlyList<Material>? fallbackMaterials)
        {
            if (renderer?.m_Materials != null && slotIndex >= 0 && slotIndex < renderer.m_Materials.Length)
            {
                var matPPtr = renderer.m_Materials[slotIndex];
                if (matPPtr != null && !matPPtr.IsNull)
                {
                    if (matPPtr.TryGet(out var resolvedMat) && resolvedMat != null)
                    {
                        return resolvedMat;
                    }

                    if (renderer.assetsFile?.assetsManager != null)
                    {
                        var files = renderer.assetsFile.assetsManager.assetsFileList;
                        for (int i = 0; i < files.Count; i++)
                        {
                            if (files[i].ObjectsDic.TryGetValue(matPPtr.m_PathID, out var obj) && obj is Material foundMat)
                            {
                                return foundMat;
                            }
                        }
                    }
                }
            }

            if (fallbackMaterials != null && slotIndex >= 0 && slotIndex < fallbackMaterials.Count && fallbackMaterials[slotIndex] != null)
            {
                return fallbackMaterials[slotIndex];
            }

            return null;
        }

        public void CombineMeshes(string? combinedMeshName = null)
        {
            if (MeshList == null || MeshList.Count <= 1)
            {
                return;
            }

            ImportedFrame targetFrame = RootFrame;
            if (RootFrame != null && !string.IsNullOrWhiteSpace(combinedMeshName))
            {
                var normalizedRootName = RootFrame.Name ?? string.Empty;
                var normalizedRootPath = NormalizeFramePath(RootFrame.Path);
                var normalizedTarget = NormalizeFramePath(combinedMeshName);
                if (!string.Equals(normalizedRootName, combinedMeshName, StringComparison.Ordinal)
                    && !string.Equals(normalizedRootPath, normalizedTarget, StringComparison.Ordinal))
                {
                    var existing = FindFrameForMesh(RootFrame, combinedMeshName);
                    if (existing != null)
                    {
                        targetFrame = existing;
                    }
                    else
                    {
                        var childFrame = CreateFrame(combinedMeshName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
                        RootFrame.AddChild(childFrame);
                        targetFrame = childFrame;
                    }
                }
            }

            var combinedMesh = CombineMeshesInternal(
                MeshList,
                RootFrame,
                targetFrame,
                combinedMeshName,
                MorphList,
                AnimationList,
                out var combinedMorphs,
                out var updatedAnimations,
                cloneAnimationsForView: false);

            MeshList = new List<ImportedMesh> { combinedMesh };
            MorphList = combinedMorphs;
            AnimationList = updatedAnimations;
        }

        public static ImportedMesh CombineMeshList(IReadOnlyList<ImportedMesh>? meshes, ImportedFrame? rootFrame, string? combinedName = null)
        {
            if (meshes == null || meshes.Count == 0)
            {
                return new ImportedMesh
                {
                    Path = !string.IsNullOrWhiteSpace(combinedName)
                        ? combinedName
                        : !string.IsNullOrWhiteSpace(rootFrame?.Path)
                            ? rootFrame.Path
                            : "CombinedMesh",
                    VertexList = new List<ImportedVertex>(),
                    SubmeshList = new List<ImportedSubmesh>(),
                    hasUV = new bool[8]
                };
            }

            ImportedFrame? targetFrame = rootFrame;
            if (rootFrame != null && !string.IsNullOrWhiteSpace(combinedName))
            {
                var existing = FindFrameForMesh(rootFrame, combinedName);
                if (existing != null)
                {
                    targetFrame = existing;
                }
            }

            return CombineMeshesInternal(
                meshes,
                rootFrame,
                targetFrame,
                combinedName,
                morphList: null,
                animationList: null,
                out _,
                out _,
                cloneAnimationsForView: false);
        }

        [return: NotNullIfNotNull(nameof(imported))]
        public static IImported? CreateCombinedView(IImported? imported, string? combinedMeshName = null)
        {
            if (imported == null || imported.MeshList == null || imported.MeshList.Count <= 1)
            {
                return imported;
            }

            ImportedFrame targetFrame = imported.RootFrame;
            if (imported.RootFrame != null && !string.IsNullOrWhiteSpace(combinedMeshName))
            {
                var existing = FindFrameForMesh(imported.RootFrame, combinedMeshName);
                if (existing != null)
                {
                    targetFrame = existing;
                }
            }

            var combinedMesh = CombineMeshesInternal(
                imported.MeshList,
                imported.RootFrame,
                targetFrame,
                combinedMeshName,
                imported.MorphList,
                imported.AnimationList,
                out var combinedMorphs,
                out var updatedAnimations,
                cloneAnimationsForView: true);

            return new ImportedWrapper(imported)
            {
                MeshList = new List<ImportedMesh> { combinedMesh },
                MorphList = combinedMorphs,
                AnimationList = updatedAnimations
            };
        }

        private readonly struct CombinedPartTransformMeta
        {
            public CombinedPartTransformMeta(
                string normalizedPath,
                int vertexOffset,
                int vertexCount,
                System.Numerics.Matrix4x4 relativeMatrix,
                System.Numerics.Matrix4x4 normalMatrix)
            {
                NormalizedPath = normalizedPath;
                VertexOffset = vertexOffset;
                VertexCount = vertexCount;
                RelativeMatrix = relativeMatrix;
                NormalMatrix = normalMatrix;
            }

            public string NormalizedPath { get; }
            public int VertexOffset { get; }
            public int VertexCount { get; }
            public System.Numerics.Matrix4x4 RelativeMatrix { get; }
            public System.Numerics.Matrix4x4 NormalMatrix { get; }
        }

        private static ImportedMesh CombineMeshesInternal(
            IReadOnlyList<ImportedMesh> meshes,
            ImportedFrame? rootFrame,
            ImportedFrame? targetFrame,
            string? combinedName,
            List<ImportedMorph>? morphList,
            List<ImportedKeyframedAnimation>? animationList,
            out List<ImportedMorph> combinedMorphs,
            out List<ImportedKeyframedAnimation> updatedAnimations,
            bool cloneAnimationsForView)
        {
            int totalVertices = 0;
            int totalSubmeshes = 0;
            bool anyNormal = false;
            bool anyTangent = false;
            bool anyColor = false;
            var anyUV = new bool[8];
            string? firstNonEmptyMeshPath = null;

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh == null) continue;

                if (firstNonEmptyMeshPath == null && !string.IsNullOrWhiteSpace(mesh.Path))
                {
                    firstNonEmptyMeshPath = mesh.Path;
                }

                totalVertices += mesh.VertexList?.Count ?? 0;
                totalSubmeshes += mesh.SubmeshList?.Count ?? 0;
                if (mesh.hasNormal) anyNormal = true;
                if (mesh.hasTangent) anyTangent = true;
                if (mesh.hasColor) anyColor = true;
                if (mesh.hasUV != null)
                {
                    int uvLen = Math.Min(8, mesh.hasUV.Length);
                    for (int u = 0; u < uvLen; u++)
                    {
                        if (mesh.hasUV[u]) anyUV[u] = true;
                    }
                }
            }

            string targetPath;
            if (targetFrame != null && !string.IsNullOrWhiteSpace(targetFrame.Path))
            {
                if (!string.IsNullOrWhiteSpace(combinedName)
                    && !string.Equals(targetFrame.Name, combinedName, StringComparison.Ordinal)
                    && !string.Equals(NormalizeFramePath(targetFrame.Path), NormalizeFramePath(combinedName), StringComparison.Ordinal))
                {
                    targetPath = combinedName;
                }
                else
                {
                    targetPath = targetFrame.Path;
                }
            }
            else if (!string.IsNullOrWhiteSpace(combinedName))
            {
                targetPath = combinedName;
            }
            else
            {
                targetPath = firstNonEmptyMeshPath ?? "CombinedMesh";
            }

            var unifiedBones = new List<ImportedBone>();
            var boneIndexByNormalizedPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var perMeshBoneRemaps = new int[meshes.Count][];
            var partMetas = new List<CombinedPartTransformMeta>(meshes.Count);
            var partRelMatrices = new System.Numerics.Matrix4x4[meshes.Count];
            var partNormalMatrices = new System.Numerics.Matrix4x4[meshes.Count];
            var partFlipWinding = new bool[meshes.Count];

            // Precompute relative transform matrices and unified bone table
            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh == null) continue;

                var meshFrame = FindFrameForMesh(rootFrame, mesh.Path);
                var relMatrix = ComputeRelativeFrameMatrix(meshFrame, targetFrame);
                var normalMatrix = BuildNormalMatrix(relMatrix, out var flipWinding);
                partRelMatrices[m] = relMatrix;
                partNormalMatrices[m] = normalMatrix;
                partFlipWinding[m] = flipWinding;

                if (mesh.BoneList != null && mesh.BoneList.Count > 0)
                {
                    var remap = new int[mesh.BoneList.Count];
                    for (int b = 0; b < mesh.BoneList.Count; b++)
                    {
                        var bone = mesh.BoneList[b];
                        var normBonePath = NormalizeFramePath(bone?.Path);
                        if (!string.IsNullOrEmpty(normBonePath) && boneIndexByNormalizedPath.TryGetValue(normBonePath, out var existingIdx))
                        {
                            remap[b] = existingIdx;
                        }
                        else
                        {
                            var newIdx = unifiedBones.Count;
                            var adjustedBindPose = bone != null
                                ? TransformBindPoseMatrix(bone.Matrix, relMatrix)
                                : default(Matrix4x4);
                            unifiedBones.Add(new ImportedBone
                            {
                                Path = bone?.Path,
                                Matrix = adjustedBindPose
                            });
                            if (!string.IsNullOrEmpty(normBonePath))
                            {
                                boneIndexByNormalizedPath[normBonePath] = newIdx;
                            }
                            remap[b] = newIdx;
                        }
                    }
                    perMeshBoneRemaps[m] = remap;
                }
            }

            bool hasAnySkinning = unifiedBones.Count > 0;
            var combinedVertices = new List<ImportedVertex>(totalVertices);
            var combinedSubmeshes = new List<ImportedSubmesh>(totalSubmeshes);

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh?.VertexList == null || mesh.VertexList.Count == 0)
                {
                    continue;
                }

                int vertexOffset = combinedVertices.Count;
                int srcVertCount = mesh.VertexList.Count;
                var relMatrix = partRelMatrices[m];
                var normalMatrix = partNormalMatrices[m];
                bool flipWinding = partFlipWinding[m];
                var boneRemap = perMeshBoneRemaps[m];
                bool meshHasSkinning = boneRemap != null && boneRemap.Length > 0;

                partMetas.Add(new CombinedPartTransformMeta(
                    NormalizeFramePath(mesh.Path),
                    vertexOffset,
                    srcVertCount,
                    relMatrix,
                    normalMatrix));

                for (int v = 0; v < srcVertCount; v++)
                {
                    var src = mesh.VertexList[v];
                    var dst = new ImportedVertex();

                    var transformedPos = System.Numerics.Vector3.Transform(
                        new System.Numerics.Vector3(src.Vertex.X, src.Vertex.Y, src.Vertex.Z),
                        relMatrix);
                    dst.Vertex = new Vector3(
                        SanitizeFloat(transformedPos.X, 0f),
                        SanitizeFloat(transformedPos.Y, 0f),
                        SanitizeFloat(transformedPos.Z, 0f));

                    if (anyNormal)
                    {
                        dst.Normal = mesh.hasNormal
                            ? TransformDirectionNormalized(src.Normal, normalMatrix)
                            : new Vector3(0f, 1f, 0f);
                    }

                    dst.UV = new float[8][];
                    for (int u = 0; u < 8; u++)
                    {
                        if (!anyUV[u]) continue;
                        if (mesh.hasUV != null && u < mesh.hasUV.Length && mesh.hasUV[u]
                            && src.UV != null && u < src.UV.Length && src.UV[u] != null && src.UV[u].Length >= 2)
                        {
                            dst.UV[u] = new[]
                            {
                                SanitizeFloat(src.UV[u][0], 0f),
                                SanitizeFloat(src.UV[u][1], 0f)
                            };
                        }
                        else
                        {
                            dst.UV[u] = new[] { 0f, 0f };
                        }
                    }

                    if (anyTangent)
                    {
                        if (mesh.hasTangent)
                        {
                            var transformedTangent = TransformDirectionNormalized(
                                new Vector3(src.Tangent.X, src.Tangent.Y, src.Tangent.Z),
                                normalMatrix);
                            dst.Tangent = new Vector4(
                                transformedTangent.X,
                                transformedTangent.Y,
                                transformedTangent.Z,
                                SanitizeFloat(src.Tangent.W, 1f));
                        }
                        else
                        {
                            dst.Tangent = new Vector4(1f, 0f, 0f, 1f);
                        }
                    }

                    if (anyColor)
                    {
                        dst.Color = mesh.hasColor
                            ? src.Color
                            : new Color(1f, 1f, 1f, 1f);
                    }

                    if (hasAnySkinning)
                    {
                        dst.BoneIndices = new int[4];
                        dst.Weights = new float[4];
                        if (meshHasSkinning && boneRemap != null && src.BoneIndices != null && src.Weights != null)
                        {
                            int count = Math.Min(4, Math.Min(src.BoneIndices.Length, src.Weights.Length));
                            for (int k = 0; k < count; k++)
                            {
                                int oldBoneIdx = src.BoneIndices[k];
                                float weight = SanitizeFloat(src.Weights[k], 0f);
                                if (oldBoneIdx >= 0 && oldBoneIdx < boneRemap.Length)
                                {
                                    dst.BoneIndices[k] = boneRemap[oldBoneIdx];
                                    dst.Weights[k] = weight;
                                }
                            }
                        }
                    }

                    combinedVertices.Add(dst);
                }

                if (mesh.SubmeshList != null)
                {
                    for (int s = 0; s < mesh.SubmeshList.Count; s++)
                    {
                        var srcSub = mesh.SubmeshList[s];
                        if (srcSub?.FaceList == null) continue;

                        var dstSub = new ImportedSubmesh
                        {
                            Material = srcSub.Material,
                            BaseVertex = 0,
                            FaceList = new List<ImportedFace>(srcSub.FaceList.Count)
                        };

                        for (int f = 0; f < srcSub.FaceList.Count; f++)
                        {
                            var face = srcSub.FaceList[f];
                            if (!TryResolveCombinedFaceIndices(face, srcSub.BaseVertex, srcVertCount, out int i0, out int i1, out int i2))
                            {
                                continue;
                            }

                            var newFace = new ImportedFace
                            {
                                VertexIndices = flipWinding
                                    ? new[] { i0 + vertexOffset, i2 + vertexOffset, i1 + vertexOffset }
                                    : new[] { i0 + vertexOffset, i1 + vertexOffset, i2 + vertexOffset }
                            };
                            dstSub.FaceList.Add(newFace);
                        }

                        combinedSubmeshes.Add(dstSub);
                    }
                }
            }

            var combinedMesh = new ImportedMesh
            {
                Path = targetPath,
                VertexList = combinedVertices,
                SubmeshList = combinedSubmeshes,
                BoneList = hasAnySkinning ? unifiedBones : null,
                hasNormal = anyNormal,
                hasUV = anyUV,
                hasTangent = anyTangent,
                hasColor = anyColor
            };

            combinedMorphs = MergeMorphsForCombinedMesh(morphList, partMetas, targetPath);
            updatedAnimations = RemapBlendShapeAnimationTracks(animationList, partMetas, targetPath, cloneAnimationsForView);

            return combinedMesh;
        }

        private static List<ImportedMorph> MergeMorphsForCombinedMesh(
            List<ImportedMorph>? morphList,
            List<CombinedPartTransformMeta> partMetas,
            string combinedMeshPath)
        {
            if (morphList == null || morphList.Count == 0)
            {
                return morphList ?? new List<ImportedMorph>();
            }

            var metaByPath = new Dictionary<string, List<CombinedPartTransformMeta>>(StringComparer.Ordinal);
            for (int i = 0; i < partMetas.Count; i++)
            {
                var key = partMetas[i].NormalizedPath ?? string.Empty;
                if (!metaByPath.TryGetValue(key, out var list))
                {
                    list = new List<CombinedPartTransformMeta>(1);
                    metaByPath[key] = list;
                }
                list.Add(partMetas[i]);
            }

            var consumedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var remainingMorphs = new List<ImportedMorph>();
            var mergedChannels = new List<ImportedMorphChannel>();
            var channelByName = new Dictionary<string, ImportedMorphChannel>(StringComparer.Ordinal);

            for (int i = 0; i < morphList.Count; i++)
            {
                var morph = morphList[i];
                if (morph == null) continue;

                var normPath = NormalizeFramePath(morph.Path) ?? string.Empty;
                if (!metaByPath.TryGetValue(normPath, out var candidates) || candidates.Count == 0)
                {
                    remainingMorphs.Add(morph);
                    continue;
                }

                consumedCounts.TryGetValue(normPath, out var useIndex);
                var meta = candidates[Math.Min(useIndex, candidates.Count - 1)];
                consumedCounts[normPath] = useIndex + 1;

                if (morph.Channels == null) continue;

                for (int c = 0; c < morph.Channels.Count; c++)
                {
                    var srcChannel = morph.Channels[c];
                    if (srcChannel == null) continue;

                    var channelName = srcChannel.Name ?? string.Empty;
                    if (!channelByName.TryGetValue(channelName, out var dstChannel))
                    {
                        dstChannel = new ImportedMorphChannel
                        {
                            Name = channelName,
                            KeyframeList = new List<ImportedMorphKeyframe>()
                        };
                        channelByName[channelName] = dstChannel;
                        mergedChannels.Add(dstChannel);
                    }

                    if (srcChannel.KeyframeList == null) continue;

                    for (int k = 0; k < srcChannel.KeyframeList.Count; k++)
                    {
                        var srcKf = srcChannel.KeyframeList[k];
                        if (srcKf == null) continue;

                        ImportedMorphKeyframe? dstKf = null;
                        for (int ek = 0; ek < dstChannel.KeyframeList.Count; ek++)
                        {
                            if (Math.Abs(dstChannel.KeyframeList[ek].Weight - srcKf.Weight) <= 1e-4f)
                            {
                                dstKf = dstChannel.KeyframeList[ek];
                                break;
                            }
                        }

                        if (dstKf == null)
                        {
                            dstKf = new ImportedMorphKeyframe
                            {
                                Weight = srcKf.Weight,
                                hasNormals = srcKf.hasNormals,
                                hasTangents = srcKf.hasTangents,
                                VertexList = new List<ImportedMorphVertex>(srcKf.VertexList?.Count ?? 0)
                            };
                            dstChannel.KeyframeList.Add(dstKf);
                        }
                        else
                        {
                            dstKf.hasNormals |= srcKf.hasNormals;
                            dstKf.hasTangents |= srcKf.hasTangents;
                        }

                        if (srcKf.VertexList == null) continue;

                        for (int v = 0; v < srcKf.VertexList.Count; v++)
                        {
                            var mv = srcKf.VertexList[v];
                            if (mv?.Vertex == null || mv.Index >= (uint)meta.VertexCount)
                            {
                                continue;
                            }

                            var transformedPos = System.Numerics.Vector3.Transform(
                                new System.Numerics.Vector3(mv.Vertex.Vertex.X, mv.Vertex.Vertex.Y, mv.Vertex.Vertex.Z),
                                meta.RelativeMatrix);

                            var dstVert = new ImportedVertex
                            {
                                Vertex = new Vector3(
                                    SanitizeFloat(transformedPos.X, 0f),
                                    SanitizeFloat(transformedPos.Y, 0f),
                                    SanitizeFloat(transformedPos.Z, 0f))
                            };

                            if (srcKf.hasNormals)
                            {
                                var transformedNormal = System.Numerics.Vector3.TransformNormal(
                                    new System.Numerics.Vector3(mv.Vertex.Normal.X, mv.Vertex.Normal.Y, mv.Vertex.Normal.Z),
                                    meta.NormalMatrix);
                                dstVert.Normal = new Vector3(
                                    SanitizeFloat(transformedNormal.X, 0f),
                                    SanitizeFloat(transformedNormal.Y, 0f),
                                    SanitizeFloat(transformedNormal.Z, 0f));
                            }

                            if (srcKf.hasTangents)
                            {
                                var transformedTangent = System.Numerics.Vector3.TransformNormal(
                                    new System.Numerics.Vector3(mv.Vertex.Tangent.X, mv.Vertex.Tangent.Y, mv.Vertex.Tangent.Z),
                                    meta.NormalMatrix);
                                dstVert.Tangent = new Vector4(
                                    SanitizeFloat(transformedTangent.X, 0f),
                                    SanitizeFloat(transformedTangent.Y, 0f),
                                    SanitizeFloat(transformedTangent.Z, 0f),
                                    SanitizeFloat(mv.Vertex.Tangent.W, 0f));
                            }

                            dstKf.VertexList.Add(new ImportedMorphVertex
                            {
                                Index = mv.Index + (uint)meta.VertexOffset,
                                Vertex = dstVert
                            });
                        }
                    }
                }
            }

            if (mergedChannels.Count > 0)
            {
                remainingMorphs.Add(new ImportedMorph
                {
                    Path = combinedMeshPath,
                    Channels = mergedChannels
                });
            }

            return remainingMorphs;
        }

        private static List<ImportedKeyframedAnimation> RemapBlendShapeAnimationTracks(
            List<ImportedKeyframedAnimation>? animationList,
            List<CombinedPartTransformMeta> partMetas,
            string combinedMeshPath,
            bool cloneForView)
        {
            if (animationList == null || animationList.Count == 0 || string.IsNullOrEmpty(combinedMeshPath))
            {
                return animationList ?? new List<ImportedKeyframedAnimation>();
            }

            var combinedNormalizedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < partMetas.Count; i++)
            {
                if (!string.IsNullOrEmpty(partMetas[i].NormalizedPath))
                {
                    combinedNormalizedPaths.Add(partMetas[i].NormalizedPath);
                }
            }

            var normalizedTargetPath = NormalizeFramePath(combinedMeshPath);
            bool anyRemapNeeded = false;
            for (int a = 0; a < animationList.Count && !anyRemapNeeded; a++)
            {
                var tracks = animationList[a]?.TrackList;
                if (tracks == null) continue;
                for (int t = 0; t < tracks.Count; t++)
                {
                    var tr = tracks[t];
                    if (tr?.BlendShape != null)
                    {
                        var normTrackPath = NormalizeFramePath(tr.Path);
                        if (!string.IsNullOrEmpty(normTrackPath)
                            && combinedNormalizedPaths.Contains(normTrackPath)
                            && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                        {
                            anyRemapNeeded = true;
                            break;
                        }
                    }
                }
            }

            if (!anyRemapNeeded)
            {
                return animationList;
            }

            var result = cloneForView ? new List<ImportedKeyframedAnimation>(animationList.Count) : animationList;
            for (int a = 0; a < animationList.Count; a++)
            {
                var srcAnim = animationList[a];
                if (srcAnim == null)
                {
                    continue;
                }

                var dstAnim = srcAnim;
                if (cloneForView)
                {
                    dstAnim = new ImportedKeyframedAnimation
                    {
                        Name = srcAnim.Name,
                        SampleRate = srcAnim.SampleRate,
                        TrackList = new List<ImportedAnimationKeyframedTrack>(srcAnim.TrackList?.Count ?? 0)
                    };
                    if (srcAnim.TrackList != null)
                    {
                        for (int t = 0; t < srcAnim.TrackList.Count; t++)
                        {
                            var tr = srcAnim.TrackList[t];
                            dstAnim.TrackList.Add(new ImportedAnimationKeyframedTrack
                            {
                                Path = tr.Path,
                                Translations = tr.Translations,
                                Rotations = tr.Rotations,
                                Scalings = tr.Scalings,
                                BlendShape = tr.BlendShape
                            });
                        }
                    }
                    result.Add(dstAnim);
                }

                if (dstAnim.TrackList == null) continue;

                int trackCount = dstAnim.TrackList.Count;
                for (int t = 0; t < trackCount; t++)
                {
                    var tr = dstAnim.TrackList[t];
                    if (tr?.BlendShape == null) continue;

                    var normTrackPath = NormalizeFramePath(tr.Path);
                    if (!string.IsNullOrEmpty(normTrackPath)
                        && combinedNormalizedPaths.Contains(normTrackPath)
                        && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                    {
                        var targetTrack = dstAnim.FindTrack(combinedMeshPath);
                        if (targetTrack.BlendShape == null)
                        {
                            targetTrack.BlendShape = tr.BlendShape;
                        }
                        tr.BlendShape = null;
                    }
                }
            }

            return result;
        }

        private static bool TryResolveCombinedFaceIndices(ImportedFace face, int baseVertex, int vertexCount, out int index0, out int index1, out int index2)
        {
            index0 = index1 = index2 = 0;
            if (face?.VertexIndices == null || face.VertexIndices.Length < 3)
                return false;

            index0 = face.VertexIndices[0] + baseVertex;
            index1 = face.VertexIndices[1] + baseVertex;
            index2 = face.VertexIndices[2] + baseVertex;
            if ((uint)index0 < (uint)vertexCount && (uint)index1 < (uint)vertexCount && (uint)index2 < (uint)vertexCount)
                return true;

            index0 = face.VertexIndices[0];
            index1 = face.VertexIndices[1];
            index2 = face.VertexIndices[2];
            return (uint)index0 < (uint)vertexCount && (uint)index1 < (uint)vertexCount && (uint)index2 < (uint)vertexCount;
        }

        private static ImportedFrame? FindFrameForMesh(ImportedFrame? rootFrame, string? meshPath)
        {
            if (rootFrame == null || string.IsNullOrWhiteSpace(meshPath))
            {
                return null;
            }

            var normalizedTarget = NormalizeFramePath(meshPath);
            if (string.IsNullOrEmpty(normalizedTarget))
            {
                return null;
            }

            var visited = new HashSet<ImportedFrame>();
            var stack = new Stack<ImportedFrame>();
            stack.Push(rootFrame);

            ImportedFrame? uniqueSuffixMatch = null;
            int suffixMatchCount = 0;
            var suffix = "/" + normalizedTarget;

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame == null || !visited.Add(frame))
                {
                    continue;
                }

                var framePath = NormalizeFramePath(frame.Path);
                if (string.Equals(framePath, normalizedTarget, StringComparison.Ordinal))
                {
                    return frame;
                }

                if (framePath != null && framePath.EndsWith(suffix, StringComparison.Ordinal))
                {
                    uniqueSuffixMatch = frame;
                    suffixMatchCount++;
                }

                for (int i = frame.Count - 1; i >= 0; i--)
                {
                    stack.Push(frame[i]);
                }
            }

            return suffixMatchCount == 1 ? uniqueSuffixMatch : null;
        }

        private static System.Numerics.Matrix4x4 ComputeRelativeFrameMatrix(ImportedFrame? meshFrame, ImportedFrame? targetFrame)
        {
            if (meshFrame == null || ReferenceEquals(meshFrame, targetFrame))
            {
                return System.Numerics.Matrix4x4.Identity;
            }

            // Fast path: walk up parent chain looking for targetFrame (cycle-safe)
            var rel = System.Numerics.Matrix4x4.Identity;
            var visited = new HashSet<ImportedFrame>();
            var current = meshFrame;
            while (current != null && visited.Add(current))
            {
                rel = rel * BuildImportedFrameLocalMatrix(current);
                if (ReferenceEquals(current.Parent, targetFrame))
                {
                    return rel;
                }
                current = current.Parent;
            }

            // Fallback when targetFrame is not a direct ancestor of meshFrame
            if (targetFrame == null)
            {
                return rel;
            }

            var targetGlobal = ComputeFrameGlobalMatrix(targetFrame);
            return System.Numerics.Matrix4x4.Invert(targetGlobal, out var invTarget)
                ? rel * invTarget
                : rel;
        }

        private static System.Numerics.Matrix4x4 ComputeFrameGlobalMatrix(ImportedFrame frame)
        {
            var global = System.Numerics.Matrix4x4.Identity;
            var visited = new HashSet<ImportedFrame>();
            var current = frame;
            while (current != null && visited.Add(current))
            {
                global = global * BuildImportedFrameLocalMatrix(current);
                current = current.Parent;
            }
            return global;
        }

        private static System.Numerics.Matrix4x4 BuildImportedFrameLocalMatrix(ImportedFrame frame)
        {
            var scale = System.Numerics.Matrix4x4.CreateScale(
                SanitizeFloat(frame.LocalScale.X, 0f),
                SanitizeFloat(frame.LocalScale.Y, 0f),
                SanitizeFloat(frame.LocalScale.Z, 0f));
            var rotation =
                System.Numerics.Matrix4x4.CreateRotationX(DegreesToRadians(frame.LocalRotation.X)) *
                System.Numerics.Matrix4x4.CreateRotationY(DegreesToRadians(frame.LocalRotation.Y)) *
                System.Numerics.Matrix4x4.CreateRotationZ(DegreesToRadians(frame.LocalRotation.Z));
            var translation = System.Numerics.Matrix4x4.CreateTranslation(
                SanitizeFloat(frame.LocalPosition.X, 0f),
                SanitizeFloat(frame.LocalPosition.Y, 0f),
                SanitizeFloat(frame.LocalPosition.Z, 0f));
            return scale * rotation * translation;
        }

        private static System.Numerics.Matrix4x4 BuildNormalMatrix(System.Numerics.Matrix4x4 relMatrix, out bool flipWinding)
        {
            var upper = new System.Numerics.Matrix4x4(
                relMatrix.M11, relMatrix.M12, relMatrix.M13, 0f,
                relMatrix.M21, relMatrix.M22, relMatrix.M23, 0f,
                relMatrix.M31, relMatrix.M32, relMatrix.M33, 0f,
                0f, 0f, 0f, 1f);

            var det = upper.GetDeterminant();
            flipWinding = det < -1e-9f;
            if (Math.Abs(det) > 1e-12f && System.Numerics.Matrix4x4.Invert(upper, out var invUpper))
            {
                return System.Numerics.Matrix4x4.Transpose(invUpper);
            }

            return upper;
        }

        private static Vector3 TransformDirectionNormalized(Vector3 dir, System.Numerics.Matrix4x4 normalMatrix)
        {
            var transformed = System.Numerics.Vector3.TransformNormal(
                new System.Numerics.Vector3(
                    SanitizeFloat(dir.X, 0f),
                    SanitizeFloat(dir.Y, 0f),
                    SanitizeFloat(dir.Z, 0f)),
                normalMatrix);
            var lenSq = transformed.LengthSquared();
            if (lenSq > 1e-12f && !float.IsNaN(lenSq) && !float.IsInfinity(lenSq))
            {
                var invLen = 1f / (float)Math.Sqrt(lenSq);
                return new Vector3(transformed.X * invLen, transformed.Y * invLen, transformed.Z * invLen);
            }

            var origLenSq = dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z;
            if (origLenSq > 1e-12f && !float.IsNaN(origLenSq) && !float.IsInfinity(origLenSq))
            {
                var invLen = 1f / (float)Math.Sqrt(origLenSq);
                return new Vector3(dir.X * invLen, dir.Y * invLen, dir.Z * invLen);
            }

            return Vector3.Zero;
        }

        private static Matrix4x4 TransformBindPoseMatrix(Matrix4x4 bindPose, System.Numerics.Matrix4x4 relMatrix)
        {
            if (relMatrix.IsIdentity)
            {
                return bindPose;
            }

            if (System.Numerics.Matrix4x4.Invert(relMatrix, out var invRel))
            {
                var adjusted = invRel * ToNumericsMatrix(bindPose);
                return FromNumericsMatrix(adjusted);
            }

            return bindPose;
        }

        private static System.Numerics.Matrix4x4 ToNumericsMatrix(Matrix4x4 m)
        {
            return new System.Numerics.Matrix4x4(
                m[0, 0], m[0, 1], m[0, 2], m[0, 3],
                m[1, 0], m[1, 1], m[1, 2], m[1, 3],
                m[2, 0], m[2, 1], m[2, 2], m[2, 3],
                m[3, 0], m[3, 1], m[3, 2], m[3, 3]);
        }

        private static Matrix4x4 FromNumericsMatrix(System.Numerics.Matrix4x4 m)
        {
            var result = new Matrix4x4();
            result[0, 0] = SanitizeFloat(m.M11, 0f); result[0, 1] = SanitizeFloat(m.M12, 0f); result[0, 2] = SanitizeFloat(m.M13, 0f); result[0, 3] = SanitizeFloat(m.M14, 0f);
            result[1, 0] = SanitizeFloat(m.M21, 0f); result[1, 1] = SanitizeFloat(m.M22, 0f); result[1, 2] = SanitizeFloat(m.M23, 0f); result[1, 3] = SanitizeFloat(m.M24, 0f);
            result[2, 0] = SanitizeFloat(m.M31, 0f); result[2, 1] = SanitizeFloat(m.M32, 0f); result[2, 2] = SanitizeFloat(m.M33, 0f); result[2, 3] = SanitizeFloat(m.M34, 0f);
            result[3, 0] = SanitizeFloat(m.M41, 0f); result[3, 1] = SanitizeFloat(m.M42, 0f); result[3, 2] = SanitizeFloat(m.M43, 0f); result[3, 3] = SanitizeFloat(m.M44, 0f);
            return result;
        }

        private static float DegreesToRadians(float degrees)
        {
            return (float)(SanitizeFloat(degrees, 0f) * Math.PI / 180.0);
        }

        private static float SanitizeFloat(float value, float fallback)
        {
            return (float.IsNaN(value) || float.IsInfinity(value)) ? fallback : value;
        }

        private static string NormalizeFramePath(string? path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').Trim('/');
        }
    }
}
