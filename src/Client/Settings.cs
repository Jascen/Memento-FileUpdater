using Avalonia.Media;

namespace FileUpdaterClient;

public static class Settings
{
    public const string Title = "UODiablo";
    public const string TitleColor = "#F3D58A";

    public const string Subtitle = "Stay up to date with the latest UODiablo files";
    public const string SubtitleColor = "#D9C9A3";

    public static SolidColorBrush DefaultTextColor = SolidColorBrush.Parse("#F2F2F2");
    public static SolidColorBrush ProgressBarBackground = SolidColorBrush.Parse("#1A1512");
    public static SolidColorBrush TotalProgressColor = SolidColorBrush.Parse("#2F6FD6"); //Blue bar, overall progress across all files
    public static SolidColorBrush FileProgressColor = SolidColorBrush.Parse("#C4202C"); //Red bar, progress of the file currently downloading

    public const string UpdateUrl = "http://127.0.0.1:8080/";

    //Links shown along the top of the launcher, in order. Use NavLink.VerifyAction as the target to re-check all files instead of opening a url.
    public static readonly NavLink[] Links =
    [
        new("Website", "https://example.com"),
        new("Discord", "https://discord.gg/"),
        new("Verify", NavLink.VerifyAction),
    ];

    public const string PlayText = "PLAY NOW"; //Opens the TazUO launcher, enabled once it's installed

    public const string DefaultInstallFolder = "Client"; //Created next to the updater exe unless the player picks another folder
    public const string AppDataFolder = "UODiablo"; //Per-user folder that remembers the chosen install folder

    //TazUO launcher is installed into this folder inside the install folder, with these profiles pre-created
    public const string TazUOLauncherFolder = "TazUO Launcher";
    public static readonly TazUOProfile[] TazUOProfiles =
    [
        new("uodiablo-live", "UODiablo", "127.0.0.1", 2593, "7.0.15.1"),
        new("uodiablo-test", "UODiablo Test", "127.0.0.1", 2594, "7.0.15.1"),
    ];

    public const string Finished = "Done, you're all up to date!";
    public const string ReqFileList = "Requesting file list from server..";
    public const string ComparingFiles = "Comparing your files to the server.. ({0}/{1})"; //{0} = current file, {1} = total files
    public const string InstallingTazUO = "Setting up the TazUO launcher..";
    public const string UpdatesReady = "Updates are ready to download.";
    public const string LauncherReady = "The TazUO launcher is ready to download.";
    public const string DownloadButton = "Download updates"; //Shown on the center button while updates are waiting to download
    public const string DownloadingFiles = "Downloading files from the server.. ({0}/{1}) - ({2})"; //{0} = current file, {1} = total files, {2} dl speed
    public const string CurrentFile = "{0}"; //{0} = file name, shown on the red bar
    public const string Cancelled = "Update cancelled.";

    public const string ConError = "Unable to connect to server."; //Failed connection to server
    public const string BadData = "Got bad data from server, please try again later."; //Malformed JSON response
    public const string UnknownError = "An unknown error occured, please try again later.";
    public const string TazUOError = "Couldn't set up the TazUO launcher, it will be retried next time.";
    public const string ChooseFolderTitle = "Choose where to install UODiablo";
    public const string NoFolderChosen = "Choose an install folder to continue.";
    public const string FolderNotWritable = "Can't write to {0}, please choose another folder."; //{0} = folder
    public const string ChangeFolder = "Change";
    public const string ChooseFolder = "Choose install folder";
    public const string FileFailedError = "Failed to download [{0}] after several attempts, skipping.."; //{0} = file name
    public const string LaunchError = "Unable to start the TazUO launcher."; //Play button couldn't start it
    public const string NotVerified = "Files not verified. Click Verify to check for updates."; //Shown when verifying on launch is turned off

    //Popup when Play is clicked before the files were fully verified
    public const string UnverifiedTitle = "Files not verified";
    public const string UnverifiedMessage = "The status of your game files is unknown. They may be missing or out of date, which can cause problems in game.";
    public const string PlayAnyway = "Play anyway";
    public const string CancelText = "Cancel";
}

public record NavLink(string Label, string Target)
{
    public const string VerifyAction = "verify";
}
