using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Reports;

namespace AVARemittance;

/// <summary>
/// The Deposits tab: the bank statements' deposits matched to the 835s' payments (by trace or check number, then
/// amount and date), what's left over on each side, and the posting summary by day, which File › Export writes.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<BankStatement> _statements = [];

    public StackPanel DepositsPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };
    public IReadOnlyList<BankStatement> Statements => _statements;
    public DepositReconciliation Banked { get; private set; } = new();

    /// <summary>The posting summary by day, from what's open.</summary>
    public IReadOnlyList<PostingDay> PostingDays => Posting.Days(_open.SelectMany(f => f.Payments), Reconciled, _statements.Count > 0 ? Banked : null);

    private TabItem DepositsTab() => new() { Header = "Deposits", Content = new ScrollViewer { Content = DepositsPanel }, FontSize = 14 };

    private static bool IsStatement(string path) => BankStatement.LooksLikeOne(path);

    private void OpenStatement(string path)
    {
        var s = BankStatement.Load(path);
        _statements.RemoveAll(x => x.Path == s.Path);
        _statements.Add(s);
        Rebank();
        Say($"Opened {s.Name}: {s.Kind}, {s.Deposits.Count} deposit{(s.Deposits.Count == 1 ? "" : "s")} for {Money.Say(s.Deposits.Sum(d => d.Amount))}; "
            + $"{Banked.Matched.Count(m => m.Deposit?.Source == s.Name)} matched to the open 835s." + (s.Problems.Count > 0 ? $" {s.Problems[0]}" : ""));
    }

    public bool CloseStatement(BankStatement s)
    {
        if (!_statements.Remove(s)) return false;
        Rebank();
        return true;
    }

    private void Rebank()
    {
        Banked = DepositMatcher.Match(_statements.SelectMany(s => s.Deposits), _open.SelectMany(f => f.Payments));
        FillDeposits();
    }

    private void FillDeposits()
    {
        DepositsPanel.Children.Clear();
        if (_statements.Count == 0)
        {
            DepositsPanel.Children.Add(Heading("The money"));
            DepositsPanel.Children.Add(Dim("Open a bank statement (a CSV or Excel file with Date, Description and Amount, or a BAI2 file from the bank) to match each deposit to its 835. The posting summary below needs only the 835s."));
        }
        else
        {
            var b = Banked;
            DepositsPanel.Children.Add(Heading($"{b.Matched.Count} deposit{(b.Matched.Count == 1 ? "" : "s")} matched to {b.Matched.Sum(m => m.Payments.Count)} payment{(b.Matched.Sum(m => m.Payments.Count) == 1 ? "" : "s")}"));
            var off = b.Matched.Count(m => m.Difference != 0);
            DepositsPanel.Children.Add(new TextBlock
            {
                Text = $"{b.NoRemittance.Count} insurance deposit{(b.NoRemittance.Count == 1 ? "" : "s")} with no 835, {b.NoMoney.Count} 835 payment{(b.NoMoney.Count == 1 ? "" : "s")} with no deposit, {off} that differ{(off == 1 ? "s" : "")}.",
                Foreground = b.NoRemittance.Count + b.NoMoney.Count + off > 0 ? Look.Warn : Look.Clean, TextWrapping = TextWrapping.Wrap,
            });
            DepositsPanel.Children.Add(Section("Matched"));
            foreach (var m in b.Matched)
            {
                var row = Line(DepositMatcher.Describe(m), m.Difference == 0 ? Look.Clean : Look.Warn, []);
                row.PointerPressed += (_, _) => { ShowPayment(m.Payments[0]); Tabs.SelectedIndex = 0; };
                DepositsPanel.Children.Add(row);
            }
            if (b.NoRemittance.Count > 0)
            {
                DepositsPanel.Children.Add(Section("Insurance money with no 835"));
                foreach (var d in b.NoRemittance)
                    DepositsPanel.Children.Add(Line($"{Money.Say(d.Amount)} on {Money.Day(d.Date)}: {d.Description}" + (d.Trace.Length > 0 ? $". Its trace is {d.Trace}: ask the payer, or the clearinghouse, for that 835." : ". Ask the payer which 835 it pays."), Look.Warn, []));
            }
            if (b.NoMoney.Count > 0)
            {
                DepositsPanel.Children.Add(Section("835 payments with no deposit"));
                foreach (var p in b.NoMoney)
                    DepositsPanel.Children.Add(Line($"{p.Payer.Name}'s {Money.Say(p.Total)} {p.MethodWord} {p.Trace}" + (p.PaidOn is { } d ? $" of {Money.Day(d)}" : "") + (p.IsCheck ? ": the check hasn't been deposited, or the statement doesn't reach that far." : ": not on the open statements yet."), Look.Warn, []));
            }
            if (b.Other.Count > 0)
            {
                DepositsPanel.Children.Add(Section("Other deposits"));
                foreach (var d in b.Other) DepositsPanel.Children.Add(Dim($"{Money.Say(d.Amount)} on {Money.Day(d.Date)}: {d.Description}"));
            }
        }
        DepositsPanel.Children.Add(Section("Posting by day"));
        var days = PostingDays;
        if (days.Count == 0) DepositsPanel.Children.Add(Dim("Open an 835 to see what to post."));
        foreach (var d in days) DepositsPanel.Children.Add(new TextBlock { Text = DayLine(d, _statements.Count > 0), Foreground = Look.Text, TextWrapping = TextWrapping.Wrap });
        if (days.Count > 0) DepositsPanel.Children.Add(Dim("File › Export the Posting Summary writes this, with every claim, as an HTML page or an Excel workbook."));
    }

    internal static string DayLine(PostingDay d, bool banked) =>
        $"{Money.Day(d.Date)}: {d.Payments.Count} payment{(d.Payments.Count == 1 ? "" : "s")}, {d.Claims} claims. Received {Money.Say(d.Received)}; posted {Money.Say(d.Posted)}"
        + (d.Unapplied != 0 ? $", unapplied {Money.Say(d.Unapplied)}" : "") + $"; adjusted off {Money.Say(d.Adjusted)}; the patients' {Money.Say(d.PatientResponsibility)}"
        + (d.Recouped != 0 ? $"; recouped {Money.Say(d.Recouped)}" : "") + (d.Added != 0 ? $"; added {Money.Say(d.Added)}" : "")
        + (banked ? $"; deposited {Money.Say(d.Deposited)}" : "") + ".";

    internal Task ExportPostingForTests() => ExportPosting();

    private async Task ExportPosting()
    {
        if (_open.Count == 0)
        {
            Say("Open an 835 first: the posting summary is what the 835s paid.");
            return;
        }
        var path = await PickSaveFile("Export the posting summary (.html or .xlsx)", "posting-summary.html");
        if (path == null) return;
        PostingReport.Write(path, PostingDays);
        Say($"Wrote the posting summary to {System.IO.Path.GetFileName(path)}.");
    }
}
