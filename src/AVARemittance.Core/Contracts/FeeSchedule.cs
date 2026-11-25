using AVARemittance.Core.Billing;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Contracts;

/// <summary>
/// One rate in a fee schedule: what a payer is expected to allow for a procedure (with a modifier, between dates), or a
/// percentage of another schedule's rate (a payer paying 65% of MEDICARE's, written with procedure *).
/// </summary>
public sealed record Rate
{
    public required string Payer { get; init; }
    public required string Procedure { get; init; }
    public string Modifier { get; init; } = "";
    public decimal? Amount { get; init; }
    public decimal? Percent { get; init; }
    public string Basis { get; init; } = "";
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public int Row { get; init; }

    public bool Covers(DateOnly? day) => day == null || (From == null || day >= From) && (To == null || day <= To);
}

/// <summary>What a fee schedule expects for a line, and how it got there.</summary>
public sealed record Expected(decimal Amount, string Because);

/// <summary>
/// A fee schedule the person loads: a CSV or Excel file with Payer (its ID or name), Procedure, Modifier, Rate, and
/// optionally From and To; or a row with Procedure *, a Percent and a Basis (another payer's rows, such as MEDICARE).
/// The schedule is the person's: an expected amount is only as right as it.
/// </summary>
public sealed class FeeSchedule
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public List<Rate> Rates { get; } = [];
    public List<string> Problems { get; } = [];

    public static readonly IReadOnlyDictionary<string, string[]> Names = new Dictionary<string, string[]>
    {
        ["payer"] = ["payer", "payer id", "payer name", "plan", "insurance"],
        ["procedure"] = ["procedure", "cpt", "procedure code", "code", "hcpcs"],
        ["modifier"] = ["modifier", "mod", "modifiers"],
        ["rate"] = ["rate", "amount", "allowed", "fee", "contracted rate", "expected"],
        ["percent"] = ["percent", "percentage", "% of basis", "pct"],
        ["basis"] = ["basis", "of", "base schedule"],
        ["from"] = ["from", "effective", "effective from", "start"],
        ["to"] = ["to", "until", "effective to", "end"],
        ["account"] = ["account", "account number", "patient account"],
    };

    public static bool LooksLikeOne(string path)
    {
        var keys = Table.HeaderKeys(path, Names);
        return keys.Contains("payer") && keys.Contains("procedure") && (keys.Contains("rate") || keys.Contains("percent")) && !keys.Contains("account");
    }

    public static FeeSchedule Load(string path)
    {
        var t = Table.Read(path, Names);
        var s = new FeeSchedule { Path = path };
        var row = 0;
        foreach (var r in t.Rows)
        {
            row++;
            var rate = new Rate
            {
                Row = row, Payer = t.Get(r, "payer").ToUpperInvariant(), Procedure = t.Get(r, "procedure").ToUpperInvariant(), Modifier = t.Get(r, "modifier").ToUpperInvariant(),
                Amount = Table.Amount(t.Get(r, "rate")), Percent = Table.Amount(t.Get(r, "percent").TrimEnd('%')), Basis = t.Get(r, "basis").ToUpperInvariant(),
                From = Table.Date(t.Get(r, "from")), To = Table.Date(t.Get(r, "to")),
            };
            if (rate.Payer.Length == 0 || rate.Procedure.Length == 0) { s.Problems.Add($"Row {row}: no payer or procedure."); continue; }
            if (rate.Amount == null && (rate.Percent == null || rate.Basis.Length == 0)) { s.Problems.Add($"Row {row}: no rate, and no percent of a basis."); continue; }
            s.Rates.Add(rate);
        }
        return s;
    }

    /// <summary>
    /// What the schedule expects for a line: the payer's own rate for the procedure and modifier on the line's date (a
    /// rate with no modifier when there's none for it), times the units; or else the payer's percentage of its basis's rate.
    /// </summary>
    public Expected? For(ServicePayment line)
    {
        var payer = line.Claim.Payment.Payer;
        var names = payer.References.Where(r => r.Qualifier == "2U").Select(r => r.Value).Append(payer.Id).Append(payer.Name).Where(n => n.Length > 0).Select(n => n.ToUpperInvariant()).ToHashSet();
        var units = line.Units is { } u && u > 0 ? u : 1;
        var own = Find(names, line);
        if (own?.Amount is { } a) return new Expected(Math.Round(a * units, 2), $"{Name} row {own.Row}: {Money.Say(a)} for {Code(own)}" + (units != 1 ? $" × {units:0.##}" : ""));
        var percent = Rates.FirstOrDefault(r => names.Contains(r.Payer) && r.Procedure == "*" && r.Percent != null && r.Covers(line.From));
        if (percent != null && Find([percent.Basis], line) is { Amount: { } b } basis)
        {
            var amount = Math.Round(b * percent.Percent!.Value / 100m * units, 2);
            return new Expected(amount, $"{Name} rows {percent.Row} and {basis.Row}: {percent.Percent:0.##}% of {percent.Basis}'s {Money.Say(b)} for {Code(basis)}" + (units != 1 ? $" × {units:0.##}" : ""));
        }
        return null;
    }

    private Rate? Find(IEnumerable<string> payers, ServicePayment line)
    {
        var set = payers.ToHashSet();
        var candidates = Rates.Where(r => set.Contains(r.Payer) && r.Procedure == line.Procedure && r.Amount != null && r.Covers(line.From)).ToList();
        return candidates.FirstOrDefault(r => r.Modifier.Length > 0 && line.Modifiers.Contains(r.Modifier)) ?? candidates.FirstOrDefault(r => r.Modifier.Length == 0);
    }

    private static string Code(Rate r) => r.Modifier.Length > 0 ? $"{r.Procedure}-{r.Modifier}" : r.Procedure;
}
