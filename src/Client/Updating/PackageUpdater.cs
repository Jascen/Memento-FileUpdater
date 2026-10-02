using FileUpdaterPackages;

namespace FileUpdaterClient.Updating;

//What the signed manifest says needs doing. A null TazUO means the TazUO launcher is up to date (or not offered).
//ManifestMissing is true when the server hosts no packages at all
public record PackageUpdates(PackageEntry? TazUO, bool ManifestMissing)
{
    public static readonly PackageUpdates None = new(null, false);

    //Whether the next download has a package to install
    public bool Any => TazUO != null;
}

//Keeps the TazUO launcher up to date from the server's signed packages. A newer version of this launcher is
//LauncherUpdater's business, not this one's
public interface IPackageUpdater
{
    //Fetches the signed manifest and works out what is out of date. Throws UpdateServerException if it can't be trusted
    Task<PackageUpdates> CheckAsync(CancellationToken cancellationToken);

    //Downloads and installs the TazUO launcher if it is out of date or missing, and sets up its profiles. Problems are
    //reported through error, not thrown
    Task ApplyAsync(PackageUpdates updates, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken);
}

//Installs the TazUO launcher from a package zip
public interface ITazUOInstaller
{
    bool IsInstalled { get; }
    void InstallFromZip(string zipPath);
    void EnsureProfiles();
}

//Which package versions this machine has installed. The TazUO launcher has no version file we can read, so we remember it
public interface IPackageState
{
    Version? TazUOVersion { get; }
    void SetTazUOVersion(Version version);
}

//tazUO is null when the TazUO launcher is turned off. The manifest's signature is still checked then, so a server
//whose packages can't be trusted is reported either way
public class PackageUpdater(FileServerClient server, IReadOnlyCollection<string> trustedKeys, string rid,
    PackageDownloader downloader, ITazUOInstaller? tazUO, IPackageState state) : IPackageUpdater
{
    public async Task<PackageUpdates> CheckAsync(CancellationToken cancellationToken)
    {
        var manifest = await server.GetPackageManifestAsync(trustedKeys, cancellationToken);
        if (manifest == null) return new PackageUpdates(null, ManifestMissing: true);

        //Installed without a recorded version (installed before packages existed) counts as out of date, so it's replaced once
        var package = tazUO == null ? null : manifest.Find(PackageRole.TazUO, rid);
        if (package != null && tazUO!.IsInstalled && !PackageVersions.IsNewer(package, state.TazUOVersion ?? new Version(0, 0)))
            package = null;

        return new PackageUpdates(package, ManifestMissing: false);
    }

    public async Task ApplyAsync(PackageUpdates updates, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken)
    {
        if (tazUO == null) return;

        if (updates.TazUO != null)
        {
            await InstallAsync(updates.TazUO, progress, error, cancellationToken);
        }
        else if (!tazUO.IsInstalled)
        {
            //Nothing to install it from: the server has no packages, or none for this platform
            Console.WriteLine($"The server has no tazuo package for {rid}, so the TazUO launcher can't be installed.");
            error(new UpdateErrorInfo(UpdateError.TazUOInstallFailed));
        }

        if (tazUO.IsInstalled)
            tazUO.EnsureProfiles();
    }

    private async Task InstallAsync(PackageEntry package, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken)
    {
        var path = await downloader.DownloadAsync(package, UpdatePhase.InstallingTazUO, progress, cancellationToken);
        if (path == null)
        {
            error(new UpdateErrorInfo(UpdateError.TazUOInstallFailed));
            return;
        }

        try
        {
            tazUO!.InstallFromZip(path);
            state.SetTazUOVersion(PackageVersions.TryVersion(package)!);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(e.ToString());
            error(new UpdateErrorInfo(UpdateError.TazUOInstallFailed));
        }
        finally
        {
            downloader.Delete(path);
        }
    }
}
