using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>Supplies anime-quiz rounds (guess-the-anime from its OP/ED).</summary>
public interface IAnimeQuizService
{
    /// <summary>
    /// Fetches one round at the given difficulty. When <paramref name="progressive"/> is set,
    /// the effective difficulty ramps up across the session using <paramref name="index"/> /
    /// <paramref name="total"/>. Returns null if a playable round couldn't be built.
    /// </summary>
    Task<QuizQuestion?> GetQuestionAsync(
        QuizDifficulty difficulty, int index, int total, bool progressive, CancellationToken ct = default);

    /// <summary>
    /// Fetches up to <paramref name="count"/> random OP/ED themes (anime + clips) for the
    /// Battle Royale bracket. Deduplicated by anime.
    /// </summary>
    Task<List<QuizTheme>> GetRandomThemesAsync(int count, CancellationToken ct = default);
}
