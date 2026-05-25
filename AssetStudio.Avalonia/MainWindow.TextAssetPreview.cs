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
    private TextAssetPreviewMode currentPreviewMode = TextAssetPreviewMode.Cards;

    private enum TextAssetPreviewMode
    {
        Cards,
        Text,
        Info
    }

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
            $"{preview.FormatName} | {preview.DialogueCards.Count:N0} dialogue-like strings | {preview.ParsedStringCount:N0} parsed strings";

        TextAssetDetailsTextBox.FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
        TextAssetDetailsTextBox.FontSize = 13;

        BuildTextAssetDialogueCards(preview);
        TextAssetPreviewPanel.IsVisible = true;
        SetTextAssetPreviewMode(TextAssetPreviewMode.Cards);
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
        SetTextAssetPreviewMode(TextAssetPreviewMode.Cards);
    }

    private void TextAssetDetailsViewButton_Click(object? sender, RoutedEventArgs e)
    {
        SetTextAssetPreviewMode(TextAssetPreviewMode.Text);
    }

    private void TextAssetInfoViewButton_Click(object? sender, RoutedEventArgs e)
    {
        SetTextAssetPreviewMode(TextAssetPreviewMode.Info);
    }

    private void SetTextAssetPreviewMode(TextAssetPreviewMode mode)
    {
        if (currentTextAssetPreview == null)
        {
            return;
        }

        bool showCards = mode == TextAssetPreviewMode.Cards;
        TextAssetDialogueScrollViewer.IsVisible = showCards;
        TextAssetDetailsTextBox.IsVisible = !showCards;

        currentPreviewMode = mode;

        if (mode == TextAssetPreviewMode.Text)
        {
            SetTextWithTruncation(TextAssetDetailsTextBox, currentTextAssetPreview.PlainTextScript);
        }
        else if (mode == TextAssetPreviewMode.Info)
        {
            SetTextWithTruncation(TextAssetDetailsTextBox, currentTextAssetPreview.DetailsText);
        }

        bool isDark = ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
        IBrush activeBg = BrushFor("#34507A");
        IBrush activeFg = Brushes.White;
        IBrush inactiveBg = isDark ? (IBrush)BrushFor("#252A31") : (IBrush)BrushFor("#E2E8F0");
        IBrush inactiveFg = isDark ? Brushes.White : (IBrush)BrushFor("#2D3748");

        TextAssetCardsViewButton.Background = mode == TextAssetPreviewMode.Cards ? activeBg : inactiveBg;
        TextAssetDetailsViewButton.Background = mode == TextAssetPreviewMode.Text ? activeBg : inactiveBg;
        TextAssetInfoViewButton.Background = mode == TextAssetPreviewMode.Info ? activeBg : inactiveBg;

        TextAssetCardsViewButton.Foreground = mode == TextAssetPreviewMode.Cards ? activeFg : inactiveFg;
        TextAssetDetailsViewButton.Foreground = mode == TextAssetPreviewMode.Text ? activeFg : inactiveFg;
        TextAssetInfoViewButton.Foreground = mode == TextAssetPreviewMode.Info ? activeFg : inactiveFg;
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
        bool isDark = ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;

        var border = new Border
        {
            Background = BrushFor(card.Kind == "Note" ? (isDark ? "#23272E" : "#F8FAFC") : (isDark ? "#20242B" : "#F1F5F9")),
            BorderBrush = BrushFor(card.Kind == "Note" ? (isDark ? "#5A6A7D" : "#CBD5E1") : (isDark ? "#46678E" : "#94A3B8")),
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
            Foreground = isDark ? Brushes.White : BrushFor("#1E293B"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var metadata = new TextBlock
        {
            Text = $"0x{card.Offset.ToString("X6", CultureInfo.InvariantCulture)}",
            Foreground = isDark ? BrushFor("#8F9AA8") : BrushFor("#64748B"),
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
            Foreground = isDark ? BrushFor("#F1F5FA") : BrushFor("#0F172A"),
            FontSize = 16,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(body, 1);

        var footer = new TextBlock
        {
            Text = BuildTextAssetCardFooter(card),
            Foreground = isDark ? BrushFor("#9DA8B5") : BrushFor("#475569"),
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
        if (!string.IsNullOrWhiteSpace(card.Label))
        {
            return $"{prefix} | {card.Label}";
        }

        return $"{prefix} | {card.Kind}";
    }

    private static string BuildTextAssetCardFooter(TextAssetDialogueCard card)
    {
        var label = string.IsNullOrWhiteSpace(card.Label) ? "unlabeled" : card.Label;
        var speaker = string.IsNullOrWhiteSpace(card.Speaker) ? string.Empty : $" | speaker hint: {card.Speaker}";
        return $"{card.Kind} | {label}{speaker} | {card.Length:N0} bytes";
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
