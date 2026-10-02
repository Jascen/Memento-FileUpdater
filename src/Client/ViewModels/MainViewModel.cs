using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using FileUpdaterClient.Updating;
using FileUpdaterClient.Config;
using FileUpdaterClient.UserSettings;

namespace FileUpdaterClient.ViewModels;

//What the launcher window shows, and its actions. The work itself is done by an UpdateSession, one per install folder:
//this turns the session's state into text and buttons. The session reports from background threads, so its events are
//posted to the UI thread before touching properties.
public class MainViewModel : INotifyPropertyChanged
{
    private IMainView? _view;
    private UpdateSession? _session; //Created once a usable install folder is known

    private string _errorMessage = string.Empty;
    private bool _isDialogOpen;
    private bool _downloadsReady;
    private bool _retryReady;
    private double _progress;
    private double _fileProgress;
    private string _progressText = Strings.CheckingForUpdates;
    private string _fileProgressText = string.Empty;
    private bool _isUpdating;
    private bool _filesVerified;
    private bool _launcherInstalled;
    private bool _launcherUpdateAvailable;
    private string _launcherUpdateText = string.Empty;
    private string? _dismissedLauncherVersion; //A launcher version the player chose "Not now" for, hidden until the launcher restarts
    private DispatcherTimer? _launcherCheckTimer;
    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<NavLink> Links { get; } = LauncherConfig.Links;
    public string Title => Strings.Title;
    public string Subtitle => Strings.Subtitle;

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    //Set by the window while a dialog is open, to dim the launcher behind it
    public bool IsDialogOpen
    {
        get => _isDialogOpen;
        set => SetField(ref _isDialogOpen, value);
    }

    /// <summary>True once the launch check found updates, until the player clicks the main button to download them.</summary>
    public bool DownloadsReady
    {
        get => _downloadsReady;
        private set
        {
            if (!SetField(ref _downloadsReady, value)) return;
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(MainButtonText));
            OnPropertyChanged(nameof(MainButtonEnabled));
            OnPropertyChanged(nameof(MainButtonVisible));
        }
    }

    /// <summary>True when the last run failed or skipped files, so a Retry button is shown next to the error.</summary>
    public bool RetryReady
    {
        get => _retryReady;
        private set => SetField(ref _retryReady, value);
    }

    public string RetryText => Strings.RetryText;

    /// <summary>Overall progress across all files (top bar), 0-100.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    /// <summary>Progress of the file currently being downloaded (bottom bar), 0-100.</summary>
    public double FileProgress
    {
        get => _fileProgress;
        private set => SetField(ref _fileProgress, value);
    }

    public string FileProgressText
    {
        get => _fileProgressText;
        private set => SetField(ref _fileProgressText, value);
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (!SetField(ref _isUpdating, value)) return;
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(MainButtonEnabled));
        }
    }

    /// <summary>True once every file was checked against the server and any updates downloaded.</summary>
    public bool FilesVerified
    {
        get => _filesVerified;
        private set => SetField(ref _filesVerified, value);
    }

    public bool LauncherInstalled
    {
        get => _launcherInstalled;
        private set
        {
            if (!SetField(ref _launcherInstalled, value)) return;
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(MainButtonEnabled));
        }
    }

    /// <summary>True while a newer version of this launcher is available and hasn't been dismissed. Purely optional to act on.</summary>
    public bool LauncherUpdateAvailable
    {
        get => _launcherUpdateAvailable;
        private set => SetField(ref _launcherUpdateAvailable, value);
    }

    public string LauncherUpdateText
    {
        get => _launcherUpdateText;
        private set => SetField(ref _launcherUpdateText, value);
    }

    public string UpdateLauncherText => Strings.UpdateLauncherButton;
    public string DismissLauncherUpdateText => Strings.LauncherUpdateLater;

    public bool CanPlay => !IsUpdating && !DownloadsReady && LauncherInstalled;

    //The center button downloads pending updates first, then becomes the play button
    public string MainButtonText => DownloadsReady ? Strings.DownloadButton : Strings.PlayText;
    public bool MainButtonEnabled => DownloadsReady || CanPlay;
    public bool MainButtonVisible => DownloadsReady || TazUOLauncherConfig.Enabled; //Without TazUO it only appears to download updates

    //Called once the window is open
    public async Task StartAsync(IMainView view)
    {
        _view = view;
        InstallLocation.Load();
        Preferences.Load();
        if (!InstallLocation.EnsureUsable(out var error))
        {
            //Ask for another folder in Settings before checking anything
            ErrorMessage = error;
            ProgressText = Strings.NoFolderChosen;
            await OpenSettingsAsync(error);
            return;
        }

        await StartWithFolderAsync();
    }

    //Now and then while the launcher is open, looks for a newer version of itself. Not while an update is running
    private void StartLauncherChecks()
    {
        if (_launcherCheckTimer != null || LauncherConfig.TrustedPublicKeys.Length == 0) return;

        _launcherCheckTimer = new DispatcherTimer { Interval = LauncherConfig.PackageCheckInterval };
        _launcherCheckTimer.Tick += async (_, _) => await RefreshLauncherUpdateAsync();
        _launcherCheckTimer.Start();
    }

    private async Task RefreshLauncherUpdateAsync()
    {
        var session = _session;
        if (session == null) return;

        await session.RefreshLauncherUpdateAsync();
        if (session == _session && !session.IsBusy) ShowLauncherUpdate();
    }

    private void ShowLauncherUpdate()
    {
        var version = _session?.LauncherUpdateVersion;
        LauncherUpdateAvailable = version != null && version != _dismissedLauncherVersion;
        if (version != null)
            LauncherUpdateText = string.Format(Strings.LauncherUpdateAvailable, version);
    }

    public void DismissLauncherUpdate()
    {
        _dismissedLauncherVersion = _session?.LauncherUpdateVersion;
        LauncherUpdateAvailable = false;
    }

    //Downloads the newer launcher and restarts into it. The player asked for this, so it only ever happens on a click
    public async Task UpdateLauncherAsync()
    {
        var session = _session;
        if (session == null || _view == null) return;

        var wasBusy = session.IsBusy;
        if (wasBusy && !await _view.ConfirmAsync(Strings.LauncherUpdateBusyTitle, Strings.LauncherUpdateBusyMessage,
                Strings.LauncherUpdateBusyConfirm, Strings.CancelText))
            return;

        var previousText = wasBusy ? Strings.Cancelled : ProgressText; //The running update is cancelled first
        ErrorMessage = string.Empty;
        ResetProgress();

        var result = await session.UpdateLauncherAsync();
        if (result == null || session != _session) return; //Couldn't start, or the folder changed meanwhile

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (result == UpdateResult.Restarting)
            {
                ProgressText = Strings.Restarting;
                _view.ShutdownForRestart(); //The updater is waiting for this process to end
                return;
            }

            //Not applied (or cancelled): carry on with this version as before. A failure already set the error message
            ProgressText = previousText;
            Progress = 0;
            ShowLauncherUpdate();
        });
    }

    private async Task StartWithFolderAsync()
    {
        _session?.Dispose(); //Stop anything still working in the old folder
        var session = UpdateSession.Create(InstallLocation.Path);
        session.ProgressChanged += progress => PostFor(session, () => ShowProgress(progress));
        session.FileProgressChanged += file => PostFor(session, () => ShowFileProgress(file));
        session.ErrorOccurred += error => PostFor(session, () => ShowError(error));
        session.StateChanged += ShowState;
        _session = session;

        //Start from a clean slate, since nothing from a previous folder applies
        ShowState();
        ErrorMessage = string.Empty;
        ResetProgress();
        if (session.HasClient && !session.PackagesConfigured)
            ErrorMessage = Strings.PackagesNotConfigured;

        StartLauncherChecks();
        if (Preferences.Current.VerifyOnLaunch)
        {
            await CheckForUpdatesAsync();
        }
        else
        {
            ProgressText = Strings.NotVerified;
            await RefreshLauncherUpdateAsync();
        }
    }

    public Task CheckForUpdatesAsync() => RunAsync(session => session.CheckAsync());

    //Checks again and, if anything is still missing or out of date, downloads it straight away
    public Task RetryAsync() => RunAsync(session => session.RetryAsync());

    //The center button downloads pending updates first, then launches the game
    public async Task MainButtonAsync()
    {
        if (DownloadsReady)
            await RunAsync(session => session.DownloadAsync());
        else
            await PlayAsync();
    }

    private async Task RunAsync(Func<UpdateSession, Task<UpdateResult?>> run)
    {
        var session = _session;
        if (session == null || session.IsBusy) return; //No folder yet, or already running

        ErrorMessage = string.Empty;
        ResetProgress();

        var result = await run(session);
        if (result == null || session != _session) return; //Didn't run, or the folder changed meanwhile, so this result is stale
        await Dispatcher.UIThread.InvokeAsync(() => ShowResult(result.Value)); //Queued behind any progress still waiting to be shown
    }

    //Opens the TazUO launcher, first asking the player to confirm if the files weren't fully verified
    private async Task PlayAsync()
    {
        var session = _session;
        if (session is not { HasClient: true } || _view == null) return;

        if (!FilesVerified && Preferences.Current.WarnIfNotVerified
            && !await _view.ConfirmAsync(Strings.UnverifiedTitle, Strings.UnverifiedMessage, Strings.PlayAnyway, Strings.CancelText))
            return;

        try
        {
            session.StartClient();
        }
        catch (Exception ex)
        {
            ErrorMessage = Strings.LaunchError;
            Console.WriteLine(ex.Message);
        }
    }

    public async Task OpenLinkAsync(NavLink link)
    {
        if (link.Target == NavLink.VerifyAction)
            await CheckForUpdatesAsync();
        else if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
            _view?.OpenUrl(uri);
    }

    public async Task OpenSettingsAsync(string folderError = "")
    {
        if (_view == null) return;

        var newFolder = await _view.ShowSettingsAsync(folderError);
        if (newFolder == null) return;

        if (!InstallLocation.TrySet(newFolder, out var error))
        {
            ErrorMessage = error;
            return;
        }

        await StartWithFolderAsync();
    }

    public void CancelUpdate() => _session?.Cancel();

    //Runs on the UI thread, unless the session has been replaced by then
    private void PostFor(UpdateSession session, Action action) =>
        Dispatcher.UIThread.Post(() => { if (session == _session) action(); });

    private void ResetProgress()
    {
        Progress = 0;
        FileProgress = 0;
        FileProgressText = string.Empty;
    }

    //Copies the session's state to the properties the window binds to
    private void ShowState()
    {
        IsUpdating = _session?.IsBusy ?? false;
        DownloadsReady = _session?.DownloadsReady ?? false;
        FilesVerified = _session?.FilesVerified ?? false;
        RetryReady = _session?.RetryReady ?? false;
        LauncherInstalled = _session?.ClientInstalled ?? false;
    }

    private void ShowProgress(UpdateProgress progress)
    {
        switch (progress.Phase)
        {
            case UpdatePhase.RequestingFileList:
                Progress = 0;
                ProgressText = Strings.ReqFileList;
                break;
            case UpdatePhase.Comparing:
                Progress = progress.Percent;
                ProgressText = string.Format(Strings.ComparingFiles, progress.Done, progress.Total);
                break;
            case UpdatePhase.Downloading:
                Progress = progress.Percent;
                ProgressText = progress.BytesTotal > 0
                    ? string.Format(Strings.DownloadingBytes, Units.Bytes(progress.BytesDone), Units.Bytes(progress.BytesTotal),
                        Units.Speed(progress.BytesPerSecond), progress.TimeLeft is { } left ? Units.Duration(left) : "?")
                    : string.Format(Strings.DownloadingFiles, progress.Done, progress.Total, Units.Speed(progress.BytesPerSecond));
                break;
            case UpdatePhase.UpdatingLauncher:
                Progress = progress.Percent;
                ProgressText = progress.BytesTotal > 0 && progress.Done == 0
                    ? string.Format(Strings.DownloadingLauncher, Units.Bytes(progress.BytesDone), Units.Bytes(progress.BytesTotal))
                    : Strings.UpdatingLauncher;
                break;
            case UpdatePhase.InstallingClient:
                Progress = progress.Percent;
                ProgressText = progress.BytesTotal > 0 && progress.Done == 0
                    ? string.Format(Strings.DownloadingTazUO, Units.Bytes(progress.BytesDone), Units.Bytes(progress.BytesTotal))
                    : Strings.InstallingTazUO;
                break;
        }
    }

    private void ShowFileProgress(FileProgress file)
    {
        FileProgress = file.Percent;
        FileProgressText = string.Format(Strings.CurrentFile, file.FileName);
    }

    private void ShowError(UpdateErrorInfo error)
    {
        ErrorMessage = error.Error switch
        {
            UpdateError.ConnectionFailed => Strings.ConError,
            UpdateError.BadData => Strings.BadData,
            UpdateError.FileFailed => string.Format(Strings.FileFailedError, error.FileName),
            UpdateError.FileLocked => string.Format(Strings.FileLockedError, error.FileName),
            UpdateError.LauncherFailed => Strings.TazUOError,
            UpdateError.SelfUpdateFailed => Strings.SelfUpdateError,
            UpdateError.PackagesUntrusted => Strings.PackagesUntrustedError,
            _ => Strings.UnknownError,
        };
    }

    //Says how the run ended. The session's state (busy, downloads ready..) is already shown by ShowState
    private void ShowResult(UpdateResult result)
    {
        switch (result)
        {
            case UpdateResult.UpdatesReady:
            case UpdateResult.PackagesReady:
                Progress = 0;
                ProgressText = result == UpdateResult.UpdatesReady ? Strings.UpdatesReady : Strings.PackagesReady;
                break;
            case UpdateResult.Restarting:
                //The new launcher takes over, so the window stays busy until this process exits
                ProgressText = Strings.Restarting;
                return;
            case UpdateResult.Finished:
                var failed = _session?.FailedFileCount ?? 0;
                Progress = 100;
                FileProgress = 100;
                ProgressText = failed > 0 ? string.Format(Strings.FinishedWithFailures, failed)
                    : _session is { HasClient: true, ClientInstalled: false } ? Strings.FinishedNoClient //Nothing to play yet, so don't claim all is well
                    : Strings.Finished;
                FileProgressText = string.Empty;
                break;
            case UpdateResult.Failed:
                ProgressText = Strings.CheckFailed; //The error line says why
                break;
            case UpdateResult.Cancelled:
                ProgressText = Strings.Cancelled;
                break;
        }

        ShowState(); //The client may have just been installed
        ShowLauncherUpdate();
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
