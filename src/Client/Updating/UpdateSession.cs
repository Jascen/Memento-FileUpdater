using FileUpdaterClient.Config;
using FileUpdaterClient.TazUO;
using FileUpdaterClient.UserSettings;

namespace FileUpdaterClient.Updating;

//Everything the launcher does for one install folder: checking, downloading, the TazUO launcher and this launcher's own updates.
//Choosing another folder replaces the session. Knows nothing about the UI: its events fire on background threads, while its
//methods and state are used from one thread (the UI's). One run at a time; a method called while busy returns null.
public sealed class UpdateSession : IDisposable
{
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(10); //How long a launcher update waits for a cancelled run to end

    //Where launcher and client packages are saved while they download
    private static readonly string PackageDownloadFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LauncherConfig.AppDataFolder, "downloads");

    private readonly UpdateService _updates;
    private readonly IGameClient? _client;
    private bool _disposed;

    public event Action<UpdateProgress>? ProgressChanged;
    public event Action<FileProgress>? FileProgressChanged;
    public event Action<UpdateErrorInfo>? ErrorOccurred;

    //The state below changed. Fires on the thread that called into the session
    public event Action? StateChanged;

    //client is null when TazUOLauncherConfig.Enabled is off. packagesConfigured is false when there's no trusted signing key,
    //so the client can never be installed
    public UpdateSession(UpdateService updates, IGameClient? client, bool packagesConfigured)
    {
        _updates = updates;
        _client = client;
        PackagesConfigured = packagesConfigured;
        //Once replaced, a session that is still winding down goes quiet
        updates.ProgressChanged += progress => { if (!_disposed) ProgressChanged?.Invoke(progress); };
        updates.FileProgressChanged += file => { if (!_disposed) FileProgressChanged?.Invoke(file); };
        updates.ErrorOccurred += error => { if (!_disposed) ErrorOccurred?.Invoke(error); };
    }

    //Builds a session for installPath from the launcher's configuration
    public static UpdateSession Create(string installPath)
    {
        var client = TazUOLauncherConfig.Enabled ? new TazUOLauncher(installPath) : null;
        var localFiles = new LocalFiles(new System.IO.Abstractions.FileSystem());
        var server = new FileServerClient(LauncherConfig.UpdateUrl, localFiles);
        //Without a trusted signing key nothing the server hosts as a package is ever installed
        var packages = LauncherConfig.TrustedPublicKeys.Length == 0 ? null
            : new PackageUpdater(server, localFiles, LauncherConfig.TrustedPublicKeys, PlatformId.Current, LauncherVersion.Current,
                PackageDownloadFolder, new SelfUpdater(), client, new PackageState());
        var updates = new UpdateService(server, localFiles, installPath, packages, LauncherConfig.KeepLocalFiles);
        return new UpdateSession(updates, client, packages != null);
    }

    /// <summary>True while a check, download or launcher update runs. Stays true once the launcher is restarting.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>True once a check found updates, until they're downloaded. A cancelled download leaves it set.</summary>
    public bool DownloadsReady { get; private set; }

    /// <summary>True once every file was checked against the server and any updates downloaded.</summary>
    public bool FilesVerified { get; private set; }

    /// <summary>True when the last run failed or skipped files, so it's worth trying again.</summary>
    public bool RetryReady { get; private set; }

    public int FailedFileCount => _updates.FailedFiles.Count;

    public bool HasClient => _client != null;
    public bool ClientInstalled => _client?.IsInstalled ?? false;
    public bool PackagesConfigured { get; }

    /// <summary>The version of a newer launcher found by the last check or refresh, or null.</summary>
    public string? LauncherUpdateVersion => _updates.LauncherUpdate?.Version;

    public void Cancel() => _updates.Cancel();

    public Task<UpdateResult?> CheckAsync() => RunAsync(_updates.CheckAsync, isDownload: false);

    public Task<UpdateResult?> DownloadAsync() => RunAsync(_updates.DownloadAsync, isDownload: true);

    //Checks again and, if anything is still missing or out of date, downloads it straight away
    public async Task<UpdateResult?> RetryAsync()
    {
        var result = await CheckAsync();
        return result is UpdateResult.UpdatesReady or UpdateResult.PackagesReady ? await DownloadAsync() : result;
    }

    private async Task<UpdateResult?> RunAsync(Func<Task<UpdateResult>> run, bool isDownload)
    {
        if (_disposed || IsBusy) return null;

        IsBusy = true;
        DownloadsReady = false;
        FilesVerified = false;
        RetryReady = false;
        OnStateChanged();

        var result = await run();
        switch (result)
        {
            case UpdateResult.UpdatesReady:
            case UpdateResult.PackagesReady:
                DownloadsReady = true;
                break;
            case UpdateResult.Restarting:
                return result; //The new launcher takes over, so this one stays busy until the process exits
            case UpdateResult.Finished:
                FilesVerified = _updates.FilesVerified;
                RetryReady = FailedFileCount > 0;
                break;
            case UpdateResult.Failed:
                RetryReady = true; //Downloads that were waiting are re-found by a retry
                break;
            case UpdateResult.Cancelled:
                //A cancelled download leaves known out of date files, so offer it again. A cancelled check just leaves them unverified
                DownloadsReady = isDownload;
                break;
        }

        IsBusy = false;
        OnStateChanged();
        return result;
    }

    //Downloads the newer launcher and restarts into it. Anything running is cancelled first, so ask the player before calling
    //this while busy. Null when it couldn't start; otherwise Restarting, or this launcher carries on with the state it had
    public async Task<UpdateResult?> UpdateLauncherAsync()
    {
        if (_disposed) return null;

        if (IsBusy)
        {
            Cancel();
            var waited = TimeSpan.Zero;
            for (; IsBusy && waited < CancelWait; waited += TimeSpan.FromMilliseconds(100)) //The cancelled run ends shortly
                await Task.Delay(100);
            if (IsBusy || _disposed) return null;
        }

        var wasReady = DownloadsReady;
        IsBusy = true;
        DownloadsReady = false;
        RetryReady = false;
        OnStateChanged();

        var result = await _updates.UpdateLauncherAsync();
        if (result == UpdateResult.Restarting) return result; //Stays busy until the process exits

        DownloadsReady = wasReady;
        IsBusy = false;
        OnStateChanged();
        return result;
    }

    //Looks for a newer version of this launcher without checking any files. Skipped while busy
    public async Task RefreshLauncherUpdateAsync()
    {
        if (_disposed || IsBusy) return;
        await _updates.RefreshLauncherUpdateAsync();
    }

    private void OnStateChanged()
    {
        if (!_disposed) StateChanged?.Invoke();
    }

    //Opens the TazUO launcher. Throws if it can't be started
    public void StartClient() => _client?.Start();

    //Stops anything still running and silences the session, once another folder is chosen
    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
