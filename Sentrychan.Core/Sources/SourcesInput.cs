using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sentrychan.Core.Sources;

/// <summary>A source pack found in what the user picked: its file name and its bytes.</summary>
public sealed record SourcesPackFile(string Name, byte[] Bytes);

/// <summary>
/// Everything a user hands the importer, in whatever shape it arrived: the <c>.scsources</c> file,
/// the same file renamed to <c>.zip</c> or re-zipped, the folder it unpacks to (a Mac unpacks a zip
/// on a double-click), a bare <c>sources.json</c>, a source pack's DLL on its own, or several of
/// these at once. A first tester unpacked the file and then had nothing the app would take.
/// </summary>
public sealed class SourcesInput
{
    public SourcesManifest Manifest { get; } = new();
    public List<SourcesPackFile> Packs { get; } = [];
    /// <summary>What was looked at and left out, with the reason — shown to the user.</summary>
    public List<string> Skipped { get; } = [];
    public bool FoundManifest { get; private set; }

    public bool IsEmpty => !FoundManifest && Packs.Count == 0;

    private const int MaxDepth = 4;

    public static SourcesInput Read(IEnumerable<string> paths)
    {
        var input = new SourcesInput();
        foreach (var path in paths) input.Add(path, 0);
        return input;
    }

    public static SourcesInput Read(string path) => Read([path]);

    private void Add(string path, int depth)
    {
        if (Directory.Exists(path)) { AddFolder(path, depth); return; }
        if (!File.Exists(path)) { Skipped.Add($"{Path.GetFileName(path)}: not found"); return; }
        if (IsJunk(Path.GetFileName(path))) return;

        if (IsZip(path))
        {
            using var zip = ZipFile.OpenRead(path);
            AddZip(zip, Path.GetFileName(path));
            return;
        }

        var name = Path.GetFileName(path);
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) AddPack(name, File.ReadAllBytes(path));
        else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(SourcesFile.Extension, StringComparison.OrdinalIgnoreCase))
            AddManifest(File.ReadAllText(path), name);
        else if (depth == 0) Skipped.Add($"{name}: not a sources file, folder or source pack");
    }

    private void AddFolder(string dir, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (IsJunk(name)) continue;
            if (name.Equals("sources.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(SourcesFile.Extension, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                Add(file, depth + 1);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (!IsJunk(Path.GetFileName(sub))) AddFolder(sub, depth + 1);
    }

    private void AddZip(ZipArchive zip, string label)
    {
        var any = false;
        foreach (var entry in zip.Entries)
        {
            // Skip folders, and the "__MACOSX/._name" copies macOS adds when it zips something.
            if (entry.FullName.EndsWith('/') || entry.FullName.Split('/').Any(IsJunk)) continue;
            if (entry.Name.Equals("sources.json", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(entry.Open());
                AddManifest(reader.ReadToEnd(), $"{label}/{entry.FullName}");
                any = true;
            }
            else if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                AddPack(entry.Name, ms.ToArray());
                any = true;
            }
        }
        if (!any) Skipped.Add($"{label}: a zip with no sources.json or source pack inside");
    }

    private void AddManifest(string json, string label)
    {
        SourcesManifest m;
        try { m = SourcesFile.Parse(json); }
        catch (InvalidDataException ex) { Skipped.Add($"{label}: {ex.Message}"); return; }
        FoundManifest = true;
        foreach (var f in m.Feeds)
            if (!Manifest.Feeds.Any(x => string.Equals(x.Url.Trim(), f.Url.Trim(), StringComparison.OrdinalIgnoreCase)))
                Manifest.Feeds.Add(f);
        if (string.IsNullOrWhiteSpace(Manifest.PreferredReleaseGroups)) Manifest.PreferredReleaseGroups = m.PreferredReleaseGroups;
        foreach (var r in m.MihonRepositories)
            if (!Manifest.MihonRepositories.Contains(r, StringComparer.OrdinalIgnoreCase)) Manifest.MihonRepositories.Add(r);
        Manifest.Name ??= m.Name;
    }

    private void AddPack(string name, byte[] bytes)
    {
        if (CheckPack(bytes) is { } why) { Skipped.Add($"{name}: {why}"); return; }
        // The same pack twice (the file and its unpacked folder): the later copy wins.
        Packs.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Packs.Add(new SourcesPackFile(name, bytes));
    }

    /// <summary>
    /// Why these bytes aren't a source pack, or null when they are: a .NET assembly built against
    /// Sentrychan's contracts. Read from the metadata only — nothing is loaded or run here.
    /// </summary>
    public static string? CheckPack(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata) return "not a .NET library";
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return "not a .NET library";
            var refsCore = md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .Any(n => n.Equals("Sentrychan.Core", StringComparison.OrdinalIgnoreCase));
            return refsCore ? null : "a library, but not a Sentrychan source pack";
        }
        catch (BadImageFormatException) { return "not a .NET library (the file may be damaged)"; }
    }

    /// <summary>Things operating systems leave beside files that must never be read as sources.</summary>
    public static bool IsJunk(string name) =>
        name.Length == 0 || name.StartsWith("._", StringComparison.Ordinal) || name == "__MACOSX"
        || name.Equals(".DS_Store", StringComparison.Ordinal) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    internal static bool IsZip(string path)
    {
        Span<byte> head = stackalloc byte[2];
        using var f = File.OpenRead(path);
        return f.Read(head) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
    }
}
