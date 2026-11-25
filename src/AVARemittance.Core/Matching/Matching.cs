using AVARemittance.Core.Billing;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Matching;

/// <summary>Where a billed claim stands, from every 835 open about it.</summary>
public enum BilledState { Paid, PartlyPaid, Denied, PatientOwes, Reversed, NoAnswer, PaidTwice }

/// <summary>A billed line and the 835 lines that answered it.</summary>
public sealed record LineMatch(BilledLine Billed, IReadOnlyList<ServicePayment> Answers)
{
    /// <summary>The answers still standing (not reversed).</summary>
    public IReadOnlyList<ServicePayment> Standing { get; init; } = [];

    public decimal Paid => Standing.Sum(a => a.Paid);
    public bool PaidTwice => Standing.Count(a => a.Paid > 0) > 1;
    public bool Denied => Standing.Count > 0 && Standing.All(a => a.Denied);

    /// <summary>Bundled into another line (CO-97 and its kin): paid nothing, and not a denial to work.</summary>
    public bool Bundled => Standing.Count > 0 && Standing.All(a => a.Paid == 0 && a.Adjustments.Any(x => x.Reason is "97" or "234" or "B15"));

    public string Word => Standing.Count == 0 ? Answers.Count > 0 ? "reversed" : "not answered"
        : PaidTwice ? "paid twice" : Bundled ? "bundled" : Denied ? "denied"
        : Paid == 0 && Standing.Any(a => a.Adjustments.Any(x => x.Group == "PR")) ? "patient owes" : "paid";
}

/// <summary>
/// A billed claim and the 835 claims that answered it, oldest first: a payment, a reversal and its correction, the
/// pieces of a split claim, a second payment of the same claim. <see cref="Standing"/> leaves out what a reversal
/// took back.
/// </summary>
public sealed class ClaimMatch
{
    public required BilledClaim Billed { get; init; }
    public List<ClaimPayment> Answers { get; } = [];
    public List<LineMatch> Lines { get; } = [];

    /// <summary>How the answers were found: "account", or "patient, date and charge".</summary>
    public string By { get; set; } = "";

    /// <summary>The answers not reversed since.</summary>
    public IReadOnlyList<ClaimPayment> Standing =>
        [.. Answers.Where(a => !a.IsReversal && !Answers.Any(r => r.IsReversal && r.PayerClaim == a.PayerClaim && Later(r, a)))];

    public decimal Paid => Answers.Sum(a => a.Paid);
    public decimal PatientOwes => Standing.Sum(a => a.PatientResponsibility);
    public decimal Adjusted => Standing.Sum(a => a.AllAdjustments.Where(x => x.Group != "PR").Sum(x => x.Amount));

    /// <summary>The payer split the claim: more than one standing answer, each for different lines.</summary>
    public bool Split => Standing.Count > 1 && !Lines.Any(l => l.Standing.Count > 1);

    public bool Corrected => Answers.Any(a => a.IsReversal) && Standing.Count > 0;

    public BilledState State =>
        Answers.Count == 0 ? BilledState.NoAnswer
        : Standing.Count == 0 ? BilledState.Reversed
        : Lines.Any(l => l.PaidTwice) ? BilledState.PaidTwice
        : Lines.Count > 0 && Lines.All(l => l.Denied || l.Bundled) || Standing.All(a => a.State == ClaimState.Denied) ? BilledState.Denied
        : Standing.All(a => a.State == ClaimState.PatientOwes) ? BilledState.PatientOwes
        : Lines.Any(l => l.Denied && !l.Bundled || l.Standing.Count == 0) || Standing.Any(a => a.State == ClaimState.Denied) ? BilledState.PartlyPaid
        : BilledState.Paid;

    /// <summary>What's paid too much when the payer paid a line twice: the second payment of each.</summary>
    public decimal Overpaid => Lines.Where(l => l.PaidTwice).Sum(l => l.Standing.Where(a => a.Paid > 0).Skip(1).Sum(a => a.Paid));

    private static bool Later(ClaimPayment a, ClaimPayment b) =>
        (a.Payment.PaidOn ?? DateOnly.MinValue, a.Payment.File.Name, a.Number).CompareTo((b.Payment.PaidOn ?? DateOnly.MinValue, b.Payment.File.Name, b.Number)) > 0;
}

/// <summary>The billed claims matched to the 835s, and the 835 claims nothing billed answers to.</summary>
public sealed class Reconciliation
{
    public List<ClaimMatch> Claims { get; } = [];

    /// <summary>835 claims that no open billed claim matches (their 837 isn't open, or it was billed elsewhere).</summary>
    public List<ClaimPayment> Unbilled { get; } = [];

    /// <summary>The day "days since billed" is counted to.</summary>
    public DateOnly Today { get; init; }

    public int DaysWaiting(ClaimMatch m) => m.Billed.Billed is { } d ? Today.DayNumber - d.DayNumber : 0;
}

/// <summary>
/// Matches billed claims to the 835 claims that answer them: by the account (CLP01 is the 837's CLM01) at the same
/// payer, then by patient, service dates and charge. Lines are matched by the line item control number (REF 6R),
/// then by procedure, modifiers and date, allowing a line paid as a different code (SVC06 has what was billed).
/// </summary>
public static class Matcher
{
    public static Reconciliation Match(IEnumerable<BilledClaim> billed, IEnumerable<ClaimPayment> answers, DateOnly today)
    {
        var all = answers.OrderBy(a => a.Payment.PaidOn ?? DateOnly.MinValue).ThenBy(a => a.Payment.File.Name, StringComparer.Ordinal).ThenBy(a => a.Number).ToList();
        var used = new HashSet<ClaimPayment>();
        var r = new Reconciliation { Today = today };
        var list = billed.ToList();
        foreach (var b in list)
        {
            var m = new ClaimMatch { Billed = b };
            var found = all.Where(a => !used.Contains(a) && a.Account.Equals(b.Account, StringComparison.OrdinalIgnoreCase) && SamePayer(b, a.Payment)).ToList();
            m.By = "account";
            if (found.Count == 0)
            {
                found = [.. all.Where(a => !used.Contains(a) && SamePayer(b, a.Payment) && SamePatient(b, a) && a.From == b.From && Math.Abs(a.Charge) == b.Charge)];
                m.By = "patient, date and charge";
            }
            m.Answers.AddRange(found);
            used.UnionWith(found);
            r.Claims.Add(m);
        }
        foreach (var m in r.Claims) MatchLines(m);
        r.Unbilled.AddRange(all.Where(a => !used.Contains(a)));
        return r;
    }

    private static void MatchLines(ClaimMatch m)
    {
        var standing = m.Standing.SelectMany(a => a.Lines).ToList();
        var every = m.Answers.SelectMany(a => a.Lines).ToList();
        foreach (var line in m.Billed.Lines)
        {
            bool Same(ServicePayment s) =>
                line.ControlNumber.Length > 0 && s.ControlNumber.Length > 0 ? s.ControlNumber == line.ControlNumber
                : (s.Procedure == line.Procedure && s.Modifiers.SequenceEqual(line.Modifiers) || s.OriginalProcedure == line.Procedure) && (s.From == null || line.Date == null || s.From == line.Date);
            m.Lines.Add(new LineMatch(line, [.. every.Where(Same)]) { Standing = [.. standing.Where(Same)] });
        }
    }

    /// <summary>The same payer: the 835's payer ID (REF 2U, or N1 PR's ID) against the claim's, or else the name.</summary>
    public static bool SamePayer(BilledClaim b, Payment p)
    {
        var ids = p.Payer.References.Where(x => x.Qualifier == "2U").Select(x => x.Value).Append(p.Payer.Id).Where(x => x.Length > 0).ToList();
        if (b.PayerId.Length > 0 && ids.Count > 0) return ids.Contains(b.PayerId, StringComparer.OrdinalIgnoreCase);
        return b.Payer.Length == 0 || b.Payer.Equals(p.Payer.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePatient(BilledClaim b, ClaimPayment a)
    {
        var p = a.Patient;
        if (p == null) return false;
        return b.MemberId.Length > 0 && p.Id.Equals(b.MemberId, StringComparison.OrdinalIgnoreCase)
            || p.Last.Equals(b.Last, StringComparison.OrdinalIgnoreCase) && p.First.Equals(b.First, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Matching in words.</summary>
public static class MatchWords
{
    public static string State(BilledState s) => s switch
    {
        BilledState.Paid => "paid",
        BilledState.PartlyPaid => "partly paid",
        BilledState.Denied => "denied",
        BilledState.PatientOwes => "patient owes",
        BilledState.Reversed => "reversed",
        BilledState.NoAnswer => "no answer",
        BilledState.PaidTwice => "paid twice",
        _ => s.ToString(),
    };

    /// <summary>"Billed $300.00 on 2026-11-01; paid $150.00 on 2026-11-10, reversed on 2026-11-17 and paid $162.00 again."</summary>
    public static string Describe(ClaimMatch m, Reconciliation r)
    {
        var b = m.Billed;
        var text = $"Billed {Money.Say(b.Charge)}" + (b.Billed is { } on ? $" on {Money.Day(on)}" : "");
        if (m.Answers.Count == 0) return text + $"; no answer in the open 835s" + (b.Billed != null ? $" after {r.DaysWaiting(m)} days." : ".");
        var parts = new List<string>();
        foreach (var a in m.Answers)
        {
            var when = a.Payment.PaidOn is { } d ? $" on {Money.Day(d)}" : "";
            parts.Add(a.IsReversal ? $"reversed{when} ({Money.Say(a.Paid)})" : $"{(parts.Count > 0 && m.Standing.Count == 1 ? "paid again " : "paid ")}{Money.Say(a.Paid)}{when}");
        }
        text += "; " + string.Join(", ", parts);
        if (m.Split) text += $": the payer split it into {m.Standing.Count} claims";
        text += $". In all {Money.Say(m.Paid)} paid";
        if (m.PatientOwes != 0) text += $", {Money.Say(m.PatientOwes)} the patient's";
        if (m.Adjusted != 0) text += $", {Money.Say(m.Adjusted)} adjusted off";
        text += ".";
        if (m.State == BilledState.PaidTwice) text += $" The payer paid it twice: {Money.Say(m.Overpaid)} too much. Expect it to be taken back, or refund it.";
        if (m.State == BilledState.Reversed) text += " The payment was taken back, and nothing has replaced it yet.";
        var answered = m.Standing.Sum(a => a.Charge);
        if (m.Standing.Count > 0 && m.State != BilledState.PaidTwice && answered != b.Charge) text += $" The 835s answer for {Money.Say(answered)} of the {Money.Say(b.Charge)} billed.";
        return text;
    }

    /// <summary>"1. 99213 on 2026-10-20, $150.00: paid $85.00."</summary>
    public static string Describe(LineMatch l) =>
        $"{l.Billed.Number}. {l.Billed.Code}" + (l.Billed.Units != 1 ? $" × {l.Billed.Units:0.##}" : "") + (l.Billed.Date is { } d ? $" on {Money.Day(d)}" : "")
        + $", {Money.Say(l.Billed.Charge)}: {l.Word}" + (l.Standing.Count > 0 && l.Paid != 0 ? $" {Money.Say(l.Paid)}" : "")
        + (l.PaidTwice ? $" (in {l.Standing.Count(a => a.Paid > 0)} payments)" : "") + ".";
}
