using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentrychan.Core.Sources;

/// <summary>An RSS feed as a sources file carries it.</summary>
public sealed record SourcesFeed(string Url, string Type = "Priority", string? PreferredQuality = null);

/// <summary>
/// What a sources file says, apart from the packs it carries: the RSS feeds, the release groups to
/// prefer, and Mihon extension repositories. Everything is optional; unknown fields are ignored so
/// a newer app's file still opens in an older one.
/// </summary>
public sealed class SourcesManifest
{
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<SourcesFeed> Feeds { get; set; } = [];
    public string? PreferredReleaseGroups { get; set; }
    public List<string> MihonRepositories { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Feeds.Count == 0 && string.IsNullOrWhiteSpace(PreferredReleaseGroups) && MihonRepositories.Count == 0;
}

/// <summary>A sources file as read: its manifest and the names of the source packs inside it.</summary>
public sealed record SourcesFileContents(SourcesManifest Manifest, IReadOnlyList<string> PackNames);

/// <summary>
/// The ".scsources" file: one file that sets up someone's sources — feeds, preferred groups, Mihon
/// repositories and source packs. It's a zip holding <c>sources.json</c> and, optionally,
/// <c>packs/*.dll</c>. A bare JSON manifest is read too, for files written by hand.
/// The app ships no sources; this only moves the ones a user already has.
/// </summary>
public static class SourcesFile
{
    public const string Extension = ".scsources";
    private const string ManifestEntry = "sources.json";
    private const string PackFolder = "packs/";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string path, SourcesManifest manifest, IEnumerable<string> packFiles)
    {
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(ManifestEntry).Open()))
                writer.Write(JsonSerializer.Serialize(manifest, Json));
            foreach (var pack in packFiles)
                zip.CreateEntryFromFile(pack, PackFolder + Path.GetFileName(pack));
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static SourcesFileContents Read(string path)
    {
        if (!IsZip(path))
            return new(Parse(File.ReadAllText(path)), []);

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(ManifestEntry)
                    ?? throw new InvalidDataException("This isn't a sources file: it has no sources.json inside.");
        using var reader = new StreamReader(entry.Open());
        return new(Parse(reader.ReadToEnd()), Packs(zip).Select(e => e.Name).ToList());
    }

    /// <summary>Copies the file's source packs into <paramref name="destination"/>. Returns their names.</summary>
    public static IReadOnlyList<string> ExtractPacks(string path, string destination)
    {
        if (!IsZip(path)) return [];
        using var zip = ZipFile.OpenRead(path);
        var names = new List<string>();
        foreach (var entry in Packs(zip))
        {
            Directory.CreateDirectory(destination);
            // Only the file name is used, so an entry can't write outside the sources folder.
            entry.ExtractToFile(Path.Combine(destination, Path.GetFileName(entry.Name)), overwrite: true);
            names.Add(entry.Name);
        }
        return names;
    }

    private static IEnumerable<ZipArchiveEntry> Packs(ZipArchive zip) =>
        zip.Entries.Where(e => e.FullName.StartsWith(PackFolder, StringComparison.OrdinalIgnoreCase)
                               && e.FullName.IndexOf('/', PackFolder.Length) < 0
                               && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

    internal static SourcesManifest Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SourcesManifest>(json, Json)
                           ?? throw new InvalidDataException("The sources file is empty.");
            manifest.Feeds ??= [];
            manifest.MihonRepositories ??= [];
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The sources file can't be read: " + ex.Message, ex);
        }
    }

    private static bool IsZip(string path)
    {
        Span<byte> head = stackalloc byte[2];
        using var f = File.OpenRead(path);
        return f.Read(head) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
    }
}
