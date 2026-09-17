namespace Sentrychan.Core.Models;

public class TitleAlias
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public string Alias { get; set; } = string.Empty;

    // Navigation
    public Series? Series { get; set; }
}
