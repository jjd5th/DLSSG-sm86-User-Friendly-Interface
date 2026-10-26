# DLSSG Manager

DLSSG Manager installs, configures, updates, and removes [DLSSG for SM86](https://github.com/sdli1995/dlssg_for_sm86) on games that are already installed. It does not implement frame generation. Mod packages come from the upstream GitHub releases. Updates to this application are separate and come from [DLSSG Manager releases](https://github.com/jjd5th/DLSSG_Manager_UI/releases).

## For end users

The normal download does not need Visual Studio, the .NET SDK, or Python.

1. Open the [latest release](https://github.com/jjd5th/DLSSG_Manager_UI/releases/latest).
2. Download `DLSSGManager-vX.Y.Z-win-x64.zip`.
3. Compare its SHA-256 with `SHA256SUMS.txt` if you want to check the download.
4. Extract the ZIP to a normal folder, such as `Documents\DLSSG Manager`. Do not put it in a protected `WindowsApps` directory.
5. Double-click `DLSSGManager.exe`.

Windows SmartScreen can warn about an unsigned program. That warning is expected. Do not turn off Microsoft Defender or other Windows security protections to use this application.

### Scan, install, and remove

- **Scan games** looks through Steam, Epic, GOG, Xbox / Microsoft Store, EA App, Ubisoft Connect, Battle.net, and any folders you add in Settings.
- Open a game to read the compatibility evidence. **Confirmed** and **Likely** are not the same as **Unknown**. A DLL by itself is not treated as confirmation.
- **Blocked** games, including anti-cheat and protected `WindowsApps` installs, cannot be modified.
- **Install** copies the selected upstream release next to the rendering executable, after a backup. If another program already has a proxy DLL there, each file has to be approved. The app backs it up before replacing or removing it.
- **Configure** edits `dlssg_sm86.ini`. Restart the game after saving. The mod does not reload the file while the game is running.
- **Update** moves a game to a newer mod release and keeps the original game files that were backed up on the first install.
- **Rollback** returns the mod files to the previous mod release. It does not put the game back to the files it had before DLSSG Manager ever changed them. The original-file backup is kept.
- **Uninstall** restores those original game files and puts back any third-party DLL this app removed. Use uninstall when you want the game directory as it was before the mod. Use rollback when you want the last mod version instead of the current one.
- A finished install means the files were copied and checked. It does not mean frame generation is running. The game detail page says when a backend log shows an active route.

### Update DLSSG Manager

The Updates page checks `jjd5th/DLSSG_Manager_UI` separately from the mod. **Download manager update** saves the new ZIP to a folder you choose. **Manager release** opens the same release in the browser. Extract that ZIP yourself and run the new EXE. The running program is not replaced. Settings, the game library, and backups stay in `%LOCALAPPDATA%\DLSSGManager`.

### Update DLSSG for SM86

**Check for updates** reads the upstream mod releases. **Update all compatible** and the per-game **Update** button change the mod on games this app already installed. They do not upgrade DLSSG Manager.

### Where data is stored

`%LOCALAPPDATA%\DLSSGManager` holds settings, the game library, backups, downloaded mod packages, and logs. The folder next to the EXE is not used for that data. Updating or replacing `DLSSGManager.exe` does not clear that folder.

Troubleshooting logs are in `%LOCALAPPDATA%\DLSSGManager\logs`. An interrupted install, update, rollback, repair, or uninstall leaves a journal in `%LOCALAPPDATA%\DLSSGManager\journals`. The next launch tries to undo it. If that undo cannot finish, the journal and backups stay on disk and the game is marked as needing manual recovery.

### Troubleshooting

- **Potentially compatible** means the GPU looks like an NVIDIA card, but the architecture, driver, or Hardware-accelerated GPU scheduling could not be fully matched to the upstream requirements. An unrecognized RTX name is not treated as supported.
- **Action required** usually means HAGS is off or the NVIDIA driver is older than the documented R555 line. The app will not change those settings. Turning HAGS on in Windows Graphics settings needs a restart.
- **Unknown** games can be installed only after you confirm that choice. **Blocked** games stay blocked.
- If an upstream package does not match a known layout, installation stops and explains why instead of guessing which files to copy.
- If a backup of an original game file is missing, update and uninstall stop before changing files.

## For developers

The solution is `DLSSGManager.sln`:

- `src/DLSSGManager.Core` — discovery, compatibility, hardware, releases, installation, configuration, and the SQLite library
- `src/DLSSGManager.App` — WPF interface
- `tests/DLSSGManager.Tests` — unit and installer tests against temporary directories

Build requirement: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). End users do not need it.

```powershell
dotnet build DLSSGManager.sln -c Release
dotnet test DLSSGManager.sln -c Release
```

Publish a self-contained Windows executable:

```powershell
dotnet publish src/DLSSGManager.App/DLSSGManager.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishProfile=win-x64
```

The executable is `src/DLSSGManager.App/bin/publish/win-x64/DLSSGManager.exe`. The single-file host extracts native components such as SQLite when the process starts. That cache is managed by the runtime. Users do not install .NET separately.

Check the published executable without opening the window:

```powershell
.\src\DLSSGManager.App\bin\publish\win-x64\DLSSGManager.exe --self-test --data $env:TEMP\dlssg-self-test --log $env:TEMP\dlssg-self-test.log
Get-Content $env:TEMP\dlssg-self-test.log
```

Exit code 0 and a log line `result: pass` mean the runtime, SQLite, compatibility database, settings, and core services started. The check uses the directory given by `--data`, so it does not change `%LOCALAPPDATA%\DLSSGManager`. It does not scan games or use the network. A nonzero exit code means a required check failed. The same log is written because the published program is a Windows application and may have no console window.

Copy that EXE to another Windows 10 or Windows 11 x64 PC, extract nothing else, and run the same command. That machine does not need the .NET SDK. `dotnet publish` succeeding on a developer PC is not that check.

The same settings are in `src/DLSSGManager.App/Properties/PublishProfiles/win-x64.pubxml`. Trimming and Native AOT are not enabled.

### CI and releases

- `.github/workflows/ci.yml` restores, builds, tests, publishes, and runs `DLSSGManager.exe --self-test` on GitHub-hosted Windows. The self-test must exit 0 within 90 seconds.
- `.github/workflows/release.yml` runs on tags named `v*`, such as `v1.0.0`. The tag must match `<Version>` in `src/DLSSGManager.App/DLSSGManager.App.csproj` (`v1.0.0` for `1.0.0`). The workflow then builds, tests, and creates `DLSSGManager-<tag>-win-x64.zip` plus `SHA256SUMS.txt`. The ZIP is checked for `DLSSGManager.exe`, `README.md`, and `LICENSE` only. The extracted EXE must pass `--self-test` before the GitHub Release is created.

Tag a release only after that version is the one in the project file:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

If the release workflow fails, open the failed job log. A tag mismatch names the tag and the project version; change one of them and push a new tag rather than reusing the failed one. A self-test failure prints `self-test.log` in the job log. A ZIP failure names the missing or extra file. The release is not created when an earlier step fails.

Installer tests use temporary directories only. They do not modify real game installations.
