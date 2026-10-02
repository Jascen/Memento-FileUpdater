using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

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
    private readonly LocalFiles _localFiles;
    private readonly string _installPath;
    private readonly IPackageUpdater? _packages;
    private readonly IReadOnlyCollection<string> _keepLocalFiles;
    private readonly IReadOnlyCollection<string> _reservedPaths;
    private readonly HashCache _hashes;

    private readonly ConcurrentQueue<FileEntry> _toCompare = new();
    private readonly ConcurrentQueue<FileEntry> _toDownload = new();
    private List<FileEntry> _fileList = new(); //The server's list as last fetched, apart from reserved paths
    private string[] _ignored = []; //Files and folders the player's ignore list covered in the last run
    private readonly Stopwatch _downloadClock = new(); //Wall-clock time of the download phase, shared by all workers
    private CancellationTokenSource _cancellation = new();
    private PackageUpdates _packageUpdates = PackageUpdates.None; //What the last check found out of date in the signed manifest
    private bool _packagesApplied; //Whether this run already applied the package updates, so Finish doesn't repeat it
    private bool _needsFileList = true; //A cancelled or failed run leaves the queues incomplete, so the next run starts from a fresh file list
    private volatile bool _stopAtFirstDifference;
    private int _compareTotal;
    private int _downloadTotal;
    private int _completedDownloads;
    private readonly ConcurrentQueue<string> _failedFiles = new();
    private long _totalBytesDownloaded; //Bytes received this download phase, for the speed
    private long _downloadTotalBytes; //Size of everything queued for download, 0 when the server doesn't send sizes
    private long _finishedBytes; //Size of queued files that finished downloading or failed
    private readonly ConcurrentDictionary<string, long> _inFlightBytes = new(); //Bytes so far of each file downloading now
    private DateTime _lastProgressTime = DateTime.MinValue;

    public event Action<UpdateProgress>? ProgressChanged;
    public event Action<FileProgress>? FileProgressChanged;
    public event Action<UpdateErrorInfo>? ErrorOccurred;

    //How long to wait before each retry of a failed download: 1s, 2s, 4s, 8s. Tests set it to zero
    public Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(1 << (attempt - 1));

    //True after a run finished with every file matching the server
    public bool FilesVerified { get; private set; }

    //Files the last run couldn't download
    public IReadOnlyCollection<string> FailedFiles => _failedFiles.ToArray();

    //Server files and folders the last run skipped because the player's ignore list covers them, sorted.
    //A folder is listed once with a trailing /, however many of the server's files are in it
    public IReadOnlyList<string> IgnoredItems => _ignored;

    //packages is null when the server hosts no packages for this launcher (no trusted signing key), so only files are updated.
    //keepLocalFiles are name patterns (e.g. "*.cfg") for files players change themselves: downloaded only when missing, never replaced.
    //reservedPaths are files or folders inside the install folder that the file list may never write to, like the folder
    //a signed package is installed into: the file list isn't signed, so it must not be a way around the signature
    public UpdateService(FileServerClient server, LocalFiles localFiles, string installPath, IPackageUpdater? packages,
        IReadOnlyCollection<string>? keepLocalFiles = null, IReadOnlyCollection<string>? reservedPaths = null)
    {
        _server = server;
        _localFiles = localFiles;
        _installPath = installPath;
        _packages = packages;
        _keepLocalFiles = keepLocalFiles ?? [];
        _reservedPaths = [HashCache.FileName, IgnoreRules.FileName, .. reservedPaths ?? []];
        _hashes = new HashCache(localFiles, installPath);
    }

    public void Cancel() => _cancellation.Cancel();

    //Checks the server's file list against local files, stopping at the first difference. Nothing is downloaded here
    public Task<UpdateResult> CheckAsync() => RunAsync(async token =>
    {
        _needsFileList = true;
        if (!await LoadFileList(token)) return UpdateResult.Failed;
        if (!await CheckPackages(token)) return UpdateResult.Failed;

        //Only need to know whether anything changed, the rest is checked by DownloadAsync
        await CompareFiles(stopAtFirstDifference: true);
        token.ThrowIfCancellationRequested();

        if (!_toDownload.IsEmpty) return UpdateResult.UpdatesReady;
        if (_packageUpdates.Any) return UpdateResult.PackagesReady;

        return await Finish(token);
    });

    //Installs the TazUO launcher if needed, then checks the files CheckAsync skipped and downloads everything that differs
    public Task<UpdateResult> DownloadAsync() => RunAsync(async token =>
    {
        if (_needsFileList)
        {
            if (!await LoadFileList(token)) return UpdateResult.Failed;
        }
        else
        {
            ApplyIgnoreList(); //The player may have changed it since the check
        }

        if (!await CheckPackages(token)) return UpdateResult.Failed;

        await ApplyPackages(token);
        token.ThrowIfCancellationRequested();

        await CompareFiles(stopAtFirstDifference: false);
        await DownloadFiles();
        return await Finish(token);
    });

    private async Task<UpdateResult> RunAsync(Func<CancellationToken, Task<UpdateResult>> run)
    {
        if (_cancellation.IsCancellationRequested) _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        FilesVerified = false;
        _packagesApplied = false;
        _failedFiles.Clear();

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
        finally
        {
            _hashes.Save();
        }
    }

    private async Task<bool> LoadFileList(CancellationToken token)
    {
        _toCompare.Clear();
        _toDownload.Clear();
        _fileList = new List<FileEntry>();
        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.RequestingFileList, 0, 0));

        try
        {
            foreach (var file in await _server.GetFileListAsync(_installPath, token))
            {
                if (LocalFiles.IsReserved(_installPath, file.Name, _reservedPaths))
                {
                    Console.WriteLine($"[{file.Name}] is not the file list's to update, skipping..");
                    continue;
                }

                _fileList.Add(file);
                _toCompare.Enqueue(file);
            }
        }
        catch (UpdateServerException e)
        {
            Console.WriteLine(e.Message);
            ErrorOccurred?.Invoke(new UpdateErrorInfo(e.Error));
            return false;
        }

        ApplyIgnoreList();
        _compareTotal = _toCompare.Count;
        _needsFileList = false;
        return true;
    }

    //Reads the install folder's ignore list again and drops what it covers from the files still to compare or download.
    //Ignored files are the player's choice, so they don't count against FilesVerified
    private void ApplyIgnoreList()
    {
        var rules = IgnoreRules.None;
        try
        {
            rules = IgnoreRules.Parse(_localFiles.ReadAllTextIfExists(IgnoreRules.PathIn(_installPath)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Couldn't read {IgnoreRules.FileName}, nothing is ignored: {e.Message}");
        }

        _ignored = _fileList.Select(file => rules.Match(file.Name)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_ignored.Length == 0) return;

        foreach (var queue in new[] { _toCompare, _toDownload })
        {
            var keep = queue.Where(file => rules.Match(file.Name) == null).ToList();
            queue.Clear();
            foreach (var file in keep)
                queue.Enqueue(file);
        }

        Console.WriteLine($"Skipping {_ignored.Length} ignored file(s) or folder(s): {string.Join(", ", _ignored)}");
    }

    private async Task<UpdateResult> Finish(CancellationToken token)
    {
        if (!_packagesApplied) await ApplyPackages(token); //Nothing was pending, but the TazUO profiles still get set up
        token.ThrowIfCancellationRequested();

        FilesVerified = _failedFiles.IsEmpty; //Every file matched the server or was downloaded
        return UpdateResult.Finished;
    }

    //Asks the server's signed manifest which packages are out of date. An untrusted or unreachable manifest stops the run
    private async Task<bool> CheckPackages(CancellationToken token)
    {
        _packageUpdates = PackageUpdates.None;
        if (_packages == null) return true;

        try
        {
            _packageUpdates = await _packages.CheckAsync(token);
            return true;
        }
        catch (UpdateServerException e)
        {
            Console.WriteLine(e.Message);
            ErrorOccurred?.Invoke(new UpdateErrorInfo(e.Error));
            return false;
        }
    }

    private async Task ApplyPackages(CancellationToken token)
    {
        if (_packages == null || token.IsCancellationRequested) return;

        _packagesApplied = true;
        try
        {
            await _packages.ApplyAsync(_packageUpdates, progress => ProgressChanged?.Invoke(progress),
                error => ErrorOccurred?.Invoke(error), token);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            Console.WriteLine(e.ToString());
            ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.TazUOInstallFailed));
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
            if (_localFiles.Exists(fullPath))
            {
                if (!KeepLocal(file.Name) && !MatchesServer(file, fullPath))
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

    private bool KeepLocal(string name) =>
        _keepLocalFiles.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    //A different size means changed without hashing. Otherwise compare the MD5, reusing the cached one if the file hasn't changed
    private bool MatchesServer(FileEntry file, string fullPath)
    {
        if (file.Size is long size && _localFiles.Length(fullPath) != size) return false;
        return file.Md5.Equals(_hashes.GetMd5(file.Name, fullPath), StringComparison.OrdinalIgnoreCase);
    }

    private void ReportCompareProgress() =>
        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.Comparing, _compareTotal - _toCompare.Count, _compareTotal));

    private async Task DownloadFiles()
    {
        _downloadTotal = _toDownload.Count;
        _completedDownloads = 0;
        Interlocked.Exchange(ref _totalBytesDownloaded, 0);
        Interlocked.Exchange(ref _finishedBytes, 0);
        _inFlightBytes.Clear();
        _downloadTotalBytes = _toDownload.All(f => f.Size != null) ? _toDownload.Sum(f => f.Size!.Value) : 0;
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
                    _localFiles.EnsureDirectory(filePath);
                    await _server.DownloadFileAsync(file, filePath,
                        (chunk, fileBytes, fileLength) => OnBytesDownloaded(file.Name, chunk, fileBytes, fileLength), token);
                    _hashes.Set(file.Name, filePath, file.Md5); //Just checked, so the next check needn't hash it again
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                        return;

                    Console.WriteLine(ex.ToString());

                    if (LocalFiles.IsLocked(ex))
                    {
                        //Retrying won't help while the game has it open. The download is kept and finished on the next run
                        Console.WriteLine($"[{file.Name}] is in use by another program, skipping..");
                        ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.FileLocked, file.Name));
                        _failedFiles.Enqueue(file.Name);
                        break;
                    }

                    if (attempt == MAX_ATTEMPTS)
                    {
                        Console.WriteLine($"Failed to download [{file.Name}] after {MAX_ATTEMPTS} attempts, skipping..");
                        ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.FileFailed, file.Name));
                        _failedFiles.Enqueue(file.Name);
                        break;
                    }

                    //Back off before retrying
                    try
                    {
                        await Task.Delay(RetryDelay(attempt), token);
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
            _inFlightBytes.TryRemove(file.Name, out _);
            Interlocked.Add(ref _finishedBytes, file.Size ?? 0);

            // Final update after file is done
            FileProgressChanged?.Invoke(new FileProgress(file.Name, 100));
            ReportDownloadProgress();
        }
    }

    private void OnBytesDownloaded(string fileName, int chunk, long fileBytes, long? fileLength)
    {
        Interlocked.Add(ref _totalBytesDownloaded, chunk);
        _inFlightBytes[fileName] = fileBytes;

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
        long bytesDone = _downloadTotalBytes > 0
            ? Math.Min(_downloadTotalBytes, Interlocked.Read(ref _finishedBytes) + _inFlightBytes.Values.Sum())
            : 0;
        ProgressChanged?.Invoke(new UpdateProgress(UpdatePhase.Downloading,
            Volatile.Read(ref _completedDownloads), _downloadTotal, bytesPerSecond, bytesDone, _downloadTotalBytes));
    }
}
