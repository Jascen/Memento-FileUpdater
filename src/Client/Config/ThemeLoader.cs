using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FileUpdaterClient.Config;

//Loads Theme/theme.json (built into the exe) into the app's resources, where the windows use each entry as {DynamicResource Theme.<name>}.
//An entry that can't be read is skipped and logged, so that part falls back to Avalonia's default look instead of stopping the launcher
public static class ThemeLoader
{
    private const string KeyPrefix = "Theme.";
    private static readonly Uri ThemeFolder = new($"avares://{typeof(ThemeLoader).Assembly.GetName().Name}/Theme/");

    public static void Apply(IResourceDictionary resources)
    {
        var theme = Read();
        if (theme == null) return;

        foreach (var (name, value) in theme.Fonts)
            Set(resources, name, () => new FontFamily(ThemeFolder, value));
        foreach (var (name, value) in theme.Sizes)
            resources[KeyPrefix + name] = value;
        foreach (var (name, value) in theme.Colors)
            Set(resources, name, () => new ImmutableSolidColorBrush(Color.Parse(value)));
        foreach (var (name, value) in theme.Gradients)
            Set(resources, name, () => CreateGradient(value));
        foreach (var (name, value) in theme.Shadows)
            Set(resources, name, () => new DropShadowEffect
            {
                Color = Color.Parse(value.Color), BlurRadius = value.Blur, OffsetX = value.OffsetX, OffsetY = value.OffsetY, Opacity = value.Opacity,
            });
        foreach (var (name, value) in theme.Images)
            Set(resources, name, () => LoadImage(value));

        //The play button's glow takes the image's shape. Without an image the glow is hidden rather than drawn as a rectangle
        resources[KeyPrefix + "PlayButtonMask"] = resources.TryGetResource(KeyPrefix + "PlayButtonImage", null, out var image) && image is Bitmap bitmap
            ? new ImageBrush(bitmap) { Stretch = Stretch.Fill }
            : Brushes.Transparent;

        ApplyCheckBoxColors(resources, theme.Colors);
    }

    private static ThemeFile? Read()
    {
        try
        {
            using var stream = typeof(ThemeLoader).Assembly.GetManifestResourceStream("Theme/theme.json");
            if (stream != null) return JsonSerializer.Deserialize<ThemeFile>(stream, ThemeJson.Options);
            Console.WriteLine("Theme/theme.json is missing from the build");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to read Theme/theme.json: {e.Message}");
        }
        return null;
    }

    private static void Set(IResourceDictionary resources, string name, Func<object?> create)
    {
        try
        {
            var value = create();
            if (value != null) resources[KeyPrefix + name] = value;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Skipped theme entry {name}: {e.Message}");
        }
    }

    private static IBrush CreateGradient(ThemeGradient gradient)
    {
        var stops = new GradientStops();
        stops.AddRange(gradient.Stops.Select(s => new GradientStop(Color.Parse(s.Color), s.Offset)));

        return gradient.Type.ToLowerInvariant() switch
        {
            "linear" => new LinearGradientBrush
            {
                StartPoint = RelativePoint.Parse(gradient.Start), EndPoint = RelativePoint.Parse(gradient.End), GradientStops = stops,
            },
            "radial" => new RadialGradientBrush
            {
                RadiusX = RelativeScalar.Parse(gradient.Radius), RadiusY = RelativeScalar.Parse(gradient.Radius), GradientStops = stops,
            },
            _ => throw new FormatException($"unknown gradient type '{gradient.Type}', use linear or radial"),
        };
    }

    //A missing image is left blank, like a missing entry
    private static Bitmap? LoadImage(string fileName)
    {
        var uri = new Uri(ThemeFolder, fileName);
        if (AssetLoader.Exists(uri)) return new Bitmap(AssetLoader.Open(uri));
        Console.WriteLine($"Theme image {fileName} not found, leaving it blank");
        return null;
    }

    //Check boxes are drawn by the Fluent theme, which reads its own resource names
    private static void ApplyCheckBoxColors(IResourceDictionary resources, Dictionary<string, string> colors)
    {
        void Map(string color, params string[] fluentKeys)
        {
            if (!colors.TryGetValue(color, out var value) || !Color.TryParse(value, out var parsed)) return;
            foreach (var key in fluentKeys)
                resources[key] = key == "SystemAccentColor" ? parsed : new ImmutableSolidColorBrush(parsed);
        }

        Map("CheckBox", "SystemAccentColor", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked");
        Map("CheckBoxHover", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver");
        Map("CheckBoxPressed", "CheckBoxCheckBackgroundFillCheckedPressed");
        Map("CheckBoxGlyph", "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver");
    }
}

//Shape of Theme/theme.json
public class ThemeFile
{
    public Dictionary<string, string> Fonts { get; set; } = new();
    public Dictionary<string, double> Sizes { get; set; } = new();
    public Dictionary<string, string> Colors { get; set; } = new();
    public Dictionary<string, ThemeGradient> Gradients { get; set; } = new();
    public Dictionary<string, ThemeShadow> Shadows { get; set; } = new();
    public Dictionary<string, string> Images { get; set; } = new();
}

public class ThemeGradient
{
    public string Type { get; set; } = "linear";
    public string Start { get; set; } = "0%,0%";
    public string End { get; set; } = "0%,100%";
    public string Radius { get; set; } = "50%";
    public List<ThemeGradientStop> Stops { get; set; } = new();
}

public record ThemeGradientStop(double Offset, string Color);

public class ThemeShadow
{
    public string Color { get; set; } = "Black";
    public double Blur { get; set; } = 5;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Opacity { get; set; } = 1;
}

//theme.json and strings.json allow comments and trailing commas so they can explain themselves
public static class ThemeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
