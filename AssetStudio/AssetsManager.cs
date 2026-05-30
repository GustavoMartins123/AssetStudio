using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static AssetStudio.ImportHelper;

namespace AssetStudio
{
    public enum MemoryPressureResult
    {
        Cancel,
        Continue,
        StopAndKeep
    }

    public class AssetsManager
    {
        private const double DefaultLoadThreadRatio = 0.4;
        private const double DefaultReadThreadRatio = 0.4;
        private const int DefaultMemoryLimitPercent = 90;

        public static bool DisableMemoryPressureCheck = false;
        public static Func<string, int, int, MemoryPressureResult> MemoryPressureCallback;
        public static Func<bool> ShouldYieldForUserInteraction;
        public static volatile bool ShouldStopLoading = false;

        public string SpecifyUnityVersion;
        public string ProjectRoot;
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();
        public ConcurrentBag<Stream> bundleStreams = new ConcurrentBag<Stream>();
        public ProjectIndex ProjectIndex = new ProjectIndex();
        public bool LazyLoading = false;

        internal ConcurrentDictionary<string, int> assetsFileIndexCache = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal ConcurrentDictionary<string, BinaryReader> resourceFileReaders = new ConcurrentDictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        private ConcurrentBag<string> importFiles = new ConcurrentBag<string>();
        private ConcurrentDictionary<string, byte> importFilesHash = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, byte> noexistFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, byte> assetsFileListHash = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public readonly object loadLock = new object();
        private ConcurrentQueue<string> loadQueue;
        private int loadTotalCount;

        private static string NormalizeResourceKey(string value)
        {
            return value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        private void RegisterResourceFileReader(BinaryReader reader, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                resourceFileReaders[key] = reader;
                var normalizedKey = NormalizeResourceKey(key);
                resourceFileReaders[normalizedKey] = reader;
            }
        }

        public void LoadFiles(params string[] files)
        {
            LoadFilesAsync(files).GetAwaiter().GetResult();
        }

        public async System.Threading.Tasks.Task LoadFilesAsync(params string[] files)
        {
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = path;
            }
            MergeSplitAssets(path);
            var toReadFile = ProcessingSplitFiles(files);
            await LoadAsync(toReadFile);
        }

        public void LoadFolder(string path)
        {
            LoadFolderAsync(path).GetAwaiter().GetResult();
        }

        public async System.Threading.Tasks.Task LoadFolderAsync(string path)
        {
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            if (string.IsNullOrEmpty(ProjectRoot))
            {
                ProjectRoot = Path.GetFullPath(path);
            }
            MergeSplitAssets(path, true);
            var files = ImportHelper.GetFilesSafe(path, "*.*", true);
            var toReadFile = ProcessingSplitFiles(files);
            await LoadAsync(toReadFile);
        }

        private async System.Threading.Tasks.Task YieldForUserInteractionIfNeededAsync()
        {
            if (!LazyLoading)
            {
                return;
            }

            var shouldYield = ShouldYieldForUserInteraction;
            if (shouldYield == null)
            {
                return;
            }

            while (!ShouldStopLoading && shouldYield())
            {
                await System.Threading.Tasks.Task.Delay(40);
            }
        }

        private async System.Threading.Tasks.Task LoadAsync(string[] files)
        {
            loadQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            loadTotalCount = files.Length;

            foreach (var file in files)
            {
                importFiles.Add(file);
                importFilesHash.TryAdd(Path.GetFileName(file), 0);
                loadQueue.Enqueue(file);
            }

            Progress.Reset();

            int completedCount = 0;
            int threadCount = Math.Min(GetConfiguredThreadCount("ASSETSTUDIO_LOAD_THREADS", DefaultLoadThreadRatio), loadTotalCount);
            if (threadCount < 1) threadCount = 1;

            int activeWorkers = threadCount;
            var tasks = new System.Threading.Tasks.Task[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = System.Threading.Tasks.Task.Run(async () =>
                {
                    bool isWorkerActive = true;
                    while (true)
                    {
                        await YieldForUserInteractionIfNeededAsync();

                        if (System.Threading.Volatile.Read(ref ShouldStopLoading))
                        {
                            if (isWorkerActive)
                            {
                                System.Threading.Interlocked.Decrement(ref activeWorkers);
                                isWorkerActive = false;
                            }
                            break;
                        }

                        string file = null;
                        if (loadQueue.TryDequeue(out file))
                        {
                            ThrowIfMemoryPressureTooHigh("loading files");

                            if (System.Threading.Volatile.Read(ref ShouldStopLoading))
                            {
                                if (isWorkerActive)
                                {
                                    System.Threading.Interlocked.Decrement(ref activeWorkers);
                                    isWorkerActive = false;
                                }
                                break;
                            }

                            if (!isWorkerActive)
                            {
                                System.Threading.Interlocked.Increment(ref activeWorkers);
                                isWorkerActive = true;
                            }

                            try
                            {
                                LoadFile(file);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error loading file {file}: {ex.Message}", ex);
                            }
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

                            await System.Threading.Tasks.Task.Delay(10);
                        }
                    }
                });
            }
            await System.Threading.Tasks.Task.WhenAll(tasks);

            loadQueue = null;

            importFiles = new ConcurrentBag<string>();
            importFilesHash.Clear();
            noexistFiles.Clear();
            assetsFileListHash.Clear();

            await ReadAssetsAsync();
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
            bool alreadyLoaded = assetsFileListHash.ContainsKey(reader.FileName);

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
                    }
                    assetsFileListHash.TryAdd(assetsFile.fileName, 0);

                    foreach (var sharedFile in assetsFile.m_Externals)
                    {
                        var sharedFileName = sharedFile.fileName;

                        bool containsShared = importFilesHash.ContainsKey(sharedFileName);

                        if (!containsShared)
                        {
                            var sharedFilePath = Path.Combine(Path.GetDirectoryName(reader.FullPath), sharedFileName);
                            
                            bool containsNoExist = noexistFiles.ContainsKey(sharedFilePath);

                            if (!containsNoExist)
                            {
                                 if (!File.Exists(sharedFilePath))
                                {
                                    var findFiles = ImportHelper.GetFilesSafe(Path.GetDirectoryName(reader.FullPath), sharedFileName, true);
                                    if (findFiles.Length > 0)
                                    {
                                        sharedFilePath = findFiles[0];
                                    }
                                }
                                if (File.Exists(sharedFilePath))
                                {
                                    importFiles.Add(sharedFilePath);
                                    importFilesHash.TryAdd(sharedFileName, 0);
                                    loadQueue?.Enqueue(sharedFilePath);
                                    System.Threading.Interlocked.Increment(ref loadTotalCount);
                                }
                                else
                                {
                                    noexistFiles.TryAdd(sharedFilePath, 0);
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
            bool alreadyLoaded = assetsFileListHash.ContainsKey(reader.FileName);

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
                    }
                    assetsFileListHash.TryAdd(assetsFile.fileName, 0);
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                    RegisterResourceFileReader(reader, reader.FileName, reader.FullPath);
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
                    bundleStreams.Add(bundleFile.BlocksStream);
                }
                foreach (var file in bundleFile.fileList)
                {
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.path);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(subReader, originalPath ?? reader.FullPath, bundleFile.m_Header.unityRevision);
                    }
                    else
                    {
                        RegisterResourceFileReader(subReader, file.fileName, file.path, subReader.FullPath); //TODO
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
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.path);
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
                            RegisterResourceFileReader(subReader, file.fileName, file.path, subReader.FullPath); //TODO
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
                                importFilesHash.TryAdd(baseName, 0);
                            }
                        }
                        else
                        {
                            importFilesHash.TryAdd(entry.Name, 0);
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
                            if (entryReader.FileType == FileType.ResourceFile)
                            {
                                entryReader.Position = 0;
                                RegisterResourceFileReader(entryReader, entry.Name, entry.FullName, entryReader.FullPath);
                            }
                            else
                            {
                                LoadFile(entryReader);
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
            DisableMemoryPressureCheck = false;
            ShouldStopLoading = false;
            ProjectIndex.Clear();
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

            foreach (var stream in bundleStreams)
            {
                stream.Dispose();
            }
            bundleStreams = new ConcurrentBag<Stream>();

            assetsFileIndexCache.Clear();
        }

        public static Object CreateObjectFromReader(ObjectReader objectReader)
        {
            switch (objectReader.type)
            {
                case ClassIDType.Animation:
                    return new Animation(objectReader);
                case ClassIDType.AnimationClip:
                    return new AnimationClip(objectReader);
                case ClassIDType.Animator:
                    return new Animator(objectReader);
                case ClassIDType.AnimatorController:
                    return new AnimatorController(objectReader);
                case ClassIDType.AnimatorOverrideController:
                    return new AnimatorOverrideController(objectReader);
                case ClassIDType.AssetBundle:
                    return new AssetBundle(objectReader);
                case ClassIDType.AudioClip:
                    return new AudioClip(objectReader);
                case ClassIDType.Avatar:
                    return new Avatar(objectReader);
                case ClassIDType.Font:
                    return new Font(objectReader);
                case ClassIDType.GameObject:
                    return new GameObject(objectReader);
                case ClassIDType.Material:
                    return new Material(objectReader);
                case ClassIDType.Mesh:
                    return new Mesh(objectReader);
                case ClassIDType.MeshFilter:
                    return new MeshFilter(objectReader);
                case ClassIDType.MeshRenderer:
                    return new MeshRenderer(objectReader);
                case ClassIDType.MonoBehaviour:
                    return new MonoBehaviour(objectReader);
                case ClassIDType.MonoScript:
                    return new MonoScript(objectReader);
                case ClassIDType.MovieTexture:
                    return new MovieTexture(objectReader);
                case ClassIDType.PlayerSettings:
                    return new PlayerSettings(objectReader);
                case ClassIDType.RectTransform:
                    return new RectTransform(objectReader);
                case ClassIDType.Shader:
                    return new Shader(objectReader);
                case ClassIDType.SkinnedMeshRenderer:
                    return new SkinnedMeshRenderer(objectReader);
                case ClassIDType.Sprite:
                    return new Sprite(objectReader);
                case ClassIDType.SpriteAtlas:
                    return new SpriteAtlas(objectReader);
                case ClassIDType.TextAsset:
                    return new TextAsset(objectReader);
                case ClassIDType.Texture2D:
                    return new Texture2D(objectReader);
                case ClassIDType.Transform:
                    return new Transform(objectReader);
                case ClassIDType.VideoClip:
                    return new VideoClip(objectReader);
                case ClassIDType.VideoPlayer:
                    return new VideoPlayer(objectReader);
                case ClassIDType.ResourceManager:
                    return new ResourceManager(objectReader);
                default:
                    return new Object(objectReader);
            }
        }

        public Object ResolveHandle(AssetHandle handle)
        {
            if (handle == null) return null;
            if (handle.RealObject != null) return handle.RealObject;

            lock (handle)
            {
                if (handle.RealObject != null) return handle.RealObject;

                try
                {
                    var assetsFile = handle.SourceFile;
                    if (assetsFile?.reader == null)
                    {
                        return null;
                    }

                    using (var localReader = assetsFile.reader.Clone())
                    {
                        var objectInfo = new ObjectInfo
                        {
                            m_PathID = handle.PathID,
                            byteStart = handle.ByteStart,
                            byteSize = (uint)handle.ByteSize,
                            classID = (int)handle.Type,
                            serializedType = assetsFile.m_Objects.FirstOrDefault(x => x.m_PathID == handle.PathID)?.serializedType
                        };

                        var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                        var obj = CreateObjectFromReader(objectReader);
                        handle.RealObject = obj;
                        
                        lock (assetsFile)
                        {
                            if (!assetsFile.ObjectsDic.ContainsKey(obj.m_PathID))
                            {
                                assetsFile.AddObject(obj);
                            }
                        }
                        return obj;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error materializing object {handle.TypeString} PathID={handle.PathID}", ex);
                    return null;
                }
            }
        }

        private async System.Threading.Tasks.Task ReadAssetsAsync()
        {
            Logger.Info("Read assets...");

            var progressCount = assetsFileList.Sum(x => x.m_Objects.Count);
            int progressValue = 0;
            int errorCount = 0;
            Progress.Reset();

            if (LazyLoading)
            {
                var unprocessedFiles = assetsFileList.Where(x => !x.IsProcessed).ToList();
                var maxDegreeOfParallelism = GetConfiguredThreadCount("ASSETSTUDIO_READ_THREADS", DefaultReadThreadRatio);
                using (var semaphore = new System.Threading.SemaphoreSlim(maxDegreeOfParallelism))
                {
                    var tasks = unprocessedFiles.Select(async assetsFile =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            await YieldForUserInteractionIfNeededAsync();

                            assetsFile.IsProcessed = true;

                            // Lookup cached handles using O(1) dictionary index
                            bool hasCachedHandles = false;
                            foreach (var handle in ProjectIndex.GetHandlesForFile(assetsFile.fileName))
                            {
                                handle.SourceFile = assetsFile;
                                hasCachedHandles = true;
                            }

                            if (hasCachedHandles)
                            {
                                // Eagerly materialize containers (AssetBundle / ResourceManager)
                                // so container mapping works.
                                foreach (var objectInfo in assetsFile.m_Objects)
                                {
                                    await YieldForUserInteractionIfNeededAsync();

                                    var classID = objectInfo.classID;
                                    ClassIDType type = ClassIDType.UnknownType;
                                    if (Enum.IsDefined(typeof(ClassIDType), classID))
                                    {
                                        type = (ClassIDType)classID;
                                    }

                                    if (type == ClassIDType.AssetBundle || type == ClassIDType.ResourceManager)
                                    {
                                        try
                                        {
                                            using var localReader = assetsFile.reader.Clone();
                                            var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                                            var obj = CreateObjectFromReader(objectReader);
                                            assetsFile.AddObject(obj);

                                            // Link to the handle
                                            var handle = ProjectIndex.GetHandle($"{assetsFile.fileName}#{objectInfo.m_PathID}");
                                            if (handle != null)
                                            {
                                                handle.RealObject = obj;
                                            }
                                        }
                                        catch
                                        {
                                            // Skip container loading errors
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var localObjects = new List<Object>();
                                using var localReader = assetsFile.reader.Clone();

                                foreach (var objectInfo in assetsFile.m_Objects)
                                {
                                    if ((System.Threading.Volatile.Read(ref progressValue) & 0x3f) == 0)
                                    {
                                        await YieldForUserInteractionIfNeededAsync();
                                    }

                                    if (System.Threading.Volatile.Read(ref ShouldStopLoading))
                                    {
                                        break;
                                    }

                                    if ((System.Threading.Volatile.Read(ref progressValue) & 0xff) == 0)
                                    {
                                        ThrowIfMemoryPressureTooHigh("reading assets (lazy)");
                                    }

                                    if (System.Threading.Volatile.Read(ref ShouldStopLoading))
                                    {
                                        break;
                                    }

                                    var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                                    
                                    ClassIDType type = ClassIDType.UnknownType;
                                    if (Enum.IsDefined(typeof(ClassIDType), objectInfo.classID))
                                    {
                                        type = (ClassIDType)objectInfo.classID;
                                    }

                                    var handle = new AssetHandle
                                    {
                                        UniqueID = $"{assetsFile.fileName}#{objectInfo.m_PathID}",
                                        Type = type,
                                        OriginalPath = assetsFile.originalPath,
                                        SerializedFileName = assetsFile.fileName,
                                        SourceFile = assetsFile,
                                        PathID = objectInfo.m_PathID,
                                        ByteStart = objectInfo.byteStart,
                                        ByteSize = objectInfo.byteSize
                                    };

                                    string name = null;
                                    if (type == ClassIDType.AssetBundle || type == ClassIDType.ResourceManager)
                                    {
                                        try
                                        {
                                            var obj = CreateObjectFromReader(objectReader);
                                            handle.RealObject = obj;
                                            localObjects.Add(obj);
                                            if (obj is AssetBundle bundle)
                                                name = bundle.m_Name;
                                            else if (obj is ResourceManager rm)
                                                name = "ResourceManager";
                                        }
                                        catch
                                        {
                                            // Fallback if container loading fails
                                        }
                                    }
                                    else
                                    {
                                        name = AssetHandle.TryReadObjectName(objectReader);
                                    }

                                    handle.Name = name ?? $"Unnamed_{type}_{objectInfo.m_PathID}";
                                    ProjectIndex.AddHandle(handle);

                                    var currentProgress = System.Threading.Interlocked.Increment(ref progressValue);
                                    Progress.Report(currentProgress, progressCount);
                                }

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
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    await System.Threading.Tasks.Task.WhenAll(tasks);
                }
                return;
            }

            foreach (var assetsFile in assetsFileList)
            {
                if (assetsFile.IsProcessed) continue;
                assetsFile.IsProcessed = true;
                var localObjects = new System.Collections.Concurrent.ConcurrentBag<Object>();

                var parallelOptions = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = GetConfiguredThreadCount("ASSETSTUDIO_READ_THREADS", DefaultReadThreadRatio)
                };

                System.Threading.Tasks.Parallel.ForEach(
                    assetsFile.m_Objects,
                    parallelOptions,
                    () => assetsFile.reader.Clone(),
                    (objectInfo, state, localReader) =>
                    {
                        if (System.Threading.Volatile.Read(ref ShouldStopLoading))
                        {
                            state.Stop();
                            return localReader;
                        }

                        if ((System.Threading.Volatile.Read(ref progressValue) & 0xff) == 0)
                        {
                            ThrowIfMemoryPressureTooHigh("reading assets");
                        }

                        if (System.Threading.Volatile.Read(ref ShouldStopLoading))
                        {
                            state.Stop();
                            return localReader;
                        }

                        var objectReader = new ObjectReader(localReader, assetsFile, objectInfo);
                        try
                        {
                            Object obj = CreateObjectFromReader(objectReader);
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

        public static int GetConfiguredThreadCount(string environmentVariable, double defaultRatio)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariable);
            if (int.TryParse(value, out var configuredValue) && configuredValue > 0)
            {
                return configuredValue;
            }

            var processorCount = Environment.ProcessorCount;
            if (processorCount < 1)
            {
                return 1;
            }

            return Math.Max(1, (int)Math.Floor(processorCount * defaultRatio));
        }

        private static int GetConfiguredMemoryLimitPercent()
        {
            var value = Environment.GetEnvironmentVariable("ASSETSTUDIO_MEMORY_LIMIT_PERCENT");
            if (int.TryParse(value, out var configuredValue) && configuredValue > 0)
            {
                return Math.Min(configuredValue, 100);
            }

            return DefaultMemoryLimitPercent;
        }

#if NET6_0_OR_GREATER
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private static bool TryGetLinuxAvailableMemory(out ulong availableBytes)
        {
            availableBytes = 0;
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    ulong memAvailable = 0;
                    ulong swapFree = 0;
                    foreach (var line in File.ReadLines("/proc/meminfo"))
                    {
                        if (line.StartsWith("MemAvailable:"))
                        {
                            memAvailable = ParseMeminfoValue(line);
                        }
                        else if (line.StartsWith("SwapFree:"))
                        {
                            swapFree = ParseMeminfoValue(line);
                        }
                    }
                    availableBytes = memAvailable + swapFree;
                    return true;
                }
            }
            catch
            {
                // Ignore and fallback
            }
            return false;
        }

        private static ulong ParseMeminfoValue(string line)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && ulong.TryParse(parts[1], out var kb))
            {
                return kb * 1024;
            }
            return 0;
        }
#endif

        public static void ThrowIfMemoryPressureTooHigh(string operation)
        {
#if NET6_0_OR_GREATER
            if (DisableMemoryPressureCheck)
            {
                return;
            }

            var limitPercent = GetConfiguredMemoryLimitPercent();
            if (limitPercent >= 100)
            {
                return;
            }

            if (!TryGetMemoryLoadPercent(out var memoryLoadPercent))
            {
                return;
            }

            if (memoryLoadPercent < limitPercent)
            {
                return;
            }

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            if (TryGetMemoryLoadPercent(out memoryLoadPercent) && memoryLoadPercent >= limitPercent)
            {
                bool isRealPressure = true;
                const ulong minAvailableCommitBytes = 1536UL * 1024UL * 1024UL; // 1.5 GB

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus))
                    {
                        if (memStatus.ullAvailPageFile >= minAvailableCommitBytes)
                        {
                            isRealPressure = false;
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (TryGetLinuxAvailableMemory(out var availableBytes))
                    {
                        if (availableBytes >= minAvailableCommitBytes)
                        {
                            isRealPressure = false;
                        }
                    }
                }

                if (isRealPressure)
                {
                    if (MemoryPressureCallback != null)
                    {
                        var result = MemoryPressureCallback(operation, memoryLoadPercent, limitPercent);
                        if (result == MemoryPressureResult.Continue)
                        {
                            DisableMemoryPressureCheck = true;
                            return;
                        }
                        else if (result == MemoryPressureResult.StopAndKeep)
                        {
                            ShouldStopLoading = true;
                            DisableMemoryPressureCheck = true;
                            return;
                        }
                    }
                    throw new MemoryPressureException(operation, memoryLoadPercent, limitPercent);
                }
            }
#endif
        }

        private static bool TryGetMemoryLoadPercent(out int memoryLoadPercent)
        {
            memoryLoadPercent = 0;
#if NET6_0_OR_GREATER
            var memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.HighMemoryLoadThresholdBytes <= 0 || memoryInfo.MemoryLoadBytes <= 0)
            {
                return false;
            }

            memoryLoadPercent = (int)Math.Ceiling(memoryInfo.MemoryLoadBytes * 100d / memoryInfo.HighMemoryLoadThresholdBytes);
            return true;
#else
            return false;
#endif
        }

        private void ProcessAssets()
        {
            Logger.Info("Process Assets...");

            var processOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = GetConfiguredThreadCount("ASSETSTUDIO_PROCESS_THREADS", DefaultReadThreadRatio)
            };

            var spriteAtlasEntries = new List<KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>>[assetsFileList.Count];

            System.Threading.Tasks.Parallel.For(0, assetsFileList.Count, processOptions, fileIndex =>
            {
                var assetsFile = assetsFileList[fileIndex];
                List<KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>> entries = null;
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is SpriteAtlas m_SpriteAtlas && m_SpriteAtlas.m_RenderDataMap != null)
                    {
                        entries ??= new List<KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>>(m_SpriteAtlas.m_RenderDataMap.Count);
                        foreach (var key in m_SpriteAtlas.m_RenderDataMap.Keys)
                        {
                            entries.Add(new KeyValuePair<KeyValuePair<Guid, long>, SpriteAtlas>(key, m_SpriteAtlas));
                        }
                    }
                }

                spriteAtlasEntries[fileIndex] = entries;
            });

            var spriteAtlasCache = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();
            foreach (var entries in spriteAtlasEntries)
            {
                if (entries == null)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    spriteAtlasCache[entry.Key] = entry.Value;
                }
            }

            var gameObjects = assetsFileList
                .SelectMany(assetsFile => assetsFile.Objects)
                .OfType<GameObject>()
                .ToArray();

            System.Threading.Tasks.Parallel.ForEach(gameObjects, processOptions, m_GameObject =>
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
            });

            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is SpriteAtlas m_SpriteAtlas)
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
