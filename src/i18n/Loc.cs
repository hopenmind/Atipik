using System.Collections.Generic;

namespace Atypik.i18n;

/// <summary>
/// Static localisation helper. The application ships English-only.
/// </summary>
public static class Loc
{
    public static string Language { get; private set; } = "en";

    public static void SetLanguage(string lang)
        => Language = _strings.ContainsKey(lang) ? lang : "en";

    /// <summary>Returns the localised string for <paramref name="key"/>,
    /// falling back to the raw key when missing.</summary>
    public static string T(string key)
    {
        if (_strings.TryGetValue(Language, out var d) && d.TryGetValue(key, out var v)) return v;
        return $"[{key}]";
    }

    // English string table
    private static readonly Dictionary<string, Dictionary<string, string>> _strings = new()
    {
        ["en"] = new()
        {
            // Main window
            ["app.subtitle"]             = "a behavioural normaliser",
            ["menu.file"]                = "_File",
            ["menu.file.new_frame"]      = "New frame",
            ["menu.file.quit"]           = "Quit",
            ["menu.edit"]                = "_Edit",
            ["menu.edit.settings"]       = "Settings...",
            ["menu.edit.extensions"]     = "Extensions...",
            ["menu.about"]               = "_About",
            ["menu.about.about"]         = "About A-typik...",
            ["menu.about.license"]       = "License & Terms",
            ["tutorial.title"]           = "Getting started",
            ["tutorial.step1"]           = "Click New frame and draw a rectangle around the target input area.",
            ["tutorial.step2"]           = "Type your text in the overlay; A-typik captures it locally.",
            ["tutorial.step3"]           = "Press Enter to inject with a natural behavioural signature.",
            ["tutorial.warning"]         = "Important: do not include buttons, menus or interactive controls of the target app inside the frame. They must remain directly clickable.",
            ["tutorial.dismiss.tooltip"] = "Don't show again",
            ["btn.new_frame"]            = "New",
            ["btn.new_frame.sub"]        = "frame",
            ["btn.settings"]             = "Settings",
            ["btn.settings.sub"]         = "& options",
            ["btn.extensions"]           = "Extensions",
            ["btn.extensions.sub"]       = "& scripts",
            ["btn.new_frame.tooltip"]    = "Draw a capture frame on the target application",
            ["btn.settings.tooltip"]     = "Behaviour options and model configuration",
            ["btn.extensions.tooltip"]   = "Add Python scripts, MCP modules, custom behaviours",
            ["status.model_label"]       = "Correction model",
            ["status.no_model"]          = "No model loaded",
            ["status.textualiser_off"]   = "Textualiser disabled",
            ["status.model_indicator"]   = "Model status",
            ["status.ready"]             = "Ready",
            ["status.loading_model"]     = "Loading model...",
            ["status.place_gguf"]        = "Place textualiser.gguf in:",
            ["status.load_failed"]       = "Load failed",
            ["status.no_model_err"]      = "No model",
            ["status.model_ready"]       = "Textualiser ready",
            ["status.draw_frame"]        = "Draw the frame around the target area...",
            ["status.reusing_frame"]     = "Reusing last frame...",
            ["status.cancelled"]         = "Selection cancelled.",
            ["status.frame_info"]        = "Frame: {0}x{1} px - \"{2}\"",
            ["status.hotkey_hint"]       = "Tip: Ctrl+Alt+Space captures a frame, Ctrl+Alt+C grabs the selected text",
            ["status.no_selection"]      = "No text selected - nothing to grab",

            // Settings window
            ["settings.title"]                = "Settings - A-typik",
            ["settings.save"]                 = "Save",
            ["settings.textualiser"]          = "TEXTUALISER",
            ["settings.textualiser.enable"]   = "Enable Textualiser",
            ["settings.textualiser.desc"]     = "Optional LLM-based text correction. Disabled by default. Requires a GGUF model file.",
            ["settings.shortskip"]            = "Skip correction for short messages",
            ["settings.shortskip.desc"]       = "Quick replies bypass the model and go out instantly. Motor-artifact correction matters most on longer text.",
            ["settings.latency_hint"]         = "Latency: a ~2B model corrects in roughly 0.5 to 2 s per message; larger models are slower. Short replies are instant when the option above is on.",
            ["settings.model_path"]           = "Model file (GGUF)",
            ["settings.browse"]               = "...",
            ["settings.browse.title"]         = "Select a GGUF model file",
            ["settings.browse.filter"]        = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
            ["settings.model_behavior"]       = "KEYBOARD BEHAVIOUR",
            ["settings.keyboard_layout"]      = "Keyboard layout",
            ["settings.layout_azerty"]        = "AZERTY (French)",
            ["settings.layout_qwerty"]        = "QWERTY (English)",
            ["settings.kinetic"]              = "KINETIC INJECTION",
            ["settings.typing_profile"]       = "Target typing profile",
            ["settings.weibull_exp"]          = "Experienced typist (k=1.85)",
            ["settings.weibull_mod"]          = "Moderate typing (k=1.50)",
            ["settings.weibull_slow"]         = "Slow / careful (k=1.20)",
            ["settings.typo_rate"]            = "Topographic error rate",
            ["settings.weibull_hint"]         = "Adjusts the Weibull distribution of inter-keystroke delays.",
            ["settings.tone_filter"]          = "TONE FILTER",
            ["settings.frustration_mode"]     = "Frustration mode",
            ["settings.frustration_desc"]     = "Smooths injected text before sending. Rewrite requires Textualiser.",
            ["settings.frustration_off"]      = "Disabled",
            ["settings.frustration_keywords"] = "Keywords  (rules, instant)",
            ["settings.frustration_rewrite"]  = "Rewrite  (LLM, natural)",

            // Tooltips and dialogs
            ["tooltip.needs_textualiser"]       = "Enable Textualiser to unlock this option",
            ["dialog.enable_textualiser.title"] = "Enable Textualiser?",
            ["dialog.enable_textualiser.msg"]   = "This option requires Textualiser. Enable it now?",

            // Overlay
            ["overlay.ready"]      = "Ready - Enter to inject, Shift+Enter for newline, Esc to cancel",
            ["overlay.correcting"] = "Correcting...",
            ["overlay.injecting"]  = "Injecting...",
            ["overlay.blocked"]    = "Nothing constructive to say.",
            ["overlay.done_ms"]    = "Done - {0} ms",
            ["overlay.cancel"]     = "Cancel",
            ["overlay.inject"]     = "Inject",

            // About
            ["about.title"]              = "About - A-typik",
            ["about.title.license"]      = "License & Terms - A-typik",
            ["about.version"]            = "Version 0.1.0 - Pre-release",
            ["about.section.editor"]     = "EDITOR",
            ["about.section.symbol"]     = "SYMBOL",
            ["about.section.license"]    = "LICENSE & TERMS OF USE",
            ["about.close"]              = "Close",

            // Tray
            ["tray.capture"]   = "New capture",
            ["tray.show"]      = "Show A'Tipik",
            ["tray.settings"]  = "Settings...",
            ["tray.quit"]      = "Quit",

            // Extensions
            ["ext.title"]              = "Extensions - A-typik",
            ["ext.installed"]          = "INSTALLED EXTENSIONS",
            ["ext.empty"]              = "Add Python scripts, .dll modules or MCP connectors to extend A'Tipik.",
            ["ext.add"]                = "Add script...",
            ["ext.add_module"]         = "Add module...",
            ["ext.security"]           = "Security: ",
            ["ext.security_desc"]      = "Only install scripts from sources you trust. Loaded modules run in your user space.",
            ["ext.no_extensions"]      = "No extensions installed.",
            ["ext.add_script.title"]   = "Add a script",
            ["ext.add_script.filter"]  = "Python scripts (*.py)|*.py|MCP connectors (*.json)|*.json|All files (*.*)|*.*",
            ["ext.add_module.title"]   = "Add an A'Tipik module",
            ["ext.add_module.filter"]  = "A'Tipik modules (atypik.*.dll)|atypik.*.dll|Libraries (*.dll)|*.dll",
        },
    };
}
