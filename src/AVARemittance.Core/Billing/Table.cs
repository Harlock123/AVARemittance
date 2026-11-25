using System.Globalization;
using ClosedXML.Excel;

namespace AVARemittance.Core.Billing;

/// <summary>
/// A CSV or an Excel workbook (its first sheet) with a header row: the charges list, the bank statement and the fee
/// schedule are all read this way. Columns are found by their names, in any order and case; unknown columns are ignored.
/// </summary>
public sealed class Table
{
    private readonly Dictionary<string, int> _columns = [];

    private Table(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyDictionary<string, string[]> names)
    {
        Rows = rows.Count > 0 ? rows.Skip(1).ToList() : [];
        if (rows.Count == 0) return;
        for (var i = 0; i < rows[0].Count; i++)
            if (Key(rows[0][i], names) is { } k && !_columns.ContainsKey(k)) _columns[k] = i;
    }

    /// <summary>The rows after the header.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public bool Has(string key) => _columns.ContainsKey(key);

    public string Get(IReadOnlyList<string> row, string key) => _columns.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : "";

    public static Table Read(string path, IReadOnlyDictionary<string, string[]> names) => new(Cells(path), names);

    public static Table Read(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyDictionary<string, string[]> names) => new(rows, names);

    /// <summary>The header's keys, for telling which kind of list a file is without reading it all.</summary>
    public static HashSet<string> HeaderKeys(string path, IReadOnlyDictionary<string, string[]> names)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx")) return [];
        try
        {
            var header = ext == ".csv" ? SplitCsv(File.ReadLines(path).FirstOrDefault() ?? "") : Cells(path).FirstOrDefault() ?? [];
            return [.. header.Select(h => Key(h, names)).OfType<string>()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            return [];
        }
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "M/d/yy"];

    public static DateOnly? Date(string v)
    {
        if (v.Length == 0) return null;
        if (DateOnly.TryParseExact(v, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateOnly.FromDateTime(dt) : null;
    }

    /// <summary>An amount as people write it: $1,234.56, (12.00) or -12.00.</summary>
    public static decimal? Amount(string v)
    {
        var t = v.Trim().Replace("$", "").Replace(",", "");
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative) t = t[1..^1];
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? negative ? -d : d : null;
    }

    private static string? Key(string header, IReadOnlyDictionary<string, string[]> names)
    {
        var h = header.Trim().ToLowerInvariant().Replace('_', ' ');
        return names.FirstOrDefault(kv => kv.Value.Contains(h)).Key;
    }

    private static List<IReadOnlyList<string>> Cells(string path)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return [.. File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(SplitCsv)];
        using var book = new XLWorkbook(path);
        var used = book.Worksheets.First().RangeUsed();
        if (used == null) return [];
        return [.. used.Rows().Select(r => (IReadOnlyList<string>)[.. r.Cells().Select(c => c.DataType == XLDataType.DateTime ? c.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : c.GetFormattedString())])];
    }

    /// <summary>One CSV line into cells: quotes around cells with commas, doubled quotes inside them.</summary>
    public static IReadOnlyList<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }
}
