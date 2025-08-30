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

            // Aggregate albums
            var albums = new Dictionary<string, AlbumAggregate>(StringComparer.Ordinal);
            var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (stars, playlistId) in cfg.StarPlaylists.OrderByDescending(kv => kv.Key))
            {
                Console.WriteLine($"Reading {stars}★ playlist {playlistId}...");
                await foreach (var track in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId))
                {
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
                            StarCounts = new() { { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 } }
                        };
                        albums[id] = agg;
                    }

                    agg.Count += 1;
                    agg.Score += stars;
                    agg.StarCounts[stars] = agg.StarCounts.GetValueOrDefault(stars) + 1;
                }
            }

            var ranked = albums.Values
                .OrderByDescending(a => a.Score)
                .ThenByDescending(a => a.StarCounts.GetValueOrDefault(5))
                .ThenByDescending(a => a.Count)
                .ThenBy(a => a.AlbumName)
                .ToList();

            var title = "Matt’s Favourite Albums (Weighted by ★)";
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
}
