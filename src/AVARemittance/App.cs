using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AVARemittance;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        // Denser lists than Fluent's defaults: a remittance has dozens of claims.
        Styles.Add(new Style(x => x.OfType<TreeViewItem>()) { Setters = { new Setter(TreeViewItem.MinHeightProperty, 26.0) } });
        Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 4)), new Setter(ListBoxItem.MinHeightProperty, 26.0) } });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // AVARemittance [files…]: opens 835s and the files beside them.
            var args = desktop.Args ?? [];
            desktop.MainWindow = new MainWindow(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
