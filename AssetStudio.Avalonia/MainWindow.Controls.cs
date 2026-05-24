using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AssetStudio.Avalonia;

public partial class MainWindow : Window
{
    private void ResetViewBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.ResetView();
    }

    private void RotateLeftBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateLeft90();
    }

    private void RotateRightBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateRight90();
    }

    private void RotateUpBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateUp90();
    }

    private void RotateDownBtn_Click(object? sender, RoutedEventArgs e)
    {
        GLPreviewControl?.RotateDown90();
    }

    private void BoneSizeSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GLPreviewControl != null && BoneSizeSlider != null)
        {
            GLPreviewControl.BoneScale = (float)BoneSizeSlider.Value;
            if (BoneSizeLabel != null)
            {
                BoneSizeLabel.Text = $"{BoneSizeSlider.Value:0.0}x";
            }
        }
    }
}
