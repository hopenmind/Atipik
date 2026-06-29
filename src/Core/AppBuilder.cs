using System.Threading;
using System.Threading.Tasks;
using Atypik.KineticEngine;
using Atypik.LLM;
using Atypik.Modules;
using static Atypik.Modules.FrustrationMode;

namespace Atypik.Core;

/// <summary>
/// Fluent builder - wires the full pipeline in one place.
///
/// Minimal startup:
///   var pipeline = await AppBuilder.Create()
///       .WithTextualiser(@"src\LLM\textualiser.gguf")
///       .BuildAsync();
///
/// Full:
///   var pipeline = await AppBuilder.Create()
///       .WithFrustrationFilter(enabled: true)   // Priority 50 - runs first
///       .WithTextualiser(@"src\LLM\textualiser.gguf")  // Priority 100
///       .WithLayout(KeyboardLayout.Azerty)
///       .BuildAsync();
/// </summary>
public sealed class AppBuilder
{
    private string?          _modelPath;
    private KeyboardLayout   _layout           = KeyboardLayout.Azerty;
    private FrustrationMode  _frustrationMode  = FrustrationMode.Off;
    private double           _typoRate         = 0.012;   // fraction, e.g. 0.012 = 1.2%
    private double           _weibullK         = 1.85;    // Weibull shape
    private int              _shortSkipChars;             // 0 = always correct
    private bool             _timingSteg;
    private bool             _unicodeSteg;

    public static AppBuilder Create() => new();

    public AppBuilder WithTextualiser(string modelPath)
    {
        _modelPath = modelPath;
        return this;
    }

    /// <param name="ratePercent">Typo rate as a percentage, e.g. 1.2 for 1.2%.</param>
    public AppBuilder WithTypoRate(double ratePercent)
    {
        _typoRate = Math.Clamp(ratePercent / 100.0, 0.0, 0.15);
        return this;
    }

    /// <param name="k">Weibull shape - 1.85 = experienced, 1.50 = moderate, 1.20 = slow.</param>
    public AppBuilder WithWeibullProfile(double k)
    {
        _weibullK = Math.Clamp(k, 1.0, 3.0);
        return this;
    }

    /// <summary>
    /// Registers the frustration filter at Priority 50.
    /// <list type="bullet">
    ///   <item><see cref="FrustrationMode.Off"/> - disabled (default)</item>
    ///   <item><see cref="FrustrationMode.Keywords"/> - rule-based, instant, no LLM needed</item>
    ///   <item><see cref="FrustrationMode.Rewrite"/> - LLM rewrites sentence in neutral tone</item>
    /// </list>
    /// </summary>
    public AppBuilder WithFrustrationFilter(FrustrationMode mode = Keywords)
    {
        _frustrationMode = mode;
        return this;
    }

    public AppBuilder WithLayout(KeyboardLayout layout)
    {
        _layout = layout;
        return this;
    }

    /// <summary>
    /// Messages at or below this length bypass LLM correction for instant replies.
    /// 0 = always run inference (default).
    /// </summary>
    public AppBuilder WithShortSkipChars(int chars)
    {
        _shortSkipChars = Math.Clamp(chars, 0, 200);
        return this;
    }

    /// <summary>Registers timing steganography module (no-op until implemented).</summary>
    public AppBuilder WithTimingSteg()  { _timingSteg  = true; return this; }

    /// <summary>Registers unicode steganography module (no-op until implemented).</summary>
    public AppBuilder WithUnicodeSteg() { _unicodeSteg = true; return this; }

    public async Task<ModularPipeline> BuildAsync(CancellationToken ct = default)
    {
        var registry = new ModuleRegistry();

        // LLM correction stage - built first so Rewrite mode can hold a ref to it
        Textualiser? textualiser = null;
        if (_modelPath is not null)
        {
            textualiser = new Textualiser { ShortSkipChars = _shortSkipChars };
            await textualiser.InitializeAsync(_modelPath, "src/LLM/context.md",
                keepContext: false, ct: ct);
            registry.Register(textualiser);
        }

        // Tone filter - Priority 50 (runs before the LLM corrector)
        var filter = new FrustrationFilter();
        filter.Configure(_frustrationMode, textualiser);
        registry.Register(filter);

        // Optional steg modules (registered but IsAvailable = false until implemented)
        if (_timingSteg)  registry.Register(new TimingStegModule());
        if (_unicodeSteg) registry.Register(new UnicodeStegModule());

        return new ModularPipeline(registry, _layout, _typoRate, _weibullK);
    }
}
