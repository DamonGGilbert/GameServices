using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GameServices.Tests;

/// <summary>
/// Starts the REAL server in memory for each test class, with its own throw-away database,
/// so tests never touch your real data and never affect each other.
///
/// By default the database is a SQLite file (fast, needs nothing installed). Set the environment
/// variable TEST_DATABASE=postgres to run the SAME tests against a real PostgreSQL server instead:
/// a throw-away container is started automatically (needs Docker running), or, if
/// TEST_POSTGRES_CONNECTION is set (as on a CI server), that server is used.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private static readonly bool UsePostgres =
        string.Equals(Environment.GetEnvironmentVariable("TEST_DATABASE"), "postgres", StringComparison.OrdinalIgnoreCase);

    private readonly string databaseName = "gs_test_" + Guid.NewGuid().ToString("N");
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"gameservices-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:Key", "TEST-ONLY-key-0123456789abcdef0123456789abcdef");

        // Tests make lots of requests quickly -- don't let the rate limiter interfere
        // (it has its own dedicated test below).
        builder.UseSetting("RateLimit:AuthRequestsPerMinute", "1000");

        if (UsePostgres)
        {
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", PostgresTestServer.CreateDatabase(databaseName));
        }
        else
        {
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={databasePath}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (UsePostgres)
        {
            PostgresTestServer.DropDatabase(databaseName);
            return;
        }

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

/// <summary>
/// One PostgreSQL server shared by the whole test run, giving every test class its own empty
/// database inside it (creating a database is far faster than starting a new server).
/// </summary>
internal static class PostgresTestServer
{
    private static readonly object Gate = new();
    private static PostgreSqlContainer? container;
    private static string? adminConnection;

    private static string AdminConnection()
    {
        lock (Gate)
        {
            if (adminConnection != null)
            {
                return adminConnection;
            }

            string? provided = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");

            if (!string.IsNullOrWhiteSpace(provided))
            {
                adminConnection = provided;
            }
            else
            {
                container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                container.StartAsync().GetAwaiter().GetResult();
                adminConnection = container.GetConnectionString();
            }

            return adminConnection;
        }
    }

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public static string CreateDatabase(string name)
    {
        string admin = AdminConnection();

        using (NpgsqlConnection connection = new(admin))
        {
            connection.Open();
            using NpgsqlCommand command = new($"CREATE DATABASE \"{name}\"", connection);
            command.ExecuteNonQuery();
        }

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static void DropDatabase(string name)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();

            using NpgsqlConnection connection = new(AdminConnection());
            connection.Open();
            using NpgsqlCommand command = new($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Best effort: the whole container disappears at the end of the run anyway.
        }
    }
}
