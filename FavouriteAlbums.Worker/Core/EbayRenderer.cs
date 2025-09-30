using System.Text;

namespace FavouriteAlbums.Core;

public static class EbayRenderer
{
    public sealed record Row(
        string Title,
        string Url,
        string? ImageUrl,
        string Currency,
        decimal Total,
        bool IsAuction,
        DateTime? EndUtc
    );

    public static string Render(IEnumerable<Row> items, string title, string? navHtml = null)
    {
        var rows = items.ToList();

        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>").Append(Html(title)).Append(@"</title>
<link rel=""stylesheet"" href=""https://www.mattdurrant.com/styles.css"">
<link rel=""stylesheet"" type=""text/css"" href=""https://www.mattdurrant.com/albums.css"">
<style>
.ebitem { display:flex; align-items:flex-start; gap:12px; }
.ebleft { flex: 1 1 auto; }
.ebright img { max-width: 120px; height:auto; border-radius:8px; }
.badge-green { color:#0a0; font-weight:600; }
.meta { color:#666; }
.ebrow { padding:10px 0; border-bottom:1px solid #ddd; }
</style>
</head><body class=""albums-page"">");

        sb.Append(@"<header>
          <div class=""site-nav""><a href=""https://www.mattdurrant.com/"">← Home</a></div>
          <h1>").Append(Html(title)).Append("</h1>");

        if (!string.IsNullOrWhiteSpace(navHtml))
            sb.Append(@"<nav class=""subnav"">").Append(navHtml).Append("</nav>");
        sb.Append("</header><main>");

        foreach (var r in rows)
        {
            sb.Append(@"<div class=""ebrow""><div class=""ebitem"">");

            // left text block
            sb.Append(@"<div class=""ebleft"">");
            sb.Append(@"<div><a href=""").Append(r.Url).Append(@""" target=""_blank"">")
              .Append(Html(r.Title)).Append("</a></div>");
            sb.Append(@"<div><strong>").Append(HtmlMoney(r.Currency, r.Total)).Append("</strong></div>");
            sb.Append(@"<div><span class=""badge-green"">")
              .Append(r.IsAuction ? "Auction" : "Buy It Now").Append("</span></div>");

            if (r.IsAuction && r.EndUtc is DateTime endUtc)
            {
                var isoUtc = endUtc.ToString("o"); // ISO 8601 for JS Date.parse
                sb.Append($@"<div class=""meta""><span class=""eta js-eta"" data-end-utc=""{isoUtc}""></span></div>");
            }

            sb.Append("</div>"); // ebleft

            // right image (optional)
            sb.Append(@"<div class=""ebright"">");
            if (!string.IsNullOrWhiteSpace(r.ImageUrl))
                sb.Append($@"<a href=""{r.Url}"" target=""_blank""><img src=""{r.ImageUrl}"" alt=""""></a>");
            sb.Append("</div>");

            sb.Append("</div></div>"); // ebitem, ebrow
        }

        var updated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
        sb.Append(@"</main><div class=""footer"">Last updated: ").Append(updated).Append("</div>");

        // Live "time left" updater (runs on load + every 30s)
        sb.Append(@"<script>
(function(){
  function fmt(ms){
    if (ms <= 0) return 'ended';
    var s = Math.floor(ms/1000);
    var d = Math.floor(s/86400); s %= 86400;
    var h = Math.floor(s/3600);  s %= 3600;
    var m = Math.floor(s/60);
    if (d > 0) return d + 'd ' + h + 'h ' + m + 'm left';
    if (h > 0) return h + 'h ' + m + 'm left';
    return m + 'm left';
  }
  function update(){
    var now = Date.now();
    document.querySelectorAll('.js-eta').forEach(function(el){
      var row = el.closest('.ebrow');
      var endAttr = el.getAttribute('data-end-utc');
      var end = Date.parse(endAttr);
      if (isNaN(end)) { el.textContent = ''; return; }
      var ms = end - now;
      el.textContent = fmt(ms);
      if (ms <= 0) row && row.classList.add('ended'); else row && row.classList.remove('ended');
    });
  }
  document.addEventListener('DOMContentLoaded', function(){
    update();
    setInterval(update, 30000);
  });
  document.addEventListener('visibilitychange', function(){
    if (!document.hidden) update();
  });
})();
</script>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Html(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string HtmlMoney(string currency, decimal value)
    {
        if (string.Equals(currency, "GBP", StringComparison.OrdinalIgnoreCase))
            return $"£{value:0.00}";
        return $"{currency} {value:0.00}";
    }
}
