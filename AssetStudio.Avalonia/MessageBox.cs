using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public class MessageBox : Window
{
    private static MessageBox? _activeInstance;
    private static readonly object _lock = new object();
    private static int _errorCount = 1;

    private readonly ListBox _listBox;
    private readonly Button _button;
    private readonly StringBuilder _contentBuilder = new();
    private readonly ObservableCollection<string> _lines = new();
    private readonly List<string> _pendingLines = new();
    private bool _updatePending = false;

    public MessageBox(string text, string title = "Message")
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = true;
        Width = 650;
        Height = 450;
        MinWidth = 400;
        MinHeight = 250;
        Padding = new Thickness(20);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 15
        };

        _contentBuilder.Append(text);
        
        foreach (var line in text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            _lines.Add(line);
        }

        _listBox = new ListBox
        {
            ItemsSource = _lines,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = global::Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = global::Avalonia.Media.Brushes.LightGray,
            ItemTemplate = new FuncDataTemplate<string>((value, namescope) => new TextBlock
            {
                Text = value,
                FontFamily = new global::Avalonia.Media.FontFamily("Consolas, DejaVu Sans Mono, Courier New, monospace"),
                FontSize = 12,
                TextWrapping = global::Avalonia.Media.TextWrapping.NoWrap,
                Margin = new Thickness(0, 1)
            })
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10
        };

        var copyButton = new Button
        {
            Content = "Copy",
            MinWidth = 90,
            Height = 30,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        copyButton.Click += async (s, e) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(_contentBuilder.ToString());
            }
        };

        _button = new Button
        {
            Content = "OK",
            MinWidth = 90,
            Height = 30,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _button.Click += (s, e) => Close();

        buttonPanel.Children.Add(copyButton);
        buttonPanel.Children.Add(_button);

        Grid.SetRow(_listBox, 0);
        Grid.SetRow(buttonPanel, 1);

        grid.Children.Add(_listBox);
        grid.Children.Add(buttonPanel);

        Content = grid;

        Closed += (s, e) =>
        {
            lock (_lock)
            {
                if (_activeInstance == this)
                {
                    _activeInstance = null;
                    _errorCount = 1;
                }
            }
        };
    }

    public void AppendMessage(string message)
    {
        _errorCount++;
        var divider = $"\n\n-----------------------------------------\n\nError {_errorCount}:\n{message}";
        _contentBuilder.Append(divider);
        Title = $"Errors ({_errorCount})";
        
        lock (_pendingLines)
        {
            foreach (var line in divider.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                _pendingLines.Add(line);
            }
        }
        ScheduleUIUpdate();
    }

    private void ScheduleUIUpdate()
    {
        if (!_updatePending)
        {
            _updatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                lock (_pendingLines)
                {
                    foreach (var line in _pendingLines)
                    {
                        _lines.Add(line);
                    }
                    _pendingLines.Clear();
                }
                
                if (_lines.Count > 0)
                {
                    _listBox.ScrollIntoView(_lines.Count - 1);
                }
                _updatePending = false;
            }, DispatcherPriority.Background);
        }
    }

    public static void Show(Window? owner, string text, string title = "Message")
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_lock)
            {
                if (_activeInstance != null)
                {
                    _activeInstance.AppendMessage(text);
                    return;
                }

                var box = new MessageBox(text, title);
                _activeInstance = box;
                
                if (owner != null && owner.IsVisible)
                {
                    box.ShowDialog(owner);
                }
                else
                {
                    box.Show();
                }
            }
        });
    }
}
