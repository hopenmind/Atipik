# Security Policy

## Reporting a vulnerability

A'Tipik runs entirely on the user's machine. Still, if you find a security or
privacy issue (for example, unexpected network access, a crash that leaks
buffer contents, or unsafe handling of input), please report it privately.

- Email: contact@hopenmind.com
- Preferred subject: `[A'Tipik] Security report`

Do **not** open a public issue for security reports. We will acknowledge
receipt within 5 business days and aim to publish a fix and advisory within
30 days for confirmed issues.

## Scope

This policy covers the latest release of A'Tipik on Windows 10/11, including:
the capture overlay, the kinetic injection engine, the optional local LLM
bridge (`atypik_llm.dll`), and the preferences stored under
`%APPDATA%\Atypik`.

## Privacy guarantees we commit to

- No network requests from the application itself.
- No telemetry or usage statistics collected.
- Optional LLM inference is local only. Input text never leaves the machine.

If you can demonstrate a violation of any of the above, it qualifies as a
reportable issue.

## Responsible disclosure

We appreciate responsible disclosure and will credit reporters in release
notes unless they prefer to remain anonymous.
