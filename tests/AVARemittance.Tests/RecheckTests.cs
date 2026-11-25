using AVARemittance.Core.Billing;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Matching;

namespace AVARemittance.Tests;

/// <summary>Check Eligibility Again: the coverage denials as AVA Eligibility's appointment list.</summary>
public sealed class RecheckTests
{
    internal static (List<WorkItem> Work, Reconciliation Matched) Recheck(bool billed = true)
    {
        var remit = Samples.Load("recheck/tallgrass-835-1119.x12");
        var claims = billed ? ClaimsReader.Read(Core.X12.X12File.Load(Samples.Path("recheck/tallgrass-837p-1105.x12")), Samples.Path("recheck/tallgrass-837p-1105.x12")).Claims : [];
        return (Worklist.Build(remit.Claims, []), Matcher.Match(claims, remit.Claims, MatchingTests.Today));
    }

    [Fact]
    public void The_samples_list_is_what_the_app_writes()
    {
        var (work, matched) = Recheck();
        Assert.Equal(["RB26118 CO-27", "RB26119 CO-31", "RB26120 CO-26"], work.Select(w => $"{w.Claim.Account} {w.Reasons[0].Group}-{w.Reasons[0].Reason}").Order());
        Assert.All(work, w => Assert.True(EligibilityRecheck.Applies(w)));
        var rows = EligibilityRecheck.Rows(work, matched).OrderBy(r => r.Account, StringComparer.Ordinal).ToList();
        Assert.Equal(File.ReadAllText(Samples.Path("recheck/eligibility-recheck.csv")), EligibilityRecheck.Csv(rows));
        Assert.True(EligibilityRecheck.LooksLikeOne(Samples.Path("recheck/eligibility-recheck.csv")));
        Assert.False(ChargesList.LooksLikeOne(Samples.Path("recheck/eligibility-recheck.csv")));
        Assert.Null(Core.FileKinds.Of(Samples.Path("recheck/eligibility-recheck.csv")));
        Assert.True(ChargesList.LooksLikeOne(Samples.Path("charges/charges.csv")));
    }

    [Fact]
    public void Without_the_837_the_list_has_no_birth_dates_and_names_the_rendering_provider()
    {
        var (work, matched) = Recheck(billed: false);
        var r = EligibilityRecheck.Rows(work, matched).Single(x => x.Account == "RB26119");
        Assert.Null(r.BirthDate);
        Assert.Equal(("TRAN", "VIET", "TG55090123", "TGM01", "self", "99214", 220m, new DateOnly(2026, 11, 4)), (r.Last, r.First, r.MemberId, r.PayerId, r.Subscriber, r.Procedure, r.Charge, r.Date!.Value));
        Assert.Equal("CO-31: Patient cannot be identified as our insured.", r.Denial);
    }

    [Fact]
    public void In_the_payers_november_files_only_the_other_payer_denial_is_one()
    {
        var remits = MatchingTests.Remits().SelectMany(f => f.Claims).ToList();
        var work = Worklist.Build(remits, []);
        var rows = EligibilityRecheck.Rows(work, Matcher.Match(MatchingTests.Billed(), remits, MatchingTests.Today));
        var r = Assert.Single(rows);
        Assert.Equal(("RB26111", "KOWALSKI", "1958-09-14", "CO-22: This care may be covered by another payer per coordination of benefits. MA04: Secondary payment cannot be considered without the identity of or payment information from the primary payer. The information was either not reported or was illegible."),
            (r.Account, r.Last, r.BirthDate?.ToString("yyyy-MM-dd"), r.Denial));
        Assert.Empty(EligibilityRecheck.Rows(work.Where(w => w.Claim.Account != "RB26111")));
    }

    [Fact]
    public void Cells_with_commas_are_quoted_and_the_program_is_the_one_set()
    {
        var (work, matched) = Recheck();
        var row = EligibilityRecheck.Row(work[0], matched) with { Payer = "SMITH, JONES \"AND\" CO" };
        Assert.Contains(",\"SMITH, JONES \"\"AND\"\" CO\",", EligibilityRecheck.Csv([row]));
        var exe = Path.Combine(Samples.Copy(), "AVAEligibility");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, EligibilityRecheck.FindProgram(exe));
    }
}
