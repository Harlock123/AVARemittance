using System.Globalization;
using System.Text;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Denials;

/// <summary>
/// One denied claim to ask about again, as a row of AVA Eligibility's appointment list: the patient, the payer and
/// member ID, the date of service as the visit date, and the denial that sent it back.
/// </summary>
public sealed record RecheckRow
{
    public DateOnly? Date { get; init; }
    public string Last { get; init; } = "";
    public string First { get; init; } = "";
    public DateOnly? BirthDate { get; init; }
    public string Sex { get; init; } = "";
    public string PayerId { get; init; } = "";
    public string Payer { get; init; } = "";
    public string MemberId { get; init; } = "";

    /// <summary>"self", or the subscriber as "LAST, FIRST" when the 835 names an insured who isn't the patient.</summary>
    public string Subscriber { get; init; } = "self";
    public string Provider { get; init; } = "";
    public string ProviderNpi { get; init; } = "";
    public string Procedure { get; init; } = "";
    public decimal Charge { get; init; }
    public string Account { get; init; } = "";

    /// <summary>The denial in codes and words: "CO-27: Expenses incurred after coverage terminated. N30: …".</summary>
    public string Denial { get; init; } = "";
    public required WorkItem Item { get; init; }
}

/// <summary>
/// "Check eligibility again" for denials a fresh 271 answers: the payer said the patient wasn't covered on the dates
/// (CARC 26, 27), couldn't find them (31), or that another payer comes first (22). It writes those claims as an
/// appointment list AVA Eligibility reads (its columns, by their header names, plus Account and Denial), with the date
/// of service as the visit date, so AVA Eligibility writes the 270 for that date and says what the answer means for
/// the denial. The patient's birth date and sex come from the billed claim (837 or charges list) when one is open.
/// </summary>
public static class EligibilityRecheck
{
    /// <summary>The CARCs a 271 for the date of service answers: the same ones AVA Claim Scrubber's V rules check.</summary>
    public static readonly IReadOnlyList<string> Codes = ["22", "26", "27", "31"];

    public const string Header = "Date,Last name,First name,Birth date,Sex,Payer ID,Payer,Member ID,Subscriber,Provider,Provider NPI,Visit,Procedure,Charge,Account,Denial";

    /// <summary>Whether a file is a list this wrote: for AVA Eligibility, not a charges list (it has Account, Procedure and Charge too).</summary>
    public static bool LooksLikeOne(string path)
    {
        try
        {
            return Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) && File.ReadLines(path).FirstOrDefault() == Header;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether a fresh 271 would answer the denial: one of <see cref="Codes"/>, not the patient's share.</summary>
    public static bool Applies(WorkItem w) => w.Reasons.Any(r => r.Group != "PR" && Codes.Contains(r.Reason));

    /// <summary>The open denials a 271 answers, one row per claim, in the worklist's order.</summary>
    public static List<RecheckRow> Rows(IEnumerable<WorkItem> items, Reconciliation? matched = null) =>
        [.. items.Where(w => !w.Done && Applies(w)).DistinctBy(w => w.Claim).Select(w => Row(w, matched))];

    public static RecheckRow Row(WorkItem w, Reconciliation? matched = null)
    {
        var c = w.Claim;
        var billed = matched?.Claims.FirstOrDefault(m => m.Answers.Contains(c))?.Billed;
        var patient = c.Patient;
        var insured = c.Insured;
        var dependent = insured != null && patient != null && !(insured.Last == patient.Last && insured.First == patient.First);
        var lines = w.Lines.Count > 0 ? w.Lines : c.Lines;
        var reasons = w.Reasons.Where(r => Codes.Contains(r.Reason)).Concat(w.Reasons).DistinctBy(r => (r.Group, r.Reason));
        var denial = string.Join(" ", reasons.Select(r => $"{r.Group}-{r.Reason}: {CodeList.Carc.Say(r.Reason)}").Concat(w.Remarks.Select(r => $"{r.Code}: {CodeList.Rarc.Say(r.Code)}")));
        var payerRef = c.Payment.Payer.References.FirstOrDefault(r => r.Qualifier == "2U")?.Value;
        return new RecheckRow
        {
            Item = w,
            Date = lines.Select(l => l.From).Where(d => d != null).Min() ?? c.From ?? billed?.From,
            Last = patient?.Last ?? billed?.Last ?? "",
            First = patient?.First ?? billed?.First ?? "",
            BirthDate = billed?.BirthDate,
            Sex = billed?.Sex ?? "",
            PayerId = billed is { PayerId.Length: > 0 } ? billed.PayerId : payerRef ?? c.Payment.Payer.Id,
            Payer = c.Payment.Payer.Name,
            MemberId = (dependent ? insured!.Id : patient?.Id is { Length: > 0 } id ? id : insured?.Id) ?? billed?.MemberId ?? "",
            Subscriber = dependent ? insured!.SortName : "self",
            Provider = billed is { ProviderName.Length: > 0 } ? billed.ProviderName : c.Rendering?.Name ?? "",
            ProviderNpi = billed is { ProviderNpi.Length: > 0 } ? billed.ProviderNpi : c.Rendering?.Id ?? "",
            Procedure = lines.FirstOrDefault()?.Procedure ?? "",
            Charge = lines.Count > 0 ? lines.Sum(l => l.Charge) : c.Charge,
            Account = c.Account,
            Denial = denial,
        };
    }

    /// <summary>The rows as AVA Eligibility's appointment list: a CSV with its header names.</summary>
    public static string Csv(IEnumerable<RecheckRow> rows)
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var r in rows)
            text.Append(string.Join(",", new[]
            {
                Day(r.Date), r.Last, r.First, Day(r.BirthDate), r.Sex, r.PayerId, r.Payer, r.MemberId, r.Subscriber, r.Provider, r.ProviderNpi,
                "Denied claim", r.Procedure, r.Charge.ToString("0.00", CultureInfo.InvariantCulture), r.Account, r.Denial,
            }.Select(Cell))).Append('\n');
        return text.ToString();
    }

    private static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    private static string Cell(string v) => v.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    /// <summary>The program to open the list with: the path the person set, or AVAEligibility on the PATH; null when neither.</summary>
    public static string? FindProgram(string? setting)
    {
        if (setting is { Length: > 0 } && File.Exists(setting)) return setting;
        var names = OperatingSystem.IsWindows() ? new[] { "AVAEligibility.exe" } : ["AVAEligibility"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
