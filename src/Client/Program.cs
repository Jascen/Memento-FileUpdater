using Avalonia;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient;

internal class Program
{
    // This is required by Avalonia
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // Entry point of the application
    public static int Main(string[] args)
    {
        //A temporary copy of the launcher started by SelfUpdater to swap in a new version, with no window
        if (args.Length > 0 && args[0] == SelfUpdater.ApplyArgument)
            return SelfUpdater.RunApply(args);

        SelfUpdater.CleanUpTempFolders();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}