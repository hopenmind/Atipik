using System;
using System.Windows;

namespace Atypik.Overlay;

/// <summary>
/// Result of a bounding box selection.
/// Contains screen coordinates and the HWND of the target window.
/// </summary>
public sealed record FrameResult(
    Rect   ScreenRect,   // selection in screen pixels
    IntPtr TargetHWnd,   // window under the selection center
    string TargetTitle   // window title for display
);
