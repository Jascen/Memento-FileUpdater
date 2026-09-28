using Avalonia.Media;

namespace FileUpdaterClient;

//Build-time configuration for a server's launcher: branding, colors, server address and optional features.
//On-screen text lives in Strings.cs. Player choices made at runtime live in Preferences.cs.
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
    public const string AppDataFolder = "UODiablo"; //Per-user folder that remembers the chosen install folder and preferences

    //When false the TazUO launcher is never downloaded and there is no Play Now button, the updater only keeps files up to date
    public const bool EnableTazUO = true;

    //TazUO launcher is installed into this folder inside the install folder, with these profiles pre-created
    public const string TazUOLauncherFolder = "TazUO Launcher";
    public static readonly TazUOProfile[] TazUOProfiles =
    [
        new("uodiablo-live", "UODiablo", "127.0.0.1", 2593, "7.0.15.1"),
        new("uodiablo-test", "UODiablo Test", "127.0.0.1", 2594, "7.0.15.1"),
    ];
}

public record NavLink(string Label, string Target)
{
    public const string VerifyAction = "verify";
}
