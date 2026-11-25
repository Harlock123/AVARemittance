using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AVARemittance.Agents.Chat;
using AVARemittance.Tests.Agents;
using static AVARemittance.Tests.Agents.FakeModelServer;

namespace AVARemittance.Tests;

/// <summary>The README's pictures (REMIT_SHOTS set to a folder), headless. Off unless REMIT_SHOTS is set; run with --filter Screenshots.</summary>
public sealed class Screenshots
{
    internal static void Save(Window w, string path)
    {
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path);
    }

    private static string? Folder() => Environment.GetEnvironmentVariable("REMIT_SHOTS") is { Length: > 0 } dir ? Directory.CreateDirectory(dir).FullName : null;

    private static MainWindow Window(string folder, int width = 1500, int height = 900)
    {
        var w = new MainWindow { Width = width, Height = height, Today = () => MatchingTests.Today, Now = () => StatusTests.SampleTime };
        w.AgentSettings.ChatOpen = false;
        w.Show();
        if (w.ChatHolder.IsVisible) w.ToggleChat();
        foreach (var f in AgentTests.Rig.Everything) w.Open(Path.Combine(folder, f));
        return w;
    }

    [Fact]
    public void Remittance() => HeadlessApp.Run(() =>
    {
        if (Folder() is not { } dir) return;
        var folder = Samples.Copy();
        var w = Window(folder);
        try
        {
            var claim = w.OpenFiles[0].Claims.Single(c => c.Account == "RB26104");
            w.FileList.SelectedItem = w.FileList.Items.OfType<ListEntry>().Single(e => e.Claim == claim);
            w.Tabs.SelectedIndex = 0;
            w.Highlight([.. claim.Lines[0].Segments.Select(s => s.Index)]);
            Save(w, Path.Combine(dir, "claim.png"));
            var payment = w.OpenFiles[1].Payments[0];
            w.FileList.SelectedItem = w.FileList.Items.OfType<ListEntry>().Single(e => e.Payment == payment && e.Claim == null);
            Save(w, Path.Combine(dir, "payment.png"));
            w.Open(Path.Combine(folder, "problems", "unbalanced-835.x12"));
            w.Tabs.SelectedIndex = 1;
            Save(w, Path.Combine(dir, "checks.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Tabs() => HeadlessApp.Run(() =>
    {
        if (Folder() is not { } dir) return;
        var folder = Samples.Copy();
        var w = Window(folder);
        try
        {
            w.Tabs.SelectedIndex = 2;
            w.ClaimList.SelectedItem = w.Reconciled.Claims.Single(m => m.Billed.Account == "RB26105");
            Save(w, Path.Combine(dir, "claims.png"));
            w.Tabs.SelectedIndex = 3;
            Save(w, Path.Combine(dir, "deposits.png"));
            w.Tabs.SelectedIndex = 4;
            Save(w, Path.Combine(dir, "underpayments.png"));
            w.Tabs.SelectedIndex = 5;
            w.WorkList.SelectedItem = w.Work[0];
            Save(w, Path.Combine(dir, "denials.png"));
            w.Tabs.SelectedIndex = 6;
            Save(w, Path.Combine(dir, "status.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Ai_chat() => HeadlessApp.Run(() =>
    {
        if (Folder() is not { } dir) return;
        var root = Samples.Copy();
        var w = new MainWindow { Width = 1700, Height = 950, Today = () => MatchingTests.Today, Now = () => StatusTests.SampleTime };
        w.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        w.AgentSettings.ChatProvider = "OpenAICompatible";
        w.AgentSettings.ChatServerUrl = "http://localhost:1234/v1";
        w.AgentSettings.ChatServerModel = "qwen3-8b";
        w.AgentSettings.WorkingFolder = root;
        w.Show();
        try
        {
            foreach (var f in AgentTests.Rig.Everything) w.Open(Path.Combine(root, f));
            if (!w.ChatHolder.IsVisible) w.ToggleChat();
            var server = new FakeModelServer("http://localhost:1234/v1")
                .Reply(Say("I'll read the denials."), Call("c1", "get_worklist", new { }))
                .Reply(Call("c2", "get_work_item", new { denial = "W01" }), Call("c3", "get_work_item", new { denial = "W02" }))
                .Reply(Say("Showing you W01."), Call("c4", "show_in_view", new { handle = "W01" }))
                .Reply(Say("Six denials to work, $2,080.00 at stake. This week:\n\n" +
                           "• W01 (C17, $160.00): physical therapy denied as not medically necessary (CO-50). Appeal with the plan of care and the notes; 48 days left.\n" +
                           "• W02 (C03, $1,200.00): the MRI had no authorization on the claim (CO-197, M62). Add the number in REF*G1 and send a corrected claim; 76 days left.\n" +
                           "• W03 (C04, $220.00): the visit needs modifier 25 (N822). Correct and resubmit.\n" +
                           "• W04 (C14, $220.00): another payer is primary (CO-22): bill it first.\n\n" +
                           "W06 was filed too late (CO-29): write it off unless you have proof it went in on time. W07 is a bundled blood draw: nothing to do. Names and claim numbers here are masked."));
            var panel = w.Chat!;
            panel.ServerFactory = (_, key) => server.Client(key);
            panel.Input.Text = "Which denials should I work this week?";
            panel.Send();
            AgentTests.Wait(panel.Running.ContinueWith(_ => true));
            Save(w, Path.Combine(dir, "ai-chat.png"));
            panel.SetupOpen = true;
            Save(w, Path.Combine(dir, "ai-chat-setup.png"));
        }
        finally { w.Close(); }
    });
}
