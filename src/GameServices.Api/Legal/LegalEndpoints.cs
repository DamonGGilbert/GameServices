using System.Net;
using System.Text;

namespace GameServices.Api.Legal;

/// <summary>
/// THE PAGES THE APP STORES ASK TO LINK TO (plain web pages, no sign-in needed):
///     GET /privacy           the privacy policy (Legal/privacy-policy.txt)
///     GET /delete-account    how to delete an account (Legal/delete-account.txt)
///
/// The game shows the same policy on a screen of its own: its copy is Assets/_Game/Resources/Legal/privacy-policy.txt,
/// so change both together. In the text, "# " is a title, "## " a heading and "- " a list item.
/// </summary>
public static class LegalEndpoints
{
    public static IEndpointRouteBuilder MapLegalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/privacy", (IWebHostEnvironment environment) => Page(environment, "privacy-policy.txt", "Privacy Policy")).WithTags("Legal");
        app.MapGet("/delete-account", (IWebHostEnvironment environment) => Page(environment, "delete-account.txt", "Delete your account")).WithTags("Legal");
        return app;
    }

    private static IResult Page(IWebHostEnvironment environment, string file, string title)
    {
        string path = Path.Combine(environment.ContentRootPath, "Legal", file);
        return Results.Content(ToHtml(File.ReadAllLines(path), title), "text/html; charset=utf-8");
    }

    public static string ToHtml(IEnumerable<string> lines, string title)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"en-GB\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(WebUtility.HtmlEncode(title))
            .Append(" - Steplings</title><style>body{font-family:system-ui,sans-serif;max-width:42rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#333}</style></head><body>");
        bool inList = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            bool item = line.StartsWith("- ");
            if (inList && !item)
            {
                html.Append("</ul>");
                inList = false;
            }

            if (line.Length == 0) continue;
            if (line.StartsWith("## ")) html.Append("<h2>").Append(WebUtility.HtmlEncode(line[3..])).Append("</h2>");
            else if (line.StartsWith("# ")) html.Append("<h1>").Append(WebUtility.HtmlEncode(line[2..])).Append("</h1>");
            else if (item)
            {
                if (!inList) html.Append("<ul>");
                inList = true;
                html.Append("<li>").Append(WebUtility.HtmlEncode(line[2..])).Append("</li>");
            }
            else html.Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>");
        }

        if (inList) html.Append("</ul>");
        return html.Append("</body></html>").ToString();
    }
}
