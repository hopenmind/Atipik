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
    /// 0 = no budget (wait forever). Default 6000 (headroom for the 3B model on
    /// CPU; short messages are skipped entirely via ShortSkipChars).
    /// </summary>
    public int LatencyBudgetMs { get; set; } = 6000;

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

        // Skip ONLY when the WHOLE input is a single non-prose token (a bare URL,
        // path, or email). A normal sentence that merely CONTAINS a URL is still
        // prose and must be corrected - the URL itself is preserved by the
        // faithfulness guard, not by skipping the whole message.
        string t = s.Trim();
        bool singleToken = t.IndexOfAny(new[] { ' ', '\t', '\n', '\r' }) < 0;
        if (singleToken)
        {
            if (t.Contains("://") || t.StartsWith("/") || t.StartsWith("\\"))
                return true;
            if (t.Contains('@') && t.Contains('.'))
                return true;           // email
        }

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
        NativeLoader.Register();   // ensure the native bridge resolver is installed

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Model file not found.", modelPath);

        // Pick the prompt variant for the loaded model. Each mode's base prompt
        // gets a short family-specific preamble (empty for Qwen-class models), so
        // swapping models re-tunes correction, rewrite AND poetry in one place.
        // Family is read from the GGUF header first (the file may be renamed).
        var info = ModelProfile.Inspect(modelPath);
        var family = info.Family;
        Core.DebugLog.Write("Textualiser.Initialize: model={0} gguf-name=\"{1}\" arch={2} family={3}",
            Path.GetFileName(modelPath), info.Name ?? "?", info.Architecture ?? "?", family);

        string prompt = ComposePrompt("context.md", family)
            ?? throw new InvalidOperationException("Embedded system prompt (context.md) not found.");
        int result = atypik_init(modelPath, prompt, keepContext ? 1 : 0);

        if (result != 0)
            throw new InvalidOperationException(
                "Failed to load model - check the file path and format.");

        _ready = true;

        // Style prompts (tone-rewrite + poetry), embedded so they ship with the
        // exe; a src/LLM sibling is a dev-only fallback. Same family preamble.
        _rewritePrompt = ComposePrompt("rewrite_context.md", family);
        _poetryActive  = false;   // fresh model -> rewrite slot starts on the calm prompt
        if (_rewritePrompt is not null) atypik_set_rewrite_prompt(_rewritePrompt);

        _poetryPrompt = ComposePrompt("poetry_context.md", family);
    }

    // Load a base prompt and prepend the family-specific preamble (if any), so
    // one source file per mode serves every model family.
    private static string? ComposePrompt(string name, ModelFamily family)
    {
        string? body = ReadPrompt(name);
        if (body is null) return null;
        string preamble = ModelProfile.Preamble(family);
        return preamble.Length == 0 ? body : preamble + "\n\n" + body;
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

        // Correction (default): fix spelling. Long text is corrected sentence by
        // sentence so each LLM call stays small (fast, within the latency budget)
        // and the faithfulness guard runs PER segment - a bad segment falls back
        // to its own original, never dragging the whole message to passthrough.
        return ProcessorResult.Ok(CorrectLongText(input, ctx.Ct));
    }

    private const int ChunkThreshold = 180;   // chars; longer inputs are chunked

    private string CorrectLongText(string input, CancellationToken ct)
    {
        if (input.Length <= ChunkThreshold)
            return CorrectOne(input, ct);

        var sb = new StringBuilder(input.Length + 16);
        foreach ((string text, string sep) in SplitIntoChunks(input))
        {
            if (ct.IsCancellationRequested) { sb.Append(text).Append(sep); continue; }
            sb.Append(CorrectOne(text, ct)).Append(sep);
        }
        Core.DebugLog.Write("Textualiser.Correct: long text corrected in segments");
        return sb.ToString();
    }

    /// <summary>
    /// Correct one small segment. Returns the corrected text, or the segment's
    /// OWN original on timeout / unusable / unfaithful output - never corrupted.
    /// </summary>
    private string CorrectOne(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;   // whitespace-only chunk
        string? raw = RunBounded(() => CallCorrect(input), ct);
        if (raw is null) return input;
        string cleaned = SanitizeCorrection(input, raw);
        if (IsUnusable(input, cleaned) || !IsFaithfulCorrection(input, cleaned)) return input;
        return cleaned;
    }

    /// <summary>
    /// Split text into (segment, trailing-separator) pairs at sentence ends and
    /// newlines, so re-joining the corrected segments reproduces the original
    /// spacing and punctuation exactly.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<(string text, string sep)> SplitIntoChunks(string s)
    {
        int i = 0, start = 0;
        while (i < s.Length)
        {
            char c = s[i];
            bool boundary = c == '\n' || c == '\r'
                         || ((c == '.' || c == '!' || c == '?' || c == ';')
                             && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])));
            if (boundary)
            {
                int sepStart = i;
                if (c != '\n' && c != '\r') i++;                 // keep the . ! ? ; in the separator
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                yield return (s.Substring(start, sepStart - start), s.Substring(sepStart, i - sepStart));
                start = i;
            }
            else i++;
        }
        if (start < s.Length)
            yield return (s.Substring(start), string.Empty);
    }

    // -- Faithfulness guard ----------------------------------------------------
    // The corrected text is sent AS THE USER'S OWN MESSAGE. A silent rephrase,
    // dropped word, or mangled ending is worse than an uncorrected typo for a
    // user who is already judged on their writing. So we ACCEPT a correction only
    // when it is clearly a spelling-only edit of the input; otherwise we send the
    // user's original text UNTOUCHED. When in doubt, never corrupt.

    private static bool IsFaithfulCorrection(string input, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        // 1) Every tag / URL / @handle the user typed must survive verbatim.
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     input, @"</?[A-Za-z][^>\n]{0,80}>|https?://\S+|www\.\S+|[@#]\w+"))
            if (!output.Contains(m.Value, StringComparison.Ordinal))
                return false;

        // 2) Word count must stay close - a spelling fix barely changes it, a
        //    rewrite that drops or invents content does.
        int iw = CountWords(input), ow = CountWords(output);
        if (iw > 0)
        {
            double ratio = (double)ow / iw;
            if (ratio < 0.72 || ratio > 1.35) return false;
        }

        // 3) The accent/case/punctuation-stripped skeletons must stay similar -
        //    a faithful correction keeps the same words, a rephrase diverges.
        string a = Skeleton(input), b = Skeleton(output);
        if (a.Length == 0) return b.Length == 0;
        if (Math.Max(a.Length, b.Length) > 4000) return true;   // too long to diff; trust earlier gates
        int dist = Levenshtein(a, b);
        double sim = 1.0 - (double)dist / Math.Max(a.Length, b.Length);
        return sim >= 0.62;
    }

    private static int CountWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Lowercase, strip diacritics, keep only letters/digits and single spaces.</summary>
    private static string Skeleton(string s)
    {
        string norm = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        bool prevSpace = false;
        foreach (char c in norm)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue; // drop accents
            if (char.IsLetterOrDigit(c)) { sb.Append(c); prevSpace = false; }
            else if (!prevSpace) { sb.Append(' '); prevSpace = true; }
        }
        return sb.ToString().Trim();
    }

    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    /// <summary>
    /// Safety net for small instruct models: strip a leading label the model may
    /// prepend (few-shot echo like "Output:") and, when the input was a single
    /// line, drop any extra line the model appended (e.g. an echoed instruction).
    /// </summary>
    private static readonly string[] _leakLabels =
    {
        "output:", "corrected:", "corrected text:", "correction:",
        "corrigé:", "texte corrigé:", "here is the corrected text:",
        "here's the corrected text:", "here is the corrected version:",
    };

    private static string SanitizeCorrection(string input, string output)
    {
        string s   = output.Trim();
        string inL = input.TrimStart();   // user's text (for "did the user type this?")
        string inR = input.TrimEnd();

        // IMPORTANT: only strip an ENVELOPE the model added around the answer -
        // never markup, tags, fences or symbols the USER actually typed. Every
        // step below is skipped when the user's own input already carries it.
        var reOpts = System.Text.RegularExpressions.RegexOptions.IgnoreCase;

        // Drop a reasoning model's chain-of-thought block, unless the user typed one.
        if (!inL.Contains("<think", StringComparison.OrdinalIgnoreCase))
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"<think>.*?</think>", "",
                System.Text.RegularExpressions.RegexOptions.Singleline | reOpts).Trim();

        // Unwrap a tag around the whole answer (e.g. <output>...</output>), unless
        // the user's own text opens with that very same tag.
        var wrap = System.Text.RegularExpressions.Regex.Match(
            s, @"^<\s*([A-Za-z_][\w-]*)\s*>(.*?)</\s*\1\s*>$",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (wrap.Success &&
            !System.Text.RegularExpressions.Regex.IsMatch(
                inL, @"^<\s*" + System.Text.RegularExpressions.Regex.Escape(wrap.Groups[1].Value) + @"\s*>", reOpts))
        {
            s = wrap.Groups[2].Value.Trim();
        }

        // One-sided leftover wrapper tag (known noise tags), guarded by the user's
        // own text not starting with '<' / ending with '>'.
        if (!inL.StartsWith("<"))
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"^<\s*(output|corrected|corrected[_-]?text|text|response|result)\s*>\s*", "", reOpts);
        if (!inR.EndsWith(">"))
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"\s*</\s*(output|corrected|corrected[_-]?text|text|response|result)\s*>$", "", reOpts);

        // Surrounding code fences - only if the user did not type a fence.
        if (s.StartsWith("```") && !inL.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            s = nl >= 0 ? s.Substring(nl + 1) : s.Substring(3);
            if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
            s = s.Trim();
        }

        // Leading label (e.g. "Output:") - only if the user did not type it.
        foreach (var label in _leakLabels)
            if (s.StartsWith(label, StringComparison.OrdinalIgnoreCase) &&
                !inL.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(label.Length).TrimStart();
                break;
            }

        // Single-line input -> keep only the first output line (drop an appended
        // echo). Safe: the user's input had no newline of its own.
        if (input.IndexOfAny(new[] { '\n', '\r' }) < 0)
        {
            int nl = s.IndexOfAny(new[] { '\n', '\r' });
            if (nl >= 0) s = s.Substring(0, nl).TrimEnd();
        }

        return s;
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
