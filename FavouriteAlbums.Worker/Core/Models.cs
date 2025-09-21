using System.Text.Json.Serialization;

namespace FavouriteAlbums.Core;


// -------------------------
// Spotify DTOs (playlist items, albums, artists, images)
// -------------------------
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

    // Extra fields we request via `fields=` so we don't need extra API calls
    [JsonPropertyName("album_type")] public string? AlbumType { get; set; }           // "album" | "single" | "compilation"
    [JsonPropertyName("total_tracks")] public int TotalTracks { get; set; }           // full track count on the album
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }       // "YYYY", "YYYY-MM", or "YYYY-MM-DD"
    [JsonPropertyName("release_date_precision")] public string? ReleaseDatePrecision { get; set; } // "year" | "month" | "day"
}

public sealed class SimplifiedArtist
{
    public string? Name { get; set; }
}

public sealed class SimplifiedImage
{
    public string? Url { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

// -------------------------
// View models used by our app
// -------------------------

// One track as displayed on the album page (with optional star rating)
public sealed class AlbumTrackView
{
    public int Number { get; set; }     // 1-based track number
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";  // https://open.spotify.com/track/...
    public int? Stars { get; set; }        // null if unrated; 1..5 if rated
}

// Aggregated info per album for scoring & rendering
public sealed class AlbumAggregate
{
    public required string AlbumId { get; init; }
    public required string AlbumName { get; init; }
    public required List<string> Artists { get; init; } = new();
    public required string ImageUrl { get; init; }
    public required string Uri { get; init; } // spotify:album:...

    // Legacy counters (kept for diagnostics if desired)
    public int Count { get; set; }                           // unique rated tracks counted (after excludes)
    public int Score { get; set; }                           // legacy integer score (sum of star values)
    public Dictionary<int, int> StarCounts { get; set; } = new(); // e.g., {5:12,4:3,...}

    // Percentage scoring fields
    public double WeightedSum { get; set; }                  // sum of per-track weights (e.g., 1 + 0.8 + ...)
    public int Denominator { get; set; }                  // album.total_tracks - excludedOnAlbum
    public int TotalTracks { get; set; }                  // as reported by Spotify
    public double RawPercent => Denominator > 0 ? (WeightedSum / Denominator) * 100.0 : 0.0;
    public double Percent => RawPercent > 100.0 ? 100.0 : RawPercent; // display cap at 100

    // Nice-to-have metadata
    public int? ReleaseYear { get; set; }

    // For renderer: detailed track list (Top N only, filled later)
    public List<AlbumTrackView> Tracks { get; } = new();
}
