using System.Diagnostics;
using AVARemittance.Core;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Contracts;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Privacy;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Status;
using AVARemittance.Core.X12;

namespace AVARemittance.Mcp;

/// <summary>
/// What the window shows: the tab on show (remittance, checks, claims, deposits, underpayments, denials or status) and
/// what's selected, by the handle agents know it by (R01 a payment, C03 a claim in an 835, B05 a billed claim, W02 a
/// denial to work).
/// </summary>
public sealed record ViewState(string Tab, string? Handle)
{
    public static readonly ViewState Empty = new("remittance", null);
}

/// <summary>What a picture of the view shows: a title, a line under it, and lines, some highlighted.</summary>
public sealed record ViewPicture(string Title, string Subtitle, IReadOnlyList<string> Lines, int FirstLine, int HighlightFrom, int HighlightTo, bool Numbered, IReadOnlyList<string> Notes);

/// <summary>What the server needs from the program that it can't do itself: drawing the view, which needs Avalonia.</summary>
public interface IViewPainter
{
    /// <summary>The view drawn as a PNG, at <paramref name="scale"/>.</summary>
    byte[] Picture(ViewPicture view, double scale);
}

/// <summary>The window (or the headless server's stand-in for one) as an agent works with it. Used on the host's thread only.</summary>
public interface IExplorerSession
{
    /// <summary>What the window shows now.</summary>
    ViewState View { get; }

    /// <summary>
    /// Whether the person lets agents see people's details (AI › Let AI Agents See Values, or <c>--real</c>). Off unless
    /// turned on: names, member IDs, accounts, claim, check and trace numbers and dates are masked (<see cref="Masker"/>).
    /// </summary>
    bool SeeRealValues { get; }

    IReadOnlyList<RemitFile> Remits { get; }
    IReadOnlyList<BilledFile> Billed { get; }
    IReadOnlyList<BankStatement> Statements { get; }
    FeeSchedule? Schedule { get; }
    IReadOnlyList<PayerLimits> Limits { get; }
    IReadOnlyList<string> StatusFiles { get; }
    IReadOnlyList<StatusAnswer> Statuses { get; }

    /// <summary>The paths of everything open, for checkpoints.</summary>
    IReadOnlyList<string> OpenPaths { get; }

    decimal Tolerance { get; }
    StatusSettings Status { get; }

    Reconciliation Reconciled { get; }
    DepositReconciliation Banked { get; }
    IReadOnlyList<Underpayment> Underpaid { get; }
    IReadOnlyList<WorkItem> Work { get; }

    /// <summary>The day days are counted to, and the time a 276 is written at.</summary>
    DateOnly Today { get; }
    DateTime Now { get; }

    /// <summary>Opens an 835, 837, 277, charges list, bank statement, fee schedule or payers' limits; throws saying why when it's none.</summary>
    void Open(string path);

    void Close(string path);

    /// <summary>Selects in the window (headless: just remembers it).</summary>
    void Show(ViewState view);

    /// <summary>Records what was done with a denial, beside its 835 (the open window only: null headless).</summary>
    Action<WorkItem, WorkState, string>? MarkWork { get; }

    /// <summary>Remembers that a 276 was written (the next one takes the next control number).</summary>
    void Wrote276();

    /// <summary>
    /// Asks the clearinghouse about the claims waiting in real time, after the person says yes in the window; says what
    /// happened. Null headless: sending is the person's, in their window.
    /// </summary>
    Func<Task<string>>? AskClearinghouse { get; }
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="RemittanceTools"/>) touch the window only through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server and the open window.
/// </summary>
public interface IExplorerHost
{
    /// <summary>The working folder: files are read from it, and new files written only inside it.</summary>
    string Root { get; }

    IViewPainter Painter { get; }

    /// <summary>Working in the open window, with the person watching.</summary>
    bool Live { get; }

    /// <summary>Runs <paramref name="work"/> on the thread that owns the window, one call at a time.</summary>
    Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default);
}

public static class ExplorerHostExtensions
{
    public static Task<T> RunAsync<T>(this IExplorerHost host, string action, Func<IExplorerSession, T> work, CancellationToken cancel = default) =>
        host.RunAsync(action, s => Task.FromResult(work(s)), cancel);
}

/// <summary>Runs work on a UI thread (the program's Avalonia dispatcher, or a test's).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The program as the host sees it: the session, whether it can take a call now, and the status bar.</summary>
public interface IExplorerDesk
{
    IExplorerSession Session { get; }

    /// <summary>Why the program can't take a call just now ("a dialog box is open"), or null. The call waits.</summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did.</summary>
    void Report(string text);
}

/// <summary>
/// The host: calls run against the desk's session on its UI thread, one at a time. While the person is busy (a dialog
/// open) a call waits up to <see cref="BusyWait"/>.
/// </summary>
public sealed class ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, Func<string> root) : IExplorerHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, string root)
        : this(ui, desk, painter, () => root) { }

    public string Root => Path.GetFullPath(root());

    public IViewPainter Painter { get; } = painter;

    public bool Live { get; init; }

    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(async () =>
                {
                    if (desk.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = await work(desk.Session);
                        desk.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        desk.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new ExplorerBusyException($"AVA Remittance is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class ExplorerBusyException(string message) : Exception(message);

/// <summary>A session with no window round it, as <c>AVARemittance --mcp</c> serves it: what's open, and the same readings the window makes.</summary>
public sealed class HeadlessSession(bool seeRealValues = false, Func<DateTime>? now = null, decimal tolerance = 1.00m, StatusSettings? status = null) : IExplorerSession
{
    private readonly List<RemitFile> _remits = [];
    private readonly List<BilledFile> _billed = [];
    private readonly List<BankStatement> _statements = [];
    private readonly List<PayerLimits> _limits = [];
    private readonly List<(string Path, List<StatusAnswer> Answers)> _statuses = [];
    private string? _schedulePath, _limitsPath;

    public ViewState View { get; private set; } = ViewState.Empty;
    public bool SeeRealValues { get; set; } = seeRealValues;
    public IReadOnlyList<RemitFile> Remits => _remits;
    public IReadOnlyList<BilledFile> Billed => _billed;
    public IReadOnlyList<BankStatement> Statements => _statements;
    public FeeSchedule? Schedule { get; private set; }
    public IReadOnlyList<PayerLimits> Limits => _limits;
    public IReadOnlyList<string> StatusFiles => [.. _statuses.Select(s => s.Path)];
    public IReadOnlyList<StatusAnswer> Statuses => [.. _statuses.SelectMany(s => s.Answers)];
    public decimal Tolerance { get; set; } = tolerance;
    public StatusSettings Status { get; private set; } = status ?? new StatusSettings();

    public IReadOnlyList<string> OpenPaths =>
        [.. _remits.Select(f => f.Path), .. _billed.Select(f => f.Path), .. _statements.Select(s => s.Path), .. _schedulePath is { } p ? [p] : Array.Empty<string>(),
         .. _limitsPath is { } l ? [l] : Array.Empty<string>(), .. _statuses.Select(s => s.Path)];

    public Reconciliation Reconciled => Matcher.Match(_billed.SelectMany(b => b.Claims), _remits.SelectMany(f => f.Claims), Today);
    public DepositReconciliation Banked => DepositMatcher.Match(_statements.SelectMany(s => s.Deposits), _remits.SelectMany(f => f.Payments));
    public IReadOnlyList<Underpayment> Underpaid => Underpayments.Find(_remits.SelectMany(f => f.Claims), Schedule, Tolerance);

    public IReadOnlyList<WorkItem> Work
    {
        get
        {
            var saved = new Dictionary<string, SavedState>();
            foreach (var folder in _remits.Select(f => Path.GetDirectoryName(f.Path)!).Distinct())
                foreach (var (k, v) in WorkStore.Load(folder)) saved[k] = v;
            return Worklist.Build(_remits.SelectMany(f => f.Claims), _limits, saved);
        }
    }

    public DateTime Now => (now ?? (() => DateTime.Now))();
    public DateOnly Today => DateOnly.FromDateTime(Now);
    public Action<WorkItem, WorkState, string>? MarkWork => null;
    public Func<Task<string>>? AskClearinghouse => null;

    public void Open(string path)
    {
        var full = Path.GetFullPath(path);
        var kind = FileKinds.Of(full) ?? throw new NotSupportedException($"{Path.GetFileName(full)} isn't an 835, an 837, a 277, a charges list, a bank statement, a fee schedule or payers' limits.");
        Close(full);
        switch (kind)
        {
            case "835": _remits.Add(RemitReader.Load(full)); break;
            case "837P" or "837I": _billed.Add(ClaimsReader.Read(X12File.Load(full), full)); break;
            case "charges list": _billed.Add(ChargesList.Load(full)); break;
            case "277": _statuses.Add((full, StatusReader.Read(X12File.Load(full), Path.GetFileName(full)))); break;
            case "bank statement": _statements.Add(BankStatement.Load(full)); break;
            case "fee schedule": Schedule = FeeSchedule.Load(full); _schedulePath = full; break;
            case "payers' limits": _limits.Clear(); _limits.AddRange(PayerLimits.Load(full)); _limitsPath = full; break;
            default: throw new NotSupportedException($"{Path.GetFileName(full)} is a {kind}: the program writes those, it doesn't read them.");
        }
    }

    public void Close(string path)
    {
        var full = Path.GetFullPath(path);
        _remits.RemoveAll(f => f.Path == full);
        _billed.RemoveAll(f => f.Path == full);
        _statements.RemoveAll(f => f.Path == full);
        _statuses.RemoveAll(f => f.Path == full);
        if (_schedulePath == full) { Schedule = null; _schedulePath = null; }
        if (_limitsPath == full) { _limits.Clear(); _limitsPath = null; }
    }

    public void Show(ViewState view) => View = view;

    public void Wrote276() => Status = Status with { ControlNumber = Status.ControlNumber + 1 };
}

public sealed class HeadlessDesk(TextWriter? log = null, bool seeRealValues = false, Func<DateTime>? now = null, decimal tolerance = 1.00m, StatusSettings? status = null) : IExplorerDesk
{
    public IExplorerSession Session { get; } = new HeadlessSession(seeRealValues, now, tolerance, status);

    public string? Busy => null;

    public void Report(string text) => log?.WriteLine(text);
}
