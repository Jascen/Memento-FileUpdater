namespace FileUpdaterClient.Config;

//Every message the player sees. Messages with {0}-style placeholders are filled in with string.Format.
public static class Strings
{
    public const string PlayText = "PLAY NOW"; //Center button, opens the TazUO launcher once it's installed
    public const string DownloadButton = "Download updates"; //Center button while updates are waiting to download

    public const string CheckingForUpdates = "Checking for updates..";
    public const string Finished = "Done, you're all up to date!";
    public const string FinishedWithFailures = "Finished, but {0} file(s) couldn't be downloaded."; //{0} = number of files
    public const string CheckFailed = "Couldn't check for updates.";
    public const string RetryText = "Retry";
    public const string ReqFileList = "Requesting file list from server..";
    public const string ComparingFiles = "Comparing your files to the server.. ({0}/{1})"; //{0} = current file, {1} = total files
    public const string InstallingTazUO = "Setting up the TazUO launcher..";
    public const string UpdatesReady = "Updates are ready to download.";
    public const string LauncherReady = "The TazUO launcher is ready to download.";
    public const string DownloadingFiles = "Downloading files from the server.. ({0}/{1}) - ({2})"; //{0} = current file, {1} = total files, {2} dl speed
    public const string DownloadingBytes = "Downloading.. {0} of {1} - {2}, {3} left"; //{0} = downloaded, {1} = total size, {2} = speed, {3} = time left. Used when the server sends file sizes
    public const string CurrentFile = "{0}"; //{0} = file name, shown on the red bar
    public const string Cancelled = "Update cancelled.";
    public const string NotVerified = "Files not verified. Click Verify to check for updates."; //Shown when verifying on launch is turned off

    public const string ConError = "Unable to connect to server."; //Failed connection to server
    public const string BadData = "Got bad data from server, please try again later."; //Malformed JSON response
    public const string UnknownError = "An unknown error occured, please try again later.";
    public const string TazUOError = "Couldn't set up the TazUO launcher, it will be retried next time.";
    public const string FileFailedError = "Failed to download [{0}] after several attempts, skipping.."; //{0} = file name
    public const string FileLockedError = "[{0}] is in use. Close the game and click Retry to finish updating."; //{0} = file name
    public const string LaunchError = "Unable to start the TazUO launcher."; //Play button couldn't start it

    public const string ChooseFolderTitle = "Choose where to install UODiablo";
    public const string NoFolderChosen = "Choose an install folder in Settings to continue.";
    public const string FolderNotWritable = "Can't write to {0}, please choose another folder."; //{0} = folder
    public const string ChangeFolder = "Change";

    //Popup when Play is clicked before the files were fully verified
    public const string UnverifiedTitle = "Files not verified";
    public const string UnverifiedMessage = "The status of your game files is unknown. They may be missing or out of date, which can cause problems in game.";
    public const string PlayAnyway = "Play anyway";
    public const string CancelText = "Cancel";
}
