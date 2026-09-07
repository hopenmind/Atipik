using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using Atypik.i18n;
using PixelFormat = System.Windows.Media.PixelFormats;

namespace Atypik.Core;

/// <summary>
/// System tray icon built on raw <c>Shell_NotifyIconW</c> + a hidden message-only
/// <see cref="HwndSource"/>. No WinForms dependency (keeps the single-file exe lean
/// and avoids WPF/WinForms namespace clashes).
///
/// Left double-click shows the main window; right click opens a WPF context menu.
/// </summary>
public sealed class TrayController : IDisposable
{
    // -- Shell_NotifyIcon ------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int    cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint   uVersion;            // union: uTimeout / uVersion
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public uint   dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, byte[]? lpvBits);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool   fIcon;
        public uint   xHotspot;
        public uint   yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private const uint NIM_ADD     = 0x00000000;
    private const uint NIM_MODIFY  = 0x00000001;
    private const uint NIM_DELETE  = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON    = 0x00000002;
    private const uint NIF_TIP     = 0x00000004;
    private const uint NIF_INFO    = 0x00000010;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int WM_APP          = 0x8000;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP     = 0x0205;

    // -- State -----------------------------------------------------------------
    private readonly MainWindow    _main;
    private readonly HwndSource    _source;
    private readonly uint          _callbackMsg;
    private readonly ContextMenu   _menu;
    private          IntPtr        _hIcon;
    private          bool          _added;
    private          bool          _disposed;
    private          byte[]?       _basePixels;  // cached logo pixels for status dot compositing
    private          int           _baseW, _baseH;

    public TrayController(MainWindow main)
    {
        _main = main;

        // Hidden message-only window to receive tray callbacks.
        var p = new HwndSourceParameters("AtypikTray")
        {
            WindowStyle  = 0,
            PositionX    = 0, PositionY = 0,
            Width        = 0, Height    = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW - keep out of the taskbar
        };
        _source = new HwndSource(p);
        _callbackMsg = (uint)(WM_APP + 1);
        _source.AddHook(WndProc);

        _menu = BuildMenu();
        _hIcon = LoadIcon();

        var nid = new NOTIFYICONDATA
        {
            cbSize          = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd            = _source.Handle,
            uID             = 1,
            uFlags          = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage= _callbackMsg,
            hIcon           = _hIcon,
            szTip           = "A'Tipik",
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref nid);

        // Announce version 4 so we get clean mouse messages.
        var ver = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd   = _source.Handle,
            uID    = 1,
            uFlags = 0,
            uVersion = NOTIFYICON_VERSION_4,
        };
        Shell_NotifyIconW(NIM_MODIFY, ref ver);   // best-effort
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var miCapture  = new MenuItem { Header = Loc.T("tray.capture") };
        var miShow     = new MenuItem { Header = Loc.T("tray.show") };
        var miSettings = new MenuItem { Header = Loc.T("tray.settings") };
        var miQuit     = new MenuItem { Header = Loc.T("tray.quit") };

        miCapture.Click  += (_, _) => _main.TriggerCapture();
        miShow.Click     += (_, _) => ShowMain();
        miSettings.Click += (_, _) => _main.OpenSettingsFromTray();
        miQuit.Click     += (_, _) => Quit();

        menu.Items.Add(miCapture);
        menu.Items.Add(miShow);
        menu.Items.Add(miSettings);
        menu.Items.Add(new Separator());
        menu.Items.Add(miQuit);
        return menu;
    }

    /// <summary>Show a balloon tip (e.g. to announce the global hotkey once).</summary>
    public void Notify(string title, string message, int timeoutMs = 4000)
    {
        if (!_added) return;
        var nid = new NOTIFYICONDATA
        {
            cbSize       = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd         = _source.Handle,
            uID          = 1,
            uFlags       = NIF_INFO,
            szInfo       = message,
            szInfoTitle  = title,
            dwInfoFlags  = NIIF_INFO,
            uVersion     = (uint)Math.Min(timeoutMs, 30000),
        };
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_APP + 1) return IntPtr.Zero;

        // Version 4 packs the mouse message in lParam, the icon id in wParam hi-word.
        uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

        switch (mouseMsg)
        {
            case WM_LBUTTONDBLCLK:
                ShowMain();
                handled = true;
                break;

            case WM_RBUTTONUP:
                OpenContextMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMain()
    {
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OpenContextMenu()
    {
        // SetForegroundWindow is the documented trick so the menu tracks
        // click-away correctly.
        SetForegroundWindow(_source.Handle);

        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen    = true;
        // Position the keyboard focus for accessibility.
        Keyboard.Focus(_menu);
    }

    private void Quit()
    {
        _disposed = true;
        RemoveIcon();
        (Application.Current as App)?.RequestQuit();
    }

    private void RemoveIcon()
    {
        if (!_added) return;
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd   = _source.Handle,
            uID    = 1,
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _added = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _source.Dispose();
    }

    // -- Icon creation (PNG -> HICON via WPF decoder + GDI CreateBitmap) ----------

    private IntPtr LoadIcon()
    {
        var (px, w, h) = BuildBasePixels();
        _basePixels = px;
        _baseW = w;
        _baseH = h;
        return HIconFromPixels(px, w, h);
    }

    /// <summary>Refresh the tray icon with a coloured status dot in the corner.</summary>
    public void UpdateStatus(TrayState state)
    {
        if (!_added || _basePixels is null || _disposed) return;
        var px = (byte[])_basePixels.Clone();
        StampDot(px, _baseW, _baseH, StateColor(state));

        var next = HIconFromPixels(px, _baseW, _baseH);
        if (next == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd   = _source.Handle,
            uID    = 1,
            uFlags = NIF_ICON,
            hIcon  = next,
        };
        Shell_NotifyIconW(NIM_MODIFY, ref nid);

        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = next;
    }

    private static (byte[] px, int w, int h) BuildBasePixels()
    {
        // Embedded brand assets first: a single-file standalone build (-p:EmbedAssets=true)
        // ships no loose assets folder, so the tray logo lives as a WPF resource.
        foreach (var uri in new[]
                 {
                     "pack://application:,,,/assets/logo-ico.png",
                     "pack://application:,,,/assets/logo.png",
                 })
        {
            var got = PixelsFromPng(uri, out int w, out int h);
            if (got is not null) return (got, w, h);
        }
        // Otherwise the loose files next to the exe (default build), then a drawn fallback.
        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "logo-ico.png"),
                     System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "logo.png"),
                 })
        {
            if (!System.IO.File.Exists(candidate)) continue;
            var got = PixelsFromPng(candidate, out int w, out int h);
            if (got is not null) return (got, w, h);
        }
        return (FallbackPixels(out int fw, out int fh), fw, fh);
    }

    private static byte[]? PixelsFromPng(string path, out int w, out int h)
    {
        w = 0; h = 0;
        try
        {
            var bmp = new BitmapImage(new Uri(path));
            bmp.Freeze();
            BitmapSource src = bmp.Format != PixelFormat.Bgra32
                ? new FormatConvertedBitmap(bmp, PixelFormat.Bgra32, null, 0)
                : bmp;
            w = src.PixelWidth; h = src.PixelHeight;
            int stride = w * 4;
            byte[] px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);
            return px;
        }
        catch { return null; }
    }

    private static IntPtr HIconFromPixels(byte[] pixels, int w, int h)
    {
        if (w == 0 || h == 0) return IntPtr.Zero;
        IntPtr color = CreateBitmap(w, h, 1, 32, pixels);
        byte[] andMask = new byte[((w + 7) / 8) * h];   // all 0 = fully opaque
        IntPtr mask = CreateBitmap(w, h, 1, 1, andMask);
        var ii = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        IntPtr hicon = CreateIconIndirect(ref ii);
        DeleteObject(color);
        DeleteObject(mask);
        return hicon;
    }

    /// <summary>Stamp a filled status dot at the bottom-right corner (BGRA in place).</summary>
    private static void StampDot(byte[] px, int w, int h, (byte b, byte g, byte r) col)
    {
        int radius = Math.Max(3, w / 6);
        double cx = w - radius - 1.5;
        double cy = h - radius - 1.5;
        // Outline ring (dark) for contrast on any background.
        for (int y = (int)(cy - radius - 1); y <= (int)(cy + radius + 1); y++)
        for (int x = (int)(cx - radius - 1); x <= (int)(cx + radius + 1); x++)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            int idx = (y * w + x) * 4;
            if (d <= radius)
            {
                px[idx + 0] = col.b; px[idx + 1] = col.g; px[idx + 2] = col.r; px[idx + 3] = 0xFF;
            }
            else if (d <= radius + 1.2)
            {
                px[idx + 0] = 0x10; px[idx + 1] = 0x0A; px[idx + 2] = 0x1E; px[idx + 3] = 0xFF;
            }
        }
    }

    private static (byte b, byte g, byte r) StateColor(TrayState state) => state switch
    {
        TrayState.ModelReady => (0x84, 0xDC, 0x3D), // green
        TrayState.Blocked    => (0x5C, 0x5C, 0xFF), // red
        _                    => (0x40, 0xB7, 0xF4), // amber (model off / idle)
    };

    private static byte[] FallbackPixels(out int w, out int h)
    {
        const int s = 32;
        w = s; h = s;
        byte[] px = new byte[s * s * 4]; // BGRA
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            double dx = x - 15.5, dy = y - 15.5;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int idx = (y * s + x) * 4;
            if (dist <= 14)
            {
                px[idx + 0] = 0xE6; // B
                px[idx + 1] = 0x8B; // G
                px[idx + 2] = 0x2E; // R
                px[idx + 3] = 0xFF; // A
            }
            else
            {
                px[idx + 0] = 0; px[idx + 1] = 0; px[idx + 2] = 0; px[idx + 3] = 0;
            }
        }
        return px;
    }
}

/// <summary>Visual states reflected by the tray icon dot.</summary>
public enum TrayState { Idle, ModelReady, ModelOff, Blocked }
