using AVARemittance.Core.X12;

namespace AVARemittance.Core.Remittance;

public enum Severity { Error, Warning }

/// <summary>A check on an 835: its id, what it checks, why it matters, and where the rule comes from.</summary>
public sealed record Rule(string Id, string Title, string Why, string Source, Severity Severity);

/// <summary>What a check found: the rule, what's wrong in words, and the segments it rests on.</summary>
public sealed record Finding(Rule Rule, string Message, Payment Payment, ClaimPayment? Claim, ServicePayment? Line, IReadOnlyList<Segment> Segments)
{
    public override string ToString() => $"{Rule.Id}: {Message}";
}

/// <summary>
/// The 835's arithmetic: an 835 that doesn't balance is the first thing a poster needs to know, because posting it
/// as it stands puts the wrong money on the books. Each rule says what it checks, why, and its source.
/// </summary>
public static class Checks
{
    private const string Guide = "ASC X12 005010X221A1, the 835 implementation guide's balancing rules";

    public static readonly Rule B1 = new("B1", "The payment equals the claims less the provider adjustments",
        "The money sent (BPR02) is the sum of the claim payments (CLP04) less the provider-level adjustments (PLB). When it isn't, the deposit won't match what's posted.", Guide, Severity.Error);
    public static readonly Rule B2 = new("B2", "Each claim's charge less its adjustments is what was paid",
        "A claim's charge (CLP03), less every adjustment on the claim and its lines (CAS), is its payment (CLP04). Otherwise some of the charge is unexplained: neither paid nor adjusted.", Guide, Severity.Error);
    public static readonly Rule B3 = new("B3", "Each line's charge less its adjustments is what was paid",
        "A line's charge (SVC02), less its adjustments (CAS), is its payment (SVC03).", Guide, Severity.Error);
    public static readonly Rule B4 = new("B4", "The lines' payments add up to the claim's",
        "The claim's payment (CLP04) is the sum of its lines' payments (SVC03) less any adjustments made to the claim as a whole.", Guide, Severity.Error);
    public static readonly Rule B5 = new("B5", "The patient's share is the patient-responsibility adjustments",
        "CLP05, what the patient owes, is the sum of the PR adjustments on the claim and its lines. When they differ, the bill to the patient is in doubt.", Guide, Severity.Warning);
    public static readonly Rule B6 = new("B6", "A payment of nothing says so",
        "When the payment (BPR02) is zero, the method (BPR04) is NON, no payment; a check or transfer of nothing can't be deposited.", RemitCodesSource, Severity.Warning);
    public static readonly Rule B7 = new("B7", "A reversal takes the money back",
        "A reversal of a previous payment (CLP02 22) carries the original claim's amounts with their signs turned, so the charge and payment are negative or zero.", RemitCodesSource, Severity.Warning);
    public static readonly Rule E1 = new("E1", "The segment count is right",
        "SE01 counts the transaction's segments from ST to SE. A wrong count means segments were lost or added on the way.", "ASC X12 005010 envelope (SE segment)", Severity.Error);

    private const string RemitCodesSource = Codes.RemitCodes.Source;

    public static IReadOnlyList<Rule> Rules { get; } = [B1, B2, B3, B4, B5, B6, B7, E1];

    public static List<Finding> Run(Payment p)
    {
        var found = new List<Finding>();
        var claims = p.Claims.Sum(c => c.Paid);
        var plb = p.ProviderAdjustments.Sum(a => a.Amount);
        if (claims - plb != p.Total)
            found.Add(new(B1, $"The payment is {Money.Say(p.Total)}, but the claims pay {Money.Say(claims)}" + (plb != 0 ? $" less {Money.Say(plb)} of provider adjustments, {Money.Say(claims - plb)}" : "")
                + $": {Money.Say(Math.Abs(claims - plb - p.Total))} {(claims - plb > p.Total ? "more than was sent" : "less than was sent")}.", p, null, null, [.. Opt(p.Bpr), .. p.ProviderAdjustments.Select(a => a.Segment).Distinct()]));
        if (p.Total == 0 && p.Method is not ("NON" or ""))
            found.Add(new(B6, $"Nothing is paid, but the method is {p.MethodWord} ({p.Method}), not NON.", p, null, null, [.. Opt(p.Bpr)]));
        foreach (var c in p.Claims)
        {
            var all = c.AllAdjustments.Sum(a => a.Amount);
            if (c.Charge - all != c.Paid)
                found.Add(new(B2, $"Claim {c.Account}: the charge {Money.Say(c.Charge)} less {Money.Say(all)} of adjustments is {Money.Say(c.Charge - all)}, but {Money.Say(c.Paid)} was paid: {Money.Say(Math.Abs(c.Charge - all - c.Paid))} unexplained.", p, c, null, [c.Segments[0], .. c.AllAdjustments.Select(a => a.Segment).Distinct()]));
            if (c.Lines.Count > 0)
            {
                var lines = c.Lines.Sum(l => l.Paid) - c.Adjustments.Sum(a => a.Amount);
                if (lines != c.Paid)
                    found.Add(new(B4, $"Claim {c.Account}: its lines pay {Money.Say(c.Lines.Sum(l => l.Paid))}" + (c.Adjustments.Count > 0 ? $" less {Money.Say(c.Adjustments.Sum(a => a.Amount))} adjusted on the claim" : "") + $", but the claim says {Money.Say(c.Paid)}.", p, c, null, [c.Segments[0], .. c.Lines.Select(l => l.Segments[0])]));
            }
            var pr = c.AllAdjustments.Where(a => a.Group == "PR").Sum(a => a.Amount);
            if (pr != c.PatientResponsibility)
                found.Add(new(B5, $"Claim {c.Account}: the patient's share says {Money.Say(c.PatientResponsibility)}, but its PR adjustments come to {Money.Say(pr)}.", p, c, null, [c.Segments[0], .. c.AllAdjustments.Where(a => a.Group == "PR").Select(a => a.Segment).Distinct()]));
            if (c.IsReversal && (c.Charge > 0 || c.Paid > 0))
                found.Add(new(B7, $"Claim {c.Account} reverses a payment, but its amounts aren't negative.", p, c, null, [c.Segments[0]]));
            foreach (var l in c.Lines)
            {
                var adj = l.Adjustments.Sum(a => a.Amount);
                if (l.Charge - adj != l.Paid)
                    found.Add(new(B3, $"Claim {c.Account}, line {l.Number} ({l.Code}): the charge {Money.Say(l.Charge)} less {Money.Say(adj)} of adjustments is {Money.Say(l.Charge - adj)}, but {Money.Say(l.Paid)} was paid.", p, c, l, [.. l.Segments.Where(s => s.Id is "SVC" or "CAS")]));
            }
        }
        if (p.Source.Trailer is { } se && int.TryParse(se[1].Trim(), out var count) && count != p.Source.Body.Count + 2)
            found.Add(new(E1, $"SE01 says {count} segments, but the transaction has {p.Source.Body.Count + 2}.", p, null, null, [se]));
        return found;
    }

    public static List<Finding> Run(RemitFile f) => [.. f.Payments.SelectMany(Run)];

    private static IEnumerable<Segment> Opt(Segment? s) => s == null ? [] : [s];
}
