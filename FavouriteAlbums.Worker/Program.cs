using System.Globalization;
using System.Text;
using FavouriteAlbums.Core;

namespace FavouriteAlbums.Worker;

internal class Program
{
    static async Task<int> Main()
    {
        try
        {
            // --- read env vars ---
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
            var starWeights = ParseStarWeights(Environment.GetEnvironmentVariable("STAR_WEIGHTS")); // optional override

            Directory.CreateDirectory(cfg.OutputDir);

            using var http = new HttpClient();
            var token = await SpotifyApi.GetAccessTokenAsync(http, cfg.SpotifyClientId, cfg.SpotifyClientSecret, cfg.SpotifyRefreshToken);

            // --- build exclusion sets: track URIs + per-album excluded counts ---
            var excludedTrackIds = new HashSet<string>(StringComparer.Ordinal);
            var excludedCountPerAlbumId = new Dictionary<string, int>(StringComparer.Ordinal);

            async Task AddPlaylistTracksTo(string playlistId)
            {
                await foreach (var t in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId))
                {
                    if (string.IsNullOrWhiteSpace(t?.Uri) || !t.Uri.StartsWith("spotify:track:")) continue;
                    excludedTrackIds.Add(t.Uri);
                    var aid = t.Album?.Id;
                    if (!string.IsNullOrWhiteSpace(aid))
                        excludedCountPerAlbumId[aid!] = excludedCountPerAlbumId.GetValueOrDefault(aid!, 0) + 1;
                }
            }

            // filler
            await AddPlaylistTracksTo(cfg.FillerPlaylistId);
            // single extra excluded playlist (optional)
            if (!string.IsNullOrWhiteSpace(cfg.ExcludedPlaylistId))
                await AddPlaylistTracksTo(cfg.ExcludedPlaylistId!);

            // --- aggregate albums with weights; skip singles & compilations; global de-dup ---
            var albums = new Dictionary<string, AlbumAggregate>(StringComparer.Ordinal);
            var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);
            var ratedTrackStars = new Dictionary<string, int>(StringComparer.Ordinal); // spotify:track:... -> 1..5

            foreach (var (stars, playlistId) in cfg.StarPlaylists.OrderByDescending(kv => kv.Key))
            {
                var weight = starWeights.TryGetValue(stars, out var w) ? w : 0.0;

                // Expected total (for sanity check)
                var expected = await SpotifyApi.GetPlaylistTotalAsync(http, token, playlistId);

                int fetched = 0, included = 0, skipExcluded = 0, skipDup = 0, skipNonAlbum = 0;

                Console.WriteLine($"→ Reading {stars}★ playlist {playlistId} (weight {weight}, expected ~{expected} items)…");

                await foreach (var track in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId, info => Console.WriteLine(info)))
                {
                    fetched++;

                    if (track?.Album?.Id is null || string.IsNullOrWhiteSpace(track.Uri)) continue;
                    if (!track.Uri.StartsWith("spotify:track:")) continue;

                    if (!ratedTrackStars.TryGetValue(track.Uri, out var prev) || stars > prev)
                        ratedTrackStars[track.Uri] = stars;

                    var albumType = track.Album.AlbumType?.ToLowerInvariant();
                    if (albumType is "single" or "compilation") { skipNonAlbum++; continue; }

                    if (excludedTrackIds.Contains(track.Uri)) { skipExcluded++; continue; }
                    if (!seenTrackIds.Add(track.Uri)) { skipDup++; continue; }

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
                            Denominator = 0,
                            TotalTracks = track.Album.TotalTracks
                        };

                        if (track.Album.ReleaseDate is { } rd && rd.Length >= 4)
                        {
                            if (int.TryParse(rd.Substring(0, 4), out var year)) agg.ReleaseYear = year;
                        }

                        albums[id] = agg;
                    }
                    else if (agg.TotalTracks == 0 && track.Album.TotalTracks > 0)
                    {
                        agg.TotalTracks = track.Album.TotalTracks;
                    }

                    agg.Count += 1;
                    agg.Score += stars; // legacy
                    agg.StarCounts[stars] = agg.StarCounts.GetValueOrDefault(stars) + 1;
                    agg.WeightedSum += weight;
                    included++;
                }

                Console.WriteLine($"   ✓ {stars}★: fetched {fetched}/{expected}, included {included}, " +
                                  $"skipped excluded {skipExcluded}, dup {skipDup}, non-album {skipNonAlbum}");
            }

            // --- compute denominators WITHOUT per-album API calls ---
            foreach (var agg in albums.Values)
            {
                var excludedOnAlbum = excludedCountPerAlbumId.GetValueOrDefault(agg.AlbumId);
                var total = agg.TotalTracks > 0 ? agg.TotalTracks : agg.Count; // fallback if Spotify didn't send total_tracks
                agg.Denominator = Math.Max(0, total - excludedOnAlbum);
            }

            // --- rank by percentage, then 5★ count, then total rated tracks; take top N (default 100) ---
            var ranked = albums.Values
                .Where(a => a.Denominator > 0)
                .OrderByDescending(a => a.Percent)
                .ThenByDescending(a => a.StarCounts.GetValueOrDefault(5))
                .ThenByDescending(a => a.Count)
                .ThenBy(a => a.AlbumName)
                .ToList();

            var topN = EnvInt("TOP_N", 100);
            ranked = ranked.Take(topN).ToList();

            static string OpenTrackUrl(string uri) =>
                uri.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? "https://open.spotify.com/track/" + uri.Substring("spotify:track:".Length)
                : uri;

            string Stars(int? s)
            {
                if (s is null) return "";
                var filled = new string('★', Math.Clamp(s.Value, 0, 5));
                var hollow = new string('☆', 5 - Math.Clamp(s.Value, 0, 5));
                return filled + hollow;
            }

            foreach (var a in ranked)
            {
                var tracks = new List<AlbumTrackView>();
                await foreach (var t in SpotifyApi.GetAlbumTracksDetailedAsync(http, token, a.AlbumId))
                {
                    // Hide filler/excluded tracks in the rendered list (they're also excluded from scoring)
                    if (excludedTrackIds.Contains(t.Uri)) continue;

                    tracks.Add(new AlbumTrackView
                    {
                        Number = t.TrackNumber,
                        Name = t.Name,
                        Url = OpenTrackUrl(t.Uri),
                        Stars = ratedTrackStars.TryGetValue(t.Uri, out var s) ? s : (int?)null
                    });
                }

                // sort by track number (API is ordered but be safe)
                a.Tracks.Clear();
                a.Tracks.AddRange(tracks.OrderBy(x => x.Number));
            }

            var title = $"Matt’s Favourite Albums — Top {ranked.Count} by %";
            var html = HtmlRenderer.Render(ranked, title);
            var outPath = Path.Combine(cfg.OutputDir, "index.html");
            await File.WriteAllTextAsync(outPath, html, Encoding.UTF8);

            Console.WriteLine($"✅ Wrote {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("❌ " + ex.Message);
            return 1;
        }
    }

    // ---------- helpers ----------
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
        // default (balanced): 5:1, 4:0.8, 3:0.5, 2:0.25, 1:0.1
        var weights = new Dictionary<int, double> { { 5, 1.0 }, { 4, 0.8 }, { 3, 0.5 }, { 2, 0.25 }, { 1, 0.10 } };
        if (string.IsNullOrWhiteSpace(csv)) return weights;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (!int.TryParse(kv[0], out var stars)) continue;
            if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) continue;
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
