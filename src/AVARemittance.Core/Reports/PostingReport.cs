using System.Net;
using System.Text;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.Remittance;
using ClosedXML.Excel;

namespace AVARemittance.Core.Reports;

/// <summary>
/// The posting summary as an HTML page or an Excel workbook: each day's totals, then every claim posted. People's
/// details go through <c>mask</c> (agents get the masked report unless values are allowed).
/// </summary>
public static class PostingReport
{
    private static readonly string[] DayColumns = ["Date", "Payments", "Claims", "Received", "Posted", "Unapplied", "Adjusted off", "Patient", "Recouped", "Added", "Deposited"];
    private static readonly string[] ClaimColumns = ["Paid on", "Payer", "Trace", "Account", "Patient", "Payer claim", "Status", "Charge", "Paid", "Patient owes", "Adjusted off"];

    private static object[] Day(PostingDay d) =>
        [Money.Day(d.Date), d.Payments.Count, d.Claims, d.Received, d.Posted, d.Unapplied, d.Adjusted, d.PatientResponsibility, d.Recouped, d.Added, d.Deposited];

    private static object[] Claim(ClaimPayment c, Func<string, string> mask) =>
    [
        Money.Day(c.Payment.PaidOn), c.Payment.Payer.Name, mask(c.Payment.Trace), mask(c.Account), mask(c.Patient?.Name ?? ""), mask(c.PayerClaim),
        Words.State(c.State), c.Charge, c.Paid, c.PatientResponsibility, c.AllAdjustments.Where(a => a.Group != "PR").Sum(a => a.Amount),
    ];

    public static void Write(string path, IReadOnlyList<PostingDay> days, Func<string, string>? mask = null)
    {
        mask ??= s => s;
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) WriteExcel(path, days, mask);
        else File.WriteAllText(path, Html(days, mask));
    }

    public static string Html(IReadOnlyList<PostingDay> days, Func<string, string> mask)
    {
        var h = new StringBuilder();
        h.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Posting summary</title><style>")
         .Append("body{font-family:system-ui,sans-serif;margin:24px;color:#222}table{border-collapse:collapse;margin:8px 0 24px}")
         .Append("th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}td.n{text-align:right}th{background:#f2f2f2}</style></head><body>");
        h.Append("<h1>Posting summary</h1><p>What the open 835s paid, by the day they were paid. Received is what each 835 says was sent; ")
         .Append("posted is paid on claims; unapplied is paid on claims nothing open billed; recouped was taken back by the payer; added is interest and the like.</p>");
        Table(h, DayColumns, days.Select(Day));
        h.Append("<h2>Claims</h2>");
        Table(h, ClaimColumns, days.SelectMany(d => d.Payments).SelectMany(p => p.Claims).Select(c => Claim(c, mask)));
        return h.Append("</body></html>").ToString();
    }

    private static void Table(StringBuilder h, string[] columns, IEnumerable<object[]> rows)
    {
        h.Append("<table><tr>");
        foreach (var c in columns) h.Append("<th>").Append(WebUtility.HtmlEncode(c)).Append("</th>");
        h.Append("</tr>");
        foreach (var r in rows)
        {
            h.Append("<tr>");
            foreach (var v in r) h.Append(v is decimal m ? $"<td class=\"n\">{WebUtility.HtmlEncode(Money.Say(m))}</td>" : v is int i ? $"<td class=\"n\">{i}</td>" : $"<td>{WebUtility.HtmlEncode(v.ToString())}</td>");
            h.Append("</tr>");
        }
        h.Append("</table>");
    }

    private static void WriteExcel(string path, IReadOnlyList<PostingDay> days, Func<string, string> mask)
    {
        using var book = new XLWorkbook();
        Sheet(book.AddWorksheet("Days"), DayColumns, days.Select(Day));
        Sheet(book.AddWorksheet("Claims"), ClaimColumns, days.SelectMany(d => d.Payments).SelectMany(p => p.Claims).Select(c => Claim(c, mask)));
        book.SaveAs(path);
    }

    private static void Sheet(IXLWorksheet sheet, string[] columns, IEnumerable<object[]> rows)
    {
        for (var i = 0; i < columns.Length; i++) sheet.Cell(1, i + 1).Value = columns[i];
        sheet.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                var cell = sheet.Cell(r, i + 1);
                switch (row[i])
                {
                    case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "$#,##0.00;-$#,##0.00"; break;
                    case int n: cell.Value = n; break;
                    default: cell.Value = row[i].ToString(); break;
                }
            }
            r++;
        }
        sheet.Columns().AdjustToContents();
    }
}
