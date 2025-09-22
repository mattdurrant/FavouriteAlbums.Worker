using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FavouriteAlbums.Core; // for SimplifiedTrack & friends in Models.cs

namespace FavouriteAlbums.Core;

public static class SpotifyApi
{
    // Json options (case-insensitive names)
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Exchange a refresh token for a short-lived access token.
    /// </summary>
    public static async Task<string> GetAccessTokenAsync(
        HttpClient http, string clientId, string clientSecret, string refreshToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");

        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type", "refresh_token"),
            new KeyValuePair<string,string>("refresh_token", refreshToken),
        });

        // Basic auth header with clientId:clientSecret
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Spotify token request failed: {(int)res.StatusCode} {res.ReasonPhrase}\nBody: {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var atEl))
            throw new InvalidOperationException($"Spotify token response missing access_token.\nBody: {body}");

        var accessToken = atEl.GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException($"Spotify token response missing access_token value.\nBody: {body}");

        return accessToken!;
    }

    /// <summary>
    /// Stream all tracks in a playlist (paginated). Includes album fields we need:
    /// album_type, total_tracks, release_date(+precision).
    /// Politely backs off on 429 using Retry-After.
    /// </summary>
    public static async IAsyncEnumerable<SimplifiedTrack> GetAllPlaylistTracksAsync(
        HttpClient http,
        string accessToken,
        string playlistId,
        Action<string>? onInfo = null)
    {
        string? next =
            $"https://api.spotify.com/v1/playlists/{playlistId}/tracks" +
            $"?limit=100&fields=items(track(" +
              "album(id,name,images,artists(name),uri,album_type,total_tracks,release_date,release_date_precision)," +
              "name,uri" +
            ")),next";

        while (next is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                var retryAfter = GetRetryAfter(res);
                onInfo?.Invoke($"⚠️ 429 from Spotify. Waiting {retryAfter}s then retrying…");
                await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                continue; // retry same URL
            }

            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync();
            var page = await JsonSerializer.DeserializeAsync<PlaylistTracksPage>(stream, Json);

            if (page?.Items is not null)
            {
                foreach (var it in page.Items)
                {
                    if (it.Track is not null)
                        yield return it.Track;
                }
            }

            next = page?.Next;
        }
    }

    /// <summary>
    /// NEW: Stream playlist entries with added_at so we can pick the most-recent rating.
    /// </summary>
    public static async IAsyncEnumerable<PlaylistTrackEntry> GetAllPlaylistEntriesAsync(
        HttpClient http,
        string accessToken,
        string playlistId,
        Action<string>? onInfo = null)
    {
        string? next =
            $"https://api.spotify.com/v1/playlists/{playlistId}/tracks" +
            $"?limit=100&fields=items(added_at,track(" +
              "album(id,name,images,artists(name),uri,album_type,total_tracks,release_date,release_date_precision)," +
              "name,uri" +
            ")),next";

        while (next is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                var retryAfter = GetRetryAfter(res);
                onInfo?.Invoke($"⚠️ 429 from Spotify. Waiting {retryAfter}s then retrying…");
                await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                continue;
            }

            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync();
            var page = await JsonSerializer.DeserializeAsync<PlaylistTracksPage>(stream, Json);

            if (page?.Items is not null)
            {
                foreach (var it in page.Items)
                {
                    if (it.Track is not null)
                        yield return new PlaylistTrackEntry { Track = it.Track, AddedAt = it.AddedAt };
                }
            }

            next = page?.Next;
        }
    }

    /// <summary>
    /// Get the playlist's expected 'tracks.total' count (for diagnostics).
    /// Handles 429 with backoff.
    /// </summary>
    public static async Task<int> GetPlaylistTotalAsync(HttpClient http, string accessToken, string playlistId)
    {
        var url = $"https://api.spotify.com/v1/playlists/{playlistId}?fields=tracks(total)";

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                var retryAfter = GetRetryAfter(res);
                await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                continue; // try again
            }

            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Spotify playlist total failed: {(int)res.StatusCode} {res.ReasonPhrase}\nBody: {body}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("total", out var totalEl))
            {
                return totalEl.GetInt32();
            }
            return 0;
        }
    }

    /// <summary>
    /// Stream detailed album tracks (track_number, name, uri) with 429 backoff.
    /// Used only for Top-N albums to render per-track links.
    /// </summary>
    public static async IAsyncEnumerable<AlbumTrackItem> GetAlbumTracksDetailedAsync(
        HttpClient http, string accessToken, string albumId)
    {
        string? next =
            $"https://api.spotify.com/v1/albums/{albumId}/tracks?limit=50&fields=items(track_number,name,uri),next";

        while (next is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                var retryAfter = GetRetryAfter(res);
                await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                continue;
            }

            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    yield return new AlbumTrackItem
                    {
                        TrackNumber = it.TryGetProperty("track_number", out var n) ? n.GetInt32() : 0,
                        Name = it.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        Uri = it.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : ""
                    };
                }
            }

            next = root.TryGetProperty("next", out var nextEl) ? nextEl.GetString() : null;
        }
    }

    // ====== Types ======

    public sealed class AlbumTrackItem
    {
        public int TrackNumber { get; set; }
        public string Name { get; set; } = "";
        public string Uri { get; set; } = "";
    }

    public sealed class PlaylistTrackEntry
    {
        public SimplifiedTrack? Track { get; set; }

        [JsonPropertyName("added_at")]
        public DateTime? AddedAt { get; set; }
    }

    private sealed class PlaylistTracksPage
    {
        public PlaylistItem[]? Items { get; set; }
        public string? Next { get; set; }
    }

    private sealed class PlaylistItem
    {
        public SimplifiedTrack? Track { get; set; }

        [JsonPropertyName("added_at")]
        public DateTime? AddedAt { get; set; }
    }

    private static int GetRetryAfter(HttpResponseMessage res) =>
        (res.Headers.TryGetValues("Retry-After", out var vals) && int.TryParse(vals.FirstOrDefault(), out var sec))
            ? Math.Max(sec, 1) : 2;
}
