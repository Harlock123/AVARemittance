using Avalonia.Controls;
using Avalonia.LogicalTree;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>The window: opening 835s, the list by payment and claim, a claim in words, its segments, and the checks.</summary>
public sealed class WindowTests
{
    [Fact]
    public void Opening_an_835_lists_its_payment_and_claims_marked() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            w.Open(Samples.Path("harborview-835-1110.x12"));
            Assert.Equal("Opened harborview-835-1110.x12: 1 payment from HARBORVIEW HEALTH PLAN: $471.00 for 7 claims.", w.txtStatusBar.Text);
            var entries = w.FileList.Items.OfType<ListEntry>().ToList();
            Assert.Equal(9, entries.Count);
            Assert.Equal(["paid", "paid", "denied", "partly denied", "paid", "paid", "patient owes"], entries.Where(e => e.Claim != null).Select(e => w.Mark(e.Claim!).Word));
            Assert.Contains("Yes: the claims less the provider adjustments come to the payment, and every claim and line balances.", Texts(w));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void A_claim_shows_in_words_and_a_line_highlights_its_segments() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            w.Open(Samples.Path("harborview-835-1110.x12"));
            var claim = w.OpenFiles[0].Claims.Single(c => c.Account == "RB26104");
            w.FileList.SelectedItem = w.FileList.Items.OfType<ListEntry>().Single(e => e.Claim == claim);
            HeadlessApp.Settle();
            Assert.Same(claim, w.CurrentClaim);
            var texts = Texts(w);
            Assert.Contains("RB26104  ELENA DUBOIS", texts);
            Assert.Contains("Processed as Primary. Paid $96.00 of $400.00; the patient owes $24.00; $280.00 adjusted off.", texts);
            Assert.Contains("1. 99214 on 2026-10-23: billed $220.00, paid $0.00", texts);
            Assert.Contains("N822: Missing procedure modifier(s).", texts);
            w.Highlight([.. claim.Lines[0].Segments.Select(s => s.Index)]);
            Assert.StartsWith("SVC*HC:99214*220*0**1~DTM*472*20261023~CAS*CO*4*220~", w.Highlighted);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void A_folder_opens_every_835_and_the_checks_say_which_balance() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            w.Open(Samples.Folder);
            Assert.Equal(["harborview-835-1110.x12", "harborview-835-1117.x12", "tallgrass-835-1112.x12"], w.OpenFiles.Select(f => f.Name));
            Assert.Empty(w.Findings);
            w.Open(Samples.Path("problems/unbalanced-835.x12"));
            Assert.EndsWith("It doesn't balance: 4 problems (see Checks).", w.txtStatusBar.Text);
            Assert.Equal(5, w.Findings.Count);
            Assert.Contains("B1 The payment is $100.00, but the claims pay $62.40: $37.60 less than was sent.", Texts(w));
            Assert.Contains("Balances: 1 payment from TALLGRASS MEDICAID: $296.15 for 6 claims.", Texts(w, w.ChecksPanel));
            Assert.True(w.CloseFile(w.OpenFiles[^1]));
            Assert.Empty(w.Findings);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void A_payment_shows_its_provider_adjustments() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            w.Open(Samples.Path("harborview-835-1117.x12"));
            var texts = Texts(w);
            Assert.Contains("Paid $253.12 by EFT on 2026-11-17, trace 1HV2611170002, for 5 claims.", texts);
            Assert.Contains("WO $75.00 Overpayment Recovery (reference HV26090300088): taken from this payment", texts);
            Assert.Contains("Claims pay $325.00; less $71.88 of provider adjustments, $253.12.", texts);
            Assert.Contains("RB26105  JORDAN ELLIS: reversal. Takes back $150.00 paid earlier on this claim (payer claim HV26110500005).", texts);
        }
        finally { w.Close(); }
    });

    internal static List<string> Texts(MainWindow w, Control? within = null) =>
        [.. (within ?? w.RemitPanel).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
}
