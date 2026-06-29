using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atypik.KineticEngine;

namespace Atypik.Core;

/// <summary>
/// Resilient modular pipeline.
///
/// Execution order (by priority):
///   0-99   -> Analysis     (language detection, typo profiling)
///   100-199 -> Correction  (LLM motor correction, spellcheck)
///   200-299 -> Transform   (general text processing)
///   300-399 -> Filter      (PII removal, content policy)
///   400-499 -> Encode      (steganographic payload embedding)
///   500-599 -> Encrypt     (encryption layer)
///   900+   -> Kinetic      (injection - always last)
///
/// A failed stage logs its error and passes the text unchanged.
/// The pipeline never aborts on module failure.
/// </summary>
public sealed class ModularPipeline : IAsyncDisposable
{
    private readonly ModuleRegistry    _registry;
    private readonly KineticObfuscator _kinetic;
    private readonly List<string>      _errorLog = [];

    public IReadOnlyList<string> ErrorLog   => _errorLog;
    public bool                  WasBlocked { get; private set; }

    /// <summary>
    /// Sentinel returned by the corrector delegate when the pipeline was blocked.
    /// OverlayWindow checks for this value and suppresses injection.
    /// </summary>
    public const string BlockedSentinel = "\x01BLOCKED\x01";

    /// <summary>
    /// Returns the first registered module of type <typeparamref name="T"/>, or null.
    /// Includes disabled modules - useful for live enable/disable (e.g. FrustrationFilter).
    /// </summary>
    public T? GetModule<T>() where T : class, ITextProcessor
        => _registry.GetFirst<T>();

    public ModularPipeline(
        ModuleRegistry registry,
        KeyboardLayout layout   = KeyboardLayout.Azerty,
        double         typoRate = 0.012,
        double         weibullK = 1.85)
    {
        _registry = registry;
        _kinetic  = new KineticObfuscator(layout, typoRate, weibullK);
    }

    /// <summary>
    /// The single configured kinetic engine (respects layout, Weibull k, typo rate).
    /// Shared with the overlay so injection honours the user's settings -
    /// the overlay must NOT spin up its own default obfuscator.
    /// </summary>
    public KineticObfuscator Engine => _kinetic;

    // -- Main entry point ------------------------------------------------------

    /// <summary>
    /// Run all registered modules then inject the result into the target window.
    /// </summary>
    public async Task ProcessAndInjectAsync(
        string            rawInput,
        IntPtr            targetHWnd,
        object?           stegPayload   = null,
        CancellationToken ct            = default)
    {
        string processed = await ProcessTextAsync(rawInput, stegPayload, ct);

        await Task.Run(() =>
        {
            _kinetic.InjectText(processed, targetHWnd);
            _kinetic.SendEnter(targetHWnd);
        }, ct);
    }

    /// <summary>
    /// Run the text-processing pipeline without injection.
    /// Useful for preview, testing, or export.
    /// </summary>
    public async Task<string> ProcessTextAsync(
        string            rawInput,
        object?           stegPayload = null,
        CancellationToken ct          = default)
    {
        WasBlocked = false;

        var metadata = new PipelineMetadata { StegPayload = stegPayload };
        var ctx      = new ProcessorContext(rawInput, rawInput, metadata, ct);

        foreach (var module in _registry.GetAll())
        {
            if (ct.IsCancellationRequested) break;

            var result = await module.ProcessAsync(ctx);

            if (!result.IsSuccess && result.Error is not null)
                _errorLog.Add($"[{module.Name}] {result.Error}");

            if (result.IsBlocked)
            {
                WasBlocked = true;
                return result.Text;   // early exit - do not inject
            }

            ctx = ctx.WithText(result.Text);
        }

        return ctx.CurrentText;
    }

    public async ValueTask DisposeAsync()
    {
        _kinetic.Dispose();

        foreach (var module in _registry.GetAll())
            if (module is IAsyncDisposable d)
                await d.DisposeAsync();
    }
}
