using System;
using System.IO;

namespace Atypik.Core;

/// <summary>
/// What the optional local model does to the text at send time.
/// Cycled by the Ctrl+Shift+M global shortcut.
/// </summary>
public enum OutputMode
{
    /// <summary>No model involvement. The text passes through unchanged.</summary>
    Off,

    /// <summary>Fix motor-typing artifacts (orthography). Preserves voice.</summary>
    Correction,

    /// <summary>Rewrite the text as refined poetic prose, without reusing the
    /// author's loaded words.</summary>
    Poetry,
}

/// <summary>
/// Runtime output-mode state. Thread-safe, switchable from anywhere (hotkey,
/// settings). The overlay's corrector reads <see cref="Current"/> at send time.
/// </summary>
public static class ModeState
{
    private static OutputMode _current = OutputMode.Correction;
    private static readonly object _lock = new();

    /// <summary>Raised on the calling thread when the mode changes.</summary>
    public static event Action<OutputMode>? Changed;

    public static OutputMode Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Advance to the next mode and return it: Off -> Correction -> Poetry -> Off.</summary>
    public static OutputMode Cycle()
    {
        OutputMode next;
        lock (_lock)
        {
            _current = _current switch
            {
                OutputMode.Off        => OutputMode.Correction,
                OutputMode.Correction => OutputMode.Poetry,
                _                     => OutputMode.Off,
            };
            next = _current;
        }
        Changed?.Invoke(next);
        return next;
    }

    public static void Set(OutputMode mode)
    {
        lock (_lock) _current = mode;
        Changed?.Invoke(mode);
    }

    // -- Persistence (preference file "output_mode") ---------------------------

    private static string PrefFile => Path.Combine(AppPrefs.Dir, "output_mode");

    public static OutputMode LoadPersisted()
    {
        try
        {
            if (!File.Exists(PrefFile)) return OutputMode.Correction;
            return File.ReadAllText(PrefFile).Trim() switch
            {
                "off"        => OutputMode.Off,
                "poetry"     => OutputMode.Poetry,
                "correction" => OutputMode.Correction,
                _            => OutputMode.Correction,
            };
        }
        catch { return OutputMode.Correction; }
    }

    public static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPrefs.Dir);
            File.WriteAllText(PrefFile, Current.ToString().ToLowerInvariant());
        }
        catch { /* never block on prefs */ }
    }
}
