using System.Globalization;

namespace FileUpdaterClient.Updating;

//Human-readable sizes, speeds and durations for the progress text
public static class Units
{
    public static string Bytes(double bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => Format(bytes / (1024 * 1024 * 1024), "GB"),
        >= 1024 * 1024 => Format(bytes / (1024 * 1024), "MB"),
        >= 1024 => Format(bytes / 1024, "KB"),
        _ => $"{bytes:F0} B",
    };

    public static string Speed(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    //Rounded up to whole seconds, e.g. "45s", "3m 20s", "1h 5m"
    public static string Duration(TimeSpan time)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, time.TotalSeconds));
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60}m";
    }

    private static string Format(double value, string unit) =>
        value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + unit;
}
