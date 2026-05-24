using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private TextAssetPreviewResult? currentTextAssetPreview;

    private void ShowTextAssetDialoguePreview(AssetItem assetItem, TextAssetPreviewResult preview)
    {
        currentTextAssetPreview = preview;

        TextPreviewBox.IsVisible = false;
        ImagePreviewBox.IsVisible = false;
        if (GLPreviewControl != null)
        {
            GLPreviewControl.IsVisible = false;
        }
        if (TextureGLPreview != null)
        {
            TextureGLPreview.IsVisible = false;
        }
        PreviewLabel.IsVisible = false;
        PreviewInfoBorder.IsVisible = false;

        TextAssetPreviewTitle.Text = assetItem.Name;
        TextAssetPreviewSubtitle.Text =
            $"{preview.FormatName} | {preview.DialogueCards.Count:N0} cards | {preview.ParsedStringCount:N0} parsed strings";

        TextAssetDetailsTextBox.FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
        TextAssetDetailsTextBox.FontSize = 13;
        SetTextWithTruncation(TextAssetDetailsTextBox, preview.DetailsText);

        BuildTextAssetDialogueCards(preview);
        TextAssetPreviewPanel.IsVisible = true;
        SetTextAssetPreviewMode(showCards: true);
    }

    private void ClearTextAssetPreview()
    {
        currentTextAssetPreview = null;
        TextAssetPreviewPanel.IsVisible = false;
        TextAssetDetailsTextBox.Text = string.Empty;
        TextAssetDialogueCardsHost.Children.Clear();
    }

    private void TextAssetCardsViewButton_Click(object? sender, RoutedEventArgs e)
    {
        SetTextAssetPreviewMode(showCards: true);
    }

    private void TextAssetDetailsViewButton_Click(object? sender, RoutedEventArgs e)
    {
        SetTextAssetPreviewMode(showCards: false);
    }

    private void SetTextAssetPreviewMode(bool showCards)
    {
        if (currentTextAssetPreview == null)
        {
            return;
        }

        TextAssetDialogueScrollViewer.IsVisible = showCards;
        TextAssetDetailsTextBox.IsVisible = !showCards;
        TextAssetCardsViewButton.Background = showCards ? BrushFor("#34507A") : BrushFor("#252A31");
        TextAssetDetailsViewButton.Background = showCards ? BrushFor("#252A31") : BrushFor("#34507A");
        TextAssetCardsViewButton.Foreground = Brushes.White;
        TextAssetDetailsViewButton.Foreground = Brushes.White;
    }

    private void BuildTextAssetDialogueCards(TextAssetPreviewResult preview)
    {
        TextAssetDialogueCardsHost.Children.Clear();
        for (int i = 0; i < preview.DialogueCards.Count; i++)
        {
            TextAssetDialogueCardsHost.Children.Add(CreateTextAssetDialogueCard(preview.DialogueCards[i], i + 1));
        }
    }

    private Control CreateTextAssetDialogueCard(TextAssetDialogueCard card, int number)
    {
        var border = new Border
        {
            Background = BrushFor(card.Kind == "Note" ? "#23272E" : "#20242B"),
            BorderBrush = BrushFor(card.Kind == "Note" ? "#5A6A7D" : "#46678E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 8
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var title = new TextBlock
        {
            Text = BuildTextAssetCardTitle(card, number),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var metadata = new TextBlock
        {
            Text = $"0x{card.Offset.ToString("X6", CultureInfo.InvariantCulture)}",
            Foreground = BrushFor("#8F9AA8"),
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(metadata, 1);
        header.Children.Add(title);
        header.Children.Add(metadata);

        var body = new TextBlock
        {
            Text = card.Text,
            Foreground = BrushFor("#F1F5FA"),
            FontSize = 16,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(body, 1);

        var footer = new TextBlock
        {
            Text = BuildTextAssetCardFooter(card),
            Foreground = BrushFor("#9DA8B5"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(footer, 2);

        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(footer);
        border.Child = root;

        return border;
    }

    private static string BuildTextAssetCardTitle(TextAssetDialogueCard card, int number)
    {
        var prefix = number.ToString("D3", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(card.Speaker))
        {
            return $"{prefix} | {card.Speaker}";
        }

        if (!string.IsNullOrWhiteSpace(card.Label))
        {
            return $"{prefix} | {card.Label}";
        }

        return prefix;
    }

    private static string BuildTextAssetCardFooter(TextAssetDialogueCard card)
    {
        var label = string.IsNullOrWhiteSpace(card.Label) ? "unlabeled" : card.Label;
        return $"{card.Kind} | {label} | {card.Length:N0} bytes";
    }

    private static SolidColorBrush BrushFor(string color)
    {
        if (color.Length == 7 && color[0] == '#')
        {
            byte r = byte.Parse(color.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(color.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(color.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new SolidColorBrush(new global::Avalonia.Media.Color(255, r, g, b));
        }

        return new SolidColorBrush(global::Avalonia.Media.Colors.Transparent);
    }
}
