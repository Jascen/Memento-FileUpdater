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

    public const string DefaultInstallFolder = "Client"; //Created next to the updater exe unless the player picks another folder
    //Per-user folder (e.g. %AppData%/UODiablo) that remembers the chosen install folder and preferences.
    //Follows Title so each server's launcher keeps its own settings. Only set it separately if two launchers share a title
    public const string AppDataFolder = Title;
}

public record NavLink(string Label, string Target)
{
    public const string VerifyAction = "verify";
}
