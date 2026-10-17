using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace GameServices.Api.Map;

/// <summary>One map square ready to send: its data (already gzip-compressed), and whether it's an old copy.</summary>
public record MapTile(byte[] Gzipped, bool Stale);

/// <summary>
/// THE MAP RELAY'S MEMORY. Every map square is fetched from OpenStreetMap ONCE, kept on the server's disk (compressed,
/// about 20-40 KB each) and handed to every player who walks there for the next 30 days.
///
///   * fresh copy on disk          -> straight from disk (fast, OpenStreetMap never hears about it)
///   * none, or too old            -> fetched from Overpass, saved, sent
///   * Overpass busy or down       -> the old copy if there is one (marked stale), otherwise "try again shortly"
///   * 50 players ask at once      -> ONE fetch; the other 49 wait for it ("request coalescing")
///   * one request to Overpass at a time, with a short gap, and a pause for everyone after "too busy"
///
/// Files live in {CacheFolder}/v{MapQuery.Version}/15_x_y.json.gz - a folder, not the database: they're big, they're
/// only ever read whole, and if they're deleted the relay simply fetches them again.
/// </summary>
public sealed class MapTileStore
{
    private readonly IMapUpstream upstream;
    private readonly ILogger<MapTileStore> log;
    private readonly string folder;
    private readonly TimeSpan maxAge;
    private readonly TimeSpan gap;

    // Lazy: ConcurrentDictionary.GetOrAdd may run its "make one" code twice when two requests arrive at the same
    // instant - wrapping the fetch in Lazy means only the one that actually gets stored ever starts.
    private readonly ConcurrentDictionary<(int X, int Y), Lazy<Task<MapTile?>>> inFlight = new();
    private readonly SemaphoreSlim oneAtATime = new(1, 1);
    private DateTime nextRequestUtc = DateTime.MinValue;
    private DateTime pausedUntilUtc = DateTime.MinValue;

    public MapTileStore(IMapUpstream upstream, IConfiguration configuration, IWebHostEnvironment environment, ILogger<MapTileStore> log)
    {
        this.upstream = upstream;
        this.log = log;
        string root = configuration["Map:CacheFolder"] ?? "map-cache";
        folder = Path.Combine(Path.IsPathRooted(root) ? root : Path.Combine(environment.ContentRootPath, root), "v" + MapQuery.Version);
        maxAge = TimeSpan.FromDays(configuration.GetValue("Map:CacheDays", 30));
        gap = TimeSpan.FromMilliseconds(configuration.GetValue("Map:GapBetweenRequestsMs", 1500));
        Directory.CreateDirectory(folder);
    }

    /// <summary>How many seconds a player should wait before asking again when there's nothing to send yet.</summary>
    public int RetryAfterSeconds => Math.Clamp((int)Math.Ceiling((pausedUntilUtc - DateTime.UtcNow).TotalSeconds), 15, 120);

    public async Task<MapTile?> GetAsync(int x, int y)
    {
        string path = PathFor(x, y);
        var file = new FileInfo(path);

        if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < maxAge)
        {
            return new MapTile(await File.ReadAllBytesAsync(path), false);
        }

        // Everyone asking for this square right now shares one fetch.
        Lazy<Task<MapTile?>> shared = inFlight.GetOrAdd((x, y), key => new Lazy<Task<MapTile?>>(() => FetchAndSave(key.X, key.Y, path)));
        return await shared.Value;
    }

    private async Task<MapTile?> FetchAndSave(int x, int y, string path)
    {
        try
        {
            UpstreamResult result = await AskUpstream(x, y);

            if (result.Json != null)
            {
                byte[] gzipped = Gzip(result.Json);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, gzipped);
                File.Move(temp, path, overwrite: true);   // appears all at once: nobody ever reads half a file
                return new MapTile(gzipped, false);
            }

            // Couldn't get a new one: an old copy is far better than nothing.
            if (File.Exists(path)) return new MapTile(await File.ReadAllBytesAsync(path), true);
            return null;
        }
        finally
        {
            inFlight.TryRemove((x, y), out _);
        }
    }

    // One at a time, with a gap - and nobody asks at all while Overpass has told us to slow down.
    private async Task<UpstreamResult> AskUpstream(int x, int y)
    {
        await oneAtATime.WaitAsync();
        try
        {
            if (DateTime.UtcNow < pausedUntilUtc) return UpstreamResult.Fail("pausing after Overpass said it was busy");

            TimeSpan wait = nextRequestUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            UpstreamResult result = await upstream.FetchAsync(x, y, timeout.Token);

            nextRequestUtc = DateTime.UtcNow + gap;
            if (result.WaitBeforeRetry is { } pause) pausedUntilUtc = DateTime.UtcNow + pause;
            if (result.Json != null) log.LogInformation("Map square 15/{X}/{Y} fetched from OpenStreetMap", x, y);
            return result;
        }
        catch (OperationCanceledException)
        {
            return UpstreamResult.Fail("Overpass took too long");
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    private string PathFor(int x, int y) => Path.Combine(folder, $"{MapQuery.Zoom}_{x}_{y}.json.gz");

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    public static byte[] Gunzip(byte[] gzipped)
    {
        using var input = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
