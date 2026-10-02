using System.Diagnostics;
using FileUpdaterPackages;

namespace FileUpdaterClient.Updating;

//What the signed manifest says needs doing. A null entry means that package is up to date (or not offered).
//ManifestMissing is true when the server hosts no packages at all
public record PackageUpdates(PackageEntry? Launcher, PackageEntry? Client, bool ManifestMissing)
{
    public static readonly PackageUpdates None = new(null, null, false);

    //Whether the next download has a package to install. A newer launcher isn't counted: updating it is the player's choice
    //(UpdateLauncherAsync) and never holds up downloading files or playing
    public bool Any => Client != null;
}

//Keeps this launcher and the TazUO launcher up to date from the server's signed packages
public interface IPackageUpdater
{
    //Fetches the signed manifest and works out what is out of date. Throws UpdateServerException if it can't be trusted
    Task<PackageUpdates> CheckAsync(CancellationToken cancellationToken);

    //Downloads and installs the TazUO launcher if it is out of date or missing, and sets up its profiles. Problems are
    //reported through error, not thrown. Never touches this launcher: see UpdateLauncherAsync
    Task ApplyAsync(PackageUpdates updates, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken);

    //Downloads a newer version of this launcher and replaces it. Problems are reported through error, not thrown.
    //Returns true when the launcher replaced itself and is restarting, so nothing else should run in this process
    Task<bool> UpdateLauncherAsync(PackageEntry launcher, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken);
}

//Installs the TazUO launcher from a package zip
public interface IClientInstaller
{
    bool IsInstalled { get; }
    void InstallFromZip(string zipPath);
    void EnsureProfiles();
}

//The TazUO launcher as the player sees it: installed by PackageUpdater, then opened by the Play button
public interface IGameClient : IClientInstaller
{
    void Start();
}

//Replaces the running launcher with the one in a downloaded, verified package and restarts into it
public interface ISelfUpdater
{
    //packagePath is the verified zip. Returns true once the swap is under way and the process is restarting.
    //Returns false when nothing was applied, and the launcher carries on with the old version
    Task<bool> ApplyAndRestartAsync(string packagePath, Version newVersion, CancellationToken cancellationToken);
}

//Which package versions this machine has installed. The TazUO launcher has no version file we can read, so we remember it
public interface IPackageState
{
    Version? ClientVersion { get; }
    void SetClientVersion(Version version);
}

public class PackageUpdater(FileServerClient server, LocalFiles localFiles, IReadOnlyCollection<string> trustedKeys, string rid,
    Version launcherVersion, string downloadFolder, ISelfUpdater selfUpdater, IClientInstaller? client, IPackageState state) : IPackageUpdater
{
    private const int MAX_ATTEMPTS = 3;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(0.5);

    private Version? _declinedLauncherVersion; //A launcher version the self-updater declined to apply, not offered again this run

    //How long to wait before each retry of a failed download. Tests set it to zero
    public Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(1 << (attempt - 1));

    public async Task<PackageUpdates> CheckAsync(CancellationToken cancellationToken)
    {
        var manifest = await server.GetPackageManifestAsync(trustedKeys, cancellationToken);
        if (manifest == null) return new PackageUpdates(null, null, ManifestMissing: true);

        var launcher = manifest.Find(PackageRole.Launcher, rid);
        if (launcher != null && !IsNewer(launcher, launcherVersion)) launcher = null;
        if (launcher != null && Same(TryVersion(launcher), _declinedLauncherVersion)) launcher = null;

        //Installed without a recorded version (installed before packages existed) counts as out of date, so it's replaced once
        var package = client == null ? null : manifest.Find(PackageRole.Client, rid);
        if (package != null && client!.IsInstalled && !IsNewer(package, state.ClientVersion ?? new Version(0, 0))) package = null;

        return new PackageUpdates(launcher, package, ManifestMissing: false);
    }

    public async Task ApplyAsync(PackageUpdates updates, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken)
    {
        if (client == null) return;

        if (updates.Client != null)
        {
            await InstallClientAsync(updates.Client, progress, error, cancellationToken);
        }
        else if (!client.IsInstalled)
        {
            //Nothing to install it from: the server has no packages, or none for this platform
            Console.WriteLine($"The server has no client package for {rid}, so the TazUO launcher can't be installed.");
            error(new UpdateErrorInfo(UpdateError.LauncherFailed));
        }

        if (client.IsInstalled)
            client.EnsureProfiles();
    }

    public async Task<bool> UpdateLauncherAsync(PackageEntry package, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken)
    {
        var version = TryVersion(package)!;
        var path = await DownloadAsync(package, UpdatePhase.UpdatingLauncher, progress, cancellationToken);
        if (path == null)
        {
            error(new UpdateErrorInfo(UpdateError.SelfUpdateFailed));
            return false;
        }

        try
        {
            if (await selfUpdater.ApplyAndRestartAsync(path, version, cancellationToken)) return true;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(e.ToString());
            error(new UpdateErrorInfo(UpdateError.SelfUpdateFailed));
        }

        //Not applied: don't offer the same version again until the launcher is restarted
        _declinedLauncherVersion = version;
        localFiles.DeleteIfExists(path);
        return false;
    }

    private async Task InstallClientAsync(PackageEntry package, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken)
    {
        var path = await DownloadAsync(package, UpdatePhase.InstallingClient, progress, cancellationToken);
        if (path == null)
        {
            error(new UpdateErrorInfo(UpdateError.LauncherFailed));
            return;
        }

        try
        {
            client!.InstallFromZip(path);
            state.SetClientVersion(TryVersion(package)!);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(e.ToString());
            error(new UpdateErrorInfo(UpdateError.LauncherFailed));
        }
        finally
        {
            localFiles.DeleteIfExists(path);
        }
    }

    //Returns where the verified package was saved, or null if it couldn't be downloaded (cancellation is rethrown)
    private async Task<string?> DownloadAsync(PackageEntry package, UpdatePhase phase, Action<UpdateProgress> progress,
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

    //The version as the manifest wrote it, e.g. 1.5.0
    private static Version? TryVersion(PackageEntry package) =>
        Version.TryParse(package.Version, out var version) ? version : null;

    private static bool IsNewer(PackageEntry package, Version installed) =>
        TryVersion(package) is { } version && Normalize(version) > Normalize(installed);

    private static bool Same(Version? a, Version? b) => a != null && b != null && Normalize(a) == Normalize(b);

    //1.2 and 1.2.0 mean the same version, but Version orders them differently
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
