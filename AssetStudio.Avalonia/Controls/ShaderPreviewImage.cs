using System;
using System.Runtime.InteropServices;
using AssetStudio.Avalonia.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace AssetStudio.Avalonia.Controls;

public sealed class ShaderPreviewImage : Image
{
    public static readonly StyledProperty<ShaderPreviewFrame?> FrameProperty =
        AvaloniaProperty.Register<ShaderPreviewImage, ShaderPreviewFrame?>(nameof(Frame));

    public ShaderPreviewFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty)
        {
            UpdateFrame(Frame);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Source == null && Frame != null)
        {
            UpdateFrame(Frame);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UpdateFrame(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateFrame(ShaderPreviewFrame? frame)
    {
        WriteableBitmap? previous = Source as WriteableBitmap;
        WriteableBitmap? bitmap = null;
        if (frame != null)
        {
            bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new global::Avalonia.Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using var pixels = bitmap.Lock();
            int stride = frame.Width * 4;
            for (int row = 0; row < frame.Height; row++)
            {
                Marshal.Copy(frame.RgbaPixels, row * stride, IntPtr.Add(pixels.Address, row * pixels.RowBytes), stride);
            }
        }
        Source = bitmap;
        previous?.Dispose();
    }
}
