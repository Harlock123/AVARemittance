using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Contracts;

/// <summary>A line the fee schedule says was allowed less than expected, or whose amounts don't agree with each other.</summary>
public sealed record Underpayment(string Kind, ServicePayment Line, decimal Allowed, Expected? Expected, decimal Below, string Message)
{
    public ClaimPayment Claim => Line.Claim;
}

/// <summary>A payer's underpayments for one procedure over the open 835s.</summary>
public sealed record UnderpaidCode(string Payer, string Procedure, int Lines, decimal Below);

/// <summary>
/// Lines below the fee schedule. Every flag says what it rests on, and is worded as below what the schedule expects,
/// never as wrong: the schedule is the person's, and the payer's contract decides.
/// <list type="bullet">
/// <item>U1: the allowed amount (AMT B6, or else the payment plus the patient's share) is less than the schedule
/// expects, by more than the tolerance;</item>
/// <item>U2: the patient's share and the payment don't add up to the allowed amount the payer sent;</item>
/// <item>U3: with no allowed amount sent, a CO-45 larger than the gap between the charge and the schedule's rate.</item>
/// </list>
/// Denied and bundled lines, reversals and payments reversed since are left out: they're the denials' business.
/// </summary>
public static class Underpayments
{
    public static List<Underpayment> Find(IEnumerable<ClaimPayment> claims, FeeSchedule? schedule, decimal tolerance)
    {
        var all = claims.ToList();
        var reversed = all.Where(c => c.IsReversal).Select(c => c.PayerClaim).ToHashSet();
        var found = new List<Underpayment>();
        foreach (var c in all.Where(c => !c.IsReversal && !(reversed.Contains(c.PayerClaim) && all.Any(r => r.IsReversal && r.PayerClaim == c.PayerClaim && r.Payment != c.Payment))))
        {
            foreach (var l in c.Lines)
            {
                if (l.Charge <= 0 || l.Denied || l.Paid == 0 && l.Adjustments.All(a => a.Group != "PR")) continue;
                var pr = l.Adjustments.Where(a => a.Group == "PR").Sum(a => a.Amount);
                var allowed = l.Allowed ?? l.Paid + pr;
                if (l.Allowed is { } sent && sent != l.Paid + pr && Math.Abs(sent - l.Paid - pr) > tolerance)
                    found.Add(new("U2", l, sent, null, sent - l.Paid - pr,
                        $"The payer allowed {Money.Say(sent)}, but the payment {Money.Say(l.Paid)} and the patient's {Money.Say(pr)} come to {Money.Say(l.Paid + pr)}: {Money.Say(Math.Abs(sent - l.Paid - pr))} {(sent > l.Paid + pr ? "unpaid" : "too much")}, by the payer's own numbers."));
                if (schedule?.For(l) is not { } e) continue;
                if (allowed < e.Amount - tolerance)
                {
                    var co45 = l.Adjustments.Where(a => a.Group == "CO" && a.Reason == "45").Sum(a => a.Amount);
                    found.Add(l.Allowed == null && co45 > 0
                        ? new("U3", l, allowed, e, e.Amount - allowed, $"CO-45 took {Money.Say(co45)} off, but the schedule leaves only {Money.Say(l.Charge - e.Amount)} between the {Money.Say(l.Charge)} charge and its {Money.Say(e.Amount)}: {Money.Say(e.Amount - allowed)} below what the schedule expects ({e.Because}).")
                        : new("U1", l, allowed, e, e.Amount - allowed, $"Allowed {Money.Say(allowed)}{(l.Allowed == null ? " (the payment and the patient's share; no allowed amount was sent)" : "")}; the schedule expects {Money.Say(e.Amount)}: {Money.Say(e.Amount - allowed)} below what the schedule expects ({e.Because})."));
                }
            }
        }
        return found;
    }

    /// <summary>What each payer allowed below the schedule, by procedure, the most first.</summary>
    public static List<UnderpaidCode> ByCode(IEnumerable<Underpayment> found) =>
        [.. found.Where(u => u.Kind != "U2").GroupBy(u => (u.Claim.Payment.Payer.Name, u.Line.Procedure))
            .Select(g => new UnderpaidCode(g.Key.Name, g.Key.Procedure, g.Count(), g.Sum(u => u.Below)))
            .OrderByDescending(x => x.Below).ThenBy(x => x.Payer, StringComparer.Ordinal).ThenBy(x => x.Procedure, StringComparer.Ordinal)];

    public static string Describe(Underpayment u) =>
        $"{u.Kind} {u.Claim.Account}, line {u.Line.Number} ({Words.Code(u.Line)}" + (u.Line.From is { } d ? $" on {Money.Day(d)}" : "") + $", {u.Claim.Payment.Payer.Name}): {u.Message}";
}
