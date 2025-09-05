using System.Text.Json.Serialization;

namespace FavouriteAlbums.Core;

public sealed class AlbumAggregate
{
    public required string AlbumId { get; init; }
    public required string AlbumName { get; init; }
    public required List<string> Artists { get; init; }
    public required string ImageUrl { get; init; }
    public required string Uri { get; init; }

    public int Count { get; set; }
    public int Score { get; set; }
    public Dictionary<int, int> StarCounts { get; set; } = new();

    // % scoring fields
    public double WeightedSum { get; set; }    // sum of per-track weights
    public int Denominator { get; set; }    // total_tracks - excluded_on_album
    public int TotalTracks { get; set; }    // captured from SimplifiedAlbum.TotalTracks
    public double Percent => Denominator > 0 ? (WeightedSum / Denominator) * 100.0 : 0.0;
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
    [JsonPropertyName("album_type")] public string? AlbumType { get; set; }   // "album" | "single" | "compilation"
    [JsonPropertyName("total_tracks")] public int TotalTracks { get; set; } // full track count for this album
}

public sealed class SimplifiedArtist { public string? Name { get; set; } }
public sealed class SimplifiedImage { public string? Url { get; set; } public int Width { get; set; } public int Height { get; set; } }
