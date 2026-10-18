using System.Text.Json;

namespace GameServices.Api.Walking.Routes;

/// <summary>
/// THE WALKING NETWORK: every path, pavement and street in some map squares, joined up where they meet, with a
/// SAFETY COST on each stretch. The cost is the length multiplied by how pleasant and safe that kind of way is to walk:
///
///   1.0   footpaths, park paths, pedestrian streets, living streets (the nicest)
///   1.2+  tracks, bridleways, cycle paths (shared), steps
///   1.6   quiet residential streets (1.2 with a pavement)
///   3.5   busier "tertiary" roads (2.2 with a pavement)
///   6.0   secondary roads (3.0 with a pavement)
///   5.0   main roads WITH a pavement - otherwise not used at all
///   never motorways, main roads without a pavement, private paths, anything marked "no walking"
///   x0.9  if the way is lit at night
///
/// A route planner looking for the cheapest way round therefore prefers parks and quiet streets, and only uses a busy
/// road when there's no sensible alternative - exactly what a careful local would do.
///
/// Built from the Overpass answers the map relay already keeps (see Map/MapTileStore).
/// </summary>
public sealed class RouteGraph
{
    public readonly List<double> Lat = new();
    public readonly List<double> Lon = new();
    public readonly List<List<Edge>> Links = new();
    private readonly Dictionary<long, int> byOsmId = new();
    private int nextEdgeId;

    /// <summary>One direction of a stretch of way. Both directions share an EdgeId (so a route can avoid repeating it).</summary>
    public readonly record struct Edge(int To, double Metres, double Cost, int EdgeId, bool Busy);

    public int NodeCount => Lat.Count;

    public static RouteGraph FromOverpass(IEnumerable<string> overpassJsonAnswers)
    {
        var graph = new RouteGraph();
        var seenWays = new HashSet<long>();

        foreach (string json in overpassJsonAnswers)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elements", out JsonElement elements)) continue;

            foreach (JsonElement e in elements.EnumerateArray())
            {
                if (!e.TryGetProperty("type", out JsonElement type) || type.GetString() != "way") continue;
                if (!e.TryGetProperty("tags", out JsonElement tags) || !e.TryGetProperty("nodes", out JsonElement nodes) || !e.TryGetProperty("geometry", out JsonElement geometry)) continue;

                long wayId = e.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0;
                if (!seenWays.Add(wayId)) continue;   // the same way appears in every square it crosses

                double? factor = SafetyFactor(tags, out bool busy);
                if (factor == null || nodes.GetArrayLength() != geometry.GetArrayLength() || nodes.GetArrayLength() < 2) continue;

                int previous = -1;
                int i = 0;
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    JsonElement point = geometry[i++];
                    if (point.ValueKind != JsonValueKind.Object) { previous = -1; continue; }   // part of the way outside the square
                    int index = graph.NodeFor(node.GetInt64(), point.GetProperty("lat").GetDouble(), point.GetProperty("lon").GetDouble());
                    if (previous >= 0 && previous != index)
                    {
                        double metres = Distance(graph.Lat[previous], graph.Lon[previous], graph.Lat[index], graph.Lon[index]);
                        graph.Link(previous, index, metres, metres * factor.Value, busy);
                    }
                    previous = index;
                }
            }
        }

        return graph;
    }

    private int NodeFor(long osmId, double lat, double lon)
    {
        if (byOsmId.TryGetValue(osmId, out int index)) return index;
        index = Lat.Count;
        byOsmId[osmId] = index;
        Lat.Add(lat);
        Lon.Add(lon);
        Links.Add(new List<Edge>());
        return index;
    }

    private void Link(int a, int b, double metres, double cost, bool busy)
    {
        int edge = nextEdgeId++;
        Links[a].Add(new Edge(b, metres, cost, edge, busy));
        Links[b].Add(new Edge(a, metres, cost, edge, busy));
    }

    /// <summary>How unpleasant a way is to walk, per metre - or null if a walker shouldn't use it at all.</summary>
    public static double? SafetyFactor(JsonElement tags, out bool busy)
    {
        busy = false;
        string? highway = Tag(tags, "highway");
        if (highway == null) return null;

        string? access = Tag(tags, "access");
        string? foot = Tag(tags, "foot");
        if (foot == "no" || ((access == "private" || access == "no") && foot != "yes" && foot != "designated")) return null;

        string? sidewalk = Tag(tags, "sidewalk");
        bool pavement = sidewalk is "both" or "left" or "right" or "yes" or "separate";

        double factor;
        switch (highway)
        {
            case "footway": case "path": case "pedestrian": case "living_street": factor = 1.0; break;
            case "track": case "bridleway": factor = 1.2; break;
            case "cycleway": factor = foot == "no" ? -1 : 1.3; break;
            case "steps": factor = 1.5; break;
            case "residential": case "unclassified": case "road": factor = pavement ? 1.2 : 1.6; break;
            case "service": factor = 1.4; break;
            case "tertiary": case "tertiary_link": factor = pavement ? 2.2 : 3.5; busy = true; break;
            case "secondary": case "secondary_link": factor = pavement ? 3.0 : 6.0; busy = true; break;
            case "primary": case "primary_link": case "trunk": case "trunk_link":
                if (!pavement) return null;
                factor = 5.0; busy = true; break;
            default: return null;   // motorways, construction, platforms, raceways...
        }

        if (factor < 0) return null;
        if (Tag(tags, "lit") == "yes") factor *= 0.9;
        return factor;
    }

    private static string? Tag(JsonElement tags, string key) => tags.TryGetProperty(key, out JsonElement v) ? v.GetString() : null;

    /// <summary>Metres between two points (flat-earth maths - plenty accurate over a few kilometres).</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double k = Math.Cos((lat1 + lat2) * 0.5 * Math.PI / 180.0);
        double dx = (lon2 - lon1) * 111320.0 * k;
        double dy = (lat2 - lat1) * 110540.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
