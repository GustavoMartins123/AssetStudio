using Avalonia.Input;

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
                    GLPreviewControl.WireframeMode = (GLPreviewControl.WireframeMode + 1) % 3;
                    handled = true;
                    break;
                case Key.S:
                    GLPreviewControl.ShadeMode = GLPreviewControl.ShadeMode == 0 ? 1 : 0;
                    handled = true;
                    break;
                case Key.N:
                    GLPreviewControl.NormalMode = GLPreviewControl.NormalMode == 0 ? 1 : 0;
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
                    textureChannels[0] = !textureChannels[0];
                    handled = true;
                    break;
                case Key.G:
                    textureChannels[1] = !textureChannels[1];
                    handled = true;
                    break;
                case Key.B:
                    textureChannels[2] = !textureChannels[2];
                    handled = true;
                    break;
                case Key.A:
                    textureChannels[3] = !textureChannels[3];
                    handled = true;
                    break;
            }

            if (handled)
            {
                UpdateImagePreview();
                e.Handled = true;
            }
        }
    }

}