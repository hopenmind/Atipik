using System;
using System.Threading.Tasks;
using Atypik.Core;

namespace Atypik.Modules;

/// <summary>
/// Timing steganography module - CONCEPT / PLACEHOLDER.
///
/// Encodes a binary payload in the inter-keystroke timing pattern
/// by selecting between two Weibull parameter sets per bit:
///   bit 0 -> λ drawn from distribution A (slightly faster mean)
///   bit 1 -> λ drawn from distribution B (slightly slower mean)
///
/// The difference is sub-perceptual (~8ms mean shift) but statistically
/// recoverable by a receiver who knows the encoding key.
///
/// NOT YET WIRED to KineticObfuscator - requires a shared-state timing
/// override interface. Registered but reports IsAvailable = false
/// until the interface is implemented.
///
/// Placed at Priority 400 - runs after correction and filtering,
/// before kinetic injection so the timing hints can propagate.
/// </summary>
public sealed class TimingStegModule : TextProcessorBase
{
    public override string              Name         => "timing-steg";
    public override int                 Priority     => 400;
    public override ProcessorCapability Capabilities => ProcessorCapability.Encode;
    public override bool                IsAvailable  => false;  // flip when ready

    // Bit-encoding: mean IKT shift per bit value (ms)
    // Delta must be large enough to survive Weibull variance but below
    // perceptual detection threshold (~15ms for average observer)
    private const double DeltaMs = 8.0;

    protected override Task<ProcessorResult> ExecuteAsync(ProcessorContext ctx)
    {
        // Text is NOT modified - payload is encoded in timing hints
        // attached to the context metadata for the kinetic stage to read.
        //
        // Implementation sketch:
        //   var bits = ExtractBits(ctx.Metadata.StegPayload);
        //   ctx.Metadata.TimingHints = BuildTimingHints(ctx.CurrentText, bits);
        //
        // KineticObfuscator reads TimingHints and applies λ offsets per character.

        return Task.FromResult(ProcessorResult.Passthrough(ctx.CurrentText));
    }
}

/// <summary>
/// Unicode whitespace steganography - CONCEPT / PLACEHOLDER.
///
/// Encodes payload in invisible Unicode variation selectors or
/// zero-width characters inserted between words.
/// Survives copy-paste but stripped by most sanitizers.
///
/// Priority 410 - runs alongside timing steg (independent channels).
/// </summary>
public sealed class UnicodeStegModule : TextProcessorBase
{
    public override string              Name         => "unicode-steg";
    public override int                 Priority     => 410;
    public override ProcessorCapability Capabilities => ProcessorCapability.Encode;
    public override bool                IsAvailable  => false;

    // Invisible characters available for encoding
    // U+200B ZERO WIDTH SPACE
    // U+200C ZERO WIDTH NON-JOINER
    // U+2060 WORD JOINER
    // U+FEFF ZERO WIDTH NO-BREAK SPACE

    protected override Task<ProcessorResult> ExecuteAsync(ProcessorContext ctx)
    {
        // Will encode ctx.Metadata.StegPayload as bits
        // inserted as invisible chars between words.
        return Task.FromResult(ProcessorResult.Passthrough(ctx.CurrentText));
    }
}
