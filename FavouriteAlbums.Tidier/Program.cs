using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FavouriteAlbums.Core;   // SpotifyApi + SimplifiedTrack

namespace FavouriteAlbums.Tidier;

internal class Program
{
    static async Task<int> Main()
    {
        try
        {
            // ---- Config ----
            var starPlaylists = ParseStarPlaylists(Env("STAR_PLAYLISTS")); // 5:id,4:id,...,1:id
            var clientId = Env("SPOTIFY_CLIENT_ID");
            var clientSecret = Env("SPOTIFY_CLIENT_SECRET");
            var refreshToken = Env("SPOTIFY_REFRESH_TOKEN");
            var dryRun = EnvBool("DRY_RUN", true);
            var sampleCount = int.TryParse(Environment.GetEnvironmentVariable("SAMPLE_COUNT"), out var sc) ? sc : 10;

            using var http = new HttpClient();
            var accessToken = await SpotifyApi.GetAccessTokenAsync(http, clientId, clientSecret, refreshToken);

            // Resolve playlist names once (for friendlier output)
            var playlistNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, pid) in starPlaylists)
                playlistNames[pid] = await GetPlaylistNameAsync(http, accessToken, pid);

            Console.WriteLine("⭐ FavouriteAlbums.Tidier — newest rating wins");
            Console.WriteLine($"   Dry run: {dryRun}");
            Console.WriteLine($"   Playlists: {string.Join(", ", starPlaylists.OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Key}★={playlistNames[kv.Value]} ({kv.Value})"))}");
            Console.WriteLine();

            // ---- Scan playlists & collect facts ----
            // latestByTrack: trackUri -> newest (stars, addedAt, playlistId, track)
            var latestByTrack = new Dictionary<string, (int Stars, DateTime AddedAt, string PlaylistId, SimplifiedTrack Track)>(StringComparer.Ordinal);

            // counts[playlistId][trackUri] = occurrence count (to spot duplicates)
            var counts = starPlaylists.Values.ToDictionary(pid => pid, _ => new Dictionary<string, int>(StringComparer.Ordinal));

            // lastAddedAt[playlistId][trackUri] = most recent added time we saw in that playlist
            var lastAddedAt = starPlaylists.Values.ToDictionary(
                pid => pid,
                _ => new Dictionary<string, DateTime>(StringComparer.Ordinal)
            );

            foreach (var (stars, playlistId) in starPlaylists.OrderByDescending(kv => kv.Key))
            {
                var expected = await SpotifyApi.GetPlaylistTotalAsync(http, accessToken, playlistId);
                Console.WriteLine($"→ Scanning {stars}★ [{playlistNames[playlistId]}] ({playlistId}) … expected ~{expected}");

                int fetched = 0;
                await foreach (var entry in SpotifyApi.GetAllPlaylistEntriesAsync(http, accessToken, playlistId, info => Console.WriteLine(info)))
                {
                    fetched++;
                    var t = entry.Track;
                    if (t?.Uri is null || !t.Uri.StartsWith("spotify:track:", StringComparison.Ordinal)) continue;

                    // count occurrence
                    var dict = counts[playlistId];
                    dict[t.Uri] = dict.GetValueOrDefault(t.Uri) + 1;

                    var added = entry.AddedAt ?? DateTime.MinValue;

                    // track latest for this playlist/track (for audit output)
                    var la = lastAddedAt[playlistId];
                    if (!la.TryGetValue(t.Uri, out var prev) || added > prev) la[t.Uri] = added;

                    // choose newest across all star lists (tie → higher star wins)
                    if (latestByTrack.TryGetValue(t.Uri, out var cur))
                    {
                        if (added > cur.AddedAt || (added == cur.AddedAt && stars > cur.Stars))
                            latestByTrack[t.Uri] = (stars, added, playlistId, t);
                    }
                    else
                    {
                        latestByTrack[t.Uri] = (stars, added, playlistId, t);
                    }
                }
                Console.WriteLine($"   ✓ fetched {fetched}/{expected}");
            }

            // ---- Build plan: remove from losers, ensure exactly 1 in winner ----
            var removePlan = starPlaylists.Values.ToDictionary(pid => pid, _ => new HashSet<string>(StringComparer.Ordinal));
            var addPlan = starPlaylists.Values.ToDictionary(pid => pid, _ => new HashSet<string>(StringComparer.Ordinal));

            foreach (var (trackUri, win) in latestByTrack)
            {
                var winnerPid = starPlaylists[win.Stars];
                var countInWin = counts[winnerPid].GetValueOrDefault(trackUri);

                // remove from all non-winner lists
                foreach (var (s, pid) in starPlaylists)
                {
                    if (pid == winnerPid) continue;
                    if (counts[pid].ContainsKey(trackUri))
                        removePlan[pid].Add(trackUri);
                }

                // fix winner dupes / absence
                if (countInWin == 0)
                {
                    addPlan[winnerPid].Add(trackUri);
                }
                else if (countInWin > 1)
                {
                    removePlan[winnerPid].Add(trackUri);
                    addPlan[winnerPid].Add(trackUri);
                }
            }

            // ---- Summaries ----
            Console.WriteLine();
            Console.WriteLine("Plan summary:");
            foreach (var (stars, pid) in starPlaylists.OrderByDescending(kv => kv.Key))
            {
                var r = removePlan[pid].Count;
                var a = addPlan[pid].Count;
                Console.WriteLine($"  {stars}★ [{playlistNames[pid]}]: remove {r}, add {a}");
            }

            // ---- Audit examples (dry-run preview) ----
            var changes = new List<(string TrackUri,
                                    (int Stars, DateTime AddedAt, string PlaylistId, SimplifiedTrack Track) Winner,
                                    List<(int Stars, string PlaylistId, int Count, DateTime? LastAdded)> Losers,
                                    int WinnerCount)>();

            foreach (var kv in latestByTrack)
            {
                var uri = kv.Key;
                var win = kv.Value;
                var winnerPid = starPlaylists[win.Stars];
                var winnerCount = counts[winnerPid].GetValueOrDefault(uri);

                var loserList = new List<(int Stars, string PlaylistId, int Count, DateTime? LastAdded)>();
                foreach (var (s, pid) in starPlaylists)
                {
                    if (pid == winnerPid) continue;
                    if (counts[pid].TryGetValue(uri, out var c) && c > 0)
                    {
                        lastAddedAt[pid].TryGetValue(uri, out var last);
                        loserList.Add((s, pid, c, last == default ? (DateTime?)null : last));
                    }
                }

                if (loserList.Count > 0 || winnerCount > 1)
                    changes.Add((uri, win, loserList, winnerCount));
            }

            if (changes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Examples of planned changes (showing up to {sampleCount}):");

                foreach (var c in changes.OrderByDescending(x => x.Winner.AddedAt).Take(sampleCount))
                {
                    var t = c.Winner.Track;
                    var trackName = t?.Name ?? "(unknown)";
                    var artists = string.Join(", ", t?.Album?.Artists?.Select(a => a.Name) ?? Array.Empty<string>());
                    var winnerPid = c.Winner.PlaylistId;
                    var winnerName = playlistNames[winnerPid];

                    Console.WriteLine();
                    Console.WriteLine($"• \"{trackName}\" — {artists}");
                    Console.WriteLine($"   Winner: {c.Winner.Stars}★ [{winnerName}] added {FmtUtc(c.Winner.AddedAt)}");
                    if (c.WinnerCount > 1)
                        Console.WriteLine($"   Winner currently has {c.WinnerCount} copies (will keep 1).");

                    foreach (var l in c.Losers.OrderByDescending(x => x.LastAdded ?? DateTime.MinValue))
                    {
                        var lname = playlistNames[l.PlaylistId];
                        var when = l.LastAdded.HasValue ? FmtUtc(l.LastAdded.Value) : "unknown time";
                        Console.WriteLine($"   Remove from {l.Stars}★ [{lname}] ×{l.Count} (last added {when})");
                    }
                }
                Console.WriteLine();
            }

            if (dryRun)
            {
                Console.WriteLine("Dry run only. Set DRY_RUN=false to apply changes.");
                return 0;
            }

            // ---- Apply plan ----
            Console.WriteLine("Applying changes…");
            foreach (var (stars, pid) in starPlaylists.OrderByDescending(kv => kv.Key))
            {
                var toRemove = removePlan[pid].ToList();
                var toAdd = addPlan[pid].ToList();

                if (toRemove.Count > 0)
                {
                    Console.WriteLine($"  Removing {toRemove.Count} from {stars}★ [{playlistNames[pid]}] …");
                    await RemoveTracksAllAsync(http, accessToken, pid, toRemove);
                }

                if (toAdd.Count > 0)
                {
                    Console.WriteLine($"  Adding {toAdd.Count} to {stars}★ [{playlistNames[pid]}] …");
                    await AddTracksAsync(http, accessToken, pid, toAdd);
                }
            }

            Console.WriteLine("✅ Tidy complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("❌ " + ex.Message);
            return 1;
        }
    }

    // ---------- HTTP mutators ----------

    private static async Task RemoveTracksAllAsync(HttpClient http, string accessToken, string playlistId, List<string> uris)
    {
        foreach (var chunk in Chunk(uris, 90))
        {
            var payload = new { tracks = chunk.Select(u => new { uri = u }).ToArray() };
            var json = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.spotify.com/v1/playlists/{playlistId}/tracks")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(RetryAfter(res)));
                res = await http.SendAsync(req);
            }
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Remove failed ({playlistId}): {(int)res.StatusCode} {res.ReasonPhrase}\n{body}");
        }
    }

    private static async Task AddTracksAsync(HttpClient http, string accessToken, string playlistId, List<string> uris)
    {
        foreach (var chunk in Chunk(uris, 90))
        {
            var payload = new { uris = chunk };
            var json = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.spotify.com/v1/playlists/{playlistId}/tracks")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(RetryAfter(res)));
                res = await http.SendAsync(req);
            }
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Add failed ({playlistId}): {(int)res.StatusCode} {res.ReasonPhrase}\n{body}");
        }
    }

    // ---------- Helpers ----------

    private static string Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Missing environment variable: {name}");
        return v;
    }

    private static bool EnvBool(string name, bool @default)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return @default;
        return v.Equals("false", StringComparison.OrdinalIgnoreCase) ? false :
               v.Equals("0", StringComparison.OrdinalIgnoreCase) ? false :
               v.Equals("no", StringComparison.OrdinalIgnoreCase) ? false : true;
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

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
        {
            var take = Math.Min(size, src.Count - i);
            var chunk = new List<T>(take);
            for (int j = 0; j < take; j++) chunk.Add(src[i + j]);
            yield return chunk;
        }
    }

    private static int RetryAfter(HttpResponseMessage res) =>
        (res.Headers.TryGetValues("Retry-After", out var vals) && int.TryParse(vals.FirstOrDefault(), out var sec))
            ? Math.Max(sec, 1) : 2;

    private static string FmtUtc(DateTime dt) => dt == DateTime.MinValue
        ? "unknown time"
        : dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");

    // Friendly names for playlists
    private static async Task<string> GetPlaylistNameAsync(HttpClient http, string accessToken, string playlistId)
    {
        var url = $"https://api.spotify.com/v1/playlists/{playlistId}?fields=name";
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(RetryAfter(res)));
                continue;
            }

            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"GetPlaylistName failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{body}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? playlistId) : playlistId;
        }
    }
}
