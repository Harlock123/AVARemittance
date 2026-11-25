using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVARemittance.Mcp;

/// <summary>One call in a batch or a steps file.</summary>
public sealed class BatchStep
{
    [JsonPropertyName("tool"), Description("The tool's name, such as \"validate\" or \"search\".")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("args"), Description("Its arguments, as the tool takes them.")]
    public JsonElement? Args { get; set; }
}

/// <summary>
/// Working in steps: batches (several calls at once, their answers together), steps files (a session's calls kept with
/// named parameters and run again on next month's files), and checkpoints (the files open, what the window shows, and
/// which files the session has written), which the AI Chat's Undo this reply uses.
/// </summary>
public sealed partial class RemittanceTools
{
    private readonly Dictionary<string, Checkpointed> _checkpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A checkpoint: the view, the scan and its marks then, and how many files the session had written.</summary>
    private sealed record Checkpointed(ViewState View, IReadOnlyList<string> Open, int Written, DateTime At, string? Note);
    private readonly List<(string Tool, JsonElement Args)> _recording = [];

    /// <summary>The tools a batch or a steps file may not call: ones that call others, or look at or change the view.</summary>
    private static readonly HashSet<string> NotInBatch = new(StringComparer.Ordinal)
    {
        "batch", "run_steps", "save_steps", "get_recording", "render_view", "render_checkpoint", "checkpoint", "restore_checkpoint", "diff",
        "show_in_view", "get_view",
    };

    /// <summary>Notes a call that worked, for get_recording and save_steps.</summary>
    internal void Record(string tool, IDictionary<string, JsonElement>? args)
    {
        if (NotInBatch.Contains(tool)) return;
        var o = new JsonObject();
        if (args != null) foreach (var (k, v) in args) o[k] = JsonNode.Parse(v.GetRawText());
        lock (_recording)
        {
            _recording.Add((tool, JsonSerializer.SerializeToElement(o)));
            if (_recording.Count > 500) _recording.RemoveAt(0);
        }
    }

    [McpServerTool(Name = "batch", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Runs several calls in order and returns their answers together, to save round trips: read five claims, explain ten codes. It stops at the first that fails unless keep_going is true. Not for calls that look at or change the view, or call other calls.")]
    public async Task<string> Batch(
        McpServer server,
        [Description("The calls: each a tool name and its arguments.")] List<BatchStep> steps,
        [Description("Carry on after a call fails, noting its error. Default false.")] bool keep_going = false,
        CancellationToken cancel = default) =>
        (await RunBatch(server, steps, keep_going, cancel).ConfigureAwait(false)).ToJsonString(Json.Options);

    private async Task<JsonObject> RunBatch(McpServer? server, List<BatchStep> steps, bool keepGoing, CancellationToken cancel)
    {
        if (steps.Count == 0) throw new McpException("Give at least one step.");
        if (steps.Count > 100) throw new McpException("A batch is up to 100 steps.");
        foreach (var (step, i) in steps.Select((x, i) => (x, i)))
        {
            if (NotInBatch.Contains(step.Tool)) throw new McpException($"Step {i + 1}: {step.Tool} can't be in a batch.");
            if (!Methods.ContainsKey(step.Tool)) throw new McpException($"Step {i + 1}: there's no tool \"{step.Tool}\".");
        }
        var answers = new JsonArray();
        var failed = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            try
            {
                answers.Add(new JsonObject { ["step"] = i + 1, ["tool"] = steps[i].Tool, ["answer"] = JsonNode.Parse(await Call(server, steps[i], cancel).ConfigureAwait(false)) });
            }
            catch (Exception e) when (e is McpException or ArgumentException or JsonException)
            {
                if (!keepGoing) throw new McpException($"Step {i + 1} ({steps[i].Tool}) failed: {e.Message}{(i > 0 ? $" The {i} step{(i == 1 ? "" : "s")} before it ran." : "")}");
                failed++;
                answers.Add(new JsonObject { ["step"] = i + 1, ["tool"] = steps[i].Tool, ["error"] = e.Message });
            }
        }
        return new JsonObject { ["steps"] = steps.Count, ["failed"] = failed, ["answers"] = answers };
    }

    /// <summary>The tools by name, for batches and steps files.</summary>
    private static readonly Dictionary<string, MethodInfo> Methods = typeof(RemittanceTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name is not null && m.ReturnType == typeof(Task<string>))
        .ToDictionary(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, StringComparer.Ordinal);

    /// <summary>Calls a tool in this session, binding its JSON arguments to the handler's parameters by name.</summary>
    private Task<string> Call(McpServer? server, BatchStep step, CancellationToken cancel)
    {
        var method = Methods[step.Tool];
        var given = step.Args is { ValueKind: JsonValueKind.Object } a ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
        var parameters = method.GetParameters();
        foreach (var name in given.Keys)
            if (!parameters.Any(p => p.Name == name))
                throw new ArgumentException($"{step.Tool} has no argument \"{name}\". It takes: {string.Join(", ", parameters.Where(p => p.ParameterType != typeof(CancellationToken) && p.ParameterType != typeof(McpServer)).Select(p => p.Name))}.");
        var values = parameters.Select(p =>
        {
            if (p.ParameterType == typeof(CancellationToken)) return (object?)cancel;
            if (p.ParameterType == typeof(McpServer)) return server;
            if (given.TryGetValue(p.Name!, out var v)) return v.Deserialize(p.ParameterType, Json.Options);
            if (p.HasDefaultValue) return p.DefaultValue;
            throw new ArgumentException($"{step.Tool} needs \"{p.Name}\".");
        }).ToArray();
        try
        {
            return (Task<string>)method.Invoke(this, values)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is { } inner)
        {
            throw inner;
        }
    }

    // ───────────────────────────── steps files ─────────────────────────────

    [McpServerTool(Name = "get_recording", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The calls this session has made, in order, as steps (tool and arguments): what save_steps keeps.")]
    public Task<string> GetRecording(CancellationToken cancel = default)
    {
        lock (_recording)
            return Task.FromResult(new JsonObject
            {
                ["steps"] = new JsonArray(_recording.Select(r => (JsonNode)new JsonObject { ["tool"] = r.Tool, ["args"] = JsonNode.Parse(r.Args.GetRawText()) }).ToArray()),
            }.ToJsonString(Json.Options));
    }

    [McpServerTool(Name = "save_steps", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Keeps a sequence of calls as a steps file (.avasteps.json) in the working folder, to run again with run_steps (next week's files): by default this session's recording, or steps given. Put ${name} in any argument where a value should be filled in each time (a file, a visit), and give each name a default in parameters.")]
    public Task<string> SaveSteps(
        [Description("The new file, relative to the working folder; .avasteps.json is added if missing.")] string path,
        [Description("The steps (tool and args). Default: this session's recording.")] List<BatchStep>? steps = null,
        [Description("Each ${name} used in the steps and its default value, e.g. {\"file\": \"harborview-835-1110.x12\"}.")] Dictionary<string, JsonElement>? parameters = null,
        [Description("What the steps do, in a sentence. Optional.")] string? description = null,
        CancellationToken cancel = default) =>
        Run("saved steps", _ =>
        {
            try
            {
                List<(string Tool, JsonElement Args)> list;
                if (steps is { Count: > 0 }) list = steps.Select(x => (x.Tool, x.Args ?? JsonSerializer.SerializeToElement(new { }))).ToList();
                else lock (_recording) list = [.. _recording];
                if (list.Count == 0) throw new InvalidOperationException("There are no steps to keep: this session hasn't made any calls yet, and none were given.");
                foreach (var (tool, _) in list)
                    if (NotInBatch.Contains(tool) || !Methods.ContainsKey(tool)) throw new ArgumentException($"A steps file can't hold {tool}.");
                var text = JsonSerializer.Serialize(list.Select(x => new { tool = x.Tool, args = x.Args }));
                var used = Regex.Matches(text, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
                var missing = used.Where(u => parameters?.ContainsKey(u) != true).ToList();
                if (missing.Count > 0) throw new ArgumentException($"Give a default in parameters for {string.Join(", ", missing.Select(m => "${" + m + "}"))}.");
                var full = _files.Resolve(path.EndsWith(".avasteps.json", StringComparison.OrdinalIgnoreCase) ? path : path + ".avasteps.json");
                _files.CheckWritable(full);
                var file = new JsonObject
                {
                    ["format"] = "avaremittance.steps",
                    ["version"] = 1,
                    ["description"] = description,
                    ["parameters"] = JsonNode.Parse(JsonSerializer.Serialize(parameters ?? [])),
                    ["steps"] = JsonNode.Parse(text),
                };
                File.WriteAllText(full, file.ToJsonString(new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
                _files.Wrote(full);
                return new JsonObject { ["saved"] = _files.Relative(full), ["steps"] = list.Count, ["parameters"] = new JsonArray(used.Select(u => (JsonNode)u).ToArray()) }.ToJsonString(Json.Options);
            }
            catch (Exception e) when (e is not McpException)
            {
                throw new McpException(Explain(e), e);
            }
        }, cancel);

    [McpServerTool(Name = "run_steps", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Runs a steps file (save_steps) again, filling its ${parameters} with the values given (the rest keep their defaults), and returns every call's answer.")]
    public async Task<string> RunSteps(
        McpServer server,
        [Description("The steps file, relative to the working folder.")] string path,
        [Description("Values for its parameters, e.g. {\"file\": \"tallgrass-835-1112.x12\"}.")] Dictionary<string, JsonElement>? values = null,
        [Description("Carry on after a call fails. Default false.")] bool keep_going = false,
        CancellationToken cancel = default)
    {
        List<BatchStep> steps;
        try
        {
            var full = _files.Existing(path);
            var file = JsonNode.Parse(await File.ReadAllTextAsync(full, cancel).ConfigureAwait(false)) as JsonObject ?? throw new InvalidDataException($"{path} isn't a steps file.");
            if ((string?)file["format"] != "avaremittance.steps") throw new InvalidDataException($"{path} isn't an AVA Remittance steps file.");
            var fill = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (k, v) in file["parameters"] as JsonObject ?? []) fill[k] = v?.DeepClone();
            foreach (var (k, v) in values ?? [])
            {
                if (!fill.ContainsKey(k)) throw new ArgumentException($"{path} has no parameter \"{k}\". Its parameters: {string.Join(", ", fill.Keys)}.");
                fill[k] = JsonNode.Parse(v.GetRawText());
            }
            steps = (file["steps"] as JsonArray ?? []).Select(n => Substitute(n!.DeepClone(), fill))
                .Select(n => new BatchStep { Tool = (string?)n["tool"] ?? "", Args = n["args"] is { } a ? JsonSerializer.SerializeToElement(a) : null }).ToList();
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e is InvalidDataException ? e.Message : Explain(e), e);
        }
        return (await RunBatch(server, steps, keep_going, cancel).ConfigureAwait(false)).ToJsonString(Json.Options);
    }

    /// <summary>Puts parameters' values in: a string that is just "${name}" becomes the value itself; inside text, its text.</summary>
    private static JsonNode Substitute(JsonNode node, Dictionary<string, JsonNode?> fill)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(p => p.Key).ToList())
                    if (o[k] is { } child && Substitute(child, fill) is var put && !ReferenceEquals(put, child))
                        o[k] = put;
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is { } child && Substitute(child, fill) is var put && !ReferenceEquals(put, child))
                        a[i] = put;
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("${", StringComparison.Ordinal):
                var whole = Regex.Match(s, @"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$");
                if (whole.Success) return Lookup(whole.Groups[1].Value)?.DeepClone() ?? JsonValue.Create((string?)null)!;
                return JsonValue.Create(Regex.Replace(s, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m =>
                    Lookup(m.Groups[1].Value) is JsonValue jv && jv.TryGetValue<string>(out var t) ? t : Lookup(m.Groups[1].Value)?.ToJsonString() ?? ""));
            default:
                return node;
        }

        JsonNode? Lookup(string name) => fill.TryGetValue(name, out var value) ? value : throw new ArgumentException($"The steps use ${{{name}}}, which has no value.");
    }

    // ───────────────────────────── checkpoints ─────────────────────────────

    private static IReadOnlyList<string> OpenNow(IExplorerSession s) => s.OpenPaths;

    [McpServerTool(Name = "checkpoint", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Remembers things as they are now (the files open, what the window shows) and how many files this session has written, under a name: diff says what changed since, render_checkpoint draws the view then, restore_checkpoint goes back to it. Without a name, lists the checkpoints.")]
    public Task<string> Checkpoint(
        [Description("The checkpoint's name. A name used before is replaced.")] string? name = null,
        [Description("A note on what it's for. Optional.")] string? note = null,
        [Description("Accepted for other clients; checkpoints last as long as the session.")] bool persist = false,
        CancellationToken cancel = default) =>
        Run("took a checkpoint", s =>
        {
            _ = persist;
            if (string.IsNullOrWhiteSpace(name))
                return new JsonObject { ["checkpoints"] = new JsonArray(_checkpoints.Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["note"] = kv.Value.Note, ["taken"] = kv.Value.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture) }).ToArray()) }.ToJsonString(Json.Options);
            _checkpoints[name.Trim()] = new Checkpointed(s.View, OpenNow(s), _files.Written.Count, DateTime.Now, note);
            if (_checkpoints.Count > 40) _checkpoints.Remove(_checkpoints.MinBy(kv => kv.Value.At).Key);
            return new JsonObject { ["checkpoint"] = name.Trim() }.ToJsonString(Json.Options);
        }, cancel);

    [McpServerTool(Name = "diff", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What has changed since a checkpoint: files this session has written since (added), files opened or closed, and what the window shows.")]
    public Task<string> Diff(
        [Description("The checkpoint's name.")] string since,
        CancellationToken cancel = default) =>
        Run("compared with a checkpoint", s => Compare(CheckpointNamed(since), s).ToJsonString(Json.Options), cancel);

    private JsonObject Compare(Checkpointed was, IExplorerSession now)
    {
        var written = _files.Written;
        var added = written.Count > was.Written ? written.Skip(was.Written).ToList() : [];
        var open = OpenNow(now);
        var changes = new List<string>();
        changes.AddRange(open.Except(was.Open).Select(p => "opened " + Path.GetFileName(p)));
        changes.AddRange(was.Open.Except(open).Select(p => "closed " + Path.GetFileName(p)));
        if (was.View != now.View) changes.Add($"showed {now.View.Tab}" + (now.View.Handle != null ? $" {now.View.Handle}" : ""));
        return new JsonObject
        {
            ["changed"] = added.Count > 0 || changes.Count > 0,
            ["added"] = new JsonArray(added.Select(a => (JsonNode)a).ToArray()),
            ["removed"] = new JsonArray(),
            ["changed_elements"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray()),
        };
    }

    [McpServerTool(Name = "render_checkpoint", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A picture of the view as it was at a checkpoint (the payment, claim or denial it showed), about width by height pixels.")]
    public async Task<CallToolResult> RenderCheckpoint(
        [Description("The checkpoint's name.")] string name,
        [Description("Width in pixels, from 40 to 1600. Default 320.")] int width = 320,
        [Description("Height in pixels, from 40 to 1600. Default 240.")] int height = 240,
        CancellationToken cancel = default)
    {
        if (width is < 40 or > 1600 || height is < 40 or > 1600) throw new McpException("width and height are from 40 to 1600 pixels.");
        Checkpointed c;
        try
        {
            c = CheckpointNamed(name);
        }
        catch (ArgumentException e)
        {
            throw new McpException(e.Message);
        }
        var picture = await Run("drew a checkpoint", s => PictureOf(s, c.View), cancel).ConfigureAwait(false);
        var scale = Math.Clamp(Math.Min(width / 1000.0, height / 700.0), 0.5, 2);
        var png = await Run("drew a checkpoint", _ => host.Painter.Picture(picture, scale), cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png")] };
    }

    [McpServerTool(Name = "restore_checkpoint", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Goes back to a checkpoint: the same files open and the same view. Files written since stay (nothing is ever deleted); it says which.")]
    public Task<string> RestoreCheckpoint(
        [Description("The checkpoint's name.")] string name,
        CancellationToken cancel = default) =>
        Run("went back to a checkpoint", s =>
        {
            try
            {
                var c = CheckpointNamed(name);
                var change = Compare(c, s);
                foreach (var p in OpenNow(s).Except(c.Open).ToList()) s.Close(p);
                foreach (var p in c.Open.Except(OpenNow(s)).ToList())
                    if (File.Exists(p)) s.Open(p);
                s.Show(c.View);
                change["restored"] = name;
                if (change["added"] is JsonArray { Count: > 0 }) change["note"] = "Back as it was. Files written since are kept: agents never delete files.";
                return change.ToJsonString(Json.Options);
            }
            catch (ArgumentException e)
            {
                throw new McpException(e.Message);
            }
        }, cancel);

    private Checkpointed CheckpointNamed(string name) =>
        _checkpoints.TryGetValue(name.Trim(), out var c) ? c
        : throw new ArgumentException($"There's no checkpoint \"{name}\"" + (_checkpoints.Count > 0 ? $". The checkpoints: {string.Join(", ", _checkpoints.Keys)}." : ": checkpoint takes one."));
}
