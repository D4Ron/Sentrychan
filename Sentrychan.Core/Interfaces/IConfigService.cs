namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Shared service for reading AppConfig values from the database.
/// Eliminates duplicated GetConfigValueAsync methods across services.
/// </summary>
public interface IConfigService
{
    Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default);

    /// <summary>Upserts an AppConfig value. Best-effort — never throws.</summary>
    Task SetValueAsync<T>(string key, T value, CancellationToken ct = default);
}
