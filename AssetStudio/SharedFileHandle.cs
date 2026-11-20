#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AssetStudio
{
    /// <summary>
    /// Thread-safe reference-counted <see cref="SafeFileHandle"/> wrapper shared across multiple
    /// <see cref="SubStream"/> slices of the same backing file for lock-free <see cref="RandomAccess.Read"/>.
    /// </summary>
    public sealed class SharedFileHandle : IDisposable
    {
        private static readonly object RegistryLock = new object();
        private static readonly Dictionary<string, SharedFileHandle> ActiveHandles =
            new Dictionary<string, SharedFileHandle>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private readonly object handleLock = new object();
        private readonly string? registryKey;
        private SafeFileHandle? safeFileHandle;
        private int refCount;
        private int ownerDisposed;

        public string FilePath { get; }

        public int ReferenceCount => Volatile.Read(ref refCount);

        public bool IsDisposed => Volatile.Read(ref refCount) <= 0;

        public bool IsHandleCreated
        {
            get
            {
                var h = Volatile.Read(ref safeFileHandle);
                return h != null && !h.IsClosed && !h.IsInvalid;
            }
        }

        public SafeFileHandle SafeFileHandle => EnsureHandle();

        public SharedFileHandle(string filePath)
            : this(filePath, NormalizePathKey(filePath), initialRefCount: 1, hasExplicitOwner: true)
        {
            lock (RegistryLock)
            {
                if (registryKey != null && !ActiveHandles.ContainsKey(registryKey))
                {
                    ActiveHandles[registryKey] = this;
                }
            }
        }

        private SharedFileHandle(string filePath, string? registryKey, int initialRefCount, bool hasExplicitOwner)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(filePath));
            }

            FilePath = filePath;
            this.registryKey = registryKey;
            refCount = initialRefCount;
            ownerDisposed = hasExplicitOwner ? 0 : 1;
        }

        public static SharedFileHandle Acquire(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(filePath));
            }

            var key = NormalizePathKey(filePath);
            lock (RegistryLock)
            {
                if (ActiveHandles.TryGetValue(key, out var existing) && existing.TryAddRefUnderLock())
                {
                    return existing;
                }

                var created = new SharedFileHandle(filePath, key, initialRefCount: 1, hasExplicitOwner: false);
                ActiveHandles[key] = created;
                return created;
            }
        }

        public SharedFileHandle AddRef()
        {
            lock (RegistryLock)
            {
                if (!TryAddRefUnderLock())
                {
                    throw new ObjectDisposedException(nameof(SharedFileHandle));
                }
                return this;
            }
        }

        private bool TryAddRefUnderLock()
        {
            int current = Volatile.Read(ref refCount);
            if (current <= 0)
            {
                return false;
            }

            Interlocked.Increment(ref refCount);
            return true;
        }

        public SafeFileHandle EnsureHandle()
        {
            if (Volatile.Read(ref refCount) <= 0)
            {
                throw new ObjectDisposedException(nameof(SharedFileHandle));
            }

            var existing = Volatile.Read(ref safeFileHandle);
            if (existing != null && !existing.IsClosed && !existing.IsInvalid)
            {
                return existing;
            }

            lock (handleLock)
            {
                if (Volatile.Read(ref refCount) <= 0)
                {
                    throw new ObjectDisposedException(nameof(SharedFileHandle));
                }

                existing = safeFileHandle;
                if (existing != null && !existing.IsClosed && !existing.IsInvalid)
                {
                    return existing;
                }

                var opened = File.OpenHandle(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.RandomAccess);

                lock (RegistryLock)
                {
                    if (Volatile.Read(ref refCount) <= 0)
                    {
                        opened.Dispose();
                        throw new ObjectDisposedException(nameof(SharedFileHandle));
                    }

                    Volatile.Write(ref safeFileHandle, opened);
                    return opened;
                }
            }
        }

        public int Release()
        {
            SafeFileHandle? handleToDispose = null;
            int remaining;
            lock (RegistryLock)
            {
                remaining = Interlocked.Decrement(ref refCount);
                if (remaining < 0)
                {
                    Interlocked.Exchange(ref refCount, 0);
                    return 0;
                }

                if (remaining == 0)
                {
                    if (registryKey != null
                        && ActiveHandles.TryGetValue(registryKey, out var registered)
                        && ReferenceEquals(registered, this))
                    {
                        ActiveHandles.Remove(registryKey);
                    }

                    handleToDispose = Volatile.Read(ref safeFileHandle);
                    Volatile.Write(ref safeFileHandle, null);
                }
            }

            handleToDispose?.Dispose();
            return remaining;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ownerDisposed, 1) == 0)
            {
                Release();
            }
        }

        private static string NormalizePathKey(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
