using System;
using System.IO;
using System.Threading;
using System.Windows;
using Atypik.Core;
using Atypik.i18n;
using Atypik.KineticEngine;
using Atypik.Modules;
using static Atypik.Modules.FrustrationMode;

namespace Atypik;

public partial class App : Application
{
    private ModularPipeline?         _pipeline;
    private FrustrationFilter?       _frustrationFilter;
    private CancellationTokenSource  _startupCts = new();
    private MainWindow?              _mainWindow;

    // -- Single-instance guard -------------------------------------------------
    // A second launch signals the running instance (via the named event) and
    // exits. The running instance brings itself to the front.
    private Mutex?                     _singleInstanceMutex;
    private EventWaitHandle?           _showSignal;
    private const string MutexName     = "Local\\Atypik-SingleInstance-3F7A9C1E";
    private const string ShowEventName = "Local\\Atypik-ShowMe-3F7A9C1E";
    private Thread?                    _signalListener;

    // -- Global hotkey + tray --------------------------------------------------
    private GlobalHotkey?   _hotkey;
    private GlobalHotkey?   _hotkeyCopy;   // Ctrl+Alt+C - grab selection
    private GlobalHotkey?   _hotkeyMode;   // Ctrl+Shift+M - cycle output mode
    private TrayController? _tray;
    private bool            _shownHotkeyHint;
    private bool            _exiting;   // true only when quitting via tray/menu

    // -- Prefs paths -----------------------------------------------------------

    private static readonly string _prefsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atypik");

    private static string FrustrationModeFile  => Path.Combine(_prefsDir, "frustration_mode");
    private static string TextualiserFlag      => Path.Combine(_prefsDir, "textualiser_enabled");
    private static string ModelPathFile        => Path.Combine(_prefsDir, "model_path");
    private static string LayoutFile           => Path.Combine(_prefsDir, "keyboard_layout");
    private static string WeibullFile          => Path.Combine(_prefsDir, "weibull_k");
    private static string TypoRateFile         => Path.Combine(_prefsDir, "typo_rate");
    private static string ShortSkipFile        => Path.Combine(_prefsDir, "short_skip_chars");

    // -- Pref loaders ---------------------------------------------------------

    private static FrustrationMode LoadFrustrationMode()
    {
        if (!File.Exists(FrustrationModeFile)) return Off;
        return File.ReadAllText(FrustrationModeFile).Trim() switch
        {
            "keywords" => Keywords,
            "rewrite"  => Rewrite,
            _          => Off,
        };
    }

    private static bool LoadTextualiserEnabled()
        => File.Exists(TextualiserFlag);

    private static KineticEngine.KeyboardLayout LoadLayout()
    {
        if (!File.Exists(LayoutFile)) return KineticEngine.KeyboardLayout.Auto;
        return File.ReadAllText(LayoutFile).Trim() switch
        {
            "qwerty"  => KineticEngine.KeyboardLayout.Qwerty,
            "azerty"  => KineticEngine.KeyboardLayout.Azerty,
            _         => KineticEngine.KeyboardLayout.Auto,
        };
    }

    private static double LoadWeibullK()
    {
        if (File.Exists(WeibullFile) &&
            double.TryParse(File.ReadAllText(WeibullFile).Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double k))
            return k;
        return 1.85;
    }

    private static double LoadTypoRatePercent()
    {
        if (File.Exists(TypoRateFile) &&
            double.TryParse(File.ReadAllText(TypoRateFile).Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double r))
            return r;
        return 1.2;
    }

    private static int LoadShortSkipChars()
    {
        // Default 16: quick replies skip the model for immediacy. File absent on
        // first run -> use the recommended default.
        if (File.Exists(ShortSkipFile) &&
            int.TryParse(File.ReadAllText(ShortSkipFile).Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out int c))
            return Math.Clamp(c, 0, 200);
        return 16;
    }

    // -- Startup ---------------------------------------------------------------

    protected override void OnStartup(StartupEventArgs e)
    {
        // -- Single-instance enforcement --------------------------------------
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);

        if (!createdNew)
        {
            // Another instance is running - wake it and bail out.
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName)
                    ?.Set();
            }
            catch { /* best effort */ }
            Shutdown();
            return;
        }

        // We are the first instance - own the show-signal and listen for pokes.
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _signalListener = new Thread(ListenForShowSignal) { IsBackground = true };
            _signalListener.Start();
        }
        catch { /* non-fatal */ }

        base.OnStartup(e);

        DebugLog.Init();
        DebugLog.Write("App startup - layout={0} weibullK={1} typoRate={2}%",
            LoadLayout(), LoadWeibullK(), LoadTypoRatePercent());

        // Restore the last output mode (Off / Correction / Poetry).
        ModeState.Set(ModeState.LoadPersisted());

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;

        // Tray icon keeps A-typik reachable while the window is hidden.
        _tray = new TrayController(_mainWindow);

        // Global hotkey: Ctrl+Alt+Space -> instant capture from anywhere.
        // Subscribe BEFORE Show() - SourceInitialized fires during Show().
        _mainWindow.SourceInitialized += (_, _) => RegisterGlobalHotkey();

        // Minimising to tray instead of the taskbar.
        _mainWindow.StateChanged += (_, _) =>
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.Hide();
        };

        // The window's X button hides to the tray; only Quit really exits.
        _mainWindow.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            _mainWindow.Hide();
        };

        _mainWindow.Show();

        _ = LoadPipelineAsync(_mainWindow);
    }

    private async System.Threading.Tasks.Task LoadPipelineAsync(MainWindow mainWindow)
    {
        try
        {
            mainWindow.SetStatus(Loc.T("status.loading_model"));

            bool   textualiserOn   = LoadTextualiserEnabled();
            var    frustrationMode = LoadFrustrationMode();
            string modelPath       = Core.AppPrefs.ResolveModelPath();
            var    layout          = LoadLayout();
            double weibullK        = LoadWeibullK();
            double typoRate        = LoadTypoRatePercent();
            int    shortSkip       = LoadShortSkipChars();

            DebugLog.Write("LoadPipeline: textualiser={0} frustration={1} layout={2} k={3} typo={4}",
                textualiserOn, frustrationMode, layout, weibullK, typoRate);
            DebugLog.Write("LoadPipeline: modelPath={0} exists={1}", modelPath, File.Exists(modelPath));

            if (!textualiserOn || !File.Exists(modelPath))
            {
                DebugLog.Write("LoadPipeline: building without LLM");
                var noLlm = await AppBuilder.Create()
                    .WithFrustrationFilter(frustrationMode)
                    .WithLayout(layout)
                    .WithWeibullProfile(weibullK)
                    .WithTypoRate(typoRate)
                    .BuildAsync(_startupCts.Token);

                _frustrationFilter = noLlm.GetModule<FrustrationFilter>();
                _pipeline = noLlm;

                if (!textualiserOn)
                {
                    mainWindow.SetModelError(Loc.T("status.textualiser_off"));
                    mainWindow.SetStatus(Loc.T("status.ready"));
                    DebugLog.Write("LoadPipeline: no-LLM pipeline ready (textualiser disabled)");
                }
                else
                {
                    mainWindow.SetModelError(Loc.T("status.no_model_err"));
                    mainWindow.SetStatus($"{Loc.T("status.place_gguf")} {modelPath}");
                    DebugLog.Write("LoadPipeline: no-LLM pipeline ready (model file missing)");
                }

                mainWindow.SetPipeline(_pipeline);
                return;
            }

            DebugLog.Write("LoadPipeline: building WITH LLM - initialising Textualiser...");
            _pipeline = await AppBuilder.Create()
                .WithFrustrationFilter(frustrationMode)
                .WithTextualiser(modelPath)
                .WithLayout(layout)
                .WithWeibullProfile(weibullK)
                .WithTypoRate(typoRate)
                .WithShortSkipChars(shortSkip)
                .BuildAsync(_startupCts.Token);

            _frustrationFilter = _pipeline.GetModule<FrustrationFilter>();

            mainWindow.SetModelReady(Loc.T("status.model_ready"));
            mainWindow.SetStatus(Loc.T("status.ready"));
            mainWindow.SetPipeline(_pipeline);
            DebugLog.Write("LoadPipeline: full pipeline ready (LLM loaded)");
        }
        catch (OperationCanceledException)
        {
            DebugLog.Write("LoadPipeline: cancelled");
        }
        catch (Exception ex)
        {
            DebugLog.Write("LoadPipeline: EXCEPTION {0}\n  {1}", ex.Message, ex.StackTrace ?? "(no stack)");
            mainWindow.SetModelError(Loc.T("status.load_failed"));
            mainWindow.SetStatus($"Error: {ex.Message}");
        }
    }

    // -- Runtime settings updates ----------------------------------------------

    /// <summary>Called from SettingsWindow when frustration mode changes.</summary>
    public void SetFrustrationFilter(FrustrationMode mode)
        => _frustrationFilter?.Configure(mode);

    /// <summary>
    /// Called from SettingsWindow when Textualiser is toggled.
    /// Rebuilds the pipeline asynchronously; UI stays responsive.
    /// </summary>
    public void SetTextualiserEnabled(bool enabled, string modelPath)
    {
        if (_mainWindow is null) return;

        // If caller passes empty/placeholder path, use the persisted saved path
        string resolvedPath = !string.IsNullOrWhiteSpace(modelPath)
            ? modelPath
            : Core.AppPrefs.ResolveModelPath();

        DebugLog.Write("SetTextualiserEnabled: enabled={0} path={1}", enabled, resolvedPath);

        _startupCts.Cancel();
        _startupCts = new CancellationTokenSource();

        _ = RebuildPipelineAsync(_mainWindow, enabled, resolvedPath);
    }

    private async System.Threading.Tasks.Task RebuildPipelineAsync(
        MainWindow mainWindow, bool textualiserOn, string modelPath)
    {
        try
        {
            mainWindow.SetStatus(Loc.T("status.loading_model"));

            var    frustrationMode = LoadFrustrationMode();
            string fullPath        = Path.GetFullPath(modelPath);
            var    layout          = LoadLayout();
            double weibullK        = LoadWeibullK();
            double typoRate        = LoadTypoRatePercent();
            int    shortSkip       = LoadShortSkipChars();

            // Dispose previous pipeline
            if (_pipeline is not null)
                await _pipeline.DisposeAsync();

            if (!textualiserOn || !File.Exists(fullPath))
            {
                var noLlm = await AppBuilder.Create()
                    .WithFrustrationFilter(frustrationMode)
                    .WithLayout(layout)
                    .WithWeibullProfile(weibullK)
                    .WithTypoRate(typoRate)
                    .BuildAsync(_startupCts.Token);

                _frustrationFilter = noLlm.GetModule<FrustrationFilter>();
                _pipeline          = noLlm;

                mainWindow.SetModelError(textualiserOn
                    ? Loc.T("status.no_model_err")
                    : Loc.T("status.textualiser_off"));
                mainWindow.SetStatus(Loc.T("status.ready"));
                mainWindow.SetPipeline(_pipeline);
                return;
            }

            _pipeline = await AppBuilder.Create()
                .WithFrustrationFilter(frustrationMode)
                .WithTextualiser(fullPath)
                .WithLayout(layout)
                .WithWeibullProfile(weibullK)
                .WithTypoRate(typoRate)
                .WithShortSkipChars(shortSkip)
                .BuildAsync(_startupCts.Token);

            _frustrationFilter = _pipeline.GetModule<FrustrationFilter>();

            mainWindow.SetModelReady(Loc.T("status.model_ready"));
            mainWindow.SetStatus(Loc.T("status.ready"));
            mainWindow.SetPipeline(_pipeline);
        }
        catch (OperationCanceledException)
        {
            DebugLog.Write("RebuildPipeline: cancelled");
        }
        catch (Exception ex)
        {
            DebugLog.Write("RebuildPipeline: EXCEPTION {0}\n  {1}", ex.Message, ex.StackTrace ?? "(no stack)");
            mainWindow.SetModelError(Loc.T("status.load_failed"));
            mainWindow.SetStatus($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Register Ctrl+Alt+Space as a global capture shortcut. Falls back silently
    /// if the OS rejects it (combination already owned by another process).
    /// </summary>
    private void RegisterGlobalHotkey()
    {
        if (_mainWindow is null) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;

            // Ctrl+Alt+Space - open capture (reuse last frame or draw).
            _hotkey = new GlobalHotkey(hwnd, id: 1);
            if (_hotkey.Register(GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x20))
            {
                _hotkey.Pressed += OnGlobalHotkey;
                DebugLog.Write("GlobalHotkey: Ctrl+Alt+Space registered");
            }
            else
            {
                DebugLog.Write("GlobalHotkey: Ctrl+Alt+Space registration refused");
            }

            // Ctrl+Alt+C - grab the foreground selection into the overlay.
            _hotkeyCopy = new GlobalHotkey(hwnd, id: 2);
            if (_hotkeyCopy.Register(GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x43))
            {
                _hotkeyCopy.Pressed += OnSelectionHotkey;
                DebugLog.Write("GlobalHotkey: Ctrl+Alt+C registered");
            }
            else
            {
                DebugLog.Write("GlobalHotkey: Ctrl+Alt+C registration refused");
            }

            // Ctrl+Shift+M - cycle output mode (Off / Correction / Poetry).
            _hotkeyMode = new GlobalHotkey(hwnd, id: 3);
            if (_hotkeyMode.Register(GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_SHIFT, 0x4D))
            {
                _hotkeyMode.Pressed += OnModeHotkey;
                DebugLog.Write("GlobalHotkey: Ctrl+Shift+M registered");
            }
            else
            {
                DebugLog.Write("GlobalHotkey: Ctrl+Shift+M registration refused");
            }

            if (!_shownHotkeyHint)
            {
                _shownHotkeyHint = true;
                _tray?.Notify("A'Tipik", Loc.T("status.hotkey_hint"));
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("GlobalHotkey: EXCEPTION {0}", ex.Message);
        }
    }

    private void OnGlobalHotkey()
    {
        DebugLog.Write("GlobalHotkey: Ctrl+Alt+Space - capture");
        _mainWindow?.TriggerCapture();
    }

    private void OnSelectionHotkey()
    {
        DebugLog.Write("GlobalHotkey: Ctrl+Alt+C - selection grab");
        _mainWindow?.CaptureSelection();
    }

    private void OnModeHotkey()
    {
        var mode = ModeState.Cycle();
        ModeState.Persist();
        // Swap the rewrite engine prompt for poetry, and back for the rest.
        _pipeline?.GetModule<LLM.Textualiser>()?.UsePoetryPrompt(mode == OutputMode.Poetry);
        Overlay.ModeToast.ShowFor(mode);
        DebugLog.Write("GlobalHotkey: Ctrl+Shift+M -> {0}", mode);
    }

    // -- Tray status -----------------------------------------------------------
    private TrayState _lastModelState = TrayState.Idle;

    public void OnModelReady()
    {
        _lastModelState = TrayState.ModelReady;
        _tray?.UpdateStatus(TrayState.ModelReady);
    }

    public void OnModelOff()
    {
        _lastModelState = TrayState.ModelOff;
        _tray?.UpdateStatus(TrayState.ModelOff);
    }

    /// <summary>Briefly flash the tray red (pipeline blocked), then revert.</summary>
    public void SignalBlocked()
    {
        _tray?.UpdateStatus(TrayState.Blocked);
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2500);
            _tray?.UpdateStatus(_lastModelState);
        }));
    }

    /// <summary>The only sanctioned exit path (tray -> Quit). Hides first for snappiness.</summary>
    public void RequestQuit()
    {
        _exiting = true;
        if (_mainWindow is not null) _mainWindow.Hide();
        Shutdown();
    }

    /// <summary>
    /// Background thread: waits for "show me" pokes from secondary launches and
    /// brings the main window to the front on the UI thread.
    /// </summary>
    private void ListenForShowSignal()
    {
        while (_showSignal is not null)
        {
            try
            {
                _showSignal.WaitOne();

                Dispatcher.Invoke(() =>
                {
                    if (_mainWindow is null) return;
                    _mainWindow.Show();
                    _mainWindow.WindowState = WindowState.Normal;
                    _mainWindow.Activate();
                });
            }
            catch
            {
                break; // handle disposed
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupCts.Cancel();
        _ = _pipeline?.DisposeAsync();

        _hotkey?.Dispose();
        _hotkeyCopy?.Dispose();
        _hotkeyMode?.Dispose();
        _tray?.Dispose();

        _showSignal?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }

        base.OnExit(e);
    }
}
