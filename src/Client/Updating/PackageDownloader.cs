using System.Diagnostics;
using FileUpdaterPackages;

namespace FileUpdaterClient.Updating;

//Downloads a package named in the signed manifest into the download folder, checked against the manifest's SHA-256.
//Shared by PackageUpdater (the TazUO launcher) and LauncherUpdater (this launcher)
public class PackageDownloader(FileServerClient server, LocalFiles localFiles, string downloadFolder)
{
    private const int MAX_ATTEMPTS = 3;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(0.5);

    //How long to wait before each retry of a failed download. Tests set it to zero
    public Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(1 << (attempt - 1));

    //Returns where the verified package was saved, or null if it couldn't be downloaded (cancellation is rethrown)
    public async Task<string?> DownloadAsync(PackageEntry package, UpdatePhase phase, Action<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        //Checked before any folder is created: the name comes from a signed manifest but is only ever a plain file name
        if (!PackageFileName.IsPlain(package.File))
        {
            Console.WriteLine($"[{package.File}] is not a plain file name, skipping..");
            return null;
        }

        var path = Path.Combine(downloadFolder, package.File);
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        progress(new UpdateProgress(phase, 0, 1, BytesTotal: package.Size));

        void OnBytes(int chunk, long fileBytes, long? fileLength)
        {
            if (clock.Elapsed - lastReport < ProgressInterval) return;
            lastReport = clock.Elapsed;
            var seconds = clock.Elapsed.TotalSeconds;
            progress(new UpdateProgress(phase, 0, 1, seconds > 0 ? fileBytes / seconds : 0, fileBytes, fileLength ?? package.Size));
        }

        for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            try
            {
                localFiles.EnsureDirectory(path);
                await server.DownloadPackageAsync(package, path, OnBytes, cancellationToken);
                progress(new UpdateProgress(phase, 1, 1, BytesDone: package.Size, BytesTotal: package.Size));
                return path;
            }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine(e.ToString());
                if (attempt == MAX_ATTEMPTS) break;

                await Task.Delay(RetryDelay(attempt), cancellationToken);
            }
        }

        Console.WriteLine($"Failed to download [{package.File}] after {MAX_ATTEMPTS} attempts, skipping..");
        return null;
    }

    //Removes a downloaded package once it has been used, or turned out to be no use
    public void Delete(string path) => localFiles.DeleteIfExists(path);
}

//Compares the versions packages are published under
public static class PackageVersions
{
    //The version as the manifest wrote it, e.g. 1.5.0
    public static Version? TryVersion(PackageEntry package) =>
        Version.TryParse(package.Version, out var version) ? version : null;

    public static bool IsNewer(PackageEntry package, Version installed) =>
        TryVersion(package) is { } version && Normalize(version) > Normalize(installed);

    public static bool Same(Version? a, Version? b) => a != null && b != null && Normalize(a) == Normalize(b);

    //1.2 and 1.2.0 mean the same version, but Version orders them differently
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
