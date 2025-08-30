namespace FavouriteAlbums.Core;

public sealed class AppConfig
{
    public required string SpotifyClientId { get; init; }
    public required string SpotifyClientSecret { get; init; }
    public required string SpotifyRefreshToken { get; init; }
    public required string OutputDir { get; init; }
    public required Dictionary<int, string> StarPlaylists { get; init; } // 1–5 star playlists
    public required string FillerPlaylistId { get; init; }
    public string? ExcludedPlaylistId { get; init; }
}
