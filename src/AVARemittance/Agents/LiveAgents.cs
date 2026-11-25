using Avalonia.Threading;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Contracts;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Status;
using AVARemittance.Mcp;

namespace AVARemittance.Agents;

/// <summary>The window as an agent sees it: what it shows, everything open and its readings, whether agents may see people's details, recording a denial and asking the clearinghouse (the person says yes).</summary>
internal sealed class WindowSession(MainWindow window) : IExplorerSession
{
    public ViewState View => window.CurrentView;
    public bool SeeRealValues => window.AgentsSeeRealValues;
    public IReadOnlyList<RemitFile> Remits => window.OpenFiles;
    public IReadOnlyList<BilledFile> Billed => window.BilledFiles;
    public IReadOnlyList<BankStatement> Statements => window.Statements;
    public FeeSchedule? Schedule => window.Schedule;
    public IReadOnlyList<PayerLimits> Limits => window.Limits;
    public IReadOnlyList<string> StatusFiles => window.StatusFiles;
    public IReadOnlyList<StatusAnswer> Statuses => window.StatusAnswers;
    public IReadOnlyList<string> OpenPaths => window.OpenPaths;
    public decimal Tolerance => window.Settings.Tolerance;
    public StatusSettings Status => window.Settings.Status;
    public Reconciliation Reconciled => window.Reconciled;
    public DepositReconciliation Banked => window.Banked;
    public IReadOnlyList<Underpayment> Underpaid => window.Underpaid;
    public IReadOnlyList<WorkItem> Work => window.Work;
    public DateOnly Today => window.Today();
    public DateTime Now => window.Now();
    public Action<WorkItem, WorkState, string>? MarkWork => window.MarkForAgent;
    public Func<Task<string>>? AskClearinghouse => window.AskForAgent;
    public void Open(string path) => window.OpenForAgent(path);
    public void Close(string path) => window.CloseForAgent(path);
    public void Show(ViewState view) => window.ShowView(view);
    public void Wrote276() => window.Wrote276ForAgent();
}

/// <summary>
/// AI agents in the open window: the session and host the chat and outside agents share, the door on this computer
/// (a pipe, for <c>AVARemittance --mcp --attach</c>) and the network door (MCP over HTTP with a token).
/// </summary>
internal sealed class LiveAgents(MainWindow window) : IExplorerDesk, IUiThread, IAsyncDisposable
{
    private readonly WindowSession _session = new(window);
    private LiveListener? _listener;
    private NetworkListener? _network;

    public ViewPainter Painter { get; } = new(window.AgentStage);

    public bool Running => _listener != null;

    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How many outside agents are connected (pipe and network together), when it changes.</summary>
    public event Action<int>? ConnectionsChanged;

    public IExplorerSession Session => _session;

    public string? Busy => window.OwnedWindows.Count > 0 || ChatLook.ModalsOpen > 0 || window.Asking ? "a dialog box is open in AVA Remittance" : null;

    public void Report(string text) => window.Say(text);

    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);

    /// <summary>The host outside agents work through, in <paramref name="root"/>.</summary>
    public ExplorerHost Host(string root) => new(this, this, Painter, root) { BusyWait = BusyWait, Live = true };

    /// <summary>The host the AI Chat runs its in-process MCP server on; its status lines name the chat's model.</summary>
    public ExplorerHost ChatHost(Func<string> root, Func<string> who) => new(this, new ChatDesk(this, who), Painter, root) { BusyWait = BusyWait, Live = true };

    private sealed class ChatDesk(LiveAgents agents, Func<string> who) : IExplorerDesk
    {
        public IExplorerSession Session => agents.Session;

        public string? Busy => agents.Busy;

        public void Report(string text) =>
            agents.Report(text.StartsWith("AI agent: ", StringComparison.Ordinal) ? who() + ": " + text["AI agent: ".Length..] : text);
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another AVA Remittance has it), or null.</summary>
    public string? Start(string pipeName, Func<string> root)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, asked => Host(Directory.Exists(asked) ? asked : root()));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        listener.Stop();
        await listener.DisposeAsync();
    }

    public bool NetworkRunning => _network != null;

    public NetworkSettings? NetworkSettings { get; private set; }

    public string? NetworkRoot { get; private set; }

    public IReadOnlyList<string> NetworkUrls => _network?.Urls ?? [];

    public async Task<string?> StartNetworkAsync(NetworkSettings settings, string root)
    {
        if (_network != null) return null;
        if (!Directory.Exists(root)) return $"the working folder {root} doesn't exist.";
        var listener = new NetworkListener(settings, RemittanceServer.Name, o => RemittanceServer.Configure(o, Host(root)));
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() => ConnectionsChanged?.Invoke(n));
        try
        {
            await listener.StartAsync();
        }
        catch (IOException e)
        {
            return e.Message;
        }
        _network = listener;
        NetworkSettings = settings;
        NetworkRoot = root;
        return null;
    }

    public async Task StopNetworkAsync()
    {
        var listener = _network;
        _network = null;
        NetworkSettings = null;
        NetworkRoot = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await StopNetworkAsync();
    }
}
