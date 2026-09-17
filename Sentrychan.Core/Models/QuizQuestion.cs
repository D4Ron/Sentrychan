namespace Sentrychan.Core.Models;

/// <summary>Quiz difficulty, driven by how popular (on AniList) the picked anime is.</summary>
public enum QuizDifficulty { Easy, Normal, Challenge }

/// <summary>
/// One anime-quiz round: a theme (OP/ED) to play, the anime it belongs to, and a set of
/// shuffled multiple-choice options (the correct one plus decoys from the same tier).
/// </summary>
public class QuizQuestion
{
    /// <summary>Direct video URL of the OP/ED (animethemes CDN, .webm, larger).</summary>
    public string VideoUrl { get; set; } = string.Empty;

    /// <summary>Direct audio URL (.ogg, ~1–2 MB — played during the guessing phase).</summary>
    public string AudioUrl { get; set; } = string.Empty;

    /// <summary>Audio prefetched to disk (guess clip) — played locally for an instant start.</summary>
    public string? LocalAudioPath { get; set; }

    /// <summary>Video prefetched to disk (reveal clip) — played on reveal to jog memory.</summary>
    public string? LocalVideoPath { get; set; }

    /// <summary>e.g. "OP1", "ED3" — shown after answering.</summary>
    public string ThemeLabel { get; set; } = string.Empty;

    public string CorrectAnime { get; set; } = string.Empty;
    public int? Year { get; set; }

    /// <summary>4 options, shuffled, including the correct answer.</summary>
    public List<string> Options { get; set; } = [];
}
