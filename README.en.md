# DLSSG 30-Series Manager

[简体中文](README.md) | **English**

[![Release](https://img.shields.io/github/v/release/BUNNY-19C/DLSSG-30s-manager?style=flat-square&label=latest)](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)
[![License](https://img.shields.io/github/license/BUNNY-19C/DLSSG-30s-manager?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?style=flat-square)
![GPU](https://img.shields.io/badge/GPU-RTX%2030%20Series-76B900?style=flat-square)

A graphical manager for [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86): scan for games, configure frame generation per game, deploy proxy DLLs, restore original files, and manage local Mod updates.

Primarily intended for **RTX 30-series GPUs**, with hardware testing by the maintainer on an RTX 3080 Ti. The manager handles configuration and files; image quality, performance and game compatibility depend on the upstream Mod, driver and game.

## Core features

- **Portable single executable**: download it, place it in a writable folder and run it.
- **Multiple download sources and offline import**: automatically compare source speeds, pause or resume downloads, skip the current source, or drag an upstream ZIP into the window to import local Mod files offline.
- **Per-game configuration**: save frame-generation multipliers, optimization tiers and advanced settings separately for each game, including proxy entry, preset, log level and per-frame GPU timing.
- **Automatic adoption and restoration**: status checks adopt recognized manual installations, including supported community proxies. Restore verifies file ownership by file hash and pinned-thumbprint project signatures, restores original configuration backups and keeps unknown files. Operations cut short by a crash or power loss are finished or undone on the next start.
- **Verified Mod downloads**: from any source, every file must match the release manifest's hash before it is used; each release lives in its own folder and the previous one is kept for rollback.
- **Elevation on demand**: when a game folder needs administrator rights, only that one operation is elevated; the manager itself never runs as administrator.
- **Batch management and upgrades**: combine search and status filters with batch deployment, batch restore and failed-item retries. Upgrades from 0.3.x to 0.4.0 remove redundant legacy proxies whose ownership can be verified.
- **Anti-cheat warnings**: detect games with kernel-level anti-cheat and require explicit confirmation before deployment.

## Download and run

Future releases provide the **portable build only**. Open the **[latest release](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)** and download:

| File | Use |
|---|---|
| `DLSSGManager.exe` | Portable single executable; place it in any writable folder and double-click to run |
| `dlssgm.exe` | Optional command-line tool sharing the same library, for scripts and batch work (`dlssgm --help`) |
| `SHA256SUMS.txt` | SHA256 checksums for the executables |

- **Requirements**: Windows 10 / 11 x64. **No installation or separate .NET runtime is required.** Place the executable in a writable folder and run it.
- **Mod files**: downloaded separately on first start, or manually through “Download / update mod files”.
- **Further reading**: [v1.13.0 release notes](docs/RELEASE-NOTES.md) · [Mod files and licensing](docs/mod-files.md)

## First use

1. **Prepare**: **close the game you want to modify.** Start the manager and wait for the Mod download to finish.
2. **Add a game**: use “Scan Steam library”, “Scan folder…” or “Add game…”, then select a game. Confirm that the render directory contains the actual rendering executable rather than the launcher.
3. **Adjust settings**: choose the frame-generation multiplier and optimization tier. Proxy entry, preset and log level are under advanced settings.
4. **Deploy**: click “Deploy to this game”, or “Apply to game” for an existing deployment. Start the game and enable its corresponding frame-generation option.
5. **Restore**: to undo the installation, close the game and click “Restore”.

The status card shows the game state, configuration state, local payload and deployed version. Name search and status filters can be combined. Tools and output can be expanded; collapsed output still shows the latest result.

> [!WARNING]
> **Games with kernel-level anti-cheat carry account risk.** Anti-cheat may block or quarantine a proxy DLL, or record a detection. The manager detects and warns; deployment requires explicit confirmation. Confirming the warning does not establish that the game permits this Mod.

## Configuration, builds and updates

### Saving versus applying

Settings are stored per game. If saving fails, the error and retry action remain visible. Complete the save before exiting.

| Action or state | Meaning |
|---|---|
| **Save configuration** | Saves to the manager's library without writing game files |
| **Deploy / Apply to game** | Writes one proxy DLL, the configuration and the `dlssg_sm86/` runtime directory to the game's render directory |
| **Unsaved** | Current settings differ from the configuration saved in the manager |
| **Saved, pending application** | Settings are saved but not applied; older records without a configuration snapshot also show this state |
| **Applied** | Settings match the last deployment record; later changes require applying again and restarting the game |

### Upstream 0.4.1

**NVIDIA R580 or newer is required.** Older drivers no longer fall back to PTX. Upstream may refuse older game frame-generation plugins; consult the game log for the reason.

| Runtime | Multipliers | DLSS tiers | DLSS presets |
|---|---|---|---|
| `310.9.16` (current) | DLSS 2X–6X; XeSS 2X / 3X / 4X | 0–3; new profiles default to 1 | A / B / C |

- **Downloads and defaults**: downloads target **0.4.1**. The full Release package includes SR files; the base package provides frame generation and XeSS. New profiles use DLSS, tier 1, preset A and SR off. Existing tiers and DLSS multipliers are retained. XeSS keeps a separate count: existing profiles retain 2X and can select 3X / 4X.
- **Multipliers and presets**: the requested count must be supported or frame generation is refused; the game's menu can still choose less. Presets A / B / C override game and driver preferences. Legacy Auto remains visible but is written as A on 0.4.1. C is FG Lite, with lower quality on HUD and occlusion edges.
- **XeSS**: adds `CoarseAt4K` (Coarse at output pixel counts of at least 3840×2160, otherwise Middle) and Auto / strict / off HUD-less input. The old preference migrates to Auto. Unsupported RGBA16F buffers no longer fall back to DLSS.
- **Super resolution**: RTX 20 / 30 only. Select Off, M-INT8, L-INT8, M-FP16 or L-FP16 explicitly; the selected preset affects every quality mode. Quality, Balanced and DLAA can be slower than the game's preset K. Legacy On has no equivalent single mode: it is retained and requires an explicit choice before applying 0.4.1.
- **Deployment and recovery**: one proxy plus the complete runtime directory; existing ownership checks and backups remain. Downloads do not update game files. Apply to each game and restart it.
- **Logs**: the log action prefers `dlssg_sm86/logs/dlssg_sm86.log`. Per-frame GPU timing writes a separate `frame_timing.txt`.

### Updates and batch operations

1. **Update local Mod files**: click “Download / update mod files” and choose the full or base package. Source speeds are measured; automatic mode tries the fastest first, or you can choose one source. Pause, resume or skip a source during downloads. Drag an upstream ZIP into the window to import it offline. **Downloads and imports update local payload files only; they do not modify games automatically.**
2. **Select games**: use “Select updates”, or check the games you want to process.
3. **Run a batch operation**: review the current search and status filters, then click the batch deploy button. Batch restore also requires selecting games first. Normal batch operations process **only visible checked games**; hidden selections are counted separately.
4. **Review results and retry**: review individual results. “Retry failed items” processes the previous failed games, regardless of whether they are currently visible.

- **Automatic adoption of manual installations**: status checks automatically adopt recognized manual installations, including community proxies identified by pinned hashes. No separate action is needed. Adoption records existing files without changing the game directory; manual installations have no original backup. Existing management records and backups are retained.

## Restore, data and troubleshooting

### Restore and file protection

- **File ownership checks**: a file is only overwritten or removed when it is provably the manager's — by a hash in the deployment record, a hash in a release manifest, or a pinned-thumbprint project signature; configuration files are recognized using records and project-specific content. Files with unconfirmed ownership are kept. Displaced originals are backed up and, after their hash is checked, restored according to the saved records.
- **Interruption safety**: every step of a deploy or restore is journalled first. After a crash or power loss, the next start undoes an unfinished deploy and completes an unfinished restore.
- **Library protection**: each save keeps the previous `library.json.bak`; an unreadable library is set aside and the backup loaded, never replaced by an empty one.
- **Runtime directory cleanup**: runtime components are recorded individually by hash. Restore keeps unknown files and unrecognized replacements; `dlssg_sm86/logs/` is removed only when log cleanup is selected.
- **Closing during an operation**: closing the window offers to wait for completion; a download can be cancelled before exiting. Keep the library and restore backups so managed installations can be restored later.

### Data locations

| Data | Location |
|---|---|
| Game library, manager log and restore backups | `%APPDATA%\DLSSGManager\` |
| Mod files | Prefer `mod/` beside the program; if the download location is not writable, use `%APPDATA%\DLSSGManager\mod`. Each release lives in `versions\<version>\`; the previous one is kept |
| In-game Mod logs | `dlssg_sm86\logs` inside the game's render directory |

### Common fixes

- **Download failed:** try a different source and check the output for the specific error.
- **Game missing or wrong directory:** add it manually and check the render directory. Use render-directory detection for supported layouts.
- **Deployment denied or files in use:** close the game first. For directory permission errors, deploy or restore asks whether to elevate that one operation (Windows then shows its administrator prompt).
- **No effect or a crash after deployment:** check prerequisites, proxy entry, in-game frame-generation settings and Mod logs. Restore first if needed, then follow upstream troubleshooting instructions.
- **Return to the original setup:** close the game and restore. Mod logs can optionally be removed at the same time.

## Development and feedback

Building from source requires Windows and the .NET 10 SDK:

```powershell
dotnet build -c Release
dotnet test -c Release
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, packaging and contributions, and [HANDOVER.md](docs/HANDOVER.md) for project context.

- **Manager scanning, deployment, configuration or UI problems**: [open an issue](https://github.com/BUNNY-19C/DLSSG-30s-manager/issues).
- **Frame-generation quality, performance or upstream runtime problems**: [upstream issues](https://github.com/sdli1995/dlssg_for_sm86/issues).

> [!NOTE]
> This project is developed with AI assistance. The maintainer handles requirements and releases. Reproducible bug reports and game compatibility feedback are welcome.

## License

The manager code uses the [MIT license](LICENSE). Upstream Mod files and reference copies in `extra-proxies/` belong to their respective rights holders and **are not covered by this project's MIT license**. Release executables do not bundle or relicense those files.
