# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SimpleFileUpdater is a cross-platform file synchronization system consisting of:
- **Client**: Avalonia-based .NET 9.0 desktop application that checks local files against a server and downloads updates
- **Server**: ASP.NET Core .NET 9.0 HTTP server that serves file listings with MD5 hashes and handles file downloads

The client downloads only files that differ (by MD5 hash) or are missing, avoiding full re-downloads.

## Directory Structure

```
SimpleFileUpdater/
├── src/
│   ├── Client/              # C# .NET client application
│   │   ├── App.axaml(.cs), Program.cs  # Entry point and application
│   │   ├── Views/           # Windows and dialogs (MainWindow, SettingsDialog, ConfirmDialog)
│   │   ├── ViewModels/      # MainViewModel and IMainView
│   │   ├── Config/          # Build-time config (LauncherConfig, TazUOLauncherConfig) and on-screen text (Strings)
│   │   ├── UserSettings/    # Player choices saved per user (InstallLocation, Preferences)
│   │   ├── TazUO/           # TazUO launcher install and start
│   │   ├── Updating/        # UI-free update logic (server client, file checks, UpdateService)
│   │   ├── Assets/          # Visual assets (background.png, icon.ico)
│   │   ├── *.csproj         # Project file
│   │   └── *.sln            # Solution file
│   ├── Client.Tests/        # xUnit tests for the update logic (Updating/, plus Fakes/ for the fake server and launcher)
│   ├── Server/              # C# .NET server application
│   │   ├── Program.cs       # Main entry point with endpoints
│   │   ├── ServerSettings.cs    # Configuration model
│   │   ├── IniConfigProvider.cs # INI file parser
│   │   ├── CacheService.cs      # Background cache service (watches the files directory)
│   │   ├── FileListBuilder.cs   # Builds the file list, reusing hashes of unchanged files
│   │   ├── FileLoggerProvider.cs # Writes logs to LogFilePath
│   │   ├── FileUpdaterServer.csproj  # Project file
│   │   └── settings.ini     # Server configuration file
│   └── Server.Tests/        # xUnit tests for the file list builder
├── README.md
├── LICENSE
└── CLAUDE.md
```

## Build Commands

### Client (C# .NET)

Basic build:
```bash
cd src/Client
dotnet build -c Release
```

Platform-specific builds (creates self-contained single-file executables):
```bash
cd src/Client

# Windows
dotnet publish -c Release -r win-x64

# Linux
dotnet publish -c Release -r linux-x64

# macOS (may need to run on Mac hardware)
dotnet publish -c Release -r osx-x64
dotnet publish -c Release -r osx-arm64
```

Output location: `src/Client/bin/Release/net9.0/{runtime}/publish/`. The exe is named by `<AssemblyName>` in `FileUpdaterClient.csproj` (default `UODiabloLauncher`); the root namespace stays `FileUpdaterClient`, and XAML refers to assets by relative path (`/Assets/...`) so renaming the exe doesn't break them.

GitHub Actions: `.github/workflows/ci.yml` builds the client and server and runs the client tests on every push to main and every PR. `.github/workflows/release.yml` publishes the client for win-x64, linux-x64, osx-x64 and osx-arm64 and the server for win-x64 and linux-x64 when a `v*` tag is pushed (or by hand), and attaches the zips to a GitHub release.

Tests (update logic against an in-memory fake server and file system):
```bash
cd src/Client.Tests
dotnet test
```

Each test is split into `//Arrange`, `//Act` and `//Assert` sections, with one action under `//Act` (tests with nothing to set up skip `//Arrange`).

Unit tests never touch the real disk or network: use `MockFileSystem` (System.IO.Abstractions) for files, with paths from `MockUnixSupport.Path(...)`, and `FakeServer` for HTTP.

### Server (C# .NET)

Basic build:
```bash
cd src/Server
dotnet build -c Release
```

Tests (file list builder against an in-memory file system and fake clock):
```bash
cd src/Server.Tests
dotnet test
```

Platform-specific builds (creates self-contained single-file executables):
```bash
cd src/Server

# Windows
dotnet publish -c Release -r win-x64 --self-contained

# Linux
dotnet publish -c Release -r linux-x64 --self-contained

# macOS (may need to run on Mac hardware)
dotnet publish -c Release -r osx-x64 --self-contained
```

Output location: `src/Server/bin/Release/net9.0/{runtime}/publish/`

## Architecture

### Client Architecture

The client uses Avalonia UI framework with MVVM pattern:

- **Entry Point**: `src/Client/Program.cs` → bootstraps Avalonia with `App`
- **Application**: `App.axaml(.cs)` → shared styles/brushes, creates `MainViewModel` and `MainWindow`, `App.Restart()`
- **Main Window**: `Views/MainWindow.axaml(.cs)` → view only. Forwards clicks to the view model and implements `IMainView` (settings/confirm dialogs, opening links, restarting), dimming the launcher while a dialog is open
- **View Model**: `ViewModels/MainViewModel.cs` → launcher state and actions: startup (install folder, preferences, check on launch), Verify, the center Download/Play button, settings, cancel. Subscribes to `UpdateService` events and posts them to the UI thread
- **Dialogs**: `Views/SettingsDialog` (install folder + preferences), `ConfirmDialog` (generic yes/no)
- **Config**: `Config/LauncherConfig.cs` (build-time branding, colors, server URL, links, folders), `Config/TazUOLauncherConfig.cs` (whether TazUO is used, its release URL, install folder and profiles), `Config/Strings.cs` (all on-screen text)
- **Player state**: `UserSettings/InstallLocation.cs` (install folder), `UserSettings/Preferences.cs` (settings dialog options), both saved under `%AppData%/<AppDataFolder>/`
- **TazUO**: `TazUO/TazUOLauncher.cs` → installs the TazUO launcher and its profiles (`ILauncherInstaller`) and starts it

### Update Logic (`src/Client/Updating/`)

No Avalonia or UI code, so it can be tested on its own:

- `FileServerClient` → HTTP: fetches the file list (5s timeout) and downloads a file to `.part`, checks its MD5, then moves it into place (15 min timeout). A `.part` left by a failed or cancelled attempt is resumed with a Range request; one that fails the MD5 check is deleted so the next attempt starts over. File names are URL-escaped per path segment. Takes an optional `HttpMessageHandler` so tests can fake the server, and writes files through `LocalFiles`
- `LocalFiles` → path safety (`TryGetLocalPath` rejects names outside the install folder), MD5, directory creation and file writes. All file access goes through an injected `IFileSystem` (System.IO.Abstractions): the app passes `new FileSystem()`, tests pass a `MockFileSystem`
- `UpdateService` → one instance per install folder:
  1. **CheckAsync()**: fetches the file list and compares local MD5s with `WORKER_COUNT` (2) workers, stopping at the first difference. Returns `UpdatesReady`, `LauncherReady` (TazUO missing), `Finished`, `Failed` or `Cancelled`; nothing is downloaded
  2. **DownloadAsync()**: runs after the player clicks "Download updates". Compares the files the check skipped, then downloads with `WORKER_COUNT` workers (up to 5 attempts per file, with backoff), then installs TazUO if enabled
  - Reports through `ProgressChanged`, `FileProgressChanged` and `ErrorOccurred` events (raised on background threads) and `FilesVerified`
- `UpdateTypes.cs` → the progress, error and result types and `ILauncherInstaller`

### Server Architecture

The server is an ASP.NET Core minimal API application with the following components:

**Core Files:**
- **Program.cs**: Main entry point and endpoint configuration
  - Loads settings from `settings.ini` using `IniConfigProvider`
  - Configures Kestrel server, CORS, logging, and middleware
  - Defines two HTTP endpoints (see below)
  - Registers `CacheService` as a background service

- **ServerSettings.cs**: Configuration model with properties for all settings
  - Port, hostname, files directory, cache settings
  - Security settings (CORS, path traversal protection, max file size)
  - Logging settings (log level, file path, request logging)
  - Performance settings (buffer size, compression, concurrent downloads)

- **IniConfigProvider.cs**: INI file parser
  - Reads and parses `settings.ini` file
  - Maps sections and key-value pairs to `ServerSettings` properties
  - Creates default `settings.ini` if missing
  - Handles type conversion and validation

- **CacheService.cs**: Background service (implements `BackgroundService`)
  - Generates cache immediately on startup
  - Watches `FilesDirectory` (`WatchFilesDirectory`) and rebuilds `FileSettleTime` seconds after a change (files still being written are rechecked until they settle)
  - Rebuilds every `CacheRegenerationInterval` seconds as a fallback, and again after `FileSettleTime` when files were skipped
  - Writes cache atomically (temp file + rename), and only when the list changed

- **FileListBuilder.cs**: Builds the file list (`name`, `md5`, `size`)
  - Remembers each file's size, modified time and MD5, so only new or changed files are hashed
  - Leaves out files modified within `FileSettleTime`, open for writing elsewhere, or changed while being hashed
  - Computes MD5 by streaming files (not loading into memory); file access goes through `IFileSystem` so tests use `MockFileSystem`

- **FileLoggerProvider.cs**: Appends log lines to `LogFilePath` when it is set

**HTTP Endpoints:**
- **GET /**: Returns JSON array of all files in `files/` directory with their MD5 hashes and sizes in bytes (`[{"name","md5","size"}]`)
  - Content-Type: `application/json`
  - CORS: Configurable via `CorsAllowedOrigins` setting
  - Returns cached data from `jsoncache.json`
  - Returns empty array if cache doesn't exist

- **GET /file/{**path}**: Streams file content from `files/` directory
  - Content-Type: `application/octet-stream`
  - CORS: Configurable via `CorsAllowedOrigins` setting
  - Supports range requests (partial downloads/resume)
  - Path traversal protection enabled by default
  - Concurrent download limiting via semaphore
  - Returns 404 if file doesn't exist
  - Returns 413 if file exceeds `MaxFileSize` limit

**Security Features:**
- Path traversal protection prevents access outside `files/` directory
- Configurable max file size to prevent abuse
- Concurrent download limiting to prevent resource exhaustion
- CORS policy configurable per deployment

**Performance Features:**
- Files streamed to clients (not loaded into memory)
- Response compression (gzip/brotli) for JSON responses
- Async/await throughout for scalability
- Cache regeneration in background thread doesn't block requests

## Customization Points

Branding/configuration is in `src/Client/Config/LauncherConfig.cs`, TazUO launcher settings in `src/Client/Config/TazUOLauncherConfig.cs`, and on-screen text in `src/Client/Config/Strings.cs`:
- `Title`, `Subtitle`: Header text. `AppDataFolder` (where player settings are saved) follows `Title`, so forks with different titles don't share settings
- Exe name: `<AssemblyName>` in `src/Client/FileUpdaterClient.csproj`
- `TitleColor`, `SubtitleColor`: Hex color strings for text
- `DefaultTextColor`, `ProgressBarBackground`: Brush colors
- `TotalProgressColor` (blue bar, progress across all files), `FileProgressColor` (red bar, current file download)
- `Links`: Top navigation links (`NavLink(label, url)`); `NavLink.VerifyAction` as the target re-runs the file check
- `TazUOLauncherConfig.Enabled`: When false, the TazUO launcher is never downloaded, there is no Play Now button, and the play warning option is hidden from Settings
- `DownloadButton`, `PlayText`: Center button; shows `DownloadButton` while updates are waiting, then `PlayText`, which opens the TazUO launcher once it is installed. If the files weren't fully verified it first asks the player to confirm (`UnverifiedTitle`, `UnverifiedMessage`)
- Player settings (cog button, modal `SettingsDialog`): install directory (saved by `InstallLocation.cs`; changing it re-checks the new folder, or restarts the updater if a check or download is running), plus verify files on launch and warn before playing with unverified files (saved to `%AppData%/<AppDataFolder>/settings.json` by `Preferences.cs`)
- `UpdateUrl`: Server endpoint (must include trailing slash if using path segments)
- `Finished`, `ReqFileList`, `ComparingFiles`, `DownloadingFiles`: Status messages (support `string.Format` placeholders)
- Error messages: `ConError`, `BadData`, `UnknownError`, `FileFailedError`, `FileLockedError`. After a failed check or failed files, the status reads `CheckFailed` or `FinishedWithFailures` and a Retry button (`RetryText`) appears next to the error
- `KeepLocalFiles` (`LauncherConfig`): name patterns like `*.cfg` for files players change themselves; downloaded when missing, never replaced

Visual assets:
- `src/Client/Assets/background.png`: Background image (window is 900x675, borderless)
- `src/Client/Assets/icon.ico`: Application icon

### Server Configuration

All server configuration is in `src/Server/settings.ini`:

**Server Section:**
- `Port`: Server port (default: 8080)
- `Hostname`: Bind address (empty = all interfaces, "localhost" = local only)
- `MaxConcurrentDownloads`: Concurrent download limit (default: 50, 0 = unlimited)

**Files Section:**
- `FilesDirectory`: Directory containing files to serve (default: ./files/)
- `CacheFileName`: Cache file name (default: jsoncache.json)
- `WatchFilesDirectory`: Rebuild the file list shortly after files change (default: true)
- `FileSettleTime`: Seconds a file must go unmodified before it is published (default: 5)
- `CacheRegenerationInterval`: Full rebuild interval in seconds, as a fallback to watching (default: 3600, 0 = disable)

**Security Section:**
- `CorsAllowedOrigins`: CORS origins (default: *, semicolon-separated for multiple)
- `EnablePathTraversalProtection`: Path traversal protection (default: true)
- `MaxFileSize`: Maximum file size in bytes (default: 0 = unlimited)

**Logging Section:**
- `LogLevel`: Minimum log level (default: Information)
- `LogFilePath`: Log file path, relative to the server (empty = console only)
- `EnableRequestLogging`: Log one line per request (default: true)

**Performance Section:**
- `EnableCompression`: Enable gzip/brotli compression (default: true)

## Key Implementation Details

### Threading and Concurrency
- Uses `WORKER_COUNT = 2` parallel workers for both comparing and downloading
- `ConcurrentQueue<FileEntry>` for thread-safe file queuing
- `UpdateService` raises events on worker threads; `MainViewModel` marshals them with `Dispatcher.UIThread.Post()`
- Cancellation support via `CancellationToken`

### File Download Strategy
- 81920-byte buffer size for streaming downloads
- Up to 5 attempts per file, retried in the same worker with a 1s, 2s, 4s, 8s backoff (`UpdateService.RetryDelay`, zero in tests). A file another program has open (the game) isn't retried; it's reported with `FileLockedError`
- When the server's list includes `size`, progress, the status text and the time left are by bytes (`DownloadingBytes`); otherwise by file count (`DownloadingFiles`). `Units` formats sizes, speeds and durations
- Download speed calculation based on cumulative bytes/time across all workers
- UI updates throttled to 0.5-second intervals during downloads
- Automatic directory creation for nested paths

### HTTP Client Configuration
- Initial connection timeout: 5 seconds (for file list retrieval)
- Download timeout: 15 minutes (`FileServerClient` keeps one `HttpClient` for each)

### MD5 Comparison
- Server computes MD5 on startup and caches in `jsoncache.json`, re-hashing only files whose size or modified time changed
- Client compares each local file during the comparison phase: a size different from the server's `size` (when sent) means changed without hashing; otherwise the MD5 comes from `HashCache` (`.launcher-hashes.json` in the install folder) when the file's size and modified time are unchanged since it was last hashed, and is computed otherwise
- Files are queued for download if MD5 differs or file doesn't exist, except `KeepLocalFiles` matches, which are only downloaded when missing

## Common Development Scenarios

### Changing Server URL
Edit `LauncherConfig.UpdateUrl` in `src/Client/Config/LauncherConfig.cs`. Ensure URL includes protocol and port if non-standard.

### Adjusting Worker Count
Modify the `WORKER_COUNT` constant in `src/Client/Updating/UpdateService.cs`. Higher values increase parallelism but may stress server.

### Modifying UI Text
All user-facing strings are in `src/Client/Config/Strings.cs`. Messages using format placeholders (`{0}`, `{1}`) correspond to:
- `ComparingFiles`: `{0}` = current file count, `{1}` = total files
- `DownloadingFiles`: `{0}` = current file count, `{1}` = total files, `{2}` = download speed

### Changing Server Settings
Edit `settings.ini` in `src/Server/` directory and restart the server. All settings take effect on restart. The server automatically creates `files/` directory on first run.

### Testing Server Changes
Place test files in the `files/` directory. They appear in the file list about `FileSettleTime` seconds after they stop changing.

## Deployment Notes

- This repo is meant to be forked per server: forks edit the files under `src/Client/Config/`, the exe name and the assets, then rebuild. Placeholder values in those files are expected, not bugs. The README has the checklist for forks
- The client can live anywhere: game files go into `LauncherConfig.DefaultInstallFolder` (`Client`) next to the exe, or the folder the player picked in Settings
- Client creates subdirectories automatically if server provides paths like `maps/map0.mul`
- Server `files/` directory structure is mirrored on client side
