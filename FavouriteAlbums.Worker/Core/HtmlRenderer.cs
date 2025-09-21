using System.Text;

namespace FavouriteAlbums.Core;

public static class HtmlRenderer
{
    // NEW: optional navHtml lets us inject tiny link bars per page
    public static string Render(IEnumerable<AlbumAggregate> albums, string title, string? navHtml = null)
    {
        var sb = new StringBuilder();

        // ---- <head> ----
        sb.Append(@"<!doctype html><html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>").Append(Html(title)).Append(@"</title>
<link rel=""stylesheet"" href=""https://www.mattdurrant.com/styles.css"">
<link rel=""stylesheet"" type=""text/css"" href=""https://www.mattdurrant.com/albums.css"">
</head><body class=""albums-page"">");

        // Header + optional subnav
        sb.Append("<header><h1>").Append(Html(title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(navHtml))
            sb.Append(@"<nav class=""subnav"">").Append(navHtml).Append("</nav>");
        sb.Append("</header><main>");

        // ---- table layout (rank / album-info-with-tracks / cover) ----
        sb.Append(@"<table class=""albums""><tbody>");

        int rank = 1;
        foreach (var a in albums)
        {
            var albumUrl = OpenAlbumUrl(a.Uri);
            var scorePercent = a.Percent.ToString("0"); // integer percent
            sb.Append("<tr>");

            // Col 1: rank + %
            sb.Append("<td>")
              .Append(@"<div class=""rank"">").Append(rank).Append(".</div>")
              .Append(@"<div class=""score"">").Append(scorePercent).Append("%</div>")
              .Append("</td>");

            // Col 2: album/artist/year + linked track list with star glyphs
            sb.Append("<td>");

            // album title → spotify album
            sb.Append(@"<a href=""").Append(albumUrl).Append(@""">")
              .Append(Html(a.AlbumName)).Append("</a><br>");

            // artists → your site (per-artist)
            if (a.Artists.Count > 0)
            {
                for (int i = 0; i < a.Artists.Count; i++)
                {
                    var artist = a.Artists[i];
                    var href = "https://www.mattdurrant.com/albums/artist/" + Slug(artist);
                    sb.Append(@"<a href=""").Append(href).Append(@""">")
                      .Append(Html(artist)).Append("</a>");
                    if (i < a.Artists.Count - 1) sb.Append(", ");
                }
                sb.Append("<br>");
            }

            if (a.ReleaseYear is int year) sb.Append(year).Append("<br>");
            sb.Append("<br>");

            // Track links + stars (only if we populated a.Tracks for this album)
            if (a.Tracks.Count > 0)
            {
                int i = 0;
                foreach (var t in a.Tracks.OrderBy(t => t.Number))
                {
                    i++;
                    sb.Append(@"<a href=""").Append(t.Url).Append(@""">")
                      .Append(i).Append(". ").Append(Html(t.Name)).Append("</a>");

                    if (t.Stars is int st)
                        sb.Append(" ").Append(StarGlyphs(st));

                    sb.Append("<br>");
                }
            }

            sb.Append("</td>");

            // Col 3: cover image → spotify album
            sb.Append("<td>");
            if (!string.IsNullOrWhiteSpace(a.ImageUrl))
            {
                sb.Append(@"<a href=""").Append(albumUrl).Append(@""">")
                  .Append(@"<img class=""albumArt"" src=""").Append(a.ImageUrl).Append(@""" alt=""")
                  .Append(Html(a.AlbumName)).Append(@""">")
                  .Append("</a>");
            }
            sb.Append("</td>");

            sb.Append("</tr>");
            rank++;
        }

        sb.Append("</tbody></table>");

        // Footer: last updated (UTC)
        var updated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
        sb.Append(@"</main><div class=""footer"">Last updated: ").Append(updated).Append("</div>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ---- helpers ----

    private static string Html(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-') sb.Append('-');
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private static string OpenAlbumUrl(string uri)
    {
        const string prefix = "spotify:album:";
        if (!string.IsNullOrWhiteSpace(uri) && uri.StartsWith(prefix, StringComparison.Ordinal))
            return "https://open.spotify.com/album/" + uri[prefix.Length..];
        return uri;
    }

    private static string StarGlyphs(int stars)
    {
        var s = Math.Clamp(stars, 0, 5);
        return new string('★', s) + new string('☆', 5 - s);
    }
}
