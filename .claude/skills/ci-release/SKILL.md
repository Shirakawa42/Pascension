---
name: ci-release
description: CI/CD and the in-game self-updater — the GitHub Actions release pipeline (GameCI Windows+macOS builds, draft-then-publish atomicity, PUBLIC_RELEASE SoI strip), the Application.version==manifest update contract, updater download/verify/swap flow, and hard-won CI lessons. Use when touching .github/workflows, CiBuild, Core/Update or Game/Update, cutting a release, or debugging CI or updater failures.
---

# CI/CD & Self-Update

## Pipeline (`.github/workflows/release.yml`)
Every push to main → EngineVerify dotnet gate → GameCI on ubuntu (Mono) builds StandaloneWindows64 + StandaloneOSX (universal x64+arm64 via reflection in CiBuild; rcodesign 0.29.0 ad-hoc signs the .app — mandatory for Apple Silicon) → GitHub Release `v1.0.<run_number>` with:
- `pascension-windows-v*.zip` (zip root = install root)
- `pascension-macos-v*.tar.gz` (single `pascension.app`; the client extracts ONLY with /usr/bin/tar — C# ZipFile strips exec bits)
- `latest.json` (schema = `Core/Update/UpdateManifest`: version, tag, publishedAt, per-platform url/sha256/sizeBytes)

**Draft → upload ALL assets → publish is the atomicity contract**: `/releases/latest/download/latest.json` skips drafts, so updaters never see a half release.

`tests.yml` = dotnet gate only for PRs/non-main branches (sparse checkout of Tools/EngineVerify + Tools/ShardsData + Assets/Scripts + Assets/Tests + ProjectSettings — skips ~260 MB of art, ~1-2 min).

Secrets (set once): `UNITY_LICENSE` (.ulf XML) / `UNITY_EMAIL` / `UNITY_PASSWORD`. `PUBLIC_RELEASE: "true"` in release.yml strips SoI from builds (define gates GameCatalog registration + drops the GameShards scene; SoI DLLs/art still ship unreachable — full strip deferred). unity-mcp is jq-stripped from the CI manifests (hence `allowDirtyBuild`).

## Build entry
`Pascension.Editor.CiBuild.Build` (unity-builder `buildMethod`); bakes `-buildVersion` into `PlayerSettings.bundleVersion` — **Application.version == manifest version IS the update contract**. Re-running a published run collides on the tag; the release job fails fast (push a new commit instead).

## Self-update
- Pure logic in `Assets/Scripts/Core/Update/{VersionCompare,UpdateManifest,UpdateSwapScripts}` (headless-tested — `UpdateLogicTests`); Unity side in `Assets/Scripts/Game/Update/{UpdateChecker,UpdateInstaller,UpdateMenuControl}`.
- Menu shows a version label (bottom-left) and, when `latest.json` is newer than Application.version, a gold UPDATE button above the language toggle (Root-parented widgets, MainMenu.Start hook).
- Click → UWR streams to `persistentDataPath/updates/` → sha256 verify (manifest re-fetched pre-download so a stale hash never mismatches) → extract → swap script in %TEMP% (waits for our PID; Windows robocopy /E **no /PURGE**; mac .app mv-with-rollback + `xattr -dr`; relaunches; self-deletes) → quit.
- **macOS translocation (2026-07-20)**: Gatekeeper runs a quarantined browser-downloaded .app from a read-only nullfs mount (`/AppTranslocation/`) — the norm for a fresh mac install. `CanSelfInstall` now recovers the REAL bundle from the mount table (`Core/Update/TranslocationResolver` parses `/sbin/mount`: the nullfs source IS the original path — same data Apple's private SecTranslocate SPI reads) and the swap targets it; its `xattr -dr` ends translocation for good. OPEN DOWNLOAD PAGE remains the fallback when resolution or the writability probe fails. The updater's own downloads are never quarantined (plain POSIX writes, no LSFileQuarantineEnabled), so a self-updated app never re-triggers Gatekeeper.
- Editor = dry-run to staging ("editor — swap skipped"). Check is once per app run and silent on ANY failure. `-updateurl <url>` overrides the manifest URL for testing.
- Verified live: fake-server loop in the editor (fetch→button→download→verify→extract) AND the generated .cmd standalone on real cmd.exe (French locale; no-purge + self-delete observed).
- Multiplayer version gate (mismatch rejected at connection approval) — see the networking skill.

## CI lessons (verified live: release v1.0.3, run 3, 2026-07-19)
- **Verify CI fixes against a fresh CLONE of HEAD, not the working tree** (WSL Ubuntu + user-local ~/.dotnet is the repro rig). Runs 1-2 failed on committed-vs-disk gaps:
  - The blanket `*.csproj` gitignore hid `Engine.Verify.csproj` from CI (MSB1003) — now `!Tools/EngineVerify/*.csproj`.
  - The test job's sparse checkout lacked ProjectSettings (WireFormatGoldenTests root-walk) + Tools/ShardsData (export-test writes).
- Repo/company facts: repo public; Company Name = "Lucas 2684634" (the Unity Cloud org — matches the pre-existing persistentDataPath).
- Run 3 green end-to-end; downloaded zip hash-matched latest.json; installed build logged "installed v1.0.3, latest v1.0.3 → UpToDate" against the real GitHub manifest.

## Still unverified
- The macOS artifact on real Apple hardware — including the translocated-swap path (`mv`-ing the source dir of a live nullfs mount right after the game quits; the swap script rolls back on failure).
- A real button-click self-update against a newer release (any next push to main makes installed copies show the button).
