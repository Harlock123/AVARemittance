using AVARemittance.Core.Remittance;

namespace AVARemittance.Tests;

/// <summary>The samples folder, found from the test's output folder, and its files read.</summary>
public static class Samples
{
    public static string Folder { get; } = Find();

    public static string Path(string name) => System.IO.Path.Combine(Folder, name);

    public static RemitFile Load(string name) => RemitReader.Load(Path(name));

    /// <summary>A claim in a sample, by account (the first when the payer split or reversed it).</summary>
    public static ClaimPayment Claim(string file, string account) => Load(file).Claims.First(c => c.Account == account);

    /// <summary>A copy of the samples in a new temporary folder (agents and tests write into it).</summary>
    public static string Copy()
    {
        var root = Directory.CreateTempSubdirectory("remittance-samples-").FullName;
        foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
        {
            var to = System.IO.Path.Combine(root, System.IO.Path.GetRelativePath(Folder, f));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return root;
    }

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(System.IO.Path.Combine(d.FullName, "samples")) && File.Exists(System.IO.Path.Combine(d.FullName, "AVARemittance.slnx")))
                return System.IO.Path.Combine(d.FullName, "samples");
        throw new DirectoryNotFoundException("The samples folder wasn't found above the test's output folder.");
    }
}
