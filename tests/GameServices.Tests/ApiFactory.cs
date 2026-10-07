using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameServices.Tests;

/// <summary>
/// Starts the REAL server in memory for each test class, with its own throw-away
/// database file, so tests never touch your real data and never affect each other.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"gameservices-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={databasePath}");
        builder.UseSetting("Jwt:Key", "TEST-ONLY-key-0123456789abcdef0123456789abcdef");

        // Tests make lots of requests quickly -- don't let the rate limiter interfere
        // (it has its own dedicated test below).
        builder.UseSetting("RateLimit:AuthRequestsPerMinute", "1000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        try
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
        catch (IOException)
        {
            // The file may still be briefly locked; the OS temp folder gets cleaned up anyway.
        }
    }
}

/// <summary>Same server, but with a tiny rate limit, to prove the limiter works.</summary>
public class StrictRateLimitFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:AuthRequestsPerMinute", "3");
    }
}
