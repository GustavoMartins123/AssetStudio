using System;
using System.Collections.Generic;
using System.ComponentModel;
using AssetStudio;

namespace AssetStudio.Avalonia;

public class AssetClassItem : INotifyPropertyChanged, IEquatable<AssetClassItem>
{
    private int _classID;
    private string _name = string.Empty;
    private string _namespace = string.Empty;
    private string _assembly = string.Empty;
    private string _unityVersion = string.Empty;
    private string _sourceFile = string.Empty;
    private string _sourceKind = string.Empty;
    private int _objectCount;
    private SerializedType _serializedType = null!;

    public int ClassID
    {
        get => _classID;
        set => SetProperty(ref _classID, value, nameof(ClassID));
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty, nameof(Name));
    }

    public string Namespace
    {
        get => _namespace;
        set => SetProperty(ref _namespace, value ?? string.Empty, nameof(Namespace));
    }

    public string Assembly
    {
        get => _assembly;
        set => SetProperty(ref _assembly, value ?? string.Empty, nameof(Assembly));
    }

    public string UnityVersion
    {
        get => _unityVersion;
        set => SetProperty(ref _unityVersion, value ?? string.Empty, nameof(UnityVersion));
    }

    public string SourceFile
    {
        get => _sourceFile;
        set => SetProperty(ref _sourceFile, value ?? string.Empty, nameof(SourceFile));
    }

    public string SourceKind
    {
        get => _sourceKind;
        set => SetProperty(ref _sourceKind, value ?? string.Empty, nameof(SourceKind));
    }

    public int ObjectCount
    {
        get => _objectCount;
        set => SetProperty(ref _objectCount, value, nameof(ObjectCount));
    }

    public SerializedType SerializedType
    {
        get => _serializedType;
        set => SetProperty(ref _serializedType, value, nameof(SerializedType));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CopyFrom(AssetClassItem other)
    {
        ClassID = other.ClassID;
        Name = other.Name;
        Namespace = other.Namespace;
        Assembly = other.Assembly;
        UnityVersion = other.UnityVersion;
        SourceFile = other.SourceFile;
        SourceKind = other.SourceKind;
        ObjectCount = other.ObjectCount;
        SerializedType = other.SerializedType;
    }

    public bool Equals(AssetClassItem? other)
    {
        return other != null
            && ClassID == other.ClassID
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
            && string.Equals(Assembly, other.Assembly, StringComparison.Ordinal)
            && string.Equals(UnityVersion, other.UnityVersion, StringComparison.Ordinal)
            && string.Equals(SourceFile, other.SourceFile, StringComparison.Ordinal)
            && string.Equals(SourceKind, other.SourceKind, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as AssetClassItem);

    public override int GetHashCode()
    {
        return HashCode.Combine(ClassID, Name, Namespace, Assembly, UnityVersion, SourceFile, SourceKind);
    }

    private bool SetProperty<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
