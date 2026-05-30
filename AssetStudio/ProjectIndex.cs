using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AssetStudio
{
    public class ProjectIndex
    {
        private readonly ConcurrentDictionary<string, AssetHandle> _handles = new ConcurrentDictionary<string, AssetHandle>();
        private readonly ConcurrentDictionary<string, ConcurrentBag<AssetHandle>> _handlesByFile = new ConcurrentDictionary<string, ConcurrentBag<AssetHandle>>();
        
        public void AddHandle(AssetHandle handle)
        {
            if (handle != null && !string.IsNullOrEmpty(handle.UniqueID))
            {
                _handles[handle.UniqueID] = handle;

                if (!string.IsNullOrEmpty(handle.SerializedFileName))
                {
                    var fileBag = _handlesByFile.GetOrAdd(handle.SerializedFileName, _ => new ConcurrentBag<AssetHandle>());
                    fileBag.Add(handle);
                }
            }
        }

        public AssetHandle GetHandle(string uniqueID)
        {
            if (string.IsNullOrEmpty(uniqueID)) return null;
            _handles.TryGetValue(uniqueID, out var handle);
            return handle;
        }

        public IEnumerable<AssetHandle> GetHandles()
        {
            return _handles.Values;
        }

        public IEnumerable<AssetHandle> GetHandlesForFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return Array.Empty<AssetHandle>();
            _handlesByFile.TryGetValue(fileName, out var fileBag);
            return fileBag ?? (IEnumerable<AssetHandle>)Array.Empty<AssetHandle>();
        }

        public void Clear()
        {
            _handles.Clear();
            _handlesByFile.Clear();
        }
    }
}
