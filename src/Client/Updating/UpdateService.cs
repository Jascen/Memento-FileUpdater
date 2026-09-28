using System.Collections.Concurrent;
using System.Diagnostics;

namespace FileUpdaterClient.Updating;

//Checks the install folder against the server and downloads what differs. Knows nothing about the UI: it reports
//through the events below, which fire on background threads, and each run returns an UpdateResult.
//Not reentrant, the caller runs one CheckAsync or DownloadAsync at a time.
public class UpdateService
{
    private const int WORKER_COUNT = 2;
    private const int MAX_ATTEMPTS = 5;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(0.5);

    private readonly FileServerClient _server;
    private readonly string _installPath;
    private readonly ILauncherInstaller? _launcher;

    private readonly ConcurrentQueue<FileEntry> _toCompare = new();
    private readonly ConcurrentQueue<FileEntry> _toDownload = new();
    private readonly Stopwatch _downloadClock = new(); //Wall-clock time of the download phase, shared by all workers
    private CancellationTokenSource _cancellation = new();
    private bool _needsFileList = true; //A cancelled or failed run leaves the queues incomplete, so the next run starts from a fresh file list
    private volatile bool _stopAtFirstDifference;
    private int _compareTotal;
    private int _downloadTotal;
    private int _completedDownloads;
    private int _failedDownloads;
    private long _totalBytesDownloaded;
    private DateTime _lastProgressTime = DateTime.MinValue;

    public event Action<UpdateProgress>? ProgressChanged;
    public event Action<FileProgress>? FileProgressChanged;
    public event Action<UpdateErrorInfo>? ErrorOccurred;

    //True after a run finished with every file matching the server
    public bool FilesVerified { get; private set; }

    //launcher is null when the TazUO launcher is turned off
    public UpdateService(FileServerClient server, string installPath, ILauncherInstaller? launcher)
    {
        _server = server;
        _installPath = installPath;
        _launcher = launcher;
    }

    public void Cancel() => _cancellation.Cancel();

    //Checks the server's file list against local files, stopping at the first difference. Nothing is downloaded here
    public Task<UpdateResult> CheckAsync() => RunAsync(async token =>
    {
        _needsFileList = true;
        if (!await LoadFileList(token)) return UpdateResult.Failed;

        //Only need to know whether anything changed, the rest is checked by DownloadAsync
        await CompareFiles(stopAtFirstDifference: true);
        token.ThrowIfCancellationRequested();

        if (!_toDownload.IsEmpty) return UpdateResult.UpdatesReady;
        if (_launcher is { IsInstalled: false }) return UpdateResult.LauncherReady;

        return await Finish(token);
    });

    //Checks the files CheckAsync skipped and downloads everything that differs
    public Task<UpdateResult> DownloadAsync() => RunAsync(async token =>
    {
        if (_needsFileList && !await LoadFileList(token)) return UpdateResult.Failed;

        await CompareFiles(stopAtFirstDifference: false);
        await DownloadFiles();
        return await Finish(token);
    });

    private async Task<UpdateResult> RunAsync(Func<CancellationToken, Task<UpdateResult>> run)
    {
        if (_cancellation.IsCancellationRequested) _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        FilesVerified = false;

        try
        {
            var result = await run(token);
            if (token.IsCancellationRequested) result = UpdateResult.Cancelled;
            if (result is UpdateResult.Failed or UpdateResult.Cancelled) _needsFileList = true;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _needsFileList = true;
            return UpdateResult.Cancelled;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.Unknown));
            _needsFileList = true;
            return UpdateResult.Failed;
        }
    }

    private async Task<bool> LoadFileList(CancellationToken token)
    {
        _toCompare.Clear();
        _toDownload.Clear();
        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.RequestingFileList, 0, 0));

        try
        {
            foreach (var file in await _server.GetFileListAsync(_installPath, token))
                _toCompare.Enqueue(file);
        }
        catch (UpdateServerException e)
        {
            Console.WriteLine(e.Message);
            ErrorOccurred?.Invoke(new UpdateErrorInfo(e.Error));
            return false;
        }

        _compareTotal = _toCompare.Count;
        _needsFileList = false;
        return true;
    }

    private async Task<UpdateResult> Finish(CancellationToken token)
    {
        await SetUpLauncher(token);
        token.ThrowIfCancellationRequested();

        FilesVerified = Volatile.Read(ref _failedDownloads) == 0; //Every file matched the server or was downloaded
        return UpdateResult.Finished;
    }

    private async Task SetUpLauncher(CancellationToken token)
    {
        if (_launcher == null || token.IsCancellationRequested) return;

        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.InstallingLauncher, 0, 0));
        try
        {
            await _launcher.EnsureInstalledAsync(token);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            Console.WriteLine(e.ToString());
            ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.LauncherFailed));
        }
    }

    private async Task CompareFiles(bool stopAtFirstDifference)
    {
        if (_toCompare.IsEmpty) return;

        _stopAtFirstDifference = stopAtFirstDifference;
        ReportCompareProgress();

        var tasks = new List<Task>();
        for (int i = 0; i < WORKER_COUNT; i++)
        {
            tasks.Add(Task.Run(CompareWorker));
        }

        await Task.WhenAll(tasks);
    }

    private void CompareWorker()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested
               && !(_stopAtFirstDifference && !_toDownload.IsEmpty)
               && _toCompare.TryDequeue(out var file))
        {
            LocalFiles.TryGetLocalPath(_installPath, file.Name, out var fullPath);
            if (File.Exists(fullPath))
            {
                if (!file.Md5.Equals(LocalFiles.ComputeMd5(fullPath), StringComparison.OrdinalIgnoreCase))
                {
                    _toDownload.Enqueue(file);
                    Console.WriteLine($"[{file.Name}] does not match the version from the server, queued for download..");
                }
            }
            else
            {
                _toDownload.Enqueue(file);
                Console.WriteLine($"[{file.Name}] does not exist, queued for download..");
            }

            ReportCompareProgress();
        }
    }

    private void ReportCompareProgress() =>
        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.Comparing, _compareTotal - _toCompare.Count, _compareTotal));

    private async Task DownloadFiles()
    {
        _downloadTotal = _toDownload.Count;
        _completedDownloads = 0;
        _failedDownloads = 0;
        Interlocked.Exchange(ref _totalBytesDownloaded, 0);
        if (_downloadTotal == 0) return;

        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.Downloading, 0, _downloadTotal));

        _downloadClock.Restart();
        var tasks = new List<Task>();
        for (int i = 0; i < WORKER_COUNT; i++)
        {
            tasks.Add(Task.Run(DownloadWorker));
        }

        await Task.WhenAll(tasks);
    }

    private async Task DownloadWorker()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested && _toDownload.TryDequeue(out var file))
        {
            LocalFiles.TryGetLocalPath(_installPath, file.Name, out var filePath);
            Console.WriteLine($"Downloading [{file.Name}]...");
            FileProgressChanged?.Invoke(new FileProgress(file.Name, 0));

            for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
            {
                try
                {
                    LocalFiles.EnsureDirectory(filePath);
                    await _server.DownloadFileAsync(file, filePath,
                        (chunk, fileBytes, fileLength) => OnBytesDownloaded(file.Name, chunk, fileBytes, fileLength), token);
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                        return;

                    Console.WriteLine(ex.ToString());

                    if (attempt == MAX_ATTEMPTS)
                    {
                        Console.WriteLine($"Failed to download [{file.Name}] after {MAX_ATTEMPTS} attempts, skipping..");
                        ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.FileFailed, file.Name));
                        Interlocked.Increment(ref _failedDownloads);
                        break;
                    }

                    //Back off before retrying: 1s, 2s, 4s, 8s
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            if (token.IsCancellationRequested)
                return;

            Interlocked.Increment(ref _completedDownloads);

            // Final update after file is done
            FileProgressChanged?.Invoke(new FileProgress(file.Name, 100));
            ReportDownloadProgress();
        }
    }

    private void OnBytesDownloaded(string fileName, int chunk, long fileBytes, long? fileLength)
    {
        Interlocked.Add(ref _totalBytesDownloaded, chunk);

        // Limit updates to every 0.5 seconds
        if (DateTime.UtcNow - _lastProgressTime < ProgressInterval) return;
        _lastProgressTime = DateTime.UtcNow;

        FileProgressChanged?.Invoke(new FileProgress(fileName, fileLength > 0 ? fileBytes * 100.0 / fileLength.Value : 0));
        ReportDownloadProgress();
    }

    private void ReportDownloadProgress()
    {
        double elapsedSeconds = _downloadClock.Elapsed.TotalSeconds;
        double bytesPerSecond = elapsedSeconds > 0 ? Interlocked.Read(ref _totalBytesDownloaded) / elapsedSeconds : 0;
        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.Downloading,
            Volatile.Read(ref _completedDownloads), _downloadTotal, bytesPerSecond));
    }
}
