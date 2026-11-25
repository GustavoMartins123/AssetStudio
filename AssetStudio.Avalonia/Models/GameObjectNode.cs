using System;
using System.Collections.Generic;
using System.ComponentModel;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public class GameObjectNode : INotifyPropertyChanged
{
    private const byte FlagIsChecked = 1 << 0;
    private const byte FlagIsExpanded = 1 << 1;
    private const byte FlagUpdatingChildren = 1 << 2;
    private const byte FlagIsPlaceholder = 1 << 3;
    private const byte FlagIsLazySceneRoot = 1 << 4;
    private const byte FlagIsSceneLoaded = 1 << 5;
    private const byte FlagIsSceneLoading = 1 << 6;

    private static readonly IReadOnlyList<GameObjectNode> EmptyChildren = Array.Empty<GameObjectNode>();
    private List<GameObjectNode>? children;
    private BulkObservableCollection<GameObjectNode>? observableChildren;
    private string? subtitle;
    private byte flags;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<GameObjectNode>? Expanded;

    public string Name { get; set; } = string.Empty;
    public GameObject? GameObject { get; set; }
    public GameObjectNode? Parent { get; private set; }
    public LazySceneDescriptor? LazyScene { get; set; }

    public IReadOnlyList<GameObjectNode> Children => (IReadOnlyList<GameObjectNode>?)observableChildren ?? children ?? EmptyChildren;
    public int ChildCount => observableChildren?.Count ?? children?.Count ?? 0;

    public string Subtitle
    {
        get => subtitle ?? string.Empty;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(subtitle ?? string.Empty, normalized, StringComparison.Ordinal))
            {
                return;
            }

            subtitle = normalized.Length == 0 ? null : normalized;
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool HasSubtitle => !string.IsNullOrEmpty(subtitle);

    public bool IsPlaceholder
    {
        get => (flags & FlagIsPlaceholder) != 0;
        set
        {
            if (SetFlag(FlagIsPlaceholder, value))
            {
                OnPropertyChanged(nameof(IsPlaceholder));
                OnPropertyChanged(nameof(ShowCheckBox));
                OnPropertyChanged(nameof(NodeTextOpacity));
            }
        }
    }

    public bool ShowCheckBox => !IsPlaceholder;

    public bool IsInactiveGameObject => GameObject != null && !GameObject.m_IsActive;

    public double NodeTextOpacity => (IsPlaceholder || IsInactiveGameObject) ? 0.58 : 1.0;

    public bool IsLazySceneRoot
    {
        get => (flags & FlagIsLazySceneRoot) != 0;
        set
        {
            if (SetFlag(FlagIsLazySceneRoot, value))
            {
                OnPropertyChanged(nameof(IsLazySceneRoot));
            }
        }
    }

    public bool IsSceneLoaded
    {
        get => (flags & FlagIsSceneLoaded) != 0;
        set
        {
            if (SetFlag(FlagIsSceneLoaded, value))
            {
                OnPropertyChanged(nameof(IsSceneLoaded));
            }
        }
    }

    public bool IsSceneLoading
    {
        get => (flags & FlagIsSceneLoading) != 0;
        set
        {
            if (SetFlag(FlagIsSceneLoading, value))
            {
                OnPropertyChanged(nameof(IsSceneLoading));
            }
        }
    }

    public bool IsChecked
    {
        get => (flags & FlagIsChecked) != 0;
        set
        {
            if (!SetFlag(FlagIsChecked, value)) return;
            OnPropertyChanged(nameof(IsChecked));

            if ((flags & FlagUpdatingChildren) != 0) return;
            var currentChildren = Children;
            for (var i = 0; i < currentChildren.Count; i++)
            {
                var child = currentChildren[i];
                if (!child.IsPlaceholder)
                {
                    child.SetCheckedFromParent(value);
                }
            }
        }
    }

    public bool IsExpanded
    {
        get => (flags & FlagIsExpanded) != 0;
        set
        {
            if (!SetFlag(FlagIsExpanded, value)) return;
            OnPropertyChanged(nameof(IsExpanded));
            if (value)
            {
                Expanded?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Opts this node into observable child collection notifications (used on lazy-expandable scene roots
    /// without adding allocation overhead to eager 50k+ GameObject trees).
    /// </summary>
    public void EnableDynamicChildren()
    {
        if (observableChildren != null)
        {
            return;
        }

        observableChildren = children != null
            ? new BulkObservableCollection<GameObjectNode>(children)
            : new BulkObservableCollection<GameObjectNode>();
        children = null;
        OnPropertyChanged(nameof(Children));
    }

    public void AddChild(GameObjectNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
        if (observableChildren != null)
        {
            observableChildren.Add(child);
            OnPropertyChanged(nameof(ChildCount));
            return;
        }

        children ??= new List<GameObjectNode>();
        children.Add(child);
    }

    public void ReplaceChildren(IReadOnlyList<GameObjectNode> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);

        var previous = Children;
        for (var i = 0; i < previous.Count; i++)
        {
            if (ReferenceEquals(previous[i].Parent, this))
            {
                previous[i].Parent = null;
            }
        }

        for (var i = 0; i < newChildren.Count; i++)
        {
            newChildren[i].Parent = this;
        }

        if (observableChildren != null)
        {
            observableChildren.ReplaceAll(newChildren);
            OnPropertyChanged(nameof(ChildCount));
            return;
        }

        observableChildren = new BulkObservableCollection<GameObjectNode>(newChildren);
        children = null;
        OnPropertyChanged(nameof(Children));
        OnPropertyChanged(nameof(ChildCount));
    }

    public void ExpandAncestors()
    {
        var node = Parent;
        while (node != null)
        {
            node.IsExpanded = true;
            node = node.Parent;
        }
    }

    private void SetCheckedFromParent(bool value)
    {
        flags |= FlagUpdatingChildren;
        IsChecked = value;
        flags = (byte)(flags & ~FlagUpdatingChildren);

        var currentChildren = Children;
        for (var i = 0; i < currentChildren.Count; i++)
        {
            var child = currentChildren[i];
            if (!child.IsPlaceholder)
            {
                child.SetCheckedFromParent(value);
            }
        }
    }

    private bool SetFlag(byte mask, bool value)
    {
        var had = (flags & mask) != 0;
        if (had == value)
        {
            return false;
        }

        flags = value ? (byte)(flags | mask) : (byte)(flags & ~mask);
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
