using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using static AssetStudio.ImportHelper;

namespace AssetStudio
{
    public class AssetsManager
    {
        public string SpecifyUnityVersion;
        public string ProjectRoot;
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();
        public List<Stream> bundleStreams = new List<Stream>();

        internal Dictionary<string, int> assetsFileIndexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, BinaryReader> resourceFileReaders = new Dictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        private List<string> importFiles = new List<string>();
        private HashSet<string> importFilesHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> noexistFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> assetsFileListHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly object loadLock = new object();
        private System.Collections.Concurrent.ConcurrentQueue<string> loadQueue;
        private int loadTotalCount;

        public void LoadFiles(params string[] files)
        {
            var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = path;
            }
            MergeSplitAssets(path);
            var toReadFile = ProcessingSplitFiles(files.ToList());
            Load(toReadFile);
        }

        public void LoadFolder(string path)
        {
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = Path.GetFullPath(path);
            }
            MergeSplitAssets(path, true);
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).ToList();
            var toReadFile = ProcessingSplitFiles(files);
            Load(toReadFile);
        }

        private void Load(string[] files)
        {
            loadQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            loadTotalCount = files.Length;

            lock (loadLock)
            {
                foreach (var file in files)
                {
                    importFiles.Add(file);
                    importFilesHash.Add(Path.GetFileName(file));
                    loadQueue.Enqueue(file);
                }
            }

            Progress.Reset();

            int completedCount = 0;
            int threadCount = Math.Min(Environment.ProcessorCount, loadTotalCount);
            if (threadCount < 1) threadCount = 1;

            int activeWorkers = threadCount;
            var tasks = new System.Threading.Tasks.Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = System.Threading.Tasks.Task.Run(() =>
                {
                    bool isWorkerActive = true;
                    while (true)
                    {
                        string file = null;
                        if (loadQueue.TryDequeue(out file))
                        {
                            if (!isWorkerActive)
                            {
                                System.Threading.Interlocked.Increment(ref activeWorkers);
                                isWorkerActive = true;
                            }

                            LoadFile(file);
                            var completed = System.Threading.Interlocked.Increment(ref completedCount);
                            Progress.Report(completed, System.Threading.Volatile.Read(ref loadTotalCount));
                        }
                        else
                        {
                            if (isWorkerActive)
                            {
                                System.Threading.Interlocked.Decrement(ref activeWorkers);
                                isWorkerActive = false;
                            }

                            if (System.Threading.Volatile.Read(ref activeWorkers) == 0 && loadQueue.IsEmpty)
                            {
                                break;
                            }

                            System.Threading.Thread.Sleep(10);
                        }
                    }
                });
            }
            System.Threading.Tasks.Task.WaitAll(tasks);

            loadQueue = null;

            importFiles.Clear();
            importFilesHash.Clear();
            noexistFiles.Clear();
            assetsFileListHash.Clear();

            ReadAssets();
            ProcessAssets();
        }

        private void LoadFile(string fullName)
        {
            var reader = new FileReader(fullName);
            LoadFile(reader);
        }

        private void LoadFile(FileReader reader)
        {
            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(reader);
                    break;
                case FileType.BundleFile:
                    LoadBundleFile(reader);
                    break;
                case FileType.WebFile:
                    LoadWebFile(reader);
                    break;
                case FileType.GZipFile:
                    LoadFile(DecompressGZip(reader));
                    break;
                case FileType.BrotliFile:
                    LoadFile(DecompressBrotli(reader));
                    break;
                case FileType.ZipFile:
                    LoadZipFile(reader);
                    break;
                default:
                    reader.Dispose();
                    break;
            }
        }

        private void LoadAssetsFile(FileReader reader)
        {
            bool alreadyLoaded;
            lock (loadLock)
            {
                alreadyLoaded = assetsFileListHash.Contains(reader.FileName);
            }

            if (!alreadyLoaded)
            {
                Logger.Info($"Loading {reader.FullPath}");
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    CheckStrippedVersion(assetsFile);
                    
                    lock (loadLock)
                    {
                        assetsFileList.Add(assetsFile);
                        assetsFileListHash.Add(assetsFile.fileName);
                    }

                    foreach (var sharedFile in assetsFile.m_Externals)
                    {
                        var sharedFileName = sharedFile.fileName;

                        bool containsShared;
                        lock (loadLock)
                        {
                            containsShared = importFilesHash.Contains(sharedFileName);
                        }

                        if (!containsShared)
                        {
                            var sharedFilePath = Path.Combine(Path.GetDirectoryName(reader.FullPath), sharedFileName);
                            
                            bool containsNoExist;
                            lock (loadLock)
                            {
                                containsNoExist = noexistFiles.Contains(sharedFilePath);
                            }

                            if (!containsNoExist)
                            {
                                if (!File.Exists(sharedFilePath))
                                {
                                    var findFiles = Directory.GetFiles(Path.GetDirectoryName(reader.FullPath), sharedFileName, SearchOption.AllDirectories);
                                    if (findFiles.Length > 0)
                                    {
                                        sharedFilePath = findFiles[0];
                                    }
                                }
                                if (File.Exists(sharedFilePath))
                                {
                                    lock (loadLock)
                                    {
                                        importFiles.Add(sharedFilePath);
                                        importFilesHash.Add(sharedFileName);
                                        loadQueue?.Enqueue(sharedFilePath);
                                        System.Threading.Interlocked.Increment(ref loadTotalCount);
                                    }
                                }
                                else
                                {
                                    lock (loadLock)
                                    {
                                        noexistFiles.Add(sharedFilePath);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath}", e);
                    reader.Dispose();
                }
            }
            else
            {
                Logger.Info($"Skipping {reader.FullPath}");
                reader.Dispose();
            }
        }

        private void LoadAssetsFromMemory(FileReader reader, string originalPath, string unityVersion = null)
        {
            bool alreadyLoaded;
            lock (loadLock)
            {
                alreadyLoaded = assetsFileListHash.Contains(reader.FileName);
            }

            if (!alreadyLoaded)
            {
                try
                {
                    var assetsFile = new SerializedFile(reader, this);
                    assetsFile.originalPath = originalPath;
                    if (!string.IsNullOrEmpty(unityVersion) && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                    {
                        assetsFile.SetVersion(unityVersion);
                    }
                    CheckStrippedVersion(assetsFile);
                    
                    lock (loadLock)
                    {
                        assetsFileList.Add(assetsFile);
                        assetsFileListHash.Add(assetsFile.fileName);
                    }
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                    lock (loadLock)
                    {
                        resourceFileReaders[reader.FileName] = reader;
                    }
                }
            }
            else
            {
                Logger.Info($"Skipping {originalPath} ({reader.FileName})");
            }
        }

        private void LoadBundleFile(FileReader reader, string originalPath = null)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                var bundleFile = new BundleFile(reader);
                if (bundleFile.BlocksStream != null)
                {
                    lock (loadLock)
                    {
                        bundleStreams.Add(bundleFile.BlocksStream);
                    }
                }
                foreach (var file in bundleFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(subReader, originalPath ?? reader.FullPath, bundleFile.m_Header.unityRevision);
                    }
                    else
                    {
                        lock (loadLock)
                        {
                            resourceFileReaders[file.fileName] = subReader; //TODO
                        }
                    }
                }
            }
            catch (Exception e)
            {
                var str = $"Error while reading bundle file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadWebFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FullPath);
            try
            {
                var webFile = new WebFile(reader);
                foreach (var file in webFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    switch (subReader.FileType)
                    {
                        case FileType.AssetsFile:
                            LoadAssetsFromMemory(subReader, reader.FullPath);
                            break;
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, reader.FullPath);
                            break;
                        case FileType.WebFile:
                            LoadWebFile(subReader);
                            break;
                        case FileType.ResourceFile:
                            lock (loadLock)
                            {
                                resourceFileReaders[file.fileName] = subReader; //TODO
                            }
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading web file {reader.FullPath}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadZipFile(FileReader reader)
        {
            Logger.Info("Loading " + reader.FileName);
            try
            {
                using (ZipArchive archive = new ZipArchive(reader.BaseStream, ZipArchiveMode.Read))
                {
                    List<string> splitFiles = new List<string>();
                    // register all files before parsing the assets so that the external references can be found
                    // and find split files
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Contains(".split"))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                            string basePath = Path.Combine(Path.GetDirectoryName(entry.FullName), baseName);
                            if (!splitFiles.Contains(basePath))
                            {
                                splitFiles.Add(basePath);
                                lock (loadLock)
                                {
                                    importFilesHash.Add(baseName);
                                }
                            }
                        }
                        else
                        {
                            lock (loadLock)
                            {
                                importFilesHash.Add(entry.Name);
                            }
                        }
                    }

                    // merge split files and load the result
                    foreach (string basePath in splitFiles)
                    {
                        try
                        {
                            Stream splitStream = new MemoryStream();
                            int i = 0;
                            while (true)
                            {
                                string path = $"{basePath}.split{i++}";
                                ZipArchiveEntry entry = archive.GetEntry(path);
                                if (entry == null)
                                    break;
                                using (Stream entryStream = entry.Open())
                                {
                                    entryStream.CopyTo(splitStream);
                                }
                            }
                            splitStream.Seek(0, SeekOrigin.Begin);
                            FileReader entryReader = new FileReader(basePath, splitStream);
                            LoadFile(entryReader);
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip split file {basePath}", e);
                        }
                    }

                    // load all entries
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        try
                        {
                            string dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), reader.FileName, entry.FullName);
                            // create a new stream
                            // - to store the deflated stream in
                            // - to keep the data for later extraction
                            Stream streamReader = new MemoryStream((int)entry.Length);
                            using (Stream entryStream = entry.Open())
                            {
                                entryStream.CopyTo(streamReader);
                            }
                            streamReader.Position = 0;

                            FileReader entryReader = new FileReader(dummyPath, streamReader);
                            LoadFile(entryReader);
                            if (entryReader.FileType == FileType.ResourceFile)
                            {
                                entryReader.Position = 0;
                                lock (loadLock)
                                {
                                    if (!resourceFileReaders.ContainsKey(entry.Name))
                                    {
                                        resourceFileReaders.Add(entry.Name, entryReader);
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip entry {entry.FullName}", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading zip file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        public void CheckStrippedVersion(SerializedFile assetsFile)
        {
            if (assetsFile.IsVersionStripped && string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                throw new Exception("The Unity version has been stripped, please set the version in the options");
            }
            if (!string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                assetsFile.SetVersion(SpecifyUnityVersion);
            }
        }

        public void Clear()
        {
            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.reader.Close();
            }
            assetsFileList.Clear();

            foreach (var resourceFileReader in resourceFileReaders)
            {
                resourceFileReader.Value.Close();
            }
            resourceFileReaders.Clear();

            lock (loadLock)
            {
                foreach (var stream in bundleStreams)
                {
                    stream.Dispose();
                }
                bundleStreams.Clear();
            }

            assetsFileIndexCache.Clear();
        }

        private void ReadAssets()
        {
            Logger.Info("Read assets...");

            var progressCount = assetsFileList.Sum(x => x.m_Objects.Count);
            int progressValue = 0;
            int errorCount = 0;
            Progress.Reset();

            foreach (var assetsFile in assetsFileList)
            {
                var localObjects = new System.Collections.Concurrent.ConcurrentBag<Object>();

                System.Threading.Tasks.Parallel.ForEach(
                    assetsFile.m_Objects,
                    () => assetsFile.reader.Clone(),
                    (objectInfo, state, localReader) =>
                    {
                        var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                        try
                        {
                            Object obj;
                            switch (objectReader.type)
                            {
                                case ClassIDType.Animation:
                                    obj = new Animation(objectReader);
                                    break;
                                case ClassIDType.AnimationClip:
                                    obj = new AnimationClip(objectReader);
                                    break;
                                case ClassIDType.Animator:
                                    obj = new Animator(objectReader);
                                    break;
                                case ClassIDType.AnimatorController:
                                    obj = new AnimatorController(objectReader);
                                    break;
                                case ClassIDType.AnimatorOverrideController:
                                    obj = new AnimatorOverrideController(objectReader);
                                    break;
                                case ClassIDType.AssetBundle:
                                    obj = new AssetBundle(objectReader);
                                    break;
                                case ClassIDType.AudioClip:
                                    obj = new AudioClip(objectReader);
                                    break;
                                case ClassIDType.Avatar:
                                    obj = new Avatar(objectReader);
                                    break;
                                case ClassIDType.Font:
                                    obj = new Font(objectReader);
                                    break;
                                case ClassIDType.GameObject:
                                    obj = new GameObject(objectReader);
                                    break;
                                case ClassIDType.Material:
                                    obj = new Material(objectReader);
                                    break;
                                case ClassIDType.Mesh:
                                    obj = new Mesh(objectReader);
                                    break;
                                case ClassIDType.MeshFilter:
                                    obj = new MeshFilter(objectReader);
                                    break;
                                case ClassIDType.MeshRenderer:
                                    obj = new MeshRenderer(objectReader);
                                    break;
                                case ClassIDType.MonoBehaviour:
                                    obj = new MonoBehaviour(objectReader);
                                    break;
                                case ClassIDType.MonoScript:
                                    obj = new MonoScript(objectReader);
                                    break;
                                case ClassIDType.MovieTexture:
                                    obj = new MovieTexture(objectReader);
                                    break;
                                case ClassIDType.PlayerSettings:
                                    obj = new PlayerSettings(objectReader);
                                    break;
                                case ClassIDType.RectTransform:
                                    obj = new RectTransform(objectReader);
                                    break;
                                case ClassIDType.Shader:
                                    obj = new Shader(objectReader);
                                    break;
                                case ClassIDType.SkinnedMeshRenderer:
                                    obj = new SkinnedMeshRenderer(objectReader);
                                    break;
                                case ClassIDType.Sprite:
                                    obj = new Sprite(objectReader);
                                    break;
                                case ClassIDType.SpriteAtlas:
                                    obj = new SpriteAtlas(objectReader);
                                    break;
                                case ClassIDType.TextAsset:
                                    obj = new TextAsset(objectReader);
                                    break;
                                case ClassIDType.Texture2D:
                                    obj = new Texture2D(objectReader);
                                    break;
                                case ClassIDType.Transform:
                                    obj = new Transform(objectReader);
                                    break;
                                case ClassIDType.VideoClip:
                                    obj = new VideoClip(objectReader);
                                    break;
                                case ClassIDType.ResourceManager:
                                    obj = new ResourceManager(objectReader);
                                    break;
                                default:
                                    obj = new Object(objectReader);
                                    break;
                            }
                            localObjects.Add(obj);
                        }
                        catch (Exception e)
                        {
                            var errCount = System.Threading.Interlocked.Increment(ref errorCount);
                            if (errCount <= 100)
                            {
                                var sb = new StringBuilder();
                                sb.AppendLine("Unable to load object")
                                    .AppendLine($"Assets {assetsFile.fileName}")
                                    .AppendLine($"Path {assetsFile.originalPath}")
                                    .AppendLine($"Type {objectReader.type}")
                                    .AppendLine($"PathID {objectInfo.m_PathID}");

                                if (objectInfo.serializedType?.m_Type?.m_Nodes != null)
                                {
                                    sb.AppendLine("TypeTree Dump:");
                                    foreach (var node in objectInfo.serializedType.m_Type.m_Nodes)
                                    {
                                        sb.AppendLine($"[{node.m_Level}] {node.m_Type} {node.m_Name}");
                                    }
                                }

                                if (errCount <= 20)
                                {
                                    // TODO: REMOVE LATER - Hex dump for Unity 6 debugging only
                                    try
                                    {
                                        objectReader.Reset(); // go back to start
                                        int bytesToRead = Math.Min((int)objectInfo.byteSize, 1024);
                                        var rawBytes = objectReader.ReadBytes(bytesToRead);
                                        var hexDump = BitConverter.ToString(rawBytes).Replace("-", " ");
                                        sb.AppendLine($"Raw Object Bytes (first {bytesToRead} bytes):");
                                        sb.AppendLine(hexDump);
                                    }
                                    catch (Exception ex2)
                                    {
                                        sb.AppendLine($"Failed to dump bytes: {ex2.Message}");
                                    }
                                    // END TODO: REMOVE LATER
                                }

                                sb.Append(e);
                                Logger.Error(sb.ToString());
                            }
                            else if (errCount == 101)
                            {
                                Logger.Error("Too many errors encountered. Further error details are suppressed to maintain stability.");
                            }
                        }

                        var currentProgress = System.Threading.Interlocked.Increment(ref progressValue);
                        Progress.Report(currentProgress, progressCount);

                        return localReader;
                    },
                    localReader => localReader.Dispose()
                );

                var pathIdToIndex = new Dictionary<long, int>();
                for (int idx = 0; idx < assetsFile.m_Objects.Count; idx++)
                {
                    pathIdToIndex[assetsFile.m_Objects[idx].m_PathID] = idx;
                }
                var sortedObjects = localObjects.OrderBy(obj => pathIdToIndex[obj.m_PathID]).ToList();
                foreach (var obj in sortedObjects)
                {
                    assetsFile.AddObject(obj);
                }
            }
        }

        private void ProcessAssets()
        {
            Logger.Info("Process Assets...");

            var spriteAtlasCache = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is SpriteAtlas m_SpriteAtlas && m_SpriteAtlas.m_RenderDataMap != null)
                    {
                        foreach (var key in m_SpriteAtlas.m_RenderDataMap.Keys)
                        {
                            spriteAtlasCache[key] = m_SpriteAtlas;
                        }
                    }
                }
            }

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is GameObject m_GameObject)
                    {
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            if (pptr.TryGet(out var m_Component))
                            {
                                switch (m_Component)
                                {
                                    case Transform m_Transform:
                                        m_GameObject.m_Transform = m_Transform;
                                        break;
                                    case MeshRenderer m_MeshRenderer:
                                        m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                        break;
                                    case MeshFilter m_MeshFilter:
                                        m_GameObject.m_MeshFilter = m_MeshFilter;
                                        break;
                                    case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                        m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                        break;
                                    case Animator m_Animator:
                                        m_GameObject.m_Animator = m_Animator;
                                        break;
                                    case Animation m_Animation:
                                        m_GameObject.m_Animation = m_Animation;
                                        break;
                                }
                            }
                        }
                    }
                    else if (obj is SpriteAtlas m_SpriteAtlas)
                    {
                        foreach (var m_PackedSprite in m_SpriteAtlas.m_PackedSprites)
                        {
                            if (m_PackedSprite.TryGet(out var m_Sprite))
                            {
                                if (m_Sprite.m_SpriteAtlas.IsNull)
                                {
                                    m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                }
                                else
                                {
                                    m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlaOld);
                                    if (m_SpriteAtlaOld != null && m_SpriteAtlaOld.m_IsVariant)
                                    {
                                        m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                    }
                                }
                            }
                        }
                    }
                    else if (obj is Sprite m_Sprite)
                    {
                        if (m_Sprite.m_SpriteAtlas.IsNull && m_Sprite.m_RenderDataKey.Key != Guid.Empty)
                        {
                            if (spriteAtlasCache.TryGetValue(m_Sprite.m_RenderDataKey, out var atlas))
                            {
                                m_Sprite.m_SpriteAtlas.Set(atlas);
                            }
                        }
                    }
                }
            }
        }
    }
}
