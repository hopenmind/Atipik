using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atypik.Core;

namespace Atypik.LLM;

/// <summary>
/// Textualiser - model-agnostic correction module via P/Invoke into atypik_llm.dll.
///
/// Works with any GGUF model that has a chat template embedded (most instruct models do).
/// No HTTP, no ports, no subprocess. llama.cpp runs entirely in-process.
///
/// Build the DLL:
///   cd rust/atypik-llm && cargo build --release
/// </summary>
public sealed class Textualiser : TextProcessorBase, IDisposable
{
    // -- ITextProcessor --------------------------------------------------------
    public override string              Name         => "textualiser-correction";
    public override int                 Priority     => 100;
    public override ProcessorCapability Capabilities => ProcessorCapability.Correct;
    public override bool                IsAvailable  => _ready;

    // -- P/Invoke --------------------------------------------------------------
    private const string Dll = "atypik_llm";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int atypik_init(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string systemPrompt,
        int keepContext);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int atypik_correct(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string input,
        byte[] outBuf,
        int bufLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int atypik_rewrite(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string input,
        byte[] outBuf,
        int bufLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void atypik_reset_context();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void atypik_free();

    // -- State -----------------------------------------------------------------
    private bool _ready;
    private bool _disposed;

    private const int OutputBufferBytes = 4096;

    /// <summary>
    /// Immediacy control. If non-zero, inputs at or below this length skip
    /// inference entirely (pass-through) - quick replies ("ok", "merci", a short
    /// yes/no) never wait for the model. Motor-artifact correction matters most
    /// on longer text; short messages gain nothing from a correction round-trip.
    /// 0 = always run inference. Default 0.
    /// </summary>
    public int ShortSkipChars { get; set; }

    // -- Init ------------------------------------------------------------------

    /// <summary>
    /// Load the model.
    /// <paramref name="keepContext"/> - if true, conversation history is
    /// accumulated across corrections (user-configurable, off by default).
    /// </summary>
    public void Initialize(string modelPath, string systemPromptPath, bool keepContext = false)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Model file not found.", modelPath);

        string prompt = File.ReadAllText(systemPromptPath, Encoding.UTF8);
        int    result = atypik_init(modelPath, prompt, keepContext ? 1 : 0);

        if (result != 0)
            throw new InvalidOperationException(
                "Failed to load model - check the file path and format.");

        _ready = true;
    }

    /// <summary>Async wrapper - call once at startup to avoid blocking the UI.</summary>
    public Task InitializeAsync(
        string modelPath,
        string systemPromptPath,
        bool keepContext = false,
        CancellationToken ct = default)
        => Task.Run(() => Initialize(modelPath, systemPromptPath, keepContext), ct);

    /// <summary>
    /// Clear accumulated conversation history.
    /// Only relevant when keepContext was set to true at init.
    /// </summary>
    public void ResetContext() => atypik_reset_context();

    /// <summary>
    /// Rewrite <paramref name="input"/> to remove aggressive or offensive tone.
    /// Uses the loaded model with a built-in tone-smoothing prompt - stateless.
    /// </summary>
    public Task<string> RewriteAsync(string input, CancellationToken ct = default)
        => Task.Run(() => Rewrite(input), ct);

    private string Rewrite(string input)
    {
        var outBuf  = new byte[OutputBufferBytes];
        int written = atypik_rewrite(input, outBuf, OutputBufferBytes);
        if (written <= 0) return input;
        return Encoding.UTF8.GetString(outBuf, 0, written - 1).Trim();
    }

    // -- Processing ------------------------------------------------------------

    protected override Task<ProcessorResult> ExecuteAsync(ProcessorContext ctx)
        => Task.Run(() => Correct(ctx), ctx.Ct);

    private ProcessorResult Correct(ProcessorContext ctx)
    {
        string input  = ctx.CurrentText;

        // Immediacy: skip the model for short messages - no perceptible benefit,
        // and it removes the only per-message latency in the LLM path.
        if (ShortSkipChars > 0 && input.Length <= ShortSkipChars)
            return ProcessorResult.Passthrough(input);

        var    outBuf = new byte[OutputBufferBytes];

        int written = atypik_correct(input, outBuf, OutputBufferBytes);

        if (written <= 0)
            return ProcessorResult.Fail(input, "Correction returned no output.");

        string corrected = Encoding.UTF8.GetString(outBuf, 0, written - 1).Trim();

        return string.IsNullOrEmpty(corrected)
            ? ProcessorResult.Passthrough(input)
            : ProcessorResult.Ok(corrected);
    }

    // -- Cleanup ---------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready    = false;
        atypik_free();
    }
}
