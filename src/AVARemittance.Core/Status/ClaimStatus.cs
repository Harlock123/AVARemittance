using System.Globalization;
using System.Text;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.X12;

namespace AVARemittance.Core.Status;

/// <summary>Who's asking about claim status: the envelope's IDs and its next control number.</summary>
public sealed record StatusSettings
{
    /// <summary>ISA06 and the submitter's ID (1000A NM109 of the 837), when the billed claims don't say.</summary>
    public string SenderId { get; init; } = "";

    /// <summary>ISA08: the clearinghouse's ID.</summary>
    public string ReceiverId { get; init; } = "CLEARINGHOUSE";
    public int ControlNumber { get; init; } = 1;

    /// <summary>Ask about claims with no answer this many days after they were billed.</summary>
    public int AfterDays { get; init; } = 21;
}

/// <summary>A 276 written: its text, the trace each claim was asked under, and how many payers.</summary>
public sealed record Built276(string Text, IReadOnlyDictionary<BilledClaim, string> Traces, int Transactions);

/// <summary>
/// Writes the 276 claim status request (005010X212): one transaction per payer; under the payer (HL 20) the clinic as
/// information receiver (21) and provider (19), then one subscriber (22) per claim with its DMG and NM1, and the claim's
/// trace (TRN*1), account (REF*EJ), charge (AMT*T3) and service dates (DTP*472).
/// </summary>
public static class StatusRequest
{
    /// <summary>The trace a claim is asked under: its account and the day it's asked.</summary>
    public static string Trace(BilledClaim c, DateTime now) => $"{c.Account}-{now:yyMMdd}";

    public static Built276 Build(IReadOnlyList<BilledClaim> claims, StatusSettings settings, DateTime now)
    {
        if (claims.Count == 0) throw new InvalidOperationException("No claims to ask about.");
        var sender = claims.Select(c => c.SubmitterId).FirstOrDefault(x => x.Length > 0) ?? settings.SenderId;
        if (sender.Length == 0) sender = settings.SenderId;
        if (sender.Length == 0) throw new InvalidOperationException("The submitter's ID isn't known: open the 837s, or set it under Claim status.");
        var d = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var t = now.ToString("HHmm", CultureInfo.InvariantCulture);
        var control = settings.ControlNumber;
        var lines = new List<string>
        {
            $"ISA*00*{"",10}*00*{"",10}*ZZ*{sender,-15}*ZZ*{settings.ReceiverId,-15}*{d[2..]}*{t}*^*00501*{control:D9}*0*P*:",
            $"GS*HR*{sender}*{settings.ReceiverId}*{d}*{t}*{control}*X*005010X212",
        };
        var traces = new Dictionary<BilledClaim, string>();
        var n = 0;
        foreach (var payer in claims.GroupBy(c => (c.PayerId, c.Payer)))
        {
            n++;
            var body = new List<string>();
            void Add(params string[] e)
            {
                var list = e.ToList();
                while (list.Count > 1 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
                body.Add(string.Join('*', list));
            }
            var first = payer.First();
            Add("BHT", "0010", "13", $"{sender}{control}{n}", d, t);
            Add("HL", "1", "", "20", "1");
            Add("NM1", "PR", "2", payer.Key.Payer, "", "", "", "", "PI", payer.Key.PayerId);
            Add("HL", "2", "1", "21", "1");
            Add("NM1", "41", "2", first.SubmitterName.Length > 0 ? first.SubmitterName : first.ProviderName, "", "", "", "", "46", sender);
            Add("HL", "3", "2", "19", "1");
            Add("NM1", "1P", "2", first.ProviderName, "", "", "", "", "XX", first.ProviderNpi);
            var hl = 3;
            foreach (var c in payer)
            {
                hl++;
                var trace = Trace(c, now);
                traces[c] = trace;
                Add("HL", hl.ToString(CultureInfo.InvariantCulture), "3", "22", "0");
                if (c.BirthDate is { } born) Add("DMG", "D8", born.ToString("yyyyMMdd", CultureInfo.InvariantCulture), c.Sex.Length > 0 ? c.Sex : "U");
                Add("NM1", "IL", "1", c.Last, c.First, "", "", "", "MI", c.MemberId);
                Add("TRN", "1", trace);
                Add("REF", "EJ", c.Account);
                Add("AMT", "T3", Amount(c.Charge));
                if (c.From is { } from) Add("DTP", "472", "RD8", $"{from:yyyyMMdd}-{c.To ?? from:yyyyMMdd}");
            }
            var st = $"{n:D4}";
            lines.Add($"ST*276*{st}*005010X212");
            lines.AddRange(body);
            lines.Add($"SE*{body.Count + 2}*{st}");
        }
        lines.Add($"GE*{n}*{control}");
        lines.Add($"IEA*1*{control:D9}");
        return new Built276(string.Join("~\n", lines) + "~\n", traces, n);
    }

    private static string Amount(decimal x)
    {
        var s = x.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return s.Length == 0 ? "0" : s;
    }
}

/// <summary>One status the payer gave (an STC composite): category, status, entity, and the amounts with it.</summary>
public sealed record StatusCode(string Category, string Code, string Entity, DateOnly? Date, decimal? Charge, decimal? Paid, Segment Segment)
{
    public string Text => $"{Category} {Code}: {CodeList.StatusCategory.Say(Category)} {CodeList.Status.Say(Code)}";
}

/// <summary>What a 277 says about one claim: whose, which, and its statuses.</summary>
public sealed class StatusAnswer
{
    public required string File { get; init; }
    public string Payer { get; init; } = "";
    public string PayerId { get; init; } = "";
    public string Trace { get; set; } = "";
    public string Account { get; set; } = "";
    public string PayerClaim { get; set; } = "";
    public string Last { get; init; } = "";
    public string First { get; init; } = "";
    public string MemberId { get; init; } = "";
    public DateOnly? Made { get; init; }
    public List<StatusCode> Statuses { get; } = [];
    public List<Segment> Segments { get; } = [];

    /// <summary>What it means for the claim, in a sentence with what to do.</summary>
    public string Meaning => Statuses.FirstOrDefault()?.Category is not { Length: > 0 } c ? "The 277 gives no status."
        : c.StartsWith('F') && c == "F1" ? "Paid: the 835 should follow."
        : c is "F2" ? "Denied: the 835 will say why."
        : c.StartsWith('F') ? "Finalized: the 835 will say how."
        : c is "A4" ? "The payer has no record of it: send it again now, before the time to file runs out."
        : c is "A3" or "A6" or "A7" or "A8" ? "Rejected before it was processed: fix what the status says and send it again."
        : c.StartsWith('P') ? "In process: wait, and ask again in a couple of weeks."
        : c.StartsWith('R') ? "The payer wants more: send what it asks for."
        : c is "A1" or "A2" or "A0" ? "Received: wait for the 835."
        : "Read the status and decide.";
}

/// <summary>Reads 277 claim status responses (005010X212): each 2200D (or 2200E) loop's TRN*2, STC, REF and DTP.</summary>
public static class StatusReader
{
    public static bool Is277(X12File x12) =>
        x12.Transactions.Any(t => t.Code == "277" && (t.Start[3].Contains("X212") || t.Group?[8].Contains("X212") == true));

    public static List<StatusAnswer> Read(X12File x12, string name)
    {
        var list = new List<StatusAnswer>();
        var sep = x12.Delimiters.Component;
        foreach (var t in x12.Transactions.Where(t => t.Code == "277"))
        {
            var made = t.Body.FirstOrDefault(s => s.Id == "BHT") is { } bht ? Money.Date(bht[4]) : null;
            string payer = "", payerId = "", last = "", first = "", member = "", level = "";
            StatusAnswer? a = null;
            foreach (var s in t.Body)
            {
                switch (s.Id)
                {
                    case "HL":
                        level = s[3].Trim();
                        a = null;
                        break;
                    case "NM1" when level == "20" && s[1].Trim() == "PR":
                        payer = s[3].Trim(); payerId = s[9].Trim();
                        break;
                    case "NM1" when s[1].Trim() is "IL" or "QC":
                        last = s[3].Trim(); first = s[4].Trim();
                        if (s[1].Trim() == "IL") member = s[9].Trim();
                        break;
                    case "TRN" when s[1].Trim() == "2":
                        a = new StatusAnswer { File = name, Payer = payer, PayerId = payerId, Last = last, First = first, MemberId = member, Made = made, Trace = s[2].Trim() };
                        a.Segments.Add(s);
                        list.Add(a);
                        break;
                    case "STC" when a != null:
                        a.Segments.Add(s);
                        foreach (var i in new[] { 1, 10, 11 })
                        {
                            var parts = s[i].Split(sep);
                            if (parts[0].Trim().Length == 0) continue;
                            a.Statuses.Add(new StatusCode(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "", parts.Length > 2 ? parts[2].Trim() : "",
                                i == 1 ? Money.Date(s[2]) : null, i == 1 ? Money.Read(s[4]) : null, i == 1 ? Money.Read(s[5]) : null, s));
                        }
                        break;
                    case "REF" when a != null:
                        a.Segments.Add(s);
                        if (s[1].Trim() == "1K") a.PayerClaim = s[2].Trim();
                        if (s[1].Trim() == "EJ") a.Account = s[2].Trim();
                        break;
                    case "DTP" or "SVC" when a != null:
                        a.Segments.Add(s);
                        break;
                }
            }
        }
        return list;
    }

    /// <summary>The newest 277 answer about a billed claim: by its account, or the trace it was asked under.</summary>
    public static StatusAnswer? For(BilledClaim c, IEnumerable<StatusAnswer> answers) =>
        answers.Where(a => a.Account.Equals(c.Account, StringComparison.OrdinalIgnoreCase) || a.Trace.StartsWith(c.Account + "-", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.PayerId.Length == 0 || c.PayerId.Length == 0 || a.PayerId.Equals(c.PayerId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Made).FirstOrDefault();

    public static string Describe(StatusAnswer a) =>
        ($"277 of {Money.Day(a.Made)}: " + string.Join("; ", a.Statuses.Select(s => s.Text.TrimEnd('.'))) + (a.PayerClaim.Length > 0 ? $" (payer claim {a.PayerClaim})" : "")).TrimEnd('.') + $". {a.Meaning}";
}
