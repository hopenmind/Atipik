using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Atypik.i18n;
using Atypik.Modules;
using Microsoft.Win32;

namespace Atypik.Windows;

public partial class SettingsWindow : Window
{
    // -- Prefs paths -----------------------------------------------------------

    private static readonly string _prefsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atypik");

    private static string TextualiserFlag  => Path.Combine(_prefsDir, "textualiser_enabled");
    private static string ModelPathFile   => Path.Combine(_prefsDir, "model_path");
    private static string FrustrationFile => Path.Combine(_prefsDir, "frustration_mode");
    private static string LayoutFile      => Path.Combine(_prefsDir, "keyboard_layout");
    private static string PasteModeFile   => Path.Combine(_prefsDir, "paste_mode");
    private static string WeibullFile     => Path.Combine(_prefsDir, "weibull_k");
    private static string TypoRateFile    => Path.Combine(_prefsDir, "typo_rate");
    private static string ShortSkipFile   => Path.Combine(_prefsDir, "short_skip_chars");

    // -- Init ------------------------------------------------------------------

    public SettingsWindow()
    {
        InitializeComponent();
        ApplyLocalization();
        LoadSettings();
        PopulateModelChoices();
    }

    // -- Downloadable-model chooser --------------------------------------------

    private bool _populatingModelChoices;

    /// <summary>
    /// Fill the "Model to download" list from the catalog (each entry shows its
    /// size) and pre-select the one matching the saved <c>model_url</c>, else the
    /// recommended default. The chosen entry's URL is what the Download button pulls.
    /// </summary>
    private void PopulateModelChoices()
    {
        _populatingModelChoices = true;
        ModelChoiceBox.Items.Clear();

        string savedUrl = Atypik.Core.ModelDownloader.GetUrl();
        int selected = 0;
        var catalog = Atypik.Core.ModelDownloader.Catalog;

        for (int i = 0; i < catalog.Length; i++)
        {
            var opt = catalog[i];
            ModelChoiceBox.Items.Add(new ComboBoxItem { Content = opt.Label, Tag = opt.Id });
            if (opt.Url == savedUrl) selected = i;
        }

        ModelChoiceBox.SelectedIndex = catalog.Length > 0 ? selected : -1;
        _populatingModelChoices = false;
        UpdateModelChoiceBlurb();
    }

    private Atypik.Core.ModelDownloader.ModelOption? SelectedModel()
        => Atypik.Core.ModelDownloader.ById(
               (ModelChoiceBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private void UpdateModelChoiceBlurb()
    {
        if (ModelChoiceBlurb is null) return;   // guard: fired before init completes
        ModelChoiceBlurb.Text = SelectedModel()?.Blurb ?? string.Empty;
    }

    private void OnModelChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingModelChoices) return;
        UpdateModelChoiceBlurb();
    }

    // -- Localization ----------------------------------------------------------

    private void ApplyLocalization()
    {
        Title = Loc.T("settings.title");

        SectionTextualiser.Text      = Loc.T("settings.textualiser");
        TextualiserEnableLabel.Text  = Loc.T("settings.textualiser.enable");
        TextualiserDesc.Text         = Loc.T("settings.textualiser.desc");
        ShortSkipLabel.Text          = Loc.T("settings.shortskip");
        ShortSkipDesc.Text           = Loc.T("settings.shortskip.desc");
        LatencyHint.Text             = Loc.T("settings.latency_hint");
        ModelPathLabel.Text          = Loc.T("settings.model_path");
        DownloadChooseLabel.Text     = Loc.T("settings.download_choose");
        DownloadModelBtn.Content     = Loc.T("settings.download_model");
        BrowseBtn.Content            = Loc.T("settings.browse");

        SectionKeyboard.Text         = Loc.T("settings.model_behavior");
        LayoutLabel.Text             = Loc.T("settings.keyboard_layout");
        LayoutAzerty.Content         = Loc.T("settings.layout_azerty");
        LayoutQwerty.Content         = Loc.T("settings.layout_qwerty");

        SectionKinetic.Text          = Loc.T("settings.kinetic");
        ProfileLabel.Text            = Loc.T("settings.typing_profile");
        ProfileExp.Content           = Loc.T("settings.weibull_exp");
        ProfileMod.Content           = Loc.T("settings.weibull_mod");
        ProfileSlow.Content          = Loc.T("settings.weibull_slow");
        WeibullHint.Text             = Loc.T("settings.weibull_hint");
        PasteModeLabel.Text          = "Fast paste mode";
        PasteModeDesc.Text           = "Paste the whole message at once (Ctrl+V) instead of typing it. Faster for long text, but it loses the human-like keystroke signature.";
        TypoRateLabel.Text           = Loc.T("settings.typo_rate");

        SectionToneFilter.Text       = Loc.T("settings.tone_filter");
        FrustrationLabel.Text        = Loc.T("settings.frustration_mode");
        FrustOff.Content             = Loc.T("settings.frustration_off");
        FrustKeywords.Content        = Loc.T("settings.frustration_keywords");
        FrustRewrite.Content         = Loc.T("settings.frustration_rewrite");
        FrustrationDesc.Text         = Loc.T("settings.frustration_desc");

        SaveBtn.Content              = Loc.T("settings.save");
    }

    // -- Load / Save -----------------------------------------------------------

    private void LoadSettings()
    {
        // Textualiser
        bool textualiserOn = File.Exists(TextualiserFlag);
        TextualiserCheck.IsChecked = textualiserOn;

        // Model path - restore saved path, fall back to default relative path
        string savedPath = File.Exists(ModelPathFile)
            ? File.ReadAllText(ModelPathFile).Trim()
            : Atypik.Core.AppPrefs.ResolveModelPath();
        ModelPathBox.Text = savedPath;

        // Frustration mode
        string fMode = File.Exists(FrustrationFile)
            ? File.ReadAllText(FrustrationFile).Trim()
            : "off";
        SelectComboByTag(FrustrationCombo, fMode);

        // Keyboard layout
        string layout = File.Exists(LayoutFile)
            ? File.ReadAllText(LayoutFile).Trim()
            : "auto";
        SelectComboByTag(LayoutCombo, layout);

        // Weibull profile
        string profile = File.Exists(WeibullFile)
            ? File.ReadAllText(WeibullFile).Trim()
            : "1.85";
        SelectComboByTag(ProfileCombo, profile);

        // Typo rate
        if (File.Exists(TypoRateFile) &&
            double.TryParse(File.ReadAllText(TypoRateFile).Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double savedRate))
        {
            TypoRateSlider.Value = savedRate;
            TypoRateValue.Text   = $"{savedRate:F1} %";
        }

        // Overlay frame-background opacity (fraction stored, shown as a percent)
        OverlayOpacitySlider.Value = Atypik.Core.AppPrefs.GetDouble("overlay_opacity", 0.40) * 100.0;

        // Apply visual state for Textualiser toggle
        UpdateTextualiserDependents(textualiserOn, animate: false);

        // Short-text skip (default on for immediacy)
        ShortSkipCheck.IsChecked = LoadShortSkipValue() > 0;

        // Fast paste mode
        PasteModeCheck.IsChecked = File.Exists(PasteModeFile);

        // Typing speed (0.1 - 5.0, higher = faster). Applies live via SpeedState.
        _syncingSpeed = true;
        double speed = Atypik.Core.SpeedState.Factor;
        SpeedSlider.Value = speed;
        SpeedBox.Text     = speed.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
        _syncingSpeed = false;
    }

    // -- Typing-speed slider + numeric box (two-way, applied live) -------------

    private bool _syncingSpeed;

    private void OnSpeedSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSpeed || SpeedBox is null) return;
        _syncingSpeed = true;
        SpeedBox.Text = e.NewValue.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
        _syncingSpeed = false;
        Atypik.Core.SpeedState.Set(e.NewValue);
    }

    private void OnSpeedBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { CommitSpeedBox(); e.Handled = true; }
    }

    private void OnSpeedBoxCommit(object sender, RoutedEventArgs e) => CommitSpeedBox();

    private void CommitSpeedBox()
    {
        if (_syncingSpeed) return;
        if (double.TryParse(SpeedBox.Text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            v = Math.Clamp(v, Atypik.Core.SpeedState.Min, Atypik.Core.SpeedState.Max);
            _syncingSpeed = true;
            SpeedSlider.Value = v;
            SpeedBox.Text = v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
            _syncingSpeed = false;
            Atypik.Core.SpeedState.Set(v);
        }
        else
        {
            // Not a number - restore the current value.
            _syncingSpeed = true;
            SpeedBox.Text = Atypik.Core.SpeedState.Factor.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
            _syncingSpeed = false;
        }
    }

    private static int LoadShortSkipValue()
    {
        if (File.Exists(ShortSkipFile) &&
            int.TryParse(File.ReadAllText(ShortSkipFile).Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out int c))
            return Math.Clamp(c, 0, 200);
        return 16;   // recommended default
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_prefsDir);

        // Textualiser
        bool textualiserOn = TextualiserCheck.IsChecked == true;
        if (textualiserOn) File.WriteAllText(TextualiserFlag, "1");
        else if (File.Exists(TextualiserFlag)) File.Delete(TextualiserFlag);

        // Model path - persist so RebuildPipeline can find it
        if (!string.IsNullOrWhiteSpace(ModelPathBox.Text))
            File.WriteAllText(ModelPathFile, ModelPathBox.Text.Trim());

        // Frustration mode
        string fTag = GetComboTag(FrustrationCombo) ?? "off";
        File.WriteAllText(FrustrationFile, fTag);

        // Keyboard layout
        string layoutTag = GetComboTag(LayoutCombo) ?? "azerty";
        File.WriteAllText(LayoutFile, layoutTag);

        // Weibull profile
        string profileTag = GetComboTag(ProfileCombo) ?? "1.85";
        File.WriteAllText(WeibullFile, profileTag);

        // Typo rate (stored as %, e.g. "1.2")
        string typoRateStr = TypoRateSlider.Value.ToString("F1",
            System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(TypoRateFile, typoRateStr);

        // Short-text skip (16 = on, 0 = off)
        File.WriteAllText(ShortSkipFile, ShortSkipCheck.IsChecked == true ? "16" : "0");

        // Fast paste mode (flag file)
        if (PasteModeCheck.IsChecked == true) File.WriteAllText(PasteModeFile, "1");
        else if (File.Exists(PasteModeFile)) File.Delete(PasteModeFile);

        // Propagate to running app
        if (Application.Current is App app)
        {
            var fMode = fTag switch
            {
                "keywords" => FrustrationMode.Keywords,
                "rewrite"  => FrustrationMode.Rewrite,
                _          => FrustrationMode.Off,
            };
            app.SetFrustrationFilter(fMode);
            app.SetTextualiserEnabled(textualiserOn, ModelPathBox.Text);
        }

        Close();
    }

    // -- Textualiser toggle ----------------------------------------------------

    private void OnTextualiserToggled(object sender, RoutedEventArgs e)
        => UpdateTextualiserDependents(TextualiserCheck.IsChecked == true, animate: true);

    private void UpdateTextualiserDependents(bool on, bool animate)
    {
        double opacity = on ? 1.0 : 0.4;

        // Model path row
        ModelPathLabel.Opacity = opacity;
        ModelPathRow.Opacity   = opacity;
        ModelPathBox.IsEnabled = on;
        BrowseBtn.IsEnabled    = on;

        // Rewrite frustration option
        FrustRewrite.Opacity = opacity;
        FrustRewrite.ToolTip = on ? null : Loc.T("tooltip.needs_textualiser");

        // Short-skip + latency guidance only matter when the LLM is on
        ShortSkipRow.Opacity = opacity;
        ShortSkipCheck.IsEnabled = on;
        LatencyHint.Opacity = opacity;

        // If Textualiser just disabled and Rewrite was selected -> revert to Off
        if (!on && GetComboTag(FrustrationCombo) == "rewrite")
            FrustrationCombo.SelectedItem = FrustOff;
    }

    // -- Frustration selection guard -------------------------------------------

    private bool _suppressFrustrationGuard;

    private void OnFrustrationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFrustrationGuard) return;
        if (GetComboTag(FrustrationCombo) != "rewrite") return;
        if (TextualiserCheck.IsChecked == true) return;

        // User clicked Rewrite without Textualiser -> prompt
        _suppressFrustrationGuard = true;

        var result = MessageBox.Show(
            Loc.T("dialog.enable_textualiser.msg"),
            Loc.T("dialog.enable_textualiser.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            TextualiserCheck.IsChecked = true;
            // Selection stays on Rewrite
        }
        else
        {
            FrustrationCombo.SelectedItem = FrustOff;
        }

        _suppressFrustrationGuard = false;
    }

    // -- Other handlers --------------------------------------------------------

    private void OnBrowseModel(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.T("settings.browse.title"),
            Filter = Loc.T("settings.browse.filter"),
        };
        if (dlg.ShowDialog() == true)
            ModelPathBox.Text = dlg.FileName;
    }

    private bool _downloading;

    private void OnOverlayOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayOpacityValue is null) return;   // guard: fired before init completes
        OverlayOpacityValue.Text = $"{e.NewValue:F0} %";
        Atypik.Core.AppPrefs.Set("overlay_opacity",
            (e.NewValue / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    // Choice returned by AskDownloadTarget.
    private enum DownloadTarget { Default, Choose, Cancel }

    /// <summary>
    /// Small themed prompt: download to the app-managed folder, or pick a location.
    /// </summary>
    private DownloadTarget AskDownloadTarget()
    {
        var win = new Window
        {
            Title                 = "Download model",
            Width                 = 430,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            WindowStyle           = WindowStyle.SingleBorderWindow,
            Background            = ResBrush("BrBgDeep", Colors.Black),
        };

        var result = DownloadTarget.Cancel;
        var panel  = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text       = "Where should the model be downloaded?",
            Foreground = ResBrush("BrTextPrimary", Colors.White),
            FontSize   = 13, FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text         = "The default location is managed by the app. Choosing a location lets you keep the model file where you want it.",
            Foreground   = ResBrush("BrTextMuted", Colors.Gray),
            FontSize     = 11, TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button Btn(string text)
        {
            var b = new Button
            {
                Content    = text,
                Margin     = new Thickness(6, 0, 0, 0),
                Padding    = new Thickness(12, 6, 12, 6),
                Cursor     = Cursors.Hand,
                Background  = ResBrush("BrBgCard", Colors.DimGray),
                Foreground  = ResBrush("BrTextPrimary", Colors.White),
                BorderBrush = ResBrush("BrBorderSubtle", Colors.Gray),
                BorderThickness = new Thickness(1),
            };
            return b;
        }
        var bDefault = Btn("Download");
        var bChoose  = Btn("Choose location...");
        var bCancel  = Btn("Cancel");
        bDefault.Click += (_, _) => { result = DownloadTarget.Default; win.Close(); };
        bChoose.Click  += (_, _) => { result = DownloadTarget.Choose;  win.Close(); };
        bCancel.Click  += (_, _) => { result = DownloadTarget.Cancel;  win.Close(); };
        row.Children.Add(bDefault);
        row.Children.Add(bChoose);
        row.Children.Add(bCancel);
        panel.Children.Add(row);

        win.Content = panel;
        win.ShowDialog();
        return result;
    }

    private SolidColorBrush ResBrush(string key, Color fallback)
        => TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(fallback);

    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;

        var choice = SelectedModel() ?? Atypik.Core.ModelDownloader.DefaultOption;

        // Ask where to put it: the app-managed default, or a location the user picks.
        DownloadTarget target = AskDownloadTarget();
        if (target == DownloadTarget.Cancel) return;

        string dest;
        if (target == DownloadTarget.Choose)
        {
            var save = new SaveFileDialog
            {
                Title    = "Choose where to save the model",
                Filter   = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
                FileName = "textualiser.gguf",
            };
            if (save.ShowDialog() != true) return;
            dest = save.FileName;
        }
        else
        {
            dest = Atypik.Core.AppPrefs.ModelFile;   // app-managed default location
        }

        _downloading = true;
        DownloadModelBtn.IsEnabled = false;

        // Remember the picked model so it stays selected and GetUrl() reflects it.
        Atypik.Core.AppPrefs.Set("model_url", choice.Url);

        DownloadStatus.Text = $"Downloading {choice.SizeText}...";
        var progress = new Progress<int?>(p =>
            DownloadStatus.Text = p is null ? "Downloading..." : $"Downloading {p}%");

        try
        {
            await System.Threading.Tasks.Task.Run(() =>
                Core.ModelDownloader.DownloadAsync(choice.Url, dest, (IProgress<int?>)progress).Wait());

            // Auto-load: point the app at the fresh model, enable the Textualiser,
            // show the path in the box above, and (re)build the pipeline so it loads
            // right away.
            ModelPathBox.Text = dest;
            TextualiserCheck.IsChecked = true;
            UpdateTextualiserDependents(true, animate: true);
            (Application.Current as Atypik.App)?.SetTextualiserEnabled(true, dest);
            DownloadStatus.Text = "Downloaded - loading model...";
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = "Failed: " + ex.Message;
        }
        finally
        {
            DownloadModelBtn.IsEnabled = true;
            _downloading = false;
        }
    }

    private void OnTypoRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TypoRateValue is null) return;  // guard: fired before InitializeComponent completes
        TypoRateValue.Text = $"{e.NewValue:F1} %";
    }

    // -- Helpers ---------------------------------------------------------------

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string?)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string? GetComboTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
}
