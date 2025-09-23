using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FavouriteAlbums.Core;

public static class EbayApi
{
    // Result model
    public sealed record EbayItem(
        string ItemId,
        string Title,
        string WebUrl,
        string? ImageUrl,
        string Currency,
        decimal Price,           // current bid (if auction) else listing price
        decimal Shipping,        // 0 if unknown/not returned
        decimal Total,           // Price + Shipping (same currency)
        DateTime? EndTimeUtc,    // auction end time if available
        string? SellerUsername,
        string[] BuyingOptions    // e.g., ["AUCTION"] or ["FIXED_PRICE"]
    );

    // ---- OAuth: client-credentials ----
    public static async Task<string> GetAppAccessTokenAsync(HttpClient http, string clientId, string clientSecret)
    {
        var tokenUrl = "https://api.ebay.com/identity/v1/oauth2/token";
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://api.ebay.com/oauth/api_scope"
        });

        var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"eBay OAuth failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var at))
            throw new InvalidOperationException("eBay OAuth response missing access_token.");
        return at.GetString()!;
    }

    // ---- Search AUCTION or FIXED_PRICE; we post-filter by Total ≤ maxPriceGbp ----
    public static async IAsyncEnumerable<EbayItem> SearchAuctionsAsync(
        HttpClient http,
        string accessToken,
        string marketplaceId,         // e.g., "EBAY_GB"
        string deliveryCountry,       // e.g., "GB"
        string query,                 // e.g., "<artist> <album> vinyl"
        decimal maxPriceGbp = 25m,
        int limitPerPage = 50,
        int maxPages = 3,
        Action<string>? log = null)
    {
        // Keep server-side filter broad; rely on client-side Total filter
        var filter = $"buyingOptions:{{AUCTION|FIXED_PRICE}},deliveryCountry:{deliveryCountry}";
        var baseUrl = "https://api.ebay.com/buy/browse/v1/item_summary/search";

        int offset = 0;
        int pages = 0;

        while (pages < maxPages)
        {
            // Limit to Vinyl/Records category (176985) + your query
            const string vinylCategory = "176985";

            // Keep only 12" (and 10") records via aspect_filter (Record Size)
            var allowedSizes = new[] { "12\"", "10\"" }; // change if you ever want to include 7"
            var sizesJoined = string.Join('|', allowedSizes.Select(s => s.Replace("\"", "%22"))); // 12%22, 10%22
            var aspect = $"categoryId:{vinylCategory},Record Size:{{{sizesJoined}}}";

            var url = $"{baseUrl}"
                    + $"?q={Uri.EscapeDataString(query)}"
                    + $"&category_ids={vinylCategory}"
                    + $"&aspect_filter={aspect}" // eBay Browse supports aspect_filter with categoryId + aspect. 
                    + $"&limit={limitPerPage}"
                    + $"&offset={offset}"
                    + $"&filter={Uri.EscapeDataString(filter)}";


            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("X-EBAY-C-MARKETPLACE-ID", marketplaceId);            // EBAY_GB
            req.Headers.Add("X-EBAY-C-ENDUSERCTX", $"contextualLocation=country={deliveryCountry}");

            var res = await http.SendAsync(req);
            if ((int)res.StatusCode == 429)
            {
                await Task.Delay(TimeSpan.FromSeconds(RetryAfter(res)));
                res = await http.SendAsync(req);
            }

            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"eBay search failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int returnedCount = 0;

            if (root.TryGetProperty("itemSummaries", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                returnedCount = items.GetArrayLength();

                foreach (var it in items.EnumerateArray())
                {
                    var itemId = GetString(it, "itemId") ?? "";
                    var title = GetString(it, "title") ?? "";
                    var urlWeb = GetString(it, "itemWebUrl") ?? "";

                    string? imageUrl = null;
                    if (it.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                        imageUrl = GetString(img, "imageUrl");

                    // Prefer currentBidPrice for auctions; fallback to price
                    var priceAmt = GetAmountFromChild(it, "currentBidPrice")
                                   ?? GetAmountFromChild(it, "price")
                                   ?? new Amount("GBP", 0m);

                    var shipAmt = GetShippingAmount(it) ?? new Amount(priceAmt.Currency, 0m);
                    decimal totalCost = (priceAmt.Currency == shipAmt.Currency) ? priceAmt.Value + shipAmt.Value : priceAmt.Value;

                    DateTime? endUtc = GetDateTime(it, "itemEndDate");

                    string? seller = null;
                    if (it.TryGetProperty("seller", out var sellerObj) && sellerObj.ValueKind == JsonValueKind.Object)
                        seller = GetString(sellerObj, "username");

                    // buyingOptions is an array of strings
                    var buying = Array.Empty<string>();
                    if (it.TryGetProperty("buyingOptions", out var bo) && bo.ValueKind == JsonValueKind.Array)
                        buying = bo.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();

                    yield return new EbayItem(
                        ItemId: itemId,
                        Title: title,
                        WebUrl: urlWeb,
                        ImageUrl: imageUrl,
                        Currency: priceAmt.Currency,
                        Price: priceAmt.Value,
                        Shipping: shipAmt.Value,
                        Total: totalCost,
                        EndTimeUtc: endUtc,
                        SellerUsername: seller,
                        BuyingOptions: buying
                    );
                }
            }

            // pagination
            int totalCount = GetInt(root, "total") ?? 0;
            offset += limitPerPage;
            pages++;

            log?.Invoke($"   eBay page {pages}: got {returnedCount} / total ≈ {totalCount}");

            if (offset >= totalCount) break;
        }
    }

    // ----- helpers -----
    private static int RetryAfter(HttpResponseMessage res) =>
        (res.Headers.TryGetValues("Retry-After", out var vals) && int.TryParse(vals.FirstOrDefault(), out var sec))
        ? Math.Max(sec, 1) : 2;

    private sealed record Amount(string Currency, decimal Value);

    private static Amount? GetAmount(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var currency = GetString(el, "currency") ?? GetString(el, "convertedFromCurrency") ?? "GBP";
        var valueStr = GetString(el, "value") ?? GetString(el, "convertedFromValue");
        if (decimal.TryParse(valueStr, out var v)) return new Amount(currency, v);
        return null;
    }

    private static Amount? GetAmountFromChild(JsonElement parent, string childName)
    {
        if (parent.TryGetProperty(childName, out var child))
            return GetAmount(child);
        return null;
    }

    private static Amount? GetShippingAmount(JsonElement it)
    {
        if (it.TryGetProperty("shippingOptions", out var opts) && opts.ValueKind == JsonValueKind.Array && opts.GetArrayLength() > 0)
        {
            var first = opts[0];
            if (first.TryGetProperty("shippingCost", out var cost))
                return GetAmount(cost);
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.String) return p.GetString();
            if (p.ValueKind == JsonValueKind.Number) return p.ToString();
        }
        return null;
    }

    private static DateTime? GetDateTime(JsonElement obj, string name)
    {
        var s = GetString(obj, name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, out var dt)) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return null;
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number)
            return p.TryGetInt32(out var n) ? n : (int?)null;
        return null;
    }
}
