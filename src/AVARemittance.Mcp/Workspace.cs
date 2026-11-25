using AVARemittance.Core.X12;

namespace AVARemittance.Mcp;

/// <summary>
/// The working folder an agent reads from and writes new files into. Every path is taken relative to it, and anything
/// that resolves outside it (through "..", an absolute path or a link) is refused. A file is only written when no file
/// is there yet, or this workspace wrote it earlier in the session: nothing the person has is ever written over, and
/// nothing is ever deleted.
/// </summary>
public sealed class Workspace(Func<string> root)
{
    /// <summary>The most files <see cref="List"/> returns.</summary>
    public const int MaxListed = 500;

    private readonly HashSet<string> _written = new(PathComparer);
    private readonly List<string> _order = [];

    public Workspace(string root) : this(() => root) { }

    public string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root()));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>A path made absolute against the root; one that leads outside it throws.</summary>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Name a file, relative to the working folder (list_files shows them).");
        var full = Path.GetFullPath(Path.Combine(Root, path.Trim()));
        if (!Inside(full))
            throw new ArgumentException($"{path} is outside the working folder ({Root}). Give a path inside it, such as \"out/{Path.GetFileName(full)}\".");
        return full;
    }

    /// <summary>An existing file inside the root (and not a link out of it).</summary>
    public string Existing(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"There's no file \"{path}\" in the working folder. list_files shows the files there are.");
        if (new FileInfo(full).ResolveLinkTarget(true) is { } target && !Inside(target.FullName))
            throw new ArgumentException($"\"{path}\" is a link to a file outside the working folder.");
        return full;
    }

    /// <summary>A path inside the root with one of <paramref name="extensions"/>, or an error naming them.</summary>
    public string Resolve(string path, string what, params string[] extensions)
    {
        var full = Resolve(path);
        var name = Path.GetFileName(full).ToLowerInvariant();
        if (!extensions.Any(e => name.EndsWith(e, StringComparison.Ordinal)))
            throw new NotSupportedException($"{what} is {(extensions.Length == 1 ? "a" : "one of")} {string.Join(", ", extensions)} file, not {(Path.GetExtension(name).Length == 0 ? "a file without an extension" : Path.GetExtension(name))}.");
        return full;
    }

    /// <summary>
    /// Makes ready to write a new file inside the root: refuses one that exists unless this workspace wrote it, and
    /// makes its folder. Call <see cref="Wrote"/> once it's written.
    /// </summary>
    public void CheckWritable(string fullPath)
    {
        if (File.Exists(fullPath) && !WroteBefore(fullPath))
            throw new IOException($"{Path.GetFileName(fullPath)} already exists, and agents only write new files. Choose another name.");
        if (Path.GetDirectoryName(fullPath) is { } dir) Directory.CreateDirectory(dir);
    }

    /// <summary>Notes a file this workspace wrote, so it may write over it again.</summary>
    public void Wrote(string fullPath)
    {
        lock (_written)
            if (_written.Add(fullPath)) _order.Add(fullPath);
    }

    public bool WroteBefore(string fullPath)
    {
        lock (_written) return _written.Contains(fullPath);
    }

    /// <summary>The files written so far, relative to the root, in the order they were first written.</summary>
    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_written) return _order.Select(Relative).ToList();
        }
    }

    public string Relative(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    /// <summary>The files the program reads (835s, 837s, 277s, charges lists, bank statements, fee schedules, payers' limits) under <paramref name="folder"/> (the root when null), found by their content.</summary>
    public (IReadOnlyList<string> Files, bool More) List(string? folder, bool recursive = true)
    {
        var start = Root;
        if (!string.IsNullOrWhiteSpace(folder) && folder.Trim() != ".")
        {
            start = Resolve(folder);
            if (!Directory.Exists(start)) throw new DirectoryNotFoundException($"There's no folder \"{folder}\" in the working folder.");
        }
        var skip = new[] { "bin", "obj", "node_modules", ".git", ".vs", ".idea" };
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(start, "*", options))
        {
            if (Relative(path).Split('/').Any(p => skip.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            if (new FileInfo(path).Length > 50_000_000 || Core.FileKinds.Of(path) == null) continue;
            if (found.Count == MaxListed) return (found, true);
            found.Add(Relative(path));
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return (found, false);
    }

    private bool Inside(string full) =>
        full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison);
}
