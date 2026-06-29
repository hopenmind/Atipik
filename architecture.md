# A-typik - Architecture

Accessibility tool for atypical typists whose natural input patterns are
incorrectly flagged by behavioral telemetry systems (Cloudflare Turnstile,
keystroke dynamics analyzers, Ajax intent trackers).

---

## Problem

Behavioral telemetry is calibrated on Gaussian distributions of neurotypical
typing patterns. Atypical typists fall outside this distribution by construction:

| Profile       | Pattern                          | Flag reason               |
|---------------|----------------------------------|---------------------------|
| Autistic      | burst + long pause + burst       | "bot rate-limiting"       |
| ADHD          | hyperfocus acceleration + stop   | "non-human velocity"      |
| HPI           | thought > motor -> transpositions | "high error rate"         |
| Dyspraxic     | spatial coordination slips       | "random input pattern"    |
| Vision issues | line-loss, duplicate chars        | "repeated anomaly"        |

A-typik normalizes the output signature - not the person.

---

## Data Flow

```
User types in overlay (captured locally)
          |
          v
   Raw text buffer
          |
          v
  Gemma-2B (local llama.cpp)
  Kinetic reconstruction filter
  -> corrects motor artifacts only
  -> preserves voice, style, intent
          |
          v
  KineticObfuscator
  +-- WeibullSampler    -> IKT per keystroke (Weibull k≈1.85)
  +-- BigramTable       -> λ per bigram (frequency + distance)
  +-- LayoutMatrix      -> 2D adjacency (AZERTY/QWERTY)
  +-- Typo injector     -> 1.2% topographical errors + correction
          |
          v
  SendInput -> target window
```

---

## Timing Model

Human inter-keystroke timing (IKT) follows a **Weibull distribution**, not uniform.

```
X = lambda * (-ln(1-U))^(1/k)    where U ~ Uniform(0,1)
```

| Parameter | Value         | Meaning                         |
|-----------|---------------|---------------------------------|
| k         | 1.85          | shape - experienced typist      |
| k_burst   | 2.10          | tighter distribution in bursts  |
| k_pause   | 1.30          | longer tail at word boundaries  |
| λ_fast    | 55 ms         | frequent alternate-hand bigrams |
| λ_mid     | 105 ms        | average bigram                  |
| λ_slow    | 175 ms        | same-finger bigrams             |
| λ_extend  | 220 ms        | long reach + infrequent         |

λ is modulated per bigram by:
1. Frequency in target language (FR/EN)
2. Physical key distance on 2D grid
3. Same-finger penalty
4. Word-boundary hesitation injection (6% probability)

---

## Error Injection

Topographical errors use an 8-directional neighbor model on the physical grid -
not a linear string. A typo on 'e' (AZERTY) yields 'z','r','s','d' - not 'f'.

Error rate: **1.2%** of letter keystrokes (empirical average typist baseline).

Correction sequence timing:
- Wrong key -> Weibull pause (λ=180ms, realization delay)
- Backspace
- Weibull correction pause (λ=175ms, slower tail k=1.4)
- Correct key
- Weibull resumption delay (λ=84ms, motor already primed)

---

## Modules

```
src/
+-- Core/
|   +-- Pipeline.cs         main async pipeline
|   +-- ILlmClient.cs       LLM backend contract
+-- KineticEngine/
|   +-- WeibullSampler.cs   Weibull IKT distribution
|   +-- BigramTable.cs      per-bigram λ computation
|   +-- LayoutMatrix.cs     2D keyboard layout + adjacency
|   +-- KineticObfuscator.cs  SendInput orchestration
+-- LLM/
|   +-- GemmaClient.cs      llama.cpp bridge (TODO)
|   +-- gemma_context.md    system prompt
+-- Overlay/
    +-- OverlayWindow.xaml   transparent always-on-top capture (TODO)
    +-- BoundingBoxTool.cs  frame drawing tool (TODO)
```

---

## Distribution Strategy

Available exclusively via the A-typik author's site on autism research.
Context-as-gating: the surrounding content (thesis on autism, vision disorders,
late diagnosis) naturally filters for the intended audience.
No token, no login, no paywall - just a context wall.
