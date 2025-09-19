using System.Text;

namespace FavouriteAlbums.Core;

public static class HtmlRenderer
{
    public static string Render(IEnumerable<AlbumAggregate> albums, string title)
    {
        var sb = new StringBuilder();

        // ---- <head> ----
        sb.Append(@"<!doctype html><html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>").Append(Html(title)).Append(@"</title>
<link rel=""stylesheet"" href=""https://www.mattdurrant.com/styles.css"">
</head><body class=""albums-page"">");

        // Optional page heading
        sb.Append("<header><h1>").Append(Html(title)).Append("</h1></header><main>");

        // ---- table layout like your sample ----
        sb.Append(@"<table class=""albums""><tbody>");

        int rank = 1;
        foreach (var a in albums)
        {
            var albumUrl = OpenAlbumUrl(a.Uri);
            var scorePercent = a.Percent.ToString("0"); // integer percent (e.g. 98)
            var artistsJoined = string.Join(", ", a.Artists);

            sb.Append("<tr>");

            // Col 1: rank + % score
            sb.Append("<td>")
              .Append(@"<div class=""rank"">").Append(rank).Append(".</div>")
              .Append(@"<div class=""score"">").Append(scorePercent).Append("%</div>")
              .Append("</td>");

            // Col 2: album link, artist link(s), year, and per-track links with star glyphs
            sb.Append("<td>");

            // album title links to open.spotify album
            sb.Append(@"<a href=""").Append(albumUrl).Append(@""">")
              .Append(Html(a.AlbumName)).Append("</a><br>");

            // artist links to your site (one link per artist)
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
            else
            {
                sb.Append(Html(artistsJoined)).Append("<br>");
            }

            // year (if known)
            if (a.ReleaseYear is int year)
                sb.Append(year).Append("<br>");

            sb.Append("<br>");

            // Track list (each title links to open.spotify track, with star glyphs for rated tracks)
            if (a.Tracks.Count > 0)
            {
                int i = 0;
                foreach (var t in a.Tracks)
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

            // Col 3: album image (links to album)
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
        // very light slug: lowercase, spaces -> '-', strip non-url-friendly chars
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-') sb.Append('-');
            // else drop punctuation
        }
        // collapse multiple '-'
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private static string OpenAlbumUrl(string uri)
    {
        // uri expected like "spotify:album:..."
        const string prefix = "spotify:album:";
        if (!string.IsNullOrWhiteSpace(uri) && uri.StartsWith(prefix, StringComparison.Ordinal))
            return "https://open.spotify.com/album/" + uri.Substring(prefix.Length);
        return uri; // already a web URL?
    }

    private static string StarGlyphs(int stars)
    {
        var s = Math.Clamp(stars, 0, 5);
        return new string('★', s) + new string('☆', 5 - s);
    }
}
