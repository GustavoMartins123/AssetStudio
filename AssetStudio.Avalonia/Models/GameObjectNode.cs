using System;
using System.Collections.Generic;
using System.ComponentModel;
using AssetStudio;

namespace AssetStudio.Avalonia;

public class GameObjectNode : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<GameObjectNode> EmptyChildren = Array.Empty<GameObjectNode>();
    private List<GameObjectNode>? children;
    private bool isChecked;
    private bool isExpanded;
    private bool updatingChildren;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = string.Empty;
    public GameObject? GameObject { get; set; }
    public GameObjectNode? Parent { get; private set; }
    public IReadOnlyList<GameObjectNode> Children => children ?? EmptyChildren;
    public int ChildCount => children?.Count ?? 0;

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (updatingChildren || children == null) return;
            foreach (var child in children)
            {
                child.SetCheckedFromParent(value);
            }
        }
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public void AddChild(GameObjectNode child)
    {
        children ??= new List<GameObjectNode>();
        child.Parent = this;
        children.Add(child);
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
        updatingChildren = true;
        IsChecked = value;
        updatingChildren = false;

        if (children == null) return;
        foreach (var child in children)
        {
            child.SetCheckedFromParent(value);
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
