using System.Globalization;

namespace BeatzOnKeys.Core.YouTube;

/// <summary>
/// Turns a byte count into the shortest readable unit. The caller supplies the unit words so the
/// same numbers read correctly in Russian and in English.
/// </summary>
public static class ByteSizeFormatter
{
    private const double Kilo = 1024d;
    private const double Mega = Kilo * Kilo;
    private const double Giga = Mega * Kilo;

    public static string Format(long bytes, string kiloUnit, string megaUnit, string gigaUnit)
    {
        if (bytes < 0) { bytes = 0; }
        if (bytes == 0) { return $"0 {kiloUnit}"; }
        if (bytes < Mega) { return $"{Round(bytes / Kilo)} {kiloUnit}"; }
        if (bytes < Giga) { return $"{Round(bytes / Mega)} {megaUnit}"; }
        return $"{Round(bytes / Giga)} {gigaUnit}";
    }

    private static string Round(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}