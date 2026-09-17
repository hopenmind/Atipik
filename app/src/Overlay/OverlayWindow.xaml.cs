using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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

    // Used to force the TARGET window to the foreground reliably (bypasses the
    // Windows foreground lock via AttachThreadInput) before injecting.
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    // Click-through: while set, the (pinned, topmost) overlay lets synthetic
    // mouse clicks pass through to the target window beneath it.
    private const int WS_EX_TRANSPARENT = 0x00000020;

    // Height (device-independent units) of the flux strip above the text frame.
    private const double FluxStripHeight = 30;

    // Default frame-background opacity (fraction). Lower = more see-through.
    private const double DefaultOverlayOpacity = 0.40;

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

        // Brand mark: load robustly so it shows in every build type (embedded
        // resource for the standalone, loose file for the default build).
        var icon = BrandIcon();
        if (icon is not null) { OptionsIcon.Source = icon; PopupIcon.Source = icon; }

        Loaded  += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CancelAndClose(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Convert screen pixels -> WPF device-independent units
        var    src = PresentationSource.FromVisual(this);
        double sx  = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy  = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // The text frame stays exactly over the drawn target; the flux strip sits
        // ABOVE it (outside the target), so the window is FluxStripHeight taller
        // and starts that much higher.
        Left   = _frame.ScreenRect.Left   / sx;
        Top    = _frame.ScreenRect.Top    / sy - FluxStripHeight;
        Width  = Math.Max(_frame.ScreenRect.Width  / sx, MinWidth);
        Height = Math.Max(_frame.ScreenRect.Height / sy, MinHeight) + FluxStripHeight;

        // Apply the user's frame-background transparency (adjustable in settings).
        ApplyOverlayOpacity(AppPrefs.GetDouble("overlay_opacity", DefaultOverlayOpacity));

        // Activate the Win32 window first, THEN set logical focus
        // (Focus() alone fails silently when the overlay isn't the foreground window)
        Activate();
        InputBox.Focus();
        Keyboard.Focus(InputBox);

        // Diagnostic: did the InputBox actually take keyboard focus? If not, typed
        // keys and Enter never reach OnInputKeyDown and injection can't trigger.
        DebugLog.Write("OnLoaded: after focus - InputBox.IsKeyboardFocused={0} IsActive={1}",
            InputBox.IsKeyboardFocused, IsActive);

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

    // Wired to the InputBox's PreviewKeyDown (tunneling), NOT KeyDown: a TextBox
    // with AcceptsReturn=True consumes the Enter KeyDown itself (to insert a
    // newline) before it reaches a bubbling KeyDown handler, so Enter never
    // triggered injection. PreviewKeyDown fires first, so we intercept Enter here.
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;   // ignore every other key (no per-keystroke logging)

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        DebugLog.Write("OnInputKeyDown: Enter (shift={0}) -> {1}", shift, shift ? "newline" : "inject");

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

    // Guard: suppress the change handlers while we sync the popup controls to the
    // current state (setting IsChecked would otherwise re-fire them).
    private bool _syncingPopup;

    private void OnOptionsClick(object sender, RoutedEventArgs e)
    {
        _syncingPopup = true;
        try
        {
            switch (Core.ModeState.Current)
            {
                case Core.OutputMode.Off:    ModeOffRadio.IsChecked        = true; break;
                case Core.OutputMode.Poetry: ModePoetryRadio.IsChecked     = true; break;
                default:                     ModeCorrectionRadio.IsChecked = true; break;
            }
            SendEnterCheck.IsChecked      = Core.AppPrefs.Get("send_enter", "1") != "0";
            PasteModeQuickCheck.IsChecked = Core.AppPrefs.GetFlag("paste_mode");
            OpacityQuickSlider.Value      = Core.AppPrefs.GetDouble("overlay_opacity", DefaultOverlayOpacity) * 100.0;
        }
        finally { _syncingPopup = false; }

        OptionsPopup.IsOpen = true;
    }

    private void OnModeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingPopup) return;
        var mode = ((sender as System.Windows.Controls.RadioButton)?.Tag as string) switch
        {
            "off"    => Core.OutputMode.Off,
            "poetry" => Core.OutputMode.Poetry,
            _        => Core.OutputMode.Correction,
        };
        (Application.Current as App)?.SetOutputMode(mode);
    }

    private void OnSendEnterToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingPopup) return;
        Core.AppPrefs.Set("send_enter", SendEnterCheck.IsChecked == true ? "1" : "0");
    }

    private void OnPasteModeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingPopup) return;
        Core.AppPrefs.SetFlag("paste_mode", PasteModeQuickCheck.IsChecked == true);
    }

    private void OnOpacityQuickChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPopup) return;
        double frac = e.NewValue / 100.0;
        ApplyOverlayOpacity(frac);
        Core.AppPrefs.Set("overlay_opacity",
            frac.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    // -- Brand icon + transparency --------------------------------------------

    private static ImageSource? _brandIcon;

    /// <summary>Load the brand mark from whichever source the current build ships
    /// it in: embedded resource (standalone), loose file (default), or disk.</summary>
    private static ImageSource? BrandIcon()
    {
        if (_brandIcon is not null) return _brandIcon;
        foreach (var uri in new[]
        {
            "pack://application:,,,/assets/logo-ico.png",
            "pack://siteoforigin:,,,/assets/logo-ico.png",
        })
        {
            try
            {
                var b = new BitmapImage(new Uri(uri));
                b.Freeze();
                _brandIcon = b;
                return b;
            }
            catch { /* try the next source */ }
        }
        try
        {
            string p = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "logo-ico.png");
            if (System.IO.File.Exists(p))
            {
                var b = new BitmapImage(new Uri(p));
                b.Freeze();
                _brandIcon = b;
                return b;
            }
        }
        catch { /* no icon available */ }
        return null;
    }

    /// <summary>Set the frame-background alpha from a 0..1 fraction (0 = fully see-through).</summary>
    private void ApplyOverlayOpacity(double frac)
    {
        frac = Math.Clamp(frac, 0.0, 1.0);
        byte a = (byte)Math.Round(frac * 255);
        FrameBorder.Background = new SolidColorBrush(Color.FromArgb(a, 0x12, 0x0A, 0x1E));
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

        // Captured before the try so the finally can ALWAYS restore the overlay's
        // window style, even if injection throws - otherwise the overlay could
        // stay click-through / non-activating and the user could no longer type.
        var selfHwnd = new WindowInteropHelper(this).Handle;
        int exStyle  = GetWindowLong(selfHwnd, GWL_EXSTYLE);

        try
        {
            EnterInjectionMode();
            DebugLog.Write("InjectAsync: start - {0} chars, hwnd=0x{1:X}", raw.Length, _frame.TargetHWnd.ToInt64());

            // -- Make the overlay non-activating AND click-through -------------
            // The overlay is pinned exactly over the target's text field. Without
            // WS_EX_TRANSPARENT the synthetic focus click below lands on the
            // overlay itself, so the target field never gets focus and nothing is
            // injected. Click-through lets the click reach the field beneath while
            // the overlay stays visually on top (pinned).
            SetWindowLong(selfHwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
            DebugLog.Write("InjectAsync: NOACTIVATE|TRANSPARENT applied to overlay hwnd=0x{0:X}", selfHwnd.ToInt64());

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

            Task<bool> focusTask = FocusTargetAsync();

            string corrected = await correctTask;
            DebugLog.Write("InjectAsync: corrector done - {0} chars", corrected.Length);
            bool focused = await focusTask;

            // Pipeline blocked - nothing constructive to inject
            if (corrected == ModularPipeline.BlockedSentinel)
            {
                DebugLog.Write("InjectAsync: pipeline blocked");
                (Application.Current as App)?.SignalBlocked();
                ShowStatus(Loc.T("overlay.blocked"));
                return;   // finally restores the style and leaves injection mode
            }

            // SAFETY: if the TARGET field is not in focus, do NOT type - keystrokes
            // would land in the wrong window and leak/scramble the message. Keep
            // the text in the overlay so the user can click the field and retry.
            if (!focused)
            {
                DebugLog.Write("InjectAsync: target NOT focused - aborting (no wrong-window typing)");
                ShowStatus("Couldn't focus the target - click it, then press Enter again");
                return;   // finally restores; InputBox keeps the text (not cleared)
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
            // Auto-validate: after injecting, press Enter in the target to send.
            // On by default; toggled from the overlay's quick-settings panel.
            bool sendEnter = Core.AppPrefs.Get("send_enter", "1") != "0";
            await Task.Run(() =>
            {
                if (paste)
                    engine.InjectByPaste(corrected, _frame.TargetHWnd, uiProgress);
                else
                    engine.InjectText(corrected, _frame.TargetHWnd, uiProgress);

                if (sendEnter)
                    engine.SendEnter(_frame.TargetHWnd);
            }, _cts.Token);

            DebugLog.Write("InjectAsync: InjectText done (sendEnter={0})", sendEnter);

            // -- Restore overlay interactivity before refocusing --------------
            SetWindowLong(selfHwnd, GWL_EXSTYLE, exStyle);

            // -- Persist - clear and refocus instead of closing ----------------
            DebugLog.Write("InjectAsync: injection complete, clearing and refocusing");
            InputBox.Clear();

            // Surface the correction latency so the user feels (and trusts) the
            // immediacy - especially the short-text skip which lands near 0 ms.
            sw.Stop();
            ShowStatus(usedCorrector
                ? string.Format(Loc.T("overlay.done_ms"), sw.ElapsedMilliseconds)
                : "✓");
            Activate();
            InputBox.Focus();
            Keyboard.Focus(InputBox);
        }
        catch (OperationCanceledException)
        {
            DebugLog.Write("InjectAsync: cancelled");
        }
        catch (Exception ex)
        {
            DebugLog.Write("InjectAsync: EXCEPTION {0}\n{1}", ex.Message, ex.StackTrace ?? "(no stack)");
            ShowStatus($"Error: {ex.Message}");
        }
        finally
        {
            // Always restore interactivity (idempotent with the success path):
            // remove click-through / non-activating so the user can type again.
            SetWindowLong(selfHwnd, GWL_EXSTYLE, exStyle);
            LeaveInjectionMode();
            _busy = false;
        }
    }

    /// <summary>
    /// Hand focus to the target's text field with a two-stage click, sent through
    /// the (now click-through) overlay so the clicks reach the field beneath:
    ///   1. raise the target window - the host software may have pushed it behind
    ///      the overlay's owner, so it is no longer foreground;
    ///   2. engage the text field itself and place the caret.
    /// A second click into an already-focused field only repositions the caret, so
    /// it is harmless on apps where a single click would have sufficed.
    /// Returns as soon as the target is foreground (capped), so the first keystroke
    /// stays as immediate as possible.
    /// </summary>
    private async Task<bool> FocusTargetAsync()
    {
        int clickX = (int)(_frame.ScreenRect.Left + _frame.ScreenRect.Width  / 2);
        int clickY = (int)(_frame.ScreenRect.Top  + _frame.ScreenRect.Height / 2);
        DebugLog.Write("FocusTargetAsync: force+engage at ({0},{1})", clickX, clickY);

        bool ok = await Task.Run(() =>
        {
            // 1) Force the TARGET window to the foreground (bypasses the Windows
            //    focus lock). This makes the click land on the RIGHT window even
            //    if another window was covering the frame's screen position.
            ForceForeground(_frame.TargetHWnd);
            WaitForForeground(220);

            // 2) Click the field to place the caret (through the click-through
            //    overlay), re-asserting foreground before the engage click.
            KineticEngine.KineticObfuscator.SendMouseClick(clickX, clickY);
            WaitForForeground(160);
            Thread.Sleep(40 + _focusJitter.Next(0, 40));
            ForceForeground(_frame.TargetHWnd);
            KineticEngine.KineticObfuscator.SendMouseClick(clickX, clickY);
            WaitForForeground(160);
            Thread.Sleep(16 + _focusJitter.Next(0, 34));

            return GetForegroundWindow() == _frame.TargetHWnd;
        }, _cts.Token);

        DebugLog.Write("FocusTargetAsync: focused={0} foreground={1} target={2}",
            ok, GetForegroundWindow().ToInt64(), _frame.TargetHWnd.ToInt64());
        return ok;
    }

    /// <summary>
    /// Force <paramref name="hwnd"/> to become the foreground window, using the
    /// AttachThreadInput trick to get around Windows' foreground lock (a process
    /// that is not already foreground is normally forbidden to steal focus).
    /// Best effort.
    /// </summary>
    private void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr fg = GetForegroundWindow();
        if (fg == hwnd) return;

        uint thisThread = GetCurrentThreadId();
        uint fgThread   = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        bool attached   = fgThread != 0 && fgThread != thisThread
                          && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }

    /// <summary>Poll the foreground window until it is the target, or the cap elapses.</summary>
    private void WaitForForeground(int capMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < capMs)
        {
            if (GetForegroundWindow() == _frame.TargetHWnd) break;
            Thread.Sleep(8);
        }
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
        StartFlux();
    }

    private void LeaveInjectionMode()
    {
        InputBox.IsReadOnly         = false;
        ProgressBorder.Visibility   = Visibility.Collapsed;
        FrameBorder.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x7C, 0x36, 0xCC)); // brand violet (idle)
        StopFlux();
    }

    // -- Flux strip animation --------------------------------------------------
    // A single luminous "comet" sweeps left->right along the strip while text is
    // being processed/injected, so the pipeline feels alive. It runs ONLY during
    // injection (not at idle) to keep the overlay low-stimulation. The travel
    // distance is read from the live strip width, so it adapts to any frame size.

    private void StartFlux()
    {
        double travel = Math.Max(FluxStrip.ActualWidth - 70, 30);
        var dur = new Duration(TimeSpan.FromMilliseconds(1300));

        // Luminous comet sweeping the pipeline.
        FluxComet.Opacity = 1;
        FluxCometShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, travel, dur) { RepeatBehavior = RepeatBehavior.Forever });

        // Wider highlight sweep behind it, for a sense of flow.
        FluxSweep.Opacity = 1;
        FluxSweepShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-70, travel + 70, dur) { RepeatBehavior = RepeatBehavior.Forever });

        // Stage nodes light up in sequence (data moving through the stages).
        var nodes = new[] { Node0, Node1, Node2, Node3 };
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1.0,
                new Duration(TimeSpan.FromMilliseconds(650)))
            {
                AutoReverse    = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime      = TimeSpan.FromMilliseconds(i * 300),
            });
        }
    }

    private void StopFlux()
    {
        FluxCometShift.BeginAnimation(TranslateTransform.XProperty, null);
        FluxComet.Opacity = 0;
        FluxSweepShift.BeginAnimation(TranslateTransform.XProperty, null);
        FluxSweep.Opacity = 0;
        foreach (var n in new[] { Node0, Node1, Node2, Node3 })
        {
            n.BeginAnimation(OpacityProperty, null);
            n.Opacity = 1;
        }
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
