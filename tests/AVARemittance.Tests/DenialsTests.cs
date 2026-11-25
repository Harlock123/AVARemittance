using AVARemittance.Core.Denials;
using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>The denials worklist: what each denial asks for, its deadline, the drafts and the saved state.</summary>
public sealed class DenialsTests
{
    private static List<PayerLimits> Limits() => PayerLimits.Load(Samples.Path("contracts/payer-limits.json"));

    private static List<WorkItem> Build(IReadOnlyDictionary<string, SavedState>? saved = null) =>
        Worklist.Build(MatchingTests.Remits().SelectMany(f => f.Claims), Limits(), saved);

    [Fact]
    public void Every_denial_is_sorted_by_what_to_do_with_its_days_left()
    {
        Assert.True(PayerLimits.LooksLikeOne(Samples.Path("contracts/payer-limits.json")));
        Assert.Equal(
        [
            "W01 RB26114 appeal $160.00 (line 1 97110-GP): CO-50, N130. 48 days left (by 2027-01-11).",
            "W02 RB26103 fix and resubmit $1,200.00 (line 1 72148): CO-197, M62. 76 days left (by 2027-02-08).",
            "W03 RB26104 fix and resubmit $220.00 (line 1 99214): CO-4, N822. 76 days left (by 2027-02-08).",
            "W04 RB26111 bill elsewhere $220.00 (line 1 99214): CO-22, MA04. 168 days left (by 2027-05-11).",
            "W05 RB26108 bill the patient $130.00 (line 1 99203): PR-1.",
            "W06 RB26112 write off $150.00 (line 1 99213): CO-29. 48 days left (by 2027-01-11).",
            "W07 RB26113 bundled $15.00 (line 2 36415): CO-97, M15.",
        ], Build().Select(w => Worklist.Describe(w, MatchingTests.Today)));
    }

    [Fact]
    public void What_to_do_comes_from_the_codes()
    {
        var w = Build();
        Assert.Equal("Correct the claim (missing procedure modifier(s)) and send it again. Send it as a replacement claim (frequency 7), with the payer's claim number HV26110500004 in REF*F8.", w[2].WhatToDo);
        Assert.StartsWith("Add the authorization number (REF*G1) and send a corrected claim.", w[1].WhatToDo);
        Assert.StartsWith("Another payer comes first: bill the primary payer", w[3].WhatToDo);
        Assert.StartsWith("Filed too late: write it off. Appeal only with proof", w[5].WhatToDo);
        Assert.Equal("90 days to send a corrected claim, from the 835 of 2026-11-10 (invented for the samples)", w[1].DeadlineBecause);
        Assert.All(Worklist.Build(MatchingTests.Remits().SelectMany(f => f.Claims), []), x => Assert.Null(x.Deadline));
    }

    [Fact]
    public void Codes_point_to_actions_and_write_downs_make_no_work()
    {
        Adjustment A(string group, string reason) => new(group, reason, 10, null, new Core.X12.Segment("CAS", Core.X12.Delimiters.Standard));
        Assert.Null(Classifier.For(A("CO", "45"), []));
        Assert.Null(Classifier.For(A("PR", "2"), []));
        Assert.Equal(WorkAction.BillPatient, Classifier.For(A("PR", "96"), []));
        Assert.Equal(WorkAction.FixAndResubmit, Classifier.For(A("CO", "16"), ["N290"]));
        Assert.Equal(WorkAction.FixAndResubmit, Classifier.For(A("CO", "50"), ["MA130"]));
        Assert.Equal(WorkAction.WriteOff, Classifier.For(A("CO", "50"), ["N211"]));
        Assert.Equal(WorkAction.BillElsewhere, Classifier.For(A("CO", "109"), []));
        Assert.Equal(WorkAction.Review, Classifier.For(A("OA", "18"), []));
        Assert.Equal(WorkAction.Review, Classifier.For(A("CO", "999"), []));
    }

    [Fact]
    public void The_appeal_draft_says_its_a_draft_and_marks_what_a_person_adds()
    {
        var appeal = Build()[0];
        var md = Letters.Markdown(Letters.Appeal(appeal, MatchingTests.Today));
        Assert.StartsWith("> **DRAFT: check it against the payer's appeal policy", md);
        Assert.Contains("# Request for reconsideration: claim TG2611000005", md);
        Assert.Contains("Patient: LINH NGUYEN  \nMember ID: TG55056789", md);
        Assert.Contains("- CO-50: These are non-covered services because this is not deemed a 'medical necessity' by the payer.\n- N130: Consult plan benefit documents/guidelines for information about restrictions for this service.\n\nThe care was medically necessary. [Say why", md);
        var masked = Letters.Markdown(Letters.Appeal(appeal, MatchingTests.Today, s => new string('*', s.Length)));
        Assert.DoesNotContain("NGUYEN", masked);
        Assert.DoesNotContain("TG55056789", masked);

        var dir = Directory.CreateTempSubdirectory("appeal-").FullName;
        foreach (var ext in new[] { ".md", ".html", ".docx" }) Letters.WriteAppeal(Path.Combine(dir, "a" + ext), appeal, MatchingTests.Today);
        Assert.Contains("<p class=\"draft\">DRAFT:", File.ReadAllText(Path.Combine(dir, "a.html")));
        using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(Path.Combine(dir, "a.docx"), false);
        Assert.Contains("Request for reconsideration", word.MainDocumentPart!.Document!.Body!.InnerText);

        Assert.Equal(
        [
            "Corrected claim for RB26104 (HARBORVIEW HEALTH PLAN, payer claim HV26110500004):",
            "- CLM05-3, the claim frequency: 7 (replacement of prior claim).",
            "- REF*F8, the original reference number: HV26110500004.",
            "- N822: Missing procedure modifier(s). Add the modifier the line needs (25 on a visit separate from a same-day procedure, if the notes support it).",
            "- Everything else as first billed. AVA Claim Scrubber can check the corrected 837 before it goes.",
        ], Letters.Corrected(Build()[2]));
    }

    [Fact]
    public void What_was_done_is_saved_beside_the_835s_and_read_back()
    {
        var folder = Samples.Copy();
        var items = Worklist.Build(RemitReader.Load(Path.Combine(folder, "tallgrass-835-1112.x12")).Claims, Limits());
        var appeal = items.Single(i => i.Action == WorkAction.Appeal);
        appeal.State = WorkState.Appealed;
        appeal.StateDate = MatchingTests.Today;
        appeal.Note = "sent with the PT notes";
        WorkStore.Save(folder, appeal);
        var again = Worklist.Build(RemitReader.Load(Path.Combine(folder, "tallgrass-835-1112.x12")).Claims, Limits(), WorkStore.Load(folder));
        var read = again.Single(i => i.Key == appeal.Key);
        Assert.Equal(WorkState.Appealed, read.State);
        Assert.Equal("sent with the PT notes", read.Note);
        Assert.Same(read, again[^1]);
        Assert.Contains("\"Appealed\"", File.ReadAllText(Path.Combine(folder, WorkStore.FileName)));
    }
}
