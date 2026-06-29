using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Atypik.Core;

/// <summary>
/// Minimal Win32 interop surface for cross-app input and window queries.
/// Kept dependency-free and isolated from the kinetic engine.
/// </summary>
internal static class WinNative
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    internal const uint INPUT_KEYBOARD    = 1;
    internal const uint KEYEVENTF_KEYUP   = 0x0002;
    internal const ushort VK_CONTROL      = 0x11;
    internal const ushort VK_C            = 0x43;
    internal const ushort VK_V            = 0x56;

    // Clipboard
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    private const uint GMEM_MOVEABLE  = 0x0002;
    private const uint CF_UNICODETEXT = 13;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    // INPUT (keyboard subset) - explicit layout, 40 bytes on x64.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct INPUT
    {
        [FieldOffset(0)]  public uint   type;
        [FieldOffset(8)]  public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint   kbdFlags;
        [FieldOffset(16)] public uint   kbdTime;
        [FieldOffset(24)] public IntPtr kbdExtra;
    }

    /// <summary>
    /// Send Ctrl+C (copy) to whatever window is currently foreground.
    /// Returns the number of events injected.
    /// </summary>
    internal static uint SendCopy()
    {
        var inputs = new INPUT[4];

        inputs[0] = Kbd(VK_CONTROL, down: true);
        inputs[1] = Kbd(VK_C,       down: true);
        inputs[2] = Kbd(VK_C,       down: false);
        inputs[3] = Kbd(VK_CONTROL, down: false);

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Kbd(ushort vk, bool down) => new()
    {
        type     = INPUT_KEYBOARD,
        wVk      = vk,
        kbdFlags = down ? 0u : KEYEVENTF_KEYUP,
    };

    /// <summary>Send Ctrl+V (paste) to the foreground window.</summary>
    internal static uint SendPaste()
    {
        var inputs = new INPUT[4];
        inputs[0] = Kbd(VK_CONTROL, down: true);
        inputs[1] = Kbd(VK_V,       down: true);
        inputs[2] = Kbd(VK_V,       down: false);
        inputs[3] = Kbd(VK_CONTROL, down: false);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Place Unicode text on the clipboard (thread-agnostic; no STA required).
    /// Replaces the previous clipboard contents.
    /// </summary>
    internal static bool SetClipboardText(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (h == IntPtr.Zero) return false;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) return false;
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);

        // The clipboard is often briefly locked by other apps; retry a few times.
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                EmptyClipboard();
                if (SetClipboardData(CF_UNICODETEXT, h) != IntPtr.Zero)
                {
                    CloseClipboard();
                    h = IntPtr.Zero;   // ownership transferred to the OS
                    return true;
                }
                CloseClipboard();
                break;
            }
            Thread.Sleep(10);
        }
        return false;
    }
}
