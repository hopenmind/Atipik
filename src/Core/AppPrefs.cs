using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Atypik.Overlay;

namespace Atypik.Core;

/// <summary>
/// Centralised, file-backed preferences under <c>%APPDATA%\Atypik</c>.
///
/// Replaces the scattered pref-path fields that were duplicated across
/// <see cref="App"/>, <see cref="MainWindow"/> and the settings windows.
/// Stores plain string values (one file per key) for backwards compatibility
/// with the existing pref files written by older builds.
/// </summary>
public static class AppPrefs
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atypik");

    // -- Win32: validate that a saved HWND is still a live window --------------
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    // -- Generic string pref helpers -------------------------------------------

    private static string PathFor(string key) => Path.Combine(Dir, key);

    public static string Get(string key, string fallback = "")
    {
        string p = PathFor(key);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : fallback;
    }

    public static double GetDouble(string key, double fallback)
    {
        return double.TryParse(Get(key),
                   System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out double v)
               ? v : fallback;
    }

    public static bool GetFlag(string key) => File.Exists(PathFor(key));

    public static void Set(string key, string value)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(key), value);
    }

    public static void SetFlag(string key, bool on)
    {
        Directory.CreateDirectory(Dir);
        string p = PathFor(key);
        if (on) File.WriteAllText(p, "1");
        else if (File.Exists(p)) File.Delete(p);
    }

    // -- Last capture frame (for instant recall) -------------------------------

    private const string LastFrameFile = "last_frame.json";

    /// <summary>Save the most recent successful capture so it can be reused.</summary>
    public static void SaveLastFrame(FrameResult frame)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var dto = new StoredFrame(
                frame.ScreenRect.Left, frame.ScreenRect.Top,
                frame.ScreenRect.Width, frame.ScreenRect.Height,
                frame.TargetHWnd.ToInt64(), frame.TargetTitle);
            File.WriteAllText(PathFor(LastFrameFile),
                JsonSerializer.Serialize(dto));
        }
        catch { /* never block the capture flow on persistence */ }
    }

    /// <summary>
    /// Returns the last captured frame if its target window is still alive,
    /// otherwise null. The screen rectangle is reused as-is.
    /// </summary>
    public static FrameResult? LoadLastFrameIfValid()
    {
        string p = PathFor(LastFrameFile);
        if (!File.Exists(p)) return null;

        try
        {
            var dto = JsonSerializer.Deserialize<StoredFrame>(File.ReadAllText(p));
            if (dto is null) return null;

            var hwnd = new IntPtr(dto.Hwnd);
            if (!IsWindow(hwnd)) return null;   // target closed -> force redraw

            var rect = new Rect(dto.Left, dto.Top, dto.Width, dto.Height);
            if (rect.Width < 20 || rect.Height < 20) return null;

            return new FrameResult(rect, hwnd, dto.Title);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forget the saved frame (e.g. user asked to redraw permanently).</summary>
    public static void ClearLastFrame()
    {
        try { if (File.Exists(PathFor(LastFrameFile))) File.Delete(PathFor(LastFrameFile)); }
        catch { }
    }

    private sealed record StoredFrame(
        double Left, double Top, double Width, double Height, long Hwnd, string Title);
}
