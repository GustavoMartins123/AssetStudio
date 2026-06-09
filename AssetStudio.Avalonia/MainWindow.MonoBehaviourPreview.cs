using AssetStudio;
using System;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private async void PreviewMonoBehaviour(AssetItem assetItem, MonoBehaviour m_MonoBehaviour, string fbxHeader, string? dumpStr)
    {
        try
        {
            object? obj = m_MonoBehaviour.ToType();
            if (obj == null)
            {
                var typeTree = await MonoBehaviourToTypeTree(m_MonoBehaviour);
                if (typeTree != null)
                {
                    obj = m_MonoBehaviour.ToType(typeTree);
                }
            }

            if (obj != null)
            {
                var str = Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                SetTextWithTruncation(TextPreviewBox, fbxHeader + str);
                TextPreviewBox.IsVisible = true;
                PreviewLabel.IsVisible = false;
                StatusStripUpdate("MonoBehaviour preview loaded (JSON format).");
                return;
            }
        }
        catch
        {
            // Fallback
        }

        if (dumpStr == null)
        {
            var typeTree = await MonoBehaviourToTypeTree(m_MonoBehaviour);
            if (typeTree != null)
            {
                dumpStr = m_MonoBehaviour.Dump(typeTree);
            }
        }

        if (dumpStr != null)
        {
            SetTextWithTruncation(TextPreviewBox, fbxHeader + dumpStr);
            TextPreviewBox.IsVisible = true;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate("MonoBehaviour loaded (text dump).");
        }
        else
        {
            StatusStripUpdate("MonoBehaviour loaded (no dump/types available).");
        }
    }

}