using Avalonia;

namespace AVARemittance;

internal static class Program
{
    /// <summary>
    /// Starts the window, opening any files given. With <c>--mcp</c> it opens no window: it serves the Model Context
    /// Protocol for AI agents instead (see <see cref="Mcp.McpCommandLine"/> and Agents/AgentMain).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (Mcp.McpCommandLine.Wanted(args)) return Agents.AgentMain.Run(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
