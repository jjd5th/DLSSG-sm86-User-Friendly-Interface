# DLSSG Manager

Windows desktop app for installing, configuring, updating, and removing [DLSSG for SM86](https://github.com/sdli1995/dlssg_for_sm86) on games that are already installed. It does not implement frame generation. It manages the official release packages.

The current install layout follows the upstream 0.4.0 documentation: one proxy DLL beside the rendering executable, `dlssg_sm86.ini`, and the `dlssg_sm86` payload folder. Older embedded-proxy packages are recognized and are not installed until their layout can be confirmed.

## Requirements

- Windows 10 or Windows 11, 64-bit
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- The published build is self-contained and does not need a separate .NET install

## Build

```powershell
dotnet build DLSSGManager.sln -c Release
dotnet test DLSSGManager.sln -c Release
```

## Publish a self-contained executable

```powershell
dotnet publish src/DLSSGManager.App/DLSSGManager.App.csproj -p:PublishProfile=win-x64
```

The executable is written to `src/DLSSGManager.App/bin/publish/win-x64/DLSSGManager.exe`.

## What the app does

- Detects the GPU, NVIDIA driver, Windows version, and Hardware-accelerated GPU scheduling. It does not change HAGS.
- Scans Steam, Epic, GOG, Xbox / Microsoft Store, EA App, Ubisoft Connect, Battle.net, and folders you add. Protected `WindowsApps` packages are detected and left alone.
- Classifies games from local files and a small database of games the upstream project has reported. Community reports are not treated as confirmed.
- Downloads official GitHub release zips over HTTPS, checks SHA-256 when GitHub publishes a digest, and refuses path-traversal entries.
- Installs, updates, repairs, rolls back, and uninstalls only files this app recorded. Existing unrelated proxy DLLs are not overwritten unless you approve it.
- Edits the upstream INI, including Optimized and MaxGeneratedFrames. 6× is offered only when the installed runtime and that game both support it.
- Exports a diagnostics report with user-profile paths redacted. A successful file install is not reported as frame generation being active.

## Data

Settings, the game library, backups, downloaded packages, and logs are stored under `%LOCALAPPDATA%\DLSSGManager`.

## Upstream

Installation rules come from [INSTALL.en.md](https://github.com/sdli1995/dlssg_for_sm86/blob/main/docs/INSTALL.en.md) and the current [releases](https://github.com/sdli1995/dlssg_for_sm86/releases). If a future package layout cannot be interpreted, the installer asks before changing files.
