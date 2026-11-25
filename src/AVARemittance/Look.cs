using Avalonia.Media;
namespace AVARemittance;

/// <summary>The colours and fonts the window is drawn in: dark, as the other AVA tools, a colour for each outcome: green paid, yellow partly, orange denied, red doesn't balance.</summary>
internal static class Look
{
    public static readonly IBrush Window = Brush("#14161B");
    public static readonly IBrush Bar = Brush("#1B1E25");
    public static readonly IBrush Card = Brush("#1F232B");
    public static readonly IBrush Border = Brush("#2D323D");
    public static readonly IBrush Heading = Brush("#8C93A1");
    public static readonly IBrush Text = Brush("#DDE1E8");
    public static readonly IBrush Dim = Brush("#7D8594");
    public static readonly IBrush Accent = Brush("#7FC8F2");
    public static readonly IBrush Code = Brush("#C9D1DC");
    public static readonly IBrush Gutter = Brush("#5B6372");
    public static readonly IBrush Highlight = Brush("#3B3420");
    public static readonly IBrush Sheet = Brush("#101217");
    public static readonly IBrush SegmentId = Brush("#7FD1C7");
    public static readonly IBrush Separator = Brush("#6E7686");
    public static readonly IBrush Reject = Brush("#FF7A7A");
    public static readonly IBrush Deny = Brush("#F2A65A");
    public static readonly IBrush Warn = Brush("#E8D27A");
    public static readonly IBrush Clean = Brush("#8FD19E");

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, Courier New, monospace");

    private static SolidColorBrush Brush(string colour) => new(Color.Parse(colour));
}
