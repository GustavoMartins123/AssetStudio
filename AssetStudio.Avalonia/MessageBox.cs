using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using System;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public class MessageBox : Window
{
    private static MessageBox? _activeInstance;
    private static readonly object _lock = new object();
    private static int _errorCount = 1;

    private readonly TextBox _textBox;
    private readonly Button _button;
    private readonly StringBuilder _contentBuilder = new();
    private bool _updatePending = false;

    public MessageBox(string text, string title = "Message")
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = true;
        MinWidth = 320;
        MaxWidth = 600;
        MinHeight = 150;
        MaxHeight = 500;
        Padding = new Thickness(20);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 15
        };

        _contentBuilder.Append(text);

        _textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            AcceptsReturn = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MaxHeight = 350,
            FontFamily = global::Avalonia.Media.FontFamily.Default,
            FontSize = 13
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
                await clipboard.SetTextAsync(_textBox.Text);
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

        Grid.SetRow(_textBox, 0);
        Grid.SetRow(buttonPanel, 1);

        grid.Children.Add(_textBox);
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
        _contentBuilder.Append($"\n\n-----------------------------------------\n\nError {_errorCount}:\n{message}");
        Title = $"Errors ({_errorCount})";
        ScheduleUIUpdate();
    }

    private void ScheduleUIUpdate()
    {
        if (!_updatePending)
        {
            _updatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _textBox.Text = _contentBuilder.ToString();
                _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
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
