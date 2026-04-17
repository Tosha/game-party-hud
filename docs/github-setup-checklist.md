# GitHub repository setup checklist

One-time configuration of the GitHub repository for safe public use.
Each setting below has a direct link; do them in order.

**Policy goals:**
- The repository is public so anyone can read / fork / file issues.
- Nobody can push directly to `main` — all changes go through pull requests.
- Only the repository owner (@Tosha) can approve and merge PRs.
- CI must pass before anything merges.
- Secrets stay out of the repo; sensitive reports have a private channel.

---

## 1. General repository settings

Open **Settings → General** (https://github.com/Tosha/game-party-hud/settings).

- [ ] **Description:** Set to something like *"Peer-to-peer party HP overlay for Windows games without a built-in party UI. Free, no accounts, anti-cheat safe."*
- [ ] **Website:** Leave blank or link to the GitHub Releases page.
- [ ] **Topics:** Add tags so people can discover the repo: `wpf`, `csharp`, `dotnet`, `gaming`, `party`, `overlay`, `webrtc`, `windows`, `peer-to-peer`.
- [ ] Under **Features**:
  - [x] **Issues** — enable (lets users file bugs)
  - [ ] **Discussions** — optional, enable if you want a Q&A space
  - [ ] **Wiki** — disable (docs live in the repo)
  - [ ] **Projects** — optional
  - [x] **Preserve this repository** — enable if offered (GitHub Archive)
- [ ] Under **Pull Requests**:
  - [x] **Allow squash merging** — recommended default merge strategy
  - [ ] **Allow merge commits** — your call; squash keeps history cleaner
  - [ ] **Allow rebase merging** — your call
  - [x] **Always suggest updating pull request branches**
  - [x] **Automatically delete head branches** — keeps the branch list tidy

---

## 2. Branch protection on `main`

This is the most important one — it's what enforces "no direct pushes, PRs required, owner must approve."

Open **Settings → Branches → Add branch ruleset** (or edit the existing one for `main`).

- [ ] **Ruleset name:** `main-protection`
- [ ] **Enforcement status:** Active
- [ ] **Target branches:** Include `main` (or `Default branch`)
- [ ] **Restrict deletions** — ✅
- [ ] **Require a pull request before merging** — ✅
  - [ ] **Required approvals:** `1`
  - [ ] **Dismiss stale pull request approvals when new commits are pushed** — ✅
  - [ ] **Require review from Code Owners** — ✅ *(this is what makes you the sole approver; combined with `.github/CODEOWNERS`, every file requires your review)*
  - [ ] **Require approval of the most recent reviewable push** — ✅
- [ ] **Require status checks to pass** — ✅
  - [ ] Add `build-test` (the job name from `.github/workflows/ci.yml`) as a required check
  - [ ] **Require branches to be up to date before merging** — ✅
- [ ] **Block force pushes** — ✅
- [ ] **Require linear history** — ✅ (keeps history clean for a solo-maintained repo)
- [ ] **Do not allow bypasses** — or, if you want to self-merge your own PRs without waiting for an external approver: add yourself as a bypass actor under *Bypass list*. For a solo repo, this is the pragmatic choice.

> **Note on "Require review from Code Owners" for a solo owner:**
> If you are the only contributor and push your own PRs, GitHub won't let you approve your own PR. The clean fix is: add yourself to the Ruleset's **Bypass list** with role *Repository admin*, so you can merge your own PRs after CI passes but nobody else can. External contributors still need your explicit approval.

---

## 3. Require signed commits (optional but recommended)

Open the same branch ruleset.

- [ ] **Require signed commits** — ✅ *(ensures every commit on main is cryptographically signed; prevents identity spoofing)*

You'll need GPG or SSH commit signing configured locally. GitHub guide: https://docs.github.com/en/authentication/managing-commit-signature-verification.

---

## 4. Actions permissions

Open **Settings → Actions → General**.

- [ ] **Actions permissions:** `Allow Tosha and select actions and reusable workflows` — restrict to trusted actions.
  - [ ] Enabled actions: check `Allow actions created by GitHub` and `Allow Marketplace actions by verified creators`.
  - [ ] Add the specific non-verified actions we use as exceptions:
    - `softprops/action-gh-release@v2`
- [ ] **Workflow permissions:** `Read and write permissions` *(needed by `release.yml` to create releases and upload artifacts)*
- [ ] **Fork pull request workflows:** **Require approval for all outside collaborators** — ✅ *(prevents drive-by fork PRs from triggering our workflows without your explicit go-ahead)*

---

## 5. Secrets and environments

We currently need no secrets (the release workflow uses the default `GITHUB_TOKEN`).

- [ ] If you later add code signing: store the certificate in **Settings → Secrets and variables → Actions** as a secret, not in the repo.
- [ ] Never commit values from `%AppData%\GamePartyHud\config.json` — that file can contain a user's custom TURN credentials.

---

## 6. Security features

Open **Settings → Code security and analysis**.

- [ ] **Dependency graph** — enabled (default for public repos)
- [ ] **Dependabot alerts** — ✅ *(emails you when a dependency has a CVE)*
- [ ] **Dependabot security updates** — ✅ *(auto-opens PRs with fixes)*
- [ ] **Dependabot version updates** — optional; enable with a `.github/dependabot.yml` if you want routine dep bumps
- [ ] **Secret scanning** — ✅
- [ ] **Push protection** — ✅ *(blocks commits that contain secrets)*
- [ ] **Code scanning (CodeQL)** — optional; enable the default setup for C#

---

## 7. Private vulnerability reporting

Open **Settings → Code security and analysis → Private vulnerability reporting**.

- [ ] ✅ Enable. This gives security researchers the "Report a vulnerability" link on the repo, which creates a draft advisory visible only to you. The `SECURITY.md` file in this repo points here.

---

## 8. Moderation and interaction limits

Open **Settings → Moderation → Code review limits** / **Interaction limits**.

- [ ] You can leave these at defaults. If the repo ever gets spammed, tighten to **Limit to existing users** for 24 hours.

---

## 9. Release pipeline

### How to cut a release

1. Make sure `main` is green on CI and contains everything you want in the release (including the `CHANGELOG.md` entry for the new version).
2. Locally, on `main`:
   ```bash
   git checkout main && git pull --ff-only
   git tag vX.Y.Z -m "vX.Y.Z"
   git push origin vX.Y.Z
   ```
3. Watch **Actions → release** — it builds, tests, publishes the self-contained single-file `.exe`, zips it as `GamePartyHud-vX.Y.Z-win-x64.zip`, and creates a GitHub Release with auto-generated notes.

The **tag name is the version of record.** No `<Version>` in the `.csproj`; the published binary inherits the default (1.0.0.0) for its AssemblyVersion metadata, but everything user-facing (release title, ZIP filename, release notes) reflects the tag.

### Two gotchas to avoid

- **Push the tag with `git push`, not via GitHub's web "Create a new release" form.** The workflow is triggered by `on: push: tags:` events. Creating a release through the web UI may create the tag without firing that event, and you'll end up with a release page that only has "Source code (zip/tar.gz)" — no built `.exe`. If that happens, delete the release AND the tag (`git push origin --delete vX.Y.Z`), then re-tag with `git tag` and `git push origin vX.Y.Z`.

- **Tag the correct commit.** By default `git tag` tags the current `HEAD`, which must be the commit on `main` that contains the `CHANGELOG.md` entry for this version. If you're not sure, do it explicitly: `git tag vX.Y.Z <sha> -m "..."`.

### Sanity check (optional)

- [ ] Do a pre-flight release by pushing a tag like `v0.0.0-preflight`:
  ```bash
  git tag v0.0.0-preflight -m "preflight"
  git push origin v0.0.0-preflight
  ```
  Watch **Actions → release**. If it produces a release with the zipped `.exe`, delete the test release (`gh release delete v0.0.0-preflight --yes --cleanup-tag`) and cut the real tag.

---

## 10. Community files

- [x] `README.md` — landing page with install + configuration guide
- [x] `LICENSE` — already in repo
- [x] `SECURITY.md` — how to report vulnerabilities
- [x] `.github/CODEOWNERS` — you own everything
- [x] `.github/pull_request_template.md` — already in repo (M1)
- [x] `.github/ISSUE_TEMPLATE/*.md` — already in repo (M1)
- [ ] `CONTRIBUTING.md` *(optional; README has a brief section, expand only if contributor volume grows)*
- [ ] `CODE_OF_CONDUCT.md` *(optional; GitHub offers a one-click template under Insights → Community Standards)*

Check progress at **Insights → Community Standards** — GitHub shows a checklist of recommended community files.

---

## 11. After everything is set

Verify the protection actually works:

- [ ] Try to push directly to `main` from a fresh clone:
  ```bash
  git clone https://github.com/Tosha/game-party-hud.git check
  cd check
  echo "test" >> README.md
  git commit -am "direct push test"
  git push
  ```
  You should get a rejection message like *"Changes must be made through a pull request."* If it succeeds, branch protection isn't active — revisit step 2.

- [ ] Discard that test clone.
