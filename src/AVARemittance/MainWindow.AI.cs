using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AVARemittance.Core.Matching;
using AVARemittance.Mcp;

namespace AVARemittance;

/// <summary>The AI side's place in the window: the chat's dock, the AI menu, and the view as agents see and set it.</summary>
public sealed partial class MainWindow
{
    public Border ChatHolder { get; } = new() { Width = 440, IsVisible = false, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 0, 0, 0) };

    /// <summary>Where pictures of the view are drawn for agents, out of sight.</summary>
    public Panel AgentStage { get; } = new() { Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false };

    public MenuItem ChatMenuItem { get; } = new() { Header = "AI _Chat", InputGesture = new KeyGesture(Key.F12), ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem SeeRealMenuItem { get; } = new() { Header = "Let AI Agents _See Values", ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem AllowAgentsMenuItem { get; } = new() { Header = "_Allow AI Agents (MCP)", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>Started with <c>--mask</c>: agents see people's details masked whatever the setting says.</summary>
    public bool ForceDeidentify { get; init; }

    /// <summary>Started with <c>--real</c>: agents see people's details (unless <see cref="ForceDeidentify"/>).</summary>
    public bool RealFromCommandLine { get; init; }

    partial void AddExtraDocks(DockPanel root)
    {
        DockPanel.SetDock(ChatHolder, Dock.Right);
        root.Children.Add(ChatHolder);
        DockPanel.SetDock(AgentStage, Dock.Right);
        root.Children.Add(AgentStage);
    }

    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item)
    {
        ChatMenuItem.Click += OnToggleChatClick;
        SeeRealMenuItem.Click += OnSeeRealClick;
        AllowAgentsMenuItem.Click += OnAllowAgentsClick;
        return
        [
            new MenuItem
            {
                Header = "_AI",
                ItemsSource = (object[])
                [
                    ChatMenuItem,
                    new Separator(),
                    SeeRealMenuItem,
                    item("_Working Folder…", () => OnWorkingFolderClick(null, new RoutedEventArgs()), null, KeyModifiers.None),
                    new Separator(),
                    AllowAgentsMenuItem,
                    item("C_onnect an Agent…", () => _ = ShowConnect(), null, KeyModifiers.None),
                ],
            },
            new MenuItem
            {
                Header = "_Help",
                ItemsSource = (object[])
                [
                    item("_About", () => Say($"AVA Remittance {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}: reads 835 remittances and says what each payer paid and why, matches them to the claims and the bank, and keeps the denials. MIT licence, Lonnie Watson."), null, KeyModifiers.None),
                ],
            },
        ];
    }

    private static readonly string[] TabNames = ["remittance", "checks", "claims", "deposits", "underpayments", "denials", "status"];

    /// <summary>What the window shows, as agents see it: the tab, and what's shown there (by handle).</summary>
    public ViewState CurrentView
    {
        get
        {
            var tab = Tabs.SelectedIndex >= 0 && Tabs.SelectedIndex < TabNames.Length ? TabNames[Tabs.SelectedIndex] : "remittance";
            var session = Agents?.Session;
            string? handle = null;
            if (session != null)
            {
                handle = tab switch
                {
                    "claims" when ClaimList.SelectedItem is ClaimMatch m => RemittanceTools.BilledClaims(Reconciled).FirstOrDefault(x => x.Match.Billed == m.Billed).Handle,
                    "denials" => CurrentWork?.Id,
                    _ => _claim != null ? RemittanceTools.Handle(session, _claim) : _payment != null ? RemittanceTools.Handle(session, _payment) : null,
                };
            }
            return new ViewState(tab, handle is { Length: > 0 } ? handle : null);
        }
    }

    /// <summary>Shows what an agent asked: a payment, a claim, a billed claim or a denial, or a tab.</summary>
    public void ShowView(ViewState view)
    {
        if (view.Handle is { } h && Agents?.Session is { } session)
        {
            switch (h[0])
            {
                case 'R' when RemittanceTools.Payments(session).FirstOrDefault(x => x.Handle == h).Payment is { } p: ShowPayment(p); break;
                case 'C' when RemittanceTools.Claims(session).FirstOrDefault(x => x.Handle == h).Claim is { } c: ShowClaim(c); break;
                case 'B' when RemittanceTools.BilledClaims(Reconciled).FirstOrDefault(x => x.Handle == h).Match is { } m: ClaimList.SelectedItem = m; break;
                case 'W' when Work.FirstOrDefault(w => w.Id == h) is { } w: WorkList.SelectedItem = w; break;
            }
        }
        Tabs.SelectedIndex = Math.Max(0, Array.IndexOf(TabNames, view.Tab));
    }

    /// <summary>Everything open, by path.</summary>
    internal IReadOnlyList<string> OpenPaths =>
        [.. _open.Select(f => f.Path), .. _billed.Select(f => f.Path), .. _statements.Select(s => s.Path), .. _schedule is { } s ? [s.Path] : Array.Empty<string>(),
         .. _limitsPath is { } l ? [l] : Array.Empty<string>(), .. _statusFiles];

    /// <summary>Opens a file for an agent, saying why when it isn't one the program reads.</summary>
    internal void OpenForAgent(string path)
    {
        var full = Path.GetFullPath(path);
        Open(full);
        if (!OpenPaths.Contains(full))
            throw new NotSupportedException(txtStatusBar.Text is { Length: > 0 } said ? said : $"{Path.GetFileName(full)} isn't a file AVA Remittance reads.");
    }

    /// <summary>Closes an open file of any kind for an agent.</summary>
    internal void CloseForAgent(string path)
    {
        var full = Path.GetFullPath(path);
        if (_open.FirstOrDefault(o => o.Path == full) is { } f) CloseFile(f);
        if (_billed.FirstOrDefault(b => b.Path == full) is { } b) CloseBilled(b);
        if (_statements.FirstOrDefault(s => s.Path == full) is { } st) CloseStatement(st);
        if (_schedule?.Path == full) CloseSchedule();
        if (_limitsPath == full) CloseLimits();
        if (_statusFiles.Contains(full)) CloseStatus(full);
    }

    /// <summary>What an agent recorded about a denial, as if the person had pressed the button.</summary>
    internal void MarkForAgent(Core.Denials.WorkItem w, Core.Denials.WorkState state, string note) =>
        SetWorkState(Work.FirstOrDefault(x => x.Key == w.Key) ?? w, state, note);

    /// <summary>A 276 an agent wrote: the next takes the next control number.</summary>
    internal void Wrote276ForAgent()
    {
        Settings.Status = Settings.Status with { ControlNumber = Settings.Status.ControlNumber + 1 };
        Settings.Save();
    }

    /// <summary>Ask Now for an agent: the person is asked first, as always; says what happened.</summary>
    internal Task<string> AskForAgent() => AskNow();

    partial void AgentsReady()
    {
        SetUpAgents();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.F12) return;
            ToggleChat();
            e.Handled = true;
        };
    }
}
