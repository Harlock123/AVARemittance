using AVARemittance.Core.Codes;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>Reading the samples' 835s: payments, claims, lines, adjustments and remarks, and the code lists.</summary>
public sealed class ReaderTests
{
    [Fact]
    public void A_payment_has_its_money_trace_payer_and_payee()
    {
        var f = Samples.Load("harborview-835-1110.x12");
        var p = Assert.Single(f.Payments);
        Assert.Equal(471m, p.Total);
        Assert.Equal("ACH", p.Method);
        Assert.Equal("1HV2611100001", p.Trace);
        Assert.Equal(new DateOnly(2026, 11, 10), p.PaidOn);
        Assert.Equal(new DateOnly(2026, 11, 7), p.Produced);
        Assert.Equal("HARBORVIEW HEALTH PLAN", p.Payer.Name);
        Assert.Equal("RIVERBEND FAMILY MEDICINE", p.Payee.Name);
        Assert.Equal("1588667638", p.Payee.Id);
        Assert.Equal(["PROVIDER SERVICES phone 8005550190 web WWW.HARBORVIEW.EXAMPLE"], p.Payer.Contacts);
        Assert.Equal(["RB26101", "RB26102", "RB26103", "RB26104", "RB26105", "RB26107", "RB26108"], p.Claims.Select(c => c.Account));
        Assert.Equal("Paid $471.00 by EFT on 2026-11-10, trace 1HV2611100001, for 7 claims.", Words.Summary(p));
    }

    [Fact]
    public void A_claim_has_its_patient_lines_adjustments_and_remarks()
    {
        var c = Samples.Claim("harborview-835-1110.x12", "RB26102");
        Assert.Equal("CLAIRE BRENNAN", c.Patient!.Name);
        Assert.Equal("HV800100344", c.Patient.Id);
        Assert.Equal("ADAEZE OKONKWO", c.Rendering!.Name);
        Assert.Equal(2, c.Lines.Count);
        Assert.Equal(new DateOnly(2026, 10, 21), c.From);
        var visit = c.Lines[0];
        Assert.Equal("99214", visit.Code);
        Assert.Equal(140m, visit.Allowed);
        Assert.Equal(40m, visit.Paid);
        Assert.Equal("RB26102-1", visit.ControlNumber);
        Assert.Equal("CO-45 $80.00: Charge exceeds fee schedule/maximum allowable or contracted/legislated fee arrangement. (contractual obligations: the provider's to write off: not billable to the patient)", Words.Describe(visit.Adjustments[0]));
        Assert.Equal("PR-1 $100.00: Deductible Amount (patient responsibility: the patient's to pay)", Words.Describe(visit.Adjustments[1]));
        Assert.Equal("Paid $55.00 of $260.00; the patient owes $100.00; $105.00 adjusted off.", Words.Summary(c));

        var denied = Samples.Claim("harborview-835-1110.x12", "RB26103");
        Assert.Equal(ClaimState.Denied, denied.State);
        Assert.Equal("M62: Missing/incomplete/invalid treatment authorization code.", Words.Describe(denied.Lines[0].Remarks[0]));
    }

    [Fact]
    public void Each_sample_claim_says_what_happened_to_it()
    {
        string States(string file) => string.Join(" ", Samples.Load(file).Claims.Select(c => $"{c.Account}:{Words.State(c.State)}"));
        Assert.Equal("RB26101:paid RB26102:paid RB26103:denied RB26104:partly denied RB26105:paid RB26107:paid RB26108:patient owes", States("harborview-835-1110.x12"));
        Assert.Equal("RB26105:reversal RB26105:paid RB26106:paid RB26106:paid RB26107:paid", States("harborview-835-1117.x12"));
        Assert.Equal("RB26110:paid RB26111:denied RB26112:denied RB26113:partly denied RB26114:partly denied RB26115:paid", States("tallgrass-835-1112.x12"));
    }

    [Fact]
    public void A_reversal_a_correction_a_split_claim_and_the_provider_adjustments_are_read()
    {
        var p = Samples.Load("harborview-835-1117.x12").Payments[0];
        var reversal = p.Claims[0];
        Assert.True(reversal.IsReversal);
        Assert.Equal(-150m, reversal.Paid);
        Assert.Equal("Takes back $150.00 paid earlier on this claim (payer claim HV26110500005).", Words.Summary(reversal));
        Assert.Equal("HV26110500005", p.Claims[1].Original);
        Assert.Equal(["HV26111200042", "HV26111200043"], p.Claims.Where(c => c.Account == "RB26106").Select(c => c.PayerClaim));
        Assert.Equal(2, p.ProviderAdjustments.Count);
        Assert.Equal("WO $75.00 Overpayment Recovery (reference HV26090300088): taken from this payment", Words.Describe(p.ProviderAdjustments[0]));
        Assert.Equal("L6 −$3.12 Interest Owed (reference HV26111200031): added to this payment", Words.Describe(p.ProviderAdjustments[1]));
        Assert.Equal(253.12m, p.Total);
    }

    [Fact]
    public void A_check_payment_and_a_bundled_line()
    {
        var p = Samples.Load("tallgrass-835-1112.x12").Payments[0];
        Assert.True(p.IsCheck);
        Assert.Equal("40017731", p.Trace);
        var bundled = p.Claims.Single(c => c.Account == "RB26113").Lines[1];
        Assert.Equal("36415", bundled.Procedure);
        Assert.True(bundled.Denied);
        Assert.Equal("M15", bundled.Remarks[0].Code);
        var therapy = p.Claims.Single(c => c.Account == "RB26114").Lines[0];
        Assert.Equal("97110-GP", therapy.Code);
        Assert.Equal(4m, therapy.Units);
    }

    [Fact]
    public void The_code_lists_are_x12s_with_a_note_for_a_code_they_havent_got()
    {
        Assert.True(CodeList.Carc.Count >= 400);
        Assert.True(CodeList.Rarc.Count >= 1200);
        Assert.True(CodeList.Status.Count >= 790);
        Assert.Equal(56, CodeList.StatusCategory.Count);
        Assert.Equal("The time limit for filing has expired.", CodeList.Carc.Say("29"));
        Assert.Equal("Non-covered charge(s).", CodeList.Carc.Say("96"));
        Assert.Equal("(not in the X12 claim adjustment reason code list)", CodeList.Carc.Say("ZZ9"));
        Assert.Equal(new DateOnly(2006, 2, 1), CodeList.Carc["30"]!.Stop);
        Assert.Contains(CodeList.Rarc.Search("missing procedure modifier"), c => c.Code == "N822");
        Assert.Equal("Overpayment Recovery", RemitCodes.Say(RemitCodes.ProviderAdjustment, "WO"));
        Assert.Equal("code Q (not in the 835's list)", RemitCodes.Say(RemitCodes.Method, "Q"));
    }
}
