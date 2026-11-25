using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AVARemittance.Mcp;

namespace AVARemittance.Agents;

/// <summary>Runs work on Avalonia's UI thread, where pictures of the view are drawn.</summary>
internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// <c>AVARemittance --mcp</c>: the MCP server with no window. Avalonia starts on its headless platform with Skia (so
/// render_view draws as the window does, in a hidden window of its own), its dispatcher runs on this thread, and the
/// server runs beside it until the client goes away or Ctrl+C.
/// </summary>
internal static class AgentMain
{
    public static int Run(string[] args)
    {
        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var stage = new Panel { Width = 0, Height = 0, ClipToBounds = true };
        new Window { Content = stage, Width = 10, Height = 10 }.Show();
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var painter = new ViewPainter(stage);
        Mcp.McpCommandLine.Settings = () => { var s = AppSettings.Load(); return (s.Tolerance, s.Status); };
        var server = Task.Run(() => McpCommandLine.RunAsync(args, () => painter, new AvaloniaThread(), input, output, Console.Error, stop.Token));
        server.ContinueWith(_ => Dispatcher.UIThread.Post(() => stop.Cancel()), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(stop.Token);
        try
        {
            return server.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"The MCP server stopped: {e.Message}");
            return 1;
        }
    }

    /// <summary>The app for headless work: the Fluent theme, and no main window.</summary>
    private sealed class HeadlessApp : Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }
}
