using AVARemittance.Core.Remittance;
using AVARemittance.Core.X12;

namespace AVARemittance.Core.Billing;

/// <summary>A billed service line: what was charged for, on which day.</summary>
public sealed record BilledLine
{
    public int Number { get; init; }
    public required string Procedure { get; init; }
    public IReadOnlyList<string> Modifiers { get; init; } = [];
    public decimal Units { get; init; } = 1;
    public decimal Charge { get; init; }
    public DateOnly? Date { get; init; }

    /// <summary>REF 6R, the line item control number, which the 835 sends back.</summary>
    public string ControlNumber { get; init; } = "";
    public string Code => string.Join('-', new[] { Procedure }.Concat(Modifiers));
}

/// <summary>
/// A claim as the clinic billed it: from an 837 or a charges list. Only what matching needs: the account (CLM01),
/// payer, patient, when it was sent, and its lines.
/// </summary>
public sealed class BilledClaim
{
    /// <summary>The file it came from.</summary>
    public required string Source { get; init; }
    public required string Account { get; init; }
    public string PayerId { get; init; } = "";
    public string Payer { get; init; } = "";
    public string Last { get; init; } = "";
    public string First { get; init; } = "";
    public string MemberId { get; init; } = "";
    public DateOnly? BirthDate { get; init; }

    /// <summary>M, F or U, as the 837's DMG03.</summary>
    public string Sex { get; init; } = "";

    /// <summary>The billing provider (2010AA) and the submitter (1000A), which a 276 names too.</summary>
    public string ProviderName { get; init; } = "";
    public string ProviderNpi { get; init; } = "";
    public string SubmitterName { get; init; } = "";
    public string SubmitterId { get; init; } = "";

    /// <summary>When the claim was sent: the 837's BHT04, or the list's billed date.</summary>
    public DateOnly? Billed { get; init; }
    public string Frequency { get; init; } = "1";

    /// <summary>For a replacement (7) or void (8): the payer's claim number being replaced (REF F8).</summary>
    public string Original { get; set; } = "";
    public List<BilledLine> Lines { get; } = [];

    /// <summary>The 837's CLM segment (null from a charges list).</summary>
    public Segment? Clm { get; init; }

    public decimal Charge => Lines.Sum(l => l.Charge);
    public DateOnly? From => Lines.Select(l => l.Date).Where(d => d != null).Min();
    public DateOnly? To => Lines.Select(l => l.Date).Where(d => d != null).Max();
    public string Name => string.Join(' ', new[] { First, Last }.Where(p => p.Length > 0));
    public string SortName => First.Length > 0 ? $"{Last}, {First}" : Last;
}

/// <summary>An 837 or a charges list read as billed claims.</summary>
public sealed class BilledFile
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>"837P", "837I" or "charges list".</summary>
    public required string Kind { get; init; }
    public List<BilledClaim> Claims { get; } = [];
    public List<string> Problems { get; } = [];
}

/// <summary>
/// Reads the claims an 837P or 837I bills, as far as matching needs them: the payer (2010BB), the subscriber and patient
/// (2010BA, 2010CA), each claim (CLM) and its lines (SV1, SV2) with their dates and control numbers. AVA Claim Scrubber
/// reads an 837 in full; this is the part a remittance answers to.
/// </summary>
public static class ClaimsReader
{
    public static bool Is837(X12File x12) => x12.Transactions.Any(t => t.Code == "837");

    public static BilledFile Read(X12File x12, string path)
    {
        var version = x12.Transactions.FirstOrDefault()?.Group?[8] ?? "";
        var file = new BilledFile { Path = path, Kind = version.Contains("X223") ? "837I" : "837P" };
        var name = System.IO.Path.GetFileName(path);
        var sep = x12.Delimiters.Component;
        foreach (var t in x12.Transactions.Where(t => t.Code == "837"))
        {
            var billed = t.Body.FirstOrDefault(s => s.Id == "BHT") is { } bht ? Money.Date(bht[4]) : null;
            string payerId = "", payer = "", last = "", first = "", member = "", level = "";
            string pLast = "", pFirst = "", sex = "", pSex = "", provider = "", npi = "", submitter = "", submitterId = "";
            DateOnly? born = null, pBorn = null;
            BilledClaim? claim = null;
            BilledLine? pending = null;
            void Flush()
            {
                if (pending != null && claim != null) claim.Lines.Add(pending);
                pending = null;
            }
            foreach (var s in t.Body)
            {
                switch (s.Id)
                {
                    case "HL":
                        Flush();
                        claim = null;
                        level = s[3].Trim();
                        if (level == "22") { pLast = pFirst = pSex = ""; pBorn = null; }
                        break;
                    case "NM1" when s[1].Trim() == "41":
                        submitter = s[3].Trim(); submitterId = s[9].Trim();
                        break;
                    case "NM1" when s[1].Trim() == "85":
                        provider = s[3].Trim(); npi = s[9].Trim();
                        break;
                    case "DMG" when claim == null:
                        if (level == "23") { pBorn = Money.Date(s[2]); pSex = s[3].Trim(); }
                        else { born = Money.Date(s[2]); sex = s[3].Trim(); }
                        break;
                    case "NM1" when claim == null && s[1].Trim() == "IL":
                        last = s[3].Trim(); first = s[4].Trim(); member = s[9].Trim();
                        break;
                    case "NM1" when claim == null && s[1].Trim() == "QC":
                        pLast = s[3].Trim(); pFirst = s[4].Trim();
                        break;
                    case "NM1" when claim == null && s[1].Trim() == "PR":
                        payer = s[3].Trim(); payerId = s[9].Trim();
                        break;
                    case "CLM":
                        Flush();
                        var facility = s[5].Split(sep);
                        claim = new BilledClaim
                        {
                            Source = name, Account = s[1].Trim(), PayerId = payerId, Payer = payer,
                            Last = level == "23" ? pLast : last, First = level == "23" ? pFirst : first, MemberId = member,
                            BirthDate = level == "23" ? pBorn : born, Sex = level == "23" ? pSex : sex,
                            ProviderName = provider, ProviderNpi = npi, SubmitterName = submitter, SubmitterId = submitterId,
                            Billed = billed, Frequency = facility.Length > 2 ? facility[2].Trim() : "1", Clm = s,
                        };
                        file.Claims.Add(claim);
                        break;
                    case "REF" when claim != null && pending == null && s[1].Trim() == "F8":
                        claim.Original = s[2].Trim();
                        break;
                    case "SV1" or "SV2" when claim != null:
                        Flush();
                        var proc = (s.Id == "SV1" ? s[1] : s[2]).Split(sep);
                        pending = new BilledLine
                        {
                            Number = claim.Lines.Count + 1,
                            Procedure = proc.Length > 1 ? proc[1].Trim() : "",
                            Modifiers = [.. proc.Skip(2).Take(4).Select(m => m.Trim()).Where(m => m.Length > 0)],
                            Charge = Money.Read(s.Id == "SV1" ? s[2] : s[3]) ?? 0,
                            Units = Money.Read(s.Id == "SV1" ? s[4] : s[5]) ?? 1,
                        };
                        break;
                    case "DTP" when pending != null && s[1].Trim() == "472":
                        pending = pending with { Date = Money.Date(s[3].Split('-')[0]) };
                        break;
                    case "REF" when pending != null && s[1].Trim() == "6R":
                        pending = pending with { ControlNumber = s[2].Trim() };
                        break;
                }
            }
            Flush();
        }
        if (file.Claims.Count == 0) file.Problems.Add("It has no claims (CLM) in it.");
        return file;
    }
}

/// <summary>
/// Reads a charges list: one row per billed line, with the claim's account on every row. Columns by name: Account,
/// Payer ID, Payer, Last name, First name, Member ID, Date (of service), Procedure, Modifiers, Units, Charge, Billed.
/// </summary>
public static class ChargesList
{
    public static readonly IReadOnlyDictionary<string, string[]> Names = new Dictionary<string, string[]>
    {
        ["account"] = ["account", "account number", "patient account", "claim", "claim number", "patient control number"],
        ["payerid"] = ["payer id", "payerid", "payer code"],
        ["payer"] = ["payer", "payer name", "insurance", "plan"],
        ["last"] = ["last name", "last", "surname", "patient last name"],
        ["first"] = ["first name", "first", "patient first name"],
        ["member"] = ["member id", "member", "subscriber id", "insurance id"],
        ["date"] = ["date", "date of service", "service date", "dos"],
        ["procedure"] = ["procedure", "cpt", "procedure code", "code"],
        ["modifiers"] = ["modifiers", "modifier", "mods"],
        ["units"] = ["units", "unit", "quantity"],
        ["charge"] = ["charge", "fee", "amount", "billed amount"],
        ["billed"] = ["billed", "billed date", "date billed", "sent", "submitted"],
        ["dob"] = ["birth date", "dob", "date of birth", "birthdate"],
        ["sex"] = ["sex", "gender"],
    };

    public static bool LooksLikeOne(string path)
    {
        var keys = Table.HeaderKeys(path, Names);
        return keys.Contains("account") && keys.Contains("procedure") && keys.Contains("charge") && !Denials.EligibilityRecheck.LooksLikeOne(path);
    }

    public static BilledFile Load(string path)
    {
        var table = Table.Read(path, Names);
        var file = new BilledFile { Path = path, Kind = "charges list" };
        var name = System.IO.Path.GetFileName(path);
        foreach (var group in table.Rows.Select((r, i) => (Row: r, Index: i + 1)).GroupBy(r => table.Get(r.Row, "account")).Where(g => g.Key.Length > 0))
        {
            var first = group.First().Row;
            var claim = new BilledClaim
            {
                Source = name, Account = group.Key, PayerId = table.Get(first, "payerid"), Payer = table.Get(first, "payer"),
                Last = table.Get(first, "last").ToUpperInvariant(), First = table.Get(first, "first").ToUpperInvariant(), MemberId = table.Get(first, "member"),
                Billed = Table.Date(table.Get(first, "billed")), BirthDate = Table.Date(table.Get(first, "dob")), Sex = table.Get(first, "sex").ToUpperInvariant(),
            };
            foreach (var (row, index) in group)
            {
                if (Table.Amount(table.Get(row, "charge")) is not { } charge)
                {
                    file.Problems.Add($"Row {index}: the charge \"{table.Get(row, "charge")}\" isn't an amount.");
                    continue;
                }
                claim.Lines.Add(new BilledLine
                {
                    Number = claim.Lines.Count + 1, Procedure = table.Get(row, "procedure").ToUpperInvariant(),
                    Modifiers = [.. table.Get(row, "modifiers").Split([' ', ',', '-', ':'], StringSplitOptions.RemoveEmptyEntries).Select(m => m.ToUpperInvariant())],
                    Units = Table.Amount(table.Get(row, "units")) ?? 1, Charge = charge, Date = Table.Date(table.Get(row, "date")),
                });
            }
            file.Claims.Add(claim);
        }
        return file;
    }
}
