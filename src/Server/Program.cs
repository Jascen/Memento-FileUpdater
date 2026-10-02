using FileUpdaterPackages;
using FileUpdaterServer;
using Microsoft.AspNetCore.ResponseCompression;

// appsettings.json is read from next to the server, wherever it is started from
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Every section of FileUpdater fills the same settings. A misspelled setting stops the server instead of being ignored
var settings = new ServerSettings();
foreach (var section in builder.Configuration.GetSection("FileUpdater").GetChildren())
    section.Bind(settings, options => options.ErrorOnUnknownConfiguration = true);

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
if (!string.IsNullOrEmpty(settings.LogFilePath) && !Path.IsPathRooted(settings.LogFilePath))
{
    settings.LogFilePath = Path.GetFullPath(Path.Combine(exeDirectory, settings.LogFilePath));
}

// Configure Kestrel server. Endpoints in a Kestrel section (e.g. https with a certificate) take the place of Port and Hostname
var kestrelEndpoints = builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
builder.WebHost.ConfigureKestrel(options =>
{
    if (kestrelEndpoints)
    {
        return;
    }

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
builder.Services.AddSingleton<FileListCache>();
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

// Game files: GET /file/{path}
app.MapGet("/file/{**path}", (HttpContext context, string path) =>
    fileHandler.HandleAsync(context, settings.FilesDirectory, path));

// Launcher and TazUO packages. manifest.json and manifest.sig are made and signed offline by the PackageSigner tool,
// the server only hands them out. They are always fetched fresh, a stale manifest would hide a new release
app.MapGet("/packages/" + PackageManifest.ManifestFileName, (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    return fileHandler.HandleAsync(context, settings.PackagesDirectory, PackageManifest.ManifestFileName, "application/json");
});

app.MapGet("/packages/" + PackageManifest.SignatureFileName, (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    return fileHandler.HandleAsync(context, settings.PackagesDirectory, PackageManifest.SignatureFileName, "text/plain");
});

// Packages sit directly in the packages folder, no subfolders
app.MapGet("/packages/file/{name}", (HttpContext context, string name) =>
    fileHandler.HandleAsync(context, settings.PackagesDirectory, name));

// Test endpoint
app.MapGet("/test", () => "Server is working!");

// GET / - the file list JSON, kept in memory by CacheService
app.MapGet("/", (HttpContext context, FileListCache cache) =>
{
    if (cache.Json is { } json)
        return Results.Content(json, "application/json");

    //Still hashing the files directory after a start
    context.Response.Headers.RetryAfter = "5";
    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("========================================");
logger.LogInformation("SimpleFileUpdater Server Starting");
logger.LogInformation("========================================");
if (kestrelEndpoints)
{
    logger.LogInformation("Listening on the endpoints in the Kestrel section");
}
else
{
    logger.LogInformation("Port: {Port}", settings.Port);
    logger.LogInformation("Hostname: {Hostname}", string.IsNullOrEmpty(settings.Hostname) ? "All interfaces" : settings.Hostname);
}
logger.LogInformation("Files directory: {Dir}", Path.GetFullPath(settings.FilesDirectory));
logger.LogInformation("Packages directory: {Dir}", settings.PackagesDirectory);
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
