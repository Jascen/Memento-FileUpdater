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
    private static int failedDownloads = 0;
    private static int isRunning; //Set while a check or download is in progress, so repeat clicks and Verify are ignored
    private static volatile bool stopAtFirstDifference;
    private static bool needsRecheck; //A cancelled run leaves the queues incomplete, so the next download starts from a fresh file list
    private static long totalBytesDownloaded = 0;
    private static readonly Stopwatch downloadClock = new(); //Wall-clock time of the download phase, shared by all workers
    private static DateTime lastUiUpdateTime = DateTime.MinValue;
    private static CancellationTokenSource cancellationSource = new();
    private static CancellationToken cancellationToken = cancellationSource.Token;

    //Checks the server's file list against local files. Nothing is downloaded until the player clicks the download button
    public static async Task HandleUpdates(MainViewModel dataModel)
    {
        if (Interlocked.Exchange(ref isRunning, 1) == 1) return;

        data = dataModel;
        Reset();
        try
        {
            client = new HttpClient(); //Fresh client each check, the timeout can't change after a request
            client.Timeout = TimeSpan.FromSeconds(5); //Initial connection
            if (!await GetFileList()) return;
            needsRecheck = false;

            //Only need to know whether anything changed, the rest is checked when the player clicks download
            await StartComparingFiles(stopAtFirstDifference: true);

            if (cancellationToken.IsCancellationRequested) return;

            if (!downloadQueue.IsEmpty || !TazUOSetup.IsInstalled)
            {
                var filesChanged = !downloadQueue.IsEmpty;
                Dispatcher.UIThread.Post(() =>
                {
                    data.Progress = 0;
                    data.ProgressText = filesChanged ? Settings.UpdatesReady : Settings.LauncherReady;
                    data.DownloadsReady = true;
                });
                return;
            }

            await FinishUpdate();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            Dispatcher.UIThread.Post(() => data.ErrorMessage = Settings.UnknownError);
        }
        finally
        {
            EndRun(wasDownload: false);
        }
    }

    //Downloads the files found by HandleUpdates, called when the player clicks the download button
    public static async Task DownloadUpdates()
    {
        if (Interlocked.Exchange(ref isRunning, 1) == 1) return; //Ignore repeat clicks
        Dispatcher.UIThread.Post(() =>
        {
            data.DownloadsReady = false;
            data.IsUpdating = true;
        });
        try
        {
            if (needsRecheck)
            {
                Reset();
                client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5); //Initial connection
                if (!await GetFileList()) return;
                needsRecheck = false;
            }

            await StartComparingFiles(stopAtFirstDifference: false); //Check the files the launch check skipped

            client = new HttpClient(); //Must have new client for new timeout
            client.Timeout = TimeSpan.FromMinutes(15); //Download timeout
            await StartDownloading();

            await FinishUpdate();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            Dispatcher.UIThread.Post(() => data.ErrorMessage = Settings.UnknownError);
        }
        finally
        {
            EndRun(wasDownload: true);
        }
    }

    private static async Task FinishUpdate()
    {
        await SetUpTazUO();

        if (cancellationToken.IsCancellationRequested) return;

        var verified = Volatile.Read(ref failedDownloads) == 0; //Every file matched the server or was downloaded
        Dispatcher.UIThread.Post(() => //Ensure the final finished text is queued in case other text updates are already queued, making sure this is the last one ran.
        {
            data.FilesVerified = verified;
            data.Progress = 100;
            data.FileProgress = 100;
            data.ProgressText = Settings.Finished;
            data.FileProgressText = string.Empty;
        });
    }

    //Clears state from a previous run so Verify can check everything again
    private static void Reset()
    {
        if (cancellationSource.IsCancellationRequested)
        {
            cancellationSource = new CancellationTokenSource();
            cancellationToken = cancellationSource.Token;
        }

        downloadQueue.Clear();
        remoteFileListQueue.Clear();
        completedDownloads = 0;
        failedDownloads = 0;
        totalBytesDownloaded = 0;

        data.IsUpdating = true;
        data.FilesVerified = false;
        data.DownloadsReady = false;
        data.ErrorMessage = string.Empty;
        data.Progress = 0;
        data.FileProgress = 0;
        data.FileProgressText = string.Empty;
    }

    private static void EndRun(bool wasDownload)
    {
        var cancelled = cancellationToken.IsCancellationRequested;
        if (cancelled) needsRecheck = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (cancelled)
            {
                //A cancelled download leaves known out of date files, so offer it again. A cancelled check just leaves them unverified
                data.ProgressText = Settings.Cancelled;
                data.DownloadsReady = wasDownload;
            }
            data.IsUpdating = false;
        });
        Interlocked.Exchange(ref isRunning, 0);
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

        var installed = TazUOSetup.IsInstalled;
        Dispatcher.UIThread.Post(() => data.LauncherInstalled = installed);
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

    private static async Task StartComparingFiles(bool stopAtFirstDifference)
    {
        if (remoteFileListQueue.IsEmpty) return;

        UpdateHandler.stopAtFirstDifference = stopAtFirstDifference;
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
        while (!cancellationToken.IsCancellationRequested
               && !(stopAtFirstDifference && !downloadQueue.IsEmpty)
               && remoteFileListQueue.TryDequeue(out FileEntry file))
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

            TryGetLocalPath(file.name, out var filePath);
            Console.WriteLine($"Downloading [{file.name}]...");

            for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
            {
                try
                {
                    EnsureDirectory(filePath);
                    await DownloadFile(file, filePath);
                    break;
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    Console.WriteLine(ex.ToString());

                    if (attempt == MAX_ATTEMPTS)
                    {
                        var fname = file.name;
                        Console.WriteLine($"Failed to download [{file.name}] after {MAX_ATTEMPTS} attempts, skipping..");
                        Dispatcher.UIThread.Post(() => data.ErrorMessage = string.Format(Settings.FileFailedError, fname));
                        Interlocked.Increment(ref failedDownloads);
                        break;
                    }

                    //Back off before retrying: 1s, 2s, 4s, 8s
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            Interlocked.Increment(ref completedDownloads);

            // Final UI update after file is done
            Dispatcher.UIThread.Post(() => data.FileProgress = 100);
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

            using var response = await client.GetAsync(updateUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? fileLength = response.Content.Headers.ContentLength;
            long fileBytesDownloaded = 0;
            var fileLabel = string.Format(Settings.CurrentFile, file.name);
            Dispatcher.UIThread.Post(() =>
            {
                data.FileProgress = 0;
                data.FileProgressText = fileLabel;
            });

            using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = File.Create(tempPath))
            {
                byte[] buffer = new byte[81920];
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                    Interlocked.Add(ref totalBytesDownloaded, bytesRead);
                    fileBytesDownloaded += bytesRead;

                    // Limit UI updates to every 0.5 seconds
                    if ((DateTime.UtcNow - lastUiUpdateTime).TotalSeconds >= 0.5)
                    {
                        lastUiUpdateTime = DateTime.UtcNow;
                        double fileProgress = fileLength > 0 ? fileBytesDownloaded / (double)fileLength * 100 : 0;
                        Dispatcher.UIThread.Post(() =>
                        {
                            data.FileProgress = fileProgress;
                            data.FileProgressText = fileLabel;
                        });
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
