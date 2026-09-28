using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;

namespace FileUpdaterClient;

public static class UpdateHandler
{
    private const int WORKER_COUNT = 2;
    private const int MAX_ATTEMPTS = 5;
    private static HttpClient client = new();
    private static ConcurrentQueue<FileEntry> downloadQueue = new();
    private static ConcurrentQueue<FileEntry> remoteFileListQueue = new();
    private static MainViewModel data;
    private static double currentMaxProgress;
    private static int completedDownloads = 0;
    private static ConcurrentDictionary<string, int> retryMap = new();
    private static long totalBytesDownloaded = 0;
    private static readonly Stopwatch downloadClock = new(); //Wall-clock time of the download phase, shared by all workers
    private static DateTime lastUiUpdateTime = DateTime.MinValue;
    private static readonly CancellationTokenSource cancellationSource = new();
    private static readonly CancellationToken cancellationToken = cancellationSource.Token;

    public static async Task HandleUpdates(MainViewModel dataModel)
    {
        data = dataModel;
        try
        {
            client.Timeout = TimeSpan.FromSeconds(5); //Initial connection
            if (!await GetFileList()) return;

            await StartComparingFiles();

            client = new HttpClient(); //Must have new client for new timeout
            client.Timeout = TimeSpan.FromMinutes(15); //Download timeout
            await StartDownloading();

            await SetUpTazUO();

            if (cancellationToken.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() => //Ensure the final finished text is queued in case other text updates are already queued, making sure this is the last one ran.
            {
                data.Progress = 100;
                data.ProgressText = Settings.Finished;
            });
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            Dispatcher.UIThread.Post(() => data.ErrorMessage = Settings.UnknownError);
        }
    }

    public static void Cancel()
    {
        cancellationSource.Cancel();
    }

    private static async Task SetUpTazUO()
    {
        if (cancellationToken.IsCancellationRequested) return;

        Dispatcher.UIThread.Post(() => data.ProgressText = Settings.InstallingTazUO);
        try
        {
            await TazUOSetup.EnsureInstalledAsync(cancellationToken);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            Dispatcher.UIThread.Post(() => data.ErrorMessage = Settings.TazUOError);
        }
    }

    private static async Task<bool> GetFileList()
    {
        data.ProgressText = Settings.ReqFileList;

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(new Uri(Settings.UpdateUrl), cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            //Server unreachable, refused the connection, or timed out
            data.ErrorMessage = Settings.ConError;
            Console.WriteLine(e.Message);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            data.ErrorMessage = Settings.ConError;
            Console.WriteLine($"Server returned {(int)response.StatusCode} for the file list.");
            return false;
        }

        try
        {
            string json = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(json))
            {
                data.ErrorMessage = Settings.BadData;
                return false;
            }

            FileEntry[] fileList = JsonSerializer.Deserialize<FileEntry[]>(json);
            if (fileList == null)
            {
                data.ErrorMessage = Settings.BadData;
                return false;
            }

            Console.WriteLine(
                $"Received information for {fileList.Length} files from the server, comparing to local files..");

            foreach (var item in fileList)
            {
                if (item == null || string.IsNullOrEmpty(item.name) || item.md5 == null)
                    continue;

                if (!TryGetLocalPath(item.name, out _))
                {
                    Console.WriteLine($"[{item.name}] points outside the update folder, skipping..");
                    continue;
                }

                remoteFileListQueue.Enqueue(item);
            }

            data.Progress = 0;
            return true;
        }
        catch (JsonException e)
        {
            data.ErrorMessage = Settings.BadData;
            Console.WriteLine(e.Message);
            return false;
        }
        catch (Exception e)
        {
            data.ErrorMessage = Settings.UnknownError;
            Console.WriteLine(e.Message);
            return false;
        }
    }

    private static async Task StartComparingFiles()
    {
        if (remoteFileListQueue.IsEmpty) return;

        currentMaxProgress = remoteFileListQueue.Count;
        Dispatcher.UIThread.Post(() =>
        {
            data.Progress = 0;
            data.ProgressText = string.Format(Settings.ComparingFiles, "0", currentMaxProgress);
        });

        var tasks = new List<Task>();
        for (int i = 0; i < WORKER_COUNT; i++)
        {
            tasks.Add(Task.Run(BackgroundWorker_CompareFile));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task StartDownloading()
    {
        if (downloadQueue.IsEmpty) return;


        currentMaxProgress = downloadQueue.Count;
        Dispatcher.UIThread.Post(() =>
        {
            data.Progress = 0;
            data.ProgressText = string.Format(Settings.DownloadingFiles, "0", currentMaxProgress, "0");
        });

        downloadClock.Restart();
        var tasks = new List<Task>();
        for (int i = 0; i < WORKER_COUNT; i++)
        {
            tasks.Add(Task.Run(BackgroundWorker_DoWork));
        }

        await Task.WhenAll(tasks);
    }

    private static void BackgroundWorker_CompareFile()
    {
        while (!cancellationToken.IsCancellationRequested && remoteFileListQueue.TryDequeue(out FileEntry file))
        {
            TryGetLocalPath(file.name, out var fullPath);
            if (File.Exists(fullPath))
            {
                if (!file.md5.Equals(GetMD5HashFromFile(fullPath)))
                {
                    downloadQueue.Enqueue(file);
                    Console.WriteLine(
                        $"[{file.name}] does not match the version from the server, queued for download..");
                }
            }
            else
            {
                downloadQueue.Enqueue(file);
                Console.WriteLine($"[{file.name}] does not exist, queued for download..");
            }

            Dispatcher.UIThread.Post(() =>
            {
                data.Progress = ((currentMaxProgress - remoteFileListQueue.Count) / currentMaxProgress) * 100;
                data.ProgressText = string.Format(Settings.ComparingFiles,
                    currentMaxProgress - remoteFileListQueue.Count, currentMaxProgress);
            });
        }
    }

    private static async Task BackgroundWorker_DoWork()
    {
        while (!cancellationToken.IsCancellationRequested && downloadQueue.TryDequeue(out FileEntry file))
        {
            if (file == null)
                continue;

            try
            {
                TryGetLocalPath(file.name, out var filePath);
                Console.WriteLine($"Downloading [{file.name}]...");
                EnsureDirectory(filePath);

                await DownloadFile(file, filePath);
                if (cancellationToken.IsCancellationRequested)
                    return;

                Interlocked.Increment(ref completedDownloads);
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                Console.WriteLine(ex.ToString());
                int attempts = retryMap.AddOrUpdate(file.name, 1, (_, count) => count + 1);

                if (attempts >= MAX_ATTEMPTS)
                {
                    var fname = file.name;
                    Console.WriteLine($"Failed to download [{file.name}] after {MAX_ATTEMPTS} attempts, skipping..");
                    Dispatcher.UIThread.Post(() => data.ErrorMessage = string.Format(Settings.FileFailedError, fname));
                    Interlocked.Increment(ref completedDownloads);
                }
                else
                {
                    downloadQueue.Enqueue(file);
                    continue;
                }
            }

            // Final UI update after file is done
            PostDownloadProgress();
        }
    }

    //Downloads to a temporary file first so a failed or cancelled download never leaves a partial file in place
    private static async Task DownloadFile(FileEntry file, string filePath)
    {
        var tempPath = filePath + ".part";
        try
        {
            Uri updateUrl = new Uri(Settings.UpdateUrl + "/file/" + file.name);

            using (var responseStream = await client.GetStreamAsync(updateUrl, cancellationToken))
            using (var fileStream = File.Create(tempPath))
            {
                byte[] buffer = new byte[81920];
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                    Interlocked.Add(ref totalBytesDownloaded, bytesRead);

                    // Limit UI updates to every 0.5 seconds
                    if ((DateTime.UtcNow - lastUiUpdateTime).TotalSeconds >= 0.5)
                    {
                        lastUiUpdateTime = DateTime.UtcNow;
                        PostDownloadProgress();
                    }
                }
            }

            //Reject truncated or changed downloads so they are retried instead of replacing the local file
            var downloadedMd5 = GetMD5HashFromFile(tempPath);
            if (!file.md5.Equals(downloadedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"[{file.name}] hash mismatch after download (expected {file.md5}, got {downloadedMd5})");

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not remove partial download [{tempPath}]: {e.Message}");
            }
        }
    }

    private static void PostDownloadProgress()
    {
        double elapsedSeconds = downloadClock.Elapsed.TotalSeconds;
        double avgSpeed = elapsedSeconds > 0
            ? Interlocked.Read(ref totalBytesDownloaded) / elapsedSeconds
            : 0;

        string speedStr = $"{(avgSpeed / 1024):F2} KB/s";
        int completed = Volatile.Read(ref completedDownloads);
        Dispatcher.UIThread.Post(() =>
        {
            data.Progress = (completed / currentMaxProgress) * 100;
            data.ProgressText = string.Format(Settings.DownloadingFiles, completed, currentMaxProgress, speedStr);
        });
    }

    //Resolves a server-provided file name to a local path, rejecting any name that would land outside the client's folder
    private static bool TryGetLocalPath(string name, out string fullPath)
    {
        var baseDirectory = Path.GetFullPath(InstallLocation.Path);
        fullPath = Path.GetFullPath(name, baseDirectory);
        var root = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMD5HashFromFile(string fileName)
    {
        using (var md5 = MD5.Create())
        {
            using (var stream = File.OpenRead(fileName))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    private static void EnsureDirectory(string filePath)
    {
        string dirPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
        {
            Directory.CreateDirectory(dirPath);
        }
    }
}

public class FileEntry
{
    public string name { get; set; }
    public string md5 { get; set; }
}
