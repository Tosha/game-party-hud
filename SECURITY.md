# Security Policy

## Supported versions

The latest release is supported. Older releases receive no security updates — if you're on an old version, please upgrade first.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.**

Instead, use GitHub's private vulnerability reporting:

1. Go to https://github.com/Tosha/game-party-hud/security/advisories/new
2. File a private advisory describing the issue.

Alternatively, email the maintainer at **zemskovsantons@gmail.com** with the subject line `[SECURITY] game-party-hud`.

You should expect an acknowledgement within 7 days. Please allow a reasonable window (30–90 days) for a fix to be prepared before any public disclosure.

## Scope

In-scope issues include, but are not limited to:

- Remote code execution via crafted signaling or party messages
- Leaks of private information (IP addresses, OS details) beyond what the design explicitly shares
- Vulnerabilities in the WebRTC data-channel handling (SIPSorcery layer)
- Supply-chain issues in the release workflow / published `.exe`

Out-of-scope:

- The inherent "signaling server sees your IP at connection time" property — this is a documented design trade-off (see `docs/superpowers/specs/`).
- Requests that the app should not reveal teammates' HP to teammates — that is the app's entire purpose.
