using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using Atypik.Overlay;

namespace Atypik.Core;

/// <summary>
/// Grabs the currently-selected text from whatever foreign window is in the
/// foreground, and synthesizes a capture frame around the mouse cursor.
///
/// Flow: snapshot the clipboard -> send Ctrl+C to the focused window -> poll the
/// clipboard for the new text -> restore the previous clipboard -> build a frame
/// anchored on the cursor (so re-focusing lands back in the field the user was
/// editing) -> return the text + frame for the overlay.
/// </summary>
internal static class SelectionCapture
{
    private const double FrameW = 380;
    private const double FrameH = 150;

    /// <summary>
    /// Attempt to grab the foreground selection. Returns false when the
    /// foreground window is our own process, or no selectable text was found.
    /// </summary>
    public static bool TryGrab(out FrameResult? frame, out string text)
    {
        frame = null;
        text  = string.Empty;

        var fg = WinNative.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        // Don't steal from our own windows.
        WinNative.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        // Snapshot the clipboard text so we can restore it afterwards.
        string previous = SafeGetText();

        // Ask the foreground window to copy its selection.
        WinNative.SendCopy();

        // Poll for the clipboard to reflect the selection.
        string selected = string.Empty;
        for (int i = 0; i < 40; i++)          // up to ~400 ms
        {
            Thread.Sleep(10);
            selected = SafeGetText();
            if (!string.IsNullOrEmpty(selected) && selected != previous)
                break;
        }

        // Restore the user's clipboard to its prior text.
        try
        {
            if (!string.IsNullOrEmpty(previous)) Clipboard.SetText(previous);
            else Clipboard.Clear();
        }
        catch { /* clipboard locked - non-fatal */ }

        if (string.IsNullOrEmpty(selected))
        {
            // No selection copied. We still open the overlay (empty) so the user
            // isn't left hanging, but signal that nothing was grabbed.
            DebugLog.Write("SelectionCapture: no selection found");
            frame = CursorFrame(fg);
            return false;
        }

        DebugLog.Write("SelectionCapture: grabbed {0} chars", selected.Length);
        frame = CursorFrame(fg);
        text  = selected;
        return true;
    }

    private static string SafeGetText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Build a frame centred on the cursor, clamped to the virtual screen.</summary>
    private static FrameResult CursorFrame(IntPtr hwnd)
    {
        WinNative.GetCursorPos(out WinNative.POINT pt);

        double x = pt.X - FrameW / 2;
        double y = pt.Y - FrameH / 2;

        // Clamp into the virtual desktop so the overlay stays fully on-screen.
        double vwLeft = SystemParameters.VirtualScreenLeft;
        double vwTop  = SystemParameters.VirtualScreenTop;
        double vwW    = SystemParameters.VirtualScreenWidth;
        double vwH    = SystemParameters.VirtualScreenHeight;

        x = Math.Clamp(x, vwLeft, vwLeft + vwW - FrameW);
        y = Math.Clamp(y, vwTop,  vwTop  + vwH - FrameH);

        var rect  = new Rect(x, y, FrameW, FrameH);
        var title = GetTitle(hwnd);
        return new FrameResult(rect, hwnd, title);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        WinNative.GetWindowText(hwnd, sb, 256);
        return sb.Length > 0 ? sb.ToString() : "(selection)";
    }
}
