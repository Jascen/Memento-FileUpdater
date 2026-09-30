using FileUpdaterPackages;
using FileUpdaterServer;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

// Load configuration from settings.ini
var configProvider = new IniConfigProvider("settings.ini");
var settings = configProvider.LoadSettings();

// Resolve relative paths to be relative to executable location, not current working directory
var exeDirectory = AppContext.BaseDirectory;
if (!Path.IsPathRooted(settings.FilesDirectory))
{
    settings.FilesDirectory = Path.GetFullPath(Path.Combine(exeDirectory, settings.FilesDirectory));
}
if (!Path.IsPathRooted(settings.PackagesDirectory))
{
    settings.PackagesDirectory = Path.GetFullPath(Path.Combine(exeDirectory, settings.PackagesDirectory));
}
if (!Path.IsPathRooted(settings.CacheFileName))
{
    settings.CacheFileName = Path.GetFullPath(Path.Combine(exeDirectory, settings.CacheFileName));
}
if (!string.IsNullOrEmpty(settings.LogFilePath) && !Path.IsPathRooted(settings.LogFilePath))
{
    settings.LogFilePath = Path.GetFullPath(Path.Combine(exeDirectory, settings.LogFilePath));
}

// Configure Kestrel server
builder.WebHost.ConfigureKestrel(options =>
{
    if (string.IsNullOrEmpty(settings.Hostname))
    {
        options.ListenAnyIP(settings.Port);
    }
    else
    {
        options.Listen(System.Net.IPAddress.Parse(settings.Hostname), settings.Port);
    }
});

// Add services
builder.Services.AddSingleton(settings);
builder.Services.AddHostedService<CacheService>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (settings.CorsAllowedOrigins == "*")
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            var origins = settings.CorsAllowedOrigins.Split(';', StringSplitOptions.RemoveEmptyEntries);
            policy.WithOrigins(origins);
        }
        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

// Add response compression if enabled
if (settings.EnableCompression)
{
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<GzipCompressionProvider>();
        options.Providers.Add<BrotliCompressionProvider>();
    });
}

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
    options.IncludeScopes = false;
});
if (!string.IsNullOrEmpty(settings.LogFilePath))
{
    builder.Logging.AddProvider(new FileLoggerProvider(settings.LogFilePath));
}

// Set minimum log level from settings
if (Enum.TryParse<LogLevel>(settings.LogLevel, ignoreCase: true, out var logLevel))
{
    builder.Logging.SetMinimumLevel(logLevel);
}

// Filter out noisy ASP.NET Core logs
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var app = builder.Build();

// Ensure files directory exists
Directory.CreateDirectory(settings.FilesDirectory);
Directory.CreateDirectory(settings.PackagesDirectory);

// Use middleware
app.UseCors();

if (settings.EnableCompression)
{
    app.UseResponseCompression();
}

// Path normalization middleware - handle double slashes from client URLs
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path != null && path.Contains("//"))
    {
        // Replace consecutive slashes with a single slash
        var normalizedPath = System.Text.RegularExpressions.Regex.Replace(path, "/+", "/");
        context.Request.Path = normalizedPath;
    }
    await next();
});

// Request logging middleware
if (settings.EnableRequestLogging)
{
    app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Request: {Method} {Path} from {IP}",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress);
        await next();
    });
}

// Create semaphore for concurrent download limiting
var downloadSemaphore = settings.MaxConcurrentDownloads > 0
    ? new SemaphoreSlim(settings.MaxConcurrentDownloads)
    : null;

var fileHandler = new FileRequestHandler(settings, downloadSemaphore,
    app.Services.GetRequiredService<ILogger<FileRequestHandler>>());

// File download middleware - handle /file/* requests (game files)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/file", out var remaining))
    {
        await fileHandler.HandleAsync(context, settings.FilesDirectory, remaining.Value?.TrimStart('/') ?? "");
        return;
    }

    await next();
});

// Package middleware - handle /packages/* requests (launcher and client packages).
// manifest.json and manifest.sig are made and signed offline by the PackageSigner tool, the server only hands them out
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/packages", out var remaining))
    {
        var name = remaining.Value?.TrimStart('/') ?? "";
        if (name is PackageManifest.ManifestFileName or PackageManifest.SignatureFileName)
        {
            //Always fetched fresh, a stale manifest would hide a new release
            context.Response.Headers.CacheControl = "no-cache";
            await fileHandler.HandleAsync(context, settings.PackagesDirectory, name,
                name == PackageManifest.ManifestFileName ? "application/json" : "text/plain");
        }
        else if (name.StartsWith("file/") && !name["file/".Length..].Contains('/'))
        {
            //Packages sit directly in the packages folder, no subfolders
            await fileHandler.HandleAsync(context, settings.PackagesDirectory, name["file/".Length..]);
        }
        else
        {
            context.Response.StatusCode = 404;
        }
        return;
    }

    await next();
});

// Test endpoint
app.MapGet("/test", () => "Server is working!");

// Endpoint 1: GET / - Return cached file list JSON
app.MapGet("/", async (HttpContext context, ServerSettings settings, ILogger<Program> logger) =>
{
    var cacheFile = settings.CacheFileName;

    if (!File.Exists(cacheFile))
    {
        logger.LogWarning("Cache file not found, returning empty array");
        return Results.Json(Array.Empty<object>());
    }

    try
    {
        var json = await File.ReadAllTextAsync(cacheFile);
        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error reading cache file");
        return Results.Problem("Error reading file list");
    }
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("========================================");
logger.LogInformation("SimpleFileUpdater Server Starting");
logger.LogInformation("========================================");
logger.LogInformation("Port: {Port}", settings.Port);
logger.LogInformation("Hostname: {Hostname}", string.IsNullOrEmpty(settings.Hostname) ? "All interfaces" : settings.Hostname);
logger.LogInformation("Files directory: {Dir}", Path.GetFullPath(settings.FilesDirectory));
logger.LogInformation("Packages directory: {Dir}", settings.PackagesDirectory);
logger.LogInformation("Cache file: {Cache}", settings.CacheFileName);
logger.LogInformation("Watch files directory: {Enabled}", settings.WatchFilesDirectory);
logger.LogInformation("Cache interval: {Interval} seconds", settings.CacheRegenerationInterval);
if (!string.IsNullOrEmpty(settings.LogFilePath))
{
    logger.LogInformation("Log file: {LogFile}", settings.LogFilePath);
}
logger.LogInformation("Max concurrent downloads: {Max}", settings.MaxConcurrentDownloads > 0 ? settings.MaxConcurrentDownloads : "Unlimited");
logger.LogInformation("Path traversal protection: {Enabled}", settings.EnablePathTraversalProtection);
logger.LogInformation("Compression: {Enabled}", settings.EnableCompression);
logger.LogInformation("========================================");

app.Run();
