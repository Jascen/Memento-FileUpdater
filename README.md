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


## Building and Customizing the client
1. Clone the repo
2. Change `src/Client/Assets/background.png` to your own background. The window is 900x675; other sizes are scaled to fill it.
3. Change `src/Client/Assets/icon.ico` to your own icon. Size isn't too important.
4. Open `src/Client/Config/LauncherConfig.cs` and update with your info (title, server URL, top links, bar colors), `src/Client/Config/TazUOLauncherConfig.cs` to turn the TazUO launcher on or off and set its server profiles, and `src/Client/Config/Strings.cs` to change any on-screen text.
5. Navigate to the client directory: `cd src/Client`
6. Run in terminal/command prompt/powershell etc: `dotnet build -c Release`  

### Build information
- This is cross platform, to build on other platforms use (from `src/Client` directory):
- Linux: `dotnet publish -c Release -r linux-x64`
- MacOS: `dotnet publish -c Release -r osx-x64` *May need to be ran on a Mac*
- Windows: `dotnet publish -c Release -r win-x64`
- Each platform needs its own release.
- The final output will be in `src/Client/bin/Release/net9.0/{platform}/publish/` (These are the only files you need to distribute to players)

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
- **EnableCompression**: Enable gzip/brotli compression (default: true)
- And more... see settings.ini for full configuration options 

# Client Info
- After building the project, place the output files in the `same` directory you'd like the server files to be downloaded to.  
- - For example:
```
/FileUpdaterClient.exe
/map0.mul
/map1.mul
```
- Run `FileUpdaterClient.exe`  
- This will check your files md5 vs the server's md5 and download any differing or non-existent files.  
