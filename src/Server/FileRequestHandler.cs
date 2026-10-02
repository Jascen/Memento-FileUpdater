namespace FileUpdaterServer;

//Serves one file from a root directory: path checks, size limit, download slots and range requests.
//Shared by /file (game files) and /packages (launcher and TazUO packages), so both are limited the same way.
public class FileRequestHandler(ServerSettings settings, SemaphoreSlim? downloadSlots, ILogger<FileRequestHandler> logger)
{
    //Returns the full path when relativePath stays inside rootDirectory
    public static bool TryResolve(string rootDirectory, string relativePath, bool traversalProtection, out string fullPath)
    {
        fullPath = string.Empty;

        //Only a whole ".." segment climbs out, a name like notes..txt is fine
        if (traversalProtection && (relativePath.Split('/', '\\').Contains("..") || Path.IsPathRooted(relativePath)))
            return false;

        var combined = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = combined;
        return true;
    }

    //Sets the response status (404 for anything missing or outside the root, 413 for files over MaxFileSize)
    public async Task HandleAsync(HttpContext context, string rootDirectory, string relativePath, string contentType = "application/octet-stream")
    {
        if (!TryResolve(rootDirectory, relativePath, settings.EnablePathTraversalProtection, out var fullPath))
        {
            logger.LogWarning("Path traversal attempt blocked: {Path}", relativePath);
            context.Response.StatusCode = 404;
            return;
        }

        if (!File.Exists(fullPath))
        {
            logger.LogDebug("File not found: {Path}", relativePath);
            context.Response.StatusCode = 404;
            return;
        }

        if (settings.MaxFileSize > 0)
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > settings.MaxFileSize)
            {
                logger.LogWarning("File too large: {Path} ({Size} bytes)", relativePath, fileInfo.Length);
                context.Response.StatusCode = 413;
                return;
            }
        }

        //Wait for a free download slot if concurrent downloads are limited
        if (downloadSlots != null)
        {
            try
            {
                await downloadSlots.WaitAsync(context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        try
        {
            logger.LogDebug("Serving file: {Path}", relativePath);
            //Range processing lets clients resume partial downloads
            await Results.File(fullPath, contentType, enableRangeProcessing: true).ExecuteAsync(context);
        }
        finally
        {
            downloadSlots?.Release();
        }
    }
}
