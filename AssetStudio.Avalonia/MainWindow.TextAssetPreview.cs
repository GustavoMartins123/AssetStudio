using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AssetStudio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public sealed class TextAssetDialogueCardViewModel
{
    public required string Title { get; init; }
    public required string OffsetText { get; init; }
    public required string BodyText { get; init; }
    public required string FooterText { get; init; }
    public required IBrush CardBackground { get; init; }
    public required IBrush CardBorderBrush { get; init; }
    public required IBrush TitleForeground { get; init; }
    public required IBrush MetadataForeground { get; init; }
    public required IBrush BodyForeground { get; init; }
    public required IBrush FooterForeground { get; init; }
}

public partial class MainWindow
{
    private static readonly FontFamily TextAssetMonoFontFamily = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    private static readonly IBrush ActiveModeBackground = ImmutableBrushFor("#34507A");
    private static readonly IBrush ActiveModeForeground = Brushes.White;
    private static readonly IBrush InactiveModeDarkBackground = ImmutableBrushFor("#252A31");
    private static readonly IBrush InactiveModeLightBackground = ImmutableBrushFor("#E2E8F0");
    private static readonly IBrush InactiveModeLightForeground = ImmutableBrushFor("#2D3748");

    private static readonly IBrush DarkNoteBackground = ImmutableBrushFor("#23272E");
    private static readonly IBrush LightNoteBackground = ImmutableBrushFor("#F8FAFC");
    private static readonly IBrush DarkDialogueBackground = ImmutableBrushFor("#20242B");
    private static readonly IBrush LightDialogueBackground = ImmutableBrushFor("#F1F5F9");

    private static readonly IBrush DarkNoteBorder = ImmutableBrushFor("#5A6A7D");
    private static readonly IBrush LightNoteBorder = ImmutableBrushFor("#CBD5E1");
    private static readonly IBrush DarkDialogueBorder = ImmutableBrushFor("#46678E");
    private static readonly IBrush LightDialogueBorder = ImmutableBrushFor("#94A3B8");

    private static readonly IBrush LightTitleForeground = ImmutableBrushFor("#1E293B");
    private static readonly IBrush DarkMetadataForeground = ImmutableBrushFor("#8F9AA8");
    private static readonly IBrush LightMetadataForeground = ImmutableBrushFor("#64748B");
    private static readonly IBrush DarkBodyForeground = ImmutableBrushFor("#F1F5FA");
    private static readonly IBrush LightBodyForeground = ImmutableBrushFor("#0F172A");
    private static readonly IBrush DarkFooterForeground = ImmutableBrushFor("#9DA8B5");
    private static readonly IBrush LightFooterForeground = ImmutableBrushFor("#475569");

    private TextAssetPreviewResult? currentTextAssetPreview;
    private TextAssetPreviewMode currentPreviewMode = TextAssetPreviewMode.Cards;

    private enum TextAssetPreviewMode
    {
        Cards,
        Text,
        Info
    }

    private void ShowTextAssetDialoguePreview(
        AssetItem assetItem,
        TextAssetPreviewResult preview,
        TextAssetDialogueCardViewModel[]? prebuiltCards = null)
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

        TextAssetDetailsTextBox.FontFamily = TextAssetMonoFontFamily;
        TextAssetDetailsTextBox.FontSize = 13;

        if (prebuiltCards != null)
        {
            TextAssetDialogueCardsList.ItemsSource = prebuiltCards;
        }
        else
        {
            BuildTextAssetDialogueCards(preview);
        }

        TextAssetPreviewPanel.IsVisible = true;
        SetTextAssetPreviewMode(TextAssetPreviewMode.Cards);
    }

    private void ClearTextAssetPreview()
    {
        _previewRequestCoordinator.CancelCurrent();
        currentTextAssetPreview = null;
        TextAssetPreviewPanel.IsVisible = false;
        TextAssetDetailsTextBox.Text = string.Empty;
        TextAssetDialogueCardsList.ItemsSource = null;
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
        TextAssetDialogueCardsList.IsVisible = showCards;
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
        IBrush inactiveBg = isDark ? InactiveModeDarkBackground : InactiveModeLightBackground;
        IBrush inactiveFg = isDark ? Brushes.White : InactiveModeLightForeground;

        TextAssetCardsViewButton.Background = mode == TextAssetPreviewMode.Cards ? ActiveModeBackground : inactiveBg;
        TextAssetDetailsViewButton.Background = mode == TextAssetPreviewMode.Text ? ActiveModeBackground : inactiveBg;
        TextAssetInfoViewButton.Background = mode == TextAssetPreviewMode.Info ? ActiveModeBackground : inactiveBg;

        TextAssetCardsViewButton.Foreground = mode == TextAssetPreviewMode.Cards ? ActiveModeForeground : inactiveFg;
        TextAssetDetailsViewButton.Foreground = mode == TextAssetPreviewMode.Text ? ActiveModeForeground : inactiveFg;
        TextAssetInfoViewButton.Foreground = mode == TextAssetPreviewMode.Info ? ActiveModeForeground : inactiveFg;
    }

    private void BuildTextAssetDialogueCards(TextAssetPreviewResult preview)
    {
        bool isDark = ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
        TextAssetDialogueCardsList.ItemsSource = BuildTextAssetDialogueCardViewModels(preview.DialogueCards, isDark);
    }

    internal static TextAssetDialogueCardViewModel[] BuildTextAssetDialogueCardViewModels(
        IReadOnlyList<TextAssetDialogueCard> cards,
        bool isDark,
        CancellationToken cancellationToken = default)
    {
        if (cards == null || cards.Count == 0)
        {
            return Array.Empty<TextAssetDialogueCardViewModel>();
        }

        var result = new TextAssetDialogueCardViewModel[cards.Count];
        IBrush noteBg = isDark ? DarkNoteBackground : LightNoteBackground;
        IBrush noteBorder = isDark ? DarkNoteBorder : LightNoteBorder;
        IBrush dialogueBg = isDark ? DarkDialogueBackground : LightDialogueBackground;
        IBrush dialogueBorder = isDark ? DarkDialogueBorder : LightDialogueBorder;
        IBrush titleFg = isDark ? Brushes.White : LightTitleForeground;
        IBrush metadataFg = isDark ? DarkMetadataForeground : LightMetadataForeground;
        IBrush bodyFg = isDark ? DarkBodyForeground : LightBodyForeground;
        IBrush footerFg = isDark ? DarkFooterForeground : LightFooterForeground;

        for (int i = 0; i < cards.Count; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var card = cards[i];
            bool isNote = string.Equals(card.Kind, "Note", StringComparison.Ordinal);
            result[i] = new TextAssetDialogueCardViewModel
            {
                Title = BuildTextAssetCardTitle(card, i + 1),
                OffsetText = $"0x{card.Offset.ToString("X6", CultureInfo.InvariantCulture)}",
                BodyText = card.Text,
                FooterText = BuildTextAssetCardFooter(card),
                CardBackground = isNote ? noteBg : dialogueBg,
                CardBorderBrush = isNote ? noteBorder : dialogueBorder,
                TitleForeground = titleFg,
                MetadataForeground = metadataFg,
                BodyForeground = bodyFg,
                FooterForeground = footerFg
            };
        }

        return result;
    }

    internal static string BuildTextAssetCardTitle(TextAssetDialogueCard card, int number)
    {
        var prefix = number.ToString("D3", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(card.Label))
        {
            return $"{prefix} | {card.Label}";
        }

        return $"{prefix} | {card.Kind}";
    }

    internal static string BuildTextAssetCardFooter(TextAssetDialogueCard card)
    {
        var label = string.IsNullOrWhiteSpace(card.Label) ? "unlabeled" : card.Label;
        var speaker = string.IsNullOrWhiteSpace(card.Speaker) ? string.Empty : $" | speaker hint: {card.Speaker}";
        return $"{card.Kind} | {label}{speaker} | {card.Length:N0} bytes";
    }

    private static IBrush ImmutableBrushFor(string color)
    {
        if (color.Length == 7 && color[0] == '#')
        {
            byte r = byte.Parse(color.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(color.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(color.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new ImmutableSolidColorBrush(new global::Avalonia.Media.Color(255, r, g, b));
        }

        return Brushes.Transparent;
    }

    private void PreviewTextAsset(AssetItem assetItem, TextAsset m_TextAsset, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        bool isDark = ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;

        TextPreviewBox.FontFamily = TextAssetMonoFontFamily;
        TextPreviewBox.FontSize = 13;
        TextPreviewBox.Text = "Loading TextAsset preview...";
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        PreviewInfoBorder.IsVisible = false;
        StatusStripUpdate($"Loading TextAsset preview: {assetItem.Name}...");

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var data = m_TextAsset.m_Script ?? Array.Empty<byte>();
                var preview = TextAssetPreviewBuilder.BuildPreview(assetItem, data, fbxHeader);
                token.ThrowIfCancellationRequested();

                TextAssetDialogueCardViewModel[]? cardViewModels = null;
                string? truncatedDetails = null;
                if (preview.HasDialogueCards)
                {
                    cardViewModels = BuildTextAssetDialogueCardViewModels(preview.DialogueCards, isDark, token);
                }
                else
                {
                    truncatedDetails = TruncatePreviewText(preview.DetailsText);
                }

                token.ThrowIfCancellationRequested();

                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    if (preview.HasDialogueCards)
                    {
                        bool currentDark = ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
                        var finalCards = currentDark == isDark
                            ? cardViewModels
                            : BuildTextAssetDialogueCardViewModels(preview.DialogueCards, currentDark);
                        ShowTextAssetDialoguePreview(assetItem, preview, finalCards);
                        StatusStripUpdate($"TextAsset localized preview loaded ({preview.DialogueCards.Count:N0} dialogue-like strings, {data.Length:N0} bytes).");
                        return;
                    }

                    TextPreviewBox.FontFamily = TextAssetMonoFontFamily;
                    TextPreviewBox.FontSize = 13;
                    TextPreviewBox.Text = truncatedDetails ?? string.Empty;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    PreviewInfoBorder.IsVisible = false;

                    StatusStripUpdate($"TextAsset preview loaded ({data.Length:N0} bytes).");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"TextAsset preview failed for {assetItem.Name}: {ex}");
                var errorText = TruncatePreviewText($"Failed to preview TextAsset {assetItem.Name}:{Environment.NewLine}{ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    TextPreviewBox.Text = errorText;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    PreviewInfoBorder.IsVisible = false;
                    StatusStripUpdate($"TextAsset preview error: {ex.Message}");
                });
            }
        }, token);
    }
}
