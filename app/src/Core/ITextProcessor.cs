using System;
using System.Threading;
using System.Threading.Tasks;

namespace Atypik.Core;

// -- Capability flags ----------------------------------------------------------

[Flags]
public enum ProcessorCapability
{
    None      = 0,
    Correct   = 1 << 0,   // motor artifact correction (LLM, spellcheck)
    Transform = 1 << 1,   // general text transformation
    Encode    = 1 << 2,   // steganographic encoding (timing, whitespace, unicode)
    Encrypt   = 1 << 3,   // encryption layer
    Analyze   = 1 << 4,   // read-only analysis (no text modification)
    Filter    = 1 << 5,   // content filtering / PII removal
}

// -- Processing context ---------------------------------------------------------

/// <summary>
/// Immutable context threaded through all pipeline stages.
/// Each stage may enrich the metadata without mutating prior stages' data.
/// </summary>
public sealed record ProcessorContext(
    string           RawInput,
    string           CurrentText,
    PipelineMetadata Metadata,
    CancellationToken Ct = default)
{
    public ProcessorContext WithText(string next)
        => this with { CurrentText = next };
}

public sealed class PipelineMetadata
{
    // Populated by Analyze-capable modules, read by downstream stages
    public double?  DetectedLanguageConfidence { get; set; }
    public string?  DetectedLanguage           { get; set; }
    public string?  TypoProfile                { get; set; }   // "burst", "proximity", "transposition"
    public object?  StegPayload                { get; set; }   // opaque - set by caller, consumed by Encode stage
}

// -- Result ---------------------------------------------------------------------

public readonly struct ProcessorResult
{
    public string  Text      { get; init; }
    public bool    IsSuccess { get; init; }
    public bool    IsBlocked { get; init; }   // short-circuits the pipeline
    public string? Error     { get; init; }

    public static ProcessorResult Ok(string text)
        => new() { Text = text, IsSuccess = true };

    public static ProcessorResult Fail(string passthrough, string error)
        => new() { Text = passthrough, IsSuccess = false, Error = error };

    public static ProcessorResult Passthrough(string text)
        => new() { Text = text, IsSuccess = true };

    /// <summary>
    /// Stops the pipeline - no further module runs, text is NOT injected.
    /// The pipeline returns <paramref name="reason"/> as its output so the UI
    /// can display it; OverlayWindow checks IsBlocked via pipeline.ErrorLog.
    /// </summary>
    public static ProcessorResult Block(string reason)
        => new() { Text = reason, IsSuccess = false, IsBlocked = true, Error = reason };
}

// -- Core interface -------------------------------------------------------------

/// <summary>
/// Contract for every A-typik processing module.
///
/// Resilience contract: a module MUST return ProcessorResult.Fail(passthrough)
/// rather than throw on recoverable errors. The pipeline continues regardless.
/// </summary>
public interface ITextProcessor
{
    /// <summary>Unique module identifier.</summary>
    string Name { get; }

    /// <summary>Execution order - lower = earlier in pipeline.</summary>
    int Priority { get; }

    /// <summary>Declared capabilities for introspection and filtering.</summary>
    ProcessorCapability Capabilities { get; }

    /// <summary>Whether this module is currently available (model loaded, etc.).</summary>
    bool IsAvailable { get; }

    Task<ProcessorResult> ProcessAsync(ProcessorContext ctx);
}

// -- Convenience base ----------------------------------------------------------

/// <summary>
/// Base class with built-in exception guard.
/// Override <see cref="ExecuteAsync"/> - never throws to the pipeline.
/// </summary>
public abstract class TextProcessorBase : ITextProcessor
{
    public abstract string              Name         { get; }
    public abstract int                 Priority     { get; }
    public abstract ProcessorCapability Capabilities { get; }
    public virtual  bool                IsAvailable  => true;

    public async Task<ProcessorResult> ProcessAsync(ProcessorContext ctx)
    {
        try
        {
            return await ExecuteAsync(ctx);
        }
        catch (OperationCanceledException)
        {
            return ProcessorResult.Passthrough(ctx.CurrentText);
        }
        catch (Exception ex)
        {
            return ProcessorResult.Fail(ctx.CurrentText, $"[{Name}] {ex.Message}");
        }
    }

    protected abstract Task<ProcessorResult> ExecuteAsync(ProcessorContext ctx);
}
