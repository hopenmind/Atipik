using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Atypik.Core;

/// <summary>
/// System-wide hotkey registration (works while A-typik is in the tray or any
/// other app is foreground). Built on <c>RegisterHotKey</c> + a WPF
/// <see cref="HwndSource"/> hook to receive <c>WM_HOTKEY</c>.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int  WM_HOTKEY   = 0x0312;

    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly int        _id;
    private          bool       _registered;
    private          bool       _disposed;

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <param name="hwnd">Handle of a WPF window (use <c>WindowInteropHelper</c>).</param>
    /// <param name="id">Arbitrary id distinguishing this hotkey from others
    /// registered on the same window (each id = one key combination).</param>
    public GlobalHotkey(IntPtr hwnd, int id = 1)
    {
        _source = HwndSource.FromHwnd(hwnd)
                   ?? throw new InvalidOperationException("HwndSource not available.");
        _id = id & 0x7FFF;
    }

    /// <summary>
    /// Register the hotkey. Returns false if the OS rejects it (usually because
    /// another app already owns the key combination).
    /// </summary>
    public bool Register(uint modifiers, uint key)
    {
        _source.AddHook(Hook);
        _registered = RegisterHotKey(_source.Handle, _id, modifiers | MOD_NOREPEAT, key);
        return _registered;
    }

    public bool IsRegistered => _registered;

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source.RemoveHook(Hook); } catch { }
        if (_registered) UnregisterHotKey(_source.Handle, _id);
    }
}
