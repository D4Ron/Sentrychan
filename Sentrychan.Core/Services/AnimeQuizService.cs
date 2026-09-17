using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>
/// Anime "guess the opening" quiz. Difficulty is popularity-based: candidates come from a
/// slice of AniList's popularity ranking (Easy = most popular, Challenge = obscure; progressive
/// ramps the slice across a session). The picked anime's OP/ED is then resolved on
/// animethemes.moe (MAL id → anime → theme), which supplies the audio (fast) and video clips.
/// </summary>
public class AnimeQuizService : IAnimeQuizService
{
    private const string AniList = "https://graphql.anilist.co";
    private const string AtResource = "https://api.animethemes.moe/resource";
    private const string AtAnime = "https://api.animethemes.moe/anime";
    private const int PerPage = 12;

    private readonly HttpClient _http;
    private readonly ILogger<AnimeQuizService> _logger;

    public AnimeQuizService(ILogger<AnimeQuizService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/1.0 (+quiz)");
    }

    public async Task<QuizQuestion?> GetQuestionAsync(
        QuizDifficulty difficulty, int index, int total, bool progressive, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var q = await BuildOneAsync(difficulty, index, total, progressive, ct);
                if (q != null) return q;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "[Quiz] fetch failed (attempt {N})", attempt); }
        }
        return null;
    }

    public async Task<List<QuizTheme>> GetRandomThemesAsync(int count, CancellationToken ct = default)
    {
        var result = new List<QuizTheme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Over-fetch a bit since some entries lack usable video/audio or duplicate an anime.
            var size = Math.Clamp(count * 2, count, 60);
            var url = "https://api.animethemes.moe/animetheme?include=anime,animethemeentries.videos.audio" +
                      $"&filter[has]=animethemeentries&sort=random&page[size]={size}";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("animethemes", out var themes)) return result;

            foreach (var t in themes.EnumerateArray())
            {
                if (result.Count >= count) break;
                if (!t.TryGetProperty("anime", out var anime)) continue;
                var name = anime.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name!)) continue;

                if (!t.TryGetProperty("animethemeentries", out var entries) || entries.GetArrayLength() == 0) continue;
                if (!entries[0].TryGetProperty("videos", out var vids) || vids.GetArrayLength() == 0) continue;
                var v = vids[0];
                var video = v.TryGetProperty("link", out var vl) ? vl.GetString() : null;
                var audio = v.TryGetProperty("audio", out var au) && au.TryGetProperty("link", out var al) ? al.GetString() : null;
                if (string.IsNullOrEmpty(video) && string.IsNullOrEmpty(audio)) continue;

                result.Add(new QuizTheme
                {
                    Anime = name!,
                    Year = anime.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                    ThemeLabel = t.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "",
                    VideoUrl = video ?? "",
                    AudioUrl = audio ?? ""
                });
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Quiz] random themes fetch failed"); }
        return result;
    }

    private async Task<QuizQuestion?> BuildOneAsync(QuizDifficulty difficulty, int index, int total, bool progressive, CancellationToken ct)
    {
        var (lo, hi) = RankRange(difficulty, index, total, progressive);
        var rank = Random.Shared.Next(lo, hi + 1);
        var page = Math.Max(1, rank / PerPage + 1);

        var candidates = await FetchCandidatesAsync(page, ct);
        if (candidates.Count < 4) return null;

        var shuffled = candidates.OrderBy(_ => Random.Shared.Next()).ToList();
        foreach (var answer in shuffled)
        {
            var theme = await FindThemeAsync(answer.MalId, ct);
            if (theme == null) continue;

            var decoys = shuffled
                .Where(c => c.MalId != answer.MalId && c.Name != answer.Name)
                .Select(c => c.Name).Distinct().Take(3).ToList();
            if (decoys.Count < 3) return null;

            var options = new List<string> { answer.Name };
            options.AddRange(decoys);

            return new QuizQuestion
            {
                VideoUrl = theme.Value.Video,
                AudioUrl = theme.Value.Audio,
                ThemeLabel = theme.Value.Label,
                Year = theme.Value.Year,
                CorrectAnime = answer.Name,
                Options = options.OrderBy(_ => Random.Shared.Next()).ToList()
            };
        }
        return null;
    }

    // Popularity rank window (1 = most popular). Progressive ramps the ceiling up quadratically.
    private static (int lo, int hi) RankRange(QuizDifficulty d, int index, int total, bool progressive)
    {
        if (progressive)
        {
            var t = total > 1 ? (double)index / (total - 1) : 0;   // 0..1
            var ceil = (int)(200 + t * t * 5800);                  // easy early, obscure late
            return (Math.Max(1, ceil / 2), Math.Max(30, ceil));
        }
        return d switch
        {
            QuizDifficulty.Easy      => (1, 300),
            QuizDifficulty.Normal    => (300, 1500),
            QuizDifficulty.Challenge => (1500, 6000),
            _                        => (1, 300)
        };
    }

    private async Task<List<(int MalId, string Name)>> FetchCandidatesAsync(int page, CancellationToken ct)
    {
        var query = "query($p:Int){Page(page:$p,perPage:" + PerPage +
                    "){media(type:ANIME,sort:POPULARITY_DESC,format_in:[TV,MOVIE,OVA,ONA]){idMal title{romaji english}}}}";
        var body = JsonSerializer.Serialize(new { query, variables = new { p = page } });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(AniList, content, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var list = new List<(int, string)>();
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("Page", out var pg)
            && pg.TryGetProperty("media", out var media))
        {
            foreach (var m in media.EnumerateArray())
            {
                if (!m.TryGetProperty("idMal", out var idm) || idm.ValueKind != JsonValueKind.Number) continue;
                var mal = idm.GetInt32();
                // title can be JSON null for some entries — guard before reading it.
                if (!m.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.Object) continue;
                var name = (title.TryGetProperty("romaji", out var ro) && ro.ValueKind == JsonValueKind.String ? ro.GetString() : null)
                        ?? (title.TryGetProperty("english", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() : null);
                if (mal > 0 && !string.IsNullOrWhiteSpace(name)) list.Add((mal, name!));
            }
        }
        return list;
    }

    private async Task<(string Video, string Audio, string Label, int? Year)?> FindThemeAsync(int malId, CancellationToken ct)
    {
        try
        {
            // MAL id → animethemes anime slug.
            var rjson = await _http.GetStringAsync(
                $"{AtResource}?filter%5Bsite%5D=MyAnimeList&filter%5Bexternal_id%5D={malId}&include=anime", ct);
            using var rdoc = JsonDocument.Parse(rjson);
            if (!rdoc.RootElement.TryGetProperty("resources", out var res) || res.GetArrayLength() == 0) return null;
            var animeArr = res[0].GetProperty("anime");
            if (animeArr.GetArrayLength() == 0) return null;
            var slug = animeArr[0].GetProperty("slug").GetString();
            if (string.IsNullOrEmpty(slug)) return null;

            // slug → themes with audio + video.
            var ajson = await _http.GetStringAsync(
                $"{AtAnime}/{slug}?include=animethemes.animethemeentries.videos.audio", ct);
            using var adoc = JsonDocument.Parse(ajson);
            var anime = adoc.RootElement.GetProperty("anime");
            int? year = anime.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;

            var playable = new List<(string Video, string Audio, string Label)>();
            foreach (var th in anime.GetProperty("animethemes").EnumerateArray())
            {
                var label = th.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "";
                if (!th.TryGetProperty("animethemeentries", out var entries) || entries.GetArrayLength() == 0) continue;
                if (!entries[0].TryGetProperty("videos", out var vids) || vids.GetArrayLength() == 0) continue;
                var v = vids[0];
                var video = v.TryGetProperty("link", out var vl) ? vl.GetString() : null;
                var audio = v.TryGetProperty("audio", out var au) && au.TryGetProperty("link", out var al) ? al.GetString() : null;
                if (!string.IsNullOrEmpty(video)) playable.Add((video!, audio ?? "", label));
            }
            if (playable.Count == 0) return null;

            var pick = playable[Random.Shared.Next(playable.Count)];
            return (pick.Video, pick.Audio, pick.Label, year);
        }
        catch { return null; }
    }
}
