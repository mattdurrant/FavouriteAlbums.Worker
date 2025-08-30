using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

string clientId = Env("SPOTIFY_CLIENT_ID");
string clientSecret = Env("SPOTIFY_CLIENT_SECRET");

string redirectUri = "http://localhost:5055/callback";
string scope = "playlist-read-private playlist-read-collaborative";
string? code = null;

// 1) Build auth URL
var state = Guid.NewGuid().ToString("N");

var authUrl =
  $"https://accounts.spotify.com/authorize" +
  $"?response_type=code" +
  $"&client_id={Uri.EscapeDataString(clientId)}" +
  $"&scope={Uri.EscapeDataString(scope)}" +
  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
  $"&state={state}" +
  $"&show_dialog=true";

Console.WriteLine("🔓 A browser window should open. If it doesn't, copy this URL and open it manually:\n");
Console.WriteLine(authUrl + "\n");

// Try to open browser automatically
try { Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true }); } catch { /* ignore */ }

// 2) Try to receive the callback (with timeout). If it fails, fallback to manual paste.
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    using var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:5055/callback/");
    listener.Start();

    Console.WriteLine("⏳ Waiting for Spotify callback (up to 3 minutes)...");
    var ctxTask = listener.GetContextAsync();
    await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, cts.Token));

    if (ctxTask.IsCompletedSuccessfully)
    {
        var ctx = ctxTask.Result;
        var rState = ctx.Request.QueryString["state"];
        code = ctx.Request.QueryString["code"];

        var resp = ctx.Response;
        var body = "<html><body><h3>✅ Auth complete. You can close this window.</h3></body></html>";
        var buf = Encoding.UTF8.GetBytes(body);
        resp.ContentLength64 = buf.Length;
        await resp.OutputStream.WriteAsync(buf);
        resp.Close();

        if (rState != state) throw new Exception("State mismatch.");
        Console.WriteLine("✅ Callback received.");
    }
    else
    {
        Console.WriteLine("⌛ Callback not received in time.");
    }

    listener.Stop();
}
catch (HttpListenerException)
{
    Console.WriteLine("⚠️ Could not start local listener (permissions or port busy).");
}

if (string.IsNullOrEmpty(code))
{
    // 3) Manual fallback: user pastes the full redirected URL
    Console.WriteLine("\n📝 Fallback: after approving in the browser, you'll be redirected to:");
    Console.WriteLine("   http://localhost:5055/callback?code=...&state=...");
    Console.Write("Paste that FULL URL here and press Enter: ");
    var pasted = Console.ReadLine() ?? "";
    try
    {
        var uri = new Uri(pasted);
        var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var rState = qs["state"];
        code = qs["code"];
        if (rState != state) throw new Exception("State mismatch.");
    }
    catch
    {
        throw new Exception("Could not parse pasted URL. Make sure you pasted the full address bar URL.");
    }
}

if (string.IsNullOrEmpty(code))
    throw new Exception("No authorization code obtained.");

// 4) Exchange code for tokens
using var http = new HttpClient();
var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "authorization_code",
    ["code"] = code!,
    ["redirect_uri"] = redirectUri
});
var res = await http.SendAsync(req);
var content = await res.Content.ReadAsStringAsync();
res.EnsureSuccessStatusCode();

var token = JsonSerializer.Deserialize<Token>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
if (token?.RefreshToken is null) throw new Exception("No refresh token in response.");

Console.WriteLine("\n🎉 Your Spotify REFRESH TOKEN is:\n");
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine(token.RefreshToken);
Console.ResetColor();
Console.WriteLine("\nCopy this into your FavouriteAlbums.Worker environment variables as SPOTIFY_REFRESH_TOKEN.");

static string Env(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException($"Missing {name}");
    return v;
}

public sealed class Token
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
}
