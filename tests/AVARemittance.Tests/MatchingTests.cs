using AVARemittance.Core.Billing;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>Billed claims (837s, a charges list) matched to the samples' 835s.</summary>
public sealed class MatchingTests
{
    public static readonly DateOnly Today = new(2026, 11, 24);

    public static IReadOnlyList<RemitFile> Remits() =>
        [Samples.Load("harborview-835-1110.x12"), Samples.Load("harborview-835-1117.x12"), Samples.Load("tallgrass-835-1112.x12")];

    public static List<BilledClaim> Billed() =>
    [
        .. ClaimsReader.Read(Core.X12.X12File.Load(Samples.Path("harborview-837p-1101.x12")), Samples.Path("harborview-837p-1101.x12")).Claims,
        .. ClaimsReader.Read(Core.X12.X12File.Load(Samples.Path("tallgrass-837p-0930.x12")), Samples.Path("tallgrass-837p-0930.x12")).Claims,
        .. ClaimsReader.Read(Core.X12.X12File.Load(Samples.Path("tallgrass-837p-1101.x12")), Samples.Path("tallgrass-837p-1101.x12")).Claims,
    ];

    public static Reconciliation Reconcile() => Matcher.Match(Billed(), Remits().SelectMany(f => f.Claims), Today);

    [Fact]
    public void The_837s_are_read_as_billed_claims()
    {
        var billed = Billed();
        Assert.Equal(16, billed.Count);
        var c = billed.Single(b => b.Account == "RB26102");
        Assert.Equal("HVHP1", c.PayerId);
        Assert.Equal("CLAIRE BRENNAN", c.Name);
        Assert.Equal("HV800100344", c.MemberId);
        Assert.Equal(new DateOnly(2026, 11, 1), c.Billed);
        Assert.Equal(["99214", "85025"], c.Lines.Select(l => l.Code));
        Assert.Equal("RB26102-2", c.Lines[1].ControlNumber);
        Assert.Equal(260m, c.Charge);
        Assert.Equal(["97110-GP", "97161-GP"], billed.Single(b => b.Account == "RB26114").Lines.Select(l => l.Code));
        Assert.Equal(4m, billed.Single(b => b.Account == "RB26114").Lines[0].Units);
    }

    [Fact]
    public void Each_billed_claim_says_where_it_stands()
    {
        var r = Reconcile();
        Assert.Equal(
            "RB26101:paid RB26102:paid RB26103:denied RB26104:partly paid RB26105:paid RB26107:paid twice RB26108:patient owes RB26106:paid RB26116:no answer "
            + "RB26117:no answer RB26110:paid RB26111:denied RB26112:denied RB26113:paid RB26114:partly paid RB26115:paid",
            string.Join(" ", r.Claims.Select(m => $"{m.Billed.Account}:{MatchWords.State(m.State)}")));
        Assert.Empty(r.Unbilled);
        Assert.All(r.Claims.Where(m => m.Answers.Count > 0), m => Assert.Equal("account", m.By));
    }

    [Fact]
    public void A_reversal_a_split_and_a_second_payment_are_told_apart()
    {
        var r = Reconcile();
        string Say(string account) => MatchWords.Describe(r.Claims.Single(m => m.Billed.Account == account), r);
        Assert.Equal("Billed $300.00 on 2026-11-01; paid $150.00 on 2026-11-10, reversed on 2026-11-17 (−$150.00), paid again $162.00 on 2026-11-17. In all $162.00 paid, $30.00 the patient's, $108.00 adjusted off.", Say("RB26105"));
        Assert.Equal("Billed $320.00 on 2026-11-01; paid $180.00 on 2026-11-17, paid $48.00 on 2026-11-17: the payer split it into 2 claims. In all $228.00 paid, $92.00 adjusted off.", Say("RB26106"));
        Assert.Equal("Billed $150.00 on 2026-11-01; paid $85.00 on 2026-11-10, paid $85.00 on 2026-11-17. In all $170.00 paid, $50.00 the patient's, $80.00 adjusted off. The payer paid it twice: $85.00 too much. Expect it to be taken back, or refund it.", Say("RB26107"));
        Assert.Equal("Billed $220.00 on 2026-11-01; no answer in the open 835s after 23 days.", Say("RB26116"));
        Assert.Equal("Billed $150.00 on 2026-09-30; no answer in the open 835s after 55 days.", Say("RB26117"));
    }

    [Fact]
    public void Each_line_billed_is_matched_to_what_paid_it()
    {
        var r = Reconcile();
        string Lines(string account) => string.Join(" | ", r.Claims.Single(m => m.Billed.Account == account).Lines.Select(MatchWords.Describe));
        Assert.Equal("1. 99214 on 2026-10-23, $220.00: denied. | 2. 20610 on 2026-10-23, $180.00: paid $96.00.", Lines("RB26104"));
        Assert.Equal("1. 99214 on 2026-10-21, $220.00: paid $88.10. | 2. 36415 on 2026-10-21, $15.00: bundled. | 3. 85025 on 2026-10-21, $40.00: paid $8.50.", Lines("RB26113"));
        Assert.Equal("1. 99213 on 2026-10-27, $150.00: paid twice $170.00 (in 2 payments).", Lines("RB26107"));
        Assert.Equal("1. 99203 on 2026-10-28, $200.00: patient owes.", Lines("RB26108"));
    }

    [Fact]
    public void A_charges_list_matches_the_same_way()
    {
        var list = ChargesList.Load(Samples.Path("charges/charges.csv"));
        Assert.True(ChargesList.LooksLikeOne(Samples.Path("charges/charges.csv")));
        Assert.Equal(16, list.Claims.Count);
        Assert.Empty(list.Problems);
        var r = Matcher.Match(list.Claims, Remits().SelectMany(f => f.Claims), Today);
        Assert.Equal(Reconcile().Claims.ToDictionary(m => m.Billed.Account, m => m.State), r.Claims.ToDictionary(m => m.Billed.Account, m => m.State));
    }

    [Fact]
    public void Without_the_account_a_claim_is_matched_by_patient_date_and_charge()
    {
        var billed = Billed().Single(b => b.Account == "RB26101");
        var renamed = new BilledClaim { Source = "x", Account = "OTHER1", PayerId = billed.PayerId, Payer = billed.Payer, Last = billed.Last, First = billed.First, MemberId = billed.MemberId, Billed = billed.Billed };
        renamed.Lines.AddRange(billed.Lines);
        var r = Matcher.Match([renamed], Remits().SelectMany(f => f.Claims), Today);
        var m = Assert.Single(r.Claims);
        Assert.Equal("patient, date and charge", m.By);
        Assert.Equal(BilledState.Paid, m.State);
        Assert.Contains(r.Unbilled, u => u.Account == "RB26102");
    }
}
