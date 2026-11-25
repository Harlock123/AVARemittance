using System.Net;
using System.Text;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Remittance;
using ClosedXML.Excel;

namespace AVARemittance.Core.Reports;

/// <summary>The denials worklist as an HTML page or an Excel workbook. People's details go through <c>mask</c>.</summary>
public static class DenialsReport
{
    private static readonly string[] Columns = ["Item", "Account", "Patient", "Payer", "Payer claim", "Action", "Amount", "Reasons", "Remarks", "Deadline", "Days left", "State", "What to do"];

    private static object[] Row(WorkItem w, DateOnly today, Func<string, string> mask) =>
    [
        w.Id, mask(w.Claim.Account), mask(w.Claim.Patient?.Name ?? ""), w.Claim.Payment.Payer.Name, mask(w.Claim.PayerClaim), Classifier.Say(w.Action), w.Amount,
        string.Join("; ", w.Reasons.Select(r => $"{r.Group}-{r.Reason} {CodeList.Carc.Say(r.Reason)}").Distinct()),
        string.Join("; ", w.Remarks.Select(r => $"{r.Code} {CodeList.Rarc.Say(r.Code)}")),
        w.Deadline is { } d ? Money.Day(d) : "", w.DaysLeft(today) is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
        Classifier.Say(w.State) + (w.StateDate is { } s ? $" {Money.Day(s)}" : ""), w.WhatToDo,
    ];

    public static void Write(string path, IReadOnlyList<WorkItem> items, DateOnly today, Func<string, string>? mask = null)
    {
        mask ??= s => s;
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var book = new XLWorkbook();
            var sheet = book.AddWorksheet("Denials");
            for (var i = 0; i < Columns.Length; i++) sheet.Cell(1, i + 1).Value = Columns[i];
            sheet.Row(1).Style.Font.Bold = true;
            var r = 2;
            foreach (var w in items)
            {
                var row = Row(w, today, mask);
                for (var i = 0; i < row.Length; i++)
                {
                    if (row[i] is decimal m) { sheet.Cell(r, i + 1).Value = m; sheet.Cell(r, i + 1).Style.NumberFormat.Format = "$#,##0.00"; }
                    else sheet.Cell(r, i + 1).Value = row[i].ToString();
                }
                r++;
            }
            sheet.Columns(1, 12).AdjustToContents();
            book.SaveAs(path);
            return;
        }
        var h = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>Denials</title><style>body{font-family:system-ui,sans-serif;margin:24px}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;text-align:left;vertical-align:top}th{background:#f2f2f2}</style></head><body>");
        var open = items.Where(i => !i.Done).ToList();
        h.Append($"<h1>Denials</h1><p>{open.Count} open, {Money.Say(open.Sum(i => i.Amount))} at stake, as of {Money.Day(today)}.</p><table><tr>");
        foreach (var c in Columns) h.Append("<th>").Append(WebUtility.HtmlEncode(c)).Append("</th>");
        h.Append("</tr>");
        foreach (var w in items)
        {
            h.Append("<tr>");
            foreach (var v in Row(w, today, mask)) h.Append("<td>").Append(WebUtility.HtmlEncode(v is decimal m ? Money.Say(m) : v.ToString())).Append("</td>");
            h.Append("</tr>");
        }
        File.WriteAllText(path, h.Append("</table></body></html>").ToString());
    }
}
