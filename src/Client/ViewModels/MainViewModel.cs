using System.ComponentModel;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using FileUpdaterClient.Updating;
using FileUpdaterClient.Config;
using FileUpdaterClient.TazUO;
using FileUpdaterClient.UserSettings;

namespace FileUpdaterClient.ViewModels;

//Launcher state and actions. The window forwards clicks here and shows what these properties say.
//UpdateService reports from background threads, so its events are posted to the UI thread before touching properties.
public class MainViewModel : INotifyPropertyChanged
{
    private IMainView? _view;
    private UpdateService? _updates; //Created once a usable install folder is known
    private TazUOLauncher? _launcher; //Null when TazUOLauncherConfig.Enabled is off

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
    public event PropertyChangedEventHandler? PropertyChanged;

    //Where launcher and client packages are saved while they download
    private static readonly string PackageDownloadFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LauncherConfig.AppDataFolder, "downloads");

    public IReadOnlyList<NavLink> Links { get; } = LauncherConfig.Links;
    public string Title => LauncherConfig.Title;
    public string TitleColor => LauncherConfig.TitleColor;
    public string Subtitle => LauncherConfig.Subtitle;
    public string SubtitleColor => LauncherConfig.SubtitleColor;

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

    /// <summary>Overall progress across all files (blue bar), 0-100.</summary>
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

    /// <summary>Progress of the file currently being downloaded (red bar), 0-100.</summary>
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

    private async Task StartWithFolderAsync()
    {
        var installPath = InstallLocation.Path;
        _launcher = TazUOLauncherConfig.Enabled ? new TazUOLauncher(installPath) : null;
        var localFiles = new LocalFiles(new FileSystem());
        var server = new FileServerClient(LauncherConfig.UpdateUrl, localFiles);
        //Without a trusted signing key nothing the server hosts as a package is ever installed
        var packages = LauncherConfig.TrustedPublicKeys.Length == 0 ? null
            : new PackageUpdater(server, localFiles, LauncherConfig.TrustedPublicKeys, PlatformId.Current, LauncherVersion.Current,
                PackageDownloadFolder, new NotImplementedSelfUpdater(), _launcher, new PackageState());
        var updates = new UpdateService(server, localFiles, installPath, packages, LauncherConfig.KeepLocalFiles);
        //A service that has been replaced (the folder changed) may still be winding down, so its updates are ignored
        updates.ProgressChanged += progress => Dispatcher.UIThread.Post(() => { if (updates == _updates) ShowProgress(progress); });
        updates.FileProgressChanged += file => Dispatcher.UIThread.Post(() => { if (updates == _updates) ShowFileProgress(file); });
        updates.ErrorOccurred += error => Dispatcher.UIThread.Post(() => { if (updates == _updates) ShowError(error); });
        _updates = updates;
        LauncherInstalled = _launcher?.IsInstalled ?? false;

        //Start from a clean slate, since nothing from a previous folder applies
        IsUpdating = false;
        DownloadsReady = false;
        RetryReady = false;
        FilesVerified = false;
        ErrorMessage = string.Empty;
        Progress = 0;
        FileProgress = 0;
        FileProgressText = string.Empty;
        if (packages == null && TazUOLauncherConfig.Enabled)
            ErrorMessage = Strings.PackagesNotConfigured;

        if (Preferences.Current.VerifyOnLaunch)
            await CheckForUpdatesAsync();
        else
            ProgressText = Strings.NotVerified;
    }

    public async Task<UpdateResult?> CheckForUpdatesAsync()
    {
        if (_updates == null || IsUpdating) return null; //No folder yet, or already checking

        IsUpdating = true;
        FilesVerified = false;
        DownloadsReady = false;
        RetryReady = false;
        ErrorMessage = string.Empty;
        Progress = 0;
        FileProgress = 0;
        FileProgressText = string.Empty;

        var updates = _updates;
        var result = await updates.CheckAsync();
        if (updates != _updates) return null; //The folder changed meanwhile, so this result is stale
        await Dispatcher.UIThread.InvokeAsync(() => ShowResult(result, wasDownload: false)); //Queued behind any progress still waiting to be shown
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
        if (_updates == null || IsUpdating) return; //Ignore repeat clicks

        DownloadsReady = false;
        RetryReady = false;
        ErrorMessage = string.Empty;
        IsUpdating = true;
        FilesVerified = false;

        var updates = _updates;
        var result = await updates.DownloadAsync();
        if (updates != _updates) return; //The folder changed meanwhile, so this result is stale
        await Dispatcher.UIThread.InvokeAsync(() => ShowResult(result, wasDownload: true));
    }

    //Opens the TazUO launcher, first asking the player to confirm if the files weren't fully verified
    private async Task PlayAsync()
    {
        if (_launcher == null || _view == null) return;

        if (!FilesVerified && Preferences.Current.WarnIfNotVerified
            && !await _view.ConfirmAsync(Strings.UnverifiedTitle, Strings.UnverifiedMessage, Strings.PlayAnyway, Strings.CancelText))
            return;

        try
        {
            _launcher.Start();
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

        CancelUpdate(); //Stop anything still working in the old folder
        await StartWithFolderAsync();
    }

    public void CancelUpdate() => _updates?.Cancel();

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

    private void ShowResult(UpdateResult result, bool wasDownload)
    {
        switch (result)
        {
            case UpdateResult.UpdatesReady:
            case UpdateResult.PackagesReady:
                Progress = 0;
                ProgressText = result == UpdateResult.UpdatesReady ? Strings.UpdatesReady : Strings.PackagesReady;
                DownloadsReady = true;
                break;
            case UpdateResult.Restarting:
                //The new launcher takes over, so the window stays busy until this process exits
                ProgressText = Strings.Restarting;
                return;
            case UpdateResult.Finished:
                var failed = _updates?.FailedFiles.Count ?? 0;
                FilesVerified = _updates?.FilesVerified ?? false;
                Progress = 100;
                FileProgress = 100;
                ProgressText = failed > 0 ? string.Format(Strings.FinishedWithFailures, failed) : Strings.Finished;
                FileProgressText = string.Empty;
                RetryReady = failed > 0;
                break;
            case UpdateResult.Failed:
                //The error line says why. Downloads that were waiting are re-found by Retry
                ProgressText = Strings.CheckFailed;
                RetryReady = true;
                break;
            case UpdateResult.Cancelled:
                //A cancelled download leaves known out of date files, so offer it again. A cancelled check just leaves them unverified
                ProgressText = Strings.Cancelled;
                DownloadsReady = wasDownload;
                break;
        }

        LauncherInstalled = _launcher?.IsInstalled ?? false;
        IsUpdating = false;
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
