using System;
using System.Runtime.InteropServices;
using System.Threading;
using Atypik.Core;

namespace Atypik.KineticEngine;

/// <summary>
/// Injects text into a target window with a statistically naturalistic
/// behavioral signature - Weibull-distributed IKT, bigram-aware timing,
/// 2D topographical error injection, and polymorphic correction sequences.
///
/// No two injections produce the same timing fingerprint.
/// </summary>
public sealed class KineticObfuscator : IDisposable
{
    // -- Win32 -----------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // -- INPUT struct - explicit layout, 40 bytes on x64 ----------------------
    //
    // Win32 layout (x64):
    //   offset  0 : DWORD  type            (4 bytes)
    //   offset  4 : padding                (4 bytes - union requires 8-byte alignment)
    //   offset  8 : union { KEYBDINPUT | MOUSEINPUT }
    //
    // KEYBDINPUT (at union offset 0 = INPUT offset 8):
    //   +0  wVk        (2)   +2  wScan   (2)   +4  dwFlags (4)
    //   +8  time       (4)   +12 padding (4)   +16 dwExtraInfo (8)
    //   -> INPUT offsets: wVk@8, wScan@10, kbdFlags@12, kbdTime@16, kbdExtra@24
    //
    // MOUSEINPUT (at union offset 0 = INPUT offset 8):
    //   +0  dx (4)  +4  dy (4)  +8  mouseData (4)  +12 dwFlags (4)
    //   +16 time (4)  +20 padding (4)  +24 dwExtraInfo (8)
    //   -> INPUT offsets: dx@8, dy@12, mouseData@16, mouseFlags@20, mouseTime@24, mouseExtra@32
    //
    // Total INPUT size = 40 bytes.
    // -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]  public uint   type;

        // KEYBDINPUT fields
        [FieldOffset(8)]  public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint   kbdFlags;
        [FieldOffset(16)] public uint   kbdTime;
        [FieldOffset(24)] public IntPtr kbdExtra;

        // MOUSEINPUT fields (share same union bytes)
        [FieldOffset(8)]  public int    dx;
        [FieldOffset(12)] public int    dy;
        [FieldOffset(16)] public uint   mouseData;
        [FieldOffset(20)] public uint   mouseFlags;
        [FieldOffset(24)] public uint   mouseTime;
        [FieldOffset(32)] public IntPtr mouseExtra;
    }

    private const uint INPUT_KEYBOARD    = 1;
    private const uint INPUT_MOUSE       = 0;
    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int  SM_XVIRTUALSCREEN  = 76;
    private const int  SM_YVIRTUALSCREEN  = 77;
    private const int  SM_CXVIRTUALSCREEN = 78;
    private const int  SM_CYVIRTUALSCREEN = 79;
    private const ushort VK_BACK   = 0x08;
    private const ushort VK_RETURN = 0x0D;

    // -- Engine ----------------------------------------------------------------
    private readonly Random          _rng;
    private readonly WeibullSampler  _sampler;
    private readonly double          _typoRate;
    private readonly KeyboardLayout  _configuredLayout;
    private          LayoutMatrix    _layout;
    private          BigramTable     _bigrams;

    // Maximum consecutive characters before a micro-burst pause
    private const int BurstThreshold = 8;

    /// <param name="layout">Physical keyboard layout for topographical error generation.</param>
    /// <param name="typoRate">Fraction of letter keystrokes that trigger a fat-finger sequence (0.012 = 1.2%).</param>
    /// <param name="weibullK">Weibull shape k for IKT distribution (1.85 = experienced typist).</param>
    /// <param name="seed">Optional RNG seed for reproducible tests.</param>
    public KineticObfuscator(
        KeyboardLayout layout    = KeyboardLayout.Azerty,
        double         typoRate  = 0.012,
        double         weibullK  = 1.85,
        int?           seed      = null)
    {
        _rng      = seed.HasValue ? new Random(seed.Value) : new Random();
        _sampler  = new WeibullSampler(seed, weibullK);
        _typoRate = typoRate;
        _configuredLayout = layout;
        _layout   = new LayoutMatrix(LayoutDetector.Resolve(layout));
        _bigrams  = new BigramTable(_layout);
    }

    /// <summary>
    /// When the configured layout is Auto, re-detect the foreground window's
    /// layout before each injection so the topographic error model matches the
    /// active input method. Cheap no-op for fixed layouts.
    /// </summary>
    private void EnsureLayout()
    {
        if (_configuredLayout != KeyboardLayout.Auto) return;
        var resolved = LayoutDetector.Resolve(_configuredLayout);
        if (resolved == _layout.ActiveLayout) return;
        _layout  = new LayoutMatrix(resolved);
        _bigrams = new BigramTable(_layout);
    }

    /// <summary>
    /// Injects <paramref name="cleanText"/> into <paramref name="targetHWnd"/>
    /// with a naturalistic behavioral signature.
    /// Does NOT send Enter - caller decides.
    /// <paramref name="progress"/> receives (charsInjected, totalChars) after each character.
    /// </summary>
    public void InjectText(
        string                          cleanText,
        IntPtr                          targetHWnd,
        IProgress<(int done, int total)>? progress = null)
    {
        if (string.IsNullOrEmpty(cleanText)) return;

        int total = cleanText.Length;

        EnsureLayout();   // refresh layout when Auto-detect is enabled

        SetForegroundWindow(targetHWnd);
        Thread.Sleep(85 + _rng.Next(40)); // focus stabilization jitter

        int burstCounter = 0;

        for (int i = 0; i < cleanText.Length; i++)
        {
            char current = cleanText[i];
            char prev    = i > 0 ? cleanText[i - 1] : '\0';
            bool isWordBoundary = current == ' ' || current == '\n'
                               || char.IsPunctuation(current);

            // Topographical error injection
            if (ShouldInjectTypo(current))
            {
                InjectTypoSequence(current, prev, isWordBoundary);
                burstCounter = 0;
                progress?.Report((i + 1, total));
                continue;
            }

            // Send the actual character
            SendUnicodeChar(current);

            // Compute bigram-aware Weibull delay
            BigramProfile profile = i < cleanText.Length - 1
                ? _bigrams.Compute(current, cleanText[i + 1], isWordBoundary)
                : default;

            int delay = _sampler.SampleDelay(profile);

            // Burst counter - inject micro-pause after long runs
            burstCounter = isWordBoundary ? 0 : burstCounter + 1;
            if (burstCounter >= BurstThreshold)
            {
                delay += _sampler.SampleDelay(new BigramProfile
                {
                    ScaleLambda    = 180,
                    IsWordBoundary = true
                });
                burstCounter = 0;
            }

            Thread.Sleep(delay);
            progress?.Report((i + 1, total));
        }
    }

    /// <summary>
    /// Sends Enter after text injection.
    /// </summary>
    public void SendEnter(IntPtr targetHWnd)
    {
        SetForegroundWindow(targetHWnd);
        Thread.Sleep(_sampler.SampleDelay(new BigramProfile { ScaleLambda = 140 }));
        SendVirtualKey(VK_RETURN);
    }

    /// <summary>
    /// Fast paste mode: instead of typing character by character, place the text
    /// on the clipboard and send Ctrl+V. Trades the naturalistic keystroke
    /// signature for speed (useful for long messages where keystroke-by-keystroke
    /// injection would be too slow). Keeps small human-like preparation delays.
    /// </summary>
    public void InjectByPaste(
        string cleanText,
        IntPtr targetHWnd,
        IProgress<(int done, int total)>? progress = null)
    {
        if (string.IsNullOrEmpty(cleanText)) return;

        EnsureLayout();

        SetForegroundWindow(targetHWnd);
        // Human-like preparation pause before pasting.
        Thread.Sleep(120 + _rng.Next(70));

        if (!WinNative.SetClipboardText(cleanText))
        {
            Core.DebugLog.Write("InjectByPaste: clipboard set failed, aborting");
            return;
        }

        Thread.Sleep(34 + _rng.Next(36));   // small gap, clipboard -> paste
        WinNative.SendPaste();
        Thread.Sleep(70 + _rng.Next(50));   // post-paste settle

        progress?.Report((1, 1));
        Core.DebugLog.Write("InjectByPaste: done ({0} chars)", cleanText.Length);
    }

    // -- Private ---------------------------------------------------------------

    private bool ShouldInjectTypo(char c)
        => char.IsLetter(c) && _rng.NextDouble() < _typoRate;

    /// <summary>
    /// Full typo-correction sequence:
    ///   wrong key -> realization pause -> backspace -> correction pause -> correct key
    /// All delays Weibull-sampled independently.
    /// </summary>
    private void InjectTypoSequence(char intended, char prev, bool isWordBoundary)
    {
        if (!_layout.TryGetTypo(intended, _rng, out char typo))
        {
            // No viable neighbor - skip typo
            SendUnicodeChar(intended);
            return;
        }

        // 1. Type the wrong key
        SendUnicodeChar(typo);
        Thread.Sleep(_sampler.SampleDelay(new BigramProfile
        {
            ScaleLambda    = 180,   // realization pause - longer than normal IKT
            IsWordBoundary = false
        }));

        // 2. Backspace
        SendVirtualKey(VK_BACK);
        Thread.Sleep(_sampler.SampleCorrectionPause());

        // 3. Type the correct key
        SendUnicodeChar(intended);
        Thread.Sleep(_sampler.SampleResumptionDelay());
    }

    private void SendUnicodeChar(char c)
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0] = MakeUnicodeInput(c, false);
        inputs[1] = MakeUnicodeInput(c, true);
        uint sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            Core.DebugLog.Write("SendUnicodeChar '{0}': SendInput returned 0 (err={1})",
                c, Marshal.GetLastWin32Error());
    }

    private void SendVirtualKey(ushort vk)
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0] = MakeVkInput(vk, false);
        inputs[1] = MakeVkInput(vk, true);
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeUnicodeInput(char c, bool keyUp) => new()
    {
        type     = INPUT_KEYBOARD,
        wVk      = 0,
        wScan    = c,
        kbdFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u),
        kbdTime  = 0,
        kbdExtra = IntPtr.Zero,
    };

    private static INPUT MakeVkInput(ushort vk, bool keyUp) => new()
    {
        type     = INPUT_KEYBOARD,
        wVk      = vk,
        wScan    = 0,
        kbdFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
        kbdTime  = 0,
        kbdExtra = IntPtr.Zero,
    };

    // -- Mouse click - used by OverlayWindow to focus the target control --------

    /// <summary>
    /// Sends a left mouse click at absolute screen coordinates.
    /// Handles multi-monitor setups via MOUSEEVENTF_VIRTUALDESK.
    /// </summary>
    public static void SendMouseClick(int screenX, int screenY)
    {
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);

        int absX = (int)((double)(screenX - vx) / vw * 65535 + 0.5);
        int absY = (int)((double)(screenY - vy) / vh * 65535 + 0.5);

        var inputs = new[]
        {
            new INPUT {
                type = INPUT_MOUSE, dx = absX, dy = absY,
                mouseFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
            new INPUT {
                type = INPUT_MOUSE, dx = absX, dy = absY,
                mouseFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
            new INPUT {
                type = INPUT_MOUSE, dx = absX, dy = absY,
                mouseFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Core.DebugLog.Write("SendMouseClick: ({0},{1}) abs({2},{3}) sent={4}/3",
            screenX, screenY, absX, absY, sent);
    }

    public void Dispose() { /* future: cleanup resources */ }
}
