# Creating a TranscodeTools GitHub Release

*Start-to-finish reference for cutting a new release.*

## Overview

A release is the combination of three things: a git tag pointing at the commit you want to ship, a built binary attached as a download, and release notes describing what changed. This document walks through the full sequence.

Time required: about 10 minutes once the code is committed and pushed.

## 1. Confirm the code is ready

Before tagging, make sure:

- All intended changes are committed and pushed to the main branch
- The app builds cleanly in Release configuration
- Quick smoke test: launch from `bin\Release`, confirm the app opens, run a small transcode if practical
- Reference doc is up to date with the new behaviour

## 2. Tag the release commit

From a PowerShell prompt in the repo directory:

```powershell
git tag -a v1.2.0 -m "Release v1.2.0 — keep-awake + single-instance"
git push origin v1.2.0
```

Replace `v1.2.0` with the actual version. Use semantic versioning: MAJOR for breaking changes, MINOR for new features, PATCH for bug fixes only. The current change set (one bug fix, one new feature) is a MINOR bump.

> **Note:** Annotated tags (`-a` flag) carry an author, date, and message. Use annotated tags for releases — never lightweight tags. If you tag the wrong commit, delete with `git tag -d v1.2.0 && git push origin :refs/tags/v1.2.0`, then re-tag and re-push.

## 3. Build the release binary

### Option A — Visual Studio UI

1. Right-click the TranscodeTools project in Solution Explorer → Publish
2. Target: Folder → Specific target: Folder
3. Location: a folder outside the repo, e.g. `C:\temp\TranscodeTools\publish\`
4. Click Show all settings and configure:
   - Configuration: Release | Any CPU
   - Target framework: net10.0-windows
   - Deployment mode: Self-contained
   - Target runtime: win-x64
   - Produce single file: ✓ checked
   - ReadyToRun compilation: optional (faster startup, larger file)
   - Trim unused code: leave OFF (trimming breaks WPF XAML reflection)
5. Click Save, then Publish

### Option B — Command line

Faster once you've done it once. From the project directory:

```powershell
dotnet publish TranscodeTools.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o C:\temp\TranscodeTools\publish
```

`IncludeNativeLibrariesForSelfExtract` bundles WPF's native DLLs into the .exe so you really do get a single file.

### Why self-contained

Self-contained bundles the .NET 10 runtime into the package, producing a ~70 MB .exe that runs anywhere on Windows 10+ x64 without requiring the .NET Desktop Runtime to be installed. Framework-dependent builds are smaller (~5 MB) but require the user to install .NET first — not worth the friction for a personal-utility app.

## 4. Prepare the zip

In the publish folder, you should see roughly:

- `TranscodeTools.exe` — the main file, ~70 MB
- `TranscodeTools.pdb` — debug symbols, delete before zipping
- A few native DLLs may also appear (e.g. `D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`). Keep these — they are WPF natives that sometimes resist single-file packing.

Delete the .pdb, then right-click remaining files → Send to → Compressed folder. Name the zip to match the tag:

```
TranscodeTools-v1.2.0-win-x64.zip
```

Including `win-x64` in the name pays off later if you ever ship an arm64 build.

## 5. Smoke-test the zip

Before publishing, verify the zip works on a clean path:

1. Copy the zip to a folder that is NOT the publish folder, e.g. `C:\temp\release-test\`
2. Extract
3. Double-click `TranscodeTools.exe`
4. Verify: app opens, theme loads correctly
5. Verify: launching the .exe a second time shows the 'Already Running' message
6. Verify: a small test transcode runs to completion

If all checks pass, the build is good to ship.

## 6. Create the release on GitHub

1. Open the repo on GitHub in a browser
2. Right sidebar → Releases (or append `/releases` to the repo URL)
3. Click Draft a new release
4. Choose a tag: select the tag pushed in step 2 (e.g. `v1.2.0`)
5. Target: leave on the default branch (main / master) — this only matters if the tag does not yet exist
6. Release title: `v1.2.0 — Short Description` (e.g. 'v1.2.0 — Sleep fix and single-instance enforcement')
7. Description: paste the release notes (see next section)
8. Attach binaries: drag the zip from step 4 into the assets area
9. Set as latest release: leave checked
10. Pre-release: leave unchecked unless this is a beta
11. Click Publish release

> **Note:** Drafts are invisible until published. Useful if you want to stage the notes and binary before going live. Watchers are not notified until publish.

## 7. Release notes template

Keep release notes user-facing. Technical detail belongs in commit messages, not release notes. A brief, scannable structure works best:

```markdown
# What's New

## Fixes

- **Brief description of the fix** — one or two sentences explaining what was broken and what the user will now see.

## Changes

- **Brief description of the change** — what is different from the previous version.

---

## Install

Download `TranscodeTools-vX.Y.Z.zip` below, extract, and run `TranscodeTools.exe`. Existing settings and preferences carry over.
```

This is markdown — paste it directly into the GitHub release description box.

## 8. After publishing

- GitHub auto-generates a 'Compare to previous release' link at the bottom of the rendered release page
- The release shows up under Releases and as a banner on the repo home page
- Anyone watching the repo with release notifications enabled will get an email
- The download URL is stable: `https://github.com/<user>/<repo>/releases/download/v1.2.0/TranscodeTools-v1.2.0-win-x64.zip`

## 9. Common troubleshooting

### Tagged the wrong commit

```powershell
git tag -d v1.2.0
git push origin :refs/tags/v1.2.0
git checkout <correct-commit>
git tag -a v1.2.0 -m "..."
git push origin v1.2.0
```

### Need to update release notes after publishing

Edits to release notes don't notify watchers a second time. Click the release on GitHub → pencil icon → edit → Update release.

### Need to replace the binary after publishing

Edit the release, delete the old asset, drag in the new one. The download URL stays the same.

### Released the wrong build entirely

Don't try to fix a published release. Cut a new patch version (e.g. `v1.2.1`) with the corrected build, and optionally mark the bad release as a pre-release or delete it. Don't reuse the same version number — anyone who already downloaded the bad build will not get the new one automatically.

## Quick reference

```powershell
# Tag and push
git tag -a v1.2.0 -m "Release v1.2.0 — short description"
git push origin v1.2.0

# Build self-contained single-file
dotnet publish TranscodeTools.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o C:\temp\TranscodeTools\publish

# Then in browser:
# 1. Repo → Releases → Draft a new release
# 2. Choose tag v1.2.0
# 3. Title, notes, drag zip
# 4. Publish release
```
