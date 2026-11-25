using AVARemittance.Agents.Chat;

namespace AVARemittance.Tests.Agents;

/// <summary>
/// The chat against a real model server, through each of its APIs: opt-in, with REMIT_LIVE_SERVER (Ollama's address, such
/// as http://localhost:11434, which speaks both the OpenAI and the Anthropic Messages API) and REMIT_LIVE_MODEL (a model
/// there that calls tools, such as qwen3:1.7b). Nothing runs without them.
/// </summary>
public sealed class LiveModelTests
{
    private static (string Server, string Model)? Live =>
        Environment.GetEnvironmentVariable("REMIT_LIVE_SERVER") is { Length: > 0 } s && Environment.GetEnvironmentVariable("REMIT_LIVE_MODEL") is { Length: > 0 } m
            ? (s.TrimEnd('/'), m) : null;

    private const string Ask = "Call explain_code with list reject and code 72, then say in one sentence what it means. Then stop.";

    [Fact]
    public void A_real_model_opens_a_batch_through_the_openai_compatible_api()
    {
        if (Live is not { } live) return;
        Run(rig => new ChatSession(rig.Bridge)
        {
            Provider = ChatProvider.OpenAICompatible,
            Server = new OpenAICompatibleClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, live.Server + "/v1", null),
            ServerModel = live.Model,
        });
    }

    [Fact]
    public void A_real_model_opens_a_batch_through_the_messages_api()
    {
        if (Live is not { } live) return;
        Run(rig => new ChatSession(rig.Bridge)
        {
            Provider = ChatProvider.Anthropic,
            Client = new Anthropic.AnthropicClient { ApiKey = "local", BaseUrl = live.Server, HttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) } },
            Model = live.Model,
            ClaudeServer = "local",
            ClaudeCoreToolsOnly = true,
            ClaudeSeesPictures = false,
        });
    }

    private static void Run(Func<AgentTests.Rig, ChatSession> make) => HeadlessApp.Run(() =>
    {
        using var rig = new AgentTests.Rig();
        var chat = make(rig);
        AgentTests.Wait(chat.SendAsync(Ask).ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), 600);
        var calls = chat.Entries.OfType<ToolEntry>().Select(t => $"{t.Name}{(t.IsError ? " (error: " + t.Result + ")" : "")}").ToList();
        var notices = chat.Entries.OfType<NoticeEntry>().Select(n => n.Text).ToList();
        Assert.True(chat.Entries.OfType<ToolEntry>().Any(t => t.Name == "explain_code" && !t.IsError),
            $"The model didn't open the file. Calls: {string.Join("; ", calls)}. Notices: {string.Join("; ", notices)}");
    });
}
