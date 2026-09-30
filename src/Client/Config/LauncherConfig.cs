using Avalonia.Media;

namespace FileUpdaterClient.Config;

//Build-time configuration for a server's launcher: branding, colors, server address and links.
//TazUO launcher settings live in TazUOLauncherConfig.cs and on-screen text in Strings.cs. Player choices made at runtime live in Preferences.cs.
public static class LauncherConfig
{
    public const string Title = "UODiablo";
    public const string TitleColor = "#F3D58A";

    public const string Subtitle = "Stay up to date with the latest UODiablo files";
    public const string SubtitleColor = "#D9C9A3";

    public static SolidColorBrush DefaultTextColor = SolidColorBrush.Parse("#F2F2F2");
    public static SolidColorBrush ProgressBarBackground = SolidColorBrush.Parse("#1A1512");

    public const string UpdateUrl = "http://127.0.0.1:8080/";

    //Public keys (base64, printed by `PackageSigner keygen`) whose signature the server's launcher and client packages must carry.
    //Several can be listed so a key can be replaced without stranding old installs. While this is empty the launcher only
    //updates game files: it installs and runs nothing it downloaded, so the TazUO launcher can't be installed either
    public static readonly string[] TrustedPublicKeys = [];

    //How often an open launcher looks for a newer version of itself. The check at launch always happens
    public static readonly TimeSpan PackageCheckInterval = TimeSpan.FromHours(4);

    //Links shown along the top of the launcher, in order. Use NavLink.VerifyAction as the target to re-check all files instead of opening a url.
    public static readonly NavLink[] Links =
    [
        new("Website", "https://example.com"),
        new("Discord", "https://discord.gg/"),
        new("Verify", NavLink.VerifyAction),
    ];

    //Files players change themselves, like their own settings: downloaded when missing but never replaced once they exist.
    //Patterns are matched against the server's file names, e.g. "*.cfg" or "Data/Macros.txt"
    public static readonly string[] KeepLocalFiles = [];

    public const string DefaultInstallFolder = "Client"; //Created next to the updater exe unless the player picks another folder
    //Per-user folder (e.g. %AppData%/UODiablo) that remembers the chosen install folder and preferences.
    //Follows Title so each server's launcher keeps its own settings. Only set it separately if two launchers share a title
    public const string AppDataFolder = Title;
}

public record NavLink(string Label, string Target)
{
    public const string VerifyAction = "verify";
}
