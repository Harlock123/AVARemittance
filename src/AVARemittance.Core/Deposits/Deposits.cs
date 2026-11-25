using System.Globalization;
using System.Text.RegularExpressions;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Deposits;

/// <summary>A credit on the bank statement: when, how much, the bank's words, and the trace or check number in them.</summary>
public sealed record Deposit
{
    public required string Source { get; init; }

    /// <summary>The row (a list) or record (BAI2) it came from, from 1.</summary>
    public int Row { get; init; }
    public DateOnly? Date { get; init; }
    public decimal Amount { get; init; }
    public string Description { get; init; } = "";
    public string Reference { get; init; } = "";

    /// <summary>The 835's TRN02 the deposit carries: from the CCD+ addenda (TRN*1*…), or the check number.</summary>
    public string Trace { get; init; } = "";

    /// <summary>An insurance payment by the bank's words: an HCCLAIMPMT, or an addenda with a trace.</summary>
    public bool LooksLikeInsurance => Description.Contains("HCCLAIMPMT", StringComparison.OrdinalIgnoreCase) || Description.Contains("TRN*1*", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A bank statement read into its deposits (credits only: debits aren't remittances).</summary>
public sealed class BankStatement
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>"bank statement" or "BAI2 statement".</summary>
    public required string Kind { get; init; }
    public List<Deposit> Deposits { get; } = [];
    public List<string> Problems { get; } = [];

    public static readonly IReadOnlyDictionary<string, string[]> Names = new Dictionary<string, string[]>
    {
        ["date"] = ["date", "posted", "posting date", "transaction date", "value date"],
        ["description"] = ["description", "details", "memo", "narrative", "transaction description"],
        ["amount"] = ["amount", "transaction amount"],
        ["credit"] = ["credit", "credits", "deposit", "deposits", "credit amount"],
        ["debit"] = ["debit", "debits", "withdrawal", "withdrawals", "debit amount"],
        ["reference"] = ["reference", "check number", "check", "check no", "bank reference", "ref"],
    };

    public static bool LooksLikeOne(string path)
    {
        if (IsBai(path)) return true;
        var keys = Table.HeaderKeys(path, Names);
        return keys.Contains("date") && keys.Contains("description") && (keys.Contains("amount") || keys.Contains("credit"));
    }

    private static bool IsBai(string path)
    {
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".bai" or ".bai2" or ".txt")) return false;
        try { return File.ReadLines(path).FirstOrDefault()?.StartsWith("01,", StringComparison.Ordinal) == true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static BankStatement Load(string path) => IsBai(path) ? LoadBai(path) : LoadList(path);

    private static BankStatement LoadList(string path)
    {
        var table = Table.Read(path, Names);
        var s = new BankStatement { Path = path, Kind = "bank statement" };
        var row = 0;
        foreach (var r in table.Rows)
        {
            row++;
            var amount = table.Has("amount") ? Table.Amount(table.Get(r, "amount")) : Table.Amount(table.Get(r, "credit")) ?? -Table.Amount(table.Get(r, "debit"));
            if (amount is not { } a)
            {
                s.Problems.Add($"Row {row}: no amount it could read.");
                continue;
            }
            if (a <= 0) continue;
            var text = table.Get(r, "description");
            var reference = table.Get(r, "reference");
            s.Deposits.Add(new Deposit
            {
                Source = s.Name, Row = row, Date = Table.Date(table.Get(r, "date")), Amount = a, Description = text, Reference = reference,
                Trace = TraceIn(text, reference),
            });
        }
        return s;
    }

    /// <summary>
    /// BAI2 (the cash management balance reporting format): 02 groups carry the as-of date, 16 records the transactions
    /// (type code, amount in cents, funds type and its extra fields, bank and customer references, text), 88 records
    /// continue the one before. Credits are type codes 100 to 399.
    /// </summary>
    private static BankStatement LoadBai(string path)
    {
        var s = new BankStatement { Path = path, Kind = "BAI2 statement" };
        DateOnly? asOf = null;
        var records = new List<(int Line, string Text)>();
        var n = 0;
        foreach (var raw in File.ReadLines(path))
        {
            n++;
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("88,", StringComparison.Ordinal) && records.Count > 0)
                records[^1] = (records[^1].Line, records[^1].Text.TrimEnd('/') + "," + line[3..]);
            else records.Add((n, line));
        }
        foreach (var (line, text) in records)
        {
            var body = text.TrimEnd('/');
            var f = body.Split(',');
            switch (f[0])
            {
                case "02":
                    asOf = f.Length > 4 && DateOnly.TryParseExact(f[4], "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    break;
                case "16":
                    if (f.Length < 4 || !int.TryParse(f[1], out var type)) { s.Problems.Add($"Line {line}: a 16 record it couldn't read."); break; }
                    var cents = decimal.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
                    // The funds type decides how many fields follow it before the references.
                    var at = 4;
                    switch (f[3])
                    {
                        case "S": at += 3; break;
                        case "V": at += 2; break;
                        case "D": at += 1 + 2 * (int.TryParse(f.ElementAtOrDefault(4), out var k) ? k : 0); break;
                    }
                    var bankRef = f.ElementAtOrDefault(at) ?? "";
                    var customerRef = f.ElementAtOrDefault(at + 1) ?? "";
                    var words = string.Join(",", f.Skip(at + 2));
                    if (type is < 100 or > 399) break;
                    s.Deposits.Add(new Deposit
                    {
                        Source = s.Name, Row = line, Date = asOf, Amount = cents / 100m, Description = words, Reference = customerRef.Length > 0 ? customerRef : bankRef,
                        Trace = TraceIn(words, customerRef),
                    });
                    break;
            }
        }
        if (!records.Any(r => r.Text.StartsWith("99,", StringComparison.Ordinal))) s.Problems.Add("It has no 99 (file trailer) record: it may be cut off.");
        return s;
    }

    /// <summary>The trace in the bank's words: TRN*1*trace*… from the addenda, or else a check number in the reference.</summary>
    private static string TraceIn(string text, string reference)
    {
        var m = Regex.Match(text, @"TRN\*1\*([^*\\~]+)");
        if (m.Success) return m.Groups[1].Value.Trim();
        var check = Regex.Match(text, @"CHE?C?K\s*#?\s*(\d{4,})", RegexOptions.IgnoreCase);
        if (check.Success) return check.Groups[1].Value;
        return reference.All(char.IsDigit) ? reference : "";
    }
}

/// <summary>How a payment and the money for it were found to go together, or didn't.</summary>
public sealed record DepositMatch(Deposit? Deposit, IReadOnlyList<Payment> Payments, string By)
{
    /// <summary>The deposit less what the 835s say was sent: 0 when they agree.</summary>
    public decimal Difference => (Deposit?.Amount ?? 0) - Payments.Sum(p => p.Total);
}

/// <summary>The deposits matched to the payments, and what's left over on each side.</summary>
public sealed class DepositReconciliation
{
    public List<DepositMatch> Matched { get; } = [];

    /// <summary>Insurance money on the statement with no 835 open for it.</summary>
    public List<Deposit> NoRemittance { get; } = [];

    /// <summary>Other credits: not insurance by the bank's words (patients' card payments, transfers).</summary>
    public List<Deposit> Other { get; } = [];

    /// <summary>Payments of money with no deposit on the open statements.</summary>
    public List<Payment> NoMoney { get; } = [];

    public DepositMatch? For(Payment p) => Matched.FirstOrDefault(m => m.Payments.Contains(p));
}

/// <summary>
/// Matches deposits to 835 payments: by the trace (TRN02) the deposit carries, or the check number; then by amount, for
/// a payment with no trace on the statement, within a week of the 835's date; then one deposit for several 835s of the
/// same trace or day. A difference is said, and named when a provider-level adjustment explains it.
/// </summary>
public static class DepositMatcher
{
    public static DepositReconciliation Match(IEnumerable<Deposit> deposits, IEnumerable<Payment> payments)
    {
        var r = new DepositReconciliation();
        var open = payments.Where(p => p.Total != 0 && p.Method != "NON").ToList();
        var left = deposits.ToList();
        foreach (var d in left.ToList())
        {
            if (d.Trace.Length == 0) continue;
            var same = open.Where(p => p.Trace.Equals(d.Trace, StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count == 0) continue;
            r.Matched.Add(new DepositMatch(d, same, same[0].IsCheck ? "check number" : "trace"));
            open.RemoveAll(same.Contains);
            left.Remove(d);
        }
        foreach (var d in left.ToList())
        {
            var p = open.FirstOrDefault(p => p.Total == d.Amount && Near(p.PaidOn, d.Date, 7));
            if (p == null) continue;
            r.Matched.Add(new DepositMatch(d, [p], "amount and date"));
            open.Remove(p);
            left.Remove(d);
        }
        foreach (var d in left.ToList())
        {
            var group = open.Where(p => Near(p.PaidOn, d.Date, 3)).GroupBy(p => (p.Payer.Name, p.PaidOn)).FirstOrDefault(g => g.Count() > 1 && g.Sum(p => p.Total) == d.Amount);
            if (group == null) continue;
            r.Matched.Add(new DepositMatch(d, [.. group], "several 835s, one deposit"));
            open.RemoveAll(group.Contains);
            left.Remove(d);
        }
        foreach (var d in left) (d.LooksLikeInsurance ? r.NoRemittance : r.Other).Add(d);
        r.NoMoney.AddRange(open);
        return r;
    }

    private static bool Near(DateOnly? a, DateOnly? b, int days) => a is { } x && b is { } y && Math.Abs(x.DayNumber - y.DayNumber) <= days;

    /// <summary>"The deposit is $75.00 more than the 835 says: the size of its WO overpayment recovery; the bank may have the payment before the recoupment."</summary>
    public static string Describe(DepositMatch m)
    {
        var d = m.Deposit!;
        var text = $"{Money.Say(d.Amount)} on {Money.Day(d.Date)}: {string.Join(" and ", m.Payments.Select(p => $"{p.Payer.Name}'s {Money.Say(p.Total)} {p.MethodWord} {p.Trace}"))} (by {m.By})";
        if (m.Difference == 0) return text + ".";
        text += $". The deposit is {Money.Say(Math.Abs(m.Difference))} {(m.Difference > 0 ? "more" : "less")} than the 835{(m.Payments.Count > 1 ? "s say" : " says")} was sent";
        var plb = m.Payments.SelectMany(p => p.ProviderAdjustments).Where(a => a.Amount == m.Difference || a.Amount == -m.Difference).FirstOrDefault();
        if (plb != null) return text + $": the size of its {plb.Reason} {Codes.RemitCodes.Say(Codes.RemitCodes.ProviderAdjustment, plb.Reason).ToLowerInvariant()}, so the bank may have the money {(m.Difference > 0 ? "before" : "after")} it.";
        return text + ".";
    }
}
