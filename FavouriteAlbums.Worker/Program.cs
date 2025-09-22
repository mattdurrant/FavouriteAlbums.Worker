using System.Text;
using System.Text.Json;
using FavouriteAlbums.Core;

namespace FavouriteAlbums.Worker;

internal class Program
{
    // ----- Hard-coded boosted weights: index by star value [0..5] -----
    // 5★=1.20, 4★=1.00, 3★=0.60, 2★=0.30, 1★=0.10
    private static readonly double[] StarWeights = { 0.0, 0.10, 0.30, 0.60, 1.00, 1.20 };

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
            var topN = EnvInt("TOP_N", 100);
            Directory.CreateDirectory(cfg.OutputDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            var token = await SpotifyApi.GetAccessTokenAsync(http, cfg.SpotifyClientId, cfg.SpotifyClientSecret, cfg.SpotifyRefreshToken);

            // --- exclusion sets: tracks in Filler/Excluded playlists (URIs) + per-album excluded counts ---
            var excludedTrackIds = new HashSet<string>(StringComparer.Ordinal);
            var excludedCountPerAlbumId = new Dictionary<string, int>(StringComparer.Ordinal);

            async Task AddPlaylistTracksTo(string playlistId)
            {
                await foreach (var t in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId, info => Console.WriteLine(info)))
                {
                    if (string.IsNullOrWhiteSpace(t?.Uri) || !t.Uri.StartsWith("spotify:track:")) continue;
                    excludedTrackIds.Add(t.Uri);
                    var aid = t.Album?.Id;
                    if (!string.IsNullOrWhiteSpace(aid))
                        excludedCountPerAlbumId[aid!] = excludedCountPerAlbumId.GetValueOrDefault(aid!, 0) + 1;
                }
            }

            await AddPlaylistTracksTo(cfg.FillerPlaylistId);
            if (!string.IsNullOrWhiteSpace(cfg.ExcludedPlaylistId))
                await AddPlaylistTracksTo(cfg.ExcludedPlaylistId!);

            // --- aggregate albums from star playlists; skip singles/compilations; global de-dup ---
            var albums = new Dictionary<string, AlbumAggregate>(StringComparer.Ordinal);
            var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);
            var ratedTrackStars = new Dictionary<string, int>(StringComparer.Ordinal); // for ★ glyphs later

            foreach (var (stars, playlistId) in cfg.StarPlaylists.OrderByDescending(kv => kv.Key))
            {
                var weight = StarWeights[Math.Clamp(stars, 1, 5)];
                var expected = await SpotifyApi.GetPlaylistTotalAsync(http, token, playlistId);
                int fetched = 0, included = 0, skipExcluded = 0, skipDup = 0, skipNonAlbum = 0;

                Console.WriteLine($"→ Reading {stars}★ playlist {playlistId} (weight {weight}, expected ~{expected} items)…");

                await foreach (var track in SpotifyApi.GetAllPlaylistTracksAsync(http, token, playlistId, info => Console.WriteLine(info)))
                {
                    fetched++;

                    if (track?.Album?.Id is null || string.IsNullOrWhiteSpace(track.Uri)) continue;
                    if (!track.Uri.StartsWith("spotify:track:")) continue;

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
                        if (track.Album.ReleaseDate is { } rd && rd.Length >= 4 && int.TryParse(rd[..4], out var year))
                            agg.ReleaseYear = year;
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

                    // record the star for glyphs; since we iterate 5→1, highest wins if duplicates slipped through
                    if (!ratedTrackStars.TryGetValue(track.Uri, out var prev) || stars > prev)
                        ratedTrackStars[track.Uri] = stars;

                    included++;
                }

                Console.WriteLine($"   ✓ {stars}★: fetched {fetched}/{expected}, included {included}, skipped excluded {skipExcluded}, dup {skipDup}, non-album {skipNonAlbum}");
            }

            // --- compute denominators: album.total_tracks - excludedOnAlbum ---
            foreach (var agg in albums.Values)
            {
                var excludedOnAlbum = excludedCountPerAlbumId.GetValueOrDefault(agg.AlbumId);
                var total = agg.TotalTracks > 0 ? agg.TotalTracks : agg.Count;
                agg.Denominator = Math.Max(0, total - excludedOnAlbum);
            }

            // --- rank all eligible by RawPercent (unclipped), then tiebreakers ---
            var allEligible = albums.Values.Where(a => a.Denominator > 0).ToList();
            var ranked = RankOrder(allEligible).ToList();
            var totalEligible = ranked.Count;
            ranked = ranked.Take(topN).ToList();

            // --- build Top 10 per year (from all eligible, not just Top N) ---
            var byYear = allEligible
                .Where(a => a.ReleaseYear.HasValue)
                .GroupBy(a => a.ReleaseYear!.Value)
                .ToDictionary(g => g.Key, g => RankOrder(g).Take(10).ToList());

            // --- figure out which albums need detailed tracklists (union Top N + all year lists) ---
            var detailAlbumIds = new HashSet<string>(ranked.Select(a => a.AlbumId), StringComparer.Ordinal);
            foreach (var list in byYear.Values)
                foreach (var a in list)
                    detailAlbumIds.Add(a.AlbumId);

            // --- cache-aware per-album tracklists WITH throttling ---
            var cacheUrl = Environment.GetEnvironmentVariable("CACHE_URL")
                           ?? "https://albums.mattdurrant.com/cache/albums.json";
            var cacheTtlDays = int.TryParse(Environment.GetEnvironmentVariable("CACHE_TTL_DAYS"), out var dDays) ? dDays : 30;
            var albumCache = await LoadAlbumCacheAsync(http, cacheUrl);

            var now = DateTime.UtcNow;
            var needsFetch = new List<string>();
            var cachedCount = 0;

            foreach (var id in detailAlbumIds)
            {
                var a = albums[id];
                if (albumCache.TryGetValue(id, out var entry) &&
                    (now - entry.FetchedUtc).TotalDays <= cacheTtlDays &&
                    entry.Tracks.Count > 0)
                {
                    a.Tracks.Clear();
                    a.Tracks.AddRange(entry.Tracks.Select(t => new AlbumTrackView
                    {
                        Number = t.Number,
                        Name = t.Name,
                        Url = t.Url
                    }));
                    cachedCount++;
                }
                else
                {
                    needsFetch.Add(id);
                }
            }

            Console.WriteLine($"Preparing album tracklists for {detailAlbumIds.Count} albums (cached {cachedCount}, fetch {needsFetch.Count})…");

            int maxConcurrency = EnvInt("DETAIL_FETCH_CONCURRENCY", 4);
            using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            var rnd = new Random();
            int done = 0;

            async Task FetchOneAsync(string albumId)
            {
                await gate.WaitAsync();
                try
                {
                    var a = albums[albumId];
                    var list = new List<AlbumTrackView>();
                    await foreach (var t in SpotifyApi.GetAlbumTracksDetailedAsync(http, token, albumId))
                    {
                        if (excludedTrackIds.Contains(t.Uri)) continue; // hide filler/excluded in display
                        list.Add(new AlbumTrackView
                        {
                            Number = t.TrackNumber,
                            Name = t.Name,
                            Url = OpenTrackUrl(t.Uri)
                        });
                    }
                    list.Sort((x, y) => x.Number.CompareTo(y.Number));
                    a.Tracks.Clear();
                    a.Tracks.AddRange(list);

                    albumCache[albumId] = new CacheEntry
                    {
                        FetchedUtc = DateTime.UtcNow,
                        Tracks = list
                    };

                    var n = Interlocked.Increment(ref done);
                    if (n % 10 == 0 || n == needsFetch.Count)
                        Console.WriteLine($"   fetched {n}/{needsFetch.Count} albums…");

                    await Task.Delay(100 + rnd.Next(50)); // tiny jitter, be polite
                }
                finally
                {
                    gate.Release();
                }
            }

            var tasks = needsFetch.Select(FetchOneAsync).ToArray();
            if (tasks.Length > 0) await Task.WhenAll(tasks);
            Console.WriteLine("   done fetching album tracklists.");

            // --- apply latest star values to displayed tracks (for ★ glyphs) ---
            foreach (var id in detailAlbumIds)
            {
                var a = albums[id];
                foreach (var t in a.Tracks)
                {
                    var key = ToSpotifyTrackUriKey(t.Url);
                    if (key is not null && ratedTrackStars.TryGetValue(key, out var s)) t.Stars = s;
                }
            }

            // --- render & write MAIN page ---
            var title = $"Favourite {ranked.Count} albums";
            var mainNav = BuildMainBlurbWithSource() + BuildYearLinksHtml(isMainPage: true);
            var html = HtmlRenderer.Render(ranked, title, mainNav);
            var outPath = Path.Combine(cfg.OutputDir, "index.html");
            await File.WriteAllTextAsync(outPath, html, Encoding.UTF8);

            // --- render & write YEAR pages (ensure 2000..current exist, even if empty) ---
            var yearsDir = Path.Combine(cfg.OutputDir, "years");
            Directory.CreateDirectory(yearsDir);

            for (int y = DateTime.UtcNow.Year; y >= 2000; y--)
            {
                if (!byYear.ContainsKey(y)) byYear[y] = new List<AlbumAggregate>();
                var list = byYear[y];

                var yTitle = $"Favourite {list.Count} albums of {y}";
                var yNav = BuildYearLinksHtml(isMainPage: false);
                var yHtml = HtmlRenderer.Render(list, yTitle, yNav);
                var yPath = Path.Combine(yearsDir, $"{y}.html");
                await File.WriteAllTextAsync(yPath, yHtml, Encoding.UTF8);
            }

            // --- persist cache for next run ---
            await SaveAlbumCacheAsync(albumCache, cfg.OutputDir);

            Console.WriteLine($"✅ Wrote {outPath} and {byYear.Count} year pages to {yearsDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("❌ " + ex.Message);
            return 1;
        }
    }

    // ---------- ranking helper ----------
    private static IEnumerable<AlbumAggregate> RankOrder(IEnumerable<AlbumAggregate> src) =>
        src.OrderByDescending(a => a.RawPercent) // sort by UNCLIPPED value
           .ThenByDescending(a => a.StarCounts.GetValueOrDefault(5))
           .ThenByDescending(a => a.Count)
           .ThenBy(a => a.AlbumName);

    // ---------- HTML nav helpers ----------
    private static string BuildYearLinksHtml(bool isMainPage)
    {
        int start = 2000;
        int end = DateTime.UtcNow.Year;

        var prefix = isMainPage ? "./years/" : "./";
        var allTimeHref = isMainPage ? "./" : "../";

        var sb = new StringBuilder();
        sb.Append($@"<a href=""{allTimeHref}"">All Time</a>");
        for (int y = end; y >= start; y--)
        {
            sb.Append(" || ");
            sb.Append($@"<a href=""{prefix}{y}.html"">{y}</a>");
        }
        return $@"<div class=""year-links"">{sb}</div>";
    }

    private static string BuildMainBlurbWithSource() =>
        @"<div class=""blurb"">My favourite albums as determined by my Spotify account (<a href=""https://github.com/mattdurrant/FavouriteAlbums.Worker"">source code</a>).</div>";

    // ---------- misc helpers ----------
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

    private static int EnvInt(string name, int @default)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) && n > 0 ? n : @default;
    }

    private static string OpenTrackUrl(string uri)
    {
        const string prefix = "spotify:track:";
        if (!string.IsNullOrWhiteSpace(uri) && uri.StartsWith(prefix, StringComparison.Ordinal))
            return "https://open.spotify.com/track/" + uri[prefix.Length..];
        return uri;
    }

    private static string? ToSpotifyTrackUriKey(string url)
    {
        const string prefix = "https://open.spotify.com/track/";
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var idPart = url[prefix.Length..];
            var q = idPart.IndexOf('?', StringComparison.Ordinal);
            if (q >= 0) idPart = idPart[..q];
            return "spotify:track:" + idPart;
        }
        return null;
    }

    // ---- very-lightweight album track cache persisted to out/cache/albums.json ----
    private sealed class CacheEntry
    {
        public DateTime FetchedUtc { get; set; }
        public List<AlbumTrackView> Tracks { get; set; } = new();
    }

    private static async Task<Dictionary<string, CacheEntry>> LoadAlbumCacheAsync(HttpClient http, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return new();
        try
        {
            var json = await http.GetStringAsync(url);
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json) ?? new();
        }
        catch
        {
            return new(); // 404/timeout -> empty cache
        }
    }

    private static async Task SaveAlbumCacheAsync(Dictionary<string, CacheEntry> cache, string outputDir)
    {
        var dir = Path.Combine(outputDir, "cache");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "albums.json");
        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
    }
}
