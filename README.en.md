# DLSSG 30-Series Manager

[简体中文](README.md) | **English**

[![Release](https://img.shields.io/github/v/release/BUNNY-19C/DLSSG-30s-manager?style=flat-square&label=download)](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)
[![License](https://img.shields.io/github/license/BUNNY-19C/DLSSG-30s-manager?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4?style=flat-square)](#)
[![GPU](https://img.shields.io/badge/GPU-RTX%2030%20series%20(SM86)-76B900?style=flat-square)](#)

> [!NOTE]
> **This project is developed by AI.** The code, the interface text, the documentation and the tests are all AI-generated; the maintainer runs them on real hardware and publishes. Every number here comes from an actual run, but AI output is not free of mistakes — please [open an issue](../../issues) when you find one.

A graphical manager for [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86): deploy the mod per game and restore with one click, instead of copying DLLs into game folders by hand. The mod is a DLL proxy — put the proxy DLL and `dlssg_sm86.ini` beside the game's rendering executable and RTX 30 series (SM86) cards get DLSS frame generation.

> [!IMPORTANT]
> **Two game-specific prerequisites**
>
> - **Monster Hunter Wilds** needs the [REFramework](https://github.com/praydog/REFramework) prerequisite first: download its `MHWILDS.zip` and extract `dinput8.dll`, `openvr_api.dll`, `openxr_loader.dll` and `reframework\` into the game root. Without it, deploying this mod crashes the game every time (measured).
> - **Zenless Zone Zero** needs the `d3d12.dll` entry — names like `version.dll` get renamed away by its anti-cheat. The manager downloads that DLL for you.

## Download

Grab either file from [Releases](../../releases/latest); neither needs .NET installed: `DLSSGManager-*-setup.exe` (installer, choose your own path, comes with an uninstaller) or `DLSSGManager.exe` (portable, single file).

The mod files (about 101 MB) are not bundled and setup does not download them either — installing needs no network. **The first start detects the newest published version and fetches it**; later updates use “Download / update mod files”.

## Using it

1. “Scan Steam library” or “Add game…”. Games are found by the `nvngx_dlssg.dll` they ship, and the folder is scanned for anti-cheat at the same time.
2. Select a game and click “Deploy to this game”.
3. Click “Restore” to undo. Batch actions live at the bottom of the window.

> [!WARNING]
> **Games with kernel-level anti-cheat carry account risk.** The anti-cheat may block or quarantine the proxy DLL, and a recorded detection may put your account at risk. The manager detects it and warns; whether to deploy is your decision.

## Notes

- **Entry names**: `version.dll` by default, plus `winmm`, `dinput8`, `dbghelp`, `dxgi` and `d3d12`. If another mod occupies a name, the manager picks a different one.
- **Settings** are stored per game (frame generation, optimized kernels, render preset, multiplier ceiling, log level) and written into `dlssg_sm86.ini` on deploy.
- **Restore** only deletes files whose signature and hash both check out; displaced originals are backed up to `%APPDATA%\DLSSGManager\restore\` first.
- **Hand-installed copies** can be adopted, including a community `d3d12.dll` dropped in by hand (recognised by its hash).
- **GPU**: for RTX 30 series (validated by the author on a 3080 Ti). 40/50 series support frame generation natively and do not need this. VRAM grows with output resolution, about +700–770 MiB at 4K.
- **When something breaks**, look in `dlssg_sm86\logs` inside the game folder or click “View mod log”. The interface switches between dark/light and Chinese/English.

## From source

You need the .NET 8 SDK; build, test and packaging commands are in [CONTRIBUTING.md](CONTRIBUTING.md). The repository carries no mod binaries — the first run fetches them (see [docs/mod-files.md](docs/mod-files.md) for why).

## License

The code is [MIT](LICENSE). **MIT covers neither the files dlssg_for_sm86 publishes nor the reference copy under `extra-proxies/`** — those belong to their respective owners, and this project downloads them on demand without redistributing or re-licensing them.

Read the upstream documentation before use, especially the notes on antivirus false positives, VRAM cost and anti-cheat.

## Contributing

Compatibility results and improvements are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). This program is only a **deployment tool** for the mod; issues with frame generation itself (image quality, performance, per-game compatibility) belong with the [mod author](https://github.com/sdli1995/dlssg_for_sm86/issues).
