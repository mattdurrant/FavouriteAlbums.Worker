using System.Text.Json.Serialization;

namespace FavouriteAlbums.Core;

public sealed class AlbumTrackView
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";  // https://open.spotify.com/track/...
    public int? Stars { get; set; }        // null if unrated
}

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

    public int? ReleaseYear { get; set; }
    public List<AlbumTrackView> Tracks { get; } = new();
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
    public string? AlbumType { get; set; }   // "album" | "single" | "compilation"
    public int TotalTracks { get; set; }
    public string? ReleaseDate { get; set; } // e.g. "1995-08-01" or "1995"
    public string? ReleaseDatePrecision { get; set; } // "year" | "month" | "day"
}

public sealed class SimplifiedArtist { public string? Name { get; set; } }
public sealed class SimplifiedImage { public string? Url { get; set; } public int Width { get; set; } public int Height { get; set; } }
