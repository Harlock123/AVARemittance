using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Sending;
using AVARemittance.Core.Status;
using AVARemittance.Core.X12;

namespace AVARemittance;

/// <summary>
/// The Claim Status tab: the billed claims with no answer after so many days, each with its latest 277; Write the 276
/// for them; and the clearinghouse (off until the person turns it on), with Ask Now, which asks first, sends each
/// claim's 276 in a CORE envelope, saves each 277 beside the billed claims and opens them.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<StatusAnswer> _statuses = [];
    private readonly List<string> _statusFiles = [];

    public StackPanel StatusPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };
    public TextBox AfterDaysBox { get; } = new() { Width = 60 };
    public TextBox StatusSenderBox { get; } = new() { Width = 160 };
    public TextBox StatusReceiverBox { get; } = new() { Width = 160 };
    public CheckBox SendEnabledBox { get; } = new() { Content = "Ask a clearinghouse in real time (CAQH CORE)" };
    public TextBox EndpointBox { get; } = new() { Width = 420, Watermark = "https://…" };
    public ComboBox EnvelopeBox { get; } = new() { ItemsSource = new[] { "mime", "soap" }, SelectedIndex = 0, Width = 100 };
    public TextBox CoreSenderBox { get; } = new() { Width = 160 };
    public TextBox CoreReceiverBox { get; } = new() { Width = 160 };
    public TextBox UserBox { get; } = new() { Width = 200 };
    public TextBox PasswordBox { get; } = new() { Width = 200, PasswordChar = '•' };
    private readonly StackPanel _waiting = new() { Spacing = 4 };

    /// <summary>Asks the person to confirm; tests answer it themselves.</summary>
    public Func<string, Task<bool>> AskSure { get; set; } = null!;

    /// <summary>Makes the HTTP client that talks to the clearinghouse; tests give their own.</summary>
    public Func<HttpClient> MakeHttp { get; set; } = () => new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The time a 276 is written at; tests fix it.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    /// <summary>The last real-time run.</summary>
    public IReadOnlyList<Asked> LastAsked { get; private set; } = [];
    public IReadOnlyList<StatusAnswer> StatusAnswers => _statuses;

    private PasswordStore Passwords => new(SettingsFolder);

    /// <summary>The billed claims with no answer in the open 835s after the days set.</summary>
    public IReadOnlyList<ClaimMatch> Waiting => [.. Reconciled.Claims.Where(m => m.State == BilledState.NoAnswer && Reconciled.DaysWaiting(m) >= Settings.Status.AfterDays)];

    private TabItem StatusTab()
    {
        AskSure ??= AskYesNo;
        var s = Settings.Status;
        AfterDaysBox.Text = s.AfterDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StatusSenderBox.Text = s.SenderId;
        StatusReceiverBox.Text = s.ReceiverId;
        AfterDaysBox.LostFocus += (_, _) => { ReadStatusSettings(); FillStatus(); };
        var c = Settings.Clearinghouse;
        SendEnabledBox.IsChecked = c.Enabled;
        EndpointBox.Text = c.Endpoint;
        EnvelopeBox.SelectedItem = c.Envelope;
        CoreSenderBox.Text = c.SenderId;
        CoreReceiverBox.Text = c.ReceiverId;
        UserBox.Text = c.Username;
        PasswordBox.Text = Passwords.Read() ?? "";
        var details = new StackPanel { Spacing = 2, IsVisible = c.Enabled };
        SendEnabledBox.IsCheckedChanged += (_, _) => details.IsVisible = SendEnabledBox.IsChecked == true;
        details.Children.Add(Field("Address", EndpointBox, "Envelope", EnvelopeBox));
        details.Children.Add(Field("CORE sender ID", CoreSenderBox, "CORE receiver ID", CoreReceiverBox));
        details.Children.Add(Field("User name", UserBox, "Password", PasswordBox));
        var ask = new Button { Content = "Ask Now…", Margin = new Thickness(0, 6, 0, 0) };
        ask.Click += async (_, _) => await AskNow();
        details.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { ask, new TextBlock { Text = "One request per claim; each 277 is saved beside the billed claims and opened.", Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } },
        });
        var write = new Button { Content = "Write the 276…" };
        write.Click += async (_, _) => await Write276();
        var page = new StackPanel { Margin = new Thickness(20, 14), Spacing = 4 };
        page.Children.Add(Heading("Claim status"));
        page.Children.Add(Field("Ask after (days)", AfterDaysBox, "Submitter ID", StatusSenderBox));
        page.Children.Add(Field("Receiver ID", StatusReceiverBox, "", new StackPanel { Orientation = Orientation.Horizontal, Children = { write } }));
        page.Children.Add(_waiting);
        page.Children.Add(Section("Clearinghouse"));
        page.Children.Add(SendEnabledBox);
        page.Children.Add(new TextBlock { Text = "Off unless you turn it on. The password is kept in a file of its own that only you can read; the address must be HTTPS.", Foreground = Look.Dim, TextWrapping = TextWrapping.Wrap });
        page.Children.Add(details);
        StatusPanel.Children.Add(page);
        return new TabItem { Header = "Claim Status", Content = new ScrollViewer { Content = StatusPanel }, FontSize = 14 };
    }

    private static Control Field(string a, Control boxA, string b, Control boxB)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2) };
        row.Children.Add(new TextBlock { Text = a, Width = 120, Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(boxA);
        if (b.Length > 0) row.Children.Add(new TextBlock { Text = b, Width = 120, Foreground = Look.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
        row.Children.Add(boxB);
        return row;
    }

    /// <summary>The claim status settings as the boxes have them, saved.</summary>
    public StatusSettings ReadStatusSettings()
    {
        var s = Settings.Status with
        {
            AfterDays = int.TryParse(AfterDaysBox.Text, out var d) && d >= 0 ? d : Settings.Status.AfterDays,
            SenderId = StatusSenderBox.Text?.Trim() ?? "",
            ReceiverId = StatusReceiverBox.Text?.Trim() is { Length: > 0 } r ? r : "CLEARINGHOUSE",
        };
        Settings.Status = s;
        Settings.Save();
        return s;
    }

    private bool OpenStatusX12(string path, X12File x12)
    {
        if (!StatusReader.Is277(x12)) return false;
        var name = System.IO.Path.GetFileName(path);
        _statuses.RemoveAll(a => a.File == name);
        _statusFiles.Remove(path);
        var read = StatusReader.Read(x12, name);
        _statuses.AddRange(read);
        _statusFiles.Add(path);
        FillStatus();
        Say($"Opened {name}: a 277 about {read.Count} claim{(read.Count == 1 ? "" : "s")}" + (read.FirstOrDefault() is { } first ? $": {first.Meaning}" : "."));
        return true;
    }

    public IReadOnlyList<string> StatusFiles => _statusFiles;

    /// <summary>A question to the person is open (Ask Now's): agents wait.</summary>
    public bool Asking => _asking;

    public void CloseStatus(string path)
    {
        _statuses.RemoveAll(a => a.File == System.IO.Path.GetFileName(path));
        _statusFiles.Remove(path);
        FillStatus();
    }

    private void FillStatus()
    {
        _waiting.Children.Clear();
        if (_billed.Count == 0)
        {
            _waiting.Children.Add(Dim("Open the 837s you sent (or a charges list) and the 835s: the claims with no answer are asked about here."));
            return;
        }
        var waiting = Waiting;
        _waiting.Children.Add(Section($"{waiting.Count} claim{(waiting.Count == 1 ? "" : "s")} with no answer after {Settings.Status.AfterDays} days"));
        foreach (var m in waiting)
        {
            _waiting.Children.Add(new TextBlock { Text = $"{m.Billed.Account}  {m.Billed.Name} · {m.Billed.Payer}: billed {Money.Say(m.Billed.Charge)} on {Money.Day(m.Billed.Billed)}, {Reconciled.DaysWaiting(m)} days ago.", Foreground = Look.Text, TextWrapping = TextWrapping.Wrap });
            if (StatusReader.For(m.Billed, _statuses) is { } a)
                _waiting.Children.Add(new TextBlock { Text = StatusReader.Describe(a), Foreground = a.Statuses.FirstOrDefault()?.Category is "A4" or "A3" or "A6" or "A7" or "A8" ? Look.Reject : Look.Warn, Margin = new Thickness(18, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
    }

    partial void ShowingMatch(ClaimMatch m)
    {
        if (StatusReader.For(m.Billed, _statuses) is { } a)
            ClaimDetail.Children.Add(new TextBlock { Text = StatusReader.Describe(a), Foreground = Look.Warn, TextWrapping = TextWrapping.Wrap });
    }

    internal Task Write276ForTests() => Write276();

    private async Task Write276()
    {
        var waiting = Waiting;
        if (waiting.Count == 0)
        {
            Say($"No claim has gone {Settings.Status.AfterDays} days without an answer.");
            return;
        }
        var settings = ReadStatusSettings();
        Built276 built;
        try
        {
            built = StatusRequest.Build([.. waiting.Select(m => m.Billed)], settings, Now());
        }
        catch (InvalidOperationException e)
        {
            Say(e.Message);
            return;
        }
        var path = await PickSaveFile("Write the 276", $"status-276-{Now():yyyyMMdd}.x12");
        if (path == null) return;
        File.WriteAllText(path, built.Text);
        Settings.Status = settings with { ControlNumber = settings.ControlNumber + 1 };
        Settings.Save();
        Say($"Wrote the 276 to {System.IO.Path.GetFileName(path)}: {built.Traces.Count} claim{(built.Traces.Count == 1 ? "" : "s")} to {built.Transactions} payer{(built.Transactions == 1 ? "" : "s")}. Send it through your clearinghouse; open the 277s that come back.");
    }

    /// <summary>The clearinghouse as the boxes have it, saved with its password.</summary>
    public Clearinghouse ReadClearinghouse()
    {
        var c = new Clearinghouse
        {
            Enabled = SendEnabledBox.IsChecked == true,
            Endpoint = EndpointBox.Text?.Trim() ?? "",
            Envelope = EnvelopeBox.SelectedItem as string ?? "mime",
            SenderId = CoreSenderBox.Text?.Trim() ?? "",
            ReceiverId = CoreReceiverBox.Text?.Trim() ?? "",
            Username = UserBox.Text?.Trim() ?? "",
        };
        Settings.Clearinghouse = c;
        Settings.Save();
        if (PasswordBox.Text is { Length: > 0 } pw) Passwords.Save(pw);
        return c;
    }

    /// <summary>Asks the clearinghouse about every claim waiting, after the person says yes; says what happened.</summary>
    public async Task<string> AskNow()
    {
        string Done(string text)
        {
            Say(text);
            return text;
        }
        var waiting = Waiting;
        if (waiting.Count == 0) return Done($"No claim has gone {Settings.Status.AfterDays} days without an answer.");
        var ch = ReadClearinghouse();
        if (ch.Problem() is { } problem) return Done(problem);
        var settings = ReadStatusSettings();
        if (!await AskSure($"Ask {new Uri(ch.Endpoint).Host} about {waiting.Count} claim{(waiting.Count == 1 ? "" : "s")}?\n\nEach question holds a patient's name, birth date and member ID. The answers are saved beside the billed claims."))
            return Done("Nothing was sent.");
        var source = _billed.First(b => b.Claims.Contains(waiting[0].Billed));
        var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source.Path)!, $"status-{Now():yyyyMMdd-HHmm}");
        using var http = MakeHttp();
        IReadOnlyList<Asked> asked;
        try
        {
            asked = await StatusRealTime.AskAsync([.. waiting.Select(m => m.Billed)], settings, ch, Passwords.Read() ?? "", new CoreClient(http), folder, Now(), new Progress<string>(Say));
        }
        catch (InvalidOperationException e)
        {
            return Done(e.Message);
        }
        LastAsked = asked;
        Settings.Status = settings with { ControlNumber = settings.ControlNumber + asked.Count };
        Settings.Save();
        foreach (var a in asked.Where(a => a.Saved != null)) Open(a.Saved!);
        FillStatus();
        var failed = asked.Where(a => !a.Response.Ok).ToList();
        return Done($"Asked about {asked.Count}: {asked.Count - failed.Count} answered" + (failed.Count > 0 ? $", {failed.Count} not ({failed[0].Claim.Account}: {failed[0].Response.ErrorCode} {failed[0].Response.ErrorMessage})." : "."));
    }

    private async Task<bool> AskYesNo(string question)
    {
        var answer = false;
        var yes = new Button { Content = "Yes", MinWidth = 90, IsDefault = true };
        var no = new Button { Content = "No", MinWidth = 90, IsCancel = true };
        var dialog = new Window
        {
            Title = "AVA Remittance", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Window,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 14,
                Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } } },
            },
        };
        yes.Click += (_, _) => { answer = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        _asking = true;
        try { await dialog.ShowDialog(this); }
        finally { _asking = false; }
        return answer;
    }

    private bool _asking;
}
