using System.Globalization;
using CsvHelper;

namespace MagicMovieNight.Integrations.Importers;

/// <summary>
/// Streaming-service exports are not a stable contract. Amazon has shipped at least
/// three different column layouts for the same report, and Hulu's varies by request
/// date. Rather than pin to one header spelling and break silently, importers declare
/// the aliases they know and resolve them against whatever header actually arrived.
/// </summary>
internal static class CsvColumnMap
{
    /// <summary>Returns the first header present in the file from <paramref name="aliases"/>, or null.</summary>
    public static string? Resolve(CsvReader csv, params string[] aliases)
    {
        var header = csv.HeaderRecord;
        if (header is null)
        {
            return null;
        }

        foreach (var alias in aliases)
        {
            var match = header.FirstOrDefault(h =>
                string.Equals(Normalize(h), Normalize(alias), StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public static string? Read(CsvReader csv, string? column) =>
        column is null ? null : csv.TryGetField<string>(column, out var value) ? value : null;

    /// <summary>
    /// Exports use whatever date format the requesting account's locale produced.
    /// Try the common shapes explicitly before falling back to a loose parse.
    /// </summary>
    public static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();

        string[] formats =
        [
            "M/d/yy", "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy",
            "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-dd HH:mm:ss.fff", "yyyy/MM/dd",
            "ddd MMM dd HH:mm:ss 'UTC' yyyy",
        ];

        if (DateTimeOffset.TryParseExact(
                raw, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            return exact;
        }

        if (DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var loose))
        {
            return loose;
        }

        return null;
    }

    private static string Normalize(string s) =>
        s.Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace(".", string.Empty)
            .Replace("-", string.Empty)
            .Trim();
}
