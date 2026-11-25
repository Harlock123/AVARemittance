using System.Reflection;

namespace AVARemittance.Core.Codes;

/// <summary>One code from a published list: its wording, and the dates it was in use (a stopped code may still turn up on an old claim).</summary>
public sealed record CodeEntry(string Code, string Text, DateOnly? Start, DateOnly? Stop);

/// <summary>
/// A code list as x12.org publishes it, read from the .tsv that <c>tools/make_codes.py</c> makes. The wording is the
/// list's own, with its usage notes left off.
/// </summary>
public sealed class CodeList
{
    private readonly Dictionary<string, CodeEntry> _codes;

    private CodeList(string name, string title)
    {
        Title = title;
        _codes = [];
        using var stream = typeof(CodeList).Assembly.GetManifestResourceStream($"Codes.{name}.tsv")
            ?? throw new InvalidOperationException($"The {name} code list isn't built in.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 4) continue;
            _codes[parts[0]] = new CodeEntry(parts[0], parts[3], Day(parts[1]), Day(parts[2]));
        }
    }

    /// <summary>What the list is called, as the program says it ("claim adjustment reason code").</summary>
    public string Title { get; }

    public int Count => _codes.Count;
    public IEnumerable<CodeEntry> All => _codes.Values;

    public CodeEntry? this[string code] => _codes.GetValueOrDefault(code.Trim());

    /// <summary>The code's wording, or a note that the list hasn't got it: never a guess.</summary>
    public string Say(string code) => this[code]?.Text ?? $"(not in the X12 {Title} list)";

    /// <summary>Codes whose wording has all of these words.</summary>
    public IEnumerable<CodeEntry> Search(string words)
    {
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _codes.Values.Where(c => parts.All(p => c.Text.Contains(p, StringComparison.OrdinalIgnoreCase) || c.Code.Equals(p, StringComparison.OrdinalIgnoreCase)));
    }

    private static DateOnly? Day(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d) ? d : null;

    public const string Source = "X12 code lists published at x12.org (CARC, RARC, Claim Status Category, Claim Status), retrieved 2026-11-24";

    private static readonly Lazy<CodeList> _carc = new(() => new("carc", "claim adjustment reason code"));
    private static readonly Lazy<CodeList> _rarc = new(() => new("rarc", "remittance advice remark code"));
    private static readonly Lazy<CodeList> _category = new(() => new("status-category", "claim status category code"));
    private static readonly Lazy<CodeList> _status = new(() => new("status", "claim status code"));

    /// <summary>Claim adjustment reason codes: why a line or claim was paid differently from what was billed (CAS02…).</summary>
    public static CodeList Carc => _carc.Value;

    /// <summary>Remittance advice remark codes: more about an adjustment (LQ02, MOA, MIA).</summary>
    public static CodeList Rarc => _rarc.Value;

    /// <summary>277 claim status category codes (STC01-1).</summary>
    public static CodeList StatusCategory => _category.Value;

    /// <summary>277 claim status codes (STC01-2).</summary>
    public static CodeList Status => _status.Value;
}
