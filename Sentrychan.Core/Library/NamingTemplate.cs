using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sentrychan.Core.Library;

public enum NamingPreset { JellyfinPlex, Minimal, Custom }

/// <summary>Everything a library path can be built from.</summary>
public sealed record EpisodeNaming(
    string Title,
    int? Year,
    int Season,
    int? Episode,
    string Extension,
    string? Group = null,
    string? Quality = null,
    int? Version = null,
    bool IsMovie = false);

/// <summary>
/// How downloads are named in the library. A template is a relative path with '/' between
/// folders and tokens in braces: {Title} {Year} {Season} {Episode} {Group} {Quality} {Version},
/// numbers optionally padded ({Season:00}). A token with no value disappears along with the
/// brackets around it, so "{Title} ({Year})" is just "Title" while the year is unknown.
/// Movies always use <see cref="MovieTemplate"/> — a movie has no season or episode to name.
/// </summary>
public sealed partial class NamingTemplate
{
    public const string PresetKey   = "NamingPreset";
    public const string TemplateKey = "NamingTemplate";

    public const string JellyfinTemplate = "{Title} ({Year})/Season {Season:00}/{Title} S{Season:00}E{Episode:00}";
    public const string MinimalTemplate  = "{Title}/Season {Season}/{Episode:00}";
    public const string MovieTemplate    = "{Title} ({Year})/{Title} ({Year})";

    private static readonly string[] KnownTokens = ["Title", "Year", "Season", "Episode", "Group", "Quality", "Version"];

    public NamingPreset Preset { get; }
    public string Template { get; }

    private NamingTemplate(NamingPreset preset, string template)
    {
        Preset = preset;
        Template = template;
    }

    public static NamingTemplate Default { get; } = new(NamingPreset.JellyfinPlex, JellyfinTemplate);

    public static NamingTemplate For(NamingPreset preset, string? custom = null) => preset switch
    {
        NamingPreset.Minimal => new(preset, MinimalTemplate),
        NamingPreset.Custom when Validate(custom) == null => new(preset, custom!.Trim()),
        _ => Default,
    };

    /// <summary>From the stored settings; anything missing or invalid means the default.</summary>
    public static NamingTemplate FromConfig(string? preset, string? custom) =>
        Enum.TryParse<NamingPreset>(preset, ignoreCase: true, out var p) ? For(p, custom) : Default;

    /// <summary>Null when the template is usable, else what's wrong with it, in words for the settings page.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "The template is empty.";
        if (!template.Contains("{Title}", StringComparison.Ordinal)) return "The template needs {Title}.";
        if (!template.Contains("{Episode", StringComparison.Ordinal)) return "The template needs {Episode} — without it every episode gets the same name.";
        foreach (Match m in TokenPattern().Matches(template))
            if (!KnownTokens.Contains(m.Groups["name"].Value))
                return $"Unknown token {{{m.Groups["name"].Value}}}. Use {string.Join(" ", KnownTokens.Select(t => "{" + t + "}"))}.";
        var parts = template.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Trim() is "" or "." or "..")) return "The template can't have empty, '.' or '..' folders.";
        if (template.TrimStart().StartsWith('/') || Path.IsPathRooted(template)) return "The template is relative to the library folder.";
        return null;
    }

    /// <summary>The path under the library folder, with the platform's separators and the extension.</summary>
    public string Render(EpisodeNaming n)
    {
        var template = n.IsMovie ? MovieTemplate : Template;
        var parts = template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => CleanSegment(TokenPattern().Replace(part, m => Value(m, n))))
            .Where(p => p.Length > 0)
            .ToList();
        if (parts.Count == 0) parts.Add(CleanSegment(n.Title));

        var ext = n.Extension.StartsWith('.') ? n.Extension : "." + n.Extension;
        parts[^1] += ext;
        return Path.Combine([.. parts]);
    }

    /// <summary>The folders a rendered path would go in, relative to the library — for a file whose own name is kept.</summary>
    public string RenderFolder(EpisodeNaming n) =>
        Path.GetDirectoryName(Render(n with { Extension = ".x" })) ?? string.Empty;

    private static string Value(Match m, EpisodeNaming n)
    {
        var format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
        string Num(int? v) => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : string.Empty;
        return m.Groups["name"].Value switch
        {
            "Title"   => n.Title,
            "Year"    => Num(n.Year),
            "Season"  => Num(n.Season),
            "Episode" => Num(n.Episode),
            "Group"   => n.Group ?? string.Empty,
            "Quality" => n.Quality ?? string.Empty,
            "Version" => n.Version is > 1 ? $"v{n.Version}" : string.Empty,
            _         => m.Value,
        };
    }

    /// <summary>
    /// Tidies what an empty token leaves behind — "()" and "[]", doubled spaces, dangling
    /// separators — and drops characters no Windows folder may contain, so a library on a
    /// shared drive stays readable from every OS. Dropped rather than replaced, as the app
    /// always has ("Re:ZERO" → "ReZERO"), so folder names stay comparable with older ones.
    /// </summary>
    public static string CleanSegment(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c)))
                sb.Append(c);
        s = sb.ToString();
        string before;
        do
        {
            before = s;
            s = EmptyBrackets().Replace(s, " ");
            s = MultiSpace().Replace(s, " ");
            s = DanglingSeparator().Replace(s, " ");
        } while (s != before);
        // Windows drops trailing dots and spaces from names, which would make the path we
        // record differ from the one on disk.
        return s.Trim().Trim('-', '–', '_').TrimEnd('.', ' ').Trim();
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)(?::(?<fmt>[0#]+))?\}")]
    private static partial Regex TokenPattern();
    [GeneratedRegex(@"\(\s*\)|\[\s*\]|\{\s*\}")]
    private static partial Regex EmptyBrackets();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
    [GeneratedRegex(@"\s[-–_.]\s*$|^\s*[-–_.]\s|\s[-–]\s(?=[-–])")]
    private static partial Regex DanglingSeparator();
}
