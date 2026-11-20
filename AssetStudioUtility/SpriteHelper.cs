using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public static class SpriteHelper
    {
        private const int MaxCachedAtlases = 4;
        private const long MaxCachedBytes = 256L * 1024L * 1024L; // 256 MiB

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<AtlasCacheKey, AtlasCacheEntry> _cacheByKey = new Dictionary<AtlasCacheKey, AtlasCacheEntry>();
        private static readonly LinkedList<AtlasCacheEntry> _lruList = new LinkedList<AtlasCacheEntry>();
        private static readonly Dictionary<AtlasCacheKey, InFlightDecode> _inFlight = new Dictionary<AtlasCacheKey, InFlightDecode>();
        private static long _currentCachedBytes;
        private static long _cacheGeneration;

        public static void ClearAtlasCache()
        {
            List<AtlasCacheEntry> toDispose = null;
            lock (_cacheLock)
            {
                _cacheGeneration++;
                while (_lruList.First != null)
                {
                    RemoveEntryLocked(_lruList.First.Value, ref toDispose);
                }
            }
            DisposeEntries(toDispose);
        }

        public static Image<Bgra32> GetImage(this Sprite m_Sprite)
        {
            if (m_Sprite.m_SpriteAtlas != null && m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlas))
            {
                if (m_SpriteAtlas.m_RenderDataMap.TryGetValue(m_Sprite.m_RenderDataKey, out var spriteAtlasData) && spriteAtlasData.texture.TryGet(out var m_Texture2D))
                {
                    return CutImage(m_Sprite, m_Texture2D, spriteAtlasData.textureRect, spriteAtlasData.textureRectOffset, spriteAtlasData.downscaleMultiplier, spriteAtlasData.settingsRaw);
                }
            }
            else
            {
                if (m_Sprite.m_RD.texture.TryGet(out var m_Texture2D))
                {
                    return CutImage(m_Sprite, m_Texture2D, m_Sprite.m_RD.textureRect, m_Sprite.m_RD.textureRectOffset, m_Sprite.m_RD.downscaleMultiplier, m_Sprite.m_RD.settingsRaw);
                }
            }
            return null;
        }

        private static Image<Bgra32> CutImage(Sprite m_Sprite, Texture2D m_Texture2D, Rectf textureRect, Vector2 textureRectOffset, float downscaleMultiplier, SpriteSettings settingsRaw)
        {
            if (m_Texture2D == null)
            {
                return null;
            }

            int targetWidth = 0;
            int targetHeight = 0;
            if (downscaleMultiplier > 0f && downscaleMultiplier != 1f)
            {
                targetWidth = (int)(m_Texture2D.m_Width / downscaleMultiplier);
                targetHeight = (int)(m_Texture2D.m_Height / downscaleMultiplier);
                if (targetWidth <= 0 || targetHeight <= 0)
                {
                    return null;
                }
            }

            Image<Bgra32> spriteImage;
            Rectangle rect;
            using (var atlasLease = AcquireAtlasLease(m_Texture2D, targetWidth, targetHeight))
            {
                if (atlasLease.Entry == null)
                {
                    return null;
                }

                spriteImage = CropSpriteRegion(atlasLease.Entry, textureRect, out rect);
                if (spriteImage == null)
                {
                    return null;
                }
            }

            try
            {
                if (settingsRaw.packed == 1)
                {
                    //RotateAndFlip
                    switch (settingsRaw.packingRotation)
                    {
                        case SpritePackingRotation.FlipHorizontal:
                            spriteImage.Mutate(x => x.Flip(FlipMode.Horizontal));
                            break;
                        case SpritePackingRotation.FlipVertical:
                            spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                            break;
                        case SpritePackingRotation.Rotate180:
                            spriteImage.Mutate(x => x.Rotate(180));
                            break;
                        case SpritePackingRotation.Rotate90:
                            spriteImage.Mutate(x => x.Rotate(270));
                            break;
                    }
                }

                //Tight
                if (settingsRaw.packingMode == SpritePackingMode.Tight)
                {
                    try
                    {
                        var triangles = GetTriangles(m_Sprite.m_RD);
                        var polygons = triangles.Select(x => new Polygon(new LinearLineSegment(x.Select(y => new PointF(y.X, y.Y)).ToArray()))).ToArray();
                        IPathCollection path = new PathCollection(polygons);
                        var matrix = Matrix3x2.CreateScale(m_Sprite.m_PixelsToUnits);
                        matrix *= Matrix3x2.CreateTranslation(m_Sprite.m_Rect.width * m_Sprite.m_Pivot.X - textureRectOffset.X, m_Sprite.m_Rect.height * m_Sprite.m_Pivot.Y - textureRectOffset.Y);
                        path = path.Transform(matrix);
                        var graphicsOptions = new GraphicsOptions
                        {
                            Antialias = false,
                            AlphaCompositionMode = PixelAlphaCompositionMode.DestOut
                        };
                        var options = new DrawingOptions
                        {
                            GraphicsOptions = graphicsOptions
                        };
                        using (var mask = new Image<Bgra32>(rect.Width, rect.Height, SixLabors.ImageSharp.Color.Black))
                        {
                            mask.Mutate(x => x.Fill(options, SixLabors.ImageSharp.Color.Red, path));
                            var bursh = new ImageBrush(mask);
                            spriteImage.Mutate(x => x.Fill(options, bursh));
                            spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                            return spriteImage;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to apply Tight packing mode for Sprite {m_Sprite.m_Name}, falling back to Rectangle packing mode: {ex.Message}");
                    }
                }

                //Rectangle
                spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                return spriteImage;
            }
            catch
            {
                spriteImage.Dispose();
                throw;
            }
        }

        private static Image<Bgra32> CropSpriteRegion(AtlasCacheEntry entry, Rectf textureRect, out Rectangle rect)
        {
            lock (entry.SyncRoot)
            {
                var originalImage = entry.Image;
                var rectX = Math.Max(0, (int)Math.Floor(textureRect.x));
                var rectY = Math.Max(0, (int)Math.Floor(textureRect.y));
                var rectRight = Math.Min((int)Math.Ceiling(textureRect.x + textureRect.width), originalImage.Width);
                var rectBottom = Math.Min((int)Math.Ceiling(textureRect.y + textureRect.height), originalImage.Height);
                var rectWidth = rectRight - rectX;
                var rectHeight = rectBottom - rectY;
                if (rectWidth <= 0 || rectHeight <= 0)
                {
                    rect = default;
                    return null;
                }

                rect = new Rectangle(rectX, rectY, rectWidth, rectHeight);
                var spriteImage = new Image<Bgra32>(rectWidth, rectHeight);
                try
                {
                    originalImage.ProcessPixelRows(spriteImage, (srcAccessor, dstAccessor) =>
                    {
                        for (int y = 0; y < rectHeight; y++)
                        {
                            var srcRow = srcAccessor.GetRowSpan(rectY + y).Slice(rectX, rectWidth);
                            var dstRow = dstAccessor.GetRowSpan(y);
                            srcRow.CopyTo(dstRow);
                        }
                    });
                    return spriteImage;
                }
                catch
                {
                    spriteImage.Dispose();
                    throw;
                }
            }
        }

        private static AtlasLease AcquireAtlasLease(Texture2D m_Texture2D, int targetWidth, int targetHeight)
        {
            var key = new AtlasCacheKey(
                m_Texture2D.assetsFile?.fullName,
                m_Texture2D.m_PathID,
                RuntimeHelpers.GetHashCode(m_Texture2D),
                targetWidth,
                targetHeight);

            AtlasCacheEntry cachedHit = null;
            InFlightDecode flightToWait = null;
            InFlightDecode flightToLead = null;
            long capturedGeneration = 0;
            bool bypassInFlight = false;
            List<AtlasCacheEntry> toDispose = null;

            lock (_cacheLock)
            {
                PruneDeadEntriesLocked(ref toDispose);

                if (_cacheByKey.TryGetValue(key, out var cached))
                {
                    if (cached.Matches(m_Texture2D))
                    {
                        if (cached.LruNode != _lruList.First)
                        {
                            _lruList.Remove(cached.LruNode);
                            _lruList.AddFirst(cached.LruNode);
                        }
                        cached.AddRef();
                        cachedHit = cached;
                    }
                    else
                    {
                        RemoveEntryLocked(cached, ref toDispose);
                    }
                }

                if (cachedHit == null)
                {
                    if (_inFlight.TryGetValue(key, out var existingFlight))
                    {
                        if (existingFlight.Matches(m_Texture2D))
                        {
                            existingFlight.WaiterCount++;
                            flightToWait = existingFlight;
                        }
                        else
                        {
                            // Extremely rare identity-hash collision with another live Texture2D currently decoding.
                            bypassInFlight = true;
                        }
                    }
                    else
                    {
                        flightToLead = new InFlightDecode(m_Texture2D);
                        _inFlight[key] = flightToLead;
                        capturedGeneration = _cacheGeneration;
                    }
                }
            }

            DisposeEntries(toDispose);

            if (cachedHit != null)
            {
                return new AtlasLease(cachedHit);
            }

            if (flightToWait != null)
            {
                var awaited = flightToWait.Completion.Task.GetAwaiter().GetResult();
                return awaited != null ? new AtlasLease(awaited) : default;
            }

            if (bypassInFlight)
            {
                var directImage = DecodeAtlasImage(m_Texture2D, targetWidth, targetHeight);
                if (directImage == null)
                {
                    return default;
                }

                var directEntry = new AtlasCacheEntry(key, m_Texture2D, directImage, (long)directImage.Width * directImage.Height * 4L);
                directEntry.InitializeRefCount(1);
                return new AtlasLease(directEntry);
            }

            AtlasCacheEntry decodedEntry = null;
            try
            {
                var decoded = DecodeAtlasImage(m_Texture2D, targetWidth, targetHeight);
                if (decoded != null)
                {
                    long estimatedBytes = (long)decoded.Width * decoded.Height * 4L;
                    decodedEntry = new AtlasCacheEntry(key, m_Texture2D, decoded, estimatedBytes);
                }
            }
            finally
            {
                List<AtlasCacheEntry> evictedToDispose = null;
                lock (_cacheLock)
                {
                    if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flightToLead))
                    {
                        _inFlight.Remove(key);
                    }

                    if (decodedEntry != null)
                    {
                        // Reserve 1 lease for the leader plus 1 lease for every waiter that joined while in-flight.
                        decodedEntry.InitializeRefCount(flightToLead.WaiterCount);

                        if (capturedGeneration == _cacheGeneration && decodedEntry.EstimatedBytes <= MaxCachedBytes)
                        {
                            if (_cacheByKey.TryGetValue(key, out var existing))
                            {
                                RemoveEntryLocked(existing, ref evictedToDispose);
                            }

                            // Cache membership holds +1 reference until evicted or cleared.
                            decodedEntry.AddRef();
                            decodedEntry.LruNode = _lruList.AddFirst(decodedEntry);
                            _cacheByKey[key] = decodedEntry;
                            _currentCachedBytes += decodedEntry.EstimatedBytes;

                            EvictOverflowLocked(ref evictedToDispose);
                        }
                    }
                }

                flightToLead.Completion.TrySetResult(decodedEntry);
                DisposeEntries(evictedToDispose);
            }

            return decodedEntry != null ? new AtlasLease(decodedEntry) : default;
        }

        private static Image<Bgra32> DecodeAtlasImage(Texture2D m_Texture2D, int targetWidth, int targetHeight)
        {
            var originalImage = m_Texture2D.ConvertToImage(false);
            if (originalImage == null)
            {
                return null;
            }

            if (targetWidth > 0 && targetHeight > 0 && (originalImage.Width != targetWidth || originalImage.Height != targetHeight))
            {
                try
                {
                    originalImage.Mutate(x => x.Resize(targetWidth, targetHeight));
                }
                catch
                {
                    originalImage.Dispose();
                    throw;
                }
            }

            return originalImage;
        }

        private static void PruneDeadEntriesLocked(ref List<AtlasCacheEntry> toDispose)
        {
            var node = _lruList.First;
            while (node != null)
            {
                var next = node.Next;
                if (!node.Value.IsTextureAlive)
                {
                    RemoveEntryLocked(node.Value, ref toDispose);
                }
                node = next;
            }
        }

        private static void EvictOverflowLocked(ref List<AtlasCacheEntry> toDispose)
        {
            while (_lruList.Count > MaxCachedAtlases || _currentCachedBytes > MaxCachedBytes)
            {
                var lru = _lruList.Last;
                if (lru == null)
                {
                    break;
                }
                RemoveEntryLocked(lru.Value, ref toDispose);
            }
        }

        private static void RemoveEntryLocked(AtlasCacheEntry entry, ref List<AtlasCacheEntry> toDispose)
        {
            if (entry.LruNode == null)
            {
                return;
            }

            _lruList.Remove(entry.LruNode);
            entry.LruNode = null;
            if (_cacheByKey.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _cacheByKey.Remove(entry.Key);
            }
            _currentCachedBytes = Math.Max(0L, _currentCachedBytes - entry.EstimatedBytes);

            if (entry.Release())
            {
                toDispose ??= new List<AtlasCacheEntry>();
                toDispose.Add(entry);
            }
        }

        private static void DisposeEntries(List<AtlasCacheEntry> entries)
        {
            if (entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].DisposeImage();
            }
        }

        private readonly struct AtlasCacheKey : IEquatable<AtlasCacheKey>
        {
            public readonly string FileKey;
            public readonly long PathId;
            public readonly int IdentityHash;
            public readonly int TargetWidth;
            public readonly int TargetHeight;

            public AtlasCacheKey(string fileKey, long pathId, int identityHash, int targetWidth, int targetHeight)
            {
                FileKey = fileKey ?? string.Empty;
                PathId = pathId;
                IdentityHash = identityHash;
                TargetWidth = targetWidth;
                TargetHeight = targetHeight;
            }

            public bool Equals(AtlasCacheKey other)
            {
                return PathId == other.PathId
                    && IdentityHash == other.IdentityHash
                    && TargetWidth == other.TargetWidth
                    && TargetHeight == other.TargetHeight
                    && string.Equals(FileKey, other.FileKey, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is AtlasCacheKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(FileKey, PathId, IdentityHash, TargetWidth, TargetHeight);
        }

        private sealed class AtlasCacheEntry
        {
            public readonly AtlasCacheKey Key;
            public readonly WeakReference<Texture2D> TextureRef;
            public readonly Image<Bgra32> Image;
            public readonly long EstimatedBytes;
            public readonly object SyncRoot = new object();
            public LinkedListNode<AtlasCacheEntry> LruNode;

            private int _refCount;

            public AtlasCacheEntry(AtlasCacheKey key, Texture2D texture, Image<Bgra32> image, long estimatedBytes)
            {
                Key = key;
                TextureRef = new WeakReference<Texture2D>(texture);
                Image = image;
                EstimatedBytes = estimatedBytes;
            }

            public bool IsTextureAlive => TextureRef.TryGetTarget(out _);

            public bool Matches(Texture2D texture)
            {
                return TextureRef.TryGetTarget(out var target) && ReferenceEquals(target, texture);
            }

            public void InitializeRefCount(int initialCount)
            {
                Volatile.Write(ref _refCount, initialCount);
            }

            public void AddRef()
            {
                Interlocked.Increment(ref _refCount);
            }

            public bool Release()
            {
                return Interlocked.Decrement(ref _refCount) == 0;
            }

            public void DisposeImage()
            {
                Image.Dispose();
            }
        }

        private sealed class InFlightDecode
        {
            public readonly WeakReference<Texture2D> TextureRef;
            public readonly TaskCompletionSource<AtlasCacheEntry> Completion = new TaskCompletionSource<AtlasCacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int WaiterCount = 1;

            public InFlightDecode(Texture2D texture)
            {
                TextureRef = new WeakReference<Texture2D>(texture);
            }

            public bool Matches(Texture2D texture)
            {
                return TextureRef.TryGetTarget(out var target) && ReferenceEquals(target, texture);
            }
        }

        private readonly struct AtlasLease : IDisposable
        {
            public readonly AtlasCacheEntry Entry;

            public AtlasLease(AtlasCacheEntry entry)
            {
                Entry = entry;
            }

            public void Dispose()
            {
                if (Entry != null && Entry.Release())
                {
                    Entry.DisposeImage();
                }
            }
        }

        private static Vector2[][] GetTriangles(SpriteRenderData m_RD)
        {
            if (m_RD.vertices != null) //5.6 down
            {
                var vertices = m_RD.vertices.Select(x => (Vector2)x.pos).ToArray();
                var triangleCount = m_RD.indices.Length / 3;
                var triangles = new Vector2[triangleCount][];
                for (int i = 0; i < triangleCount; i++)
                {
                    var first = m_RD.indices[i * 3];
                    var second = m_RD.indices[i * 3 + 1];
                    var third = m_RD.indices[i * 3 + 2];
                    var triangle = new[] { vertices[first], vertices[second], vertices[third] };
                    triangles[i] = triangle;
                }
                return triangles;
            }
            else //5.6 and up
            {
                var triangles = new List<Vector2[]>();
                var m_VertexData = m_RD.m_VertexData;
                var m_Channel = m_VertexData.m_Channels[0]; //kShaderChannelVertex
                var m_Stream = m_VertexData.m_Streams[m_Channel.stream];
                using (var vertexReader = new BinaryReader(new MemoryStream(m_VertexData.m_DataSize)))
                {
                    using (var indexReader = new BinaryReader(new MemoryStream(m_RD.m_IndexBuffer)))
                    {
                        foreach (var subMesh in m_RD.m_SubMeshes)
                        {
                            vertexReader.BaseStream.Position = m_Stream.offset + subMesh.firstVertex * m_Stream.stride + m_Channel.offset;

                            var vertices = new Vector2[subMesh.vertexCount];
                            for (int v = 0; v < subMesh.vertexCount; v++)
                            {
                                vertices[v] = vertexReader.ReadVector3();
                                vertexReader.BaseStream.Position += m_Stream.stride - 12;
                            }

                            indexReader.BaseStream.Position = subMesh.firstByte;

                            var triangleCount = subMesh.indexCount / 3u;
                            for (int i = 0; i < triangleCount; i++)
                            {
                                var first = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var second = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var third = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var triangle = new[] { vertices[first], vertices[second], vertices[third] };
                                triangles.Add(triangle);
                            }
                        }
                    }
                }
                return triangles.ToArray();
            }
        }
    }
}
