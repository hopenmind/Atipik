# Design: the Poetic mode (and a path to style modes)

> First of the "modes of functioning". This doc explains how to add a **Poetic**
> output style to A'Tipik and how to enable the local LLM to perform it. It is a
> design and how-to, not the implementation.

## Intent

A new mode that rewrites anything the author writes as **refined poetic prose**.
It keeps the exact meaning and emotional weight, but it **never reuses the
author's loaded, vulgar, or aggressive words**: it transmutes them into imagery
and tone so the reader feels the same thing without the words being spoken.

It is not softening into nothing (that is the existing tone-smooth mode). It is
transmutation into poetry. Anger stays anger; it just wears different clothes.

## Where it sits for the user

An **output style** choice, alongside the existing ones. Conceptually:

| Style | What happens to the text |
|---|---|
| Normal | Passes through (only optional spelling correction) |
| Smoothed | Existing tone filter: strips aggression/profanity, keeps meaning |
| Poetic | New: rewrites as poetic prose, conveying loaded meaning without the loaded words |

Poetic requires the Textualiser (the local LLM), exactly like the Rewrite mode.
Without the model, it is unavailable, and the app falls back to Normal.

## The core challenge, and the enabler

A small local model can smooth tone reliably, but **poetry is harder**: it needs
style, restraint, and the discipline of not echoing the source words. Two things
make it achievable on a ~1.5B model:

1. A **directive, few-shot system prompt** (`src/LLM/poetry_context.md`, already
   drafted). The few-shot examples are the single biggest quality lever for a
   small model, and they teach the "convey without naming" rule by example.
2. A **loaded-word leakage check** that rejects any output where a profanity or
   loaded term from the input survived verbatim, and falls back. This enforces
   the user's hard requirement even when the model slips.

## Pipeline integration

Add a new module, `PoetryTransform`, implementing `ITextProcessor` (see
`src/Core/ITextProcessor.cs`). It mirrors `FrustrationFilter` but with a poetic
output contract.

- **Priority**: around **55**, next to the tone filter. Poetic and Smoothed are
  mutually exclusive styles; only one runs.
- **Capability**: `ProcessorCapability.Transform`.
- **Behaviour**:
  - If the style is not Poetic, or the Textualiser is unavailable, pass through.
  - Otherwise call `Textualiser.PoeticAsync(input)` and return its result.
  - On failure / refusal / leakage, fall back deterministically (see Safety).

Suggested selector model: promote the existing `FrustrationMode` concept to a
small `OutputStyle { Normal, Smoothed, Poetic }`. The current
`FrustrationMode.Off/Keywords/Rewrite` maps to Normal/Smoothed; add `Poetic`.
Keep the Keywords rule pass available as a fallback inside Poetic.

## Enabling the local LLM (the bridge)

Today the Rust bridge (`rust/atypik-llm/src/lib.rs`) holds two persistent-KV
modes: `correct` and `rewrite`. Add a third, lazily built, exactly like
`rewrite`:

1. `Engine` gains `poetry: Option<Mode>` and `poetry_prompt: String`.
2. New export: `atypik_set_poetry_prompt(prompt)` (twin of
   `atypik_set_rewrite_prompt`), which stores the prompt and drops the cached
   mode so it rebuilds on first use.
3. New export: `atypik_poetic(input, out, len)` (twin of `atypik_rewrite`),
   which ensures the poetry mode exists and calls `run_mode`.
4. `run_mode` already takes any `&mut Mode`, so no inference change is needed:
   it reuses the persistent system-prompt KV and the rewind logic for free.
5. Token budget: poetry is longer. Give the poetic call a larger `MAX_NEW`
   (e.g. 384-512). Cleanest is a per-mode budget passed into `run_mode`, or a
   separate `run_mode_long`.

On the C# side (`src/LLM/textualiser.cs`):

- Add the `atypik_poetic` and `atypik_set_poetry_prompt` P/Invoke declarations.
- In `Initialize`, after loading `rewrite_context.md`, also load
  `poetry_context.md` (sibling file) and call `atypik_set_poetry_prompt`.
- Add `PoeticAsync(input, ct) -> Task<string?>` mirroring `RewriteAsync`: run
  bounded by the latency budget, return `null` on timeout/refusal/unusable,
  `"***"` on the explicit "nothing to convey" signal.

## Safety: the loaded-word leakage check

This is what makes the mode trustworthy. Reuse the profanity set already in
`FrustrationFilter._profanityWords` / its compiled regex:

1. Before sending to the model, collect the loaded tokens present in the input
   (regex matches).
2. After the poetic rewrite, scan the output for any of those tokens verbatim.
3. If one survived, the model echoed a word it should not have. Treat the output
   as unusable and fall back: run the deterministic Keywords pass on the input
   (strips the vulgarity, keeps meaning) and return that. Never pass a loaded
   word through in Poetic mode.

Also relax the existing `IsUnusable` validator for this mode: its
"suspicious bloat" rule (output much longer than input) would wrongly flag
legitimate poetry. Make the allowed expansion factor **mode-aware**: Normal
allows ~1.5x, Poetic allows up to ~4x (poetic prose is naturally longer). Keep
the refusal-marker and quote-wrapping checks active.

## Latency and quality notes

- Poetry generates more tokens, so it is slower than smoothing. Raise the
  latency budget for this mode (e.g. 6-8 s) or make `LatencyBudgetMs`
  mode-aware. The app still falls back gracefully on timeout.
- Decide short-skip behaviour for Poetic: either keep it (very short messages
  pass through literally) or disable it (even "ok" becomes a poetic line).
  Recommended: a separate toggle, default on, so quick replies stay instant.
- A ~1.5B model writes competent short poetry in English and French; for longer
  or subtler pieces, allow an optional larger model slot (the architecture
  already supports a bigger model for rewrite).

## UI

In `SettingsWindow`, add **Poetic** to the output-style selector. Guard it like
Rewrite: disabled unless the Textualiser is enabled. When chosen, the pipeline
runs `PoetryTransform` instead of the smooth filter. Persist the choice in a
preference (e.g. `output_style`) exactly like `frustration_mode`.

## Implementation checklist (file by file)

1. `src/LLM/poetry_context.md` - done (the directive few-shot prompt).
2. `rust/atypik-llm/src/lib.rs` - add `poetry: Option<Mode>`, `poetry_prompt`,
   `atypik_set_poetry_prompt`, `atypik_poetic`; optional per-mode token budget.
   Rebuild via `scripts/build-llm-bridge.ps1`.
3. `src/LLM/textualiser.cs` - P/Invoke the two new exports, load
   `poetry_context.md` in `Initialize`, add `PoeticAsync`; relax the bloat
   threshold via a mode parameter.
4. `src/Modules/PoetryTransform.cs` - new `ITextProcessor` (priority ~55),
   calling `PoeticAsync`, with the loaded-word leakage check + Keywords
   fallback.
5. `src/Core/AppBuilder.cs` - register the module when the style is Poetic.
6. `src/Windows/SettingsWindow.xaml(.cs)` - add Poetic to the selector, guard
   on Textualiser, persist `output_style`.
7. `src/i18n/Loc.cs` - strings for the new option.
8. Manual test: edgy input -> poetic output with no loaded word echoed; pure
   hostility -> `***` block; timeout -> Keywords fallback.

## Generalization: a style-mode framework

Poetic is the first of a family. The same pattern (a prompt file + a lazy bridge
mode + a transform module + a UI option + a leakage/validator policy) generalizes
to:

- **Formal**: rewrite in a polite, professional register.
- **Concise**: compress to the essential.
- **Empathic**: soften toward warmth and acknowledgment.
- **Playful / humour**: lighten without losing meaning.

Once two or three modes exist, refactor the per-mode duplication into a small
`StyleMode` registry: one prompt file, one bridge mode slot, one transform
wrapper, driven by a table. The bridge's `run_mode(&mut Mode, ...)` already
makes the Rust side trivial to scale to N modes.

## Risks and tradeoffs

- Small-model poetry quality varies; few-shot and the leakage check keep it
  safe but not always sublime. Acceptable for a personal tool; tune the
  examples as you collect real inputs.
- More tokens = more latency and more heat/battery on CPU-only machines. The
  GPU path (planned) and the latency budget + fallback keep it usable.
- Poetry can be misread as evasion in some contexts; the mode is opt-in and the
  user always sees the result before it is sent.
