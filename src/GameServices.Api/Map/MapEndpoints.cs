namespace GameServices.Api.Map;

/// <summary>
/// THE MAP RELAY: the walking game's map data, from this server instead of from the public Overpass service.
///
///   GET /api/map/15/{x}/{y}   one map square (OpenStreetMap roads, paths, water, parks, coastline) as Overpass JSON
///
/// Why: the public Overpass servers are run by volunteers for developers - not as the back end for an app with many
/// players (and they were often too busy to answer). Now each square is fetched from them ONCE and shared.
///
/// PRIVACY: no account needed, and none is looked at - a map square is about 1 km across, and requests are not
/// logged, so the server never builds up a picture of where any player walks.
///
/// Map data © OpenStreetMap contributors, available under the Open Database Licence (openstreetmap.org/copyright).
/// </summary>
public static class MapEndpoints
{
    public static void MapMapEndpoints(this WebApplication app)
    {
        app.MapGet("/api/map/{z:int}/{x:int}/{y:int}", async (int z, int x, int y, HttpContext http, MapTileStore store) =>
            {
                if (!MapQuery.IsValid(z, x, y))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                        title: $"Map squares are zoom {MapQuery.Zoom}, with x and y from 0 to {(1 << MapQuery.Zoom) - 1}.");
                }

                MapTile? tile = await store.GetAsync(x, y);

                if (tile == null)
                {
                    http.Response.Headers.RetryAfter = store.RetryAfterSeconds.ToString();
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The map for this area isn't ready yet - please try again shortly.");
                }

                http.Response.Headers.CacheControl = "public, max-age=86400";
                http.Response.Headers.Vary = "Accept-Encoding";
                if (tile.Stale) http.Response.Headers["X-Map-Stale"] = "true";

                // Squares are stored compressed. Most apps (Unity included) accept that as-is; anyone who doesn't
                // gets it unpacked.
                if (http.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    http.Response.Headers.ContentEncoding = "gzip";
                    return Results.Bytes(tile.Gzipped, "application/json");
                }

                return Results.Bytes(MapTileStore.Gunzip(tile.Gzipped), "application/json");
            })
            .AllowAnonymous()
            .RequireRateLimiting("map")
            .WithTags("Map");
    }
}
