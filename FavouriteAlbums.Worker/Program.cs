using System.Globalization;
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
            // --- read env vars (normalize + validate BEFORE building cfg) ---
            var spotifyClientId = Env("SPOTIFY_CLIENT_ID");
            var spotifyClientSecret = Env("SPOTIFY_CLIENT_SECRET");
            var spotifyRefreshToken = Env("SPOTIFY_REFRESH_TOKEN");
            var outputDir = Environment.GetEnvironmentVariable("OUTPUT_DIR") ?? "out";
            var topN = EnvInt("TOP_N", 100);

            // Star playlists: parse, normalize, validate
            var starPlaylistsRaw = ParseStarPlaylists(Env("STAR_PLAYLISTS"));
            var starPlaylistsNorm = starPlaylistsRaw.ToDictionary(
                kv => kv.Key,
                kv => NormalizePlaylistId(kv.Value)
            );

            // Filler / Excluded: normalize + validate
            var fillerIdRaw = Env("FILLER_PLAYLIST_ID");
            var fillerId = NormalizePlaylistId(fillerIdRaw);

            var excludedIdRaw = Environment.GetEnvironmentVariable("EXCLUDED_PLAYLIST_ID");
            string? excludedId = null;
            if (!string.IsNullOrWhiteSpace(excludedIdRaw))
                excludedId = NormalizePlaylistId(excludedIdRaw);

            // Validate IDs are base62 (22 alnum chars)
            foreach (var (stars, pid) in starPlaylistsNorm)
                RequireBase62($"{stars}★ in STAR_PLAYLISTS", pid);
            RequireBase62("FILLER_PLAYLIST_ID", fillerId);
            if (!string.IsNullOrWhiteSpace(excludedId))
                RequireBase62("EXCLUDED_PLAYLIST_ID", excludedId!);

            // Build cfg with INIT assignments only
            var cfg = new AppConfig
            {
                SpotifyClientId = spotifyClientId,
                SpotifyClientSecret = spotifyClientSecret,
                SpotifyRefreshToken = spotifyRefreshToken,
                OutputDir = outputDir,
                StarPlaylists = starPlaylistsNorm,
                FillerPlaylistId = fillerId,
                ExcludedPlaylistId = excludedId
            };

            Directory.CreateDirectory(cfg.OutputDir);


            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            var token = await SpotifyApi.GetAccessTokenAsync(http, cfg.SpotifyClientId, cfg.SpotifyClientSecret, cfg.SpotifyRefreshToken);

            // ---- Purchased albums (optional) ----
            var purchasedPlaylistIdRaw = Environment.GetEnvironmentVariable("PURCHASED_PLAYLIST_ID");
            string? purchasedPlaylistId = null;
            if (!string.IsNullOrWhiteSpace(purchasedPlaylistIdRaw))
            {
                purchasedPlaylistId = NormalizePlaylistId(purchasedPlaylistIdRaw);
                if (!IsBase62(purchasedPlaylistId))
                    throw new InvalidOperationException($"PURCHASED_PLAYLIST_ID is not a valid Spotify playlist id: '{purchasedPlaylistIdRaw}'");
            }

            var purchasedAlbumIds = new HashSet<string>(StringComparer.Ordinal);
            var purchasedKeys = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(purchasedPlaylistId))
            {
                Console.WriteLine($"→ Loading purchased playlist {purchasedPlaylistId}…");
                await foreach (var t in SpotifyApi.GetAllPlaylistTracksAsync(http, token, purchasedPlaylistId, info => Console.WriteLine(info)))
                {
                    var aid = t.Album?.Id;
                    if (!string.IsNullOrWhiteSpace(aid))
                    {
                        purchasedAlbumIds.Add(aid);
                        var artist = t.Album?.Artists?.FirstOrDefault()?.Name ?? "";
                        var album = t.Album?.Name ?? "";
                        purchasedKeys.Add(MakeAlbumKey(artist, album));
                    }
                }
                Console.WriteLine($"   ✓ Purchased: {purchasedAlbumIds.Count} album ids, {purchasedKeys.Count} keys.");
            }

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

                Console.WriteLine($"→ Checking {stars}★ playlist id {playlistId}…");
                int expected;
                try
                {
                    expected = await SpotifyApi.GetPlaylistTotalAsync(http, token, playlistId);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to read playlist {stars}★ id='{playlistId}': {ex.Message}", ex);
                }

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

            // --- build the eBay page from the same Top-100 set (safe to skip if creds not set) ---
            await GenerateEbayPageAsync(
                http,
                ranked.Where(a => !purchasedAlbumIds.Contains(a.AlbumId) && !purchasedKeys.Contains(MakeAlbumKey(a))),
                cfg.OutputDir);
                       
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

    private static string NormalizeAlbumTitle(string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return "";
        int i = album.IndexOf(" (", StringComparison.Ordinal);
        if (i > 0) album = album[..i];
        foreach (var sep in new[] { " - ", ": " })
        {
            int j = album.IndexOf(sep, StringComparison.Ordinal);
            if (j > 0) { album = album[..j]; break; }
        }
        return album.Trim();
    }

    private static string Canon(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant().Replace("’", "'").Trim();
        // keep letters/digits/space/' only
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '\'') sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string MakeAlbumKey(string artist, string album)
        => $"{Canon(artist)} | {Canon(NormalizeAlbumTitle(album))}";

    private static string MakeAlbumKey(AlbumAggregate a)
        => MakeAlbumKey(a.Artists.FirstOrDefault() ?? "", a.AlbumName ?? "");

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

    private static decimal EnvDecimal(string name, decimal @default)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : @default;
    }

    // Build more matchable eBay queries: strip edition suffixes/parentheses
    private static string MakeEbayQuery(AlbumAggregate a)
    {
        string artist = (a.Artists.FirstOrDefault() ?? "").Trim();
        string album = (a.AlbumName ?? "").Trim();

        // strip parentheticals: "Album (Deluxe Edition)" -> "Album"
        int i = album.IndexOf(" (", StringComparison.Ordinal);
        if (i > 0) album = album[..i];

        // strip after " - " or ":" which often carry edition/remaster info
        foreach (var sep in new[] { " - ", ": " })
        {
            int j = album.IndexOf(sep, StringComparison.Ordinal);
            if (j > 0) { album = album[..j]; break; }
        }

        artist = artist.Replace("’", "'"); // normalize apostrophes
        album = album.Replace("’", "'");

        return $"{artist} {album} vinyl";
    }

    // Extra heuristic to keep vinyl, ditch CDs/cassettes/DVDs
    private static bool LooksLikeVinylTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var t = title.ToLowerInvariant();

        // hard excludes
        if (t.Contains(" cd ") || t.EndsWith(" cd") || t.StartsWith("cd ") || t.Contains(" compact disc")) return false;
        if (t.Contains("cassette") || t.Contains("tape") || t.Contains("minidisc") || t.Contains(" md ")) return false;
        if (t.Contains("dvd") || t.Contains("blu-ray") || t.Contains("vhs")) return false;

        // reject obvious 7" singles
        if (t.Contains("7\"") || t.Contains("7”") || t.Contains(" 7in") || t.Contains(" 7 in") || t.Contains("7-inch") || t.Contains(" 7 inch"))
            return false;

        // inklings of vinyl
        if (t.Contains("vinyl")) return true;

        // common LP hints (avoid “help”, etc.)
        if (t.Contains(" lp ") || t.EndsWith(" lp") || t.StartsWith("lp ") || t.Contains("(lp")) return true;

        // sizes often present on listings (12/10 only)
        if (t.Contains("12\"") || t.Contains("10\"")) return true;

        return false;
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

    // ---------- eBay page generation ----------
    private static async Task GenerateEbayPageAsync(
        HttpClient http,
        IEnumerable<AlbumAggregate> topAlbums,
        string outputDir)
    {
        var clientId = (Environment.GetEnvironmentVariable("EBAY_CLIENT_ID") ?? "").Trim();
        var clientSecret = (Environment.GetEnvironmentVariable("EBAY_CLIENT_SECRET") ?? "").Trim();

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Console.WriteLine("eBay: EBAY_CLIENT_ID/EBAY_CLIENT_SECRET not set — skipping eBay page.");
            return;
        }

        var marketplace = Environment.GetEnvironmentVariable("EBAY_MARKETPLACE") ?? "EBAY_GB";
        var deliveryCc = Environment.GetEnvironmentVariable("EBAY_DELIVERY_CC") ?? "GB";
        var maxTotalGbp = EnvDecimal("EBAY_MAX_PRICE_GBP", 25m);
        var maxPages = EnvInt("EBAY_PAGES_PER_QUERY", 2);
        var limitPerPage = EnvInt("EBAY_LIMIT_PER_PAGE", 50);
        var concurrency = EnvInt("EBAY_QUERY_CONCURRENCY", 3);
        var maxResultsOut = EnvInt("EBAY_MAX_RESULTS", 400);

        Console.WriteLine($"eBay: building results (Total ≤ £{maxTotalGbp:0.##}, {marketplace}, deliver to {deliveryCc})…");

        var ebayToken = await EbayApi.GetAppAccessTokenAsync(http, clientId, clientSecret);

        var queries = topAlbums.Select(a => (Album: a, Query: MakeEbayQuery(a))).ToList();

        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var bag = new System.Collections.Concurrent.ConcurrentDictionary<string, EbayApi.EbayItem>(StringComparer.Ordinal);
        var rnd = new Random();

        async Task RunQueryAsync((AlbumAggregate Album, string Query) q)
        {
            await gate.WaitAsync();
            try
            {
                await foreach (var item in EbayApi.SearchAuctionsAsync(
                    http,
                    ebayToken,
                    marketplace,
                    deliveryCc,
                    q.Query,
                    maxPriceGbp: maxTotalGbp,
                    limitPerPage: limitPerPage,
                    maxPages: maxPages,
                    log: s => Console.WriteLine($"   [{q.Query}] {s}")
                ))
                {
                    if (!string.Equals(item.Currency, "GBP", StringComparison.OrdinalIgnoreCase)) continue;

                    bool isAuction = item.BuyingOptions.Any(x => x.Equals("AUCTION", StringComparison.OrdinalIgnoreCase));
                    bool isBuyNow = item.BuyingOptions.Any(x => x.Equals("FIXED_PRICE", StringComparison.OrdinalIgnoreCase));

                    if (item.Total > maxTotalGbp) continue;

                    if (isAuction)
                    {
                        if (!(item.EndTimeUtc is DateTime end) || end > DateTime.UtcNow.AddDays(2))
                            continue; // auction not ending within 2 days
                    }
                    else if (!isBuyNow)
                    {
                        continue; // ignore other listing types
                    }

                    if (!LooksLikeVinylTitle(item.Title)) continue;

                    bag.TryAdd(item.ItemId ?? Guid.NewGuid().ToString("N"), item);
                }

                await Task.Delay(150 + rnd.Next(75));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ eBay query failed for “{q.Query}”: {ex.GetType().Name}: {ex.Message}");
            }
            finally { gate.Release(); }
        }

        Console.WriteLine($"eBay: querying {queries.Count} album terms (concurrency {concurrency})…");
        var tasks = queries.Select(RunQueryAsync).ToArray();
        if (tasks.Length > 0) await Task.WhenAll(tasks);

        Console.WriteLine($"eBay: got {bag.Count} unique items after filtering.");

        if (bag.Count == 0)
        {
            // Write a minimal page so the pipeline succeeds and you can see it rendered
            var outDir0 = Path.Combine(outputDir, "ebay");
            Directory.CreateDirectory(outDir0);
            var html0 = EbayRenderer.Render(Array.Empty<EbayRenderer.Row>(),
                $"Vinyl deals ≤ £{maxTotalGbp:0} (eBay)",
                navHtml: null);
            var outPath0 = Path.Combine(outDir0, "index.html");
            await File.WriteAllTextAsync(outPath0, html0, Encoding.UTF8);
            Console.WriteLine("eBay: no matches found — wrote an empty results page.");
            return;
        }

        // Auctions first (cheapest), then Buy-It-Now (cheapest)
        var auctionItems = bag.Values
            .Where(i => i.BuyingOptions.Any(x => x.Equals("AUCTION", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Total)                              // price first
            .ThenBy(i => i.EndTimeUtc ?? DateTime.MaxValue);    // tie-breaker: ends sooner

        var buyNowItems = bag.Values
            .Where(i => i.BuyingOptions.Any(x => x.Equals("FIXED_PRICE", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Total);

        var ordered = auctionItems.Concat(buyNowItems).Take(maxResultsOut).ToList();

        var rows = ordered.Select(i => new EbayRenderer.Row(
            Title: i.Title,
            Url: i.WebUrl,
            ImageUrl: i.ImageUrl,
            Currency: i.Currency,
            Total: i.Total,
            IsAuction: i.BuyingOptions.Any(x => string.Equals(x, "AUCTION", StringComparison.OrdinalIgnoreCase)),
            EndUtc: i.EndTimeUtc
        )).ToList();

        var outDir = Path.Combine(outputDir, "ebay");
        Directory.CreateDirectory(outDir);

        // No back-link from main page; you'll link to /ebay/ elsewhere on the site.
        var htmlTitle = $"Vinyl deals ≤ £{maxTotalGbp:0} (eBay)";
        var html = EbayRenderer.Render(rows, htmlTitle, navHtml: null);
        var outPath = Path.Combine(outDir, "index.html");
        await File.WriteAllTextAsync(outPath, html, Encoding.UTF8);

        Console.WriteLine($"eBay: wrote {outPath} ({rows.Count} rows).");
    }

    // ---------- ID normalization/validation ----------
    private static string NormalizePlaylistId(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        s = s.Trim().Trim('"', '\'');
        const string sp = "spotify:playlist:";
        if (s.StartsWith(sp, StringComparison.OrdinalIgnoreCase)) return s[sp.Length..];

        var idx = s.IndexOf("/playlist/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = s[(idx + "/playlist/".Length)..];
            var id = rest.Split(new[] { '?', '/', '"', '\'', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        return s;
    }

    private static bool IsBase62(string id) => id.Length == 22 && id.All(char.IsLetterOrDigit);

    private static void RequireBase62(string label, string id)
    {
        if (!IsBase62(id))
            throw new InvalidOperationException($"{label} is not a valid Spotify playlist id (expected 22 alphanumeric chars). Got: '{id}'. If you pasted a URL, use just the ID.");
    }
}
