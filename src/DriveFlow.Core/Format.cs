using System.Globalization;

namespace DriveFlow.Core;

public static class Format
{
    static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Size(double value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        foreach (var unit in units)
        {
            if (value < 1024 || unit == "TiB")
                return unit == "B" ? $"{value.ToString("0", PtBr)} B" : $"{value.ToString("0.0", PtBr)} {unit}";
            value /= 1024;
        }
        return "";
    }

    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }
}
