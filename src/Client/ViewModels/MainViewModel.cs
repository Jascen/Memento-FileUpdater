using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileUpdaterClient.Config;
using FileUpdaterClient.Updating;
using FileUpdaterClient.UserSettings;

namespace FileUpdaterClient.ViewModels;

//Launcher state and actions. The window forwards clicks here and shows what these properties say.
//The update services report from background threads, so their events are posted to the UI thread before touching properties.
public class MainViewModel : INotifyPropertyChanged
{
    private readonly IUserSettings _settings;
    private readonly Func<string, InstallSession> _openFolder;
    private readonly LauncherUpdater? _launcherUpdater; //Null when the launcher has no trusted signing key
    private readonly IUiThread _ui;
    private IMainView? _view;
    private InstallSession? _session; //Created once a usable install folder is known

    private LauncherState _state = LauncherState.Idle;
    private string _errorMessage = string.Empty;
    private bool _isDialogOpen;
    private double _progress;
    private double _fileProgress;
    private string _progressText = Strings.CheckingForUpdates;
    private string _fileProgressText = string.Empty;
    private bool _tazUOInstalled;
    private bool _launcherUpdateAvailable;
    private string _launcherUpdateText = string.Empty;
    private string? _dismissedLauncherVersion; //A launcher version the player chose "Not now" for, hidden until the launcher restarts
    public event PropertyChangedEventHandler? PropertyChanged;

    //openFolder builds what belongs to an install folder, and is called again whenever the player picks another one
    public MainViewModel(IUserSettings settings, Func<string, InstallSession> openFolder, LauncherUpdater? launcherUpdater, IUiThread ui)
    {
        _settings = settings;
        _openFolder = openFolder;
        _launcherUpdater = launcherUpdater;
        _ui = ui;

        if (launcherUpdater != null)
        {
            launcherUpdater.ProgressChanged += progress => _ui.Post(() => ShowProgress(progress));
            launcherUpdater.ErrorOccurred += error => _ui.Post(() => ShowError(error));
        }
    }

    public IReadOnlyList<NavLink> Links { get; } = LauncherConfig.Links;
    public string Title => LauncherConfig.Title;
    public string TitleColor => LauncherConfig.TitleColor;
    public string Subtitle => LauncherConfig.Subtitle;
    public string SubtitleColor => LauncherConfig.SubtitleColor;

    public LauncherState State
    {
        get => _state;
        private set
        {
            if (!SetField(ref _state, value)) return;
            OnPropertyChanged(nameof(IsUpdating));
            OnPropertyChanged(nameof(DownloadsReady));
            OnPropertyChanged(nameof(RetryReady));
            OnPropertyChanged(nameof(FilesVerified));
            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(MainButtonText));
            OnPropertyChanged(nameof(MainButtonEnabled));
            OnPropertyChanged(nameof(MainButtonVisible));
        }
    }

    public bool IsUpdating => State == LauncherState.Working;

    /// <summary>True once a check found updates, until the player clicks the main button to download them.</summary>
    public bool DownloadsReady => State == LauncherState.UpdatesReady;

    /// <summary>True when the last run failed or skipped files, so a Retry button is shown next to the error.</summary>
    public bool RetryReady => State == LauncherState.Failed;

    /// <summary>True once every file was checked against the server and any updates downloaded.</summary>
    public bool FilesVerified => State == LauncherState.Verified;

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

    public bool TazUOInstalled
    {
        get => _tazUOInstalled;
        private set
        {
            if (!SetField(ref _tazUOInstalled, value)) return;
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

    public bool CanPlay => !IsUpdating && !DownloadsReady && TazUOInstalled;

    //The center button downloads pending updates first, then becomes the play button
    public string MainButtonText => DownloadsReady ? Strings.DownloadButton : Strings.PlayText;
    public bool MainButtonEnabled => DownloadsReady || CanPlay;
    public bool MainButtonVisible => DownloadsReady || TazUOLauncherConfig.Enabled; //Without TazUO it only appears to download updates

    //Called once the window is open
    public async Task StartAsync(IMainView view)
    {
        _view = view;
        _settings.Load();
        var launcherCheck = RefreshLauncherUpdateAsync(); //Doesn't depend on the install folder, so it runs alongside

        if (_settings.EnsureInstallPathUsable(out var error))
        {
            await StartWithFolderAsync();
        }
        else
        {
            //Ask for another folder in Settings before checking anything
            ErrorMessage = error;
            ProgressText = Strings.NoFolderChosen;
            await OpenSettingsAsync(error);
        }

        await launcherCheck;
    }

    //Looks for a newer version of this launcher. Called at launch, and by the app now and then while the launcher is open
    public async Task RefreshLauncherUpdateAsync()
    {
        if (_launcherUpdater == null) return;

        await _launcherUpdater.RefreshAsync();
        await _ui.InvokeAsync(ShowLauncherUpdate);
    }

    private void ShowLauncherUpdate()
    {
        var version = _launcherUpdater?.Available?.Version;
        LauncherUpdateAvailable = version != null && version != _dismissedLauncherVersion;
        if (version != null)
            LauncherUpdateText = string.Format(Strings.LauncherUpdateAvailable, version);
    }

    public void DismissLauncherUpdate()
    {
        _dismissedLauncherVersion = _launcherUpdater?.Available?.Version;
        LauncherUpdateAvailable = false;
    }

    //Downloads the newer launcher and restarts into it. The player asked for this, so it only ever happens on a click
    public async Task UpdateLauncherAsync()
    {
        if (_launcherUpdater == null || _view == null) return;

        if (IsUpdating)
        {
            if (!await _view.ConfirmAsync(Strings.LauncherUpdateBusyTitle, Strings.LauncherUpdateBusyMessage,
                    Strings.LauncherUpdateBusyConfirm, Strings.CancelText))
                return;

            CancelUpdate();
            for (var waited = 0; IsUpdating && waited < 100; waited++) //The cancelled run ends shortly
                await Task.Delay(100);
            if (IsUpdating) return;
        }

        //The launcher update borrows the progress bar, then hands things back as they were
        var session = _session;
        var previousState = State;
        var previousText = ProgressText;
        BeginRun();

        var result = await _launcherUpdater.UpdateAsync();
        await _ui.InvokeAsync(() =>
        {
            if (result == UpdateResult.Restarting)
            {
                ProgressText = Strings.Restarting;
                _view.ShutdownForRestart(); //The updater is waiting for this process to end
                return;
            }

            ShowLauncherUpdate();
            if (session != _session) return; //The folder changed meanwhile, and its own check owns the state now

            //Not applied (or cancelled): carry on with this version as before. A failure already set the error message
            ProgressText = previousText;
            Progress = 0;
            State = previousState;
        });
    }

    private async Task StartWithFolderAsync()
    {
        var session = _openFolder(_settings.InstallPath);
        //A session that has been replaced (the folder changed) may still be winding down, so its updates are ignored
        session.Updates.ProgressChanged += progress => _ui.Post(() => { if (session == _session) ShowProgress(progress); });
        session.Updates.FileProgressChanged += file => _ui.Post(() => { if (session == _session) ShowFileProgress(file); });
        session.Updates.ErrorOccurred += error => _ui.Post(() => { if (session == _session) ShowError(error); });
        _session = session;
        TazUOInstalled = session.TazUO?.IsInstalled ?? false;

        //Start from a clean slate, since nothing from a previous folder applies
        State = LauncherState.Idle;
        ErrorMessage = string.Empty;
        Progress = 0;
        FileProgress = 0;
        FileProgressText = string.Empty;
        if (LauncherConfig.TrustedPublicKeys.Length == 0 && TazUOLauncherConfig.Enabled)
            ErrorMessage = Strings.PackagesNotConfigured;

        if (_settings.Preferences.VerifyOnLaunch)
            await CheckForUpdatesAsync();
        else
            ProgressText = Strings.NotVerified;
    }

    //Every check, download and launcher update starts by clearing what the last one left on screen
    private void BeginRun()
    {
        State = LauncherState.Working;
        ErrorMessage = string.Empty;
        Progress = 0;
        FileProgress = 0;
        FileProgressText = string.Empty;
    }

    public async Task<UpdateResult?> CheckForUpdatesAsync()
    {
        var session = _session;
        if (session == null || IsUpdating) return null; //No folder yet, or already busy

        BeginRun();
        var result = await session.Updates.CheckAsync();
        if (session != _session) return null; //The folder changed meanwhile, so this result is stale
        await _ui.InvokeAsync(() => ShowResult(result, wasDownload: false)); //Queued behind any progress still waiting to be shown
        return result;
    }

    //Checks again and, if anything is still missing or out of date, downloads it straight away
    public async Task RetryAsync()
    {
        var result = await CheckForUpdatesAsync();
        if (result is UpdateResult.UpdatesReady or UpdateResult.PackagesReady)
            await DownloadUpdatesAsync();
    }

    //The center button downloads pending updates first, then launches the game
    public async Task MainButtonAsync()
    {
        if (DownloadsReady)
            await DownloadUpdatesAsync();
        else
            await PlayAsync();
    }

    private async Task DownloadUpdatesAsync()
    {
        var session = _session;
        if (session == null || IsUpdating) return; //Ignore repeat clicks

        BeginRun();
        var result = await session.Updates.DownloadAsync();
        if (session != _session) return; //The folder changed meanwhile, so this result is stale
        await _ui.InvokeAsync(() => ShowResult(result, wasDownload: true));
    }

    //Opens the TazUO launcher, first asking the player to confirm if the files weren't fully verified
    private async Task PlayAsync()
    {
        var tazUO = _session?.TazUO;
        if (tazUO == null || _view == null) return;

        if (!FilesVerified && _settings.Preferences.WarnIfNotVerified
            && !await _view.ConfirmAsync(Strings.UnverifiedTitle, Strings.UnverifiedMessage, Strings.PlayAnyway, Strings.CancelText))
            return;

        try
        {
            tazUO.Start();
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

        if (!_settings.TrySetInstallPath(newFolder, out var error))
        {
            ErrorMessage = error;
            return;
        }

        _session?.Updates.Cancel(); //Stop anything still working in the old folder
        await StartWithFolderAsync();
    }

    public void CancelUpdate()
    {
        _session?.Updates.Cancel();
        _launcherUpdater?.Cancel();
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
            case UpdatePhase.InstallingTazUO:
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
            UpdateError.TazUOInstallFailed => Strings.TazUOError,
            UpdateError.SelfUpdateFailed => Strings.SelfUpdateError,
            UpdateError.PackagesUntrusted => Strings.PackagesUntrustedError,
            UpdateError.InsecureServer => Strings.InsecureServerError,
            _ => Strings.UnknownError,
        };
    }

    private void ShowResult(UpdateResult result, bool wasDownload)
    {
        switch (result)
        {
            case UpdateResult.UpdatesReady:
            case UpdateResult.PackagesReady:
                Progress = 0;
                ProgressText = result == UpdateResult.UpdatesReady ? Strings.UpdatesReady : Strings.PackagesReady;
                State = LauncherState.UpdatesReady;
                break;
            case UpdateResult.Finished:
                var failed = _session?.Updates.FailedFiles.Count ?? 0;
                Progress = 100;
                FileProgress = 100;
                ProgressText = failed > 0 ? string.Format(Strings.FinishedWithFailures, failed) : Strings.Finished;
                FileProgressText = string.Empty;
                State = failed > 0 ? LauncherState.Failed : LauncherState.Verified;
                break;
            case UpdateResult.Failed:
                //The error line says why. Downloads that were waiting are re-found by Retry
                ProgressText = Strings.CheckFailed;
                State = LauncherState.Failed;
                break;
            case UpdateResult.Cancelled:
                //A cancelled download leaves known out of date files, so offer it again. A cancelled check just leaves them unverified
                ProgressText = Strings.Cancelled;
                State = wasDownload ? LauncherState.UpdatesReady : LauncherState.Idle;
                break;
        }

        TazUOInstalled = _session?.TazUO?.IsInstalled ?? false;
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
