using AVARemittance.Core.Deposits;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Reports;

namespace AVARemittance.Tests;

/// <summary>The bank statements, deposits matched to payments, and the posting summary.</summary>
public sealed class DepositsTests
{
    private static IEnumerable<Payment> Payments() => MatchingTests.Remits().SelectMany(f => f.Payments);

    [Theory]
    [InlineData("bank/statement-2026-11.csv", "bank statement")]
    [InlineData("bank/statement-2026-11.bai", "BAI2 statement")]
    public void A_statement_reads_its_deposits_and_their_traces(string file, string kind)
    {
        Assert.True(BankStatement.LooksLikeOne(Samples.Path(file)));
        var s = BankStatement.Load(Samples.Path(file));
        Assert.Equal(kind, s.Kind);
        Assert.Empty(s.Problems);
        Assert.Equal([471.00m, 296.15m, 253.12m, 345.00m, 412.50m], s.Deposits.Select(d => d.Amount));
        Assert.Equal(["1HV2611100001", "40017731", "1HV2611170002", "", "1HV2611240003"], s.Deposits.Select(d => d.Trace));
        Assert.Equal(new DateOnly(2026, 11, 14), s.Deposits[1].Date);
    }

    [Fact]
    public void Each_deposit_is_matched_by_trace_or_check_and_the_rest_is_said()
    {
        var r = DepositMatcher.Match(BankStatement.Load(Samples.Path("bank/statement-2026-11.csv")).Deposits, Payments());
        Assert.Equal(
        [
            "$471.00 on 2026-11-10: HARBORVIEW HEALTH PLAN's $471.00 EFT 1HV2611100001 (by trace).",
            "$296.15 on 2026-11-14: TALLGRASS MEDICAID's $296.15 check 40017731 (by check number).",
            "$253.12 on 2026-11-17: HARBORVIEW HEALTH PLAN's $253.12 EFT 1HV2611170002 (by trace).",
        ], r.Matched.Select(DepositMatcher.Describe));
        Assert.Equal([412.50m], r.NoRemittance.Select(d => d.Amount));
        Assert.Equal([345.00m], r.Other.Select(d => d.Amount));
        Assert.Empty(r.NoMoney);
    }

    [Fact]
    public void A_difference_the_size_of_a_recoupment_is_named_and_a_missing_deposit_is_listed()
    {
        var deposits = BankStatement.Load(Samples.Path("bank/statement-2026-11.csv")).Deposits;
        var changed = deposits.Select(d => d.Trace == "1HV2611170002" ? d with { Amount = 328.12m } : d).Where(d => d.Trace != "40017731");
        var r = DepositMatcher.Match(changed, Payments());
        Assert.Equal("$328.12 on 2026-11-17: HARBORVIEW HEALTH PLAN's $253.12 EFT 1HV2611170002 (by trace). The deposit is $75.00 more than the 835 says was sent: the size of its WO overpayment recovery, so the bank may have the money before it.",
            DepositMatcher.Describe(r.Matched.Single(m => m.Difference != 0)));
        Assert.Equal(["40017731"], r.NoMoney.Select(p => p.Trace));
    }

    [Fact]
    public void A_deposit_with_no_trace_matches_by_amount_and_date()
    {
        var d = new Deposit { Source = "x", Date = new DateOnly(2026, 11, 12), Amount = 471.00m, Description = "DEPOSIT" };
        var r = DepositMatcher.Match([d], Payments());
        Assert.Equal("amount and date", Assert.Single(r.Matched).By);
    }

    [Fact]
    public void The_posting_summary_by_day()
    {
        var payments = Payments().ToList();
        var banked = DepositMatcher.Match(BankStatement.Load(Samples.Path("bank/statement-2026-11.csv")).Deposits, payments);
        var days = Posting.Days(payments, null, banked);
        Assert.Equal(
        [
            "2026-11-10: 1 payment, 7 claims. Received $471.00; posted $471.00; adjusted off $1,855.00; the patients' $334.00; deposited $471.00.",
            "2026-11-12: 1 payment, 6 claims. Received $296.15; posted $296.15; adjusted off $944.85; the patients' $4.00; deposited $296.15.",
            "2026-11-17: 1 payment, 5 claims. Received $253.12; posted $325.00; adjusted off $120.00; the patients' $25.00; recouped $75.00; added $3.12; deposited $253.12.",
        ], days.Select(d => MainWindow.DayLine(d, true)));
    }

    [Fact]
    public void The_posting_report_writes_html_and_excel_and_masks_what_it_is_told_to()
    {
        var days = Posting.Days(Payments(), null, null);
        var dir = Directory.CreateTempSubdirectory("posting-").FullName;
        PostingReport.Write(Path.Combine(dir, "p.html"), days, s => s.Length == 0 ? s : "***");
        var html = File.ReadAllText(Path.Combine(dir, "p.html"));
        Assert.Contains("<h1>Posting summary</h1>", html);
        Assert.Contains("HARBORVIEW HEALTH PLAN", html);
        Assert.DoesNotContain("ALVAREZ", html);
        Assert.DoesNotContain("RB26101", html);
        PostingReport.Write(Path.Combine(dir, "p.xlsx"), days);
        using var book = new ClosedXML.Excel.XLWorkbook(Path.Combine(dir, "p.xlsx"));
        Assert.Equal(["Days", "Claims"], book.Worksheets.Select(s => s.Name));
        Assert.Equal(18, book.Worksheet("Claims").RowsUsed().Count() - 1);
        Assert.Equal(471m, book.Worksheet("Days").Cell(2, 4).GetValue<decimal>());
    }
}
