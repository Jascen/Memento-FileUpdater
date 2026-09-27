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
if (!Path.IsPathRooted(settings.CacheFileName))
{
    settings.CacheFileName = Path.GetFullPath(Path.Combine(exeDirectory, settings.CacheFileName));
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

// File download middleware - handle /file/* requests
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/file"))
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var path = context.Request.Path.Value?.Substring("/file/".Length) ?? "";

        logger.LogInformation("FILE MIDDLEWARE HIT: path={Path}, FilesDirectory={Dir}", path, settings.FilesDirectory);

        // Path traversal protection
        if (settings.EnablePathTraversalProtection)
        {
            if (path.Contains("..") || Path.IsPathRooted(path))
            {
                logger.LogWarning("Path traversal attempt blocked: {Path}", path);
                context.Response.StatusCode = 404;
                return;
            }
        }

        var fullPath = Path.Combine(settings.FilesDirectory, path);
        var normalizedPath = Path.GetFullPath(fullPath);
        var normalizedFilesDir = Path.GetFullPath(settings.FilesDirectory);

        if (!normalizedPath.StartsWith(normalizedFilesDir, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Path traversal attempt blocked: {Path} (normalized check)", path);
            context.Response.StatusCode = 404;
            return;
        }

        if (!File.Exists(fullPath))
        {
            logger.LogDebug("File not found: {Path}", path);
            context.Response.StatusCode = 404;
            return;
        }

        // Check file size if limit is set
        if (settings.MaxFileSize > 0)
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > settings.MaxFileSize)
            {
                logger.LogWarning("File too large: {Path} ({Size} bytes)", path, fileInfo.Length);
                context.Response.StatusCode = 413;
                return;
            }
        }

        logger.LogInformation("Serving file: {Path}", path);
        context.Response.ContentType = "application/octet-stream";
        await context.Response.SendFileAsync(fullPath);
        return;
    }

    await next();
});

// Create semaphore for concurrent download limiting
var downloadSemaphore = settings.MaxConcurrentDownloads > 0
    ? new SemaphoreSlim(settings.MaxConcurrentDownloads)
    : null;

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
logger.LogInformation("Cache file: {Cache}", settings.CacheFileName);
logger.LogInformation("Cache interval: {Interval} seconds", settings.CacheRegenerationInterval);
logger.LogInformation("Max concurrent downloads: {Max}", settings.MaxConcurrentDownloads > 0 ? settings.MaxConcurrentDownloads : "Unlimited");
logger.LogInformation("Path traversal protection: {Enabled}", settings.EnablePathTraversalProtection);
logger.LogInformation("Compression: {Enabled}", settings.EnableCompression);
logger.LogInformation("========================================");

app.Run();
