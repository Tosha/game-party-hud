# Discord Release Notification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the release workflow publishes a new GitHub Release, post a Discord embed (title → release URL, body = auto-generated notes, color-coded for stable/prerelease) to the existing `GPH_DISCORD_WEBHOOK_URL`.

**Architecture:** Single new `pwsh` step at the end of `release.yml`, after `Create GitHub Release`. The step fetches the just-created release with `gh release view`, builds an embed payload, and POSTs via `Invoke-RestMethod`. Marked `continue-on-error: true` and short-circuits internally when the webhook secret is unset, so failures never gate the release and forks without the secret are unaffected.

**Tech Stack:** GitHub Actions, `pwsh` (already on `windows-latest`), `gh` CLI (already authenticated via `GITHUB_TOKEN`), Discord webhook API.

**Spec:** [docs/superpowers/specs/2026-05-16-discord-release-notification-design.md](../specs/2026-05-16-discord-release-notification-design.md)

---

## File Structure

**Modify:**
- `.github/workflows/release.yml` — append one new step after `Create GitHub Release`.

No app code, no tests, no other docs.

---

## Task 1: Add the "Announce release on Discord" step

**Files:**
- Modify: `.github/workflows/release.yml`

- [ ] **Step 1: Append the step to the end of the `build-and-release` job**

Open `.github/workflows/release.yml`. The current last step is `Create GitHub Release` using `softprops/action-gh-release@v2`. Append the following new step immediately after it (same indentation level — top-level `steps:` entry):

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

          # gh release view runs against the just-created release; GitHub
          # guarantees the prior action's call has committed the release
          # before returning, so there's no race here.
          $release = gh release view $env:TAG_NAME --json name,body,url,isPrerelease |
                     ConvertFrom-Json

          # Discord embed description has a 4096-char limit. Truncate with
          # a "see full release" footer link so long release notes never
          # break the request.
          $desc = $release.body
          if ($desc.Length -gt 3900) {
              $desc = $desc.Substring(0, 3900) + "`n`n…notes truncated, [see full release]($($release.url))"
          }

          # Decimal Discord color values.
          # Stable  = green 0x2ECC71 = 3066993
          # Prerel. = amber 0xFFA500 = 16753920
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
              # Defensive: release notes mentioning @here / @everyone
              # would ping the channel otherwise. Same posture as
              # src/GamePartyHud/Network/DiscordNotifier.cs.
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
              # continue-on-error means this Write-Error won't fail the
              # whole job, but the message + Discord's response body land
              # in the workflow log for diagnosis.
              Write-Error "Discord release notification failed: $($_.Exception.Message)"
              if ($_.ErrorDetails) { Write-Error $_.ErrorDetails.Message }
          }
```

- [ ] **Step 2: YAML syntax sanity check**

Run: `pwsh -NoProfile -Command "Get-Content .github/workflows/release.yml | Out-Null; Write-Host OK"`
Expected: `OK` printed. (This only verifies the file is readable; full YAML lint isn't necessary because GitHub will surface parse errors when the workflow runs.)

Open `.github/workflows/release.yml` in an editor and visually confirm:
- The new step's `- name:` line starts with exactly 6 spaces (same indent as the existing `- name: Create GitHub Release` step).
- The `run: |` block's pwsh content is indented one level deeper.
- No tabs anywhere in the new block (GitHub Actions YAML uses spaces only).

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "feat(ci): announce new releases on Discord webhook with embed"
```

---

## Task 2: Pre-merge dry-run verification

**Files:** None (CI-only test, no code changes).

The change can only be exercised end-to-end on a real `v*` tag push. Strategy: cut a throwaway prerelease tag on the feature branch *after* it's pushed, watch the workflow run, confirm the Discord embed shows up, then clean up.

`continue-on-error: true` on the new step means even a totally broken script can't permanently break the release pipeline — worst case the `.exe` ships but no Discord ping fires.

- [ ] **Step 1: Push the feature branch**

```bash
git push -u origin feat/discord-release-notification
```

- [ ] **Step 2: Cut a throwaway prerelease tag on this branch**

The tag name must start with `v` (to match the workflow's `tags: ['v*']` trigger) and contain a `-` (so the workflow marks it as prerelease — handy because amber color visually distinguishes the test from a real release).

```bash
git tag v0.0.0-discord-test
git push origin v0.0.0-discord-test
```

- [ ] **Step 3: Watch the workflow run**

Open https://github.com/Tosha/game-party-hud/actions and click into the new `release` workflow run for the `v0.0.0-discord-test` tag.

Expected:
- All earlier steps (`Restore`, `Build`, `Test`, `Publish`, `Zip release`, `Create GitHub Release`) succeed.
- The new `Announce release on Discord` step logs `Discord release notification sent for v0.0.0-discord-test.` and exits green.

If the step fails: read the `Write-Error` output in the log for the HTTP status code and Discord's response body, fix the script in a follow-up commit, push, and re-run by deleting and re-pushing the tag (Step 5 covers cleanup; just redo the create after).

- [ ] **Step 4: Confirm the Discord embed**

Open the project's Discord channel.

Expected:
- An embed appears with an **amber** sidebar (prerelease color).
- The title `v0.0.0-discord-test` is bold and clickable; clicking opens the GitHub release page.
- The description shows the auto-generated release notes — at minimum the changelog comparison link, since this throwaway release has no PRs since the previous one.
- The footer reads `Prerelease`.
- No `@here` / `@everyone` pings fired.

- [ ] **Step 5: Clean up the test tag and release**

```bash
# Delete the release on GitHub (also detaches the tag from any release).
gh release delete v0.0.0-discord-test --yes

# Delete the tag locally and on the remote.
git tag -d v0.0.0-discord-test
git push origin :refs/tags/v0.0.0-discord-test
```

Confirm at https://github.com/Tosha/game-party-hud/releases that the test entry is gone.

- [ ] **Step 6: No commit**

Verification-only. If any of the above failed, fix-forward in a new commit on the same branch and re-run the dry-run from Step 2 (with a new tag name like `v0.0.0-discord-test2` to avoid stale-tag confusion).

---

## Verification Summary

After all tasks complete:

- New `Announce release on Discord` step exists in `.github/workflows/release.yml`, indented consistently with the surrounding steps.
- Dry-run prerelease tag produced a properly formatted amber embed in Discord with a clickable title.
- Test tag and release deleted from GitHub so the release list stays clean.
- No app code, no unit tests, no other workflows touched.

Post-merge: the next real `v*` tag push triggers the live test — embed should be green (stable color) with `Release` footer.
