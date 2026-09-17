using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Creates and manages Hyperbeam virtual browser sessions for watch parties.
/// Rooms are stored in Supabase so friends can discover and join them.
/// </summary>
public interface IHyperbeamService
{
    /// <summary>Whether the Hyperbeam API key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Creates a Hyperbeam VM session, saves the room to Supabase, and returns it.
    /// The embed_url can be opened in any browser to join the shared session.
    /// </summary>
    Task<WatchRoomEntry?> CreateRoomAsync(
        string seriesTitle, int malId, int? episode,
        string? startUrl = null, CancellationToken ct = default);

    /// <summary>
    /// Fetches active rooms visible to the current user (own + friends').
    /// </summary>
    Task<List<WatchRoomEntry>> GetActiveRoomsAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks a room as inactive (host closes it). Does NOT terminate the Hyperbeam VM
    /// (it will expire on its own after the idle timeout).
    /// </summary>
    Task CloseRoomAsync(string roomId, CancellationToken ct = default);
}
