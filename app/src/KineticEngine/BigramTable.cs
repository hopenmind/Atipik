using System;
using System.Collections.Generic;

namespace Atypik.KineticEngine;

/// <summary>
/// Per-bigram timing scale (λ) derived from:
///   1. Bigram frequency in the target language (common = faster)
///   2. Physical key distance on the layout grid
///   3. Same-finger penalty
///   4. Hand alternation bonus
///
/// Produces a BigramProfile for every consecutive key pair.
/// </summary>
public sealed class BigramTable
{
    private readonly LayoutMatrix _layout;

    // High-frequency French bigrams -> lower base scale (faster)
    // Source: frequency lists from Lexique 3 / CELEX
    private static readonly HashSet<string> FrequentFrBigrams =
    [
        "es","de","le","en","re","on","er","nt","ou","an",
        "te","ai","se","it","et","ne","la","un","pa","co",
        "is","ra","ar","me","us","eu","ro","ma","po","ce",
        "qu","sa","nd","ur","at","si","ri","ns","in","ti"
    ];

    // High-frequency English bigrams
    private static readonly HashSet<string> FrequentEnBigrams =
    [
        "th","he","in","er","an","re","on","at","en","nd",
        "ti","es","or","te","of","ed","is","it","al","ar",
        "st","to","nt","ng","se","ha","as","ou","io","le"
    ];

    // Scale constants (ms) - Weibull λ
    private const double LambdaFast   = 55.0;   // frequent + alternate hand
    private const double LambdaMid    = 105.0;  // average
    private const double LambdaSlow   = 175.0;  // same-finger or infrequent
    private const double LambdaExtend = 220.0;  // long reach + infrequent

    public BigramTable(LayoutMatrix layout)
    {
        _layout = layout;
    }

    /// <summary>
    /// Compute timing profile for the bigram (prev -> next).
    /// </summary>
    public BigramProfile Compute(char prev, char next, bool isWordBoundary)
    {
        string bigram = $"{char.ToLower(prev)}{char.ToLower(next)}";

        bool  frequent   = FrequentFrBigrams.Contains(bigram) || FrequentEnBigrams.Contains(bigram);
        bool  sameFinger = _layout.IsSameFinger(prev, next);
        double distance  = _layout.KeyDistance(prev, next);

        double lambda = ComputeLambda(frequent, sameFinger, distance);

        // Burst detection: short distance + frequent = candidate for burst sequence
        bool isBurst = frequent && distance <= 1.5 && !sameFinger;

        return new BigramProfile
        {
            ScaleLambda    = lambda,
            IsBurst        = isBurst,
            IsWordBoundary = isWordBoundary,
            IsSameFinger   = sameFinger
        };
    }

    private static double ComputeLambda(bool frequent, bool sameFinger, double distance)
    {
        if (sameFinger)
            return LambdaSlow + distance * 18.0;  // same-finger always penalized

        if (frequent && distance <= 2.0)
            return LambdaFast + distance * 8.0;   // common alternate-hand bigrams

        if (frequent)
            return LambdaMid + distance * 6.0;    // common but distant

        if (distance > 3.5)
            return LambdaExtend;                  // long reach, infrequent

        return LambdaMid + distance * 10.0;       // average case
    }
}
