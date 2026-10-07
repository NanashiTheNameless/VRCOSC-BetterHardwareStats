namespace BetterHardwareStats.Core.Output;
public static class Normalization
{
    public const double BytesPerGiB = 1024d * 1024 * 1024;
    public const double BytesPerMiB = 1024d * 1024;
    public static float? PercentToFraction(float? percent) => percent is { } p && float.IsFinite(p) ? Math.Clamp(p / 100f, 0f, 1f) : null;
    public static float? Ratio(double? used, double? total) =>
        used is { } u && total is { } t && t > 0 && double.IsFinite(u) && double.IsFinite(t) ? (float)Math.Clamp(u / t, 0d, 1d) : null;
    public static float? Scaled(double? value, double max) => Ratio(value, max);
    public static int? ToByteInt(double? value) =>
        value is { } v && double.IsFinite(v) ? (int)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255) : null;
    public static int? Percent(float? percent) =>
        percent is { } p && float.IsFinite(p) ? (int)Math.Clamp(Math.Round(p, MidpointRounding.ToEven), 0, 100) : null;
    public static int? PercentFromFraction(float? fraction) => fraction is { } f ? Percent(f * 100f) : null;
    public static int? GiBInt(double? bytes) => bytes is { } b ? ToByteInt(b / BytesPerGiB) : null;

    public static double? GiB(double? bytes) => bytes / BytesPerGiB;

    public static double? MiB(double? bytes) => bytes / BytesPerMiB;
    public static double? MBps(double? bytesPerSec) => bytesPerSec / 1_000_000d;
    public static double? Mbps(double? bytesPerSec) => bytesPerSec * 8d / 1_000_000d;
    public static double? CelsiusToFahrenheit(double? c) => c * 9d / 5d + 32d;
}
