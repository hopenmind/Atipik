using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Atypik.i18n;
using Atypik.Overlay;
using Atypik.Windows;

namespace Atypik;

public partial class MainWindow : Window
{
    // Prefs stored in %APPDATA%\Atypik\
    private static readonly string _prefsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atypik");
    private static string TutorialFlag => Path.Combine(_prefsDir, "tutorial_dismissed");

    // Last confirmed bounding box selection
    private FrameResult?          _activeFrame;
    private Core.ModularPipeline? _pipeline;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalization();
        ApplyTutorialPreference();
    }

    // -- Localization ----------------------------------------------------------

    public void ApplyLocalization()
    {
        // Menu
        MenuFile.Header       = Loc.T("menu.file");
        MenuNewFrame.Header   = Loc.T("menu.file.new_frame");
        MenuQuit.Header       = Loc.T("menu.file.quit");
        MenuEdit.Header       = Loc.T("menu.edit");
        MenuSettings.Header   = Loc.T("menu.edit.settings");
        MenuExtensions.Header = Loc.T("menu.edit.extensions");
        MenuAbout.Header      = Loc.T("menu.about");
        MenuAboutApp.Header   = Loc.T("menu.about.about");
        MenuLicense.Header    = Loc.T("menu.about.license");

        // Subtitle
        AppSubtitle.Text = Loc.T("app.subtitle");

        // Tutorial
        TutorialTitle.Text   = Loc.T("tutorial.title");
        TutorialWarning.Text = Loc.T("tutorial.warning");

        // Buttons
        BtnNewFrameLabel.Text   = Loc.T("btn.new_frame");
        BtnNewFrameSub.Text     = Loc.T("btn.new_frame.sub");
        BtnNewFrame.ToolTip     = Loc.T("btn.new_frame.tooltip");
        BtnSettingsLabel.Text   = Loc.T("btn.settings");
        BtnSettingsSub.Text     = Loc.T("btn.settings.sub");
        BtnSettings.ToolTip     = Loc.T("btn.settings.tooltip");
        BtnExtensionsLabel.Text = Loc.T("btn.extensions");
        BtnExtensionsSub.Text   = Loc.T("btn.extensions.sub");
        BtnExtensions.ToolTip   = Loc.T("btn.extensions.tooltip");

        // Model status
        ModelSectionLabel.Text  = Loc.T("status.model_label");
        ModelStatusText.Text    = Loc.T("status.no_model");
        ModelIndicator.ToolTip  = Loc.T("status.model_indicator");

        // Status bar
        StatusText.Text = Loc.T("status.ready");
    }

    // -- Tutorial --------------------------------------------------------------

    private void ApplyTutorialPreference()
    {
        bool dismissed = File.Exists(TutorialFlag);
        TutorialCard.Visibility = dismissed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDismissTutorial(object sender, RoutedEventArgs e)
    {
        TutorialCard.Visibility = Visibility.Collapsed;
        Directory.CreateDirectory(_prefsDir);
        File.WriteAllText(TutorialFlag, "1");
    }

    // -- Main actions ----------------------------------------------------------

    private void OnNewFrame(object sender, RoutedEventArgs e)
        => StartCapture(forceRedraw: true);

    /// <summary>
    /// Public entry point for the global hotkey and tray menu.
    /// Reuses the last valid capture frame if one exists (instant), otherwise
    /// asks the user to draw a new frame.
    /// </summary>
    public void TriggerCapture()
    {
        // If an overlay or the bounding-box tool is already open, don't stack.
        if (Application.Current.Windows.OfType<OverlayWindow>().Any()) return;

        StartCapture(forceRedraw: false);
    }

    /// <summary>
    /// Begin a capture. When <paramref name="forceRedraw"/> is false and a valid
    /// saved frame exists, the overlay opens directly on it - zero redraw latency.
    /// </summary>
    private void StartCapture(bool forceRedraw)
    {
        if (!forceRedraw)
        {
            var recalled = Core.AppPrefs.LoadLastFrameIfValid();
            if (recalled is not null)
            {
                SetStatus(Loc.T("status.reusing_frame"));
                Hide();
                OpenOverlay(recalled);
                return;
            }
        }

        SetStatus(Loc.T("status.draw_frame"));
        Hide();

        var tool = new BoundingBoxTool();
        tool.FrameSelected += OnFrameSelected;
        tool.Show();
    }

    private void OnFrameSelected(FrameResult? result)
    {
        if (result is null)
        {
            Show();
            Activate();
            SetStatus(Loc.T("status.cancelled"));
            return;
        }

        // Persist for instant recall next time.
        Core.AppPrefs.SaveLastFrame(result);

        OpenOverlay(result);
    }

    private void OpenOverlay(FrameResult result, string? initialText = null)
    {
        _activeFrame = result;

        int w = (int)result.ScreenRect.Width;
        int h = (int)result.ScreenRect.Height;
        SetStatus(string.Format(Loc.T("status.frame_info"), w, h, result.TargetTitle));

        // Build corrector delegate from pipeline if available
        Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<string>>? corrector = null;
        if (_pipeline is not null)
        {
            corrector = async (text, ct) =>
            {
                string processed = await _pipeline.ProcessTextAsync(text, ct: ct);
                return _pipeline.WasBlocked
                    ? Core.ModularPipeline.BlockedSentinel
                    : processed;
            };
        }

        var overlay = new OverlayWindow(result, corrector, _pipeline?.Engine, initialText);
        overlay.Closed += (_, _) =>
        {
            Show();
            Activate();
        };
        overlay.Show();
    }

    // -- Selection grab (Ctrl+Alt+C) -------------------------------------------

    /// <summary>
    /// Grab the current foreground selection, auto-create a frame at the cursor,
    /// and open the overlay pre-filled with the selected text (Enter to inject).
    /// Runs on the UI thread because clipboard access requires STA.
    /// </summary>
    public void CaptureSelection()
    {
        if (Application.Current.Windows.OfType<OverlayWindow>().Any()) return;

        bool grabbed = Core.SelectionCapture.TryGrab(out var frame, out string text);

        if (frame is null)
        {
            SetStatus(Loc.T("status.no_selection"));
            return;
        }

        Hide();
        OpenOverlay(frame, grabbed ? text : null);

        if (!grabbed)
            SetStatus(Loc.T("status.no_selection"));
    }

    private void OnSettings(object sender, RoutedEventArgs e)
        => OpenSettingsFromTray();

    /// <summary>Open the settings dialog (shared by menu and tray).</summary>
    public void OpenSettingsFromTray()
    {
        Show();
        Activate();
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
    }

    private void OnExtensions(object sender, RoutedEventArgs e)
    {
        var win = new ExtensionsWindow { Owner = this };
        win.ShowDialog();
    }

    // -- Menu ------------------------------------------------------------------

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow { Owner = this };
        win.ShowDialog();
    }

    private void OnLicense(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow(showLicense: true) { Owner = this };
        win.ShowDialog();
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.RequestQuit();
        else
            Application.Current.Shutdown();
    }

    // -- Pipeline --------------------------------------------------------------

    public void SetPipeline(Core.ModularPipeline pipeline)
        => _pipeline = pipeline;

    // -- Status helpers --------------------------------------------------------

    public void SetStatus(string msg)
        => StatusText.Text = msg;

    public void SetModelReady(string modelName)
    {
        ModelStatusText.Text    = modelName;
        ModelIndicator.Fill     = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xAA));
        ModelIndicator.ToolTip  = Loc.T("status.model_ready");
        (Application.Current as App)?.OnModelReady();
    }

    public void SetModelError(string error)
    {
        ModelStatusText.Text    = error;
        ModelIndicator.Fill     = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35));
        ModelIndicator.ToolTip  = Loc.T("status.load_failed");
        (Application.Current as App)?.OnModelOff();
    }
}
