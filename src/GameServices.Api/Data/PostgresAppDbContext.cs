using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameServices.Api.Data;

/// <summary>
/// The same database model, for PostgreSQL. Why a separate class? A "migration" is a script written
/// in ONE database's dialect (SQLite and PostgreSQL name their column types differently). Each
/// database gets its own migrations folder:
///
///     AppDbContext          -> Migrations/           (SQLite: your PC, quick tests)
///     PostgresAppDbContext  -> MigrationsPostgres/   (PostgreSQL: Docker and production)
///
/// Both share every table definition, because this class inherits it all from AppDbContext.
/// </summary>
public class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options) : AppDbContext(options);

/// <summary>Lets the `dotnet ef migrations add` tool create PostgreSQL migrations without a running database.</summary>
public class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PostgresAppDbContext> options = new();
        options.UseNpgsql("Host=localhost;Database=design_time_only;Username=postgres;Password=unused");
        return new PostgresAppDbContext(options.Options);
    }
}

/// <summary>The SQLite twin of the factory above, so `dotnet ef ... --context AppDbContext` stays on SQLite.</summary>
public class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        options.UseSqlite("Data Source=design_time_only.db");
        return new AppDbContext(options.Options);
    }
}
