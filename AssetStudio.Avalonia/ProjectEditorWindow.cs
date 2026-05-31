using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public sealed class ProjectEditorWindow : Window
{
    private readonly ProjectManagerStore _store;
    private readonly ManagedProject _project;
    private readonly TextBox _nameBox = new();
    private readonly CheckBox _useAutoNameBox = new() { Content = "Use game name automatically when detected" };
    private readonly TextBox _rootBox = new() { PlaceholderText = "Project root" };
    private readonly TextBox _iconBox = new() { PlaceholderText = "Project icon" };
    private readonly Image _iconPreview = new() { Width = 64, Height = 64, Stretch = Stretch.Uniform };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private string _pendingIconSourcePath = string.Empty;

    public ProjectEditorWindow(ProjectManagerStore store, ManagedProject? project = null)
    {
        _store = store;
        _project = project?.Clone() ?? new ManagedProject();

        Title = project == null ? "Add project" : "Edit project";
        Width = 620;
        Height = 430;
        MinWidth = 520;
        MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _nameBox.Text = _project.Name;
        _useAutoNameBox.IsChecked = _project.UseAutoName;
        _rootBox.Text = _project.ProjectRoot;
        _iconBox.Text = _project.IconPath;
        _rootBox.PlaceholderText = "Example: D:\\Games\\GameName";
        _iconBox.PlaceholderText = "Optional image or game .exe";

        _nameBox.TextChanged += (_, _) => UpdateStatus();
        _useAutoNameBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(CheckBox.IsChecked))
            {
                UpdateStatus();
            }
        };

        Content = BuildContent();
        UpdateIconPreview(_project.IconPath);
        UpdateStatus();
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            RowSpacing = 14
        };

        var title = new TextBlock
        {
            Text = "Project",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var namePanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*"),
            ColumnSpacing = 10
        };
        namePanel.Children.Add(Label("Name"));
        Grid.SetColumn(_nameBox, 1);
        namePanel.Children.Add(_nameBox);
        Grid.SetRow(namePanel, 1);
        root.Children.Add(namePanel);

        Grid.SetRow(_useAutoNameBox, 2);
        Grid.SetColumn(_useAutoNameBox, 1);
        root.Children.Add(_useAutoNameBox);

        var rootPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*,Auto"),
            ColumnSpacing = 10
        };
        rootPanel.Children.Add(Label("Project root"));
        Grid.SetColumn(_rootBox, 1);
        rootPanel.Children.Add(_rootBox);
        var browseRoot = new Button { Content = "Browse...", MinWidth = 92 };
        browseRoot.Click += BrowseRoot_Click;
        Grid.SetColumn(browseRoot, 2);
        rootPanel.Children.Add(browseRoot);
        Grid.SetRow(rootPanel, 3);
        root.Children.Add(rootPanel);

        var iconPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*,Auto,Auto"),
            ColumnSpacing = 10
        };
        iconPanel.Children.Add(Label("Icon"));
        Grid.SetColumn(_iconBox, 1);
        iconPanel.Children.Add(_iconBox);
        var browseIcon = new Button { Content = "Choose...", MinWidth = 92 };
        browseIcon.Click += BrowseIcon_Click;
        Grid.SetColumn(browseIcon, 2);
        iconPanel.Children.Add(browseIcon);
        var detectIcon = new Button { Content = "Detect", MinWidth = 80 };
        detectIcon.Click += DetectIcon_Click;
        Grid.SetColumn(detectIcon, 3);
        iconPanel.Children.Add(detectIcon);
        Grid.SetRow(iconPanel, 4);

        var middle = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16
        };
        middle.Children.Add(iconPanel);
        var previewBorder = new Border
        {
            Width = 78,
            Height = 78,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Padding = new Thickness(6),
            Child = _iconPreview
        };
        Grid.SetColumn(previewBorder, 1);
        middle.Children.Add(previewBorder);
        Grid.SetRow(middle, 4);
        root.Children.Add(middle);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        _statusText.Opacity = 0.78;
        footer.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 86 };
        cancel.Click += (_, _) => Close(null);
        var save = new Button { Content = "Save", MinWidth = 86 };
        save.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        return root;
    }

    private static TextBlock Label(string text)
    {
        return new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
    }

    private async void BrowseRoot_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select project root",
            AllowMultiple = false
        });

        if (folders == null || folders.Count == 0)
        {
            return;
        }

        var path = folders[0].Path.LocalPath;
        _rootBox.Text = path;

        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _nameBox.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(_iconBox.Text))
        {
            await DetectIcon();
        }

        UpdateStatus();
    }

    private async void BrowseIcon_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select project icon",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.ico", "*.bmp", "*.webp", "*.exe" }
                }
            }
        });

        if (files == null || files.Count == 0)
        {
            return;
        }

        _pendingIconSourcePath = files[0].Path.LocalPath;
        _iconBox.Text = _pendingIconSourcePath;
        UpdateIconPreview(GetPreviewPath(_pendingIconSourcePath));
        UpdateStatus();
    }

    private async void DetectIcon_Click(object? sender, RoutedEventArgs e)
    {
        await DetectIcon();
    }

    private Task DetectIcon()
    {
        var root = _rootBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _statusText.Text = "Choose an existing project root before detecting an icon.";
            return Task.CompletedTask;
        }

        var iconPath = _store.TryFindProjectIcon(root);
        if (iconPath == null)
        {
            _statusText.Text = "No icon-like image was found in this project root.";
            return Task.CompletedTask;
        }

        _pendingIconSourcePath = iconPath;
        _iconBox.Text = iconPath;
        UpdateIconPreview(GetPreviewPath(iconPath));
        UpdateStatus();
        return Task.CompletedTask;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var name = _nameBox.Text?.Trim() ?? string.Empty;
        var root = _rootBox.Text?.Trim() ?? string.Empty;
        var icon = _iconBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(root))
        {
            _statusText.Text = "Set a project name or choose a project root.";
            return;
        }

        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(root))
        {
            name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        _project.Name = name;
        _project.UseAutoName = _useAutoNameBox.IsChecked == true;
        _project.ProjectRoot = root;
        if (!string.IsNullOrWhiteSpace(root) && string.IsNullOrWhiteSpace(_project.LastLoadPath))
        {
            _project.LastLoadPath = root;
        }

        if (!string.IsNullOrWhiteSpace(_pendingIconSourcePath))
        {
            _project.PendingIconSourcePath = _pendingIconSourcePath;
        }
        else if (!string.IsNullOrWhiteSpace(icon) && !string.Equals(icon, _project.IconPath, StringComparison.OrdinalIgnoreCase))
        {
            _project.PendingIconSourcePath = icon;
        }

        Close(_project);
    }

    private void UpdateStatus()
    {
        if (_useAutoNameBox.IsChecked == true)
        {
            _statusText.Text = "The project card will use the detected game name after assets are loaded.";
        }
        else
        {
            _statusText.Text = "The custom name will stay as typed here.";
        }
    }

    private void UpdateIconPreview(string? path)
    {
        _iconPreview.Source = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            _iconPreview.Source = new Bitmap(stream);
        }
        catch
        {
        }
    }

    private string? GetPreviewPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return _store.TryCreateExecutableIconPreview(path) ?? path;
        }

        return path;
    }
}
