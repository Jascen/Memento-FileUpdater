using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FileUpdaterClient.Views;

//Replaceable images in Assets/. A missing image is null, so that part is left blank instead of stopping the launcher
public static class LauncherArt
{
    public static readonly Bitmap? Background = Load("background.png");
    public static readonly Bitmap? PlayButton = Load("play-button.png");
    public static readonly Bitmap? ProgressFrame = Load("progress-frame.png");
    public static readonly Bitmap? ProgressTotal = Load("progress-total.png");
    public static readonly Bitmap? ProgressFile = Load("progress-file.png");

    private static Bitmap? Load(string fileName)
    {
        var uri = new Uri($"avares://{typeof(LauncherArt).Assembly.GetName().Name}/Assets/{fileName}");
        return AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
    }
}
