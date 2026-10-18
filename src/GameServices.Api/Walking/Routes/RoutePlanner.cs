namespace GameServices.Api.Walking.Routes;

/// <summary>A planned loop: the way to walk, checkpoints along it, and how safe it is.</summary>
public sealed record PlannedRoute(List<(double Lat, double Lon)> Path, List<(double Lat, double Lon)> Checkpoints, double Metres,
    double QuietShare, int BusyStretches);

/// <summary>
/// PLANS A SAFE WALKING LOOP that starts and ends where you are.
///
/// How: pick two turning points roughly the right distance away (in a random direction, so every route is different),
/// then find the CHEAPEST way start -> first turn -> second turn -> home, using the safety costs in RouteGraph. Stretches
/// already used are made more expensive for the later legs, so it's a proper loop rather than there-and-back.
/// It tries a dozen times and keeps the route that is closest to the length you asked for while staying on the
/// quietest ways.
///
/// Checkpoints are dropped along the route every few hundred metres; the last one is back at the start.
/// </summary>
public static class RoutePlanner
{
    private const double MaxSnapMetres = 250;
    private const double ReusePenalty = 4.0;
    private const int Attempts = 14;

    public static PlannedRoute? Plan(RouteGraph graph, double lat, double lon, double targetMetres, int seed)
    {
        int start = Nearest(graph, lat, lon, null, out double snap);
        if (start < 0 || snap > MaxSnapMetres) return null;

        bool[] reachable = Reachable(graph, start);
        var random = new Random(seed);
        PlannedRoute? best = null;
        double bestScore = double.MaxValue;

        // Walking paths wiggle: a turning point "d" away as the crow flies is usually further on foot.
        double spread = targetMetres / 3.9;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            double bearing = random.NextDouble() * Math.PI * 2;
            double turn = (random.NextDouble() < 0.5 ? 1 : -1) * (Math.PI / 3 + random.NextDouble() * Math.PI / 6);

            int w1 = Nearest(graph, Offset(lat, lon, bearing, spread), reachable, out _);
            int w2 = Nearest(graph, Offset(lat, lon, bearing + turn, spread), reachable, out _);
            if (w1 < 0 || w2 < 0 || w1 == start || w2 == start || w1 == w2) continue;

            var used = new HashSet<int>();
            List<RouteGraph.Edge>? a = Cheapest(graph, start, w1, used);
            if (a == null) continue;
            foreach (RouteGraph.Edge e in a) used.Add(e.EdgeId);
            List<RouteGraph.Edge>? b = Cheapest(graph, w1, w2, used);
            if (b == null) continue;
            foreach (RouteGraph.Edge e in b) used.Add(e.EdgeId);
            List<RouteGraph.Edge>? c = Cheapest(graph, w2, start, used);
            if (c == null) continue;

            var edges = new List<RouteGraph.Edge>(a.Count + b.Count + c.Count);
            edges.AddRange(a);
            edges.AddRange(b);
            edges.AddRange(c);

            double metres = edges.Sum(e => e.Metres);
            double quiet = edges.Where(e => !e.Busy).Sum(e => e.Metres) / Math.Max(1, metres);
            double repeated = 1.0 - edges.Select(e => e.EdgeId).Distinct().Count() / (double)Math.Max(1, edges.Count);

            // Closest to the asked-for length, then quietest, then least doubling-back.
            double score = Math.Abs(metres - targetMetres) / targetMetres + 0.5 * (1 - quiet) + 0.4 * repeated;
            if (metres < targetMetres * 0.7 || metres > targetMetres * 1.35) score += 2;

            if (score < bestScore)
            {
                bestScore = score;
                best = Build(graph, start, edges, metres, quiet);
            }

            // Too long or too short: aim the next turning points closer or further.
            spread *= Math.Clamp(targetMetres / Math.Max(1, metres), 0.8, 1.25);
        }

        return best != null && best.Metres >= targetMetres * 0.7 && best.Metres <= targetMetres * 1.35 ? best : null;
    }

    private static PlannedRoute Build(RouteGraph graph, int start, List<RouteGraph.Edge> edges, double metres, double quiet)
    {
        var path = new List<(double, double)> { (graph.Lat[start], graph.Lon[start]) };
        foreach (RouteGraph.Edge e in edges) path.Add((graph.Lat[e.To], graph.Lon[e.To]));

        // Checkpoints: evenly along the way (4 to 10 of them), the last back at the start.
        int count = Math.Clamp((int)Math.Round(metres / 400), 4, 10);
        double spacing = metres / count;
        var checkpoints = new List<(double, double)>();
        double walked = 0, next = spacing;
        for (int i = 1; i < path.Count && checkpoints.Count < count - 1; i++)
        {
            walked += RouteGraph.Distance(path[i - 1].Item1, path[i - 1].Item2, path[i].Item1, path[i].Item2);
            if (walked >= next)
            {
                checkpoints.Add(path[i]);
                next += spacing;
            }
        }
        checkpoints.Add(path[0]);

        int busy = 0;
        bool onBusy = false;
        foreach (RouteGraph.Edge e in edges)
        {
            if (e.Busy && !onBusy) busy++;
            onBusy = e.Busy;
        }

        return new PlannedRoute(path, checkpoints, metres, quiet, busy);
    }

    // Dijkstra on safety cost. Stretches already used cost more, so the loop takes a different way back.
    private static List<RouteGraph.Edge>? Cheapest(RouteGraph graph, int from, int to, HashSet<int> used)
    {
        int n = graph.NodeCount;
        var cost = new double[n];
        var via = new RouteGraph.Edge?[n];
        var cameFrom = new int[n];
        Array.Fill(cost, double.MaxValue);
        cost[from] = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out int node, out double c))
        {
            if (node == to) break;
            if (c > cost[node]) continue;
            foreach (RouteGraph.Edge e in graph.Links[node])
            {
                double next = c + e.Cost * (used.Contains(e.EdgeId) ? ReusePenalty : 1.0);
                if (next >= cost[e.To]) continue;
                cost[e.To] = next;
                via[e.To] = e;
                cameFrom[e.To] = node;
                queue.Enqueue(e.To, next);
            }
        }

        if (cost[to] == double.MaxValue) return null;
        var edges = new List<RouteGraph.Edge>();
        for (int at = to; at != from; at = cameFrom[at]) edges.Add(via[at]!.Value);
        edges.Reverse();
        return edges;
    }

    private static bool[] Reachable(RouteGraph graph, int start)
    {
        var seen = new bool[graph.NodeCount];
        var stack = new Stack<int>();
        stack.Push(start);
        seen[start] = true;
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            foreach (RouteGraph.Edge e in graph.Links[node])
            {
                if (seen[e.To]) continue;
                seen[e.To] = true;
                stack.Push(e.To);
            }
        }
        return seen;
    }

    private static int Nearest(RouteGraph graph, (double Lat, double Lon) p, bool[]? onlyThese, out double metres) => Nearest(graph, p.Lat, p.Lon, onlyThese, out metres);

    private static int Nearest(RouteGraph graph, double lat, double lon, bool[]? onlyThese, out double metres)
    {
        int best = -1;
        metres = double.MaxValue;
        for (int i = 0; i < graph.NodeCount; i++)
        {
            if (onlyThese != null && !onlyThese[i]) continue;
            if (graph.Links[i].Count == 0) continue;
            double d = RouteGraph.Distance(lat, lon, graph.Lat[i], graph.Lon[i]);
            if (d < metres)
            {
                metres = d;
                best = i;
            }
        }
        return best;
    }

    private static (double Lat, double Lon) Offset(double lat, double lon, double bearing, double metres)
    {
        double dLat = Math.Cos(bearing) * metres / 110540.0;
        double dLon = Math.Sin(bearing) * metres / (111320.0 * Math.Cos(lat * Math.PI / 180.0));
        return (lat + dLat, lon + dLon);
    }
}
