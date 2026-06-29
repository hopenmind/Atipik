using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Atypik.Core;
using Atypik.i18n;

namespace Atypik.Overlay;

public partial class OverlayWindow : Window
{
    // -- Win32 -----------------------------------------------------------------
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // -- State -----------------------------------------------------------------
    private readonly FrameResult                              _frame;
    private readonly Func<string, CancellationToken, Task<string>>? _corrector;
    private readonly KineticEngine.KineticObfuscator?           _engine;
    private readonly string?                                     _initialText;
    private          KineticEngine.KineticObfuscator?           _fallbackEngine;
    private readonly Random                                    _focusJitter = new();
    private          CancellationTokenSource                   _cts = new();
    private          bool                                     _busy;

    // -- Init ------------------------------------------------------------------

    public OverlayWindow(
        FrameResult frame,
        Func<string, CancellationToken, Task<string>>? corrector = null,
        KineticEngine.KineticObfuscator?               engine   = null,
        string?                                        initialText = null)
    {
        InitializeComponent();

        _frame       = frame;
        _corrector   = corrector;
        _engine      = engine;          // configured engine - honours user layout/Weibull/typo settings
        _initialText = initialText;     // pre-filled text (e.g. from a selection grab)

        Loaded  += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CancelAndClose(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Convert screen pixels -> WPF device-independent units
        var    src = PresentationSource.FromVisual(this);
        double sx  = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy  = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // Position exactly over the drawn frame - no extra strips
        Left   = _frame.ScreenRect.Left   / sx;
        Top    = _frame.ScreenRect.Top    / sy;
        Width  = Math.Max(_frame.ScreenRect.Width  / sx, MinWidth);
        Height = Math.Max(_frame.ScreenRect.Height / sy, MinHeight);

        // Activate the Win32 window first, THEN set logical focus
        // (Focus() alone fails silently when the overlay isn't the foreground window)
        Activate();
        InputBox.Focus();
        Keyboard.Focus(InputBox);

        // Pre-fill (e.g. text grabbed from a selection) and place the caret at the end.
        if (!string.IsNullOrEmpty(_initialText))
        {
            InputBox.Text = _initialText;
            InputBox.CaretIndex = _initialText.Length;
        }

        DebugLog.Write("OverlayWindow loaded: rect={0} hwnd=0x{1:X} title={2}",
            _frame.ScreenRect, _frame.TargetHWnd.ToInt64(), _frame.TargetTitle);
    }

    // -- Keyboard shortcuts ----------------------------------------------------

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (shift)
        {
            // Shift+Enter -> insert newline at caret
            int pos = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(pos, Environment.NewLine);
            InputBox.CaretIndex = pos + Environment.NewLine.Length;
            e.Handled = true;
        }
        else
        {
            // Enter (plain or Ctrl+Enter) -> inject
            e.Handled = true;
            _ = InjectAsync();
        }
    }

    // -- Options button --------------------------------------------------------

    private void OnOptionsClick(object sender, RoutedEventArgs e)
    {
        OptionsPopup.IsOpen = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
        => CancelAndClose();

    // -- Injection pipeline ----------------------------------------------------

    private async Task InjectAsync()
    {
        // -- Log BEFORE all guards so we always know this was called ----------
        DebugLog.Write("InjectAsync: called - busy={0} textLen={1}",
            _busy, InputBox.Text?.Length ?? -1);

        if (_busy) return;

        string raw = InputBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            DebugLog.Write("InjectAsync: aborted - empty text");
            return;
        }

        _busy = true;

        try
        {
            EnterInjectionMode();
            DebugLog.Write("InjectAsync: start - {0} chars, hwnd=0x{1:X}", raw.Length, _frame.TargetHWnd.ToInt64());

            // -- Prevent overlay from re-stealing focus during injection -------
            var selfHwnd = new WindowInteropHelper(this).Handle;
            int exStyle  = GetWindowLong(selfHwnd, GWL_EXSTYLE);
            SetWindowLong(selfHwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            DebugLog.Write("InjectAsync: WS_EX_NOACTIVATE applied to overlay hwnd=0x{0:X}", selfHwnd.ToInt64());

            // ++ IMMEDIACY: run focus + correction in parallel ++++++++++++++++++
            // In no-LLM mode the corrector is near-instant, so the focus task
            // dominates and the first keystroke goes out as soon as the target
            // is foreground. In LLM mode the focus settles DURING inference,
            // eliminating the old fixed 150 ms wait that stacked on top.
            bool usedCorrector = _corrector is not null;
            var  sw            = System.Diagnostics.Stopwatch.StartNew();

            Task<string> correctTask = _corrector is not null
                ? _corrector(raw, _cts.Token)
                : Task.FromResult(raw);

            if (_corrector is not null)
                SetStatus(Loc.T("overlay.correcting"));

            Task focusTask = FocusTargetAsync();

            string corrected = await correctTask;
            DebugLog.Write("InjectAsync: corrector done - {0} chars", corrected.Length);
            await focusTask;
            DebugLog.Write("InjectAsync: target focused");

            // Pipeline blocked - nothing constructive to inject
            if (corrected == ModularPipeline.BlockedSentinel)
            {
                DebugLog.Write("InjectAsync: pipeline blocked");
                (Application.Current as App)?.SignalBlocked();
                ShowStatus(Loc.T("overlay.blocked"));
                SetWindowLong(selfHwnd, GWL_EXSTYLE, exStyle);
                LeaveInjectionMode();
                _busy = false;
                return;
            }

            SetStatus(Loc.T("overlay.injecting"));
            DebugLog.Write("InjectAsync: target hwnd=0x{0:X} title={1}", _frame.TargetHWnd.ToInt64(), _frame.TargetTitle);

            // Progress callback - marshalled back to UI thread
            var uiProgress = new Progress<(int done, int total)>(p =>
            {
                double pct = p.total > 0 ? (double)p.done / p.total * 100.0 : 100;
                InjectionProgress.Value = pct;
            });

            DebugLog.Write("InjectAsync: KineticObfuscator.InjectText starting...");

            // Use the configured engine - NEVER a default-constructed one.
            // Fallback only covers the brief startup race before the pipeline is ready.
            var engine = _engine ?? (_fallbackEngine ??= new KineticEngine.KineticObfuscator());

            // Fast paste mode: bypass keystroke-by-keystroke typing for speed.
            bool paste = Core.AppPrefs.GetFlag("paste_mode");
            await Task.Run(() =>
            {
                if (paste)
                    engine.InjectByPaste(corrected, _frame.TargetHWnd, uiProgress);
                else
                    engine.InjectText(corrected, _frame.TargetHWnd, uiProgress);
            }, _cts.Token);

            DebugLog.Write("InjectAsync: KineticObfuscator.InjectText done");

            // -- Restore overlay to normal (allow activation again) ------------
            SetWindowLong(selfHwnd, GWL_EXSTYLE, exStyle);

            // -- Persist - clear and refocus instead of closing ----------------
            DebugLog.Write("InjectAsync: injection complete, clearing and refocusing");
            InputBox.Clear();
            LeaveInjectionMode();

            // Surface the correction latency so the user feels (and trusts) the
            // immediacy - especially the short-text skip which lands near 0 ms.
            sw.Stop();
            ShowStatus(usedCorrector
                ? string.Format(Loc.T("overlay.done_ms"), sw.ElapsedMilliseconds)
                : "✓");
            Activate();
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            _busy = false;
        }
        catch (OperationCanceledException)
        {
            DebugLog.Write("InjectAsync: cancelled");
            LeaveInjectionMode();
            _busy = false;
        }
        catch (Exception ex)
        {
            DebugLog.Write("InjectAsync: EXCEPTION {0}\n{1}", ex.Message, ex.StackTrace ?? "(no stack)");
            ShowStatus($"Error: {ex.Message}");
            LeaveInjectionMode();
            _busy = false;
        }
    }

    /// <summary>
    /// Click the centre of the frame to hand focus to the target field, then wait
    /// - adaptively - until the target window is actually foreground (capped).
    /// Replaces the old fixed <c>Task.Delay(150)</c>: returns as soon as focus is
    /// acquired, so the first keystroke is as immediate as possible.
    /// </summary>
    private async Task FocusTargetAsync()
    {
        int clickX = (int)(_frame.ScreenRect.Left + _frame.ScreenRect.Width  / 2);
        int clickY = (int)(_frame.ScreenRect.Top  + _frame.ScreenRect.Height / 2);
        DebugLog.Write("FocusTargetAsync: click-to-focus at ({0},{1})", clickX, clickY);
        KineticEngine.KineticObfuscator.SendMouseClick(clickX, clickY);

        await Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Poll foreground until the target is active, or hit the safety cap.
            while (sw.ElapsedMilliseconds < 140)
            {
                if (GetForegroundWindow() == _frame.TargetHWnd) break;
                Thread.Sleep(8);
            }
            // Small naturalistic jitter so the gap before the first keystroke
            // isn't a constant value (constant -> detectable signature).
            Thread.Sleep(16 + _focusJitter.Next(0, 34));
        }, _cts.Token);
    }

    private void CancelAndClose()
    {
        _cts.Cancel();
        Close();
    }

    // -- UI helpers ------------------------------------------------------------

    private void EnterInjectionMode()
    {
        InputBox.IsReadOnly         = true;
        ProgressBorder.Visibility   = Visibility.Visible;
        InjectionProgress.Value     = 0;
        FrameBorder.BorderBrush     = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)); // amber (injecting)
    }

    private void LeaveInjectionMode()
    {
        InputBox.IsReadOnly         = false;
        ProgressBorder.Visibility   = Visibility.Collapsed;
        FrameBorder.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x7C, 0x36, 0xCC)); // brand violet (idle)
    }

    private void SetStatus(string msg)
    {
        StatusLabel.Text    = msg;
        StatusPill.Visibility = Visibility.Visible;
    }

    private async void ShowStatus(string msg)
    {
        SetStatus(msg);
        await Task.Delay(1800);
        StatusPill.Visibility = Visibility.Collapsed;
    }
}
