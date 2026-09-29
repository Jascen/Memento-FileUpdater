namespace FileUpdaterClient.Updating;

public enum UpdatePhase
{
    RequestingFileList,
    Comparing,
    Downloading,
    InstallingLauncher,
}

//Overall progress of a check or download. Done/Total count files. BytesPerSecond is only set while downloading,
//and BytesDone/BytesTotal only when the server sent every queued file's size
public record UpdateProgress(UpdatePhase Phase, int Done, int Total, double BytesPerSecond = 0, long BytesDone = 0, long BytesTotal = 0)
{
    public double Percent => BytesTotal > 0 ? BytesDone * 100.0 / BytesTotal
        : Total > 0 ? Done * 100.0 / Total : 0;

    //Estimated time left in the download, null when there's no size or speed to go on yet
    public TimeSpan? TimeLeft => BytesTotal > 0 && BytesPerSecond > 0
        ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond)
        : null;
}

//Progress of the single file currently downloading
public record FileProgress(string FileName, double Percent);

public enum UpdateError
{
    ConnectionFailed,
    BadData,
    Unknown,
    FileFailed, //FileName says which file
    FileLocked, //FileName is open in another program, usually the game
    LauncherFailed,
}

public record UpdateErrorInfo(UpdateError Error, string? FileName = null);

public enum UpdateResult
{
    Finished, //Everything checked, downloaded and set up. See UpdateService.FilesVerified for whether any file failed
    UpdatesReady, //Files differ from the server, waiting for DownloadAsync
    LauncherReady, //Files match but the TazUO launcher still needs downloading, waiting for DownloadAsync
    Failed, //Stopped by an error, see UpdateService.ErrorOccurred
    Cancelled,
}

//Optional step run after the files are up to date, e.g. installing the TazUO launcher
public interface ILauncherInstaller
{
    bool IsInstalled { get; }
    Task EnsureInstalledAsync(CancellationToken cancellationToken);
}
