using System;

namespace Atypik.KineticEngine;

/// <summary>
/// Weibull distribution sampler for inter-keystroke timing (IKT).
///
/// Human typing follows a Weibull distribution, NOT a uniform distribution.
/// Reference: Bergadano et al. (2002), Killourhy & Maxion (2009)
///
/// Parameters by typist profile:
///   k ≈ 1.8-2.2  -> experienced typist (less variance, right tail compressed)
///   k ≈ 1.2-1.5  -> atypical / hunt-and-peck (more variance, longer right tail)
///   λ             -> scale, varies by bigram frequency (ms)
/// </summary>
public sealed class WeibullSampler
{
    private readonly Random _rng;
    private readonly double _shapeK;       // configurable - set by typing profile
    private readonly double _shapeKBurst;  // derived: shapeK + 0.25 (less variance in bursts)
    private readonly double _shapeKPause;  // derived: shapeK - 0.55 (more variance at boundaries)

    // Scale baselines (ms) - will be modulated by bigram table
    private const double BaseScaleFast = 60.0;   // common alternate-hand bigrams
    private const double BaseScaleMid  = 110.0;  // average digraph
    private const double BaseScaleSlow = 200.0;  // same-finger or rare bigrams

    // Hesitation injection probability
    private const double HesitationProbability = 0.06;  // 6% of keystrokes

    /// <param name="seed">Optional RNG seed for reproducible tests.</param>
    /// <param name="shapeK">Weibull shape k. 1.85 = experienced typist (default).</param>
    public WeibullSampler(int? seed = null, double shapeK = 1.85)
    {
        _rng        = seed.HasValue ? new Random(seed.Value) : new Random();
        _shapeK      = shapeK;
        _shapeKBurst = Math.Min(shapeK + 0.25, 3.0);
        _shapeKPause = Math.Max(shapeK - 0.55, 1.0);
    }

    /// <summary>
    /// Sample IKT for a given bigram.
    /// Returns delay in milliseconds.
    /// </summary>
    public int SampleDelay(BigramProfile profile)
    {
        double k      = SelectShape(profile);
        double lambda = profile.ScaleLambda;

        double raw = SampleWeibull(lambda, k);

        // Cognitive hesitation injection (word boundary, rare bigram)
        if (profile.IsWordBoundary && _rng.NextDouble() < HesitationProbability)
            raw += SampleWeibull(BaseScaleSlow * 2.5, _shapeKPause);

        // Clamp to physiologically plausible range [18ms, 1200ms]
        return (int)Math.Clamp(raw, 18.0, 1200.0);
    }

    /// <summary>
    /// Inverse CDF of Weibull distribution.
    /// X = λ * (-ln(1 - U))^(1/k),  U ~ Uniform(0,1)
    /// </summary>
    private double SampleWeibull(double lambda, double k)
    {
        double u = _rng.NextDouble();
        // Guard against u = 1 (ln(0) = -∞)
        u = Math.Min(u, 0.9999);
        return lambda * Math.Pow(-Math.Log(1.0 - u), 1.0 / k);
    }

    private double SelectShape(BigramProfile profile)
    {
        if (profile.IsBurst)        return _shapeKBurst;
        if (profile.IsWordBoundary) return _shapeKPause;
        return _shapeK;
    }

    /// <summary>
    /// Sample a backspace correction pause.
    /// Longer than normal IKT - represents realization + decision.
    /// </summary>
    public int SampleCorrectionPause()
        => (int)Math.Clamp(SampleWeibull(BaseScaleSlow, 1.4), 80, 600);

    /// <summary>
    /// Sample post-correction resumption delay.
    /// Shorter - motor system already primed.
    /// </summary>
    public int SampleResumptionDelay()
        => (int)Math.Clamp(SampleWeibull(BaseScaleMid * 0.8, _shapeK), 40, 300);
}

/// <summary>
/// Per-bigram timing profile computed by BigramTable.
/// </summary>
public readonly struct BigramProfile
{
    public double ScaleLambda   { get; init; }
    public bool   IsBurst       { get; init; }   // high-velocity sequence
    public bool   IsWordBoundary{ get; init; }   // space, punctuation follows
    public bool   IsSameFinger  { get; init; }   // slowest category
}
