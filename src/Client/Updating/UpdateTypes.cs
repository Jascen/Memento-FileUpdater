namespace FileUpdaterClient.Updating;

public enum UpdatePhase
{
    RequestingFileList,
    Comparing,
    Downloading,
    InstallingLauncher,
}

//Overall progress of a check or download. Done/Total count files, BytesPerSecond is only set while downloading
public record UpdateProgress(UpdatePhase Phase, int Done, int Total, double BytesPerSecond = 0)
{
    public double Percent => Total > 0 ? Done * 100.0 / Total : 0;
}

//Progress of the single file currently downloading
public record FileProgress(string FileName, double Percent);

public enum UpdateError
{
    ConnectionFailed,
    BadData,
    Unknown,
    FileFailed, //FileName says which file
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
