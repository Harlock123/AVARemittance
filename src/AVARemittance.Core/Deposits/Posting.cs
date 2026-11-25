using AVARemittance.Core.Matching;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Deposits;

/// <summary>
/// A day's posting: the 835 payments dated that day, and what they come to. Received is what the 835s say was sent;
/// posted is what was paid on claims; unapplied is paid on claims nothing open billed (only when billed claims are
/// open); recouped is taken back at the provider level, and added is interest and the like.
/// </summary>
public sealed record PostingDay(DateOnly? Date, IReadOnlyList<Payment> Payments)
{
    public decimal Received => Payments.Sum(p => p.Total);
    public decimal Posted { get; init; }
    public decimal Unapplied { get; init; }
    public decimal Adjusted => Payments.SelectMany(p => p.Claims).SelectMany(c => c.AllAdjustments).Where(a => a.Group != "PR").Sum(a => a.Amount);
    public decimal PatientResponsibility => Payments.SelectMany(p => p.Claims).Sum(c => c.PatientResponsibility);
    public decimal Recouped => Payments.SelectMany(p => p.ProviderAdjustments).Where(a => a.Amount > 0).Sum(a => a.Amount);
    public decimal Added => -Payments.SelectMany(p => p.ProviderAdjustments).Where(a => a.Amount < 0).Sum(a => a.Amount);
    public decimal Deposited { get; init; }
    public int Claims => Payments.Sum(p => p.Claims.Count);
}

public static class Posting
{
    /// <summary>The payments by the day they were paid (BPR16), oldest first.</summary>
    public static List<PostingDay> Days(IEnumerable<Payment> payments, Reconciliation? billed, DepositReconciliation? deposits)
    {
        var unbilled = billed is { Claims.Count: > 0 } ? billed.Unbilled.ToHashSet() : [];
        return [.. payments.GroupBy(p => p.PaidOn).OrderBy(g => g.Key).Select(g =>
        {
            var claims = g.SelectMany(p => p.Claims).ToList();
            return new PostingDay(g.Key, [.. g])
            {
                Posted = claims.Where(c => !unbilled.Contains(c)).Sum(c => c.Paid),
                Unapplied = claims.Where(unbilled.Contains).Sum(c => c.Paid),
                Deposited = deposits == null ? 0 : g.Select(deposits.For).OfType<DepositMatch>().Distinct().Sum(m => m.Deposit?.Amount ?? 0),
            };
        })];
    }
}
