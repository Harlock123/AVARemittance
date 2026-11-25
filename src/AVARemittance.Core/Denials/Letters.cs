using System.Net;
using System.Text;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Remittance;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AVARemittance.Core.Denials;

/// <summary>
/// Drafts for a person to finish: an appeal letter (Markdown, HTML or Word, by the file's extension) and a corrected
/// claim's list of changes. Every draft says it's a draft, and marks in [brackets] what only a person can add. Nothing
/// here sends anything.
/// </summary>
public static class Letters
{
    public const string DraftNote = "DRAFT: check it against the payer's appeal policy and the patient's records, and fill in every [bracket], before it goes anywhere.";

    /// <summary>The letter as paragraphs: a heading ("# …"), lines, and paragraphs; one renderer per format reads it.</summary>
    public static List<string> Appeal(WorkItem w, DateOnly today, Func<string, string>? mask = null)
    {
        mask ??= s => s;
        var c = w.Claim;
        var p = c.Payment;
        var lines = new List<string>
        {
            "! " + DraftNote,
            p.Payee.Name + (p.Payee.Address.Length > 0 ? "\n" + mask(p.Payee.Address) : "") + (p.Payee.Id.Length > 0 ? $"\nNPI {p.Payee.Id}" : ""),
            Money.Day(today),
            p.Payer.Name + (p.Payer.Address.Length > 0 ? "\n" + p.Payer.Address : "") + "\nAttn: Appeals",
            $"# {(w.Action == WorkAction.Appeal ? "Request for reconsideration" : "Request for review")}: claim {mask(c.PayerClaim)}",
            $"Patient: {mask(c.Patient?.Name ?? "[patient]")}\nMember ID: {mask(c.Patient?.Id ?? "[member ID]")}\nPatient account: {mask(c.Account)}\n"
                + $"Payer claim number: {mask(c.PayerClaim)}\nDates of service: {Dates(c, mask)}\nAmount in question: {Money.Say(w.Amount)}\n"
                + $"Remittance: {Money.Day(p.PaidOn)}, {(p.IsCheck ? "check" : "trace")} {mask(p.Trace)}",
            "To the appeals department:",
            $"We ask you to reconsider your decision on the claim above. Your remittance of {Money.Day(p.PaidOn)} adjusted "
                + (w.Lines.Count > 0 ? string.Join(" and ", w.Lines.Select(l => $"{Words.Code(l)} ({Money.Say(l.Charge)})")) : "the claim")
                + " for these reasons:",
        };
        foreach (var r in w.Reasons.DistinctBy(r => (r.Group, r.Reason)))
            lines.Add($"- {r.Group}-{r.Reason}: {CodeList.Carc.Say(r.Reason)}");
        foreach (var r in w.Remarks)
            lines.Add($"- {r.Code}: {CodeList.Rarc.Say(r.Code)}");
        lines.Add(Argument(w));
        lines.Add("Enclosed: [the records named above, a copy of the remittance, and anything else the payer's appeal policy asks for].");
        lines.Add("We ask that the claim be reprocessed and paid. Please contact us with any question about it.");
        lines.Add("Sincerely,\n[name and title]\n" + p.Payee.Name + "\n[phone]");
        return lines;
    }

    private static string Argument(WorkItem w)
    {
        var codes = w.Reasons.Select(r => r.Reason).ToHashSet();
        if (codes.Overlaps(["50", "55", "56", "57", "150", "151", "152", "167"]))
            return "The care was medically necessary. [Say why: the patient's condition, what was tried before, the plan of care and its goals, and how this care follows the payer's own coverage policy, naming it.] The enclosed records show it.";
        if (codes.Contains("197"))
            return "The care was authorized before it was given: authorization [number], issued [date] by [who], for [what and how many]. [If it couldn't be obtained in time, say why.]";
        if (codes.Contains("29"))
            return "The claim was filed within your limit. [Name the proof: the 999 or 277CA acceptance, its date and control number, or the clearinghouse's report.]";
        if (codes.Overlaps(["97", "234", "236", "B15"]))
            return "The service was separate from the one it was bundled into. [Say why: a separate problem, site or session, and the modifier that shows it.]";
        if (codes.Contains("22"))
            return "[Say whether another payer is primary. If not, say why this payer is: the other coverage ended on [date], or the patient has none.]";
        return "[Say why the payer's reason doesn't apply, with what shows it.]";
    }

    private static string Dates(ClaimPayment c, Func<string, string> mask) =>
        c.From is { } f ? mask(c.To is { } t && t != f ? $"{Money.Day(f)} to {Money.Day(t)}" : Money.Day(f)) : "[dates]";

    /// <summary>What to change before a corrected claim goes out: the frequency, the original claim number, and the fix.</summary>
    public static List<string> Corrected(WorkItem w, Func<string, string>? mask = null)
    {
        mask ??= s => s;
        var c = w.Claim;
        var list = new List<string>
        {
            $"Corrected claim for {mask(c.Account)} ({c.Payment.Payer.Name}, payer claim {mask(c.PayerClaim)}):",
            "- CLM05-3, the claim frequency: 7 (replacement of prior claim).",
            $"- REF*F8, the original reference number: {mask(c.PayerClaim)}.",
        };
        foreach (var r in w.Remarks)
            list.Add($"- {r.Code}: {CodeList.Rarc.Say(r.Code)} " + (r.Code switch
            {
                "N822" => "Add the modifier the line needs (25 on a visit separate from a same-day procedure, if the notes support it).",
                "M62" => "Add the authorization number in REF*G1.",
                "N290" => "Put the rendering provider's NPI in 2310B NM109.",
                _ => "Correct what it names.",
            }));
        if (w.Reasons.Any(r => r.Reason == "197") && w.Remarks.All(r => r.Code != "M62")) list.Add("- Add the authorization number in REF*G1.");
        list.Add("- Everything else as first billed. AVA Claim Scrubber can check the corrected 837 before it goes.");
        return list;
    }

    /// <summary>Writes the appeal draft: .md, .html or .docx by the path's extension.</summary>
    public static void WriteAppeal(string path, WorkItem w, DateOnly today, Func<string, string>? mask = null)
    {
        var parts = Appeal(w, today, mask);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx": WriteWord(path, parts); break;
            case ".html" or ".htm": File.WriteAllText(path, Html(parts)); break;
            default: File.WriteAllText(path, Markdown(parts)); break;
        }
    }

    public static string Markdown(IEnumerable<string> parts)
    {
        var s = new StringBuilder();
        var inList = false;
        foreach (var p in parts)
        {
            var isItem = p.StartsWith("- ", StringComparison.Ordinal);
            if (inList && !isItem) s.Append('\n');
            inList = isItem;
            if (p.StartsWith("! ", StringComparison.Ordinal)) s.Append("> **").Append(p[2..]).Append("**\n\n");
            else if (isItem) s.Append(p).Append('\n');
            else s.Append(p.Replace("\n", "  \n")).Append("\n\n");
        }
        return s.ToString().TrimEnd() + "\n";
    }

    public static string Html(IEnumerable<string> parts)
    {
        var s = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>Appeal draft</title><style>body{font-family:Georgia,serif;max-width:720px;margin:40px auto;line-height:1.45}.draft{border:2px solid #c00;padding:8px;color:#900;font-family:system-ui,sans-serif}</style></head><body>");
        var inList = false;
        foreach (var p in parts)
        {
            var isItem = p.StartsWith("- ", StringComparison.Ordinal);
            if (isItem && !inList) { s.Append("<ul>"); inList = true; }
            if (!isItem && inList) { s.Append("</ul>"); inList = false; }
            var text = WebUtility.HtmlEncode(isItem ? p[2..] : p.TrimStart('!', '#', ' ')).Replace("\n", "<br>");
            s.Append(p.StartsWith("! ", StringComparison.Ordinal) ? $"<p class=\"draft\">{text}</p>" : p.StartsWith("# ", StringComparison.Ordinal) ? $"<h2>{text}</h2>" : isItem ? $"<li>{text}</li>" : $"<p>{text}</p>");
        }
        if (inList) s.Append("</ul>");
        return s.Append("</body></html>").ToString();
    }

    private static void WriteWord(string path, IEnumerable<string> parts)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        foreach (var p in parts)
        {
            var heading = p.StartsWith("# ", StringComparison.Ordinal);
            var draft = p.StartsWith("! ", StringComparison.Ordinal);
            var text = p.StartsWith("- ", StringComparison.Ordinal) ? "• " + p[2..] : p.TrimStart('!', '#', ' ');
            var run = new Run();
            var props = new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = heading ? "28" : "22" });
            if (heading || draft) props.Append(new Bold());
            if (draft) props.Append(new Color { Val = "C00000" });
            run.Append(props);
            var pieces = text.Split('\n');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0) run.Append(new Break());
                run.Append(new Text(pieces[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "160" }), run));
        }
        body.Append(new SectionProperties(new PageSize { Width = 12240, Height = 15840 }, new PageMargin { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440 }));
        main.Document = new Document(body);
        main.Document.Save();
    }
}
