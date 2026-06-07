using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia;

internal sealed class LazyConnectionBuildDiagnostics
{
    private const int SampleLimit = 3;
    private readonly Dictionary<ClassIDType, int> materializedTypes = new();
    private readonly List<string> sourceSamplesWithoutLoadedFiles = new();
    private readonly List<string> sourceSamplesWithoutReferenceObjects = new();

    public int SourceCount { get; set; }
    public int BatchCount { get; set; }
    public int SourcesWithLoadedFiles { get; private set; }
    public int SourcesWithoutLoadedFiles { get; private set; }
    public int LoadedFileMatches { get; private set; }
    public int CandidateHandles { get; private set; }
    public int ResolvedObjects { get; private set; }
    public int FailedObjects { get; private set; }
    public int SourcesWithoutReferenceObjects { get; private set; }
    public int ObjectsBeforeMaterialize { get; private set; }
    public int ObjectsAfterMaterialize { get; private set; }
    public int RelationPasses { get; private set; }
    public int FailedSources { get; set; }
    public int AssetEdges { get; private set; }
    public int ModelGroups { get; private set; }
    public int ModelGroupMeshes { get; private set; }
    public int MeshRenderers { get; private set; }
    public int MeshMaterials { get; private set; }
    public int MaterialTextures { get; private set; }

    public void RecordLoadedFiles(string sourcePath, int loadedFileCount)
    {
        if (loadedFileCount <= 0)
        {
            SourcesWithoutLoadedFiles++;
            AddSample(sourceSamplesWithoutLoadedFiles, sourcePath);
            return;
        }

        SourcesWithLoadedFiles++;
        LoadedFileMatches += loadedFileCount;
    }

    public void RecordMaterializedSource(string sourcePath, int objectsBefore, int objectsAfter)
    {
        ObjectsBeforeMaterialize += Math.Max(0, objectsBefore);
        ObjectsAfterMaterialize += Math.Max(0, objectsAfter);
        if (objectsAfter <= 0)
        {
            SourcesWithoutReferenceObjects++;
            AddSample(sourceSamplesWithoutReferenceObjects, sourcePath);
        }
    }

    public void RecordMaterializationCandidateCount(int count)
    {
        CandidateHandles += Math.Max(0, count);
    }

    public void RecordResolvedObject(ClassIDType type)
    {
        ResolvedObjects++;
        materializedTypes.TryGetValue(type, out var count);
        materializedTypes[type] = count + 1;
    }

    public void RecordFailedObject()
    {
        FailedObjects++;
    }

    public void RecordRelationsPass(SemanticAssetRelations relations)
    {
        if (relations == null)
        {
            return;
        }

        RelationPasses++;
        AssetEdges += relations.AssetEdges.Count;
        ModelGroups += relations.ModelGroups.Count;
        ModelGroupMeshes += relations.ModelGroupMeshes.Count;
        MeshRenderers += relations.MeshRenderers.Count;
        MeshMaterials += relations.MeshMaterials.Count;
        MaterialTextures += relations.MaterialTextures.Count;
    }

    public string FormatStatusSummary(int totalSources, SemanticAssetRelations relations)
    {
        return $"sources {totalSources:N0}, matched {SourcesWithLoadedFiles:N0}, no files {SourcesWithoutLoadedFiles:N0}, " +
            $"handles {CandidateHandles:N0}, objects {ResolvedObjects:N0}, " +
            $"model-groups {relations.ModelGroups.Count:N0}, group-meshes {relations.ModelGroupMeshes.Count:N0}, " +
            $"mesh-material {relations.MeshMaterials.Count:N0}, material-texture {relations.MaterialTextures.Count:N0}, edges {relations.AssetEdges.Count:N0}";
    }

    public string FormatDetailedSummary(int totalSources, SemanticAssetRelations relations)
    {
        var materialized = materializedTypes.Count == 0
            ? "none"
            : string.Join(", ", materializedTypes
                .OrderByDescending(entry => entry.Value)
                .Take(5)
                .Select(entry => $"{entry.Key}:{entry.Value:N0}"));

        var summary = $"{FormatStatusSummary(totalSources, relations)}, failed objects {FailedObjects:N0}, " +
            $"object files empty {SourcesWithoutReferenceObjects:N0}, relation passes {RelationPasses:N0}, " +
            $"types {materialized}";

        if (sourceSamplesWithoutLoadedFiles.Count > 0)
        {
            summary += $" | no-file sample: {string.Join(", ", sourceSamplesWithoutLoadedFiles)}";
        }

        if (sourceSamplesWithoutReferenceObjects.Count > 0)
        {
            summary += $" | empty-object sample: {string.Join(", ", sourceSamplesWithoutReferenceObjects)}";
        }

        return summary;
    }

    private static void AddSample(List<string> samples, string sourcePath)
    {
        if (samples.Count >= SampleLimit)
        {
            return;
        }

        samples.Add(Path.GetFileName(sourcePath));
    }
}
