using System.IO.Abstractions;
using System.Text.Json;
using System.Threading.Channels;

namespace FileUpdaterServer;

//The file list served at GET /, as JSON. Null until the first build after startup has finished
public class FileListCache
{
    private volatile string? _json;

    public string? Json
    {
        get => _json;
        set => _json = value;
    }
}

//Keeps the file list in step with the files directory: builds it on startup, again shortly after
//anything in the directory changes, and on the CacheRegenerationInterval as a safety net
public class CacheService : BackgroundService
{
    private readonly ServerSettings _settings;
    private readonly FileListCache _cache;
    private readonly ILogger<CacheService> _logger;
    private readonly FileListBuilder _builder;
    private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();

    public CacheService(ServerSettings settings, FileListCache cache, ILogger<CacheService> logger)
    {
        _settings = settings;
        _cache = cache;
        _logger = logger;
        _builder = new FileListBuilder(new FileSystem(), settings.FilesDirectory,
            TimeSpan.FromSeconds(settings.FileSettleTime), TimeProvider.System);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_settings.FilesDirectory);

        using var watcher = _settings.WatchFilesDirectory ? StartWatching() : null;
        if (watcher == null)
        {
            _logger.LogInformation("Not watching the files directory; the file list updates every {Interval} seconds",
                _settings.CacheRegenerationInterval);
        }

        var interval = _settings.CacheRegenerationInterval > 0
            ? TimeSpan.FromSeconds(_settings.CacheRegenerationInterval)
            : Timeout.InfiniteTimeSpan;
        var settleTime = TimeSpan.FromSeconds(Math.Max(1, _settings.FileSettleTime));

        try
        {
            var skipped = await RegenerateCacheAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                //Skipped files are checked again once they have had time to finish copying
                if (await WaitForChangeAsync(skipped ? settleTime : interval, stoppingToken))
                {
                    //A rebuild during a copy only skips the file, so a short pause is enough to batch the events
                    await Task.Delay(settleTime, stoppingToken);
                    while (_changes.Reader.TryRead(out _))
                    {
                    }
                }

                skipped = await RegenerateCacheAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            //Stopping
        }
    }

    private FileSystemWatcher? StartWatching()
    {
        try
        {
            var watcher = new FileSystemWatcher(_settings.FilesDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += (_, _) => _changes.Writer.TryWrite(true);
            watcher.Changed += (_, _) => _changes.Writer.TryWrite(true);
            watcher.Deleted += (_, _) => _changes.Writer.TryWrite(true);
            watcher.Renamed += (_, _) => _changes.Writer.TryWrite(true);
            //Too many changes at once: rebuild anyway, the builder reads the whole directory
            watcher.Error += (_, _) => _changes.Writer.TryWrite(true);
            watcher.EnableRaisingEvents = true;

            _logger.LogInformation("Watching {Directory} for changes", _settings.FilesDirectory);
            return watcher;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't watch {Directory}; the file list updates every {Interval} seconds",
                _settings.FilesDirectory, _settings.CacheRegenerationInterval);
            return null;
        }
    }

    //True when the directory changed within the wait, false when the wait ran out
    private async Task<bool> WaitForChangeAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(wait);

        try
        {
            await _changes.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }

        while (_changes.Reader.TryRead(out _))
        {
        }
        return true;
    }

    //Returns whether any files were skipped because they were still being written
    private async Task<bool> RegenerateCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _builder.BuildAsync(cancellationToken);

            foreach (var (name, error) in result.Errors)
                _logger.LogError(error, "Couldn't read {File}", name);
            if (result.Skipped.Count > 0)
                _logger.LogInformation("Waiting for {Count} file(s) still being written, e.g. {File}", result.Skipped.Count, result.Skipped[0]);

            var json = JsonSerializer.Serialize(result.Entries);
            if (json == _cache.Json)
            {
                _logger.LogDebug("File list unchanged ({Count} files)", result.Entries.Count);
                return result.Skipped.Count > 0;
            }

            _cache.Json = json;

            _logger.LogInformation("File list updated: {Count} files, {Hashed} hashed", result.Entries.Count, result.Hashed);
            return result.Skipped.Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error regenerating cache");
            return false;
        }
    }
}
