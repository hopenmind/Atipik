# Contributing to A'Tipik

Thank you for considering a contribution. A'Tipik is a small, focused tool and
we want to keep it that way: lean, private, and Windows-native.

## Before you start

- Read the [README](README.md) to understand what the tool is and is not.
- A'Tipik is Windows 10/11 only (WPF + Win32 `SendInput`). Cross-platform
  proposals are out of scope.
- Keep the privacy guarantees: no network calls, no telemetry, local-only LLM.

## Getting set up

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.
Rust is only needed if you change the optional LLM bridge.

```pwsh
git clone https://github.com/<your-org>/A-typik.git
cd A-typik
dotnet build
```

The app builds and runs without the Rust DLL and without any model file. Both
are optional. If you only touch C# or XAML, you do not need Rust or a GGUF.

## What we welcome

- Bug fixes and correctness improvements to the kinetic engine or overlay.
- Accessibility and i18n improvements (new languages welcome, see `src/i18n`).
- Documentation fixes.
- Performance work that reduces per-message latency without changing the
  naturalistic timing model.

## What to avoid

- Adding network calls, accounts, or any kind of telemetry.
- Features that depend on a remote/cloud LLM.
- Large refactors without a prior discussion in an issue.

## Submitting a pull request

1. Open an issue first for anything beyond a small fix, so we can agree on the
   approach.
2. Keep PRs focused: one logical change per PR.
3. Make sure the project builds with `dotnet build` and has zero warnings.
4. Do not add emojis or decorative symbols to user-visible strings unless the
   change explicitly calls for them.
5. Write a clear PR description referencing the issue (see the PR template).

## Code style

- Match the style of the files around your change.
- Comments are fine where they explain *why*; avoid restating *what* the code
  does.

By contributing, you agree that your contributions are licensed under the
project's [dual license](LICENSE).
