using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            : System.IO.Path.GetFullPath("src/LLM/textualiser.gguf");
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

        // Apply visual state for Textualiser toggle
        UpdateTextualiserDependents(textualiserOn, animate: false);

        // Short-text skip (default on for immediacy)
        ShortSkipCheck.IsChecked = LoadShortSkipValue() > 0;

        // Fast paste mode
        PasteModeCheck.IsChecked = File.Exists(PasteModeFile);
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
