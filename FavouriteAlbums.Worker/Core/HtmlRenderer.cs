using System.Text;

namespace FavouriteAlbums.Core;

public static class HtmlRenderer
{
    public static string Render(IEnumerable<AlbumAggregate> albums, string title)
    {
        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><html><head><meta charset=""utf-8"">
<title>").Append(title).Append(@"</title>
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
body{font-family:system-ui,-apple-system,Segoe UI,Roboto,Ubuntu,Helvetica,Arial,sans-serif;margin:20px;background:#fff;color:#111}
h1{font-size:1.6rem;margin-bottom:0.5rem}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px;margin-top:1rem}
.card{border:1px solid #eee;border-radius:12px;padding:12px}
img{width:100%;border-radius:8px}
.album{font-weight:600;margin:6px 0 2px}
.meta{font-size:.9rem;color:#555}
.count{font-size:.85rem;color:#333;margin-top:4px}
a{color:inherit;text-decoration:none}
.pills{display:flex;gap:8px;flex-wrap:wrap;margin-top:6px}
.pill{border:1px solid #eee;border-radius:999px;padding:2px 8px;font-size:.85rem}
.pill strong{margin-right:4px}
</style></head><body><h1>").Append(title).Append("</h1><div class=\"grid\">");

        foreach (var a in albums)
        {
            sb.Append("<article class=\"card\">");
            if (!string.IsNullOrWhiteSpace(a.ImageUrl))
                sb.Append("<a href=\"").Append(a.Uri).Append("\" target=\"_blank\" rel=\"noopener\"><img src=\"")
                  .Append(a.ImageUrl).Append("\" alt=\"").Append(Html(title)).Append("\"></a>");
            sb.Append("<div class=\"album\">").Append(Html(a.AlbumName)).Append("</div>");
            sb.Append("<div class=\"meta\">").Append(Html(string.Join(", ", a.Artists))).Append("</div>");
            sb.Append("<div class=\"count\">Unique rated tracks counted: ").Append(a.Count).Append("</div>");
            sb.Append("<div class=\"pills\">");
            sb.Append("<span class=\"pill\"><strong>Score</strong> ").Append(a.Score).Append("</span>");
            sb.Append("<span class=\"pill\"><strong>%</strong> ").Append(a.Percent.ToString("0.0")).Append("</span>");
            sb.Append("<span class=\"pill\"><strong>Denom</strong> ").Append(a.Denominator).Append("</span>");
            for (int s = 5; s >= 1; s--)
            {
                var c = a.StarCounts.GetValueOrDefault(s);
                if (c > 0) sb.Append("<span class=\"pill\">").Append(s).Append("★ ").Append(c).Append("</span>");
            }
            sb.Append("</div>");
            sb.Append("</article>");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    static string Html(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
