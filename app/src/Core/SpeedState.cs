using System;
using System.IO;

namespace Atypik.Core;

/// <summary>
/// Global "base typing speed" control. A single speed factor applied to the
/// kinetic engine's inter-keystroke timings - HIGHER = FASTER:
///   1.0 = natural base pace, up to 5.0 = ~5x faster, down to 0.1 = very slow.
/// (The sampler divides its delays by this factor.)
///
/// IMPORTANT: this ONLY scales the base pace. It does NOT disable or bypass the
/// Weibull / bigram / typo modulation - that behavioural signature is the whole
/// point of the tool and always stays on. The slider just lets an atypical
/// typist pick a comfortable base rhythm around which the modulation happens.
///
/// Thread-safe and switchable from anywhere (settings slider, tray menu). The
/// <see cref="WeibullSampler"/> reads <see cref="Factor"/> at sample time, so a
/// change takes effect on the very next injection with no pipeline rebuild.
/// </summary>
public static class SpeedState
{
    public const double Min     = 0.1;   // very slow / deliberate
    public const double Max     = 5.0;   // ~5x faster than the natural base pace
    public const double Default = 1.0;   // natural base pace

    private static double _factor = Default;
    private static readonly object _lock = new();

    /// <summary>Raised on the calling thread when the factor changes.</summary>
    public static event Action<double>? Changed;

    /// <summary>Current speed factor, higher = faster (clamped to [<see cref="Min"/>, <see cref="Max"/>]).</summary>
    public static double Factor
    {
        get { lock (_lock) return _factor; }
    }

    public static void Set(double factor)
    {
        double clamped = Math.Clamp(factor, Min, Max);
        lock (_lock) _factor = clamped;
        Persist();
        Changed?.Invoke(clamped);
    }

    // -- Persistence (preference file "speed_factor") --------------------------

    private static string PrefFile => Path.Combine(AppPrefs.Dir, "speed_factor");

    /// <summary>Restore the persisted factor (call once at startup).</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(PrefFile)) return;
            if (double.TryParse(File.ReadAllText(PrefFile).Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v))
            {
                lock (_lock) _factor = Math.Clamp(v, Min, Max);
            }
        }
        catch { /* never block startup on prefs */ }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPrefs.Dir);
            File.WriteAllText(PrefFile,
                Factor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { /* never block on prefs */ }
    }
}
