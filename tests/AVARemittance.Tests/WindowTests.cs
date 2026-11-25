using Avalonia.Controls;
using Avalonia.LogicalTree;
using AVARemittance.Core.Matching;
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

/// <summary>The Claims tab: billed claims matched to the open 835s.</summary>
public sealed class ClaimsTabTests
{
    [Fact]
    public void The_samples_folder_matches_every_billed_claim() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow { Today = () => MatchingTests.Today };
        w.Show();
        try
        {
            w.Open(Samples.Folder);
            Assert.Equal(3, w.BilledFiles.Count);
            Assert.Equal(16, w.Reconciled.Claims.Count);
            var summary = WindowTests.Texts(w, w.ClaimSummary);
            Assert.Contains("16 claims billed for $4,595.00", summary);
            Assert.Contains("7 paid, 2 partly paid, 3 denied, 1 patient owes, 2 no answer, 1 paid twice. Paid $1,092.15; the patients' $363.00.", summary);
            w.ClaimList.SelectedItem = w.Reconciled.Claims.Single(m => m.Billed.Account == "RB26107");
            HeadlessApp.Settle();
            Assert.Contains("1. 99213 on 2026-10-27, $150.00: paid twice $170.00 (in 2 payments).", WindowTests.Texts(w, w.ClaimDetail));
            var second = w.OpenFiles.Single(f => f.Name == "harborview-835-1117.x12").Claims.Single(c => c.Account == "RB26107");
            Assert.Equal("paid twice", w.Mark(second).Word);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void A_charges_list_opens_and_closing_an_835_matches_again() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow { Today = () => MatchingTests.Today };
        w.Show();
        try
        {
            w.Open(Samples.Path("harborview-835-1110.x12"));
            w.Open(Samples.Path("charges/charges.csv"));
            Assert.Equal("Opened charges.csv: charges list, 16 claims billed for $4,595.00; 7 answered in the open 835s.", w.txtStatusBar.Text);
            Assert.Equal(BilledState.Paid, w.Reconciled.Claims.Single(m => m.Billed.Account == "RB26105").State);
            Assert.True(w.CloseFile(w.OpenFiles[0]));
            Assert.All(w.Reconciled.Claims, m => Assert.Equal(BilledState.NoAnswer, m.State));
        }
        finally { w.Close(); }
    });
}

/// <summary>The Deposits tab and the export.</summary>
public sealed class DepositsTabTests
{
    [Fact]
    public void A_statement_opens_and_the_tab_says_what_matched() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow { Today = () => MatchingTests.Today };
        w.Show();
        try
        {
            w.Open(Samples.Folder);
            w.Open(Samples.Path("bank/statement-2026-11.bai"));
            Assert.Equal("Opened statement-2026-11.bai: BAI2 statement, 5 deposits for $1,777.77; 3 matched to the open 835s.", w.txtStatusBar.Text);
            var texts = WindowTests.Texts(w, w.DepositsPanel);
            Assert.Contains("3 deposits matched to 3 payments", texts);
            Assert.Contains("1 insurance deposit with no 835, 0 835 payments with no deposit, 0 that differ.", texts);
            Assert.Contains("$412.50 on 2026-11-24: ACH CREDIT HARBORVIEW HLTH HCCLAIMPMT RIVERBEND FAMILY TRN*1*1HV2611240003*1860000001\\. Its trace is 1HV2611240003: ask the payer, or the clearinghouse, for that 835.", texts);
            var path = Path.Combine(Directory.CreateTempSubdirectory("export-").FullName, "posting.xlsx");
            w.PickSaveFile = (_, _) => Task.FromResult<string?>(path);
            HeadlessApp.Wait(w.ExportPostingForTests());
            Assert.True(File.Exists(path));
            Assert.Equal("Wrote the posting summary to posting.xlsx.", w.txtStatusBar.Text);
            w.CloseFile(w.OpenFiles.Single(f => f.Name == "tallgrass-835-1112.x12"));
            Assert.Contains("TALLGRASS MEDICAID", string.Join(" ", w.Banked.Other.Concat(w.Banked.NoRemittance).Select(d => d.Description)));
        }
        finally { w.Close(); }
    });
}

/// <summary>The Underpayments tab.</summary>
public sealed class UnderpaymentsTabTests
{
    [Fact]
    public void A_fee_schedule_opens_and_the_tolerance_changes_what_is_flagged() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            w.Open(Samples.Folder);
            w.Open(Samples.Path("contracts/fee-schedule.csv"));
            Assert.Equal("Opened fee-schedule.csv: a fee schedule of 19 rates; 3 lines in the open 835s below it.", w.txtStatusBar.Text);
            Assert.Contains("$27.90 below the schedule on 3 lines", WindowTests.Texts(w, w.UnderPanel));
            Assert.Contains("HARBORVIEW HEALTH PLAN 99396: $15.00 below, on 1 line.", WindowTests.Texts(w, w.UnderPanel));
            w.SetTolerance("5");
            Assert.Equal(5m, AppSettings.Load().Tolerance);
            Assert.Contains("$25.00 below the schedule on 2 lines", WindowTests.Texts(w, w.UnderPanel));
            w.SetTolerance("lots");
            Assert.Equal("\"lots\" isn't an amount of dollars; the tolerance stays $5.00.", w.txtStatusBar.Text);
            w.SetTolerance("1");
        }
        finally { w.Close(); }
    });
}

/// <summary>The Denials tab: the worklist, recording what was done, the drafts and the export.</summary>
public sealed class DenialsTabTests
{
    [Fact]
    public void The_worklist_fills_marks_what_was_done_and_writes_the_drafts() => HeadlessApp.Run(() =>
    {
        var folder = Samples.Copy();
        var w = new MainWindow { Today = () => MatchingTests.Today };
        w.Show();
        try
        {
            w.Open(folder);
            w.Open(Path.Combine(folder, "contracts", "payer-limits.json"));
            Assert.Equal("Opened payer-limits.json: limits for 2 payers; 5 denials now have a deadline.", w.txtStatusBar.Text);
            var summary = WindowTests.Texts(w, w.WorkSummary);
            Assert.Contains("6 denials to work, $2,080.00 at stake", summary);
            Assert.Contains("fix and resubmit 2 ($1,420.00) · appeal 1 ($160.00) · bill elsewhere 1 ($220.00) · bill the patient 1 ($130.00) · write off 1 ($150.00) · bundled 1 ($15.00)", summary);
            var appeal = w.Work[0];
            w.WorkList.SelectedItem = appeal;
            HeadlessApp.Settle();
            Assert.Contains("By 2027-01-11: 60 days to appeal, from the 835 of 2026-11-12 (invented for the samples).", WindowTests.Texts(w, w.WorkDetail));
            var draft = Path.Combine(folder, "appeal.docx");
            w.PickSaveFile = (_, _) => Task.FromResult<string?>(draft);
            HeadlessApp.Wait(w.DraftAppealForTests(appeal));
            Assert.True(File.Exists(draft));
            w.SetWorkState(appeal, Core.Denials.WorkState.Appealed);
            Assert.Equal("W01 RB26114: appealed on 2026-11-24, saved in remittance-work.json.", w.txtStatusBar.Text);
            Assert.Contains("5 denials to work, $1,920.00 at stake", WindowTests.Texts(w, w.WorkSummary));
            Assert.Equal(Core.Denials.WorkState.Appealed, w.Work.Single(x => x.Key == appeal.Key).State);
            var report = Path.Combine(folder, "denials.xlsx");
            w.PickSaveFile = (_, _) => Task.FromResult<string?>(report);
            HeadlessApp.Wait(w.ExportDenialsForTests());
            Assert.True(File.Exists(report));
        }
        finally { w.Close(); }
    });
}
