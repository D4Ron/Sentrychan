using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

public interface ITitleAliasService
{
    Task<List<TitleAlias>> GetForSeriesAsync(int seriesId, CancellationToken ct = default);
    Task AddAliasAsync(int seriesId, string alias, CancellationToken ct = default);
    Task RemoveAliasAsync(int aliasId, CancellationToken ct = default);
    Task<int?> FindSeriesIdByAliasAsync(string alias, CancellationToken ct = default);
}
