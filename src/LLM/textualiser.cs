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
/// No HTTP, no ports, no subprocess. The model runs entirely in-process.
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
    private static extern int atypik_set_rewrite_prompt(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt);

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

    /// <summary>
    /// Per-call latency budget in ms. If the model does not answer in time, the
    /// call is abandoned and a deterministic fallback is used instead. The app
    /// never makes the user wait indefinitely for an optional model.
    /// 0 = no budget (wait forever). Default 3000.
    /// </summary>
    public int LatencyBudgetMs { get; set; } = 3000;

    // Style prompts for the rewrite engine (mode 2). Swapped at runtime when the
    // user cycles to Poetry and back. Null = not loaded / not available.
    private string? _rewritePrompt;
    private string? _poetryPrompt;
    private bool _poetryActive;

    /// <summary>
    /// When true (default), inputs that are clearly non-prose (URLs, paths,
    /// emails, code, or symbol/number-only snippets) skip the model entirely.
    /// Dictionary-free: it never tries to detect typos, so it never needs a
    /// per-language word list - it only recognises things that should NOT be
    /// "corrected".
    /// </summary>
    public bool CleanSkip { get; set; } = true;

    private static bool LooksNonProse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;

        bool hasLetter = false;
        foreach (var ch in s)
            if (char.IsLetter(ch)) { hasLetter = true; break; }
        if (!hasLetter) return true;   // emoji / punctuation / numbers only

        // URLs, paths, emails, code: never "correct" these.
        if (s.Contains("://") || s.StartsWith("/") || s.StartsWith("\\"))
            return true;
        if (s.Contains('@') && s.Contains('.') && !s.Contains(' '))
            return true;               // email

        return false;
    }

    // Signatures that mean the model refused, preached, or added commentary
    // instead of transforming. Small instruct models (even abliterated ones)
    // occasionally do this; the app must not pass that noise through.
    private static readonly string[] _refusalMarkers =
    {
        "i can't", "i cannot", "i can not", "i'm sorry", "i am sorry",
        "i apologize", "as an ai", "as a language model", "i'm unable",
        "i am unable", "i won't", "i will not", "i must decline",
        "i must refuse", "i'm not able", "i'm afraid", "as an assistant",
        "i don't feel comfortable", "cannot assist", "can't assist",
        "however, i", "i should point out", "please note that i",
        "i'd like to point out", "just so you know", "as a responsible"
    };

    /// <summary>
    /// True when the model output looks like a refusal / lecture / commentary /
    /// framing rather than a faithful transform. Mode 1 then keeps the user's
    /// original text verbatim (the safe default); mode 2 falls back to the
    /// deterministic Keywords pass.
    /// </summary>
    private static bool IsUnusable(string input, string output, double expansion = 1.5)
    {
        if (string.IsNullOrWhiteSpace(output)) return true;

        string low = output.ToLowerInvariant();
        foreach (var m in _refusalMarkers)
            if (low.Contains(m)) return true;

        var t = output.Trim();
        // Wrapped entirely in quotes / guillemets -> the model added framing.
        if (t.Length >= 2 &&
            ((t[0] == '"' && t[^1] == '"') || (t[0] == '\u00AB' && t[^1] == '\u00BB')))
            return true;

        // Suspicious bloat: output much longer than allowed -> added commentary.
        // Poetry legitimately expands more, so the caller raises the factor.
        if (output.Length > input.Length * expansion + 40) return true;
        // Suspicious collapse: output far shorter than input -> dropped content.
        if (!string.IsNullOrEmpty(input) && output.Length < input.Length * 0.3) return true;

        return false;
    }

    /// <summary>
    /// Run a native call with a latency budget. Returns null on timeout,
    /// cancellation, or native fault - the caller then uses its deterministic
    /// fallback. (The abandoned native call finishes in the background.)
    /// </summary>
    private string? RunBounded(Func<string?> fn, CancellationToken ct)
    {
        try
        {
            var task = Task.Run(fn, ct);
            return LatencyBudgetMs > 0
                ? task.WaitAsync(TimeSpan.FromMilliseconds(LatencyBudgetMs), ct).GetAwaiter().GetResult()
                : task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Core.DebugLog.Write("Textualiser.RunBounded: fallback ({0})", ex.GetType().Name);
            return null;
        }
    }

    private string? CallCorrect(string input)
    {
        var buf = new byte[OutputBufferBytes];
        int n = atypik_correct(input, buf, OutputBufferBytes);
        return n > 0 ? Encoding.UTF8.GetString(buf, 0, n - 1).Trim() : null;
    }

    private string? CallRewrite(string input)
    {
        var buf = new byte[OutputBufferBytes];
        int n = atypik_rewrite(input, buf, OutputBufferBytes);
        return n > 0 ? Encoding.UTF8.GetString(buf, 0, n - 1).Trim() : null;
    }

    // -- Init ------------------------------------------------------------------

    /// <summary>
    /// Load the model.
    /// <paramref name="keepContext"/> - if true, conversation history is
    /// accumulated across corrections (user-configurable, off by default).
    /// </summary>
    public void Initialize(string modelPath, bool keepContext = false)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Model file not found.", modelPath);

        string prompt = ReadPrompt("context.md")
            ?? throw new InvalidOperationException("Embedded system prompt (context.md) not found.");
        int result = atypik_init(modelPath, prompt, keepContext ? 1 : 0);

        if (result != 0)
            throw new InvalidOperationException(
                "Failed to load model - check the file path and format.");

        _ready = true;

        // Style prompts (tone-rewrite + poetry), embedded so they ship with the
        // exe; a src/LLM sibling is a dev-only fallback.
        _rewritePrompt = ReadPrompt("rewrite_context.md");
        if (_rewritePrompt is not null) atypik_set_rewrite_prompt(_rewritePrompt);

        _poetryPrompt = ReadPrompt("poetry_context.md");
    }

    // Read a prompt from the embedded resources first (so a single-file or
    // installed build carries it), then a src/LLM sibling for dev runs.
    private static string? ReadPrompt(string name)
    {
        using (var s = typeof(Textualiser).Assembly.GetManifestResourceStream(name))
            if (s is not null)
            {
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd();
            }
        string dev = Path.GetFullPath(Path.Combine("src", "LLM", name));
        return File.Exists(dev) ? File.ReadAllText(dev, Encoding.UTF8) : null;
    }

    /// <summary>
    /// Swap the rewrite engine's system prompt between tone-smoothing and
    /// poetry. Triggers a lazy rebuild of the rewrite KV on the next call.
    /// No-op if the requested prompt is not loaded.
    /// </summary>
    public void UsePoetryPrompt(bool poetry)
    {
        if (poetry == _poetryActive) return;
        string? prompt = poetry ? _poetryPrompt : _rewritePrompt;
        if (poetry && prompt is null) return;   // poetry prompt not available
        if (prompt is not null) atypik_set_rewrite_prompt(prompt);
        _poetryActive = poetry;
    }

    /// <summary>Whether the poetry prompt is available (file was found at init).</summary>
    public bool HasPoetryPrompt => _poetryPrompt is not null;

    /// <summary>Async wrapper - call once at startup to avoid blocking the UI.</summary>
    public Task InitializeAsync(
        string modelPath,
        bool keepContext = false,
        CancellationToken ct = default)
        => Task.Run(() => Initialize(modelPath, keepContext), ct);

    /// <summary>
    /// Clear accumulated conversation history.
    /// Only relevant when keepContext was set to true at init.
    /// </summary>
    public void ResetContext() => atypik_reset_context();

    /// <summary>
    /// Rewrite <paramref name="input"/> to remove aggressive or offensive tone.
    /// Returns null on timeout, refusal, or any unusable output so the caller
    /// (frustration filter) can fall back to the deterministic Keywords pass.
    /// Returns "***" when the model signals "nothing constructive to say".
    /// </summary>
    public Task<string?> RewriteAsync(string input, CancellationToken ct = default, double expansion = 1.5)
        => Task.Run(() => Rewrite(input, ct, expansion), ct);

    private string? Rewrite(string input, CancellationToken ct, double expansion)
    {
        string? raw = RunBounded(() => CallRewrite(input), ct);
        if (raw is null) return null;

        string t = raw.Trim();
        if (t == "***" || string.IsNullOrWhiteSpace(t)) return "***";   // explicit block
        if (IsUnusable(input, t, expansion)) return null;                // refusal -> fallback
        return t;
    }

    // -- Processing ------------------------------------------------------------

    protected override Task<ProcessorResult> ExecuteAsync(ProcessorContext ctx)
        => Task.Run(() => Correct(ctx), ctx.Ct);

    private ProcessorResult Correct(ProcessorContext ctx)
    {
        string input = ctx.CurrentText;
        var    mode  = Core.ModeState.Current;

        // Off: the optional model does nothing at send time.
        if (mode == Core.OutputMode.Off)
            return ProcessorResult.Passthrough(input);

        // Immediacy: short and non-prose inputs skip the model in every mode.
        if (ShortSkipChars > 0 && input.Length <= ShortSkipChars)
            return ProcessorResult.Passthrough(input);
        if (CleanSkip && LooksNonProse(input))
            return ProcessorResult.Passthrough(input);

        // Poetry: rewrite as poetic prose. The poetry prompt is already active on
        // the rewrite path (UsePoetryPrompt was applied when the mode was chosen).
        // Poetic prose legitimately expands, so allow a wider factor; on refusal,
        // timeout or unusable output, fall back to the user's own text.
        if (mode == Core.OutputMode.Poetry)
        {
            string? poetic = _poetryActive ? RunBounded(() => CallRewrite(input), ctx.Ct) : null;
            if (poetic is null) return ProcessorResult.Passthrough(input);
            string p = poetic.Trim();
            if (p == "***" || string.IsNullOrWhiteSpace(p) || IsUnusable(input, p, expansion: 4.0))
                return ProcessorResult.Passthrough(input);
            return ProcessorResult.Ok(p);
        }

        // Correction (default): fix motor-typing artifacts, keep the voice.
        string? raw = RunBounded(() => CallCorrect(input), ctx.Ct);
        if (raw is null || IsUnusable(input, raw))
        {
            Core.DebugLog.Write("Textualiser.Correct: model unusable, faithful passthrough");
            return ProcessorResult.Passthrough(input);
        }
        return ProcessorResult.Ok(raw);
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
