using System.Globalization;

namespace SpaceLens.Core;

public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Format(double bytes) => Format((long)Math.Round(bytes));

    public static string Rate(double bytesPerSecond) => Format(bytesPerSecond) + "/s";

    public static string Percent(double value) => value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
}
