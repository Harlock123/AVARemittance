using AVARemittance.Core.Contracts;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>The fee schedule and the lines allowed below it.</summary>
public sealed class UnderpaymentTests
{
    private static FeeSchedule Schedule() => FeeSchedule.Load(Samples.Path("contracts/fee-schedule.csv"));

    [Fact]
    public void The_schedule_reads_rates_dates_and_a_percentage_of_another_schedule()
    {
        Assert.True(FeeSchedule.LooksLikeOne(Samples.Path("contracts/fee-schedule.csv")));
        Assert.False(FeeSchedule.LooksLikeOne(Samples.Path("charges/charges.csv")));
        var s = Schedule();
        Assert.Empty(s.Problems);
        Assert.Equal(19, s.Rates.Count);
        var visit = Samples.Claim("tallgrass-835-1112.x12", "RB26113").Lines[0];
        Assert.Equal(new Expected(91.00m, "fee-schedule.csv rows 12 and 15: 65% of MEDICARE's $140.00 for 99214"), s.For(visit));
        var ellis = Samples.Load("harborview-835-1117.x12").Claims.ToList()[1].Lines[0];
        Assert.Equal(192m, s.For(ellis)!.Amount);
    }

    [Fact]
    public void Three_lines_come_in_below_the_schedule()
    {
        var found = Underpayments.Find(MatchingTests.Remits().SelectMany(f => f.Claims), Schedule(), 1.00m);
        Assert.Equal(
        [
            "U1 RB26108, line 1 (99203 on 2026-10-28, HARBORVIEW HEALTH PLAN): Allowed $130.00; the schedule expects $140.00: $10.00 below what the schedule expects (fee-schedule.csv row 1: $140.00 for 99203).",
            "U1 RB26106, line 1 (99396 on 2026-10-29, HARBORVIEW HEALTH PLAN): Allowed $180.00; the schedule expects $195.00: $15.00 below what the schedule expects (fee-schedule.csv row 9: $195.00 for 99396).",
            "U1 RB26113, line 1 (99214 on 2026-10-21, TALLGRASS MEDICAID): Allowed $88.10; the schedule expects $91.00: $2.90 below what the schedule expects (fee-schedule.csv rows 12 and 15: 65% of MEDICARE's $140.00 for 99214).",
        ], found.Select(Underpayments.Describe));
        Assert.Equal(["HARBORVIEW HEALTH PLAN 99396 15.00", "HARBORVIEW HEALTH PLAN 99203 10.00", "TALLGRASS MEDICAID 99214 2.90"],
            Underpayments.ByCode(found).Select(c => $"{c.Payer} {c.Procedure} {c.Below:0.00}"));
        Assert.Equal(2, Underpayments.Find(MatchingTests.Remits().SelectMany(f => f.Claims), Schedule(), 5.00m).Count);
    }

    [Fact]
    public void The_reversed_payment_is_left_out_but_checked_when_its_reversal_isnt_open()
    {
        var alone = Underpayments.Find(Samples.Load("harborview-835-1110.x12").Claims, Schedule(), 1.00m);
        Assert.Contains(alone, u => u.Claim.Account == "RB26105" && u.Below == 12m);
    }

    [Fact]
    public void Amounts_that_dont_agree_and_a_co45_too_large_without_an_allowed_amount()
    {
        var text = File.ReadAllText(Samples.Path("harborview-835-1110.x12"))
            .Replace("AMT*B6*110~\nCLP*RB26102", "AMT*B6*120~\nCLP*RB26102")
            .Replace("AMT*B6*130~", "");
        var file = RemitReader.Read(Core.X12.X12File.Parse(text), "changed.x12");
        var found = Underpayments.Find(file.Claims, Schedule(), 1.00m);
        Assert.Contains("U2 RB26101, line 1 (99213 on 2026-10-20, HARBORVIEW HEALTH PLAN): The payer allowed $120.00, but the payment $85.00 and the patient's $25.00 come to $110.00: $10.00 unpaid, by the payer's own numbers.", found.Select(Underpayments.Describe));
        Assert.Contains("U3 RB26108, line 1 (99203 on 2026-10-28, HARBORVIEW HEALTH PLAN): CO-45 took $70.00 off, but the schedule leaves only $60.00 between the $200.00 charge and its $140.00: $10.00 below what the schedule expects (fee-schedule.csv row 1: $140.00 for 99203).", found.Select(Underpayments.Describe));
    }
}
