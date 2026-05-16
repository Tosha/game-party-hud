# Discord notification on new release

**Date:** 2026-05-16
**Scope:** `.github/workflows/release.yml` only. No app code, no new tests, no doc changes beyond the workflow comments.

## Goals

When the release workflow publishes a new GitHub Release (triggered by a `v*` tag push), post a Discord embed to the same `GPH_DISCORD_WEBHOOK_URL` we already use for party-creation notifications. The embed has a clickable title (release name → release URL), the auto-generated release notes as the description, and a sidebar color indicating stable vs prerelease.

## Non-goals

- No client-side release notification (each running app polling GitHub would be noisy and is already rejected in the party-notification spec).
- No marketplace action dependency. Native `pwsh` + `gh` only.
- No retry / queue. One POST; if Discord eats it, `continue-on-error: true` means we don't fail the release.
- No `@here` / `@everyone` ping in the message — already blocked via `allowed_mentions`.
- No custom rendering / reformatting of the release notes. We trust the auto-generated content from `.github/release.yml` (PR #76's label-based grouping).

## Design

### 1. Trigger & ordering

Single new step at the end of `release.yml`, placed **after** `Create GitHub Release`. Properties:

- Runs only if the prior step (`softprops/action-gh-release@v2`) succeeded — default GitHub Actions behavior. If the release didn't get created, we don't lie about it.
- Marked `continue-on-error: true` so a Discord 4xx / network blip can't fail the release after the `.exe` is already published.
- Internally short-circuits with `exit 0` and a log line ("skipping — Discord webhook not configured") if `GPH_DISCORD_WEBHOOK_URL` is empty, so forks without the secret get a normal release. The check lives in the pwsh script rather than a YAML `if:` because step-level `env:` is not exposed to step-level `if:` expressions in GitHub Actions, and secrets cannot be read directly in `if:` conditions.

### 2. Fetching the release info

Use the `gh` CLI (already installed and authenticated on `windows-latest` via the auto-injected `GITHUB_TOKEN`):

```pwsh
$release = gh release view $env:TAG_NAME --json name,body,url,isPrerelease |
           ConvertFrom-Json
```

Four fields drive the embed:

- `release.name` → embed title (typically the tag itself with `softprops/action-gh-release` defaults, e.g. `v0.6.0`)
- `release.url` → embed URL (clickable title)
- `release.body` → embed description (auto-generated release notes from PR #76's grouping config)
- `release.isPrerelease` → switches accent color + adds a footer flag

`gh release view` is used rather than the action's outputs because `softprops/action-gh-release@v2` only exposes `url` / `id` / `upload_url`, not `body`.

### 3. Building the payload

```pwsh
# Discord embed description has a 4096-char limit. Truncate with a
# "see full release" footer link so long release notes never break the
# request — typical auto-generated notes fit comfortably, but a
# squash-merge of 30+ PRs could exceed it.
$desc = $release.body
if ($desc.Length -gt 3900) {
    $desc = $desc.Substring(0, 3900) + "`n`n…notes truncated, [see full release]($($release.url))"
}

# Green for stable, amber for prerelease. Decimal Discord color values.
$color  = if ($release.isPrerelease) { 16753920 } else { 3066993 }
$footer = if ($release.isPrerelease) { "Prerelease" } else { "Release" }

$payload = @{
    embeds = @(
        @{
            title       = $release.name
            url         = $release.url
            description = $desc
            color       = $color
            footer      = @{ text = $footer }
        }
    )
    # Defensive: release notes mentioning @here / @everyone would ping the
    # channel otherwise. Same posture as DiscordNotifier.cs.
    allowed_mentions = @{ parse = @() }
}
$body = $payload | ConvertTo-Json -Depth 6 -Compress
```

Key choices:

- **Truncate at 3900 chars** (well under Discord's 4096 limit) with a clickable "see full release" fallback link.
- **Color** — green `0x2ECC71` (decimal `3066993`) for stable, amber `0xFFA500` (decimal `16753920`) for prerelease.
- **Footer text** — just "Release" / "Prerelease" so prereleases are visibly distinct without forcing readers to parse the URL.
- **`allowed_mentions: { parse: [] }`** — same belt-and-braces stance as `src/GamePartyHud/Network/DiscordNotifier.cs`.
- **`-Compress`** on `ConvertTo-Json` to keep the request body small.

### 4. Posting the request

```pwsh
try {
    Invoke-RestMethod -Uri $env:GPH_DISCORD_WEBHOOK_URL `
                      -Method Post `
                      -ContentType 'application/json' `
                      -Body $body | Out-Null
    Write-Host "Discord release notification sent for $($release.name)."
}
catch {
    # continue-on-error on the step means this Write-Error won't fail the
    # whole job, but the message + response body land in the workflow log
    # for diagnosis.
    Write-Error "Discord release notification failed: $($_.Exception.Message)"
    if ($_.ErrorDetails) { Write-Error $_.ErrorDetails.Message }
}
```

- **`Invoke-RestMethod`** rather than `curl`: native pwsh, throws on non-2xx so we can catch cleanly. The `| Out-Null` discards Discord's empty `204 No Content` response.
- **Token from env**: webhook URL passed via the step's `env:` block, never inlined into the command, so it doesn't appear in the workflow log even if a future edit echoes the command.
- **`try` / `catch`** is the in-step belt; **`continue-on-error: true`** on the step is the suspenders. Together, any Discord-side failure logs cleanly without failing the release.

### 5. Final step shape

```yaml
- name: Announce release on Discord
  continue-on-error: true
  shell: pwsh
  env:
    GPH_DISCORD_WEBHOOK_URL: ${{ secrets.GPH_DISCORD_WEBHOOK_URL }}
    GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
    TAG_NAME: ${{ github.ref_name }}
  run: |
    if ([string]::IsNullOrWhiteSpace($env:GPH_DISCORD_WEBHOOK_URL)) {
        Write-Host "GPH_DISCORD_WEBHOOK_URL not configured; skipping Discord announcement."
        exit 0
    }

    $release = gh release view $env:TAG_NAME --json name,body,url,isPrerelease |
               ConvertFrom-Json

    $desc = $release.body
    if ($desc.Length -gt 3900) {
        $desc = $desc.Substring(0, 3900) + "`n`n…notes truncated, [see full release]($($release.url))"
    }

    $color  = if ($release.isPrerelease) { 16753920 } else { 3066993 }
    $footer = if ($release.isPrerelease) { "Prerelease" } else { "Release" }

    $payload = @{
        embeds = @(
            @{
                title       = $release.name
                url         = $release.url
                description = $desc
                color       = $color
                footer      = @{ text = $footer }
            }
        )
        allowed_mentions = @{ parse = @() }
    }
    $body = $payload | ConvertTo-Json -Depth 6 -Compress

    try {
        Invoke-RestMethod -Uri $env:GPH_DISCORD_WEBHOOK_URL `
                          -Method Post `
                          -ContentType 'application/json' `
                          -Body $body | Out-Null
        Write-Host "Discord release notification sent for $($release.name)."
    }
    catch {
        Write-Error "Discord release notification failed: $($_.Exception.Message)"
        if ($_.ErrorDetails) { Write-Error $_.ErrorDetails.Message }
    }
```

### 6. Testing

CI-only change, no unit tests. End-to-end verification:

- **Pre-merge dry run.** After the feature branch is pushed, cut a throwaway prerelease tag (e.g. `v0.0.0-discord-test`) targeting that branch, let the workflow run, confirm the embed shows up in Discord, then delete both the test release and the tag. The `continue-on-error: true` step means even a totally broken script can't permanently break the release pipeline; the worst case is that the `.exe` ships but no Discord ping fires.
- **Post-merge verification.** The next real release (`v0.6.0` or whatever lands next) is the live test.

No matrix testing, no fake Discord — the surface is too small to be worth mocking, and the dry-run tag covers the happy path.

## Risks & mitigations

- **Webhook URL leak via workflow logs.** The URL is only in the step's `env:` block, never echoed; the `Write-Error` paths log the exception message + Discord's response body but not the URL. pwsh doesn't expand env vars inside `Write-Host` arguments unless asked.
- **`${env:VAR}` parsing in YAML.** Easy to get subtly wrong. We use the established pattern already proven in the `Publish` step.
- **Release name vs tag name.** `softprops/action-gh-release@v2` defaults the release `name` to the tag (`v0.6.0`). If a future PR sets a custom `name:`, the embed title picks that up automatically.
- **GitHub release notes generation lag.** `gh release view` runs in the same job, immediately after `Create GitHub Release` — GitHub guarantees the release is committed before the action's API call returns, so there's no race.
- **`continue-on-error` masking real bugs.** It does — that's by design. The release itself succeeding is the user-facing contract; Discord is a nice-to-have. The Write-Error output is still in the workflow log for diagnosis.

## Out of scope / future ideas

- Posting screenshots / animated previews of changes — would need asset generation, much bigger scope.
- Separate Discord channel for prereleases — currently flagged via footer text + color; if it becomes noisy, a second webhook URL is one new secret away.
- Pinging a specific role (e.g. `<@&123…>`) for major-version releases — easy follow-up if useful.
- Cross-posting to other platforms (Twitter / Mastodon / RSS) — distinct integration; would mirror this design.
