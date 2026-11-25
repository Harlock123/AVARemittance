using System.Text.Json;
using System.Text.Json.Serialization;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Core.Denials;

/// <summary>What a denial asks for, by what its reason and remark codes point to.</summary>
public enum WorkAction { FixAndResubmit, Appeal, BillElsewhere, BillPatient, WriteOff, Bundled, Review }

/// <summary>Where a work item stands: kept in a file beside the 835s.</summary>
public enum WorkState { Open, Appealed, Resubmitted, BilledElsewhere, BilledPatient, WrittenOff }

/// <summary>
/// One denial to work: a claim's denied lines (or the whole claim) that point to the same action, the adjustments and
/// remarks behind it, what's at stake, what to do, and the days left to do it.
/// </summary>
public sealed class WorkItem
{
    /// <summary>W01, W02… in the worklist's order.</summary>
    public string Id { get; set; } = "";

    /// <summary>What the state file keys it by: the payer's claim number, the lines and the action.</summary>
    public required string Key { get; init; }
    public required ClaimPayment Claim { get; init; }
    public required WorkAction Action { get; init; }
    public IReadOnlyList<ServicePayment> Lines { get; init; } = [];
    public IReadOnlyList<Adjustment> Reasons { get; init; } = [];
    public IReadOnlyList<Remark> Remarks { get; init; } = [];
    public decimal Amount { get; init; }
    public required string WhatToDo { get; init; }

    /// <summary>The last day to act, from the payer's limits (none when no limits are loaded for the payer).</summary>
    public DateOnly? Deadline { get; init; }
    public string DeadlineBecause { get; init; } = "";
    public WorkState State { get; set; }
    public DateOnly? StateDate { get; set; }
    public string Note { get; set; } = "";

    public int? DaysLeft(DateOnly today) => Deadline is { } d ? d.DayNumber - today.DayNumber : null;
    public bool Done => State != WorkState.Open;
}

/// <summary>
/// A payer's limits for working denials: days to appeal, to send a corrected claim, and to file at all, counted from
/// the 835's payment date. From a JSON file the person keeps (the samples' are invented and say so).
/// </summary>
public sealed record PayerLimits
{
    [JsonPropertyName("payer")] public string Payer { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("appeal_days")] public int? AppealDays { get; init; }
    [JsonPropertyName("corrected_claim_days")] public int? CorrectedClaimDays { get; init; }
    [JsonPropertyName("timely_filing_days")] public int? TimelyFilingDays { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";

    public static bool LooksLikeOne(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) && Safe(() => File.ReadAllText(path).Contains("\"appeal_days\""));

    public static List<PayerLimits> Load(string path) =>
        JsonSerializer.Deserialize<List<PayerLimits>>(File.ReadAllText(path)) ?? [];

    public bool Matches(Payment p) =>
        Payer.Length > 0 && (p.Payer.References.Any(r => r.Qualifier == "2U" && r.Value.Equals(Payer, StringComparison.OrdinalIgnoreCase)) || p.Payer.Id.Equals(Payer, StringComparison.OrdinalIgnoreCase))
        || Name.Length > 0 && p.Payer.Name.Equals(Name, StringComparison.OrdinalIgnoreCase);

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Sorts each denial into what to do, by its codes: CARCs say why, the group says whose it is, and remarks can change
/// the answer (MA130: no appeal rights, send a new claim). Contractual write-downs (CO-45 and its kin) and routine
/// patient shares aren't denials and make no work.
/// </summary>
public static class Classifier
{
    private static readonly HashSet<string> Fix = ["4", "5", "6", "7", "8", "9", "10", "11", "16", "31", "125", "140", "146", "181", "182", "197", "198", "199", "206", "207", "208", "226", "252", "A1"];
    private static readonly HashSet<string> Necessity = ["50", "55", "56", "57", "58", "150", "151", "152", "167", "B7", "185", "170"];
    private static readonly HashSet<string> Elsewhere = ["22", "109", "26", "27", "177", "200", "B11"];
    private static readonly HashSet<string> Bundling = ["97", "234", "B15", "236"];
    private static readonly HashSet<string> Duplicate = ["18", "B13"];
    private static readonly HashSet<string> Late = ["29"];
    private static readonly HashSet<string> NotCovered = ["96", "204", "49", "119", "149", "35", "B1"];

    /// <summary>The action an adjustment points to, or null when it makes no work (a write-down to contract, a routine patient share).</summary>
    public static WorkAction? For(Adjustment a, IReadOnlyCollection<string> remarks)
    {
        if (ClaimPayment.IsContractual(a) || a.Amount <= 0) return null;
        if (a.Group == "PR") return NotCovered.Contains(a.Reason) ? WorkAction.BillPatient : null;
        if (remarks.Contains("MA130")) return WorkAction.FixAndResubmit;
        if (Bundling.Contains(a.Reason)) return WorkAction.Bundled;
        if (Duplicate.Contains(a.Reason)) return WorkAction.Review;
        if (Late.Contains(a.Reason)) return WorkAction.WriteOff;
        if (Elsewhere.Contains(a.Reason)) return WorkAction.BillElsewhere;
        if (Fix.Contains(a.Reason)) return WorkAction.FixAndResubmit;
        if (Necessity.Contains(a.Reason)) return remarks.Contains("N211") ? WorkAction.WriteOff : WorkAction.Appeal;
        if (NotCovered.Contains(a.Reason)) return WorkAction.WriteOff;
        return WorkAction.Review;
    }

    public static string Say(WorkAction a) => a switch
    {
        WorkAction.FixAndResubmit => "fix and resubmit",
        WorkAction.Appeal => "appeal",
        WorkAction.BillElsewhere => "bill elsewhere",
        WorkAction.BillPatient => "bill the patient",
        WorkAction.WriteOff => "write off",
        WorkAction.Bundled => "bundled",
        WorkAction.Review => "review",
        _ => a.ToString(),
    };

    public static string Say(WorkState s) => s switch
    {
        WorkState.Open => "open",
        WorkState.Appealed => "appealed",
        WorkState.Resubmitted => "resubmitted",
        WorkState.BilledElsewhere => "billed elsewhere",
        WorkState.BilledPatient => "billed to the patient",
        WorkState.WrittenOff => "written off",
        _ => s.ToString(),
    };

    /// <summary>What to do, in words, from the action and the codes behind it.</summary>
    public static string WhatToDo(WorkAction action, IReadOnlyList<Adjustment> reasons, IReadOnlyList<string> remarks, ClaimPayment c)
    {
        var codes = reasons.Select(r => r.Reason).ToHashSet();
        var missing = remarks.Where(r => CodeList.Rarc[r]?.Text is { } t && (t.StartsWith("Missing", StringComparison.OrdinalIgnoreCase) || t.Contains("incomplete/invalid", StringComparison.OrdinalIgnoreCase))).Select(r => CodeList.Rarc.Say(r).TrimEnd('.').ToLowerInvariant()).ToList();
        var replace = c.PayerClaim.Length > 0 ? $" Send it as a replacement claim (frequency 7), with the payer's claim number {c.PayerClaim} in REF*F8." : " Send it as a replacement claim (frequency 7).";
        return action switch
        {
            WorkAction.FixAndResubmit when codes.Contains("197") =>
                "Add the authorization number (REF*G1) and send a corrected claim." + replace + " If the authorization was on the claim already, appeal instead, with the authorization's number and date.",
            WorkAction.FixAndResubmit when codes.Contains("31") =>
                "Check the member ID, name and birth date against the patient's card (or a fresh 271), correct them, and send the claim again." + replace,
            WorkAction.FixAndResubmit when remarks.Contains("MA130") =>
                "The payer couldn't process it and gives no appeal rights: correct what the remarks say and send a new claim." + (missing.Count > 0 ? $" It says: {string.Join("; ", missing)}." : ""),
            WorkAction.FixAndResubmit =>
                "Correct the claim" + (missing.Count > 0 ? $" ({string.Join("; ", missing)})" : remarks.Contains("N822") ? " (add the missing modifier)" : "") + " and send it again." + replace,
            WorkAction.Appeal =>
                "Appeal with the records that show why the care was needed: the notes, the plan of care, and the payer's own coverage policy where it supports it. Write by the deadline.",
            WorkAction.BillElsewhere when codes.Contains("22") =>
                "Another payer comes first: bill the primary payer, then send this payer the claim as secondary with the primary's 835 (its payment and adjustments).",
            WorkAction.BillElsewhere when codes.Contains("26") || codes.Contains("27") =>
                "The payer says coverage didn't cover the dates: check eligibility for the dates of service, then bill the plan that did, or the patient.",
            WorkAction.BillElsewhere =>
                "This payer isn't the one: find the patient's right payer (a 271, the card) and bill it.",
            WorkAction.BillPatient =>
                "The plan leaves it to the patient: bill the patient, with the payer's reason. For a non-covered service, only with the patient's signed waiver where one is needed.",
            WorkAction.WriteOff when codes.Contains("29") =>
                "Filed too late: write it off. Appeal only with proof it was filed on time, such as a 999 or 277CA acceptance dated within the payer's limit.",
            WorkAction.WriteOff =>
                "Not covered, and not billable to the patient under the contract: write it off, unless the patient signed a waiver before the service.",
            WorkAction.Bundled =>
                "Paid within another line, as the payer bundles them. Nothing to do, unless a modifier (25, 59, XU and the like) should have made it separate: then correct and resubmit.",
            _ => "Read the payer's reason and remarks, and decide: " + string.Join("; ", reasons.Select(r => $"{r.Group}-{r.Reason} {CodeList.Carc.Say(r.Reason)}")),
        };
    }
}

/// <summary>Builds the worklist from the open 835s: every denial, grouped by claim and action, with its deadline.</summary>
public static class Worklist
{
    public static List<WorkItem> Build(IEnumerable<ClaimPayment> claims, IReadOnlyList<PayerLimits> limits, IReadOnlyDictionary<string, SavedState>? saved = null)
    {
        var all = claims.ToList();
        var reversedLater = all.Where(c => c.IsReversal).Select(c => c.PayerClaim).ToHashSet();
        var items = new List<WorkItem>();
        foreach (var c in all.Where(c => !c.IsReversal && !(reversedLater.Contains(c.PayerClaim) && all.Any(r => r.IsReversal && r.PayerClaim == c.PayerClaim && r.Payment != c.Payment))))
        {
            var found = new List<(WorkAction Action, ServicePayment? Line, Adjustment Reason)>();
            var claimRemarks = c.Remarks.Select(r => r.Code).ToList();
            foreach (var a in c.Adjustments)
                if (Classifier.For(a, claimRemarks) is { } act) found.Add((act, null, a));
            foreach (var l in c.Lines)
            {
                var remarks = l.Remarks.Select(r => r.Code).Concat(claimRemarks).ToList();
                foreach (var a in l.Adjustments)
                    if (Classifier.For(a, remarks) is { } act) found.Add((act, l, a));
            }
            if (c.State == ClaimState.PatientOwes && found.All(f => f.Action != WorkAction.BillPatient))
                found.AddRange(c.AllAdjustments.Where(a => a.Group == "PR").Select(a => (WorkAction.BillPatient, c.Lines.FirstOrDefault(l => l.Adjustments.Contains(a)), a)));
            foreach (var g in found.GroupBy(f => f.Action).OrderBy(g => g.Key))
            {
                var lines = g.Select(f => f.Line).OfType<ServicePayment>().Distinct().ToList();
                var remarks = lines.SelectMany(l => l.Remarks).Concat(c.Remarks).DistinctBy(r => r.Code).ToList();
                var reasons = g.Select(f => f.Reason).ToList();
                var key = $"{c.Payment.Payer.Name}|{(c.PayerClaim.Length > 0 ? c.PayerClaim : c.Account)}|{string.Join(",", lines.Select(l => l.Number))}|{g.Key}";
                var (deadline, because) = Deadline(g.Key, c, limits);
                var item = new WorkItem
                {
                    Key = key, Claim = c, Action = g.Key, Lines = lines, Reasons = reasons, Remarks = remarks, Amount = reasons.Sum(r => r.Amount),
                    WhatToDo = Classifier.WhatToDo(g.Key, reasons, [.. remarks.Select(r => r.Code)], c), Deadline = deadline, DeadlineBecause = because,
                };
                if (saved != null && saved.TryGetValue(key, out var s))
                {
                    item.State = s.State;
                    item.StateDate = s.Date;
                    item.Note = s.Note;
                }
                items.Add(item);
            }
        }
        var ordered = items.OrderBy(i => i.Done).ThenBy(i => i.Action is WorkAction.Bundled or WorkAction.WriteOff).ThenBy(i => i.Deadline ?? DateOnly.MaxValue)
            .ThenBy(i => i.Claim.Account, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Id = $"W{i + 1:00}";
        return ordered;
    }

    private static (DateOnly?, string) Deadline(WorkAction action, ClaimPayment c, IReadOnlyList<PayerLimits> limits)
    {
        var from = c.Payment.PaidOn ?? c.Payment.Produced;
        var l = limits.FirstOrDefault(x => x.Matches(c.Payment));
        if (from is not { } day || l == null) return (null, l == null ? "no limits loaded for this payer" : "the 835 has no date");
        var (days, what) = action switch
        {
            WorkAction.Appeal => (l.AppealDays, "to appeal"),
            WorkAction.FixAndResubmit => (l.CorrectedClaimDays ?? l.AppealDays, "to send a corrected claim"),
            WorkAction.WriteOff when c.AllAdjustments.Any(a => a.Reason == "29") => (l.AppealDays, "to appeal with proof of timely filing"),
            WorkAction.BillElsewhere => (l.TimelyFilingDays, "for the claim to this payer as secondary, from the primary's answer at the latest"),
            _ => ((int?)null, ""),
        };
        return days is { } n ? (day.AddDays(n), $"{n} days {what}, from the 835 of {Money.Day(day)} ({(l.Source.Length > 0 ? l.Source : "the payer's limits")})") : (null, "");
    }

    public static string Describe(WorkItem w, DateOnly today) =>
        $"{w.Id} {w.Claim.Account} {Classifier.Say(w.Action)} {Money.Say(w.Amount)}"
        + (w.Lines.Count > 0 ? $" (line{(w.Lines.Count == 1 ? "" : "s")} {string.Join(", ", w.Lines.Select(l => $"{l.Number} {l.Code}"))})" : " (the claim)")
        + $": {string.Join("; ", w.Reasons.Select(r => $"{r.Group}-{r.Reason}").Distinct())}"
        + (w.Remarks.Count > 0 ? $", {string.Join(", ", w.Remarks.Select(r => r.Code))}" : "")
        + (w.Done ? $". {char.ToUpperInvariant(Classifier.Say(w.State)[0])}{Classifier.Say(w.State)[1..]}" + (w.StateDate is { } d ? $" {Money.Day(d)}" : "") + "."
            : w.DaysLeft(today) is { } left ? left < 0 ? $". {-left} days past the deadline ({Money.Day(w.Deadline)})." : $". {left} days left (by {Money.Day(w.Deadline)})." : ".");
}

/// <summary>A work item's state as saved: what was done, when, and a note.</summary>
public sealed record SavedState(WorkState State, DateOnly? Date, string Note);

/// <summary>
/// The worklist's state, in remittance-work.json beside the 835s: the person's own record of what they did. Keyed by
/// <see cref="WorkItem.Key"/>, so it survives the 835s being opened again or in another order.
/// </summary>
public static class WorkStore
{
    public const string FileName = "remittance-work.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Dictionary<string, SavedState> Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, SavedState>>(File.ReadAllText(path), Options) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string folder, WorkItem item)
    {
        var all = Load(folder);
        if (item.State == WorkState.Open && item.Note.Length == 0) all.Remove(item.Key);
        else all[item.Key] = new SavedState(item.State, item.StateDate, item.Note);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(all, Options));
    }
}
