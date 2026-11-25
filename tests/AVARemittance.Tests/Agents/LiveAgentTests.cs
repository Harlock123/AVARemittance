using System.IO.Pipelines;
using AVARemittance.Agents;
using AVARemittance.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVARemittance.Tests.Agents;

/// <summary>Agents in the open window: the pipe door (<c>--mcp --attach</c>), the network door, and the command line.</summary>
public sealed class LiveAgentTests
{
    private static string Text(CallToolResult r) => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public void An_outside_agent_attaches_through_the_pipe_shows_a_claim_records_a_denial_and_asks_only_when_the_person_says_yes() => HeadlessApp.Run(() =>
    {
        using var mock = new MockClearinghouse();
        var root = Samples.Copy();
        var w = new MainWindow { AgentPipe = "avaremittance-test-" + Guid.NewGuid().ToString("N"), Now = () => StatusTests.SampleTime, Today = () => MatchingTests.Today };
        w.Show();
        var asked = new List<string>();
        var answer = false;
        w.AskSure = q => { asked.Add(q); return Task.FromResult(answer); };
        Assert.StartsWith("AI agents may connect", w.SetAllowAgents(true));
        Assert.True(LiveListener.Answers(w.AgentPipe));
        var bridge = new LiveBridge(w.AgentPipe, root);
        Pipe up = new(), down = new();
        var server = McpServer.Create(new StreamServerTransport(up.Reader.AsStream(), down.Writer.AsStream(), "attach"), bridge.Options());
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => server.RunAsync(stop.Token));
        var client = AgentTests.Wait(McpClient.CreateAsync(new StreamClientTransport(up.Writer.AsStream(), down.Reader.AsStream())));
        CallToolResult Call(string tool, Dictionary<string, object?>? args = null) => AgentTests.Wait(client.CallToolAsync(tool, args ?? []).AsTask());
        try
        {
            Assert.Equal(AgentTests.ToolNames.Length, AgentTests.Wait(client.ListToolsAsync().AsTask()).Count);
            var opened = Call("open_file", new() { ["file"] = "harborview-835-1110.x12" });
            Assert.False(opened.IsError == true, Text(opened));
            Assert.Contains("\"values_masked\":true", Text(opened));
            Assert.Single(w.OpenFiles);
            foreach (var f in AgentTests.Rig.Everything.Skip(1)) Assert.False(Call("open_file", new() { ["file"] = f }).IsError == true, f);
            var shown = Call("show_in_view", new() { ["handle"] = "C04" });
            Assert.False(shown.IsError == true, Text(shown));
            Assert.Equal("RB26104", w.CurrentClaim!.Account);
            Assert.Equal(0, w.Tabs.SelectedIndex);
            Assert.Equal(new ViewState("remittance", "C04"), w.CurrentView);
            Assert.False(Call("show_in_view", new() { ["handle"] = "W01" }).IsError == true);
            Assert.Equal(new ViewState("denials", "W01"), w.CurrentView);
            var marked = Call("mark_work", new() { ["denial"] = "W01", ["state"] = "appealed", ["note"] = "the person sent it" });
            Assert.False(marked.IsError == true, Text(marked));
            Assert.Equal(Core.Denials.WorkState.Appealed, w.Work.Single(x => x.Claim.Account == "RB26114").State);
            Assert.True(File.Exists(Path.Combine(root, Core.Denials.WorkStore.FileName)));

            w.SendEnabledBox.IsChecked = true;
            (w.EndpointBox.Text, w.CoreSenderBox.Text, w.CoreReceiverBox.Text, w.UserBox.Text, w.PasswordBox.Text) = (mock.Address, "RIVERBEND", "MOCKCH", MockClearinghouse.User, MockClearinghouse.Password);
            var no = Call("ask_clearinghouse");
            Assert.False(no.IsError == true, Text(no));
            Assert.Contains("Nothing was sent.", Text(no));
            Assert.Empty(mock.Requests);
            answer = true;
            var yes = Call("ask_clearinghouse");
            Assert.Contains("Asked about 2: 2 answered.", Text(yes));
            Assert.Equal(2, asked.Count);
            Assert.StartsWith("Ask 127.0.0.1 about 2 claims?", asked[1]);
            Assert.Equal(4, w.StatusAnswers.Count);
        }
        finally
        {
            AgentTests.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => true));
            stop.Cancel();
            AgentTests.Wait(serving.ContinueWith(_ => true));
            AgentTests.Wait(bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            Assert.Equal("AI agents are shut out.", w.SetAllowAgents(false));
            AgentTests.Wait(w.Agents.StopAsync().ContinueWith(_ => true));
            new Core.Sending.PasswordStore(MainWindow.SettingsFolder).Forget();
            new AppSettings().Save();
            w.Close();
        }
    });

    [Fact]
    public void The_network_door_opens_on_the_settings_and_wants_the_token() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            Assert.Equal(8780, new AgentSettings().NetworkAgentsPort);
            Assert.Equal(8780, McpCommandLine.NetworkPort);
            Assert.Equal("AVAREMITTANCE_MCP_TOKEN", McpCommandLine.TokenVariable);
            w.AgentSettings.AllowNetworkAgents = true;
            w.AgentSettings.NetworkAgentsAddress = "localhost";
            w.AgentSettings.NetworkAgentsPort = 0;
            w.AgentSettings.NetworkAgentsFolder = Samples.Copy();
            Assert.StartsWith("AI agents on other computers may connect", AgentTests.Wait(w.SyncNetworkAsync()));
            using var http = new HttpClient();
            var refused = AgentTests.Wait(http.PostAsync(w.Agents.NetworkUrls.First(), new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
            w.AgentSettings.Save();
            Assert.DoesNotContain(w.NetworkToken, File.ReadAllText(AgentSettings.Path));
            Assert.StartsWith(AgentSettings.FolderOverride!, AgentSettings.Path);
        }
        finally
        {
            w.AgentSettings.AllowNetworkAgents = false;
            AgentTests.Wait(w.SyncNetworkAsync());
            w.Close();
        }
    });

    [Fact]
    public void The_pipe_name_and_command_line_have_this_programs_names() => HeadlessApp.Run(() =>
    {
        Assert.StartsWith("AVARemittance-mcp-", LiveProtocol.PipeName);
        Assert.Contains("AVAREMITTANCE_MCP_TOKEN", McpCommandLine.Usage);
        Assert.Contains("--real", McpCommandLine.Usage);
        Assert.Contains("--mask", McpCommandLine.Usage);
        var output = new MemoryStream();
        var log = new StringWriter();
        Assert.Equal(2, AgentTests.Wait(McpCommandLine.RunAsync(["--mcp", "--bogus"], () => throw new InvalidOperationException(), new AvaloniaThread(), new MemoryStream(), output, log)));
        Assert.Contains("Unknown option --bogus", log.ToString());
        Assert.Equal(0, output.Length);
    });

    [Fact]
    public void Agents_see_peoples_details_masked_unless_the_person_says_otherwise() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        try
        {
            Assert.False(w.AgentsSeeRealValues);
            Assert.StartsWith("AI agents now see people's details", w.SetAgentsSeeRealValues(true));
            Assert.True(w.AgentsSeeRealValues);
            w.SetAgentsSeeRealValues(false);
            var forced = new MainWindow { ForceDeidentify = true, RealFromCommandLine = true };
            forced.Show();
            try
            {
                Assert.False(forced.AgentsSeeRealValues);
                Assert.Contains("--mask", forced.SetAgentsSeeRealValues(true));
            }
            finally { forced.Close(); }
        }
        finally { w.Close(); }
    });
}
