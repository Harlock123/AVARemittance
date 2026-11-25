using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AVARemittance.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for what agents are often asked to do with
/// remittances, written with the real tool names and arguments. Each step that calls a tool shows the call on a line of
/// its own, "→ tool {arguments}" (the tests run every recipe through the server, call by call, on the samples, so a
/// recipe that stops working fails the build). The defaults name the samples' files and handles.
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    private static readonly string[] SampleRemits = ["harborview-835-1110.x12", "harborview-835-1117.x12", "tallgrass-835-1112.x12"];
    private static readonly string[] SampleBilled = ["harborview-837p-1101.x12", "tallgrass-837p-0930.x12", "tallgrass-837p-1101.x12"];

    private static void OpenRemits(Recipe r)
    {
        r.Step("The remittances.", "open_file", new { file = SampleRemits[0] });
        foreach (var f in SampleRemits.Skip(1)) r.Step(null, "open_file", new { file = f });
    }

    private static void OpenBilled(Recipe r)
    {
        r.Step("What was billed.", "open_file", new { file = SampleBilled[0] });
        foreach (var f in SampleBilled.Skip(1)) r.Step(null, "open_file", new { file = f });
    }

    [McpServerPrompt(Name = "days_posting", Title = "The day's posting")]
    [Description("Says what to post from the open 835s, day by day, and whether the money came in.")]
    public static string DaysPosting([Description("The bank statement (default \"bank/statement-2026-11.csv\").")] string? statement = null)
    {
        var r = new Recipe("Say what to post from the remittances, and whether each payment's money is in the bank.");
        OpenRemits(r);
        r.Step("Whether each balances.", "check_balance", new { });
        r.Step(null, "open_file", new { file = Or(statement, "bank/statement-2026-11.csv") });
        r.Step("What to post, by day.", "posting_summary", new { });
        r.Step("The deposits against the payments.", "get_deposits", new { });
        return r.Text("For each day: received, posted, adjusted off, the patients' share, and any recoupment or interest. Then the money: what matched, any difference and what explains it, insurance money with no 835 (ask for it), and 835s with no deposit. Say plainly if an 835 doesn't balance: it shouldn't be posted as it stands.");
    }

    [McpServerPrompt(Name = "why_paid_less", Title = "Why a claim was paid less")]
    [Description("Explains why a claim was paid less than billed: line by line, whose each adjustment is, and whether it's below the fee schedule.")]
    public static string WhyPaidLess([Description("The claim's handle (default \"C10\").")] string? claim = null)
    {
        var c = Or(claim, "C10");
        var r = new Recipe($"Explain why claim {c} was paid less than billed.");
        OpenRemits(r);
        r.Step(null, "get_claim", new { claim = c });
        r.Step("The contract write-down's code.", "explain_code", new { list = "carc", code = "45" });
        r.Step("The fee schedule.", "open_file", new { file = "contracts/fee-schedule.csv" });
        r.Step("Whether it's below the schedule.", "get_underpayments", new { });
        return r.Text("Line by line: billed, allowed, paid, and each adjustment with whose it is (the provider's write-off, the patient's share, a denial). Then say whether the allowed amount is below what the schedule expects, by how much, and from which rows: below what the schedule expects, never wrong.");
    }

    [McpServerPrompt(Name = "appeals_this_week", Title = "Which denials to appeal this week")]
    [Description("Goes through the denials and picks the ones worth appealing or correcting soon, by amount and days left.")]
    public static string AppealsThisWeek([Description("Days ahead to look (default 60).")] int? days = null)
    {
        var r = new Recipe("Say which denials are worth working first: appeals and corrections, by what's at stake and the days left.");
        OpenRemits(r);
        r.Step("The payers' limits, for the deadlines.", "open_file", new { file = "contracts/payer-limits.json" });
        r.Step("Every denial, by deadline.", "get_worklist", new { });
        r.Step("The appeals due soon.", "get_worklist", new { action = "appeal", within_days = days ?? 60 });
        r.Step("The first in full.", "get_work_item", new { denial = "W01" });
        return r.Text("List the appeals and corrected claims to do first, the soonest deadline and biggest amount first, each with what to do in a line. Say which are write-offs or bundled and need nothing. The deadlines are from the person's limits file: say so.");
    }

    [McpServerPrompt(Name = "draft_an_appeal", Title = "Draft an appeal")]
    [Description("Drafts an appeal letter for a denial, for the person to finish and check.")]
    public static string DraftAnAppeal(
        [Description("The denial's handle (default \"W01\").")] string? denial = null,
        [Description("The draft to write (default \"appeal-W01.md\"; .docx or .html too).")] string? output = null)
    {
        var w = Or(denial, "W01");
        var r = new Recipe($"Draft the appeal letter for denial {w}.");
        OpenRemits(r);
        r.Step(null, "open_file", new { file = "contracts/payer-limits.json" });
        r.Step("The denial.", "get_work_item", new { denial = w });
        r.Step("The draft.", "draft_appeal", new { denial = w, output = Or(output, $"appeal-{w}.md") });
        return r.Text("Name the draft file and say it's a draft: list each [bracket] the person must fill in (the records, the reason the care was needed, who signs) and the deadline. If values were masked, say the person drafts it again from the window to have the patient's details in.");
    }

    [McpServerPrompt(Name = "unmatched_at_the_bank", Title = "What's unmatched at the bank")]
    [Description("Finds the money with no 835, the 835s with no money, and the deposits that differ.")]
    public static string UnmatchedAtTheBank([Description("The bank statement (default \"bank/statement-2026-11.bai\").")] string? statement = null)
    {
        var r = new Recipe("Find what doesn't match between the remittances and the bank.");
        OpenRemits(r);
        r.Step(null, "open_file", new { file = Or(statement, "bank/statement-2026-11.bai") });
        r.Step(null, "get_deposits", new { });
        return r.Text("Say each thing to chase: insurance money with no 835 (with its trace, to ask the payer or clearinghouse for the 835), 835s with no deposit (a check not deposited, or a transfer not in yet), and differences (and when a recoupment or interest explains one). Other deposits are patients' payments and the like: name them in a line.");
    }

    [McpServerPrompt(Name = "payer_underpayments", Title = "A payer's underpayments")]
    [Description("Totals what a payer allowed below the fee schedule, by procedure, with the lines behind it.")]
    public static string PayerUnderpayments([Description("The payer (default \"HARBORVIEW\").")] string? payer = null)
    {
        var p = Or(payer, "HARBORVIEW");
        var r = new Recipe($"Total what {p} allowed below the fee schedule, by procedure.");
        OpenRemits(r);
        r.Step(null, "open_file", new { file = "contracts/fee-schedule.csv" });
        r.Step(null, "get_underpayments", new { payer = p });
        r.Step("With a wider tolerance, what's left.", "get_underpayments", new { payer = p, tolerance = 5 });
        return r.Text("Give the total below the schedule and the procedures, the most first, with the lines behind each and the schedule rows. Say it's below what the schedule expects: the contract decides, so the next step is to check the contract and, if it agrees, ask the payer to reprocess.");
    }

    [McpServerPrompt(Name = "claims_with_no_answer", Title = "Claims with no answer")]
    [Description("Finds the billed claims with no answer yet and says what their 277s say, and what to do.")]
    public static string ClaimsWithNoAnswer([Description("Days since billed to count (default 21).")] int? days = null)
    {
        var r = new Recipe("Find the claims with no answer yet, and say where each stands.");
        OpenBilled(r);
        OpenRemits(r);
        r.Step("The claims waiting.", "claims_waiting", new { days = days ?? 21 });
        r.Step("The payers' 277s.", "open_file", new { file = "status/harborview-277-1124.x12" });
        r.Step(null, "open_file", new { file = "status/tallgrass-277-1124.x12" });
        r.Step(null, "get_status", new { });
        r.Step("Each claim with its status.", "claims_waiting", new { days = days ?? 21 });
        return r.Text("For each claim waiting: the days since it was billed, what its 277 says, and what to do (wait, send more, or send it again now when the payer has no record of it). With no 277 open, the person can ask in the window (ask_clearinghouse, only when they say yes) or write the 276 (Claim Status › Write the 276).");
    }

    [McpServerPrompt(Name = "denials_report", Title = "The denials report")]
    [Description("Writes the denials worklist as a report, and summarizes it.")]
    public static string DenialsReport([Description("The report to write (default \"denials.html\"; .xlsx for a workbook).")] string? output = null)
    {
        var o = Or(output, "denials.html");
        var r = new Recipe($"Write the denials report, {o}, and summarize it.");
        OpenRemits(r);
        r.Step(null, "open_file", new { file = "contracts/payer-limits.json" });
        r.Step(null, "get_worklist", new { });
        r.Step(null, "export_denials", new { output = o });
        return r.Text("Summarize: how many denials, the amount at stake, by action, and the ones due soonest. Name the report file; people's details in it are masked unless the person allowed values.");
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on. File names and handles in these calls are the samples': use the person's own.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ").Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }
}
