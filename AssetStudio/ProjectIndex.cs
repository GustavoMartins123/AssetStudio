#nullable enable
using System;
using System.Collections.Generic;

namespace AssetStudio
{
    public class ProjectIndex
    {
        private readonly AssetIndexArena _arena = new AssetIndexArena();

        public AssetIndexArena Arena => _arena;

        public int Count => _arena.Count;

        public void AddHandle(AssetHandle handle)
        {
            if (handle != null && handle.HasUniqueId)
            {
                _arena.AddHandle(handle);
            }
        }

        public int AddRecord(
            string? serializedFileName,
            string? originalPath,
            long pathId,
            string? name,
            ClassIDType type,
            string? container = null,
            long byteStart = 0,
            long byteSize = 0,
            string? sourceHash = null,
            string? unityVersion = null,
            SerializedFile? sourceFile = null,
            Object? realObject = null,
            object? tag = null,
            string? customUniqueId = null)
        {
            return _arena.AddRecord(
                serializedFileName,
                originalPath,
                pathId,
                name,
                type,
                container,
                byteStart,
                byteSize,
                sourceHash,
                unityVersion,
                sourceFile,
                realObject,
                tag,
                customUniqueId);
        }

        public bool TryGetIndex(AssetHandleKey key, out int index)
        {
            return _arena.TryGetIndex(key, out index);
        }

        public bool TryGetIndexByUniqueId(ReadOnlySpan<char> uniqueId, out int index)
        {
            return _arena.TryGetIndexByUniqueId(uniqueId, out index);
        }

        public bool TryGetIndexByUniqueId(string? uniqueId, out int index)
        {
            return _arena.TryGetIndexByUniqueId(uniqueId, out index);
        }

        public AssetHandle MaterializeHandle(int index)
        {
            return _arena.MaterializeHandle(index);
        }

        public AssetHandle? GetHandle(AssetHandleKey key)
        {
            if (_arena.TryGetIndex(key, out int index)
                && _arena.TryMaterializeHandle(index, out AssetHandle? handle))
            {
                return handle;
            }

            return null;
        }

        public AssetHandle? GetHandle(SerializedFile? file, long pathID)
        {
            AssetHandleOrigin origin = AssetHandleOrigin.Intern(file);
            return GetHandle(new AssetHandleKey(origin.KeyOriginId, pathID));
        }

        public AssetHandle? GetHandle(string? uniqueID)
        {
            if (string.IsNullOrEmpty(uniqueID))
            {
                return null;
            }

            if (_arena.TryGetIndexByUniqueId(uniqueID.AsSpan(), out int index)
                && _arena.TryMaterializeHandle(index, out AssetHandle? handle))
            {
                return handle;
            }

            return null;
        }

        public IEnumerable<AssetHandle> GetHandles()
        {
            int count = _arena.Count;
            for (int i = 0; i < count; i++)
            {
                if (_arena.TryMaterializeHandle(i, out AssetHandle? handle) && handle != null)
                {
                    yield return handle;
                }
            }
        }

        public int[] DrainPendingIndices(int maxCount = int.MaxValue)
        {
            return _arena.DrainPendingIndices(maxCount);
        }

        public IEnumerable<AssetHandle> DrainPendingHandles()
        {
            while (_arena.TryDequeuePendingIndex(out int index))
            {
                if (_arena.TryMaterializeHandle(index, out AssetHandle? handle) && handle != null)
                {
                    yield return handle;
                }
            }
        }

        public IEnumerable<AssetHandle> DrainPendingHandles(int maxCount)
        {
            if (maxCount <= 0)
            {
                yield break;
            }

            int drained = 0;
            while (drained < maxCount && _arena.TryDequeuePendingIndex(out int index))
            {
                if (_arena.TryMaterializeHandle(index, out AssetHandle? handle) && handle != null)
                {
                    drained++;
                    yield return handle;
                }
            }
        }

        public IEnumerable<AssetHandle> GetHandlesForFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return Array.Empty<AssetHandle>();
            }

            int[] indices = _arena.GetIndicesForFile(fileName);
            if (indices.Length == 0)
            {
                return Array.Empty<AssetHandle>();
            }

            return MaterializeIndices(indices);
        }

        private IEnumerable<AssetHandle> MaterializeIndices(int[] indices)
        {
            for (int i = 0; i < indices.Length; i++)
            {
                if (_arena.TryMaterializeHandle(indices[i], out AssetHandle? handle) && handle != null)
                {
                    yield return handle;
                }
            }
        }

        public void Clear()
        {
            _arena.Clear();
        }
    }
}
