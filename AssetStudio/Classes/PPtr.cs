using System;

namespace AssetStudio
{
    public sealed class PPtr<T> where T : Object
    {
        public int m_FileID;
        public long m_PathID;

        private SerializedFile assetsFile;
        private int index = -2; //-2 - Prepare, -1 - Missing

        public PPtr(int fileID, long pathID, SerializedFile assetsFile)
        {
            m_FileID = fileID;
            m_PathID = pathID;
            this.assetsFile = assetsFile;
        }

        public PPtr(ObjectReader reader)
        {
            m_FileID = reader.ReadInt32();
            m_PathID = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? reader.ReadInt32() : reader.ReadInt64();
            assetsFile = reader.assetsFile;
        }

        public bool TryGetAssetsFile(out SerializedFile result)
        {
            result = null;
            if (m_FileID == 0)
            {
                result = assetsFile;
                return true;
            }

            if (m_FileID > 0 && m_FileID - 1 < assetsFile.m_Externals.Count)
            {
                var assetsManager = assetsFile.assetsManager;
                lock (assetsManager.loadLock)
                {
                    var assetsFileList = assetsManager.assetsFileList;
                    var assetsFileIndexCache = assetsManager.assetsFileIndexCache;

                    if (index == -2)
                    {
                        var m_External = assetsFile.m_Externals[m_FileID - 1];
                        var name = m_External.fileName;
                        var cacheKey = name + "|" + m_External.pathName;
                        if (!assetsFileIndexCache.TryGetValue(cacheKey, out index))
                        {
                            index = assetsFileList.FindIndex(x => IsExternalFileMatch(x, m_External));
                            assetsFileIndexCache[cacheKey] = index;
                        }
                    }

                    if (index >= 0 && index < assetsFileList.Count)
                    {
                        result = assetsFileList[index];
                        return true;
                    }
                }
            }

            return false;
        }

        public bool TryGet(out T result)
        {
            if (TryGetAssetsFile(out var sourceFile))
            {
                if (TryGetObject(sourceFile, out result))
                    return true;
            }

            if (TryGetObjectFromLoadedFiles(out result))
                return true;

            result = null;
            return false;
        }

        public bool TryGet<T2>(out T2 result) where T2 : Object
        {
            if (TryGetAssetsFile(out var sourceFile))
            {
                if (TryGetObject(sourceFile, out result))
                    return true;
            }

            if (TryGetObjectFromLoadedFiles(out result))
                return true;

            result = null;
            return false;
        }

        private bool TryGetObject<TObject>(SerializedFile sourceFile, out TObject result) where TObject : Object
        {
            lock (sourceFile)
            {
                if (sourceFile.ObjectsDic.TryGetValue(m_PathID, out var obj) && obj is TObject variable)
                {
                    result = variable;
                    return true;
                }
            }

            var handle = sourceFile.assetsManager?.ProjectIndex?.GetHandle($"{sourceFile.fileName}#{m_PathID}");
            if (handle != null)
            {
                var obj = sourceFile.assetsManager.ResolveHandle(handle);
                if (obj is TObject variable)
                {
                    result = variable;
                    return true;
                }
            }

            result = null;
            return false;
        }

        private bool TryGetObjectFromLoadedFiles<TObject>(out TObject result) where TObject : Object
        {
            result = null;
            if (m_FileID <= 0)
            {
                return false;
            }

            var matches = new System.Collections.Generic.List<TObject>();
            var matchFiles = new System.Collections.Generic.List<SerializedFile>();
            System.Collections.Generic.List<SerializedFile> filesSnapshot;
            lock (assetsFile.assetsManager.loadLock)
            {
                filesSnapshot = new System.Collections.Generic.List<SerializedFile>(assetsFile.assetsManager.assetsFileList);
            }

            foreach (var sourceFile in filesSnapshot)
            {
                if (sourceFile == assetsFile)
                {
                    continue;
                }

                if (TryGetObject(sourceFile, out TObject variable))
                {
                    matches.Add(variable);
                    matchFiles.Add(sourceFile);
                }
            }

            if (matches.Count == 0)
            {
                return false;
            }

            if (matches.Count == 1)
            {
                result = matches[0];
                return true;
            }

            TObject bestMatch = null;
            int bestScore = -1;

            var refFile = assetsFile;
            var refDir = System.IO.Path.GetDirectoryName(refFile.fullName) ?? string.Empty;
            var refName = System.IO.Path.GetFileNameWithoutExtension(refFile.fullName) ?? string.Empty;

            for (int i = 0; i < matches.Count; i++)
            {
                var candidate = matches[i];
                var candidateFile = matchFiles[i];

                int score = 0;

                var candidateDir = System.IO.Path.GetDirectoryName(candidateFile.fullName) ?? string.Empty;
                if (string.Equals(candidateDir, refDir, StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                }

                var candidateName = System.IO.Path.GetFileNameWithoutExtension(candidateFile.fullName) ?? string.Empty;
                if (string.Equals(candidateName, refName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
                else if (candidateName.Contains(refName, StringComparison.OrdinalIgnoreCase) || refName.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 20;
                }

                if (string.Equals(candidateFile.originalPath, refFile.originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = candidate;
                }
            }

            if (bestMatch != null)
            {
                result = bestMatch;
                return true;
            }

            return false;
        }

        private static bool IsExternalFileMatch(SerializedFile candidate, FileIdentifier external)
        {
            var externalFileName = NormalizeFileName(external.fileName);
            if (!string.IsNullOrEmpty(externalFileName)
                && (EqualsIgnoreCase(NormalizeFileName(candidate.fileName), externalFileName)
                    || EqualsIgnoreCase(NormalizeFileName(candidate.originalPath), externalFileName)
                    || EqualsIgnoreCase(NormalizeFileName(candidate.fullName), externalFileName)))
            {
                return true;
            }

            var externalPathFileName = NormalizeFileName(external.pathName);
            return !string.IsNullOrEmpty(externalPathFileName)
                && (EqualsIgnoreCase(NormalizeFileName(candidate.fileName), externalPathFileName)
                    || EqualsIgnoreCase(NormalizeFileName(candidate.originalPath), externalPathFileName)
                    || EqualsIgnoreCase(NormalizeFileName(candidate.fullName), externalPathFileName));
        }

        private static string NormalizeFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private static bool EqualsIgnoreCase(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public void Set(T m_Object)
        {
            var name = m_Object.assetsFile.fileName;
            if (string.Equals(assetsFile.fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                m_FileID = 0;
            }
            else
            {
                m_FileID = assetsFile.m_Externals.FindIndex(x => string.Equals(x.fileName, name, StringComparison.OrdinalIgnoreCase));
                if (m_FileID == -1)
                {
                    assetsFile.m_Externals.Add(new FileIdentifier
                    {
                        fileName = m_Object.assetsFile.fileName
                    });
                    m_FileID = assetsFile.m_Externals.Count;
                }
                else
                {
                    m_FileID += 1;
                }
            }

            var assetsManager = assetsFile.assetsManager;
            lock (assetsManager.loadLock)
            {
                var assetsFileList = assetsManager.assetsFileList;
                var assetsFileIndexCache = assetsManager.assetsFileIndexCache;

                if (!assetsFileIndexCache.TryGetValue(name, out index))
                {
                    index = assetsFileList.FindIndex(x => x.fileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    assetsFileIndexCache[name] = index;
                }
            }

            m_PathID = m_Object.m_PathID;
        }

        public bool IsNull => m_PathID == 0 || m_FileID < 0;
    }
}
