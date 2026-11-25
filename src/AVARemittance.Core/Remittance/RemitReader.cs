using AVARemittance.Core.X12;

namespace AVARemittance.Core.Remittance;

/// <summary>
/// Reads 835s (005010X221A1) into payments, claims and lines. The header runs to the first LX or CLP; the payer (N1 PR)
/// and payee (N1 PE) loops are there; each CLP starts a claim (2100), each SVC a line (2110) under it; PLB segments
/// after the claims are the provider-level adjustments. Every item keeps the segments behind it.
/// </summary>
public static class RemitReader
{
    public static RemitFile Load(string path) => Read(X12File.Load(path), path);

    public static RemitFile Read(X12File x12, string path)
    {
        var file = new RemitFile { Path = path, X12 = x12 };
        var others = x12.Transactions.Where(t => t.Code != "835").Select(t => t.Code).Distinct().ToList();
        if (others.Count > 0) file.Problems.Add($"It has {string.Join(", ", others)} transactions too; only the 835s are read.");
        var n = 0;
        foreach (var t in x12.Transactions.Where(t => t.Code == "835"))
        {
            try
            {
                file.Payments.Add(ReadPayment(file, t, ++n, x12.Delimiters.Component));
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
            {
                file.Problems.Add($"Transaction {t.Control} couldn't be read: {e.Message}");
            }
        }
        if (x12.Transactions.Count == 0) file.Problems.Add("There's no ST segment: it isn't an X12 transaction.");
        return file;
    }

    private static Payment ReadPayment(RemitFile file, Transaction t, int number, char sep)
    {
        var p = new Payment { File = file, Source = t, Number = number };
        Party? party = null;
        ClaimPayment? claim = null;
        ServicePayment? line = null;
        var lx = "";
        var claims = 0;
        foreach (var s in t.Body)
        {
            switch (s.Id)
            {
                case "BPR":
                    p.Bpr = s;
                    p.Handling = s[1].Trim();
                    p.Total = Money.Read(s[2]) ?? 0;
                    p.CreditDebit = s[3].Trim();
                    p.Method = s[4].Trim();
                    p.Format = s[5].Trim();
                    p.SenderBank = s[7].Trim();
                    p.SenderAccount = s[9].Trim();
                    p.ReceiverBank = s[13].Trim();
                    p.ReceiverAccount = s[15].Trim();
                    p.PaidOn = Money.Date(s[16]);
                    p.Header.Add(s);
                    continue;
                case "TRN" when claim == null:
                    p.Trn = s;
                    p.Trace = s[2].Trim();
                    p.Originator = s[3].Trim();
                    p.Header.Add(s);
                    continue;
                case "N1" when claim == null:
                    party = new Party { Name = s[2].Trim(), IdQualifier = s[3].Trim(), Id = s[4].Trim() };
                    party.Segments.Add(s);
                    if (s[1].Trim() == "PR") p.Payer = party;
                    else if (s[1].Trim() == "PE") p.Payee = party;
                    p.Header.Add(s);
                    continue;
                case "PLB":
                    line = null;
                    for (var i = 3; i + 1 <= s.Elements.Length - 1; i += 2)
                    {
                        if (s[i].Trim().Length == 0) break;
                        var parts = s[i].Split(sep);
                        p.ProviderAdjustments.Add(new ProviderAdjustment(s[1].Trim(), Money.Date(s[2]), parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "", Money.Read(s[i + 1]) ?? 0, s));
                    }
                    continue;
                case "LX":
                    lx = s[1].Trim();
                    claim = null;
                    line = null;
                    p.Header.Add(s);
                    continue;
                case "TS3" or "TS2" when claim == null:
                    p.Header.Add(s);
                    continue;
                case "CLP":
                    line = null;
                    claim = new ClaimPayment
                    {
                        Payment = p, Number = ++claims, Lx = lx, Account = s[1].Trim(), Status = s[2].Trim(),
                        Charge = Money.Read(s[3]) ?? 0, Paid = Money.Read(s[4]) ?? 0, PatientResponsibility = Money.Read(s[5]) ?? 0,
                        Filing = s[6].Trim(), PayerClaim = s[7].Trim(), Facility = s[8].Trim(), Frequency = s[9].Trim(), Drg = s[11].Trim(),
                    };
                    claim.Segments.Add(s);
                    p.Claims.Add(claim);
                    continue;
            }

            if (claim == null)
            {
                // The header and the payer's and payee's loops.
                if (party != null && s.Id is "N3" or "N4" or "REF" or "PER" or "RDM")
                {
                    party.Segments.Add(s);
                    switch (s.Id)
                    {
                        case "N3": party.Address = s[1].Trim() + (s[2].Trim().Length > 0 ? ", " + s[2].Trim() : ""); break;
                        case "N4": party.Address += (party.Address.Length > 0 ? ", " : "") + $"{s[1].Trim()}, {s[2].Trim()} {s[3].Trim()}".Trim(); break;
                        case "REF": party.References.Add(new Reference(s[1].Trim(), s[2].Trim(), s)); break;
                        case "PER":
                            party.Contacts.Add(string.Join(' ', new[] { s[2].Trim() }.Concat(Enumerable.Range(0, 3).Select(k => (s[3 + 2 * k].Trim(), s[4 + 2 * k].Trim()))
                                .Where(c => c.Item2.Length > 0).Select(c => $"{Contact(c.Item1)} {c.Item2}"))).Trim());
                            break;
                    }
                }
                else
                {
                    if (s.Id == "REF") p.References.Add(new Reference(s[1].Trim(), s[2].Trim(), s));
                    if (s.Id == "DTM" && s[1].Trim() == "405") p.Produced = Money.Date(s[2]);
                    p.Header.Add(s);
                }
                continue;
            }

            if (s.Id == "SVC")
            {
                var proc = s[1].Split(sep);
                var orig = s[6].Split(sep);
                line = new ServicePayment
                {
                    Claim = claim, Number = claim.Lines.Count + 1,
                    Qualifier = proc[0].Trim(), Procedure = proc.Length > 1 ? proc[1].Trim() : "",
                    Modifiers = [.. proc.Skip(2).Take(4).Select(m => m.Trim()).Where(m => m.Length > 0)],
                    Description = proc.Length > 6 ? proc[6].Trim() : "",
                    Charge = Money.Read(s[2]) ?? 0, Paid = Money.Read(s[3]) ?? 0, Revenue = s[4].Trim(), Units = Money.Read(s[5]),
                    OriginalProcedure = orig.Length > 1 ? orig[1].Trim() : "", OriginalModifiers = [.. orig.Skip(2).Take(4).Select(m => m.Trim()).Where(m => m.Length > 0)],
                    OriginalUnits = Money.Read(s[7]),
                };
                line.Segments.Add(s);
                claim.Lines.Add(line);
                continue;
            }

            var into = (IList<Segment>?)line?.Segments ?? claim.Segments;
            into.Add(s);
            switch (s.Id)
            {
                case "CAS":
                    var list = line?.Adjustments ?? claim.Adjustments;
                    for (var i = 2; i + 1 <= 19; i += 3)
                    {
                        if (s[i].Trim().Length == 0) break;
                        list.Add(new Adjustment(s[1].Trim(), s[i].Trim(), Money.Read(s[i + 1]) ?? 0, Money.Read(s[i + 2]), s));
                    }
                    break;
                case "NM1" when line == null:
                    var who = s[1].Trim();
                    switch (who)
                    {
                        case "QC" or "IL" or "74" or "GB":
                            var person = new Person(who, s[3].Trim(), s[4].Trim(), s[5].Trim(), s[8].Trim(), s[9].Trim(), s);
                            if (who == "QC") claim.Patient = person;
                            else if (who == "IL") claim.Insured = person;
                            else if (who == "74") claim.Corrected = person;
                            break;
                        case "82": claim.Rendering = Named(s); break;
                        case "TT": claim.Crossover = Named(s); break;
                        case "PR": claim.PriorityPayer = Named(s); break;
                    }
                    break;
                case "MOA" when line == null:
                    for (var i = 3; i <= 7; i++) if (s[i].Trim().Length > 0) claim.Remarks.Add(new Remark(s[i].Trim(), s));
                    break;
                case "MIA" when line == null:
                    foreach (var i in new[] { 5, 20, 21, 22, 23 }) if (s[i].Trim().Length > 0) claim.Remarks.Add(new Remark(s[i].Trim(), s));
                    break;
                case "LQ" when line != null && s[1].Trim() == "HE":
                    line.Remarks.Add(new Remark(s[2].Trim(), s));
                    break;
                case "DTM":
                    (line?.Dates ?? claim.Dates).Add(new Dated(s[1].Trim(), Money.Date(s[2]), s[2].Trim(), s));
                    break;
                case "REF":
                    (line?.References ?? claim.References).Add(new Reference(s[1].Trim(), s[2].Trim(), s));
                    break;
                case "AMT":
                    (line?.Amounts ?? claim.Amounts).Add(new Amount(s[1].Trim(), Money.Read(s[2]) ?? 0, s));
                    break;
            }
        }
        return p;
    }

    private static Named Named(Segment s) =>
        new(s[1].Trim(), string.Join(' ', new[] { s[4].Trim(), s[3].Trim() }.Where(x => x.Length > 0)), s[8].Trim(), s[9].Trim(), s);

    private static string Contact(string qualifier) => qualifier switch
    {
        "TE" => "phone", "EM" => "email", "FX" => "fax", "UR" => "web", "EX" => "ext.", _ => qualifier,
    };
}
