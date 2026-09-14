# DLSSG 30-Series Manager

[简体中文](README.md) | **English**

[![Release](https://img.shields.io/github/v/release/BUNNY-19C/DLSSG-30s-manager?style=flat-square&label=download)](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)
[![License](https://img.shields.io/github/license/BUNNY-19C/DLSSG-30s-manager?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4?style=flat-square)](#)
[![GPU](https://img.shields.io/badge/GPU-RTX%2030%20series%20(SM86)-76B900?style=flat-square)](#)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4?style=flat-square)](#)

> [!NOTE]
> **This project is developed by AI.** The code, the interface text, the documentation and the tests are all AI-generated; the maintainer runs them on real hardware and publishes. Every number and conclusion here comes from an actual run (on-machine logs, controlled tests), but AI output is not free of mistakes — please [open an issue](../../issues) when you find one.

A graphical manager for [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86). Deploy the mod per game and restore with one click, instead of copying DLLs into game folders by hand.

The mod is a DLL proxy: placing `version.dll` (or one of the alternative entry names) and `dlssg_sm86.ini` **beside the game's rendering executable** enables DLSS frame generation on RTX 30 series (SM86) cards.

> [!IMPORTANT]
> **Two game-specific prerequisites, read before you start**
>
> - **Monster Hunter Wilds**: install the [REFramework](https://github.com/praydog/REFramework) prerequisite first — download its `MHWILDS.zip` and extract `dinput8.dll`, `openvr_api.dll`, `openxr_loader.dll` and `reframework\` next to `MonsterHunterWilds.exe`. Without it, deploying this mod crashes the game every time (measured). With it, 4X works.
> - **Zenless Zone Zero**: you must use the `d3d12` entry (its HoYoKProtect renames away the bundled entry names). This repository ships that DLL with the download; set “Proxy entry” from *Automatic* to `d3d12.dll` in the interface.

> [!WARNING]
> **Games with kernel-level anti-cheat are a risk zone.** Such anti-cheat may block or quarantine the proxy DLL, and a recorded detection may put your account at risk. The manager detects it and **warns, leaving the decision to you** — see [the anti-cheat section](#anti-cheat-risk-assessment-your-call).

**[⬇ Download the latest release](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)** — installer or portable exe; neither needs .NET installed.

**Highlights**

- Scans Steam libraries or any folder, finding games by the `nvngx_dlssg.dll` they ship, so unrelated programs never show up
- Per-game configuration (route, frame multiplier, sampling mode, log level)
- Kernel-level anti-cheat is detected when a game is added; the manager warns you and leaves the decision to you
- Displaced files are backed up; restore only deletes files whose signature and hash both check out
- Batch deploy and restore
- Built-in downloader, so the repository carries no 75 MB of binaries. It also fetches the `d3d12.dll` entry Zenless Zone Zero needs

---

## Contents

- [Direct use (recommended)](#direct-use-recommended)
- [Running from source](#running-from-source)
- [Interface](#interface)
- [How it protects your files](#how-it-protects-your-files)
- [Anti-cheat: risk assessment, your call](#anti-cheat-risk-assessment-your-call)
- [About GPUs](#about-gpus)
- [Updating the mod files](#updating-the-mod-files)
- [Entry names and your own DLLs](#entry-names-and-your-own-dlls)
- [Repository layout](#repository-layout)
- [Theme and localisation](#theme-and-localisation)
- [Building from source](#building-from-source)
- [License](#license)

---

## Direct use (recommended)

Download from [**Releases**](../../releases/latest) — two options:

| File | Description |
|---|---|
| `DLSSGManager-*-setup.exe` | **Installer.** Choose the install path, ships an uninstaller, adds a Start menu entry |
| `DLSSGManager.exe` | **Portable.** A single file; put it anywhere and run it |

Neither requires .NET or any other runtime.

The mod files (about 75 MB) are not bundled with the installer, and setup does not download them either — installing needs no network at all. The program detects the newest published version on first start and fetches it, writing the version it got into the log.

### About the installer

The wizard asks for the install language first (Simplified Chinese or English), then the install location (default `C:\Program Files\DLSSG 30-Series Manager`, changeable).

Setup installs the program only: pick a language, pick a folder, no network, no downloads. (Forgetting about the rest is fine — the first start fetches the mod files by itself.)

Where the mod files end up:

| Situation | Location |
|---|---|
| Program folder is writable (the usual case) | `mod\` beside the program |
| Program folder is not writable (`Program Files` without elevation) | `%APPDATA%\DLSSGManager\mod` |

Keeping them beside the program makes the folder self-contained — copy it anywhere and it still works. Uninstalling asks separately whether to delete the mod files and game data; **a silent uninstall always keeps them**, so reinstalling does not mean re-downloading 75 MB.

### Using it

1. **Find your games** — use “Scan Steam library”, “Scan folder…” for a game drive, or “Add game…” for a single game folder.
   Games are located by the `nvngx_dlssg.dll` they ship: a game must include it to expose DLSS frame generation at all, so unrelated programs never appear. The Steam scan reads the registry and `libraryfolders.vdf`, covering custom library paths.
   **Anti-cheat is checked as you add a game**: a kernel-level one is reported immediately and deployment is blocked.
2. **Pick a game** — its own configuration appears on the right. The route is pre-filled from your GPU (RTX 3080 Ti → `SM86`).
3. **Click “Deploy to this game”** — done. The mod files are copied into the render directory.

To undo it, click “Restore”. Batch actions are at the bottom of the window: “Deploy all” and “Restore all”.

**Zenless Zone Zero needs a different entry**: its anti-cheat renames away the bundled names such as `version.dll`, and only `d3d12.dll` survived in testing. See [which entry it needs](#which-entry-zenless-zone-zero-needs).

Releases ship a `SHA256SUMS.txt` if you want to verify the download:

```powershell
Get-FileHash DLSSGManager.exe -Algorithm SHA256
```

---

## Running from source

This repository **does not contain** the mod binaries (about 75 MB, and their licence does not permit redistribution — see [docs/mod-files.md](docs/mod-files.md)).

You need the .NET 8 SDK; see [Building from source](#building-from-source). The first run fetches the mod files by itself, exactly as the released build does.

---

## Interface

The toolbar shows the current mod file source and your GPU; its right-hand side holds the scan, download and “Add proxy DLL…” buttons (the last of these is covered under [entry names](#entry-names-and-your-own-dlls)). Top right has four controls:

- **Restart as administrator** — needed when the game lives under `C:\Program Files`, where writing requires elevation. Restarts through a UAC prompt.
- **Open data folder** — opens `%APPDATA%\DLSSGManager`, which holds the configuration and backups.
- **Theme** — switches the interface between dark and light, applied immediately and remembered.
- **Language** — switches the interface between Simplified Chinese and English, applied immediately and remembered.

Every field in the detail panel maps directly onto the mod's INI keys (upstream documents them under "Advanced keys"):

| Field | INI key | Notes |
|---|---|---|
| Enable frame generation | `Enabled` | Off falls back to the game's own DLSS-G, which means no frame generation on Ampere |
| Optimized kernels | `Optimized` | On by default. Off keeps the runtime's stock numerics, matching the vendor output |
| Render preset | `Preset` | 310.9 builds only: `Auto` lets the game or driver profile decide, `A` forces UI recomposition off, `B` on |
| Frame multiplier | `MaxGeneratedFrames` | 1–5 map to 2X–6X (310.9 package; the 310.1 package tops out at 4X). The game decides the actual multiplier |
| Log level | `Level` | Set to 2 when troubleshooting; logs land in `dlssg_sm86\logs` inside the game folder |

The panel shows whichever set the mod source's INI actually defines: 0.3.0 uses the fields above, and an older 0.2.x payload shows `Router` / `KernelImage` / `HardwareBilinear` instead. The manager only writes keys the template contains, so one generation's keys never end up in the other's INI.

Configuration is stored **per game**; re-deploy to write changes.

---

## How it protects your files

Game folders often already contain other mods (ReShade's `dxgi.dll`, for instance), so removal has to distinguish "ours" from "theirs". Three checks are used together:

1. **Digital signature** — all five proxy DLLs carry the `CN=DLSSG Native Project` self-signed certificate; the manager reads it to confirm ownership.
2. **Hashes** — a SHA-256 is recorded for every file at deploy time and verified afterwards.
3. **Backups** — if a target name is occupied by a file that is not ours, it is copied to `%APPDATA%\DLSSGManager\restore\<game>\<timestamp>\` before being displaced, and put back on restore.

What that means in practice:

- **Anti-cheat games get a warning first** — kernel-level anti-cheat may block the proxy DLL and carries account risk, so the manager lays out the evidence before deploying and writes nothing until you confirm. See [the anti-cheat section](#anti-cheat-risk-assessment-your-call).
- **An occupied entry name is never overwritten**: the manager picks a free name from the available entries: the six bundled ones first, in upstream's own order (`version.dll` → `winmm.dll` → `dinput8.dll` → `dbghelp.dll` → `dxgi.dll` → `d3d12.dll`), then anything you added. If all of them are taken it reports the conflict and leaves everything untouched.
- **Restore only deletes its own files**: a hash mismatch means the file is kept and reported, never deleted blindly.
- **A running game blocks the operation**: both deploy and restore check for processes inside the render directory first.
- **Existing manual installs can be adopted**, bringing them under management so restore works later. A hand-copied `d3d12.dll` is included in that: it carries no signature from this project, so it is recognised by its pinned hash.

---

## Anti-cheat: risk assessment, your call

The mod is a DLL proxy, which is exactly what kernel-level anti-cheat watches for, so these games are a risk zone. The anti-cheat may rename and quarantine the proxy before the game starts (leaving something like `version.dll.3787982156` behind), and it may record the detection, which is a risk to your account.

Confirmed by testing: **Zenless Zone Zero** ships miHoYo's HoYoKProtect, which quarantined `version.dll` on sight, after which the game reported `The client component is running abnormally, please restart the client. Error Code:(0,11008,2195210578)`. A community-built `d3d12.dll` entry survives the same game; see [which entry Zenless Zone Zero needs](#which-entry-zenless-zone-zero-needs).

The scan covers the game folder **and three levels of parent directories** and recognises:

| Anti-cheat | Indicators |
|---|---|
| miHoYo HoYoKProtect | `HoYoKProtect.sys`, `mhypbase.dll` |
| Tencent ACE | `ACE-*.sys`, `AntiCheatExpert\`, `SGuardSvc*.exe` |
| NetEase NEAC | `NeacSafe*.sys`, `NeacInterface.dll`, `NeacLoader.exe` |
| Easy Anti-Cheat | `EasyAntiCheat*.sys`, `start_protected_game.exe` |
| BattlEye | `BEClient*.dll`, `BEService*.exe` |
| nProtect GameGuard | `GameGuard.des`, `npgmup.des` |
| Riot Vanguard | `vgk.sys`, `vgc.exe` |
| XIGNCODE3 | `x3.xem`, `XignCode` |

Detection matches **both files and directories** (Tencent ACE usually ships as an `AntiCheatExpert\` folder) and walks up three parent levels, since anti-cheat usually sits in the game root rather than the render directory. Any `.sys` kernel driver in the game folder is reported too (no shipping game needs one), which catches vendors missing from the table. Windows' own files such as `pagefile.sys` are not misreported.

The folder you pick is **resolved to the actual render directory** before scanning. Skip that and the walk starts too high and misses the anti-cheat entirely: Overwatch keeps its own in `E:\Overwatch\_retail_\`, so pointing at the outer `E:\Overwatch` never sees `NeacSafe64.sys`. Verified in practice.

When a kernel-level anti-cheat turns up, the manager warns rather than forbids:

- one prompt when the game is added (several at once are merged into a single dialog);
- an orange warning bar at the top of the detail panel;
- “Deploy to this game” asks for confirmation with “No” preselected, and nothing is written until you agree;
- “Deploy all” lists these games separately in its confirmation and includes them once you agree;
- “Restore” stays available to clean up leftovers.

The scan can tell *whether* anti-cheat is present, not *whether it will block the entry name you chose*. Zenless Zone Zero quarantines `version.dll` and leaves `d3d12.dll` alone. That call is yours, so the manager shows the evidence and the consequences and stops there.

**Measured results on the development machine**, for reference:

| Game | Anti-cheat | Result |
|---|---|---|
| Zenless Zone Zero | miHoYo HoYoKProtect | ⚠ bundled entries get quarantined; use `d3d12.dll` ([below](#which-entry-zenless-zone-zero-needs)) |
| War Thunder | BattlEye | untested |
| Arknights: Endfield | Tencent ACE | untested |
| Overwatch | NetEase NEAC | untested |
| Monster Hunter Wilds | none | ✅ works once the REFramework prerequisite is installed (4X, no errors) |

For the four with anti-cheat the manager warns before deploying, and whether to deploy is your decision.

**Monster Hunter Wilds**: no anti-cheat, but the mod crashed it every time (`MonsterHunterWilds.exe + 0xa4d69d0`; switching mod versions, updating the DLSS runtime and turning off the in-game FG toggle changed nothing).

**The cause was a missing prerequisite: [REFramework](https://github.com/praydog/REFramework)** — the mod framework RE Engine games use; Wilds takes its `MHWILDS` package. With it installed the test passed: the manager deploys as usual (proxy + INI), `dinput8.dll` goes to the framework and the proxy falls back to `version.dll`, frame generation works in game, the mod's log shows zero errors and three generated frames per real frame (4X), and it shuts down cleanly.

The prerequisite is installed by hand, since it belongs to another project: grab `MHWILDS.zip` from REFramework's releases and extract `dinput8.dll`, `openvr_api.dll`, `openxr_loader.dll` and `reframework\` next to `MonsterHunterWilds.exe`.

**If you already deployed**: click “Restore”. The manager recognises and removes the renamed copies anti-cheat leaves behind (only files confirmed by signature or hash to be ours), along with the INI. The status column reports this as “quarantined by anti-cheat” rather than a plain missing file.

**After deploying to a game with anti-cheat**: if the game errors out or frame generation does not appear, that entry name is being blocked; try another entry or just click “Restore”. The account risk of a recorded detection is yours to carry.

**Contributing results**: the [compatibility report](https://github.com/BUNNY-19C/DLSSG-30s-manager/issues/new?template=game_compatibility.yml) template is there for both working and failing cases — failures are just as useful to others.

---

## About GPUs

The mod's SM86 path was validated on real hardware by the author on an **RTX 3080 Ti**.

Worth knowing:

- **RTX 40/50 series do not need it**: Ada and Blackwell support DLSS frame generation natively. The manager says so when it detects one.
- **VRAM** scales with output resolution: roughly 320–340 MiB at 1080p, 490–520 MiB at 1440p and 700–770 MiB at 4K. Without headroom you get occasional stutter even when the average frame rate looks fine.
- **No 6X, no dynamic multiplier**, and no Reflex Warp.
- **Antivirus may flag it**: DLL proxying plus hooking is the kind of behaviour heuristics watch for. All five DLLs carry a self-signed certificate (file properties → Digital Signatures), but self-signing gets no default Windows trust and does not prevent alerts.
- **Turn frame generation on in the game** after deploying; the mod does not enable it for you.

### The architecture comes from the hardware ID, not the GPU name

The manager reads the **PCI device ID** to decide between the SM86 and SM75 routes; the product name is only cross-checked.

Not needless caution: the name lives in the registry and tools can rewrite it, while the device ID is bound to the physical chip. One machine we tested reported `RTX 4090` (Ada) in the registry while hardware ID `2208` was in fact a 3080 Ti (Ampere). Judging by name would have concluded "a 40-series card does not need this mod"; the hardware ID gives the right answer.

When the two disagree the manager warns and suggests putting the name back: a wrong name is not cosmetic, because drivers and games make functional decisions from it.

---

## Updating the mod files

Click “Download / update mod files”. Sources are tried in order until one succeeds:

> The 0.3.0 payload is larger than before: about 101 MB compressed, six entry DLLs of roughly 17.5 MB each. This step fetches all six.

| Order | Source | Notes |
|---|---|---|
| 1 | GitHub archive (codeload) | One request, about 101 MB |
| 2 | GitHub API (zipball) | Same content, different entry point |
| 3 | GitHub raw files | Per-file, about 110 MB; reachable from China without a proxy (measured 2026-09-14) |
| 4 | ghfast (China accelerator) | China-based node, per-file only; verified working |
| 5 | jsDelivr CDN mirror | Serves a cached branch snapshot, so it can lag by hours; a stale payload fails the certificate pin and the next source is used |
| 6 | gh-proxy (China accelerator) | Used to be the fastest node here; on 2026-09-14 it answers 403 for every path in this repository, so it is tried last |

**Clicking “Download / update mod files” opens a picker first**, where you can name a source or leave it on the default “Automatic” (try each in turn, falling back when one is unavailable). Choosing a specific source uses only that one and will not quietly switch elsewhere, so the source in the log is trustworthy.

Each source is retried once before moving on, so one blocked or flaky endpoint causes a fallback rather than a failed update.

### Trust boundary for third-party mirrors

The last three entries are third-party forwarders, not the authority for this content, so the **certificate pin is enforced strictly** on them: only a signer matching the recorded value is accepted. On GitHub's own endpoints a mismatch is logged and accepted instead, so an upstream certificate rotation does not break updating.

That distinction is documented in the fetcher's source (`ModFetcher.Verify`), and it is also why the source list is defined at compile time rather than in a runtime config file: the list is itself a trust boundary.

### Downloads are verified

The payload is native DLLs destined for game folders, so the download path is treated as untrusted:

- HTTPS only, only the hosts in the table above, and every resolved IP must be public (loopback, private, CGNAT, link-local, multicast and reserved addresses are rejected). Redirects are re-validated at every hop.
- Response size is capped, and archive entries may not escape the destination folder.
- **The payload is signature-verified before anything is written**: every DLL must carry a valid Authenticode signature from the project's certificate — changing a single byte invalidates it — and the signer's certificate thumbprint must match the recorded value.

That last point matters for the mirror: a mirror is not the authority for the content, so its certificate must match exactly. On GitHub's own endpoints a mismatch is logged and accepted instead, so an upstream certificate rotation does not break updating.

You can also place the files yourself: put `version.dll`, `dlssg_sm86.ini` and `altnative\` into `mod\`; the manager recognises them by the folder structure. See [docs/mod-files.md](docs/mod-files.md).

## Entry names and your own DLLs

0.3.0 ships **six entry names**: `version.dll` in the root, plus `winmm.dll`, `dinput8.dll`, `dbghelp.dll`, `dxgi.dll` and `d3d12.dll` under `alternatives\`. An entry name is simply the DLL name the game will load; the proxy only enters the process if the game loads that name, and a wrong name means a deployment that does nothing at all.

The manager fetches all six (into `mod\altnative\`) and offers them in every game's “Proxy entry” picker. The order follows upstream's own advice: names off the D3D12 render path first (`version` → `winmm` → `dbghelp` → `dinput8`), with `dxgi` and `d3d12` last, because those are called every frame and their load order is sensitive.

### Which entry Zenless Zone Zero needs

Zenless Zone Zero is the case where you must switch entries. Its HoYoKProtect watches the game folder and renames away anything called `version.dll` or another classic entry name, after which the game reports `Error Code:(0,11008,2195210578)`. The community-built `d3d12.dll` survived in testing, and 0.3.0 ships a d3d12 entry of its own — so picking `d3d12.dll` in the entry picker is all there is to it, with no file to hunt down.

If you installed the community d3d12.dll by hand earlier: it belongs to the previous generation, and pairing it with a 0.3.0 INI is meaningless, so restore first and deploy the manager's copy. That file is still recognised by its pinned hash, so “Check status” offers to adopt it and a restore removes exactly what it recorded.

One aside: third-party Zenless Zone Zero bundles usually also carry two NVIDIA runtime DLLs (`nvngx_dlss.dll` and `nvngx_dlssg.dll`, version 310.9.1). The manager only handles the proxy and the INI and never touches those two. If frame generation does not appear with the proxy and INI alone, copy them into the game folder by hand as well, keeping a backup of the game's own copies first.

### Using your own DLL

For your own build, or another community one, click “Add proxy DLL…” in the toolbar and pick the file:

- it is copied into `mod\altnative\` under its own file name. The name cannot change: it is what the game looks for;
- the manager only reads the signer into the log (the certificate subject if there is one, an explicit "unsigned" if not). The file is yours, so its origin is yours to vouch for;
- from then on it appears in the entry picker like the bundled names and can be deployed and removed with the normal buttons. Restore deletes it by the SHA256 recorded at deploy time and leaves everything else alone.

Two limits: the five bundled names cannot be replaced, since the signature check depends on them, and a name outside the known set (those five plus `d3d12.dll`) triggers a note that the game most likely never loads it, which leaves the decision with you. Also, if `mod\altnative\d3d12.dll` already exists with different contents, the downloader keeps your copy instead of overwriting it.

Last thing, on why the name matters so much: each build exports only the system API surface of the name it stands in for. The manager will not rename `version.dll` to `d3d12.dll` and deploy it — the game's D3D12 imports would find no implementation and the game would not start at all.

---

## Repository layout

```
DLSSGManager/
├─ src/DLSSGManager/          ← source (WPF, .NET 8)
├─ test/Harness/              ← tests and diagnostic tooling
├─ installer/                 ← Inno Setup script (Simplified Chinese language file included; English uses the built-in Default.isl)
├─ docs/mod-files.md          ← why the repo has no mod binaries, and how to obtain them
├─ mod/                       ← mod file source (not committed; fetched on first run)
├─ publish/ dist/             ← build output (not committed)
```

Runtime data:

```
%APPDATA%\DLSSGManager\
├─ library.json               ← game list, per-game configuration, deployment records, UI language
├─ restore\                   ← backups of displaced files
├─ mod\                       ← mod files (only when the program folder is not writable)
└─ manager.log                ← operation log
```

---

## Theme and localisation

**Theme**: dark and light palettes, switched from the right end of the toolbar, applied immediately and remembered (`InterfaceTheme` in `library.json`). Both live under `src/DLSSGManager/Themes/` and the interface references them with `DynamicResource` — that has to be dynamic, because a static reference is resolved when the element is created and most of the window would keep the old colours after a switch.

The light theme is not the dark one inverted; the values are picked again, since a green or orange that reads well on dark is too light on white. A test checks that both themes define the same keys and computes the contrast of each text/background pair (7:1 for body text, 4.5:1 for secondary). Another check keeps colours out of the interface files and code — a control that misses theming only shows it after a switch, and that is easy to miss by eye.

**Localisation**: Simplified Chinese and English, same toolbar switch, applied immediately and remembered. The installer wizard asks for its language first.

Both tables live in `src/DLSSGManager/Strings.*.cs` and must define exactly the same keys — a test compares them, so a missing translation fails the suite instead of showing a raw key in the interface. Placeholders (`{0}`) are compared the same way, so arguments cannot end up in the wrong order in one language.

---

## Building from source

Requires the .NET 8 SDK.

```bash
git clone <repository-url>
cd DLSSGManager
dotnet build -c Release

# single-file, self-contained exe (target machine needs no .NET)
dotnet publish src/DLSSGManager/DLSSGManager.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o publish
```

`publish/DLSSGManager.exe` is about 63 MB and can be copied anywhere.

For a quick local run, switch `--self-contained true` to `false`: the output is about 340 KB, but the target machine then needs the .NET 8 runtime.

### Building the installer

The installer is compiled with [Inno Setup 6](https://jrsoftware.org/isdl.php). The Simplified Chinese language file under `installer/languages/` ships with this repository because the Inno Setup distribution does not include it; English uses the built-in `Default.isl`.

```powershell
# after the publish step above:
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.4.0 installer\setup.iss
```

The result is `dist/DLSSGManager-<version>-setup.exe`. The design decisions in that script — why the mod files do not go into the install directory, and why uninstalling keeps data — are documented in its header comments.

Releases are built automatically by GitHub Actions: pushing a `v*` tag (for example `git tag v1.4.0 && git push origin v1.4.0`) builds, tests, compiles the installer and creates a Release with both executables and `SHA256SUMS.txt`. The workflow can also be triggered manually from the Actions tab.

Testing (372 cases covering deployment and restore, backup protection, anti-cheat detection and risk prompts, entry-name management, extra-entry distribution and hash pinning, path validation, INI rendering, persistence, download URL policy, signature verification, source selection, theming and localisation):

```bash
cd test/Harness
dotnet build -c Release

# full suite; cases needing the mod files are skipped with a hint when they are absent
./bin/Release/net8.0-windows/Harness.exe

# fetch the mod files into the project's mod folder
./bin/Release/net8.0-windows/Harness.exe --fetch

# list the configured download sources and check the address policy
./bin/Release/net8.0-windows/Harness.exe --sources

# scan this machine's games and report anti-cheat, without modifying anything
./bin/Release/net8.0-windows/Harness.exe --scan "D:\Games\SomeGame"
```

Tests redirect the data folder to a temporary location (via the `DLSSGMANAGER_HOME` environment variable), so they never touch your real `library.json` or `manager.log`.

---

## License

The source code in this repository (the manager's C# implementation) is released under the [MIT License](LICENSE).

**That licence does not cover any file published by dlssg_for_sm86.** Those belong to the upstream project; this manager downloads them from their published location on demand and copies them into game folders. It does not redistribute or relicense them. See [docs/mod-files.md](docs/mod-files.md).

Please read the upstream repository's notes before using the mod, particularly regarding antivirus false positives, VRAM usage and anti-cheat restrictions.

---

## Contributing

Game compatibility results, anti-cheat signatures and other improvements are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). One note before you open a PR: this project's code and documentation are AI-generated (see the note at the top), and are organised to stay clear for AI readers too, so what counts is that a change passes the full `test/Harness` suite.

This program is a **deployment tool** for dlssg_for_sm86; it does not contain or modify the mod itself. Issues with frame generation itself (image quality, performance, per-game compatibility) belong with the [mod author](https://github.com/sdli1995/dlssg_for_sm86/issues).
