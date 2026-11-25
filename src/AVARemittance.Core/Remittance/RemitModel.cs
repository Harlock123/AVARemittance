using System.Globalization;
using AVARemittance.Core.X12;

namespace AVARemittance.Core.Remittance;

/// <summary>An 835 file: one payment (transaction) or more, each with its claims, and what went wrong reading it.</summary>
public sealed class RemitFile
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public required X12File X12 { get; init; }
    public List<Payment> Payments { get; } = [];
    public List<string> Problems { get; } = [];

    public IEnumerable<ClaimPayment> Claims => Payments.SelectMany(p => p.Claims);
}

/// <summary>A date, reference or amount with its qualifier, and the segment it came from.</summary>
public sealed record Dated(string Qualifier, DateOnly? Date, string Text, Segment Segment);
public sealed record Reference(string Qualifier, string Value, Segment Segment);
public sealed record Amount(string Qualifier, decimal Value, Segment Segment);

/// <summary>A payer or payee (the N1 loops), with its identifiers and contacts.</summary>
public sealed class Party
{
    public string Name { get; init; } = "";
    public string IdQualifier { get; init; } = "";
    public string Id { get; init; } = "";
    public string Address { get; set; } = "";
    public List<Reference> References { get; } = [];
    public List<string> Contacts { get; } = [];
    public List<Segment> Segments { get; } = [];
}

/// <summary>A person named in a claim (NM1): the patient, the insured, a corrected insured.</summary>
public sealed record Person(string Entity, string Last, string First, string Middle, string IdQualifier, string Id, Segment Segment)
{
    public string Name => string.Join(' ', new[] { First, Middle, Last }.Where(p => p.Length > 0));
    public string SortName => First.Length > 0 ? $"{Last}, {First}" : Last;
}

/// <summary>A provider or carrier named in a claim (NM1 82, TT, PR).</summary>
public sealed record Named(string Entity, string Name, string IdQualifier, string Id, Segment Segment);

/// <summary>An adjustment (one group, reason, amount and quantity of a CAS segment). Positive amounts reduce the payment.</summary>
public sealed record Adjustment(string Group, string Reason, decimal Amount, decimal? Quantity, Segment Segment);

/// <summary>A remark code (LQ HE, or MOA/MIA's remarks), and where it came from.</summary>
public sealed record Remark(string Code, Segment Segment);

/// <summary>
/// A provider-level adjustment (one reason and amount of a PLB segment): money taken from or added to the whole
/// payment, not to one claim. Positive amounts reduce the payment (a recoupment), negative ones add to it (interest).
/// </summary>
public sealed record ProviderAdjustment(string Provider, DateOnly? FiscalEnd, string Reason, string Reference, decimal Amount, Segment Segment);

/// <summary>
/// One 835 transaction: a payment (BPR) and its trace (TRN), the payer and payee, the claims it pays, and the
/// provider-level adjustments (PLB).
/// </summary>
public sealed class Payment
{
    public required RemitFile File { get; init; }
    public required Transaction Source { get; init; }

    /// <summary>1 for the first transaction in the file.</summary>
    public int Number { get; init; }

    public string Handling { get; set; } = "";
    public decimal Total { get; set; }
    public string CreditDebit { get; set; } = "";
    public string Method { get; set; } = "";
    public string Format { get; set; } = "";
    public string SenderBank { get; set; } = "";
    public string SenderAccount { get; set; } = "";
    public string ReceiverBank { get; set; } = "";
    public string ReceiverAccount { get; set; } = "";
    public DateOnly? PaidOn { get; set; }

    /// <summary>TRN02: the check number or EFT trace number, the one the bank deposit carries.</summary>
    public string Trace { get; set; } = "";

    /// <summary>TRN03: who made the trace (a 1 and the payer's tax ID, usually).</summary>
    public string Originator { get; set; } = "";
    public DateOnly? Produced { get; set; }
    public List<Reference> References { get; } = [];
    public Party Payer { get; set; } = new();
    public Party Payee { get; set; } = new();
    public List<ClaimPayment> Claims { get; } = [];
    public List<ProviderAdjustment> ProviderAdjustments { get; } = [];

    /// <summary>The BPR, TRN and other header segments, before the first claim.</summary>
    public List<Segment> Header { get; } = [];

    public Segment? Bpr { get; set; }
    public Segment? Trn { get; set; }

    public bool IsCheck => Method == "CHK";
    public string MethodWord => Method switch { "ACH" => "EFT", "CHK" => "check", "FWT" => "wire", "NON" => "no payment", "BOP" => "bank's choice", _ => Method };
    public string Label => $"{Payer.Name} · {Money.Say(Total)} {MethodWord} {Trace}".TrimEnd();
}

/// <summary>A claim's payment (CLP and its loop): what was billed, paid and adjusted, and its service lines.</summary>
public sealed class ClaimPayment
{
    public required Payment Payment { get; init; }

    /// <summary>1 for the first claim in the payment.</summary>
    public int Number { get; init; }
    public string Lx { get; init; } = "";

    /// <summary>CLP01: the patient control number, as the provider billed it (the 837's CLM01).</summary>
    public required string Account { get; init; }
    public required string Status { get; init; }
    public decimal Charge { get; init; }
    public decimal Paid { get; init; }
    public decimal PatientResponsibility { get; init; }
    public string Filing { get; init; } = "";

    /// <summary>CLP07: the payer's own number for the claim.</summary>
    public string PayerClaim { get; init; } = "";
    public string Facility { get; init; } = "";
    public string Frequency { get; init; } = "";
    public string Drg { get; init; } = "";

    public Person? Patient { get; set; }
    public Person? Insured { get; set; }
    public Person? Corrected { get; set; }
    public Named? Rendering { get; set; }
    public Named? Crossover { get; set; }
    public Named? PriorityPayer { get; set; }
    public List<Adjustment> Adjustments { get; } = [];
    public List<Remark> Remarks { get; } = [];
    public List<Dated> Dates { get; } = [];
    public List<Reference> References { get; } = [];
    public List<Amount> Amounts { get; } = [];
    public List<ServicePayment> Lines { get; } = [];

    /// <summary>The claim's own segments, CLP up to its first SVC.</summary>
    public List<Segment> Segments { get; } = [];

    /// <summary>The claim's segments and its lines'.</summary>
    public IEnumerable<Segment> AllSegments => Segments.Concat(Lines.SelectMany(l => l.Segments));

    /// <summary>Every adjustment, the claim's own and its lines'.</summary>
    public IEnumerable<Adjustment> AllAdjustments => Adjustments.Concat(Lines.SelectMany(l => l.Adjustments));
    public IEnumerable<Remark> AllRemarks => Remarks.Concat(Lines.SelectMany(l => l.Remarks));

    public bool IsReversal => Status == "22";
    public DateOnly? From => Dates.FirstOrDefault(d => d.Qualifier == "232")?.Date ?? Lines.Select(l => l.From).Where(d => d != null).Min();
    public DateOnly? To => Dates.FirstOrDefault(d => d.Qualifier == "233")?.Date ?? Lines.Select(l => l.To).Where(d => d != null).Max();
    public string Original => References.FirstOrDefault(r => r.Qualifier == "F8")?.Value ?? "";

    /// <summary>What happened to the claim, in a word.</summary>
    public ClaimState State =>
        IsReversal ? ClaimState.Reversal
        : Status == "4" ? ClaimState.Denied
        : Paid == 0 && Charge != 0 && PatientResponsibility > 0 && AllAdjustments.All(a => a.Group == "PR" || IsContractual(a)) ? ClaimState.PatientOwes
        : Paid == 0 && Charge != 0 ? ClaimState.Denied
        : Lines.Any(l => l.Denied) ? ClaimState.PartlyDenied
        : ClaimState.Paid;

    /// <summary>The usual write-down to the contracted rate (CO-45 and its kin), which isn't a denial.</summary>
    public static bool IsContractual(Adjustment a) => a.Group == "CO" && a.Reason is "45" or "253" or "94" or "59" or "131" or "144" or "237";

    public string Who => Patient?.Name ?? Insured?.Name ?? "";
}

public enum ClaimState { Paid, PartlyDenied, Denied, PatientOwes, Reversal }

/// <summary>A service line's payment (SVC and its loop).</summary>
public sealed class ServicePayment
{
    public required ClaimPayment Claim { get; init; }

    /// <summary>1 for the claim's first line.</summary>
    public int Number { get; init; }
    public string Qualifier { get; init; } = "";
    public required string Procedure { get; init; }
    public IReadOnlyList<string> Modifiers { get; init; } = [];
    public string Description { get; init; } = "";
    public decimal Charge { get; init; }
    public decimal Paid { get; init; }
    public string Revenue { get; init; } = "";
    public decimal? Units { get; init; }

    /// <summary>SVC06: what was billed, when the payer paid it as a different code.</summary>
    public string OriginalProcedure { get; init; } = "";
    public IReadOnlyList<string> OriginalModifiers { get; init; } = [];
    public decimal? OriginalUnits { get; init; }
    public List<Adjustment> Adjustments { get; } = [];
    public List<Remark> Remarks { get; } = [];
    public List<Dated> Dates { get; } = [];
    public List<Reference> References { get; } = [];
    public List<Amount> Amounts { get; } = [];
    public List<Segment> Segments { get; } = [];

    public decimal? Allowed => Amounts.FirstOrDefault(a => a.Qualifier == "B6")?.Value;
    public string ControlNumber => References.FirstOrDefault(r => r.Qualifier == "6R")?.Value ?? "";
    public DateOnly? From => Dates.FirstOrDefault(d => d.Qualifier is "472" or "150")?.Date;
    public DateOnly? To => Dates.FirstOrDefault(d => d.Qualifier == "151")?.Date ?? From;

    /// <summary>The code as billed, with its modifiers: 99214-25.</summary>
    public string Code => string.Join('-', new[] { Procedure }.Concat(Modifiers));

    /// <summary>Paid nothing, with an adjustment that isn't the patient's or the contract's write-down.</summary>
    public bool Denied => Paid == 0 && Charge > 0 && Adjustments.Any(a => a.Group != "PR" && !ClaimPayment.IsContractual(a));
}

/// <summary>Reading and saying amounts and dates the way the 835 writes them and a billing office reads them.</summary>
public static class Money
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static decimal? Read(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>$1,234.56, or −$12.00.</summary>
    public static string Say(decimal amount) => (amount < 0 ? "−" : "") + Math.Abs(amount).ToString("C2", Us);

    public static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Day(DateOnly? d) => d is { } x ? x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "no date";
}
