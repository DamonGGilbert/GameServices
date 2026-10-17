namespace GameServices.Api.Map;

/// <summary>
/// WHAT the map relay asks OpenStreetMap for, for one map square: the roads and paths, rivers, the coastline, and the
/// green and blue areas (as simple shapes and as "multipolygons" - big lakes and parks with holes). It's the same
/// question the walking game used to ask Overpass itself, so the answer is exactly what the game already reads.
///
/// Squares are slippy-map tiles at zoom 15 (roughly 0.7 - 1.2 km across in the UK).
/// </summary>
public static class MapQuery
{
    public const int Zoom = 15;

    /// <summary>Bump when the question changes, so every square is fetched again (old copies are kept separately).</summary>
    public const int Version = 3;

    private static readonly string[] AreaFilters =
    {
        "[\"natural\"~\"^(water|wood|scrub|beach|sand|grassland|heath)$\"]",
        "[\"water\"]",
        "[\"leisure\"~\"^(park|garden|playground|dog_park|pitch|track|golf_course|sports_centre|nature_reserve|common|recreation_ground)$\"]",
        "[\"landuse\"~\"^(grass|meadow|village_green|recreation_ground|allotments|cemetery|forest|reservoir|basin)$\"]",
    };

    /// <summary>Is this a real square at our zoom level?</summary>
    public static bool IsValid(int z, int x, int y) => z == Zoom && x >= 0 && y >= 0 && x < (1 << Zoom) && y < (1 << Zoom);

    /// <summary>The Overpass question for one square.</summary>
    public static string For(int x, int y)
    {
        (double north, double west) = NorthWest(x, y);
        (double south, double east) = NorthWest(x + 1, y + 1);
        string bbox = string.Join(",", F(south), F(west), F(north), F(east));

        var q = new System.Text.StringBuilder("[out:json][timeout:25];(");
        q.Append("way[\"highway\"](").Append(bbox).Append(");");
        q.Append("way[\"waterway\"~\"^(river|canal|stream|brook|riverbank)$\"](").Append(bbox).Append(");");
        q.Append("way[\"natural\"=\"coastline\"](").Append(bbox).Append(");");
        foreach (string filter in AreaFilters)
        {
            q.Append("way").Append(filter).Append('(').Append(bbox).Append(");");
            q.Append("relation[\"type\"=\"multipolygon\"]").Append(filter).Append('(').Append(bbox).Append(");");
        }
        return q.Append(");out body geom qt;").ToString();
    }

    // The north-west corner of a square (standard slippy-map maths).
    private static (double Lat, double Lon) NorthWest(int x, int y)
    {
        double n = 1 << Zoom;
        double lon = x / n * 360.0 - 180.0;
        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180.0 / Math.PI;
        return (lat, lon);
    }

    private static string F(double v) => v.ToString("0.0000000", System.Globalization.CultureInfo.InvariantCulture);
}
