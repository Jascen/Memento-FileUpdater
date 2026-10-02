using FileUpdaterClient.TazUO;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.ViewModels;

//Where the launcher is with the game files. Everything the window shows or enables about them follows from this one value
public enum LauncherState
{
    Idle, //Nothing is known about the files: no folder yet, verifying on launch is off, or a check was cancelled
    Working, //A check, a download or a launcher update is running
    UpdatesReady, //A check found files or the TazUO launcher to download, waiting for the player to click the main button
    Verified, //Every file was checked against the server and any updates downloaded
    Failed, //The last run failed or couldn't download some files, so Retry is offered
}

//What belongs to one install folder: its update service and, when TazUO is used, the TazUO launcher inside it
public record InstallSession(UpdateService Updates, IGameLauncher? TazUO);

//How MainViewModel gets onto the UI thread. The update services report from background threads
public interface IUiThread
{
    //Queues the action and returns straight away
    void Post(Action action);

    //Queues the action behind anything already posted, and completes once it has run
    Task InvokeAsync(Action action);
}
