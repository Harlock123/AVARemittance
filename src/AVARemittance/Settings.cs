using System.Text.Json;

namespace AVARemittance;

/// <summary>What the program remembers between runs: the tolerance, claim status and the clearinghouse. Kept in the settings folder; the clearinghouse's password is in a file of its own.</summary>
public sealed class AppSettings
{
    /// <summary>How far below the fee schedule a line may be allowed before it's flagged, per line.</summary>
    public decimal Tolerance { get; set; } = 1.00m;

    /// <summary>Who asks about claim status, and when.</summary>
    public Core.Status.StatusSettings Status { get; set; } = new();

    /// <summary>The clearinghouse to ask in real time; off until the person turns it on. Its password isn't here.</summary>
    public Core.Sending.Clearinghouse Clearinghouse { get; set; } = new();

    /// <summary>Where AVA Eligibility is, to open a Check Eligibility Again list in; empty: looked for on the PATH.</summary>
    public string EligibilityProgram { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(MainWindow.SettingsFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(MainWindow.SettingsFolder);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
    }
}

public sealed partial class MainWindow
{
    /// <summary>Tests point the settings folder at a temporary one.</summary>
    public static string? FolderOverride { get; set; }

    /// <summary>Where the program keeps its settings: the user's application data folder, AVARemittance.</summary>
    public static string SettingsFolder => FolderOverride ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVARemittance");
}
