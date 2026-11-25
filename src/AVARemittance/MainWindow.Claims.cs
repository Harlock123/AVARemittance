using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.X12;

namespace AVARemittance;

/// <summary>
/// The Claims tab: what was billed (837s or a charges list) matched to the open 835s, each claim marked paid, partly
/// paid, denied, patient owes, reversed, paid twice or no answer (with the days since it was billed), and the 835
/// claims nothing open billed.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<BilledFile> _billed = [];

    public ListBox ClaimList { get; } = new() { Background = Look.Bar };
    public StackPanel ClaimSummary { get; } = new() { Margin = new Thickness(20, 14, 20, 6), Spacing = 4 };
    public StackPanel ClaimDetail { get; } = new() { Margin = new Thickness(20, 10), Spacing = 4 };

    public IReadOnlyList<BilledFile> BilledFiles => _billed;

    /// <summary>The billed claims matched to the open 835s; empty with nothing billed open.</summary>
    public Reconciliation Reconciled { get; private set; } = new();

    /// <summary>The day "days since billed" is counted to; tests fix it.</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Today);

    private TabItem ClaimsTab()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,2*,4,3*") };
        grid.Children.Add(ClaimSummary);
        Grid.SetRow(ClaimList, 1);
        grid.Children.Add(ClaimList);
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 2);
        grid.Children.Add(split);
        var detail = new ScrollViewer { Content = ClaimDetail };
        Grid.SetRow(detail, 3);
        grid.Children.Add(detail);
        ClaimList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ClaimMatch>((m, _) => m == null ? new TextBlock() : MatchLabel(m));
        ClaimList.SelectionChanged += (_, _) => { if (ClaimList.SelectedItem is ClaimMatch m) ShowMatch(m); };
        return new TabItem { Header = "Claims", Content = grid, FontSize = 14 };
    }

    private bool OpenBilledX12(string path, X12File x12)
    {
        if (!ClaimsReader.Is837(x12)) return false;
        AddBilled(ClaimsReader.Read(x12, path));
        return true;
    }

    private static bool IsChargesList(string path) => ChargesList.LooksLikeOne(path);

    private void OpenChargesList(string path) => AddBilled(ChargesList.Load(path));

    private void AddBilled(BilledFile f)
    {
        _billed.RemoveAll(b => b.Path == f.Path);
        _billed.Add(f);
        Rematch();
        var m = Reconciled.Claims.Where(c => c.Billed.Source == f.Name).ToList();
        Say($"Opened {f.Name}: {f.Kind}, {f.Claims.Count} claim{(f.Claims.Count == 1 ? "" : "s")} billed for {Money.Say(f.Claims.Sum(c => c.Charge))}; "
            + $"{m.Count(c => c.Answers.Count > 0)} answered in the open 835s." + (f.Problems.Count > 0 ? $" {f.Problems[0]}" : ""));
    }

    /// <summary>Closes a billed file.</summary>
    public bool CloseBilled(BilledFile f)
    {
        if (!_billed.Remove(f)) return false;
        Rematch();
        return true;
    }

    /// <summary>Matches again after anything opens or closes.</summary>
    private void Rematch()
    {
        Reconciled = Matcher.Match(_billed.SelectMany(b => b.Claims), _open.SelectMany(f => f.Claims), Today());
        var chosen = (ClaimList.SelectedItem as ClaimMatch)?.Billed.Account;
        ClaimList.ItemsSource = Reconciled.Claims.ToList();
        FillClaimSummary();
        var again = Reconciled.Claims.FirstOrDefault(c => c.Billed.Account == chosen);
        if (again != null) ClaimList.SelectedItem = again;
        else ShowMatchNothing();
        Rematched();
    }

    partial void Rematched();

    private void FillClaimSummary()
    {
        ClaimSummary.Children.Clear();
        var r = Reconciled;
        if (_billed.Count == 0)
        {
            ClaimSummary.Children.Add(Heading("What was billed"));
            ClaimSummary.Children.Add(Dim("Open the 837s you sent, or a charges list (a CSV or Excel file: Account, Payer ID, Last name, First name, Date of service, Procedure, Charge…), to match each claim to its payments."));
            return;
        }
        ClaimSummary.Children.Add(Heading($"{r.Claims.Count} claim{(r.Claims.Count == 1 ? "" : "s")} billed for {Money.Say(r.Claims.Sum(c => c.Billed.Charge))}"));
        var counts = Enum.GetValues<BilledState>().Select(s => (s, n: r.Claims.Count(c => c.State == s))).Where(x => x.n > 0).Select(x => $"{x.n} {MatchWords.State(x.s)}");
        ClaimSummary.Children.Add(new TextBlock { Text = string.Join(", ", counts) + $". Paid {Money.Say(r.Claims.Sum(c => c.Paid))}; the patients' {Money.Say(r.Claims.Sum(c => c.PatientOwes))}.", Foreground = Look.Text, TextWrapping = TextWrapping.Wrap });
        if (r.Unbilled.Count > 0)
            ClaimSummary.Children.Add(new TextBlock { Text = $"{r.Unbilled.Count} claim{(r.Unbilled.Count == 1 ? "" : "s")} in the 835s that nothing open billed: {string.Join(", ", r.Unbilled.Select(u => u.Account).Distinct())}.", Foreground = Look.Warn, TextWrapping = TextWrapping.Wrap });
    }

    internal static IBrush Brush(BilledState s) => s switch
    {
        BilledState.Paid => Look.Clean,
        BilledState.PatientOwes or BilledState.PartlyPaid => Look.Warn,
        BilledState.Denied or BilledState.Reversed => Look.Deny,
        BilledState.PaidTwice => Look.Reject,
        _ => Look.Accent,
    };

    private Control MatchLabel(ClaimMatch m)
    {
        var row = new DockPanel();
        var word = MatchWords.State(m.State) + (m.State == BilledState.NoAnswer && m.Billed.Billed != null ? $" · {Reconciled.DaysWaiting(m)} days" : "");
        var badge = new TextBlock { Text = word, Foreground = Brush(m.State), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);
        var money = new TextBlock { Text = $"{Money.Say(m.Paid)} of {Money.Say(m.Billed.Charge)}", Foreground = Look.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(money, Dock.Right);
        row.Children.Add(money);
        row.Children.Add(new TextBlock { Text = $"{m.Billed.Account}  {m.Billed.SortName}  ·  {m.Billed.Payer}", Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private void ShowMatchNothing()
    {
        ClaimDetail.Children.Clear();
        if (_billed.Count > 0) ClaimDetail.Children.Add(Dim("Choose a claim to see each line billed and what the payer did with it."));
    }

    /// <summary>Shows a billed claim: its answers in order, and each line billed against what paid it.</summary>
    public void ShowMatch(ClaimMatch m)
    {
        ClaimDetail.Children.Clear();
        var b = m.Billed;
        ClaimDetail.Children.Add(new TextBlock { Text = $"{b.Account}  {b.Name}", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        ClaimDetail.Children.Add(Dim($"{b.Payer}" + (b.MemberId.Length > 0 ? $" · member {b.MemberId}" : "") + $" · from {b.Source}" + (m.Answers.Count > 0 ? $" · matched by {m.By}" : "")));
        ClaimDetail.Children.Add(new TextBlock { Text = MatchWords.Describe(m, Reconciled), Foreground = Brush(m.State), TextWrapping = TextWrapping.Wrap });
        ShowingMatch(m);
        ClaimDetail.Children.Add(Section("Lines"));
        foreach (var l in m.Lines)
        {
            var brush = l.Word switch { "paid" => Look.Clean, "bundled" or "patient owes" => Look.Warn, "denied" or "not answered" or "reversed" => Look.Deny, _ => Look.Reject };
            ClaimDetail.Children.Add(new TextBlock { Text = MatchWords.Describe(l), Foreground = brush, TextWrapping = TextWrapping.Wrap });
            foreach (var a in l.Standing.SelectMany(s => s.Adjustments).Where(a => !ClaimPayment.IsContractual(a)))
                ClaimDetail.Children.Add(new TextBlock { Text = Words.Describe(a), Foreground = Look.Dim, Margin = new Thickness(18, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        if (m.Answers.Count > 0)
        {
            ClaimDetail.Children.Add(Section("The payer's answers"));
            foreach (var a in m.Answers)
            {
                var row = new Button { Content = $"{a.Payment.File.Name}: {Words.State(a.State)}, payer claim {a.PayerClaim}, {Words.Summary(a)}", Background = Brushes.Transparent, Foreground = Look.Accent, Padding = new Thickness(6, 2) };
                row.Click += (_, _) => { ShowClaim(a); Tabs.SelectedIndex = 0; };
                ClaimDetail.Children.Add(row);
            }
        }
    }

    partial void ShowingMatch(ClaimMatch m);

    /// <summary>The 835 claim's mark when matching knows more than the 835 alone: paid twice.</summary>
    private (string, IBrush)? MatchMark(ClaimPayment c) =>
        Reconciled.Claims.FirstOrDefault(m => m.Answers.Contains(c)) is { State: BilledState.PaidTwice } m && m.Standing.Skip(1).Contains(c) ? ("paid twice", Look.Reject) : null;
}
