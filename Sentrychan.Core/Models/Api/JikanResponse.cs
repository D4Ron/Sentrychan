using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class JikanResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("pagination")]
    public JikanPagination? Pagination { get; set; }
}

public class JikanPagination
{
    [JsonPropertyName("has_next_page")]
    public bool HasNextPage { get; set; }

    [JsonPropertyName("items")]
    public JikanPaginationItems? Items { get; set; }
}

public class JikanPaginationItems
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}