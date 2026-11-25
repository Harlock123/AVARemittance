using System.ComponentModel;
using System.Text.Json.Nodes;
using AVARemittance.Core;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Codes;
using AVARemittance.Core.Contracts;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.Matching;
using AVARemittance.Core.Privacy;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Reports;
using AVARemittance.Core.Status;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVARemittance.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with remittances, the claims billed, the bank's deposits, the fee schedule and
/// the denials, through the same readings the window shows. Things are known by handles: R01… payments, C01… claims in
/// the 835s, B01… billed claims, W01… denials to work, D01… deposits, P01… patients. Unless the person allows values,
/// every string an answer carries passes through <see cref="Masker"/> on the way out: names, IDs, accounts, claim, check
/// and trace numbers to their shape, dates to their year. Writing a 276 needs values allowed; asking the clearinghouse
/// and recording what was done with a denial need the open window. Failures come back as MCP tool errors.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case.</remarks>
public sealed partial class RemittanceTools(IExplorerHost host)
{
    private readonly Workspace _files = new(() => host.Root);

    public const int MaxResults = 300;
    private const string PaymentHelp = "A payment (one 835 transaction), by handle: R01 (list_payments lists them).";
    private const string ClaimHelp = "A claim in the open 835s, by handle: C01 (get_payment and list_payments list them).";
    private const string BilledHelp = "A billed claim (from an 837 or a charges list), by handle: B01 (list_billed lists them).";
    private const string WorkHelp = "A denial to work, by handle: W01 (get_worklist lists them).";

    // ───────────────────────────── handles ─────────────────────────────

    internal static IReadOnlyList<(string Handle, Payment Payment)> Payments(IExplorerSession s) =>
        [.. s.Remits.SelectMany(f => f.Payments).Select((p, i) => ($"R{i + 1:D2}", p))];

    internal static IReadOnlyList<(string Handle, ClaimPayment Claim)> Claims(IExplorerSession s) =>
        [.. s.Remits.SelectMany(f => f.Claims).Select((c, i) => ($"C{i + 1:D2}", c))];

    internal static IReadOnlyList<(string Handle, ClaimMatch Match)> BilledClaims(Reconciliation r) =>
        [.. r.Claims.Select((m, i) => ($"B{i + 1:D2}", m))];

    internal static IReadOnlyList<(string Handle, Deposit Deposit)> Deposits(IExplorerSession s) =>
        [.. s.Statements.SelectMany(x => x.Deposits).Select((d, i) => ($"D{i + 1:D2}", d))];

    /// <summary>Patients by name and member ID, across the 835s and the billed claims: P01….</summary>
    internal static IReadOnlyList<(string Handle, string Key)> Patients(IExplorerSession s) =>
        [.. s.Remits.SelectMany(f => f.Claims).Select(c => Key(c.Patient?.Last, c.Patient?.First))
            .Concat(s.Billed.SelectMany(b => b.Claims).Select(b => Key(b.Last, b.First)))
            .Where(k => k.Length > 1).Distinct().Select((k, i) => ($"P{i + 1:D2}", k))];

    private static string Key(string? last, string? first) => $"{last?.ToUpperInvariant()}|{first?.ToUpperInvariant()}";

    private static string? PatientOf(IExplorerSession s, string? last, string? first) =>
        Patients(s).FirstOrDefault(p => p.Key == Key(last, first)).Handle;

    internal static string Handle(IExplorerSession s, Payment p) => Payments(s).FirstOrDefault(x => x.Payment == p).Handle ?? "";
    internal static string Handle(IExplorerSession s, ClaimPayment c) => Claims(s).FirstOrDefault(x => x.Claim == c).Handle ?? "";

    private static Payment PaymentOf(IExplorerSession s, string handle) =>
        Payments(s).FirstOrDefault(x => x.Handle.Equals(handle.Trim(), StringComparison.OrdinalIgnoreCase)).Payment
        ?? throw new ArgumentException($"There's no payment {handle}: list_payments lists the payments in the open 835s.");

    private static ClaimPayment ClaimOf(IExplorerSession s, string handle) =>
        Claims(s).FirstOrDefault(x => x.Handle.Equals(handle.Trim(), StringComparison.OrdinalIgnoreCase)).Claim
        ?? throw new ArgumentException($"There's no claim {handle}: get_payment lists a payment's claims.");

    private static (string Handle, ClaimMatch Match) BilledOf(Reconciliation r, string handle) =>
        BilledClaims(r).FirstOrDefault(x => x.Handle.Equals(handle.Trim(), StringComparison.OrdinalIgnoreCase)) is { Match: not null } found ? found
        : throw new ArgumentException($"There's no billed claim {handle}: list_billed lists them (open the 837s or a charges list first).");

    private static WorkItem WorkOf(IExplorerSession s, string handle) =>
        s.Work.FirstOrDefault(w => w.Id.Equals(handle.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There's no denial {handle}: get_worklist lists them.");

    private static Masker MaskerFor(IExplorerSession s) => new(s.Remits, s.Billed, s.Statements, s.Statuses);

    // ───────────────────────────── files ─────────────────────────────

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the files the program reads in the working folder and its subfolders, with their kind: 835 remittances, 837 claims, 277 claim status answers, charges lists, bank statements (CSV, Excel or BAI2), fee schedules and payers' limits.")]
    public Task<string> ListFiles(
        [Description("A folder inside the working folder to list instead. Optional.")] string? folder = null,
        CancellationToken cancel = default) =>
        Session("listed the files", s =>
        {
            var (list, more) = _files.List(folder);
            var o = new JsonObject
            {
                ["folder"] = _files.Root,
                ["files"] = new JsonArray([.. list.Select(f => (JsonNode)new JsonObject { ["file"] = f, ["kind"] = FileKinds.Of(_files.Resolve(f)) })]),
            };
            if (more) o["note"] = $"Only the first {Workspace.MaxListed} are listed. Name a folder to see the rest.";
            return o;
        }, cancel);

    [McpServerTool(Name = "open_file", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Opens a file from the working folder: an 835, an 837 or charges list (what was billed), a 277, a bank statement, a fee schedule or the payers' limits. Each one opened adds to the matching, the deposits, the underpayments and the denials.")]
    public Task<string> OpenFile(
        [Description("The file, relative to the working folder.")] string file,
        CancellationToken cancel = default) =>
        Session("opened " + file, s =>
        {
            var full = _files.Existing(file);
            s.Open(full);
            var o = new JsonObject { ["opened"] = _files.Relative(full), ["kind"] = FileKinds.Of(full) };
            if (s.Remits.FirstOrDefault(f => f.Path == full) is { } r)
            {
                o["payments"] = new JsonArray([.. r.Payments.Select(p => (JsonNode)PaymentLine(s, p))]);
                o["balances"] = Checks.Run(r).Count == 0;
                if (r.Problems.Count > 0) o["problems"] = Strings(r.Problems);
            }
            else if (s.Billed.FirstOrDefault(f => f.Path == full) is { } b)
            {
                o["claims"] = b.Claims.Count;
                o["billed"] = b.Claims.Sum(c => c.Charge);
                o["answered"] = s.Reconciled.Claims.Count(m => m.Billed.Source == b.Name && m.Answers.Count > 0);
            }
            else if (s.Statements.FirstOrDefault(x => x.Path == full) is { } st)
            {
                o["deposits"] = st.Deposits.Count;
                o["matched"] = s.Banked.Matched.Count(m => m.Deposit?.Source == st.Name);
            }
            else if (s.Schedule?.Path == full) o["rates"] = s.Schedule.Rates.Count;
            else if (s.StatusFiles.Contains(full)) o["claims"] = s.Statuses.Count(a => a.File == Path.GetFileName(full));
            else o["payers"] = s.Limits.Count;
            return o;
        }, cancel);

    [McpServerTool(Name = "close_file", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Closes an open file.")]
    public Task<string> CloseFile(
        [Description("The file, relative to the working folder.")] string file,
        CancellationToken cancel = default) =>
        Session("closed " + file, s =>
        {
            s.Close(_files.Resolve(file));
            return new JsonObject { ["closed"] = file };
        }, cancel);

    [McpServerTool(Name = "list_open", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What's open: the 835s with their payments, the billed claims' files, the bank statements, the fee schedule, the payers' limits and the 277s.")]
    public Task<string> ListOpen(CancellationToken cancel = default) =>
        Session("listed what's open", s => new JsonObject
        {
            ["remittances"] = new JsonArray([.. s.Remits.Select(f => (JsonNode)new JsonObject { ["file"] = _files.Relative(f.Path), ["payments"] = string.Join(", ", f.Payments.Select(p => Handle(s, p))), ["claims"] = f.Claims.Count() })]),
            ["billed"] = new JsonArray([.. s.Billed.Select(b => (JsonNode)new JsonObject { ["file"] = _files.Relative(b.Path), ["kind"] = b.Kind, ["claims"] = b.Claims.Count })]),
            ["bank_statements"] = new JsonArray([.. s.Statements.Select(b => (JsonNode)new JsonObject { ["file"] = _files.Relative(b.Path), ["deposits"] = b.Deposits.Count })]),
            ["fee_schedule"] = s.Schedule is { } f ? _files.Relative(f.Path) : null,
            ["payers_limits"] = s.Limits.Count > 0 ? string.Join(", ", s.Limits.Select(l => l.Name)) : null,
            ["claim_status"] = new JsonArray([.. s.StatusFiles.Select(p => (JsonNode)_files.Relative(p))]),
        }, cancel);

    // ───────────────────────────── remittances ─────────────────────────────

    private static JsonObject PaymentLine(IExplorerSession s, Payment p) => new()
    {
        ["payment"] = Handle(s, p), ["payer"] = p.Payer.Name, ["amount"] = p.Total, ["method"] = p.MethodWord, ["paid_on"] = Money.Day(p.PaidOn),
        ["claims"] = p.Claims.Count, ["says"] = Words.Summary(p),
    };

    private static JsonObject ClaimLine(IExplorerSession s, ClaimPayment c) => new()
    {
        ["claim"] = Handle(s, c), ["account"] = c.Account, ["patient"] = PatientOf(s, c.Patient?.Last, c.Patient?.First), ["name"] = c.Patient?.SortName,
        ["state"] = Words.State(c.State), ["charge"] = c.Charge, ["paid"] = c.Paid, ["patient_owes"] = c.PatientResponsibility, ["says"] = Words.Summary(c),
    };

    [McpServerTool(Name = "list_payments", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Every payment in the open 835s, by handle (R01…): the payer, amount, method (EFT or check), date, how many claims, and whether it balances; with each payment's claims by handle (C01…) and what happened to each (paid, partly denied, denied, patient owes, reversal).")]
    public Task<string> ListPayments(CancellationToken cancel = default) =>
        Session("listed the payments", s => new JsonObject
        {
            ["payments"] = new JsonArray([.. Payments(s).Select(x =>
            {
                var o = PaymentLine(s, x.Payment);
                o["balances"] = Checks.Run(x.Payment).Count == 0;
                o["claim_list"] = new JsonArray([.. x.Payment.Claims.Take(MaxResults).Select(c => (JsonNode)$"{Handle(s, c)} {c.Account} {Words.State(c.State)} {Money.Say(c.Paid)}")]);
                return (JsonNode)o;
            })]),
        }, cancel);

    [McpServerTool(Name = "get_payment", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A payment in full: the money, method, trace or check number and date, the payer and payee, the provider-level adjustments (recoupments taken, interest added) in words, whether it balances (and what doesn't), and its claims.")]
    public Task<string> GetPayment(
        [Description(PaymentHelp)] string payment,
        CancellationToken cancel = default) =>
        Session("read a payment", s =>
        {
            var p = PaymentOf(s, payment);
            var o = PaymentLine(s, p);
            o["trace"] = p.Trace;
            o["payer_contacts"] = Strings(p.Payer.Contacts);
            o["payee"] = p.Payee.Name;
            o["provider_adjustments"] = Strings(p.ProviderAdjustments.Select(Words.Describe));
            o["balance_problems"] = Strings(Checks.Run(p).Select(f => f.ToString()));
            o["claim_list"] = new JsonArray([.. p.Claims.Take(MaxResults).Select(c => (JsonNode)ClaimLine(s, c))]);
            return o;
        }, cancel);

    [McpServerTool(Name = "get_claim", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A claim's payment in full: its status, what was billed, paid and adjusted, and each line with its charge, allowed amount, payment, every adjustment (group, reason code and its words, and whose it is to pay) and remark code in words; the claim-level adjustments, references and amounts.")]
    public Task<string> GetClaim(
        [Description(ClaimHelp)] string claim,
        CancellationToken cancel = default) =>
        Session("read a claim", s =>
        {
            var c = ClaimOf(s, claim);
            var o = ClaimLine(s, c);
            o["status"] = RemitCodes.Say(RemitCodes.ClaimStatus, c.Status);
            o["payer"] = c.Payment.Payer.Name;
            o["payment"] = Handle(s, c.Payment);
            o["payer_claim"] = c.PayerClaim;
            o["member_id"] = c.Patient?.Id;
            o["service"] = c.From is { } f ? Money.Day(f) : null;
            o["claim_adjustments"] = Strings(c.Adjustments.Select(Words.Describe));
            o["claim_remarks"] = Strings(c.Remarks.Select(Words.Describe));
            o["references"] = Strings(c.References.Select(Words.Describe));
            o["amounts"] = Strings(c.Amounts.Select(Words.Describe));
            o["lines"] = new JsonArray([.. c.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["line"] = l.Number, ["code"] = Words.Code(l), ["units"] = l.Units, ["date"] = Money.Day(l.From), ["charge"] = l.Charge, ["allowed"] = l.Allowed, ["paid"] = l.Paid,
                ["denied"] = l.Denied ? true : null,
                ["adjustments"] = Strings(l.Adjustments.Select(Words.Describe)), ["remarks"] = Strings(l.Remarks.Select(Words.Describe)),
            })]);
            o["balance_problems"] = Strings(Checks.Run(c.Payment).Where(x => x.Claim == c).Select(x => x.ToString()));
            return o;
        }, cancel);

    [McpServerTool(Name = "get_segments", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The raw X12 segments of a claim (C01) or a payment's header (R01), numbered as in the file, for checking exactly what the payer sent.")]
    public Task<string> GetSegments(
        [Description("A claim (C01) or a payment (R01).")] string handle,
        CancellationToken cancel = default) =>
        Session("read the segments", s =>
        {
            var h = handle.Trim().ToUpperInvariant();
            var segments = h.StartsWith('R') ? PaymentOf(s, h).Header : ClaimOf(s, h).AllSegments.ToList();
            return new JsonObject { ["handle"] = h, ["segments"] = new JsonArray([.. segments.Take(MaxResults).Select(x => (JsonNode)$"{x.Index + 1}: {x.Text}")]) };
        }, cancel);

    [McpServerTool(Name = "check_balance", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Whether each open 835 balances: the payment against its claims and provider adjustments (B1), each claim against its adjustments (B2) and lines (B4), each line (B3), the patient's share (B5), a payment of nothing (B6), a reversal's signs (B7), the segment count (E1). Each problem with the rule's why and source.")]
    public Task<string> CheckBalance(
        [Description("Only this file, relative to the working folder. Optional.")] string? file = null,
        CancellationToken cancel = default) =>
        Session("checked the balancing", s =>
        {
            var only = file == null ? null : _files.Resolve(file);
            var found = s.Remits.Where(f => only == null || f.Path == only).SelectMany(Checks.Run).ToList();
            return new JsonObject
            {
                ["balances"] = found.All(f => f.Rule.Severity != Severity.Error),
                ["problems"] = new JsonArray([.. found.Take(MaxResults).Select(f => (JsonNode)new JsonObject
                {
                    ["rule"] = f.Rule.Id, ["severity"] = f.Rule.Severity.ToString().ToLowerInvariant(), ["says"] = f.Message, ["payment"] = Handle(s, f.Payment),
                    ["claim"] = f.Claim is { } c ? Handle(s, c) : null, ["why"] = f.Rule.Why, ["source"] = f.Rule.Source,
                })]),
            };
        }, cancel);

    // ───────────────────────────── codes ─────────────────────────────

    private static readonly Dictionary<string, Func<string, string>> Lists = new()
    {
        ["carc"] = CodeList.Carc.Say, ["rarc"] = CodeList.Rarc.Say, ["status_category"] = CodeList.StatusCategory.Say, ["status"] = CodeList.Status.Say,
        ["group"] = c => RemitCodes.Say(RemitCodes.Group, c) + (RemitCodes.GroupMeaning.TryGetValue(c.Trim(), out var m) ? $": {m}" : ""),
        ["provider_adjustment"] = c => RemitCodes.Say(RemitCodes.ProviderAdjustment, c), ["claim_status"] = c => RemitCodes.Say(RemitCodes.ClaimStatus, c),
        ["method"] = c => RemitCodes.Say(RemitCodes.Method, c), ["filing"] = c => RemitCodes.Say(RemitCodes.Filing, c), ["reference"] = c => RemitCodes.Say(RemitCodes.Reference, c),
    };

    [McpServerTool(Name = "explain_code", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What a code means: carc (claim adjustment reason code, CAS02), rarc (remark code, LQ02, MOA, MIA), group (CAS01: CO, PR, OA, PI, CR, and whose it is to pay), provider_adjustment (PLB: WO, L6…), claim_status (CLP02), status_category and status (a 277's STC), method (BPR04), filing (CLP06), reference (REF01). The X12 lists' own words; for a CARC, what a denial with it asks for.")]
    public Task<string> ExplainCode(
        [Description("The list: carc, rarc, group, provider_adjustment, claim_status, status_category, status, method, filing or reference.")] string list,
        [Description("The code: 45, N822, CO, WO, 22, A4…")] string code,
        CancellationToken cancel = default) =>
        Session("explained a code", s =>
        {
            var key = list.Trim().ToLowerInvariant();
            var say = Lists.GetValueOrDefault(key) ?? throw new ArgumentException($"list is one of {string.Join(", ", Lists.Keys)}.");
            var o = new JsonObject { ["list"] = key, ["code"] = code.Trim(), ["meaning"] = say(code) };
            if (key == "carc")
            {
                var entry = CodeList.Carc[code];
                if (entry?.Stop is { } stop) o["stopped"] = Money.Day(stop);
                var action = Classifier.For(new Adjustment("CO", code.Trim(), 1, null, new Core.X12.Segment("CAS", Core.X12.Delimiters.Standard)), []);
                o["as_a_denial"] = action is { } a ? Classifier.Say(a) : "a write-down to the contract, not a denial";
            }
            return o;
        }, cancel);

    [McpServerTool(Name = "search_codes", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Finds CARCs, RARCs or 277 status codes whose words have all the words given (\"prior authorization\", \"missing modifier\").")]
    public Task<string> SearchCodes(
        [Description("The words to look for.")] string words,
        [Description("carc, rarc, status_category or status. Optional: all four.")] string? list = null,
        CancellationToken cancel = default) =>
        Session("searched the codes", s =>
        {
            var lists = new Dictionary<string, CodeList> { ["carc"] = CodeList.Carc, ["rarc"] = CodeList.Rarc, ["status_category"] = CodeList.StatusCategory, ["status"] = CodeList.Status };
            var chosen = list == null ? lists : lists.Where(kv => kv.Key.Equals(list.Trim(), StringComparison.OrdinalIgnoreCase)).ToDictionary();
            if (chosen.Count == 0) throw new ArgumentException("list is carc, rarc, status_category or status.");
            var hits = chosen.SelectMany(kv => kv.Value.Search(words).Select(c => (JsonNode)$"{kv.Key} {c.Code}: {c.Text}" + (c.Stop is { } st ? $" (stopped {Money.Day(st)})" : ""))).Take(60);
            return new JsonObject { ["found"] = new JsonArray([.. hits]) };
        }, cancel);

    // ───────────────────────────── what was billed ─────────────────────────────

    [McpServerTool(Name = "list_billed", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Every billed claim (from the open 837s or charges list), by handle (B01…), matched to the open 835s: paid, partly paid, denied, patient owes, reversed, paid twice, or no answer (with the days since it was billed); what was paid and the patient's share; and the 835 claims nothing open billed.")]
    public Task<string> ListBilled(
        [Description("Only one state: paid, partly paid, denied, patient owes, reversed, paid twice or no answer. Optional.")] string? only = null,
        CancellationToken cancel = default) =>
        Session("listed the billed claims", s =>
        {
            var r = s.Reconciled;
            if (s.Billed.Count == 0) throw new InvalidOperationException("Nothing billed is open: open_file opens the 837s or a charges list.");
            return new JsonObject
            {
                ["counts"] = new JsonObject(r.Claims.GroupBy(m => MatchWords.State(m.State)).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
                ["claims"] = new JsonArray([.. BilledClaims(r).Where(x => only == null || MatchWords.State(x.Match.State).Equals(only.Trim(), StringComparison.OrdinalIgnoreCase)).Take(MaxResults).Select(x => (JsonNode)new JsonObject
                {
                    ["billed"] = x.Handle, ["account"] = x.Match.Billed.Account, ["patient"] = PatientOf(s, x.Match.Billed.Last, x.Match.Billed.First), ["payer"] = x.Match.Billed.Payer,
                    ["state"] = MatchWords.State(x.Match.State), ["charge"] = x.Match.Billed.Charge, ["paid"] = x.Match.Paid, ["patient_owes"] = x.Match.PatientOwes,
                    ["days_waiting"] = x.Match.State == BilledState.NoAnswer ? r.DaysWaiting(x.Match) : null,
                    ["answers"] = x.Match.Answers.Count > 0 ? string.Join(", ", x.Match.Answers.Select(a => Handle(s, a))) : null,
                })]),
                ["not_billed_here"] = Strings(r.Unbilled.Select(u => $"{Handle(s, u)} {u.Account}")),
            };
        }, cancel);

    [McpServerTool(Name = "get_billed", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A billed claim against its answers: what was billed and when, each 835 answer in order (a payment, a reversal and its correction, the pieces of a split claim, a second payment), each line billed with what paid it, and its latest 277 status when one is open.")]
    public Task<string> GetBilled(
        [Description(BilledHelp)] string billed,
        CancellationToken cancel = default) =>
        Session("read a billed claim", s =>
        {
            var r = s.Reconciled;
            var (h, m) = BilledOf(r, billed);
            return new JsonObject
            {
                ["billed"] = h, ["account"] = m.Billed.Account, ["patient"] = PatientOf(s, m.Billed.Last, m.Billed.First), ["name"] = m.Billed.SortName, ["payer"] = m.Billed.Payer,
                ["from"] = m.Billed.Source, ["state"] = MatchWords.State(m.State), ["says"] = MatchWords.Describe(m, r), ["matched_by"] = m.Answers.Count > 0 ? m.By : null,
                ["answers"] = new JsonArray([.. m.Answers.Select(a => (JsonNode)ClaimLine(s, a))]),
                ["lines"] = new JsonArray([.. m.Lines.Select(l => (JsonNode)MatchWords.Describe(l))]),
                ["status"] = StatusReader.For(m.Billed, s.Statuses) is { } st ? StatusReader.Describe(st) : null,
            };
        }, cancel);

    // ───────────────────────────── the money ─────────────────────────────

    [McpServerTool(Name = "get_deposits", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The bank statements' deposits (D01…) matched to the 835 payments (by trace or check number, then amount and date): each match and any difference (named when a recoupment or interest explains it), insurance money with no 835, 835 payments with no deposit, and other deposits.")]
    public Task<string> GetDeposits(CancellationToken cancel = default) =>
        Session("read the deposits", s =>
        {
            if (s.Statements.Count == 0) throw new InvalidOperationException("No bank statement is open: open_file opens one (a CSV, Excel or BAI2 file).");
            var b = s.Banked;
            var handles = Deposits(s);
            string H(Deposit? d) => handles.FirstOrDefault(x => x.Deposit == d).Handle ?? "";
            return new JsonObject
            {
                ["matched"] = new JsonArray([.. b.Matched.Select(m => (JsonNode)new JsonObject
                {
                    ["deposit"] = H(m.Deposit), ["payments"] = string.Join(", ", m.Payments.Select(p => Handle(s, p))), ["by"] = m.By, ["difference"] = m.Difference, ["says"] = DepositMatcher.Describe(m),
                })]),
                ["insurance_money_with_no_835"] = new JsonArray([.. b.NoRemittance.Select(d => (JsonNode)new JsonObject { ["deposit"] = H(d), ["amount"] = d.Amount, ["date"] = Money.Day(d.Date), ["description"] = d.Description, ["trace"] = d.Trace.Length > 0 ? d.Trace : null })]),
                ["payments_with_no_deposit"] = new JsonArray([.. b.NoMoney.Select(p => (JsonNode)PaymentLine(s, p))]),
                ["other_deposits"] = new JsonArray([.. b.Other.Select(d => (JsonNode)new JsonObject { ["deposit"] = H(d), ["amount"] = d.Amount, ["date"] = Money.Day(d.Date), ["description"] = d.Description })]),
            };
        }, cancel);

    [McpServerTool(Name = "posting_summary", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What to post, by the day the 835s paid: payments and claims, received, posted to claims, unapplied (paid on claims nothing open billed), adjusted off, the patients' share, recouped, interest added, and deposited (with a statement open).")]
    public Task<string> PostingSummary(CancellationToken cancel = default) =>
        Session("read the posting summary", s =>
        {
            if (s.Remits.Count == 0) throw new InvalidOperationException("No 835 is open: open_file opens one.");
            var days = Posting.Days(s.Remits.SelectMany(f => f.Payments), s.Reconciled, s.Statements.Count > 0 ? s.Banked : null);
            return new JsonObject
            {
                ["days"] = new JsonArray([.. days.Select(d => (JsonNode)new JsonObject
                {
                    ["date"] = Money.Day(d.Date), ["payments"] = string.Join(", ", d.Payments.Select(p => Handle(s, p))), ["claims"] = d.Claims, ["received"] = d.Received, ["posted"] = d.Posted,
                    ["unapplied"] = d.Unapplied, ["adjusted_off"] = d.Adjusted, ["patients"] = d.PatientResponsibility, ["recouped"] = d.Recouped, ["added"] = d.Added,
                    ["deposited"] = s.Statements.Count > 0 ? d.Deposited : null,
                })]),
            };
        }, cancel);

    [McpServerTool(Name = "export_posting", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the posting summary, with every claim, as a new .html page or .xlsx workbook in the working folder. People's details are masked in it unless the person allows values.")]
    public Task<string> ExportPosting(
        [Description("The new file, relative to the working folder: .html or .xlsx.")] string output,
        CancellationToken cancel = default) =>
        Write("exported the posting summary", output, "A posting summary", [".html", ".htm", ".xlsx"], (s, full) =>
        {
            if (s.Remits.Count == 0) throw new InvalidOperationException("No 835 is open: open_file opens one.");
            var days = Posting.Days(s.Remits.SelectMany(f => f.Payments), s.Reconciled, s.Statements.Count > 0 ? s.Banked : null);
            PostingReport.Write(full, days, s.SeeRealValues ? null : MaskerFor(s).Text);
            return new JsonObject { ["days"] = days.Count, ["claims"] = days.Sum(d => d.Claims) };
        }, cancel);

    // ───────────────────────────── underpayments ─────────────────────────────

    [McpServerTool(Name = "get_underpayments", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lines allowed below the open fee schedule by more than the tolerance (U1), lines whose own amounts don't agree (U2), and contractual write-downs larger than the schedule explains (U3), each with the schedule rows it rests on; and each payer's total below the schedule by procedure. Below what the schedule expects, never wrong: the schedule is the person's.")]
    public Task<string> GetUnderpayments(
        [Description("Only this payer (its name). Optional.")] string? payer = null,
        [Description("A tolerance in dollars to try instead of the person's (what-if; the setting isn't changed). Optional.")] decimal? tolerance = null,
        CancellationToken cancel = default) =>
        Session("read the underpayments", s =>
        {
            if (s.Schedule == null) throw new InvalidOperationException("No fee schedule is open: open_file opens one (Payer, Procedure, Modifier, Rate…).");
            if (tolerance is < 0) throw new ArgumentException("tolerance is 0 or more dollars.");
            var found = (tolerance is { } t ? Underpayments.Find(s.Remits.SelectMany(f => f.Claims), s.Schedule, t) : s.Underpaid)
                .Where(u => payer == null || u.Claim.Payment.Payer.Name.Contains(payer.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return new JsonObject
            {
                ["tolerance"] = tolerance ?? s.Tolerance, ["schedule"] = s.Schedule.Name,
                ["below_total"] = found.Where(u => u.Kind != "U2").Sum(u => u.Below),
                ["lines"] = new JsonArray([.. found.Take(MaxResults).Select(u => (JsonNode)new JsonObject
                {
                    ["kind"] = u.Kind, ["claim"] = Handle(s, u.Claim), ["line"] = u.Line.Number, ["code"] = Words.Code(u.Line), ["payer"] = u.Claim.Payment.Payer.Name,
                    ["allowed"] = u.Allowed, ["expected"] = u.Expected?.Amount, ["below"] = u.Below, ["says"] = u.Message,
                })]),
                ["by_payer_and_procedure"] = new JsonArray([.. Underpayments.ByCode(found).Select(c => (JsonNode)new JsonObject { ["payer"] = c.Payer, ["procedure"] = c.Procedure, ["lines"] = c.Lines, ["below"] = c.Below })]),
            };
        }, cancel);

    // ───────────────────────────── denials ─────────────────────────────

    private JsonObject WorkLine(IExplorerSession s, WorkItem w) => new()
    {
        ["denial"] = w.Id, ["claim"] = Handle(s, w.Claim), ["account"] = w.Claim.Account, ["patient"] = PatientOf(s, w.Claim.Patient?.Last, w.Claim.Patient?.First),
        ["payer"] = w.Claim.Payment.Payer.Name, ["action"] = Classifier.Say(w.Action), ["amount"] = w.Amount,
        ["codes"] = string.Join(", ", w.Reasons.Select(r => $"{r.Group}-{r.Reason}").Distinct().Concat(w.Remarks.Select(r => r.Code))),
        ["deadline"] = w.Deadline is { } d ? Money.Day(d) : null, ["days_left"] = w.DaysLeft(s.Today), ["state"] = Classifier.Say(w.State),
    };

    [McpServerTool(Name = "get_worklist", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Every denial in the open 835s as a work item (W01…), sorted by deadline: what its codes ask for (fix and resubmit, appeal, bill elsewhere, bill the patient, write off, bundled, review), the amount at stake, the days left by the payers' limits (when open), and what's been done.")]
    public Task<string> GetWorklist(
        [Description("Only one action: fix and resubmit, appeal, bill elsewhere, bill the patient, write off, bundled or review. Optional.")] string? action = null,
        [Description("Only items due within this many days. Optional.")] int? within_days = null,
        [Description("Include the ones already done. Default false.")] bool include_done = false,
        CancellationToken cancel = default) =>
        Session("read the worklist", s =>
        {
            if (s.Remits.Count == 0) throw new InvalidOperationException("No 835 is open: open_file opens one.");
            var work = s.Work.Where(w => (include_done || !w.Done) && (action == null || Classifier.Say(w.Action).Equals(action.Trim(), StringComparison.OrdinalIgnoreCase))
                && (within_days == null || w.DaysLeft(s.Today) is { } d && d <= within_days)).ToList();
            var open = s.Work.Where(w => !w.Done && w.Action != WorkAction.Bundled).ToList();
            return new JsonObject
            {
                ["to_work"] = open.Count, ["at_stake"] = open.Sum(w => w.Amount), ["limits_open"] = s.Limits.Count > 0,
                ["denials"] = new JsonArray([.. work.Take(MaxResults).Select(w => (JsonNode)WorkLine(s, w))]),
            };
        }, cancel);

    [McpServerTool(Name = "get_work_item", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("One denial in full: the claim and lines, each reason and remark code in words, what to do, the deadline and what it's counted from, and what's been done.")]
    public Task<string> GetWorkItem(
        [Description(WorkHelp)] string denial,
        CancellationToken cancel = default) =>
        Session("read a denial", s =>
        {
            var w = WorkOf(s, denial);
            var o = WorkLine(s, w);
            o["lines"] = Strings(w.Lines.Select(l => $"{l.Number} {Words.Code(l)} {Money.Say(l.Charge)}"));
            o["reasons"] = Strings(w.Reasons.DistinctBy(r => (r.Group, r.Reason)).Select(Words.Describe));
            o["remarks"] = Strings(w.Remarks.Select(Words.Describe));
            o["what_to_do"] = w.WhatToDo;
            o["deadline_because"] = w.DeadlineBecause.Length > 0 ? w.DeadlineBecause : null;
            o["done_on"] = w.StateDate is { } d ? Money.Day(d) : null;
            o["note"] = w.Note.Length > 0 ? w.Note : null;
            return o;
        }, cancel);

    [McpServerTool(Name = "draft_appeal", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes an appeal letter draft for a denial as a new .md, .html or .docx file in the working folder: the claim, the codes in words and the argument they call for, with [brackets] for what only a person can add. It says it's a draft. People's details are masked in it unless the person allows values. Nothing is sent.")]
    public Task<string> DraftAppeal(
        [Description(WorkHelp)] string denial,
        [Description("The new file, relative to the working folder: .md, .html or .docx.")] string output,
        CancellationToken cancel = default) =>
        Write("drafted an appeal", output, "An appeal draft", [".md", ".html", ".htm", ".docx"], (s, full) =>
        {
            var w = WorkOf(s, denial);
            Letters.WriteAppeal(full, w, s.Today, s.SeeRealValues ? null : MaskerFor(s).Text);
            return new JsonObject
            {
                ["denial"] = w.Id, ["draft"] = true, ["masked"] = !s.SeeRealValues,
                ["note"] = s.SeeRealValues ? "A draft: the person fills in every [bracket] and checks it before it goes." : "A draft with people's details masked: the person drafts it again from the window (Denials › Draft the Appeal Letter) to have them in.",
            };
        }, cancel);

    [McpServerTool(Name = "corrected_claim", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What to change before a denial's corrected claim goes out: the frequency (7), the payer's claim number in REF*F8, and the fix its remark codes call for. A list for the person, not an edited 837.")]
    public Task<string> CorrectedClaim(
        [Description(WorkHelp)] string denial,
        CancellationToken cancel = default) =>
        Session("listed a corrected claim's changes", s =>
        {
            var w = WorkOf(s, denial);
            if (w.Action != WorkAction.FixAndResubmit) throw new InvalidOperationException($"{w.Id} is {Classifier.Say(w.Action)}, not fix and resubmit.");
            return new JsonObject { ["denial"] = w.Id, ["changes"] = Strings(Letters.Corrected(w)) };
        }, cancel);

    [McpServerTool(Name = "mark_work", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Records what was done with a denial: appealed, resubmitted, billed elsewhere, billed to the patient, written off, or open again; kept beside the 835 in remittance-work.json. Only in the open window, where the person sees it, and only for what the person says was done.")]
    public Task<string> MarkWork(
        [Description(WorkHelp)] string denial,
        [Description("appealed, resubmitted, billed elsewhere, billed to the patient, written off or open.")] string state,
        [Description("A note to keep with it. Optional.")] string? note = null,
        CancellationToken cancel = default) =>
        Session("recorded a denial", s =>
        {
            if (!host.Live || s.MarkWork is not { } mark) throw new InvalidOperationException("That's only done in the open window, where the person sees it (AVARemittance --mcp --attach, or its AI Chat).");
            var w = WorkOf(s, denial);
            var to = Enum.GetValues<WorkState>().FirstOrDefault(x => Classifier.Say(x).Equals(state.Trim(), StringComparison.OrdinalIgnoreCase), (WorkState)(-1));
            if ((int)to < 0) throw new ArgumentException("state is appealed, resubmitted, billed elsewhere, billed to the patient, written off or open.");
            mark(w, to, note ?? "");
            return new JsonObject { ["denial"] = w.Id, ["state"] = Classifier.Say(to) };
        }, cancel);

    [McpServerTool(Name = "export_denials", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the denials worklist as a new .html page or .xlsx workbook in the working folder. People's details are masked in it unless the person allows values.")]
    public Task<string> ExportDenials(
        [Description("The new file, relative to the working folder: .html or .xlsx.")] string output,
        CancellationToken cancel = default) =>
        Write("exported the denials", output, "A denials report", [".html", ".htm", ".xlsx"], (s, full) =>
        {
            var work = s.Work;
            if (work.Count == 0) throw new InvalidOperationException("No denials in the open 835s.");
            DenialsReport.Write(full, work, s.Today, s.SeeRealValues ? null : MaskerFor(s).Text);
            return new JsonObject { ["denials"] = work.Count };
        }, cancel);

    // ───────────────────────────── claim status ─────────────────────────────

    private static IReadOnlyList<ClaimMatch> Waiting(IExplorerSession s, int? days)
    {
        var r = s.Reconciled;
        return [.. r.Claims.Where(m => m.State == BilledState.NoAnswer && r.DaysWaiting(m) >= (days ?? s.Status.AfterDays))];
    }

    [McpServerTool(Name = "claims_waiting", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The billed claims with no answer in the open 835s after so many days (the person's setting unless given), with the days waiting and the latest 277 status for each when one is open.")]
    public Task<string> ClaimsWaiting(
        [Description("Days since billed to count as waiting. Optional: the person's setting.")] int? days = null,
        CancellationToken cancel = default) =>
        Session("listed the claims waiting", s =>
        {
            if (s.Billed.Count == 0) throw new InvalidOperationException("Nothing billed is open: open_file opens the 837s or a charges list.");
            var r = s.Reconciled;
            var handles = BilledClaims(r);
            return new JsonObject
            {
                ["after_days"] = days ?? s.Status.AfterDays,
                ["claims"] = new JsonArray([.. Waiting(s, days).Select(m => (JsonNode)new JsonObject
                {
                    ["billed"] = handles.First(x => x.Match.Billed == m.Billed).Handle, ["account"] = m.Billed.Account, ["payer"] = m.Billed.Payer, ["charge"] = m.Billed.Charge,
                    ["days_waiting"] = r.DaysWaiting(m), ["status"] = StatusReader.For(m.Billed, s.Statuses) is { } st ? StatusReader.Describe(st) : null,
                })]),
            };
        }, cancel);

    [McpServerTool(Name = "get_status", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Every claim in the open 277s: its account, the payer's claim number, each status (category and code in words) and what it means to do.")]
    public Task<string> GetStatus(CancellationToken cancel = default) =>
        Session("read the claim status", s =>
        {
            if (s.Statuses.Count == 0) throw new InvalidOperationException("No 277 is open: open_file opens one.");
            return new JsonObject
            {
                ["claims"] = new JsonArray([.. s.Statuses.Take(MaxResults).Select(a => (JsonNode)new JsonObject
                {
                    ["file"] = a.File, ["payer"] = a.Payer, ["account"] = a.Account, ["payer_claim"] = a.PayerClaim.Length > 0 ? a.PayerClaim : null,
                    ["statuses"] = Strings(a.Statuses.Select(x => x.Text)), ["means"] = a.Meaning,
                })]),
            };
        }, cancel);

    [McpServerTool(Name = "write_status_request", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the 276 claim status request for the claims waiting as a new .x12 file in the working folder, one transaction per payer. It holds people's details, so it needs the person to allow values.")]
    public Task<string> WriteStatusRequest(
        [Description("The new .x12 file, relative to the working folder.")] string output,
        [Description("Days since billed to count as waiting. Optional: the person's setting.")] int? days = null,
        CancellationToken cancel = default) =>
        Write("wrote a 276", output, "A 276", [".x12", ".edi", ".txt"], (s, full) =>
        {
            if (!s.SeeRealValues) throw new InvalidOperationException("A 276 holds patients' names, birth dates and member IDs, and agents see values masked here: the person writes it (Claim Status › Write the 276), or lets agents see values (AI menu).");
            var waiting = Waiting(s, days);
            if (waiting.Count == 0) throw new InvalidOperationException("No claim is waiting that long.");
            var built = StatusRequest.Build([.. waiting.Select(m => m.Billed)], s.Status, s.Now);
            File.WriteAllText(full, built.Text);
            s.Wrote276();
            return new JsonObject { ["claims"] = built.Traces.Count, ["payers"] = built.Transactions };
        }, cancel);

    [McpServerTool(Name = "ask_clearinghouse", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = true)]
    [Description("Asks the clearinghouse about the claims waiting in real time (CAQH CORE, one 276 per claim), and opens the 277s that come back. Only in the open window, with the clearinghouse the person set up, and only when the person says yes.")]
    public Task<string> AskClearinghouse(CancellationToken cancel = default) =>
        Run("asked the clearinghouse", async s =>
        {
            if (!host.Live || s.AskClearinghouse is not { } ask)
                throw new InvalidOperationException("That's only done in the open window, with the person saying yes (AVARemittance --mcp --attach, or its AI Chat).");
            var said = await ask();
            return Finish(s, new JsonObject { ["said"] = said, ["status_answers"] = s.Statuses.Count });
        }, cancel);

    // ───────────────────────────── the view ─────────────────────────────

    private static readonly string[] Tabs = ["remittance", "checks", "claims", "deposits", "underpayments", "denials", "status"];

    [McpServerTool(Name = "get_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What the window shows: the tab, and what's selected (a payment R01, a claim C01, a billed claim B01 or a denial W01).")]
    public Task<string> GetView(CancellationToken cancel = default) =>
        Session("looked at the view", s => new JsonObject { ["tab"] = s.View.Tab, ["selected"] = s.View.Handle, ["live"] = host.Live }, cancel);

    [McpServerTool(Name = "show_in_view", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Shows the person something in their window: a payment (R01) or claim (C01) on the remittance tab, a billed claim (B01) on the claims tab, a denial (W01) on the denials tab, or a tab: remittance, checks, claims, deposits, underpayments, denials or status. Headless, it only remembers it (render_view draws it).")]
    public Task<string> ShowInView(
        [Description("R01, C01, B01 or W01. Optional.")] string? handle = null,
        [Description("The tab: remittance, checks, claims, deposits, underpayments, denials or status. Optional: the one the handle belongs on.")] string? tab = null,
        CancellationToken cancel = default) =>
        Session("showed something", s =>
        {
            var h = handle?.Trim().ToUpperInvariant();
            var t = tab?.Trim().ToLowerInvariant() ?? (h?[0] switch { 'B' => "claims", 'W' => "denials", _ => "remittance" });
            if (!Tabs.Contains(t)) throw new ArgumentException($"tab is one of {string.Join(", ", Tabs)}.");
            if (h != null) Check(s, h);
            s.Show(new ViewState(t, h));
            return new JsonObject { ["showing"] = t, ["selected"] = h };
        }, cancel);

    private static void Check(IExplorerSession s, string h)
    {
        switch (h[0])
        {
            case 'R': PaymentOf(s, h); break;
            case 'C': ClaimOf(s, h); break;
            case 'B': BilledOf(s.Reconciled, h); break;
            case 'W': WorkOf(s, h); break;
            default: throw new ArgumentException($"{h} isn't a handle: R01, C01, B01 or W01.");
        }
    }

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A picture (PNG) to show the person: a payment, a claim (line by line, with each adjustment in words), a billed claim or a denial; without one, what the window shows. Masked as everything else is.")]
    public async Task<CallToolResult> RenderView(
        [Description("R01, C01, B01 or W01. Optional.")] string? handle = null,
        [Description("Width in pixels, from 200 to 2000. Default 1000.")] int width = 1000,
        CancellationToken cancel = default)
    {
        try
        {
            if (width is < 200 or > 2000) throw new ArgumentException("width is from 200 to 2000 pixels.");
            var picture = await host.RunAsync("drew the view", s => PictureOf(s, new ViewState(s.View.Tab, handle?.Trim().ToUpperInvariant() ?? s.View.Handle)), cancel).ConfigureAwait(false);
            var png = await host.RunAsync("drew the view", _ => host.Painter.Picture(picture, width / 1000.0), cancel).ConfigureAwait(false);
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = picture.Title + (picture.Subtitle.Length > 0 ? " — " + picture.Subtitle : "") }] };
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    internal ViewPicture PictureOf(IExplorerSession s, ViewState view)
    {
        var mask = s.SeeRealValues ? (Func<string, string>)(x => x) : MaskerFor(s).Text;
        ViewPicture Masked(string title, string sub, IEnumerable<string> lines, IEnumerable<string> notes) =>
            new(mask(title), mask(sub), [.. lines.Select(mask)], 1, 0, 0, false, [.. notes.Select(mask)]);
        switch (view.Handle?[0])
        {
            case 'C':
                var c = ClaimOf(s, view.Handle);
                return Masked($"{c.Account} {c.Patient?.Name}: {Words.State(c.State)}", $"{c.Payment.Payer.Name} · {Words.Summary(c)}",
                    c.Lines.SelectMany(l => new[] { $"{l.Number}. {Words.Code(l)}: billed {Money.Say(l.Charge)}" + (l.Allowed is { } a ? $", allowed {Money.Say(a)}" : "") + $", paid {Money.Say(l.Paid)}" }
                        .Concat(l.Adjustments.Select(x => "   " + Words.Describe(x))).Concat(l.Remarks.Select(x => "   " + Words.Describe(x)))),
                    c.Adjustments.Select(Words.Describe));
            case 'R':
                var p = PaymentOf(s, view.Handle);
                return Masked(p.Payer.Name, Words.Summary(p), p.Claims.Select(x => $"{x.Account,-10} {x.Patient?.SortName,-22} {Words.State(x.State),-14} {Money.Say(x.Paid)}"),
                    [.. p.ProviderAdjustments.Select(Words.Describe), .. Checks.Run(p).Select(f => f.ToString())]);
            case 'B':
                var r = s.Reconciled;
                var (_, m) = BilledOf(r, view.Handle);
                return Masked($"{m.Billed.Account} {m.Billed.Name}: {MatchWords.State(m.State)}", m.Billed.Payer, [MatchWords.Describe(m, r), .. m.Lines.Select(MatchWords.Describe)], []);
            case 'W':
                var w = WorkOf(s, view.Handle);
                return Masked($"{w.Id} {w.Claim.Account} {w.Claim.Patient?.Name}: {Classifier.Say(w.Action)} {Money.Say(w.Amount)}", w.Claim.Payment.Payer.Name,
                    [.. w.Reasons.DistinctBy(x => (x.Group, x.Reason)).Select(Words.Describe), .. w.Remarks.Select(Words.Describe), "", w.WhatToDo],
                    w.Deadline is { } d ? [$"By {Money.Day(d)}: {w.DeadlineBecause}."] : []);
        }
        if (view.Tab == "denials")
            return Masked("Denials", $"{s.Work.Count(x => !x.Done)} open", s.Work.Select(x => $"{x.Id} {x.Claim.Account,-10} {Classifier.Say(x.Action),-16} {Money.Say(x.Amount),10} {(x.DaysLeft(s.Today) is { } left ? $"{left} days left" : "")}"), []);
        return Masked("Remittances", string.Join(", ", s.Remits.Select(f => f.Name)), Payments(s).Select(x => $"{x.Handle} {x.Payment.Label}"), []);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static JsonArray? Strings(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? null : new JsonArray([.. list.Select(x => (JsonNode)x)]);
    }

    /// <summary>Every string in an answer masked (unless values are allowed), and "values_masked" added.</summary>
    private static string Finish(IExplorerSession s, JsonObject o)
    {
        if (!s.SeeRealValues) MaskStrings(o, MaskerFor(s));
        o["values_masked"] = !s.SeeRealValues;
        return o.ToJsonString(Json.Options);
    }

    private static void MaskStrings(JsonNode? node, Masker m)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var text)) o[key] = m.Text(text);
                    else MaskStrings(o[key], m);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var text)) a[i] = m.Text(text);
                    else MaskStrings(a[i], m);
                }
                break;
        }
    }

    private async Task<string> Session(string action, Func<IExplorerSession, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, s => Finish(s, work(s)), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that writes one new file in the working folder, and answers with where it went.</summary>
    private async Task<string> Write(string action, string output, string what, string[] extensions, Func<IExplorerSession, string, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            var full = _files.Resolve(output, what, extensions);
            _files.CheckWritable(full);
            return await host.RunAsync(action, s =>
            {
                var o = work(s, full);
                _files.Wrote(full);
                o["wrote"] = _files.Relative(full);
                return Finish(s, o);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private async Task<T> Run<T>(string action, Func<IExplorerSession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private async Task<T> Run<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or ExplorerBusyException or KeyNotFoundException or InvalidDataException or System.Text.Json.JsonException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
