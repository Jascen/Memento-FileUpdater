# SimpleFileUpdater
This is a simple cross-platform file synchronization system with a .NET server and .NET client that checks MD5 hashes against server files and downloads only files that differ.
- This is intended for servers to provide an easy way for players to stay up to date with their files, only downloading files the player needs, instead of all of them in a zip.
- This is customizable to your needs; you can change art, text, etc.

![image](https://github.com/user-attachments/assets/19862851-c269-4c8e-a442-71060d5fbc2b)
![image](https://github.com/user-attachments/assets/ef2e7725-02b8-498d-a93d-449ce535e062)

## Project Structure
```
SimpleFileUpdater/
├── src/
│   ├── Client/          # C# .NET client application
│   └── Server/          # C# .NET server application
├── README.md
├── LICENSE
└── CLAUDE.md
```  


## Setting up your own launcher
This repo is meant to be forked: you change a few config files for your server and build it. Work through this checklist in your fork:

1. **Launcher name and look**, in `src/Client/Config/LauncherConfig.cs`:
   - `Title`, `Subtitle` and their colors. `Title` also names the per-user folder where each player's install folder and settings are saved (`%AppData%/<Title>`), so give your launcher its own title.
   - `UpdateUrl`: the address of your update server, e.g. `http://updates.example.com:8080/`.
   - `Links`: the Website and Discord links along the top. Keep the `Verify` entry.
   - `DefaultInstallFolder`: the folder next to the exe that game files go into (default `Client`).
2. **Exe name**: `<AssemblyName>` at the top of `src/Client/FileUpdaterClient.csproj` (default `UODiabloLauncher`).
3. **TazUO launcher**, in `src/Client/Config/TazUOLauncherConfig.cs`:
   - `Enabled`: set to `false` if your players don't use TazUO. The launcher then only keeps files up to date.
   - `Profiles`: one entry per shard the TazUO launcher should list, with your server's IP, port and client version.
4. **Text**: any on-screen wording in `src/Client/Config/Strings.cs`.
5. **Art**: replace `src/Client/Assets/background.png` (the window is 900x675; other sizes are scaled to fill it) and `src/Client/Assets/icon.ico`.

### Building
**Automatic (recommended):** push a tag like `v1.0.0` to your fork on GitHub. The Release workflow builds the launcher for Windows, Linux, macOS (Intel) and macOS (Apple Silicon), plus the server for Windows and Linux, and attaches the zips to a GitHub release. You can also run the Release workflow by hand from the Actions tab to get the zips without making a release.

**By hand**, from `src/Client`:
- Quick build: `dotnet build -c Release`
- Windows: `dotnet publish -c Release -r win-x64`
- Linux: `dotnet publish -c Release -r linux-x64`
- macOS (Intel): `dotnet publish -c Release -r osx-x64` *May need to be run on a Mac*
- macOS (Apple Silicon): `dotnet publish -c Release -r osx-arm64` *May need to be run on a Mac*
- Each platform needs its own release. The output is in `src/Client/bin/Release/net9.0/{platform}/publish/`, and those are the only files you need to give players.

The macOS builds aren't signed, so players may need to right-click the launcher and choose Open the first time.

# Server

## Building the Server
1. Navigate to the server directory: `cd src/Server`
2. Build the server:
   - Development: `dotnet build -c Release`
   - Linux: `dotnet publish -c Release -r linux-x64 --self-contained`
   - Windows: `dotnet publish -c Release -r win-x64 --self-contained`
   - MacOS: `dotnet publish -c Release -r osx-x64 --self-contained`
3. The output will be in `src/Server/bin/Release/net9.0/{platform}/publish/`

## Running the Server
1. Run the server executable (from the publish directory or development build)
2. On first run, a `settings.ini` file will be created with default settings
3. A `files/` folder will be created automatically where you place all files you want the client to be able to check/download
4. Files you add, change or remove in `files/` show up in the file list a few seconds after they finish copying

## Configuration (settings.ini)
The server is fully configurable via `settings.ini`:
- **Port**: Server port (default: 8080)
- **FilesDirectory**: Where to serve files from (default: ./files/)
- **WatchFilesDirectory**: Rebuild the file list shortly after files change (default: true)
- **FileSettleTime**: Seconds a file must go unmodified before it is published, so half-copied files are never listed (default: 5)
- **CacheRegenerationInterval**: Full rebuild interval in seconds, as a fallback to watching (default: 3600 = 1 hour)
- **MaxConcurrentDownloads**: Limit concurrent downloads (default: 50)
- **MaxFileSize**: Maximum file size to serve in bytes (default: 0 = unlimited)
- **EnablePathTraversalProtection**: Security feature to prevent directory traversal attacks (default: true)
- **LogLevel**: Logging verbosity (default: Information)
- **LogFilePath**: Also write logs to this file, relative to the server (default: empty = console only)
- **EnableCompression**: Enable gzip/brotli compression (default: true)
- And more... see settings.ini for full configuration options 

## Hosting the launcher and client packages
The server can also host the launcher and client (TazUO) packages for players to download. These are programs players run, so they are signed:
1. Generate a signing key once, on your own machine: `dotnet run --project src/PackageSigner -- keygen`. Keep `package-signing.key` private (it is git-ignored) and never put it on the server. The launcher will be built with the printed public key.
2. Name your zips `{role}-{version}.{rid}.zip`, e.g. `launcher-1.2.0.win-x64.zip` and `client-3.4.0.win-x64.zip`, and put them in one folder.
3. Sign: `dotnet run --project src/PackageSigner -- sign --packages <folder> --key package-signing.key`. This writes `manifest.json` and `manifest.sig` next to the zips.
4. Upload the folder's contents to the server's `packages/` folder (`PackagesDirectory` in `settings.ini`). Players' launchers fetch `/packages/manifest.json` and only trust it if the signature matches.

The launcher uses these packages in two ways. The **client** (the TazUO launcher, unzipped into `TazUOLauncherConfig.InstallFolder`) is installed and updated with the game files. A newer **launcher** is only offered: a small notice appears next to the play button (at launch, and every `LauncherConfig.PackageCheckInterval` while it stays open) with an **Update launcher** button and a "Not now" dismiss. Players are never forced to update it, and Verify, Download and Play keep working while the notice is showing. The TazUO launcher is no longer downloaded from GitHub; it comes only from your server's `client-{version}.{rid}.zip`.

In your fork, set `LauncherConfig.TrustedPublicKeys` to the public key `keygen` printed. **While it is empty the launcher installs nothing it downloaded**, so it only updates game files and can't install the TazUO launcher. Only platforms you publish packages for are supported: a player on another platform gets no client install.

When the player clicks **Update launcher**, the launcher downloads the package (SHA-256 checked against the signed manifest) and starts a temporary copy of itself with `--apply-update`, then closes. The copy waits for the launcher to exit, unpacks the zip to a temporary folder first (so a bad zip or a full disk leaves the install alone), copies the files over with the exe last, and starts the new launcher. If anything goes wrong the old launcher is started again. It needs the launcher's folder to be writable and to be running as its own exe (not through `dotnet run`); otherwise the update is skipped for that run. A launcher package is the zip `release.yml` builds: the exe plus the native libraries beside it.

If you already had players on a launcher that installed TazUO from GitHub, the first update replaces their TazUO launcher files once with your hosted `client` package (their profiles are kept), since no installed version was recorded.

# Client Info
- Players can put the launcher anywhere. Game files are downloaded into a `Client` folder next to it by default, and players can pick another folder in Settings (the cog button).
- For example:
```
/UODiabloLauncher.exe
/Client/map0.mul
/Client/map1.mul
/Client/TazUO Launcher/
```
- The launcher checks each file's md5 against the server's and downloads any that differ or are missing.
