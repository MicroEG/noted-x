using System.Globalization;

namespace NotedX.Desktop.ViewModels;

/// <summary>Formats byte counts and transfer speeds for display.</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count such as <c>1.4 MB</c>.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>Formats a transfer rate such as <c>3.2 MB/s</c>.</summary>
    public static string FormatSpeed(double bytesPerSecond) =>
        FormatSize((long)Math.Max(0d, bytesPerSecond)) + "/s";
}
