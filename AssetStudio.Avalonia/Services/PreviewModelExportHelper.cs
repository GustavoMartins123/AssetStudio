using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia.Services
{
    public enum PreviewModelExportKind
    {
        None = 0,
        SingleMesh = 1,
        ModelGroup = 2,
        AvatarRig = 3,
        AnimatedSingleMesh = 4,
        AnimatedModelGroup = 5
    }

    public sealed class PreviewModelExportTarget
    {
        public PreviewModelExportKind Kind { get; init; }
        public string SuggestedBaseName { get; init; } = "model";
        public bool ExportFullModelGroup { get; init; }
        public string ModelGroupId { get; init; } = string.Empty;
        public string ModelGroupName { get; init; } = string.Empty;
        public Mesh? PrimaryMesh { get; init; }
        public Avatar? Avatar { get; init; }
        public AnimationClip? AnimationClip { get; init; }
        public IReadOnlyList<Mesh> CandidateMeshes { get; init; } = Array.Empty<Mesh>();
        internal IReadOnlyList<ModelGroupMeshInfo> CandidateMeshInfos { get; init; } = Array.Empty<ModelGroupMeshInfo>();
        public IReadOnlyList<float[]?> CandidateTransforms { get; init; } = Array.Empty<float[]?>();
        public AssetItem? SelectedAsset { get; init; }

        public bool IsValid => Kind != PreviewModelExportKind.None;

        public string BuildSuggestedFileName(ExportOptionsState? exportOptions)
        {
            var ext = AssetExportService.GetModelFileExtension(exportOptions);
            var safeBase = AssetExportService.FixFileName(string.IsNullOrWhiteSpace(SuggestedBaseName) ? "model" : SuggestedBaseName);
            if (string.IsNullOrWhiteSpace(safeBase))
            {
                safeBase = "model";
            }

            return safeBase + ext;
        }
    }

    public static class PreviewModelExportHelper
    {
        public static bool CanExportSurfaceKind(PreviewSurfaceKind activeKind, bool isGeometryControlsVisible)
        {
            if (isGeometryControlsVisible)
            {
                return true;
            }

            return activeKind is PreviewSurfaceKind.Mesh3D
                or PreviewSurfaceKind.Avatar
                or PreviewSurfaceKind.Animation3D;
        }

        public static PreviewModelExportTarget ResolveExportTarget(
            PreviewSurfaceKind activeKind,
            AssetItem? selectedAsset,
            PreviewCandidateItem? selectedCandidate,
            Mesh? currentMesh,
            Avatar? currentAvatar,
            AnimationClip? currentClip,
            bool showFullModelGroup)
        {
            var isAnimationContext = activeKind == PreviewSurfaceKind.Animation3D
                || currentClip != null
                || selectedCandidate?.AnimationClip != null
                || selectedAsset?.Type == ClassIDType.AnimationClip;

            var clip = isAnimationContext
                ? (currentClip ?? selectedCandidate?.AnimationClip ?? (selectedAsset?.Asset as AnimationClip))
                : null;
            var avatar = currentAvatar ?? selectedCandidate?.Avatar ?? (selectedAsset?.Asset as Avatar);
            var mesh = currentMesh ?? selectedCandidate?.Mesh ?? (selectedAsset?.Asset as Mesh);

            var hasCandidateGroupParts = selectedCandidate != null && (
                selectedCandidate.IsModelGroup
                || selectedCandidate.ModelGroupMeshes.Count > 1
                || selectedCandidate.ModelGroupMeshInfos.Count > 1
                || selectedCandidate.ModelGroupMeshIds.Count > 1);

            var exportFullGroup = showFullModelGroup && hasCandidateGroupParts;

            PreviewModelExportKind kind;
            if (exportFullGroup)
            {
                kind = clip != null
                    ? PreviewModelExportKind.AnimatedModelGroup
                    : PreviewModelExportKind.ModelGroup;
            }
            else if (mesh != null)
            {
                kind = clip != null
                    ? PreviewModelExportKind.AnimatedSingleMesh
                    : PreviewModelExportKind.SingleMesh;
            }
            else if (avatar != null)
            {
                kind = clip != null
                    ? PreviewModelExportKind.AnimatedModelGroup
                    : PreviewModelExportKind.AvatarRig;
            }
            else if (selectedCandidate != null && selectedCandidate.ModelGroupMeshes.Count > 0)
            {
                mesh = selectedCandidate.ModelGroupMeshes[0];
                kind = clip != null
                    ? PreviewModelExportKind.AnimatedSingleMesh
                    : PreviewModelExportKind.SingleMesh;
            }
            else
            {
                return new PreviewModelExportTarget
                {
                    Kind = PreviewModelExportKind.None,
                    SelectedAsset = selectedAsset
                };
            }

            var modelGroupName = selectedCandidate?.ModelGroupName ?? string.Empty;
            var baseName = SelectSuggestedBaseName(
                exportFullGroup,
                modelGroupName,
                mesh,
                avatar,
                clip,
                selectedAsset);

            return new PreviewModelExportTarget
            {
                Kind = kind,
                SuggestedBaseName = baseName,
                ExportFullModelGroup = exportFullGroup,
                ModelGroupId = exportFullGroup ? (selectedCandidate?.ModelGroupId ?? string.Empty) : string.Empty,
                ModelGroupName = exportFullGroup ? modelGroupName : string.Empty,
                PrimaryMesh = mesh,
                Avatar = avatar,
                AnimationClip = clip,
                CandidateMeshes = exportFullGroup && selectedCandidate != null
                    ? selectedCandidate.ModelGroupMeshes
                    : Array.Empty<Mesh>(),
                CandidateMeshInfos = exportFullGroup && selectedCandidate != null
                    ? selectedCandidate.ModelGroupMeshInfos
                    : Array.Empty<ModelGroupMeshInfo>(),
                CandidateTransforms = exportFullGroup && selectedCandidate != null
                    ? selectedCandidate.ModelGroupTransforms
                    : Array.Empty<float[]?>(),
                SelectedAsset = selectedAsset
            };
        }

        public static string SelectSuggestedBaseName(
            bool exportFullGroup,
            string? modelGroupName,
            Mesh? mesh,
            Avatar? avatar,
            AnimationClip? clip,
            AssetItem? selectedAsset)
        {
            string? primary = null;
            if (exportFullGroup && !string.IsNullOrWhiteSpace(modelGroupName))
            {
                primary = modelGroupName;
            }
            else if (mesh != null && !string.IsNullOrWhiteSpace(mesh.m_Name))
            {
                primary = mesh.m_Name;
            }
            else if (avatar != null && !string.IsNullOrWhiteSpace(avatar.m_Name))
            {
                primary = avatar.m_Name;
            }
            else if (!string.IsNullOrWhiteSpace(selectedAsset?.Name))
            {
                primary = selectedAsset!.Name;
            }
            else if (clip != null && !string.IsNullOrWhiteSpace(clip.m_Name))
            {
                primary = clip.m_Name;
            }
            else
            {
                primary = "model";
            }

            if (clip != null
                && !string.IsNullOrWhiteSpace(clip.m_Name)
                && !string.Equals(primary, clip.m_Name, StringComparison.OrdinalIgnoreCase))
            {
                return $"{primary}_{clip.m_Name}";
            }

            return primary;
        }

        public static ModelExportFormat ResolveEffectiveFormat(string? filePath, ExportOptionsState? exportOptions)
        {
            var ext = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetExtension(filePath);
            if (string.Equals(ext, ".glb", StringComparison.OrdinalIgnoreCase))
            {
                return ModelExportFormat.Glb;
            }

            if (string.Equals(ext, ".gltf", StringComparison.OrdinalIgnoreCase))
            {
                return ModelExportFormat.Gltf;
            }

            if (string.Equals(ext, ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return ModelExportFormat.Fbx;
            }

            return exportOptions?.ModelFormat ?? ModelExportFormat.Fbx;
        }
    }
}
