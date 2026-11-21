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

        public Avatar? SourceAvatar => avatar;

        private ImageFormat imageFormat;
        private Avatar? avatar;
        private bool hasRootFrame;
        private string? animatorRootPath;
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
                        foreach (var sf in tempTransform.assetsFile.assetsManager.assetsFileList)
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

            if (m_Transform != null)
            {
                animatorRootPath = GetTransformPath(m_Transform) ?? RootFrame?.Path;
            }
            ConvertMeshRenderer(m_Transform);
        }

        private void ConvertMeshRenderer(Transform? m_Transform)
        {
            ConvertMeshRenderer(m_Transform, new HashSet<Transform>());
        }

        private void ConvertMeshRenderer(Transform? m_Transform, HashSet<Transform> visited)
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
            double cosY = Math.Sqrt((double)m11 * m11 + (double)m12 * m12);
            float rotY = (float)Math.Atan2(sinY, cosY);

            float rotX, rotZ;
            if (Math.Abs(sinY) < 0.99999f)
            {
                rotX = (float)Math.Atan2(m23, m33);
                rotZ = (float)Math.Atan2(m12, m11);
            }
            else
            {
                rotY = sinY >= 0f ? (float)(Math.PI * 0.5) : -(float)(Math.PI * 0.5);
                float m32 = 2f * (qy * qz - qx * qw);
                float m22 = 1f - 2f * (qx * qx + qz * qz);
                rotX = (float)Math.Atan2(-m32, m22);
                rotZ = 0f;
            }

            const float rad2deg = 180f / (float)Math.PI;
            return new Vector3(rotX * rad2deg, rotY * rad2deg, rotZ * rad2deg);
        }

        private void ConvertTransforms(Transform? trans, ImportedFrame? parent)
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
            iMesh.Path = (m_GameObject2 != null ? GetTransformPath(m_GameObject2.m_Transform) : null) ?? "";
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
            int vertexCount = (int)mesh.m_VertexCount;
            var uvArrays = new float[8][];
            var uvStrides = new int[8];
            for (int uv = 0; uv < 8; uv++)
            {
                if (iMesh.hasUV[uv])
                {
                    var uvData = mesh.GetUV(uv);
                    if (uvData != null && vertexCount > 0 && uvData.Length >= vertexCount)
                    {
                        uvArrays[uv] = uvData;
                        uvStrides[uv] = uvData.Length / vertexCount;
                    }
                }
            }

            iMesh.VertexList = new List<ImportedVertex>(vertexCount);
            for (var j = 0; j < vertexCount; j++)
            {
                if (mesh.m_Vertices == null)
                {
                    break;
                }
                var iVertex = new ImportedVertex();
                //Vertices
                int posStride = mesh.m_Vertices.Length == vertexCount * 4 ? 4 : 3;
                iVertex.Vertex = new Vector3(-mesh.m_Vertices[j * posStride], mesh.m_Vertices[j * posStride + 1], mesh.m_Vertices[j * posStride + 2]);
                //Normals
                if (iMesh.hasNormal && mesh.m_Normals != null)
                {
                    int normStride = mesh.m_Normals.Length == vertexCount * 4 ? 4 : 3;
                    iVertex.Normal = new Vector3(-mesh.m_Normals[j * normStride], mesh.m_Normals[j * normStride + 1], mesh.m_Normals[j * normStride + 2]);
                }
                //UV
                iVertex.UV = new float[8][];
                for (int uv = 0; uv < 8; uv++)
                {
                    int uvStride = uvStrides[uv];
                    if (uvStride > 0)
                    {
                        var m_UV = uvArrays[uv];
                        int uvOffset = j * uvStride;
                        if (uvStride == 1)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], 0f };
                        }
                        else if (uvOffset + 1 < m_UV.Length)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], m_UV[uvOffset + 1] };
                        }
                    }
                }
                //Tangent
                if (iMesh.hasTangent && mesh.m_Tangents != null)
                {
                    iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], -mesh.m_Tangents[j * 4 + 3]);
                }
                //Colors
                if (iMesh.hasColor && mesh.m_Colors != null)
                {
                    if (mesh.m_Colors.Length == vertexCount * 3)
                    {
                        iVertex.Color = new Color(mesh.m_Colors[j * 3], mesh.m_Colors[j * 3 + 1], mesh.m_Colors[j * 3 + 2], 1.0f);
                    }
                    else
                    {
                        iVertex.Color = new Color(mesh.m_Colors[j * 4], mesh.m_Colors[j * 4 + 1], mesh.m_Colors[j * 4 + 2], mesh.m_Colors[j * 4 + 3]);
                    }
                }
                //BoneInfluence
                if (mesh.m_Skin != null && j < mesh.m_Skin.Length && mesh.m_Skin[j]?.boneIndex != null && mesh.m_Skin[j]?.weight != null)
                {
                    var inf = mesh.m_Skin[j];
                    int rawSlots = Math.Min(inf.boneIndex.Length, inf.weight.Length);
                    int slotCount = Math.Max(4, rawSlots);
                    iVertex.BoneIndices = new int[slotCount];
                    iVertex.Weights = new float[slotCount];
                    for (var k = 0; k < rawSlots; k++)
                    {
                        iVertex.BoneIndices[k] = inf.boneIndex[k];
                        iVertex.Weights[k] = inf.weight[k];
                    }
                }
                iMesh.VertexList.Add(iVertex);
            }

            if (meshR is SkinnedMeshRenderer sMesh)
            {
                EnsureBoneTransformsInHierarchy(sMesh);
                //Bone
                /*
                 * 0 - None
                 * 1 - m_Bones
                 * 2 - m_BoneNameHashes
                 */
                var boneType = 0;
                if (sMesh.m_Bones.Length > 0 && mesh.m_BindPose != null)
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
                if (boneType == 0 && mesh.m_BindPose != null)
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

                if (boneType == 1 && mesh.m_BindPose != null)
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
                else if (boneType == 2 && mesh.m_BoneNameHashes != null && mesh.m_BindPose != null)
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

                        var cleanChannelName = NormalizeBlendShapeChannelName(shapeChannel.name);
                        RegisterMorphChannelHashes(shapeChannel, cleanChannelName);

                        channel.Name = cleanChannelName;
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

            // Un-bake world-space vertices for Unity static-batched meshes into the mesh frame's local space
            // so shared parent/sibling frame transforms are NEVER mutated.
            if (combine)
            {
                UnbakeStaticBatchedMeshToLocalSpace(iMesh);
            }

            MeshList.Add(iMesh);
        }

        private void UnbakeStaticBatchedMeshToLocalSpace(ImportedMesh iMesh)
        {
            if (RootFrame == null || iMesh.VertexList == null || iMesh.VertexList.Count == 0)
            {
                return;
            }

            var meshFrame = FindFrameForMesh(RootFrame, iMesh.Path);
            if (meshFrame == null)
            {
                return;
            }

            var globalMatrix = ComputeFrameGlobalMatrix(meshFrame);
            if (globalMatrix.IsIdentity || !System.Numerics.Matrix4x4.Invert(globalMatrix, out var invGlobal))
            {
                return;
            }

            var normalMatrix = BuildNormalMatrix(invGlobal, out var flipWinding);
            for (int v = 0; v < iMesh.VertexList.Count; v++)
            {
                var vert = iMesh.VertexList[v];
                var localPos = System.Numerics.Vector3.Transform(
                    new System.Numerics.Vector3(vert.Vertex.X, vert.Vertex.Y, vert.Vertex.Z),
                    invGlobal);
                vert.Vertex = new Vector3(
                    SanitizeFloat(localPos.X, 0f),
                    SanitizeFloat(localPos.Y, 0f),
                    SanitizeFloat(localPos.Z, 0f));

                if (iMesh.hasNormal)
                {
                    vert.Normal = TransformDirectionNormalized(vert.Normal, normalMatrix);
                }

                if (iMesh.hasTangent)
                {
                    var localTan = TransformDirectionNormalized(
                        new Vector3(vert.Tangent.X, vert.Tangent.Y, vert.Tangent.Z),
                        invGlobal);
                    float w = SanitizeFloat(vert.Tangent.W, 1f);
                    vert.Tangent = new Vector4(localTan.X, localTan.Y, localTan.Z, flipWinding ? -w : w);
                }
            }

            if (flipWinding && iMesh.SubmeshList != null)
            {
                for (int s = 0; s < iMesh.SubmeshList.Count; s++)
                {
                    var faceList = iMesh.SubmeshList[s]?.FaceList;
                    if (faceList == null) continue;
                    for (int f = 0; f < faceList.Count; f++)
                    {
                        var indices = faceList[f]?.VertexIndices;
                        if (indices != null && indices.Length >= 3)
                        {
                            (indices[1], indices[2]) = (indices[2], indices[1]);
                        }
                    }
                }
            }
        }

        private static string NormalizeBlendShapeChannelName(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            const string prefix = "blendShape.";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(prefix.Length);
            }

            return name;
        }

        private void RegisterMorphChannelHashes(MeshBlendShapeChannel shapeChannel, string cleanChannelName)
        {
            if (shapeChannel.nameHash != 0)
            {
                morphChannelNames[shapeChannel.nameHash] = cleanChannelName;
            }

            if (!string.IsNullOrEmpty(shapeChannel.name))
            {
                morphChannelNames[ComputeCrc32(shapeChannel.name)] = cleanChannelName;
                morphChannelNames[ComputeCrc32("blendShape." + shapeChannel.name)] = cleanChannelName;
            }

            if (!string.IsNullOrEmpty(cleanChannelName))
            {
                morphChannelNames[ComputeCrc32(cleanChannelName)] = cleanChannelName;
                morphChannelNames[ComputeCrc32("blendShape." + cleanChannelName)] = cleanChannelName;
            }
        }

        private static uint ComputeCrc32(string value)
        {
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(value);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
        }

        private static bool TryGetBoneTransform(PPtr<Transform> bonePtr, Renderer meshR, [NotNullWhen(true)] out Transform? transform)
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

        private string? GetTransformPath(Transform? transform)
        {
            if (transform != null && transformDictionary.TryGetValue(transform, out var frame))
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
            ImportedMaterial? iMat;
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
                    ConvertTexture2D(m_Texture2D, texture.Name, mat.m_Name, texEnv.Key, forceNormalMapUnpack: texture.Dest == 1);
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

        private void ConvertTexture2D(Texture2D m_Texture2D, string name, string materialName, string propertyName, bool forceNormalMapUnpack = false)
        {
            var iTex = ImportedHelpers.FindTexture(name, TextureList);
            if (iTex != null)
            {
                return;
            }

            var stream = m_Texture2D.ConvertToStream(imageFormat, true, unpackNormalMap: true, forceNormalMapUnpack: forceNormalMapUnpack);
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
                case "_Albedo":
                case "_AlbedoMap":
                case "_Diffuse":
                case "_DiffuseMap":
                case "_ColorMap":
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
                case "_EmissiveMap":
                    return 4;
                case "_OcclusionMap":
                case "_AOMap":
                    return 5;
                case "_ParallaxMap":
                case "_HeightMap":
                    return 6;
                case "_DetailAlbedoMap":
                case "_DetailMap":
                case "_DetailMask":
                case "_MaskMap":
                    return 7;
                case "_RoughnessMap":
                case "_SmoothnessMap":
                case "_GlossMap":
                    return 8;
                case "_AlphaMap":
                case "_OpacityMap":
                case "_TransparencyMap":
                    return 9;
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
            if (propertyName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0)
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
            if (propertyName.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Detail", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 7;
            }
            if (propertyName.IndexOf("Rough", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Smooth", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Gloss", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 8;
            }
            if (propertyName.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Opacity", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Transparen", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 9;
            }
            if (propertyName.IndexOf("Albedo", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("BaseColor", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("BaseMap", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("MainTex", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("Diffuse", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            return -1;
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
                    foreach (var m_CompressedRotationCurve in animationClip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
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
                    foreach (var m_RotationCurve in animationClip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_RotationCurve.path));
                        foreach (var m_Curve in m_RotationCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z) || float.IsInfinity(m_Curve.outSlope.W);
                            var value = QuaternionToEuler(new Quaternion(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z, m_Curve.value.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, value, isStepped));
                        }
                    }
                    foreach (var m_PositionCurve in animationClip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_PositionCurve.path));
                        foreach (var m_Curve in m_PositionCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                            track.Translations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(-m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z), isStepped));
                        }
                    }
                    foreach (var m_ScaleCurve in animationClip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_ScaleCurve.path));
                        foreach (var m_Curve in m_ScaleCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z), isStepped));
                        }
                    }
                    if (animationClip.m_EulerCurves != null)
                    {
                        foreach (var m_EulerCurve in animationClip.m_EulerCurves)
                        {
                            var track = iAnim.FindTrack(FixBonePath(animationClip, m_EulerCurve.path));
                            int rotOrder = m_EulerCurve.curve.m_RotationOrder;
                            foreach (var m_Curve in m_EulerCurve.curve.m_Curve)
                            {
                                bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z, rotOrder);
                                var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, QuaternionToEuler(qF), isStepped));
                            }
                        }
                    }
                    foreach (var m_FloatCurve in animationClip.m_FloatCurves ?? Array.Empty<FloatCurve>())
                    {
                        if (m_FloatCurve.classID == ClassIDType.SkinnedMeshRenderer) //BlendShape
                        {
                            var channelName = NormalizeBlendShapeChannelName(m_FloatCurve.attribute);
                            var path = ResolveBlendShapeTrackPath(animationClip, m_FloatCurve.path, channelName);
                            var track = iAnim.FindTrack(path);
                            var blendShape = track.FindOrCreateBlendShape(channelName);
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                blendShape.Keyframes.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value, float.IsInfinity(m_Curve.outSlope)));
                            }
                        }
                        else if (m_FloatCurve.classID == ClassIDType.GameObject
                            && string.Equals(m_FloatCurve.attribute, "m_IsActive", StringComparison.Ordinal))
                        {
                            var path = FixBonePath(animationClip, m_FloatCurve.path);
                            var track = iAnim.FindTrack(path);
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                track.Visibilities.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                            }
                        }
                    }
                }
                else if (animationClip.m_MuscleClip?.m_Clip != null)
                {
                    var m_Clip = animationClip.m_MuscleClip.m_Clip;
                    var m_ClipBindingConstant = animationClip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();
                    float clipSampleRate = animationClip.m_SampleRate > 0f && float.IsFinite(animationClip.m_SampleRate)
                        ? animationClip.m_SampleRate
                        : 30f;

                    // 1. StreamedClip: evaluate per-binding via cubic Hermite evaluator (handles sparse partial frames,
                    //    skips sentinels 0 and Count-1, preserves stepped segments, and subdivides curved Hermite spans).
                    uint streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    if (m_Clip.m_StreamedClip != null && streamCount > 0 && m_Clip.m_StreamedClip.data != null && m_Clip.m_StreamedClip.data.Length > 0)
                    {
                        List<StreamedClip.StreamedFrame>? streamedFrames = null;
                        try
                        {
                            streamedFrames = m_Clip.m_StreamedClip.ReadData();
                        }
                        catch
                        {
                            streamedFrames = null;
                        }
                        if (streamedFrames != null && streamedFrames.Count >= 3)
                        {
                            var streamedEval = new HumanoidMuscleDecoder.StreamedCurveEvaluator(streamedFrames);
                            ReadStreamedClipCurves(iAnim, animationClip, m_ClipBindingConstant, streamedEval, (int)streamCount, clipSampleRate);
                        }
                    }

                    // 2. DenseClip (with sample-rate > 0 guard)
                    var m_DenseClip = m_Clip.m_DenseClip;
                    if (m_DenseClip != null && m_DenseClip.m_FrameCount > 0 && m_DenseClip.m_CurveCount > 0 && m_DenseClip.m_SampleArray != null)
                    {
                        float denseSampleRate = m_DenseClip.m_SampleRate > 0f && float.IsFinite(m_DenseClip.m_SampleRate)
                            ? m_DenseClip.m_SampleRate
                            : clipSampleRate;
                        float beginTime = float.IsFinite(m_DenseClip.m_BeginTime) ? m_DenseClip.m_BeginTime : 0f;
                        for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                        {
                            var time = beginTime + frameIndex / denseSampleRate;
                            if (time < 0f) time = 0f;
                            var frameOffset = frameIndex * (int)m_DenseClip.m_CurveCount;
                            for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                            {
                                var index = streamCount + curveIndex;
                                ReadCurveData(iAnim, animationClip, m_ClipBindingConstant, (int)index, time, m_DenseClip.m_SampleArray, frameOffset, ref curveIndex);
                            }
                        }
                    }

                    // 3. ConstantClip (honor m_StartTime, avoid duplicate t=0 key when m_StopTime <= m_StartTime)
                    if (m_Clip.m_ConstantClip?.data != null && m_Clip.m_ConstantClip.data.Length > 0)
                    {
                        var m_ConstantClip = m_Clip.m_ConstantClip;
                        var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                        float startTime = float.IsFinite(animationClip.m_MuscleClip.m_StartTime) && animationClip.m_MuscleClip.m_StartTime > 0f
                            ? animationClip.m_MuscleClip.m_StartTime
                            : 0f;
                        float stopTime = float.IsFinite(animationClip.m_MuscleClip.m_StopTime) && animationClip.m_MuscleClip.m_StopTime > startTime
                            ? animationClip.m_MuscleClip.m_StopTime
                            : startTime;
                        int passCount = stopTime > startTime + 1e-6f ? 2 : 1;
                        for (int i = 0; i < passCount; i++)
                        {
                            float time2 = i == 0 ? startTime : stopTime;
                            for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                            {
                                var index = streamCount + denseCount + curveIndex;
                                ReadCurveData(iAnim, animationClip, m_ClipBindingConstant, (int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                            }
                        }
                    }

                    // 4. Humanoid MuscleClip decoding
                    if (avatar != null)
                    {
                        ReadHumanoidMuscleCurves(iAnim, animationClip);
                    }
                }

                FinalizeAnimationTracks(iAnim);
            }
        }

        private void ReadStreamedClipCurves(
            ImportedKeyframedAnimation iAnim,
            AnimationClip animationClip,
            AnimationClipBindingConstant? m_ClipBindingConstant,
            HumanoidMuscleDecoder.StreamedCurveEvaluator evaluator,
            int streamCount,
            float clipSampleRate)
        {
            if (m_ClipBindingConstant?.genericBindings == null || !evaluator.HasAnyKeys || streamCount <= 0)
            {
                return;
            }

            float dtStep = 1f / Math.Clamp(clipSampleRate, 1f, 120f);
            var tempValues = new float[4];
            var bindingTimes = new List<float>();

            int slotCursor = 0;
            var bindings = m_ClipBindingConstant.genericBindings;
            for (int bIdx = 0; bIdx < bindings.Length; bIdx++)
            {
                var binding = bindings[bIdx];
                int width = GetGenericBindingCurveWidth(binding);
                int startSlot = slotCursor;
                slotCursor += width;

                if (startSlot >= streamCount)
                {
                    break;
                }

                bool isSupportedTransform = binding.typeID == ClassIDType.Transform && binding.attribute is 1 or 2 or 3 or 4;
                bool isBlendShape = binding.typeID == ClassIDType.SkinnedMeshRenderer;
                bool isVisibility = binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974;
                if (!isSupportedTransform && !isBlendShape && !isVisibility)
                {
                    continue;
                }

                // Collect union of key times across all component curves of this binding
                bindingTimes.Clear();
                int activeComponents = 0;
                for (int c = 0; c < width; c++)
                {
                    if (startSlot + c >= streamCount)
                    {
                        break;
                    }
                    if (evaluator.TryGetCurveKeys(startSlot + c, out var keys) && keys.Count > 0)
                    {
                        activeComponents++;
                        for (int k = 0; k < keys.Count; k++)
                        {
                            bindingTimes.Add(keys[k].Time);
                        }
                    }
                }

                if (activeComponents == 0 || bindingTimes.Count == 0)
                {
                    continue;
                }

                bindingTimes.Sort();
                int uniqueCount = 0;
                for (int i = 0; i < bindingTimes.Count; i++)
                {
                    if (uniqueCount == 0 || bindingTimes[i] - bindingTimes[uniqueCount - 1] > 1e-6f)
                    {
                        bindingTimes[uniqueCount++] = bindingTimes[i];
                    }
                }

                // Resolve target track & blendshape channel once per binding
                ImportedAnimationKeyframedTrack? track = null;
                ImportedBlendShape? blendShape = null;
                if (isBlendShape)
                {
                    var channelName = GetChannelNameFromHash(binding.attribute);
                    if (string.IsNullOrEmpty(channelName))
                    {
                        continue;
                    }
                    channelName = NormalizeBlendShapeChannelName(channelName);
                    var bPath = ResolveBlendShapeTrackPath(animationClip, GetPathFromHash(binding.path), channelName);
                    track = iAnim.FindTrack(bPath);
                    blendShape = track.FindOrCreateBlendShape(channelName);
                }
                else
                {
                    var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                    track = iAnim.FindTrack(path);
                }

                float curvatureTol = binding.typeID == ClassIDType.Transform && binding.attribute == 4 ? 0.05f : 1e-3f;

                void EmitSample(float sampleTime, bool stepped)
                {
                    for (int c = 0; c < width; c++)
                    {
                        float fallback = (binding.typeID == ClassIDType.Transform && binding.attribute == 3)
                            ? 1f
                            : (binding.typeID == ClassIDType.Transform && binding.attribute == 2 && c == 3 ? 1f : 0f);
                        tempValues[c] = evaluator.EvaluateOrDefault(startSlot + c, sampleTime, fallback);
                    }

                    if (isBlendShape && blendShape != null)
                    {
                        blendShape.Keyframes.Add(new ImportedKeyframe<float>(sampleTime, tempValues[0], stepped));
                    }
                    else if (isVisibility)
                    {
                        track.Visibilities.Add(new ImportedKeyframe<float>(sampleTime, tempValues[0] >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                    }
                    else if (isSupportedTransform)
                    {
                        switch (binding.attribute)
                        {
                            case 1:
                                track.Translations.Add(new ImportedKeyframe<Vector3>(
                                    sampleTime,
                                    new Vector3(-tempValues[0], tempValues[1], tempValues[2]),
                                    stepped));
                                break;
                            case 2:
                                var eulerFromQuat = QuaternionToEuler(new Quaternion(tempValues[0], -tempValues[1], -tempValues[2], tempValues[3]));
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(sampleTime, eulerFromQuat, stepped));
                                break;
                            case 3:
                                track.Scalings.Add(new ImportedKeyframe<Vector3>(
                                    sampleTime,
                                    new Vector3(tempValues[0], tempValues[1], tempValues[2]),
                                    stepped));
                                break;
                            case 4:
                                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(tempValues[0], tempValues[1], tempValues[2], 4);
                                var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(sampleTime, QuaternionToEuler(qF), stepped));
                                break;
                        }
                    }
                }

                for (int i = 0; i < uniqueCount; i++)
                {
                    float t0 = bindingTimes[i];
                    bool segStepped = isVisibility;
                    if (!segStepped)
                    {
                        segStepped = true;
                        for (int c = 0; c < width; c++)
                        {
                            if (evaluator.TryEvaluate(startSlot + c, t0, out _, out bool compStepped) && !compStepped)
                            {
                                segStepped = false;
                                break;
                            }
                        }
                    }

                    EmitSample(t0, segStepped);

                    // Adaptively subdivide non-stepped Hermite segments whose cubic midpoint diverges from linear
                    if (!segStepped && i + 1 < uniqueCount)
                    {
                        float t1 = bindingTimes[i + 1];
                        float span = t1 - t0;
                        if (span > dtStep * 1.25f)
                        {
                            float tMid = 0.5f * (t0 + t1);
                            bool needsSubdivision = false;
                            for (int c = 0; c < width; c++)
                            {
                                float v0 = evaluator.EvaluateOrDefault(startSlot + c, t0, 0f);
                                float v1 = evaluator.EvaluateOrDefault(startSlot + c, t1, 0f);
                                float vMid = evaluator.EvaluateOrDefault(startSlot + c, tMid, 0f);
                                if (MathF.Abs(vMid - 0.5f * (v0 + v1)) > curvatureTol)
                                {
                                    needsSubdivision = true;
                                    break;
                                }
                            }

                            if (needsSubdivision)
                            {
                                int subSteps = Math.Clamp((int)MathF.Ceiling(span / dtStep), 2, 120);
                                for (int s = 1; s < subSteps; s++)
                                {
                                    float ts = t0 + span * s / subSteps;
                                    EmitSample(ts, stepped: false);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static int GetGenericBindingCurveWidth(GenericBinding binding)
        {
            if (binding.typeID != ClassIDType.Transform)
            {
                return 1;
            }
            return binding.attribute switch
            {
                1 => 3,
                2 => 4,
                3 => 3,
                4 => 3,
                _ => 1
            };
        }

        private string? ResolveBlendShapeTrackPath(AnimationClip animationClip, string? rawPath, string channelName)
        {
            var resolved = FixBonePath(animationClip, rawPath);
            if (!string.IsNullOrEmpty(resolved) && HasMorphWithPath(resolved))
            {
                return resolved;
            }

            var byChannel = GetPathByChannelName(channelName);
            if (!string.IsNullOrEmpty(byChannel))
            {
                return byChannel;
            }

            return resolved;
        }

        private bool HasMorphWithPath(string path)
        {
            var norm = NormalizeFramePath(path);
            for (int i = 0; i < MorphList.Count; i++)
            {
                if (string.Equals(NormalizeFramePath(MorphList[i].Path), norm, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadCurveData(ImportedKeyframedAnimation iAnim, AnimationClip animationClip, AnimationClipBindingConstant m_ClipBindingConstant, int index, float time, float[] data, int offset, ref int curveIndex)
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
                channelName = NormalizeBlendShapeChannelName(channelName);

                var bPath = ResolveBlendShapeTrackPath(animationClip, GetPathFromHash(binding.path), channelName);
                var bTrack = iAnim.FindTrack(bPath);
                var blendShape = bTrack.FindOrCreateBlendShape(channelName);
                int idxVal = curveIndex++ + offset;
                if ((uint)idxVal < (uint)data.Length)
                {
                    blendShape.Keyframes.Add(new ImportedKeyframe<float>(time, data[idxVal]));
                }
            }
            else if (binding.typeID == ClassIDType.Transform)
            {
                var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);

                switch (binding.attribute)
                {
                    case 1:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            track.Translations.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                            (
                                -data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset]
                            )));
                        }
                        curveIndex += 3;
                        break;
                    case 2:
                        if ((uint)(curveIndex + 3 + offset) < (uint)data.Length)
                        {
                            var value = QuaternionToEuler(new Quaternion
                            (
                                data[curveIndex + offset],
                                -data[curveIndex + 1 + offset],
                                -data[curveIndex + 2 + offset],
                                data[curveIndex + 3 + offset]
                            ));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(time, value));
                        }
                        curveIndex += 4;
                        break;
                    case 3:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                            (
                                data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset]
                            )));
                        }
                        curveIndex += 3;
                        break;
                    case 4:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(
                                data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset],
                                rotationOrder: 4);
                            var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(time, QuaternionToEuler(qF)));
                        }
                        curveIndex += 3;
                        break;
                    default:
                        curveIndex++;
                        break;
                }
            }
            else if (binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974)
            {
                var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);
                int idxVal = curveIndex++ + offset;
                if ((uint)idxVal < (uint)data.Length)
                {
                    track.Visibilities.Add(new ImportedKeyframe<float>(time, data[idxVal] >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                }
            }
            else
            {
                curveIndex++;
            }
        }

        private static void FinalizeAnimationTracks(ImportedKeyframedAnimation iAnim)
        {
            if (iAnim.TrackList == null)
            {
                return;
            }

            for (int i = 0; i < iAnim.TrackList.Count; i++)
            {
                var track = iAnim.TrackList[i];
                SortAndDeduplicateVector3Keyframes(track.Translations);
                SortAndDeduplicateVector3Keyframes(track.Rotations);
                SortAndDeduplicateVector3Keyframes(track.Scalings);
                SortAndDeduplicateFloatKeyframes(track.Visibilities);

                if (track.BlendShapes != null)
                {
                    for (int b = 0; b < track.BlendShapes.Count; b++)
                    {
                        SortAndDeduplicateFloatKeyframes(track.BlendShapes[b].Keyframes);
                    }
                }

                HumanoidMuscleDecoder.UnwrapEulerKeyframes(track.Rotations);
            }
        }

        private static void SortAndDeduplicateVector3Keyframes(List<ImportedKeyframe<Vector3>> keyframes)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return;
            }

            keyframes.RemoveAll(k => !float.IsFinite(k.time) || !float.IsFinite(k.value.X) || !float.IsFinite(k.value.Y) || !float.IsFinite(k.value.Z));
            if (keyframes.Count <= 1)
            {
                if (keyframes.Count == 1 && keyframes[0].time < 0f)
                {
                    keyframes[0].time = 0f;
                }
                return;
            }

            keyframes.Sort((a, b) => a.time.CompareTo(b.time));
            int write = 0;
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k.time < 0f) k.time = 0f;
                if (write > 0 && MathF.Abs(k.time - keyframes[write - 1].time) <= 1e-6f)
                {
                    keyframes[write - 1] = k;
                }
                else
                {
                    keyframes[write++] = k;
                }
            }
            if (write < keyframes.Count)
            {
                keyframes.RemoveRange(write, keyframes.Count - write);
            }
        }

        private static void SortAndDeduplicateFloatKeyframes(List<ImportedKeyframe<float>> keyframes)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return;
            }

            keyframes.RemoveAll(k => !float.IsFinite(k.time) || !float.IsFinite(k.value));
            if (keyframes.Count <= 1)
            {
                if (keyframes.Count == 1 && keyframes[0].time < 0f)
                {
                    keyframes[0].time = 0f;
                }
                return;
            }

            keyframes.Sort((a, b) => a.time.CompareTo(b.time));
            int write = 0;
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k.time < 0f) k.time = 0f;
                if (write > 0 && MathF.Abs(k.time - keyframes[write - 1].time) <= 1e-6f)
                {
                    keyframes[write - 1] = k;
                }
                else
                {
                    keyframes[write++] = k;
                }
            }
            if (write < keyframes.Count)
            {
                keyframes.RemoveRange(write, keyframes.Count - write);
            }
        }

        private string GetPathFromHash(uint hash)
        {
            if (hash == 0)
            {
                return !string.IsNullOrEmpty(animatorRootPath)
                    ? animatorRootPath
                    : (RootFrame?.Path ?? string.Empty);
            }
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

        private void CreateBonePathHash(Transform? m_Transform)
        {
            CreateBonePathHash(m_Transform, new HashSet<Transform>());
        }

        private void CreateBonePathHash(Transform? m_Transform, HashSet<Transform> visited)
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
            var nodes = skeleton?.m_Node;

            if (skeleton == null || nodes == null || nodes.Length == 0 || skeleton.m_ID == null)
                return;

            BuildSkeletonFramesFromDefaultPose(skeleton, skeletonPose, fallbackMeshes: null);
        }

        private void BuildSkeletonFramesFromDefaultPose(Skeleton skeleton, SkeletonPose? skeletonPose, IReadOnlyList<Mesh>? fallbackMeshes)
        {
            var nodes = skeleton.m_Node;
            var skelIds = skeleton.m_ID;
            if (nodes == null || nodes.Length == 0 || skelIds == null || RootFrame == null)
                return;

            var skelCount = nodes.Length;
            bool hasDefaultPose = skeletonPose?.m_X != null && skeletonPose.m_X.Length >= skelCount;

            // Only fall back to m_BindPose decomposition when m_DefaultPose is absent/empty
            int[]? skelNodeToMeshBone = null;
            int[]? meshParentIndices = null;
            System.Numerics.Matrix4x4[]? bindPoseInverses = null;
            System.Numerics.Matrix4x4[]? bindPoses = null;
            bool useBindPoseFallback = false;

            if (!hasDefaultPose)
            {
                Mesh? fallbackMesh = null;
                int bestBoneCount = 0;
                if (fallbackMeshes != null)
                {
                    for (int i = 0; i < fallbackMeshes.Count; i++)
                    {
                        var m = fallbackMeshes[i];
                        if (m == null) continue;
                        m.EnsureProcessed();
                        if (m.m_BoneNameHashes != null && m.m_BindPose != null
                            && m.m_BoneNameHashes.Length == m.m_BindPose.Length
                            && m.m_BoneNameHashes.Length > bestBoneCount)
                        {
                            bestBoneCount = m.m_BoneNameHashes.Length;
                            fallbackMesh = m;
                        }
                    }
                }

                if (fallbackMesh != null && fallbackMesh.m_BindPose != null && fallbackMesh.m_BoneNameHashes != null && bestBoneCount > 0)
                {
                    skelNodeToMeshBone = new int[skelCount];
                    for (int i = 0; i < skelCount; i++) skelNodeToMeshBone[i] = -1;

                    var meshBoneHashToIdx = new Dictionary<uint, int>(bestBoneCount);
                    for (int j = 0; j < bestBoneCount; j++)
                    {
                        meshBoneHashToIdx[fallbackMesh.m_BoneNameHashes[j]] = j;
                    }

                    var meshBoneToSkelNode = new int[bestBoneCount];
                    for (int j = 0; j < bestBoneCount; j++) meshBoneToSkelNode[j] = -1;

                    for (int i = 0; i < skelCount && i < skelIds.Length; i++)
                    {
                        if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
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

                        int parentId = nodes[skelIdx].m_ParentId;
                        if (parentId >= 0 && parentId < skelCount && skelNodeToMeshBone[parentId] >= 0)
                        {
                            meshParentIndices[mb] = skelNodeToMeshBone[parentId];
                        }
                    }

                    bindPoseInverses = new System.Numerics.Matrix4x4[bestBoneCount];
                    bindPoses = new System.Numerics.Matrix4x4[bestBoneCount];
                    useBindPoseFallback = true;
                    for (int j = 0; j < bestBoneCount; j++)
                    {
                        var bp = ToNumericsMatrix(fallbackMesh.m_BindPose[j]);
                        bindPoseInverses[j] = bp;
                        bindPoses[j] = System.Numerics.Matrix4x4.Invert(bp, out var inv)
                            ? inv
                            : System.Numerics.Matrix4x4.Identity;
                    }
                }
            }

            var frames = new ImportedFrame[skelCount];
            for (int i = 0; i < skelCount; i++)
            {
                int parentId = nodes[i].m_ParentId;
                uint nodeId = i < skelIds.Length ? skelIds[i] : (uint)i;
                var fullPath = avatar?.FindBonePath(nodeId);

                // Root node (m_ParentId < 0 with empty bone path / ID == 0) represents RootFrame itself
                if (parentId < 0 && string.IsNullOrEmpty(fullPath))
                {
                    frames[i] = RootFrame;
                    bonePathHash[nodeId] = RootFrame.Path;
                    continue;
                }

                string name;
                if (string.IsNullOrEmpty(fullPath))
                {
                    name = $"Bone_{nodeId}";
                }
                else
                {
                    var lastSlash = fullPath.LastIndexOf('/');
                    name = lastSlash >= 0 ? fullPath.Substring(lastSlash + 1) : fullPath;
                }

                var xform = (skeletonPose?.m_X != null && i < skeletonPose.m_X.Length) ? skeletonPose.m_X[i] : null;
                var tVec = xform?.t ?? Vector3.Zero;
                var qQuat = xform?.q ?? new Quaternion(0, 0, 0, 1);
                var sVec = xform?.s ?? Vector3.One;

                if (useBindPoseFallback && skelNodeToMeshBone != null && meshParentIndices != null && bindPoses != null && bindPoseInverses != null)
                {
                    int mb = skelNodeToMeshBone[i];
                    if (mb >= 0)
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
                }

                ImportedFrame parentFrame = (parentId >= 0 && parentId < i && frames[parentId] != null)
                    ? frames[parentId]
                    : RootFrame;

                ImportedFrame? frame = null;
                if (!string.IsNullOrEmpty(fullPath))
                {
                    frame = RootFrame.FindRelativeFrameWithPath(fullPath);
                }
                frame ??= parentFrame.FindChild(name, recursive: false) ?? RootFrame.FindChild(name, recursive: false);

                if (frame != null && !ReferenceEquals(frame, RootFrame))
                {
                    SetFrame(frame, tVec, qQuat, sVec);
                    if (!ReferenceEquals(frame.Parent, parentFrame))
                    {
                        parentFrame.AddChild(frame);
                    }
                }
                else
                {
                    frame = CreateFrame(name, tVec, qQuat, sVec);
                    parentFrame.AddChild(frame);
                }

                frames[i] = frame;
            }

            // Connect any out-of-order parent edges and register bone path hashes (full path + relative suffixes)
            for (int i = 0; i < skelCount; i++)
            {
                var frame = frames[i];
                if (frame == null || ReferenceEquals(frame, RootFrame))
                {
                    continue;
                }

                int parentId = nodes[i].m_ParentId;
                var parentFrame = (parentId >= 0 && parentId < frames.Length && frames[parentId] != null && parentId != i)
                    ? frames[parentId]
                    : RootFrame;
                if (frame.Parent == null)
                {
                    parentFrame.AddChild(frame);
                }

                if (i < skelIds.Length)
                {
                    var fullFramePath = frame.Path;
                    bonePathHash[skelIds[i]] = fullFramePath;

                    // Also register CRC32 of full path and relative suffixes in bonePathHash
                    var suffix = fullFramePath;
                    while (!string.IsNullOrEmpty(suffix))
                    {
                        var crc = new SevenZip.CRC();
                        var bytes = Encoding.UTF8.GetBytes(suffix);
                        crc.Update(bytes, 0, (uint)bytes.Length);
                        bonePathHash[crc.GetDigest()] = fullFramePath;
                        int slash = suffix.IndexOf('/');
                        if (slash < 0) break;
                        suffix = suffix.Substring(slash + 1);
                    }
                }
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
                var visited = new HashSet<Transform>();
                var queue = new Queue<Transform>();
                queue.Enqueue(trans);
                visited.Add(trans);
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
                        if (childPtr.TryGet(out var childTrans) && visited.Add(childTrans))
                            queue.Enqueue(childTrans);
                    }
                }
            }

            if (go.assetsFile?.assetsManager != null)
            {
                // Match ONLY via genuine skeleton bone-hash overlap or explicit non-contradicting name match.
                // NEVER fall back blindly to an unrelated Avatar in the bundle.
                var cleanName = go.m_Name.Replace("_Model", "").Replace("Model", "").Trim();
                int nonZeroHierarchyHashes = 0;
                foreach (var key in bonePathHash.Keys)
                {
                    if (key != 0) nonZeroHierarchyHashes++;
                }

                Avatar? bestByBoneOverlap = null;
                int bestOverlap = 0;
                Avatar? bestByName = null;

                foreach (var sf in go.assetsFile.assetsManager.assetsFileList)
                {
                    foreach (var obj in sf.Objects)
                    {
                        if (obj is not Avatar candidate)
                        {
                            continue;
                        }

                        int overlap = CountAvatarBoneHashOverlap(candidate);
                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            bestByBoneOverlap = candidate;
                        }

                        if (bestByName == null
                            && cleanName.Length >= 3
                            && IsStrongAvatarNameMatch(candidate.m_Name, cleanName)
                            && (nonZeroHierarchyHashes == 0 || overlap > 0))
                        {
                            bestByName = candidate;
                        }
                    }
                }

                int minRequiredOverlap = nonZeroHierarchyHashes >= 3 ? 3 : Math.Max(1, nonZeroHierarchyHashes);
                if (bestByBoneOverlap != null && bestOverlap >= minRequiredOverlap)
                {
                    return bestByBoneOverlap;
                }

                if (bestByName != null)
                {
                    return bestByName;
                }
            }

            return null;
        }

        private int CountAvatarBoneHashOverlap(Avatar candidate)
        {
            if (bonePathHash.Count == 0)
            {
                return 0;
            }

            int overlap = 0;
            var counted = new HashSet<uint>();
            var skelIds = candidate.m_Avatar?.m_AvatarSkeleton?.m_ID;
            if (skelIds != null)
            {
                for (int i = 0; i < skelIds.Length; i++)
                {
                    uint id = skelIds[i];
                    if (id != 0 && counted.Add(id) && bonePathHash.ContainsKey(id))
                    {
                        overlap++;
                    }
                }
            }

            if (candidate.m_TOS != null)
            {
                for (int i = 0; i < candidate.m_TOS.Length; i++)
                {
                    uint id = candidate.m_TOS[i].Key;
                    if (id != 0 && counted.Add(id) && bonePathHash.ContainsKey(id))
                    {
                        overlap++;
                    }
                }
            }

            return overlap;
        }

        private static bool IsStrongAvatarNameMatch(string? avatarName, string cleanName)
        {
            if (string.IsNullOrWhiteSpace(avatarName))
            {
                return false;
            }
            var trimmed = avatarName.Trim();
            if (string.Equals(trimmed, cleanName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (trimmed.EndsWith("Avatar", StringComparison.OrdinalIgnoreCase))
            {
                var stem = trimmed.Substring(0, trimmed.Length - "Avatar".Length).TrimEnd('_', ' ', '-');
                if (string.Equals(stem, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadHumanoidMuscleCurves(ImportedKeyframedAnimation iAnim, AnimationClip animationClip)
        {
            if (avatar == null)
            {
                return;
            }

            var decodedTracks = HumanoidMuscleDecoder.DecodeHumanoidClip(animationClip, avatar);
            if (decodedTracks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < decodedTracks.Count; i++)
            {
                var decoded = decodedTracks[i];
                if (string.IsNullOrEmpty(decoded.BonePath))
                {
                    continue;
                }

                string fixedPath = FixBonePath(animationClip, decoded.BonePath) ?? decoded.BonePath;
                var track = iAnim.FindTrack(fixedPath);

                if (track.Translations.Count == 0 && decoded.Translations.Count > 0)
                {
                    for (int k = 0; k < decoded.Translations.Count; k++)
                    {
                        var kf = decoded.Translations[k];
                        track.Translations.Add(new ImportedKeyframe<Vector3>(
                            kf.time,
                            new Vector3(-kf.value.X, kf.value.Y, kf.value.Z),
                            kf.isStepped));
                    }
                }

                if (track.Rotations.Count == 0 && decoded.Rotations.Count > 0)
                {
                    for (int k = 0; k < decoded.Rotations.Count; k++)
                    {
                        var kf = decoded.Rotations[k];
                        var qFbx = new Quaternion(kf.value.X, -kf.value.Y, -kf.value.Z, kf.value.W);
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(
                            kf.time,
                            QuaternionToEuler(qFbx),
                            kf.isStepped));
                    }
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
            Transform? rootTransform,
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
            if (avatar == null || skeleton == null || nodes == null || nodes.Length == 0 || skeleton.m_ID == null)
            {
                return;
            }

            BuildSkeletonFramesFromDefaultPose(skeleton, skeletonPose, meshes);
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
                    var uvData = mesh.GetUV(uv);
                    if (uvData != null && vertexCount > 0 && uvData.Length >= vertexCount)
                    {
                        uvArrays[uv] = uvData;
                        uvStrides[uv] = uvData.Length / vertexCount;
                    }
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
                    int uvStride = uvStrides[uv];
                    if (uvStride > 0)
                    {
                        var m_UV = uvArrays[uv];
                        int uvOffset = j * uvStride;
                        if (uvStride == 1)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], 0f };
                        }
                        else if (uvOffset + 1 < m_UV.Length)
                        {
                            iVertex.UV[uv] = new[] { m_UV[uvOffset], m_UV[uvOffset + 1] };
                        }
                    }
                }

                if (iMesh.hasTangent && mesh.m_Tangents != null)
                {
                    iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], -mesh.m_Tangents[j * 4 + 3]);
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
                    int rawSlots = Math.Min(inf.boneIndex.Length, inf.weight.Length);
                    int slotCount = Math.Max(4, rawSlots);
                    iVertex.BoneIndices = new int[slotCount];
                    iVertex.Weights = new float[slotCount];
                    for (int k = 0; k < rawSlots; k++)
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
                    var cleanChannelName = NormalizeBlendShapeChannelName(shapeChannel.name);
                    RegisterMorphChannelHashes(shapeChannel, cleanChannelName);

                    var channel = new ImportedMorphChannel
                    {
                        Name = cleanChannelName,
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

            if (subHashSet != null && subHashSet.Count > 0)
            {
                UnbakeStaticBatchedMeshToLocalSpace(iMesh);
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

            string? effectiveName = combinedMeshName;
            if (string.IsNullOrWhiteSpace(effectiveName) && hasRootFrame && RootFrame != null && HasSkinnedMeshesWithRootAsBoneAncestor(MeshList, RootFrame))
            {
                effectiveName = "CombinedMesh";
            }

            ImportedFrame? targetFrame = RootFrame;
            if (RootFrame != null && !string.IsNullOrWhiteSpace(effectiveName))
            {
                var normalizedRootName = RootFrame.Name ?? string.Empty;
                var normalizedRootPath = NormalizeFramePath(RootFrame.Path);
                var normalizedTarget = NormalizeFramePath(effectiveName);
                if (!string.Equals(normalizedRootName, effectiveName, StringComparison.Ordinal)
                    && !string.Equals(normalizedRootPath, normalizedTarget, StringComparison.Ordinal))
                {
                    var existing = FindFrameForMesh(RootFrame, effectiveName);
                    if (existing != null && !IsFrameAncestorOfAnyMeshBone(existing, MeshList, RootFrame))
                    {
                        targetFrame = existing;
                    }
                    else
                    {
                        targetFrame = GetOrCreateDedicatedCombinedMeshFrame(RootFrame, effectiveName);
                    }
                }
            }

            var combinedMesh = CombineMeshesInternal(
                MeshList,
                RootFrame,
                targetFrame,
                effectiveName,
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

            string? effectiveName = combinedMeshName;
            bool needsDedicatedChild = imported.RootFrame != null
                && HasSkinnedMeshesWithRootAsBoneAncestor(imported.MeshList, imported.RootFrame);
            if (string.IsNullOrWhiteSpace(effectiveName) && needsDedicatedChild)
            {
                effectiveName = "CombinedMesh";
            }

            ImportedFrame? targetFrame = imported.RootFrame;
            if (imported.RootFrame != null && !string.IsNullOrWhiteSpace(effectiveName))
            {
                var existing = FindFrameForMesh(imported.RootFrame, effectiveName);
                if (existing != null && !IsFrameAncestorOfAnyMeshBone(existing, imported.MeshList, imported.RootFrame))
                {
                    targetFrame = existing;
                }
                else if (needsDedicatedChild
                    || (!string.Equals(imported.RootFrame.Name, effectiveName, StringComparison.Ordinal)
                        && !string.Equals(NormalizeFramePath(imported.RootFrame.Path), NormalizeFramePath(effectiveName), StringComparison.Ordinal)))
                {
                    targetFrame = GetOrCreateDedicatedCombinedMeshFrame(imported.RootFrame, effectiveName);
                }
            }

            var combinedMesh = CombineMeshesInternal(
                imported.MeshList,
                imported.RootFrame,
                targetFrame,
                effectiveName,
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

        private static bool HasSkinnedMeshesWithRootAsBoneAncestor(IReadOnlyList<ImportedMesh> meshes, ImportedFrame rootFrame)
        {
            return IsFrameAncestorOfAnyMeshBone(rootFrame, meshes, rootFrame);
        }

        private static bool IsFrameAncestorOfAnyMeshBone(ImportedFrame candidateAncestor, IReadOnlyList<ImportedMesh> meshes, ImportedFrame rootFrame)
        {
            if (candidateAncestor == null || meshes == null)
            {
                return false;
            }

            var ancestorPath = NormalizeFramePath(candidateAncestor.Path);
            for (int m = 0; m < meshes.Count; m++)
            {
                var boneList = meshes[m]?.BoneList;
                if (boneList == null) continue;
                for (int b = 0; b < boneList.Count; b++)
                {
                    var bonePath = NormalizeFramePath(boneList[b]?.Path);
                    if (string.IsNullOrEmpty(bonePath)) continue;
                    var boneFrame = FindFrameForMesh(rootFrame, bonePath);
                    if (boneFrame != null)
                    {
                        var cur = boneFrame.Parent;
                        while (cur != null)
                        {
                            if (ReferenceEquals(cur, candidateAncestor))
                            {
                                return true;
                            }
                            cur = cur.Parent;
                        }
                    }
                    else if (!string.IsNullOrEmpty(ancestorPath)
                        && bonePath.Length > ancestorPath.Length
                        && bonePath[ancestorPath.Length] == '/'
                        && bonePath.StartsWith(ancestorPath, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static ImportedFrame GetOrCreateDedicatedCombinedMeshFrame(ImportedFrame rootFrame, string preferredName)
        {
            var leafName = preferredName;
            int lastSlash = leafName.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash + 1 < leafName.Length)
            {
                leafName = leafName.Substring(lastSlash + 1);
            }
            if (string.IsNullOrWhiteSpace(leafName) || string.Equals(leafName, rootFrame.Name, StringComparison.Ordinal))
            {
                leafName = "CombinedMesh";
            }

            var existingChild = rootFrame.FindChild(leafName, recursive: false);
            if (existingChild != null && existingChild.Count == 0)
            {
                return existingChild;
            }

            string uniqueName = leafName;
            for (int i = 1; rootFrame.FindChild(uniqueName, recursive: false) != null; i++)
            {
                uniqueName = $"{leafName}_{i}";
            }

            var childFrame = CreateFrame(uniqueName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            rootFrame.AddChild(childFrame);
            return childFrame;
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
            var rigidMeshBoneIndices = new int[meshes.Count];
            for (int m = 0; m < meshes.Count; m++)
            {
                rigidMeshBoneIndices[m] = -1;
            }

            if (hasAnySkinning)
            {
                var targetGlobal = targetFrame != null
                    ? ComputeFrameGlobalMatrix(targetFrame)
                    : System.Numerics.Matrix4x4.Identity;

                for (int m = 0; m < meshes.Count; m++)
                {
                    var mesh = meshes[m];
                    if (mesh == null || (perMeshBoneRemaps[m] != null && perMeshBoneRemaps[m].Length > 0))
                    {
                        continue;
                    }

                    var meshFrame = FindFrameForMesh(rootFrame, mesh.Path);
                    if (meshFrame == null)
                    {
                        continue;
                    }

                    // 1. Walk up from meshFrame to find the nearest skeleton ancestor bone in unifiedBones
                    int foundBoneIdx = -1;
                    var cur = meshFrame;
                    var visited = new HashSet<ImportedFrame>();
                    while (cur != null && !ReferenceEquals(cur, rootFrame) && visited.Add(cur))
                    {
                        var normCurPath = NormalizeFramePath(cur.Path);
                        if (!string.IsNullOrEmpty(normCurPath) && boneIndexByNormalizedPath.TryGetValue(normCurPath, out var existingBoneIdx))
                        {
                            foundBoneIdx = existingBoneIdx;
                            break;
                        }
                        cur = cur.Parent;
                    }

                    // 2. If no ancestor is in unifiedBones yet, add meshFrame itself as a rigid bone when it is a
                    // non-mirrored child in a full model hierarchy (e.g., Shield / accessory under character sub-root)
                    if (foundBoneIdx < 0
                        && !partFlipWinding[m]
                        && !ReferenceEquals(meshFrame, rootFrame)
                        && (!ReferenceEquals(meshFrame.Parent, rootFrame) || animationList != null || morphList != null))
                    {
                        var boneGlobal = ComputeFrameGlobalMatrix(meshFrame);
                        if (System.Numerics.Matrix4x4.Invert(boneGlobal, out var invBoneGlobal))
                        {
                            var normMeshPath = NormalizeFramePath(meshFrame.Path);
                            var bindPose = FromNumericsMatrix(targetGlobal * invBoneGlobal);
                            foundBoneIdx = unifiedBones.Count;
                            unifiedBones.Add(new ImportedBone
                            {
                                Path = meshFrame.Path,
                                Matrix = bindPose
                            });
                            if (!string.IsNullOrEmpty(normMeshPath))
                            {
                                boneIndexByNormalizedPath[normMeshPath] = foundBoneIdx;
                            }
                        }
                    }

                    rigidMeshBoneIndices[m] = foundBoneIdx;
                }
            }

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
                int rigidBoneIdx = rigidMeshBoneIndices[m];

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
                                relMatrix);
                            float tangentW = SanitizeFloat(src.Tangent.W, 1f);
                            dst.Tangent = new Vector4(
                                transformedTangent.X,
                                transformedTangent.Y,
                                transformedTangent.Z,
                                flipWinding ? -tangentW : tangentW);
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
                        if (meshHasSkinning && boneRemap != null && src.BoneIndices != null && src.Weights != null)
                        {
                            int rawCount = Math.Min(src.BoneIndices.Length, src.Weights.Length);
                            int slotCount = Math.Max(4, rawCount);
                            dst.BoneIndices = new int[slotCount];
                            dst.Weights = new float[slotCount];
                            for (int k = 0; k < rawCount; k++)
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
                        else
                        {
                            dst.BoneIndices = new int[4];
                            dst.Weights = new float[4];
                            if (rigidBoneIdx >= 0)
                            {
                                dst.BoneIndices[0] = rigidBoneIdx;
                                dst.Weights[0] = 1.0f;
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
                    if (tr?.BlendShapes != null && tr.BlendShapes.Count > 0)
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
                                Visibilities = tr.Visibilities,
                                BlendShapes = tr.BlendShapes != null
                                    ? new List<ImportedBlendShape>(tr.BlendShapes)
                                    : new List<ImportedBlendShape>()
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
                    if (tr?.BlendShapes == null || tr.BlendShapes.Count == 0) continue;

                    var normTrackPath = NormalizeFramePath(tr.Path);
                    if (!string.IsNullOrEmpty(normTrackPath)
                        && combinedNormalizedPaths.Contains(normTrackPath)
                        && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                    {
                        var targetTrack = dstAnim.FindTrack(combinedMeshPath);
                        for (int b = 0; b < tr.BlendShapes.Count; b++)
                        {
                            var srcBs = tr.BlendShapes[b];
                            if (srcBs == null) continue;
                            var dstBs = targetTrack.FindOrCreateBlendShape(NormalizeBlendShapeChannelName(srcBs.ChannelName));
                            if (dstBs.Keyframes.Count == 0 && srcBs.Keyframes.Count > 0)
                            {
                                dstBs.Keyframes.AddRange(srcBs.Keyframes);
                            }
                        }
                        tr.BlendShapes.Clear();
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
