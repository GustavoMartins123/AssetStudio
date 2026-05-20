using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using System;

namespace AssetStudio.Avalonia;

public class MessageBox : Window
{
    public MessageBox(string text, string title = "Message")
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        Padding = new Thickness(20);

        var stackPanel = new StackPanel
        {
            Spacing = 15,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 400
        };

        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 80
        };
        button.Click += (s, e) => Close();

        stackPanel.Children.Add(textBlock);
        stackPanel.Children.Add(button);

        Content = stackPanel;
    }

    public static void Show(Window? owner, string text, string title = "Message")
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = new MessageBox(text, title);
            if (owner != null && owner.IsVisible)
            {
                box.ShowDialog(owner);
            }
            else
            {
                box.Show();
            }
        });
    }
}
