using Avalonia.Input;
using AssetStudio.Avalonia.ViewModels;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyModifiers == KeyModifiers.Control && GLPreviewControl != null && GLPreviewControl.IsVisible)
        {
            bool handled = false;
            switch (e.Key)
            {
                case Key.W:
                    ViewModel.CycleWireframeModeCommand.Execute();
                    handled = true;
                    break;
                case Key.S:
                    ViewModel.ToggleShadeModeCommand.Execute();
                    handled = true;
                    break;
                case Key.N:
                    ViewModel.ToggleNormalModeCommand.Execute();
                    handled = true;
                    break;
            }
            if (handled)
            {
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Control && (currentPreviewTexture != null || currentPreviewSprite != null))
        {
            bool handled = false;
            switch (e.Key)
            {
                case Key.R:
                    ViewModel.ToggleTextureChannelCommand.Execute(PreviewChannelMask.Red);
                    handled = true;
                    break;
                case Key.G:
                    ViewModel.ToggleTextureChannelCommand.Execute(PreviewChannelMask.Green);
                    handled = true;
                    break;
                case Key.B:
                    ViewModel.ToggleTextureChannelCommand.Execute(PreviewChannelMask.Blue);
                    handled = true;
                    break;
                case Key.A:
                    ViewModel.ToggleTextureChannelCommand.Execute(PreviewChannelMask.Alpha);
                    handled = true;
                    break;
            }

            if (handled)
            {
                e.Handled = true;
            }
        }
    }
}
