namespace GameServices.Api.Map;

/// <summary>Where the relay gets map squares it doesn't have yet. (An interface so tests can use a pretend one.)</summary>
public interface IMapUpstream
{
    /// <summary>The Overpass answer for one square, or a failure saying how long to wait before asking again.</summary>
    Task<UpstreamResult> FetchAsync(int x, int y, CancellationToken ct);
}

public record UpstreamResult(string? Json, string? Error, TimeSpan? WaitBeforeRetry)
{
    public static UpstreamResult Ok(string json) => new(json, null, null);
    public static UpstreamResult Fail(string error, TimeSpan? wait = null) => new(null, error, wait);
}

/// <summary>
/// Asks the public Overpass servers (OpenStreetMap data), trying each address in turn.
///
/// Overpass is run by volunteers, so the relay is polite: the store only ever runs one request at a time, and an
/// answer that says "too busy" (429) or "timed out" is a failure, never a half-empty square. A busy Overpass can
/// even answer "200 OK" with a "remark" saying it gave up part-way - that counts as a failure too.
/// </summary>
public sealed class OverpassUpstream : IMapUpstream
{
    public const string HttpClientName = "overpass";

    private readonly IHttpClientFactory httpFactory;
    private readonly string[] urls;
    private readonly ILogger<OverpassUpstream> log;

    public OverpassUpstream(IHttpClientFactory httpFactory, IConfiguration configuration, ILogger<OverpassUpstream> log)
    {
        this.httpFactory = httpFactory;
        this.log = log;
        urls = configuration.GetSection("Map:OverpassUrls").Get<string[]>() is { Length: > 0 } list
            ? list
            : new[] { "https://overpass-api.de/api/interpreter" };
    }

    public async Task<UpstreamResult> FetchAsync(int x, int y, CancellationToken ct)
    {
        HttpClient http = httpFactory.CreateClient(HttpClientName);
        string query = MapQuery.For(x, y);
        UpstreamResult last = UpstreamResult.Fail("no Overpass address configured");

        foreach (string url in urls)
        {
            try
            {
                using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
                using HttpResponseMessage response = await http.PostAsync(url, body, ct);

                if ((int)response.StatusCode == 429)
                {
                    // "Too many requests": everyone waits, not just this square.
                    TimeSpan wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                    return UpstreamResult.Fail("Overpass is busy (429)", wait);
                }

                if (!response.IsSuccessStatusCode)
                {
                    last = UpstreamResult.Fail($"Overpass answered {(int)response.StatusCode}");
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                if (json.Contains("\"remark\"", StringComparison.Ordinal))
                {
                    last = UpstreamResult.Fail("Overpass gave up part-way (remark)");
                    continue;
                }

                if (!json.Contains("\"elements\"", StringComparison.Ordinal))
                {
                    last = UpstreamResult.Fail("Overpass sent something that isn't map data");
                    continue;
                }

                return UpstreamResult.Ok(json);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                last = UpstreamResult.Fail("Overpass unreachable: " + e.Message);
            }
        }

        log.LogInformation("Map square 15/{X}/{Y} not fetched: {Error}", x, y, last.Error);
        return last;
    }
}
