using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FavouriteAlbums.Core;

public static class SpotifyApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string> GetAccessTokenAsync(HttpClient http, string clientId, string clientSecret, string refreshToken)
    {
        // Be defensive about stray quotes/whitespace from copy-paste
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        refreshToken = refreshToken.Trim();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Spotify token request failed: {(int)res.StatusCode} {res.ReasonPhrase}\nBody: {body}");
        }

        // 👇 Robust parse (handles snake_case without DTOs)
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var atEl))
            throw new InvalidOperationException($"Spotify token response missing access_token.\nBody: {body}");

        var accessToken = atEl.GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException($"Spotify token response missing access_token value.\nBody: {body}");

        return accessToken!;
    }

    public static async IAsyncEnumerable<SimplifiedTrack> GetAllPlaylistTracksAsync(HttpClient http, string accessToken, string playlistId)
    {
        string? next = $"https://api.spotify.com/v1/playlists/{playlistId}/tracks" +
               $"?limit=100&fields=items(track(album(id,name,images,artists(name),uri,album_type),name,uri)),next";

        while (next is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await http.SendAsync(req);
            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync();
            var page = await JsonSerializer.DeserializeAsync<PlaylistTracksPage>(stream, Json);

            if (page?.Items is not null)
            {
                foreach (var it in page.Items)
                    if (it.Track is not null)
                        yield return it.Track;
            }

            next = page?.Next;
        }
    }

    public static async Task<HashSet<string>> GetAllAlbumTrackUrisAsync(HttpClient http, string accessToken, string albumId)
    {
        // Return URIs like "spotify:track:xyz" for all tracks on the album
        var result = new HashSet<string>(StringComparer.Ordinal);
        string? next = $"https://api.spotify.com/v1/albums/{albumId}/tracks?limit=50&fields=items(id,uri),next";

        while (next is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await http.SendAsync(req);
            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("uri", out var uriEl))
                    {
                        var uri = uriEl.GetString();
                        if (!string.IsNullOrWhiteSpace(uri)) result.Add(uri!);
                    }
                    else if (item.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) result.Add($"spotify:track:{id}");
                    }
                }
            }

            next = root.TryGetProperty("next", out var nextEl) ? nextEl.GetString() : null;
        }

        return result;
    }

    public sealed class TokenResponse
    {
        public string? AccessToken { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresIn { get; set; }
        public string? Scope { get; set; }
    }
}
