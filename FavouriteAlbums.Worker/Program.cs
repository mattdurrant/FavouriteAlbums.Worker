using System.Text;
using FavouriteAlbums.Core;

namespace FavouriteAlbums.Worker;

internal class Program
{
    static async Task<int> Main()
    {
        try
        {
            var cfg = new AppConfig
            {
                SpotifyClientId = Env("SPOTIFY_CLIENT_ID"),
                SpotifyClientSecret = Env("SPOTIFY_CLIENT_SECRET"),
                SpotifyRefreshToken = Env("SPOTIFY_REFRESH_TOKEN"),
                OutputDir = Environment.GetEnvironmentVariable("OUTPUT_DIR") ?? "out",
                StarPlaylists = ParseStarPlaylists(Env("STAR_PLAYLISTS")),
                FillerPlaylistId = Env("FILLER_PLAYLIST_ID"),
                ExcludedPlaylistId = Environment.GetEnvironmentVariable("EXCLUDED_PLAYLIST_ID")
            };

            Directory.CreateDirectory(cfg.OutputDir);

            var starWeights = ParseStarWeights(Environment.GetEnvironmentVariable("STAR_WEIGHTS"));


            using var http = new HttpClient();
            var token = await SpotifyApi.GetAccessTokenAsync(http, cfg.SpotifyClientId, cfg.SpotifyClientSecret, cfg.SpotifyRefreshToken);

            // Build exclusion set
            var excludedTrackIds = new HashSet<string>(StringComparer.Ordinal);

            async Task AddPlaylistTracksTo(HashSet<string> set, string playlistId)
            {
                await foreach (var t in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId))
                    if (!string.IsNullOrWhiteSpace(t?.Uri) && t.Uri.StartsWith("spotify:track:"))
                        set.Add(t.Uri);
            }

            // Filler
            await AddPlaylistTracksTo(excludedTrackIds, cfg.FillerPlaylistId);
            // Single excluded playlist (optional)
            if (!string.IsNullOrWhiteSpace(cfg.ExcludedPlaylistId))
                await AddPlaylistTracksTo(excludedTrackIds, cfg.ExcludedPlaylistId!);

            // ---- Aggregate with weights; global de-dup ----
            var albums = new Dictionary<string, AlbumAggregate>(StringComparer.Ordinal);
            var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (stars, playlistId) in cfg.StarPlaylists.OrderByDescending(kv => kv.Key))
            {
                var weight = starWeights.TryGetValue(stars, out var w) ? w : 0.0;
                Console.WriteLine($"Reading {stars}★ playlist {playlistId} (weight {weight})...");
                await foreach (var track in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId))
                {
                    var albumType = track.Album?.AlbumType?.ToLowerInvariant();
                    if (albumType is "single" or "compilation") continue;

                    if (track?.Album?.Id is null || string.IsNullOrWhiteSpace(track.Uri)) continue;
                    if (!track.Uri.StartsWith("spotify:track:")) continue;
                    if (excludedTrackIds.Contains(track.Uri)) continue;
                    if (!seenTrackIds.Add(track.Uri)) continue;

                    var id = track.Album.Id;
                    if (!albums.TryGetValue(id, out var agg))
                    {
                        agg = new AlbumAggregate
                        {
                            AlbumId = id,
                            AlbumName = track.Album.Name ?? "",
                            Artists = track.Album.Artists?.Select(a => a.Name ?? "").ToList() ?? new(),
                            ImageUrl = track.Album.Images?.OrderByDescending(i => i.Width).FirstOrDefault()?.Url ?? "",
                            Uri = track.Album.Uri ?? "",
                            Count = 0,
                            Score = 0,
                            StarCounts = new() { { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 } },
                            WeightedSum = 0.0,
                            Denominator = 0
                        };
                        albums[id] = agg;
                    }

                    agg.Count += 1;
                    agg.Score += stars; // legacy
                    agg.StarCounts[stars] = agg.StarCounts.GetValueOrDefault(stars) + 1;
                    agg.WeightedSum += weight;
                }
            }

            // ---- Compute denominators using full album tracklists minus excludes ----
            foreach (var agg in albums.Values)
            {
                var albumTracks = await SpotifyApi.GetAllAlbumTrackUrisAsync(http, token, agg.AlbumId);
                if (albumTracks.Count == 0)
                {
                    // fallback: if Spotify didn’t return tracks (rare), approximate denom by rated tracks counted
                    agg.Denominator = agg.Count;
                    continue;
                }

                // Remove any tracks that are in filler/excluded playlists from the denominator
                albumTracks.ExceptWith(excludedTrackIds);

                agg.Denominator = albumTracks.Count;
            }

            // ---- Rank by percentage, then by 5★ count, then by total rated tracks ----
            var ranked = albums.Values
                .Where(a => a.Denominator > 0) // avoid divide-by-zero
                .OrderByDescending(a => a.Percent)
                .ThenByDescending(a => a.StarCounts.GetValueOrDefault(5))
                .ThenByDescending(a => a.Count)
                .ThenBy(a => a.AlbumName)
                .ToList();

            var topN = EnvInt("TOP_N", 100);   // defaults to 100 if not set
            var total = ranked.Count;          // (optional) if you want to display "Top 100 of X"
            ranked = ranked.Take(topN).ToList();

            var title = $"Matt’s Favourite Albums — Top {ranked.Count} (by %)";
            var html = HtmlRenderer.Render(ranked, title);
            
            var outPath = Path.Combine(cfg.OutputDir, "index.html");
            await File.WriteAllTextAsync(outPath, html, Encoding.UTF8);

            Console.WriteLine($"✅ Wrote {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("❌ " + ex.Message);
            Console.Error.WriteLine("If this mentions a missing environment variable, set it in your Debug profile.");
            return 1;
        }
    }

    // ---- Helpers ----
    private static string Env(string name, bool required = true)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (required && string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Missing environment variable: {name}");
        return v ?? "";
    }

    private static Dictionary<int, string> ParseStarPlaylists(string csv)
    {
        var dict = new Dictionary<int, string>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !int.TryParse(kv[0], out var stars) || stars is < 1 or > 5)
                throw new InvalidOperationException($"Invalid STAR_PLAYLISTS segment: '{part}' (expected like 5:abc123)");
            dict[stars] = kv[1];
        }
        if (!Enumerable.Range(1, 5).All(dict.ContainsKey))
            throw new InvalidOperationException("STAR_PLAYLISTS must include all 1..5 entries.");
        return dict;
    }

    private static Dictionary<int, double> ParseStarWeights(string? csv)
    {
        // default (balanced)
        var weights = new Dictionary<int, double> { { 5, 1.0 }, { 4, 0.8 }, { 3, 0.5 }, { 2, 0.25 }, { 1, 0.10 } };
        if (string.IsNullOrWhiteSpace(csv)) return weights;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (!int.TryParse(kv[0], out var stars)) continue;
            if (!double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) continue;
            if (stars is < 1 or > 5) continue;
            weights[stars] = w;
        }
        return weights;
    }

    private static int EnvInt(string name, int @default)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) && n > 0 ? n : @default;
    }
}
