using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AVARemittance.Mcp;

namespace AVARemittance;

/// <summary>
/// Draws what render_view shows an agent's person: a title, a line under it, and source lines (numbered, some
/// highlighted) or a trace, in the window's own colours. Drawn on a hidden stage in the window (or the headless
/// server's), since controls need a window to take their look.
/// </summary>
public sealed class ViewPainter(Panel host) : IViewPainter
{
    public byte[] Picture(ViewPicture view, double scale)
    {
        var lines = new TextBlock { FontFamily = Look.Mono, FontSize = 12, Foreground = Look.Code };
        var width = (view.FirstLine + view.Lines.Count).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        for (var i = 0; i < view.Lines.Count; i++)
        {
            var n = view.FirstLine + i;
            var on = n >= view.HighlightFrom && n <= view.HighlightTo && view.HighlightFrom > 0;
            if (view.Numbered) lines.Inlines!.Add(new Run(n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width) + "  ") { Foreground = on ? Look.Accent : Look.Gutter });
            var text = view.Lines[i];
            lines.Inlines!.Add(new Run((text.Length > 160 ? text[..157] + "…" : text) + (i < view.Lines.Count - 1 ? "\n" : "")) { Background = on ? Look.Highlight : null });
        }
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = view.Title, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Look.Text });
        if (view.Subtitle.Length > 0) header.Children.Add(new TextBlock { Text = view.Subtitle, FontSize = 11.5, Foreground = Look.Heading });
        foreach (var note in view.Notes) header.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Look.Warn });
        var frame = new Border
        {
            Background = Look.Window,
            Padding = new Thickness(14),
            Child = new StackPanel { Spacing = 10, Children = { header, new Border { Background = Look.Sheet, Padding = new Thickness(10, 8), Child = lines } } },
        };
        var scaled = new LayoutTransformControl { Child = frame, LayoutTransform = new ScaleTransform(scale, scale) };
        var stage = new Canvas();
        stage.Children.Add(scaled);
        host.Children.Add(stage);
        try
        {
            scaled.Measure(Size.Infinity);
            var want = scaled.DesiredSize;
            scaled.Arrange(new Rect(want));
            scaled.UpdateLayout();
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(want.Width)), Math.Max(1, (int)Math.Ceiling(want.Height)));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(scaled);
            using var png = new MemoryStream();
            bitmap.Save(png);
            return png.ToArray();
        }
        finally
        {
            stage.Children.Clear();
            host.Children.Remove(stage);
        }
    }
}
