using System;
using System.Globalization;
using Avalonia.Threading;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    /// <summary>
    /// Unified headless state machine for AudioClip / VideoClip preview transport,
    /// delegated directly to the canonical instance on <see cref="MainWindowViewModel"/>.
    /// </summary>
    internal MediaPlaybackViewModel MediaPlayback => ViewModel.MediaPlayback;

    private static void DispatchMediaPlaybackToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private static string FormatMediaTime(long currentMs, long totalMs)
    {
        return MediaPlaybackViewModel.FormatTimeRange(currentMs, totalMs);
    }

    private static string GetPreviewAssetId(AssetStudio.Object asset)
    {
        var source = asset.assetsFile?.fullName ?? asset.assetsFile?.fileName ?? string.Empty;
        return string.Concat(source, "\u001f", asset.m_PathID.ToString(CultureInfo.InvariantCulture));
    }
}
