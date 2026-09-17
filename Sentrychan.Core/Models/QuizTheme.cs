namespace Sentrychan.Core.Models;

/// <summary>A single OP/ED entry (anime + playable clip), used by the Battle Royale bracket.</summary>
public class QuizTheme
{
    public string Anime { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string ThemeLabel { get; set; } = string.Empty;   // "OP1", "ED2"
    public string AudioUrl { get; set; } = string.Empty;
    public string VideoUrl { get; set; } = string.Empty;

    /// <summary>Audio prefetched to disk for instant playback in the bracket.</summary>
    public string? LocalAudioPath { get; set; }

    public string Display => string.IsNullOrEmpty(ThemeLabel) ? Anime : $"{Anime} — {ThemeLabel}";
}
