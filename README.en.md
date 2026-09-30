# DLSSG 30-Series Manager

[简体中文](README.md) | **English**

[![Release](https://img.shields.io/github/v/release/BUNNY-19C/DLSSG-30s-manager?style=flat-square&label=latest)](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)
[![License](https://img.shields.io/github/license/BUNNY-19C/DLSSG-30s-manager?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?style=flat-square)
![GPU](https://img.shields.io/badge/GPU-RTX%2030%20Series-76B900?style=flat-square)

A graphical manager for [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86): find games, configure frame generation per game, deploy proxy DLLs, restore original files, and manage local Mod updates.

Primarily intended for **RTX 30-series GPUs**, with hardware testing by the maintainer on an RTX 3080 Ti. The manager handles configuration and files; image quality, performance and game compatibility depend on the upstream Mod, driver and game.

## Download

Open the **[latest release](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)** and choose:

| File | Use |
|---|---|
| `DLSSGManager-VERSION-setup.exe` | Installer with a choice of location and language, plus an uninstaller |
| `DLSSGManager.exe` | Portable single executable; place it in a folder and run it |
| `SHA256SUMS.txt` | SHA256 checksums for the executables |

Supports Windows 10 / 11 x64. **No separate .NET installation is required.** Setup does not use the network. Mod files are downloaded separately on first start, or manually through “Download / update mod files”.

[v1.10.0 release notes](docs/RELEASE-NOTES.md) · [Mod files and licensing](docs/mod-files.md)

## First use

1. **Close the game you want to modify.** Start the manager and wait for the Mod download to finish.
2. Use “Scan Steam library”, “Scan folder…” or “Add game…”, then select a game. Confirm that the render directory contains the actual rendering executable; some games use a separate folder structure, as described below.
3. Choose the runtime build, frame-generation multiplier and optimization tier. Proxy entry, preset and log level are under advanced settings.
4. Click “Deploy to this game”, or “Apply to game” for an existing deployment. Start the game and enable its corresponding frame-generation option.
5. To undo the installation, close the game and click “Restore”.

The status card shows the game state, configuration state, local payload and deployed version. Name search and status filters can be combined. Tools and output can be expanded; collapsed output still shows the latest result.

> [!WARNING]
> **Games with kernel-level anti-cheat carry account risk.** Anti-cheat may block or quarantine a proxy DLL, or record a detection. The manager detects and warns; deployment requires explicit confirmation. Confirming the warning does not establish that the game permits this Mod.

## Configuration, builds and updates

### Saving versus applying

| Action or state | Meaning |
|---|---|
| Save configuration | Saves to the manager's library without writing game files |
| Deploy / Apply to game | Writes the proxy DLL and configuration to the game's render directory |
| Unsaved | Current settings differ from the configuration saved in the manager |
| Saved, pending application | Settings are saved but not applied; older records without a configuration snapshot also show this state |
| Applied | Settings match the last deployment record; later changes require applying again and restarting the game |

Settings are stored per game. If saving fails, the error and retry action remain visible. Complete the save before exiting.

### 310.9 and 310.1

| Build | Multiplier ceiling | Optimization tiers | Preset |
|---|---|---|---|
| 310.9 | 6X | 0–3 | Auto / A / B |
| 310.1 | 4X | 0 / 1 | Auto |

Choose **the build to download** in the top bar, then **the build to use for a game** in its details. These control the local download and game deployment separately. Unsupported settings are explained and block deployment without being changed automatically.

The multiplier is a ceiling; the actual count depends on the game's requests and plugin support. The builds are stored separately: 310.9 in `mod/`, and 310.1 in `mod/variants/310.1/`.

### Updates and batch operations

1. Click “Download / update mod files”, choose a source and wait. **Downloading updates local payload files only; it does not modify games automatically.**
2. Use “Select updates”, or check the games you want to process.
3. Review the current search and status filters, then click the batch deploy button. Normal batch operations process **only visible checked games**; hidden selections are counted separately.
4. Review individual results. “Retry failed items” processes the previous failed games, regardless of whether they are currently visible.

Batch restore also requires selecting games first. Switching builds updates matching owned standby proxies. If a custom standby has no matching file in the new build, restore the game before deploying that build.

## Game guidance

The following setups are recorded by this project. The guidance panel can suggest a game from its name or accept a manual choice, and checks whether related files exist. Presence alone does not verify version compatibility or in-game results.

| Game | Setup notes |
|---|---|
| Monster Hunter Wilds | Install the `MHWILDS` package from [REFramework](https://github.com/praydog/REFramework) first. Check `dinput8.dll`, `openvr_api.dll`, `openxr_loader.dll` and `reframework/` before deploying the Mod. Avoid overwriting an existing prerequisite proxy. |
| Zenless Zone Zero | Choose `d3d12.dll` under advanced settings → proxy entry. Recorded cases show other entry names being quarantined by anti-cheat; the anti-cheat warning still requires separate confirmation. |
| Neverness to Everness | Confirm the render directory is `Client\WindowsNoEditor\HT\Binaries\Win64`. The recorded setup needs `d3d12.dll`, `dinput8.dll` and `dlssg_sm86.ini` together. Prepare the files manually, then use “Adopt manual install”. Redeployment keeps and records standby proxies. |

The manager can adopt recognized manual installations, including community proxies identified by pinned hashes. Other games can be added manually; consult upstream instructions when selecting entry names and settings.

## Restore, data and troubleshooting

Restore checks file ownership: proxy DLLs are identified by the project's signature or a hash in the deployment record; configuration files are recognized using records and project-specific content. Files with unconfirmed ownership are kept. Displaced original configuration files are backed up and restored according to the saved records.

| Data | Location |
|---|---|
| Game library, manager log and restore backups | `%APPDATA%\DLSSGManager\` |
| Mod files | Prefer `mod/` beside the program; if the download location is not writable, use `%APPDATA%\DLSSGManager\mod` |
| In-game Mod logs | `dlssg_sm86\logs` inside the game's render directory |

Common fixes:

- **Download failed:** try a different source and check the output for the specific error.
- **Game missing or wrong directory:** add it manually and check the render directory. Use render-directory detection for supported layouts.
- **Deployment denied or files in use:** close the game first. For directory permission errors, expand Tools and restart as administrator.
- **No effect or a crash after deployment:** check prerequisites, proxy entry, in-game frame-generation settings and Mod logs. Restore first if needed, then follow upstream troubleshooting instructions.
- **Return to the original setup:** close the game and restore. Mod logs can optionally be removed at the same time.

Closing during an operation offers to wait for completion; a download can be cancelled before exiting. Keep the library and restore backups so managed installations can be restored later.

## Development and feedback

Building from source requires Windows and the .NET 8 SDK:

```powershell
dotnet build -c Release
dotnet run --project test/Harness -c Release -- --self-test
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, packaging and contributions, and [HANDOVER.md](docs/HANDOVER.md) for project context.

- Manager scanning, deployment, configuration or UI problems: [open an issue](https://github.com/BUNNY-19C/DLSSG-30s-manager/issues).
- Frame-generation quality, performance or upstream runtime problems: [upstream issues](https://github.com/sdli1995/dlssg_for_sm86/issues).

> [!NOTE]
> This project is developed with AI assistance. The maintainer handles hardware testing and releases. Reproducible reports and game test results are welcome.

## License

The manager code uses the [MIT license](LICENSE). Upstream Mod files and reference copies in `extra-proxies/` belong to their respective rights holders and **are not covered by this project's MIT license**. Release executables do not bundle or relicense those files.
