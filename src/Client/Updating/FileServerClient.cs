using System.Text.Json;

namespace FileUpdaterClient.Updating;

public class UpdateServerException(UpdateError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public UpdateError Error { get; } = error;
}

//Talks to the update server: fetches the file list and downloads individual files
public class FileServerClient
{
    private const int BufferSize = 81920;
    private readonly string _baseUrl;
    private readonly HttpClient _listClient;
    private readonly HttpClient _downloadClient;

    //handler lets tests supply a fake server
    public FileServerClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl;
        handler ??= new SocketsHttpHandler();
        _listClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) }; //Initial connection
        _downloadClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(15) }; //Download timeout
    }

    //Returns the server's files, skipping malformed entries and names that would land outside installPath
    public async Task<List<FileEntry>> GetFileListAsync(string installPath, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _listClient.GetAsync(new Uri(_baseUrl), cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            //Server unreachable, refused the connection, or timed out
            throw new UpdateServerException(UpdateError.ConnectionFailed, e.Message, e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateServerException(UpdateError.ConnectionFailed,
                    $"Server returned {(int)response.StatusCode} for the file list.");

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(json))
                throw new UpdateServerException(UpdateError.BadData, "Empty file list.");

            FileEntry?[]? fileList;
            try
            {
                fileList = JsonSerializer.Deserialize<FileEntry?[]>(json);
            }
            catch (JsonException e)
            {
                throw new UpdateServerException(UpdateError.BadData, e.Message, e);
            }

            if (fileList == null)
                throw new UpdateServerException(UpdateError.BadData, "File list was null.");

            Console.WriteLine($"Received information for {fileList.Length} files from the server, comparing to local files..");

            var files = new List<FileEntry>();
            foreach (var item in fileList)
            {
                if (item == null || string.IsNullOrEmpty(item.Name) || string.IsNullOrEmpty(item.Md5))
                    continue;

                if (!LocalFiles.TryGetLocalPath(installPath, item.Name, out _))
                {
                    Console.WriteLine($"[{item.Name}] points outside the update folder, skipping..");
                    continue;
                }

                files.Add(item);
            }

            return files;
        }
    }

    //Downloads to a temporary file first so a failed or cancelled download never leaves a partial file in place.
    //onBytes is called after each chunk with (chunk size, bytes of this file so far, file length if the server sent it)
    public async Task DownloadFileAsync(FileEntry file, string filePath, Action<int, long, long?> onBytes,
        CancellationToken cancellationToken)
    {
        var tempPath = filePath + ".part";
        try
        {
            Uri updateUrl = new Uri(_baseUrl + "/file/" + file.Name);

            using var response = await _downloadClient.GetAsync(updateUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? fileLength = response.Content.Headers.ContentLength;
            long fileBytesDownloaded = 0;

            using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = File.Create(tempPath))
            {
                byte[] buffer = new byte[BufferSize];
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    fileBytesDownloaded += bytesRead;
                    onBytes(bytesRead, fileBytesDownloaded, fileLength);
                }
            }

            //Reject truncated or changed downloads so they are retried instead of replacing the local file
            var downloadedMd5 = LocalFiles.ComputeMd5(tempPath);
            if (!file.Md5.Equals(downloadedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"[{file.Name}] hash mismatch after download (expected {file.Md5}, got {downloadedMd5})");

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
}
