using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVARemittance.Mcp;

/// <summary>
/// The MCP server: <see cref="RemittanceTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection), with
/// instructions that tell an agent AVA Remittance's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open window, over the network with a token, and in-process for the AI Chat.
/// </summary>
public static class RemittanceServer
{
    public const string Name = "AVARemittance";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVA Remittance reads HIPAA 835 remittances (005010X221A1) and says what each payer paid, adjusted and left to the patient, claim by claim and line by line, with every reason (CARC) and remark (RARC) code in X12's own words. It checks that each 835 balances; matches the claims billed (837s or a charges list) to their payments; matches the payments to the bank's deposits; flags lines allowed below the person's fee schedule; keeps every denial as a work item with what to do and the days left; and writes 276 claim status requests for claims with no answer, reading the 277s that come back.

        People's details (it matters): unless the person lets agents see values, every patient name, member ID, patient account, payer claim number, line control number, check and trace number and bank account is masked to its shape (RB26104 is R*99999: a letter among digits still shows), and every date keeps only its year. Every answer says "values_masked". Refer to things by handle: R01… payments, C01… claims in the 835s, B01… billed claims, W01… denials to work, D01… deposits, P01… patients. Payers, providers, codes and amounts aren't people's details and are shown.

        Reading a remittance (it matters):
        - An adjustment's group says whose it is: CO and PI are the provider's to write off (not billable to the patient), PR is the patient's, OA neither's by contract. CO-45 (charge over the fee schedule) is the usual write-down to the contracted rate, not a denial.
        - Positive provider-level adjustments (PLB) take money from the payment (WO, a recoupment); negative ones add to it (L6, interest).
        - A reversal (claim status 22) takes back an earlier payment; with a new payment after it, it's a correction. The same line paid in two standing payments is paid twice. A claim in two pieces is a split.
        - Underpayments are "below what the schedule expects", never "wrong": the schedule is the person's. Deadlines come from the payers' limits file the person keeps.
        - Never guess beyond what the 835 says; explain_code gives any code's meaning.

        A good way to work: list_files; open_file the 835s (and the 837s or charges list, a bank statement, the fee schedule, the payers' limits, the 277s); list_payments; get_claim for a claim; list_billed and get_billed for what was billed; get_deposits for the bank; get_underpayments; get_worklist and get_work_item for the denials; claims_waiting for the claims with no answer. render_view shows the person a claim and show_in_view selects it in their window. batch runs several calls at once.

        Writing: draft_appeal writes a letter draft (masked unless values are allowed; always a draft, with [brackets] for the person), export_posting and export_denials write masked reports unless values are allowed, write_status_request and write_eligibility_recheck (the coverage denials, CO-22, 26, 27 and 31, as AVA Eligibility's appointment list for a fresh 271 on the date of service) need values allowed. mark_work and ask_clearinghouse run only in the open window: record only what the person says was done, and the clearinghouse is asked only when the person says yes. Nothing is ever sent to a payer. Never claim something was sent, written or checked unless a tool said so.

        The server's prompts are saved recipes (the day's posting, why a claim was paid less, which denials to appeal this week, draft an appeal, what's unmatched at the bank, a payer's underpayments, claims with no answer, the denials report): step-by-step plans with the exact tool calls.
        """;




    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IExplorerHost host) => Tools(new RemittanceTools(host));

    /// <summary>The MCP tools of one set of handlers (one session's: its checkpoints and its recording).</summary>
    public static IEnumerable<McpServerTool> Tools(RemittanceTools target)
    {
        foreach (var method in typeof(RemittanceTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVA Remittance",
        Version = typeof(RemittanceServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IExplorerHost host) => Configure(new McpServerOptions(), host);

    /// <summary>Fills a new session's options (ones the network door's HTTP handler made) as <see cref="Options"/> makes them.</summary>
    public static McpServerOptions Configure(McpServerOptions options, IExplorerHost host)
    {
        options.ServerInfo = Info;
        options.ServerInstructions = Instructions;
        options.ToolCollection = [];
        var target = new RemittanceTools(host);
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                var result = await next(request, cancel).ConfigureAwait(false);
                // The session's calls, as steps save_steps can keep: every call that worked.
                if (tool != null && result.IsError != true)
                    target.Record(tool.Name, request.Params!.Arguments);
                return result;
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(target)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IExplorerHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (!tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            // A required argument that's missing: say which, and what it is (the SDK's own words are about dictionaries).
            if (tool.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                var missing = required.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0 && args?.ContainsKey(r) != true).ToList();
                if (missing.Count > 0)
                    return $"{tool.Name} needs {string.Join(" and ", missing)}: " + string.Join(" ", missing.Select(m =>
                        props.TryGetProperty(m, out var p) && p.TryGetProperty("description", out var d) ? $"{m} is {d.GetString()?.TrimEnd('.')}." : $"{m}."));
            }
            if (args == null) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IExplorerHost
    {
        public string Root => Path.GetTempPath();
        public IViewPainter Painter => throw new InvalidOperationException("Not connected.");
        public bool Live => false;
        public Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected.");
    }
}
