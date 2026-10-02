using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FileUpdaterClient.Config;

//Every message the player sees, read from Theme/strings.json (built into the exe). Messages with {0}-style placeholders are filled in with string.Format.
//A message missing from the file shows its name instead, so a typo in the file is easy to spot
public static class Strings
{
    private static readonly Dictionary<string, string> Messages = Load();

    public static string Title => Get();
    public static string Subtitle => Get();

    public static string PlayText => Get();
    public static string DownloadButton => Get();

    public static string CheckingForUpdates => Get();
    public static string Finished => Get();
    public static string FinishedWithFailures => Get();
    public static string CheckFailed => Get();
    public static string RetryText => Get();
    public static string ReqFileList => Get();
    public static string ComparingFiles => Get();
    public static string InstallingTazUO => Get();
    public static string UpdatesReady => Get();
    public static string PackagesReady => Get();
    public static string UpdatingLauncher => Get();
    public static string DownloadingLauncher => Get();
    public static string DownloadingTazUO => Get();
    public static string Restarting => Get();
    public static string LauncherUpdateAvailable => Get();
    public static string UpdateLauncherButton => Get();
    public static string LauncherUpdateLater => Get();
    public static string LauncherUpdateBusyTitle => Get();
    public static string LauncherUpdateBusyMessage => Get();
    public static string LauncherUpdateBusyConfirm => Get();
    public static string DownloadingFiles => Get();
    public static string DownloadingBytes => Get();
    public static string CurrentFile => Get();
    public static string Cancelled => Get();
    public static string NotVerified => Get();

    public static string ConError => Get();
    public static string BadData => Get();
    public static string UnknownError => Get();
    public static string TazUOError => Get();
    public static string SelfUpdateError => Get();
    public static string PackagesUntrustedError => Get();
    public static string PackagesNotConfigured => Get();
    public static string FileFailedError => Get();
    public static string FileLockedError => Get();
    public static string LaunchError => Get();

    public static string SettingsTooltip => Get();
    public static string MinimizeTooltip => Get();
    public static string CloseTooltip => Get();
    public static string CancelUpdateTooltip => Get();

    public static string SettingsTitle => Get();
    public static string InstallFolderLabel => Get();
    public static string ChooseFolderTitle => Get();
    public static string NoFolderChosen => Get();
    public static string FolderNotWritable => Get();
    public static string ChangeFolder => Get();
    public static string VerifyOnLaunchOption => Get();
    public static string WarnIfNotVerifiedOption => Get();
    public static string SaveText => Get();

    public static string UnverifiedTitle => Get();
    public static string UnverifiedMessage => Get();
    public static string PlayAnyway => Get();
    public static string CancelText => Get();

    private static string Get([CallerMemberName] string name = "") => Messages.TryGetValue(name, out var text) ? text : name;

    private static Dictionary<string, string> Load()
    {
        try
        {
            using var stream = typeof(Strings).Assembly.GetManifestResourceStream("Theme/strings.json");
            if (stream != null)
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, ThemeJson.Options) ?? new();
            Console.WriteLine("Theme/strings.json is missing from the build");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to read Theme/strings.json: {e.Message}");
        }
        return new();
    }
}
