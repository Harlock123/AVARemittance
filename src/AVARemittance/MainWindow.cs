using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.X12;

namespace AVARemittance;

/// <summary>An entry in the list on the left: a file, a payment in it, or a claim.</summary>
public sealed record ListEntry(RemitFile File, Payment? Payment, ClaimPayment? Claim)
{
    public override string ToString() => Claim?.Account ?? Payment?.Label ?? File.Name;
}

/// <summary>
/// The window: the open 835s on the left, by payment and claim, each claim marked with what happened to it; the
/// Remittance tab (the payment or claim in plain words, line by line, with the segments behind it below, the ones behind
/// the chosen line highlighted); the Checks tab (whether each 835 balances). Built in code; its controls are reachable
/// from tests.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<RemitFile> _open = [];
    private readonly Dictionary<RemitFile, List<Finding>> _findings = [];
    private Payment? _payment;
    private ClaimPayment? _claim;
    private bool _filling;

    public ListBox FileList { get; } = new() { Background = Look.Bar };
    public TabControl Tabs { get; } = new();
    public StackPanel RemitPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };
    public StackPanel ChecksPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };
    public SelectableTextBlock Segments { get; } = new() { FontFamily = Look.Mono, FontSize = 12.5, Foreground = Look.Code, Margin = new Thickness(8) };
    public ScrollViewer SegmentScroll { get; } = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Background = Look.Sheet };
    public TextBlock txtStatusBar { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Asks for files to open; tests answer it themselves.</summary>
    public Func<Task<IReadOnlyList<string>>> PickFiles { get; set; }

    /// <summary>Asks where to save, given a title and the name to suggest; tests answer it themselves.</summary>
    public Func<string, string, Task<string?>> PickSaveFile { get; set; }

    public IReadOnlyList<RemitFile> OpenFiles => _open;
    public Payment? CurrentPayment => _payment;
    public ClaimPayment? CurrentClaim => _claim;

    /// <summary>What the checks found in each open file.</summary>
    public IReadOnlyList<Finding> Findings => [.. _open.SelectMany(f => _findings.GetValueOrDefault(f) ?? [])];

    public MainWindow() : this(null) { }

    public MainWindow(IEnumerable<string>? open)
    {
        Title = "AVA Remittance";
        Width = 1440;
        Height = 880;
        MinWidth = 800;
        MinHeight = 500;
        Background = Look.Window;
        PickFiles = AskForFiles;
        PickSaveFile = AskWhereToSave;

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var status = new Border { Child = txtStatusBar, Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        AddExtraDocks(root);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("320,4,*") };
        var left = new DockPanel { Background = Look.Bar };
        var leftTitle = new TextBlock { Text = "REMITTANCES AND CLAIMS", Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(10, 8, 10, 4) };
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        left.Children.Add(FileList);
        FileList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ListEntry>((e, _) => e == null ? new TextBlock() : EntryLabel(e));
        FileList.SelectionChanged += (_, _) =>
        {
            if (_filling || FileList.SelectedItem is not ListEntry e) return;
            if (e.Claim != null) ShowClaim(e.Claim);
            else if (e.Payment != null) ShowPayment(e.Payment);
            else if (e.File.Payments.FirstOrDefault() is { } p) ShowPayment(p);
        };
        main.Children.Add(left);
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        main.Children.Add(split);
        Grid.SetColumn(Tabs, 2);
        main.Children.Add(Tabs);
        Tabs.ItemsSource = BuildTabs();
        root.Children.Add(main);
        Content = root;

        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            foreach (var f in e.Data.GetFiles() ?? []) if (f.TryGetLocalPath() is { } p) Open(p);
        });
        DragDrop.SetAllowDrop(this, true);
        ShowNothing();
        FillChecks();
        Say("Open 835 remittances (File › Open, Ctrl+O, or drop them here). Try the samples folder.");
        AfterBuild();
        foreach (var path in open ?? []) Open(path);
    }

    /// <summary>The tabs, in order. Later parts add theirs.</summary>
    private List<TabItem> BuildTabs() =>
    [
        new TabItem { Header = "Remittance", Content = BuildRemitPage(), FontSize = 14 },
        new TabItem { Header = "Checks", Content = new ScrollViewer { Content = ChecksPanel }, FontSize = 14 },
        .. MoreTabs(),
    ];

    partial void AddExtraDocks(DockPanel root);
    partial void AfterBuild();
    private partial IEnumerable<TabItem> MoreTabs();
    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action click, Key? key = null, KeyModifiers mods = KeyModifiers.Control)
        {
            var m = new MenuItem { Header = header };
            if (key is { } k) m.InputGesture = new KeyGesture(k, mods);
            m.Click += (_, _) => click();
            return m;
        }
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new Command(() => _ = OpenPicked()) });
        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = (object[])
            [
                Item("_Open…", () => _ = OpenPicked(), Key.O, KeyModifiers.Control),
                Item("_Close File", CloseCurrent, Key.W, KeyModifiers.Control),
                new Separator(),
                .. MoreFileItems(Item),
                Item("E_xit", Close, null, KeyModifiers.None),
            ],
        };
        var view = new MenuItem
        {
            Header = "_View",
            ItemsSource = (object[])
            [
                Item("_Remittance", () => Tabs.SelectedIndex = 0, Key.D1, KeyModifiers.Control),
                Item("_Checks", () => Tabs.SelectedIndex = 1, Key.D2, KeyModifiers.Control),
                Item("C_laims", () => Tabs.SelectedIndex = 2, Key.D3, KeyModifiers.Control),
                .. MoreViewItems(Item),
            ],
        };
        return new Menu { Background = Look.Bar, ItemsSource = (object[])[file, view, .. MoreMenus(Item)] };
    }

    // ----- Opening -----

    /// <summary>Opens a file (or every file in a folder that the program reads). Later parts take files that aren't 835s.</summary>
    public void Open(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path).Order(StringComparer.Ordinal).Where(f => X12File.LooksLikeX12(f) || Takes(f))) Open(f);
            return;
        }
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Core.Denials.EligibilityRecheck.LooksLikeOne(full))
            {
                Say($"{System.IO.Path.GetFileName(full)} is a list for AVA Eligibility (Check Eligibility Again wrote it): open it there to ask about the dates of service.");
                return;
            }
            if (Takes(full))
            {
                OpenOther(full);
                return;
            }
            var x12 = X12File.Load(full);
            if (OpenX12(full, x12)) return;
            var file = RemitReader.Read(x12, full);
            if (file.Payments.Count == 0)
            {
                Say($"{file.Name} has no 835 in it" + (file.Problems.Count > 0 ? $": {file.Problems[0]}" : "."));
                return;
            }
            _open.RemoveAll(o => o.Path == full);
            _open.Add(file);
            _findings[file] = Checks.Run(file);
            FileOpened(file);
            FillList();
            FillChecks();
            ShowPayment(file.Payments[0]);
            var found = _findings[file].Count(f => f.Rule.Severity == Severity.Error);
            Say($"Opened {file.Name}: {Summary(file)}" + (found > 0 ? $" It doesn't balance: {found} problem{(found == 1 ? "" : "s")} (see Checks)." : "")
                + (file.Problems.Count > 0 ? $" {file.Problems[0]}" : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't open {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>Whether a later part opens this file itself (a charges list, a bank statement).</summary>
    private partial bool Takes(string path);
    private partial void OpenOther(string path);

    /// <summary>A later part's X12 files that aren't 835s (837s, 277s); true when it took the file.</summary>
    private partial bool OpenX12(string path, X12File x12);
    partial void FileOpened(RemitFile file);
    partial void FilesChanged();

    /// <summary>"1 payment from HARBORVIEW HEALTH PLAN: $1,234.56 for 7 claims."</summary>
    public static string Summary(RemitFile f)
    {
        var payers = string.Join(", ", f.Payments.Select(p => p.Payer.Name).Distinct());
        var claims = f.Claims.Count();
        return $"{f.Payments.Count} payment{(f.Payments.Count == 1 ? "" : "s")} from {payers}: {Money.Say(f.Payments.Sum(p => p.Total))} for {claims} claim{(claims == 1 ? "" : "s")}.";
    }

    private async Task OpenPicked()
    {
        foreach (var p in await PickFiles()) Open(p);
    }

    private void CloseCurrent()
    {
        var file = _payment?.File;
        if (file == null || !CloseFile(file)) return;
        Say($"Closed {file.Name}.");
    }

    /// <summary>Closes an open 835.</summary>
    public bool CloseFile(RemitFile file)
    {
        if (!_open.Remove(file)) return false;
        _findings.Remove(file);
        _payment = null;
        _claim = null;
        FillList();
        FillChecks();
        ShowNothing();
        FilesChanged();
        return true;
    }

    // ----- The list -----

    private void FillList()
    {
        _filling = true;
        var entries = new List<ListEntry>();
        foreach (var f in _open)
        {
            entries.Add(new ListEntry(f, null, null));
            foreach (var p in f.Payments)
            {
                entries.Add(new ListEntry(f, p, null));
                entries.AddRange(p.Claims.Select(c => new ListEntry(f, p, c)));
            }
        }
        FileList.ItemsSource = entries;
        _filling = false;
        FilesChanged();
    }

    private Control EntryLabel(ListEntry e)
    {
        if (e.Claim is { } c)
        {
            var (word, brush) = Mark(c);
            var row = new DockPanel { Margin = new Thickness(20, 0, 0, 0) };
            var badge = new TextBlock { Text = word, Foreground = brush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
            var paid = new TextBlock { Text = Money.Say(c.Paid), Foreground = Look.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(paid, Dock.Right);
            row.Children.Add(paid);
            row.Children.Add(new TextBlock { Text = $"{c.Account}  {c.Patient?.SortName}", Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            return row;
        }
        if (e.Payment is { } p)
        {
            var off = _findings.GetValueOrDefault(e.File)?.Any(f => f.Payment == p && f.Rule.Severity == Severity.Error) == true;
            return new TextBlock
            {
                Text = $"{p.Payer.Name} · {Money.Say(p.Total)} {p.MethodWord}" + (p.PaidOn is { } d ? $" · {Money.Day(d)}" : ""),
                Foreground = off ? Look.Reject : Look.Heading, FontSize = 12, Margin = new Thickness(10, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }
        return new TextBlock { Text = e.File.Name, Foreground = Look.Accent, FontWeight = FontWeight.SemiBold };
    }

    /// <summary>The claim's mark in the list. Later parts give the work to do; until then, what happened to it.</summary>
    internal (string Word, IBrush Brush) Mark(ClaimPayment c) => MarkOf(c) ?? (Words.State(c.State), c.State switch
    {
        ClaimState.Paid => Look.Clean,
        ClaimState.PatientOwes => Look.Warn,
        ClaimState.PartlyDenied => Look.Warn,
        ClaimState.Denied => Look.Deny,
        _ => Look.Accent,
    });

    private partial (string, IBrush)? MarkOf(ClaimPayment c);

    // ----- The Remittance tab -----

    private Control BuildRemitPage()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("3*,4,2*") };
        grid.Children.Add(new ScrollViewer { Content = RemitPanel });
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 1);
        grid.Children.Add(split);
        SegmentScroll.Content = Segments;
        Grid.SetRow(SegmentScroll, 2);
        grid.Children.Add(SegmentScroll);
        return grid;
    }

    private void ShowNothing()
    {
        RemitPanel.Children.Clear();
        RemitPanel.Children.Add(Heading("Nothing chosen"));
        RemitPanel.Children.Add(Dim("Open an 835 (the payer's remittance) and choose a payment or a claim on the left."));
        ShowSegments([], []);
    }

    /// <summary>Shows a payment: the money and its trace, who paid whom, the provider-level adjustments, whether it balances, and its claims.</summary>
    public void ShowPayment(Payment p)
    {
        _payment = p;
        _claim = null;
        RemitPanel.Children.Clear();
        RemitPanel.Children.Add(Heading(p.Payer.Name.Length > 0 ? p.Payer.Name : "No payer"));
        RemitPanel.Children.Add(Line(Words.Summary(p), Look.Text, [.. Opt(p.Bpr), .. Opt(p.Trn)]));
        var facts = new List<string>();
        if (p.Payer.References.FirstOrDefault(r => r.Qualifier == "2U") is { } id) facts.Add($"payer ID {id.Value}");
        if (p.Produced is { } made) facts.Add($"produced {Money.Day(made)}");
        facts.Add($"to {p.Payee.Name}" + (p.Payee.Id.Length > 0 ? $" ({(p.Payee.IdQualifier == "XX" ? "NPI" : p.Payee.IdQualifier)} {p.Payee.Id})" : ""));
        RemitPanel.Children.Add(Dim(string.Join(" · ", facts)));
        foreach (var who in p.Payer.Contacts) RemitPanel.Children.Add(Dim($"Contact: {who}"));
        var findings = _findings.GetValueOrDefault(p.File)?.Where(f => f.Payment == p).ToList() ?? [];
        RemitPanel.Children.Add(Section("Does it balance?"));
        if (findings.Count == 0) RemitPanel.Children.Add(Line("Yes: the claims less the provider adjustments come to the payment, and every claim and line balances.", Look.Clean, [.. Opt(p.Bpr)]));
        foreach (var f in findings) RemitPanel.Children.Add(FindingLine(f));
        if (p.ProviderAdjustments.Count > 0)
        {
            RemitPanel.Children.Add(Section("Provider-level adjustments"));
            foreach (var a in p.ProviderAdjustments) RemitPanel.Children.Add(Line(Words.Describe(a), a.Amount > 0 ? Look.Deny : Look.Clean, [a.Segment]));
            RemitPanel.Children.Add(Dim($"Claims pay {Money.Say(p.Claims.Sum(c => c.Paid))}; less {Money.Say(p.ProviderAdjustments.Sum(a => a.Amount))} of provider adjustments, {Money.Say(p.Claims.Sum(c => c.Paid) - p.ProviderAdjustments.Sum(a => a.Amount))}."));
        }
        RemitPanel.Children.Add(Section($"{p.Claims.Count} claim{(p.Claims.Count == 1 ? "" : "s")}"));
        foreach (var c in p.Claims)
            RemitPanel.Children.Add(Line($"{c.Account}  {c.Patient?.Name}: {Mark(c).Word}. {Words.Summary(c)}", Mark(c).Brush, c.AllSegments.ToList()));
        ShowingPayment(p);
        ShowSegments([.. p.Source.Body], [.. Opt(p.Bpr), .. Opt(p.Trn)]);
    }

    partial void ShowingPayment(Payment p);

    /// <summary>Shows one claim's payment, line by line, each adjustment and remark in words.</summary>
    public void ShowClaim(ClaimPayment c)
    {
        _claim = c;
        _payment = c.Payment;
        RemitPanel.Children.Clear();
        RemitPanel.Children.Add(Heading($"{c.Account}  {c.Patient?.Name}"));
        RemitPanel.Children.Add(Line($"{RemitCodes.Say(RemitCodes.ClaimStatus, c.Status)}. {Words.Summary(c)}", Mark(c).Brush, [c.Segments[0]]));
        var facts = new List<string> { c.Payment.Payer.Name };
        if (c.PayerClaim.Length > 0) facts.Add($"payer claim {c.PayerClaim}");
        if (c.Patient is { Id.Length: > 0 } pt) facts.Add($"member {pt.Id}");
        if (c.From is { } from) facts.Add(c.To is { } to && to != from ? $"service {Money.Day(from)} to {Money.Day(to)}" : $"service {Money.Day(from)}");
        if (c.Filing.Length > 0) facts.Add(RemitCodes.Say(RemitCodes.Filing, c.Filing));
        if (c.Rendering is { } r) facts.Add($"rendering {r.Name}");
        RemitPanel.Children.Add(Dim(string.Join(" · ", facts)));
        foreach (var f in _findings.GetValueOrDefault(c.Payment.File)?.Where(f => f.Claim == c) ?? []) RemitPanel.Children.Add(FindingLine(f));
        ShowingClaim(c);
        if (c.Adjustments.Count > 0 || c.Remarks.Count > 0)
        {
            RemitPanel.Children.Add(Section("On the whole claim"));
            foreach (var a in c.Adjustments) RemitPanel.Children.Add(AdjustmentLine(a));
            foreach (var m in c.Remarks) RemitPanel.Children.Add(Line(Words.Describe(m), Look.Dim, [m.Segment]));
        }
        foreach (var x in c.References) RemitPanel.Children.Add(Line(Words.Describe(x), Look.Dim, [x.Segment]));
        foreach (var x in c.Amounts) RemitPanel.Children.Add(Line(Words.Describe(x), Look.Dim, [x.Segment]));
        if (c.Lines.Count > 0)
        {
            RemitPanel.Children.Add(Section($"{c.Lines.Count} line{(c.Lines.Count == 1 ? "" : "s")}"));
            foreach (var l in c.Lines) RemitPanel.Children.Add(ServiceLine(l));
        }
        ShowSegments([.. c.AllSegments], [c.Segments[0]]);
    }

    partial void ShowingClaim(ClaimPayment c);

    /// <summary>A line in words: the code, charge, allowed and paid, then each adjustment and remark under it; clicking it highlights its segments.</summary>
    internal Control ServiceLine(ServicePayment l)
    {
        var panel = new StackPanel { Spacing = 1 };
        var head = $"{l.Number}. {Words.Code(l)}" + (l.Units is { } u && u != 1 ? $" × {u:0.##}" : "") + (l.From is { } d ? $" on {Money.Day(d)}" : "")
            + $": billed {Money.Say(l.Charge)}" + (l.Allowed is { } a ? $", allowed {Money.Say(a)}" : "") + $", paid {Money.Say(l.Paid)}";
        panel.Children.Add(new TextBlock { Text = head, Foreground = l.Denied ? Look.Deny : Look.Text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        foreach (var x in l.Adjustments) panel.Children.Add(Indented(Words.Describe(x), x.Group == "PR" ? Look.Warn : ClaimPayment.IsContractual(x) ? Look.Dim : Look.Deny));
        foreach (var m in l.Remarks) panel.Children.Add(Indented(Words.Describe(m), Look.Dim));
        return Clickable(panel, l.Segments);
    }

    private Control AdjustmentLine(Adjustment a) => Line(Words.Describe(a), a.Group == "PR" ? Look.Warn : ClaimPayment.IsContractual(a) ? Look.Dim : Look.Deny, [a.Segment]);

    private static TextBlock Indented(string text, IBrush brush) =>
        new() { Text = text, Foreground = brush, Margin = new Thickness(18, 0, 0, 0), TextWrapping = TextWrapping.Wrap };

    internal Control FindingLine(Finding f) =>
        Line($"{f.Rule.Id} {f.Message}", f.Rule.Severity == Severity.Error ? Look.Reject : Look.Warn, f.Segments);

    // ----- The Checks tab -----

    private void FillChecks()
    {
        ChecksPanel.Children.Clear();
        ChecksPanel.Children.Add(Heading("Does each remittance balance?"));
        ChecksPanel.Children.Add(Dim("Each payment is checked against its claims, each claim against its adjustments and lines, and each line against its own. Posting an 835 that doesn't balance puts the wrong money on the books."));
        if (_open.Count == 0) ChecksPanel.Children.Add(Dim("Open an 835 to check it."));
        foreach (var f in _open)
        {
            var found = _findings.GetValueOrDefault(f) ?? [];
            ChecksPanel.Children.Add(Section(f.Name));
            if (found.Count == 0) ChecksPanel.Children.Add(new TextBlock { Text = $"Balances: {Summary(f)}", Foreground = Look.Clean, TextWrapping = TextWrapping.Wrap });
            foreach (var x in found)
            {
                var row = FindingLine(x);
                row.PointerPressed += (_, _) =>
                {
                    if (x.Claim != null) ShowClaim(x.Claim);
                    else ShowPayment(x.Payment);
                    Tabs.SelectedIndex = 0;
                    Highlight([.. x.Segments.Select(s => s.Index)]);
                };
                ChecksPanel.Children.Add(row);
            }
        }
        ChecksPanel.Children.Add(Section("The rules"));
        foreach (var r in Checks.Rules)
            ChecksPanel.Children.Add(new TextBlock { Text = $"{r.Id} {r.Title}. {r.Why} ({r.Source})", Foreground = Look.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
    }

    // ----- Segments -----

    private IReadOnlyList<Segment> _shown = [];

    private void ShowSegments(IReadOnlyList<Segment> segments, IReadOnlyList<Segment> highlighted)
    {
        _shown = segments;
        Highlight([.. highlighted.Select(s => s.Index)]);
    }

    /// <summary>Highlights the segments at these file positions, among those shown.</summary>
    public void Highlight(HashSet<int> indexes)
    {
        Segments.Inlines ??= [];
        Segments.Inlines.Clear();
        var delimiters = _open.FirstOrDefault(f => _shown.Count > 0 && f.X12.Segments.Any(s => ReferenceEquals(s, _shown[0])))?.X12.Delimiters ?? Delimiters.Standard;
        foreach (var s in _shown)
        {
            var hit = indexes.Contains(s.Index);
            Segments.Inlines.Add(new Run($"{s.Index + 1,5}  ") { Foreground = Look.Gutter });
            Segments.Inlines.Add(new Run(s.Id) { Foreground = Look.SegmentId, Background = hit ? Look.Highlight : null });
            Segments.Inlines.Add(new Run(s.Text[s.Id.Length..] + delimiters.Shown) { Background = hit ? Look.Highlight : null });
            Segments.Inlines.Add(new LineBreak());
        }
    }

    /// <summary>The text of the highlighted segments, for tests.</summary>
    public string Highlighted => string.Concat(Segments.Inlines?.OfType<Run>().Where(r => r.Background != null).Select(r => r.Text) ?? []);

    // ----- Helpers -----

    internal Control Line(string text, IBrush brush, IReadOnlyList<Segment> segments) =>
        Clickable(new TextBlock { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap }, segments);

    private Control Clickable(Control inner, IReadOnlyList<Segment> segments)
    {
        var border = new Border { Child = inner, Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, Cursor = segments.Count > 0 ? new Cursor(StandardCursorType.Hand) : null };
        if (segments.Count > 0)
            border.PointerPressed += (_, _) => Highlight([.. segments.Select(s => s.Index)]);
        return border;
    }

    private static IEnumerable<Segment> Opt(Segment? s) => s == null ? [] : [s];

    internal static TextBlock Heading(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap };

    internal static TextBlock Section(string text) => new() { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = Look.Heading, Margin = new Thickness(0, 14, 0, 4) };

    internal static TextBlock Dim(string text) => new() { Text = text, Foreground = Look.Dim, TextWrapping = TextWrapping.Wrap };

    private async Task<IReadOnlyList<string>> AskForFiles()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open 835s, 837s, charges lists and bank statements", AllowMultiple = true });
        return picked.Select(p => p.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async Task<string?> AskWhereToSave(string title, string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
        return file?.TryGetLocalPath();
    }

    public void Say(string text) => txtStatusBar.Text = text;

    private sealed class Command(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
