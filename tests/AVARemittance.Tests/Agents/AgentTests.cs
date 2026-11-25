using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using AVARemittance.Agents;
using AVARemittance.Agents.Chat;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Status;
using AVARemittance.Core.X12;
using AVARemittance.Mcp;
using ModelContextProtocol.Server;
using static AVARemittance.Tests.Agents.FakeModelServer;

namespace AVARemittance.Tests.Agents;

/// <summary>
/// Before any test runs: the AI side's settings, keys, token and saved chats go to a temporary folder, so no test
/// reads or writes the person's own (and none ever finds a real API key).
/// </summary>
internal static class AgentSettingsIsolation
{
    [ModuleInitializer]
    internal static void Isolate() =>
        AgentSettings.FolderOverride = Directory.CreateTempSubdirectory("avaremittance-agents-").FullName;
}

internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// The MCP server and the AI Chat, driven as agents drive them: through an in-process MCP client over the real server,
/// on Avalonia's headless platform, on a copy of the samples, with people's details masked unless a test says otherwise.
/// The chat's models are scripted fakes of both APIs; no real model or key is ever used.
/// </summary>
public sealed class AgentTests
{
    /// <summary>Every tool the server offers. A new tool goes here, with descriptions on it and its arguments.</summary>
    internal static readonly string[] ToolNames =
    [
        "list_files", "open_file", "close_file", "list_open", "list_payments", "get_payment", "get_claim", "get_segments", "check_balance",
        "explain_code", "search_codes", "list_billed", "get_billed", "get_deposits", "posting_summary", "export_posting", "get_underpayments",
        "get_worklist", "get_work_item", "draft_appeal", "corrected_claim", "mark_work", "export_denials",
        "claims_waiting", "get_status", "write_status_request", "ask_clearinghouse", "get_view", "show_in_view", "render_view",
        "batch", "get_recording", "save_steps", "run_steps", "checkpoint", "diff", "render_checkpoint", "restore_checkpoint",
    ];

    /// <summary>A server on a copy of the samples, with no window round it.</summary>
    internal sealed class Rig : IDisposable
    {
        private readonly Window _window;

        public HeadlessDesk Desk { get; }
        public string Root { get; } = Samples.Copy();
        public McpToolBridge Bridge { get; }
        public HeadlessSession Session => (HeadlessSession)Desk.Session;

        public Rig(bool real = false)
        {
            Desk = new HeadlessDesk(null, real, () => StatusTests.SampleTime, 1.00m, new StatusSettings());
            var stage = new Panel();
            _window = new Window { Width = 20, Height = 20, Content = stage };
            _window.Show();
            Bridge = Wait(McpToolBridge.StartAsync(new ExplorerHost(new AvaloniaThread(), Desk, new ViewPainter(stage), Root)));
        }

        public ToolOutcome Call(string tool, object? args = null) =>
            Wait(Bridge.CallAsync(tool, JsonSerializer.SerializeToElement(args ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

        public JsonElement Ok(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.False(o.IsError, $"{tool} failed: {o.Text}");
            return o.Text.StartsWith('{') ? JsonDocument.Parse(o.Text).RootElement.Clone() : default;
        }

        public string Error(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.True(o.IsError, $"{tool} should have failed: {o.Text}");
            return o.Text;
        }

        /// <summary>The samples' files, in the order the handles in the tests assume.</summary>
        public static readonly string[] Remits = ["harborview-835-1110.x12", "harborview-835-1117.x12", "tallgrass-835-1112.x12"];
        public static readonly string[] Everything =
        [
            .. Remits, "harborview-837p-1101.x12", "tallgrass-837p-0930.x12", "tallgrass-837p-1101.x12", "bank/statement-2026-11.csv",
            "contracts/fee-schedule.csv", "contracts/payer-limits.json", "status/harborview-277-1124.x12", "status/tallgrass-277-1124.x12",
        ];

        /// <summary>Opens the three 835s.</summary>
        public void OpenRemits()
        {
            foreach (var f in Remits) Ok("open_file", new { file = f });
        }

        /// <summary>Opens every sample: the 835s, the 837s, the bank statement, the fee schedule, the payers' limits and the 277s.</summary>
        public void OpenAll()
        {
            foreach (var f in Everything) Ok("open_file", new { file = f });
        }

        public void Dispose()
        {
            Wait(Bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            _window.Close();
        }
    }

    internal static T Wait<T>(Task<T> task, int seconds = 30) => HeadlessApp.Wait(task, seconds);

    private static void WithRig(Action<Rig> test, bool real = false) => HeadlessApp.Run(() =>
    {
        using var rig = new Rig(real);
        test(rig);
    });

    private static IEnumerable<string> Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!);

    [Fact]
    public void The_tools_are_the_ones_listed_and_each_says_what_it_and_its_arguments_are() => WithRig(rig =>
    {
        var tools = Wait(rig.Bridge.ListToolsAsync());
        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Name);
            if (t.InputSchema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    Assert.True(p.Value.TryGetProperty("description", out _), $"{t.Name}.{p.Name} has no description.");
                    Assert.Matches("^[a-z_]+$", p.Name);
                }
        }
        Assert.Contains("masked to its shape", RemittanceServer.Instructions);
        Assert.Contains("has no argument \"nope\"", rig.Error("list_files", new { nope = 1 }));
    });

    [Fact]
    public void The_remittances_are_opened_and_read_with_peoples_details_masked() => WithRig(rig =>
    {
        var files = rig.Ok("list_files").GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("file").GetString()!, f => f.GetProperty("kind").GetString());
        Assert.Equal(("835", "837P", "277", "276"), (files["harborview-835-1110.x12"], files["harborview-837p-1101.x12"], files["status/tallgrass-277-1124.x12"], files["status/riverbend-276-1124.x12"]));
        Assert.Equal(("bank statement", "bank statement", "charges list", "fee schedule", "payers' limits"),
            (files["bank/statement-2026-11.csv"], files["bank/statement-2026-11.bai"], files["charges/charges.csv"], files["contracts/fee-schedule.csv"], files["contracts/payer-limits.json"]));
        rig.OpenRemits();
        var payments = rig.Ok("list_payments");
        Assert.True(payments.GetProperty("values_masked").GetBoolean());
        var first = payments.GetProperty("payments")[0];
        Assert.Equal(("R01", 471m, "EFT", "2026-**-**", true), (first.GetProperty("payment").GetString(), first.GetProperty("amount").GetDecimal(), first.GetProperty("method").GetString(), first.GetProperty("paid_on").GetString(), first.GetProperty("balances").GetBoolean()));
        Assert.Equal("C04 R*99999 partly denied $96.00", first.GetProperty("claim_list")[3].GetString());
        var claim = rig.Ok("get_claim", new { claim = "C04" });
        Assert.Equal(("D*****, E****", "R*99999", "P04", "H*999999999"), (claim.GetProperty("name").GetString(), claim.GetProperty("account").GetString(), claim.GetProperty("patient").GetString(), claim.GetProperty("member_id").GetString()));
        var line = claim.GetProperty("lines")[0];
        Assert.Equal(("99214", 220m, 0m, true), (line.GetProperty("code").GetString(), line.GetProperty("charge").GetDecimal(), line.GetProperty("paid").GetDecimal(), line.GetProperty("denied").GetBoolean()));
        Assert.Equal(["N822: Missing procedure modifier(s)."], Strings(line, "remarks"));
        var payment = rig.Ok("get_payment", new { payment = "R02" });
        Assert.Equal(["WO $75.00 Overpayment Recovery (reference H*99999999999): taken from this payment", "L6 −$3.12 Interest Owed (reference H*99999999999): added to this payment"], Strings(payment, "provider_adjustments"));
        Assert.Contains(rig.Ok("get_segments", new { handle = "C04" }).GetProperty("segments").EnumerateArray().Select(x => x.GetString()), s => s!.EndsWith(": CLP*R*99999*1*400*96*24*12*H*99999999999*11*1"));
        Assert.True(rig.Ok("check_balance").GetProperty("balances").GetBoolean());
        rig.Ok("open_file", new { file = "problems/unbalanced-835.x12" });
        var off = rig.Ok("check_balance", new { file = "problems/unbalanced-835.x12" });
        Assert.False(off.GetProperty("balances").GetBoolean());
        Assert.Equal("B1", off.GetProperty("problems")[0].GetProperty("rule").GetString());
    });

    [Fact]
    public void Codes_are_explained_from_the_x12_lists() => WithRig(rig =>
    {
        var code = rig.Ok("explain_code", new { list = "carc", code = "197" });
        Assert.Equal(("Precertification/authorization/notification/pre-treatment absent.", "fix and resubmit"), (code.GetProperty("meaning").GetString(), code.GetProperty("as_a_denial").GetString()));
        Assert.Equal("a write-down to the contract, not a denial", rig.Ok("explain_code", new { list = "carc", code = "45" }).GetProperty("as_a_denial").GetString());
        Assert.Equal("Patient Responsibility: the patient's to pay", rig.Ok("explain_code", new { list = "group", code = "PR" }).GetProperty("meaning").GetString());
        Assert.Equal("2006-**-**", rig.Ok("explain_code", new { list = "carc", code = "30" }).GetProperty("stopped").GetString());
        Assert.Contains("rarc N822: Missing procedure modifier(s).", Strings(rig.Ok("search_codes", new { words = "missing procedure modifier" }), "found"));
        Assert.Contains("is one of", rig.Error("explain_code", new { list = "nope", code = "1" }));
    });

    [Fact]
    public void Billed_claims_deposits_underpayments_and_status_are_read() => WithRig(rig =>
    {
        rig.OpenAll();
        var billed = rig.Ok("list_billed");
        Assert.Equal(7, billed.GetProperty("counts").GetProperty("paid").GetInt32());
        var twice = billed.GetProperty("claims").EnumerateArray().Single(c => c.GetProperty("state").GetString() == "paid twice");
        Assert.Equal(("B06", "C06, C12"), (twice.GetProperty("billed").GetString(), twice.GetProperty("answers").GetString()));
        var waiting = rig.Ok("list_billed", new { only = "no answer" }).GetProperty("claims");
        Assert.Equal([23, 55], waiting.EnumerateArray().Select(c => c.GetProperty("days_waiting").GetInt32()));
        var b = rig.Ok("get_billed", new { billed = "B05" });
        Assert.StartsWith("Billed $300.00 on 2026-**-**; paid $150.00 on 2026-**-**, reversed on 2026-**-** (−$150.00), paid again $162.00", b.GetProperty("says").GetString());
        var deposits = rig.Ok("get_deposits");
        Assert.Equal(3, deposits.GetProperty("matched").GetArrayLength());
        Assert.Equal("D05", deposits.GetProperty("insurance_money_with_no_835")[0].GetProperty("deposit").GetString());
        Assert.Equal("1**9999999999", deposits.GetProperty("insurance_money_with_no_835")[0].GetProperty("trace").GetString());
        var days = rig.Ok("posting_summary").GetProperty("days");
        Assert.Equal((3, 253.12m, 75m), (days.GetArrayLength(), days[2].GetProperty("received").GetDecimal(), days[2].GetProperty("recouped").GetDecimal()));
        var under = rig.Ok("get_underpayments");
        Assert.Equal(27.90m, under.GetProperty("below_total").GetDecimal());
        Assert.Equal(("C10", "U1"), (under.GetProperty("lines")[1].GetProperty("claim").GetString(), under.GetProperty("lines")[1].GetProperty("kind").GetString()));
        Assert.Equal(25m, rig.Ok("get_underpayments", new { payer = "harborview", tolerance = 0.5 }).GetProperty("below_total").GetDecimal());
        var status = rig.Ok("claims_waiting");
        Assert.Equal(("B09", "277 of 2026-**-**: P1 20"), (status.GetProperty("claims")[0].GetProperty("billed").GetString(), status.GetProperty("claims")[0].GetProperty("status").GetString()![..24]));
        Assert.Equal("The payer has no record of it: send it again now, before the time to file runs out.", rig.Ok("get_status").GetProperty("claims")[1].GetProperty("means").GetString());
    });

    [Fact]
    public void Denials_are_worked_drafted_and_exported_masked() => WithRig(rig =>
    {
        rig.OpenRemits();
        rig.Ok("open_file", new { file = "contracts/payer-limits.json" });
        var work = rig.Ok("get_worklist");
        Assert.Equal((6, 2080m), (work.GetProperty("to_work").GetInt32(), work.GetProperty("at_stake").GetDecimal()));
        var first = work.GetProperty("denials")[0];
        Assert.Equal(("W01", "appeal", "CO-50, N130", 48), (first.GetProperty("denial").GetString(), first.GetProperty("action").GetString(), first.GetProperty("codes").GetString(), first.GetProperty("days_left").GetInt32()));
        Assert.Single(rig.Ok("get_worklist", new { action = "appeal" }).GetProperty("denials").EnumerateArray());
        var item = rig.Ok("get_work_item", new { denial = "W03" });
        Assert.StartsWith("Correct the claim (missing procedure modifier(s)) and send it again.", item.GetProperty("what_to_do").GetString());
        Assert.Contains("- REF*F8, the original reference number: H*99999999999.", Strings(rig.Ok("corrected_claim", new { denial = "W03" }), "changes"));
        Assert.Contains("not fix and resubmit", rig.Error("corrected_claim", new { denial = "W01" }));
        var draft = rig.Ok("draft_appeal", new { denial = "W01", output = "out/appeal.md" });
        Assert.True(draft.GetProperty("masked").GetBoolean());
        var md = File.ReadAllText(Path.Combine(rig.Root, "out", "appeal.md"));
        Assert.Contains("DRAFT:", md);
        Assert.DoesNotContain("NGUYEN", md);
        Assert.Contains("Patient: L*** N*****", md);
        Assert.Contains("only done in the open window", rig.Error("mark_work", new { denial = "W01", state = "appealed" }));
        rig.Ok("export_denials", new { output = "out/denials.html" });
        Assert.DoesNotContain("RB26114", File.ReadAllText(Path.Combine(rig.Root, "out", "denials.html")));
        File.WriteAllText(Path.Combine(rig.Root, "theirs.html"), "the person's own file");
        Assert.Contains("already exists", rig.Error("export_denials", new { output = "theirs.html" }));
        Assert.Equal("the person's own file", File.ReadAllText(Path.Combine(rig.Root, "theirs.html")));
        Assert.Contains("outside", rig.Error("export_posting", new { output = "../x.html" }));
    });

    [Fact]
    public void Writing_a_276_needs_values_allowed_and_asking_needs_the_window() => WithRig(rig =>
    {
        rig.OpenAll();
        Assert.Contains("holds patients' names", rig.Error("write_status_request", new { output = "out/status.x12" }));
        Assert.Contains("only done in the open window", rig.Error("ask_clearinghouse"));
    });

    [Fact]
    public void With_values_allowed_the_276_is_the_samples() => WithRig(rig =>
    {
        foreach (var f in Rig.Everything.Take(6)) rig.Ok("open_file", new { file = f });
        var w = rig.Ok("write_status_request", new { output = "out/status.x12" });
        Assert.Equal((2, 2, false), (w.GetProperty("claims").GetInt32(), w.GetProperty("payers").GetInt32(), w.GetProperty("values_masked").GetBoolean()));
        Assert.Equal(File.ReadAllText(Samples.Path("status/riverbend-276-1124.x12")), File.ReadAllText(Path.Combine(rig.Root, "out", "status.x12")));
        Assert.Equal(2, rig.Session.Status.ControlNumber);
        Assert.Equal("ALVAREZ, MARCO", rig.Ok("get_claim", new { claim = "C01" }).GetProperty("name").GetString());
    }, real: true);

    [Fact]
    public void The_view_is_drawn_shown_and_batches_steps_and_checkpoints_work() => WithRig(rig =>
    {
        rig.OpenAll();
        foreach (var h in new[] { "R01", "C04", "B05", "W01", (string?)null })
        {
            var shot = rig.Call("render_view", new { handle = h });
            Assert.False(shot.IsError, shot.Text);
            Assert.Single(shot.Images);
        }
        Assert.StartsWith("R*99999 E**** D*****: partly denied", rig.Call("render_view", new { handle = "C04" }).Text);
        rig.Ok("show_in_view", new { handle = "W01" });
        Assert.Equal(new ViewState("denials", "W01"), rig.Session.View);
        Assert.Contains("There's no claim", rig.Error("show_in_view", new { handle = "C99" }));
        var batch = rig.Ok("batch", new { steps = new object[] { new { tool = "get_claim", args = new { claim = "C01" } }, new { tool = "get_claim", args = new { claim = "C02" } } } });
        Assert.Equal(2, batch.GetProperty("answers").GetArrayLength());
        rig.Ok("checkpoint", new { name = "before" });
        rig.Ok("close_file", new { file = "tallgrass-835-1112.x12" });
        Assert.Contains("closed tallgrass-835-1112.x12", rig.Ok("diff", new { since = "before" }).ToString());
        Assert.Single(rig.Call("render_checkpoint", new { name = "before" }).Images);
        rig.Ok("restore_checkpoint", new { name = "before" });
        Assert.Equal(3, rig.Session.Remits.Count);
        rig.Ok("save_steps", new { path = "steps/claim", steps = new object[] { new { tool = "get_claim", args = new { claim = "${claim}" } } }, parameters = new { claim = "C01" } });
        Assert.Equal("C05", rig.Ok("run_steps", new { path = "steps/claim.avasteps.json", values = new { claim = "C05" } }).GetProperty("answers")[0].GetProperty("answer").GetProperty("claim").GetString());
    });

    // ───────────────────────────── people's details ─────────────────────────────

    /// <summary>Every person's detail in the samples: each part of each name, member IDs, accounts, payer claim and line control numbers, check and trace numbers, bank accounts, and birth and service dates.</summary>
    internal static HashSet<string> Phi()
    {
        var phi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Name(string last, string first)
        {
            foreach (var part in $"{last} {first}".Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3)) phi.Add(part);
        }
        void Day(DateOnly? d)
        {
            if (d is not { } x) return;
            phi.Add(x.ToString("yyyy-MM-dd"));
            phi.Add(x.ToString("yyyyMMdd"));
        }
        var remits = Directory.EnumerateFiles(Samples.Folder, "*835*.x12", SearchOption.AllDirectories).Select(RemitReader.Load).ToList();
        foreach (var p in remits.SelectMany(f => f.Payments))
        {
            phi.Add(p.Trace);
            if (p.SenderAccount.Length > 0) phi.Add(p.SenderAccount);
            if (p.ReceiverAccount.Length > 0) phi.Add(p.ReceiverAccount);
            foreach (var a in p.ProviderAdjustments) phi.Add(a.Reference);
            foreach (var c in p.Claims)
            {
                phi.Add(c.Account);
                phi.Add(c.PayerClaim);
                if (c.Patient is { } pt) { Name(pt.Last, pt.First); phi.Add(pt.Id); }
                foreach (var l in c.Lines) { phi.Add(l.ControlNumber); Day(l.From); }
            }
        }
        foreach (var f in Directory.EnumerateFiles(Samples.Folder, "*837*.x12", SearchOption.AllDirectories))
            foreach (var c in ClaimsReader.Read(X12File.Load(f), f).Claims)
            {
                Name(c.Last, c.First);
                phi.Add(c.MemberId);
                Day(c.BirthDate);
                foreach (var l in c.Lines) Day(l.Date);
            }
        foreach (var f in Directory.EnumerateFiles(Samples.Path("status"), "*-277-*.x12"))
            foreach (var a in StatusReader.Read(X12File.Load(f), f)) { if (a.PayerClaim.Length > 0) phi.Add(a.PayerClaim); phi.Add(a.Trace); }
        phi.Remove("");
        return phi;
    }

    [Fact]
    public void The_list_of_details_the_no_leak_test_looks_for_covers_every_kind()
    {
        var phi = Phi();
        foreach (var real in new[] { "ALVAREZ", "KOWALSKI", "HV800100201", "TG55056789", "RB26114", "HV26110500004", "RB26104-1", "1HV2611100001", "40017731", "88001234", "HV26090300088", "1980-12-07", "20261023", "HV26111900077", "RB26117-261124" })
            Assert.Contains(real, phi);
        Assert.True(phi.Count > 100, $"{phi.Count}");
    }

    [Fact]
    public void No_tool_result_carries_a_persons_detail_when_values_are_masked() => WithRig(rig =>
    {
        var phi = Phi();
        var seen = new List<(string Call, string Text)>();
        void Check(string tool, object? args = null)
        {
            var o = rig.Call(tool, args);
            Assert.False(o.IsError, $"{tool} {JsonSerializer.Serialize(args)}: {o.Text}");
            seen.Add(($"{tool} {JsonSerializer.Serialize(args)}", o.Text));
        }
        Check("list_files");
        foreach (var f in Rig.Everything.Append("bank/statement-2026-11.bai").Append("problems/unbalanced-835.x12")) Check("open_file", new { file = f });
        Check("list_open");
        Check("list_payments");
        Check("check_balance");
        foreach (var (h, _) in RemittanceTools.Payments(rig.Session))
        {
            Check("get_payment", new { payment = h });
            Check("get_segments", new { handle = h });
            Check("render_view", new { handle = h });
        }
        foreach (var (h, _) in RemittanceTools.Claims(rig.Session))
        {
            Check("get_claim", new { claim = h });
            Check("get_segments", new { handle = h });
            Check("render_view", new { handle = h });
        }
        Check("list_billed");
        foreach (var (h, _) in RemittanceTools.BilledClaims(rig.Session.Reconciled))
        {
            Check("get_billed", new { billed = h });
            Check("render_view", new { handle = h });
        }
        Check("get_deposits");
        Check("posting_summary");
        Check("export_posting", new { output = "leak/posting.html" });
        Check("export_posting", new { output = "leak/posting.xlsx" });
        Check("get_underpayments");
        Check("get_worklist", new { include_done = true });
        foreach (var w in rig.Session.Work)
        {
            Check("get_work_item", new { denial = w.Id });
            Check("render_view", new { handle = w.Id });
            if (w.Action == Core.Denials.WorkAction.FixAndResubmit) Check("corrected_claim", new { denial = w.Id });
            Check("draft_appeal", new { denial = w.Id, output = $"leak/appeal-{w.Id}.md" });
        }
        Check("draft_appeal", new { denial = "W01", output = "leak/appeal.html" });
        Check("export_denials", new { output = "leak/denials.html" });
        Check("export_denials", new { output = "leak/denials.xlsx" });
        Check("claims_waiting");
        Check("get_status");
        Check("show_in_view", new { handle = "C04" });
        Check("get_view");
        Check("render_view");
        Check("batch", new { steps = new object[] { new { tool = "get_claim", args = new { claim = "C04" } }, new { tool = "get_billed", args = new { billed = "B01" } } } });
        Check("get_recording");
        Check("checkpoint", new { name = "x" });
        Check("diff", new { since = "x" });

        var written = new List<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(rig.Root, "leak")))
        {
            if (f.EndsWith(".xlsx"))
            {
                using var book = new ClosedXML.Excel.XLWorkbook(f);
                written.Add(("file " + Path.GetFileName(f), string.Join("\n", book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetFormattedString()))));
            }
            else written.Add(("file " + Path.GetFileName(f), File.ReadAllText(f)));
        }
        var words = phi.Select(real => (real, new Regex($"(?<![A-Za-z0-9]){Regex.Escape(real)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase))).ToList();
        var leaks = new List<string>();
        foreach (var (call, text) in seen.Concat(written))
            foreach (var (real, word) in words)
                if (word.IsMatch(text))
                    leaks.Add($"{call}: \"{real}\"");
        Assert.True(seen.Count > 100, $"Only {seen.Count} calls were made.");
        Assert.True(written.Count >= 10, $"Only {written.Count} files were written.");
        Assert.All(seen.Where(s => s.Text.StartsWith('{')), s => Assert.DoesNotContain("\"values_masked\":false", s.Text));
        Assert.True(leaks.Count == 0, $"{leaks.Count} leaks:\n" + string.Join("\n", leaks.Distinct().Take(60)));
    });

    // ───────────────────────────── recipes ─────────────────────────────

    [Fact]
    public void Every_recipe_runs_call_by_call_with_values_masked() => HeadlessApp.Run(() =>
    {
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null).ToList();
        Assert.Equal(8, recipes.Count);
        foreach (var recipe in recipes)
        {
            using var rig = new Rig();
            var text = (string)recipe.Invoke(null, recipe.GetParameters().Select(_ => (object?)null).ToArray())!;
            var calls = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).ToList();
            Assert.True(calls.Count >= 3, recipe.Name);
            foreach (var call in calls)
            {
                var rest = call[Recipes.CallMarker.Length..];
                var space = rest.IndexOf(' ');
                var tool = rest[..space];
                Assert.Contains(tool, ToolNames);
                var args = JsonDocument.Parse(rest[(space + 1)..]).RootElement;
                var outcome = Wait(rig.Bridge.CallAsync(tool, args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
                Assert.False(outcome.IsError, $"{recipe.Name}: {tool} failed: {outcome.Text}");
            }
        }
    });

    // ───────────────────────────── the chat ─────────────────────────────

    [Fact]
    public void A_chat_on_a_local_openai_compatible_model_reads_shows_and_its_reply_can_be_undone() => WithRig(rig =>
    {
        var server = new FakeModelServer("http://localhost:1234/v1")
            .Reply(Say("I'll open the remittances."), Call("c1", "open_file", new { file = "harborview-835-1110.x12" }), Call("c2", "open_file", new { file = "contracts/payer-limits.json" }))
            .Reply(Call("c3", "get_claim", new { claim = "C03" }), Call("c4", "show_in_view", new { handle = "C03" }))
            .Reply(Say("C03's MRI was denied for no authorization (CO-197): add the number and send a corrected claim."));
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen/qwen3-8b" };
        Wait(chat.SendAsync("which denials need work?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        Assert.Equal(["open_file", "open_file", "get_claim", "show_in_view"], chat.Entries.OfType<ToolEntry>().Select(t => t.Name));
        Assert.All(chat.Entries.OfType<ToolEntry>(), t => Assert.False(t.IsError, t.Result));
        var system = (string?)server.Requests[0]["messages"]![0]!["content"];
        Assert.Contains("AVA Remittance", system);
        Assert.Contains("masked", system);
        Assert.Contains("\"values_masked\":true", (string?)server.Requests[1]["messages"]!.AsArray().Last()!["content"]);
        Assert.Equal("C03", rig.Session.View.Handle);

        var undo = Assert.Single(chat.Entries.OfType<ReplyUndoEntry>());
        Assert.True(Wait(chat.UndoReplyAsync(undo)));
        Assert.Null(rig.Session.View.Handle);
        Assert.Empty(rig.Session.Remits);
        Assert.Empty(rig.Session.Limits);
    });

    [Fact]
    public void A_chat_on_a_local_server_that_speaks_the_messages_api_uses_the_core_tools() => WithRig(rig =>
    {
        var claude = new FakeMessagesServer()
            .Reply(new FakeMessagesServer.ToolUse("t1", "explain_code", new { list = "carc", code = "29" }))
            .Reply(new FakeMessagesServer.Words("29 means the claim was filed too late."));
        var client = new Anthropic.AnthropicClient { ApiKey = "local", BaseUrl = "http://localhost:1234", HttpClient = new HttpClient(claude) };
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.Anthropic, Client = client, Model = "qwen/qwen3-8b", ClaudeServer = "localhost:1234", ClaudeCoreToolsOnly = true, ClaudeSeesPictures = false };
        Wait(chat.SendAsync("what does CARC 29 mean?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        var offered = claude.Requests[0]["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(ChatTools.Core.Order(), offered.Order());
        var call = Assert.Single(chat.Entries.OfType<ToolEntry>());
        Assert.False(call.IsError, call.Result);
        Assert.Contains("The time limit for filing has expired.", call.Result);
        Assert.Empty(chat.Entries.OfType<ReplyUndoEntry>());
    });
}
