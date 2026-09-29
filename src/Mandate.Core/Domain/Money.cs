using System.Globalization;
using System.Text.RegularExpressions;

namespace Mandate.Core.Domain;

/// <summary>
/// Amounts are stored as integer minor units (kobo). Anything a model produces goes
/// through <see cref="TryParseMajor"/>, which accepts only an unambiguous decimal string.
/// "5k", "five thousand", negative numbers and more than two decimal places are rejected
/// so the model has to resolve the amount itself and the user sees the exact figure.
/// </summary>
public static partial class Money
{
    public const long MaxMinor = 100_000_000_000; // ₦1bn, well past any mandate

    [GeneratedRegex(@"^(?:₦|NGN\s?)?(\d{1,3}(?:,\d{3})+|\d+)(?:\.(\d{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex MajorPattern();

    public static bool TryParseMajor(string? input, out long minor, out string? error)
    {
        minor = 0;
        error = null;
        var trimmed = input?.Trim() ?? "";
        var match = MajorPattern().Match(trimmed);
        if (!match.Success)
        {
            error = $"'{input}' is not a plain amount. Use digits like 5000 or 5000.50.";
            return false;
        }

        var whole = long.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        var fraction = match.Groups[2].Success ? match.Groups[2].Value.PadRight(2, '0') : "00";
        if (whole > MaxMinor / 100)
        {
            error = "Amount is too large.";
            return false;
        }

        minor = whole * 100 + long.Parse(fraction, CultureInfo.InvariantCulture);
        if (minor <= 0)
        {
            error = "Amount must be greater than zero.";
            return false;
        }
        return true;
    }

    public static string Format(long minor, string currency = "NGN") =>
        currency == "NGN"
            ? "₦" + (minor / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture)
            : $"{currency} {(minor / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture)}";
}
