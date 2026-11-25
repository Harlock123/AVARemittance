using AVARemittance.Core.Codes;

namespace AVARemittance.Core.Remittance;

/// <summary>Each part of an 835 in plain words, the same in the window, the reports and what agents read.</summary>
public static class Words
{
    /// <summary>"CO-45 $40.00: Charge exceeds fee schedule… (contractual obligations: the provider's to write off: not billable to the patient)".</summary>
    public static string Describe(Adjustment a) =>
        $"{a.Group}-{a.Reason} {Money.Say(a.Amount)}" + (a.Quantity is { } q && q != 0 ? $" ({q:0.##} units)" : "")
        + $": {CodeList.Carc.Say(a.Reason)} ({RemitCodes.Say(RemitCodes.Group, a.Group).ToLowerInvariant()}: {RemitCodes.GroupMeaning.GetValueOrDefault(a.Group, "group not in the 835's list")})";

    public static string Describe(Remark r) => $"{r.Code}: {CodeList.Rarc.Say(r.Code)}";

    /// <summary>"WO −$75.00 Overpayment Recovery (reference HV26100417): taken from this payment".</summary>
    public static string Describe(ProviderAdjustment a) =>
        $"{a.Reason} {Money.Say(a.Amount)} {RemitCodes.Say(RemitCodes.ProviderAdjustment, a.Reason)}"
        + (a.Reference.Length > 0 ? $" (reference {a.Reference})" : "")
        + (a.Amount > 0 ? ": taken from this payment" : a.Amount < 0 ? ": added to this payment" : "");

    public static string Describe(Dated d) => $"{RemitCodes.Say(RemitCodes.Date, d.Qualifier)}: {(d.Date is { } x ? Money.Day(x) : d.Text)}";

    public static string Describe(Reference r) => $"{RemitCodes.Say(RemitCodes.Reference, r.Qualifier)}: {r.Value}";

    public static string Describe(Amount a) => $"{RemitCodes.Say(RemitCodes.Amount, a.Qualifier)}: {Money.Say(a.Value)}";

    public static string State(ClaimState s) => s switch
    {
        ClaimState.Paid => "paid",
        ClaimState.PartlyDenied => "partly denied",
        ClaimState.Denied => "denied",
        ClaimState.PatientOwes => "patient owes",
        ClaimState.Reversal => "reversal",
        _ => s.ToString(),
    };

    /// <summary>"Paid $85.00 of $150.00; the patient owes $25.00."</summary>
    public static string Summary(ClaimPayment c)
    {
        if (c.IsReversal) return $"Takes back {Money.Say(-c.Paid)} paid earlier on this claim" + (c.PayerClaim.Length > 0 ? $" (payer claim {c.PayerClaim})." : ".");
        var text = $"Paid {Money.Say(c.Paid)} of {Money.Say(c.Charge)}";
        if (c.PatientResponsibility != 0) text += $"; the patient owes {Money.Say(c.PatientResponsibility)}";
        var written = c.AllAdjustments.Where(a => a.Group is "CO" or "PI" or "OA").Sum(a => a.Amount);
        if (written != 0) text += $"; {Money.Say(written)} adjusted off";
        return text + ".";
    }

    /// <summary>"Paid $1,234.56 by EFT on 2026-11-10, trace 1HV0000123, for 6 claims."</summary>
    public static string Summary(Payment p)
    {
        var how = p.Method switch { "ACH" => "by EFT", "CHK" => "by check", "FWT" => "by wire", "NON" => "(no payment)", _ => $"by {p.MethodWord}" };
        return $"{(p.Total == 0 ? "Nothing paid" : $"Paid {Money.Say(p.Total)}")} {how}" + (p.PaidOn is { } d ? $" on {Money.Day(d)}" : "")
            + (p.Trace.Length > 0 ? $", {(p.IsCheck ? "check" : "trace")} {p.Trace}" : "")
            + $", for {p.Claims.Count} claim{(p.Claims.Count == 1 ? "" : "s")}.";
    }

    public static string Code(ServicePayment l) =>
        l.Code + (l.OriginalProcedure.Length > 0 && l.OriginalProcedure != l.Procedure ? $" (billed as {string.Join('-', new[] { l.OriginalProcedure }.Concat(l.OriginalModifiers))})" : "");
}
