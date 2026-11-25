using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AVARemittance.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVARemittance.Agents.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What the model is told: the MCP server's own instructions, and a few lines about working inside the program.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVA Remittance itself, in its AI Chat panel, with the person watching. Talk to them briefly and plainly: say what you're about to look at, look (list_payments, get_claim, list_billed, get_deposits, get_underpayments, get_worklist, get_work_item, explain_code), then answer in plain words, naming the claim or denial by handle (C10, W01) and the payer's codes in their own words when they decide it. Keep it to a few sentences; the tool calls show in the panel on their own. Read before you answer: never guess a code's meaning, an amount or a deadline; the tools tell you, and what an 835 doesn't say isn't known. Underpayments are below what the schedule expects, not wrong; appeal letters are drafts. To point the person at something, use show_in_view (it selects it in their window) or render_view. Unless your answers say "values_masked": false, names, IDs and claim numbers are masked to their shape and dates to their year: reason from shapes, say so when you quote them.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => RemittanceServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_view still shows the person the claim, but read it yourself with get_claim, get_billed and get_work_item.";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: finding and opening files, the
    /// payments and claims, codes, the billed claims, the deposits, the underpayments and the denials.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "list_files", "open_file", "list_open", "list_payments", "get_claim", "explain_code", "list_billed", "get_billed",
        "get_deposits", "get_underpayments", "get_worklist", "get_work_item", "claims_waiting", "show_in_view",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
