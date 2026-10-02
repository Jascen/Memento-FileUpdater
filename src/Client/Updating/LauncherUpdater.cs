using FileUpdaterPackages;

namespace FileUpdaterClient.Updating;

//Replaces the running launcher with the one in a downloaded, verified package and restarts into it
public interface ISelfUpdater
{
    //packagePath is the verified zip. Returns true once the swap is under way and the process is restarting.
    //Returns false when nothing was applied, and the launcher carries on with the old version
    Task<bool> ApplyAndRestartAsync(string packagePath, Version newVersion, CancellationToken cancellationToken);
}

//Finds and applies a newer version of this launcher from the server's signed packages. It has nothing to do with the
//install folder, so one instance lives as long as the launcher does, apart from UpdateService. A newer launcher never
//holds anything up: the player decides when to apply it with UpdateAsync.
//Reports through the events below, which can fire on background threads
public class LauncherUpdater(FileServerClient server, IReadOnlyCollection<string> trustedKeys, string rid, Version launcherVersion,
    PackageDownloader downloader, ISelfUpdater selfUpdater)
{
    private CancellationTokenSource _cancellation = new();
    private Version? _declinedVersion; //A version the self-updater declined to apply, not offered again until the launcher restarts

    public event Action<UpdateProgress>? ProgressChanged;
    public event Action<UpdateErrorInfo>? ErrorOccurred;

    //A newer version of this launcher found by the last refresh, or null
    public PackageEntry? Available { get; private set; }

    public void Cancel() => _cancellation.Cancel();

    //Looks at the server's signed manifest for a newer launcher. Meant for checking at launch and now and then while
    //the launcher stays open, so failures are only logged and what was known is kept
    public async Task RefreshAsync()
    {
        try
        {
            var manifest = await server.GetPackageManifestAsync(trustedKeys, CancellationToken.None);
            var launcher = manifest?.Find(PackageRole.Launcher, rid);
            if (launcher != null && !PackageVersions.IsNewer(launcher, launcherVersion)) launcher = null; //Only upgrades, never downgrades
            if (launcher != null && PackageVersions.Same(PackageVersions.TryVersion(launcher), _declinedVersion)) launcher = null;
            Available = launcher;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't check for a launcher update: {e.Message}");
        }
    }

    //Downloads the launcher update found by the last refresh and replaces this launcher with it.
    //Finished means there was nothing to do, Restarting that the new launcher is taking over, Failed that the update
    //couldn't be applied (ErrorOccurred says why, unless the self-updater simply declined) and this launcher carries on
    public async Task<UpdateResult> UpdateAsync()
    {
        var launcher = Available;
        if (launcher == null) return UpdateResult.Finished;

        if (_cancellation.IsCancellationRequested) _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        try
        {
            var version = PackageVersions.TryVersion(launcher)!;
            var path = await downloader.DownloadAsync(launcher, UpdatePhase.UpdatingLauncher,
                progress => ProgressChanged?.Invoke(progress), token);
            if (path == null)
            {
                //Still offered, the download may just have been corrupted
                ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.SelfUpdateFailed));
                return UpdateResult.Failed;
            }

            try
            {
                if (await selfUpdater.ApplyAndRestartAsync(path, version, token)) return UpdateResult.Restarting;
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                Console.WriteLine(e.ToString());
                ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.SelfUpdateFailed));
            }

            //Not applied: don't offer the same version again until the launcher is restarted
            _declinedVersion = version;
            Available = null;
            downloader.Delete(path);
            return UpdateResult.Failed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return UpdateResult.Cancelled;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            ErrorOccurred?.Invoke(new UpdateErrorInfo(UpdateError.SelfUpdateFailed));
            return UpdateResult.Failed;
        }
    }
}
