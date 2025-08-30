namespace FavouriteAlbums.Core;

public sealed class AlbumAggregate
{
    public required string AlbumId { get; init; }
    public required string AlbumName { get; init; }
    public required List<string> Artists { get; init; }
    public required string ImageUrl { get; init; }
    public required string Uri { get; init; }
    public int Count { get; set; }             // how many unique tracks counted
    public int Score { get; set; }             // weighted sum based on rating
    public Dictionary<int, int> StarCounts { get; set; } = new(); // {5: x, 4: y, ...}
}

// DTOs for Spotify responses
public sealed class PlaylistTracksPage
{
    public List<PlaylistItem> Items { get; set; } = new();
    public string? Next { get; set; }
}

public sealed class PlaylistItem
{
    public SimplifiedTrack? Track { get; set; }
}

public sealed class SimplifiedTrack
{
    public string? Name { get; set; }
    public string? Uri { get; set; }
    public SimplifiedAlbum? Album { get; set; }
}

public sealed class SimplifiedAlbum
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<SimplifiedArtist>? Artists { get; set; }
    public List<SimplifiedImage>? Images { get; set; }
    public string? Uri { get; set; }
}

public sealed class SimplifiedArtist { public string? Name { get; set; } }
public sealed class SimplifiedImage { public string? Url { get; set; } public int Width { get; set; } public int Height { get; set; } }
