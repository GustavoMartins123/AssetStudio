using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AssetStudio
{
    public class ProjectIndex
    {
        private readonly ConcurrentDictionary<string, AssetHandle> _handles = new ConcurrentDictionary<string, AssetHandle>();
        
        public void AddHandle(AssetHandle handle)
        {
            if (handle != null && !string.IsNullOrEmpty(handle.UniqueID))
            {
                _handles[handle.UniqueID] = handle;
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

        public void Clear()
        {
            _handles.Clear();
        }
    }
}
