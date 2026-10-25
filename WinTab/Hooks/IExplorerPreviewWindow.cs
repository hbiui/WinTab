using System;
using System.Drawing;

namespace WinTab.Hooks;

/// <summary>
/// The hover preview surface. The hook drives it; tests replace it with a fake, so the interface stays
/// free of any WPF type.
/// </summary>
internal interface IExplorerPreviewWindow : IDisposable
{
    bool Visible { get; }

    void Show(ExplorerMediaPreview preview, Point cursor);

    void Hide();
}
