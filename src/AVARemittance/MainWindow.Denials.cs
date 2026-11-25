using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Reports;

namespace AVARemittance;

/// <summary>
/// The Denials tab: every denial in the open 835s as a work item, grouped by what to do, with the days left by the
/// payer's limits; a chosen item's reasons and what to do, the buttons that record what was done (kept in
/// remittance-work.json beside the 835s), an appeal letter draft and a corrected claim's changes.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<PayerLimits> _limits = [];
    private string _limitsFile = "";

    public ListBox WorkList { get; } = new() { Background = Look.Bar };
    public StackPanel WorkSummary { get; } = new() { Margin = new Thickness(20, 14, 20, 6), Spacing = 4 };
    public StackPanel WorkDetail { get; } = new() { Margin = new Thickness(20, 10), Spacing = 4 };
    public IReadOnlyList<WorkItem> Work { get; private set; } = [];
    public IReadOnlyList<PayerLimits> Limits => _limits;
    public WorkItem? CurrentWork => WorkList.SelectedItem as WorkItem;

    private TabItem DenialsTab()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,2*,4,3*") };
        grid.Children.Add(WorkSummary);
        Grid.SetRow(WorkList, 1);
        grid.Children.Add(WorkList);
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 2);
        grid.Children.Add(split);
        var detail = new ScrollViewer { Content = WorkDetail };
        Grid.SetRow(detail, 3);
        grid.Children.Add(detail);
        WorkList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<WorkItem>((w, _) => w == null ? new TextBlock() : WorkLabel(w));
        WorkList.SelectionChanged += (_, _) => { if (WorkList.SelectedItem is WorkItem w) ShowWork(w); };
        return new TabItem { Header = "Denials", Content = grid, FontSize = 14 };
    }

    private static bool IsLimits(string path) => PayerLimits.LooksLikeOne(path);

    private void OpenLimits(string path)
    {
        try
        {
            var loaded = PayerLimits.Load(path);
            _limits.Clear();
            _limits.AddRange(loaded);
            _limitsFile = System.IO.Path.GetFileName(path);
            Rework();
            Say($"Opened {_limitsFile}: limits for {loaded.Count} payer{(loaded.Count == 1 ? "" : "s")}; {Work.Count(w => w.Deadline != null)} denial{(Work.Count(w => w.Deadline != null) == 1 ? "" : "s")} now have a deadline.");
        }
        catch (System.Text.Json.JsonException e)
        {
            Say($"Couldn't read {System.IO.Path.GetFileName(path)} as payers' limits: {e.Message}");
        }
    }

    /// <summary>Builds the worklist again, with each 835 folder's saved state.</summary>
    private void Rework()
    {
        var saved = new Dictionary<string, SavedState>();
        foreach (var folder in _open.Select(f => System.IO.Path.GetDirectoryName(f.Path)!).Distinct())
            foreach (var (k, v) in WorkStore.Load(folder)) saved[k] = v;
        var chosen = CurrentWork?.Key;
        Work = Worklist.Build(_open.SelectMany(f => f.Claims), _limits, saved);
        WorkList.ItemsSource = Work.ToList();
        FillWorkSummary();
        if (Work.FirstOrDefault(w => w.Key == chosen) is { } again) WorkList.SelectedItem = again;
        else
        {
            WorkDetail.Children.Clear();
            if (Work.Count > 0) WorkDetail.Children.Add(Dim("Choose a denial to see why, what to do, and to record what was done."));
        }
    }

    private void FillWorkSummary()
    {
        WorkSummary.Children.Clear();
        if (_open.Count == 0)
        {
            WorkSummary.Children.Add(Heading("Denials"));
            WorkSummary.Children.Add(Dim("Open 835s to list every denial in them, sorted by what to do. Open the payers' limits (a JSON file of appeal_days, corrected_claim_days and timely_filing_days per payer) to count the days left."));
            return;
        }
        var open = Work.Where(w => !w.Done && w.Action != WorkAction.Bundled).ToList();
        WorkSummary.Children.Add(Heading($"{open.Count} denial{(open.Count == 1 ? "" : "s")} to work, {Money.Say(open.Sum(w => w.Amount))} at stake"));
        var groups = Enum.GetValues<WorkAction>().Select(a => (a, items: Work.Where(w => !w.Done && w.Action == a).ToList())).Where(g => g.items.Count > 0)
            .Select(g => $"{Classifier.Say(g.a)} {g.items.Count} ({Money.Say(g.items.Sum(i => i.Amount))})");
        WorkSummary.Children.Add(new TextBlock { Text = string.Join(" · ", groups) + (Work.Any(w => w.Done) ? $" · done {Work.Count(w => w.Done)}" : ""), Foreground = Look.Text, TextWrapping = TextWrapping.Wrap });
        var soon = open.Where(w => w.DaysLeft(Today()) is { } d && d <= 14).ToList();
        if (soon.Count > 0) WorkSummary.Children.Add(new TextBlock { Text = $"{soon.Count} due within two weeks: {string.Join(", ", soon.Select(w => w.Id))}.", Foreground = Look.Deny });
        if (_limits.Count == 0) WorkSummary.Children.Add(Dim("No payers' limits open: no deadlines counted."));
        else WorkSummary.Children.Add(Dim($"Deadlines from {_limitsFile}."));
    }

    internal static IBrush Brush(WorkAction a) => a switch
    {
        WorkAction.FixAndResubmit or WorkAction.Appeal => Look.Deny,
        WorkAction.BillElsewhere or WorkAction.BillPatient => Look.Warn,
        WorkAction.Review => Look.Accent,
        _ => Look.Dim,
    };

    private Control WorkLabel(WorkItem w)
    {
        var row = new DockPanel { Opacity = w.Done ? 0.55 : 1 };
        var left = w.DaysLeft(Today());
        var when = w.Done ? Classifier.Say(w.State) : left is { } d ? d < 0 ? $"{-d} days late" : $"{d} days left" : "";
        var badge = new TextBlock { Text = when, Foreground = left is < 15 && !w.Done ? Look.Reject : Look.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);
        var action = new TextBlock { Text = Classifier.Say(w.Action), Foreground = Brush(w.Action), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 110 };
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);
        var amount = new TextBlock { Text = Money.Say(w.Amount), Foreground = Look.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(amount, Dock.Right);
        row.Children.Add(amount);
        row.Children.Add(new TextBlock { Text = $"{w.Id}  {w.Claim.Account}  {w.Claim.Patient?.SortName}  ·  {string.Join(", ", w.Reasons.Select(r => $"{r.Group}-{r.Reason}").Distinct())}", Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    /// <summary>Shows a work item: why, what to do, the deadline, and the buttons.</summary>
    public void ShowWork(WorkItem w)
    {
        WorkDetail.Children.Clear();
        var c = w.Claim;
        WorkDetail.Children.Add(new TextBlock { Text = $"{w.Id}  {c.Account}  {c.Patient?.Name}: {Classifier.Say(w.Action)}, {Money.Say(w.Amount)}", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap });
        WorkDetail.Children.Add(Dim($"{c.Payment.Payer.Name} · payer claim {c.PayerClaim} · {c.Payment.File.Name}" + (w.Lines.Count > 0 ? $" · line{(w.Lines.Count == 1 ? "" : "s")} {string.Join(", ", w.Lines.Select(l => $"{l.Number} {Words.Code(l)}"))}" : " · the whole claim")));
        foreach (var r in w.Reasons.DistinctBy(r => (r.Group, r.Reason))) WorkDetail.Children.Add(new TextBlock { Text = $"{r.Group}-{r.Reason}: {CodeList.Carc.Say(r.Reason)}", Foreground = Look.Deny, TextWrapping = TextWrapping.Wrap });
        foreach (var r in w.Remarks) WorkDetail.Children.Add(new TextBlock { Text = $"{r.Code}: {CodeList.Rarc.Say(r.Code)}", Foreground = Look.Dim, TextWrapping = TextWrapping.Wrap });
        WorkDetail.Children.Add(Section("What to do"));
        WorkDetail.Children.Add(new TextBlock { Text = w.WhatToDo, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap });
        if (w.Deadline is { } d) WorkDetail.Children.Add(new TextBlock { Text = $"By {Money.Day(d)}: {w.DeadlineBecause}.", Foreground = w.DaysLeft(Today()) is < 15 ? Look.Reject : Look.Warn, TextWrapping = TextWrapping.Wrap });
        else if (w.DeadlineBecause.Length > 0) WorkDetail.Children.Add(Dim($"No deadline: {w.DeadlineBecause}."));
        if (w.Done) WorkDetail.Children.Add(new TextBlock { Text = $"Done: {Classifier.Say(w.State)}" + (w.StateDate is { } s ? $" on {Money.Day(s)}" : "") + (w.Note.Length > 0 ? $". {w.Note}" : "."), Foreground = Look.Clean, TextWrapping = TextWrapping.Wrap });
        ShowingWork(w);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Add(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
        }
        if (w.Action == WorkAction.Appeal || w.Action == WorkAction.FixAndResubmit && w.Reasons.Any(r => r.Reason == "197") || w.Action == WorkAction.WriteOff && w.Reasons.Any(r => r.Reason == "29"))
            Add("Draft the Appeal Letter…", () => _ = DraftAppeal(w));
        if (w.Action == WorkAction.FixAndResubmit) Add("Corrected Claim Changes…", () => _ = WriteCorrected(w));
        if (!w.Done)
        {
            var (state, word) = w.Action switch
            {
                WorkAction.Appeal => (WorkState.Appealed, "Mark Appealed"),
                WorkAction.FixAndResubmit => (WorkState.Resubmitted, "Mark Resubmitted"),
                WorkAction.BillElsewhere => (WorkState.BilledElsewhere, "Mark Billed Elsewhere"),
                WorkAction.BillPatient => (WorkState.BilledPatient, "Mark Billed to the Patient"),
                _ => (WorkState.WrittenOff, "Mark Written Off"),
            };
            Add(word, () => SetWorkState(w, state));
            if (state != WorkState.WrittenOff) Add("Mark Written Off", () => SetWorkState(w, WorkState.WrittenOff));
        }
        else Add("Reopen", () => SetWorkState(w, WorkState.Open));
        WorkDetail.Children.Add(buttons);
        var go = new Button { Content = "Show the Claim in the Remittance", Background = Brushes.Transparent, Foreground = Look.Accent };
        go.Click += (_, _) => { ShowClaim(c); Tabs.SelectedIndex = 0; };
        WorkDetail.Children.Add(go);
    }

    partial void ShowingWork(WorkItem w);

    /// <summary>Records what was done with an item (today), in remittance-work.json beside its 835.</summary>
    public void SetWorkState(WorkItem w, WorkState state, string note = "")
    {
        w.State = state;
        w.StateDate = state == WorkState.Open ? null : Today();
        if (note.Length > 0) w.Note = note;
        if (state == WorkState.Open) w.Note = "";
        try
        {
            WorkStore.Save(System.IO.Path.GetDirectoryName(w.Claim.Payment.File.Path)!, w);
            Say($"{w.Id} {w.Claim.Account}: {Classifier.Say(state)}" + (state == WorkState.Open ? "." : $" on {Money.Day(w.StateDate)}, saved in {WorkStore.FileName}."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save {WorkStore.FileName} beside {w.Claim.Payment.File.Name}: {e.Message}");
        }
        Rework();
    }

    private async Task DraftAppeal(WorkItem w)
    {
        var path = await PickSaveFile("Save the appeal draft (.docx, .html or .md)", $"appeal-{w.Claim.Account}.docx");
        if (path == null) return;
        Letters.WriteAppeal(path, w, Today());
        Say($"Wrote the appeal draft to {System.IO.Path.GetFileName(path)}. It's a draft: fill in every [bracket] and check it before it goes anywhere.");
    }

    private async Task WriteCorrected(WorkItem w)
    {
        var path = await PickSaveFile("Save the corrected claim's changes", $"corrected-{w.Claim.Account}.txt");
        if (path == null) return;
        File.WriteAllText(path, string.Join("\n", Letters.Corrected(w)) + "\n");
        Say($"Wrote what to change for {w.Claim.Account}'s corrected claim to {System.IO.Path.GetFileName(path)}.");
    }

    internal Task DraftAppealForTests(WorkItem w) => DraftAppeal(w);
    internal Task WriteCorrectedForTests(WorkItem w) => WriteCorrected(w);
    internal Task ExportDenialsForTests() => ExportDenials();

    private async Task ExportDenials()
    {
        if (Work.Count == 0)
        {
            Say("No denials in the open 835s to export.");
            return;
        }
        var path = await PickSaveFile("Export the denials (.html or .xlsx)", "denials.html");
        if (path == null) return;
        DenialsReport.Write(path, Work, Today());
        Say($"Wrote the denials to {System.IO.Path.GetFileName(path)}.");
    }
}
