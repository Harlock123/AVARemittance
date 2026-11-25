using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVARemittance.Core.Contracts;
using AVARemittance.Core.Remittance;

namespace AVARemittance;

/// <summary>
/// The Underpayments tab: the fee schedule the person loads, the tolerance, every line allowed below what the schedule
/// expects (or whose amounts don't agree), and each payer's total below it by procedure.
/// </summary>
public sealed partial class MainWindow
{
    private FeeSchedule? _schedule;
    private AppSettings? _settings;

    public StackPanel UnderPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };
    public TextBox ToleranceBox { get; } = new() { Width = 90 };
    public FeeSchedule? Schedule => _schedule;
    public IReadOnlyList<Underpayment> Underpaid { get; private set; } = [];

    internal AppSettings Settings => _settings ??= AppSettings.Load();

    private TabItem UnderTab()
    {
        ToleranceBox.Text = Settings.Tolerance.ToString("0.00", CultureInfo.InvariantCulture);
        ToleranceBox.LostFocus += (_, _) => SetTolerance(ToleranceBox.Text ?? "");
        ToleranceBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) SetTolerance(ToleranceBox.Text ?? ""); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 12, 20, 0) };
        bar.Children.Add(new TextBlock { Text = "Flag a line allowed more than", Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(ToleranceBox);
        bar.Children.Add(new TextBlock { Text = "dollars below the schedule.", Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center });
        var page = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(new ScrollViewer { Content = UnderPanel });
        return new TabItem { Header = "Underpayments", Content = page, FontSize = 14 };
    }

    /// <summary>Sets the tolerance from the box (dollars, 0 or more), saves it and checks again.</summary>
    public void SetTolerance(string text)
    {
        if (!decimal.TryParse(text.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var t) || t < 0)
        {
            Say($"\"{text}\" isn't an amount of dollars; the tolerance stays {Money.Say(Settings.Tolerance)}.");
            ToleranceBox.Text = Settings.Tolerance.ToString("0.00", CultureInfo.InvariantCulture);
            return;
        }
        if (t == Settings.Tolerance) return;
        Settings.Tolerance = t;
        Settings.Save();
        Recheck();
    }

    private static bool IsSchedule(string path) => FeeSchedule.LooksLikeOne(path);

    private void OpenSchedule(string path)
    {
        _schedule = FeeSchedule.Load(path);
        Recheck();
        Say($"Opened {_schedule.Name}: a fee schedule of {_schedule.Rates.Count} rates; {Underpaid.Count(u => u.Kind != "U2")} line{(Underpaid.Count(u => u.Kind != "U2") == 1 ? "" : "s")} in the open 835s below it."
            + (_schedule.Problems.Count > 0 ? $" {_schedule.Problems[0]}" : ""));
    }

    public void CloseSchedule()
    {
        _schedule = null;
        Recheck();
    }

    private void Recheck()
    {
        Underpaid = Underpayments.Find(_open.SelectMany(f => f.Claims), _schedule, Settings.Tolerance);
        FillUnder();
    }

    private void FillUnder()
    {
        UnderPanel.Children.Clear();
        if (_schedule == null)
            UnderPanel.Children.Add(Dim("Open a fee schedule (a CSV or Excel file: Payer, Procedure, Modifier, Rate, and From and To when rates change; or Procedure *, a Percent and a Basis, such as 65 of MEDICARE) to see what each payer allowed below it. The schedule is yours: what it expects is only as right as it."));
        else
            UnderPanel.Children.Add(Dim($"{_schedule.Name}: {_schedule.Rates.Count} rates for {string.Join(", ", _schedule.Rates.Select(r => r.Payer).Distinct())}."));
        var below = Underpaid.Where(u => u.Kind != "U2").ToList();
        if (_schedule != null)
        {
            UnderPanel.Children.Add(Heading(below.Count == 0 ? "Nothing below the schedule" : $"{Money.Say(below.Sum(u => u.Below))} below the schedule on {below.Count} line{(below.Count == 1 ? "" : "s")}"));
            foreach (var u in below) UnderPanel.Children.Add(UnderLine(u));
            var codes = Underpayments.ByCode(below);
            if (codes.Count > 0)
            {
                UnderPanel.Children.Add(Section("By payer and procedure"));
                foreach (var c in codes)
                    UnderPanel.Children.Add(new TextBlock { Text = $"{c.Payer} {c.Procedure}: {Money.Say(c.Below)} below, on {c.Lines} line{(c.Lines == 1 ? "" : "s")}.", Foreground = Look.Text });
            }
        }
        var odd = Underpaid.Where(u => u.Kind == "U2").ToList();
        if (odd.Count > 0)
        {
            UnderPanel.Children.Add(Section("Amounts that don't agree"));
            foreach (var u in odd) UnderPanel.Children.Add(UnderLine(u));
        }
    }

    private Control UnderLine(Underpayment u)
    {
        var row = Line(Underpayments.Describe(u), u.Kind == "U2" ? Look.Reject : Look.Warn, u.Line.Segments);
        row.PointerPressed += (_, _) => { ShowClaim(u.Claim); Tabs.SelectedIndex = 0; Highlight([.. u.Line.Segments.Select(s => s.Index)]); };
        return row;
    }
}
