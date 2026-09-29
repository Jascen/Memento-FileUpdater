using System.Globalization;
using System.Reflection;

namespace FileUpdaterServer;

public class IniConfigProvider
{
    private readonly string _iniFilePath;

    public IniConfigProvider(string iniFilePath)
    {
        _iniFilePath = iniFilePath;
    }

    public ServerSettings LoadSettings()
    {
        var settings = new ServerSettings();

        if (!File.Exists(_iniFilePath))
        {
            Console.WriteLine($"Warning: settings.ini not found at {_iniFilePath}, using defaults");
            CreateDefaultIniFile();
            return settings;
        }

        try
        {
            var lines = File.ReadAllLines(_iniFilePath);
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("#"))
                    continue;

                // Handle sections [SectionName]
                if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                {
                    currentSection = trimmedLine.Substring(1, trimmedLine.Length - 2);
                    continue;
                }

                // Handle key=value pairs
                var separatorIndex = trimmedLine.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = trimmedLine.Substring(0, separatorIndex).Trim();
                    var value = trimmedLine.Substring(separatorIndex + 1).Trim();

                    SetProperty(settings, key, value);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing settings.ini: {ex.Message}");
            Console.WriteLine("Using default settings");
        }

        return settings;
    }

    private void SetProperty(ServerSettings settings, string propertyName, string value)
    {
        var property = typeof(ServerSettings).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property == null)
        {
            Console.WriteLine($"Warning: Unknown setting '{propertyName}', ignoring");
            return;
        }

        try
        {
            if (property.PropertyType == typeof(int))
            {
                property.SetValue(settings, int.Parse(value));
            }
            else if (property.PropertyType == typeof(long))
            {
                property.SetValue(settings, long.Parse(value));
            }
            else if (property.PropertyType == typeof(bool))
            {
                property.SetValue(settings, bool.Parse(value));
            }
            else if (property.PropertyType == typeof(string))
            {
                property.SetValue(settings, value);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to parse value for '{propertyName}': {ex.Message}");
        }
    }

    private void CreateDefaultIniFile()
    {
        var defaultIni = @"# SimpleFileUpdater Server Configuration
# Restart the server after making changes to this file.

[Server]
# Port number to listen on (default: 8080)
Port = 8080

# Hostname to bind to (empty = all interfaces, ""localhost"" = local only)
# Use ""0.0.0.0"" for all IPv4 or ""::"" for all IPv6
Hostname =

# Maximum concurrent file downloads (0 = unlimited, not recommended)
MaxConcurrentDownloads = 50

[Files]
# Directory containing files to serve (relative or absolute path)
# Created automatically if it doesn't exist
FilesDirectory = ./files/

# Name of the JSON cache file
CacheFileName = jsoncache.json

# Rebuild the file list shortly after files are added, changed or removed (default: true)
WatchFilesDirectory = true

# Seconds a file must go unmodified before it is published (default: 5)
# Files still being copied in, or open for writing, are left out until they are finished
FileSettleTime = 5

# Full rebuild interval in seconds, as a fallback to watching (default: 3600 = 1 hour)
# Set to 0 to disable periodic rebuilds
CacheRegenerationInterval = 3600

[Security]
# CORS allowed origins separated by semicolons (use * for all)
# Example: http://localhost:3000;https://example.com
CorsAllowedOrigins = *

# Enable path traversal protection (highly recommended)
# Prevents access to files outside FilesDirectory using .. in paths
EnablePathTraversalProtection = true

# Maximum file size to serve in bytes (0 = unlimited)
# Example: 1073741824 = 1 GB
MaxFileSize = 0

[Logging]
# Minimum log level: Trace, Debug, Information, Warning, Error, Critical
LogLevel = Information

# Log file path, relative to the server (empty = console only)
LogFilePath =

# Log one line per request (default: true)
EnableRequestLogging = true

[Performance]
# Enable response compression (gzip/brotli)
EnableCompression = true
";

        try
        {
            File.WriteAllText(_iniFilePath, defaultIni);
            Console.WriteLine($"Created default settings.ini at {_iniFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating default settings.ini: {ex.Message}");
        }
    }
}
