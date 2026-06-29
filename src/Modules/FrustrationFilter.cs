using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Atypik.Core;
using Atypik.LLM;

namespace Atypik.Modules;

/// <summary>
/// Frustration mode for the filter.
/// </summary>
public enum FrustrationMode
{
    /// <summary>Filter disabled - text passes through unchanged.</summary>
    Off,

    /// <summary>
    /// Rule-based: removes known profanity/insults via regex word list.
    /// Instant, no LLM required. Best for predictable filtering.
    /// </summary>
    Keywords,

    /// <summary>
    /// LLM-based: asks the model to rewrite the sentence with a neutral tone.
    /// Language-agnostic - works on any language the model knows.
    /// Requires the Textualiser to be loaded. Slower but more natural output.
    /// </summary>
    Rewrite,
}

/// <summary>
/// Frustration filter - tones down operator text before injection.
///
/// Priority 50 -> runs before the LLM corrector (Priority 100) so it
/// receives clean, de-aggressed text.
/// </summary>
public sealed class FrustrationFilter : TextProcessorBase
{
    public override string              Name         => "frustration-filter";
    public override int                 Priority     => 50;
    public override ProcessorCapability Capabilities => ProcessorCapability.Filter;

    public override bool IsAvailable => _mode switch
    {
        FrustrationMode.Keywords => true,
        FrustrationMode.Rewrite  => _textualiser?.IsAvailable == true,
        _                        => false,
    };

    private FrustrationMode _mode        = FrustrationMode.Off;
    private Textualiser?    _textualiser;

    /// <summary>Configure the active mode (and optionally bind a Textualiser for Rewrite).</summary>
    public void Configure(FrustrationMode mode, Textualiser? textualiser = null)
    {
        _mode        = mode;
        _textualiser = textualiser ?? _textualiser;
    }

    // Convenience - kept for backwards compat with App.xaml.cs
    public void SetEnabled(bool value)
        => _mode = value ? FrustrationMode.Keywords : FrustrationMode.Off;

    // -- Pipeline entry --------------------------------------------------------

    protected override Task<ProcessorResult> ExecuteAsync(ProcessorContext ctx)
        => _mode switch
        {
            FrustrationMode.Keywords => Task.FromResult(ApplyKeywords(ctx.CurrentText)),
            FrustrationMode.Rewrite  => RewriteAsync(ctx),
            _                        => Task.FromResult(ProcessorResult.Passthrough(ctx.CurrentText)),
        };

    // -- Rewrite mode (LLM) ----------------------------------------------------

    private async Task<ProcessorResult> RewriteAsync(ProcessorContext ctx)
    {
        if (_textualiser is null || !_textualiser.IsAvailable)
            return ProcessorResult.Passthrough(ctx.CurrentText);

        // null = the model timed out or refused. Fall back to the deterministic
        // Keywords pass so mode 2 NEVER injects unfiltered vulgarity, even when
        // the model misbehaves.
        string? rewritten = await _textualiser.RewriteAsync(ctx.CurrentText, ctx.Ct);

        if (rewritten == "***")
            return ProcessorResult.Block("Nothing constructive to say.");

        if (rewritten is null)
            return ApplyKeywords(ctx.CurrentText);

        return ProcessorResult.Ok(rewritten);
    }

    // -- Keywords mode (rule-based) --------------------------------------------

    private static ProcessorResult ApplyKeywords(string input)
    {
        string s = input;

        s = RemoveProfanity(s);
        s = NormalizePunctuation(s);
        s = NormalizeCaps(s);
        s = CleanWhitespace(s);

        return string.IsNullOrWhiteSpace(s)
            ? ProcessorResult.Passthrough(input)
            : ProcessorResult.Ok(s);
    }

    // -- Step 1: profanity / insults removal -----------------------------------

    /// <summary>
    /// Matches whole words only (regex \b boundaries).
    /// Case-insensitive. Accented variants included.
    /// </summary>
    private static readonly HashSet<string> _profanityWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // -- French --
        "merde", "putain", "pute", "connard", "connasse", "salope", "salopard",
        "enculé", "enculée", "encule", "enculer",
        "bite", "bites", "couilles", "branleur", "branleuse",
        "bordel", "saloperie", "chieur", "chieuse",
        "nique", "niquer", "va te faire foutre", "foutre",
        "fils de pute", "va chier", "casse-toi", "casse toi",
        "fdp", "ntm", "sti", "ostie", "câlice", "tabarnak", "criss",
        "batard", "bâtard", "baltringue",

        // -- English --
        "fuck", "fucking", "fucked", "fucker", "fucks",
        "shit", "shitty", "shitting", "bullshit",
        "ass", "asshole", "asshat", "jackass",
        "bitch", "bitching", "bitchy",
        "bastard", "bastards",
        "crap", "crappy",
        "damn", "dammit", "goddamn", "goddammit",
        "piss", "pissed",
        "prick", "dick", "cock",
        "twat", "cunt",
        "motherfucker", "mf",
        "wtf", "wth", "ffs", "fml", "omfg", "stfu", "gtfo",
        "kys", "idiot", "moron", "retard", "imbecile",
        "jerk", "dumbass", "dumb ass", "dipshit",

        // -- Frustration interjections --
        "argh", "arghhh", "arrgh", "aargh", "aarrgh",
        "grr", "grrr", "grrrr",
        "ugh", "ughhh", "urgh",
        "dammit", "bloody hell",
        "nom de dieu", "sacrebleu", "sacré bleu",
        "putain de merde",
    };

    // Build the pattern once at class load - word-boundary on each term
    private static readonly Regex _profanityRegex = BuildProfanityRegex();

    private static Regex BuildProfanityRegex()
    {
        // Sort by length descending so multi-word phrases match before single words
        var sorted = _profanityWords
            .OrderByDescending(w => w.Length)
            .Select(w => Regex.Escape(w));

        string pattern = $@"\b(?:{string.Join("|", sorted)})\b";
        return new Regex(pattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static string RemoveProfanity(string s)
        => _profanityRegex.Replace(s, string.Empty);

    // -- Step 2: punctuation normalization ------------------------------------

    // !!! -> !   ??? -> ?   !? sequences -> ?!   ............. -> …
    private static readonly Regex _multiExcl  = new(@"!{2,}",   RegexOptions.Compiled);
    private static readonly Regex _multiQuery = new(@"\?{2,}",  RegexOptions.Compiled);
    private static readonly Regex _mixedExcl  = new(@"[!?]{3,}",RegexOptions.Compiled);
    private static readonly Regex _multiDot   = new(@"\.{4,}",  RegexOptions.Compiled);

    private static string NormalizePunctuation(string s)
    {
        s = _mixedExcl.Replace(s,  "?!");   // !?!?!? -> ?!
        s = _multiExcl.Replace(s,  "!");    // !!! -> !
        s = _multiQuery.Replace(s, "?");    // ??? -> ?
        s = _multiDot.Replace(s,   "…");    // ..... -> …
        return s;
    }

    // -- Step 3: SHOUTING normalization ----------------------------------------

    // Words of 3+ chars that are ALL CAPS -> convert to Title case
    private static readonly Regex _capsWord = new(@"\b[A-ZÀÂÄÉÈÊËÎÏÔÙÛÜÇ]{3,}\b",
        RegexOptions.Compiled);

    private static string NormalizeCaps(string s)
        => _capsWord.Replace(s, m => ToTitleCase(m.Value));

    private static string ToTitleCase(string word)
        => word.Length == 0
            ? word
            : char.ToUpper(word[0]) + word.Substring(1).ToLower();

    // -- Step 4: whitespace cleanup --------------------------------------------

    // Multiple spaces -> single space; leading/trailing punctuation artifacts
    private static readonly Regex _multiSpace = new(@"\s{2,}", RegexOptions.Compiled);
    // Orphaned punctuation at start of sentence: ", connard, ça ne..." -> "ça ne..."
    private static readonly Regex _leadingPunct = new(@"^\s*[,;:]+\s*", RegexOptions.Compiled);

    private static string CleanWhitespace(string s)
    {
        s = _multiSpace.Replace(s, " ");
        s = _leadingPunct.Replace(s, string.Empty);
        return s.Trim();
    }
}
