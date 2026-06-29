using System;
using System.Threading.Tasks;
using Atypik.KineticEngine;

namespace Atypik.Core;

/// <summary>
/// A-typik main pipeline:
///   captured text -> LLM correction -> kinetic injection
///
/// Runs async to keep the overlay UI responsive during LLM inference.
/// </summary>
public sealed class Pipeline : IAsyncDisposable
{
    private readonly KineticObfuscator _kinetic;
    private readonly ILlmClient        _llm;

    public Pipeline(ILlmClient llm, KeyboardLayout layout = KeyboardLayout.Azerty)
    {
        _kinetic = new KineticObfuscator(layout);
        _llm     = llm;
    }

    /// <summary>
    /// Full pipeline: raw input -> cleaned text -> kinetic injection.
    /// </summary>
    public async Task ProcessAsync(string rawInput, IntPtr targetHWnd)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return;

        // Step 1: LLM kinetic correction (local inference)
        string cleaned = await _llm.CorrectAsync(rawInput);

        // Step 2: Kinetic injection with Weibull timing
        // Run on a dedicated STA thread - SendInput requires message pump context
        await Task.Run(() =>
        {
            _kinetic.InjectText(cleaned, targetHWnd);
            _kinetic.SendEnter(targetHWnd);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _kinetic.Dispose();
        if (_llm is IAsyncDisposable d)
            await d.DisposeAsync();
    }
}

/// <summary>
/// Contract for the local LLM correction backend.
/// Implemented by GemmaClient (llama.cpp via P/Invoke or named pipe).
/// </summary>
public interface ILlmClient
{
    Task<string> CorrectAsync(string rawText);
}
