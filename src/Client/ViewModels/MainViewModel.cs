using System.ComponentModel;
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
    private TazUOLauncher? _launcher; //Null when LauncherConfig.EnableTazUO is off

    private string _errorMessage = string.Empty;
    private bool _isDialogOpen;
    private bool _downloadsReady;
    private double _progress;
    private double _fileProgress;
    private string _progressText = Strings.CheckingForUpdates;
    private string _fileProgressText = string.Empty;
    private bool _isUpdating;
    private bool _filesVerified;
    private bool _launcherInstalled;
    public event PropertyChangedEventHandler? PropertyChanged;

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
    public bool MainButtonVisible => DownloadsReady || LauncherConfig.EnableTazUO; //Without TazUO it only appears to download updates

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
        _launcher = LauncherConfig.EnableTazUO ? new TazUOLauncher(installPath) : null;
        _updates = new UpdateService(new FileServerClient(LauncherConfig.UpdateUrl), installPath, _launcher);
        _updates.ProgressChanged += progress => Dispatcher.UIThread.Post(() => ShowProgress(progress));
        _updates.FileProgressChanged += file => Dispatcher.UIThread.Post(() => ShowFileProgress(file));
        _updates.ErrorOccurred += error => Dispatcher.UIThread.Post(() => ShowError(error));
        LauncherInstalled = _launcher?.IsInstalled ?? false;

        if (Preferences.Current.VerifyOnLaunch)
            await CheckForUpdatesAsync();
        else
            ProgressText = Strings.NotVerified;
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_updates == null || IsUpdating) return; //No folder yet, or already checking

        IsUpdating = true;
        FilesVerified = false;
        DownloadsReady = false;
        ErrorMessage = string.Empty;
        Progress = 0;
        FileProgress = 0;
        FileProgressText = string.Empty;

        var result = await _updates.CheckAsync();
        Dispatcher.UIThread.Post(() => ShowResult(result, wasDownload: false)); //Queued behind any progress still waiting to be shown
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
        IsUpdating = true;
        FilesVerified = false;

        var result = await _updates.DownloadAsync();
        Dispatcher.UIThread.Post(() => ShowResult(result, wasDownload: true));
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

        ErrorMessage = string.Empty;
        if (_updates == null)
        {
            //The default folder wasn't usable, so start now that one is picked
            await StartWithFolderAsync();
            return;
        }

        //The updater runs once per launch, so restart it to check the new folder
        CancelUpdate();
        _view.RestartApp();
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
                ProgressText = progress.Done >= progress.Total
                    ? Strings.Finished
                    : string.Format(Strings.DownloadingFiles, progress.Done, progress.Total,
                        $"{progress.BytesPerSecond / 1024:F2} KB/s");
                break;
            case UpdatePhase.InstallingLauncher:
                ProgressText = Strings.InstallingTazUO;
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
            UpdateError.LauncherFailed => Strings.TazUOError,
            _ => Strings.UnknownError,
        };
    }

    private void ShowResult(UpdateResult result, bool wasDownload)
    {
        switch (result)
        {
            case UpdateResult.UpdatesReady:
            case UpdateResult.LauncherReady:
                Progress = 0;
                ProgressText = result == UpdateResult.UpdatesReady ? Strings.UpdatesReady : Strings.LauncherReady;
                DownloadsReady = true;
                break;
            case UpdateResult.Finished:
                FilesVerified = _updates?.FilesVerified ?? false;
                Progress = 100;
                FileProgress = 100;
                ProgressText = Strings.Finished;
                FileProgressText = string.Empty;
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
