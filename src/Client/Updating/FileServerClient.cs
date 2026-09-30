using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FileUpdaterPackages;

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
    private readonly LocalFiles _localFiles;
    private readonly HttpClient _listClient;
    private readonly HttpClient _downloadClient;

    //handler lets tests supply a fake server
    public FileServerClient(string baseUrl, LocalFiles localFiles, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl;
        _localFiles = localFiles;
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

    //Downloads to a temporary .part file first so a failed or cancelled download never leaves a partial file in place.
    //A .part left by an earlier attempt is resumed with a Range request instead of starting over.
    //onBytes is called after each chunk with (chunk size, bytes of this file so far, file length if the server sent it)
    public Task DownloadFileAsync(FileEntry file, string filePath, Action<int, long, long?> onBytes,
        CancellationToken cancellationToken) =>
        DownloadAsync(FileUrl(file.Name), file.Name, file.Size, file.Md5, _localFiles.ComputeMd5, filePath, onBytes, cancellationToken);

    //Same for a launcher or client package, checked against the SHA-256 in the signed manifest
    public Task DownloadPackageAsync(PackageEntry package, string filePath, Action<int, long, long?> onBytes,
        CancellationToken cancellationToken)
    {
        //The name comes from a signed manifest, but it is still only ever a plain file name
        if (!PackageFileName.IsPlain(package.File))
            throw new InvalidDataException($"[{package.File}] is not a plain file name");

        return DownloadAsync(PackageUrl("file/" + Uri.EscapeDataString(package.File)), package.File, package.Size,
            package.Sha256, _localFiles.ComputeSha256, filePath, onBytes, cancellationToken);
    }

    private async Task DownloadAsync(Uri url, string name, long? expectedSize, string expectedHash, Func<string, string> computeHash,
        string filePath, Action<int, long, long?> onBytes, CancellationToken cancellationToken)
    {
        var tempPath = filePath + ".part";
        long resumeFrom = _localFiles.Exists(tempPath) ? _localFiles.Length(tempPath) : 0;
        if (resumeFrom > 0 && resumeFrom == expectedSize)
        {
            //Fully downloaded earlier but never moved into place, e.g. the file was locked
            VerifyAndMove(name, expectedHash, computeHash, tempPath, filePath);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            //The .part is already as long as the file (or longer), so it can't be resumed. Start over on the next attempt
            _localFiles.DeleteIfExists(tempPath);
            throw new InvalidDataException($"[{name}] partial download can't be resumed");
        }

        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent)
            resumeFrom = 0; //Server sent the whole file

        long? fileLength = response.Content.Headers.ContentLength + resumeFrom;
        long fileBytesDownloaded = resumeFrom;

        using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        using (var fileStream = resumeFrom > 0 ? _localFiles.OpenAppend(tempPath) : _localFiles.Create(tempPath))
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

        VerifyAndMove(name, expectedHash, computeHash, tempPath, filePath);
    }

    //Rejects truncated or changed downloads so they are retried from scratch instead of replacing the local file
    private void VerifyAndMove(string name, string expectedHash, Func<string, string> computeHash, string tempPath, string filePath)
    {
        var downloadedHash = computeHash(tempPath);
        if (!expectedHash.Equals(downloadedHash, StringComparison.OrdinalIgnoreCase))
        {
            _localFiles.DeleteIfExists(tempPath);
            throw new InvalidDataException(
                $"[{name}] hash mismatch after download (expected {expectedHash}, got {downloadedHash})");
        }

        _localFiles.Move(tempPath, filePath);
    }

    //Fetches the package manifest and returns it only if one of trustedKeys signed exactly those bytes.
    //Null when the server hosts no packages. Anything else wrong throws UpdateServerException, so nothing unsigned is ever acted on
    public async Task<PackageManifest?> GetPackageManifestAsync(IReadOnlyCollection<string> trustedKeys, CancellationToken cancellationToken)
    {
        var manifest = await GetBytesAsync(PackageManifest.ManifestFileName, cancellationToken);
        if (manifest == null) return null;

        var signature = await GetBytesAsync(PackageManifest.SignatureFileName, cancellationToken);
        if (signature == null || !ManifestSigning.Verify(manifest, Encoding.UTF8.GetString(signature), trustedKeys))
            throw new UpdateServerException(UpdateError.PackagesUntrusted, "The package manifest is not signed by a trusted key.");

        try
        {
            return PackageManifest.Parse(manifest);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new UpdateServerException(UpdateError.BadData, e.Message, e);
        }
    }

    //Null on 404
    private async Task<byte[]?> GetBytesAsync(string packageFile, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _listClient.GetAsync(PackageUrl(packageFile), cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new UpdateServerException(UpdateError.ConnectionFailed,
                    $"Server returned {(int)response.StatusCode} for {packageFile}.");

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateServerException(UpdateError.ConnectionFailed, e.Message, e);
        }
    }

    private Uri PackageUrl(string path) => new(_baseUrl.TrimEnd('/') + "/packages/" + path);

    //Escapes each part of the name so files with spaces, # or ? in their names download correctly
    private Uri FileUrl(string name) =>
        new(_baseUrl + "/file/" + string.Join("/", name.Split('/').Select(Uri.EscapeDataString)));
}
