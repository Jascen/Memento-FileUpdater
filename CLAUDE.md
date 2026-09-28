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
│   │   ├── *.cs             # C# source files
│   │   ├── *.axaml          # Avalonia XAML UI files
│   │   ├── *.csproj         # Project file
│   │   ├── *.sln            # Solution file
│   │   └── resources/       # Visual assets (background.png, icon.ico)
│   └── Server/              # C# .NET server application
│       ├── Program.cs       # Main entry point with endpoints
│       ├── ServerSettings.cs    # Configuration model
│       ├── IniConfigProvider.cs # INI file parser
│       ├── CacheService.cs      # Background cache service
│       ├── FileUpdaterServer.csproj  # Project file
│       └── settings.ini     # Server configuration file
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
```

Output location: `src/Client/bin/Release/net9.0/{runtime}/publish/`

### Server (C# .NET)

Basic build:
```bash
cd src/Server
dotnet build -c Release
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

- **Entry Point**: `src/Client/Program.cs` → bootstraps Avalonia application
- **Application Lifecycle**: `src/Client/MainWindow.cs` (Application class) → creates main window (`Main.cs`)
- **Main Window**: `src/Client/Main.axaml.cs` + `Main.axaml` → initializes `MainViewModel` and triggers `UpdateHandler.HandleUpdates()`
- **View Model**: `src/Client/MainViewModel.cs` → implements `INotifyPropertyChanged` for data binding (progress, status text, error messages)
- **Update Logic**: `src/Client/UpdateHandler.cs` → core file synchronization logic
- **Configuration**: `src/Client/Settings.cs` → all customizable strings, colors, and server URL

### UpdateHandler Flow

`UpdateHandler.cs` orchestrates the update process in three phases:

1. **GetFileList()**: Fetches JSON array of `{name, md5}` from server root endpoint
2. **StartComparingFiles()**: Spawns `WORKER_COUNT` (2) workers that compare local file MD5s against server MD5s, queuing mismatches/missing files for download
3. **StartDownloading()**: Runs only after the player clicks the "Download updates" button (`DownloadUpdates()`); the launch check stops at the first changed or missing file, and the remaining files are compared after the click. Spawns `WORKER_COUNT` (2) workers that download queued files with retry logic (up to 5 attempts per file, with backoff)

All phases use `ConcurrentQueue` for thread-safe work distribution and `Dispatcher.UIThread.Post()` to update UI from background threads.

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
  - Regenerates cache periodically based on `CacheRegenerationInterval` setting
  - Uses `SemaphoreSlim` for thread-safe cache regeneration
  - Computes MD5 by streaming files (not loading into memory)
  - Writes cache atomically (temp file + rename) to prevent corruption

**HTTP Endpoints:**
- **GET /**: Returns JSON array of all files in `files/` directory with their MD5 hashes
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
- Configurable streaming buffer size
- Async/await throughout for scalability
- Cache regeneration in background thread doesn't block requests

## Customization Points

All branding/configuration is in `src/Client/Settings.cs`:
- `Title`, `Subtitle`: Header text
- `TitleColor`, `SubtitleColor`: Hex color strings for text
- `DefaultTextColor`, `ProgressBarBackground`, `ProgressBarForeground`: Brush colors
- `UpdateUrl`: Server endpoint (must include trailing slash if using path segments)
- `Finished`, `ReqFileList`, `ComparingFiles`, `DownloadingFiles`: Status messages (support `string.Format` placeholders)
- Error messages: `ConError`, `BadData`, `UnknownError`, `FileFailedError`

Visual assets:
- `src/Client/resources/background.png`: 800x450 background image
- `src/Client/resources/icon.ico`: Application icon

### Server Configuration

All server configuration is in `src/Server/settings.ini`:

**Server Section:**
- `Port`: Server port (default: 8080)
- `Hostname`: Bind address (empty = all interfaces, "localhost" = local only)
- `MaxConcurrentDownloads`: Concurrent download limit (default: 50, 0 = unlimited)

**Files Section:**
- `FilesDirectory`: Directory containing files to serve (default: ./files/)
- `CacheFileName`: Cache file name (default: jsoncache.json)
- `CacheRegenerationInterval`: Cache regeneration interval in seconds (default: 3600, 0 = disable)

**Security Section:**
- `CorsAllowedOrigins`: CORS origins (default: *, semicolon-separated for multiple)
- `EnablePathTraversalProtection`: Path traversal protection (default: true)
- `MaxFileSize`: Maximum file size in bytes (default: 0 = unlimited)

**Logging Section:**
- `LogLevel`: Minimum log level (default: Information)
- `LogFilePath`: Log file path (empty = console only)
- `EnableRequestLogging`: Log each request (default: true)

**Performance Section:**
- `StreamBufferSize`: Streaming buffer size in bytes (default: 81920)
- `EnableCompression`: Enable gzip/brotli compression (default: true)

## Key Implementation Details

### Threading and Concurrency
- Uses `WORKER_COUNT = 2` parallel workers for both comparing and downloading
- `ConcurrentQueue<FileEntry>` for thread-safe file queuing
- UI updates via `Dispatcher.UIThread.Post()` to marshal to UI thread
- Cancellation support via `CancellationToken`

### File Download Strategy
- 81920-byte buffer size for streaming downloads
- Up to 5 attempts per file, retried in the same worker with a 1s, 2s, 4s, 8s backoff
- Download speed calculation based on cumulative bytes/time across all workers
- UI updates throttled to 0.5-second intervals during downloads
- Automatic directory creation for nested paths

### HTTP Client Configuration
- Initial connection timeout: 5 seconds (for file list retrieval)
- Download timeout: 15 minutes (client recreated between phases)

### MD5 Comparison
- Server computes MD5 on startup and caches in `jsoncache.json`
- Client computes MD5 for each local file during comparison phase
- Files are queued for download if MD5 differs or file doesn't exist

## Common Development Scenarios

### Changing Server URL
Edit `Settings.UpdateUrl` in `src/Client/Settings.cs`. Ensure URL includes protocol and port if non-standard.

### Adjusting Worker Count
Modify `WORKER_COUNT` constant in `src/Client/UpdateHandler.cs` (line 13). Higher values increase parallelism but may stress server.

### Modifying UI Text
All user-facing strings are in `src/Client/Settings.cs`. Messages using format placeholders (`{0}`, `{1}`) correspond to:
- `ComparingFiles`: `{0}` = current file count, `{1}` = total files
- `DownloadingFiles`: `{0}` = current file count, `{1}` = total files, `{2}` = download speed

### Changing Server Settings
Edit `settings.ini` in `src/Server/` directory and restart the server. All settings take effect on restart. The server automatically creates `files/` directory on first run.

### Testing Server Changes
Place test files in the `files/` directory. The cache will regenerate automatically based on `CacheRegenerationInterval` (default: 1 hour), or restart the server for immediate cache refresh.

## Deployment Notes

- Client executable should be placed in the **same directory** where downloaded files will be stored (not a subdirectory)
- Client creates subdirectories automatically if server provides paths like `maps/map0.mul`
- Server `files/` directory structure is mirrored on client side
