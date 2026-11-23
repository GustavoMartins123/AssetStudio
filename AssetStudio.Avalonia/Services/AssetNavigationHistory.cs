using System;
using System.Collections.Generic;
using System.Globalization;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class AssetNavigationEntry : IEquatable<AssetNavigationEntry>
    {
        public string AssetUniqueId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ClassIDType AssetType { get; init; }
        public string TypeName { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string SerializedFileName { get; init; } = string.Empty;

        public string NavigationKey
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(AssetUniqueId))
                {
                    return AssetUniqueId;
                }

                if (PathId != 0)
                {
                    return $"{SerializedFileName}#{PathId.ToString(CultureInfo.InvariantCulture)}#{((int)AssetType).ToString(CultureInfo.InvariantCulture)}";
                }

                return string.Empty;
            }
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(NavigationKey);

        public AssetReferenceItem ToReferenceItem()
        {
            var effectiveTypeName = !string.IsNullOrWhiteSpace(TypeName)
                ? TypeName
                : AssetItem.GetCachedTypeString(AssetType);
            return new AssetReferenceItem
            {
                AssetUniqueId = AssetUniqueId,
                Name = Name,
                AssetType = AssetType,
                TypeName = effectiveTypeName,
                Relation = "History",
                PathId = PathId,
                SerializedFileName = SerializedFileName,
                Direction = AssetReferenceDirection.References
            };
        }

        public static AssetNavigationEntry? FromAssetItem(AssetItem? item)
        {
            if (item == null)
            {
                return null;
            }

            var uniqueId = item.Handle != null && !string.IsNullOrWhiteSpace(item.Handle.UniqueID)
                ? item.Handle.UniqueID
                : (item.UniqueID ?? string.Empty);
            var fileName = item.SourceFile?.fileName
                ?? item.Handle?.SerializedFileName
                ?? string.Empty;
            var entry = new AssetNavigationEntry
            {
                AssetUniqueId = uniqueId,
                Name = item.Name ?? string.Empty,
                AssetType = item.Type,
                TypeName = !string.IsNullOrWhiteSpace(item.DisplayType)
                    ? item.DisplayType
                    : AssetItem.GetCachedTypeString(item.Type),
                PathId = item.PathID,
                SerializedFileName = fileName
            };
            return entry.IsValid ? entry : null;
        }

        public static AssetNavigationEntry? FromReferenceItem(AssetReferenceItem? item)
        {
            if (item == null || !item.IsNavigable)
            {
                return null;
            }

            var entry = new AssetNavigationEntry
            {
                AssetUniqueId = item.AssetUniqueId ?? string.Empty,
                Name = item.Name ?? string.Empty,
                AssetType = item.AssetType,
                TypeName = !string.IsNullOrWhiteSpace(item.TypeName)
                    ? item.TypeName
                    : AssetItem.GetCachedTypeString(item.AssetType),
                PathId = item.PathId,
                SerializedFileName = item.SerializedFileName ?? string.Empty
            };
            return entry.IsValid ? entry : null;
        }

        public bool Equals(AssetNavigationEntry? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other is null)
            {
                return false;
            }

            return string.Equals(NavigationKey, other.NavigationKey, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => obj is AssetNavigationEntry other && Equals(other);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(NavigationKey);
    }

    public sealed class AssetNavigationHistory
    {
        public const int DefaultCapacity = 64;

        private readonly List<AssetNavigationEntry> _entries = new();
        private int _currentIndex = -1;

        public AssetNavigationHistory(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Navigation history capacity must be at least 1.");
            }

            Capacity = capacity;
        }

        public int Capacity { get; }

        public int Count => _entries.Count;

        public int CurrentIndex => _currentIndex;

        public AssetNavigationEntry? Current =>
            _currentIndex >= 0 && _currentIndex < _entries.Count
                ? _entries[_currentIndex]
                : null;

        public bool CanGoBack => _currentIndex > 0;

        public bool CanGoForward => _currentIndex >= 0 && _currentIndex < _entries.Count - 1;

        public AssetNavigationEntry? PreviousEntry =>
            CanGoBack ? _entries[_currentIndex - 1] : null;

        public AssetNavigationEntry? NextEntry =>
            CanGoForward ? _entries[_currentIndex + 1] : null;

        public bool Push(AssetNavigationEntry? entry)
        {
            if (entry == null || !entry.IsValid)
            {
                return false;
            }

            // Consecutive deduplication: do not push the same asset back-to-back.
            if (Current != null && Current.Equals(entry))
            {
                if (string.IsNullOrWhiteSpace(Current.Name) && !string.IsNullOrWhiteSpace(entry.Name))
                {
                    _entries[_currentIndex] = entry;
                }
                return false;
            }

            // Truncate any forward branch when navigating to a new target.
            if (_currentIndex < _entries.Count - 1)
            {
                _entries.RemoveRange(_currentIndex + 1, _entries.Count - (_currentIndex + 1));
            }

            _entries.Add(entry);
            _currentIndex = _entries.Count - 1;

            if (_entries.Count > Capacity)
            {
                var excess = _entries.Count - Capacity;
                _entries.RemoveRange(0, excess);
                _currentIndex = Math.Max(0, _currentIndex - excess);
            }

            return true;
        }

        public bool TryGoBack(out AssetNavigationEntry? entry)
        {
            if (!CanGoBack)
            {
                entry = null;
                return false;
            }

            _currentIndex--;
            entry = _entries[_currentIndex];
            return true;
        }

        public bool TryGoForward(out AssetNavigationEntry? entry)
        {
            if (!CanGoForward)
            {
                entry = null;
                return false;
            }

            _currentIndex++;
            entry = _entries[_currentIndex];
            return true;
        }

        public void Clear()
        {
            _entries.Clear();
            _currentIndex = -1;
        }
    }
}
